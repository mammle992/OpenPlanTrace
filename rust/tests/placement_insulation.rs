use lopdf::{Document as Pdf, Object, Stream, dictionary};
use openplantrace_rs::{
    export,
    input::{self, DrawingFormat},
    model::{Options, Scan},
    pipeline,
};
use serde_json::{Value, json};
use std::fmt::Write;

// A hand-drawn one-cell batt. Each half-lobe folds back on itself, traversing
// the full depth before meeting its neighbour at the next cell boundary.
fn batt() -> String {
    let half = [(0, 100), (13, 90), (17, 80), (7, 25), (12, 12), (25, 0)];
    let mut points = Vec::new();
    for offset in [0, 50] {
        points.extend(half.iter().map(|&(x, y)| (offset + x, y)));
        points.extend(
            half.iter()
                .rev()
                .skip(1)
                .map(|&(x, y)| (offset + 50 - x, y)),
        );
    }
    points.dedup();
    let mut strokes = String::from("0.25 G 1 w\n");
    // The spatial chain is independent of drawing-command order and direction.
    for (index, pair) in points.windows(2).rev().enumerate() {
        let [(a, b), (c, d)] = [pair[index % 2], pair[(index + 1) % 2]];
        writeln!(strokes, "{a} {b} m {c} {d} l S").unwrap();
    }
    strokes
}

const FACES: &str = "20 30 m 120 30 l S 120 38 m 20 38 l S";
const FILL: &str = "20 30 100 8 re f";
const DOOR: &str = "/OC /Door BDC 0 G 59 29 m 59 39 l S 91 29 m 91 39 l S
    59 34 m 59 66 l S 91 34 m 91 51.6731 76.6731 66 59 66 c S EMC";

#[test]
fn insulated_core_terminates_at_a_signed_door_jamb() {
    let core = "20 30 m 59 30 l S 59 38 m 20 38 l S";
    let skin = "19 29 m 59 29 l S 59 29 m 91 29 l S 91 29 m 132 29 l S
                21 39 m 59 39 l S 59 39 m 91 39 l S 91 39 m 132 39 l S";
    let signed = "/OC /Door BDC 0 G 59 30 m 59 38 l S 91 30 m 91 38 l S
                  59 34 m 59 66 l S 91 34 m 91 51.6731 76.6731 66 59 66 c S EMC";
    for (rotate, competing_face) in [(0, ""), (90, ""), (0, "20 25 m 59 25 l S")] {
        let scan = drawing(
            &batt(),
            "A-WALL",
            &format!("{core} {skin} {competing_face}"),
            "20 30 39 8 re f",
            rotate,
            signed,
        );
        let page = packet(&scan);
        let core = page["walls"]
            .as_array()
            .unwrap()
            .iter()
            .find(|w| w["decision"]["reason"] == "insulated_door_jamb")
            .expect("signed and outlined batt at jamb");
        assert_eq!(core["thickness"], 8.);
        assert_eq!(core["decision"]["status"], "Ready");
        if !competing_face.is_empty() {
            assert!(core["wallId"].as_str().unwrap().starts_with("jamb-core:"));
            let mut altered = scan.clone();
            let wall = altered.pages[0]
                .walls
                .iter_mut()
                .find(|w| w.id == core["wallId"])
                .unwrap();
            wall.thickness += 0.00001;
            assert!(export::validate(&altered).is_err());
        }
        let host = &core["decision"]["supportWallIds"][0];
        let doors = page["openings"].as_array().unwrap();
        assert_eq!(doors.len(), 1);
        assert!(doors[0]["hostWallIds"].as_array().unwrap().contains(host));
        assert!(
            !doors[0]["placementHostWallIds"]
                .as_array()
                .unwrap()
                .contains(host)
        );
        let proof = core["decision"]["supportSourceIds"].as_array().unwrap();
        assert!(proof.contains(&doors[0]["swingObservations"][0]["arcSourceId"]));
        assert!(proof.contains(&page["materialTiles"][0]["id"]));
        assert!(
            core["sourceIds"]
                .as_array()
                .unwrap()
                .iter()
                .all(|id| proof.contains(id))
        );
        let solid = page["barriers"]
            .as_array()
            .unwrap()
            .iter()
            .find(|b| b["wallId"] == core["wallId"])
            .unwrap();
        let s = &solid["spans"][0]["start"];
        let e = &solid["spans"][0]["end"];
        let length = (s["x"].as_f64().unwrap() - e["x"].as_f64().unwrap())
            .hypot(s["y"].as_f64().unwrap() - e["y"].as_f64().unwrap());
        assert!((length - 39.).abs() < 1e-7);
    }
    for (faces, paint, door) in [
        (skin, "20 30 39 8 re f", signed),
        (core, "20 30 39 8 re f", signed),
        (&format!("{core} {skin}"), "20 30 40 8 re f", signed),
        (&format!("{core} {skin}"), "20 30 39 8 re f", ""),
    ] {
        let page = packet(&drawing(&batt(), "A-WALL", faces, paint, 0, door));
        assert!(
            page["walls"]
                .as_array()
                .unwrap()
                .iter()
                .all(|w| w["decision"]["reason"] != "insulated_door_jamb")
        );
    }
}

fn drawing(tile: &str, layer: &str, faces: &str, paint: &str, rotate: i64, extra: &str) -> Scan {
    let mut pdf = Pdf::with_version("1.6");
    let structural =
        pdf.add_object(dictionary! {"Type"=>"OCG", "Name"=>Object::string_literal(layer)});
    let door = pdf.add_object(dictionary! {"Type"=>"OCG", "Name"=>Object::string_literal("Door")});
    let pattern = pdf.add_object(Stream::new(
        dictionary! {
            "Type"=>"Pattern", "PatternType"=>1, "PaintType"=>1, "TilingType"=>1,
            "BBox"=>vec![(-1).into(),(-1).into(),101.into(),101.into()],
            "XStep"=>100, "YStep"=>100, "Resources"=>dictionary!{},
            "Matrix"=>vec![0.08f32.into(),0.into(),0.into(),0.08f32.into(),20.into(),30.into()],
        },
        tile.as_bytes().to_vec(),
    ));
    let resources = dictionary! {
        "Pattern"=>dictionary!{"Stitch"=>pattern},
        "Properties"=>dictionary!{"Wall"=>structural, "Door"=>door},
    };
    let commands =
        format!("/OC /Wall BDC 0 G {faces} q /Pattern cs /Stitch scn {paint} Q EMC {extra}");
    let stream = pdf.add_object(Stream::new(dictionary! {}, commands.into_bytes()));
    let tree = pdf.new_object_id();
    let page = pdf.add_object(dictionary! {
        "Type"=>"Page","Parent"=>tree,"Contents"=>stream, "Rotate"=>rotate,
        "MediaBox"=>vec![0.into(),0.into(),200.into(),160.into()], "Resources"=>resources,
    });
    pdf.objects.insert(
        tree,
        Object::Dictionary(dictionary! {
            "Type"=>"Pages","Kids"=>vec![page.into()],"Count"=>1,
        }),
    );
    let catalog = pdf.add_object(dictionary! {"Type"=>"Catalog","Pages"=>tree});
    pdf.trailer.set("Root", catalog);
    let mut bytes = Vec::new();
    pdf.save_to(&mut bytes).unwrap();
    let doc = input::load_bytes(DrawingFormat::Pdf, &bytes).unwrap();
    pipeline::scan_document(doc, &Options::default(), None, false).unwrap()
}

fn packet(scan: &Scan) -> Value {
    export::validate(scan).unwrap();
    export::placement(scan)["pages"][0].take()
}

#[test]
fn painted_batt_matches_separate_measured_faces_in_both_page_orientations() {
    for rot in [0, 90] {
        let scan = drawing(&batt(), "A-WALL", FACES, FILL, rot, "");
        let page = packet(&scan);
        let ready = page["wallSets"]["readyWallIds"].as_array().unwrap();
        assert_eq!(ready.len(), 1, "{rot}: {}", page["walls"]);
        let selected = page["walls"]
            .as_array()
            .unwrap()
            .iter()
            .find(|w| w["wallId"] == ready[0])
            .unwrap();
        assert_eq!(selected["thickness"], 8.);
        assert_eq!(page["barriers"].as_array().unwrap().len(), 1);
        let solid = &page["barriers"][0]["spans"][0];
        let mut ends = [(&solid["start"]), (&solid["end"])];
        ends.sort_by(|a, b| {
            a["x"]
                .as_f64()
                .unwrap()
                .total_cmp(&b["x"].as_f64().unwrap())
                .then_with(|| {
                    a["y"]
                        .as_f64()
                        .unwrap()
                        .total_cmp(&b["y"].as_f64().unwrap())
                })
        });
        let (begin, end) = if rot == 0 {
            (json!({"x":20.0,"y":126.0}), json!({"x":120.0,"y":126.0}))
        } else {
            (json!({"x":34.0,"y":20.0}), json!({"x":34.0,"y":120.0}))
        };
        assert_eq!(ends, [&begin, &end]);
        let evidence = selected["decision"]["supportSourceIds"].as_array().unwrap();
        let tile = &page["materialTiles"][0];
        assert_eq!(tile["strokes"].as_array().unwrap().len(), 20);
        assert!(evidence.contains(&tile["id"]));
        for face in selected["sourceIds"].as_array().unwrap() {
            assert!(evidence.contains(face));
        }
        assert_eq!(page["materialTiles"].as_array().unwrap().len(), 1);
        let mut altered = scan.clone();
        let decision = altered.pages[0]
            .placement_decisions
            .as_mut()
            .unwrap()
            .iter_mut()
            .find(|d| d.wall_id == ready[0].as_str().unwrap())
            .unwrap();
        decision.support_source_ids.pop();
        assert!(export::validate(&altered).is_err());
        let mut shifted = scan.clone();
        let tile = &mut shifted.pages[0].material_tiles[0];
        tile.origin = tile.origin + tile.across * 0.5;
        assert!(export::validate(&shifted).is_err());
    }
}

#[test]
fn absent_paint_glass_furniture_clips_and_decorative_cells_stay_under_review() {
    let material = batt();
    let invisible = material.replace("0.25 G", "1 G");
    let broken = material
        .lines()
        .enumerate()
        .filter_map(|(i, line)| (i != 4).then_some(line))
        .collect::<Vec<_>>()
        .join("\n");
    let plain = "0.25 G 0 100 m 25 0 l 50 100 l 75 0 l 100 100 l S";
    for (label, tile, layer, paint) in [
        (
            "unused cell",
            material.as_str(),
            "A-WALL",
            "20 30 100 8 re n",
        ),
        (
            "partial paint",
            material.as_str(),
            "A-WALL",
            "20 30 40 8 re f",
        ),
        (
            "clipped paint",
            material.as_str(),
            "A-WALL",
            "20 30 62 8 re W n 20 30 100 8 re f",
        ),
        (
            "open aperture",
            material.as_str(),
            "A-WALL",
            "20 30 100 8 re 59 30 32 8 re f*",
        ),
        ("furniture textile", material.as_str(), "A-FURN", FILL),
        ("glazing", material.as_str(), "A-GLASS", FILL),
        ("dimension span", material.as_str(), "A-DIMS", FILL),
        (
            "door-layer pattern over structural wall faces",
            material.as_str(),
            "A-WALL",
            "/OC /Door BDC 20 30 100 8 re f EMC",
        ),
        ("decorative chevrons", plain, "A-WALL", FILL),
        ("white cell ink", &invisible, "A-WALL", FILL),
        ("disconnected stitch path", &broken, "A-WALL", FILL),
    ] {
        let page = packet(&drawing(tile, layer, FACES, paint, 0, ""));
        assert_eq!(page["barriers"], json!([]), "{label}");
    }
}

#[test]
fn an_independently_signed_aperture_survives_an_overlapping_material_fill() {
    let page = packet(&drawing(&batt(), "A-WALL", FACES, FILL, 0, DOOR));
    let doors = page["openings"].as_array().unwrap();
    assert_eq!(doors.len(), 1);
    assert!(!doors[0]["swingObservations"].as_array().unwrap().is_empty());
    assert_eq!(doors[0]["hostWallIds"].as_array().unwrap().len(), 1);
    assert_eq!(doors[0]["placementHostWallIds"], json!([]));
    assert_eq!(page["barriers"], json!([]));
}

#[test]
fn opaque_overpaint_must_clear_the_painted_wall_interior() {
    let material = batt();
    for (label, cover, expected) in [
        ("untouched upper face", "1 g 50 38 10 8 re f", 1),
        ("untouched end face", "1 g 120 30 10 8 re f", 1),
        ("covered through wall", "1 g 50 30 10 8 re f", 0),
        ("covered along wall edge", "1 g 50 37.5 10 8 re f", 0),
    ] {
        let page = packet(&drawing(&material, "A-WALL", FACES, FILL, 0, cover));
        assert_eq!(
            page["barriers"].as_array().unwrap().len(),
            expected,
            "{label}"
        );
    }
}

#[test]
fn narrow_facing_requires_the_inner_seam_and_both_independent_returns() {
    let faces = "20 29.32 m 120 29.32 l S 120 38 m 20 38 l S";
    let seam = "20 30 m 120 30 l S";
    let left = "20 30 m 20 29.32 l S";
    let right = "120 29.32 m 120 30 l S";
    let full_jamb = "120 29.32 m 120 38 l S";
    let material = batt();
    for (label, members, complete) in [
        ("complete facing", [seam, left, right], true),
        ("missing seam", ["", left, right], false),
        ("missing right return", [seam, left, ""], false),
        (
            "full jamb instead of facing return",
            [seam, left, full_jamb],
            false,
        ),
    ] {
        // The finish continues around the next assembly. The two outer
        // wall faces end together, so they are the measured physical pair.
        let extra = format!(
            "/OC /Wall BDC 120 30 m 170 30 l S {} EMC",
            members.join(" ")
        );
        let page = packet(&drawing(&material, "A-WALL", faces, FILL, 0, &extra));
        let wall = page["walls"]
            .as_array()
            .unwrap()
            .iter()
            .find(|w| (w["thickness"].as_f64().unwrap() - 8.68).abs() < 1e-4)
            .unwrap_or_else(|| panic!("{label}: {}", page["walls"]));
        let ready = page["wallSets"]["readyWallIds"]
            .as_array()
            .unwrap()
            .contains(&wall["wallId"]);
        assert_eq!(ready, complete, "{label}: {wall}");
        if ready {
            let ids = wall["decision"]["supportSourceIds"].as_array().unwrap();
            assert_eq!(ids.len(), 6, "{ids:?}");
            assert!(ids.contains(&page["materialTiles"][0]["id"]));
            for face in wall["sourceIds"].as_array().unwrap() {
                assert!(ids.contains(face));
            }
        }
    }
}

#[test]
fn staggered_faces_witness_only_the_inner_finite_wall() {
    let short = "21 30 m 120 30 l S";
    let long = "120 38 m 20 38 l S";
    let outer = "21 29 m 120 29 l S 120 39 m 21 39 l S";
    let material = batt();
    for (label, long, outer, fill, expected) in [
        ("visible step", long, outer, FILL, true),
        ("wrong painted step", long, outer, "19 30 101 8 re f", false),
        (
            "shortened long face",
            "120 38 m 21 38 l S",
            outer,
            FILL,
            false,
        ),
        (
            "missing outer face",
            long,
            "21 29 m 120 29 l S",
            FILL,
            false,
        ),
    ] {
        let faces = format!("{short} {long} {outer}");
        let page = packet(&drawing(&material, "A-WALL", &faces, fill, 0, ""));
        let selected = page["walls"]
            .as_array()
            .unwrap()
            .iter()
            .find(|w| w["decision"]["reason"] == "tiled_insulation_at_staggered_faces");
        assert_eq!(selected.is_some(), expected, "{label}: {}", page["walls"]);
        assert_eq!(
            page["barriers"].as_array().unwrap().len(),
            usize::from(expected),
            "{label}"
        );
        if let Some(narrow) = selected {
            assert_eq!(narrow["thickness"], 8.);
            assert_eq!(narrow["decision"]["status"], "Ready");
            let witnesses = narrow["decision"]["supportSourceIds"].as_array().unwrap();
            assert_eq!(witnesses.len(), 5);
            assert!(witnesses.contains(&page["materialTiles"][0]["id"]));
            let wider = page["walls"]
                .as_array()
                .unwrap()
                .iter()
                .find(|w| w["thickness"] == 10.)
                .unwrap();
            assert_ne!(wider["decision"]["status"], "Ready");
            for wall in [narrow, wider] {
                for face in wall["sourceIds"].as_array().unwrap() {
                    assert!(witnesses.contains(face));
                }
            }
        }
    }
}
