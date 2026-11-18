//! Exercise routing and placement through real PDF image streams, never a filename hint.
use lopdf::{Document, Stream, dictionary};
use openplantrace_rs::input::raster::PdfRoute;
use openplantrace_rs::{export, input, model::Options, pipeline};

fn fixture(images: bool, vector: bool, tiled: bool) -> Vec<u8> {
    let mut pdf = Document::with_version("1.7");
    let tree = pdf.new_object_id();
    let mut pixels = vec![255u8; 320 * 240];
    for y in 0..240 {
        for x in 0..320 {
            let wall = (50..54).contains(&x) && (40..190).contains(&y)
                || (250..254).contains(&x) && (40..190).contains(&y)
                || (40..44).contains(&y) && (50..254).contains(&x)
                || (182..186).contains(&y) && ((50..170).contains(&x) || (205..254).contains(&x));
            let stem = (154..158).contains(&x) && (42..118).contains(&y);
            if wall || stem {
                pixels[y * 320 + x] = 90;
            }
            // A hollow furnishing and a visible doorway are part of the source.
            if (x == 110 || x == 140) && (90..120).contains(&y)
                || (y == 90 || y == 119) && (110..141).contains(&x)
            {
                pixels[y * 320 + x] = 0;
            }
        }
    }
    let image=pdf.add_object(Stream::new(dictionary!{"Type"=>"XObject","Subtype"=>"Image","Width"=>320,"Height"=>240,"BitsPerComponent"=>8,"ColorSpace"=>"DeviceGray"},pixels));
    let mut paint = String::new();
    if images {
        paint.push_str(if tiled {
            "q 160 0 0 240 0 0 cm /Im Do Q q 160 0 0 240 160 0 cm /Im Do Q "
        } else {
            "q 320 0 0 240 0 0 cm /Im Do Q "
        });
    }
    if vector {
        paint.push_str("0 G 0.5 w 10 10 m 300 10 l S");
    }
    let content = pdf.add_object(Stream::new(dictionary! {}, paint.into_bytes()));
    let page=pdf.add_object(dictionary!{"Type"=>"Page","Parent"=>tree,"MediaBox"=>vec![0.into(),0.into(),320.into(),240.into()],"Resources"=>dictionary!{"XObject"=>dictionary!{"Im"=>image}},"Contents"=>content});
    pdf.objects.insert(
        tree,
        dictionary! {"Type"=>"Pages","Kids"=>vec![page.into()],"Count"=>1}.into(),
    );
    let catalog = pdf.add_object(dictionary! {"Type"=>"Catalog","Pages"=>tree});
    pdf.trailer.set("Root", catalog);
    let mut bytes = Vec::new();
    pdf.save_to(&mut bytes).unwrap();
    bytes
}

#[test]
fn routing_uses_visible_composed_page_content_and_unions_tiles() {
    for (images, vectors, tiles, route) in [
        (false, true, false, PdfRoute::Vector),
        (true, false, false, PdfRoute::Raster),
        (true, true, true, PdfRoute::Mixed),
    ] {
        let doc =
            input::load_bytes(input::DrawingFormat::Pdf, &fixture(images, vectors, tiles)).unwrap();
        let info = doc.pages[0].pdf_input.as_ref().unwrap();
        assert_eq!(info.route, route);
        assert_eq!(info.image_coverage, if images { 1. } else { 0. });
        assert_eq!(
            info.source_image_ids.len(),
            if !images {
                0
            } else if tiles {
                2
            } else {
                1
            }
        );
        assert_eq!(info.render_width > 0, images);
        assert!(info.raster_source_ids.iter().all(|id| {
            doc.pages[0]
                .primitives
                .iter()
                .any(|p| p.id == *id && p.stroke_width.is_none())
        }));
        input::raster::validate(info, &doc.pages[0].primitives, 320., 240.).unwrap();
    }
}

#[test]
fn raster_material_places_with_open_door_and_saved_evidence_replays() {
    let doc = input::load_bytes(input::DrawingFormat::Pdf, &fixture(true, false, false)).unwrap();
    let scan = pipeline::scan_document(doc, &Options::default(), None, true).unwrap();
    export::validate(&scan).unwrap();
    let page = &scan.pages[0];
    let ready: Vec<_> = page
        .placement_decisions
        .as_ref()
        .unwrap()
        .iter()
        .filter(|d| d.status == openplantrace_rs::placement::WallStatus::Ready)
        .collect();
    assert!(
        ready.len() >= 3,
        "actual solid wall network should be placeable; walls={}, bands={}, decisions={:?}",
        page.walls.len(),
        page.pdf_input.as_ref().unwrap().bands.len(),
        page.placement_decisions
    );
    let place = export::placement(&scan);
    assert!(
        !place["pages"][0]["cornerJunctions"]
            .as_array()
            .unwrap()
            .is_empty(),
        "visible material bends must connect the placement graph"
    );
    assert!(
        place["pages"][0]["cornerJunctions"]
            .as_array()
            .unwrap()
            .iter()
            .any(|j| j["materialAdded"] == false
                && j["polygon"].as_array().is_some_and(Vec::is_empty)),
        "an existing material overlap is a graph contact, not a duplicate solid"
    );
    assert!(
        place["pages"][0]["wallGraph"]["nodes"]
            .as_array()
            .unwrap()
            .iter()
            .any(|n| n["wallIds"].as_array().is_some_and(|ids| ids.len() > 1)),
        "measured contacts must have actual shared graph incidence"
    );
    let graph = &place["pages"][0]["wallGraph"];
    let through = page
        .walls
        .iter()
        .find(|w| {
            (w.start.y - 42.).abs() < 0.2
                && (w.end.y - 42.).abs() < 0.2
                && w.start.x.min(w.end.x) < 100.
                && w.start.x.max(w.end.x) > 200.
        })
        .unwrap();
    let stem = page
        .walls
        .iter()
        .find(|w| (w.start.x - 156.).abs() < 0.2 && (w.end.x - 156.).abs() < 0.2)
        .unwrap();
    assert!(
        graph["nodes"]
            .as_array()
            .unwrap()
            .iter()
            .any(|n| n["wallIds"]
                .as_array()
                .is_some_and(
                    |ids| ids.iter().any(|v| v == &through.id) && ids.iter().any(|v| v == &stem.id)
                )),
        "a measured T must share graph incidence through the existing parent material"
    );
    for barrier in place["pages"][0]["barriers"].as_array().unwrap() {
        let wall = page
            .walls
            .iter()
            .find(|w| w.id == barrier["wallId"].as_str().unwrap())
            .unwrap();
        // Pixel origin is top-left. The lower wall has a 35-unit clear doorway.
        if wall.start.y > 175. && wall.end.y > 175. {
            assert!(wall.start.x.max(wall.end.x) <= 171. || wall.start.x.min(wall.end.x) >= 204.);
        }
        assert!(
            !(wall.start.x > 105.
                && wall.start.x < 145.
                && wall.start.y > 85.
                && wall.start.y < 125.),
            "furniture cannot be a placement wall"
        );
    }
    let mut changed = scan.clone();
    changed.pages[0].pdf_input.as_mut().unwrap().image_coverage = 0.5;
    assert!(export::validate(&changed).is_err());
    let mut changed = scan.clone();
    changed.pages[0].pdf_input.as_mut().unwrap().bands[0].thickness += 1.;
    assert!(export::validate(&changed).is_err());
    let mut changed = scan.clone();
    changed.schema_version = "openplantrace.rust.scan.v8".into();
    assert!(export::validate(&changed).is_err());
    let mut changed = scan.clone();
    changed.pages[0].pdf_input = None;
    assert!(export::validate(&changed).is_err());
}
