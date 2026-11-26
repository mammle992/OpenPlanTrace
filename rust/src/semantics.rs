//! Page-level interpretation of source geometry and text. Measured dimensions
//! are independent scale witnesses, not an automatic change to calibration.
use crate::model::*;
use serde::{Deserialize, Serialize};
use std::collections::{BTreeMap, BTreeSet};
use std::f64::consts::{PI, TAU};

macro_rules! record {($name:ident {$( $field:ident : $ty:ty ),* $(,)?})=>{
    #[derive(Clone,Debug,Serialize,Deserialize)]
    #[serde(rename_all="camelCase")]
    pub struct $name {$(pub $field:$ty),*}
}}
macro_rules! enumeration {($name:ident {$($variant:ident),*})=>{
    #[derive(Clone,Copy,Debug,PartialEq,Eq,PartialOrd,Ord,Serialize,Deserialize)]
    pub enum $name {$($variant),*}
}}
record!(Bounds {
    min: Point,
    max: Point
});
impl Bounds {
    fn from(p: Point) -> Self {
        Self { min: p, max: p }
    }
    fn add(&mut self, p: Point) {
        self.min.x = self.min.x.min(p.x);
        self.min.y = self.min.y.min(p.y);
        self.max.x = self.max.x.max(p.x);
        self.max.y = self.max.y.max(p.y);
    }
}
enumeration!(RegionKind {
    Sheet,
    DrawingContent,
    MainPlanCandidate,
    MainFloorPlan,
    TitleBlockCandidate,
    NotesCandidate
});
record!(Region{id:String,kind:RegionKind,bounds:Bounds,source_ids:Vec<String>,requires_review:bool});
record!(Span {
    start: Point,
    end: Point
});
enumeration!(AxisOrientation {
    Horizontal,
    Vertical,
    Aligned
});
enumeration!(Unit {
    Millimeter,
    Centimeter,
    Meter,
    Inch,
    Foot,
    DrawingUnit
});
enumeration!(DimensionEvidence {
    UnitHint,
    DimensionLayer,
    TwoFiniteWitnesses,
    NativeDxfEntity
});
record!(NativeDimension{source_id:String,line:Span,text_at:Point,text:String,reported_model_units:Option<f64>,
    reported_mm:Option<f64>,requires_review:bool});
record!(Dimension{id:String,text:String,unit:Unit,measured_mm:f64,orientation:Option<AxisOrientation>,
    line:Option<Span>,line_source:Option<String>,text_sources:Vec<String>,start_witness:Option<String>,
    end_witness:Option<String>,drawing_length:Option<f64>,observed_mm_per_unit:Option<f64>,
    calibrated_length_mm:Option<f64>,calibration_relative_error:Option<f64>,evidence:DimensionEvidence,requires_review:bool});
record!(GridAxis{id:String,orientation:AxisOrientation,line:Span,line_sources:Vec<String>,label:Option<String>,
    label_source:Option<String>,bubble_source:Option<String>,explicit_layer:bool,requires_review:bool});
record!(GridBay{id:String,orientation:AxisOrientation,axis_ids:[String;2],drawing_distance:f64,
    distance_mm:Option<f64>,source_ids:Vec<String>,requires_review:bool});
enumeration!(TitleKind {
    ProjectName,
    SheetNumber,
    SheetTitle,
    Revision,
    IssueDate,
    Scale,
    DrawnBy,
    CheckedBy,
    Discipline
});
record!(TitleField{kind:TitleKind,value:String,source_ids:Vec<String>,requires_review:bool});
enumeration!(NoteKind {
    GeneralNotes,
    Legend,
    RevisionTable,
    Keynotes
});
record!(NoteItem{marker:Option<String>,text:String,source_id:String});
record!(NoteBlock{id:String,kind:NoteKind,heading:String,heading_source:String,items:Vec<NoteItem>,requires_review:bool});
record!(RoomCluster{id:String,room_ids:Vec<String>,opening_ids:Vec<String>,requires_review:bool});
enumeration!(RoutingKind {
    WallBarrierCandidate,
    DoorPassageCandidate,
    WindowCandidate,
    ObjectForReview
});
record!(RoutingItem{kind:RoutingKind,entity_id:String,source_ids:Vec<String>,span:Option<Span>,
    curve:Option<ArcGeometry>,room_ids:Vec<String>,usable_for_routing:bool,requires_review:bool});
enumeration!(SemanticLimit {
    DimensionGridGeometry,
    DimensionGridWork,
    TextGroupingWork,
    ConflictingDimensionLabels
});
record!(Diagnostic {
    kind: SemanticLimit,
    count: usize
});
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PageSemantics {
    pub regions: Vec<Region>,
    pub dimensions: Vec<Dimension>,
    pub native_dimensions: Vec<NativeDimension>,
    pub grid_axes: Vec<GridAxis>,
    pub grid_bays: Vec<GridBay>,
    pub title_fields: Vec<TitleField>,
    pub annotation_blocks: Vec<NoteBlock>,
    pub room_clusters: Vec<RoomCluster>,
    pub routing: Vec<RoutingItem>,
    pub diagnostics: Vec<Diagnostic>,
}

#[derive(Clone, Copy)]
struct Text<'a> {
    id: &'a str,
    at: Point,
    h: f64,
    value: &'a str,
    layer: LayerKind,
    explicit_title: bool,
}
#[derive(Clone, Copy)]
struct Line<'a> {
    id: &'a str,
    a: Point,
    b: Point,
    len: f64,
    dir: Point,
    layer: LayerKind,
}

fn orient(d: Point) -> AxisOrientation {
    if d.y.abs() <= 2f64.to_radians().sin() {
        AxisOrientation::Horizontal
    } else if d.x.abs() <= 2f64.to_radians().sin() {
        AxisOrientation::Vertical
    } else {
        AxisOrientation::Aligned
    }
}
fn text_extent(t: &Text) -> Bounds {
    // Glyph width is unavailable; report the observed anchor.
    Bounds::from(t.at)
}
fn union(a: &mut Option<Bounds>, b: &Bounds) {
    if let Some(a) = a {
        a.add(b.min);
        a.add(b.max);
    } else {
        *a = Some(b.clone());
    }
}
fn arc_bounds(c: ArcGeometry) -> Bounds {
    let mut b = Bounds::from(c.at(0.));
    b.add(c.at(1.));
    for a in [0., PI / 2., PI, 1.5 * PI] {
        let delta = if c.sweep >= 0. {
            (a - c.start_angle).rem_euclid(TAU)
        } else {
            (c.start_angle - a).rem_euclid(TAU)
        };
        if delta <= c.sweep.abs() {
            b.add(c.center + Point::new(a.cos(), a.sin()) * c.radius);
        }
    }
    b
}
pub(crate) fn bounds(s: &Shape) -> Option<Bounds> {
    match s {
        Shape::Line { start, end }
        | Shape::Image {
            min: start,
            max: end,
        } => {
            let mut b = Bounds::from(*start);
            b.add(*end);
            Some(b)
        }
        Shape::Dimension { start, end, at, .. } => {
            let mut b = Bounds::from(*start);
            b.add(*end);
            b.add(*at);
            Some(b)
        }
        Shape::Polyline { points, .. } => {
            let mut b = Bounds::from(*points.first()?);
            for p in points {
                b.add(*p);
            }
            Some(b)
        }
        Shape::Arc {
            center,
            radius,
            start_angle,
            sweep,
        } => Some(arc_bounds(ArcGeometry {
            center: *center,
            radius: *radius,
            start_angle: *start_angle,
            sweep: *sweep,
        })),
        Shape::Text { at, .. } | Shape::Insert { at, .. } => Some(Bounds::from(*at)),
    }
}
fn ids<'a>(v: impl IntoIterator<Item = &'a str>) -> Vec<String> {
    v.into_iter()
        .collect::<BTreeSet<_>>()
        .into_iter()
        .map(str::to_string)
        .collect()
}

/// Bounding-box index for finite line queries. Large envelopes are kept once
/// rather than replicated across an unbounded number of grid cells.
#[derive(Default)]
struct Index {
    cells: BTreeMap<(i32, i32), Vec<usize>>,
    large: Vec<usize>,
}
impl Index {
    fn tile(x: f64) -> i32 {
        (x / 48.).floor() as i32
    }
    fn add(&mut self, id: usize, b: &Bounds) {
        let (x, y) = (Self::tile(b.min.x), Self::tile(b.min.y));
        let (u, v) = (Self::tile(b.max.x), Self::tile(b.max.y));
        if (i64::from(u) - i64::from(x) + 1).saturating_mul(i64::from(v) - i64::from(y) + 1) > 64 {
            self.large.push(id);
            return;
        }
        for i in x..=u {
            for j in y..=v {
                self.cells.entry((i, j)).or_default().push(id);
            }
        }
    }
    fn near(&self, p: Point, r: f64, budget: &mut usize) -> Result<Vec<usize>, ()> {
        let mut out = Vec::new();
        let mut push = |s: &[usize]| -> Result<(), ()> {
            *budget = budget.checked_sub(s.len()).ok_or(())?;
            out.extend_from_slice(s);
            Ok(())
        };
        push(&self.large)?;
        for i in Self::tile(p.x - r)..=Self::tile(p.x + r) {
            for j in Self::tile(p.y - r)..=Self::tile(p.y + r) {
                if let Some(v) = self.cells.get(&(i, j)) {
                    push(v)?;
                }
            }
        }
        out.sort_unstable();
        out.dedup();
        Ok(out)
    }
}

fn positive(s: &str) -> Option<f64> {
    let s = s.trim().replace(',', ".");
    if s.is_empty() || !s.bytes().all(|c| c.is_ascii_digit() || c == b'.') {
        return None;
    }
    s.parse::<f64>().ok().filter(|n| n.is_finite() && *n >= 0.)
}
fn inch_number(s: &str) -> Option<f64> {
    if let Some((n, d)) = s.split_once('/') {
        let (whole, n) = n
            .rsplit_once(' ')
            .map_or((Some(0.), n), |(w, n)| (positive(w), n));
        let d = positive(d)?;
        if d <= 0. {
            return None;
        }
        Some(whole? + positive(n)? / d)
    } else {
        positive(s)
    }
}
fn grouped_mm(s: &str) -> Option<f64> {
    let groups = s.split_whitespace().collect::<Vec<_>>();
    if groups.len() < 2
        || groups[0].len() > 3
        || !groups.iter().all(|x| x.bytes().all(|c| c.is_ascii_digit()))
        || groups[1..].iter().any(|x| x.len() != 3)
    {
        return None;
    }
    positive(&groups.concat())
}
fn measurement(s: &str, explicit_dimension: bool, model_scale: Option<f64>) -> Option<(Unit, f64)> {
    let s = s
        .trim()
        .to_ascii_lowercase()
        .replace('′', "'")
        .replace('″', "\"");
    if s.contains(':') || s.contains('=') {
        return None;
    }
    let base = if let Some((feet, tail)) = s.split_once('\'') {
        let tail = tail
            .trim()
            .trim_start_matches('-')
            .trim()
            .trim_end_matches('"')
            .trim();
        let inches = if tail.is_empty() {
            0.
        } else {
            inch_number(tail)?
        };
        if inches >= 12. {
            return None;
        }
        Some((Unit::Foot, (positive(feet)? * 12. + inches) * 25.4))
    } else if let Some(v) = s.strip_suffix('"').or_else(|| s.strip_suffix(" in")) {
        Some((Unit::Inch, inch_number(v.trim())? * 25.4))
    } else if let Some(v) = s.strip_suffix("mm") {
        Some((Unit::Millimeter, positive(v).or_else(|| grouped_mm(v))?))
    } else if let Some(v) = s.strip_suffix("cm") {
        Some((Unit::Centimeter, positive(v)? * 10.))
    } else if let Some(v) = s.strip_suffix('m') {
        Some((Unit::Meter, positive(v)? * 1000.))
    } else if let Some(v) = grouped_mm(&s) {
        Some((Unit::Millimeter, v))
    } else if explicit_dimension {
        Some((Unit::DrawingUnit, positive(&s)? * model_scale?))
    } else {
        None
    };
    base.filter(|(_, n)| n.is_finite() && *n > 0.)
}

struct DimText<'a> {
    t: Text<'a>,
    text: String,
    ids: Vec<&'a str>,
    unit: Unit,
    mm: f64,
}
fn dimension_texts<'a>(texts: &[Text<'a>], page: &Page) -> Vec<DimText<'a>> {
    let relevant = |t: &Text| matches!(t.layer, LayerKind::Dimension | LayerKind::Unknown);
    let mut ordered: Vec<_> = texts.iter().filter(|t| relevant(t)).copied().collect();
    ordered.sort_by(|a, b| {
        a.at.y
            .total_cmp(&b.at.y)
            .then(a.at.x.total_cmp(&b.at.x))
            .then(a.id.cmp(b.id))
    });
    let mut out = Vec::new();
    let mut i = 0;
    while i < ordered.len() {
        let t = ordered[i];
        i += 1;
        let mut s = t.value.trim().to_owned();
        let mut src = vec![t.id];
        if let Some(u) = ordered.get(i).filter(|u| {
            t.layer == LayerKind::Unknown
                && u.layer == t.layer
                && !s.is_empty()
                && s.len() <= 3
                && s.bytes().all(|c| c.is_ascii_digit())
                && u.value.trim().len() == 3
                && u.value.trim().bytes().all(|c| c.is_ascii_digit())
                && (u.at.y - t.at.y).abs() <= 0.2 * t.h
                && u.at.x > t.at.x
                && u.at.x - t.at.x <= (s.len() as f64 + 1.0) * t.h
        }) {
            s.push(' ');
            s.push_str(u.value.trim());
            src.push(u.id);
            i += 1;
        }
        if let Some((unit, mm)) = measurement(&s, t.layer == LayerKind::Dimension, page.units_to_mm)
        {
            out.push(DimText {
                t,
                text: s,
                ids: src,
                unit,
                mm,
            });
        }
    }
    out
}

fn witness<'a>(
    line: Line<'a>,
    p: Point,
    tolerance: f64,
    lines: &[Line<'a>],
    index: &Index,
    budget: &mut usize,
) -> Result<Option<&'a str>, ()> {
    let mut found = None;
    let mut best = f64::INFINITY;
    for j in index.near(p, tolerance, budget)? {
        let other = lines[j];
        if other.id == line.id || other.dir.dot(line.dir).abs() > 0.18 || other.len < tolerance * 2.
        {
            continue;
        }
        let d = on_segment(p, other.a, other.b).0.distance(p);
        if d <= tolerance && (d < best || (d == best && Some(other.id) < found)) {
            found = Some(other.id);
            best = d;
        }
    }
    Ok(found)
}

fn dimensions(
    page: &Page,
    texts: &[Text],
    lines: &[Line],
    line_index: &Index,
    scale: Option<f64>,
    budget: &mut usize,
) -> Result<(Vec<Dimension>, usize), ()> {
    let mut out = Vec::new();
    let mut selected: BTreeMap<usize, Vec<usize>> = BTreeMap::new();
    for d in dimension_texts(texts, page) {
        let mut dim = Dimension {
            id: format!("page:{}:dimension:{}", page.number, out.len() + 1),
            text: d.text,
            unit: d.unit,
            measured_mm: d.mm,
            orientation: None,
            line: None,
            line_source: None,
            text_sources: ids(d.ids),
            start_witness: None,
            end_witness: None,
            drawing_length: None,
            observed_mm_per_unit: None,
            calibrated_length_mm: None,
            calibration_relative_error: None,
            evidence: DimensionEvidence::UnitHint,
            requires_review: true,
        };
        let margin = (d.t.h * 2.5).clamp(12., 48.);
        let mut best = None;
        let mut rank = f64::NEG_INFINITY;
        let mut ambiguous = false;
        for i in line_index.near(d.t.at, margin, budget)? {
            let l = lines[i];
            if l.len < 8. || !matches!(l.layer, LayerKind::Unknown | LayerKind::Dimension) {
                continue;
            }
            let along = (d.t.at - l.a).dot(l.dir);
            if along < 0. || along > l.len {
                continue;
            }
            let offset = (d.t.at - l.a).cross(l.dir).abs();
            if offset > margin {
                continue;
            }
            let trusted = l.layer == LayerKind::Dimension;
            let err = scale.map(|k| (l.len * k - d.mm).abs() / d.mm);
            if err.is_some_and(|e| e > if trusted { 0.35 } else { 0.18 }) {
                continue;
            }
            let tol = (d.t.h * 0.20).clamp(1.5, 4.);
            let a = witness(l, l.a, tol, lines, line_index, budget)?;
            let b = witness(l, l.b, tol, lines, line_index, budget)?;
            let both = a.is_some() && b.is_some() && a != b;
            if !trusted && !both {
                continue;
            }
            let score = if both { 2. } else { 0. } + if trusted { 1. } else { 0. }
                - offset / margin
                - err.unwrap_or(0.) * 2.;
            if score > rank + 1e-9 {
                best = Some((i, a, b, both, err));
                rank = score;
                ambiguous = false;
            } else if (score - rank).abs() < 1e-9 {
                ambiguous = true;
            }
        }
        if let Some((i, a, b, both, err)) = best.filter(|_| !ambiguous) {
            let l = lines[i];
            dim.line = Some(Span {
                start: l.a,
                end: l.b,
            });
            dim.line_source = Some(l.id.into());
            dim.orientation = Some(orient(l.dir));
            dim.start_witness = a.map(str::to_string);
            dim.end_witness = b.map(str::to_string);
            dim.drawing_length = Some(l.len);
            dim.observed_mm_per_unit = Some(d.mm / l.len);
            dim.calibrated_length_mm = scale.map(|k| l.len * k);
            dim.calibration_relative_error = err;
            dim.evidence = if both {
                DimensionEvidence::TwoFiniteWitnesses
            } else {
                DimensionEvidence::DimensionLayer
            };
            selected.entry(i).or_default().push(out.len());
        }
        if dim.line.is_some() || d.t.layer == LayerKind::Dimension {
            out.push(dim);
        }
    }
    let mut duplicate = BTreeSet::new();
    let mut conflicts = 0;
    for group in selected.into_values().filter(|v| v.len() > 1) {
        let first = group[0];
        let mm = out[first].measured_mm;
        if group
            .iter()
            .all(|&j| (out[j].measured_mm - mm).abs() <= mm * 1e-9)
        {
            for j in group.into_iter().skip(1) {
                let extra = out[j].text_sources.clone();
                out[first].text_sources.extend(extra);
                duplicate.insert(j);
            }
            out[first].text_sources.sort();
            out[first].text_sources.dedup();
        } else {
            conflicts += 1;
            for j in group {
                let d = &mut out[j];
                d.line = None;
                d.line_source = None;
                d.orientation = None;
                d.start_witness = None;
                d.end_witness = None;
                d.drawing_length = None;
                d.observed_mm_per_unit = None;
                d.calibrated_length_mm = None;
                d.calibration_relative_error = None;
                d.evidence = DimensionEvidence::UnitHint;
            }
        }
    }
    Ok((
        out.into_iter()
            .enumerate()
            .filter_map(|(j, d)| (!duplicate.contains(&j)).then_some(d))
            .collect(),
        conflicts,
    ))
}

fn grid_label(s: &str) -> bool {
    let s = s.trim();
    !s.is_empty()
        && s.len() <= 3
        && s.bytes().all(|c| c.is_ascii_alphanumeric())
        && (s.len() <= 2 || s.bytes().any(|c| c.is_ascii_digit()))
        && !["MM", "CM", "FT", "IN"].contains(&s.to_ascii_uppercase().as_str())
}
fn bubble_contains(shape: &Shape, p: Point) -> bool {
    match shape {
        Shape::Polyline {
            points,
            closed: true,
            filled: false,
        } => inside(p, points),
        Shape::Arc {
            center,
            radius,
            sweep,
            ..
        } => sweep.abs() >= TAU - 1e-5 && p.distance(*center) <= *radius,
        _ => false,
    }
}
fn grid(
    page: &Page,
    texts: &[Text],
    lines: &[Line],
    scale: Option<f64>,
    budget: &mut usize,
) -> Result<(Vec<GridAxis>, Vec<GridBay>), ()> {
    let mut labels = Index::default();
    for (i, t) in texts.iter().enumerate() {
        if grid_label(t.value) && matches!(t.layer, LayerKind::Grid | LayerKind::Unknown) {
            labels.add(i, &Bounds::from(t.at));
        }
    }
    let mut bubbles = Index::default();
    for (i, p) in page.primitives.iter().enumerate() {
        if !matches!(p.layer_kind(), LayerKind::Unknown | LayerKind::Grid)
            || !matches!(
                p.shape,
                Shape::Polyline {
                    closed: true,
                    filled: false,
                    ..
                } | Shape::Arc { .. }
            )
        {
            continue;
        }
        if let Some(b) = bounds(&p.shape) {
            let dx = b.max.x - b.min.x;
            let dy = b.max.y - b.min.y;
            if (4. ..=48.).contains(&dx)
                && (4. ..=48.).contains(&dy)
                && dx.min(dy) / dx.max(dy) >= 0.6
            {
                bubbles.add(i, &b);
            }
        }
    }
    let mut axes = Vec::new();
    for &l in lines {
        let orientation = orient(l.dir);
        if orientation == AxisOrientation::Aligned
            || !matches!(l.layer, LayerKind::Grid | LayerKind::Unknown)
        {
            continue;
        }
        let grid = l.layer == LayerKind::Grid;
        let extent = if orientation == AxisOrientation::Vertical {
            page.height
        } else {
            page.width
        };
        if l.len < 60f64.max(0.25 * extent) {
            continue;
        }
        let mut candidates = Vec::new();
        for end in [l.a, l.b] {
            for ti in labels.near(end, 48., budget)? {
                let t = texts[ti];
                let distance = end.distance(t.at);
                if distance > 48. || (t.at - end).cross(l.dir).abs() > t.h.max(6.) * 1.5 {
                    continue;
                }
                let mut bubble = None;
                for pi in bubbles.near(t.at, 0., budget)? {
                    let p = &page.primitives[pi];
                    if p.id != l.id && bubble_contains(&p.shape, t.at) {
                        bubble = Some(p.id.clone());
                        break;
                    }
                }
                if grid || bubble.is_some() {
                    candidates.push((distance, ti, bubble));
                }
            }
        }
        candidates.sort_by(|a, b| a.0.total_cmp(&b.0).then(texts[a.1].id.cmp(texts[b.1].id)));
        let candidate = candidates.first();
        if !grid && candidate.is_none() {
            continue;
        }
        // Different endpoint labels cannot be silently reconciled.
        let ambiguous = candidate.is_some_and(|x| {
            candidates
                .iter()
                .any(|y| texts[y.1].value.trim() != texts[x.1].value.trim())
        });
        if !grid && ambiguous {
            continue;
        }
        let selected = candidate.filter(|_| !ambiguous);
        axes.push(GridAxis {
            id: String::new(),
            orientation,
            line: Span {
                start: l.a,
                end: l.b,
            },
            line_sources: vec![l.id.into()],
            label: selected.map(|c| texts[c.1].value.trim().into()),
            label_source: selected.map(|c| texts[c.1].id.into()),
            bubble_source: selected.and_then(|c| c.2.clone()),
            explicit_layer: grid,
            requires_review: true,
        });
    }
    let coordinate = |x: &GridAxis| {
        if x.orientation == AxisOrientation::Vertical {
            (x.line.start.x + x.line.end.x) * 0.5
        } else {
            (x.line.start.y + x.line.end.y) * 0.5
        }
    };
    let span = |x: &GridAxis| {
        let (a, b) = if x.orientation == AxisOrientation::Vertical {
            (x.line.start.y, x.line.end.y)
        } else {
            (x.line.start.x, x.line.end.x)
        };
        (a.min(b), a.max(b))
    };
    axes.sort_by(|a, b| {
        a.orientation
            .cmp(&b.orientation)
            .then(coordinate(a).total_cmp(&coordinate(b)))
            .then(span(a).0.total_cmp(&span(b).0))
            .then(a.line_sources.cmp(&b.line_sources))
    });
    let mut merged: Vec<GridAxis> = Vec::new();
    for a in axes {
        if let Some(prev) = merged.last_mut() {
            let (p, q) = span(prev);
            let (u, v) = span(&a);
            if a.orientation == prev.orientation
                && (coordinate(&a) - coordinate(prev)).abs() < 1e-6
                && u <= q + 8.
                && v >= p - 8.
                && prev.label == a.label
            {
                let pos = coordinate(prev);
                let point = |t| {
                    if prev.orientation == AxisOrientation::Vertical {
                        Point::new(pos, t)
                    } else {
                        Point::new(t, pos)
                    }
                };
                prev.line = Span {
                    start: point(p.min(u)),
                    end: point(q.max(v)),
                };
                prev.line_sources.extend(a.line_sources);
                prev.line_sources.sort();
                prev.line_sources.dedup();
                continue;
            }
        }
        merged.push(a);
    }
    for (i, a) in merged.iter_mut().enumerate() {
        a.id = format!("page:{}:grid:{}", page.number, i + 1);
    }
    let mut bays = Vec::new();
    for w in merged.windows(2) {
        let a = &w[0];
        let b = &w[1];
        let (a0, a1) = span(a);
        let (b0, b1) = span(b);
        let d = coordinate(b) - coordinate(a);
        if a.orientation != b.orientation || d <= 3. || a1.min(b1) <= a0.max(b0) {
            continue;
        }
        bays.push(GridBay {
            id: format!("page:{}:bay:{}", page.number, bays.len() + 1),
            orientation: a.orientation,
            axis_ids: [a.id.clone(), b.id.clone()],
            drawing_distance: d,
            distance_mm: scale.map(|s| s * d),
            source_ids: ids(a
                .line_sources
                .iter()
                .chain(&b.line_sources)
                .map(String::as_str)),
            requires_review: true,
        });
    }
    Ok((merged, bays))
}

fn title_label(s: &str) -> Option<TitleKind> {
    Some(
        match s.trim().trim_end_matches('.').to_ascii_uppercase().as_str() {
            "PROJECT" | "PROJECT NAME" => TitleKind::ProjectName,
            "SHEET NO" | "SHEET NUMBER" | "DRAWING NO" | "DRAWING NUMBER" => TitleKind::SheetNumber,
            "TITLE" | "SHEET TITLE" | "DRAWING TITLE" => TitleKind::SheetTitle,
            "REVISION" | "REV" => TitleKind::Revision,
            "ISSUE DATE" | "DATE" => TitleKind::IssueDate,
            "SCALE" => TitleKind::Scale,
            "DRAWN BY" => TitleKind::DrawnBy,
            "CHECKED BY" => TitleKind::CheckedBy,
            "DISCIPLINE" => TitleKind::Discipline,
            _ => return None,
        },
    )
}
fn title(s: &str) -> Option<(TitleKind, &str)> {
    if let Some((prefix, value)) = s.trim().split_once(char::is_whitespace) {
        if prefix.eq_ignore_ascii_case("rev")
            && !value.is_empty()
            && value.len() <= 6
            && value.bytes().all(|c| c.is_ascii_alphanumeric())
        {
            return Some((TitleKind::Revision, value));
        }
    }
    let (label, value) = s.split_once(':').unwrap_or((s, ""));
    Some((title_label(label)?, value.trim()))
}
fn heading(s: &str) -> Option<NoteKind> {
    Some(
        match s.trim_end_matches(':').trim().to_ascii_uppercase().as_str() {
            "GENERAL NOTES" | "NOTES" | "DRAWING NOTES" => NoteKind::GeneralNotes,
            "SYMBOL LEGEND" | "LEGEND" => NoteKind::Legend,
            "REVISION HISTORY" | "REVISION TABLE" | "REVISIONS" => NoteKind::RevisionTable,
            "KEYNOTES" | "KEY NOTES" => NoteKind::Keynotes,
            _ => return None,
        },
    )
}
fn annotations(
    page: &Page,
    texts: &[Text],
    budget: &mut usize,
) -> Result<(Vec<TitleField>, Vec<NoteBlock>, Vec<Region>), ()> {
    let mut fields = Vec::new();
    let mut notes = Vec::new();
    let mut regions = Vec::new();
    let mut recognized = Vec::new();
    let eligible = |t: &Text| matches!(t.layer, LayerKind::Unknown | LayerKind::Annotation);
    for &t in texts.iter().filter(|t| eligible(t)) {
        if let Some((kind, value)) = title(t.value) {
            let mut source_ids = vec![t.id.to_string()];
            let mut b = text_extent(&t);
            let value = if !value.is_empty() {
                value
            } else {
                *budget = budget.checked_sub(texts.len()).ok_or(())?;
                let Some(v) = texts
                    .iter()
                    .filter(|v| {
                        eligible(v)
                            && v.id != t.id
                            && v.layer == t.layer
                            && v.at.x > t.at.x
                            && v.at.x - t.at.x <= t.h * 16.
                            && (v.at.y - t.at.y).abs() <= t.h * 0.55
                            && title(v.value).is_none()
                            && heading(v.value).is_none()
                    })
                    .min_by(|a, b| a.at.x.total_cmp(&b.at.x).then(a.id.cmp(b.id)))
                else {
                    continue;
                };
                b.add(v.at);
                source_ids.push(v.id.to_string());
                v.value.trim()
            };
            if !value.is_empty() {
                recognized.push((
                    t,
                    TitleField {
                        kind,
                        value: value.into(),
                        source_ids,
                        requires_review: true,
                    },
                    b,
                ));
            }
        }
    }
    // Require an independent nearby field for unlayered PDF text; an isolated
    // "date:" elsewhere in a drawing is not itself a title block.
    let mut title_bounds = None;
    let mut title_ids = BTreeSet::new();
    for (t, field, b) in &recognized {
        if !t.explicit_title {
            *budget = budget.checked_sub(recognized.len()).ok_or(())?;
            if !recognized.iter().any(|(u, f, _)| {
                f.kind != field.kind
                    && (u.at.x - t.at.x).abs() <= t.h.max(u.h) * 28.
                    && (u.at.y - t.at.y).abs() <= t.h.max(u.h) * 16.
            }) {
                continue;
            }
        }
        union(&mut title_bounds, b);
        title_ids.extend(field.source_ids.iter().cloned());
        fields.push(field.clone());
    }
    if let Some(b) = title_bounds {
        regions.push(Region {
            id: format!("page:{}:title-block", page.number),
            kind: RegionKind::TitleBlockCandidate,
            bounds: b,
            source_ids: title_ids.into_iter().collect(),
            requires_review: true,
        });
    }
    for t in texts.iter().filter(|t| eligible(t)) {
        let Some(kind) = heading(t.value) else {
            continue;
        };
        *budget = budget.checked_sub(texts.len()).ok_or(())?;
        let mut rows: Vec<_> = texts
            .iter()
            .filter(|u| {
                eligible(u)
                    && u.id != t.id
                    && u.at.y > t.at.y
                    && (u.at.x - t.at.x).abs() <= t.h * 2.5
                    && u.at.y - t.at.y <= t.h * 50.
            })
            .collect();
        rows.sort_by(|a, b| {
            a.at.y
                .total_cmp(&b.at.y)
                .then(a.at.x.total_cmp(&b.at.x))
                .then(a.id.cmp(b.id))
        });
        let mut last = t.at.y;
        let mut items = Vec::new();
        let mut b = text_extent(t);
        for u in rows {
            if u.at.y - last > t.h.max(u.h) * 3.0
                || heading(u.value).is_some()
                || title(u.value).is_some()
            {
                break;
            }
            let s = u.value.trim();
            if s.is_empty() {
                continue;
            }
            let (first, rest) = s.split_once(char::is_whitespace).unwrap_or((s, ""));
            let marker = first.trim_end_matches(['.', ')', ':']);
            let marked = !rest.is_empty()
                && ((marker.len() <= 4
                    && !marker.is_empty()
                    && marker.bytes().all(|c| c.is_ascii_digit()))
                    || (kind == NoteKind::Legend
                        && marker.len() <= 3
                        && marker.bytes().all(|c| c.is_ascii_alphanumeric())));
            // Without a note layer, accept an unmarked row only inside a
            // heading-led legend or revision table; room names are not notes.
            if u.layer == LayerKind::Unknown
                && !marked
                && matches!(kind, NoteKind::GeneralNotes | NoteKind::Keynotes)
            {
                break;
            }
            items.push(NoteItem {
                marker: marked.then(|| marker.into()),
                text: if marked { rest.trim() } else { s }.into(),
                source_id: u.id.into(),
            });
            b.add(u.at);
            last = u.at.y;
        }
        let source_ids =
            ids(std::iter::once(t.id).chain(items.iter().map(|i| i.source_id.as_str())));
        let id = format!("page:{}:notes:{}", page.number, notes.len() + 1);
        regions.push(Region {
            id: id.clone(),
            kind: RegionKind::NotesCandidate,
            bounds: b,
            source_ids,
            requires_review: true,
        });
        notes.push(NoteBlock {
            id,
            kind,
            heading: t.value.into(),
            heading_source: t.id.into(),
            items,
            requires_review: true,
        });
    }
    Ok((fields, notes, regions))
}

fn cluster_root(parents: &mut [usize], mut idx: usize) -> usize {
    while idx != parents[idx] {
        parents[idx] = parents[parents[idx]];
        idx = parents[idx];
    }
    idx
}
pub(crate) fn wall_route(w: &Wall) -> RoutingItem {
    RoutingItem {
        kind: RoutingKind::WallBarrierCandidate,
        entity_id: w.id.clone(),
        source_ids: w.source_ids.clone(),
        span: w.curve.is_none().then_some(Span {
            start: w.start,
            end: w.end,
        }),
        curve: w.curve,
        room_ids: Vec::new(),
        usable_for_routing: false,
        requires_review: true,
    }
}

fn project(
    page: u32,
    walls: &[Wall],
    rooms: &[Room],
    openings: &[Opening],
    objects: &[Object],
    connections: &[Connection],
) -> (Vec<RoomCluster>, Vec<RoutingItem>) {
    let mut parents: Vec<_> = (0..rooms.len()).collect();
    let room_index: BTreeMap<_, _> = rooms
        .iter()
        .enumerate()
        .map(|(i, r)| (r.id.as_str(), i))
        .collect();
    let links: BTreeMap<_, _> = connections
        .iter()
        .map(|c| (c.opening_id.as_str(), &c.room_ids))
        .collect();
    let mut routing: Vec<_> = walls.iter().map(wall_route).collect();
    let mut observed = Vec::new();
    for o in openings {
        let door = o.kind == OpeningKind::Door;
        let room_ids: Vec<_> = links
            .get(o.id.as_str())
            .into_iter()
            .flat_map(|ids| ids.iter())
            .filter(|id| room_index.contains_key(id.as_str()))
            .cloned()
            .collect::<BTreeSet<_>>()
            .into_iter()
            .collect();
        if door && room_ids.len() == 2 {
            let a = room_index[room_ids[0].as_str()];
            let b = room_index[room_ids[1].as_str()];
            let x = cluster_root(&mut parents, a);
            let y = cluster_root(&mut parents, b);
            parents[x] = y;
            observed.push((a, o.id.clone()));
        }
        routing.push(RoutingItem {
            kind: if door {
                RoutingKind::DoorPassageCandidate
            } else {
                RoutingKind::WindowCandidate
            },
            entity_id: o.id.clone(),
            source_ids: o.source_ids.clone(),
            span: Some(Span {
                start: o.start,
                end: o.end,
            }),
            curve: None,
            room_ids,
            usable_for_routing: false,
            requires_review: true,
        });
    }
    for o in objects {
        routing.push(RoutingItem {
            kind: RoutingKind::ObjectForReview,
            entity_id: o.id.clone(),
            source_ids: o.source_ids.clone(),
            span: None,
            curve: None,
            room_ids: o
                .room_id
                .iter()
                .filter(|id| room_index.contains_key(id.as_str()))
                .cloned()
                .collect(),
            usable_for_routing: false,
            requires_review: true,
        });
    }
    let mut groups = BTreeMap::<usize, (Vec<String>, Vec<String>)>::new();
    for (i, r) in rooms.iter().enumerate() {
        groups
            .entry(cluster_root(&mut parents, i))
            .or_default()
            .0
            .push(r.id.clone());
    }
    for (i, id) in observed {
        groups
            .entry(cluster_root(&mut parents, i))
            .or_default()
            .1
            .push(id);
    }
    let mut groups: Vec<_> = groups.into_values().collect();
    for (r, o) in &mut groups {
        r.sort();
        o.sort();
    }
    groups.sort();
    let clusters = groups
        .into_iter()
        .enumerate()
        .map(|(i, (room_ids, opening_ids))| RoomCluster {
            id: format!("page:{page}:room-cluster:{}", i + 1),
            room_ids,
            opening_ids,
            requires_review: true,
        })
        .collect();
    (clusters, routing)
}

/// Confirm the main drawing area when one connected group of named architectural rooms
/// occupies most of the page's observed wall envelope. Isolated details stay outside it.
pub(crate) fn authored_plan(
    cluster_count: usize,
    walls: &[Wall],
    rooms: &[Room],
    primitives: &[Primitive],
    envelope: &Bounds,
) -> Option<(Bounds, Vec<String>)> {
    if cluster_count != 1
        || rooms.is_empty()
        || !walls
            .iter()
            .any(|w| w.body == BodyEvidence::ExplicitCenterline)
    {
        return None;
    }
    let named: BTreeMap<_, _> = primitives
        .iter()
        .filter_map(|p| match &p.shape {
            Shape::Text { value, .. } if p.layer_kind() == LayerKind::Room => {
                Some((p.id.as_str(), value.trim()))
            }
            _ => None,
        })
        .collect();
    let authored: BTreeMap<_, _> = walls
        .iter()
        .filter(|w| w.body == BodyEvidence::ExplicitCenterline)
        .map(|w| (w.id.as_str(), w))
        .collect();
    let mut sources = BTreeSet::new();
    let mut area = 0.;
    let mut extent = None;
    for r in rooms {
        let label_id = r.label_source_id.as_deref()?;
        let label = r.label.as_deref()?.trim();
        if label.is_empty()
            || !named.get(label_id)?.eq_ignore_ascii_case(label)
            || r.polygon.len() < 3
            || r.wall_ids.len() < 3
            || r.area <= 0.
        {
            return None;
        }
        for id in &r.wall_ids {
            let wall = authored.get(id.as_str())?;
            sources.extend(wall.source_ids.iter().map(String::as_str));
        }
        sources.insert(label_id);
        for p in &r.polygon {
            union(&mut extent, &Bounds::from(*p))
        }
        area += r.area;
    }
    let envelope_area = (envelope.max.x - envelope.min.x) * (envelope.max.y - envelope.min.y);
    if envelope_area <= 0. || area <= 0.5 * envelope_area {
        return None;
    }
    Some((extent?, sources.into_iter().map(str::to_owned).collect()))
}

pub(crate) fn wall_envelope(walls: &[Wall]) -> Option<Bounds> {
    let mut main = None;
    for w in walls {
        let b = if let Some(c) = w.curve {
            arc_bounds(c)
        } else {
            let mut b = Bounds::from(w.start);
            b.add(w.end);
            b
        };
        union(&mut main, &b);
    }
    main
}

/// Interpret source text/geometry; never mutates the selected bodies, graph,
/// room polygons, opening ownership or calibration. Inferred items are review
/// candidates. Exhausted work budgets are explicit and discard partial matches.
pub fn interpret(
    page: &Page,
    walls: &[Wall],
    rooms: &[Room],
    openings: &[Opening],
    objects: &[Object],
    connections: &[Connection],
    scale: Option<f64>,
) -> PageSemantics {
    let scale = scale.filter(|s| s.is_finite() && *s > 0.);
    let mut out = PageSemantics::default();
    let mut texts = Vec::new();
    let mut lines = Vec::new();
    let mut too_many = false;
    let mut content = None;
    let mut all_ids = Vec::new();
    let wall_sources: BTreeSet<_> = walls
        .iter()
        .flat_map(|w| w.source_ids.iter().map(String::as_str))
        .collect();
    for primitive in &page.primitives {
        if let Some(b) = bounds(&primitive.shape) {
            union(&mut content, &b);
            all_ids.push(primitive.id.clone());
        }
        let layer = primitive.layer_kind();
        if let Shape::Dimension {
            start,
            end,
            at,
            value,
            measurement: reported,
        } = &primitive.shape
        {
            let recorded = reported.filter(|r| r.is_finite() && *r > 0.);
            // DXF measurement attributes use model units. A printed drawing
            // scale or CLI override is *not* a source-unit declaration.
            let reported_mm = recorded
                .zip(page.units_to_mm)
                .map(|(r, k)| r * k)
                .filter(|s| s.is_finite() && *s > 0.);
            let line = Span {
                start: *start,
                end: *end,
            };
            out.native_dimensions.push(NativeDimension {
                source_id: primitive.id.clone(),
                line: line.clone(),
                text_at: *at,
                text: value.clone(),
                reported_model_units: recorded,
                reported_mm,
                requires_review: true,
            });
            let parsed = measurement(value, true, page.units_to_mm)
                .or_else(|| reported_mm.map(|mm| (Unit::DrawingUnit, mm)));
            if let Some((unit, mm)) = parsed {
                let len = start.distance(*end);
                if len > 0. && len.is_finite() {
                    out.dimensions.push(Dimension {
                        id: format!(
                            "page:{}:native-dimension:{}",
                            page.number,
                            out.native_dimensions.len()
                        ),
                        text: value.clone(),
                        unit,
                        measured_mm: mm,
                        orientation: Some(orient((*end - *start).unit())),
                        line: Some(line),
                        line_source: Some(primitive.id.clone()),
                        text_sources: vec![primitive.id.clone()],
                        start_witness: None,
                        end_witness: None,
                        drawing_length: Some(len),
                        observed_mm_per_unit: Some(mm / len),
                        calibrated_length_mm: scale.map(|k| len * k),
                        calibration_relative_error: scale.map(|k| (len * k - mm).abs() / mm),
                        evidence: DimensionEvidence::NativeDxfEntity,
                        requires_review: true,
                    });
                }
            }
            continue;
        }
        if let Shape::Text { at, value, height } = &primitive.shape {
            let explicit_title = primitive
                .layer
                .as_deref()
                .unwrap_or("")
                .split(|c: char| !c.is_ascii_alphanumeric())
                .any(|n| n.eq_ignore_ascii_case("title") || n.eq_ignore_ascii_case("titleblock"));
            texts.push(Text {
                id: &primitive.id,
                at: *at,
                h: height.abs().max(1.),
                value,
                layer,
                explicit_title,
            });
            continue;
        }
        if too_many
            || wall_sources.contains(primitive.id.as_str())
            || !matches!(
                layer,
                LayerKind::Unknown | LayerKind::Grid | LayerKind::Dimension
            )
            || matches!(primitive.shape, Shape::Polyline { filled: true, .. })
        {
            continue;
        }
        for (a, b) in segments(&primitive.shape) {
            let len = a.distance(b);
            if len > 1e-7 && len.is_finite() {
                if lines.len() >= 100_000 {
                    too_many = true;
                    break;
                }
                lines.push(Line {
                    id: &primitive.id,
                    a,
                    b,
                    len,
                    dir: (b - a) * (1. / len),
                    layer,
                });
            }
        }
    }
    out.regions.push(Region {
        id: format!("page:{}:sheet", page.number),
        kind: RegionKind::Sheet,
        bounds: Bounds {
            min: Point::default(),
            max: Point::new(page.width, page.height),
        },
        source_ids: Vec::new(),
        requires_review: false,
    });
    if let Some(b) = content {
        out.regions.push(Region {
            id: format!("page:{}:drawing", page.number),
            kind: RegionKind::DrawingContent,
            bounds: b,
            source_ids: all_ids,
            requires_review: true,
        });
    }
    if let Some(b) = wall_envelope(walls) {
        out.regions.push(Region {
            id: format!("page:{}:main", page.number),
            kind: RegionKind::MainPlanCandidate,
            bounds: b,
            source_ids: ids(wall_sources),
            requires_review: true,
        });
    }
    if too_many || texts.len() > 50_000 {
        out.diagnostics.push(Diagnostic {
            kind: SemanticLimit::DimensionGridGeometry,
            count: lines.len().max(texts.len()),
        });
    } else {
        let mut index = Index::default();
        for (i, l) in lines.iter().enumerate() {
            let mut b = Bounds::from(l.a);
            b.add(l.b);
            index.add(i, &b);
        }
        let mut budget = 8_000_000;
        match dimensions(page, &texts, &lines, &index, scale, &mut budget) {
            Ok((mut v, n)) => {
                out.dimensions.append(&mut v);
                if n > 0 {
                    out.diagnostics.push(Diagnostic {
                        kind: SemanticLimit::ConflictingDimensionLabels,
                        count: n,
                    });
                }
            }
            Err(()) => out.diagnostics.push(Diagnostic {
                kind: SemanticLimit::DimensionGridWork,
                count: lines.len(),
            }),
        };
        let mut grid_budget = 8_000_000;
        match grid(page, &texts, &lines, scale, &mut grid_budget) {
            Ok((a, b)) => {
                out.grid_axes = a;
                out.grid_bays = b;
            }
            Err(()) => out.diagnostics.push(Diagnostic {
                kind: SemanticLimit::DimensionGridWork,
                count: lines.len(),
            }),
        };
    }
    let mut budget = 4_000_000;
    match annotations(page, &texts, &mut budget) {
        Ok((f, n, r)) => {
            out.title_fields = f;
            out.annotation_blocks = n;
            out.regions.extend(r);
        }
        Err(()) => out.diagnostics.push(Diagnostic {
            kind: SemanticLimit::TextGroupingWork,
            count: texts.len(),
        }),
    };
    (out.room_clusters, out.routing) =
        project(page.number, walls, rooms, openings, objects, connections);
    if let Some(main) = out
        .regions
        .iter_mut()
        .find(|r| r.kind == RegionKind::MainPlanCandidate)
        && let Some((bounds, sources)) = authored_plan(
            out.room_clusters.len(),
            walls,
            rooms,
            &page.primitives,
            &main.bounds,
        )
    {
        main.kind = RegionKind::MainFloorPlan;
        main.bounds = bounds;
        main.source_ids = sources;
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    fn p(x: f64, y: f64) -> Point {
        Point::new(x, y)
    }
    fn primitive(id: &str, l: Option<&str>, shape: Shape) -> Primitive {
        Primitive {
            layer_category_override: None,
            stroke_width: None,
            id: id.into(),
            layer: l.map(str::to_string),
            shape,
        }
    }
    fn line(id: &str, l: Option<&str>, a: Point, b: Point) -> Primitive {
        primitive(id, l, Shape::Line { start: a, end: b })
    }
    fn text(id: &str, l: Option<&str>, s: &str, at: Point) -> Primitive {
        primitive(
            id,
            l,
            Shape::Text {
                at,
                value: s.into(),
                height: 16.,
            },
        )
    }
    fn page(primitives: Vec<Primitive>) -> Page {
        Page {
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
            width: 800.,
            height: 600.,
            units_to_mm: None,
            primitives,
        }
    }
    fn scan(items: Vec<Primitive>, scale: Option<f64>) -> PageSemantics {
        interpret(&page(items), &[], &[], &[], &[], &[], scale)
    }
    #[test]
    fn main_plan_needs_a_dominant_source_named_room_with_authored_walls() {
        let corners = [p(70., 60.), p(220., 60.), p(220., 210.), p(70., 210.)];
        let mut entities: Vec<_> = (0..4)
            .map(|i| {
                line(
                    &format!("s{i}"),
                    Some("A-WALL"),
                    corners[i],
                    corners[(i + 1) % 4],
                )
            })
            .collect();
        let run = |entities| {
            crate::pipeline::scan_document(
                Document {
                    pages: vec![page(entities)],
                    warnings: vec![],
                },
                &Options::default(),
                None,
                false,
            )
            .unwrap()
        };
        let missing = run(entities.clone());
        assert!(
            missing.pages[0]
                .semantics
                .regions
                .iter()
                .all(|r| r.kind != RegionKind::MainFloorPlan)
        );
        entities.push(text("name", Some("A-ROOM"), "WORKSHOP", p(120., 125.)));
        let mut confirmed = run(entities.clone());
        let floor = confirmed.pages[0]
            .semantics
            .regions
            .iter()
            .find(|r| r.kind == RegionKind::MainFloorPlan)
            .unwrap();
        assert_eq!(floor.bounds.min, corners[0]);
        assert_eq!(floor.bounds.max, corners[2]);
        assert_eq!(floor.source_ids, ["name", "s0", "s1", "s2", "s3"]);
        crate::export::validate(&confirmed).unwrap();
        confirmed.pages[0]
            .semantics
            .regions
            .iter_mut()
            .find(|r| r.kind == RegionKind::MainFloorPlan)
            .unwrap()
            .source_ids
            .pop();
        assert!(crate::export::validate(&confirmed).is_err());
        entities.push(line(
            "far-wall",
            Some("A-WALL"),
            p(640., 470.),
            p(700., 470.),
        ));
        let inset = run(entities);
        assert!(
            inset.pages[0]
                .semantics
                .regions
                .iter()
                .all(|r| r.kind != RegionKind::MainFloorPlan)
        );
    }
    fn dim_lines(layer: Option<&str>) -> Vec<Primitive> {
        vec![
            line("dim", layer, p(100., 320.), p(300., 320.)),
            line("left", layer, p(100., 294.), p(100., 326.)),
            line("right", layer, p(300., 294.), p(300., 326.)),
        ]
    }
    #[test]
    fn matched_metric_and_scale_agreement_follow_reference_measurement() {
        let s = scan(
            vec![
                line("dim", Some("A-DIM"), p(100., 320.), p(300., 320.)),
                text("label", Some("A-DIM"), "3.00 m", p(170., 334.)),
            ],
            Some(15.),
        );
        assert_eq!(s.dimensions.len(), 1);
        let d = &s.dimensions[0];
        assert_eq!(d.unit, Unit::Meter);
        assert_eq!(d.measured_mm, 3000.);
        assert_eq!(d.drawing_length, Some(200.));
        assert_eq!(d.observed_mm_per_unit, Some(15.));
        assert_eq!(d.calibration_relative_error, Some(0.));
        assert_eq!(d.line_source.as_deref(), Some("dim"));
        assert_eq!(d.evidence, DimensionEvidence::DimensionLayer);
        assert!(d.requires_review);
    }
    #[test]
    fn pdf_needs_two_finite_witnesses_and_preserves_fragment_sources() {
        let mut items = dim_lines(None);
        items.push(text("num-a", None, "8", p(168., 334.)));
        items.push(text("num-b", None, "400", p(184., 334.)));
        let s = scan(items.clone(), None);
        assert_eq!(s.dimensions.len(), 1);
        let d = &s.dimensions[0];
        assert_eq!(d.measured_mm, 8400.);
        assert_eq!(d.evidence, DimensionEvidence::TwoFiniteWitnesses);
        assert_eq!(d.start_witness.as_deref(), Some("left"));
        assert_eq!(d.end_witness.as_deref(), Some("right"));
        assert_eq!(d.text_sources, ["num-a", "num-b"]);
        items[2] = line("far", None, p(300., 330.), p(300., 370.));
        assert!(scan(items, None).dimensions.is_empty());
    }
    #[test]
    fn rejects_area_scale_and_duplicate_and_conflicting_claims() {
        let mut items = dim_lines(Some("A-DIM"));
        for (id, s) in [
            ("a", "3 m"),
            ("dup", "300 cm"),
            ("area", "10.7 m^2"),
            ("scale", "1:100"),
        ] {
            items.push(text(id, Some("A-DIM"), s, p(170., 334.)));
        }
        let s = scan(items.clone(), None);
        assert_eq!(s.dimensions.len(), 1);
        assert_eq!(s.dimensions[0].text_sources, ["a", "dup"]);
        items.push(text("other", Some("A-DIM"), "4 m", p(180., 334.)));
        let s = scan(items, None);
        assert!(s.dimensions.iter().all(|d| d.line.is_none()));
        assert!(
            s.diagnostics
                .iter()
                .any(|d| d.kind == SemanticLimit::ConflictingDimensionLabels)
        );
    }
    #[test]
    fn unknown_wall_and_unmatched_imperial_dimension_stay_distinct() {
        let s = scan(
            vec![
                line("wall", None, p(100., 320.), p(300., 320.)),
                text("pdf", None, "3 m", p(165., 324.)),
                text("imperial", Some("A-DIM"), "12'-0\"", p(430., 430.)),
            ],
            None,
        );
        assert_eq!(s.dimensions.len(), 1);
        let d = &s.dimensions[0];
        assert_eq!(d.unit, Unit::Foot);
        assert!((d.measured_mm - 3657.6).abs() < 1e-8);
        assert_eq!(d.drawing_length, None);
    }
    #[test]
    fn native_dimension_needs_no_layer_and_does_not_invent_model_units() {
        let mut page = page(vec![primitive(
            "native",
            None,
            Shape::Dimension {
                start: p(0., 0.),
                end: p(200., 0.),
                at: p(100., 5.),
                value: "<>".into(),
                measurement: Some(200.),
            },
        )]);
        let s = interpret(&page, &[], &[], &[], &[], &[], Some(3.));
        assert_eq!(s.native_dimensions.len(), 1);
        assert_eq!(s.native_dimensions[0].reported_model_units, Some(200.));
        assert!(s.dimensions.is_empty());
        page.units_to_mm = Some(25.4);
        let s = interpret(&page, &[], &[], &[], &[], &[], Some(25.4));
        assert_eq!(s.dimensions[0].measured_mm, 5080.);
        assert_eq!(s.dimensions[0].evidence, DimensionEvidence::NativeDxfEntity);
    }
    #[test]
    fn labeled_pdf_grid_bubble_and_adjacent_calibrated_grid_bay() {
        let mut items = vec![
            line("b", None, p(300., 80.), p(300., 500.)),
            text("label", None, "B", p(294., 52.)),
            primitive(
                "bubble",
                None,
                Shape::Polyline {
                    points: vec![p(284., 42.), p(316., 42.), p(316., 74.), p(284., 74.)],
                    closed: true,
                    filled: false,
                },
            ),
            line("a", Some("S-GRID"), p(100., 80.), p(100., 500.)),
            text("label-a", Some("S-GRID"), "A", p(100., 60.)),
            line("short", None, p(290., 80.), p(290., 115.)),
        ];
        let s = scan(items.clone(), Some(10.));
        assert_eq!(s.grid_axes.len(), 2);
        let b = &s.grid_axes[1];
        assert_eq!(b.label.as_deref(), Some("B"));
        assert_eq!(b.bubble_source.as_deref(), Some("bubble"));
        assert_eq!(s.grid_bays.len(), 1);
        assert_eq!(s.grid_bays[0].drawing_distance, 200.);
        assert_eq!(s.grid_bays[0].distance_mm, Some(2000.));
        items.remove(2);
        assert_eq!(scan(items, None).grid_axes.len(), 1);
    }
    #[test]
    fn notes_and_title_values_are_source_bound_and_partition_regions_are_finite() {
        let v = vec![
            text("head", Some("A-NOTE"), "GENERAL NOTES", p(560., 120.)),
            text(
                "row",
                Some("A-NOTE"),
                "1. VERIFY ALL DIMENSIONS",
                p(560., 145.),
            ),
            text("project", Some("A-TITLE"), "PROJECT: TEST", p(690., 620.)),
            text("sheet", Some("A-TITLE"), "SHEET NO", p(690., 646.)),
            text("no", Some("A-TITLE"), "A-101", p(770., 646.)),
            text(
                "title",
                Some("A-TITLE"),
                "TITLE: FIRST FLOOR",
                p(690., 672.),
            ),
            text("rev", Some("A-TITLE"), "REV 01", p(830., 646.)),
        ];
        let s = scan(v, None);
        assert_eq!(s.title_fields.len(), 4);
        let number = s
            .title_fields
            .iter()
            .find(|f| f.kind == TitleKind::SheetNumber)
            .unwrap();
        assert_eq!(number.value, "A-101");
        assert_eq!(number.source_ids, ["sheet", "no"]);
        let item = &s.annotation_blocks[0].items[0];
        assert_eq!(item.marker.as_deref(), Some("1"));
        assert_eq!(item.text, "VERIFY ALL DIMENSIONS");
        assert!(
            s.regions
                .iter()
                .any(|r| r.kind == RegionKind::TitleBlockCandidate)
        );
        assert!(
            s.regions
                .iter()
                .all(|r| r.kind != RegionKind::MainPlanCandidate)
        );
    }
    #[test]
    fn review_required_adjacency_never_becomes_a_routable_passage() {
        let room = |id: &str| Room {
            boundary_evidence: None,
            id: id.into(),
            polygon: vec![],
            area: 400.,
            wall_ids: vec!["w".into()],
            label: None,
            label_source_id: None,
            requires_review: true,
        };
        let open = Opening {
            physical_jambs: None,
            id: "d".into(),
            page: 1,
            kind: OpeningKind::Door,
            operation: "Hinged".into(),
            host_wall_id: "w".into(),
            host_wall_ids: vec!["w".into()],
            start: p(0., 0.),
            end: p(10., 0.),
            hinge: Some(p(0., 0.)),
            swing_observations: Vec::new(),
            source_ids: vec!["arc".into()],
            evidence: OpeningEvidence::FiniteJambSwing,
            requires_review: true,
        };
        let link = Connection {
            opening_id: "d".into(),
            room_ids: vec!["r2".into(), "r1".into()],
        };
        let s = interpret(
            &page(vec![]),
            &[],
            &[room("r1"), room("r2")],
            &[open],
            &[],
            &[link],
            None,
        );
        assert_eq!(s.room_clusters.len(), 1);
        assert_eq!(s.room_clusters[0].room_ids, ["r1", "r2"]);
        assert!(
            s.routing
                .iter()
                .all(|r| !r.usable_for_routing && r.requires_review)
        );
        assert_eq!(s.routing[0].source_ids, ["arc"]);
    }
}
