namespace OpenPlanTrace;

public sealed record BenchmarkRunResult(
    string? Name,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BenchmarkCaseResult> Cases)
{
    public const string CurrentSchemaVersion = "openplantrace.benchmark-result.v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public BenchmarkScoreboard Scoreboard { get; init; } = BenchmarkScoreboard.Empty;

    public IReadOnlyList<BenchmarkReviewQueueItem> ReviewQueue { get; init; } =
        Array.Empty<BenchmarkReviewQueueItem>();

    public int CaseCount => Cases.Count;

    public int ReviewQueueCount => ReviewQueue.Count;

    public int PassedCaseCount => Cases.Count(item => item.Passed && !item.Skipped);

    public int FailedCaseCount => Cases.Count(item => !item.Passed && !item.Skipped);

    public int SkippedCaseCount => Cases.Count(item => item.Skipped);

    public int PassedAssertionCount => Cases.Sum(item => item.PassedAssertionCount);

    public int FailedAssertionCount => Cases.Sum(item => item.FailedAssertionCount);

    public bool Passed => FailedCaseCount == 0;

    public static BenchmarkRunResult Create(string? name, IEnumerable<BenchmarkCaseResult> cases)
    {
        var caseArray = cases.ToArray();
        return new BenchmarkRunResult(name, DateTimeOffset.UtcNow, caseArray)
        {
            Scoreboard = BenchmarkScoreboard.FromCases(caseArray),
            ReviewQueue = CreateReviewQueue(caseArray)
        };
    }

    private static IReadOnlyList<BenchmarkReviewQueueItem> CreateReviewQueue(
        IReadOnlyList<BenchmarkCaseResult> cases)
    {
        var items = new List<BenchmarkReviewQueueItem>();
        foreach (var benchmarkCase in cases.Where(item => !item.Skipped))
        {
            foreach (var metric in benchmarkCase.Metrics)
            {
                foreach (var detection in metric.ExtraDetections)
                {
                    var kind = metric.PrecisionScoringEnabled
                        ? BenchmarkReviewQueueKind.PrecisionExtra
                        : BenchmarkReviewQueueKind.SpotCheckExtra;
                    items.Add(new BenchmarkReviewQueueItem(
                        benchmarkCase.FixtureId,
                        benchmarkCase.FixtureName,
                        benchmarkCase.SourcePath,
                        metric.Detector,
                        kind,
                        metric.PrecisionScoringEnabled,
                        detection,
                        RecommendedAction(kind)));
                }

                foreach (var detection in metric.ReviewOnlyDetections)
                {
                    items.Add(new BenchmarkReviewQueueItem(
                        benchmarkCase.FixtureId,
                        benchmarkCase.FixtureName,
                        benchmarkCase.SourcePath,
                        metric.Detector,
                        BenchmarkReviewQueueKind.ReviewOnly,
                        metric.PrecisionScoringEnabled,
                        detection,
                        RecommendedAction(BenchmarkReviewQueueKind.ReviewOnly)));
                }
            }
        }

        return items
            .OrderBy(item => ReviewPriority(item.Kind))
            .ThenBy(item => item.FixtureId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Detector, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Detection.Count ?? 0)
            .ThenBy(item => item.Detection.DetectionId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int ReviewPriority(BenchmarkReviewQueueKind kind) =>
        kind switch
        {
            BenchmarkReviewQueueKind.PrecisionExtra => 0,
            BenchmarkReviewQueueKind.SpotCheckExtra => 1,
            BenchmarkReviewQueueKind.ReviewOnly => 2,
            _ => 3
        };

    private static string RecommendedAction(BenchmarkReviewQueueKind kind) =>
        kind switch
        {
            BenchmarkReviewQueueKind.PrecisionExtra => "Review as likely detector false positive or add missing truth target if valid.",
            BenchmarkReviewQueueKind.SpotCheckExtra => "Review for truth expansion; add as target before enabling precision scoring.",
            BenchmarkReviewQueueKind.ReviewOnly => "Label, ignore, or promote through object review/correction workflow.",
            _ => "Review detection evidence."
        };
}
