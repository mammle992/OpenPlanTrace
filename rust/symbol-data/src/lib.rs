//! Aspect-preserving preprocessing shared by local data preparation and inference.
use image::{
    Rgb, RgbImage,
    imageops::{self, FilterType},
};

pub mod real;
pub use real::preprocess_target_rgb;

/// Detect nonwhite ink. Bounds use exclusive right/bottom coordinates.
pub fn ink_bounds(image: &RgbImage) -> Option<(u32, u32, u32, u32)> {
    let (mut x0, mut y0, mut x1, mut y1) = (image.width(), image.height(), 0, 0);
    let mut found = false;
    for (x, y, p) in image.enumerate_pixels() {
        if p.0.iter().any(|&v| v < 245) {
            found = true;
            x0 = x0.min(x);
            y0 = y0.min(y);
            x1 = x1.max(x + 1);
            y1 = y1.max(y + 1);
        }
    }
    found.then_some((x0, y0, x1, y1))
}

/// Crop to ink, add `context` times the maximum ink dimension on EACH side,
/// and letterbox to a square. Geometry is uniformly scaled, never stretched.
/// Blank crops produce a white square. Production callers should use 64, 0.15.
pub fn preprocess_rgb(image: &RgbImage, side: u32, context: f32) -> RgbImage {
    assert!((1..=4096).contains(&side));
    assert!(context.is_finite() && (0.0..=1.0).contains(&context));
    let Some((x0, y0, x1, y1)) = ink_bounds(image) else {
        return RgbImage::from_pixel(side, side, Rgb([255; 3]));
    };
    let maxside = (x1 - x0).max(y1 - y0);
    let extent = ((maxside as f32) * (1.0 + 2.0 * context)).ceil().max(1.0) as u32;
    let left = ((x0 + x1) as f64 / 2.0 - extent as f64 / 2.0).floor() as i64;
    let top = ((y0 + y1) as f64 / 2.0 - extent as f64 / 2.0).floor() as i64;
    let mut square = RgbImage::from_pixel(extent, extent, Rgb([255; 3]));
    for y in 0..extent {
        for x in 0..extent {
            let (sx, sy) = (left + x as i64, top + y as i64);
            if sx >= 0 && sy >= 0 && sx < image.width() as i64 && sy < image.height() as i64 {
                square.put_pixel(x, y, *image.get_pixel(sx as u32, sy as u32));
            }
        }
    }
    imageops::resize(&square, side, side, FilterType::Lanczos3)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn long_span_retains_aspect_and_context_even_at_border() {
        let mut source = RgbImage::from_pixel(160, 80, Rgb([255; 3]));
        for y in 0..10 {
            for x in 0..120 {
                source.put_pixel(x, y, Rgb([0; 3]));
            }
        }
        let output = preprocess_rgb(&source, 64, 0.15);
        let (x0, y0, x1, y1) = ink_bounds(&output).unwrap();
        assert!(x0 >= 5 && x1 <= 59 && y0 >= 25 && y1 <= 39);
        assert!(((x1 - x0) as f32 / (y1 - y0) as f32 - 12.0).abs() < 4.0);
    }
    #[test]
    fn blank_is_white() {
        assert!(
            preprocess_rgb(&RgbImage::from_pixel(10, 20, Rgb([255; 3])), 64, 0.15)
                .pixels()
                .all(|p| p.0 == [255; 3])
        );
    }
}
