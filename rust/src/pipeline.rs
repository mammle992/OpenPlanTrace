//! The scanner owns interpretation. Export is a projection of this result.
use crate::{input, model::*, openings, stage_profile, topology, walls};
use std::{
    collections::{BTreeSet, HashMap, HashSet},
    path::Path,
};

pub fn scan_file(path: &Path, opts: &Options, scale: Option<f64>) -> Result<Scan, String> {
    scan_file_with_layers(path, opts, scale, &[])
}
pub fn scan_file_with_layers(
    path: &Path,
    opts: &Options,
    scale: Option<f64>,
    rules: &[crate::layers::LayerCategoryOverride],
) -> Result<Scan, String> {
    scan_file_with_profiles(path, opts, scale, rules, &[])
}
pub fn scan_file_with_profiles(
    path: &Path,
    opts: &Options,
    scale: Option<f64>,
    rules: &[crate::layers::LayerCategoryOverride],
    profiles: &[crate::object_labels::Profile],
) -> Result<Scan, String> {
    scan_file_with_profiles_on_page(path, opts, scale, rules, profiles, None)
}

/// Scan a source page without interpreting the other sheets in the drawing.
pub fn scan_file_with_profiles_on_page(
    path: &Path,
    opts: &Options,
    scale: Option<f64>,
    rules: &[crate::layers::LayerCategoryOverride],
    profiles: &[crate::object_labels::Profile],
    selected: Option<u32>,
) -> Result<Scan, String> {
    let _profile = stage_profile::scope("scan_pipeline");
    let doc = input::load_selected(path, selected)?;
    let pdf = path
        .extension()
        .is_some_and(|e| e.eq_ignore_ascii_case("pdf"));
    let mut scan = scan_document_with_layers(doc, opts, scale, pdf, rules)?;
    let _stage = stage_profile::scope("object_label_profiles");
    crate::object_labels::apply(&mut scan, if pdf { "PDF" } else { "DXF" }, profiles)?;
    Ok(scan)
}
fn valid_point(p: Point) -> bool {
    p.finite() && p.x.abs() <= 1e12 && p.y.abs() <= 1e12
}
fn validate_input(page: &Page) -> Result<(), String> {
    if page.number == 0
        || !page.width.is_finite()
        || !page.height.is_finite()
        || page.width <= 0.
        || page.height <= 0.
        || page.width > 1e12
        || page.height > 1e12
    {
        return Err("invalid page dimensions or number".into());
    }
    if page.primitives.len() > 500_000 {
        return Err("page primitive limit exceeded".into());
    }
    if page.units_to_mm.is_some_and(|v| !v.is_finite() || v <= 0.) {
        return Err("invalid input unit scale".into());
    }
    validate_sources(
        page.number,
        &page.primitives,
        &page.material_tiles,
        page.placement_paint_ids.as_deref(),
    )
}
pub(crate) fn validate_sources(
    number: u32,
    primitives: &[Primitive],
    tiles: &[crate::placement::insulation::MaterialTile],
    placement_paint_ids: Option<&[String]>,
) -> Result<(), String> {
    let paint = placement_paint_ids.unwrap_or_default();
    if paint.len() > primitives.len() || paint.windows(2).any(|pair| pair[0] >= pair[1]) {
        return Err("invalid ordered PDF placement paint sources".into());
    }
    let mut unseen: std::collections::HashSet<_> = paint.iter().map(String::as_str).collect();
    let mut ids = BTreeSet::new();
    for p in primitives {
        if p.id.is_empty() || !ids.insert(&p.id) {
            return Err(format!("duplicate or empty source ID on page {number}"));
        }
        if let Some(width) = p.stroke_width
            && (!width.is_finite()
                || width <= 0.
                || !matches!(
                    &p.shape,
                    Shape::Line { .. } | Shape::Arc { .. } | Shape::Polyline { filled: false, .. }
                ))
        {
            return Err(format!("invalid stroke width at source {}", p.id));
        }
        let valid = match &p.shape {
            Shape::Line { start, end } => valid_point(*start) && valid_point(*end),
            Shape::Polyline { points, .. } => {
                points.len() >= 2 && points.iter().copied().all(valid_point)
            }
            Shape::Arc {
                center,
                radius,
                start_angle,
                sweep,
            } => {
                valid_point(*center)
                    && radius.is_finite()
                    && *radius > 0.
                    && *radius <= 1e12
                    && start_angle.is_finite()
                    && sweep.is_finite()
                    && sweep.abs() <= std::f64::consts::TAU + 1e-6
            }
            Shape::Text { at, height, .. } => {
                valid_point(*at) && height.is_finite() && *height >= 0.
            }
            Shape::Image { min, max } => {
                valid_point(*min) && valid_point(*max) && min.x <= max.x && min.y <= max.y
            }
            Shape::Insert { at, .. } => valid_point(*at),
            Shape::Dimension {
                start,
                end,
                at,
                measurement,
                ..
            } => {
                valid_point(*start)
                    && valid_point(*end)
                    && valid_point(*at)
                    && measurement.is_none_or(|v| v.is_finite() && v >= 0.)
            }
        };
        if !valid {
            return Err(format!("invalid geometry at source {}", p.id));
        }
        if !unseen.is_empty() && unseen.remove(p.id.as_str()) {
            if crate::placement::painted_rectangle(&p.shape).is_none() {
                return Err("placement paint must reference a filled rectangle".into());
            }
        }
    }
    if !unseen.is_empty() {
        return Err("missing PDF placement paint source".into());
    }
    for tile in tiles {
        if tile.id.is_empty() || !ids.insert(&tile.id) {
            return Err(format!(
                "duplicate or empty material source ID on page {number}"
            ));
        }
    }
    crate::placement::insulation::validate(tiles)?;
    Ok(())
}

/// The native inventory certifies visibility; validate its finite convex source
/// geometry independently before it can support bounded placement material.
pub(crate) fn validate_polygon_sources(
    primitives: &[Primitive],
    ids: &[String],
) -> Result<(), String> {
    if ids.len() > primitives.len() || ids.windows(2).any(|p| p[0] >= p[1]) {
        return Err("invalid ordered polygon paint inventory".into());
    }
    let mut unseen: HashSet<_> = ids.iter().map(String::as_str).collect();
    for p in primitives {
        if !unseen.remove(p.id.as_str()) {
            continue;
        }
        let Shape::Polyline {
            points,
            closed: true,
            filled: true,
        } = &p.shape
        else {
            return Err("polygon paint source must be a closed filled polygon".into());
        };
        let points = if points.first() == points.last() {
            &points[..points.len() - 1]
        } else {
            points.as_slice()
        };
        if !(3..=64).contains(&points.len())
            || points.iter().any(|p| !p.finite())
            || crate::placement::painted_rectangle(&p.shape).is_some()
        {
            return Err("invalid polygon paint source".into());
        }
        let origin = points[0];
        let scale = points
            .iter()
            .map(|p| (*p - origin).norm())
            .fold(1., f64::max);
        let eps = 64. * f64::EPSILON * scale * scale;
        let area: f64 = points
            .iter()
            .zip(points.iter().cycle().skip(1))
            .map(|(a, b)| (*a - origin).cross(*b - origin))
            .sum();
        if area.abs() <= eps
            || !points
                .iter()
                .zip(points.iter().cycle().skip(1))
                .all(|(&a, &b)| {
                    let e = b - a;
                    e.norm() > 64. * f64::EPSILON * scale
                        && points
                            .iter()
                            .all(|&v| area.signum() * e.cross(v - a) >= -eps)
                })
        {
            return Err("polygon paint source must be finite and convex".into());
        }
    }
    if !unseen.is_empty() {
        return Err("missing polygon paint source".into());
    }
    Ok(())
}
fn paper_scale(page: &Page) -> Option<f64> {
    let mut found = BTreeSet::new();
    for p in &page.primitives {
        if let Shape::Text { value, .. } = &p.shape {
            let s = value.to_uppercase();
            if !["SCALE", "MÅLESTOKK", "SKALA"]
                .iter()
                .any(|word| s.contains(word))
            {
                continue;
            }
            let compact: String = s.chars().filter(|c| !c.is_whitespace()).collect();
            for (index, _) in compact.match_indices("1:") {
                if index > 0 && compact.as_bytes()[index - 1].is_ascii_digit() {
                    continue;
                }
                let n: String = compact[index + 2..]
                    .chars()
                    .take_while(|c| c.is_ascii_digit() || *c == '.')
                    .collect();
                if let Ok(v) = n.parse::<f64>() {
                    if v > 0. && v.is_finite() {
                        found.insert(v.to_bits());
                    }
                }
            }
        }
    }
    if found.len() == 1 {
        found.first().map(|&v| f64::from_bits(v) * 25.4 / 72.)
    } else {
        None
    }
}
fn belongs(open: &Opening, room: &Room, tol: f64) -> bool {
    if !room.wall_ids.iter().any(|w| open.host_wall_ids.contains(w)) {
        return false;
    }
    let v = (open.end - open.start).unit();
    room.polygon
        .iter()
        .zip(room.polygon.iter().cycle().skip(1))
        .any(|(&a, &b)| {
            if (b - a).unit().dot(v).abs() < 0.995
                || (a - open.start).cross(v).abs() > tol
                || (b - open.start).cross(v).abs() > tol
            {
                return false;
            }
            let x = (a - open.start).dot(v);
            let y = (b - open.start).dot(v);
            x.min(y).max(0.) + 1e-6 < x.max(y).min(open.start.distance(open.end))
        })
}
pub fn scan_document(
    doc: Document,
    opts: &Options,
    scale: Option<f64>,
    pdf: bool,
) -> Result<Scan, String> {
    scan_document_with_layers(doc, opts, scale, pdf, &[])
}

/// Resolve each distinct source layer once, retaining its original name and geometry.
struct Assignments<'a> {
    rules: &'a [crate::layers::LayerCategoryOverride],
    format: &'a str,
    cache: HashMap<String, Option<crate::layers::LayerCategory>>,
}
impl Assignments<'_> {
    fn get(&mut self, p: &Primitive) -> Option<crate::layers::LayerCategory> {
        let name = p.layer_name();
        if let Some(value) = self.cache.get(name) {
            return *value;
        }
        let value =
            crate::layers::first_override(self.rules, name, self.format).map(|rule| rule.category);
        self.cache.insert(name.to_owned(), value);
        value
    }
}
pub(crate) fn validate_layer_profile(scan: &Scan) -> Result<(), String> {
    let primitives = scan.pages.iter().flat_map(|p| &p.primitives);
    if let Some(profile) = &scan.layer_profile {
        if profile.overrides.is_empty()
            || !matches!(profile.source_format.as_str(), "DXF" | "PDF")
            || profile
                .overrides
                .iter()
                .any(|r| r.pattern.trim().is_empty())
        {
            return Err("invalid applied layer profile".into());
        }
        let mut lookup = Assignments {
            rules: &profile.overrides,
            format: &profile.source_format,
            cache: HashMap::new(),
        };
        for p in primitives {
            if lookup.get(p) != p.layer_category_override {
                return Err(format!("layer override does not match source {}", p.id));
            }
        }
    } else if primitives
        .into_iter()
        .any(|p| p.layer_category_override.is_some())
    {
        return Err("primitive layer override requires an applied layer profile".into());
    }
    Ok(())
}
pub fn scan_document_with_layers(
    doc: Document,
    opts: &Options,
    scale: Option<f64>,
    pdf: bool,
    rules: &[crate::layers::LayerCategoryOverride],
) -> Result<Scan, String> {
    if scale.is_some_and(|v| v <= 0. || !v.is_finite()) {
        return Err("units-to-mm must be finite and positive".into());
    }
    if ![
        opts.min_wall_length,
        opts.min_wall_fragment_length,
        opts.min_wall_pair_separation,
        opts.max_wall_pair_separation,
        opts.snap_tolerance,
        opts.min_opening_gap,
        opts.max_opening_gap,
        opts.min_room_area,
    ]
    .iter()
    .all(|v| v.is_finite() && *v > 0.)
        || !(0.0..=1.).contains(&opts.min_wall_pair_overlap_ratio)
        || opts.min_opening_gap > opts.max_opening_gap
        || opts.min_wall_pair_separation > opts.max_wall_pair_separation
        || opts.max_wall_seeds == 0
    {
        return Err("invalid scanner limits".into());
    }
    if doc.pages.is_empty() {
        return Err("input contains no pages".into());
    }
    let format = if pdf { "PDF" } else { "DXF" };
    let mut lookup = Assignments {
        rules,
        format,
        cache: HashMap::new(),
    };
    let mut scan = Scan {
        visual_classification: None,
        schema_version: "openplantrace.rust.scan.v45".into(),
        object_label_profile: None,
        layer_profile: (!rules.is_empty()).then(|| AppliedLayerProfile {
            source_format: format.to_owned(),
            overrides: rules.to_vec(),
        }),
        engine_version: env!("CARGO_PKG_VERSION").into(),
        pages: Vec::new(),
        warnings: doc.warnings,
    };
    let mut numbers = BTreeSet::new();
    for mut page in doc.pages {
        let _profile = stage_profile::page("scan_page", page.number);
        let mut stage = stage_profile::scope("source_setup_and_layer_assignment");
        if !numbers.insert(page.number) {
            return Err("duplicate page number".into());
        }
        validate_input(&page)?;
        for p in &mut page.primitives {
            p.layer_category_override = if rules.is_empty() {
                None
            } else {
                lookup.get(p)
            };
        }
        // Interpret original vectors independently so new raster faces cannot renumber,
        // re-pair, or displace any existing physical wall or doorway.
        let raster_ids: std::collections::HashSet<_> = page
            .pdf_input
            .as_ref()
            .map(|info| info.raster_source_ids.iter().cloned().collect())
            .unwrap_or_default();
        let mut raster_sources = Vec::new();
        page.primitives.retain(|p| {
            if raster_ids.contains(&p.id) {
                raster_sources.push(p.clone());
                false
            } else {
                true
            }
        });
        let mut warnings = Vec::new();
        let (mut units, mut scale_source) = if let Some(s) = scale {
            (Some(s), Some("ExplicitOverride".into()))
        } else if let Some(s) = page.units_to_mm {
            (Some(s), Some("InputModelUnits".into()))
        } else {
            (None, None)
        };
        stage.next("room_label_observation");
        let mut printed_areas = crate::room_labels::observe(&page.primitives)?;
        stage.next("vector_wall_observation");
        let mut observations = walls::detect(&page, opts)?;
        stage.next("painted_floor_and_vector_wall_selection");
        let painted_floors =
            crate::room_fill::measured_floors(&page.primitives, &printed_areas.records, units)?;
        let floor_fills = crate::room_fill::separate(&painted_floors, &mut observations);
        let (mut walls, mut withheld_walls) = walls::select(&page, observations, opts)?;
        withheld_walls.extend(floor_fills);
        stage.next("opening_detection");
        let openings = openings::detect(&page, &walls, opts)?;
        // An accepted swing can identify a narrow panel that also produced a paired-face wall.
        // The doorway stage protects every selected opening host before returning its witnesses.
        stage.next("door_leaf_filter_and_jamb_recovery");
        let mut leaves = openings::door_leaf_walls(&page, &walls, &openings, opts)?;
        walls.retain(|wall| {
            if let Some(witness_source_ids) = leaves.remove(&wall.id) {
                withheld_walls.push(WithheldWall {
                    furniture_outline: None,
                    wall: wall.clone(),
                    reason: WallSelectionReason::DoorLeaf,
                    witness_source_ids,
                });
                return false;
            }
            true
        });
        crate::placement::jamb::recover(&page, &mut walls, &openings, opts)?;
        stage.next("topology");
        let mut graph = topology::build(&page, &walls, &openings, opts)?;
        stage.next("room_boundaries_and_labels");
        topology::perimeters::append(
            &page,
            &painted_floors,
            &mut printed_areas.records,
            &walls,
            &openings,
            &mut graph.rooms,
            opts,
        )?;
        let room_annotations = printed_areas.bind(&mut graph.rooms)?;
        stage.next("object_detection");
        let (mut objects, object_groups) = crate::objects::detect_selected(
            &page,
            &graph.rooms,
            &walls,
            &openings,
            &withheld_walls,
            &Options::default(),
        )?;
        stage.next("room_door_incidence");
        let room_connections: Vec<_> = openings
            .iter()
            .filter(|p| p.kind == OpeningKind::Door)
            .map(|p| Connection {
                opening_id: p.id.clone(),
                room_ids: graph
                    .rooms
                    .iter()
                    .filter(|r| belongs(p, r, opts.snap_tolerance))
                    .map(|r| r.id.clone())
                    .collect(),
            })
            .collect();
        // Re-paired source ink is placed through its measured graph. The existing
        // room topology and opening hosts retain their original interpretation.
        stage.next("stroke_wall_recovery");
        crate::placement::stroked::recover(&page, &mut walls, &openings, &withheld_walls, opts)?;
        stage.next("raster_wall_observation");
        page.primitives.extend(raster_sources);
        crate::placement::raster::observe(&page, &mut walls, opts);
        stage.next("object_geometry");
        let original_objects = objects.len();
        if let Err(error) =
            crate::objects::append_geometry(&page, &graph.rooms, &walls, &openings, &mut objects)
        {
            objects.truncate(original_objects);
            warnings.push(format!("Object geometry withheld: {error}"));
        }
        stage.next("semantic_interpretation");
        let mut semantics = crate::semantics::interpret(
            &page,
            &walls,
            &graph.rooms,
            &openings,
            &objects,
            &room_connections,
            units,
        );
        stage.next("calibration_and_page_record");
        let mut calibration_source_ids = Vec::new();
        if units.is_none() && pdf {
            if let Some((k, source_ids)) =
                crate::calibration::dimensions(&page.primitives, &semantics)
            {
                units = Some(k);
                scale_source = Some("AgreeingFiniteDimensions".into());
                calibration_source_ids = source_ids;
                crate::calibration::project_measurements(&mut semantics, k);
            } else if paper_scale(&page).is_some() {
                warnings
                    .push("Printed scale lacks unambiguous finite dimension corroboration.".into());
            }
        }
        if units.is_none() {
            warnings.push("Model calibration unknown; geometry remains in drawing units.".into());
        }
        let drawing_features = page
            .primitives
            .iter()
            .filter_map(|p| {
                let category = p.layer_kind();
                if matches!(
                    category,
                    LayerKind::Dimension
                        | LayerKind::Grid
                        | LayerKind::Room
                        | LayerKind::Annotation
                ) {
                    Some(Feature {
                        source_id: p.id.clone(),
                        category,
                        label: if let Shape::Text { value, .. } = &p.shape {
                            Some(value.clone())
                        } else {
                            None
                        },
                    })
                } else {
                    None
                }
            })
            .collect();
        if walls.is_empty() {
            warnings.push("No physical wall selected.".into())
        }
        if page
            .primitives
            .iter()
            .any(|p| matches!(p.shape, Shape::Image { .. }))
        {
            warnings.push(
                "Raster observations retain pixel evidence; ambiguous spans require review.".into(),
            )
        }
        let mut scanned = PageScan {
            finish_visibility: None,
            pdf_input: page.pdf_input,
            page_number: page.number,
            exterior_enclosures: None,
            exterior_connections: None,
            exterior_jamb_backings: vec![],
            placement_paint_ids: Some(page.placement_paint_ids.unwrap_or_else(|| {
                let mut ids: Vec<_> = page
                    .primitives
                    .iter()
                    .filter(|p| crate::placement::painted_rectangle(&p.shape).is_some())
                    .map(|p| p.id.clone())
                    .collect();
                ids.sort();
                ids
            })),
            material_tiles: page.material_tiles,
            placement_polygon_paint_ids: Some(page.placement_polygon_paint_ids.unwrap_or_default()),
            placement_paint_remainders: page.placement_paint_remainders,
            white_nested_walls: page.white_nested_walls,
            terminal_stock: page.terminal_stock,
            local_stock: page.local_stock,
            stepped_stock: page.stepped_stock,
            capped_stock: page.capped_stock,
            recessed_stock: page.recessed_stock,
            masked_junction_stock: page.masked_junction_stock,
            pocket_stock: page.pocket_stock,
            placement_contact_paint: page.placement_contact_paint,
            width: page.width,
            height: page.height,
            units_to_millimeters: units,
            scale_source,
            calibration_source_ids,
            primitives: page.primitives,
            walls,
            placement_decisions: None,
            withheld_walls: Some(withheld_walls),
            openings,
            window_panes: vec![],
            nodes: graph.nodes,
            edges: graph.edges,
            rooms: graph.rooms,
            objects,
            object_groups,
            room_connections,
            drawing_features,
            semantics,
            room_annotations,
            warnings,
        };
        stage.next("physical_jamb_refinement");
        crate::openings::refine_physical_jambs(&mut scanned)?;
        stage.next("placement_base_selection");
        let mut decisions = crate::placement::select_preview19(&scanned)?;
        stage.next("finish_evidence_requests");
        let requirements = crate::placement::finish_requirements(&scanned, &decisions)?;
        stage.next("finish_visibility_replay");
        scanned.finish_visibility = Some(page.finish_history.map_or_else(
            crate::finish_visibility::FinishVisibility::unavailable,
            |history| history.certify(scanned.page_number, requirements),
        ));
        stage.next("finish_material_selection");
        crate::placement::select_visible_finishes(&scanned, &mut decisions)?;
        stage.next("local_raster_placement");
        crate::placement::select_additional_material(&scanned, &mut decisions);
        stage.next("raster_companion_placement");
        crate::placement::select_raster_companions(&scanned, &mut decisions);
        stage.next("raster_offset_pair_placement");
        crate::placement::select_raster_offset_pairs(&scanned, &mut decisions);
        if crate::placement::authored_scope::supports(&scanned) {
            stage.next("exterior_core_placement");
            crate::placement::append_exterior_cores(&mut scanned, &mut decisions);
            stage.next("exterior_step_placement");
            crate::placement::append_exterior_steps(&mut scanned, &mut decisions);
            scanned.placement_decisions = Some(decisions);
            stage.next("exterior_window_frames");
            crate::openings::append_exterior_window_frames(&mut scanned)?;
            stage.next("vector_wall_assemblies");
            let mut decisions = scanned
                .placement_decisions
                .take()
                .expect("completed placement selection");
            crate::placement::apply_vector_assemblies(&mut scanned, &mut decisions);
            scanned.placement_decisions = Some(decisions);
            stage.next("interior_gap_doors");
            crate::openings::append_authored_interior_gaps(&mut scanned)?;
        } else {
            scanned.placement_decisions = Some(decisions);
        }
        stage.next("staggered_painted_cores");
        let mut decisions = scanned
            .placement_decisions
            .take()
            .expect("completed placement selection");
        crate::placement::select_staggered_painted_cores(&scanned, &mut decisions);
        stage.next("bounded_wall_cores");
        crate::placement::select_bounded_wall_cores(&scanned, &mut decisions);
        stage.next("source_bounded_wall_runs");
        crate::placement::select_painted_runs(&scanned, &mut decisions);
        stage.next("interior_mitre_stock");
        crate::placement::select_interior_mitre(&scanned, &mut decisions);
        stage.next("painted_wall_remainders");
        crate::placement::select_remainders(&scanned, &mut decisions);
        scanned.placement_decisions = Some(decisions);
        stage.next("exterior_enclosures");
        scanned.exterior_enclosures = Some(crate::placement::enclosures::build(&scanned));
        stage.next("physical_window_units");
        crate::windows::separate_panes(&mut scanned)?;
        stage.next("authored_exterior_piers");
        crate::placement::append_exterior_piers(&mut scanned, true)?;
        stage.next("authored_exterior_swing_casings");
        crate::openings::exterior_swing::append(&mut scanned)?;
        stage.next("source_bounded_wall_context");
        crate::placement::apply_bounded_wall_context(&mut scanned)?;
        stage.next("authored_exterior_overhead_frames");
        crate::openings::overhead_frame::append(&mut scanned)?;
        stage.next("authored_exterior_rough_glazing");
        crate::windows::rough_glazing::append(&mut scanned)?;
        stage.next("double_inset_wall_cores");
        crate::placement::apply_double_inset_wall_cores(&mut scanned)?;
        stage.next("bounded_raster_courses");
        crate::placement::apply_bounded_raster_courses(&mut scanned)?;
        stage.next("layerless_overhead_projection");
        crate::openings::layerless_overhead::append(&mut scanned)?;
        stage.next("partly_visible_overhead_stock");
        crate::openings::layerless_overhead::append_terminal_stock(&mut scanned)?;
        stage.next("layerless_dashed_door");
        crate::openings::dashed_swing::append(&mut scanned)?;
        stage.next("coextensive_wall_assemblies");
        crate::placement::cavity_material::coextensive::apply(&mut scanned);
        stage.next("exterior_wall_linings");
        crate::placement::cavity_material::exterior_lining::apply(&mut scanned);
        stage.next("stepped_exterior_facade");
        crate::placement::stepped_facade::append(&mut scanned)?;
        stage.next("capped_exterior_facade");
        crate::placement::capped_facade::append(&mut scanned)?;
        stage.next("recessed_wall_stock");
        crate::placement::recessed_stock::append(&mut scanned)?;
        stage.next("pocket_wall_stock");
        crate::placement::pocket_stock::append(&mut scanned)?;
        stage.next("masked_junction_stock");
        crate::placement::masked_junction_stock::append(&mut scanned)?;
        stage.next("exterior_connections");
        scanned.exterior_connections = Some(crate::placement::connections::build(&scanned)?);
        stage.next("raster_hinged_doors");
        crate::placement::raster_hinged_doors::append(&mut scanned)?;
        stage.next("raster_jamb_stocks");
        let placed_jambs = crate::placement::raster_jamb_stocks::append(&mut scanned)?;
        stage.next("raster_narrow_course");
        let placed_narrow_course = crate::placement::raster_narrow_course::append(&mut scanned)?;
        stage.next("raster_section_course");
        let placed_section_course =
            crate::placement::raster_narrow_course::append_section_courses(&mut scanned)?;
        stage.next("raster_jamb_return");
        let placed_jamb_return = crate::placement::raster_jamb_return::apply(&mut scanned)?;
        stage.next("raster_aligned_courses");
        let placed_aligned_courses = crate::placement::raster_aligned_course::append(&mut scanned)?;
        stage.next("raster_corner_courses");
        let placed_corner_courses = crate::placement::raster_corner_course::append(&mut scanned)?;
        if placed_jambs
            || placed_narrow_course
            || placed_section_course
            || placed_jamb_return
            || placed_aligned_courses
            || placed_corner_courses
        {
            scanned.exterior_connections = Some(crate::placement::connections::build(&scanned)?);
        }
        stage.next("exterior_jamb_backing");
        scanned.exterior_jamb_backings =
            crate::placement::cavity_material::jamb_backing::derive(&scanned);
        stage.next("raster_face_continuation_observations");
        crate::placement::raster_continuations::append(&mut scanned)?;
        scan.pages.push(scanned);
    }
    Ok(scan)
}
