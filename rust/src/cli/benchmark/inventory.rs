use openplantrace_rs::model::{Point, Scan};
use serde_json::{Value, json};
use std::collections::BTreeMap;

pub(super) const GROUPS: &[(&str, &str)] = &[
    ("regionMetrics", "regions"),
    ("dimensionMetrics", "dimensions"),
    ("annotationMetrics", "annotations"),
    ("gridAxisMetrics", "gridAxes"),
    ("wallMetrics", "walls"),
    ("roomMetrics", "rooms"),
    ("openingMetrics", "openings"),
    ("objectMetrics", "objects"),
    ("objectGroupMetrics", "objectGroups"),
    ("layerMetrics", "layers"),
];

pub(super) struct Inventory {
    pub counts: BTreeMap<String, usize>,
    pub groups: BTreeMap<&'static str, Vec<Value>>,
    pub calibrated: bool,
    pub has_vector_geometry: bool,
    pub image_only_pages: usize,
}
fn bounds(pts: impl IntoIterator<Item = Point>) -> Value {
    let mut pts = pts.into_iter();
    let Some(first) = pts.next() else {
        return Value::Null;
    };
    let (mut x, mut y, mut r, mut b) = (first.x, first.y, first.x, first.y);
    for p in pts {
        x = x.min(p.x);
        y = y.min(p.y);
        r = r.max(p.x);
        b = b.max(p.y);
    }
    json!({"x":x,"y":y,"width":r-x,"height":b-y})
}
impl Inventory {
    pub fn new(scan: &Scan) -> Self {
        let mut groups: BTreeMap<_, Vec<Value>> =
            GROUPS.iter().map(|(_, k)| (*k, Vec::new())).collect();
        let mut counts: BTreeMap<String, usize> = BTreeMap::new();
        let profile = scan.layer_profile.as_ref();
        let layers = openplantrace_rs::layers::analyze_with_profile(
            profile.map_or("", |p| p.source_format.as_str()),
            profile.map_or(&[], |p| p.overrides.as_slice()),
            scan.pages
                .iter()
                .flat_map(|p| p.primitives.iter().map(move |s| (p.page_number, s))),
        );
        groups.insert(
            "layers",
            layers
                .into_iter()
                .map(|l| {
                    json!({"id":l.name,"label":l.name,"layerCategory":l.likely_category,
                "bounds":l.bounds.map(|b| bounds([b.min,b.max]))})
                })
                .collect(),
        );
        for p in &scan.pages {
            let page = p.page_number;
            let mut add = |kind, mut record: Value| {
                record["pageNumber"] = json!(page);
                groups
                    .get_mut(kind)
                    .expect("registered native detector")
                    .push(record);
            };
            for r in &p.semantics.regions {
                add(
                    "regions",
                    json!({"id":r.id,"regionKind":r.kind,"bounds":bounds([r.bounds.min,r.bounds.max])}),
                );
            }
            for d in &p.semantics.dimensions {
                let b = d.line.as_ref().map(|l| bounds([l.start, l.end]));
                add(
                    "dimensions",
                    json!({"id":d.id,"text":d.text,"dimensionOrientation":d.orientation,"bounds":b}),
                );
            }
            for a in &p.semantics.annotation_blocks {
                add(
                    "annotations",
                    json!({"id":a.id,"label":a.heading,"annotationKind":a.kind}),
                );
            }
            for a in &p.semantics.grid_axes {
                add(
                    "gridAxes",
                    json!({"id":a.id,"label":a.label,"gridAxisOrientation":a.orientation,"bounds":bounds([a.line.start,a.line.end])}),
                );
            }
            for w in &p.walls {
                let mut record = json!({"id":w.id,"wallType":"Unknown"});
                if w.curve.is_none() {
                    record["centerLine"] = json!({"start":w.start,"end":w.end});
                    let u = (w.end - w.start).unit();
                    let n = Point::new(-u.y, u.x) * (w.thickness / 2.);
                    record["bounds"] = bounds([w.start + n, w.start - n, w.end + n, w.end - n]);
                }
                add("walls", record);
            }
            for r in &p.rooms {
                add(
                    "rooms",
                    json!({"id":r.id,"label":r.label,"bounds":bounds(r.polygon.iter().copied())}),
                );
            }
            for o in &p.openings {
                add(
                    "openings",
                    json!({"id":o.id,"openingType":o.kind,"openingOperation":o.operation,"bounds":bounds([o.start,o.end])}),
                );
            }
            let objects: BTreeMap<_, _> = p.objects.iter().map(|o| (o.id.as_str(), o)).collect();
            for o in &p.objects {
                add(
                    "objects",
                    json!({"id":o.id,"objectCategory":o.category,"text":o.name,"bounds":bounds([o.at])}),
                );
            }
            for g in &p.object_groups {
                let members: Vec<_> = g
                    .member_ids
                    .iter()
                    .filter_map(|id| objects.get(id.as_str()))
                    .collect();
                let category = members
                    .first()
                    .filter(|first| members.iter().all(|o| o.category == first.category))
                    .map(|o| &o.category);
                add(
                    "objectGroups",
                    json!({"id":format!("p{page}:group:{}",g.name),"text":g.name,"objectCategory":category,
                    "minCount":g.member_ids.len(),"requiresReview":g.requires_review,"bounds":bounds(members.iter().map(|o|o.at))}),
                );
            }
            for (kind, n) in [
                ("pages", 1),
                ("primitives", p.primitives.len()),
                ("wallNodes", p.nodes.len()),
                ("wallEdges", p.edges.len()),
                ("roomClusters", p.semantics.room_clusters.len()),
                ("gridBaySpacings", p.semantics.grid_bays.len()),
                ("routingItems", p.semantics.routing.len()),
            ] {
                *counts.entry(kind.into()).or_default() += n;
            }
        }
        for (kind, records) in &groups {
            counts.insert((*kind).into(), records.len());
        }
        Self {
            counts,
            groups,
            calibrated: !scan.pages.is_empty()
                && scan.pages.iter().all(|p| p.units_to_millimeters.is_some()),
            has_vector_geometry: scan.pages.iter().flat_map(|p| &p.primitives).any(|p| {
                matches!(
                    p.shape,
                    openplantrace_rs::model::Shape::Line { .. }
                        | openplantrace_rs::model::Shape::Polyline { .. }
                        | openplantrace_rs::model::Shape::Arc { .. }
                )
            }),
            image_only_pages: scan
                .pages
                .iter()
                .filter(|p| {
                    !p.primitives.is_empty()
                        && p.primitives.iter().all(|p| {
                            matches!(p.shape, openplantrace_rs::model::Shape::Image { .. })
                        })
                })
                .count(),
        }
    }
}
