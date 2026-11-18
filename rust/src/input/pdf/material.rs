//! Interpret visible straight strokes in a bounded coloured PDF tiling pattern.
use super::{Content, Dictionary, Mat, Pdf, Point, Subpath, dict, num, numbers, obj, resource};
use crate::placement::insulation::{MAX_STROKES, batting, rectangle};
use lopdf::Object;

pub(super) struct Cell {
    pub origin: Point,
    pub repeat: Point,
    pub across: Point,
    pub strokes: Vec<[Point; 2]>,
}

pub(super) fn paint_rectangle(subs: &[Subpath]) -> Option<[Point; 4]> {
    let [sub] = subs else { return None };
    if sub.curves != 0 {
        return None;
    }
    let outline = match sub.ps.as_slice() {
        [a, b, c, d] => [*a, *b, *c, *d],
        [a, b, c, d, end] if a.distance(*end) <= 1e-7 => [*a, *b, *c, *d],
        _ => return None,
    };
    rectangle(&outline).then_some(outline)
}

pub(super) fn overlaps(a: &[Point; 4], b: &[Point; 4]) -> bool {
    for axis in [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]] {
        let direction = axis.unit();
        let range = |p: &[Point; 4]| {
            p.iter()
                .map(|q| (*q - a[0]).dot(direction))
                .fold((f64::INFINITY, f64::NEG_INFINITY), |(lo, hi), t| {
                    (lo.min(t), hi.max(t))
                })
        };
        let (l, r) = range(a);
        let (s, t) = range(b);
        if r.min(t) - l.max(s) <= 1e-7 {
            return false;
        }
    }
    true
}

fn safe_state(doc: &Pdf, res: Option<&Dictionary>, name: &[u8]) -> bool {
    let Some(ext) = resource(doc, res, b"ExtGState", name).and_then(|o| dict(doc, o)) else {
        return false;
    };
    ext.iter().all(|(k, v)| match k.as_slice() {
        b"Type" => v.as_name().ok() == Some(b"ExtGState"),
        b"CA" | b"ca" => num(v) == Some(1.),
        b"BM" => v.as_name().ok() == Some(b"Normal"),
        b"SMask" => v.as_name().ok() == Some(b"None"),
        _ => false,
    })
}

impl Cell {
    pub(super) fn read(
        doc: &Pdf,
        raw: &Object,
        default_space: Mat,
        budget: &mut usize,
    ) -> Option<Self> {
        let stream = obj(doc, raw)?.as_stream().ok()?;
        let d = &stream.dict;
        let get = |key: &[u8]| obj(doc, d.get(key).ok()?);
        if get(b"PatternType")?.as_i64().ok()? != 1
            || get(b"PaintType")?.as_i64().ok()? != 1
            || !matches!(get(b"TilingType")?.as_i64().ok()?, 1..=3)
            || d.has(b"OC")
            || (d.has(b"Filter") && stream.filters().is_err())
        {
            return None;
        }
        let x = num(get(b"XStep")?)?;
        let y = num(get(b"YStep")?)?;
        if x <= 0. || y <= 0. {
            return None;
        }
        let [l, b, r, t] = numbers(get(b"BBox")?.as_array().ok()?)?;
        // Strokes used for proof must lie inside the visible pattern cell.
        let matrix = match get(b"Matrix") {
            Some(m) => Mat(numbers(m.as_array().ok()?)?),
            None => Mat::ID,
        };
        let m = default_space.then(matrix);
        let origin = m.point(Point::new(0., 0.));
        let repeat = m.point(Point::new(x, 0.)) - origin;
        let across = m.point(Point::new(0., y)) - origin;
        if !origin.finite()
            || !repeat.finite()
            || !across.finite()
            || repeat.norm() < 1e-6
            || across.norm() < 1e-6
            || repeat.unit().dot(across.unit()).abs() > 1e-5
        {
            return None;
        }
        let res = get(b"Resources").and_then(|o| o.as_dict().ok());
        let bytes = stream
            .get_plain_content_with_limit((*budget).min(32 * 1024))
            .ok()?;
        *budget = budget.checked_sub(bytes.len())?;
        // Require an explicit, visible pen whose width resolves individual marks.
        let (mut ink, mut pen) = (false, false);
        let mut path = Vec::new();
        let mut strokes = Vec::new();
        let mut states = Vec::new();
        for (i, op) in Content::decode_iter(&bytes).enumerate() {
            if i > 1024 {
                return None;
            }
            let op = op.ok()?;
            let a = &op.operands;
            match op.operator.as_str() {
                "q" if states.len() < 16 => states.push((ink, pen)),
                "Q" => (ink, pen) = states.pop()?,
                "G" => ink = numbers(a).is_some_and(|[v]| (0.0..0.99999).contains(&v)),
                "RG" => {
                    ink = numbers(a).is_some_and(|rgb: [f64; 3]| {
                        rgb.iter().all(|v| (0.0..=1.).contains(v))
                            && rgb.iter().any(|&v| v < 0.99999)
                    })
                }
                "K" => {
                    ink = numbers(a).is_some_and(|cmyk: [f64; 4]| {
                        cmyk.iter().all(|v| (0.0..=1.).contains(v))
                            && cmyk.iter().any(|&v| v > 1e-5)
                    })
                }
                "gs" if a.len() == 1 && safe_state(doc, res, a[0].as_name().ok()?) => {}
                "J" | "j" | "rg" | "g" | "k" => {}
                "w" => pen = numbers(a).is_some_and(|[w]| w > 0. && w <= x.min(y) * 0.25),
                "m" | "l" => {
                    let [px, py] = numbers(a)?;
                    if px < l
                        || px > r
                        || py < b
                        || py > t
                        || path.len() + strokes.len() >= 2 * MAX_STROKES
                    {
                        return None;
                    }
                    let pt = Point::new(px / x, py / y);
                    match op.operator.as_str() {
                        "m" => path.push((pt, false)),
                        _ if !path.is_empty() => path.push((pt, true)),
                        _ => return None,
                    }
                }
                "S" if ink && pen => {
                    for pair in path.windows(2) {
                        if pair[1].1 {
                            strokes.push([pair[0].0, pair[1].0]);
                        }
                    }
                    if strokes.len() > MAX_STROKES {
                        return None;
                    }
                    path.clear();
                }
                _ => return None,
            }
        }
        if !path.is_empty() || !states.is_empty() || !batting(&strokes) {
            return None;
        }
        Some(Self {
            origin,
            repeat,
            across,
            strokes,
        })
    }
}
