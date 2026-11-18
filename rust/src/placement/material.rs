use super::roundoff;
use crate::model::{BodyEvidence, LayerKind, PageScan, Point, Primitive, Shape, Wall, segments};
use std::collections::{BTreeSet, HashMap, HashSet};

pub(super) type EdgeKey = [(u64, u64); 2];

pub(super) fn edge_key(a: Point, b: Point) -> EdgeKey {
    let mut key = [super::point_key(a), super::point_key(b)];
    key.sort_unstable();
    key
}

pub(super) struct WallMaterial {
    pub authored: bool,
    pub named_fill: bool,
    pub structural: bool,
    pub outlined: bool,
    pub source_ids: Vec<String>,
}

/// A filled source path can vouch for this placement strip only if its actual
/// painted rectangle has the same two ends and the same two wall faces.
fn is_same_strip(w: &Wall, points: &[Point]) -> bool {
    let mut vertices = points;
    if vertices.first() == vertices.last() && !vertices.is_empty() {
        vertices = &vertices[..vertices.len() - 1];
    }
    if vertices.len() != 4 || w.thickness <= 0. {
        return false;
    }
    let axis = (w.end - w.start).unit();
    let offset = Point::new(-axis.y, axis.x) * (w.thickness / 2.);
    let corners = [
        w.start + offset,
        w.end + offset,
        w.end - offset,
        w.start - offset,
    ];
    let tol = roundoff(w);
    (0..4).any(|start| {
        [false, true].into_iter().any(|reverse| {
            (0..4).all(|j| {
                let step = if reverse { 4 - j } else { j };
                vertices[j].distance(corners[(start + step) % 4]) <= tol
            })
        })
    })
}

fn face_interval(w: &Wall, a: Point, b: Point) -> Option<(usize, f64, f64)> {
    let axis = (w.end - w.start).unit();
    let len = w.start.distance(w.end);
    let eps = roundoff(w);
    let a = a - w.start;
    let b = b - w.start;
    let side = a.cross(axis);
    let half = w.thickness * 0.5;
    if (side.abs() - half).abs() > eps || (b.cross(axis) - side).abs() > eps {
        return None;
    }
    let (a, b) = (a.dot(axis), b.dot(axis));
    // Exclude a distinct wall stroke that merely intersects or extends past the paint.
    let (lo, hi) = (a.min(b), a.max(b));
    (hi - lo > eps && lo >= -eps && hi <= len + eps).then_some((
        usize::from(side > 0.),
        lo.max(0.),
        hi.min(len),
    ))
}

fn full_face(w: &Wall, spans: &mut [(f64, f64)]) -> bool {
    spans.sort_by(|a, b| a.0.total_cmp(&b.0));
    let eps = roundoff(w);
    let mut end: f64 = 0.;
    for &(lo, hi) in spans.iter() {
        if lo > end + eps {
            return false;
        }
        end = end.max(hi);
    }
    end + eps >= w.start.distance(w.end)
}

pub(super) struct WallSource<'a> {
    pub primitive: &'a Primitive,
    pub named_wall: bool,
    pub exclusive_wall: bool,
    kind: LayerKind,
}

pub(super) type Sources<'a> = HashMap<&'a str, WallSource<'a>>;

pub(super) fn wall_name(name: &str) -> (bool, bool) {
    let named = crate::layers::is_named_wall(name);
    let exclusive = named
        && name.split(|c: char| !c.is_alphanumeric()).all(|token| {
            matches!(
                crate::model::layer_kind(Some(token)),
                LayerKind::Wall | LayerKind::Unknown
            )
        });
    (named, exclusive)
}

pub(super) fn sources(page: &PageScan) -> Sources<'_> {
    let needed: HashSet<_> = page
        .walls
        .iter()
        .flat_map(|w| w.source_ids.iter().map(String::as_str))
        .chain(
            page.openings
                .iter()
                .flat_map(|o| o.source_ids.iter().map(String::as_str)),
        )
        .collect();
    let mut names = HashMap::new();
    page.primitives
        .iter()
        .filter(|p| needed.contains(p.id.as_str()))
        .map(|p| {
            let kind = p.layer_kind();
            let (named, exclusive) = match p.layer_category_override {
                Some(_) => (kind == LayerKind::Wall, kind == LayerKind::Wall),
                None => {
                    let (wall_name, unambiguous) = *names
                        .entry(p.layer_name())
                        .or_insert_with(|| wall_name(p.layer_name()));
                    (
                        kind == LayerKind::Wall || (kind == LayerKind::Unknown && wall_name),
                        unambiguous,
                    )
                }
            };
            (
                p.id.as_str(),
                WallSource {
                    primitive: p,
                    kind,
                    named_wall: named,
                    exclusive_wall: exclusive,
                },
            )
        })
        .collect()
}

pub(super) fn materials(page: &PageScan, sources: &Sources<'_>) -> Vec<WallMaterial> {
    page.walls
        .iter()
        .map(|w| {
            let mut fill_ids = BTreeSet::new();
            let mut outlines = BTreeSet::new();
            let mut faces = [Vec::new(), Vec::new()];
            let mut named_count = 0;
            let mut nonstructural = false;
            for id in &w.source_ids {
                let Some(source) = sources.get(id.as_str()) else {
                    continue;
                };
                nonstructural |= matches!(
                    source.kind,
                    LayerKind::Object
                        | LayerKind::Window
                        | LayerKind::Door
                        | LayerKind::Annotation
                        | LayerKind::Dimension
                        | LayerKind::Grid
                );
                if !source.named_wall {
                    continue;
                }
                named_count += 1;
                if w.body != BodyEvidence::FilledBody {
                    continue;
                }
                let shape = &source.primitive.shape;
                match shape {
                    Shape::Polyline {
                        points,
                        closed: true,
                        filled: true,
                    } if is_same_strip(w, points) => {
                        fill_ids.insert(id.clone());
                    }
                    Shape::Line { .. } | Shape::Polyline { filled: false, .. } => {
                        for (a, b) in segments(shape) {
                            if let Some((side, lo, hi)) = face_interval(w, a, b) {
                                faces[side].push((lo, hi));
                                outlines.insert(id.clone());
                            }
                        }
                    }
                    _ => {}
                }
            }
            let named_fill = !fill_ids.is_empty();
            let outlined = named_fill && faces.iter_mut().all(|f| full_face(w, f));
            if outlined {
                fill_ids.extend(outlines);
            }
            let structural = !nonstructural
                && (w.body == BodyEvidence::FilledBody
                    || w.body == BodyEvidence::ParallelFaces
                        && named_count == w.source_ids.len()
                        && named_count >= 2);
            WallMaterial {
                authored: w.body == BodyEvidence::ExplicitCenterline && named_count > 0,
                structural,
                named_fill,
                outlined,
                source_ids: fill_ids.into_iter().collect(),
            }
        })
        .collect()
}
