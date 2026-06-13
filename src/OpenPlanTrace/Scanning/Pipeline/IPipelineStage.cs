namespace OpenPlanTrace;

internal interface IPipelineStage
{
    string Name { get; }

    ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken);
}
