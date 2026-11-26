//! Native drawing and decision contracts. Observed wall extents never become graph endpoints.
use serde::{Deserialize, Serialize};
use std::ops::{Add, Mul, Sub};

#[derive(Clone, Copy, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Point {
    pub x: f64,
    pub y: f64,
}
impl Point {
    pub fn new(x: f64, y: f64) -> Self {
        Self { x, y }
    }
    pub fn distance(self, b: Self) -> f64 {
        (self - b).norm()
    }
    pub fn dot(self, b: Self) -> f64 {
        self.x * b.x + self.y * b.y
    }
    pub fn cross(self, b: Self) -> f64 {
        self.x * b.y - self.y * b.x
    }
    pub fn norm(self) -> f64 {
        self.x.hypot(self.y)
    }
    pub fn unit(self) -> Self {
        let n = self.norm();
        if n > 0.0 {
            self * (1.0 / n)
        } else {
            Self::default()
        }
    }
    pub fn finite(self) -> bool {
        self.x.is_finite() && self.y.is_finite()
    }
}
impl Add for Point {
    type Output = Self;
    fn add(self, b: Self) -> Self {
        Self::new(self.x + b.x, self.y + b.y)
    }
}
impl Sub for Point {
    type Output = Self;
    fn sub(self, b: Self) -> Self {
        Self::new(self.x - b.x, self.y - b.y)
    }
}
impl Mul<f64> for Point {
    type Output = Self;
    fn mul(self, b: f64) -> Self {
        Self::new(self.x * b, self.y * b)
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase"
)]
pub enum Shape {
    Line {
        start: Point,
        end: Point,
    },
    Polyline {
        points: Vec<Point>,
        closed: bool,
        filled: bool,
    },
    Arc {
        center: Point,
        radius: f64,
        start_angle: f64,
        sweep: f64,
    },
    Text {
        at: Point,
        value: String,
        height: f64,
    },
    Image {
        min: Point,
        max: Point,
    },
    Insert {
        at: Point,
        name: String,
    },
    Dimension {
        start: Point,
        end: Point,
        at: Point,
        value: String,
        measurement: Option<f64>,
    },
}
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Primitive {
    pub id: String,
    pub layer: Option<String>,
    /// Width of a solid stroked source in page coordinates, when a single width is known.
    #[serde(
        rename = "strokeWidth",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub stroke_width: Option<f64>,
    /// The original layer name remains available when an explicit profile changes its role.
    #[serde(
        rename = "layerCategoryOverride",
        default,
        skip_serializing_if = "Option::is_none"
    )]
    pub layer_category_override: Option<crate::layers::LayerCategory>,
    pub shape: Shape,
}
impl Primitive {
    pub fn layer_kind(&self) -> LayerKind {
        use crate::layers::LayerCategory as C;
        match self.layer_category_override {
            None => layer_kind(self.layer.as_deref()),
            Some(C::Wall) => LayerKind::Wall,
            Some(C::Door) => LayerKind::Door,
            Some(C::Window) => LayerKind::Window,
            Some(C::Room) => LayerKind::Room,
            Some(C::Dimension) => LayerKind::Dimension,
            Some(C::Grid) => LayerKind::Grid,
            Some(C::Text | C::SurfacePattern) => LayerKind::Annotation,
            Some(
                C::Equipment
                | C::Electrical
                | C::HVAC
                | C::Plumbing
                | C::FireSafety
                | C::Furniture
                | C::Fixture
                | C::Structural,
            ) => LayerKind::Object,
            Some(C::Unknown) => LayerKind::Unknown,
        }
    }
    pub fn layer_name(&self) -> &str {
        self.layer
            .as_deref()
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or("(unlayered)")
    }
}
#[derive(Clone, Debug)]
pub struct Page {
    pub finish_history: Option<crate::finish_visibility::PaintHistory>,
    pub pdf_input: Option<crate::input::raster::PdfInput>,
    pub number: u32,
    /// Finite PDF fills eligible to witness placement after subsequent page paint.
    pub placement_paint_ids: Option<Vec<String>>,
    /// Conservatively visible opaque convex nonrectangular PDF fills.
    pub placement_polygon_paint_ids: Option<Vec<String>>,
    pub placement_paint_remainders: Vec<PaintRemainder>,
    pub white_nested_walls: Vec<crate::input::white_nested::Receipt>,
    pub terminal_stock: Vec<crate::input::terminal_stock::Receipt>,
    pub local_stock: Vec<crate::input::local_stock::Receipt>,
    pub stepped_stock: Vec<crate::input::stepped_stock::Receipt>,
    pub capped_stock: Vec<crate::input::capped_stock::Receipt>,
    pub placement_contact_paint: Vec<ContactPaint>,
    pub material_tiles: Vec<crate::placement::insulation::MaterialTile>,
    pub width: f64,
    pub height: f64,
    pub units_to_mm: Option<f64>,
    pub primitives: Vec<Primitive>,
}

/// A finite rectangular remnant with authored paint that establishes its new end.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PaintRemainder {
    pub source_id: String,
    pub outline: [Point; 4],
    pub occluder_source_ids: Vec<String>,
}

/// An opaque authored cell and the original later polygons that obscure parts of it.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct ContactPaint {
    pub source_id: String,
    pub occluder_source_ids: Vec<String>,
}
#[derive(Clone, Debug)]
pub struct Document {
    pub pages: Vec<Page>,
    pub warnings: Vec<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, PartialOrd, Ord, Serialize, Deserialize)]
pub enum LayerKind {
    Wall,
    Door,
    Window,
    Room,
    Dimension,
    Grid,
    Object,
    Annotation,
    Unknown,
}
pub fn layer_kind(layer: Option<&str>) -> LayerKind {
    use LayerKind::*;
    let normalized = layer.unwrap_or("").to_ascii_uppercase();
    for word in normalized.split(|c: char| !c.is_ascii_alphanumeric()) {
        match word {
            "WALL" | "WALLS" => return Wall,
            "DOOR" | "DOORS" => return Door,
            "WINDOW" | "WINDOWS" | "GLAZ" | "WIND" => return Window,
            "ROOM" | "ROOMS" | "SPACE" => return Room,
            "DIM" | "DIMS" | "DIMENSION" => return Dimension,
            "GRID" | "AXIS" => return Grid,
            "FURN" | "FURNITURE" | "HVAC" | "EQPM" | "EQUIP" | "FIXT" | "SYMBOLS" => return Object,
            "TITLE" | "NOTE" | "NOTES" | "TEXT" | "ANNO" | "HATCH" => return Annotation,
            _ => {}
        }
    }
    Unknown
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ArcGeometry {
    pub center: Point,
    pub radius: f64,
    pub start_angle: f64,
    pub sweep: f64,
}
impl ArcGeometry {
    pub fn at(self, t: f64) -> Point {
        let a = self.start_angle + self.sweep * t;
        self.center + Point::new(a.cos(), a.sin()) * self.radius
    }
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum BodyEvidence {
    ParallelFaces,
    FilledBody,
    ConcentricArcs,
    ExplicitCenterline,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Wall {
    pub id: String,
    pub page: u32,
    pub start: Point,
    pub end: Point,
    pub thickness: f64,
    pub body: BodyEvidence,
    pub curve: Option<ArcGeometry>,
    pub source_ids: Vec<String>,
    pub requires_review: bool,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum WallSelectionReason {
    RepeatedSymbolStrokes,
    InteriorDetailCrossesFace,
    OutlineEdgePair,
    LabeledEnclosure,
    CabinetFront,
    FurnishedSurface,
    PrintedRoomArea,
    DoorLeaf,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WithheldWall {
    /// Source corners of a positively identified furnishing, in perimeter order.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub furniture_outline: Option<[Point; 4]>,
    pub wall: Wall,
    pub reason: WallSelectionReason,
    pub witness_source_ids: Vec<String>,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum OpeningKind {
    Door,
    Window,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum OpeningEvidence {
    FiniteJambSwing,
    WallGapSwing,
    ExplicitWindowFrame,
    FiniteWindowAssembly,
    ExteriorFrameGap,
    AuthoredInteriorGap,
    CompositeWindowFrame,
    AuthoredWindowFrame,
    AuthoredExteriorSwingCasing,
    AuthoredExteriorOverheadFrame,
    AuthoredExteriorRoughGlazing,
    LayerlessOverheadProjection,
    RasterFiniteHingedDoor,
    LayerlessDashedSwing,
}
/// The authored radial swing stays in source coordinates even when its aperture is on a wall axis.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct SwingObservation {
    pub arc_source_id: String,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub segment_source_ids: Vec<String>,
    pub hinge: Point,
    pub closed_tip: Point,
    pub open_tip: Point,
    pub leaf_tolerance: f64,
    pub open_leaf_source_ids: Vec<String>,
    pub closed_leaf_source_ids: Vec<String>,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Opening {
    pub id: String,
    pub page: u32,
    pub kind: OpeningKind,
    pub operation: String,
    pub host_wall_id: String,
    pub host_wall_ids: Vec<String>,
    pub start: Point,
    pub end: Point,
    pub hinge: Option<Point>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub swing_observations: Vec<SwingObservation>,
    pub source_ids: Vec<String>,
    pub evidence: OpeningEvidence,
    pub requires_review: bool,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub physical_jambs: Option<crate::openings::physical_jambs::PhysicalJambEvidence>,
}

/// A pane observation within an independently measured physical window frame.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct WindowPane {
    pub frame_opening_id: String,
    /// Original observation order for replay of the wall-selection stages.
    pub opening_index: usize,
    pub observation: Opening,
}

impl Opening {
    /// The occupied part of this straight host's observed axis, in host-relative units.
    /// A door between two wall ends has no solid material on either host to cut.
    pub fn interval_on(&self, wall: &Wall) -> Option<(f64, f64)> {
        if wall.curve.is_some()
            || matches!(
                self.evidence,
                OpeningEvidence::WallGapSwing
                    | OpeningEvidence::ExteriorFrameGap
                    | OpeningEvidence::AuthoredInteriorGap
                    | OpeningEvidence::CompositeWindowFrame
                    | OpeningEvidence::AuthoredWindowFrame
                    | OpeningEvidence::AuthoredExteriorSwingCasing
                    | OpeningEvidence::AuthoredExteriorOverheadFrame
                    | OpeningEvidence::AuthoredExteriorRoughGlazing
                    | OpeningEvidence::LayerlessOverheadProjection
                    | OpeningEvidence::RasterFiniteHingedDoor
                    | OpeningEvidence::LayerlessDashedSwing
            )
            || !self.host_wall_ids.contains(&wall.id)
        {
            return None;
        }
        let v = wall.end - wall.start;
        let length_squared = v.dot(v);
        if length_squared == 0. {
            return None;
        }
        let project = |p: Point| ((p - wall.start).dot(v) / length_squared).clamp(0., 1.);
        let (a, b) = (project(self.start), project(self.end));
        ((a - b).abs() > 1e-9).then_some((a.min(b), a.max(b)))
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Node {
    pub id: String,
    pub position: Point,
    pub wall_ids: Vec<String>,
    pub requires_review: bool,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Edge {
    pub start_node: usize,
    pub end_node: usize,
    pub wall_ids: Vec<String>,
    pub opening_ids: Vec<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Room {
    pub id: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub boundary_evidence: Option<RoomBoundaryEvidence>,
    pub polygon: Vec<Point>,
    pub area: f64,
    pub wall_ids: Vec<String>,
    pub label: Option<String>,
    pub label_source_id: Option<String>,
    pub requires_review: bool,
}
/// An authored perimeter in its original page coordinate system. Graph faces
/// retain their existing edge and wall incidence instead.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all_fields = "camelCase")]
pub enum RoomBoundaryEvidence {
    RoomLayerPerimeter {
        source_id: String,
    },
    MeasuredFloorFill {
        source_id: String,
        witness_source_ids: Vec<String>,
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        perimeter_source_ids: Vec<String>,
        #[serde(default, skip_serializing_if = "Vec::is_empty")]
        entrance_opening_ids: Vec<String>,
    },
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Object {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub geometry: Option<ObjectGeometry>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile_signature: Option<String>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub footprint: Option<FurnitureFootprint>,
    pub id: String,
    pub at: Point,
    pub category: String,
    pub name: String,
    pub room_id: Option<String>,
    pub source_ids: Vec<String>,
    pub requires_review: bool,
}
/// Source-derived symbol outline; never grants wall, routing or placement authority.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ObjectGeometry {
    pub polygon: Vec<Point>,
    pub evidence: ObjectGeometryEvidence,
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all_fields = "camelCase")]
pub enum ObjectGeometryEvidence {
    StairFlight {
        cross_line_count: usize,
        travel_arrow: [Point; 2],
    },
    NestedContours {
        outer_source_id: String,
        inner_source_ids: Vec<String>,
    },
    BasinSymbol {
        outer_source_id: String,
        basin_source_ids: Vec<String>,
        drain_source_id: String,
        tap_source_id: String,
    },
}
impl ObjectGeometryEvidence {
    pub fn category_and_name(&self) -> (&'static str, &'static str) {
        match self {
            Self::StairFlight { .. } => ("Stair", "Stair flight"),
            Self::NestedContours { .. } => ("GenericSymbol", "Unclassified nested symbol"),
            Self::BasinSymbol { .. } => ("PlumbingFixture", "Sink"),
        }
    }
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct FurnitureFootprint {
    pub selection_id: String,
    pub polygon: [Point; 4],
}
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct ObjectGroup {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub profile_signature: Option<String>,
    pub name: String,
    pub member_ids: Vec<String>,
    pub requires_review: bool,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Connection {
    pub opening_id: String,
    pub room_ids: Vec<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Feature {
    pub source_id: String,
    pub category: LayerKind,
    pub label: Option<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct PageScan {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub finish_visibility: Option<crate::finish_visibility::FinishVisibility>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub pdf_input: Option<crate::input::raster::PdfInput>,
    pub page_number: u32,
    /// Source-derived exterior review outlines, recorded after placement decisions.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exterior_enclosures: Option<Vec<crate::placement::enclosures::ExteriorEnclosure>>,
    /// Source-backed boundary connectivity, kept separate from physical wall solids.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub exterior_connections: Option<crate::placement::connections::ConnectionPass>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub placement_paint_ids: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub placement_polygon_paint_ids: Option<Vec<String>>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub placement_paint_remainders: Vec<PaintRemainder>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub white_nested_walls: Vec<crate::input::white_nested::Receipt>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub terminal_stock: Vec<crate::input::terminal_stock::Receipt>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub local_stock: Vec<crate::input::local_stock::Receipt>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub stepped_stock: Vec<crate::input::stepped_stock::Receipt>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub capped_stock: Vec<crate::input::capped_stock::Receipt>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub placement_contact_paint: Vec<ContactPaint>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub material_tiles: Vec<crate::placement::insulation::MaterialTile>,
    pub width: f64,
    pub height: f64,
    pub units_to_millimeters: Option<f64>,
    pub scale_source: Option<String>,
    pub calibration_source_ids: Vec<String>,
    pub primitives: Vec<Primitive>,
    pub walls: Vec<Wall>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub placement_decisions: Option<Vec<crate::placement::WallDecision>>,
    /// Absent in preview 1, which published every observed wall.
    #[serde(skip_serializing_if = "Option::is_none")]
    pub withheld_walls: Option<Vec<WithheldWall>>,
    pub openings: Vec<Opening>,
    #[serde(default, skip_serializing_if = "Vec::is_empty")]
    pub window_panes: Vec<WindowPane>,
    pub nodes: Vec<Node>,
    pub edges: Vec<Edge>,
    pub rooms: Vec<Room>,
    pub objects: Vec<Object>,
    pub object_groups: Vec<ObjectGroup>,
    pub room_connections: Vec<Connection>,
    pub drawing_features: Vec<Feature>,
    pub semantics: crate::semantics::PageSemantics,
    pub room_annotations: Vec<crate::room_labels::RoomAnnotation>,
    pub warnings: Vec<String>,
}
impl PageScan {
    /// Include pane observations when reporting how an observed wall was cut.
    pub(crate) fn opening_evidence(&self) -> impl Iterator<Item = &Opening> {
        self.openings
            .iter()
            .chain(self.window_panes.iter().map(|pane| &pane.observation))
    }
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Scan {
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub visual_classification: Option<crate::visual::Report>,
    pub schema_version: String,
    pub engine_version: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub layer_profile: Option<AppliedLayerProfile>,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub object_label_profile: Option<crate::object_labels::Application>,
    pub pages: Vec<PageScan>,
    pub warnings: Vec<String>,
}
#[derive(Clone, Debug, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct AppliedLayerProfile {
    pub source_format: String,
    pub overrides: Vec<crate::layers::LayerCategoryOverride>,
}
#[derive(Clone, Copy, Debug)]
pub struct Options {
    pub min_wall_length: f64,
    pub min_wall_fragment_length: f64,
    pub min_wall_pair_separation: f64,
    pub max_wall_pair_separation: f64,
    pub min_wall_pair_overlap_ratio: f64,
    pub snap_tolerance: f64,
    pub min_opening_gap: f64,
    pub max_opening_gap: f64,
    pub min_room_area: f64,
    pub max_wall_seeds: usize,
}
impl Default for Options {
    fn default() -> Self {
        Self {
            min_wall_length: 24.,
            min_wall_fragment_length: 4.,
            min_wall_pair_separation: 2.,
            max_wall_pair_separation: 24.,
            min_wall_pair_overlap_ratio: 0.55,
            snap_tolerance: 2.,
            min_opening_gap: 8.,
            max_opening_gap: 60.,
            min_room_area: 400.,
            max_wall_seeds: 15_000,
        }
    }
}

pub fn segments(shape: &Shape) -> Vec<(Point, Point)> {
    match shape {
        Shape::Line { start, end } => vec![(*start, *end)],
        Shape::Polyline { points, closed, .. } => {
            let mut v: Vec<_> = points.windows(2).map(|x| (x[0], x[1])).collect();
            if *closed && points.len() > 2 && points[0] != points[points.len() - 1] {
                v.push((points[points.len() - 1], points[0]));
            }
            v
        }
        _ => vec![],
    }
}
pub fn on_segment(p: Point, a: Point, b: Point) -> (Point, f64) {
    let v = b - a;
    let l = v.dot(v);
    let t = if l > 0. {
        ((p - a).dot(v) / l).clamp(0., 1.)
    } else {
        0.
    };
    (a + v * t, t)
}
pub fn inside(p: Point, poly: &[Point]) -> bool {
    if poly.len() < 3 {
        return false;
    }
    let mut yes = false;
    let mut j = poly.len() - 1;
    for (i, &a) in poly.iter().enumerate() {
        let b = poly[j];
        if (a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x {
            yes = !yes
        }
        j = i
    }
    yes
}
