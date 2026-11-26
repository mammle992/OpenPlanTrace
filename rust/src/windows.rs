//! A window assembly requires two finite jambs and two distinct frame rails.
//! An edge of a typed filled body cannot double as a window frame.
use crate::model::*;
use std::collections::{BTreeSet, HashMap, HashSet};
mod authored_frame;
mod composite;
mod glazed_panel;
mod physical;
pub(crate) mod rough_glazing;
mod units;
pub(crate) use physical::{retained_window_observations, separate_panes, validate_window_units};
#[derive(Clone, Copy)]
struct Line<'a> {
    a: Point,
    b: Point,
    id: &'a str,
}
impl Line<'_> {
    fn mid(self) -> Point {
        (self.a + self.b) * 0.5
    }
    fn len(self) -> f64 {
        self.a.distance(self.b)
    }
}
const DIST: f64 = 1.5;
const CELL: f64 = 8.;

fn drafted_fixture(p: &Primitive) -> bool {
    p.layer_category_override.is_none()
        && p.layer.as_deref().is_some_and(|layer| {
            layer
                .to_lowercase()
                .split(|c: char| !c.is_alphanumeric())
                .any(|word| {
                    matches!(
                        word,
                        "inventar" | "innredning" | "kjøkkeninnredning" | "sanitærinstallasjoner"
                    )
                })
        })
}
fn cell(p: Point) -> (i64, i64) {
    ((p.x / CELL).floor() as i64, (p.y / CELL).floor() as i64)
}
fn bins(a: Point, b: Point, pad: f64) -> impl Iterator<Item = (i64, i64)> {
    let (lo, bot) = cell(Point::new(a.x.min(b.x) - pad, a.y.min(b.y) - pad));
    let (hi, top) = cell(Point::new(a.x.max(b.x) + pad, a.y.max(b.y) + pad));
    (lo..=hi).flat_map(move |x| (bot..=top).map(move |y| (x, y)))
}
pub(crate) fn assemblies(
    page: &Page,
    walls: &[Wall],
    doors: &[Opening],
    o: &Options,
) -> Result<Vec<Opening>, String> {
    let owned: HashSet<_> = doors
        .iter()
        .flat_map(|d| d.source_ids.iter().map(String::as_str))
        .collect();
    let leaves: HashSet<_> = doors
        .iter()
        .flat_map(|d| &d.swing_observations)
        .flat_map(|s| {
            s.open_leaf_source_ids
                .iter()
                .chain(&s.closed_leaf_source_ids)
                .chain(std::iter::once(&s.arc_source_id))
        })
        .map(String::as_str)
        .collect();
    let filled: HashSet<_> = walls
        .iter()
        .filter(|w| w.body == BodyEvidence::FilledBody)
        .flat_map(|w| w.source_ids.iter().map(String::as_str))
        .collect();
    let mut body_ends: HashMap<_, Vec<_>> = HashMap::new();
    for p in &page.primitives {
        if !filled.contains(p.id.as_str()) {
            continue;
        }
        for (a, b) in segments(&p.shape) {
            for point in [a, b] {
                body_ends.entry(cell(point)).or_default().push((a, b));
            }
        }
    }
    let mut lines = Vec::new();
    let mut ends: HashMap<_, Vec<usize>> = HashMap::new();
    let mut ticks = Vec::new();
    let mut work = 0usize;
    let max_short = o.max_wall_pair_separation * 4. + o.snap_tolerance * 2.;
    for p in &page.primitives {
        if !matches!(
            p.layer_kind(),
            LayerKind::Unknown | LayerKind::Wall | LayerKind::Window
        ) || leaves.contains(p.id.as_str())
            || drafted_fixture(p)
        {
            continue;
        }
        if matches!(p.shape, Shape::Polyline { filled: true, .. }) {
            continue;
        }
        for (a, b) in segments(&p.shape) {
            let line = Line { a, b, id: &p.id };
            let l = line.len();
            if l < 1e-6 {
                continue;
            }
            if l <= max_short {
                ticks.push(line)
            }
            // A built frame can give the neighboring pane the same jamb.
            // Door-owned stroke segments cannot supply that pane's rails.
            if owned.contains(p.id.as_str()) {
                continue;
            }
            if l < o.min_opening_gap * 0.72 || l > o.max_opening_gap * 1.35 {
                continue;
            }
            let mut solid = false;
            for key in bins(a, a, DIST) {
                if let Some(faces) = body_ends.get(&key) {
                    for &(c, d) in faces {
                        work += 1;
                        if work > 50_000_000 {
                            return Err("window evidence budget exceeded".into());
                        }
                        if (a.distance(c) <= DIST && b.distance(d) <= DIST)
                            || (a.distance(d) <= DIST && b.distance(c) <= DIST)
                        {
                            solid = true;
                            break;
                        }
                    }
                }
                if solid {
                    break;
                }
            }
            if solid {
                continue;
            }
            let i = lines.len();
            lines.push(line);
            for pt in [a, b] {
                ends.entry(cell(pt)).or_default().push(i);
            }
        }
    }
    drop(body_ends);
    let mut out = Vec::new();
    ticks.sort_by(|a, b| a.mid().x.total_cmp(&b.mid().x));
    for w in walls
        .iter()
        .filter(|w| w.curve.is_none() && w.body != BodyEvidence::FilledBody)
    {
        let u = (w.end - w.start).unit();
        let length = w.start.distance(w.end);
        let maxd = (w.thickness / 2. + o.snap_tolerance + 1.).max(4.);
        let max_tick = (w.thickness * 4. + o.snap_tolerance * 2.).max(o.min_opening_gap);
        let start = ticks.partition_point(|t| t.mid().x < w.start.x.min(w.end.x) - maxd);
        let mut js = Vec::new();
        for &t in &ticks[start..] {
            let m = t.mid();
            if m.x > w.start.x.max(w.end.x) + maxd {
                break;
            }
            work += 1;
            if work > 50_000_000 {
                return Err("window evidence budget exceeded".into());
            }
            if t.len() > max_tick
                || ((t.b - t.a).dot(u)).abs() > DIST
                || on_segment(m, w.start, w.end).0.distance(m) > maxd
            {
                continue;
            }
            let along = (m - w.start).dot(u);
            if along >= 0. && along <= length {
                js.push((along, t))
            }
        }
        js.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.id.cmp(b.1.id)));
        for pair in js.windows(2) {
            let (a, j1) = pair[0];
            let (b, j2) = pair[1];
            let width = b - a;
            let guard = (o.snap_tolerance * 1.5).max(w.thickness * 1.25);
            if j1.id == j2.id
                || width < o.min_opening_gap
                || width > o.max_opening_gap
                || a <= guard
                || length - b <= guard
                || j1.len().min(j2.len()) < 0.55 * j1.len().max(j2.len())
            {
                continue;
            }
            let dist = |(p, t): (Point, Line)| on_segment(p, t.a, t.b).0.distance(p);
            if ((j1.mid() - w.start).cross(u).abs() - (j2.mid() - w.start).cross(u).abs()).abs()
                > o.snap_tolerance.max(2.)
            {
                continue;
            }
            let mut ix = BTreeSet::new();
            for key in bins(j1.a, j1.b, DIST) {
                if let Some(ids) = ends.get(&key) {
                    ix.extend(ids.iter().copied())
                }
            }
            let mut fr = Vec::new();
            for i in ix {
                work += 1;
                if work > 50_000_000 {
                    return Err("window evidence budget exceeded".into());
                }
                let l = lines[i];
                if l.id == j1.id
                    || l.id == j2.id
                    || w.source_ids.iter().any(|id| id == l.id)
                    || l.len() < width * 0.72
                    || l.len() > width * 1.35
                    || (l.b - l.a).cross(u).abs() > DIST
                {
                    continue;
                }
                if !((dist((l.a, j1)) <= DIST && dist((l.b, j2)) <= DIST)
                    || (dist((l.b, j1)) <= DIST && dist((l.a, j2)) <= DIST))
                {
                    continue;
                }
                let x = (l.a - w.start).dot(u);
                let y = (l.b - w.start).dot(u);
                if x.max(y).min(b) - x.min(y).max(a) < 0.80 * width {
                    continue;
                }
                let offset = (l.mid() - w.start).cross(u);
                if offset.abs() > (3. * o.snap_tolerance).max(w.thickness * 3.) {
                    continue;
                }
                fr.push((offset, l.id))
            }
            fr.sort_by(|a, b| a.0.total_cmp(&b.0).then(a.1.cmp(b.1)));
            let mut offsets = Vec::new();
            let mut src = BTreeSet::from([j1.id, j2.id]);
            for (v, id) in fr {
                if offsets
                    .iter()
                    .all(|old: &f64| (v - *old).abs() > 0.75 * DIST)
                    && !src.contains(id)
                {
                    offsets.push(v);
                    src.insert(id);
                }
            }
            if offsets.len() >= 2 {
                out.push(Opening {
                    physical_jambs: None,
                    id: String::new(),
                    page: page.number,
                    kind: OpeningKind::Window,
                    operation: "Fixed".into(),
                    host_wall_id: w.id.clone(),
                    host_wall_ids: vec![w.id.clone()],
                    start: w.start + u * a,
                    end: w.start + u * b,
                    hinge: None,
                    swing_observations: Vec::new(),
                    source_ids: src.into_iter().map(str::to_owned).collect(),
                    evidence: OpeningEvidence::FiniteWindowAssembly,
                    requires_review: true,
                });
            }
        }
    }
    drop(ends);
    glazed_panel::append(page.number, walls, &lines, &ticks, max_short, o, &mut out)?;
    Ok(out)
}
