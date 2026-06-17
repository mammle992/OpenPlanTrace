namespace OpenPlanTrace;

internal static class StructuralWallSelector
{
    public static IReadOnlyList<WallSegment> Select(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var excludedWallIds = ExcludedWallIds(result);
        return result.Walls
            .Where(wall => !excludedWallIds.Contains(wall.Id))
            .ToArray();
    }

    public static IReadOnlySet<string> ExcludedWallIds(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var excludedWallIds = result.WallGraph.Components
            .Where(component => component.ExcludedFromStructuralTopology)
            .SelectMany(component => component.WallIds)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var assessment in result.WallEvidenceMap.WallAssessments)
        {
            if (string.IsNullOrWhiteSpace(assessment.WallId))
            {
                continue;
            }

            if (WallStructuralTrust.IsRejectedNonStructural(assessment))
            {
                excludedWallIds.Add(assessment.WallId);
            }
        }

        return excludedWallIds;
    }
}
