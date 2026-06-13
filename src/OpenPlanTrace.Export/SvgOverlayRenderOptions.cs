namespace OpenPlanTrace.Export;

public sealed record SvgOverlayRenderOptions
{
    public bool IncludeLegend { get; init; } = true;

    public bool IncludeDiagnostics { get; init; } = true;

    public bool IncludeRegions { get; init; } = true;

    public bool IncludeDimensions { get; init; } = true;

    public bool IncludeAnnotations { get; init; } = true;

    public bool IncludeGridAxes { get; init; } = true;

    public bool IncludeGridBaySpacings { get; init; } = true;

    public bool IncludeWalls { get; init; } = true;

    public bool IncludeWallComponents { get; init; } = true;

    public bool IncludeWallNodes { get; init; } = true;

    public bool IncludeRooms { get; init; } = true;

    public bool IncludeRoomClusters { get; init; } = true;

    public bool IncludeRoomAdjacency { get; init; } = true;

    public bool IncludeOpenings { get; init; } = true;

    public bool IncludeObjects { get; init; } = true;

    public bool IncludeObjectAggregates { get; init; } = true;

    public bool IncludeSurfacePatterns { get; init; } = true;

    public bool IncludeRoutingLayer { get; init; }

    public string BackgroundColor { get; init; } = "#ffffff";
}
