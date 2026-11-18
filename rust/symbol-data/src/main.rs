use image::{Rgb, RgbImage, imageops};
use openplantrace_symbol_data::preprocess_rgb;
use serde::{Deserialize, Serialize};
use serde_json::json;
use sha2::{Digest, Sha256};
use std::{
    collections::{BTreeMap, BTreeSet},
    error::Error,
    fs::{self, File},
    io::{BufWriter, Write},
    path::{Path, PathBuf},
};

type Result<T> = std::result::Result<T, Box<dyn Error>>;
const SIDE: u32 = 64;
const SEED: u64 = 20261117;
const LABELS: [(&str, &str); 9] = [
    ("other", "Unknown"),
    ("door", "Structural"),
    ("window", "Structural"),
    ("stair", "Stair"),
    ("toilet", "PlumbingFixture"),
    ("sink", "PlumbingFixture"),
    ("shower", "PlumbingFixture"),
    ("bath", "PlumbingFixture"),
    ("furniture", "Furniture"),
];

#[derive(Clone, Deserialize)]
#[serde(rename_all = "camelCase")]
struct Source {
    source_id: String,
    source_family: String,
    category: String,
    split: String,
    local_path: String,
    rendered_path: String,
    sha256: String,
    render_sha256: String,
}
struct Loaded {
    source: Source,
    image: RgbImage,
    label: usize,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Item {
    image_path: String,
    offset: usize,
    label_index: usize,
    source_group: String,
    split: String,
    source_id: String,
    augmentation_id: usize,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Label {
    label: String,
    category: String,
}
#[derive(Serialize)]
#[serde(rename_all = "camelCase")]
struct Manifest {
    schema: String,
    width: u32,
    height: u32,
    channels: u32,
    labels: Vec<Label>,
    items: Vec<Item>,
    seed: u64,
    dataset_sha256: String,
}
#[derive(Clone, Copy, Serialize)]
#[serde(rename_all = "camelCase")]
struct Augment {
    quarter_turn: u8,
    mirror: bool,
    angle_degrees: f64,
    scale: f64,
    shift_x: f64,
    shift_y: f64,
    pen: i8,
    opacity: f64,
    noise: f64,
    speckles: usize,
    blur: bool,
}
struct Rng(u64);
impl Rng {
    fn next(&mut self) -> u64 {
        self.0 = self.0.wrapping_add(0x9e3779b97f4a7c15);
        let mut z = self.0;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58476d1ce4e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d049bb133111eb);
        z ^ (z >> 31)
    }
    fn unit(&mut self) -> f64 {
        (self.next() >> 11) as f64 / ((1u64 << 53) as f64)
    }
    fn range(&mut self, a: f64, b: f64) -> f64 {
        a + (b - a) * self.unit()
    }
    fn index(&mut self, n: usize) -> usize {
        (self.next() % (n as u64)) as usize
    }
}
fn hex(data: &[u8]) -> String {
    data.iter().map(|x| format!("{x:02x}")).collect()
}
fn sha(data: &[u8]) -> String {
    hex(&Sha256::digest(data))
}
fn file_sha(path: &Path) -> Result<String> {
    Ok(sha(&fs::read(path)?))
}
fn sample(image: &RgbImage, x: f64, y: f64) -> f64 {
    if x < 0.0 || y < 0.0 || x > 63.0 || y > 63.0 {
        return 255.0;
    }
    let (x0, y0) = (x.floor() as u32, y.floor() as u32);
    let (x1, y1) = ((x0 + 1).min(63), (y0 + 1).min(63));
    let (fx, fy) = (x - x0 as f64, y - y0 as f64);
    let p = |x, y| image.get_pixel(x, y)[0] as f64;
    p(x0, y0) * (1.0 - fx) * (1.0 - fy)
        + p(x1, y0) * fx * (1.0 - fy)
        + p(x0, y1) * (1.0 - fx) * fy
        + p(x1, y1) * fx * fy
}
fn augment_spec(rng: &mut Rng) -> Augment {
    Augment {
        quarter_turn: rng.index(4) as u8,
        mirror: rng.index(2) == 0,
        angle_degrees: rng.range(-7.0, 7.0),
        scale: rng.range(0.83, 1.10),
        shift_x: rng.range(-2.5, 2.5),
        shift_y: rng.range(-2.5, 2.5),
        pen: match rng.index(5) {
            0 => -1,
            1 => 1,
            _ => 0,
        },
        opacity: rng.range(0.60, 1.0),
        noise: rng.range(0.0, 4.0),
        speckles: if rng.index(3) == 0 { rng.index(12) } else { 0 },
        blur: rng.index(6) == 0,
    }
}
fn validation_spec(index: usize) -> Augment {
    Augment {
        quarter_turn: (index % 4) as u8,
        mirror: index >= 4,
        angle_degrees: if index >= 4 { 3.0 } else { 0.0 },
        scale: if index >= 4 { 0.91 } else { 1.0 },
        shift_x: 0.0,
        shift_y: 0.0,
        pen: 0,
        opacity: if index >= 4 { 0.75 } else { 1.0 },
        noise: if index >= 4 { 2.0 } else { 0.0 },
        speckles: 0,
        blur: index >= 4,
    }
}
fn apply(image: &RgbImage, spec: Augment, rng: &mut Rng) -> RgbImage {
    let mut input = image.clone();
    if spec.pen != 0 {
        for y in 1..63 {
            for x in 1..63 {
                let mut value = input.get_pixel(x, y)[0];
                for dy in -1i32..=1 {
                    for dx in -1i32..=1 {
                        let v = image.get_pixel((x as i32 + dx) as u32, (y as i32 + dy) as u32)[0];
                        value = if spec.pen > 0 {
                            value.min(v)
                        } else {
                            value.max(v)
                        };
                    }
                }
                let blended = 0.45 * value as f64 + 0.55 * image.get_pixel(x, y)[0] as f64;
                input.put_pixel(x, y, Rgb([blended as u8; 3]));
            }
        }
    }
    let angle = (spec.quarter_turn as f64 * 90.0 + spec.angle_degrees).to_radians();
    let (sin, cos) = angle.sin_cos();
    let mut output = RgbImage::from_pixel(SIDE, SIDE, Rgb([255; 3]));
    for y in 0..SIDE {
        for x in 0..SIDE {
            let px = (x as f64 - 31.5 - spec.shift_x) / spec.scale;
            let py = (y as f64 - 31.5 - spec.shift_y) / spec.scale;
            let mut sx = cos * px + sin * py;
            let sy = -sin * px + cos * py;
            if spec.mirror {
                sx = -sx;
            }
            let v = sample(&input, sx + 31.5, sy + 31.5);
            let noise = (rng.unit() + rng.unit() + rng.unit() - 1.5) * spec.noise;
            let value = (255.0 - (255.0 - v) * spec.opacity + noise)
                .round()
                .clamp(0.0, 255.0) as u8;
            output.put_pixel(x, y, Rgb([value; 3]));
        }
    }
    for _ in 0..spec.speckles {
        let x = rng.index(64) as u32;
        let y = rng.index(64) as u32;
        let v = rng.range(140.0, 235.0) as u8;
        output.put_pixel(x, y, Rgb([v; 3]));
    }
    if spec.blur {
        imageops::blur(&output, 0.35)
    } else {
        output
    }
}
fn line(image: &mut RgbImage, x0: f64, y0: f64, x1: f64, y1: f64, width: f64, color: u8) {
    let n = ((x1 - x0).abs().max((y1 - y0).abs()) * 2.0).ceil() as usize;
    for i in 0..=n.max(1) {
        let t = i as f64 / n.max(1) as f64;
        let (x, y) = (x0 + (x1 - x0) * t, y0 + (y1 - y0) * t);
        let r = (width / 2.0).max(0.5);
        for yy in (y - r).floor() as i32..=(y + r).ceil() as i32 {
            for xx in (x - r).floor() as i32..=(x + r).ceil() as i32 {
                if (0..64).contains(&xx)
                    && (0..64).contains(&yy)
                    && ((xx as f64 - x).powi(2) + (yy as f64 - y).powi(2)).sqrt() <= r
                {
                    image.put_pixel(xx as u32, yy as u32, Rgb([color; 3]));
                }
            }
        }
    }
}
fn rectangle(im: &mut RgbImage, x: f64, y: f64, w: f64, h: f64, color: u8) {
    for (x0, y0, x1, y1) in [
        (x, y, x + w, y),
        (x + w, y, x + w, y + h),
        (x + w, y + h, x, y + h),
        (x, y + h, x, y),
    ] {
        line(im, x0, y0, x1, y1, 1.3, color);
    }
}
fn procedural_negative(rng: &mut Rng) -> (RgbImage, &'static str) {
    let mut im = RgbImage::from_pixel(64, 64, Rgb([255; 3]));
    let family = rng.index(6);
    match family {
        0 => {
            let a = rng.range(8.0, 20.0);
            let gap = rng.range(3.0, 12.0);
            line(&mut im, 6.0, a, 58.0, a, 2.0, 20);
            line(&mut im, 6.0, a + gap, 58.0, a + gap, 2.0, 20);
            if rng.index(2) == 0 {
                line(&mut im, 35.0, a + gap, 35.0, 58.0, 2.0, 20);
                line(&mut im, 35.0 + gap, a + gap, 35.0 + gap, 58.0, 2.0, 20);
            }
        }
        1 => {
            let spacing = rng.range(3.0, 8.0);
            for k in -12..20 {
                let x = k as f64 * spacing;
                line(&mut im, x, 6.0, x + 40.0, 58.0, 1.0, 60);
            }
            rectangle(&mut im, 5.0, 5.0, 54.0, 54.0, 30);
        }
        2 => {
            for row in 0..rng.index(3) + 1 {
                for c in 0..rng.index(5) + 2 {
                    let x = 6.0 + c as f64 * 8.0;
                    let y = 12.0 + row as f64 * 13.0;
                    let bits = rng.next();
                    for s in 0..7 {
                        if bits & (1 << s) != 0 {
                            let coords = [
                                (0., 0., 5., 0.),
                                (5., 0., 5., 4.),
                                (5., 4., 5., 8.),
                                (0., 8., 5., 8.),
                                (0., 4., 0., 8.),
                                (0., 0., 0., 4.),
                                (0., 4., 5., 4.),
                            ][s];
                            line(
                                &mut im,
                                x + coords.0,
                                y + coords.1,
                                x + coords.2,
                                y + coords.3,
                                1.0,
                                30,
                            );
                        }
                    }
                }
            }
        }
        3 => {
            let y = rng.range(20.0, 44.0);
            line(&mut im, 6., y, 58., y, 1., 40);
            for x in [10.0, 54.0] {
                line(&mut im, x, y - 8.0, x, y + 8.0, 1.0, 40);
                line(&mut im, x - 2.0, y + 2.0, x + 2.0, y - 2.0, 1.0, 40);
            }
            for i in 0..4 {
                rectangle(&mut im, 22.0 + i as f64 * 5.0, y - 9.0, 2.0, 4.0, 70);
            }
        }
        4 => {
            for _ in 0..rng.index(12) {
                let x = rng.range(0., 64.);
                let y = rng.range(0., 64.);
                let dx = rng.range(-8., 8.);
                let dy = rng.range(-8., 8.);
                line(
                    &mut im,
                    x,
                    y,
                    x + dx,
                    y + dy,
                    1.0,
                    rng.range(190., 245.) as u8,
                );
            }
        }
        _ => {
            let n = rng.index(3) + 1;
            for _ in 0..n {
                let x = rng.range(6., 35.);
                let y = rng.range(6., 35.);
                rectangle(&mut im, x, y, rng.range(5., 24.), rng.range(4., 24.), 70);
            }
        }
    }
    (
        im,
        [
            "wall-strips",
            "hatch",
            "own-seven-segment-text",
            "dimension",
            "background",
            "generic-annotation-rectangles",
        ][family],
    )
}
fn arc(
    im: &mut RgbImage,
    cx: f64,
    cy: f64,
    radius: f64,
    start: f64,
    sweep: f64,
    width: f64,
    color: u8,
) {
    let n = (sweep.abs() * radius * 2.0).ceil().max(8.0) as usize;
    for i in 0..n {
        let a = start + sweep * i as f64 / n as f64;
        let b = start + sweep * (i + 1) as f64 / n as f64;
        line(
            im,
            cx + radius * a.cos(),
            cy - radius * a.sin(),
            cx + radius * b.cos(),
            cy - radius * b.sin(),
            width,
            color,
        );
    }
}
fn arrow(im: &mut RgbImage, x: f64, y: f64, dx: f64, dy: f64, width: f64) {
    line(im, x, y, x + dx, y + dy, width, 30);
    let length = (dx * dx + dy * dy).sqrt().max(1.0);
    let (ux, uy) = (dx / length, dy / length);
    for sign in [-1.0, 1.0] {
        line(
            im,
            x + dx,
            y + dy,
            x + dx - ux * 5.0 + sign * uy * 3.0,
            y + dy - uy * 5.0 - sign * ux * 3.0,
            width,
            30,
        );
    }
}
/// Entirely original generic plan geometry, not traced from validation or QA.
fn procedural_positive(label: usize, rng: &mut Rng) -> (RgbImage, String, serde_json::Value) {
    let mut im = RgbImage::from_pixel(64, 64, Rgb([255; 3]));
    let pen = rng.range(0.8, 2.2);
    if label == 1 {
        let double = rng.index(3) == 0;
        let width = rng.range(29.0, 38.0);
        let x = (64.0 - width) / 2.0;
        let y = 47.0;
        let angle = rng.range(55.0, 108.0).to_radians();
        let frame = rng.range(2.0, 5.0);
        let walls = rng.index(5) != 0;
        if walls {
            line(&mut im, 4.0, y, x, y, frame, 25);
            line(&mut im, x + width, y, 60.0, y, frame, 25);
        }
        for jamb in [x, x + width] {
            line(&mut im, jamb, y - frame, jamb, y + frame, pen, 20);
        }
        if double {
            let leaf = width / 2.0;
            line(
                &mut im,
                x,
                y,
                x + leaf * angle.cos(),
                y - leaf * angle.sin(),
                pen,
                20,
            );
            line(
                &mut im,
                x + width,
                y,
                x + width - leaf * angle.cos(),
                y - leaf * angle.sin(),
                pen,
                20,
            );
            arc(&mut im, x, y, leaf, 0.0, angle, pen * 0.6, 35);
            arc(
                &mut im,
                x + width,
                y,
                leaf,
                std::f64::consts::PI,
                -angle,
                pen * 0.6,
                35,
            );
        } else {
            line(
                &mut im,
                x,
                y,
                x + width * angle.cos(),
                y - width * angle.sin(),
                pen,
                20,
            );
            arc(&mut im, x, y, width, 0.0, angle, pen * 0.6, 35);
        }
        let kind = if double {
            "double-swing"
        } else {
            "single-swing"
        };
        (
            preprocess_rgb(&im, 64, 0.15),
            kind.into(),
            json!({"openingWidth":width,"openingAngleDegrees":angle.to_degrees(),"penWidth":pen,"jambDepth":frame,"wallStubs":walls}),
        )
    } else {
        let kind = rng.index(3);
        let spacing = rng.range(3.2, 5.8);
        let rail = pen * 0.8;
        match kind {
            0 => {
                let half = rng.range(8.0, 13.0);
                let x0 = 32.0 - half;
                let x1 = 32.0 + half;
                rectangle(&mut im, x0, 8.0, half * 2.0, 48.0, 25);
                let mut y = 8.0 + spacing;
                while y < 55.0 {
                    line(&mut im, x0, y, x1, y, rail, 40);
                    y += spacing;
                }
                arrow(&mut im, 32.0, 50.0, 0.0, -36.0, pen);
            }
            1 => {
                for (x0, y0, x1, y1) in [
                    (14., 10., 30., 10.),
                    (30., 10., 30., 36.),
                    (30., 36., 54., 36.),
                    (54., 36., 54., 54.),
                    (54., 54., 14., 54.),
                    (14., 54., 14., 10.),
                ] {
                    line(&mut im, x0, y0, x1, y1, pen, 25);
                }
                let mut y = 10.0 + spacing;
                while y < 36.0 {
                    line(&mut im, 14.0, y, 30.0, y, rail, 40);
                    y += spacing;
                }
                let mut x = 30.0 + spacing;
                while x < 54.0 {
                    line(&mut im, x, 36.0, x, 54.0, rail, 40);
                    x += spacing;
                }
                line(&mut im, 22., 16., 22., 45., pen, 30);
                arrow(&mut im, 22., 45., 26., 0., pen);
            }
            _ => {
                rectangle(&mut im, 9., 9., 46., 46., 25);
                line(&mut im, 26., 24., 26., 55., pen, 25);
                line(&mut im, 38., 24., 38., 55., pen, 25);
                line(&mut im, 26., 24., 38., 24., pen, 25);
                let mut y = 24.0 + spacing;
                while y < 55.0 {
                    line(&mut im, 9., y, 26., y, rail, 40);
                    line(&mut im, 38., y, 55., y, rail, 40);
                    y += spacing;
                }
                line(&mut im, 18., 50., 18., 17., pen, 30);
                line(&mut im, 18., 17., 46., 17., pen, 30);
                arrow(&mut im, 46., 17., 0., 31., pen);
            }
        }
        (
            preprocess_rgb(&im, 64, 0.15),
            ["straight", "L-turn", "U-turn"][kind].into(),
            json!({"treadSpacing":spacing,"railPenWidth":pen,"treadPenWidth":rail}),
        )
    }
}
/// Class-independent synthetic scan context. Existing ink is never erased.
fn context_augmentation(image: &RgbImage, seed: u64) -> (RgbImage, serde_json::Value) {
    let mut rng = Rng(seed ^ 0x857a_420c_02aa_65fb);
    let spacing = rng.range(8.0, 17.0);
    let grid = rng.index(2) == 0;
    let mut context = RgbImage::from_pixel(64, 64, Rgb([255; 3]));
    let grid_ink = rng.range(232.0, 248.0) as u8;
    if grid {
        let mut p = rng.range(0.0, spacing);
        while p < 64.0 {
            line(&mut context, p, 0.0, p, 63.0, 0.7, grid_ink);
            line(&mut context, 0.0, p, 63.0, p, 0.7, grid_ink);
            p += spacing;
        }
    }
    let dimension_side = rng.index(4);
    let dim = rng.range(175.0, 225.0) as u8;
    let mut margin = RgbImage::from_pixel(64, 64, Rgb([255; 3]));
    line(&mut margin, 5.0, 3.0, 59.0, 3.0, 0.8, dim);
    for x in [9.0, 55.0] {
        line(&mut margin, x, 0.0, x, 6.0, 0.8, dim);
        line(&mut margin, x - 1.5, 4.5, x + 1.5, 1.5, 0.8, dim);
    }
    for i in 0..rng.index(5) + 2 {
        let x = 18.0 + i as f64 * 5.0;
        let bits = rng.next();
        for k in 0..5 {
            if bits & (1 << k) != 0 {
                line(
                    &mut margin,
                    x,
                    59.0 + k as f64 * 0.6,
                    x + 2.0,
                    59.0 + k as f64 * 0.6,
                    0.7,
                    dim,
                );
            }
        }
    }
    margin = match dimension_side {
        1 => imageops::rotate90(&margin),
        2 => imageops::rotate180(&margin),
        3 => imageops::rotate270(&margin),
        _ => margin,
    };
    let tint = match rng.index(5) {
        0 => [35.0, 55.0, 95.0],
        1 => [80.0, 45.0, 35.0],
        _ => {
            let gray = rng.range(0.0, 65.0);
            [gray; 3]
        }
    };
    let mut output = image.clone();
    for y in 0..64 {
        for x in 0..64 {
            let old = image.get_pixel(x, y);
            let border = x < 7 || y < 7 || x > 56 || y > 56;
            let added = if border {
                context.get_pixel(x, y)[0].min(margin.get_pixel(x, y)[0])
            } else {
                context.get_pixel(x, y)[0]
            };
            let mut pixel = [0u8; 3];
            for c in 0..3 {
                let v = (255.0 - (255.0 - old[c] as f64) * (1.0 - tint[c] / 255.0)).round() as u8;
                pixel[c] = v.min(added);
            }
            output.put_pixel(x, y, Rgb(pixel));
        }
    }
    (
        output,
        json!({"seed":seed,"grid":grid,"gridSpacing":spacing,"gridInk":grid_ink,"dimensionSide":dimension_side,"dimensionInk":dim,"inkRgbTint":tint,"targetInkPreserved":true}),
    )
}
fn main() -> Result<()> {
    let args: Vec<_> = std::env::args().skip(1).collect();
    if args.len() == 3 && args[0] == "preprocess" {
        let input = image::open(&args[1])?.to_rgb8();
        let output = preprocess_rgb(&input, 64, 0.15);
        output.save(&args[2])?;
        println!(
            "{}",
            json!({"width":64,"height":64,"channels":3,"pixelSha256":sha(output.as_raw()),"inputInkBounds":openplantrace_symbol_data::ink_bounds(&input)})
        );
        return Ok(());
    }
    let real_development = args.len() == 4 && args[2] == "--real-development";
    let context_experiment =
        real_development || (args.len() == 3 && args[2] == "--context-augmentation");
    let procedural_positives =
        context_experiment || (args.len() == 3 && args[2] == "--procedural-positives");
    if args.len() != 2 && !procedural_positives {
        return Err("Usage: openplantrace-symbol-data SOURCE_MANIFEST NEW_OUTPUT_DIRECTORY [--procedural-positives] | preprocess INPUT OUTPUT".into());
    }
    let source_path = PathBuf::from(&args[0]);
    let output = PathBuf::from(&args[1]);
    if output.exists() {
        return Err("Output already exists; choose a new immutable dataset directory".into());
    }
    let source_bytes = fs::read(&source_path)?;
    let mut sources: Vec<Source> = serde_json::from_slice(&source_bytes)?;
    if sha(&source_bytes) != "3476ac68f0763b237264583088a0de18456d9aeb185df3a7274609263e90fef5" {
        return Err("Source manifest differs from independent QA frozen snapshot".into());
    }
    let private_source_bytes = if real_development {
        Some(fs::read(&args[3])?)
    } else {
        None
    };
    if let Some(bytes) = &private_source_bytes {
        let private: Vec<Source> = serde_json::from_slice(bytes)?;
        if private.is_empty()
            || private.iter().any(|s| {
                s.source_family != "G09/LC11"
                    || !s.source_id.starts_with("LC11:")
                    || s.split != "train"
                    || s.sha256
                        != "1e5915755991c74d0a9cca622f7a90a538aa45bc8d5d917b2da7b22cf9484322"
            })
        {
            return Err(
                "Private data must contain only authorized LC11/G09 original TRAIN crops".into(),
            );
        }
        sources.extend(private);
    }
    let mut loaded = Vec::new();
    let mut group_splits = BTreeMap::new();
    for source in sources {
        if !["train", "validation"].contains(&source.split.as_str()) {
            return Err("Refusing held-out/test source".into());
        }
        if let Some(previous) =
            group_splits.insert(source.source_family.clone(), source.split.clone())
        {
            if previous != source.split {
                return Err("Family leaks across splits".into());
            }
        }
        if file_sha(Path::new(&source.local_path))? != source.sha256 {
            return Err(format!("Source hash mismatch: {}", source.source_id).into());
        }
        let image = image::open(&source.rendered_path)?;
        let gray = image.to_luma8();
        if sha(gray.as_raw()) != source.render_sha256 {
            return Err(format!("Render pixel hash mismatch: {}", source.source_id).into());
        }
        let label = LABELS
            .iter()
            .position(|v| v.0 == source.category)
            .ok_or("Unknown source class")?;
        loaded.push(Loaded {
            image: preprocess_rgb(&image.to_rgb8(), 64, 0.15),
            source,
            label,
        });
    }
    let mut train: Vec<Vec<usize>> = vec![Vec::new(); 9];
    let mut private_train: Vec<Vec<usize>> = vec![Vec::new(); 9];
    let mut validation = Vec::new();
    for (i, row) in loaded.iter().enumerate() {
        if row.source.split == "train" {
            if row.source.source_family == "G09/LC11" {
                private_train[row.label].push(i);
            } else {
                train[row.label].push(i);
            }
        } else {
            validation.push(i);
        }
    }
    if train.iter().any(Vec::is_empty) {
        return Err("Missing positive training class".into());
    }
    let val_labels: BTreeSet<_> = validation.iter().map(|&i| loaded[i].label).collect();
    if val_labels.len() != 9 {
        return Err("Missing validation class".into());
    }
    fs::create_dir_all(&output)?;
    let mut rgb = BufWriter::new(File::create(output.join("images.rgb"))?);
    let mut ledger = BufWriter::new(File::create(output.join("augmentation-ledger.jsonl"))?);
    let mut items = Vec::new();
    let mut rng = Rng(SEED);
    let mut dataset_hasher = Sha256::new();
    let mut contact = RgbImage::from_pixel(64 * 12, 64 * 18, Rgb([255; 3]));
    let mut shown = [0usize; 9];
    for label in 0..9 {
        for j in 0..2048 {
            let item_seed = rng.next();
            let mut local = Rng(item_seed);
            let spec = augment_spec(&mut local);
            let procedural = label == 0 && j % 2 == 1;
            let positive = procedural_positives && [1, 3].contains(&label) && j % 20 < 7;
            let mut geometry = serde_json::Value::Null;
            let use_private = real_development && !private_train[label].is_empty() && j < 2048 / 5;
            let (base, source_id, group, kind) = if use_private {
                let index = private_train[label][local.index(private_train[label].len())];
                let row = &loaded[index];
                (
                    row.image.clone(),
                    row.source.source_id.clone(),
                    row.source.source_family.clone(),
                    "user-authorized-real-development-crop".to_string(),
                )
            } else if procedural {
                let (im, kind) = procedural_negative(&mut local);
                (
                    im,
                    "own-procedural-negative".to_string(),
                    format!("own-negative-v1:{kind}"),
                    kind.to_string(),
                )
            } else if positive {
                let (im, kind, params) = procedural_positive(label, &mut local);
                geometry = params;
                (
                    im,
                    format!("own-generic-{}-v1", LABELS[label].0),
                    format!("own-positive-{}-v1", LABELS[label].0),
                    kind,
                )
            } else {
                let index = train[label][local.index(train[label].len())];
                let row = &loaded[index];
                (
                    row.image.clone(),
                    row.source.source_id.clone(),
                    row.source.source_family.clone(),
                    "licensed-real-symbol".to_string(),
                )
            };
            let mut image = apply(&base, spec, &mut local);
            let mut context = serde_json::Value::Null;
            if context_experiment && item_seed.rotate_left(23) % 100 < 35 {
                (image, context) = context_augmentation(&image, item_seed);
            }
            let offset = items.len() * 64 * 64 * 3;
            rgb.write_all(image.as_raw())?;
            dataset_hasher.update(image.as_raw());
            writeln!(
                ledger,
                "{}",
                json!({"index":items.len(),"sourceId":source_id,"sourceGroup":group,"augmentationId":j,"split":"train","seed":item_seed,"kind":kind,"geometry":geometry,"augmentation":spec,"context":context})
            )?;
            if shown[label] < 24 {
                let k = shown[label];
                let x = (k % 12) as u32 * 64;
                let y = (label * 2 + k / 12) as u32 * 64;
                imageops::replace(&mut contact, &image, x as i64, y as i64);
                shown[label] += 1;
            }
            items.push(Item {
                image_path: "images.rgb".into(),
                offset,
                label_index: label,
                source_group: group,
                split: "train".into(),
                source_id,
                augmentation_id: j,
            });
        }
    }
    for &index in &validation {
        let row = &loaded[index];
        for k in 0..8 {
            let item_seed = rng.next();
            let mut local = Rng(item_seed);
            let spec = validation_spec(k);
            let image = apply(&row.image, spec, &mut local);
            let offset = items.len() * 64 * 64 * 3;
            rgb.write_all(image.as_raw())?;
            dataset_hasher.update(image.as_raw());
            writeln!(
                ledger,
                "{}",
                json!({"index":items.len(),"sourceId":row.source.source_id,"sourceGroup":row.source.source_family,"augmentationId":k,"split":"validation","seed":item_seed,"kind":"licensed-real-symbol","augmentation":spec})
            )?;
            items.push(Item {
                image_path: "images.rgb".into(),
                offset,
                label_index: row.label,
                source_group: row.source.source_family.clone(),
                split: "validation".into(),
                source_id: row.source.source_id.clone(),
                augmentation_id: k,
            });
        }
    }
    rgb.flush()?;
    ledger.flush()?;
    contact.save(output.join("training-contact-sheet.png"))?;
    let dataset_sha256 = hex(&dataset_hasher.finalize());
    let manifest = Manifest {
        schema: "openplantrace.training-crops.v1".into(),
        width: 64,
        height: 64,
        channels: 3,
        labels: LABELS
            .iter()
            .map(|(label, category)| Label {
                label: label.to_string(),
                category: category.to_string(),
            })
            .collect(),
        items,
        seed: SEED,
        dataset_sha256: dataset_sha256.clone(),
    };
    fs::write(
        output.join("manifest.json"),
        serde_json::to_vec_pretty(&manifest)?,
    )?;
    fs::write(output.join("source-provenance.json"), &source_bytes)?;
    if let Some(bytes) = private_source_bytes {
        fs::write(output.join("PRIVATE-source-provenance.json"), &bytes)?;
        fs::write(
            output.join("PRIVATE-training-policy.json"),
            serde_json::to_vec_pretty(
                &json!({"source":"ONLY authorized LC11/G09 original source-only ROI crops","privateManifestSha256":sha(&bytes),"realExamplesPerEligibleClass":2048/5,"realFractionMaximum":0.20,"classesWithRealSources":private_train.iter().enumerate().filter(|(_,v)|!v.is_empty()).map(|(i,_)|LABELS[i].0).collect::<Vec<_>>(),"validation":"Public author-independent validation520bytes unchanged; noLC07/G05,LC09/G07orreservedLC03/4/13 content used","redistribution":"Keep originalPDF,crops,sourceimages,RGBshards/contactsheetandprivateprovenance local. Trainedweights authorization is separate from sourcepixel redistribution."}),
            )?,
        )?;
    }
    fs::write(
        output.join("context-policy.json"),
        serde_json::to_vec_pretty(
            &json!({"enabled":context_experiment,"selection":"Class-independent35percent using per-item randomseed; independent of class or procedural positive selection","content":"Faint background grid; dimensions and tiny annotation strokes confined to7pixelborder; gray/blue/brown ink variations. Original target ink is never erased or clipped.","validation":"Unchanged canonical/stress520 validation frames; no context added to validation."}),
        )?,
    )?;
    fs::write(output.join("generator-main.rs"), include_str!("main.rs"))?;
    fs::write(output.join("preprocess-lib.rs"), include_str!("lib.rs"))?;
    fs::write(
        output.join("generator-Cargo.lock"),
        include_str!("../Cargo.lock"),
    )?;
    fs::write(
        output.join("preparation.json"),
        serde_json::to_vec_pretty(
            &json!({"schema":"openplantrace.symbol-preparation.v1","seed":SEED,"sourceManifestSha256":sha(&source_bytes),"datasetSha256":dataset_sha256,"trainingItems":18432,"validationItems":validation.len()*8,"sourcesVerified":loaded.len(),"preprocessing":{"inkThreshold":245,"contextEachSideOfMaximumInkDimension":0.15,"side":64,"channels":3,"scaling":"uniform; white square letterbox; Lanczos3"},"augmentation":"Class-independent right-angle rotation/mirror, +/-7degree skew, uniform0.83..1.10scale, +/-2.5pixel shift, blended pen width,0.60..1.0ink opacity, bounded paper noise, occasional faint speckles and blur. Validation8 fixed transformations per original. No copied source families across split.","negativeMix":"Other class half licensed real Other symbols, half entirely own Rust procedural wall strips/hatch/seven-segment annotation text/dimension/background/rectangles.","proceduralPositiveExperiment":procedural_positives,"proceduralPositivePolicy":"When enabled only,35percent of Door and Stair training examples are original parameterized generic schematics: single/double swing,55..108degree opening angles and jamb/pen variations; straight/L/U stairs with rails,treads,arrows. Generated geometries are not independent real drawings. No validation template or QA copying.","generator":"rust/symbol-data; no Python or PDF helper dependency","limitations":["Source original counts175TRAIN/65VAL; augmentation does not add independent authored drawings.","Door and Stair each have one public training original."]}),
        )?,
    )?;
    println!(
        "FROZEN {} train=18432 validation={} sources={} datasetSha256={}",
        output.join("manifest.json").display(),
        validation.len() * 8,
        loaded.len(),
        dataset_sha256
    );
    Ok(())
}
