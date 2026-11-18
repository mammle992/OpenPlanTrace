//! Choose coordinate-ready walls from observations. Opening hosts and measured wall extents
//! remain in the source scan regardless of their placement status.
pub mod connections;
mod effective;
pub mod enclosures;
mod exterior;
mod opening_flanks;
mod outlined_insulation;
mod painted_junctions;
pub use effective::effective_wall;
pub(crate) use effective::{corner_joints, effective_page, observation_page};
mod hatching;
pub mod insulation;
pub(crate) mod jamb;
mod laminates;
mod material;
mod overlap;
pub(crate) mod raster;
pub(crate) mod stroked;
use crate::model::{PageScan, Point, Wall};
use material::{WallMaterial, materials};
use serde::{Deserialize, Serialize};
use std::collections::{BTreeSet, HashMap, HashSet};

// OpenPlanTrace.Export's isolated opening-fragment limit is in drawing coordinates.
const MAX_OPENING_FRAGMENT_LENGTH: f64 = 140.;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum WallStatus {
    Ready,
    Review,
    EvidenceOnly,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum WallReason {
    SupportedRoomBoundary,
    AuthoredWallInRoomComponent,
    SupportedPhysicalEndpoints,
    AuthoredFilledPartition,
    AuthoredPaintedAndOutlinedWall,
    NestedFacesAtDoorJamb,
    HatchedFacesAtDoorJamb,
    HatchedWallFaces,
    TiledInsulationBetweenFaces,
    TiledInsulationAtStaggeredFaces,
    PaintedWallLayersBetweenFaces,
    PaintedCoreBetweenWallFaces,
    InsulatedDoorJamb,
    ContrastingCappedWallFaces,
    ContrastingBentWallFaces,
    IndependentlyOutlinedPaintedBody,
    SourceBoundedDoorJambFlanks,
    SourceBoundedPaintedJunctionSpan,
    SourceBoundedVisibleInsulationAssembly,
    InsulationFinishVisibilityMissing,
    #[serde(rename = "opening_linked_isolated_fragment_suppressed")]
    ShortOpeningLinkedIsolatedFragment,
    IsolatedOpeningHost,
    IsolatedWall,
    PartialRoomBoundary,
    ConnectedWallNeedsReview,
    OverlappingWallAxes,
    CurvedWallNeedsReview,
    IncompletePhysicalSpan,
    ConnectedRasterMaterialFaces,
    RasterFacesNeedReview,
    RasterContextBudgetExceeded,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WallDecision {
    pub wall_id: String,
    pub status: WallStatus,
    pub reason: WallReason,
    pub component_id: String,
    pub component_wall_count: usize,
    pub junction_node_ids: Vec<String>,
    pub support_wall_ids: Vec<String>,
    pub support_source_ids: Vec<String>,
    pub opening_ids: Vec<String>,
    pub room_ids: Vec<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub placement_extent: Option<PlacementExtent>,
}

/// The explicitly supported finite subset of an immutable wall observation.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PlacementExtent {
    pub start: Point,
    pub end: Point,
    pub source_ids: Vec<String>,
}

/// Source solids have either four corners or an explicitly repeated closing vertex.
pub(crate) fn painted_rectangle(shape: &crate::model::Shape) -> Option<[Point; 4]> {
    use crate::model::Shape;
    let Shape::Polyline {
        points,
        filled: true,
        closed: true,
    } = shape
    else {
        return None;
    };
    let pts = if points.first() == points.last() {
        points.get(..points.len().checked_sub(1)?)?
    } else {
        points
    };
    let corners: [Point; 4] = pts.try_into().ok()?;
    insulation::rectangle(&corners).then_some(corners)
}

/// Connected components use physical wall incidence. A graph edge spanning an
/// empty door gap has no wall owners and must never join its two flanks here.
struct Components(Vec<usize>);
impl Components {
    fn root(&mut self, mut wall: usize) -> usize {
        while self.0[wall] != wall {
            self.0[wall] = self.0[self.0[wall]];
            wall = self.0[wall];
        }
        wall
    }
    fn join(&mut self, a: usize, b: usize) {
        let a = self.root(a);
        let b = self.root(b);
        self.0[a.max(b)] = a.min(b);
    }
}

#[derive(Default)]
struct Support {
    junctions: Vec<usize>,
    rooms: BTreeSet<usize>,
    openings: Vec<usize>,
    edge_count: usize,
    boundary_edges: usize,
    has_solid: bool,
    overlapping: bool,
    shared_owner: Option<usize>,
    has_open_edge: bool,
    transverse_drift: f64,
    spans: Vec<(f64, f64)>,
    complete: bool,
}

pub(crate) fn point_key(p: Point) -> (u64, u64) {
    let bits = |v: f64| if v == 0. { 0 } else { v.to_bits() };
    (bits(p.x), bits(p.y))
}
fn ordered(a: usize, b: usize) -> (usize, usize) {
    (a.min(b), a.max(b))
}

pub(crate) fn roundoff(w: &Wall) -> f64 {
    [w.start.x, w.start.y, w.end.x, w.end.y]
        .into_iter()
        .map(f64::abs)
        .fold(1., f64::max)
        * 64.
        * f64::EPSILON
}
fn complete_span(s: &mut Support, w: &Wall) -> bool {
    let len = w.start.distance(w.end);
    let eps = roundoff(w);
    let tol = w.thickness * 0.5 + eps;
    s.spans.sort_by(|a, b| a.0.total_cmp(&b.0));
    let Some(&(first, mut end)) = s.spans.first() else {
        return false;
    };
    if !s.has_solid || s.transverse_drift > tol || first.abs() > tol {
        return false;
    }
    for &(a, b) in &s.spans[1..] {
        if (a - end).abs() > eps {
            return false;
        }
        end = b;
    }
    (len - end).abs() <= tol
}

/// A supporting wall must visibly continue through the joint on both sides of
/// the candidate body's width. A corner of a furniture outline cannot supply this.
fn end_supports(
    i: usize,
    page: &PageScan,
    support: &[Support],
    material: &[WallMaterial],
    indices: &HashMap<&str, usize>,
    arms: &HashMap<(usize, usize), u8>,
) -> [Option<usize>; 2] {
    let w = &page.walls[i];
    let mut ends = [None, None];
    if !material[i].structural || !support[i].complete || support[i].overlapping {
        return ends;
    }
    let axis = (w.end - w.start).unit();
    for &n in &support[i].junctions {
        let node = &page.nodes[n];
        // A many-way hub does not identify a unique attachment.
        if node.wall_ids.len() > 8 {
            continue;
        }
        for id in &node.wall_ids {
            let Some(&other) = indices.get(id.as_str()) else {
                continue;
            };
            let parent = &page.walls[other];
            if i == other
                || parent.curve.is_some()
                || !material[other].structural
                || !support[other].complete
                || arms.get(&(other, n)) != Some(&3)
                || w.source_ids.iter().any(|s| parent.source_ids.contains(s))
            {
                continue;
            }
            let width = w.thickness * 0.5;
            let a = (parent.start - node.position).cross(axis);
            let b = (parent.end - node.position).cross(axis);
            if a.min(b) >= -width || a.max(b) <= width {
                continue;
            }
            let reach = (w.thickness + parent.thickness) * 0.5 + roundoff(w);
            let distances = [
                w.start.distance(node.position),
                w.end.distance(node.position),
            ];
            let side = usize::from(distances[1] < distances[0]);
            if distances[side] <= reach && ends[side].is_none() {
                ends[side] = Some(other);
            }
        }
    }
    ends
}

/// Current additions operate after frozen selection and preserve earlier Ready walls.
pub(crate) fn select(page: &PageScan) -> Result<Vec<WallDecision>, String> {
    let mut decisions = select_preview19(page)?;
    outlined_insulation::select(page, &mut decisions)?;
    Ok(decisions)
}

pub(crate) fn select_preview19(page: &PageScan) -> Result<Vec<WallDecision>, String> {
    let mut decisions = select_preview18(page)?;
    painted_junctions::select(page, &mut decisions)?;
    Ok(decisions)
}

pub(crate) use outlined_insulation::{
    requirements as finish_requirements, select as select_visible_finishes,
};

/// Frozen scan v14 selection retains source-bounded doorway flanks.
pub(crate) fn select_preview18(page: &PageScan) -> Result<Vec<WallDecision>, String> {
    let observed = observation_page(page);
    let mut decisions = select_preview17(&observed)?;
    opening_flanks::select(page, &mut decisions)?;
    Ok(decisions)
}

pub(crate) fn select_preview17(page: &PageScan) -> Result<Vec<WallDecision>, String> {
    let mut decisions = select_preview16(page)?;
    if decisions.len() != page.walls.len() {
        return Err("placement decision inventory does not cover source walls".into());
    }
    if decisions
        .iter()
        .zip(&page.walls)
        .any(|(d, w)| d.wall_id != w.id)
    {
        let mut by_id: HashMap<_, _> = decisions
            .into_iter()
            .map(|d| (d.wall_id.clone(), d))
            .collect();
        decisions = page
            .walls
            .iter()
            .map(|w| {
                by_id
                    .remove(&w.id)
                    .ok_or_else(|| format!("missing placement decision for {}", w.id))
            })
            .collect::<Result<_, _>>()?;
    }
    exterior::select(page, &mut decisions)?;
    Ok(decisions)
}

/// Frozen selection used by native scan v8-v12, including preview 16.
pub(crate) fn select_preview16(page: &PageScan) -> Result<Vec<WallDecision>, String> {
    if page.walls.iter().any(raster::is_raster) {
        raster::validate_observations(page)?;
        // Pixel witnesses cannot change a vector wall's original authority.
        // A coincident grid stroke also cannot suppress an independent raster body.
        let mut vector_page = page.clone();
        let ids: HashSet<_> = page
            .pdf_input
            .as_ref()
            .map(|p| p.raster_source_ids.iter().map(String::as_str).collect())
            .unwrap_or_default();
        vector_page.walls.retain(|w| !raster::is_raster(w));
        vector_page
            .primitives
            .retain(|p| !ids.contains(p.id.as_str()));
        vector_page.pdf_input = None;
        let mut decisions = select_with_policy(&vector_page, Policy::Current)?;
        decisions.extend(page.walls.iter().filter(|w| raster::is_raster(w)).map(|w| {
            WallDecision {
                placement_extent: None,
                wall_id: w.id.clone(),
                status: WallStatus::Review,
                reason: WallReason::RasterFacesNeedReview,
                component_id: format!("p{}:raster-unresolved:{}", page.page_number, w.id),
                component_wall_count: 1,
                junction_node_ids: vec![],
                support_wall_ids: vec![],
                support_source_ids: vec![],
                opening_ids: vec![],
                room_ids: vec![],
            }
        }));
        raster::select(page, &mut decisions);
        Ok(decisions)
    } else {
        select_with_policy(page, Policy::Current)
    }
}

#[derive(Clone, Copy, PartialEq, Eq)]
enum Policy {
    Preview4,
    Preview5,
    Preview6,
    Preview7,
    Preview8,
    Current,
}

fn select_with_policy(page: &PageScan, policy: Policy) -> Result<Vec<WallDecision>, String> {
    if policy == Policy::Current {
        stroked::validate_recovered(page)?;
        raster::validate_observations(page)?;
    }
    let wall_index: HashMap<_, _> = page
        .walls
        .iter()
        .enumerate()
        .map(|(i, w)| (w.id.as_str(), i))
        .collect();
    let index = |key: &str| {
        wall_index
            .get(key)
            .copied()
            .ok_or_else(|| format!("unknown placement source wall {key}"))
    };
    let mut groups = Components((0..page.walls.len()).collect());
    let mut support: Vec<_> = (0..page.walls.len()).map(|_| Support::default()).collect();
    let sources = material::sources(page);
    let material = materials(page, &sources);
    let hatch_index =
        (policy != Policy::Preview4).then(|| hatching::HatchIndex::new(page, &sources));
    let tile_index = (matches!(
        policy,
        Policy::Preview6 | Policy::Preview7 | Policy::Preview8 | Policy::Current
    ) && !page.material_tiles.is_empty())
    .then(|| insulation::TileIndex::new(page));

    for (n, node) in page.nodes.iter().enumerate() {
        if let [first, rest @ ..] = node.wall_ids.as_slice() {
            let first = index(first)?;
            if !rest.is_empty() {
                support[first].junctions.push(n);
            }
            for other in rest {
                let other = index(other)?;
                groups.join(first, other);
                support[other].junctions.push(n);
            }
        }
    }
    for (i, opening) in page.openings.iter().enumerate() {
        for host in &opening.host_wall_ids {
            support[index(host)?].openings.push(i);
        }
    }
    let nodes_at: HashMap<_, _> = page
        .nodes
        .iter()
        .enumerate()
        .map(|(i, n)| (point_key(n.position), i))
        .collect();
    let edges_at: HashMap<_, _> = page
        .edges
        .iter()
        .enumerate()
        .map(|(i, e)| (ordered(e.start_node, e.end_node), i))
        .collect();
    let printed: HashSet<_> = page
        .room_annotations
        .iter()
        .filter_map(|a| a.room_id.as_deref())
        .collect();
    let mut room_edges = vec![Vec::new(); page.edges.len()];
    for (i, room) in page.rooms.iter().enumerate() {
        for w in &room.wall_ids {
            support[index(w)?].rooms.insert(i);
        }
        let independent_room = room.label_source_id.is_some()
            || printed.contains(room.id.as_str())
            || room.boundary_evidence.is_some();
        for (a, b) in room.polygon.iter().zip(room.polygon.iter().cycle().skip(1)) {
            let (Some(&a), Some(&b)) = (nodes_at.get(&point_key(*a)), nodes_at.get(&point_key(*b)))
            else {
                continue;
            };
            if let Some(&edge) = edges_at.get(&ordered(a, b)) {
                room_edges[edge].push((i, independent_room));
            }
        }
    }
    let mut solid_arms = HashMap::new();
    for (e, witnesses) in page.edges.iter().zip(&room_edges) {
        let endpoints = [e.start_node, e.end_node].map(|n| page.nodes.get(n).map(|n| n.position));
        let [Some(a), Some(b)] = endpoints else {
            return Err("invalid placement graph endpoint".into());
        };
        let pair = match e.wall_ids.as_slice() {
            [a, b] => Some([index(a)?, index(b)?]),
            _ => None,
        };
        for id in &e.wall_ids {
            let i = index(id)?;
            let w = &page.walls[i];
            let s = &mut support[i];
            let axis = (w.end - w.start).unit();
            let other = pair.map(|[a, b]| if a == i { b } else { a });
            if s.edge_count == 0 {
                s.shared_owner = other;
            } else if s.shared_owner != other {
                s.shared_owner = None;
            }
            s.edge_count += 1;
            s.overlapping |= e.wall_ids.len() > 1;
            s.has_open_edge |= !e.opening_ids.is_empty();
            s.has_solid |= e.opening_ids.is_empty();
            let mut span = [0.; 2];
            for (side, p) in [a, b].into_iter().enumerate() {
                let offset = p - w.start;
                s.transverse_drift = s.transverse_drift.max(offset.cross(axis).abs());
                span[side] = offset.dot(axis);
            }
            s.spans.push((span[0].min(span[1]), span[0].max(span[1])));
            if e.opening_ids.is_empty() && span[0] != span[1] {
                let (low, high) = if span[0] < span[1] {
                    (e.start_node, e.end_node)
                } else {
                    (e.end_node, e.start_node)
                };
                *solid_arms.entry((i, low)).or_insert(0u8) |= 1;
                *solid_arms.entry((i, high)).or_insert(0u8) |= 2;
            }
            for (room, _) in witnesses {
                s.rooms.insert(*room);
            }
            if witnesses.iter().any(|(_, independent)| *independent) {
                s.boundary_edges += 1;
            }
        }
    }
    let mut component_sizes = vec![0; page.walls.len()];
    let mut supported_components = HashSet::new();
    for (i, (w, s)) in page.walls.iter().zip(&mut support).enumerate() {
        let root = groups.root(i);
        component_sizes[root] += 1;
        s.complete = complete_span(s, w);
        if (material[i].authored || material[i].structural)
            && w.curve.is_none()
            && !s.overlapping
            && s.complete
            && s.boundary_edges == s.edge_count
        {
            supported_components.insert(root);
        }
    }
    let jambs = if matches!(
        policy,
        Policy::Preview7 | Policy::Preview8 | Policy::Current
    ) {
        jamb::proofs(page)?
    } else {
        HashMap::new()
    };
    let mut out = Vec::with_capacity(page.walls.len());
    for (i, (w, s)) in page.walls.iter().zip(&support).enumerate() {
        let root = groups.root(i);
        let count = component_sizes[root];
        let ends = end_supports(i, page, &support, &material, &wall_index, &solid_arms);
        let two_contacts = matches!(ends,[Some(a),Some(b)] if a!=b && !page.walls[a].source_ids.iter().any(|id|page.walls[b].source_ids.contains(id)));
        let mut parents = vec![];
        let mut source_ids = material[i].source_ids.clone();
        let (status, reason) = if w.curve.is_some() {
            (WallStatus::Review, WallReason::CurvedWallNeedsReview)
        } else if policy != Policy::Preview4
            && let Some(host) = s.shared_owner
            && let Some((reason, proof)) =
                overlap::door_flank(w, s, host, page, &support, &sources, hatch_index.as_ref())
        {
            parents.push(page.walls[host].id.clone());
            source_ids = proof;
            (WallStatus::Ready, reason)
        } else if let Some(index) = &tile_index
            && let Some(other) = s.shared_owner
            && s.complete
            && !s.has_open_edge
            && s.openings.is_empty()
            && let Some(proof) =
                insulation::nested::proof(w, &page.walls[other], s, page, &sources, index)
        {
            parents.push(page.walls[other].id.clone());
            source_ids = proof;
            (
                WallStatus::Ready,
                WallReason::TiledInsulationAtStaggeredFaces,
            )
        } else if s.overlapping {
            (WallStatus::Review, WallReason::OverlappingWallAxes)
        } else if !s.complete {
            (WallStatus::Review, WallReason::IncompletePhysicalSpan)
        } else if (material[i].authored || material[i].structural)
            && s.boundary_edges == s.edge_count
        {
            (WallStatus::Ready, WallReason::SupportedRoomBoundary)
        } else if material[i].authored
            && supported_components.contains(&root)
            && !s.junctions.is_empty()
        {
            (WallStatus::Ready, WallReason::AuthoredWallInRoomComponent)
        } else if material[i].structural && material[i].outlined && s.openings.is_empty() {
            (
                WallStatus::Ready,
                WallReason::AuthoredPaintedAndOutlinedWall,
            )
        } else if two_contacts {
            parents.extend(ends.into_iter().flatten().map(|j| page.walls[j].id.clone()));
            (WallStatus::Ready, WallReason::SupportedPhysicalEndpoints)
        } else if material[i].named_fill
            && let Some(parent) = ends
                .into_iter()
                .flatten()
                .next()
                .filter(|&j| material[j].named_fill)
        {
            parents.push(page.walls[parent].id.clone());
            (WallStatus::Ready, WallReason::AuthoredFilledPartition)
        } else if material[i].structural
            && w.body == crate::model::BodyEvidence::ParallelFaces
            && s.openings.is_empty()
            && !s.has_open_edge
            && let Some(proof) = hatch_index
                .as_ref()
                .and_then(|h| h.witnesses(w, page, &sources))
        {
            source_ids = proof.ids;
            (WallStatus::Ready, WallReason::HatchedWallFaces)
        } else if w.body == crate::model::BodyEvidence::ParallelFaces
            && s.openings.is_empty()
            && !s.has_open_edge
            && let Some(proof) = tile_index
                .as_ref()
                .and_then(|idx| idx.proof(w, page, &sources))
        {
            source_ids = proof;
            (WallStatus::Ready, WallReason::TiledInsulationBetweenFaces)
        } else if !s.rooms.is_empty() {
            (WallStatus::Review, WallReason::PartialRoomBoundary)
        } else if count == 1 && !s.openings.is_empty() {
            let short_flank = w.start.distance(w.end) <= MAX_OPENING_FRAGMENT_LENGTH
                && s.openings.iter().all(|&j| {
                    let o = &page.openings[j];
                    o.host_wall_ids.len() > 1 && o.interval_on(w).is_none()
                });
            if short_flank {
                (
                    WallStatus::EvidenceOnly,
                    WallReason::ShortOpeningLinkedIsolatedFragment,
                )
            } else {
                (WallStatus::Review, WallReason::IsolatedOpeningHost)
            }
        } else if count == 1 {
            (WallStatus::Review, WallReason::IsolatedWall)
        } else {
            (WallStatus::Review, WallReason::ConnectedWallNeedsReview)
        };
        // Older qualifying observations retain their precedence over a new jamb proposal.
        let (status, reason) = if (status != WallStatus::Ready || jamb::is_recovered(w))
            && s.complete
            && !s.has_open_edge
            && s.openings.is_empty()
            && let Some(proof) = jambs.get(&w.id)
        {
            parents = vec![proof.host.clone()];
            source_ids = proof.sources.clone();
            (WallStatus::Ready, WallReason::InsulatedDoorJamb)
        } else {
            (status, reason)
        };
        out.push(WallDecision {
            placement_extent: None,
            wall_id: w.id.clone(),
            status,
            reason,
            component_id: format!("p{}:placement-component:{}", page.page_number, root + 1),
            component_wall_count: count,
            support_wall_ids: parents,
            support_source_ids: source_ids,
            junction_node_ids: s
                .junctions
                .iter()
                .map(|&j| page.nodes[j].id.clone())
                .collect(),
            room_ids: s.rooms.iter().map(|&j| page.rooms[j].id.clone()).collect(),
            opening_ids: s
                .openings
                .iter()
                .map(|&j| page.openings[j].id.clone())
                .collect(),
        });
    }
    if matches!(
        policy,
        Policy::Preview7 | Policy::Preview8 | Policy::Current
    ) {
        jamb::check_overlaps(page, &mut out);
        laminates::select(page, &sources, &mut out);
    }
    if matches!(policy, Policy::Preview8 | Policy::Current) {
        stroked::select(page, &mut out, policy == Policy::Current);
    }
    if policy == Policy::Current {
        raster::select(page, &mut out);
    }
    Ok(out)
}

pub fn validate(page: &PageScan, required: bool) -> Result<(), String> {
    match (&page.placement_decisions, required) {
        (Some(actual), true) if *actual == select(page)? => Ok(()),
        (None, false) => Ok(()),
        _ => Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        )),
    }
}

pub(crate) fn validate_preview16(page: &PageScan, required: bool) -> Result<(), String> {
    match (&page.placement_decisions, required) {
        (Some(actual), true) if *actual == select_preview16(page)? => Ok(()),
        (None, false) => Ok(()),
        _ => Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        )),
    }
}

pub(crate) fn validate_preview17(page: &PageScan, required: bool) -> Result<(), String> {
    match (&page.placement_decisions, required) {
        (Some(actual), true) if *actual == select_preview17(page)? => Ok(()),
        (None, false) => Ok(()),
        _ => Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        )),
    }
}

pub(crate) fn validate_preview18(page: &PageScan, required: bool) -> Result<(), String> {
    match (&page.placement_decisions, required) {
        (Some(actual), true) if *actual == select_preview18(page)? => Ok(()),
        (None, false) => Ok(()),
        _ => Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        )),
    }
}

pub(crate) fn validate_preview19(page: &PageScan, required: bool) -> Result<(), String> {
    match (&page.placement_decisions, required) {
        (Some(actual), true) if *actual == select_preview19(page)? => Ok(()),
        (None, false) => Ok(()),
        _ => Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        )),
    }
}

/// Replay scan.v3 decisions with the placement policy that originally produced them.
pub fn validate_preview4(page: &PageScan) -> Result<(), String> {
    validate_saved(page, Policy::Preview4)
}

pub(crate) fn validate_preview5(page: &PageScan) -> Result<(), String> {
    validate_saved(page, Policy::Preview5)
}

pub(crate) fn validate_preview6(page: &PageScan) -> Result<(), String> {
    validate_saved(page, Policy::Preview6)
}

pub(crate) fn validate_preview7(page: &PageScan) -> Result<(), String> {
    validate_saved(page, Policy::Preview7)
}

pub(crate) fn validate_preview8(page: &PageScan) -> Result<(), String> {
    validate_saved(page, Policy::Preview8)
}

fn validate_saved(page: &PageScan, policy: Policy) -> Result<(), String> {
    if page.placement_decisions.as_ref() == Some(&select_with_policy(page, policy)?) {
        Ok(())
    } else {
        Err(format!(
            "placement wall decisions disagree with source evidence on page {}",
            page.page_number
        ))
    }
}
