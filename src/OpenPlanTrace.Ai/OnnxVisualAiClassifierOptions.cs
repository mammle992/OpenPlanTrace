using OpenPlanTrace;

namespace OpenPlanTrace.Ai;

public sealed record OnnxVisualAiClassifierOptions
{
    public required string ModelPath { get; init; }

    public string? LabelsPath { get; init; }

    public string? InputName { get; init; }

    public string? OutputName { get; init; }

    public string? ModelName { get; init; }

    public string? ModelVersion { get; init; }

    public int InputWidth { get; init; } = 224;

    public int InputHeight { get; init; } = 224;

    public bool ChannelsFirst { get; init; } = true;

    public int TopK { get; init; } = 5;

    public IReadOnlyList<float> Mean { get; init; } = new[] { 0.485f, 0.456f, 0.406f };

    public IReadOnlyList<float> StandardDeviation { get; init; } = new[] { 0.229f, 0.224f, 0.225f };
}

internal sealed record OnnxVisualAiLabel(string Label, ObjectCategory Category);
