//! Separate counted window units from pane observations used by the detector.
use crate::{
    model::{Opening, OpeningEvidence, PageScan, WindowPane},
    semantics::{RoutingItem, RoutingKind, Span},
};
use std::{borrow::Cow, collections::HashSet};

struct WindowUnits {
    owners: Vec<Option<String>>,
    frames: Vec<Opening>,
}

fn window_units(
    page: &PageScan,
    combine_panes: bool,
    recover_frames: bool,
) -> Result<WindowUnits, String> {
    let frame_owners = super::units::owners(page)?;
    let mut owners = frame_owners
        .iter()
        .map(|owner| owner.map(|i| page.openings[i].id.clone()))
        .collect::<Vec<_>>();
    let mut frames = vec![];
    if combine_panes {
        let mut proposals = super::composite::proposals(page, &frame_owners)?;
        proposals.sort_by_key(|group| group.members);
        for (i, mut group) in proposals.into_iter().enumerate() {
            group.opening.id = format!("p{}:window-frame:{}", page.page_number, i + 1);
            for member in group.members {
                let owner = &mut owners[member];
                if owner.replace(group.opening.id.clone()).is_some() {
                    return Err("window pane has multiple frame owners".into());
                }
            }
            frames.push(group.opening);
        }
    }
    if recover_frames {
        let used = owners.iter().map(Option::is_some).collect::<Vec<_>>();
        let mut proposals = super::authored_frame::proposals(page, &used, &frames)?;
        proposals.sort_by(|a, b| {
            a.opening
                .start
                .x
                .total_cmp(&b.opening.start.x)
                .then(a.opening.start.y.total_cmp(&b.opening.start.y))
                .then(a.opening.end.x.total_cmp(&b.opening.end.x))
                .then(a.opening.end.y.total_cmp(&b.opening.end.y))
        });
        for (i, mut frame) in proposals.into_iter().enumerate() {
            frame.opening.id = format!("p{}:authored-window:{}", page.page_number, i + 1);
            for member in frame.members {
                if owners[member].replace(frame.opening.id.clone()).is_some() {
                    return Err("window pane has multiple frame owners".into());
                }
            }
            frames.push(frame.opening);
        }
    }
    Ok(WindowUnits { owners, frames })
}

pub(crate) fn separate_panes(page: &mut PageScan) -> Result<(), String> {
    let units = window_units(page, true, true)?;
    if units.frames.is_empty() && units.owners.iter().all(Option::is_none) {
        return Ok(());
    }
    for (opening_index, (observation, owner)) in std::mem::take(&mut page.openings)
        .into_iter()
        .zip(units.owners)
        .enumerate()
    {
        if let Some(frame_opening_id) = owner {
            page.window_panes.push(WindowPane {
                frame_opening_id,
                opening_index,
                observation,
            });
        } else {
            page.openings.push(observation);
        }
    }
    let panes: HashSet<_> = page
        .window_panes
        .iter()
        .map(|p| p.observation.id.as_str())
        .collect();
    page.semantics.routing.retain(|route| {
        route.kind != RoutingKind::WindowCandidate || !panes.contains(route.entity_id.as_str())
    });
    page.semantics
        .routing
        .extend(units.frames.iter().map(|frame| RoutingItem {
            kind: RoutingKind::WindowCandidate,
            entity_id: frame.id.clone(),
            source_ids: frame.source_ids.clone(),
            span: Some(Span {
                start: frame.start,
                end: frame.end,
            }),
            curve: None,
            room_ids: vec![],
            usable_for_routing: false,
            requires_review: true,
        }));
    page.openings.extend(units.frames);
    Ok(())
}

fn is_late_frame(opening: &Opening) -> bool {
    matches!(
        opening.evidence,
        OpeningEvidence::CompositeWindowFrame | OpeningEvidence::AuthoredWindowFrame
    )
}

/// Recreate the ordered inputs of the earlier geometry passes.
pub(crate) fn retained_window_observations(page: &PageScan) -> Result<Cow<'_, PageScan>, String> {
    let frame_count = page.openings.iter().filter(|o| is_late_frame(o)).count();
    Ok(if page.window_panes.is_empty() && frame_count == 0 {
        Cow::Borrowed(page)
    } else {
        let count = page.openings.len() - frame_count + page.window_panes.len();
        let mut physical = page
            .openings
            .iter()
            .filter(|opening| !is_late_frame(opening));
        let mut panes = page.window_panes.iter().peekable();
        let mut observations = Vec::with_capacity(count);
        for i in 0..count {
            if panes.peek().is_some_and(|pane| pane.opening_index == i) {
                observations.push(
                    panes
                        .next()
                        .expect("matched pane position")
                        .observation
                        .clone(),
                );
            } else if let Some(opening) = physical.next() {
                observations.push(opening.clone());
            } else {
                return Err("invalid retained window pane order".into());
            }
        }
        if panes.next().is_some() || physical.next().is_some() {
            return Err("invalid retained window pane order".into());
        }
        let mut replay = page.clone();
        replay.openings = observations;
        replay.window_panes.clear();
        Cow::Owned(replay)
    })
}

/// Replay the separation so that a standalone unit cannot be hidden as a pane.
pub(crate) fn validate_window_units(
    page: &PageScan,
    observed: &PageScan,
    combine_panes: bool,
    recover_frames: bool,
) -> Result<(), String> {
    let units = window_units(observed, combine_panes, recover_frames)?;
    if !page
        .openings
        .iter()
        .filter(|o| is_late_frame(o))
        .eq(units.frames.iter())
    {
        return Err("window frames disagree with source evidence".into());
    }
    let found = units
        .owners
        .iter()
        .enumerate()
        .filter_map(|(index, owner)| {
            owner
                .as_ref()
                .map(|id| (index, id, &observed.openings[index]))
        });
    let saved = page
        .window_panes
        .iter()
        .map(|p| (p.opening_index, &p.frame_opening_id, &p.observation));
    if !found.eq(saved) {
        return Err("window units disagree with their retained source evidence".into());
    }
    Ok(())
}
