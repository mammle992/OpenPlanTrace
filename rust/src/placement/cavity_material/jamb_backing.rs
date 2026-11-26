//! Restore missing core material at an already certified exterior door jamb.
use super::{Band, Frame, Paint, Stock, exact_stock, named_visible_paint, protected_bodies};
use crate::{
    input::raster::PdfRoute,
    model::{Opening, OpeningEvidence, OpeningKind, PageScan, Point, Primitive, Shape, Wall},
    placement::{
        WallDecision, WallReason, WallStatus,
        connections::{
            ConnectionEdge, ConnectionKind, STEPPED_APERTURE_JAMB, STEPPED_APERTURE_THRESHOLD,
        },
        exterior_piers,
    },
};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashMap, HashSet};

const MAX_WORK: usize = 3_000_000;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct Backing {
    pub owner_wall_id: String,
    pub opening_id: String,
    pub core_source_id: String,
    pub core: [Point; 4],
    pub separator_source_id: String,
    pub separator: [Point; 4],
    pub source_ids: Vec<String>,
}

fn unique<T>(mut choices: impl Iterator<Item = T>) -> Option<T> {
    let first = choices.next()?;
    choices.next().is_none().then_some(first)
}

fn point(f: Frame, along: f64, across: f64) -> Point {
    f.at + f.axis * along + Point::new(-f.axis.y, f.axis.x) * across
}

fn same_points(a: &[Point], b: &[Point], eps: f64) -> bool {
    let contained = |from: &[Point], within: &[Point]| {
        from.iter()
            .all(|p| within.iter().any(|q| p.distance(*q) <= eps))
    };
    a.len() == b.len() && contained(a, b) && contained(b, a)
}

fn points(p: &Primitive) -> Option<&[Point]> {
    let Shape::Polyline {
        points,
        closed: true,
        filled: true,
    } = &p.shape
    else {
        return None;
    };
    let (last, preceding) = points.split_last()?;
    Some(if preceding.first() == Some(last) {
        preceding
    } else {
        points
    })
}

struct Evidence<'a> {
    native: &'a [Primitive],
    visible_polygons: HashSet<&'a str>,
    by_id: HashMap<&'a str, &'a Primitive>,
    ready: HashMap<&'a str, (&'a Wall, &'a WallDecision)>,
    visible: HashMap<&'a str, Paint<'a>>,
    jambs: HashMap<&'a str, &'a Opening>,
    thresholds: HashMap<&'a str, Vec<&'a ConnectionEdge>>,
}

impl<'a> Evidence<'a> {
    fn cap(
        &self,
        f: Frame,
        plane: f64,
        width: f64,
        range: [f64; 2],
        layer: &str,
        edge_sources: &[String],
    ) -> Option<Vec<&'a str>> {
        let mut intervals = self
            .native
            .iter()
            .filter_map(|p| {
                if p.layer.as_deref() != Some(layer)
                    || !p
                        .stroke_width
                        .is_some_and(|pen| pen.is_finite() && pen > 0. && pen <= width * 0.15)
                {
                    return None;
                }
                let Shape::Line { start, end } = p.shape else {
                    return None;
                };
                let [a, b] = [start, end].map(|v| f.local(v));
                let lo = a.y.min(b.y).max(range[0]);
                let hi = a.y.max(b.y).min(range[1]);
                ((a.x - plane).abs().max((b.x - plane).abs()) <= f.eps && hi > lo + f.eps)
                    .then_some((lo, hi, p.id.as_str()))
            })
            .collect::<Vec<_>>();
        if !intervals
            .iter()
            .any(|&(_, _, id)| edge_sources.iter().any(|s| s == id))
        {
            return None;
        }
        intervals.sort_by(|a, b| {
            a.0.total_cmp(&b.0)
                .then(b.1.total_cmp(&a.1))
                .then(a.2.cmp(b.2))
        });
        let mut end = range[0];
        let mut used = Vec::new();
        for (lo, hi, id) in intervals {
            if lo > end + f.eps {
                break;
            }
            if hi > end + f.eps {
                used.push(id);
                end = hi;
            }
        }
        (end >= range[1] - f.eps).then_some(used)
    }

    fn corner(
        &self,
        f: Frame,
        stock: Band<'_>,
        jamb_side: usize,
        layer: &str,
    ) -> Option<Vec<&'a str>> {
        let [lo, hi] = stock.across;
        let width = hi - lo;
        let [near, far] = if lo > 0. { [lo, hi] } else { [hi, lo] };
        let terminal = stock.along[1 - jamb_side];
        let outside = terminal + if jamb_side == 0 { width } else { -width };
        let [a, b, c, d] = [
            point(f, terminal, near),
            point(f, terminal, far),
            point(f, outside, far),
            point(f, outside, near),
        ];
        let paint_triangle = |vertices: [Point; 3]| {
            unique(self.native.iter().filter_map(|p| {
                if p.layer.as_deref() != Some(layer)
                    || !self.visible_polygons.contains(p.id.as_str())
                {
                    return None;
                }
                let shape = points(p)?;
                same_points(shape, &vertices, f.eps).then_some(p.id.as_str())
            }))
        };
        let core = paint_triangle([a, b, d])?;
        let pier = paint_triangle([b, c, d])?;
        let pier_source = unique(self.ready.values().filter_map(|&(w, decision)| {
            if w.curve.is_some()
                || (w.thickness - width).abs() > f.eps
                || w.body != crate::model::BodyEvidence::FilledBody
                || decision.placement_extent.is_some()
                || !decision.support_source_ids.iter().any(|id| id == pier)
            {
                return None;
            }
            let [source] = w.source_ids.as_slice() else {
                return None;
            };
            if !self.visible_polygons.contains(source.as_str())
                || (w.end - w.start).dot(f.axis).abs() > f.eps
                || self.by_id.get(source.as_str())?.layer.as_deref() != Some(layer)
            {
                return None;
            }
            let q = exterior_piers::corners(w.start, w.end, w.thickness);
            ([[q[0], q[3]], [q[1], q[2]]]
                .into_iter()
                .any(|cap| same_points(&cap, &[b, c], f.eps)))
            .then_some(source.as_str())
        }))?;
        Some(vec![core, pier, pier_source])
    }

    fn opposite(&self, proof: &ConnectionEdge, owner: &str, door: &str) -> Option<Stock<'a>> {
        let (id, edge) = unique(self.thresholds.get(door)?.iter().filter_map(|&e| {
            let [a, b] = e.wall_ids.as_slice() else {
                return None;
            };
            if a == owner {
                Some((b.as_str(), e))
            } else if b == owner {
                Some((a.as_str(), e))
            } else {
                None
            }
        }))?;
        let &(w, d) = self.ready.get(id)?;
        let stock = exact_stock(w, d, &self.visible)?;
        (edge.source_ids.iter().any(|s| s == stock.paint.id)
            && proof.opening_ids == edge.opening_ids)
            .then_some(stock)
    }

    fn backing(&self, edge: &ConnectionEdge) -> Option<Backing> {
        let [wall_id] = edge.wall_ids.as_slice() else {
            return None;
        };
        let [door_id] = edge.opening_ids.as_slice() else {
            return None;
        };
        let &(w, d) = self.ready.get(wall_id.as_str())?;
        let door = self.jambs.get(door_id.as_str())?;
        let owner = exact_stock(w, d, &self.visible)?;
        if d.reason != WallReason::SourceBoundedStaggeredPaintedCore
            || !edge.source_ids.iter().any(|s| s == owner.paint.id)
        {
            return None;
        }
        let layer = owner.paint.layer;
        let f = Frame::from(w)?;
        let opposite = self.opposite(edge, wall_id, door_id)?;
        if opposite.paint.layer != layer {
            return None;
        }
        let source_bands = edge
            .source_ids
            .iter()
            .filter_map(|s| {
                let p = *self.visible.get(s.as_str())?;
                (p.id != owner.paint.id && p.layer == layer).then_some((p, f.band(p)?))
            })
            .collect::<Vec<_>>();
        let [(u, a), (v, b)] = source_bands.as_slice() else {
            return None;
        };
        let ((paint, core), (bridge, seam)) =
            if a.across[1] - a.across[0] > b.across[1] - b.across[0] {
                ((*u, *a), (*v, *b))
            } else {
                ((*v, *b), (*u, *a))
            };
        let width = core.across[1] - core.across[0];
        let gap = seam.across[1] - seam.across[0];
        let half = w.thickness * 0.5;
        let [lo, hi] = core.across;
        let separator = if lo > 0. { [half, lo] } else { [hi, -half] };
        if width < w.thickness * 2.
            || width > w.thickness * 8.
            || core.along[1] - core.along[0] < 3. * width
            || gap > w.thickness * 0.10
            || !f.same(seam.across, separator)
            || !d.support_source_ids.iter().any(|id| id == bridge.id)
            || core.along[0] < -f.eps
            || core.along[1] > f.length + f.eps
            || seam.along[0] > core.along[0] + f.eps
            || seam.along[1] < core.along[1] - f.eps
        {
            return None;
        }
        let far = f.band(opposite.paint)?;
        if !f.same(far.across, core.across) {
            return None;
        }
        let [a, b] = [door.start, door.end].map(|p| f.local(p));
        if (a.y - b.y).abs() > f.eps || a.y < lo - f.eps || a.y > hi + f.eps {
            return None;
        }
        let aperture = [a.x.min(b.x), a.x.max(b.x)];
        let side = unique((0..2).filter(|&i| {
            let t = core.along[i];
            let owner_end = [0., f.length][i];
            (t - owner_end).abs() <= f.eps
                && (t - seam.along[i]).abs() <= f.eps
                && (t - aperture[1 - i]).abs() <= f.eps
                && (far.along[1 - i] - aperture[i]).abs() <= f.eps
        }))?;
        let mut sources = BTreeSet::from([owner.paint.id, paint.id, bridge.id, opposite.paint.id]);
        sources.extend(self.cap(
            f,
            core.along[side],
            width,
            core.across,
            layer,
            &edge.source_ids,
        )?);
        sources.extend(self.corner(f, core, side, layer)?);
        Some(Backing {
            owner_wall_id: w.id.clone(),
            opening_id: door.id.clone(),
            core_source_id: paint.id.into(),
            core: paint.outline,
            separator_source_id: bridge.id.into(),
            separator: f.outline(core.along, seam.across),
            source_ids: sources.into_iter().map(str::to_owned).collect(),
        })
    }
}

pub(crate) fn derive(page: &PageScan) -> Vec<Backing> {
    let Some(pass) = page.exterior_connections.as_ref().filter(|_| {
        page.pdf_input
            .as_ref()
            .is_some_and(|p| matches!(p.route, PdfRoute::Vector | PdfRoute::Mixed))
    }) else {
        return vec![];
    };
    let native = &page.primitives;
    let mut thresholds: HashMap<_, Vec<_>> = HashMap::new();
    let mut contacts = Vec::new();
    for edge in &pass.edges {
        match (
            edge.kind,
            edge.evidence.as_str(),
            edge.opening_ids.as_slice(),
        ) {
            (ConnectionKind::DoorThreshold, STEPPED_APERTURE_THRESHOLD, [id]) => {
                thresholds.entry(id.as_str()).or_default().push(edge);
            }
            (ConnectionKind::OpeningJambContact, STEPPED_APERTURE_JAMB, [_]) => contacts.push(edge),
            _ => {}
        }
    }
    if contacts.is_empty() || contacts.len().saturating_mul(native.len()) > MAX_WORK {
        return vec![];
    }
    let ready = page
        .walls
        .iter()
        .zip(page.placement_decisions.iter().flatten())
        .filter(|(_, d)| d.status == WallStatus::Ready)
        .map(|(w, d)| (w.id.as_str(), (w, d)))
        .collect();
    let masked = page
        .placement_contact_paint
        .iter()
        .filter(|p| !p.occluder_source_ids.is_empty())
        .map(|p| p.source_id.as_str())
        .collect::<HashSet<_>>();
    let evidence = Evidence {
        native,
        by_id: native.iter().map(|p| (p.id.as_str(), p)).collect(),
        ready,
        visible_polygons: page
            .placement_polygon_paint_ids
            .iter()
            .flatten()
            .map(String::as_str)
            .filter(|id| !masked.contains(id))
            .collect(),
        visible: named_visible_paint(page)
            .into_iter()
            .filter(|p| crate::layers::is_named_exterior_wall(p.layer))
            .map(|p| (p.id, p))
            .collect(),
        jambs: page
            .openings
            .iter()
            .filter(|o| {
                o.kind == OpeningKind::Door
                    && o.evidence == OpeningEvidence::AuthoredExteriorSwingCasing
                    && !o.swing_observations.is_empty()
            })
            .map(|o| (o.id.as_str(), o))
            .collect(),
        thresholds,
    };
    let mut candidates = contacts
        .into_iter()
        .filter_map(|e| evidence.backing(e))
        .collect::<Vec<_>>();
    if candidates.is_empty() {
        return candidates;
    }
    let mut occupied = protected_bodies(page);
    occupied.extend(
        super::exterior_lining::linings(page)
            .into_iter()
            .flat_map(|l| l.materials.map(|(_, p)| p)),
    );
    for a in super::coextensive::assemblies(page) {
        occupied.extend([a.leaf, a.center]);
        occupied.extend(a.cap_cells.into_iter().map(|(_, p)| p));
    }
    let eps = 256. * f64::EPSILON * page.width.max(page.height).max(1.);
    let conflicts = |a: &Backing, b: &Backing| {
        a.owner_wall_id == b.owner_wall_id
            || [a.core, a.separator].into_iter().any(|p| {
                [b.core, b.separator]
                    .into_iter()
                    .any(|q| exterior_piers::overlaps(p, q, eps))
            })
    };
    let safe = candidates
        .iter()
        .enumerate()
        .map(|(i, a)| {
            ![a.core, a.separator].into_iter().any(|p| {
                occupied
                    .iter()
                    .any(|&q| exterior_piers::overlaps(p, q, eps))
            }) && !candidates
                .iter()
                .enumerate()
                .any(|(j, b)| i != j && conflicts(a, b))
        })
        .collect::<Vec<_>>();
    let mut i = 0;
    candidates.retain(|_| {
        let accept = safe[i];
        i += 1;
        accept
    });
    candidates.sort_by(|a, b| a.owner_wall_id.cmp(&b.owner_wall_id));
    candidates
}
