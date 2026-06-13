namespace OpenPlanTrace.Dxf;

public interface IDwgToDxfConverter
{
    string ConverterName { get; }

    ValueTask<DwgToDxfConversionResult> ConvertAsync(
        Stream dwgStream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default);
}
