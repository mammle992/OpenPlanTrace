namespace OpenPlanTrace.Export;

public enum SvgOverlayRenderProfile
{
    Full,
    StructuralReview,
    PlacementReview,
    WallQa,
    WallQaReview,
    WallQaFocus
}

public sealed record SvgOverlayRenderOptions
{
    public SvgOverlayRenderProfile Profile { get; init; } = SvgOverlayRenderProfile.Full;

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

    public bool IncludeWallTopologySpans { get; init; }

    public bool IncludeWallBodyFootprints { get; init; }

    public bool IncludeReviewOnlyWallTopologySpans { get; init; } = true;

    public bool IncludeSuppressedDetailWallTopologySpans { get; init; } = true;

    public bool IncludeWallGraphRepairs { get; init; } = true;

    public bool IncludeRoutingLayer { get; init; }

    public bool IncludeSourceContext { get; init; }

    public int MaxSourceContextPrimitives { get; init; } = 12000;

    public string BackgroundColor { get; init; } = "#ffffff";

    public string? BackgroundImageHref { get; init; }

    public double BackgroundImageOpacity { get; init; } = 0.68;

    public bool CropToFloorplanContent { get; init; }

    public double ViewportPaddingDrawingUnits { get; init; } = 24.0;

    public static SvgOverlayRenderOptions ForProfile(SvgOverlayRenderProfile profile) =>
        profile switch
        {
            SvgOverlayRenderProfile.StructuralReview => new SvgOverlayRenderOptions
            {
                Profile = SvgOverlayRenderProfile.StructuralReview,
                IncludeWallComponents = false,
                IncludeWallNodes = false,
                IncludeRoomClusters = false,
                IncludeRoomAdjacency = false,
                IncludeObjects = false,
                IncludeObjectAggregates = false,
                IncludeRoutingLayer = false
            },
            SvgOverlayRenderProfile.PlacementReview => new SvgOverlayRenderOptions
            {
                Profile = SvgOverlayRenderProfile.PlacementReview,
                IncludeLegend = true,
                IncludeDiagnostics = true,
                IncludeRegions = false,
                IncludeDimensions = false,
                IncludeAnnotations = false,
                IncludeGridAxes = false,
                IncludeGridBaySpacings = false,
                IncludeWalls = false,
                IncludeWallComponents = false,
                IncludeWallNodes = false,
                IncludeRooms = false,
                IncludeRoomClusters = false,
                IncludeRoomAdjacency = false,
                IncludeOpenings = false,
                IncludeObjects = false,
                IncludeObjectAggregates = false,
                IncludeSurfacePatterns = false,
                IncludeWallTopologySpans = true,
                IncludeWallBodyFootprints = true,
                IncludeReviewOnlyWallTopologySpans = false,
                IncludeRoutingLayer = false
            },
            SvgOverlayRenderProfile.WallQa => new SvgOverlayRenderOptions
            {
                Profile = SvgOverlayRenderProfile.WallQa,
                IncludeLegend = true,
                IncludeDiagnostics = true,
                IncludeRegions = false,
                IncludeDimensions = false,
                IncludeAnnotations = false,
                IncludeGridAxes = false,
                IncludeGridBaySpacings = false,
                IncludeWalls = false,
                IncludeWallComponents = false,
                IncludeWallNodes = false,
                IncludeRooms = false,
                IncludeRoomClusters = false,
                IncludeRoomAdjacency = false,
                IncludeOpenings = false,
                IncludeObjects = false,
                IncludeObjectAggregates = false,
                IncludeSurfacePatterns = false,
                IncludeWallTopologySpans = true,
                IncludeWallBodyFootprints = true,
                IncludeReviewOnlyWallTopologySpans = false,
                IncludeWallGraphRepairs = false,
                IncludeRoutingLayer = false,
                IncludeSourceContext = true
            },
            SvgOverlayRenderProfile.WallQaReview => new SvgOverlayRenderOptions
            {
                Profile = SvgOverlayRenderProfile.WallQaReview,
                IncludeLegend = true,
                IncludeDiagnostics = true,
                IncludeRegions = false,
                IncludeDimensions = false,
                IncludeAnnotations = false,
                IncludeGridAxes = false,
                IncludeGridBaySpacings = false,
                IncludeWalls = false,
                IncludeWallComponents = false,
                IncludeWallNodes = false,
                IncludeRooms = false,
                IncludeRoomClusters = false,
                IncludeRoomAdjacency = false,
                IncludeOpenings = false,
                IncludeObjects = false,
                IncludeObjectAggregates = false,
                IncludeSurfacePatterns = false,
                IncludeWallTopologySpans = true,
                IncludeWallBodyFootprints = true,
                IncludeReviewOnlyWallTopologySpans = true,
                IncludeSuppressedDetailWallTopologySpans = false,
                IncludeWallGraphRepairs = false,
                IncludeRoutingLayer = false,
                IncludeSourceContext = true
            },
            SvgOverlayRenderProfile.WallQaFocus => new SvgOverlayRenderOptions
            {
                Profile = SvgOverlayRenderProfile.WallQaFocus,
                IncludeLegend = true,
                IncludeDiagnostics = true,
                IncludeRegions = false,
                IncludeDimensions = false,
                IncludeAnnotations = false,
                IncludeGridAxes = false,
                IncludeGridBaySpacings = false,
                IncludeWalls = false,
                IncludeWallComponents = false,
                IncludeWallNodes = false,
                IncludeRooms = false,
                IncludeRoomClusters = false,
                IncludeRoomAdjacency = false,
                IncludeOpenings = false,
                IncludeObjects = false,
                IncludeObjectAggregates = false,
                IncludeSurfacePatterns = false,
                IncludeWallTopologySpans = true,
                IncludeWallBodyFootprints = true,
                IncludeReviewOnlyWallTopologySpans = false,
                IncludeWallGraphRepairs = false,
                IncludeRoutingLayer = false,
                IncludeSourceContext = true,
                CropToFloorplanContent = true
            },
            _ => new SvgOverlayRenderOptions()
            {
                IncludeWallTopologySpans = true,
                IncludeWallBodyFootprints = true,
                IncludeRoutingLayer = true
            }
        };

    public static bool TryParseProfile(string value, out SvgOverlayRenderProfile profile)
    {
        switch (NormalizeProfile(value))
        {
            case "full":
            case "debug":
            case "all":
                profile = SvgOverlayRenderProfile.Full;
                return true;
            case "structural":
            case "structuralreview":
            case "structuralreviewoverlay":
            case "review":
            case "geometry":
                profile = SvgOverlayRenderProfile.StructuralReview;
                return true;
            case "placement":
            case "placementreview":
            case "topology":
            case "topologyspans":
                profile = SvgOverlayRenderProfile.PlacementReview;
                return true;
            case "wallqa":
            case "wallaccuracy":
            case "wallaccuracyreview":
            case "walls":
            case "wallsonly":
            case "cleanwalls":
            case "cleanwallsonly":
                profile = SvgOverlayRenderProfile.WallQa;
                return true;
            case "wallqareview":
            case "wallaccuracyreviewall":
            case "wallreview":
            case "reviewwalls":
            case "reviewwallsonly":
            case "nonplacementwalls":
            case "nonplacementwallspans":
            case "omittedwalls":
                profile = SvgOverlayRenderProfile.WallQaReview;
                return true;
            case "wallqafocus":
            case "wallaccuracyfocus":
            case "wallaccuracyreviewfocus":
            case "focusedwallqa":
            case "focusedwalls":
            case "wallqazoom":
            case "wallszoom":
                profile = SvgOverlayRenderProfile.WallQaFocus;
                return true;
            default:
                profile = SvgOverlayRenderProfile.Full;
                return false;
        }
    }

    public static string ProfileName(SvgOverlayRenderProfile profile) =>
        profile switch
        {
            SvgOverlayRenderProfile.WallQaFocus => "wall-qa-focus",
            SvgOverlayRenderProfile.WallQaReview => "wall-qa-review",
            SvgOverlayRenderProfile.WallQa => "wall-qa",
            SvgOverlayRenderProfile.PlacementReview => "placement-review",
            SvgOverlayRenderProfile.StructuralReview => "structural-review",
            _ => "full"
        };

    private static string NormalizeProfile(string value) =>
        string.Concat(
            (value ?? string.Empty)
            .Where(char.IsLetterOrDigit))
            .ToLowerInvariant();
}
