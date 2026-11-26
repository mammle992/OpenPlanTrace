use super::{MAX_ITEMS, Mat, Warnings, finite};
use crate::model::{Document, Page, Point, Primitive, Shape};
use std::{
    collections::{BTreeMap, BTreeSet},
    f64::consts::TAU,
};

type Pair<'a> = (i16, &'a str);
#[derive(Clone, Copy)]
struct Entity<'a> {
    kind: &'a str,
    fields: &'a [Pair<'a>],
}
impl<'a> Entity<'a> {
    fn s(self, code: i16) -> Option<&'a str> {
        self.fields
            .iter()
            .find(|(k, _)| *k == code)
            .map(|(_, v)| *v)
    }
    fn n(self, code: i16) -> Option<f64> {
        self.s(code)?
            .trim()
            .parse::<f64>()
            .ok()
            .filter(|x| x.is_finite() && x.abs() <= 1e15)
    }
    fn p(self, code: i16) -> Option<Point> {
        Some(Point::new(self.n(code)?, self.n(code + 10)?))
    }
}
struct Block<'a> {
    base: Point,
    entries: Vec<Entity<'a>>,
}
struct Extract<'a> {
    unit_suffix: Option<&'static str>,
    blocks: BTreeMap<String, Block<'a>>,
    hidden: BTreeSet<String>,
    shapes: Vec<Primitive>,
    warnings: Warnings,
    work: usize,
}
fn polar(c: Point, r: f64, a: f64) -> Point {
    Point::new(c.x + r * a.cos(), c.y + r * a.sin())
}
fn arc(t: Mat, c: Point, r: f64, a: f64, sweep: f64) -> Shape {
    if let Some(scale) = t.similarity() {
        let start = t.point(polar(c, r, a));
        let center = t.point(c);
        Shape::Arc {
            center,
            radius: r * scale,
            start_angle: (start.y - center.y).atan2(start.x - center.x),
            sweep: sweep * t.det().signum(),
        }
    } else {
        let n = ((sweep.abs() / 0.06).ceil() as usize).clamp(2, 128);
        Shape::Polyline {
            points: (0..=n)
                .map(|i| t.point(polar(c, r, a + sweep * i as f64 / n as f64)))
                .collect(),
            closed: sweep.abs() >= TAU - 1e-9,
            filled: false,
        }
    }
}

impl<'a> Extract<'a> {
    fn put(&mut self, id: &str, layer: &str, shape: Shape) -> Result<(), String> {
        if self.shapes.len() >= MAX_ITEMS {
            return Err("DXF expanded primitive limit exceeded".into());
        }
        if !valid(&shape) {
            self.warnings
                .add("Invalid or nonfinite DXF geometry omitted");
            return Ok(());
        }
        self.shapes.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: id.into(),
            layer: (!layer.is_empty()).then(|| layer.into()),
            shape,
        });
        Ok(())
    }
    fn vertices(
        &mut self,
        id: &str,
        layer: &str,
        t: Mat,
        v: &[(Point, f64)],
        closed: bool,
    ) -> Result<(), String> {
        if v.len() < 2 {
            return Ok(());
        }
        if v.iter().all(|(_, b)| b.abs() < 1e-12) {
            return self.put(
                id,
                layer,
                Shape::Polyline {
                    points: v.iter().map(|(p, _)| t.point(*p)).collect(),
                    closed,
                    filled: false,
                },
            );
        }
        for i in 0..(if closed { v.len() } else { v.len() - 1 }) {
            let (p, b) = v[i];
            let (q, _) = v[(i + 1) % v.len()];
            let id = format!("{id}:edge:{}", i + 1);
            let dx = q.x - p.x;
            let dy = q.y - p.y;
            if dx.hypot(dy) <= 1e-10 {
                continue;
            }
            let shape = if b.abs() <= 1e-12 {
                Shape::Line {
                    start: t.point(p),
                    end: t.point(q),
                }
            } else {
                let k = (1. - b * b) / (4. * b);
                let c = Point::new((p.x + q.x) / 2. - dy * k, (p.y + q.y) / 2. + dx * k);
                arc(
                    t,
                    c,
                    p.distance(c),
                    (p.y - c.y).atan2(p.x - c.x),
                    4. * b.atan(),
                )
            };
            self.put(&id, layer, shape)?;
        }
        Ok(())
    }
    fn entities(
        &mut self,
        entries: &[Entity<'a>],
        t: Mat,
        parent: &str,
        prefix: &str,
        stack: &mut Vec<String>,
    ) -> Result<(), String> {
        let mut i = 0;
        while i < entries.len() {
            self.work += 1;
            if self.work > MAX_ITEMS * 2 {
                return Err("DXF expanded entity limit exceeded".into());
            }
            let e = entries[i];
            i += 1;
            let layer = e.s(8).unwrap_or("0").trim();
            let layer = if layer == "0" && !parent.is_empty() {
                parent
            } else {
                layer
            };
            if e.n(60) == Some(1.)
                || e.n(67) == Some(1.)
                || e.s(410).is_some_and(|s| !s.eq_ignore_ascii_case("Model"))
                || self.hidden.contains(&layer.to_ascii_uppercase())
            {
                continue;
            }
            if e.n(210).unwrap_or(0.).abs() > 1e-10
                || e.n(220).unwrap_or(0.).abs() > 1e-10
                || (e.n(230).unwrap_or(1.) - 1.).abs() > 1e-10
            {
                self.warnings.add("Non-planar DXF extrusion omitted");
                continue;
            }
            let id = format!("{prefix}{}:{}", i, e.kind);
            match e.kind {
                "LINE" => {
                    if let (Some(a), Some(b)) = (e.p(10), e.p(11)) {
                        self.put(
                            &id,
                            layer,
                            Shape::Line {
                                start: t.point(a),
                                end: t.point(b),
                            },
                        )?
                    } else {
                        self.warnings.add("Incomplete DXF line omitted")
                    }
                }
                "ARC" | "CIRCLE" => {
                    if let (Some(c), Some(r)) = (e.p(10), e.n(40).filter(|r| *r > 0.)) {
                        let (a, s) = if e.kind == "CIRCLE" {
                            (0., TAU)
                        } else if let (Some(a), Some(b)) = (e.n(50), e.n(51)) {
                            (a.to_radians(), (b - a).to_radians().rem_euclid(TAU))
                        } else {
                            self.warnings.add("Incomplete DXF arc omitted");
                            continue;
                        };
                        if s > 1e-10 {
                            self.put(&id, layer, arc(t, c, r, a, s))?
                        }
                    }
                }
                "SOLID" | "TRACE" | "3DFACE" => {
                    let ps = [10, 11, 13, 12];
                    let order = if e.kind == "3DFACE" {
                        [10, 11, 12, 13]
                    } else {
                        ps
                    };
                    let points: Vec<_> = order
                        .iter()
                        .filter_map(|k| e.p(*k))
                        .map(|p| t.point(p))
                        .collect();
                    if points.len() >= 3 {
                        self.put(
                            &id,
                            layer,
                            Shape::Polyline {
                                points,
                                closed: true,
                                filled: e.kind != "3DFACE",
                            },
                        )?
                    }
                }
                "LWPOLYLINE" => {
                    let mut vertices = Vec::new();
                    let mut x = None;
                    let mut y = None;
                    let mut b = 0.;
                    for &(k, v) in e.fields {
                        if k == 10 {
                            if let (Some(x), Some(y)) = (x, y) {
                                vertices.push((Point::new(x, y), b))
                            }
                            x = v.trim().parse().ok();
                            y = None;
                            b = 0.;
                        } else if k == 20 {
                            y = v.trim().parse().ok()
                        } else if k == 42 {
                            b = v.trim().parse().unwrap_or(f64::NAN)
                        }
                    }
                    if let (Some(x), Some(y)) = (x, y) {
                        vertices.push((Point::new(x, y), b))
                    }
                    self.vertices(
                        &id,
                        layer,
                        t,
                        &vertices,
                        e.n(70).unwrap_or(0.) as i64 & 1 != 0,
                    )?;
                }
                "POLYLINE" => {
                    let f = e.n(70).unwrap_or(0.) as i64;
                    let mut vertices = Vec::new();
                    while i < entries.len() && entries[i].kind == "VERTEX" {
                        let v = entries[i];
                        i += 1;
                        if let Some(p) = v.p(10) {
                            vertices.push((p, v.n(42).unwrap_or(0.)))
                        }
                    }
                    if f & (8 | 16 | 64) != 0 {
                        self.warnings.add("3D DXF mesh/polyline omitted")
                    } else {
                        self.vertices(&id, layer, t, &vertices, f & 1 != 0)?
                    }
                }
                "TEXT" | "MTEXT" | "ATTRIB" => {
                    if e.kind == "ATTRIB" && (e.n(70).unwrap_or(0.) as i64 & 1) != 0 {
                        continue;
                    }
                    if let Some(p) = e.p(10) {
                        let value = if e.kind == "MTEXT" {
                            e.fields
                                .iter()
                                .filter(|(k, _)| *k == 1 || *k == 3)
                                .map(|(_, s)| *s)
                                .collect::<String>()
                                .replace("\\P", "\n")
                        } else {
                            e.s(1).unwrap_or("").into()
                        };
                        if !value.is_empty() {
                            self.put(
                                &id,
                                layer,
                                Shape::Text {
                                    at: t.point(p),
                                    value,
                                    height: e.n(40).unwrap_or(1.).abs() * t.0[2].hypot(t.0[3]),
                                },
                            )?
                        }
                    }
                }
                "INSERT" => {
                    if let (Some(name), Some(p)) = (e.s(2), e.p(10)) {
                        let key = name.trim().to_ascii_uppercase();
                        self.put(
                            &id,
                            layer,
                            Shape::Insert {
                                at: t.point(p),
                                name: name.trim().into(),
                            },
                        )?;
                        if stack.contains(&key) || stack.len() >= 16 {
                            self.warnings.add("Cyclic/deep DXF block expansion stopped");
                            continue;
                        }
                        let Some(block) = self.blocks.get(&key) else {
                            self.warnings.add("Missing DXF block definition");
                            continue;
                        };
                        let base = block.base;
                        let children = block.entries.clone();
                        let a = e.n(50).unwrap_or(0.).to_radians();
                        let (s, c) = a.sin_cos();
                        let (sx, sy) = (e.n(41).unwrap_or(1.), e.n(42).unwrap_or(1.));
                        let nx = e.n(70).unwrap_or(1.) as usize;
                        let ny = e.n(71).unwrap_or(1.) as usize;
                        if nx == 0 || ny == 0 || nx.saturating_mul(ny) > 4096 {
                            return Err("DXF insert array size exceeds limit".into());
                        }
                        stack.push(key);
                        for row in 0..ny {
                            for col in 0..nx {
                                self.work += 1;
                                if self.work > MAX_ITEMS * 2 {
                                    return Err("DXF block expansion limit exceeded".into());
                                }
                                let local = Mat::translate(p.x, p.y)
                                    .then(Mat([c, s, -s, c, 0., 0.]))
                                    .then(Mat::translate(
                                        col as f64 * e.n(44).unwrap_or(0.),
                                        row as f64 * e.n(45).unwrap_or(0.),
                                    ))
                                    .then(Mat([sx, 0., 0., sy, 0., 0.]))
                                    .then(Mat::translate(-base.x, -base.y));
                                self.entities(
                                    &children,
                                    t.then(local),
                                    layer,
                                    &format!("{id}:r{row}c{col}:entity:"),
                                    stack,
                                )?;
                            }
                        }
                        stack.pop();
                    }
                }
                "DIMENSION" => {
                    // Do not draw a guessed measurement line: render the dimension's own anonymous block.
                    if let Some(key) = e.s(2).map(str::to_ascii_uppercase) {
                        if let Some(block) = self.blocks.get(&key) {
                            if !stack.contains(&key) && stack.len() < 16 {
                                let children = block.entries.clone();
                                stack.push(key);
                                self.entities(
                                    &children,
                                    t,
                                    layer,
                                    &format!("{id}:graphics:"),
                                    stack,
                                )?;
                                stack.pop();
                                continue;
                            }
                        }
                    }
                    // For a linear dimension the two measured definition points are explicit
                    // DXF input. Do not infer an extension or synthesize witness coordinates.
                    let linear = e.n(70).is_some_and(|v| matches!((v as i64) & 7, 0 | 1));
                    let span = if linear {
                        e.p(13)
                            .zip(e.p(14))
                            .map(|(a, b)| (t.point(a), t.point(b)))
                            .filter(|(a, b)| a.distance(*b) > 0.001)
                    } else {
                        None
                    };
                    let measurement =
                        e.n(42)
                            .filter(|v| *v > 0.)
                            .zip(self.unit_suffix)
                            .map(|(m, s)| {
                                let v = format!("{m:.4}");
                                format!("{} {s}", v.trim_end_matches('0').trim_end_matches('.'))
                            });
                    let raw = e.s(1).unwrap_or("<>").trim();
                    let label = if raw.contains("<>") {
                        measurement.map(|m| raw.replace("<>", &m))
                    } else if raw.is_empty() {
                        None
                    } else {
                        Some(raw.to_string())
                    };
                    let at = e
                        .p(11)
                        .filter(|p| p.x.abs() > 0.001 || p.y.abs() > 0.001)
                        .map(|p| t.point(p))
                        .or_else(|| span.map(|(a, b)| (a + b) * 0.5))
                        .or_else(|| e.p(10).map(|p| t.point(p)));
                    if let Some(((start, end), at)) = span.zip(at) {
                        self.put(
                            &id,
                            layer,
                            Shape::Dimension {
                                start,
                                end,
                                at,
                                value: label.unwrap_or_default(),
                                measurement: e.n(42).filter(|n| *n >= 0.),
                            },
                        )?;
                    } else if let Some((value, at)) = label.zip(at) {
                        self.put(
                            &format!("{id}:dimension:text"),
                            layer,
                            Shape::Text {
                                at,
                                value,
                                height: 1.,
                            },
                        )?;
                    } else if span.is_none() {
                        self.warnings.add(
                            "DXF dimension without supported definition or block graphics omitted",
                        )
                    }
                }
                "SEQEND" | "VERTEX" | "ATTDEF" => {}
                _ => self.warnings.add("Unsupported DXF entity omitted"),
            }
        }
        Ok(())
    }
}
fn valid(shape: &Shape) -> bool {
    match shape {
        Shape::Line { start, end } => {
            finite(*start) && finite(*end) && start.distance(*end) > 1e-10
        }
        Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } => {
            finite(*center)
                && radius.is_finite()
                && *radius > 0.
                && *radius < 1e15
                && start_angle.is_finite()
                && sweep.is_finite()
        }
        Shape::Polyline { points, .. } => points.len() >= 2 && points.iter().copied().all(finite),
        Shape::Text { at, height, .. } => finite(*at) && height.is_finite() && *height > 0.,
        Shape::Insert { at, .. } => finite(*at),
        Shape::Image { min, max } => finite(*min) && finite(*max),
        Shape::Dimension {
            start,
            end,
            at,
            measurement,
            ..
        } => {
            finite(*start)
                && finite(*end)
                && finite(*at)
                && measurement.is_none_or(|v| v.is_finite() && v >= 0.)
        }
    }
}

fn insunits(unit: i32) -> Option<f64> {
    Some(match unit {
        1 => 25.4,
        2 => 304.8,
        3 => 1_609_344.,
        4 => 1.,
        5 => 10.,
        6 => 1000.,
        7 => 1_000_000.,
        8 => 0.0000254,
        9 => 0.0254,
        10 => 914.4,
        11 => 0.0000001,
        12 => 0.000001,
        13 => 0.001,
        14 => 100.,
        15 => 10000.,
        16 => 100000.,
        21 => 1_200_000. / 3937.,
        22 => 100000. / 3937.,
        23 => 3_600_000. / 3937.,
        24 => 6_336_000_000. / 3937.,
        _ => return None,
    })
}

pub(super) fn read(bytes: &[u8]) -> Result<Document, String> {
    if bytes.starts_with(b"AutoCAD Binary DXF") || bytes.contains(&0) {
        return Err("Binary DXF is not supported".into());
    }
    let data = String::from_utf8_lossy(bytes);
    let mut lines = data.trim_start_matches('\u{feff}').lines();
    let mut pairs = Vec::new();
    while let Some(code) = lines.next() {
        let code = code
            .trim()
            .parse::<i16>()
            .map_err(|_| "Invalid ASCII DXF group code")?;
        let value = lines
            .next()
            .ok_or("DXF group code missing value")?
            .trim_end_matches('\r');
        pairs.push((code, value));
        if pairs.len() > 4_000_000 {
            return Err("DXF group count limit exceeded".into());
        }
    }
    let mut records = Vec::new();
    let mut i = 0;
    while i < pairs.len() {
        if pairs[i].0 != 0 {
            i += 1;
            continue;
        }
        let kind = pairs[i].1.trim();
        let start = i + 1;
        i = start;
        while i < pairs.len() && pairs[i].0 != 0 {
            i += 1
        }
        records.push(Entity {
            kind,
            fields: &pairs[start..i],
        });
    }
    let mut model = Vec::new();
    let mut blocks = BTreeMap::new();
    let mut block_key = None;
    let mut section = "";
    let mut hidden = BTreeSet::new();
    let mut units = None;
    let mut unit_suffix = None;
    let mut entities_found = false;
    for e in records {
        match e.kind {
            "SECTION" => {
                section = e.s(2).unwrap_or("").trim();
                if section == "ENTITIES" {
                    entities_found = true
                }
                if section == "HEADER" {
                    for w in e.fields.windows(2) {
                        if w[0].0 == 9 && w[0].1.trim() == "$INSUNITS" && w[1].0 == 70 {
                            let code = w[1].1.trim().parse().ok();
                            units = code.and_then(insunits);
                            unit_suffix = match code {
                                Some(1 | 22) => Some("in"),
                                Some(2 | 21) => Some("ft"),
                                Some(4) => Some("mm"),
                                Some(5) => Some("cm"),
                                Some(6) => Some("m"),
                                _ => None,
                            };
                        }
                    }
                }
            }
            "ENDSEC" => {
                section = "";
                block_key = None
            }
            "BLOCK" if section == "BLOCKS" => {
                if let Some(name) = e.s(2) {
                    let key = name.trim().to_ascii_uppercase();
                    if blocks.contains_key(&key) {
                        return Err("Duplicate DXF block definition".into());
                    }
                    blocks.insert(
                        key.clone(),
                        Block {
                            base: e.p(10).unwrap_or(Point::new(0., 0.)),
                            entries: Vec::new(),
                        },
                    );
                    block_key = Some(key);
                }
            }
            "ENDBLK" if section == "BLOCKS" => block_key = None,
            "LAYER" if section == "TABLES" => {
                if e.n(62).unwrap_or(7.) < 0. || e.n(70).unwrap_or(0.) as i64 & 1 != 0 {
                    if let Some(name) = e.s(2) {
                        hidden.insert(name.trim().to_ascii_uppercase());
                    }
                }
            }
            _ if section == "BLOCKS" => {
                if let Some(ref key) = block_key {
                    if let Some(b) = blocks.get_mut(key) {
                        b.entries.push(e)
                    }
                }
            }
            _ if section == "ENTITIES" => model.push(e),
            _ => {}
        }
    }
    if !entities_found {
        return Err("No ENTITIES section in ASCII DXF".into());
    }
    let mut extract = Extract {
        unit_suffix,
        blocks,
        hidden,
        shapes: vec![],
        warnings: Warnings::default(),
        work: 0,
    };
    extract.entities(&model, Mat::ID, "", "dxf:entity:", &mut vec![])?;
    if matches!(data, std::borrow::Cow::Owned(_)) {
        extract
            .warnings
            .add("Non-UTF8 DXF strings decoded with replacement characters")
    }
    let (mut min, mut max) = (
        Point::new(f64::INFINITY, f64::INFINITY),
        Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY),
    );
    let mut observe = |p: Point| {
        min.x = min.x.min(p.x);
        min.y = min.y.min(p.y);
        max.x = max.x.max(p.x);
        max.y = max.y.max(p.y)
    };
    for item in &extract.shapes {
        match &item.shape {
            Shape::Line { start, end } => {
                observe(*start);
                observe(*end)
            }
            Shape::Polyline { points, .. } => points.iter().copied().for_each(&mut observe),
            Shape::Arc { center, radius, .. } => {
                observe(Point::new(center.x - radius, center.y - radius));
                observe(Point::new(center.x + radius, center.y + radius))
            }
            Shape::Text { at, height, value } => {
                observe(*at);
                observe(Point::new(
                    at.x + height * (value.chars().count() as f64 * 0.6).max(1.),
                    at.y + height,
                ))
            }
            Shape::Insert { at, .. } => observe(*at),
            Shape::Dimension { start, end, at, .. } => {
                observe(*start);
                observe(*end);
                observe(*at)
            }
            Shape::Image { min, max } => {
                observe(*min);
                observe(*max)
            }
        }
    }
    if !finite(min) || !finite(max) {
        min = Point::new(0., 0.);
        max = Point::new(1., 1.)
    }
    let flip = Mat([1., 0., 0., -1., 20. - min.x, 20. + max.y]);
    for item in &mut extract.shapes {
        match &mut item.shape {
            Shape::Line { start, end } => {
                *start = flip.point(*start);
                *end = flip.point(*end)
            }
            Shape::Polyline { points, .. } => points.iter_mut().for_each(|p| *p = flip.point(*p)),
            Shape::Arc {
                center,
                start_angle,
                sweep,
                ..
            } => {
                *center = flip.point(*center);
                *start_angle = -*start_angle;
                *sweep = -*sweep
            }
            Shape::Text { at, .. } | Shape::Insert { at, .. } => *at = flip.point(*at),
            Shape::Dimension { start, end, at, .. } => {
                *start = flip.point(*start);
                *end = flip.point(*end);
                *at = flip.point(*at)
            }
            Shape::Image { .. } => unreachable!(),
        }
    }
    if units.is_none() {
        extract
            .warnings
            .add("DXF drawing units unknown; millimeter calibration unavailable")
    }
    Ok(Document {
        pages: vec![Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            recessed_stock: vec![],
            masked_junction_stock: vec![],
            pocket_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: (max.x - min.x).max(1.) + 40.,
            height: (max.y - min.y).max(1.) + 40.,
            units_to_mm: units,
            primitives: extract.shapes,
        }],
        warnings: extract.warnings.finish(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn expands_insert_relative_to_block_base_and_skips_hidden_layers() {
        let f=b"0\nSECTION\n2\nBLOCKS\n0\nBLOCK\n2\nW\n10\n2\n20\n3\n0\nLINE\n8\n0\n10\n2\n20\n3\n11\n12\n21\n3\n0\nENDBLK\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nINSERT\n2\nW\n8\nA-WALL\n10\n40\n20\n80\n41\n2\n50\n90\n0\nLINE\n60\n1\n10\n0\n20\n0\n11\n500\n21\n100\n0\nENDSEC\n0\nEOF\n";
        let d = read(f).unwrap();
        let ps = &d.pages[0].primitives;
        assert_eq!(ps.len(), 2);
        assert_eq!(ps[1].layer.as_deref(), Some("A-WALL"));
        let Shape::Line { start, end } = ps[1].shape else {
            panic!()
        };
        assert!((start.x - 20.).abs() < 1e-8);
        assert!((start.y - 40.).abs() < 1e-8);
        assert!((end.y - 20.).abs() < 1e-8);
    }
    #[test]
    fn bulge_quarter_arc_preserves_finite_signed_extent() {
        let f=b"0\nSECTION\n2\nENTITIES\n0\nLWPOLYLINE\n10\n10\n20\n0\n42\n0.414213562373095\n10\n0\n20\n10\n0\nENDSEC\n0\nEOF\n";
        let d = read(f).unwrap();
        let Shape::Arc { radius, sweep, .. } = d.pages[0].primitives[0].shape else {
            panic!()
        };
        assert!((radius - 10.).abs() < 1e-8);
        assert!((sweep + std::f64::consts::FRAC_PI_2).abs() < 1e-8);
    }
    #[test]
    fn handles_dxf_header_units_without_guessing_unitless_files() {
        let f=b"0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n1\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n";
        assert_eq!(read(f).unwrap().pages[0].units_to_mm, Some(25.4));
        assert_eq!(
            read(b"0\nSECTION\n2\nENTITIES\n0\nENDSEC\n0\nEOF\n")
                .unwrap()
                .pages[0]
                .units_to_mm,
            None
        );
    }
    #[test]
    fn bad_group_and_binary_input_are_rejected() {
        assert!(read(b"0\nSECTION\nABC\nENTITIES\n").is_err());
        assert!(read(b"AutoCAD Binary DXF\r\n\x1a\0").is_err());
    }
    #[test]
    fn linear_dimension_uses_defined_points_and_header_units_without_becoming_wall_line() {
        let d=read(b"0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n4\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nDIMENSION\n70\n1\n13\n25\n23\n18\n14\n65\n24\n18\n42\n40\n0\nENDSEC\n0\nEOF\n").unwrap();
        let Shape::Dimension {
            start,
            end,
            value,
            measurement,
            ..
        } = &d.pages[0].primitives[0].shape
        else {
            panic!()
        };
        assert_eq!(start.distance(*end), 40.);
        assert_eq!(*measurement, Some(40.));
        assert_eq!(value, "40 mm");
        assert_eq!(d.pages[0].primitives.len(), 1);
    }
}
