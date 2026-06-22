namespace OpenPlanTrace;

public static class PipelineStageMetadataCatalog
{
    private static readonly IReadOnlyList<PipelineStageMetadata> Metadata = new[]
    {
        Create(
            "layer-analysis",
            "Layer analysis",
            PipelineStageKind.SourceAnalysis,
            Reads(PlanArtifactKind.Document, PlanArtifactKind.Pages, PlanArtifactKind.Primitives),
            Writes(PlanArtifactKind.Layers, PlanArtifactKind.Diagnostics),
            Capabilities("layer-category-inference", "source-layer-provenance")),
        Create(
            "raster-extraction",
            "Raster extraction diagnostics",
            PipelineStageKind.SourceAnalysis,
            Reads(PlanArtifactKind.Document, PlanArtifactKind.Pages, PlanArtifactKind.RasterImages),
            Writes(PlanArtifactKind.RasterImages, PlanArtifactKind.Diagnostics),
            Capabilities("raster-source-audit")),
        Create(
            "pdf-image-analysis",
            "PDF image diagnostics",
            PipelineStageKind.SourceAnalysis,
            Reads(PlanArtifactKind.Document, PlanArtifactKind.Pages, PlanArtifactKind.PdfImages),
            Writes(PlanArtifactKind.PdfImages, PlanArtifactKind.Diagnostics),
            Capabilities("pdf-image-audit")),
        Create(
            "sheet-regions",
            "Sheet region detection",
            PipelineStageKind.Layout,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.Layers),
            Writes(PlanArtifactKind.SheetRegions, PlanArtifactKind.Diagnostics),
            Capabilities("sheet-frame-detection", "main-floorplan-cropping")),
        Create(
            "title-block-analysis",
            "Title block analysis",
            PipelineStageKind.Layout,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.TitleBlocks, PlanArtifactKind.Diagnostics),
            Capabilities("title-block-fields", "scale-text-evidence")),
        Create(
            "calibration",
            "Calibration",
            PipelineStageKind.Measurement,
            Reads(PlanArtifactKind.Document, PlanArtifactKind.Primitives, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.Calibration, PlanArtifactKind.Diagnostics),
            Capabilities("drawing-scale-selection", "measurement-provenance"),
            OptionalReads(PlanArtifactKind.TitleBlocks)),
        Create(
            "dimensions",
            "Dimension analysis",
            PipelineStageKind.Measurement,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.SheetRegions, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.Dimensions, PlanArtifactKind.Diagnostics),
            Capabilities("dimension-text-parsing", "dimension-line-matching")),
        Create(
            "annotations",
            "Annotation analysis",
            PipelineStageKind.Layout,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.Annotations, PlanArtifactKind.Diagnostics),
            Capabilities("notes-detection", "annotation-references")),
        Create(
            "grid-axes",
            "Grid axis detection",
            PipelineStageKind.Geometry,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.Layers, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.GridAxes, PlanArtifactKind.Diagnostics),
            Capabilities("grid-axis-lines", "grid-label-matching")),
        Create(
            "grid-bays",
            "Grid bay spacing",
            PipelineStageKind.Measurement,
            Reads(PlanArtifactKind.GridAxes, PlanArtifactKind.Dimensions, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.GridBays, PlanArtifactKind.Diagnostics),
            Capabilities("grid-bay-measurements")),
        Create(
            "measurement-consistency",
            "Measurement consistency",
            PipelineStageKind.Quality,
            Reads(PlanArtifactKind.Dimensions, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.MeasurementConsistency, PlanArtifactKind.Diagnostics),
            Capabilities("scale-outlier-detection", "metric-readiness-evidence", "dimension-cluster-calibration")),
        Create(
            "dimension-chains",
            "Dimension chain consistency",
            PipelineStageKind.Quality,
            Reads(PlanArtifactKind.Dimensions, PlanArtifactKind.GridAxes, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.DimensionChains, PlanArtifactKind.Diagnostics),
            Capabilities("dimension-chain-checks")),
        Create(
            "walls",
            "Wall detection",
            PipelineStageKind.Geometry,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.Layers, PlanArtifactKind.SheetRegions, PlanArtifactKind.GridAxes, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.WallCandidates, PlanArtifactKind.SurfacePatterns, PlanArtifactKind.Diagnostics),
            Capabilities("wall-candidates", "wall-pair-reconstruction", "dense-pattern-filtering"),
            OptionalReads(PlanArtifactKind.Dimensions)),
        Create(
            "wall-evidence",
            "Wall evidence refinement",
            PipelineStageKind.Geometry,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.WallCandidates, PlanArtifactKind.SurfacePatterns, PlanArtifactKind.Layers, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.WallEvidence, PlanArtifactKind.Walls, PlanArtifactKind.Diagnostics),
            Capabilities("wall-evidence-map", "missing-wall-band-recovery", "wall-noise-rejection"),
            OptionalReads(PlanArtifactKind.Dimensions)),
        Create(
            "wall-topology-preparation",
            "Wall topology preparation",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.Walls, PlanArtifactKind.WallEvidence),
            Writes(PlanArtifactKind.WallTopologyPreparation, PlanArtifactKind.Diagnostics),
            Capabilities("graph-input-selection", "rejected-wall-evidence-filtering", "topology-source-gating")),
        Create(
            "wall-graph",
            "Wall graph topology",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.Walls, PlanArtifactKind.WallEvidence, PlanArtifactKind.WallTopologyPreparation),
            Writes(PlanArtifactKind.WallGraph, PlanArtifactKind.TopologySpans, PlanArtifactKind.Diagnostics),
            Capabilities("wall-node-snapping", "wall-edge-graph", "repair-candidates", "object-like-wall-evidence-feedback")),
        Create(
            "openings",
            "Opening detection",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.WallGraph, PlanArtifactKind.Primitives, PlanArtifactKind.Calibration),
            Writes(PlanArtifactKind.Openings, PlanArtifactKind.Diagnostics),
            Capabilities("door-gap-detection", "swing-arc-evidence", "opening-placement"),
            OptionalReads(PlanArtifactKind.Annotations, PlanArtifactKind.SheetRegions)),
        Create(
            "rooms",
            "Room detection",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.WallGraph, PlanArtifactKind.Openings, PlanArtifactKind.Calibration, PlanArtifactKind.SheetRegions),
            Writes(PlanArtifactKind.Rooms, PlanArtifactKind.Diagnostics),
            Capabilities("room-boundary-solving", "room-label-evidence"),
            OptionalReads(PlanArtifactKind.Annotations)),
        Create(
            "room-adjacency",
            "Room adjacency",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.Rooms, PlanArtifactKind.Openings, PlanArtifactKind.WallGraph),
            Writes(PlanArtifactKind.RoomAdjacency, PlanArtifactKind.Diagnostics),
            Capabilities("room-connectivity", "room-clustering")),
        Create(
            "wall-type-refinement",
            "Wall type refinement",
            PipelineStageKind.Topology,
            Reads(PlanArtifactKind.Walls, PlanArtifactKind.WallEvidence, PlanArtifactKind.WallGraph, PlanArtifactKind.Rooms, PlanArtifactKind.RoomAdjacency),
            Writes(PlanArtifactKind.Diagnostics),
            Capabilities("room-side-wall-classification", "shared-wall-refinement", "exterior-boundary-refinement", "room-confirmed-wall-evidence")),
        Create(
            "measurement-scale-provenance",
            "Measurement scale provenance",
            PipelineStageKind.Quality,
            Reads(PlanArtifactKind.Calibration, PlanArtifactKind.Dimensions, PlanArtifactKind.TitleBlocks),
            Writes(PlanArtifactKind.Diagnostics),
            Capabilities("scale-evidence-audit")),
        Create(
            "object-candidates",
            "Object candidate detection",
            PipelineStageKind.Semantics,
            Reads(PlanArtifactKind.Primitives, PlanArtifactKind.Walls, PlanArtifactKind.WallGraph, PlanArtifactKind.Rooms, PlanArtifactKind.Annotations),
            Writes(PlanArtifactKind.ObjectCandidates, PlanArtifactKind.Diagnostics),
            Capabilities("symbol-candidates", "nearby-text-evidence", "composite-candidates")),
        Create(
            "object-groups",
            "Object grouping",
            PipelineStageKind.Semantics,
            Reads(PlanArtifactKind.ObjectCandidates),
            Writes(PlanArtifactKind.ObjectGroups, PlanArtifactKind.Diagnostics),
            Capabilities("symbol-signatures", "similarity-grouping")),
        Create(
            "object-aggregates",
            "Object aggregation",
            PipelineStageKind.Semantics,
            Reads(PlanArtifactKind.ObjectCandidates, PlanArtifactKind.ObjectGroups, PlanArtifactKind.Rooms),
            Writes(PlanArtifactKind.ObjectAggregates, PlanArtifactKind.Diagnostics),
            Capabilities("composite-object-aggregation", "routing-suppression")),
        Create(
            "routing-layer",
            "Routing layer",
            PipelineStageKind.Topology,
            Reads(
                PlanArtifactKind.Walls,
                PlanArtifactKind.WallGraph,
                PlanArtifactKind.Openings,
                PlanArtifactKind.Rooms,
                PlanArtifactKind.ObjectCandidates,
                PlanArtifactKind.ObjectAggregates,
                PlanArtifactKind.Calibration),
            Writes(
                PlanArtifactKind.RoutingBarriers,
                PlanArtifactKind.RoutingPassages,
                PlanArtifactKind.RoutingObstacles,
                PlanArtifactKind.RoutingRoomUseHints,
                PlanArtifactKind.RoutingSuppressedObjects,
                PlanArtifactKind.RoutingIgnoredObjects,
                PlanArtifactKind.Diagnostics),
            Capabilities("routing-barriers", "routing-passages", "object-routing-suppression", "room-use-hints")),
        Create(
            "visual-ai",
            "Kvemo visual classification",
            PipelineStageKind.Semantics,
            Reads(PlanArtifactKind.ObjectCandidates, PlanArtifactKind.ObjectGroups),
            Writes(PlanArtifactKind.VisualAiClassifications, PlanArtifactKind.Diagnostics),
            Capabilities("optional-local-classifier", "crop-evidence", "model-version-provenance"),
            OptionalReads(PlanArtifactKind.RasterImages)),
        Create(
            "layer-consistency",
            "Layer consistency",
            PipelineStageKind.Quality,
            Reads(PlanArtifactKind.Layers, PlanArtifactKind.Walls, PlanArtifactKind.ObjectCandidates, PlanArtifactKind.ObjectAggregates),
            Writes(PlanArtifactKind.LayerConsistency, PlanArtifactKind.Diagnostics),
            Capabilities("layer-vs-detection-consistency"))
    };

    private static readonly IReadOnlyDictionary<string, PipelineStageMetadata> MetadataByStage =
        Metadata.ToDictionary(metadata => metadata.Stage, StringComparer.Ordinal);

    public static IReadOnlyList<PipelineStageMetadata> All => Metadata;

    public static PipelineStageMetadata Get(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);

        return MetadataByStage.TryGetValue(stage, out var metadata)
            ? metadata
            : Create(
                stage,
                stage,
                PipelineStageKind.Unknown,
                Reads(PlanArtifactKind.Unknown),
                Writes(PlanArtifactKind.Diagnostics),
                Capabilities("custom-stage"));
    }

    private static PipelineStageMetadata Create(
        string stage,
        string displayName,
        PipelineStageKind kind,
        IReadOnlyList<PlanArtifactKind> reads,
        IReadOnlyList<PlanArtifactKind> writes,
        IReadOnlyList<string> capabilities,
        IReadOnlyList<PlanArtifactKind>? optionalReads = null) =>
        new(stage, displayName, kind, reads, writes, capabilities, optionalReads ?? Array.Empty<PlanArtifactKind>());

    private static IReadOnlyList<PlanArtifactKind> Reads(params PlanArtifactKind[] artifacts) => artifacts;

    private static IReadOnlyList<PlanArtifactKind> Writes(params PlanArtifactKind[] artifacts) => artifacts;

    private static IReadOnlyList<PlanArtifactKind> OptionalReads(params PlanArtifactKind[] artifacts) => artifacts;

    private static IReadOnlyList<string> Capabilities(params string[] capabilities) => capabilities;
}
