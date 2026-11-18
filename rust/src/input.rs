//! Source readers. Extraction runs directly on the original drawing, before semantic scanning.
mod dxf;
mod pdf;
pub mod raster;

use crate::model::{Document, Point};
use std::{collections::BTreeMap, fs::File, io::Read, path::Path};

const MAX_FILE: u64 = 128 << 20;
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
    let format = DrawingFormat::of(path).ok_or("Unsupported input format; use PDF or ASCII DXF")?;
    let file = File::open(path).map_err(|e| format!("Could not open drawing: {e}"))?;
    let mut bytes = Vec::new();
    file.take(MAX_FILE + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > MAX_FILE {
        return Err("Drawing exceeds 128 MiB input limit".into());
    }
    load_bytes(format, &bytes)
}

/// Read an uploaded or embedded drawing with the same limits as a local file.
pub fn load_bytes(format: DrawingFormat, bytes: &[u8]) -> Result<Document, String> {
    if bytes.len() as u64 > MAX_FILE {
        return Err("Drawing exceeds 128 MiB input limit".into());
    }
    match format {
        DrawingFormat::Pdf => {
            let mut doc = pdf::read(bytes)?;
            raster::interpret(bytes, &mut doc)?;
            Ok(doc)
        }
        DrawingFormat::Dxf => dxf::read(bytes),
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
