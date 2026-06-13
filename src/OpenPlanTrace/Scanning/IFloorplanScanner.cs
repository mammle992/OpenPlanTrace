namespace OpenPlanTrace;

public interface IFloorplanScanner
{
    ValueTask<PlanScanResult> ScanAsync(
        PlanDocument document,
        ScannerOptions? options = null,
        IProgress<PipelineStageProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
