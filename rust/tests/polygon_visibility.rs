//! Public PDF-ingestion regressions for conservative convex-paint certificates.
//! All documents are generated here; no private plan is a test dependency.
use lopdf::{Document, Stream, dictionary};
use openplantrace_rs::{
    input,
    model::{Page, Shape},
};

const TRAPEZOID: &str = "0.5 g 30 30 m 90 30 l 90 70 l 40 70 l h f";

fn page(before: &str, candidate: &str, after: &str, rotate: i64) -> Page {
    let mut pdf = Document::with_version("1.7");
    let tree = pdf.new_object_id();
    let wall = pdf.add_object(dictionary! {
        "Type"=>"OCG", "Name"=>lopdf::Object::string_literal("A-WALL")
    });
    let mask = pdf.add_object(Stream::new(dictionary! {
        "Type"=>"XObject", "Subtype"=>"Form", "BBox"=>vec![0.into(),0.into(),220.into(),220.into()],
        "Resources"=>dictionary!{},
        "Group"=>dictionary!{"Type"=>"Group","S"=>"Transparency","CS"=>"DeviceGray"}
    }, b"0.5 g 0 0 220 220 re f".to_vec()));
    let resources = dictionary! {
        "Properties"=>dictionary!{"Wall"=>wall},
        "ExtGState"=>dictionary!{
            "Faint"=>dictionary!{"Type"=>"ExtGState","ca"=>0.5,"CA"=>0.5},
            "Masked"=>dictionary!{"Type"=>"ExtGState","SMask"=>dictionary!{"S"=>"Alpha","G"=>mask}}
        }
    };
    let text = format!("{before} /OC /Wall BDC {candidate} EMC {after}");
    let content = pdf.add_object(Stream::new(dictionary! {}, text.into_bytes()));
    let sheet = pdf.add_object(dictionary! {
        "Type"=>"Page", "Parent"=>tree, "MediaBox"=>vec![0.into(),0.into(),220.into(),220.into()],
        "Rotate"=>rotate, "Resources"=>resources, "Contents"=>content
    });
    pdf.objects.insert(
        tree,
        dictionary! {"Type"=>"Pages","Kids"=>vec![sheet.into()],"Count"=>1}.into(),
    );
    let catalog = pdf.add_object(dictionary! {"Type"=>"Catalog","Pages"=>tree});
    pdf.trailer.set("Root", catalog);
    let mut bytes = vec![];
    pdf.save_to(&mut bytes).unwrap();
    input::load_bytes(input::DrawingFormat::Pdf, &bytes)
        .unwrap()
        .pages
        .remove(0)
}

fn candidate_ids(page: &Page) -> Vec<&str> {
    page.primitives
        .iter()
        .filter(|p| {
            p.layer.as_deref() == Some("A-WALL")
                && matches!(p.shape, Shape::Polyline { filled: true, .. })
        })
        .map(|p| p.id.as_str())
        .collect()
}

fn certified_candidates(page: &Page) -> usize {
    let allowed = page
        .placement_polygon_paint_ids
        .as_ref()
        .expect("PDF polygon paint inventory");
    candidate_ids(page)
        .into_iter()
        .filter(|id| allowed.iter().any(|v| v == id))
        .count()
}

#[test]
fn opaque_convex_nonrectangular_paint_has_a_separate_certificate_at_rotations() {
    for rotation in [0, 90, 180, 270] {
        let sheet = page(
            "",
            &format!("{TRAPEZOID} 0.4 g 120 30 50 40 re f"),
            "",
            rotation,
        );
        let ids = candidate_ids(&sheet);
        assert_eq!(ids.len(), 2);
        let polygons = sheet.placement_polygon_paint_ids.as_ref().unwrap();
        let rectangles = sheet.placement_paint_ids.as_ref().unwrap();
        assert!(
            polygons.iter().any(|id| id == ids[0]),
            "rotation={rotation}"
        );
        assert!(
            !polygons.iter().any(|id| id == ids[1]),
            "rectangles retain their old inventory"
        );
        assert!(
            !rectangles.iter().any(|id| id == ids[0]),
            "nonrectangles do not enter old authority"
        );
        assert!(
            rectangles.iter().any(|id| id == ids[1]),
            "old visible rectangle still qualifies"
        );
    }
}

#[test]
fn finite_miter_triangle_and_affine_rotated_trapezoid_remain_convex_authority() {
    for paint in [
        "0.5 g 30 30 m 80 30 l 80 50 l h f".to_owned(),
        format!("q 0.8 0.6 -0.6 0.8 70 0 cm {TRAPEZOID} Q"),
    ] {
        let sheet = page("", &paint, "", 0);
        assert_eq!(certified_candidates(&sheet), 1);
        assert!(sheet.placement_paint_ids.as_ref().unwrap().is_empty());
    }
}

#[test]
fn holes_concavity_self_crossing_and_zero_area_do_not_certify_material() {
    for (name, path) in [
        (
            "even-odd hole",
            "0.5 g 30 30 m 90 30 l 90 70 l 40 70 l h 50 40 20 15 re f*",
        ),
        (
            "nonzero hole",
            "0.5 g 30 30 m 90 30 l 90 70 l 40 70 l h 50 40 m 50 55 l 70 55 l 70 40 l h f",
        ),
        (
            "concave",
            "0.5 g 30 30 m 90 30 l 90 70 l 60 50 l 30 70 l h f",
        ),
        ("self-crossing", "0.5 g 30 30 m 90 70 l 30 70 l 90 30 l h f"),
        (
            "self-crossing star with consistent local turns",
            "0.5 g 50 20 m 68 75 l 21 41 l 79 41 l 32 75 l h f",
        ),
        ("zero-area", "0.5 g 30 30 m 90 30 l 60 30 l h f"),
    ] {
        assert_eq!(certified_candidates(&page("", path, "", 0)), 0, "{name}");
    }
}

#[test]
fn source_transparency_unknown_state_and_soft_mask_are_not_opaque_authority() {
    for (name, before) in [
        ("translucent", "/Faint gs"),
        ("unknown", "/Missing gs"),
        ("soft-mask", "/Masked gs"),
    ] {
        assert_eq!(
            certified_candidates(&page(before, TRAPEZOID, "", 0)),
            0,
            "{name}"
        );
    }
    let missing_pattern = page(
        "/Pattern cs /Absent scn",
        "30 30 m 90 30 l 90 70 l 40 70 l h f",
        "",
        0,
    );
    assert_eq!(
        certified_candidates(&missing_pattern),
        0,
        "unresolved source pattern"
    );
}

#[test]
fn later_white_colored_or_uncertain_paint_withholds_touched_source_polygon() {
    for (name, after) in [
        ("white fill", "1 g 50 40 5 10 re f"),
        ("colored opaque fill", "0.2 0.1 0.6 rg 50 40 5 10 re f"),
        ("white erasing stroke", "q 1 G 2 w 50 20 m 50 80 l S Q"),
        ("unknown fill state", "q /Missing gs 50 40 5 10 re f Q"),
        ("masked fill state", "q /Masked gs 50 40 5 10 re f Q"),
        (
            "unknown stroke state",
            "q /Missing gs 2 w 50 20 m 50 80 l S Q",
        ),
        (
            "masked stroke state",
            "q /Masked gs 2 w 50 20 m 50 80 l S Q",
        ),
    ] {
        assert_eq!(
            certified_candidates(&page("", TRAPEZOID, after, 0)),
            0,
            "{name}"
        );
    }
}

#[test]
fn earlier_underpaint_and_disjoint_later_paint_do_not_erase_polygon_authority() {
    for (name, before, after) in [
        ("underpaint", "1 g 50 40 5 10 re f", ""),
        ("disjoint white fill", "", "1 g 150 150 20 20 re f"),
        (
            "disjoint white stroke",
            "",
            "q 1 G 2 w 140 150 m 180 150 l S Q",
        ),
        ("shared bounding edge", "", "1 g 90 40 20 10 re f"),
        (
            "clip avoids candidate",
            "",
            "q 150 150 20 20 re W n 1 g 40 40 20 20 re f Q",
        ),
    ] {
        assert_eq!(
            certified_candidates(&page(before, TRAPEZOID, after, 0)),
            1,
            "{name}"
        );
    }
}

#[test]
fn bounding_footprint_is_conservative_even_outside_the_sloped_polygon_corner() {
    // x31,y68 is outside this trapezoid but inside its bounding rectangle.
    // The conservative visibility receipt may withhold, never invent certainty.
    let sheet = page("", TRAPEZOID, "1 g 31 67 2 2 re f", 0);
    assert_eq!(certified_candidates(&sheet), 0);
}
