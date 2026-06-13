using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenPlanTrace;

namespace OpenPlanTrace.Ai;

public sealed class OnnxVisualAiClassifier : IVisualAiObjectClassifier, IDisposable
{
    private readonly InferenceSession _session;
    private readonly OnnxVisualAiClassifierOptions _options;
    private readonly IReadOnlyList<OnnxVisualAiLabel> _labels;
    private readonly string _inputName;
    private readonly string _outputName;
    private bool _disposed;

    public OnnxVisualAiClassifier(OnnxVisualAiClassifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ModelPath))
        {
            throw new ArgumentException("Visual AI model path is required.", nameof(options));
        }

        if (!File.Exists(options.ModelPath))
        {
            throw new FileNotFoundException("Visual AI ONNX model file was not found.", options.ModelPath);
        }

        if (options.InputWidth <= 0 || options.InputHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Visual AI input width and height must be positive.");
        }

        _options = options;
        _session = new InferenceSession(options.ModelPath);
        _labels = OnnxVisualAiLabelLoader.Load(options.LabelsPath);
        _inputName = Clean(options.InputName) ?? _session.InputMetadata.Keys.First();
        _outputName = Clean(options.OutputName) ?? _session.OutputMetadata.Keys.First();
    }

    public ValueTask<VisualAiClassificationResult?> ClassifyAsync(
        VisualAiClassificationRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Crop.Channels != 3
            || request.Crop.Width <= 0
            || request.Crop.Height <= 0
            || request.Crop.Pixels.Count < request.Crop.Width * request.Crop.Height * request.Crop.Channels)
        {
            return ValueTask.FromResult<VisualAiClassificationResult?>(null);
        }

        var tensor = CreateInputTensor(request.Crop);
        using var results = _session.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor(_inputName, tensor)
        });
        var output = results.FirstOrDefault(result => result.Name == _outputName) ?? results.FirstOrDefault();
        if (output is null)
        {
            return ValueTask.FromResult<VisualAiClassificationResult?>(null);
        }

        var scores = output.AsEnumerable<float>().ToArray();
        if (scores.Length == 0)
        {
            return ValueTask.FromResult<VisualAiClassificationResult?>(null);
        }

        var probabilities = NormalizeScores(scores);
        var topK = Math.Clamp(_options.TopK, 1, Math.Min(20, probabilities.Length));
        var alternatives = probabilities
            .Select((score, index) => new { score, index })
            .OrderByDescending(item => item.score)
            .Take(topK)
            .Select(item => ToCandidate(item.index, item.score))
            .ToArray();
        var prediction = alternatives[0];

        return ValueTask.FromResult<VisualAiClassificationResult?>(
            new VisualAiClassificationResult(
                Clean(_options.ModelName) ?? Path.GetFileNameWithoutExtension(_options.ModelPath),
                Clean(_options.ModelVersion) ?? File.GetLastWriteTimeUtc(_options.ModelPath).ToString("yyyyMMddHHmmss"),
                "onnxruntime",
                prediction,
                alternatives,
                new[]
                {
                    $"ONNX model '{Path.GetFileName(_options.ModelPath)}' evaluated crop {request.Crop.SourceId}.",
                    $"input {_inputName} {(_options.ChannelsFirst ? "NCHW" : "NHWC")} {_options.InputWidth}x{_options.InputHeight}",
                    $"output {_outputName} classes={scores.Length}"
                }));
    }

    private DenseTensor<float> CreateInputTensor(VisualAiImage image)
    {
        var tensor = _options.ChannelsFirst
            ? new DenseTensor<float>(new[] { 1, 3, _options.InputHeight, _options.InputWidth })
            : new DenseTensor<float>(new[] { 1, _options.InputHeight, _options.InputWidth, 3 });
        var mean = Expand(_options.Mean, 0f);
        var standardDeviation = Expand(_options.StandardDeviation, 1f);

        for (var y = 0; y < _options.InputHeight; y++)
        {
            for (var x = 0; x < _options.InputWidth; x++)
            {
                var sourceX = (x + 0.5) * image.Width / _options.InputWidth - 0.5;
                var sourceY = (y + 0.5) * image.Height / _options.InputHeight - 0.5;
                var rgb = SampleBilinear(image, sourceX, sourceY);
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = ((rgb[channel] / 255f) - mean[channel]) / standardDeviation[channel];
                    if (_options.ChannelsFirst)
                    {
                        tensor[0, channel, y, x] = value;
                    }
                    else
                    {
                        tensor[0, y, x, channel] = value;
                    }
                }
            }
        }

        return tensor;
    }

    private static float[] Expand(IReadOnlyList<float> values, float fallback)
    {
        var expanded = new[] { fallback, fallback, fallback };
        for (var index = 0; index < Math.Min(values.Count, expanded.Length); index++)
        {
            expanded[index] = values[index] == 0 && fallback != 0 ? fallback : values[index];
        }

        return expanded;
    }

    private static float[] SampleBilinear(VisualAiImage image, double x, double y)
    {
        var x0 = Clamp((int)Math.Floor(x), 0, image.Width - 1);
        var y0 = Clamp((int)Math.Floor(y), 0, image.Height - 1);
        var x1 = Clamp(x0 + 1, 0, image.Width - 1);
        var y1 = Clamp(y0 + 1, 0, image.Height - 1);
        var tx = Math.Clamp(x - x0, 0, 1);
        var ty = Math.Clamp(y - y0, 0, 1);
        var result = new float[3];

        for (var channel = 0; channel < 3; channel++)
        {
            var top = Lerp(Pixel(image, x0, y0, channel), Pixel(image, x1, y0, channel), tx);
            var bottom = Lerp(Pixel(image, x0, y1, channel), Pixel(image, x1, y1, channel), tx);
            result[channel] = (float)Lerp(top, bottom, ty);
        }

        return result;
    }

    private static byte Pixel(VisualAiImage image, int x, int y, int channel)
    {
        var offset = ((y * image.Width) + x) * image.Channels + channel;
        return image.Pixels[offset];
    }

    private static double Lerp(double first, double second, double amount) =>
        first + ((second - first) * amount);

    private static int Clamp(int value, int min, int max) =>
        Math.Max(min, Math.Min(max, value));

    private static float[] NormalizeScores(IReadOnlyList<float> scores)
    {
        var sum = scores.Sum();
        var looksLikeProbabilities = sum > 0.98f
            && sum < 1.02f
            && scores.All(score => score >= 0f && score <= 1f);
        if (looksLikeProbabilities)
        {
            return scores.ToArray();
        }

        var max = scores.Max();
        var exps = scores.Select(score => Math.Exp(score - max)).ToArray();
        var expSum = exps.Sum();
        if (expSum <= 0 || double.IsNaN(expSum) || double.IsInfinity(expSum))
        {
            return Enumerable.Repeat(1f / scores.Count, scores.Count).ToArray();
        }

        return exps.Select(value => (float)(value / expSum)).ToArray();
    }

    private VisualAiClassificationCandidate ToCandidate(int index, float confidence)
    {
        var label = index < _labels.Count ? _labels[index].Label : $"class_{index}";
        var category = index < _labels.Count && _labels[index].Category != ObjectCategory.Unknown
            ? _labels[index].Category
            : VisualAiCategoryMapper.MapLabel(label);

        return new VisualAiClassificationCandidate(
            label,
            category,
            confidence,
            new Dictionary<string, string>
            {
                ["classIndex"] = index.ToString(),
                ["labelSource"] = index < _labels.Count ? "labels-file" : "generated-class-index"
            });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OnnxVisualAiClassifier));
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _session.Dispose();
        _disposed = true;
    }
}
