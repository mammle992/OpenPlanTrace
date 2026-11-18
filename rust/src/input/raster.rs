//! Automatic PDF routing and pixel provenance. The original vectors remain authoritative.
mod extract;
pub mod render;

use crate::model::{Document, Point, Primitive, Shape};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum PdfRoute {
    Vector,
    Raster,
    Mixed,
}

/// Measured repeated diagonal material and finite source-ink face extents.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RasterHatch {
    pub crossing_count: usize,
    pub mean_pitch_pixels: f64,
    pub pitch_cv: f64,
    pub diagonal_support: f64,
    pub stationary_correlation: f64,
    pub contrast: f64,
    pub longitudinal_coverage: f64,
    pub face_ink_half_widths_pixels: [f64; 2],
}

fn is_zero(value: &u32) -> bool {
    *value == 0
}

/// Measurements from a rendered pair of faces, rather than an authored PDF pen width.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct RasterBand {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub hatch: Option<RasterHatch>,
    pub face_source_ids: [String; 2],
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub dark_fraction: f64,
    pub mean_luma: f64,
    pub boundary_coverage: [f64; 2],
    pub width_pixels: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PdfInput {
    #[serde(default, skip_serializing_if = "is_zero")]
    pub analysis_revision: u32,
    pub route: PdfRoute,
    pub vector_primitive_count: usize,
    pub source_image_ids: Vec<String>,
    /// Union of all image footprints; summing or taking the largest tile is incorrect.
    pub image_coverage: f64,
    pub render_width: usize,
    pub render_height: usize,
    pub threshold: u8,
    pub raster_source_ids: Vec<String>,
    pub bands: Vec<RasterBand>,
}

const COVERAGE_SIDE: usize = 256;
const MAX_RENDER_DIMENSION: u32 = 3200;

fn image_rectangles(primitives: &[Primitive]) -> Vec<(&str, Point, Point)> {
    primitives
        .iter()
        .filter_map(|p| match p.shape {
            Shape::Image { min, max } if max.x > min.x && max.y > min.y => {
                Some((p.id.as_str(), min, max))
            }
            _ => None,
        })
        .collect()
}

fn mask(
    width: usize,
    height: usize,
    page_width: f64,
    page_height: f64,
    primitives: &[Primitive],
) -> Vec<bool> {
    let mut mask = vec![false; width * height];
    let mut seen = HashSet::new();
    for (_, a, b) in image_rectangles(primitives) {
        let x0 = (a.x / page_width * width as f64).floor().max(0.) as usize;
        let x1 = (b.x / page_width * width as f64).ceil().max(0.) as usize;
        let y0 = (a.y / page_height * height as f64).floor().max(0.) as usize;
        let y1 = (b.y / page_height * height as f64).ceil().max(0.) as usize;
        if !seen.insert((x0.min(width), x1.min(width), y0.min(height), y1.min(height))) {
            continue;
        }
        if x0 == 0 && y0 == 0 && x1 >= width && y1 >= height {
            mask.fill(true);
            return mask;
        }
        for y in y0.min(height)..y1.min(height) {
            mask[y * width + x0.min(width)..y * width + x1.min(width)].fill(true);
        }
    }
    mask
}

fn vector_count(primitives: &[Primitive]) -> usize {
    primitives
        .iter()
        .filter(|p| {
            matches!(
                p.shape,
                Shape::Line { .. } | Shape::Polyline { .. } | Shape::Arc { .. }
            )
        })
        .count()
}

pub(super) fn interpret(bytes: &[u8], doc: &mut Document) -> Result<(), String> {
    let mut source_count: usize = doc.pages.iter().map(|p| p.primitives.len()).sum();
    let mut band_count = 0usize;
    let renderer = doc
        .pages
        .iter()
        .any(|p| !image_rectangles(&p.primitives).is_empty())
        .then(|| render::Renderer::new(bytes))
        .transpose()?;
    for (index, page) in doc.pages.iter_mut().enumerate() {
        let mut image_ids: Vec<_> = image_rectangles(&page.primitives)
            .iter()
            .map(|(id, _, _)| id.to_string())
            .collect();
        image_ids.sort();
        let vectors = vector_count(&page.primitives);
        let coverage = mask(
            COVERAGE_SIDE,
            COVERAGE_SIDE,
            page.width,
            page.height,
            &page.primitives,
        )
        .iter()
        .filter(|v| **v)
        .count() as f64
            / (COVERAGE_SIDE * COVERAGE_SIDE) as f64;
        let route = if image_ids.is_empty() {
            PdfRoute::Vector
        } else if vectors == 0 {
            PdfRoute::Raster
        } else {
            PdfRoute::Mixed
        };
        let mut info = PdfInput {
            analysis_revision: 1,
            route,
            vector_primitive_count: vectors,
            source_image_ids: image_ids,
            image_coverage: coverage,
            render_width: 0,
            render_height: 0,
            threshold: 0,
            raster_source_ids: vec![],
            bands: vec![],
        };
        if route != PdfRoute::Vector {
            // Spend fewer pixels on small raster assets without discarding their provenance.
            // Large tiled plans use the full budget regardless of individual tile size.
            let budget = if coverage < 0.025 && vectors > 0 {
                1024
            } else {
                MAX_RENDER_DIMENSION
            };
            let rendered = renderer
                .as_ref()
                .ok_or("missing PDF renderer")?
                .render(index, budget)
                .map_err(|e| {
                    format!("PDF page {} raster interpretation failed: {e}", page.number)
                })?;
            let eligible = mask(
                rendered.width,
                rendered.height,
                page.width,
                page.height,
                &page.primitives,
            );
            let extracted = extract::extract(
                rendered.width,
                rendered.height,
                &rendered.rgba,
                page.width,
                page.height,
                Some(&eligible),
            )?;
            // A raster face can coincide with a vector grid or a real vector
            // wall. Keep both source witnesses; deduplicate physical placements
            // only after vector authority has been evaluated independently.
            let raster = extracted.primitives;
            info.bands = extracted.bands;
            info.render_width = rendered.width;
            info.render_height = rendered.height;
            info.threshold = extracted.threshold;
            info.raster_source_ids = raster.iter().map(|p| p.id.clone()).collect();
            info.raster_source_ids.sort();
            source_count = source_count
                .checked_add(raster.len())
                .filter(|n| *n <= super::MAX_ITEMS)
                .ok_or(
                    "PDF document exceeds total source geometry budget after raster interpretation",
                )?;
            band_count = band_count
                .checked_add(info.bands.len())
                .filter(|n| *n <= super::MAX_ITEMS / 4)
                .ok_or("PDF document exceeds measured raster band budget")?;
            page.primitives.extend(raster);
            doc.warnings.push(format!("PDF page {} automatically selected {:?}: {:.1}% combined image coverage, {} vector primitives, {} pixel-derived faces.", page.number, route, coverage*100., vectors, info.raster_source_ids.len()));
        }
        page.pdf_input = Some(info);
    }
    doc.warnings
        .retain(|w| !w.starts_with("PDF image footprint retained; raster contents not vectorized"));
    Ok(())
}

pub fn validate(
    info: &PdfInput,
    sources: &[Primitive],
    width: f64,
    height: f64,
) -> Result<(), String> {
    if info.analysis_revision > 1 {
        return Err("unsupported raster analysis revision".into());
    }
    let ordered = |ids: &[String]| ids.windows(2).all(|w| w[0] < w[1]);
    if info.source_image_ids.len() > sources.len()
        || info.raster_source_ids.len() > sources.len()
        || !ordered(&info.source_image_ids)
        || !ordered(&info.raster_source_ids)
        || !info.image_coverage.is_finite()
        || !(0. ..=1.).contains(&info.image_coverage)
        || info.bands.len() > 100_000
    {
        return Err("invalid automatic PDF evidence inventory".into());
    }
    let raster: HashSet<_> = info.raster_source_ids.iter().map(String::as_str).collect();
    let source_index: HashMap<_, _> = sources
        .iter()
        .filter(|p| raster.contains(p.id.as_str()))
        .map(|p| (p.id.as_str(), p))
        .collect();
    let get = |id: &str| source_index.get(id).copied();
    let images = image_rectangles(sources);
    let mut image_ids: Vec<_> = images.iter().map(|(id, _, _)| id.to_string()).collect();
    image_ids.sort();
    let nvector = sources
        .iter()
        .filter(|p| {
            !raster.contains(p.id.as_str())
                && matches!(
                    p.shape,
                    Shape::Line { .. } | Shape::Polyline { .. } | Shape::Arc { .. }
                )
        })
        .count();
    let expected = if image_ids.is_empty() {
        PdfRoute::Vector
    } else if nvector == 0 {
        PdfRoute::Raster
    } else {
        PdfRoute::Mixed
    };
    let coverage = mask(COVERAGE_SIDE, COVERAGE_SIDE, width, height, sources)
        .iter()
        .filter(|v| **v)
        .count() as f64
        / (COVERAGE_SIDE * COVERAGE_SIDE) as f64;
    if info.route != expected
        || image_ids != info.source_image_ids
        || nvector != info.vector_primitive_count
        || coverage != info.image_coverage
    {
        return Err("automatic PDF route disagrees with source footprints".into());
    }
    if expected == PdfRoute::Vector {
        if info.render_width != 0
            || info.render_height != 0
            || !raster.is_empty()
            || !info.bands.is_empty()
            || info.threshold != 0
        {
            return Err("vector page contains raster evidence".into());
        }
    } else if info.render_width == 0
        || info.render_height == 0
        || info.render_width > MAX_RENDER_DIMENSION as usize
        || info.render_height > MAX_RENDER_DIMENSION as usize
        || (info.render_width as f64 / info.render_height as f64 - width / height).abs() > 0.01
    {
        return Err("invalid raster render dimensions".into());
    }
    if sources
        .iter()
        .any(|p| p.id.starts_with("raster:") && !raster.contains(p.id.as_str()))
    {
        return Err("unlisted pixel-derived source".into());
    }
    for id in &info.raster_source_ids {
        let p = get(id).ok_or("missing pixel-derived source")?;
        if p.stroke_width.is_some() || p.layer.is_some() || !matches!(p.shape, Shape::Line { .. }) {
            return Err("pixel-derived face cannot claim authored pen or fill".into());
        }
    }
    for b in &info.bands {
        if b.face_source_ids[0] == b.face_source_ids[1]
            || !b.start.finite()
            || !b.end.finite()
            || b.start.distance(b.end) <= 0.
            || !b.thickness.is_finite()
            || b.thickness <= 0.
            || !b.width_pixels.is_finite()
            || b.width_pixels <= 0.
            || !b.dark_fraction.is_finite()
            || !(0. ..=1.).contains(&b.dark_fraction)
            || !b.mean_luma.is_finite()
            || !(0. ..=255.).contains(&b.mean_luma)
            || b.boundary_coverage
                .iter()
                .any(|v| !v.is_finite() || !(0. ..=1.).contains(v))
        {
            return Err("invalid measured raster band".into());
        }
        if let Some(h) = &b.hatch {
            if info.analysis_revision == 0
                || h.crossing_count < 2
                || !h.mean_pitch_pixels.is_finite()
                || h.mean_pitch_pixels < 1.
                || !h.pitch_cv.is_finite()
                || h.pitch_cv < 0.
                || !h.diagonal_support.is_finite()
                || !(0. ..=1.).contains(&h.diagonal_support)
                || !h.stationary_correlation.is_finite()
                || !(-1. ..=1.).contains(&h.stationary_correlation)
                || !h.contrast.is_finite()
                || !(0. ..=255.).contains(&h.contrast)
                || !h.longitudinal_coverage.is_finite()
                || !(0. ..=1.).contains(&h.longitudinal_coverage)
                || h.face_ink_half_widths_pixels
                    .iter()
                    .any(|v| !v.is_finite() || !(0. ..=32.).contains(v))
            {
                return Err("invalid measured raster hatch evidence".into());
            }
        }
        let axis = (b.end - b.start).unit();
        let pixel_width = if axis.x.abs() > axis.y.abs() {
            height / info.render_height as f64
        } else {
            width / info.render_width as f64
        };
        if (b.width_pixels - b.thickness / pixel_width).abs() > 1e-6 {
            return Err("raster band pixel scale disagrees with page coordinates".into());
        }
        if let Some(h) = &b.hatch {
            let cross = if axis.x.abs() > axis.y.abs() {
                Point::new(0., 1.)
            } else {
                Point::new(1., 0.)
            };
            let center = |id: &str| match get(id).map(|p| &p.shape) {
                Some(Shape::Line { start, .. }) => Some(start.dot(cross)),
                _ => None,
            };
            let ca = center(&b.face_source_ids[0]).ok_or("missing first hatch face")?;
            let cb = center(&b.face_source_ids[1]).ok_or("missing second hatch face")?;
            let [pa, pb] = h.face_ink_half_widths_pixels.map(|v| v * pixel_width);
            let expected_center = (ca + cb - pa + pb) * 0.5;
            let eps = pixel_width * 1e-4;
            if cb <= ca
                || ((cb - ca + pa + pb) - b.thickness).abs() > eps
                || (b.start.dot(cross) - expected_center).abs() > eps
                || (b.end.dot(cross) - expected_center).abs() > eps
            {
                return Err("hatch band disagrees with measured source-ink extents".into());
            }
        }
        for (side, id) in b.face_source_ids.iter().enumerate() {
            if !raster.contains(id.as_str()) {
                return Err("raster band requires two pixel-derived faces".into());
            }
            let Shape::Line { start, end } = get(id).ok_or("missing raster face")?.shape else {
                return Err("raster face must be linear".into());
            };
            let tol = width / info.render_width as f64 * 2.;
            let face_axis = (end - start).unit();
            let along = [b.start, b.end].map(|p| (p - start).dot(face_axis));
            let offsets = [start, end].map(|p| (p - b.start).cross(axis));
            let expected_offset = b.thickness * 0.5
                - b.hatch
                    .as_ref()
                    .map_or(0., |h| h.face_ink_half_widths_pixels[side] * pixel_width);
            if face_axis.dot(axis).abs() < 0.999
                || offsets
                    .iter()
                    .any(|v| (v.abs() - expected_offset).abs() > tol)
                || (offsets[1] - offsets[0]).abs() > tol
                || along[0].min(along[1]) < -tol
                || along[0].max(along[1]) > start.distance(end) + tol
            {
                return Err("raster band disagrees with its measured faces".into());
            }
        }
    }
    Ok(())
}
