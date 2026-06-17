namespace OpenPlanTrace;

public static class WallStructuralTrust
{
    public static bool IsRejectedNonStructural(WallEvidenceWallAssessment? evidenceAssessment) =>
        evidenceAssessment?.RejectedAsNoise == true
        || evidenceAssessment?.Decision == WallEvidenceDecision.Reject;

    public static bool IsExcludedFromStructuralTopology(
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment) =>
        component?.ExcludedFromStructuralTopology == true
        || IsRejectedNonStructural(evidenceAssessment);
}
