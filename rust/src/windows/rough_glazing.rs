//! Recover the rough aperture of a signed exterior window from facing source stock.
use crate::{
    layers::named_exterior_wall,
    model::{
        BodyEvidence, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape,
        Wall, WindowPane,
    },
    placement::{WallStatus, effective_wall, wall_layers},
    semantics::{RoutingItem, RoutingKind, Span},
};
use std::{
    borrow::Cow,
    collections::{BTreeMap, BTreeSet, HashMap, HashSet},
};

const MAX_WORK: usize = 16_000_000;
const EVIDENCE: OpeningEvidence = OpeningEvidence::AuthoredExteriorRoughGlazing;

#[derive(Clone, Copy)]
struct Stock<'a> {
    host: &'a str,
    paint: &'a str,
    cap: Point,
    outward: Point,
    width: f64,
}

#[derive(Clone, Copy)]
struct Ink<'a> {
    source: &'a str,
    ends: [Point; 2],
    pen: f64,
}

#[derive(Default)]
struct Sources<'a> {
    signs: Vec<(&'a str, Point)>,
    ink: Vec<Ink<'a>>,
    stock: Vec<Stock<'a>>,
}

struct Frame {
    origin: Point,
    axis: Point,
    len: f64,
    width: f64,
    eps: f64,
}

impl Frame {
    fn between(a: Stock<'_>, b: Stock<'_>) -> Option<Self> {
        let d = b.cap - a.cap;
        let len = d.dot(a.outward);
        let eps = 128. * f64::EPSILON * a.cap.norm().max(b.cap.norm()).max(1.) + 1e-9;
        ((3. * a.width..=18. * a.width).contains(&len)
            && a.host != b.host
            && a.paint != b.paint
            && a.outward.dot(b.outward) < 0.
            && a.outward.cross(b.outward).abs() * len <= eps
            && a.outward.cross(d).abs() <= eps
            && (a.width - b.width).abs() <= eps)
            .then_some(Self {
                origin: a.cap,
                axis: a.outward,
                len,
                width: a.width,
                eps,
            })
    }

    fn at(&self, p: Point) -> Point {
        let d = p - self.origin;
        Point::new(self.axis.dot(d), self.axis.cross(d))
    }

    fn world(&self, x: f64, y: f64) -> Point {
        self.origin + self.axis * x + Point::new(-self.axis.y, self.axis.x) * y
    }

    fn eq(&self, a: f64, b: f64) -> bool {
        (a - b).abs() <= self.eps
    }

    fn signs<'a>(&self, sources: &Sources<'a>) -> Option<&'a str> {
        let mut nearby = sources.signs.iter().filter(|(_, p)| {
            let p = self.at(*p);
            (0.22 * self.len..=0.78 * self.len).contains(&p.x)
                && (0.75 * self.width..=3. * self.width).contains(&p.y.abs())
        });
        let &(id, _) = nearby.next()?;
        nearby.next().is_none().then_some(id)
    }

    fn intersects(
        &self,
        points: impl IntoIterator<Item = Point>,
        cross: [f64; 2],
        margin: f64,
    ) -> bool {
        let (mut x, mut y) = (
            [f64::INFINITY, f64::NEG_INFINITY],
            [f64::INFINITY, f64::NEG_INFINITY],
        );
        for p in points {
            let q = self.at(p);
            x = [x[0].min(q.x), x[1].max(q.x)];
            y = [y[0].min(q.y), y[1].max(q.y)];
        }
        x[1] + margin > self.eps
            && x[0] - margin < self.len - self.eps
            && y[1] + margin > cross[0] + self.eps
            && y[0] - margin < cross[1] - self.eps
    }

    fn contains(&self, p: Point, cross: [f64; 2]) -> bool {
        let q = self.at(p);
        q.x > self.eps
            && q.x < self.len - self.eps
            && q.y >= cross[0] - self.eps
            && q.y <= cross[1] + self.eps
    }
}

#[derive(Clone, Copy)]
struct Stroke<'a> {
    source: &'a str,
    at: f64,
    span: [f64; 2],
    pen: f64,
}

fn local_ink<'a>(f: &Frame, lines: &[Ink<'a>]) -> [Vec<Stroke<'a>>; 2] {
    let mut marks = [vec![], vec![]];
    for line in lines {
        let [a, b] = line.ends.map(|p| f.at(p));
        if a.x.min(b.x) < -f.eps
            || a.x.max(b.x) > f.len + f.eps
            || a.y.abs().max(b.y.abs()) > f.width * 1.4
        {
            continue;
        }
        if f.eq(a.y, b.y) && !f.eq(a.x, b.x) {
            marks[0].push(Stroke {
                source: line.source,
                at: a.y,
                span: [a.x.min(b.x), a.x.max(b.x)],
                pen: line.pen,
            });
        } else if f.eq(a.x, b.x) && !f.eq(a.y, b.y) {
            marks[1].push(Stroke {
                source: line.source,
                at: a.x,
                span: [a.y.min(b.y), a.y.max(b.y)],
                pen: line.pen,
            });
        }
    }
    for group in &mut marks {
        group.sort_unstable_by(|a, b| a.source.cmp(b.source));
    }
    marks
}

fn matching<'a>(f: &Frame, lines: &[Stroke<'a>], at: f64, span: [f64; 2]) -> Option<Stroke<'a>> {
    lines
        .iter()
        .copied()
        .find(|s| f.eq(s.at, at) && f.eq(s.span[0], span[0]) && f.eq(s.span[1], span[1]))
}

fn path(id: &str) -> &str {
    id.split_once(":subpath:").map_or(id, |(p, _)| p)
}

fn stepped_cap<'a>(
    f: &Frame,
    ink: &[Vec<Stroke<'a>>; 2],
    rails: [Stroke<'a>; 2],
    side: usize,
    outer: usize,
) -> Option<(f64, [&'a str; 4])> {
    let [along, across] = ink;
    let inner = 1 - outer;
    let rough = if side == 0 { 0. } else { f.len };
    let face = if outer == 0 { -0.5 } else { 0.5 } * f.width;
    let [rim, inset] = rails.map(|r| r.span[side]);
    let stations = [rim, inset];
    if stations
        .iter()
        .any(|at| (*at - rough).abs() < 0.015 * f.width || (*at - rough).abs() > 0.35 * f.width)
    {
        return None;
    }
    let join = |at: f64| [rough.min(at), rough.max(at)];
    let return_outer = matching(f, along, rails[outer].at, join(stations[outer]))?;
    let return_inner = matching(f, along, rails[inner].at, join(stations[inner]))?;
    let mut matches = across.iter().filter_map(|s| {
        let split = s.span[inner];
        if !f.eq(s.at, rough) || !f.eq(s.span[outer], face) || split.abs() > 0.18 * f.width {
            return None;
        }
        let edge = rails[inner].at;
        let step = matching(
            f,
            across,
            stations[inner],
            [split.min(edge), split.max(edge)],
        )?;
        let ids = [
            s.source,
            step.source,
            return_outer.source,
            return_inner.source,
        ];
        (ids.map(path).into_iter().collect::<BTreeSet<_>>().len() == ids.len())
            .then_some((split, ids))
    });
    let one = matches.next()?;
    matches.all(|m| f.eq(one.0, m.0)).then_some(one)
}

fn glazings<'a>(
    f: &Frame,
    ink: &[Vec<Stroke<'a>>; 2],
    rim: [Stroke<'a>; 2],
    work: &mut usize,
) -> Option<Option<[&'a str; 5]>> {
    let [along, across] = ink;
    let lo = rim[0].at;
    let hi = rim[1].at;
    let rails = along
        .iter()
        .copied()
        .filter(|s| {
            s.at > lo + f.eps
                && s.at < hi - f.eps
                && s.span[0] > f.eps
                && s.span[1] < f.len - f.eps
                && s.span[1] - s.span[0] >= 0.65 * f.len
        })
        .collect::<Vec<_>>();
    charge(
        work,
        rails
            .len()
            .saturating_pow(2)
            .saturating_mul(along.len().saturating_add(across.len().saturating_mul(2))),
    )?;
    let mut proof = None;
    for lower in &rails {
        for middle in &rails {
            let upper = middle.at + middle.at - lower.at;
            let gap = upper - lower.at;
            if !(0.10 * (hi - lo)..=0.65 * (hi - lo)).contains(&gap)
                || !f.eq(lower.span[0], middle.span[0])
                || !f.eq(lower.span[1], middle.span[1])
                || !f.eq(lower.pen, middle.pen)
            {
                continue;
            }
            let same_pen = |s: &&Stroke<'a>| f.eq(s.pen, lower.pen);
            let find = |marks: &[Stroke<'a>], at: f64, span: [f64; 2]| {
                marks.iter().filter(same_pen).copied().find(|s| {
                    f.eq(s.at, at) && f.eq(s.span[0], span[0]) && f.eq(s.span[1], span[1])
                })
            };
            let (Some(upper), Some(l), Some(r)) = (
                find(along, upper, lower.span),
                find(across, lower.span[0], [lower.at, upper]),
                find(across, lower.span[1], [lower.at, upper]),
            ) else {
                continue;
            };
            let ids = [
                lower.source,
                middle.source,
                upper.source,
                l.source,
                r.source,
            ];
            if ids.map(path).into_iter().collect::<BTreeSet<_>>().len() != ids.len() {
                continue;
            }
            let geometry = [lower.at, upper.at, lower.span[0], lower.span[1]];
            if let Some((old, _)) = proof {
                if !geometry.into_iter().zip(old).all(|(a, b)| f.eq(a, b)) {
                    return Some(None);
                }
            } else {
                proof = Some((geometry, ids));
            }
        }
    }
    Some(proof.map(|(_, ids)| ids))
}

struct Casing<'a> {
    cross: [f64; 2],
    rim: [Stroke<'a>; 2],
    split: f64,
    exterior: usize,
    ids: Vec<&'a str>,
}

fn casing<'a>(
    f: &Frame,
    ink: &[Vec<Stroke<'a>>; 2],
    work: &mut usize,
) -> Option<Option<Casing<'a>>> {
    let [along, across] = ink;
    let rim = along
        .iter()
        .copied()
        .filter(|s| {
            (0.55 * f.width..=f.width).contains(&s.at.abs())
                && s.span[1] - s.span[0] >= 0.85 * f.len
                // Mirrored drafted returns can have slightly different inset sizes.
                // Each return still has to match its original cap and rail exactly.
                && (s.span[0] - (f.len - s.span[1])).abs() <= 0.001 * f.width
        })
        .collect::<Vec<_>>();
    charge(
        work,
        rim.len()
            .saturating_pow(2)
            .saturating_mul(along.len().saturating_add(across.len().saturating_pow(2))),
    )?;
    let mut proof = None;
    for ls in rim.iter().filter(|r| r.at < 0.) {
        for rs in rim.iter().filter(|r| r.at > 0.) {
            let rails = [*ls, *rs];
            if path(ls.source) == path(rs.source) {
                continue;
            }
            for exterior in 0..2 {
                let (Some((a, l)), Some((b, r))) = (
                    stepped_cap(f, ink, rails, 0, exterior),
                    stepped_cap(f, ink, rails, 1, exterior),
                ) else {
                    continue;
                };
                if !f.eq(a, b) {
                    continue;
                }
                let Some(glass) = glazings(f, ink, rails, work)? else {
                    continue;
                };
                let ids = [ls.source, rs.source]
                    .into_iter()
                    .chain(l)
                    .chain(r)
                    .chain(glass)
                    .collect::<Vec<_>>();
                if ids.iter().map(|id| path(id)).collect::<BTreeSet<_>>().len() != ids.len() {
                    continue;
                }
                let cross = [ls.at, rs.at];
                if let Some(Casing { cross: old, .. }) = proof {
                    if !f.eq(cross[0], old[0]) || !f.eq(cross[1], old[1]) {
                        return Some(None);
                    }
                } else {
                    proof = Some(Casing {
                        cross,
                        rim: rails,
                        split: a,
                        exterior,
                        ids,
                    });
                }
            }
        }
    }
    Some(proof)
}

fn charge(work: &mut usize, amount: usize) -> Option<()> {
    *work = work.checked_sub(amount)?;
    Some(())
}

fn certified_rectangle(p: &Primitive, w: &Wall) -> bool {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return false;
    };
    let len = w.start.distance(w.end);
    let half = w.thickness * 0.5;
    if !(4..=24).contains(&points.len())
        || !len.is_finite()
        || len <= 0.
        || !half.is_finite()
        || half <= 0.
    {
        return false;
    }
    let axis = (w.end - w.start) * (1. / len);
    let eps = 128. * f64::EPSILON * w.start.norm().max(w.end.norm()).max(1.) + 1e-9;
    let local = |p: Point| {
        let d = p - w.start;
        Point::new(d.dot(axis), axis.cross(d))
    };
    let corners =
        [(0., -half), (len, -half), (len, half), (0., half)].map(|(x, y)| Point::new(x, y));
    let points = points.iter().copied().map(local).collect::<Vec<_>>();
    let mut swept = 0.;
    for i in 0..points.len() {
        let [a, b] = [points[i], points[(i + 1) % points.len()]];
        let same_x = (a.x - b.x).abs() <= eps;
        let same_y = (a.y - b.y).abs() <= eps;
        let on_end = a.x.abs() <= eps || (a.x - len).abs() <= eps;
        let on_face = (a.y.abs() - half).abs() <= eps;
        if !a.finite()
            || a.x < -eps
            || a.x > len + eps
            || a.y.abs() > half + eps
            || !(same_x && on_end || same_y && on_face)
        {
            return false;
        }
        swept += a.cross(b);
    }
    corners
        .into_iter()
        .all(|c| points.iter().any(|p| c.distance(*p) <= eps))
        && (swept.abs() - 4. * len * half).abs() <= 4. * (len + half) * eps
}

fn wall_outline(w: &Wall) -> Option<[Point; 4]> {
    if let Some(c) = w.curve {
        let d = c.radius + w.thickness * 0.5;
        return Some(
            [(-d, -d), (d, -d), (d, d), (-d, d)].map(|(x, y)| c.center + Point::new(x, y)),
        );
    }
    let d = w.end - w.start;
    let u = d.norm();
    if !u.is_finite() || u <= 0. || !w.thickness.is_finite() || w.thickness <= 0. {
        return None;
    }
    let n = Point::new(-d.y, d.x) * (w.thickness * 0.5 / u);
    Some([w.start - n, w.start + n, w.end + n, w.end - n])
}

fn window_sign(p: &Primitive) -> Option<Point> {
    let Shape::Text { value, at, .. } = &p.shape else {
        return None;
    };
    matches!(
        value.trim().to_ascii_lowercase().as_str(),
        "v" | "vindu" | "window"
    )
    .then_some(*at)
}

fn route(opening: &Opening) -> RoutingItem {
    RoutingItem {
        kind: RoutingKind::WindowCandidate,
        entity_id: opening.id.clone(),
        source_ids: opening.source_ids.clone(),
        span: Some(Span {
            start: opening.start,
            end: opening.end,
        }),
        curve: None,
        room_ids: vec![],
        usable_for_routing: false,
        requires_review: true,
    }
}

fn review_route(opening: &Opening, r: &RoutingItem) -> bool {
    r.kind == RoutingKind::WindowCandidate
        && r.entity_id == opening.id
        && r.source_ids == opening.source_ids
        && r.span
            .as_ref()
            .is_some_and(|s| s.start == opening.start && s.end == opening.end)
        && r.curve.is_none()
        && r.room_ids.is_empty()
        && !r.usable_for_routing
        && r.requires_review
}

fn jamb_finish(f: &Frame, c: &Casing<'_>, outline: &[Point]) -> bool {
    if !(4..=12).contains(&outline.len()) {
        return false;
    }
    let points = outline.iter().map(|&p| f.at(p)).collect::<Vec<_>>();
    let bounds = |values: Vec<f64>| {
        values
            .into_iter()
            .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], x| {
                [lo.min(x), hi.max(x)]
            })
    };
    let x = bounds(points.iter().map(|p| p.x).collect());
    let y = bounds(points.iter().map(|p| p.y).collect());
    let outer = c.exterior;
    let inner = 1 - outer;
    let side = if f.eq(x[0], 0.) {
        0
    } else if f.eq(x[1], f.len) {
        1
    } else {
        return false;
    };
    let inward = x[1 - side];
    let start = x[side];
    if inward <= f.eps || inward >= f.len - f.eps {
        return false;
    }
    let face = if outer == 0 { -0.5 } else { 0.5 } * f.width;
    let inner_finish = f.eq(inward, c.rim[inner].span[side])
        && f.eq(y[outer], c.split)
        && f.eq(y[inner], c.cross[inner]);
    let outer_finish = (inward - start).abs() <= (c.rim[outer].span[side] - start).abs() + f.eps
        && f.eq(y[inner], face)
        && y[0] >= c.cross[0] - f.eps
        && y[1] <= c.cross[1] + f.eps;
    if !inner_finish && !outer_finish {
        return false;
    }
    let area = (x[1] - x[0]) * (y[1] - y[0]);
    if area <= f.eps {
        return false;
    }
    let origin = Point::new(x[0], y[0]);
    let mut sweep = 0.;
    for i in 0..points.len() {
        let [a, b] = [points[i], points[(i + 1) % points.len()]];
        if !(f.eq(a.x, b.x) && x.into_iter().any(|v| f.eq(a.x, v))
            || f.eq(a.y, b.y) && y.into_iter().any(|v| f.eq(a.y, v)))
        {
            return false;
        }
        sweep += (a - origin).cross(b - origin);
    }
    (sweep.abs() - 2. * area).abs() <= 4. * (x[1] - x[0] + y[1] - y[0]) * f.eps
}

struct Proposal {
    frame: Opening,
    pane: Option<usize>,
    outline: [Point; 4],
}

fn proposals(page: &PageScan, work: &mut usize) -> Option<Vec<Proposal>> {
    charge(work, page.primitives.len().saturating_mul(2))?;
    let mut by_layer = BTreeMap::<&str, Sources<'_>>::new();
    for p in &page.primitives {
        if p.id.starts_with("pdf:")
            && let Some(at) = window_sign(p)
            && let Some(layer) = named_exterior_wall(p)
        {
            by_layer.entry(layer).or_default().signs.push((&p.id, at));
        }
    }
    if by_layer.is_empty() {
        return Some(vec![]);
    }
    let visible = page
        .placement_paint_ids
        .iter()
        .flatten()
        .chain(page.placement_polygon_paint_ids.iter().flatten())
        .map(String::as_str)
        .collect::<HashSet<_>>();
    let mut native = HashMap::new();
    let mut paint = vec![];
    for p in &page.primitives {
        if !p.id.starts_with("pdf:") {
            continue;
        }
        if visible.contains(p.id.as_str())
            && let Shape::Polyline {
                points,
                filled: true,
                closed: true,
            } = &p.shape
        {
            paint.push((p, points.as_slice()));
        }
        if let Some(layer) = p.layer.as_deref()
            && let Some(group) = by_layer.get_mut(layer)
            && named_exterior_wall(p).is_some()
        {
            native.insert(p.id.as_str(), (layer, p));
            if let Shape::Line { start, end } = p.shape
                && let Some(pen) = p.stroke_width.filter(|w| w.is_finite() && *w > 0.)
                && start.finite()
                && end.finite()
                && start != end
            {
                group.ink.push(Ink {
                    source: &p.id,
                    ends: [start, end],
                    pen,
                });
            }
        }
    }
    let statuses = page
        .placement_decisions
        .iter()
        .flatten()
        .map(|d| (d.wall_id.as_str(), d))
        .collect::<HashMap<_, _>>();
    let walls = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w))
        .collect::<HashMap<_, _>>();
    let mut occupied = vec![];
    let mut referenced = HashSet::new();
    for w in &page.walls {
        let Some(d) = statuses.get(w.id.as_str()) else {
            continue;
        };
        if d.status != WallStatus::Ready {
            continue;
        }
        referenced.extend(d.opening_ids.iter().map(String::as_str));
        let f = effective_wall(w, Some(d));
        occupied.extend(wall_outline(&f));
        let [id] = w.source_ids.as_slice() else {
            continue;
        };
        if f.curve.is_some() || f.body != BodyEvidence::FilledBody || !visible.contains(id.as_str())
        {
            continue;
        }
        let Some(&(layer, p)) = native.get(id.as_str()) else {
            continue;
        };
        if !certified_rectangle(p, &f) {
            continue;
        }
        let u = (f.end - f.start).unit();
        let group = by_layer.get_mut(layer)?;
        group.stock.extend(
            [(f.start, u * -1.), (f.end, u)].map(|(cap, outward)| Stock {
                host: &w.id,
                paint: id,
                cap,
                outward,
                width: f.thickness,
            }),
        );
    }
    for ply in wall_layers::layers(page) {
        if let Some(owner) = walls.get(ply.owner_id) {
            occupied.extend(wall_outline(&ply.footprint(owner)));
        }
    }
    occupied.extend(wall_layers::contacts(page).into_iter().map(|c| c.outline));
    let furniture = page
        .objects
        .iter()
        .filter_map(|o| o.footprint.as_ref())
        .map(|p| p.polygon.as_slice())
        .collect::<Vec<_>>();
    let paint_work = paint.iter().map(|(_, points)| points.len()).sum::<usize>();
    let furniture_work = furniture.iter().map(|p| p.len()).sum::<usize>();
    referenced.extend(
        page.edges
            .iter()
            .flat_map(|e| e.opening_ids.iter())
            .map(String::as_str),
    );
    referenced.extend(page.room_connections.iter().map(|l| l.opening_id.as_str()));
    let routes = page
        .semantics
        .routing
        .iter()
        .map(|r| (r.entity_id.as_str(), r))
        .collect::<HashMap<_, _>>();
    let mut out = vec![];
    for (layer, group) in by_layer {
        charge(work, group.stock.len().saturating_pow(2))?;
        for &a in &group.stock {
            // Canonical direction also defines which host owns the first rough plane.
            if a.outward.x.abs() >= a.outward.y.abs() && a.outward.x <= 0.
                || a.outward.y.abs() > a.outward.x.abs() && a.outward.y <= 0.
            {
                continue;
            }
            'stock: for &b in &group.stock {
                let Some(f) = Frame::between(a, b) else {
                    continue;
                };
                charge(work, group.signs.len())?;
                let Some(label) = f.signs(&group) else {
                    continue;
                };
                charge(work, group.ink.len())?;
                let ink = local_ink(&f, &group.ink);
                let Some(casing) = casing(&f, &ink, work)? else {
                    continue;
                };
                let cross = casing.cross;
                charge(
                    work,
                    occupied
                        .len()
                        .saturating_add(paint_work)
                        .saturating_add(furniture_work)
                        .saturating_add(page.openings.len())
                        .saturating_add(page.window_panes.len()),
                )?;
                if occupied.iter().any(|&p| f.intersects(p, cross, 0.))
                    || furniture
                        .iter()
                        .any(|p| f.intersects(p.iter().copied(), cross, 0.))
                {
                    continue;
                }
                let mut jambs = Vec::new();
                for &(p, points) in &paint {
                    if !f.intersects(points.iter().copied(), cross, 0.) {
                        continue;
                    }
                    if named_exterior_wall(p) != Some(layer) || !jamb_finish(&f, &casing, points) {
                        continue 'stock;
                    }
                    jambs.push(p.id.as_str());
                }
                let mut pane = None;
                let overlaps = |o: &Opening| {
                    f.intersects([o.start, o.end], cross, f.eps)
                        || o.swing_observations
                            .iter()
                            .any(|s| f.intersects([s.hinge, s.closed_tip], cross, s.leaf_tolerance))
                };
                let mut conflict = page.window_panes.iter().any(|p| overlaps(&p.observation));
                for (i, o) in page
                    .openings
                    .iter()
                    .enumerate()
                    .filter(|(_, o)| overlaps(o))
                {
                    if pane.is_some() || o.kind != OpeningKind::Window || o.evidence != OpeningEvidence::FiniteWindowAssembly
                        || o.operation != "Fixed" || !o.requires_review || o.physical_jambs.is_some()
                        || !o.swing_observations.is_empty() || o.hinge.is_some() || referenced.contains(o.id.as_str())
                        || !routes.get(o.id.as_str()).is_some_and(|r| review_route(o, r))
                        || o.host_wall_ids.is_empty() || o.host_wall_ids.iter().any(|id| !statuses.get(id.as_str()).is_some_and(|d| d.status != WallStatus::Ready))
                        || ![o.start,o.end].into_iter().all(|p| f.contains(p,cross))
                        || !f.eq(f.at(o.start).y, f.at(o.end).y)
                        || !o.source_ids.iter().all(|id| native.get(id.as_str()).is_some_and(|&(l, p)| {
                            l == layer && matches!(p.shape, Shape::Line { start, end } if f.contains(start, cross) && f.contains(end, cross))
                        })) { conflict = true; break; }
                    pane = Some(i);
                }
                if conflict {
                    continue;
                }
                let mid = cross[0].midpoint(cross[1]);
                let source_ids = [a.paint, b.paint, label]
                    .into_iter()
                    .chain(casing.ids)
                    .chain(jambs)
                    .chain(
                        pane.into_iter()
                            .flat_map(|i| page.openings[i].source_ids.iter().map(String::as_str)),
                    )
                    .collect::<BTreeSet<_>>()
                    .into_iter()
                    .map(str::to_owned)
                    .collect();
                out.push(Proposal {
                    frame: Opening {
                        id: String::new(),
                        page: page.page_number,
                        kind: OpeningKind::Window,
                        operation: "Fixed".into(),
                        host_wall_id: a.host.into(),
                        host_wall_ids: vec![a.host.into(), b.host.into()],
                        start: f.world(0., mid),
                        end: f.world(f.len, mid),
                        hinge: None,
                        swing_observations: vec![],
                        source_ids,
                        evidence: EVIDENCE,
                        requires_review: true,
                        physical_jambs: None,
                    },
                    pane,
                    outline: [
                        f.world(0., cross[0]),
                        f.world(f.len, cross[0]),
                        f.world(f.len, cross[1]),
                        f.world(0., cross[1]),
                    ],
                });
            }
        }
    }
    charge(work, out.len().saturating_pow(2))?;
    let mut ambiguous = vec![false; out.len()];
    for (i, a) in out.iter().enumerate() {
        for (j, b) in out.iter().enumerate().skip(i + 1) {
            if a.pane.is_some() && a.pane == b.pane || rectangles_overlap(a.outline, b.outline) {
                ambiguous[i] = true;
                ambiguous[j] = true;
            }
        }
    }
    out = out
        .into_iter()
        .zip(ambiguous)
        .filter_map(|(p, no)| (!no).then_some(p))
        .collect();
    out.sort_by(|a, b| {
        a.frame
            .start
            .x
            .total_cmp(&b.frame.start.x)
            .then(a.frame.start.y.total_cmp(&b.frame.start.y))
    });
    Some(out)
}

fn rectangles_overlap(a: [Point; 4], b: [Point; 4]) -> bool {
    [a[1] - a[0], a[3] - a[0], b[1] - b[0], b[3] - b[0]]
        .into_iter()
        .all(|d| {
            let axis = d.unit();
            let limits = |pts: [Point; 4]| {
                pts.into_iter()
                    .map(|p| (p - a[0]).dot(axis))
                    .fold([f64::INFINITY, f64::NEG_INFINITY], |[lo, hi], n| {
                        [lo.min(n), hi.max(n)]
                    })
            };
            let ([lo, hi], [l, h]) = (limits(a), limits(b));
            hi.min(h) - lo.max(l) > 1e-8
        })
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let mut work = MAX_WORK;
    let Some(proposals) = proposals(page, &mut work) else {
        return Ok(());
    };
    if proposals.is_empty() {
        return Ok(());
    }
    let mut archived = BTreeMap::new();
    let mut recovered = Vec::with_capacity(proposals.len());
    for (i, mut p) in proposals.into_iter().enumerate() {
        p.frame.id = format!("p{}:authored-rough-window:{}", page.page_number, i + 1);
        if let Some(index) = p.pane {
            archived.insert(index, p.frame.id.clone());
        }
        recovered.push(p.frame);
    }
    let mut old_pane_ids = HashSet::new();
    for (opening_index, observation) in std::mem::take(&mut page.openings).into_iter().enumerate() {
        if let Some(frame_opening_id) = archived.remove(&opening_index) {
            old_pane_ids.insert(observation.id.clone());
            page.window_panes.push(WindowPane {
                frame_opening_id,
                opening_index,
                observation,
            });
        } else {
            page.openings.push(observation);
        }
    }
    page.semantics
        .routing
        .retain(|r| !old_pane_ids.contains(&r.entity_id));
    page.semantics.routing.extend(recovered.iter().map(route));
    page.openings.extend(recovered);
    Ok(())
}

pub(crate) fn before_recovery(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let rough = page
        .openings
        .iter()
        .filter(|o| o.evidence == EVIDENCE)
        .map(|o| o.id.as_str())
        .collect::<HashSet<_>>();
    if rough.is_empty() {
        return Ok(Cow::Borrowed(page));
    }
    let mut indexes = BTreeMap::new();
    let mut owners = HashSet::new();
    for pane in page
        .window_panes
        .iter()
        .filter(|p| rough.contains(p.frame_opening_id.as_str()))
    {
        if !owners.insert(pane.frame_opening_id.as_str())
            || indexes
                .insert(pane.opening_index, &pane.observation)
                .is_some()
            || pane.observation.kind != OpeningKind::Window
            || pane.observation.evidence != OpeningEvidence::FiniteWindowAssembly
        {
            return Err("invalid retained rough glazing pane".into());
        }
    }
    let mut replay = page.clone();
    replay
        .window_panes
        .retain(|p| !rough.contains(p.frame_opening_id.as_str()));
    replay.openings.retain(|o| o.evidence != EVIDENCE);
    replay
        .semantics
        .routing
        .retain(|r| !rough.contains(r.entity_id.as_str()));
    for (index, observation) in indexes {
        if index > replay.openings.len() {
            return Err("invalid retained rough glazing pane order".into());
        }
        replay.openings.insert(index, observation.clone());
        replay.semantics.routing.push(route(observation));
    }
    Ok(Cow::Owned(replay))
}
