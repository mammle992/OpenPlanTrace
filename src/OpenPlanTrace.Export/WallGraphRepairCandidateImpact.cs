namespace OpenPlanTrace;

internal static class WallGraphRepairCandidateImpact
{
    public static IEnumerable<string> CoordinateImpactedWallIds(WallGraphRepairCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        foreach (var wallId in candidate.WallIds)
        {
            if (string.IsNullOrWhiteSpace(wallId))
            {
                continue;
            }

            if (candidate.Kind == WallGraphRepairCandidateKind.EndpointToWall
                && string.Equals(wallId, candidate.HostWallId, StringComparison.Ordinal))
            {
                continue;
            }

            yield return wallId;
        }

        if (candidate.Kind != WallGraphRepairCandidateKind.EndpointToWall
            && !string.IsNullOrWhiteSpace(candidate.HostWallId))
        {
            yield return candidate.HostWallId;
        }
    }
}
