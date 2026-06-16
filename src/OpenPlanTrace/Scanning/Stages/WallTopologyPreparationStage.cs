using System.Globalization;

namespace OpenPlanTrace;

internal sealed class WallTopologyPreparationStage : IPipelineStage
{
    public string Name => "wall-topology-preparation";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        context.WallTopologyPreparation = Prepare(context);
        AddPreparationDiagnostic(context, context.WallTopologyPreparation);

        return ValueTask.CompletedTask;
    }

    internal static WallTopologyPreparation Prepare(ScanContext context)
    {
        var rejectedWalls = context.WallEvidenceMap.WallAssessments
            .Where(assessment => assessment.RejectedAsNoise || assessment.Decision == WallEvidenceDecision.Reject)
            .Where(assessment => !string.IsNullOrWhiteSpace(assessment.WallId))
            .OrderBy(assessment => assessment.PageNumber)
            .ThenBy(assessment => assessment.WallId, StringComparer.Ordinal)
            .Select(assessment => new WallTopologyRejectedWall(
                assessment.WallId,
                assessment.PageNumber,
                assessment.Bounds,
                assessment.Category,
                assessment.Decision,
                assessment.RejectedAsNoise,
                assessment.SourcePrimitiveIds,
                assessment.Evidence))
            .ToArray();
        var rejectedWallIds = rejectedWalls
            .Select(wall => wall.WallId)
            .ToHashSet(StringComparer.Ordinal);
        var graphWallIds = context.Walls
            .Select(wall => wall.Id)
            .Where(id => !rejectedWallIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var acceptedGraphWallIds = GraphWallIdsByDecision(
            context.WallEvidenceMap.WallAssessments,
            graphWallIds,
            WallEvidenceDecision.Accept);
        var reviewGraphWallIds = GraphWallIdsByDecision(
            context.WallEvidenceMap.WallAssessments,
            graphWallIds,
            WallEvidenceDecision.Review);
        var assessedGraphWallIds = acceptedGraphWallIds
            .Concat(reviewGraphWallIds)
            .ToHashSet(StringComparer.Ordinal);
        var unassessedGraphWallIds = graphWallIds
            .Where(id => !assessedGraphWallIds.Contains(id))
            .ToArray();

        return new WallTopologyPreparation(
            graphWallIds,
            rejectedWalls,
            acceptedGraphWallIds,
            reviewGraphWallIds,
            unassessedGraphWallIds);
    }

    private void AddPreparationDiagnostic(ScanContext context, WallTopologyPreparation preparation)
    {
        if (context.Walls.Count == 0 && preparation.RejectedWalls.Count == 0)
        {
            return;
        }

        var sourcePrimitiveIds = preparation.RejectedWalls
            .SelectMany(wall => wall.SourcePrimitiveIds)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        context.AddDiagnostic(
            "wall_topology_preparation.prepared",
            DiagnosticSeverity.Info,
            Name,
            "Wall evidence was converted into an explicit wall graph input selection.",
            confidence: Confidence.Medium,
            scope: DiagnosticScope.Document,
            sourcePrimitiveIds: sourcePrimitiveIds,
            properties: new Dictionary<string, string>
            {
                ["inputWallCount"] = context.Walls.Count.ToString(CultureInfo.InvariantCulture),
                ["graphWallCount"] = preparation.GraphWallCount.ToString(CultureInfo.InvariantCulture),
                ["acceptedGraphWallCount"] = preparation.AcceptedGraphWallCount.ToString(CultureInfo.InvariantCulture),
                ["reviewGraphWallCount"] = preparation.ReviewGraphWallCount.ToString(CultureInfo.InvariantCulture),
                ["unassessedGraphWallCount"] = preparation.UnassessedGraphWallCount.ToString(CultureInfo.InvariantCulture),
                ["automaticCoordinateRepairWallCount"] = preparation.AutomaticCoordinateRepairWallCount.ToString(CultureInfo.InvariantCulture),
                ["rejectedWallCount"] = preparation.RejectedWallCount.ToString(CultureInfo.InvariantCulture),
                ["rejectedAssessmentCount"] = preparation.RejectedWalls.Count.ToString(CultureInfo.InvariantCulture),
                ["doorOrOpeningSymbolCount"] = preparation.DoorOrOpeningSymbolCount.ToString(CultureInfo.InvariantCulture),
                ["surfacePatternDetailCount"] = preparation.SurfacePatternDetailCount.ToString(CultureInfo.InvariantCulture),
                ["dimensionOrAnnotationCount"] = preparation.DimensionOrAnnotationCount.ToString(CultureInfo.InvariantCulture),
                ["objectOrFixtureDetailCount"] = preparation.ObjectOrFixtureDetailCount.ToString(CultureInfo.InvariantCulture),
                ["wallIds"] = string.Join(",", preparation.GraphWallIds.Take(30)),
                ["acceptedWallIds"] = string.Join(",", preparation.AcceptedGraphWallIds.Take(30)),
                ["reviewWallIds"] = string.Join(",", preparation.ReviewGraphWallIds.Take(30)),
                ["unassessedWallIds"] = string.Join(",", preparation.UnassessedGraphWallIds.Take(30)),
                ["rejectedWallIds"] = string.Join(",", preparation.RejectedWallIds.Take(30))
            });
    }

    private static IReadOnlyList<string> GraphWallIdsByDecision(
        IReadOnlyList<WallEvidenceWallAssessment> assessments,
        IReadOnlyList<string> graphWallIds,
        WallEvidenceDecision decision)
    {
        var graphWallIdSet = graphWallIds.ToHashSet(StringComparer.Ordinal);
        return assessments
            .Where(assessment => assessment.Decision == decision)
            .Where(assessment => !assessment.RejectedAsNoise)
            .Select(assessment => assessment.WallId)
            .Where(id => !string.IsNullOrWhiteSpace(id) && graphWallIdSet.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
    }
}
