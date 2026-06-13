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
        out IReadOnlyList<WallGraphComponent> excludedComponents)
    {
        excludedComponents = ExcludedComponentsForStructuralTopology(context, pageNumber);
        if (excludedComponents.Count == 0)
        {
            return context.Walls
                .Where(wall => wall.PageNumber == pageNumber)
                .ToArray();
        }

        var excludedWallIds = excludedComponents
            .SelectMany(component => component.WallIds)
            .ToHashSet(StringComparer.Ordinal);

        return context.Walls
            .Where(wall => wall.PageNumber == pageNumber && !excludedWallIds.Contains(wall.Id))
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

        context.AddDiagnostic(
            $"{stage}.object_like_wall_components_excluded",
            DiagnosticSeverity.Info,
            stage,
            "Object-like wall graph components were excluded from structural topology solving while remaining in wall exports.",
            pageNumber,
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Page,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["excludedComponentCount"] = excludedComponents.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["excludedWallCount"] = excludedWallIds.Length.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["componentIds"] = string.Join(",", excludedComponents.Select(component => component.Id).Take(20)),
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
}
