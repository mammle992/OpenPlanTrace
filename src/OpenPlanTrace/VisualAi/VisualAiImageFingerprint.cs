using System.Globalization;

namespace OpenPlanTrace;

public sealed record VisualAiImageFingerprint(
    string AverageHash64,
    string DifferenceHash64,
    double InkRatio,
    double ObjectAspectRatio,
    string DensityBucket,
    string AspectBucket,
    string SimilarityKey)
{
    public static VisualAiImageFingerprint From(VisualAiImage image, PlanRect objectBounds)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (image.Width <= 0 || image.Height <= 0 || image.Channels <= 0)
        {
            return Empty(objectBounds);
        }

        var averageHash = AverageHash(image);
        var differenceHash = DifferenceHash(image);
        var inkRatio = ComputeInkRatio(image);
        var objectAspectRatio = objectBounds.Height <= 0
            ? 0
            : Math.Round(Math.Clamp(objectBounds.Width / objectBounds.Height, 0, 99), 6);
        var densityBucket = DensityBucketFor(inkRatio);
        var aspectBucket = AspectBucketFor(objectAspectRatio);
        var similarityKey =
            $"kvemo:{ShortHash(averageHash)}:{ShortHash(differenceHash)}:ink-{densityBucket}:aspect-{aspectBucket}";

        return new VisualAiImageFingerprint(
            averageHash,
            differenceHash,
            Math.Round(inkRatio, 6),
            objectAspectRatio,
            densityBucket,
            aspectBucket,
            similarityKey);
    }

    private static VisualAiImageFingerprint Empty(PlanRect objectBounds)
    {
        var objectAspectRatio = objectBounds.Height <= 0
            ? 0
            : Math.Round(Math.Clamp(objectBounds.Width / objectBounds.Height, 0, 99), 6);
        var aspectBucket = AspectBucketFor(objectAspectRatio);
        return new VisualAiImageFingerprint(
            "0000000000000000",
            "0000000000000000",
            0,
            objectAspectRatio,
            "empty",
            aspectBucket,
            $"kvemo:000000000000:000000000000:ink-empty:aspect-{aspectBucket}");
    }

    private static string AverageHash(VisualAiImage image)
    {
        var luma = new double[64];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                luma[(y * 8) + x] = SampleLuminance(
                    image,
                    (x + 0.5) * image.Width / 8.0 - 0.5,
                    (y + 0.5) * image.Height / 8.0 - 0.5);
            }
        }

        var average = luma.Average();
        ulong bits = 0;
        for (var index = 0; index < luma.Length; index++)
        {
            if (luma[index] <= average)
            {
                bits |= 1UL << index;
            }
        }

        return ToHex64(bits);
    }

    private static string DifferenceHash(VisualAiImage image)
    {
        var luma = new double[8, 9];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 9; x++)
            {
                luma[y, x] = SampleLuminance(
                    image,
                    (x + 0.5) * image.Width / 9.0 - 0.5,
                    (y + 0.5) * image.Height / 8.0 - 0.5);
            }
        }

        ulong bits = 0;
        var bitIndex = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (luma[y, x] > luma[y, x + 1])
                {
                    bits |= 1UL << bitIndex;
                }

                bitIndex++;
            }
        }

        return ToHex64(bits);
    }

    private static double ComputeInkRatio(VisualAiImage image)
    {
        var pixelCount = image.Width * image.Height;
        if (pixelCount <= 0)
        {
            return 0;
        }

        var ink = 0;
        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                if (PixelLuminance(image, x, y) < 245)
                {
                    ink++;
                }
            }
        }

        return ink / (double)pixelCount;
    }

    private static double SampleLuminance(VisualAiImage image, double x, double y)
    {
        var x0 = Clamp((int)Math.Floor(x), 0, image.Width - 1);
        var y0 = Clamp((int)Math.Floor(y), 0, image.Height - 1);
        var x1 = Clamp(x0 + 1, 0, image.Width - 1);
        var y1 = Clamp(y0 + 1, 0, image.Height - 1);
        var tx = Math.Clamp(x - x0, 0, 1);
        var ty = Math.Clamp(y - y0, 0, 1);

        var top = Lerp(PixelLuminance(image, x0, y0), PixelLuminance(image, x1, y0), tx);
        var bottom = Lerp(PixelLuminance(image, x0, y1), PixelLuminance(image, x1, y1), tx);
        return Lerp(top, bottom, ty);
    }

    private static double PixelLuminance(VisualAiImage image, int x, int y)
    {
        var offset = ((y * image.Width) + x) * image.Channels;
        if (offset < 0 || offset >= image.Pixels.Count)
        {
            return 255;
        }

        var red = image.Pixels[offset];
        var green = image.Channels > 1 && offset + 1 < image.Pixels.Count ? image.Pixels[offset + 1] : red;
        var blue = image.Channels > 2 && offset + 2 < image.Pixels.Count ? image.Pixels[offset + 2] : red;
        return (red * 0.299) + (green * 0.587) + (blue * 0.114);
    }

    private static string DensityBucketFor(double inkRatio) =>
        inkRatio switch
        {
            < 0.01 => "empty",
            < 0.03 => "sparse",
            < 0.08 => "light",
            < 0.18 => "medium",
            < 0.32 => "dense",
            _ => "very-dense"
        };

    private static string AspectBucketFor(double ratio) =>
        ratio switch
        {
            <= 0 => "unknown",
            < 0.55 => "tall",
            < 0.85 => "vertical",
            <= 1.20 => "square",
            <= 1.90 => "horizontal",
            _ => "wide"
        };

    private static string ShortHash(string hash) =>
        hash.Length <= 12 ? hash : hash[..12];

    private static string ToHex64(ulong value) =>
        value.ToString("x16", CultureInfo.InvariantCulture);

    private static double Lerp(double first, double second, double amount) =>
        first + ((second - first) * amount);

    private static int Clamp(int value, int min, int max) =>
        Math.Max(min, Math.Min(max, value));
}
