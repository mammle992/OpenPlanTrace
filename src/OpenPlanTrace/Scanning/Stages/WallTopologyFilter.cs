namespace OpenPlanTrace;

internal static class WallTopologyFilter
{
    public static IReadOnlyList<WallSegment> StructuralWallsForPage(
        ScanContext context,
        int pageNumber) =>
        StructuralWallsForPage(context, pageNumber, out _);

    public static IReadOnlyList<WallSegment> StructuralWallsForPage(
        ScanContext context,
        int pageNumber,
        out IReadOnlyList<WallGraphComponent> excludedComponents) =>
        StructuralWallsForPage(context, pageNumber, out excludedComponents, out _);

    public static IReadOnlyList<WallSegment> StructuralWallsForPage(
        ScanContext context,
        int pageNumber,
        out IReadOnlyList<WallGraphComponent> excludedComponents,
        out IReadOnlyList<WallEvidenceWallAssessment> excludedEvidenceAssessments)
    {
        excludedComponents = ExcludedComponentsForStructuralTopology(context, pageNumber);
        excludedEvidenceAssessments = RejectedWallEvidenceForStructuralTopology(context, pageNumber);
        var pageWalls = context.Walls
            .Where(wall => wall.PageNumber == pageNumber)
            .ToArray();
        if (excludedComponents.Count == 0 && excludedEvidenceAssessments.Count == 0)
        {
            return pageWalls;
        }

        var excludedWallIds = excludedComponents
            .SelectMany(component => component.WallIds)
            .Concat(excludedEvidenceAssessments.Select(assessment => assessment.WallId))
            .ToHashSet(StringComparer.Ordinal);

        return pageWalls
            .Where(wall => !excludedWallIds.Contains(wall.Id))
            .ToArray();
    }

    public static void AddStructuralTopologyExclusionDiagnostic(
        ScanContext context,
        string stage,
        int pageNumber,
        IReadOnlyList<WallGraphComponent> excludedComponents)
    {
        if (excludedComponents.Count == 0)
        {
            return;
        }

        var excludedWallIds = excludedComponents
            .SelectMany(component => component.WallIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = excludedComponents
            .SelectMany(component => component.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var objectLikeCount = excludedComponents.Count(component =>
            component.Kind == WallGraphComponentKind.ObjectLikeIsland);
        var isolatedFragmentCount = excludedComponents.Count(component =>
            component.Kind == WallGraphComponentKind.IsolatedFragment);

        context.AddDiagnostic(
            $"{stage}.non_structural_wall_components_excluded",
            DiagnosticSeverity.Info,
            stage,
            "Non-structural wall graph components were excluded from structural topology solving while remaining in wall exports.",
            pageNumber,
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Page,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["excludedComponentCount"] = excludedComponents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["excludedWallCount"] = excludedWallIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objectLikeIslandCount"] = objectLikeCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isolatedFragmentCount"] = isolatedFragmentCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["componentIds"] = string.Join(",", excludedComponents.Select(component => component.Id).Take(20)),
                ["wallIds"] = string.Join(",", excludedWallIds.Take(30))
            });
    }

    public static void AddRejectedWallEvidenceExclusionDiagnostic(
        ScanContext context,
        string stage,
        int pageNumber,
        IReadOnlyList<WallEvidenceWallAssessment> excludedEvidenceAssessments)
    {
        if (excludedEvidenceAssessments.Count == 0)
        {
            return;
        }

        var excludedWallIds = excludedEvidenceAssessments
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sourcePrimitiveIds = excludedEvidenceAssessments
            .SelectMany(assessment => assessment.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            $"{stage}.rejected_wall_evidence_excluded",
            DiagnosticSeverity.Info,
            stage,
            "Rejected Wall Evidence V2 wall candidates were excluded from structural topology solving while remaining available for QA/export review.",
            pageNumber,
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Page,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["excludedWallCount"] = excludedWallIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["doorOrOpeningSymbolCount"] = excludedEvidenceAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.DoorOrOpeningSymbol).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["surfacePatternDetailCount"] = excludedEvidenceAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.SurfacePatternDetail).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["dimensionOrAnnotationCount"] = excludedEvidenceAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.DimensionOrAnnotation).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objectOrFixtureDetailCount"] = excludedEvidenceAssessments.Count(assessment => assessment.Category == WallEvidenceCategory.ObjectOrFixtureDetail).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", excludedWallIds.Take(30))
            });
    }

    private static IReadOnlyList<WallGraphComponent> ExcludedComponentsForStructuralTopology(
        ScanContext context,
        int pageNumber)
    {
        return context.WallGraph.Components
            .Where(component => component.PageNumber == pageNumber)
            .Where(component => component.ExcludedFromStructuralTopology)
            .Where(component => component.WallIds.Count > 0)
            .OrderBy(component => component.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<WallEvidenceWallAssessment> RejectedWallEvidenceForStructuralTopology(
        ScanContext context,
        int pageNumber)
    {
        return context.WallEvidenceMap.WallAssessments
            .Where(assessment => assessment.PageNumber == pageNumber)
            .Where(assessment => assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .OrderBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .ToArray();
    }
}
