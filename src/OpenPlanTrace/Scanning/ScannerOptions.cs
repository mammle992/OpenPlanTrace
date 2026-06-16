namespace OpenPlanTrace;

public sealed record ScannerOptions
{
    public double SheetMargin { get; init; } = 12;

    public double MinWallLength { get; init; } = 24;

    public double MinWallFragmentLength { get; init; } = 4;

    public double MaxWallFragmentGap { get; init; } = 6;

    public int MaxWallCandidateSeedsPerPage { get; init; } = 15000;

    public int MaxDimensionLineCandidatesPerPage { get; init; } = 3000;

    public int MaxDimensionLineMatchCandidatesPerText { get; init; } = 32;

    public double MinGridAxisLength { get; init; } = 60;

    public double MinGridAxisLengthRatio { get; init; } = 0.25;

    public double GridAxisMergeTolerance { get; init; } = 3;

    public double MaxGridAxisFragmentGap { get; init; } = 16;

    public double MaxGridAxisLabelDistance { get; init; } = 48;

    public double WallMergeTolerance { get; init; } = 2;

    public double WallSnapTolerance { get; init; } = 2;

    public int MaxWallGraphEndpointGapReviewItems { get; init; } = 40;

    public double DefaultWallThickness { get; init; } = 4;

    public bool EnableWallPairReconstruction { get; init; } = true;

    public bool FilterCompactObjectLineworkFromWalls { get; init; } = true;

    public bool FilterDoorSymbolLineworkFromWalls { get; init; } = true;

    public bool FilterDenseOrthogonalPatternsFromWalls { get; init; } = true;

    public bool FilterUnsupportedWallBodyLinework { get; init; } = true;

    public int MinWallBodyPairsBeforeSingleLineFiltering { get; init; } = 4;

    public bool FilterDenseFragmentLineworkFromWalls { get; init; }

    public bool FilterDimensionLikeFragmentLineworkFromWalls { get; init; } = true;

    public bool EnableWallEvidenceRecovery { get; init; } = true;

    public bool EnableWallEvidenceNoiseRejection { get; init; } = true;

    public int MaxWallEvidenceRecoveredWallsPerPage { get; init; } = 60;

    public bool ExcludeObjectLikeWallComponentsFromStructuralTopology { get; init; } = true;

    public bool ExcludeWeakWallFragmentsFromStructuralTopology { get; init; } = true;

    public double MinWallPairSeparation { get; init; } = 2;

    public double MaxWallPairSeparation { get; init; } = 24;

    public double MinWallPairOverlapRatio { get; init; } = 0.55;

    public double MinOpeningGap { get; init; } = 8;

    public double MaxOpeningGap { get; init; } = 60;

    public double MinRoomArea { get; init; } = 400;

    public int MaxRoomCandidatesPerPage { get; init; } = 250;

    public bool DetectObjectCandidates { get; init; } = true;

    public bool EnableVisualAiClassification { get; init; }

    public IVisualAiObjectClassifier? VisualAiClassifier { get; init; }

    public IVisualAiCropProvider? VisualAiCropProvider { get; init; }

    public IVisualAiCropSink? VisualAiCropSink { get; init; }

    public int MaxVisualAiCropsPerScan { get; init; } = 200;

    public double VisualAiCropPadding { get; init; } = 18;

    public double MinVisualAiConfidence { get; init; } = 0.35;

    public int VisualAiTopK { get; init; } = 5;

    public bool DetectCompositeObjectCandidates { get; init; } = true;

    public int MinCompositeObjectPrimitiveCount { get; init; } = 2;

    public int MaxCompositeObjectCandidatesPerPage { get; init; } = 400;

    public int MaxCompositeObjectPrimitiveSearchCount { get; init; } = 6000;

    public double CompositeObjectClusterTolerance { get; init; } = 8;

    public double MaxCompositeObjectPrimitiveLength { get; init; } = 140;

    public double MaxCompositeObjectAreaRatio { get; init; } = 0.06;

    public double ObjectNearbyTextSearchRadius { get; init; } = 90;

    public int MaxNearbyTextPerObject { get; init; } = 5;

    public bool DetectObjectAggregates { get; init; } = true;

    public int MinObjectAggregateChildCount { get; init; } = 3;

    public double ObjectAggregateClusterTolerance { get; init; } = 16;

    public double MaxObjectAggregateAreaRatio { get; init; } = 0.12;

    public IReadOnlyList<LayerCategoryOverride> LayerCategoryOverrides { get; init; } = Array.Empty<LayerCategoryOverride>();

    public IReadOnlyList<ObjectLabelRule> ObjectLabelRules { get; init; } = Array.Empty<ObjectLabelRule>();

    public GeometryTolerance GeometryTolerance { get; init; } = new();
}
