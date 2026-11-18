use crate::{
    model::*,
    semantics::{Bounds, bounds},
};
use std::collections::BTreeSet;

pub(super) struct Crop {
    pub rgb: Vec<u8>,
    pub area: Bounds,
    pub sources: Vec<String>,
}
struct Canvas {
    width: usize,
    height: usize,
    rgb: Vec<u8>,
    area: Bounds,
}
impl Canvas {
    fn segment(&mut self, a: Point, b: Point, color: [u8; 3]) -> bool {
        // Clip in drawing space before rasterization. A remote endpoint must not leave an edge streak.
        let d = b - a;
        let mut lo: f64 = 0.;
        let mut hi: f64 = 1.;
        for (p, q) in [
            (-d.x, a.x - self.area.min.x),
            (d.x, self.area.max.x - a.x),
            (-d.y, a.y - self.area.min.y),
            (d.y, self.area.max.y - a.y),
        ] {
            if p == 0. {
                if q < 0. {
                    return false;
                }
            } else if p < 0. {
                lo = lo.max(q / p)
            } else {
                hi = hi.min(q / p)
            }
        }
        if lo > hi {
            return false;
        }
        let pixel = |p: Point| {
            (
                ((p.x - self.area.min.x) / (self.area.max.x - self.area.min.x)
                    * (self.width - 1) as f64)
                    .round_ties_even()
                    .clamp(0., (self.width - 1) as f64) as i32,
                ((p.y - self.area.min.y) / (self.area.max.y - self.area.min.y)
                    * (self.height - 1) as f64)
                    .round_ties_even()
                    .clamp(0., (self.height - 1) as f64) as i32,
            )
        };
        let (mut x, mut y) = pixel(a + d * lo);
        let (u, v) = pixel(a + d * hi);
        let dx = (u - x).abs();
        let dy = -(v - y).abs();
        let sx = (u - x).signum();
        let sy = (v - y).signum();
        let mut error = dx + dy;
        loop {
            let i = 3 * (y as usize * self.width + x as usize);
            self.rgb[i..i + 3].copy_from_slice(&color);
            if x == u && y == v {
                break;
            }
            let e = 2 * error;
            if e >= dy {
                error += dy;
                x += sx
            }
            if e <= dx {
                error += dx;
                y += sy
            }
        }
        true
    }
    fn draw(&mut self, s: &Shape) -> bool {
        const INK: [u8; 3] = [24, 28, 32];
        match s {
            Shape::Line { start, end } => self.segment(*start, *end, INK),
            Shape::Polyline { points, closed, .. } => {
                let mut seen = false;
                for p in points.windows(2) {
                    seen |= self.segment(p[0], p[1], INK)
                }
                if *closed && points.len() > 2 {
                    seen |= self.segment(points[points.len() - 1], points[0], INK)
                }
                seen
            }
            Shape::Arc {
                center,
                radius,
                start_angle,
                sweep,
            } => {
                let arc = ArcGeometry {
                    center: *center,
                    radius: *radius,
                    start_angle: *start_angle,
                    sweep: *sweep,
                };
                let mut seen = false;
                let mut a = arc.at(0.);
                for i in 1..=32 {
                    let b = arc.at(f64::from(i) / 32.);
                    seen |= self.segment(a, b, INK);
                    a = b
                }
                seen
            }
            // Native INSERT stores its observed anchor. Expanded children contribute their own strokes.
            Shape::Insert { at, .. } => self.segment(*at, *at, [58, 92, 170]),
            Shape::Dimension { .. } | Shape::Text { .. } | Shape::Image { .. } => false,
        }
    }
}
pub(super) fn render(
    page: &PageScan,
    object: &Object,
    member: &crate::object_labels::Member,
    width: usize,
    height: usize,
    padding: f64,
) -> Option<Crop> {
    let min = member.bounds.min;
    let max = member.bounds.max;
    let span = max - min;
    let pad = if span.x > 0. && span.y > 0. {
        padding.min(
            (span.x.max(span.y) * 0.6)
                .min(span.x.min(span.y) * 1.1)
                .max(2.),
        )
    } else {
        padding
    };
    let area = Bounds {
        min: Point::new((min.x - pad).max(0.), (min.y - pad).max(0.)),
        max: Point::new(
            (max.x + pad).min(page.width),
            (max.y + pad).min(page.height),
        ),
    };
    if area.max.x <= area.min.x || area.max.y <= area.min.y {
        return None;
    }
    let mut canvas = Canvas {
        width,
        height,
        rgb: vec![255; width * height * 3],
        area,
    };
    let mut sources: BTreeSet<&str> = object.source_ids.iter().map(String::as_str).collect();
    let mut ink = false;
    for p in &page.primitives {
        let Some(b) = bounds(&p.shape) else { continue };
        if b.max.x < canvas.area.min.x
            || b.min.x > canvas.area.max.x
            || b.max.y < canvas.area.min.y
            || b.min.y > canvas.area.max.y
        {
            continue;
        }
        if canvas.draw(&p.shape) {
            ink = true;
            sources.insert(&p.id);
        }
    }
    ink.then(|| Crop {
        rgb: canvas.rgb,
        area: canvas.area,
        sources: sources.into_iter().map(str::to_owned).collect(),
    })
}

pub(super) fn targets(
    page: &PageScan,
) -> Result<Vec<(&Object, Vec<String>, crate::object_labels::Member)>, String> {
    use std::collections::{HashMap, HashSet};
    let by_id: HashMap<_, _> = page.objects.iter().map(|o| (o.id.as_str(), o)).collect();
    let mut groups: Vec<_> = page.object_groups.iter().collect();
    groups.sort_by(|a, b| {
        b.member_ids
            .len()
            .cmp(&a.member_ids.len())
            .then(a.name.cmp(&b.name))
    });
    let mut observed = HashSet::new();
    let mut targets = Vec::new();
    for group in groups {
        let mut members: Vec<_> = group
            .member_ids
            .iter()
            .filter_map(|id| by_id.get(id.as_str()).copied())
            .collect();
        members.sort_by(|a, b| {
            a.at.y
                .total_cmp(&b.at.y)
                .then(a.at.x.total_cmp(&b.at.x))
                .then(a.id.cmp(&b.id))
        });
        if let Some(first) = members.first() {
            targets.push((*first, group.member_ids.clone()));
            observed.extend(group.member_ids.iter().map(String::as_str))
        }
    }
    for o in &page.objects {
        if !observed.contains(o.id.as_str()) && o.category != "TextLabel" {
            targets.push((o, vec![o.id.clone()]))
        }
    }

    let mut sources: HashMap<_, _> = crate::object_labels::source_members(page)?
        .into_iter()
        .map(|m| (m.object_id.clone(), m))
        .collect();
    targets
        .into_iter()
        .map(|(o, ids)| {
            let member = sources
                .remove(&o.id)
                .ok_or("missing visual candidate provenance")?;
            Ok((o, ids, member))
        })
        .collect()
}
