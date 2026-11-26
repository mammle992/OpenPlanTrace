//! Retained raster geometry must remain inside its measured source faces,
//! including observations that are withheld from placement.
use openplantrace_rs::{
    input::raster::{PdfInput, PdfRoute, RasterBand},
    model::*,
    pipeline, placement,
};

fn measurements(revision: u32) -> (PdfInput, Vec<Primitive>) {
    let source = |id: &str, shape| Primitive {
        id: id.into(),
        layer: None,
        stroke_width: None,
        layer_category_override: None,
        shape,
    };
    let primitives = vec![
        source(
            "pdf:p1:image:1",
            Shape::Image {
                min: Point::new(0., 0.),
                max: Point::new(100., 100.),
            },
        ),
        source(
            "raster:face:1",
            Shape::Line {
                start: Point::new(0., 10.),
                end: Point::new(10., 10.),
            },
        ),
        source(
            "raster:face:2",
            Shape::Line {
                start: Point::new(0., 20.),
                end: Point::new(10., 20.),
            },
        ),
    ];
    let info = PdfInput {
        analysis_revision: revision,
        route: PdfRoute::Raster,
        vector_primitive_count: 0,
        source_image_ids: vec!["pdf:p1:image:1".into()],
        image_coverage: 1.,
        render_width: 100,
        render_height: 100,
        threshold: 174,
        raster_source_ids: vec!["raster:face:1".into(), "raster:face:2".into()],
        raster_course_receipts: vec![],
        raster_face_continuations: vec![],
        raster_hinged_doors: vec![],
        raster_jamb_stocks: vec![],
        raster_narrow_courses: vec![],
        raster_section_courses: vec![],
        raster_jamb_returns: vec![],
        bands: vec![RasterBand {
            hatch: None,
            face_source_ids: ["raster:face:1".into(), "raster:face:2".into()],
            start: Point::new(0., 15.),
            end: Point::new(10., 15.),
            thickness: 10.,
            dark_fraction: 1.,
            mean_luma: 90.,
            boundary_coverage: [1., 1.],
            width_pixels: 10.,
        }],
    };
    (info, primitives)
}

#[test]
fn both_band_ends_must_stay_inside_the_faces_in_either_direction() {
    for revision in [0, 1] {
        let (info, sources) = measurements(revision);
        openplantrace_rs::input::raster::validate(&info, &sources, 100., 100.).unwrap();
        let mut reversed = info.clone();
        let band = &mut reversed.bands[0];
        std::mem::swap(&mut band.start, &mut band.end);
        openplantrace_rs::input::raster::validate(&reversed, &sources, 100., 100.).unwrap();
        for (start, end) in [
            (Point::new(5., 15.), Point::new(-100., 15.)),
            (Point::new(100., 15.), Point::new(5., 15.)),
        ] {
            let mut forged = info.clone();
            forged.bands[0].start = start;
            forged.bands[0].end = end;
            assert!(
                openplantrace_rs::input::raster::validate(&forged, &sources, 100., 100.).is_err()
            );
        }
    }
}

#[test]
fn a_long_slanted_source_cannot_claim_a_parallel_raster_face() {
    let (info, mut sources) = measurements(1);
    let Shape::Line { end, .. } = &mut sources[1].shape else {
        unreachable!()
    };
    *end = Point::new(100., 13.);
    assert!(openplantrace_rs::input::raster::validate(&info, &sources, 100., 100.).is_err());
}

#[test]
fn a_review_wall_still_requires_original_page_band_provenance() {
    let (info, primitives) = measurements(1);
    let scan = pipeline::scan_document(
        Document {
            warnings: vec![],
            pages: vec![Page {
                finish_history: None,
                pdf_input: Some(info),
                number: 1,
                placement_paint_ids: Some(vec![]),
                placement_polygon_paint_ids: None,
                placement_paint_remainders: vec![],
                white_nested_walls: vec![],
                terminal_stock: vec![],
                local_stock: vec![],
                stepped_stock: vec![],
                capped_stock: vec![],
                placement_contact_paint: vec![],
                material_tiles: vec![],
                width: 100.,
                height: 100.,
                units_to_mm: None,
                primitives,
            }],
        },
        &Options::default(),
        None,
        true,
    )
    .unwrap();
    let page = &scan.pages[0];
    assert_eq!(page.walls.len(), 1);
    assert!(
        page.placement_decisions
            .as_ref()
            .unwrap()
            .iter()
            .all(|d| d.status == placement::WallStatus::Review)
    );
    placement::validate(page, true).unwrap();
    let mut forged = page.clone();
    forged.walls[0].start.x -= 20.;
    // The saved Review decision does not otherwise change, and all referenced
    // source primitives and band metadata remain valid. Placement replay must
    // still bind this retained physical observation to its measured band.
    assert!(placement::validate(&forged, true).is_err());
}
