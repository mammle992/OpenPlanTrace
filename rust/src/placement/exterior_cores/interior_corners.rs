//! Authored core shoulders and thin seams at exact right-angle junctions.
use super::interior_seams::{bounds, minmax, overlap_rect, part, pattern};
use super::*;

type Tiles<'a> = [(&'a insulation::MaterialTile, Wall)];
type Ink<'a> = HashMap<&'a str, Vec<Rail<'a>>>;

pub(super) struct Patch {
    pub wall: Wall,
    tile_id: String,
    supports: Vec<String>,
}

impl Patch {
    pub(super) fn into_decision(self) -> (Wall, WallDecision) {
        let mut support_source_ids = self.wall.source_ids.clone();
        support_source_ids.push(self.tile_id);
        support_source_ids.sort();
        support_source_ids.dedup();
        let decision = WallDecision {
            wall_id: self.wall.id.clone(),
            status: WallStatus::Ready,
            reason: WallReason::SourceBoundedInteriorCore,
            component_id: format!("p{}:{}", self.wall.page, self.wall.id),
            component_wall_count: 1,
            junction_node_ids: vec![],
            support_wall_ids: self.supports,
            support_source_ids,
            opening_ids: vec![],
            room_ids: vec![],
            placement_extent: None,
        };
        (self.wall, decision)
    }
}

fn adjacent(
    mut wall: Wall,
    authored: &Wall,
    tiles: &Tiles<'_>,
    ink: &Ink<'_>,
    supports: [&str; 2],
    borders: impl Fn(&Frame, &[Rail<'_>], f64) -> Option<Vec<String>>,
) -> Option<Patch> {
    let shape = Frame::new(&wall);
    let f = Frame::new(authored);
    let eps = super::super::roundoff(authored);
    for (tile, core) in tiles {
        let [a, b] = [core.start, core.end].map(|p| f.local(p));
        if a.1.abs().max(b.1.abs()) > eps
            || (core.thickness - authored.thickness).abs() > eps
            || a.0.max(b.0).min(f.length) - a.0.min(b.0).max(0.) <= eps
        {
            continue;
        }
        let bars = &ink[tile.layer.as_deref()?];
        let Some(mut sources) = borders(&shape, bars, eps) else {
            continue;
        };
        let Some(hatch) = pattern(&shape, bars, tile) else {
            continue;
        };
        sources.extend(hatch.into_iter().map(str::to_owned));
        sources.sort();
        sources.dedup();
        wall.source_ids = sources;
        let mut support_wall_ids = supports.map(str::to_owned).to_vec();
        support_wall_ids.sort();
        return Some(Patch {
            wall,
            tile_id: tile.id.clone(),
            supports: support_wall_ids,
        });
    }
    None
}

fn clear(wall: &Wall, ready: &[Wall], page: &PageScan) -> bool {
    !ready.iter().any(|b| overlap_rect(wall, b))
        && !page
            .openings
            .iter()
            .any(|o| overlap::aperture_crosses(wall, o))
}

/// A signed short jamb may meet a square return immediately beyond a placed
/// collinear body. Require its exposed outline and tile phase, plus the
/// measured positive seam to a distinct placed partition on its other side.
pub(super) fn jamb_shoulder(
    page: &PageScan,
    flank: &Wall,
    foot: Point,
    ready: &[Wall],
    tiles: &Tiles<'_>,
    ink: &Ink<'_>,
) -> Option<[Patch; 2]> {
    let f = Frame::new(flank);
    let eps = super::super::roundoff(flank);
    let width = flank.thickness;
    let at = f.local(foot).0;
    let direction = if at.abs() <= eps { -1. } else { 1. };
    let mut result = None;
    for parent in ready {
        let [a, b] = [parent.start, parent.end].map(|p| f.local(p));
        if a.1.abs().max(b.1.abs()) > eps || (parent.thickness - width).abs() > eps {
            continue;
        }
        let extent = if direction < 0. {
            a.0.max(b.0)
        } else {
            a.0.min(b.0)
        };
        if !((width * 0.95)..=(width * 1.05)).contains(&((extent - at) * direction)) {
            continue;
        }
        let mut square = flank.clone();
        square.id = format!("interior-seam:{}:shoulder:{}", page.page_number, flank.id);
        square.start = f.origin + f.axis * extent.min(at);
        square.end = f.origin + f.axis * extent.max(at);
        if !clear(&square, ready, page) {
            continue;
        }
        for arm in ready {
            let g = Frame::new(arm);
            if f.axis.dot(g.axis).abs() > 1e-5
                || (g.half * 2. - square.start.distance(square.end)).abs() > eps
            {
                continue;
            }
            let (lo, hi, left, right) = minmax(&bounds(&g, &square));
            if (left + g.half).abs().max((right - g.half).abs()) > eps {
                continue;
            }
            let max_seam = arm.thickness * 0.19;
            let (span, side) = if (eps..=max_seam).contains(&-hi) {
                ([(hi), (0.)], arm.start - square.start)
            } else if (eps..=max_seam).contains(&(lo - g.length)) {
                ([g.length, lo], arm.end - square.start)
            } else {
                continue;
            };
            let mut seam = arm.clone();
            seam.id = format!("interior-seam:{}:arm:{}", page.page_number, flank.id);
            seam.start = g.origin + g.axis * span[0];
            seam.end = g.origin + g.axis * span[1];
            if !clear(&seam, ready, page) || overlap_rect(&square, &seam) {
                continue;
            }
            let Some(short) = adjacent(
                seam,
                arm,
                tiles,
                ink,
                [&arm.id, &square.id],
                |s, ink, eps| {
                    let (a, sa) = part(s, ink, -s.half, eps)?;
                    let (b, sb) = part(s, ink, s.half, eps)?;
                    (sa && sb && draw_path(a) != draw_path(b))
                        .then(|| vec![a.to_owned(), b.to_owned()])
                },
            ) else {
                continue;
            };
            let exposed = -f.axis.cross(side).signum();
            let Some(body) = adjacent(
                square.clone(),
                parent,
                tiles,
                ink,
                [&parent.id, &flank.id],
                |s, ink, eps| {
                    let (outer, stroked) = part(s, ink, exposed * s.half, eps)?;
                    stroked.then(|| vec![outer.to_owned()])
                },
            ) else {
                continue;
            };
            if result.is_some() {
                return None;
            }
            result = Some([body, short]);
        }
    }
    result
}

fn cap<'a>(f: &Frame, ink: &'a [Rail<'a>], at: f64, eps: f64) -> Option<&'a str> {
    ink.iter().find_map(|s| {
        let [a, b] = s.ends.map(|p| f.local(p));
        (s.stroked
            && (a.0 - at).abs().max((b.0 - at).abs()) <= eps
            && (a.1.min(b.1) + f.half)
                .abs()
                .max((a.1.max(b.1) - f.half).abs())
                <= eps)
            .then_some(s.id)
    })
}

/// A square mitred end can have its shared faces supplied by precisely
/// abutting walls. Require an independent cap and free-side rail, and the
/// actual directional tile lattice extending from the first wall.
pub(super) fn mitered_ends(
    page: &PageScan,
    ready: &[Wall],
    tiles: &Tiles<'_>,
    ink: &Ink<'_>,
) -> Vec<Patch> {
    let mut proposals: Vec<Patch> = vec![];
    for branch in ready {
        let f = Frame::new(branch);
        let eps = super::super::roundoff(branch);
        for mate in ready {
            let v = (mate.end - mate.start).unit();
            if f.axis.dot(v).abs() > 1e-5 || (mate.thickness - branch.thickness).abs() > eps {
                continue;
            }
            let (lo, hi, left, right) = minmax(&bounds(&f, mate));
            let (end, side) = if (lo - f.length).abs() <= eps {
                (true, hi)
            } else if hi.abs() <= eps {
                (false, lo)
            } else {
                continue;
            };
            let contact = if (left - f.half).abs() <= eps {
                1.
            } else if (right + f.half).abs() <= eps {
                -1.
            } else {
                continue;
            };
            let (u, v) = if end { (f.length, side) } else { (side, 0.) };
            if ((v - u) - branch.thickness).abs() > eps {
                continue;
            }
            let mut body = branch.clone();
            body.id = format!(
                "interior-seam:{}:miter:{}:{}",
                page.page_number,
                branch.id,
                usize::from(end)
            );
            body.start = f.origin + f.axis * u;
            body.end = f.origin + f.axis * v;
            if !clear(&body, ready, page) {
                continue;
            }
            let Some(patch) = adjacent(
                body,
                branch,
                tiles,
                ink,
                [&branch.id, &mate.id],
                |s, ink, eps| {
                    let external = cap(s, ink, if end { s.length } else { 0. }, eps)?;
                    let (face, _) = part(s, ink, -contact * s.half, eps)?;
                    (draw_path(face) != draw_path(external))
                        .then(|| vec![face.to_owned(), external.to_owned()])
                },
            ) else {
                continue;
            };
            if !proposals.iter().any(|p| {
                p.wall.id == patch.wall.id
                    && p.wall.start.distance(patch.wall.start) <= eps
                    && p.wall.end.distance(patch.wall.end) <= eps
            }) {
                proposals.push(patch);
            }
        }
    }
    let mut internal = vec![false; proposals.len()];
    for (i, a) in proposals.iter().enumerate() {
        for (j, b) in proposals.iter().enumerate().skip(i + 1) {
            if a.wall.id == b.wall.id || overlap_rect(&a.wall, &b.wall) {
                internal[i] = true;
                internal[j] = true;
            }
        }
    }
    proposals
        .into_iter()
        .zip(internal)
        .filter_map(|(p, reject)| (!reject).then_some(p))
        .collect()
}
