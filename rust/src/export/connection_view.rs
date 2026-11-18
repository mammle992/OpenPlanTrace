//! Source-coordinate connection overlay. No wall solid or source geometry is edited.
use crate::{model::PageScan, placement::connections::ConnectionKind};
use std::{collections::HashMap, fmt::Write};

pub(super) fn svg(page: &PageScan) -> String {
    let mut image = super::svg(page, true);
    let Some(pass) = &page.exterior_connections else {
        return image;
    };
    let mut overlay = String::from(
        "<g id=\"boundary-connections\"><desc>Green: existing wall graph. Teal: source-supported material contacts. Dashed blue/cyan: door/window boundary thresholds, no wall material. Orange: unresolved gaps, not connected. Purple: closed graph boundary candidates requiring review; area follows wall axes.</desc>",
    );
    let scale = page.width.min(page.height).max(1.);
    let line = scale * 0.0012;
    let node_radius = scale * 0.0018;
    let nodes: HashMap<_, _> = pass.nodes.iter().map(|n| (n.id.as_str(), n)).collect();
    for boundary in &pass.boundary_candidates {
        let points = boundary
            .polygon
            .iter()
            .map(|p| format!("{},{}", p.x, p.y))
            .collect::<Vec<_>>()
            .join(" ");
        let _ = write!(
            overlay,
            "<polygon data-boundary=\"{}\" points=\"{}\" fill=\"none\" stroke=\"#7153a2\" stroke-width=\"{}\" stroke-dasharray=\"{} {}\"><title>Closed connection boundary; {}; requires review</title></polygon>",
            super::esc(&boundary.id),
            points,
            line * 2.,
            line * 5.,
            line * 4.,
            super::esc(&boundary.exterior_status)
        );
    }
    for edge in &pass.edges {
        let (Some(a), Some(b)) = (
            nodes.get(edge.start_node_id.as_str()),
            nodes.get(edge.end_node_id.as_str()),
        ) else {
            continue;
        };
        let (color, dashed) = match edge.kind {
            ConnectionKind::Wall => ("#257c40", false),
            ConnectionKind::DoorThreshold => ("#245fb8", true),
            ConnectionKind::WindowThreshold => ("#008eab", true),
            _ => ("#008575", false),
        };
        let dash = if dashed {
            format!(" stroke-dasharray=\"{} {}\"", line * 4., line * 3.)
        } else {
            String::new()
        };
        let _ = write!(
            overlay,
            "<line data-connection=\"{}\" data-adds-wall-material=\"false\" x1=\"{}\" y1=\"{}\" x2=\"{}\" y2=\"{}\" stroke=\"{}\" stroke-width=\"{}\"{}><title>{:?}: {}</title></line>",
            super::esc(&edge.id),
            a.position.x,
            a.position.y,
            b.position.x,
            b.position.y,
            color,
            line,
            dash,
            edge.kind,
            super::esc(&edge.evidence)
        );
    }
    for node in &pass.nodes {
        let _ = write!(
            overlay,
            "<circle data-node=\"{}\" cx=\"{}\" cy=\"{}\" r=\"{}\" fill=\"white\" stroke=\"#257c40\" stroke-width=\"{}\"><title>{}; degree {}</title></circle>",
            super::esc(&node.id),
            node.position.x,
            node.position.y,
            node_radius,
            line * 0.6,
            super::esc(&node.id),
            node.degree
        );
    }
    for gap in &pass.uncertain_gaps {
        let reason = super::esc(&gap.reason);
        if let Some(end) = gap.end {
            let _ = write!(
                overlay,
                "<line data-uncertain-gap=\"{}\" data-connected=\"false\" x1=\"{}\" y1=\"{}\" x2=\"{}\" y2=\"{}\" stroke=\"#c77900\" stroke-width=\"{}\" stroke-dasharray=\"{} {}\"><title>Unconnected: {}</title></line>",
                super::esc(&gap.id),
                gap.start.x,
                gap.start.y,
                end.x,
                end.y,
                line * 1.5,
                line * 1.5,
                line * 2.,
                reason
            );
        }
        let _ = write!(
            overlay,
            "<circle data-open-end=\"{}\" cx=\"{}\" cy=\"{}\" r=\"{}\" fill=\"none\" stroke=\"#c77900\" stroke-width=\"{}\"><title>Unresolved: {}</title></circle>",
            super::esc(&gap.id),
            gap.start.x,
            gap.start.y,
            node_radius * 2.2,
            line,
            reason
        );
    }
    overlay.push_str("</g>");
    if let Some(at) = image.rfind("</svg>") {
        image.insert_str(at, &overlay);
    }
    image
}
