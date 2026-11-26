//! A certified casing can own individual sash observations without changing the wall aperture.
use crate::{
    model::{Opening, OpeningEvidence, OpeningKind, PageScan, Point, Shape},
    placement::WallStatus,
};
use std::collections::{HashMap, HashSet};

const MAX_WORK: usize = 8_000_000;

fn charge(work: &mut usize, count: usize) -> Result<(), String> {
    *work = work
        .checked_sub(count)
        .ok_or("window unit evidence budget exceeded")?;
    Ok(())
}

struct Frame {
    opening: usize,
    start: Point,
    axis: Point,
    length: f64,
    half: f64,
    epsilon: f64,
}

impl Frame {
    fn new(opening: usize, frame: &Opening, width: f64) -> Option<Self> {
        let extent = frame.end - frame.start;
        let length = extent.norm();
        (width.is_finite() && width > 0. && length.is_finite() && length > 0.).then_some(Self {
            opening,
            start: frame.start,
            axis: extent.unit(),
            length,
            half: width * 0.5,
            // Use the same drawing-relative tolerance as the exterior casing proof.
            epsilon: width * 0.005,
        })
    }

    fn interval(&self, a: Point, b: Point) -> [f64; 2] {
        let v = (a - self.start).dot(self.axis);
        let w = (b - self.start).dot(self.axis);
        [v.min(w), v.max(w)]
    }

    fn owns_rail(&self, pane: &Opening, a: Point, b: Point) -> bool {
        if !pane.start.finite() || !pane.end.finite() || !a.finite() || !b.finite() {
            return false;
        }
        if (pane.end - pane.start).unit().cross(self.axis).abs() > 1e-5
            || (a - b).cross(self.axis).abs() > self.epsilon
        {
            return false;
        }
        let pane_interval = self.interval(pane.start, pane.end);
        if pane_interval[0] <= self.epsilon
            || pane_interval[1] >= self.length - self.epsilon
            || pane_interval[1] - pane_interval[0] <= 2. * self.epsilon
        {
            return false;
        }
        // Early pane coordinates are projected onto their observed host, which
        // can be off-center. Check lateral placement against the real source rail.
        [a, b]
            .into_iter()
            .all(|p| (p - self.start).cross(self.axis).abs() <= self.half + self.epsilon)
            && self
                .interval(a, b)
                .into_iter()
                .zip(pane_interval)
                .all(|(rail, pane)| (rail - pane).abs() <= self.epsilon)
    }
}

/// Return only windows that are uniquely owned by a complete source-proved unit.
pub(super) fn owners(page: &PageScan) -> Result<Vec<Option<usize>>, String> {
    let mut result = vec![None; page.openings.len()];
    let Some(decisions) = &page.placement_decisions else {
        return Ok(result);
    };
    if !page
        .openings
        .iter()
        .any(|o| o.kind == OpeningKind::Window && o.evidence == OpeningEvidence::ExteriorFrameGap)
    {
        return Ok(result);
    }

    let mut work = MAX_WORK;
    charge(&mut work, page.walls.len())?;
    charge(&mut work, decisions.len())?;
    let wall_widths: HashMap<_, _> = page
        .walls
        .iter()
        .map(|w| (w.id.as_str(), w.thickness))
        .collect();
    let status: HashMap<_, _> = decisions
        .iter()
        .map(|d| (d.wall_id.as_str(), d.status))
        .collect();
    let mut frames = Vec::new();
    let mut frames_by_source = HashMap::<&str, Vec<usize>>::new();
    for (i, frame) in page.openings.iter().enumerate() {
        let [a, b] = frame.host_wall_ids.as_slice() else {
            continue;
        };
        if frame.kind != OpeningKind::Window
            || frame.evidence != OpeningEvidence::ExteriorFrameGap
            || a == b
            || status.get(a.as_str()) != Some(&WallStatus::Ready)
            || status.get(b.as_str()) != Some(&WallStatus::Ready)
        {
            continue;
        }
        let (Some(&wa), Some(&wb)) = (wall_widths.get(a.as_str()), wall_widths.get(b.as_str()))
        else {
            continue;
        };
        if !wa.is_finite() || !wb.is_finite() {
            continue;
        }
        if let Some(bounds) = Frame::new(i, frame, wa.min(wb)) {
            charge(&mut work, frame.source_ids.len())?;
            for source in &frame.source_ids {
                frames_by_source
                    .entry(source)
                    .or_default()
                    .push(frames.len());
            }
            frames.push(bounds);
        }
    }
    if frames.is_empty() {
        return Ok(result);
    }

    charge(&mut work, page.primitives.len())?;
    let mut rails = HashMap::new();
    for p in &page.primitives {
        if let Shape::Line { start, end } = p.shape
            && let Some(frame_indices) = frames_by_source.get(p.id.as_str())
        {
            rails.insert(p.id.as_str(), (start, end, frame_indices));
        }
    }
    let mut referenced = HashSet::<&str>::new();
    for refs in decisions
        .iter()
        .filter(|d| d.status == WallStatus::Ready)
        .map(|d| d.opening_ids.as_slice())
        .chain(page.edges.iter().map(|e| e.opening_ids.as_slice()))
    {
        charge(&mut work, refs.len().saturating_add(1))?;
        referenced.extend(refs.iter().map(String::as_str));
    }
    charge(&mut work, page.room_connections.len())?;
    referenced.extend(page.room_connections.iter().map(|c| c.opening_id.as_str()));

    for (i, pane) in page.openings.iter().enumerate() {
        if pane.kind != OpeningKind::Window
            || pane.evidence != OpeningEvidence::FiniteWindowAssembly
            || referenced.contains(pane.id.as_str())
            || pane.host_wall_ids.is_empty()
            || pane.host_wall_ids.iter().any(|id| {
                !matches!(
                    status.get(id.as_str()),
                    Some(WallStatus::Review | WallStatus::EvidenceOnly)
                )
            })
        {
            continue;
        }
        charge(&mut work, pane.source_ids.len())?;
        let mut owner = None;
        'sources: for source in &pane.source_ids {
            let Some(&(a, b, indices)) = rails.get(source.as_str()) else {
                continue;
            };
            charge(&mut work, indices.len())?;
            for &index in indices {
                let frame = &frames[index];
                if Some(frame.opening) == owner || !frame.owns_rail(pane, a, b) {
                    continue;
                }
                if owner.is_some() {
                    owner = None;
                    break 'sources;
                }
                owner = Some(frame.opening);
            }
        }
        result[i] = owner;
    }
    Ok(result)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn an_offset_sash_needs_its_longitudinal_rail_inside_the_frame() {
        let p = Point::new;
        let mut pane = Opening {
            id: "sash".into(),
            page: 1,
            kind: OpeningKind::Window,
            operation: "Fixed".into(),
            host_wall_id: "observed".into(),
            host_wall_ids: vec!["observed".into()],
            start: p(12., 2.),
            end: p(24., 2.),
            hinge: None,
            swing_observations: vec![],
            source_ids: vec![],
            evidence: OpeningEvidence::FiniteWindowAssembly,
            requires_review: true,
            physical_jambs: None,
        };
        let mut parent = pane.clone();
        parent.start = p(10., 8.);
        parent.end = p(40., 8.);
        let frame = Frame::new(0, &parent, 6.).unwrap();
        assert!(frame.owns_rail(&pane, p(12., 5.2), p(24., 5.2)));
        assert!(!frame.owns_rail(&pane, p(12., 5.2), p(12., 10.8)));
        assert!(!frame.owns_rail(&pane, p(12., 3.2), p(24., 3.2)));
        assert!(!frame.owns_rail(&pane, p(12., 5.2), p(30., 5.2)));
        pane.start = p(40., 2.);
        pane.end = p(52., 2.);
        assert!(!frame.owns_rail(&pane, p(40., 5.2), p(52., 5.2)));
    }
}
