namespace OpenPlanTrace;

public sealed record BenchmarkWallPlacementSummary(
    int TotalWallCount,
    int PlacementReadyWallCount,
    int PlacementReviewWallCount,
    int RejectedNoiseWallCount,
    int AcceptedWallCount,
    int ReviewDecisionWallCount,
    int RejectedWallCount,
    int StructuralComponentCount,
    int MainStructuralComponentCount,
    int SecondaryStructuralComponentCount,
    int ObjectLikeComponentCount,
    int IsolatedFragmentComponentCount,
    int TopologyEdgeCount,
    int RepairCandidateCount,
    int TopologyImportBlockedRepairCandidateCount,
    int EndpointGapRepairCandidateCount,
    int EndpointOverrunRepairCandidateCount,
    int HighSeverityRepairCandidateCount)
{
    public static BenchmarkWallPlacementSummary Empty { get; } =
        new(
            TotalWallCount: 0,
            PlacementReadyWallCount: 0,
            PlacementReviewWallCount: 0,
            RejectedNoiseWallCount: 0,
            AcceptedWallCount: 0,
            ReviewDecisionWallCount: 0,
            RejectedWallCount: 0,
            StructuralComponentCount: 0,
            MainStructuralComponentCount: 0,
            SecondaryStructuralComponentCount: 0,
            ObjectLikeComponentCount: 0,
            IsolatedFragmentComponentCount: 0,
            TopologyEdgeCount: 0,
            RepairCandidateCount: 0,
            TopologyImportBlockedRepairCandidateCount: 0,
            EndpointGapRepairCandidateCount: 0,
            EndpointOverrunRepairCandidateCount: 0,
            HighSeverityRepairCandidateCount: 0);

    public static BenchmarkWallPlacementSummary From(PlanScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var assessments = result.WallEvidenceMap.WallAssessments;
        var components = result.WallGraph.Components;
        var repairCandidates = result.WallGraph.RepairCandidates;
        var mainStructuralComponents = components.Count(component => component.Kind == WallGraphComponentKind.MainStructural);
        var secondaryStructuralComponents = components.Count(component => component.Kind == WallGraphComponentKind.SecondaryStructural);

        return new BenchmarkWallPlacementSummary(
            result.Walls.Count,
            assessments.Count(assessment => assessment.PlacementReady),
            assessments.Count(assessment => assessment.RequiresReview),
            assessments.Count(assessment => assessment.RejectedAsNoise),
            assessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Accept),
            assessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Review),
            assessments.Count(assessment => assessment.Decision == WallEvidenceDecision.Reject),
            mainStructuralComponents + secondaryStructuralComponents,
            mainStructuralComponents,
            secondaryStructuralComponents,
            components.Count(component => component.Kind == WallGraphComponentKind.ObjectLikeIsland),
            components.Count(component => component.Kind == WallGraphComponentKind.IsolatedFragment),
            result.WallGraph.Edges.Count,
            repairCandidates.Count,
            repairCandidates.Count(candidate => candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked),
            repairCandidates.Count(candidate => candidate.Kind is WallGraphRepairCandidateKind.EndpointToWall or WallGraphRepairCandidateKind.EndpointToEndpoint),
            repairCandidates.Count(candidate => candidate.Kind == WallGraphRepairCandidateKind.EndpointOverrun),
            repairCandidates.Count(candidate => candidate.Severity == WallGraphRepairSeverity.High));
    }
}
