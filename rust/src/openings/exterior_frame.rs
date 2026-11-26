//! Recover the complete frame between two authored and placed exterior cores.
//! A sash observation describes an individual pane, not the perimeter opening.
use crate::model::{
    LayerKind, Opening, OpeningEvidence, OpeningKind, PageScan, Point, Shape, Wall,
};
use crate::placement::{WallStatus, effective_wall, insulation::MaterialTile};
use crate::semantics::{RoutingItem, RoutingKind, Span};
use std::collections::{BTreeMap, BTreeSet};

const MAX_WORK: usize = 8_000_000;

struct Budget(usize);

impl Budget {
    fn take(&mut self, amount: usize) -> Result<(), String> {
        self.0 = self
            .0
            .checked_sub(amount)
            .ok_or("exterior frame proof budget exceeded")?;
        Ok(())
    }
}

fn exterior_layer(name: &str) -> bool {
    crate::layers::is_named_wall(name)
        && name
            .to_lowercase()
            .split(|c: char| !c.is_alphanumeric())
            .any(|word| matches!(word, "exterior" | "external" | "yttervegg" | "yttervegger"))
}

struct Core {
    ends: [Point; 2],
    width: f64,
}

fn core(tile: &MaterialTile) -> Option<Core> {
    let [a, b, c, d] = tile.outline;
    let repeat = tile.repeat.unit();
    let (mut ends, width) = if repeat.cross((b - a).unit()).abs() <= 1e-5 {
        ([(a + d) * 0.5, (b + c) * 0.5], a.distance(d))
    } else if repeat.cross((d - a).unit()).abs() <= 1e-5 {
        ([(a + b) * 0.5, (d + c) * 0.5], a.distance(b))
    } else {
        return None;
    };
    // The pattern direction and its visible clip may be independently rounded.
    if width <= 0. || (width - tile.across.norm()).abs() > 3e-5 * width {
        return None;
    }
    let direction = ends[1] - ends[0];
    if direction.x.abs() >= direction.y.abs() && direction.x < 0.
        || direction.y.abs() > direction.x.abs() && direction.y < 0.
    {
        ends.swap(0, 1);
    }
    (ends[0].distance(ends[1]) >= width).then_some(Core { ends, width })
}

#[derive(Clone, Copy)]
struct Line<'a> {
    pts: [Point; 2],
    id: &'a str,
}

struct Gap {
    origin: Point,
    direction: Point,
    length: f64,
    width: f64,
    eps: f64,
}

impl Gap {
    fn new(a: &Core, b: &Core) -> Option<Self> {
        let axis = (a.ends[1] - a.ends[0]).unit();
        let delta = b.ends[0] - a.ends[1];
        let across = b.ends[1] - b.ends[0];
        let eps = 0.005 * a.width;
        let length = delta.dot(axis);
        if (a.width - b.width).abs() > eps
            || axis.dot(across.unit()) < 0.99999
            || delta.cross(axis).abs() > eps
            || !(3. * a.width..=24. * a.width).contains(&length)
        {
            return None;
        }
        Some(Self {
            origin: a.ends[1],
            direction: axis,
            length,
            width: a.width,
            eps,
        })
    }

    fn local(&self, p: Point) -> Point {
        let p = p - self.origin;
        Point::new(p.dot(self.direction), self.direction.cross(p))
    }

    fn leaves_solid(&self, a: Point, b: Point, wall: &Wall) -> bool {
        // Exact finite rectangles, including their width. Endpoint-only
        // contact is allowed, while any positive wall material in the
        // aperture excludes this proof.
        let normal = |v: Point| Point::new(-v.y, v.x);
        let along = (b - a) * 0.5;
        let across = normal(self.direction) * (self.width * 0.5);
        let w_along = (wall.end - wall.start) * 0.5;
        let w_across = normal(w_along.unit()) * (wall.thickness * 0.5);
        let delta = (wall.start - a) + w_along - along;
        let eps = 128.
            * f64::EPSILON
            * [
                a.x,
                a.y,
                b.x,
                b.y,
                wall.start.x,
                wall.start.y,
                wall.end.x,
                wall.end.y,
            ]
            .into_iter()
            .map(f64::abs)
            .fold(1., f64::max);
        [along, across, w_along, w_across].into_iter().any(|v| {
            let axis = v.unit();
            delta.dot(axis).abs()
                >= [along, across, w_along, w_across]
                    .into_iter()
                    .map(|r| r.dot(axis).abs())
                    .sum::<f64>()
                    - eps
        })
    }
}

#[derive(Clone, Copy)]
struct Cap<'a> {
    t: f64,
    sides: [f64; 2],
    id: &'a str,
}

#[derive(Clone, Copy)]
struct Rail<'a> {
    on: f64,
    ends: [f64; 2],
    id: &'a str,
}

fn covers<'a>(
    rails: &[Rail<'a>],
    lane: f64,
    [start, end]: [f64; 2],
    eps: f64,
    budget: &mut Budget,
) -> Result<Option<Vec<&'a str>>, String> {
    budget.take(rails.len())?;
    let mut ids = vec![];
    let mut covered = start;
    for rail in rails {
        if rail.ends[0] > covered + eps {
            break;
        }
        if (rail.on - lane).abs() <= eps && rail.ends[1] > covered + eps {
            covered = rail.ends[1];
            ids.push(rail.id);
        }
        if covered >= end - eps {
            return Ok(Some(ids));
        }
    }
    Ok(None)
}

fn frame<'a>(
    g: &Gap,
    ink: &[Line<'a>],
    budget: &mut Budget,
) -> Result<Option<Vec<&'a str>>, String> {
    budget.take(ink.len())?;
    let mut caps = vec![];
    let mut rails = vec![];
    for line in ink {
        let [a, b] = line.pts.map(|p| g.local(p));
        let [left, right] = [a.x.min(b.x), a.x.max(b.x)];
        if right < -g.width || left > g.length + g.width {
            continue;
        }
        let [low, high] = [a.y.min(b.y), a.y.max(b.y)];
        if (a.x - b.x).abs() <= g.eps
            && (0.85 * g.width..=1.1 * g.width).contains(&(high - low))
            && (low + high).abs() <= 0.2 * g.width
        {
            caps.push(Cap {
                t: (left + right) * 0.5,
                sides: [low, high],
                id: line.id,
            });
        }
        if (a.y - b.y).abs() <= g.eps && right - left > g.eps && (low + high).abs() <= 1.2 * g.width
        {
            rails.push(Rail {
                on: (low + high) * 0.5,
                ends: [left, right],
                id: line.id,
            });
        }
    }
    rails.sort_by(|a, b| {
        a.ends[0]
            .total_cmp(&b.ends[0])
            .then(b.ends[1].total_cmp(&a.ends[1]))
            .then(a.id.cmp(b.id))
    });
    caps.sort_by(|a, b| a.t.total_cmp(&b.t).then(a.id.cmp(b.id)));
    caps.dedup_by(|a, b| {
        (a.t - b.t).abs() <= g.eps
            && a.sides
                .iter()
                .zip(b.sides)
                .all(|(x, y)| (x - y).abs() <= g.eps)
    });
    let left: Vec<_> = caps
        .iter()
        .filter(|c| (-0.35 * g.width..=g.eps).contains(&c.t))
        .collect();
    let right: Vec<_> = caps
        .iter()
        .filter(|c| (g.length - g.eps..=g.length + 0.35 * g.width).contains(&c.t))
        .collect();

    for a in &left {
        for b in &right {
            budget.take(1)?;
            if a.sides
                .iter()
                .zip(b.sides)
                .any(|(x, y)| (x - y).abs() > g.eps)
            {
                continue;
            }
            let Some(lo) = covers(&rails, a.sides[0], [a.t, b.t], g.eps, budget)? else {
                continue;
            };
            let Some(hi) = covers(&rails, a.sides[1], [a.t, b.t], g.eps, budget)? else {
                continue;
            };
            let center = (a.sides[0] + a.sides[1]) * 0.5;
            // Segmented rails alone can outline a wall cavity. An independently
            // authored center pane with its own two full transverse caps
            // confirms window geometry inside this finite casing.
            for r in &rails {
                budget.take(caps.len().saturating_mul(2))?;
                let [x, y] = r.ends;
                if (r.on - center).abs() > g.eps
                    || x <= a.t + g.eps
                    || y >= b.t - g.eps
                    || y - x < 2. * g.width
                {
                    continue;
                }
                let inner = |t: f64| {
                    caps.iter().find(|cap| {
                        (cap.t - t).abs() <= g.eps
                            && cap
                                .sides
                                .iter()
                                .zip(a.sides)
                                .all(|(u, v)| (u - v).abs() <= g.eps)
                    })
                };
                let (Some(first), Some(last)) = (inner(x), inner(y)) else {
                    continue;
                };
                let ids = [a.id, b.id, r.id, first.id, last.id]
                    .into_iter()
                    .chain(lo.iter().copied())
                    .chain(hi.iter().copied())
                    .collect::<BTreeSet<_>>();
                if ids.len() >= 7 {
                    return Ok(Some(ids.into_iter().collect()));
                }
            }
        }
    }
    Ok(None)
}

fn attached_host<'a>(walls: &[&'a Wall], p: Point, away: Point, eps: f64) -> Option<&'a Wall> {
    let mut matches = walls.iter().copied().filter(|w| {
        [(w.start, w.end), (w.end, w.start)]
            .into_iter()
            .any(|(a, b)| {
                let dir = (b - a).unit();
                let endpoint_roundoff = 128.
                    * f64::EPSILON
                    * p.x
                        .abs()
                        .max(p.y.abs())
                        .max(a.x.abs())
                        .max(a.y.abs())
                        .max(1.);
                dir.dot(away) >= 0.99999
                    && (p - a).dot(dir).abs() <= endpoint_roundoff
                    && (p - a).cross(dir).abs() <= w.thickness * 0.5 - eps
            })
    });
    let host = matches.next()?;
    matches.next().is_none().then_some(host)
}

fn proposals(page: &PageScan) -> Result<Vec<Opening>, String> {
    let mut cores: BTreeMap<&str, Vec<Core>> = BTreeMap::new();
    for tile in &page.material_tiles {
        if let Some(layer) = tile.layer.as_deref().filter(|s| exterior_layer(s))
            && let Some(core) = core(tile)
        {
            cores.entry(layer).or_default().push(core);
        }
    }
    if cores.is_empty() {
        return Ok(vec![]);
    }
    let mut ink: BTreeMap<&str, Vec<Line<'_>>> = BTreeMap::new();
    for p in &page.primitives {
        if let Some(layer) = p.layer.as_deref().filter(|s| cores.contains_key(s))
            && (p.layer_category_override.is_none() || p.layer_kind() == LayerKind::Wall)
            && let Shape::Line { start, end } = p.shape
        {
            ink.entry(layer).or_default().push(Line {
                pts: [start, end],
                id: &p.id,
            });
        }
    }
    let ready: Vec<_> = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| effective_wall(w, Some(d)))
        .filter(|w| w.curve.is_none() && w.thickness > 0.)
        .collect();
    let walls: Vec<_> = ready.iter().map(|w| w.as_ref()).collect();
    let mut result = vec![];
    let mut budget = Budget(MAX_WORK);
    for (layer, cores) in cores {
        let Some(lines) = ink.get(layer) else {
            continue;
        };
        budget.take(cores.len().saturating_mul(cores.len()))?;
        for a in &cores {
            for b in &cores {
                let Some(g) = Gap::new(a, b) else { continue };
                let [start, end] = [g.origin, b.ends[0]];
                budget.take(walls.len().saturating_mul(3) + page.openings.len())?;
                let (Some(left), Some(right)) = (
                    attached_host(&walls, start, g.direction * -1., g.eps),
                    attached_host(&walls, end, g.direction, g.eps),
                ) else {
                    continue;
                };
                if left.id == right.id
                    || walls.iter().any(|w| !g.leaves_solid(start, end, w))
                    || page
                        .openings
                        .iter()
                        .filter(|o| o.kind == OpeningKind::Door)
                        .any(|o| {
                            let [a, b] = [o.start, o.end].map(|p| g.local(p));
                            a.y.abs().min(b.y.abs()) <= g.width * 0.5
                                && a.x.max(b.x).min(g.length) > a.x.min(b.x).max(0.) + g.eps
                        })
                {
                    continue;
                }
                let Some(source_ids) = frame(&g, lines, &mut budget)? else {
                    continue;
                };
                result.push(Opening {
                    id: String::new(),
                    page: page.page_number,
                    kind: OpeningKind::Window,
                    operation: "Unknown".into(),
                    host_wall_id: left.id.clone(),
                    host_wall_ids: vec![left.id.clone(), right.id.clone()],
                    start,
                    end,
                    hinge: None,
                    swing_observations: vec![],
                    source_ids: source_ids.into_iter().map(str::to_owned).collect(),
                    evidence: OpeningEvidence::ExteriorFrameGap,
                    requires_review: true,
                    physical_jambs: None,
                });
            }
        }
    }
    result.sort_by(|a, b| {
        a.start
            .x
            .total_cmp(&b.start.x)
            .then(a.start.y.total_cmp(&b.start.y))
            .then(a.end.x.total_cmp(&b.end.x))
            .then(a.end.y.total_cmp(&b.end.y))
            .then(a.host_wall_ids.cmp(&b.host_wall_ids))
            .then(a.source_ids.cmp(&b.source_ids))
    });
    result.dedup_by(|a, b| {
        a.start == b.start && a.end == b.end && a.host_wall_ids == b.host_wall_ids
    });
    let count = page
        .openings
        .iter()
        .filter(|o| o.evidence != OpeningEvidence::ExteriorFrameGap)
        .count();
    for (index, window) in result.iter_mut().enumerate() {
        window.id = format!("p{}:opening:{}", page.page_number, count + index + 1);
    }
    Ok(result)
}

pub(crate) fn append(page: &mut PageScan) -> Result<(), String> {
    let recovered = proposals(page).unwrap_or_default();
    for o in &recovered {
        page.semantics.routing.push(RoutingItem {
            kind: RoutingKind::WindowCandidate,
            entity_id: o.id.clone(),
            source_ids: o.source_ids.clone(),
            span: Some(Span {
                start: o.start,
                end: o.end,
            }),
            curve: None,
            room_ids: vec![],
            usable_for_routing: false,
            requires_review: true,
        });
    }
    page.openings.extend(recovered);
    Ok(())
}

pub(crate) fn validate(page: &PageScan, authored: bool) -> Result<(), String> {
    let expected = if authored {
        proposals(page).unwrap_or_default()
    } else {
        vec![]
    };
    let saved: Vec<_> = page
        .openings
        .iter()
        .filter(|o| o.evidence == OpeningEvidence::ExteriorFrameGap)
        .collect();
    if saved.len() != expected.len()
        || !saved.into_iter().zip(&expected).all(|(a, b)| {
            a.id == b.id
                && a.page == b.page
                && a.kind == b.kind
                && a.operation == b.operation
                && a.host_wall_id == b.host_wall_id
                && a.host_wall_ids == b.host_wall_ids
                && a.start == b.start
                && a.end == b.end
                && a.hinge.is_none()
                && a.swing_observations.is_empty()
                && a.source_ids == b.source_ids
                && a.requires_review
                && a.physical_jambs.is_none()
        })
    {
        return Err(format!(
            "exterior window frame evidence did not replay for page {}",
            page.page_number
        ));
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_exterior_frame_needs_complete_rails_and_an_independent_bounded_pane() {
        let line = |id, [x, y]: [f64; 2], [u, v]: [f64; 2]| Line {
            pts: [Point::new(x, y), Point::new(u, v)],
            id,
        };
        let gap = Gap {
            origin: Point::default(),
            direction: Point::new(1., 0.),
            length: 12.,
            width: 2.,
            eps: 0.01,
        };
        let ink = [
            line("outer-a", [-0.3, -1.], [-0.3, 1.]),
            line("outer-b", [12., -1.], [12., 1.]),
            line("side-a1", [-0.3, -1.], [2., -1.]),
            line("side-a2", [2., -1.], [10., -1.]),
            line("side-a3", [10., -1.], [12., -1.]),
            line("side-b1", [-0.3, 1.], [2., 1.]),
            line("side-b2", [2., 1.], [10., 1.]),
            line("side-b3", [10., 1.], [12., 1.]),
            line("pane-a", [2., -1.], [2., 1.]),
            line("pane-b", [10., -1.], [10., 1.]),
            line("pane-center", [2., 0.], [10., 0.]),
        ];
        let proof = |ink| frame(&gap, ink, &mut Budget(MAX_WORK)).unwrap();
        assert_eq!(proof(&ink).unwrap().len(), ink.len());
        assert!(proof(&ink[..ink.len() - 1]).is_none());
        let broken: Vec<_> = ink.into_iter().filter(|i| i.id != "side-a2").collect();
        assert!(proof(&broken).is_none());
    }
}
