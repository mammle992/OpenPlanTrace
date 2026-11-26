//! Restore an independently drawn batt patch at the outer knee of a proven step.
use super::*;
use std::collections::HashSet;

/// Both incident pieces are already supported source material. The patch
/// extends the branch back to the tail's outside face at their shared knee.
fn knee(branch: &Wall, tail: &Wall) -> Option<(Wall, [Point; 2], [Point; 2])> {
    let eps = super::super::roundoff(branch).max(super::super::roundoff(tail));
    let corner_width = tail.thickness;
    let half = branch.thickness * 0.5;
    if (corner_width - branch.thickness).abs() > 3e-5 * branch.thickness {
        return None;
    }

    for (at, far) in [(branch.start, branch.end), (branch.end, branch.start)] {
        let back = (at - far).unit();
        let outside = at + back * corner_width;
        for (tip, inside) in [(tail.start, tail.end), (tail.end, tail.start)] {
            let outward = (tip - inside).unit();
            let expected_tip = (at + outside) * 0.5 - outward * half;
            if back.dot(outward).abs() > 1e-10 || tip.distance(expected_tip) > eps {
                continue;
            }

            let mut wall = branch.clone();
            wall.start = at;
            wall.end = outside;
            let outer_face = [at + outward * half, outside + outward * half];
            let outer_cap = [outside - outward * half, outside + outward * half];
            return Some((wall, outer_face, outer_cap));
        }
    }
    None
}

fn witnessed_side<'a>(ink: &'a [Rail<'a>], expected: [Point; 2], eps: f64) -> Option<&'a str> {
    ink.iter().find_map(|line| {
        let [a, b] = line.ends;
        let [c, d] = expected;
        (line.stroked
            && (a.distance(c).max(b.distance(d)) <= eps || a.distance(d).max(b.distance(c)) <= eps))
            .then_some(line.id)
    })
}

fn continuing_hatch<'a>(
    frame: &Frame,
    tile: &insulation::MaterialTile,
    ink: &'a [Rail<'a>],
) -> Option<Vec<&'a str>> {
    let repeat = tile.repeat.dot(frame.axis);
    let across = frame.axis.cross(tile.across);
    let phase = frame.local(tile.origin).0;
    let width = frame.half * 2.;
    let eps = frame.half * 0.006;
    if (across.abs() - width).abs() > eps || !(0.75 * width..=1.3 * width).contains(&repeat.abs()) {
        return None;
    }

    let row_edge = -across.signum() * frame.half;
    let mut paths = HashSet::new();
    let mut witnesses = Vec::new();
    let mut slopes = [0, 0];
    let mut along = [f64::INFINITY, f64::NEG_INFINITY];
    let mut cross = along;
    for line in ink {
        let [(at, ac), (bt, bc)] = line.ends.map(|p| frame.local(p));
        if at.min(bt) < -eps
            || at.max(bt) > frame.length + eps
            || ac.abs().max(bc.abs()) > frame.half + eps
            || (at - bt).abs().min((ac - bc).abs()) <= 4. * eps
        {
            continue;
        }

        let a = Point::new(at, ac);
        let b = Point::new(bt, bc);
        let cell = ((at.midpoint(bt) - phase) / repeat).round();
        let matches = tile.strokes.iter().any(|stroke| {
            [-1., 0., 1.].into_iter().any(|shift| {
                let [p, q] = stroke.map(|v| {
                    Point::new(
                        phase + (cell + shift + v.x) * repeat,
                        row_edge + v.y * across,
                    )
                });
                crate::model::on_segment(a, p, q).0.distance(a) <= eps
                    && crate::model::on_segment(b, p, q).0.distance(b) <= eps
            })
        });
        if !matches || !paths.insert(draw_path(line.id)) {
            continue;
        }
        witnesses.push(line.id);
        slopes[usize::from((at - bt) * (ac - bc) > 0.)] += 1;
        along[0] = along[0].min(at).min(bt);
        along[1] = along[1].max(at).max(bt);
        cross[0] = cross[0].min(ac).min(bc);
        cross[1] = cross[1].max(ac).max(bc);
    }

    // A short decorative diagonal or hatch confined to one triangle cannot
    // support the patch. Require paint near all four boundary planes.
    (slopes.iter().all(|&count| count >= 4)
        && along[0] <= frame.length * 0.12
        && along[1] >= frame.length * 0.88
        && cross[0] <= -frame.half * 0.76
        && cross[1] >= frame.half * 0.76)
        .then_some(witnesses)
}

pub(super) fn prove<'a>(
    branch: &Wall,
    branch_tile: &insulation::MaterialTile,
    tail: &Wall,
    tail_tile: &insulation::MaterialTile,
    ink: &'a [Rail<'a>],
    parent: &str,
) -> Option<(Wall, WallDecision)> {
    if branch_tile.layer != tail_tile.layer {
        return None;
    }
    let (wall, outer_face, outer_cap) = knee(branch, tail)?;
    let eps = super::super::roundoff(&wall);
    let face = witnessed_side(ink, outer_face, eps)?;
    let cap = witnessed_side(ink, outer_cap, eps)?;
    if draw_path(face) == draw_path(cap) {
        return None;
    }
    let mut witnesses = continuing_hatch(&Frame::new(&wall), branch_tile, ink)?;
    witnesses.extend([face, cap]);
    let (wall, mut decision) = step_decision(wall, branch_tile, &witnesses, parent, "corner");
    decision.support_source_ids.push(tail_tile.id.clone());
    decision.support_source_ids.sort();
    decision.support_source_ids.dedup();
    Some((wall, decision))
}
