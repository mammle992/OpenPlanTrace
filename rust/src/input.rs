//! Source readers. Extraction runs directly on the original drawing, before semantic scanning.
mod dxf;
mod pdf;
pub mod raster;
#[doc(hidden)]
pub use pdf::capped_stock;
#[doc(hidden)]
pub use pdf::local_stock;
#[doc(hidden)]
pub use pdf::stepped_stock;
#[doc(hidden)]
pub use pdf::terminal_stock;
pub(crate) use pdf::validate_contact_paint;
pub(crate) use pdf::validate_paint_remainders;
#[doc(hidden)]
pub use pdf::white_nested;

use crate::model::{Document, Point};
use std::{collections::BTreeMap, fs::File, io::Read, path::Path};

const MAX_FILE: u64 = 128 << 20;
const MAX_LOCAL_PDF: u64 = 512 << 20;
const MAX_ITEMS: usize = 1_000_000;

#[derive(Clone, Copy, PartialEq, Eq)]
pub enum DrawingFormat {
    Pdf,
    Dxf,
}

impl DrawingFormat {
    pub fn of(path: &Path) -> Option<Self> {
        let ext = path.extension()?.to_str()?;
        if ext.eq_ignore_ascii_case("pdf") {
            Some(Self::Pdf)
        } else if ext.eq_ignore_ascii_case("dxf") {
            Some(Self::Dxf)
        } else {
            None
        }
    }
}

pub fn load(path: &Path) -> Result<Document, String> {
    load_selected(path, None)
}

pub(crate) fn load_selected(path: &Path, page: Option<u32>) -> Result<Document, String> {
    let stage = crate::stage_profile::scope("read_input_file");
    let format = DrawingFormat::of(path).ok_or("Unsupported input format; use PDF or ASCII DXF")?;
    let file = File::open(path).map_err(|e| format!("Could not open drawing: {e}"))?;
    let needs_owned_pdf = format == DrawingFormat::Pdf
        && (page.is_some()
            || file
                .metadata()
                .is_ok_and(|metadata| metadata.is_file() && metadata.len() > MAX_FILE));
    if needs_owned_pdf {
        let bytes = read_owned_local_pdf(file, page.is_some())?;
        drop(stage);
        return load_owned_pdf(bytes, page);
    }
    let mut bytes = Vec::new();
    file.take(MAX_FILE + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_FILE {
        return Err("Drawing exceeds 128 MiB input limit".into());
    }
    drop(stage);
    load_bytes_selected(format, &bytes, page)
}

// A large or selected local PDF owns one bounded buffer. Hayro can take over that
// allocation after vector extraction and raster preflight have borrowed it.
fn read_owned_local_pdf(mut file: File, selected: bool) -> Result<Vec<u8>, String> {
    let subject = if selected {
        "Selected PDF"
    } else {
        "Local PDF"
    };
    let metadata = file
        .metadata()
        .map_err(|e| format!("Could not measure {subject}: {e}"))?;
    if !metadata.is_file() {
        return Err(format!("{subject} must be a regular file"));
    }
    let length = metadata.len();
    if length > MAX_LOCAL_PDF {
        return Err(format!("{subject} exceeds 512 MiB input limit"));
    }
    let length = usize::try_from(length).map_err(|_| format!("{subject} size is unsupported"))?;
    let mut bytes = Vec::new();
    bytes
        .try_reserve_exact(length)
        .map_err(|_| format!("Insufficient memory for {subject} input"))?;
    bytes.resize(length, 0);
    file.read_exact(&mut bytes)
        .map_err(|e| format!("Could not read {subject}: {e}"))?;
    if file
        .read(&mut [0])
        .map_err(|e| format!("Could not verify {subject} length: {e}"))?
        != 0
    {
        return Err(format!("{subject} grew while being read"));
    }
    Ok(bytes)
}

fn load_owned_pdf(bytes: Vec<u8>, page: Option<u32>) -> Result<Document, String> {
    let (pdf, mut doc) = {
        let _profile = crate::stage_profile::scope("pdf_vector_import");
        let pdf = pdf::parse(&bytes)?;
        let doc = pdf::read_document(&pdf, page)?;
        (pdf, doc)
    };
    raster::interpret_owned_with_residual_hatch(bytes, pdf, &mut doc, page)?;
    Ok(doc)
}

/// Read an uploaded or embedded drawing with the 128 MiB standard limit.
pub fn load_bytes(format: DrawingFormat, bytes: &[u8]) -> Result<Document, String> {
    load_bytes_selected(format, bytes, None)
}

fn load_bytes_selected(
    format: DrawingFormat,
    bytes: &[u8],
    page: Option<u32>,
) -> Result<Document, String> {
    if bytes.len() as u64 > MAX_FILE {
        return Err("Drawing exceeds 128 MiB input limit".into());
    }
    match format {
        DrawingFormat::Pdf => {
            let mut doc = pdf::read_selected(bytes, page)?;
            raster::interpret_with_residual_hatch(bytes, &mut doc, page)?;
            Ok(doc)
        }
        DrawingFormat::Dxf => {
            if let Some(page) = page.filter(|n| *n != 1) {
                return Err(format!("page {page} was not found"));
            }
            let _profile = crate::stage_profile::scope("dxf_import");
            dxf::read(bytes)
        }
    }
}

#[derive(Clone, Copy, Debug)]
struct Mat([f64; 6]);

impl Mat {
    const ID: Self = Self([1., 0., 0., 1., 0., 0.]);
    fn point(self, p: Point) -> Point {
        let [a, b, c, d, e, f] = self.0;
        Point::new(a * p.x + c * p.y + e, b * p.x + d * p.y + f)
    }
    /// self after rhs (column vectors).
    fn then(self, rhs: Self) -> Self {
        let [a, b, c, d, e, f] = self.0;
        let [g, h, i, j, k, l] = rhs.0;
        Self([
            a * g + c * h,
            b * g + d * h,
            a * i + c * j,
            b * i + d * j,
            a * k + c * l + e,
            b * k + d * l + f,
        ])
    }
    fn translate(x: f64, y: f64) -> Self {
        Self([1., 0., 0., 1., x, y])
    }
    fn det(self) -> f64 {
        self.0[0] * self.0[3] - self.0[1] * self.0[2]
    }
    fn similarity(self) -> Option<f64> {
        let [a, b, c, d, _, _] = self.0;
        let n = a.hypot(b);
        (n.is_finite()
            && n > 0.
            && (n - c.hypot(d)).abs() <= 1e-9 * n
            && (a * c + b * d).abs() <= 1e-9 * n * n)
            .then_some(n)
    }
}

fn finite(p: Point) -> bool {
    p.x.is_finite() && p.y.is_finite() && p.x.abs() <= 1e15 && p.y.abs() <= 1e15
}
#[derive(Default)]
struct Warnings(BTreeMap<String, usize>);
impl Warnings {
    fn add(&mut self, msg: &str) {
        *self.0.entry(msg.into()).or_default() += 1;
    }
    fn finish(self) -> Vec<String> {
        self.0
            .into_iter()
            .map(|(m, n)| format!("{m} ({n})"))
            .collect()
    }
}
