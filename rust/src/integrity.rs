//! Validate semantic references against the retained physical and source records.
use crate::{model::*, semantics::*};
use std::collections::{BTreeSet, HashMap, HashSet};

type Sources<'a> = HashMap<&'a str, &'a Primitive>;

fn positive(x: f64) -> bool {
    x.is_finite() && x > 0.
}
fn near(a: f64, b: f64) -> bool {
    a.is_finite() && b.is_finite() && (a - b).abs() <= 1e-14 + 1e-9 * a.abs().max(b.abs())
}
fn same_number(a: Option<f64>, b: Option<f64>) -> bool {
    match (a, b) {
        (Some(a), Some(b)) => near(a, b),
        (None, None) => true,
        _ => false,
    }
}
fn finite_span(s: &Span) -> bool {
    s.start.finite() && s.end.finite() && positive(s.start.distance(s.end))
}
fn references(ids: &[String], sources: &Sources<'_>) -> bool {
    !ids.is_empty() && ids.iter().all(|id| sources.contains_key(id.as_str()))
}
fn is_text(id: &str, sources: &Sources<'_>) -> bool {
    matches!(sources.get(id).map(|p| &p.shape), Some(Shape::Text { .. }))
}
fn is_line(id: &str, sources: &Sources<'_>) -> bool {
    matches!(
        sources.get(id).map(|p| &p.shape),
        Some(Shape::Line { .. } | Shape::Polyline { filled: false, .. })
    )
}
fn has_segment(
    shape: &Shape,
    budget: &mut usize,
    mut matches: impl FnMut(Point, Point) -> bool,
) -> Result<bool, String> {
    let mut check = |a, b| {
        *budget = budget
            .checked_sub(1)
            .ok_or("dimension geometry validation work limit exceeded")?;
        Ok::<_, String>(matches(a, b))
    };
    match shape {
        Shape::Line { start, end } => check(*start, *end),
        Shape::Polyline {
            points,
            closed,
            filled: false,
        } => {
            for side in points.windows(2) {
                if check(side[0], side[1])? {
                    return Ok(true);
                }
            }
            if *closed && points.len() > 2 {
                return check(points[points.len() - 1], points[0]);
            }
            Ok(false)
        }
        _ => Ok(false),
    }
}
fn require(ok: bool, message: &str, id: &str) -> Result<(), String> {
    if ok {
        Ok(())
    } else {
        Err(format!("{message} at {id}"))
    }
}

fn dimensions(page: &PageScan, sources: &Sources<'_>) -> Result<(), String> {
    let mut ids = HashSet::new();
    let mut segment_budget = 8_000_000;
    for d in &page.semantics.native_dimensions {
        let Some(Shape::Dimension {
            start,
            end,
            at,
            value,
            measurement,
        }) = sources.get(d.source_id.as_str()).map(|p| &p.shape)
        else {
            return Err(format!("missing native dimension entity {}", d.source_id));
        };
        require(
            ids.insert(d.source_id.as_str())
                && d.requires_review
                && d.line.start == *start
                && d.line.end == *end
                && d.text_at == *at
                && d.text == *value
                && d.reported_model_units == measurement.filter(|v| positive(*v))
                && d.reported_mm.is_none_or(positive)
                && (d.reported_mm.is_none() || d.reported_model_units.is_some()),
            "inconsistent native dimension",
            &d.source_id,
        )?;
        if page.scale_source.as_deref() == Some("InputModelUnits") {
            let mm = d
                .reported_model_units
                .zip(page.units_to_millimeters)
                .map(|(n, k)| n * k)
                .filter(|v| positive(*v));
            require(
                same_number(d.reported_mm, mm),
                "incorrect DXF measurement in millimeters",
                &d.source_id,
            )?;
        }
    }
    ids.clear();
    for d in &page.semantics.dimensions {
        require(
            !d.id.is_empty()
                && ids.insert(d.id.as_str())
                && d.requires_review
                && positive(d.measured_mm)
                && references(&d.text_sources, sources),
            "invalid measured dimension",
            &d.id,
        )?;
        let native = d.evidence == DimensionEvidence::NativeDxfEntity;
        if !native {
            require(
                d.text_sources.iter().all(|id| is_text(id, sources)),
                "invalid dimension text source",
                &d.id,
            )?;
        }
        if d.evidence == DimensionEvidence::UnitHint {
            require(
                d.line.is_none()
                    && d.line_source.is_none()
                    && d.orientation.is_none()
                    && d.start_witness.is_none()
                    && d.end_witness.is_none()
                    && d.drawing_length.is_none()
                    && d.observed_mm_per_unit.is_none()
                    && d.calibrated_length_mm.is_none()
                    && d.calibration_relative_error.is_none(),
                "unit hint claims unsupported geometry or scale",
                &d.id,
            )?;
            continue;
        }
        let (Some(span), Some(source)) = (&d.line, d.line_source.as_deref()) else {
            return Err(format!("missing dimension line {}", d.id));
        };
        let len = span.start.distance(span.end);
        let calibrated = page.units_to_millimeters.map(|k| len * k);
        require(
            finite_span(span)
                && d.orientation.is_some()
                && d.drawing_length.is_some_and(positive)
                && d.observed_mm_per_unit.is_some_and(positive)
                && d.calibrated_length_mm.is_none_or(positive)
                && d.calibration_relative_error
                    .is_none_or(|x| x >= 0. && x.is_finite())
                && same_number(d.drawing_length, Some(len))
                && same_number(d.observed_mm_per_unit, Some(d.measured_mm / len))
                && same_number(d.calibrated_length_mm, calibrated)
                && same_number(
                    d.calibration_relative_error,
                    calibrated.map(|n| (n - d.measured_mm).abs() / d.measured_mm),
                ),
            "inconsistent dimension geometry or calibration",
            &d.id,
        )?;
        if native {
            require(
                matches!(sources.get(source).map(|p|&p.shape),Some(Shape::Dimension{start,end,..}) if span.start==*start && span.end==*end)
                    && d.text_sources.len() == 1
                    && d.text_sources[0] == source
                    && d.start_witness.is_none()
                    && d.end_witness.is_none(),
                "invalid native dimension evidence",
                &d.id,
            )?;
        } else {
            require(
                is_line(source, sources)
                    && [&d.start_witness, &d.end_witness]
                        .into_iter()
                        .flatten()
                        .all(|w| w != source && is_line(w, sources)),
                "invalid dimension line or witness source",
                &d.id,
            )?;
            let two = d.start_witness.is_some()
                && d.end_witness.is_some()
                && d.start_witness != d.end_witness;
            require(
                has_segment(&sources[source].shape, &mut segment_budget, |a, b| {
                    a == span.start && b == span.end
                })?,
                "dimension baseline differs from cited segment",
                &d.id,
            )?;
            let dir = (span.end - span.start).unit();
            for (witness, anchor) in [(&d.start_witness, span.start), (&d.end_witness, span.end)] {
                if let Some(id) = witness {
                    let found =
                        has_segment(&sources[id.as_str()].shape, &mut segment_budget, |a, b| {
                            let v = b - a;
                            v.norm() >= 3. - 1e-9
                                && v.unit().dot(dir).abs() <= 0.18 + 1e-9
                                && on_segment(anchor, a, b).0.distance(anchor) <= 4. + 1e-9
                        })?;
                    require(found, "dimension witness misses its finite endpoint", &d.id)?;
                }
            }
            require(
                match d.evidence {
                    DimensionEvidence::TwoFiniteWitnesses => two,
                    DimensionEvidence::DimensionLayer => {
                        !two && sources[source].layer_kind() == LayerKind::Dimension
                    }
                    _ => false,
                },
                "unsupported dimension evidence",
                &d.id,
            )?;
        }
    }
    Ok(())
}

fn grids(page: &PageScan, sources: &Sources<'_>) -> Result<(), String> {
    let mut axes = HashMap::new();
    for a in &page.semantics.grid_axes {
        require(
            !a.id.is_empty()
                && axes.insert(a.id.as_str(), a).is_none()
                && a.requires_review
                && finite_span(&a.line)
                && a.orientation != AxisOrientation::Aligned
                && !a.line_sources.is_empty()
                && a.line_sources.iter().all(|s| is_line(s, sources))
                && (a.explicit_layer || a.bubble_source.is_some()),
            "invalid grid axis",
            &a.id,
        )?;
        let label_valid = match (&a.label, &a.label_source) {
            (Some(label), Some(id)) => {
                matches!(sources.get(id.as_str()).map(|p| &p.shape),Some(Shape::Text{value,..}) if value.trim()==label)
            }
            (None, None) => a.bubble_source.is_none(),
            _ => false,
        };
        require(
            label_valid
                && a.bubble_source.as_deref().is_none_or(|id| {
                    matches!(
                        sources.get(id).map(|p| &p.shape),
                        Some(
                            Shape::Polyline {
                                closed: true,
                                filled: false,
                                ..
                            } | Shape::Arc { .. }
                        )
                    )
                }),
            "invalid grid label or bubble",
            &a.id,
        )?;
        if a.explicit_layer {
            require(
                a.line_sources
                    .iter()
                    .any(|s| sources[s.as_str()].layer_kind() == LayerKind::Grid),
                "missing grid layer evidence",
                &a.id,
            )?;
        }
    }
    let mut ids = HashSet::new();
    for b in &page.semantics.grid_bays {
        let (Some(a), Some(z)) = (
            axes.get(b.axis_ids[0].as_str()),
            axes.get(b.axis_ids[1].as_str()),
        ) else {
            return Err(format!("unresolved grid bay axes {}", b.id));
        };
        let coord = |a: &GridAxis| {
            let p = (a.line.start + a.line.end) * 0.5;
            if a.orientation == AxisOrientation::Vertical {
                p.x
            } else {
                p.y
            }
        };
        let expected: HashSet<_> = a.line_sources.iter().chain(&z.line_sources).collect();
        require(
            !b.id.is_empty()
                && ids.insert(b.id.as_str())
                && b.requires_review
                && b.orientation == a.orientation
                && b.orientation == z.orientation
                && positive(b.drawing_distance)
                && near(b.drawing_distance, coord(z) - coord(a))
                && same_number(
                    b.distance_mm,
                    page.units_to_millimeters.map(|s| s * b.drawing_distance),
                )
                && b.source_ids.iter().collect::<HashSet<_>>() == expected,
            "invalid grid bay distances or sources",
            &b.id,
        )?;
    }
    Ok(())
}

fn routing(page: &PageScan) -> Result<(), String> {
    let walls: HashMap<_, _> = page.walls.iter().map(|w| (w.id.as_str(), w)).collect();
    let openings: HashMap<_, _> = page.openings.iter().map(|o| (o.id.as_str(), o)).collect();
    let objects: HashMap<_, _> = page.objects.iter().map(|o| (o.id.as_str(), o)).collect();
    let rooms: HashSet<_> = page.rooms.iter().map(|r| r.id.as_str()).collect();
    let mut links = HashMap::new();
    for c in &page.room_connections {
        let endpoints: HashSet<_> = c.room_ids.iter().map(String::as_str).collect();
        require(
            openings
                .get(c.opening_id.as_str())
                .is_some_and(|o| o.kind == OpeningKind::Door)
                && endpoints.len() == c.room_ids.len()
                && endpoints.is_subset(&rooms)
                && links.insert(c.opening_id.as_str(), endpoints).is_none(),
            "invalid observed door adjacency",
            &c.opening_id,
        )?;
    }
    let mut items = BTreeSet::new();
    for r in &page.semantics.routing {
        let id = r.entity_id.as_str();
        let span = |a: Point, b: Point| r.span.as_ref().is_some_and(|s| s.start == a && s.end == b);
        let upstream = match r.kind {
            RoutingKind::WallBarrierCandidate => walls.get(id).is_some_and(|w| {
                r.source_ids == w.source_ids
                    && r.room_ids.is_empty()
                    && r.curve == w.curve
                    && if w.curve.is_some() {
                        r.span.is_none()
                    } else {
                        span(w.start, w.end)
                    }
            }),
            RoutingKind::DoorPassageCandidate | RoutingKind::WindowCandidate => {
                openings.get(id).is_some_and(|o| {
                    let actual: HashSet<_> = r.room_ids.iter().map(String::as_str).collect();
                    (r.kind == RoutingKind::DoorPassageCandidate) == (o.kind == OpeningKind::Door)
                        && r.source_ids == o.source_ids
                        && r.curve.is_none()
                        && span(o.start, o.end)
                        && actual.len() == r.room_ids.len()
                        && links
                            .get(id)
                            .map_or(actual.is_empty(), |expected| *expected == actual)
                })
            }
            RoutingKind::ObjectForReview => objects.get(id).is_some_and(|o| {
                r.source_ids == o.source_ids
                    && r.curve.is_none()
                    && r.span.is_none()
                    && r.room_ids.iter().eq(o.room_id.iter())
            }),
        };
        require(
            !r.usable_for_routing && r.requires_review && upstream && items.insert((r.kind, id)),
            "invalid review-only routing projection",
            id,
        )?;
    }
    require(
        items.len() == walls.len() + openings.len() + objects.len(),
        "incomplete routing projection",
        "page",
    )?;

    let mut cluster_ids = HashSet::new();
    let mut assigned = HashMap::new();
    let mut joined = HashSet::new();
    for c in &page.semantics.room_clusters {
        require(
            !c.id.is_empty()
                && cluster_ids.insert(&c.id)
                && c.requires_review
                && !c.room_ids.is_empty()
                && c.room_ids.iter().all(|id| {
                    rooms.contains(id.as_str())
                        && assigned.insert(id.as_str(), c.id.as_str()).is_none()
                }),
            "invalid cluster rooms",
            &c.id,
        )?;
    }
    require(
        assigned.len() == rooms.len(),
        "incomplete room partition",
        "page",
    )?;
    for c in &page.semantics.room_clusters {
        let mut adjacency = HashMap::<&str, Vec<&str>>::new();
        for id in &c.opening_ids {
            let Some(endpoints) = links.get(id.as_str()) else {
                return Err(format!("unresolved cluster opening {id}"));
            };
            require(
                joined.insert(id.as_str())
                    && endpoints.len() == 2
                    && endpoints
                        .iter()
                        .all(|r| assigned.get(*r) == Some(&c.id.as_str())),
                "invalid cluster passage",
                id,
            )?;
            let mut pair = endpoints.iter().copied();
            let a = pair.next().unwrap();
            let b = pair.next().unwrap();
            adjacency.entry(a).or_default().push(b);
            adjacency.entry(b).or_default().push(a);
        }
        let mut reached = HashSet::new();
        let mut pending = vec![c.room_ids[0].as_str()];
        while let Some(r) = pending.pop() {
            if reached.insert(r) {
                if let Some(next) = adjacency.get(r) {
                    pending.extend(next.iter().copied());
                }
            }
        }
        require(
            reached.len() == c.room_ids.len(),
            "cluster merges unconnected rooms",
            &c.id,
        )?;
    }
    require(
        joined.len() == links.values().filter(|s| s.len() == 2).count(),
        "incomplete witnessed cluster passages",
        "page",
    )?;
    Ok(())
}

pub fn check(page: &PageScan) -> Result<(), String> {
    let s = &page.semantics;
    let sources: Sources<'_> = page.primitives.iter().map(|p| (p.id.as_str(), p)).collect();
    let mut region_ids = HashSet::new();
    let mut sheets = 0;
    for r in &s.regions {
        let b = &r.bounds;
        require(
            !r.id.is_empty()
                && region_ids.insert(r.id.as_str())
                && b.min.finite()
                && b.max.finite()
                && b.min.x <= b.max.x
                && b.min.y <= b.max.y,
            "invalid region",
            &r.id,
        )?;
        if r.kind == RegionKind::Sheet {
            sheets += 1;
            require(
                !r.requires_review
                    && r.source_ids.is_empty()
                    && b.min == Point::default()
                    && b.max == Point::new(page.width, page.height),
                "invalid sheet region",
                &r.id,
            )?;
        } else {
            require(
                r.requires_review && references(&r.source_ids, &sources),
                "invalid region sources or review",
                &r.id,
            )?;
            if r.kind == RegionKind::MainFloorPlan {
                let walls = crate::placement::original_wall_observations(page);
                let observed = wall_envelope(walls).and_then(|envelope| {
                    authored_plan(
                        s.room_clusters.len(),
                        walls,
                        &page.rooms,
                        &page.primitives,
                        &envelope,
                    )
                });
                require(
                    observed.is_some_and(|(extent, ids)| {
                        extent.min == b.min && extent.max == b.max && ids == r.source_ids
                    }),
                    "unsubstantiated main floor plan",
                    &r.id,
                )?;
            }
        }
    }
    require(sheets == 1, "expected one sheet region", "page")?;
    dimensions(page, &sources)?;
    grids(page, &sources)?;
    for f in &s.title_fields {
        require(
            f.requires_review
                && !f.value.is_empty()
                && !f.source_ids.is_empty()
                && f.source_ids.iter().all(|id| is_text(id, &sources)),
            "invalid title field",
            &f.value,
        )?;
    }
    let mut note_ids = HashSet::new();
    for n in &s.annotation_blocks {
        require(
            !n.id.is_empty()
                && note_ids.insert(&n.id)
                && region_ids.contains(n.id.as_str())
                && n.requires_review
                && matches!(sources.get(n.heading_source.as_str()).map(|p|&p.shape),Some(Shape::Text{value,..}) if value==&n.heading)
                && n.items.iter().all(|i| is_text(&i.source_id, &sources)),
            "invalid note block",
            &n.id,
        )?;
    }
    routing(page)
}

#[cfg(test)]
mod tests {
    use super::*;
    fn sample() -> PageScan {
        let a = Point::new(10., 20.);
        let b = Point::new(110., 20.);
        let p = Page {
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
            units_to_mm: Some(1.),
            primitives: vec![
                Primitive {
                    layer_category_override: None,
                    stroke_width: None,
                    id: "wall-source".into(),
                    layer: Some("A-WALL".into()),
                    shape: Shape::Line { start: a, end: b },
                },
                Primitive {
                    layer_category_override: None,
                    stroke_width: None,
                    id: "dim-source".into(),
                    layer: None,
                    shape: Shape::Dimension {
                        start: a,
                        end: b,
                        at: a,
                        value: "100 mm".into(),
                        measurement: Some(100.),
                    },
                },
            ],
        };
        let walls = vec![Wall {
            id: "wall".into(),
            page: 1,
            start: a,
            end: b,
            thickness: 0.,
            body: BodyEvidence::ExplicitCenterline,
            curve: None,
            source_ids: vec!["wall-source".into()],
            requires_review: true,
        }];
        let rooms = vec![Room {
            boundary_evidence: None,
            id: "room".into(),
            polygon: vec![],
            area: 0.,
            wall_ids: vec![],
            label: None,
            label_source_id: None,
            requires_review: true,
        }];
        let semantics = interpret(&p, &walls, &rooms, &[], &[], &[], p.units_to_mm);
        PageScan {
            finish_visibility: None,
            pdf_input: None,
            page_number: 1,
            exterior_enclosures: None,
            exterior_connections: None,
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
            width: p.width,
            height: p.height,
            units_to_millimeters: p.units_to_mm,
            scale_source: Some("InputModelUnits".into()),
            calibration_source_ids: vec![],
            primitives: p.primitives,
            walls,
            placement_decisions: None,
            withheld_walls: None,
            rooms,
            semantics,
            openings: vec![],
            window_panes: vec![],
            nodes: vec![],
            edges: vec![],
            objects: vec![],
            object_groups: vec![],
            room_connections: vec![],
            drawing_features: vec![],
            room_annotations: vec![],
            warnings: vec![],
        }
    }
    #[test]
    fn rejects_tampered_authority_and_barrier_geometry() {
        let good = sample();
        assert!(check(&good).is_ok());
        let mut p = good.clone();
        p.semantics.routing[0].usable_for_routing = true;
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.routing[0].requires_review = false;
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.routing[0].span.as_mut().unwrap().start.x += 1.;
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.routing[0].entity_id = "missing".into();
        assert!(check(&p).is_err());
        let mut p = good;
        p.semantics.routing.clear();
        assert!(check(&p).is_err());
    }
    #[test]
    fn rejects_mismatched_dimension_measurements_and_sources() {
        let good = sample();
        let mut p = good.clone();
        p.semantics.dimensions[0].observed_mm_per_unit = Some(f64::INFINITY);
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.dimensions[0].calibration_relative_error = Some(0.25);
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.native_dimensions[0].reported_model_units = Some(120.);
        assert!(check(&p).is_err());
        let mut p = good;
        p.semantics.dimensions[0].text_sources = vec!["wall-source".into()];
        assert!(check(&p).is_err());
    }
    #[test]
    fn rejects_orphaned_regions_invalid_bounds_and_clusters() {
        let good = sample();
        let mut p = good.clone();
        p.semantics.regions[1].source_ids = vec!["missing".into()];
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.regions[1].bounds.max.x = f64::NAN;
        assert!(check(&p).is_err());
        let mut p = good.clone();
        p.semantics.room_clusters[0].room_ids = vec!["missing".into()];
        assert!(check(&p).is_err());
        let mut p = good;
        p.semantics.room_clusters[0].opening_ids = vec!["missing".into()];
        assert!(check(&p).is_err());
    }
    #[test]
    fn agrees_with_projected_pdf_scale_and_rejects_foreign_witness() {
        let mut primitives = Vec::new();
        for (i, y) in [50., 120.].into_iter().enumerate() {
            let at = |x, dy| Point::new(x, y + dy);
            for (name, start, end) in [
                ("baseline", at(10., 0.), at(110., 0.)),
                ("left", at(10., -6.), at(10., 6.)),
                ("right", at(110., -6.), at(110., 6.)),
            ] {
                primitives.push(Primitive {
                    layer_category_override: None,
                    stroke_width: None,
                    id: format!("{name}{i}"),
                    layer: Some("A-DIM".into()),
                    shape: Shape::Line { start, end },
                });
            }
            primitives.push(Primitive {
                layer_category_override: None,
                stroke_width: None,
                id: format!("label{i}"),
                layer: Some("A-DIM".into()),
                shape: Shape::Text {
                    at: at(60., 3.),
                    value: "200 mm".into(),
                    height: 5.,
                },
            });
        }
        for (i, x) in [20., 90.].into_iter().enumerate() {
            primitives.push(Primitive {
                layer_category_override: None,
                stroke_width: None,
                id: format!("grid{i}"),
                layer: Some("A-GRID".into()),
                shape: Shape::Line {
                    start: Point::new(x, 0.),
                    end: Point::new(x, 180.),
                },
            });
        }
        let page = Page {
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
            primitives,
        };
        let doc = Document {
            pages: vec![page],
            warnings: vec![],
        };
        let mut p = crate::pipeline::scan_document(doc, &Options::default(), None, true)
            .unwrap()
            .pages
            .remove(0);
        assert_eq!(p.units_to_millimeters, Some(2.));
        assert_eq!(p.semantics.grid_bays[0].distance_mm, Some(140.));
        check(&p).unwrap();
        p.semantics.dimensions[0].start_witness = Some("left1".into());
        assert!(check(&p).is_err());
    }
}
