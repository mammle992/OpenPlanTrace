//! Bounded, local PDF rasterization. All decoding and composition is native Rust.
//!
//! Rendering the page, rather than extracting its largest image, is essential:
//! scanned PDFs can store linework in image masks over a JPEG background, while
//! printer PDFs can split one drawing into many transformed and clipped tiles.

use hayro::hayro_interpret::{InterpreterSettings, InterpreterWarning};
use hayro::hayro_syntax::Pdf;
use hayro::vello_cpu::color::palette::css::WHITE;
use hayro::vello_cpu::peniko::ImageAlphaType;
use hayro::{PixmapSettings, RenderCache, RenderSettings};
use lopdf::{Dictionary, Document, Object, ObjectId, Stream, content::Content};
use std::collections::{BTreeMap, BTreeSet};
use std::sync::{Arc, Mutex};

const MAX_INPUT_BYTES: usize = 128 * 1024 * 1024;
const MAX_PAGES: usize = 2048;
const MAX_DIMENSION: u32 = 8192;
const MAX_OUTPUT_PIXELS: usize = 32 * 1024 * 1024;
const MAX_IMAGE_PIXELS: u64 = 32 * 1024 * 1024;
// Same allocation ceiling as 64 megapixels of RGBA. Validated gray alpha
// resources decode to one byte per pixel in Hayro, not four color channels.
const MAX_PAGE_IMAGE_BYTES: u64 = 256 * 1024 * 1024;
const MAX_RESOURCE_OBJECTS: usize = 32_768;
const MAX_CONTENT_BYTES: usize = 48 * 1024 * 1024;
const MAX_CONTENT_OPERATIONS: usize = 6_000_000;

/// A top-left-origin, tightly packed RGBA8 page on an opaque white background.
#[derive(Debug)]
pub struct RasterImage {
    pub width: usize,
    pub height: usize,
    pub rgba: Vec<u8>,
}

pub(super) type PageBudgets = Vec<Option<Result<(), String>>>;

/// Parsed PDF state shared by a sequence of page renders. This avoids loading
/// the entire PDF with both parsers again for every page in a drawing set.
pub struct Renderer {
    pdf: Pdf,
    /// Keep the validation result, not a second complete copy of every PDF stream.
    /// Errors stay page-local, so an unused oversized resource on another page
    /// cannot prevent rendering a supported selected page.
    page_budgets: PageBudgets,
}

impl Renderer {
    /// Exact displayed crop-box dimensions, before raster rounding.
    pub fn page_dimensions(&self, page_index: usize) -> Result<(f64, f64), String> {
        let page = self
            .pdf
            .pages()
            .get(page_index)
            .ok_or_else(|| format!("PDF raster page {} does not exist", page_index + 1))?;
        let (w, h) = page.render_dimensions();
        if !w.is_finite() || !h.is_finite() || w <= 0. || h <= 0. {
            return Err("invalid displayed PDF dimensions".into());
        }
        Ok((f64::from(w), f64::from(h)))
    }
    pub fn new(bytes: &[u8]) -> Result<Self, String> {
        Self::with_page(bytes, None)
    }

    pub(super) fn with_page(bytes: &[u8], selected: Option<u32>) -> Result<Self, String> {
        if bytes.len() > MAX_INPUT_BYTES {
            return Err("PDF raster input exceeds 128 MiB".into());
        }
        let preflight =
            Document::load_mem(bytes).map_err(|e| format!("PDF raster preflight failed: {e}"))?;
        let budgets = Self::preflight(&preflight, selected)?;
        drop(preflight);
        Self::from_preflight(bytes.to_vec(), budgets)
    }

    pub(super) fn preflight(doc: &Document, selected: Option<u32>) -> Result<PageBudgets, String> {
        let pages = doc.get_pages();
        if pages.is_empty() || pages.len() > MAX_PAGES {
            return Err(format!(
                "PDF raster page count must be between 1 and {MAX_PAGES}"
            ));
        }
        Ok(pages
            .iter()
            .map(|(&number, &id)| {
                selected
                    .is_none_or(|page| page == number)
                    .then(|| preflight_images(doc, id))
            })
            .collect())
    }

    pub(super) fn from_preflight(
        bytes: Vec<u8>,
        page_budgets: PageBudgets,
    ) -> Result<Self, String> {
        let pdf = Pdf::new(bytes).map_err(|e| format!("PDF raster parser failed: {e:?}"))?;
        if pdf.pages().len() != page_budgets.len() {
            return Err("PDF parsers disagree about raster page inventory".into());
        }
        Ok(Self { pdf, page_budgets })
    }

    fn check_page_budget(&self, index: usize) -> Result<(), String> {
        self.page_budgets
            .get(index)
            .ok_or_else(|| format!("PDF raster page {} does not exist", index + 1))?
            .as_ref()
            .ok_or_else(|| format!("PDF raster page {} was not selected", index + 1))?
            .as_ref()
            .map(|_| ())
            .map_err(Clone::clone)
    }

    /// Release decoded scans after each page, while reusing both PDF parsers.
    pub fn render(&self, page_index: usize, max_dimension: u32) -> Result<RasterImage, String> {
        self.session().render(page_index, max_dimension)
    }

    /// Render an original source region without allocating a high-resolution full page.
    /// Bounds are fractions of the displayed, rotated crop box, with a top-left origin.
    pub fn render_region(
        &self,
        page_index: usize,
        region: [f64; 4],
        max_dimension: u32,
    ) -> Result<RasterImage, String> {
        self.session()
            .render_region(page_index, region, max_dimension)
    }

    /// Explicit cache reuse for callers that know pages share the same images.
    /// This retains decoded images until dropped; normal scanning should use
    /// `Renderer::render` so page-local scans are released promptly.
    pub fn session(&self) -> RenderSession<'_> {
        RenderSession {
            renderer: self,
            cache: RenderCache::new(),
        }
    }
}

pub struct RenderSession<'a> {
    renderer: &'a Renderer,
    cache: RenderCache<'a>,
}

/// Render a zero-based page index, fitting its rotated crop box within
/// `max_dimension`. Unsupported image decoding is an error, never a blank scan.
pub fn render(bytes: &[u8], page_index: usize, max_dimension: u32) -> Result<RasterImage, String> {
    Renderer::new(bytes)?.render(page_index, max_dimension)
}

impl RenderSession<'_> {
    /// Uniformly scale an original PDF region. Outside-page context remains white.
    /// The region may extend outside the page, but zoom and allocations are bounded.
    /// Images, text, clipping and vector linework use the same renderer as scanning.
    pub fn render_region(
        &self,
        page_index: usize,
        region: [f64; 4],
        max_dimension: u32,
    ) -> Result<RasterImage, String> {
        if !(1..=2048).contains(&max_dimension)
            || region
                .iter()
                .any(|v| !v.is_finite() || v.abs() > 1_000_000.)
            || region[0] >= region[2]
            || region[1] >= region[3]
        {
            return Err("invalid PDF source-region dimensions or bounds".into());
        }
        self.renderer.check_page_budget(page_index)?;
        let page = &self.renderer.pdf.pages()[page_index];
        let (pw, ph) = page.render_dimensions();
        let x = region[0] * f64::from(pw);
        let y = region[1] * f64::from(ph);
        let w = (region[2] - region[0]) * f64::from(pw);
        let h = (region[3] - region[1]) * f64::from(ph);
        if ![x, y, w, h].iter().all(|v| v.is_finite())
            || w <= 0.
            || h <= 0.
            || w < f64::from(pw) / 16384.
            || h < f64::from(ph) / 16384.
        {
            return Err("PDF source-region zoom exceeds supported bounds".into());
        }
        let scale = max_dimension as f32 / (w.max(h) as f32);
        let width = (w as f32 * scale) as usize;
        let height = (h as f32 * scale) as usize;
        if width == 0 || height == 0 || width.saturating_mul(height) > 2048 * 2048 {
            return Err("PDF source region exceeds output pixel limit or is too narrow".into());
        }
        let failures = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&failures);
        let interpreter = InterpreterSettings {
            warning_sink: Arc::new(move |warning| {
                // A fallback font changes classifier pixels. Source-review
                // crops must report/withhold this rather than silently claim fidelity.
                if let Ok(mut messages) = captured.lock()
                    && messages.len() < 8
                {
                    messages.push(format!("{warning:?}"));
                }
            }),
            ..InterpreterSettings::default()
        };
        let pixmap = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            use hayro::hayro_interpret::util::TransformExt;
            use hayro::kurbo::Affine;
            use hayro::vello_cpu::{
                Pixmap, RasterizerSettings, RenderContext, Resources, TargetInit,
            };
            let mut ctx = RenderContext::new(width as u16, height as u16);
            let s = f64::from(scale);
            let transform = Affine::translate((-x * s, -y * s))
                * Affine::scale(s)
                * page.initial_transform(true).to_kurbo();
            hayro::render_into(
                page,
                &self.cache,
                &interpreter,
                &RenderSettings::default(),
                &mut ctx,
                transform,
            );
            ctx.flush();
            let mut pixels = Pixmap::new(ctx.width(), ctx.height());
            ctx.render_with(
                &mut pixels,
                &mut Resources::default(),
                RasterizerSettings {
                    target_init: TargetInit::Clear(WHITE),
                    ..Default::default()
                },
            );
            pixels
        }))
        .map_err(|_| "PDF region renderer failed while composing source pixels".to_string())?;
        let failures = failures
            .lock()
            .map_err(|_| "PDF region warning capture failed")?;
        if !failures.is_empty() {
            return Err(format!(
                "PDF region composition incomplete: {}",
                failures.join(", ")
            ));
        }
        let rgba = pixmap.take_rgba8(ImageAlphaType::AlphaPremultiplied);
        if rgba.len() != width * height * 4 {
            return Err("invalid PDF region RGBA buffer".into());
        }
        Ok(RasterImage {
            width,
            height,
            rgba,
        })
    }

    pub fn render(&self, page_index: usize, max_dimension: u32) -> Result<RasterImage, String> {
        if !(1..=MAX_DIMENSION).contains(&max_dimension) {
            return Err(format!(
                "PDF raster dimension must be between 1 and {MAX_DIMENSION}"
            ));
        }
        self.renderer.check_page_budget(page_index)?;
        let pages = self.renderer.pdf.pages();
        let page = pages
            .get(page_index)
            .ok_or_else(|| format!("PDF raster page {} does not exist", page_index + 1))?;
        let (page_width, page_height) = page.render_dimensions();
        if !page_width.is_finite()
            || !page_height.is_finite()
            || page_width <= 0.0
            || page_height <= 0.0
        {
            return Err("PDF raster page has invalid dimensions".into());
        }
        let scale = max_dimension as f32 / page_width.max(page_height);
        // hayro truncates the scaled dimensions, so match its allocation exactly.
        let width = (page_width * scale) as usize;
        let height = (page_height * scale) as usize;
        let pixels = width
            .checked_mul(height)
            .filter(|n| *n > 0 && *n <= MAX_OUTPUT_PIXELS)
            .ok_or("PDF raster page exceeds the 32 megapixel output limit or is too narrow")?;
        let failures = Arc::new(Mutex::new(Vec::new()));
        let captured = Arc::clone(&failures);
        let interpreter = InterpreterSettings {
            warning_sink: Arc::new(move |warning| {
                // Missing font fallback does not invalidate image geometry. A missing
                // image or visible annotation does, so do not silently omit either.
                if !matches!(warning, InterpreterWarning::UnsupportedFont)
                    && let Ok(mut messages) = captured.lock()
                    && messages.len() < 8
                {
                    messages.push(format!("{warning:?}"));
                }
            }),
            ..InterpreterSettings::default()
        };
        let pixmap = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| {
            hayro::render(
                page,
                &self.cache,
                &interpreter,
                &RenderSettings::default(),
                &PixmapSettings {
                    x_scale: scale,
                    y_scale: scale,
                    bg_color: WHITE,
                },
            )
        }))
        .map_err(|_| "PDF raster renderer failed while composing the page".to_string())?;
        let failures = failures
            .lock()
            .map_err(|_| "PDF raster warning capture failed")?;
        if !failures.is_empty() {
            return Err(format!(
                "PDF raster composition incomplete: {}",
                failures.join(", ")
            ));
        }
        if pixmap.width() as usize != width || pixmap.height() as usize != height {
            return Err("PDF raster renderer returned unexpected dimensions".into());
        }
        // White composition is opaque, so its premultiplied and ordinary RGBA
        // representations coincide. Transfer ownership instead of copying ~40 MiB.
        let rgba = pixmap.take_rgba8(ImageAlphaType::AlphaPremultiplied);
        if rgba.len() != pixels * 4 {
            return Err("PDF raster renderer returned an invalid RGBA buffer".into());
        }
        Ok(RasterImage {
            width,
            height,
            rgba,
        })
    }
}

/// Count only painted image invocations, not unused shared resource dictionaries.
/// Forms are interpreted under their own resource scope or their invoking scope.
fn preflight_images(doc: &Document, id: ObjectId) -> Result<(), String> {
    let resources = page_resources(doc, id)?;
    let content = doc
        .get_page_content_with_limit(id, MAX_CONTENT_BYTES)
        .map_err(|e| format!("PDF raster page content preflight failed: {e}"))?;
    // Most vector-led pages have a small image/logo dictionary. A conservative
    // bound over that dictionary avoids parsing their vector operations again.
    // The raw BI marker check deliberately tolerates false positives in strings;
    // any possible inline image takes the complete bounded traversal instead.
    // Forms require invocation traversal: absent Resources can inherit a scope
    // containing the Form itself, so resource metadata alone cannot rule out a cycle.
    if content.len() <= MAX_CONTENT_OPERATIONS
        && !content.windows(2).any(|bytes| bytes == b"BI")
        && let Some(resources) = resources
        && ImageBudget::default()
            .simple_resource_bound(doc, resources)
            .is_ok()
    {
        return Ok(());
    }
    ImageBudget::default().content(doc, &content, resources, 0)
}

fn resolved_dictionary<'a>(
    doc: &'a Document,
    object: &'a Object,
) -> Result<&'a Dictionary, String> {
    doc.dereference(object)
        .map_err(|e| format!("PDF raster resource lookup failed: {e}"))?
        .1
        .as_dict()
        .map_err(|e| format!("PDF raster resource dictionary invalid: {e}"))
}

/// Resources are an inheritable page attribute; the nearest dictionary replaces
/// the ancestor's complete dictionary rather than merging unused ancestors.
fn page_resources(doc: &Document, mut id: ObjectId) -> Result<Option<&Dictionary>, String> {
    let mut visited = BTreeSet::new();
    for _ in 0..48 {
        if !visited.insert(id) {
            return Err("Cyclic PDF raster page resource ancestry".into());
        }
        let page = doc
            .get_dictionary(id)
            .map_err(|e| format!("PDF raster page resources unavailable: {e}"))?;
        if let Ok(object) = page.get(b"Resources") {
            let object = doc
                .dereference(object)
                .map_err(|e| format!("PDF raster page resource lookup failed: {e}"))?
                .1;
            if !matches!(object, Object::Null) {
                return resolved_dictionary(doc, object).map(Some);
            }
        }
        let Ok(parent) = page.get(b"Parent").and_then(Object::as_reference) else {
            return Ok(None);
        };
        id = parent;
    }
    Err("PDF raster page resource ancestry exceeds nesting limit".into())
}

#[derive(Default)]
struct ImageBudget {
    image_channels: BTreeMap<ObjectId, u8>,
    active_forms: BTreeSet<ObjectId>,
    // A Form without Resources can bind to different images in different
    // invocation scopes. Cache each resolved scope, not just each Form ID.
    visited_forms: BTreeSet<(ObjectId, usize)>,
    objects: usize,
    operations: usize,
    content_bytes: usize,
    source_bytes: u64,
}

impl ImageBudget {
    fn simple_resource_bound(
        &mut self,
        doc: &Document,
        resources: &Dictionary,
    ) -> Result<(), String> {
        let Ok(objects) = resources.get(b"XObject") else {
            return Ok(());
        };
        let objects = resolved_dictionary(doc, objects)?;
        for (_, object) in objects.iter() {
            self.objects += 1;
            if self.objects > MAX_RESOURCE_OBJECTS {
                return Err("PDF raster resource upper-bound limit exceeded".into());
            }
            let (id, object) = doc
                .dereference(object)
                .map_err(|e| format!("PDF raster XObject lookup failed: {e}"))?;
            let Ok(stream) = object.as_stream() else {
                continue;
            };
            match stream.dict.get(b"Subtype").and_then(Object::as_name).ok() {
                Some(b"Image") => self.image(doc, id, stream, 0, false, false)?,
                Some(b"Form") => return Err("Form content needs invocation preflight".into()),
                _ => {}
            }
        }
        Ok(())
    }

    fn content(
        &mut self,
        doc: &Document,
        bytes: &[u8],
        resources: Option<&Dictionary>,
        depth: usize,
    ) -> Result<(), String> {
        self.content_bytes = self
            .content_bytes
            .checked_add(bytes.len())
            .filter(|n| *n <= MAX_CONTENT_BYTES)
            .ok_or("PDF raster content preflight exceeds the 48 MiB expanded-content budget")?;
        if depth > 48 {
            return Err("PDF raster resource nesting limit exceeded".into());
        }
        for op in Content::decode_iter(bytes) {
            let op = op.map_err(|e| format!("PDF raster content preflight failed: {e}"))?;
            self.operations += 1;
            if self.operations > MAX_CONTENT_OPERATIONS {
                return Err("PDF raster content operation limit exceeded".into());
            }
            match (op.operator.as_str(), op.operands.as_slice()) {
                ("Do", [Object::Name(name)]) => {
                    let Some(resources) = resources else {
                        continue;
                    };
                    let Ok(objects) = resources.get(b"XObject") else {
                        continue;
                    };
                    let objects = resolved_dictionary(doc, objects)?;
                    let Ok(object) = objects.get(name) else {
                        continue;
                    };
                    self.invocation(doc, object, Some(resources), depth + 1)?;
                }
                ("BI", [Object::Stream(image)]) => {
                    self.image(doc, None, image, depth + 1, true, false)?
                }
                // lopdf deliberately emits an empty BI operation for inline
                // encodings it cannot expose (including filtered inline data).
                // Never hand unknown dimensions to the renderer unbudgeted.
                ("BI", _) => {
                    return Err("PDF raster inline image metadata cannot be bounded safely".into());
                }
                _ => {}
            }
        }
        Ok(())
    }

    fn invocation(
        &mut self,
        doc: &Document,
        object: &Object,
        parent_resources: Option<&Dictionary>,
        depth: usize,
    ) -> Result<(), String> {
        if depth > 48 {
            return Err("PDF raster resource nesting limit exceeded".into());
        }
        self.objects += 1;
        if self.objects > MAX_RESOURCE_OBJECTS {
            return Err("PDF raster invoked-object limit exceeded".into());
        }
        let (id, object) = doc
            .dereference(object)
            .map_err(|e| format!("PDF raster XObject lookup failed: {e}"))?;
        let Ok(stream) = object.as_stream() else {
            return Ok(());
        };
        match stream.dict.get(b"Subtype").and_then(Object::as_name).ok() {
            Some(b"Image") => self.image(doc, id, stream, depth, false, false),
            Some(b"Form") => {
                if id.is_some_and(|id| self.active_forms.contains(&id)) {
                    return Err("Cyclic PDF raster Form invocation".into());
                }
                let resources = match stream.dict.get(b"Resources") {
                    Ok(object) if !matches!(object, Object::Null) => {
                        Some(resolved_dictionary(doc, object)?)
                    }
                    _ => parent_resources,
                };
                let scope = resources.map_or(0, |r| r as *const Dictionary as usize);
                if let Some(id) = id {
                    if !self.visited_forms.insert((id, scope)) {
                        return Ok(());
                    }
                    self.active_forms.insert(id);
                }
                let remaining = MAX_CONTENT_BYTES.saturating_sub(self.content_bytes);
                let decoded = match stream.decompressed_content_with_limit(remaining) {
                    Ok(bytes) => bytes,
                    Err(_) if !stream.dict.has(b"Filter") && stream.content.len() <= remaining => {
                        stream.content.clone()
                    }
                    Err(e) => return Err(format!("PDF raster Form content preflight failed: {e}")),
                };
                let result = self.content(doc, &decoded, resources, depth + 1);
                if let Some(id) = id {
                    self.active_forms.remove(&id);
                }
                result
            }
            _ => Ok(()),
        }
    }

    fn image(
        &mut self,
        doc: &Document,
        id: Option<ObjectId>,
        stream: &Stream,
        depth: usize,
        inline: bool,
        mask_role: bool,
    ) -> Result<(), String> {
        if depth > 48 {
            return Err("PDF raster image-mask nesting limit exceeded".into());
        }
        let dictionary = &stream.dict;
        let is_stencil = dictionary
            .get(b"ImageMask")
            .and_then(Object::as_bool)
            .unwrap_or(false);
        let gray_mask = mask_role
            && (is_stencil
                || dictionary
                    .get(b"ColorSpace")
                    .and_then(Object::as_name)
                    .is_ok_and(|name| name == b"DeviceGray")
                    && dictionary
                        .get(b"BitsPerComponent")
                        .and_then(Object::as_i64)
                        .is_ok_and(|bpc| matches!(bpc, 1 | 8)));
        // Hayro decode_mask yields LumaData Vec<u8> for validated single-channel
        // masks. Ordinary Do images reserve RGBA even when gray. If the same
        // object later appears as an ordinary image, charge its role upgrade.
        let channels = if gray_mask { 1 } else { 4 };
        let previous = id
            .and_then(|id| self.image_channels.get(&id))
            .copied()
            .unwrap_or(0);
        if previous >= channels {
            return Ok(());
        }
        let number = |long: &[u8], short: &[u8]| {
            dictionary
                .get(long)
                .or_else(|_| {
                    if inline {
                        dictionary.get(short)
                    } else {
                        dictionary.get(long)
                    }
                })
                .and_then(Object::as_i64)
                .unwrap_or(0)
        };
        let pixels = u64::try_from(number(b"Width", b"W"))
            .ok()
            .zip(u64::try_from(number(b"Height", b"H")).ok())
            .and_then(|(w, h)| w.checked_mul(h))
            .filter(|n| *n > 0 && *n <= MAX_IMAGE_PIXELS)
            .ok_or("PDF raster source image exceeds the 32 megapixel image limit or is invalid")?;
        let bytes = pixels
            .checked_mul(u64::from(channels - previous))
            .ok_or("PDF raster image byte budget overflow")?;
        self.source_bytes = self
            .source_bytes
            .checked_add(bytes)
            .ok_or("PDF raster image byte budget overflow")?;
        if self.source_bytes > MAX_PAGE_IMAGE_BYTES {
            return Err(format!(
                "PDF raster page exceeds the 256 MiB decoded source-image budget ({} estimated source bytes)",
                self.source_bytes
            ));
        }
        if let Some(id) = id {
            self.image_channels.insert(id, channels);
        }
        // Upgrading a role must not traverse its already-counted mask tree again.
        if previous > 0 {
            return Ok(());
        }
        for key in [b"SMask".as_slice(), b"Mask".as_slice()] {
            if let Ok(mask) = dictionary.get(key) {
                let (id, object) = doc
                    .dereference(mask)
                    .map_err(|e| format!("PDF raster image mask lookup failed: {e}"))?;
                if let Ok(mask) = object.as_stream() {
                    self.image(doc, id, mask, depth + 1, false, true)?;
                }
            }
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use lopdf::{Stream, dictionary};

    fn fixture(content: &[u8], rotation: i64, crop: Option<Vec<Object>>) -> Vec<u8> {
        let mut doc = Document::with_version("1.7");
        let pages = doc.new_object_id();
        let image = doc.add_object(Stream::new(
            dictionary! { "Type" => "XObject", "Subtype" => "Image", "Width" => 1,
            "Height" => 1, "BitsPerComponent" => 8, "ColorSpace" => "DeviceRGB" },
            vec![0, 0, 0],
        ));
        let mask = doc.add_object(Stream::new(
            dictionary! { "Type" => "XObject", "Subtype" => "Image", "Width" => 1,
            "Height" => 1, "BitsPerComponent" => 1, "ImageMask" => true },
            vec![0],
        ));
        let contents = doc.add_object(Stream::new(dictionary! {}, content.to_vec()));
        let mut page = dictionary! {
            "Type" => "Page", "Parent" => pages, "MediaBox" => vec![0.into(),0.into(),100.into(),80.into()],
            "Rotate" => rotation, "Contents" => contents,
            "Resources" => dictionary! { "XObject" => dictionary! { "Im" => image, "Mask" => mask } },
        };
        if let Some(crop) = crop {
            page.set("CropBox", crop);
        }
        let page = doc.add_object(page);
        doc.objects.insert(
            pages,
            dictionary! { "Type"=>"Pages", "Kids"=>vec![page.into()], "Count"=>1 }.into(),
        );
        let catalog = doc.add_object(dictionary! { "Type"=>"Catalog", "Pages"=>pages });
        doc.trailer.set("Root", catalog);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        bytes
    }

    fn pixel(image: &RasterImage, x: usize, y: usize) -> &[u8] {
        &image.rgba[(y * image.width + x) * 4..][..4]
    }

    #[test]
    fn source_region_matches_full_page_with_crop_and_rotation() {
        for rotation in [0, 90, 180, 270] {
            let bytes = fixture(
                b"q 20 0 0 20 10 10 cm /Im Do Q q 1 0 0 rg 20 0 0 20 60 10 cm /Mask Do Q",
                rotation,
                Some(vec![10.into(), 10.into(), 90.into(), 70.into()]),
            );
            let renderer = Renderer::new(&bytes).unwrap();
            let page = renderer.render(0, 100).unwrap();
            let region = renderer.render_region(0, [0., 0., 1., 1.], 100).unwrap();
            assert_eq!((page.width, page.height), (region.width, region.height));
            assert_eq!(page.rgba, region.rgba, "rotation={rotation}");
        }
    }

    #[test]
    fn source_region_keeps_white_context_and_native_vector_detail() {
        let bytes = fixture(b"0 g 0 60 20 20 re f 45 20 0.1 8 re f", 0, None);
        let renderer = Renderer::new(&bytes).unwrap();
        let padded = renderer
            .render_region(0, [-0.1, -0.125, 0.3, 0.375], 40)
            .unwrap();
        assert_eq!((padded.width, padded.height), (40, 40));
        assert_eq!(pixel(&padded, 1, 1), [255, 255, 255, 255]);
        assert_eq!(pixel(&padded, 20, 20), [0, 0, 0, 255]);
        let detail = renderer
            .render_region(0, [0.4, 0.625, 0.5, 0.75], 200)
            .unwrap();
        assert_eq!((detail.width, detail.height), (200, 200));
        assert!(
            detail.rgba.chunks_exact(4).filter(|p| p[0] < 10).count() > 200,
            "original narrow vector must be rendered at region resolution"
        );
        let red = fixture(b"q 1 0 0 rg 20 0 0 20 60 10 cm /Mask Do Q", 0, None);
        let rendered = Renderer::new(&red)
            .unwrap()
            .render_region(0, [0.6, 0.625, 0.8, 0.875], 100)
            .unwrap();
        assert_eq!(pixel(&rendered, 50, 50), [255, 0, 0, 255]);
    }

    #[test]
    fn source_region_rejects_unbounded_or_invalid_requests() {
        let bytes = fixture(b"0 g 10 10 20 20 re f", 0, None);
        let renderer = Renderer::new(&bytes).unwrap();
        for bounds in [[0., 0., 0., 1.], [0., 0., f64::NAN, 1.], [0., 0., 1e-9, 1.]] {
            assert!(renderer.render_region(0, bounds, 96).is_err());
        }
        assert!(renderer.render_region(0, [0., 0., 1., 1.], 0).is_err());
        assert!(renderer.render_region(0, [0., 0., 1., 1.], 2049).is_err());
        assert!(renderer.render_region(1, [0., 0., 1., 1.], 96).is_err());
    }

    #[test]
    fn source_region_withholds_unreported_font_substitution() {
        let bytes = fixture(b"BT /F1 10 Tf 20 20 Td (source text) Tj ET", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let font=doc.add_object(dictionary!{"Type"=>"Font","Subtype"=>"UnsupportedSyntheticFont","BaseFont"=>"UnknownFace"});
        let id = *doc.get_pages().get(&1).unwrap();
        let mut resources = page_resources(&doc, id).unwrap().unwrap().clone();
        resources.set("Font", dictionary! {"F1"=>font});
        doc.get_dictionary_mut(id)
            .unwrap()
            .set("Resources", resources);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        // Geometry scanning keeps its established fallback policy.
        assert!(Renderer::new(&bytes).unwrap().render(0, 100).is_ok());
        let error = Renderer::new(&bytes)
            .unwrap()
            .render_region(0, [0., 0., 1., 1.], 100)
            .unwrap_err();
        assert!(error.contains("UnsupportedFont"), "{error}");
    }

    #[test]
    fn composes_tiles_and_stencil_on_white() {
        let bytes = fixture(b"q 20 0 0 20 10 10 cm /Im Do Q q 20 0 0 20 30 10 cm /Im Do Q q 1 0 0 rg 20 0 0 20 60 10 cm /Mask Do Q", 0, None);
        let image = render(&bytes, 0, 100).unwrap();
        assert_eq!((image.width, image.height), (100, 80));
        assert_eq!(pixel(&image, 20, 60), [0, 0, 0, 255]);
        assert_eq!(pixel(&image, 40, 60), [0, 0, 0, 255]);
        assert_eq!(pixel(&image, 70, 60), [255, 0, 0, 255]);
        assert_eq!(pixel(&image, 90, 10), [255, 255, 255, 255]);
    }

    #[test]
    fn rotates_and_crops_before_scaling() {
        let crop = vec![10.into(), 10.into(), 90.into(), 70.into()];
        let bytes = fixture(b"0 g 10 10 20 20 re f", 90, Some(crop));
        let image = render(&bytes, 0, 80).unwrap();
        assert_eq!((image.width, image.height), (60, 80));
        assert_eq!(pixel(&image, 10, 10), [0, 0, 0, 255]);
        assert_eq!(pixel(&image, 50, 70), [255, 255, 255, 255]);
    }

    #[test]
    fn honors_image_clip_and_rejects_invalid_requests() {
        let bytes = fixture(b"q 0 0 25 80 re W n 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let image = render(&bytes, 0, 100).unwrap();
        assert_eq!(pixel(&image, 10, 40), [0, 0, 0, 255]);
        assert_eq!(pixel(&image, 40, 40), [255, 255, 255, 255]);
        assert!(
            render(&bytes, 1, 100)
                .unwrap_err()
                .contains("does not exist")
        );
        assert!(render(&bytes, 0, 0).is_err());
        assert!(render(&bytes, 0, MAX_DIMENSION + 1).is_err());
        assert!(render(b"not a PDF", 0, 100).is_err());
    }

    #[test]
    fn rejects_large_image_metadata_before_pixel_allocation() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let image = doc
            .objects
            .values_mut()
            .find_map(|object| {
                let stream = object.as_stream_mut().ok()?;
                (stream.dict.get(b"ColorSpace").is_ok()).then_some(stream)
            })
            .unwrap();
        image.dict.set("Width", (MAX_IMAGE_PIXELS + 1) as i64);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        assert!(
            render(&bytes, 0, 100)
                .unwrap_err()
                .contains("32 megapixel image limit")
        );
    }

    #[test]
    fn preflight_errors_remain_local_when_reusing_a_multipage_document() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let page_id = *doc.get_pages().get(&1).unwrap();
        let mut page = doc.get_dictionary(page_id).unwrap().clone();
        let parent_id = page.get(b"Parent").unwrap().as_reference().unwrap();
        let oversized = doc.add_object(Stream::new(
            dictionary! {"Type"=>"XObject", "Subtype"=>"Image",
            "Width"=>(MAX_IMAGE_PIXELS+1) as i64, "Height"=>1,
            "BitsPerComponent"=>8, "ColorSpace"=>"DeviceRGB"},
            vec![],
        ));
        page.set(
            "Resources",
            dictionary! {"XObject"=>dictionary! {"Im"=>oversized}},
        );
        let second_page = doc.add_object(page);
        let tree = doc.get_dictionary_mut(parent_id).unwrap();
        tree.set("Kids", vec![page_id.into(), second_page.into()]);
        tree.set("Count", 2);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();

        let renderer = Renderer::new(&bytes).unwrap();
        assert_eq!(
            pixel(&renderer.render(0, 100).unwrap(), 20, 20),
            [0, 0, 0, 255]
        );
        assert!(
            renderer
                .render(1, 100)
                .unwrap_err()
                .contains("32 megapixel image limit")
        );
        assert_eq!(
            pixel(&renderer.render(0, 50).unwrap(), 10, 10),
            [0, 0, 0, 255]
        );
    }

    #[test]
    fn image_decode_failure_is_explicit_and_parsed_document_can_be_reused() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let renderer = Renderer::new(&bytes).unwrap();
        let first = renderer.render(0, 100).unwrap();
        let second = renderer.render(0, 50).unwrap();
        assert_eq!((first.width, first.height), (100, 80));
        assert_eq!((second.width, second.height), (50, 40));
        let mut doc = Document::load_mem(&bytes).unwrap();
        let image = doc
            .objects
            .values_mut()
            .find_map(|object| {
                let stream = object.as_stream_mut().ok()?;
                (stream.dict.get(b"ColorSpace").is_ok()).then_some(stream)
            })
            .unwrap();
        image.dict.set("Filter", "DCTDecode");
        let mut invalid = Vec::new();
        doc.save_to(&mut invalid).unwrap();
        assert!(
            render(&invalid, 0, 100)
                .unwrap_err()
                .contains("ImageDecodeFailure")
        );
    }

    #[test]
    fn shared_unused_images_are_not_charged_until_invoked() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let page_id = *doc.get_pages().get(&1).unwrap();
        let unused = doc.add_object(Stream::new(
            dictionary! {
                "Type"=>"XObject", "Subtype"=>"Image", "Width"=>(MAX_IMAGE_PIXELS+1) as i64,
                "Height"=>1, "BitsPerComponent"=>8, "ColorSpace"=>"DeviceRGB"
            },
            vec![],
        ));
        let mut resources = page_resources(&doc, page_id).unwrap().unwrap().clone();
        resources
            .get_mut(b"XObject")
            .unwrap()
            .as_dict_mut()
            .unwrap()
            .set("Unused", unused);
        doc.get_dictionary_mut(page_id)
            .unwrap()
            .set("Resources", resources);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        assert_eq!(
            pixel(&render(&bytes, 0, 100).unwrap(), 20, 20),
            [0, 0, 0, 255]
        );

        let content = doc.add_object(Stream::new(dictionary! {}, b"/Unused Do".to_vec()));
        doc.get_dictionary_mut(page_id)
            .unwrap()
            .set("Contents", content);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        assert!(
            render(&bytes, 0, 100)
                .unwrap_err()
                .contains("32 megapixel image limit")
        );
    }

    #[test]
    fn forms_use_invoking_resources_and_reject_active_cycles() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /F Do /F Do Q", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let page_id = *doc.get_pages().get(&1).unwrap();
        let form=doc.add_object(Stream::new(dictionary!{
            "Type"=>"XObject","Subtype"=>"Form","BBox"=>vec![0.into(),0.into(),1.into(),1.into()]
        },b"/Im Do".to_vec()));
        let mut resources = page_resources(&doc, page_id).unwrap().unwrap().clone();
        resources
            .get_mut(b"XObject")
            .unwrap()
            .as_dict_mut()
            .unwrap()
            .set("F", form);
        // Move resources into the parent Pages node, exercising page inheritance too.
        let page = doc.get_dictionary_mut(page_id).unwrap();
        let parent = page.get(b"Parent").unwrap().as_reference().unwrap();
        page.remove(b"Resources");
        doc.get_dictionary_mut(parent)
            .unwrap()
            .set("Resources", resources);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        assert_eq!(
            pixel(&render(&bytes, 0, 100).unwrap(), 20, 20),
            [0, 0, 0, 255]
        );
        let stream = doc.get_object_mut(form).unwrap().as_stream_mut().unwrap();
        stream.content = b"/Self Do".to_vec();
        stream.dict.set(
            "Resources",
            dictionary! {"XObject"=>dictionary!{"Self"=>form}},
        );
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        assert!(
            render(&bytes, 0, 100)
                .unwrap_err()
                .contains("Cyclic PDF raster Form")
        );
    }

    #[test]
    fn grayscale_masks_use_one_channel_and_color_role_upgrades_are_charged() {
        let mut doc = Document::with_version("1.7");
        let mask = doc.add_object(Stream::new(
            dictionary! {
                "Type"=>"XObject","Subtype"=>"Image","Width"=>8192,"Height"=>4096,
                "BitsPerComponent"=>8,"ColorSpace"=>"DeviceGray"
            },
            vec![],
        ));
        let image = doc.add_object(Stream::new(
            dictionary! {
                "Type"=>"XObject","Subtype"=>"Image","Width"=>8192,"Height"=>4096,
                "BitsPerComponent"=>8,"ColorSpace"=>"DeviceRGB","SMask"=>mask
            },
            vec![],
        ));
        let resources = dictionary! {"XObject"=>dictionary!{"Im"=>image,"Mask"=>mask}};
        let mut budget = ImageBudget::default();
        // These are metadata tests: the 32MP placeholder data is never decoded.
        budget
            .content(&doc, b"/Im Do /Im Do", Some(&resources), 0)
            .unwrap();
        assert_eq!(budget.source_bytes, MAX_IMAGE_PIXELS * 5);
        budget
            .content(&doc, b"/Mask Do", Some(&resources), 0)
            .unwrap();
        assert_eq!(budget.source_bytes, MAX_IMAGE_PIXELS * 8);
        let extra = doc.add_object(Stream::new(
            dictionary! {
                "Type"=>"XObject","Subtype"=>"Image","Width"=>1,"Height"=>1,
                "BitsPerComponent"=>8,"ColorSpace"=>"DeviceRGB"
            },
            vec![],
        ));
        assert!(
            budget
                .invocation(&doc, &extra.into(), None, 0)
                .unwrap_err()
                .contains("256 MiB")
        );
    }

    #[test]
    fn inline_image_candidates_cannot_bypass_the_resource_fast_path() {
        let bytes = fixture(b"q 100 0 0 80 0 0 cm /Im Do Q", 0, None);
        let mut doc = Document::load_mem(&bytes).unwrap();
        let page_id = *doc.get_pages().get(&1).unwrap();
        let stream = Stream::new(
            dictionary! {
                "Width"=>(MAX_IMAGE_PIXELS+1) as i64,"Height"=>1,
                "BitsPerComponent"=>8,"ColorSpace"=>"DeviceRGB","Filter"=>"DCTDecode"
            },
            vec![0xff, 0xd8, 0xff, 0xd9],
        );
        let content = Content {
            operations: vec![lopdf::content::Operation::new(
                "BI",
                vec![Object::Stream(stream)],
            )],
        }
        .encode()
        .unwrap();
        let content_id = doc.add_object(Stream::new(dictionary! {}, content));
        doc.get_dictionary_mut(page_id)
            .unwrap()
            .set("Contents", content_id);
        let mut bytes = Vec::new();
        doc.save_to(&mut bytes).unwrap();
        let renderer = Renderer::new(&bytes).unwrap();
        // Existing ordinary XObjects fit the fast bound, but BI forces bounded
        // inline parsing and rejection before invoking Hayro pixel allocation.
        assert!(matches!(renderer.page_budgets[0], Some(Err(_))));
        assert!(renderer.render(0, 100).is_err());
    }

    /// Explicit local development probe; no private path or drawing is built in.
    #[test]
    #[ignore]
    fn development_render_all_pages() {
        let path =
            std::env::var("OPT_RENDER_DEV_PDF").expect("Select a development PDF explicitly");
        let bytes = std::fs::read(path).unwrap();
        let renderer = Renderer::new(&bytes).unwrap();
        let doc = Document::load_mem(&bytes).unwrap();
        let page_ids: Vec<_> = doc.get_pages().into_values().collect();
        for page in 0..renderer.page_budgets.len() {
            let resources = page_resources(&doc, page_ids[page]).unwrap();
            let content = doc
                .get_page_content_with_limit(page_ids[page], MAX_CONTENT_BYTES)
                .unwrap();
            let mut budget = ImageBudget::default();
            budget.content(&doc, &content, resources, 0).unwrap();
            let now = std::time::Instant::now();
            let image = renderer.render(page, 3200).unwrap();
            eprintln!(
                "page {}: {}x{}; {} ms; {} active image/mask objects; {} estimated source bytes",
                page + 1,
                image.width,
                image.height,
                now.elapsed().as_millis(),
                budget.image_channels.len(),
                budget.source_bytes
            );
        }
    }
}
