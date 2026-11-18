//! A drawn pane with boxed jambs can end at a corner or at the end of a host.
//! Its three rails and two end blocks give finite limits without a host-end margin.
use super::{Line, bins, cell};
use crate::model::*;
use std::collections::{BTreeSet, HashMap};

type Index = HashMap<(i64, i64), Vec<usize>>;

struct Strokes<'lines, 'page> {
    short: &'lines [Line<'page>],
    rails: &'lines [Line<'page>],
    ends: Index,
    cap_centers: Index,
    work: usize,
}

impl<'lines, 'page> Strokes<'lines, 'page> {
    fn charge(&mut self, visits: usize) -> Result<(), String> {
        self.work = self.work.saturating_add(visits);
        if self.work > 30_000_000 {
            return Err("boxed window frame evidence budget exceeded".into());
        }
        Ok(())
    }

    fn new(rails: &'lines [Line<'page>], short: &'lines [Line<'page>], max_short: f64) -> Self {
        let mut ends = Index::new();
        let mut cap_centers = Index::new();
        let long_rails = rails
            .iter()
            .enumerate()
            .filter(|(_, line)| line.len() > max_short)
            .map(|(i, line)| (i + short.len(), line));
        for (i, line) in short.iter().enumerate().chain(long_rails) {
            // Two orientations refer to the same source segment and its existing ID.
            ends.entry(cell(line.a)).or_default().push(2 * i);
            ends.entry(cell(line.b)).or_default().push(2 * i + 1);
        }
        for (i, cap) in short.iter().enumerate() {
            cap_centers.entry(cell(cap.mid())).or_default().push(i);
        }
        Self {
            short,
            rails,
            ends,
            cap_centers,
            work: 0,
        }
    }

    fn near(&mut self, p: Point, tol: f64, centers: bool) -> Result<Vec<Line<'page>>, String> {
        let index = if centers {
            &self.cap_centers
        } else {
            &self.ends
        };
        let mut found = Vec::new();
        for key in bins(p, p, tol) {
            for &entry in index.get(&key).into_iter().flatten() {
                self.work += 1;
                if self.work > 30_000_000 {
                    return Err("boxed window frame evidence budget exceeded".into());
                }
                let line = if centers {
                    self.short[entry]
                } else {
                    let i = entry / 2;
                    let mut line = self
                        .short
                        .get(i)
                        .copied()
                        .unwrap_or_else(|| self.rails[i - self.short.len()]);
                    if entry % 2 == 1 {
                        std::mem::swap(&mut line.a, &mut line.b);
                    }
                    line
                };
                let at = if centers { line.mid() } else { line.a };
                if at.distance(p) <= tol {
                    found.push(line);
                }
            }
        }
        Ok(found)
    }

    fn joining(&mut self, a: Point, b: Point, tol: f64) -> Result<Option<&'page str>, String> {
        Ok(self
            .near(a, tol, false)?
            .into_iter()
            .filter(|l| l.b.distance(b) <= tol)
            .map(|l| l.id)
            .min())
    }

    /// Walk the other three drawn sides of the jamb block, outward from its inner cap.
    fn block(
        &mut self,
        cap: Line<'page>,
        out: Point,
        tol: f64,
    ) -> Result<Option<(Point, [&'page str; 4])>, String> {
        let mut result: Option<(Point, [&str; 4])> = None;
        for edge in self.near(cap.a, tol, false)? {
            let delta = edge.b - edge.a;
            let depth = delta.dot(out);
            if depth < 0.45 * cap.len() || depth > 2.5 * cap.len() || delta.cross(out).abs() > tol {
                continue;
            }
            let far = cap.b + delta;
            if let (Some(side), Some(end)) = (
                self.joining(cap.b, far, tol)?,
                self.joining(edge.b, far, tol)?,
            ) {
                let corner = cap.mid() + delta;
                if result.is_some_and(|(other, _)| corner.distance(other) > tol) {
                    return Ok(None);
                }
                result.get_or_insert((corner, [cap.id, edge.id, side, end]));
            }
        }
        Ok(result)
    }
}

struct Frame<'a> {
    from: Point,
    to: Point,
    ids: BTreeSet<&'a str>,
}

fn crosses_pier(pane: Line<'_>, w: &Wall, tol: f64) -> bool {
    if w.body != BodyEvidence::FilledBody || w.curve.is_some() {
        return false;
    }
    let dir = (pane.b - pane.a).unit();
    let u = (w.end - w.start).unit();
    let x = (w.start - pane.a).dot(dir);
    let y = (w.end - pane.a).dot(dir);
    u.dot(dir).abs() >= 0.995
        && (pane.mid() - w.start).cross(u).abs() < w.thickness / 2. + tol
        && x.max(y).min(pane.len()) > x.min(y).max(0.) + tol
}

impl Frame<'_> {
    fn on_host(&self, w: &Wall, a: Line<'_>, b: Line<'_>, tol: f64) -> Option<(Point, Point)> {
        let u = (w.end - w.start).unit();
        let corners = [a.a, a.b, b.a, b.b];
        if w.curve.is_some()
            || w.body == BodyEvidence::FilledBody
            || w.thickness <= a.len()
            || u.dot((self.to - self.from).unit()).abs() < 0.995
            || corners
                .iter()
                .any(|p| (*p - w.start).cross(u).abs() > w.thickness / 2. + tol)
        {
            return None;
        }
        let x = (self.from - w.start).dot(u);
        let y = (self.to - w.start).dot(u);
        if x.min(y) < -tol || x.max(y) > w.start.distance(w.end) + tol {
            return None;
        }
        Some((
            on_segment(self.from, w.start, w.end).0,
            on_segment(self.to, w.start, w.end).0,
        ))
    }
}

fn overlaps(o: &Opening, from: Point, to: Point, tol: f64) -> bool {
    let u = (to - from).unit();
    let x = (o.start - from).dot(u);
    let y = (o.end - from).dot(u);
    x.min(y).max(0.) + tol < x.max(y).min(from.distance(to))
}

fn frame<'a>(
    s: &mut Strokes<'_, 'a>,
    pane: Line<'a>,
    a: Line<'a>,
    b: Line<'a>,
    tol: f64,
) -> Result<Option<Frame<'a>>, String> {
    let dir = (pane.b - pane.a).unit();
    let thickness = a.len();
    let tol = tol.min(thickness * 0.08);
    if thickness > pane.len() / 6.
        || (thickness - b.len()).abs() > tol
        || (a.b - a.a).dot(dir).abs() > tol
        || (b.b - b.a).dot(dir).abs() > tol
        || a.mid().distance(pane.a) > tol
        || b.mid().distance(pane.b) > tol
    {
        return Ok(None);
    }
    let (far_left, far_right) = if (b.a - a.a).cross(dir).abs() <= tol {
        (b.a, b.b)
    } else {
        (b.b, b.a)
    };
    if (far_left - a.a).cross(dir).abs() > tol || (far_right - a.b).cross(dir).abs() > tol {
        return Ok(None);
    }
    // Both finite outer rails must join exactly the corners pierced by the centerline.
    let (Some(left), Some(right)) = (
        s.joining(a.a, far_left, tol)?,
        s.joining(a.b, far_right, tol)?,
    ) else {
        return Ok(None);
    };
    let (Some((from, first)), Some((to, last))) =
        (s.block(a, dir * -1., tol)?, s.block(b, dir, tol)?)
    else {
        return Ok(None);
    };
    let ids = [pane.id, left, right]
        .into_iter()
        .chain(first)
        .chain(last)
        .collect();
    Ok(Some(Frame { from, to, ids }))
}

pub(super) fn append<'a>(
    page: u32,
    walls: &[Wall],
    rails: &[Line<'a>],
    short: &[Line<'a>],
    max_short: f64,
    opts: &Options,
    windows: &mut Vec<Opening>,
) -> Result<(), String> {
    let mut strokes = Strokes::new(rails, short, max_short);
    let tol = (opts.snap_tolerance * 0.1).min(0.25);
    let established = windows.len();
    for &pane in rails {
        let first = strokes.near(pane.a, tol, true)?;
        if first.is_empty() {
            continue;
        }
        let last = strokes.near(pane.b, tol, true)?;
        strokes.charge(first.len().saturating_mul(last.len()))?;
        for &a in &first {
            for &b in &last {
                let Some(f) = frame(&mut strokes, pane, a, b, tol)? else {
                    continue;
                };
                let width = f.from.distance(f.to);
                if !(opts.min_opening_gap..=opts.max_opening_gap).contains(&width) {
                    continue;
                }
                strokes.charge(walls.len() * 2 + established)?;
                if walls.iter().any(|w| crosses_pier(pane, w, tol)) {
                    continue;
                }
                for w in walls {
                    let Some((start, end)) = f.on_host(w, a, b, tol) else {
                        continue;
                    };
                    strokes.charge(windows.len())?;
                    // A frame already assigned to a measured host keeps its attribution.
                    if windows[..established].iter().any(|o| {
                        o.host_wall_id == w.id && overlaps(o, start, end, tol)
                            || o.host_wall_id != w.id
                                && o.source_ids
                                    .iter()
                                    .filter(|s| f.ids.contains(s.as_str()))
                                    .take(2)
                                    .count()
                                    == 2
                    }) || windows[established..].iter().any(|o| {
                        o.host_wall_id == w.id
                            && ((o.start.distance(start) < 1e-5 && o.end.distance(end) < 1e-5)
                                || (o.start.distance(end) < 1e-5 && o.end.distance(start) < 1e-5))
                    }) {
                        continue;
                    }
                    windows.push(Opening {
                        physical_jambs: None,
                        id: String::new(),
                        page,
                        kind: OpeningKind::Window,
                        operation: "Fixed".into(),
                        host_wall_id: w.id.clone(),
                        host_wall_ids: vec![w.id.clone()],
                        start,
                        end,
                        hinge: None,
                        swing_observations: Vec::new(),
                        source_ids: f.ids.iter().map(|id| (*id).to_owned()).collect(),
                        evidence: OpeningEvidence::FiniteWindowAssembly,
                        requires_review: true,
                    });
                }
            }
        }
    }
    Ok(())
}
