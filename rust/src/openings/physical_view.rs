//! Publish physical cuts after frozen observation stages, with replayable proof.
use crate::{model::PageScan, semantics::RoutingKind};

pub(crate) fn apply(page: &mut PageScan) -> Result<(), String> {
    let changes = super::physical_jambs::proposals(&page.primitives, &page.walls, &page.openings)?;
    for change in changes {
        let opening = page
            .openings
            .iter_mut()
            .find(|o| o.id == change.opening_id)
            .ok_or("physical jamb proposal references missing opening")?;
        opening.start = change.start;
        opening.end = change.end;
        opening.physical_jambs = Some(change.evidence);
        for route in &mut page.semantics.routing {
            if route.entity_id == opening.id && route.kind == RoutingKind::DoorPassageCandidate {
                if let Some(span) = route.span.as_mut() {
                    span.start = opening.start;
                    span.end = opening.end;
                }
            }
        }
    }
    Ok(())
}

pub(crate) fn validate(page: &PageScan) -> Result<(), String> {
    let observed = crate::placement::observation_page(page);
    let changes = super::physical_jambs::proposals(
        &observed.primitives,
        &observed.walls,
        &observed.openings,
    )?;
    let mut expected = observed.openings.clone();
    for change in changes {
        let opening = expected
            .iter_mut()
            .find(|o| o.id == change.opening_id)
            .ok_or("physical jamb replay references missing opening")?;
        opening.start = change.start;
        opening.end = change.end;
        opening.physical_jambs = Some(change.evidence);
    }
    if expected != page.openings {
        return Err(format!(
            "physical jamb cuts disagree with source evidence on page {}",
            page.page_number
        ));
    }
    Ok(())
}
