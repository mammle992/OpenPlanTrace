use super::*;
use crate::semantics::{Dimension, Span, Unit};
fn dim(name: &str, y: f64, scale: f64) -> Dimension {
    Dimension {
        id: name.into(),
        text: "100 mm".into(),
        unit: Unit::Millimeter,
        measured_mm: 100.,
        orientation: None,
        line: Some(Span {
            start: Point::new(0., y),
            end: Point::new(20., y),
        }),
        line_source: Some(format!("{name}-line")),
        text_sources: vec![format!("{name}-text")],
        start_witness: Some(format!("{name}-a")),
        end_witness: Some(format!("{name}-b")),
        drawing_length: Some(20.),
        observed_mm_per_unit: Some(scale),
        calibrated_length_mm: None,
        calibration_relative_error: None,
        evidence: DimensionEvidence::TwoFiniteWitnesses,
        requires_review: true,
    }
}
fn go(d: Vec<Dimension>) -> Option<f64> {
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
        recessed_stock: vec![],
        masked_junction_stock: vec![],
        pocket_stock: vec![],
        placement_contact_paint: vec![],
        material_tiles: vec![],
        width: 200.,
        height: 200.,
        units_to_mm: None,
        primitives: vec![],
    };
    let s = PageSemantics {
        dimensions: d,
        ..Default::default()
    };
    dimensions(&page.primitives, &s).map(|v| v.0)
}
#[test]
fn corroborating_independent_finite_dimensions_establish_scale() {
    assert_eq!(go(vec![dim("a", 0., 5.), dim("b", 30., 5.)]), Some(5.));
}
#[test]
fn repeated_paint_or_mixed_viewport_scales_do_not_calibrate_page() {
    assert_eq!(go(vec![dim("a", 0., 5.), dim("b", 0., 5.)]), None);
    assert_eq!(go(vec![dim("a", 0., 5.), dim("b", 30., 15.)]), None);
}
