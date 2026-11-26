//! Objects retain the IDs of painted sources. Neutral strokes only support a reviewed generic
//! symbol when they form a compact, connected outline with internal linework inside one room.
mod contours;
mod stairs;

use crate::model::*;
use std::borrow::Cow;
use std::collections::{BTreeMap, BTreeSet, HashMap, HashSet};

const MAX_EDGES: usize = 250_000;
const MAX_WORK: usize = 32_000_000;
const MAX_OBJECTS: usize = 20_000;

#[derive(Clone, Copy)]
struct Bounds {
    lo: Point,
    hi: Point,
}
impl Bounds {
    fn point(p: Point) -> Self {
        Self { lo: p, hi: p }
    }
    fn add(&mut self, p: Point) {
        self.lo.x = self.lo.x.min(p.x);
        self.lo.y = self.lo.y.min(p.y);
        self.hi.x = self.hi.x.max(p.x);
        self.hi.y = self.hi.y.max(p.y);
    }
    fn of(points: &[Point]) -> Option<Self> {
        let mut b = Self::point(*points.first()?);
        for &p in &points[1..] {
            b.add(p);
        }
        Some(b)
    }
    fn size(self) -> Point {
        self.hi - self.lo
    }
    fn center(self) -> Point {
        (self.lo + self.hi) * 0.5
    }
    fn contains(self, p: Point) -> bool {
        p.x >= self.lo.x && p.y >= self.lo.y && p.x <= self.hi.x && p.y <= self.hi.y
    }
    fn overlaps(self, b: Self) -> bool {
        self.hi.x >= b.lo.x && self.hi.y >= b.lo.y && self.lo.x <= b.hi.x && self.lo.y <= b.hi.y
    }
    fn intersects_segment(self, a: Point, b: Point) -> bool {
        let (mut lo, mut hi) = (0.0_f64, 1.0_f64);
        for (start, delta, min, max) in [
            (a.x, b.x - a.x, self.lo.x, self.hi.x),
            (a.y, b.y - a.y, self.lo.y, self.hi.y),
        ] {
            if delta.abs() < 1e-12 {
                if start < min || start > max {
                    return false;
                }
            } else {
                let (u, v) = ((min - start) / delta, (max - start) / delta);
                lo = lo.max(u.min(v));
                hi = hi.min(u.max(v));
                if lo > hi {
                    return false;
                }
            }
        }
        true
    }
}
fn charge(work: &mut usize, n: usize) -> Result<(), String> {
    *work = work
        .checked_sub(n)
        .ok_or("object geometry work limit exceeded")?;
    Ok(())
}
struct Rooms<'a>(Vec<(&'a Room, Bounds)>);
impl Rooms<'_> {
    fn enclosing(
        &self,
        at: Point,
        footprint: Option<Bounds>,
        work: &mut usize,
    ) -> Result<Option<&Room>, String> {
        let mut found = None;
        for &(r, bounds) in &self.0 {
            charge(work, 1)?;
            if !bounds.contains(at) {
                continue;
            }
            charge(work, r.polygon.len())?;
            if !inside(at, &r.polygon) {
                continue;
            }
            if found.is_some() {
                return Ok(None);
            }
            if let Some(b) = footprint {
                let extent = b.size();
                if extent.x * extent.y > r.area * 0.25
                    || !bounds.contains(b.lo)
                    || !bounds.contains(b.hi)
                {
                    return Ok(None);
                }
                charge(work, r.polygon.len())?;
                if r.polygon
                    .iter()
                    .zip(r.polygon.iter().cycle().skip(1))
                    .any(|(&p, &q)| b.intersects_segment(p, q))
                {
                    return Ok(None);
                }
            }
            found = Some(r);
        }
        Ok(found)
    }
}
#[derive(Default)]
struct Strokes {
    keys: HashMap<(i64, i64), usize>,
    points: Vec<Point>,
    adjacency: Vec<Vec<usize>>,
    edges: Vec<(usize, usize, usize)>,
}
impl Strokes {
    fn node(&mut self, p: Point, precision: f64) -> usize {
        let key = (
            (p.x / precision).round() as i64,
            (p.y / precision).round() as i64,
        );
        *self.keys.entry(key).or_insert_with(|| {
            self.points.push(p);
            self.adjacency.push(Vec::new());
            self.points.len() - 1
        })
    }
    fn add(&mut self, a: Point, b: Point, source: usize, precision: f64) -> Result<(), String> {
        if !a.finite() || !b.finite() || a.distance(b) < precision {
            return Ok(());
        }
        if self.edges.len() >= MAX_EDGES {
            return Err("object stroke limit exceeded".into());
        }
        let (u, v) = (self.node(a, precision), self.node(b, precision));
        if u != v {
            let e = self.edges.len();
            self.edges.push((u, v, source));
            self.adjacency[u].push(e);
            self.adjacency[v].push(e);
        }
        Ok(())
    }
    fn candidates(
        &self,
        page: &Page,
        rooms: &Rooms,
        labels: &[Bounds],
        objects: &mut Vec<Object>,
        work: &mut usize,
    ) -> Result<(), String> {
        let mut visited = vec![false; self.points.len()];
        for first in 0..visited.len() {
            if visited[first] {
                continue;
            }
            let mut stack = vec![first];
            visited[first] = true;
            let mut edges = Vec::new();
            let mut nodes = 0;
            let mut dangling = false;
            let mut bounds = Bounds::point(self.points[first]);
            while let Some(n) = stack.pop() {
                let adjacent = &self.adjacency[n];
                nodes += 1;
                dangling |= adjacent.len() < 2;
                bounds.add(self.points[n]);
                for &e in adjacent {
                    let (u, v, _) = self.edges[e];
                    if u == n && edges.len() < 65 {
                        edges.push(e)
                    }
                    let other = if u == n { v } else { u };
                    if !visited[other] {
                        visited[other] = true;
                        stack.push(other)
                    }
                }
            }
            // A closed perimeter and an additional connected cycle can support internal detail.
            // A dangling path, a bare box, and a pair of duplicate strokes cannot support it.
            if dangling || !(5..=64).contains(&edges.len()) || edges.len() <= nodes {
                continue;
            }
            let d = bounds.size();
            if d.x.min(d.y) < 5.0
                || d.x.max(d.y) > 4.0 * d.x.min(d.y)
                || d.x * d.y > page.width * page.height * 0.05
            {
                continue;
            }
            let mut unique = BTreeSet::new();
            let mut sources = BTreeSet::new();
            let mut interior = 0;
            let inner = Bounds {
                lo: bounds.lo + d * 0.15,
                hi: bounds.hi - d * 0.15,
            };
            for e in edges {
                let (u, v, source) = self.edges[e];
                unique.insert((u.min(v), u.max(v)));
                sources.insert(source);
                if inner.contains((self.points[u] + self.points[v]) * 0.5) {
                    interior += 1
                }
            }
            if unique.len() <= nodes || sources.len() < 2 || interior == 0 {
                continue;
            }
            let at = bounds.center();
            let Some(room) = rooms.enclosing(at, Some(bounds), work)? else {
                continue;
            };
            charge(work, labels.len())?;
            if labels.iter().any(|b| b.overlaps(bounds)) {
                continue;
            }
            if objects.len() >= MAX_OBJECTS {
                return Err("object candidate limit exceeded".into());
            }
            objects.push(Object {
                geometry: None,
                profile_signature: None,
                footprint: None,
                id: format!("p{}:object:{}", page.number, objects.len() + 1),
                at,
                category: "GenericSymbol".into(),
                name: "Unclassified vector symbol".into(),
                room_id: Some(room.id.clone()),
                source_ids: sources
                    .into_iter()
                    .map(|s| page.primitives[s].id.clone())
                    .collect(),
                requires_review: true,
            });
        }
        Ok(())
    }
}
pub fn detect(
    page: &Page,
    rooms: &[Room],
    walls: &[Wall],
    openings: &[Opening],
    opts: &Options,
) -> Result<(Vec<Object>, Vec<ObjectGroup>), String> {
    detect_selected(page, rooms, walls, openings, &[], opts)
}

pub(crate) fn detect_selected(
    page: &Page,
    rooms: &[Room],
    walls: &[Wall],
    openings: &[Opening],
    decisions: &[WithheldWall],
    opts: &Options,
) -> Result<(Vec<Object>, Vec<ObjectGroup>), String> {
    let mut work = MAX_WORK;
    let mut room_bounds = Vec::new();
    for room in rooms {
        charge(&mut work, room.polygon.len())?;
        if let Some(b) = Bounds::of(&room.polygon) {
            room_bounds.push((room, b))
        }
    }
    let rooms = Rooms(room_bounds);
    let claimed: HashSet<&str> = walls
        .iter()
        .flat_map(|w| &w.source_ids)
        .chain(openings.iter().flat_map(|o| &o.source_ids))
        .chain(
            decisions
                .iter()
                .filter(|d| d.furniture_outline.is_some())
                .flat_map(|d| &d.witness_source_ids),
        )
        .map(String::as_str)
        .collect();
    let mut objects = Vec::new();
    let mut repeats = BTreeMap::<String, Vec<String>>::new();
    let mut strokes = Strokes::default();
    let mut labels = Vec::new();
    // Endpoint quantization absorbs paint-rounding noise, well below the wall snap tolerance.
    let precision = (opts.snap_tolerance * 0.025).clamp(0.01, 0.1);
    for (index, p) in page.primitives.iter().enumerate() {
        // The label can be on a dedicated layer while its surrounding strokes are unlayered.
        if let Shape::Text { at, height, value } = &p.shape {
            if !value.is_empty() && !rooms.0.is_empty() {
                let w = height * (value.chars().take(4096).count() as f64);
                labels.push(Bounds {
                    lo: Point::new(at.x - height * 0.25, at.y - height),
                    hi: Point::new(at.x + w, at.y + height * 0.35),
                });
            }
            continue;
        }
        let layer = p.layer.as_deref().unwrap_or("").trim();
        let kind = p.layer_kind();
        let named = match &p.shape {
            // An INSERT is the explicit source occurrence, even for a named door block.
            Shape::Insert { at, name } => Some((*at, name.clone(), true)),
            Shape::Polyline {
                points,
                closed: true,
                ..
            } if kind == LayerKind::Object && !points.is_empty() => {
                charge(&mut work, points.len())?;
                Some((
                    points.iter().fold(Point::default(), |s, &p| s + p)
                        * (1. / points.len() as f64),
                    layer.to_owned(),
                    false,
                ))
            }
            _ => None,
        };
        if let Some((at, name, block)) = named {
            if !at.finite() {
                continue;
            }
            if objects.len() >= MAX_OBJECTS {
                return Err("object candidate limit exceeded".into());
            }
            let id = format!("p{}:object:{}", page.number, objects.len() + 1);
            let category = source_category(p);
            if block {
                repeats.entry(name.clone()).or_default().push(id.clone())
            }
            let room_id = rooms.enclosing(at, None, &mut work)?.map(|r| r.id.clone());
            objects.push(Object {
                geometry: None,
                profile_signature: None,
                footprint: None,
                id,
                at,
                name,
                category: category.into(),
                room_id,
                source_ids: vec![p.id.clone()],
                requires_review: true,
            });
            continue;
        }
        // Block children and dimension-block graphics have source ancestry from the DXF reader.
        if claimed.contains(p.id.as_str())
            || rooms.0.is_empty()
            || !(layer.is_empty() || layer == "0")
            || p.id.contains(":INSERT:")
            || p.id.contains(":DIMENSION:")
        {
            continue;
        }
        match &p.shape {
            Shape::Line { start, end } => strokes.add(*start, *end, index, precision)?,
            Shape::Polyline {
                points,
                closed,
                filled: false,
            } => {
                charge(&mut work, points.len())?;
                for ab in points.windows(2) {
                    strokes.add(ab[0], ab[1], index, precision)?
                }
                if *closed && points.len() >= 3 {
                    strokes.add(points[points.len() - 1], points[0], index, precision)?
                }
            }
            _ => {}
        }
    }
    strokes.candidates(page, &rooms, &labels, &mut objects, &mut work)?;
    for d in decisions {
        let Some(polygon) = d.furniture_outline else {
            continue;
        };
        let Some((category, name)) = furniture_kind(d.reason) else {
            continue;
        };
        let at = polygon
            .iter()
            .copied()
            .fold(Point::default(), |sum, p| sum + p)
            * 0.25;
        charge(
            &mut work,
            objects.len().saturating_mul(1 + d.witness_source_ids.len()),
        )?;
        if objects.iter().any(|old| {
            if let Some(f) = &old.footprint {
                return polygon
                    .iter()
                    .all(|p| f.polygon.iter().any(|q| q.distance(*p) < 1e-6));
            }
            old.source_ids.iter().any(|id| {
                d.witness_source_ids.contains(id)
                    || d.witness_source_ids.iter().any(|s| {
                        s.strip_prefix(id)
                            .is_some_and(|suffix| suffix.starts_with(':'))
                    })
            })
        }) {
            continue;
        }
        if objects.len() >= MAX_OBJECTS {
            return Err("object candidate limit exceeded".into());
        }
        let room_id = rooms
            .enclosing(at, Bounds::of(&polygon), &mut work)?
            .map(|r| r.id.clone());
        objects.push(Object {
            geometry: None,
            profile_signature: None,
            id: format!("p{}:object:{}", page.number, objects.len() + 1),
            at,
            category: category.into(),
            name: name.into(),
            room_id,
            source_ids: d.witness_source_ids.clone(),
            requires_review: true,
            footprint: Some(FurnitureFootprint {
                selection_id: d.wall.id.clone(),
                polygon,
            }),
        });
    }
    let groups = repeats
        .into_iter()
        .filter(|(_, ids)| ids.len() >= 2)
        .map(|(name, member_ids)| ObjectGroup {
            profile_signature: None,
            name,
            member_ids,
            requires_review: true,
        })
        .collect();
    Ok((objects, groups))
}
fn geometry_candidates(
    page: &Page,
    walls: &[Wall],
    openings: &[Opening],
) -> Result<Vec<(Vec<Point>, ObjectGeometryEvidence, Vec<String>)>, String> {
    let mut out = Vec::new();
    for c in stairs::detect(page, walls, openings, &Options::default())? {
        out.push((
            c.polygon.to_vec(),
            ObjectGeometryEvidence::StairFlight {
                cross_line_count: c.tread_count,
                travel_arrow: [c.axis_start, c.axis_end],
            },
            c.source_ids,
        ));
    }
    for c in contours::detect(page, walls, openings)? {
        let evidence = if let Some((drain_source_id, tap_source_id)) = c.basin_witness {
            ObjectGeometryEvidence::BasinSymbol {
                outer_source_id: c.outer_source_id,
                basin_source_ids: c.inner_source_ids,
                drain_source_id,
                tap_source_id,
            }
        } else {
            ObjectGeometryEvidence::NestedContours {
                outer_source_id: c.outer_source_id,
                inner_source_ids: c.inner_source_ids,
            }
        };
        out.push((c.polygon, evidence, c.source_ids));
    }
    Ok(out)
}

/// Append measured proposals after wall recovery, without changing any wall or opening.
pub(crate) fn append_geometry(
    page: &Page,
    rooms: &[Room],
    walls: &[Wall],
    openings: &[Opening],
    objects: &mut Vec<Object>,
) -> Result<(), String> {
    let mut vector_page = Cow::Borrowed(page);
    let raster: HashSet<_> = page
        .pdf_input
        .iter()
        .flat_map(|p| &p.raster_source_ids)
        .collect();
    if !raster.is_empty() {
        vector_page
            .to_mut()
            .primitives
            .retain(|p| !raster.contains(&p.id));
    }
    let walls: Vec<_> = walls
        .iter()
        .filter(|w| !crate::placement::raster::is_raster(w))
        .cloned()
        .collect();
    let mut bounds = Vec::new();
    for room in rooms {
        if let Some(b) = Bounds::of(&room.polygon) {
            bounds.push((room, b));
        }
    }
    let rooms = Rooms(bounds);
    let mut work = MAX_WORK;
    let mut serial = 0;
    for (polygon, evidence, source_ids) in geometry_candidates(&vector_page, &walls, openings)? {
        charge(&mut work, objects.len().saturating_mul(source_ids.len()))?;
        if objects
            .iter()
            .any(|o| o.source_ids.iter().any(|s| source_ids.contains(s)))
        {
            continue;
        }
        if objects.len() >= MAX_OBJECTS {
            return Err("object candidate limit exceeded".into());
        }
        let b = Bounds::of(&polygon).ok_or("empty object geometry")?;
        let at = b.center();
        let room_id = rooms
            .enclosing(at, Some(b), &mut work)?
            .map(|r| r.id.clone());
        let (category, name) = evidence.category_and_name();
        serial += 1;
        objects.push(Object {
            geometry: Some(ObjectGeometry { polygon, evidence }),
            profile_signature: None,
            footprint: None,
            id: format!("p{}:geometry-object:{}", page.number, serial),
            at,
            category: category.into(),
            name: name.into(),
            room_id,
            source_ids,
            requires_review: true,
        });
    }
    Ok(())
}

pub(crate) fn validate_geometry(page: &PageScan, current: bool) -> Result<(), String> {
    if page.objects.iter().any(|o| {
        o.id.starts_with(&format!("p{}:geometry-object:", page.page_number)) && o.geometry.is_none()
    }) {
        return Err("measured object lacks source geometry".into());
    }
    let detected: Vec<_> = page
        .objects
        .iter()
        .filter(|o| o.geometry.is_some())
        .collect();
    if !current {
        return if detected.is_empty() {
            Ok(())
        } else {
            Err("object geometry requires native scan v11".into())
        };
    }
    let source = Page {
        finish_history: None,
        placement_polygon_paint_ids: page.placement_polygon_paint_ids.clone(),
        placement_paint_remainders: vec![],
        white_nested_walls: vec![],
        terminal_stock: vec![],
        local_stock: vec![],
        stepped_stock: vec![],
        capped_stock: vec![],
        placement_contact_paint: vec![],
        number: page.page_number,
        width: page.width,
        height: page.height,
        units_to_mm: page.units_to_millimeters,
        primitives: page.primitives.clone(),
        material_tiles: page.material_tiles.clone(),
        placement_paint_ids: page.placement_paint_ids.clone(),
        pdf_input: page.pdf_input.clone(),
    };
    let saved_base: Vec<_> = page
        .objects
        .iter()
        .filter(|o| o.geometry.is_none())
        .collect();
    let raster: HashSet<_> = source
        .pdf_input
        .iter()
        .flat_map(|p| &p.raster_source_ids)
        .collect();
    let mut original = Cow::Borrowed(&source);
    if !raster.is_empty() {
        original
            .to_mut()
            .primitives
            .retain(|p| !raster.contains(&p.id));
    }
    let original_walls: Vec<_> = page
        .walls
        .iter()
        .filter(|w| {
            !crate::placement::raster::is_raster(w) && !crate::placement::stroked::is_recovered(w)
        })
        .cloned()
        .collect();
    let (mut expected, _) = detect_selected(
        &original,
        &page.rooms,
        &original_walls,
        &page.openings,
        page.withheld_walls.as_deref().unwrap_or_default(),
        &Options::default(),
    )?;
    if saved_base.len() != expected.len() {
        return Err("object baseline differs from source candidates".into());
    }
    for (a, b) in expected.iter().zip(saved_base) {
        if a.id != b.id
            || a.at != b.at
            || a.source_ids != b.source_ids
            || a.room_id != b.room_id
            || serde_json::to_value(&a.footprint).unwrap()
                != serde_json::to_value(&b.footprint).unwrap()
            || (b.profile_signature.is_none()
                && (a.name != b.name
                    || a.category != b.category
                    || a.requires_review != b.requires_review))
        {
            return Err("object candidate differs from retained source geometry".into());
        }
    }
    if let Err(error) = append_geometry(
        &source,
        &page.rooms,
        &page.walls,
        &page.openings,
        &mut expected,
    ) {
        return if detected.is_empty()
            && page
                .warnings
                .iter()
                .any(|w| w == &format!("Object geometry withheld: {error}"))
        {
            Ok(())
        } else {
            Err("object proposal withholding disagrees with source budget".into())
        };
    }
    let expected: Vec<_> = expected.iter().filter(|o| o.geometry.is_some()).collect();
    if expected.len() != detected.len() {
        return Err("missing or duplicate measured object geometry".into());
    }
    for (a, b) in expected.into_iter().zip(detected) {
        if a.id != b.id
            || a.geometry != b.geometry
            || a.at != b.at
            || a.source_ids != b.source_ids
            || a.room_id != b.room_id
            || !b.requires_review
            || b.footprint.is_some()
            || (b.profile_signature.is_none() && (a.name != b.name || a.category != b.category))
        {
            return Err("object geometry differs from source measurements".into());
        }
    }
    Ok(())
}

pub(crate) fn source_category(p: &Primitive) -> &'static str {
    use crate::layers::LayerCategory as C;
    match p.layer_category_override {
        Some(C::Electrical) => "ElectricalDevice",
        Some(C::HVAC) => "HVACEquipment",
        Some(C::Plumbing) => "PlumbingFixture",
        Some(c @ (C::Equipment | C::FireSafety | C::Furniture | C::Fixture | C::Structural)) => {
            c.as_str()
        }
        None if p
            .layer_name()
            .split(|c: char| !c.is_ascii_alphanumeric())
            .any(|s| s.eq_ignore_ascii_case("HVAC")) =>
        {
            "HVACEquipment"
        }
        _ => "GenericSymbol",
    }
}
pub(crate) fn furniture_kind(reason: WallSelectionReason) -> Option<(&'static str, &'static str)> {
    match reason {
        WallSelectionReason::FurnishedSurface => {
            Some(("FurnishedSurface", "Furnished work surface"))
        }
        WallSelectionReason::CabinetFront => Some(("Cabinet", "Cabinet")),
        _ => None,
    }
}

pub(crate) fn checked_outline(
    outline: &[Point; 4],
    decision: &WithheldWall,
    sources: &HashMap<&str, &Primitive>,
    work: &mut usize,
) -> Result<(), String> {
    if furniture_kind(decision.reason).is_none() {
        return Err("unqualified selection for furnished outline".into());
    }
    let mut found = [false; 4];
    for id in &decision.witness_source_ids {
        let Some(p) = sources.get(id.as_str()) else {
            return Err("missing furnished outline witness".into());
        };
        let mut check = |point: Point| {
            for (i, corner) in outline.iter().enumerate() {
                found[i] |= corner.distance(point) < 1e-6;
            }
        };
        match &p.shape {
            Shape::Line { start, end } => {
                charge(work, 8)?;
                check(*start);
                check(*end);
            }
            Shape::Polyline { points, .. } => {
                charge(work, points.len().saturating_mul(4))?;
                for &p in points {
                    check(p)
                }
            }
            _ => (),
        }
        if found.iter().all(|&x| x) {
            break;
        }
    }
    let turns = std::array::from_fn::<f64, 4, _>(|i| {
        let a = outline[i];
        let b = outline[(i + 1) % 4];
        let c = outline[(i + 2) % 4];
        (b - a).cross(c - b)
    });
    if !found.into_iter().all(|x| x)
        || !(turns.iter().all(|t| *t > 0.) || turns.iter().all(|t| *t < 0.))
    {
        return Err("furniture footprint lacks four observed convex corners".into());
    }
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    fn pt(x: f64, y: f64) -> Point {
        Point::new(x, y)
    }
    fn room() -> Room {
        Room {
            boundary_evidence: None,
            id: "r".into(),
            polygon: vec![pt(0., 0.), pt(100., 0.), pt(100., 100.), pt(0., 100.)],
            area: 10000.,
            wall_ids: vec![],
            label: None,
            label_source_id: None,
            requires_review: true,
        }
    }
    fn primitive(id: &str, shape: Shape) -> Primitive {
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: id.into(),
            layer: None,
            shape,
        }
    }
    fn fixture() -> Page {
        let mut p = Vec::new();
        for (i, (a, b)) in [
            (pt(20., 20.), pt(40., 20.)),
            (pt(40., 20.), pt(40., 40.)),
            (pt(40., 40.), pt(20., 40.)),
            (pt(20., 40.), pt(20., 20.)),
            (pt(20., 20.), pt(40., 40.)),
        ]
        .into_iter()
        .enumerate()
        {
            p.push(primitive(
                &format!("s{i}"),
                Shape::Line { start: a, end: b },
            ));
        }
        Page {
            finish_history: None,
            pdf_input: None,
            number: 1,
            placement_paint_ids: None,
            placement_polygon_paint_ids: None,
            placement_paint_remainders: vec![],
            white_nested_walls: vec![],
            terminal_stock: vec![],
            local_stock: vec![],
            stepped_stock: vec![],
            capped_stock: vec![],
            placement_contact_paint: vec![],
            material_tiles: vec![],
            width: 200.,
            height: 200.,
            units_to_mm: None,
            primitives: p,
        }
    }
    #[test]
    fn finite_closed_linework_is_review_only_with_complete_source_provenance() {
        let page = fixture();
        let (o, g) = detect(&page, &[room()], &[], &[], &Options::default()).unwrap();
        assert_eq!(o.len(), 1);
        assert!(g.is_empty());
        assert!(o[0].requires_review);
        assert_eq!(o[0].category, "GenericSymbol");
        assert_eq!(o[0].room_id.as_deref(), Some("r"));
        assert_eq!(o[0].source_ids, ["s0", "s1", "s2", "s3", "s4"]);
        assert_eq!(o[0].at, pt(30., 30.));
    }
    #[test]
    fn labels_claimed_strokes_open_contours_and_sheet_geometry_do_not_become_symbols() {
        let page = fixture();
        let run = |p: &Page, r: &[Room], w: &[Wall]| {
            detect(p, r, w, &[], &Options::default()).unwrap().0.len()
        };
        assert_eq!(run(&page, &[], &[]), 0);
        let wall = Wall {
            id: "w".into(),
            page: 1,
            start: pt(20., 20.),
            end: pt(40., 40.),
            thickness: 2.,
            body: BodyEvidence::ExplicitCenterline,
            curve: None,
            source_ids: vec!["s4".into()],
            requires_review: true,
        };
        assert_eq!(run(&page, &[room()], &[wall]), 0);
        let mut open = page.clone();
        open.primitives.remove(0);
        assert_eq!(run(&open, &[room()], &[]), 0);
        let mut labeled = page.clone();
        labeled.primitives.push(Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: "t".into(),
            layer: Some("A-TEXT".into()),
            shape: Shape::Text {
                at: pt(25., 32.),
                value: "A".into(),
                height: 8.,
            },
        });
        assert_eq!(run(&labeled, &[room()], &[]), 0);
        for layer in ["A-HATCH", "A-DIMS", "A-GRID", "A-WALL", "A-DOORS"] {
            let mut p = page.clone();
            for s in &mut p.primitives {
                s.layer = Some(layer.into())
            }
            assert_eq!(run(&p, &[room()], &[]), 0);
        }
    }
    #[test]
    fn named_cad_objects_keep_their_ids_and_only_named_repeats_group() {
        let mut page = fixture();
        for (i, x) in [(1, 70.), (2, 80.)] {
            page.primitives.push(Primitive {
                layer_category_override: None,
                stroke_width: None,
                id: format!("block-{i}"),
                layer: Some("M-HVAC".into()),
                shape: Shape::Insert {
                    at: pt(x, 60.),
                    name: "FAN".into(),
                },
            })
        }
        let (o, g) = detect(&page, &[room()], &[], &[], &Options::default()).unwrap();
        assert_eq!(o.len(), 3);
        assert_eq!(o[0].category, "HVACEquipment");
        assert_eq!(o[2].category, "GenericSymbol");
        assert_eq!(g.len(), 1);
        assert_eq!(g[0].member_ids, ["p1:object:1", "p1:object:2"]);
        assert!(g[0].requires_review);
    }
}
