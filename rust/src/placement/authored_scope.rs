//! Allow authored PDF assemblies when images are outside their source evidence.
use crate::{
    input::raster::PdfRoute,
    model::{LayerKind, OpeningEvidence, PageScan, Point, Shape},
};
use std::collections::HashSet;

/// Frozen eligibility for saved scans produced before mixed evidence support.
pub(crate) fn legacy(page: &PageScan) -> bool {
    !page.material_tiles.is_empty()
        && page
            .pdf_input
            .as_ref()
            .is_some_and(|info| info.route == PdfRoute::Vector)
}

struct Bounds {
    min: Point,
    max: Point,
}

impl Bounds {
    fn include(&mut self, p: Point, margin: f64) -> Option<()> {
        if !p.finite() || !margin.is_finite() || margin < 0. {
            return None;
        }
        let lo = Point::new(p.x - margin, p.y - margin);
        let hi = Point::new(p.x + margin, p.y + margin);
        if !lo.finite() || !hi.finite() {
            return None;
        }
        self.min = Point::new(self.min.x.min(lo.x), self.min.y.min(lo.y));
        self.max = Point::new(self.max.x.max(hi.x), self.max.y.max(hi.y));
        Some(())
    }

    fn clear_of(&self, min: Point, max: Point) -> bool {
        min.finite()
            && max.finite()
            && min.x < max.x
            && min.y < max.y
            && (max.x < self.min.x
                || self.max.x < min.x
                || max.y < self.min.y
                || self.max.y < min.y)
    }
}

fn protected_bounds(page: &PageScan) -> Option<Bounds> {
    let mut bounds = Bounds {
        min: Point::new(f64::INFINITY, f64::INFINITY),
        max: Point::new(f64::NEG_INFINITY, f64::NEG_INFINITY),
    };
    let mut source_ids = HashSet::new();
    for wall in super::original_wall_observations(page)
        .iter()
        .filter(|wall| !super::raster::is_raster(wall))
    {
        // Account for half the solid width and the local reach of rail, cap,
        // and connector proofs beyond the observed wall's terminal planes.
        let reach = 2. * wall.thickness;
        bounds.include(wall.start, reach)?;
        bounds.include(wall.end, reach)?;
        if let Some(curve) = wall.curve {
            bounds.include(curve.center, curve.radius + reach)?;
        }
        source_ids.extend(wall.source_ids.iter().map(String::as_str));
    }
    for opening in page.opening_evidence() {
        // Late authored openings can depend on this test and must never alter it.
        if matches!(
            opening.evidence,
            OpeningEvidence::ExteriorFrameGap
                | OpeningEvidence::AuthoredInteriorGap
                | OpeningEvidence::CompositeWindowFrame
                | OpeningEvidence::AuthoredWindowFrame
        ) {
            continue;
        }
        bounds.include(opening.start, 0.)?;
        bounds.include(opening.end, 0.)?;
        if let Some(hinge) = opening.hinge {
            bounds.include(hinge, 0.)?;
        }
        if let Some(jambs) = &opening.physical_jambs {
            bounds.include(jambs.original_start, 0.)?;
            bounds.include(jambs.original_end, 0.)?;
            source_ids.extend(jambs.rail_source_ids.iter().map(String::as_str));
            source_ids.extend(jambs.cap_source_ids.iter().map(String::as_str));
        }
        for swing in &opening.swing_observations {
            let radius = swing
                .hinge
                .distance(swing.closed_tip)
                .max(swing.hinge.distance(swing.open_tip));
            bounds.include(swing.hinge, radius + swing.leaf_tolerance)?;
        }
        source_ids.extend(opening.source_ids.iter().map(String::as_str));
    }
    let tile_layers: HashSet<_> = page
        .material_tiles
        .iter()
        .filter_map(|tile| tile.layer.as_deref())
        .collect();
    for tile in &page.material_tiles {
        for p in tile.outline {
            bounds.include(p, 0.)?;
        }
    }
    for p in &page.primitives {
        if !source_ids.contains(p.id.as_str())
            && p.layer_kind() != LayerKind::Wall
            && !(p.layer_category_override.is_none()
                && p.layer.as_deref().is_some_and(|layer| {
                    tile_layers.contains(layer) || crate::layers::is_named_wall(layer)
                }))
        {
            continue;
        }
        // Some finite rails are independent witnesses outside an observed
        // wall extent. Protect their actual ink, including known pen width.
        let pen = p.stroke_width.unwrap_or(0.) * 0.5;
        match &p.shape {
            Shape::Line { start, end } => {
                bounds.include(*start, pen)?;
                bounds.include(*end, pen)?;
            }
            Shape::Polyline { points, .. } => {
                for &point in points {
                    bounds.include(point, pen)?;
                }
            }
            Shape::Arc { center, radius, .. } => bounds.include(*center, radius + pen)?,
            _ => {}
        }
    }
    (bounds.min.finite() && bounds.max.finite()).then_some(bounds)
}

pub(crate) fn supports(page: &PageScan) -> bool {
    if legacy(page) {
        return true;
    }
    let Some(info) = page
        .pdf_input
        .as_ref()
        .filter(|info| info.route == PdfRoute::Mixed && !info.source_image_ids.is_empty())
    else {
        return false;
    };
    if page.material_tiles.is_empty() {
        return false;
    }
    let Some(bounds) = protected_bounds(page) else {
        return false;
    };
    let mut images = 0;
    for p in &page.primitives {
        if let Shape::Image { min, max } = p.shape {
            if !bounds.clear_of(min, max) || info.source_image_ids.binary_search(&p.id).is_err() {
                return false;
            }
            images += 1;
        }
    }
    images == info.source_image_ids.len()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        input::raster::PdfInput,
        model::{BodyEvidence, Document, Options, Page, Primitive, Wall},
        placement::insulation::MaterialTile,
    };

    #[test]
    fn a_mixed_page_needs_geometric_image_separation_even_without_pixel_faces() {
        let p = Point::new;
        let mut page = crate::pipeline::scan_document(
            Document {
                pages: vec![Page {
                    finish_history: None,
                    pdf_input: None,
                    number: 1,
                    width: 600.,
                    height: 400.,
                    units_to_mm: None,
                    primitives: vec![],
                    placement_paint_ids: None,
                    placement_polygon_paint_ids: None,
                    placement_paint_remainders: vec![],
                    white_nested_walls: vec![],
                    terminal_stock: vec![],
                    local_stock: vec![],
                    stepped_stock: vec![],
                    capped_stock: vec![],
                    recessed_stock: vec![],
                    masked_junction_stock: vec![],
                    pocket_stock: vec![],
                    placement_contact_paint: vec![],
                    material_tiles: vec![],
                }],
                warnings: vec![],
            },
            &Options::default(),
            None,
            false,
        )
        .unwrap()
        .pages
        .remove(0);
        page.walls.push(Wall {
            id: "wall".into(),
            page: 1,
            start: p(100., 150.),
            end: p(500., 150.),
            thickness: 6.,
            body: BodyEvidence::ParallelFaces,
            curve: None,
            source_ids: vec![],
            requires_review: true,
        });
        page.material_tiles.push(MaterialTile {
            id: "tile".into(),
            layer: Some("Wall".into()),
            outline: [p(100., 147.), p(500., 147.), p(500., 153.), p(100., 153.)],
            origin: p(100., 147.),
            repeat: p(3., 0.),
            across: p(0., 6.),
            strokes: vec![],
        });
        page.pdf_input = Some(PdfInput {
            analysis_revision: 1,
            route: PdfRoute::Mixed,
            vector_primitive_count: 1,
            source_image_ids: vec!["bitmap".into()],
            image_coverage: 0.001,
            render_width: 600,
            render_height: 400,
            threshold: 128,
            raster_source_ids: vec![],
            raster_course_receipts: vec![],
            raster_face_continuations: vec![],
            raster_hinged_doors: vec![],
            raster_jamb_stocks: vec![],
            raster_narrow_courses: vec![],
            raster_section_courses: vec![],
            raster_jamb_returns: vec![],
            raster_aligned_courses: vec![],
            raster_corner_courses: vec![],
            bands: vec![],
        });
        page.primitives.push(Primitive {
            id: "bitmap".into(),
            layer: None,
            stroke_width: None,
            layer_category_override: None,
            shape: Shape::Image {
                min: p(5., 5.),
                max: p(20., 20.),
            },
        });
        assert!(supports(&page));
        assert!(!legacy(&page));
        page.primitives[0].shape = Shape::Image {
            min: p(280., 145.),
            max: p(295., 155.),
        };
        assert!(!supports(&page));
    }
}
