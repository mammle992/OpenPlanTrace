using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace OpenPlanTrace.Dxf;

public sealed class ExternalDwgToDxfConverter : IDwgToDxfConverter
{
    private const string InputPlaceholder = "{input}";
    private const string OutputPlaceholder = "{output}";
    private const string OutputDirectoryPlaceholder = "{outputDir}";
    private const string TempDirectoryPlaceholder = "{tempDir}";
    private const string SourceNamePlaceholder = "{sourceName}";
    private const string SourceBaseNamePlaceholder = "{sourceBaseName}";

    private readonly ExternalDwgToDxfConverterOptions options;

    public ExternalDwgToDxfConverter(ExternalDwgToDxfConverterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);
        this.options = options;
    }

    public string ConverterName => Clean(options.ConverterName) ?? "ExternalDWG";

    public async ValueTask<DwgToDxfConversionResult> ConvertAsync(
        Stream dwgStream,
        PlanSourceDescriptor source,
        PlanLoadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dwgStream);
        ArgumentNullException.ThrowIfNull(source);

        var tempDirectory = Path.Combine(Path.GetTempPath(), $"openplantrace-dwg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var sourceName = Clean(source.Name)
                ?? Clean(source.FilePath is null ? null : Path.GetFileName(source.FilePath))
                ?? "input.dwg";
            var sourceBaseName = Clean(Path.GetFileNameWithoutExtension(sourceName)) ?? "input";
            var inputPath = Path.Combine(tempDirectory, $"{SafeFileName(sourceBaseName)}.dwg");
            var outputName = SafeOutputFileName(ReplacePlaceholders(this.options.OutputFileName, inputPath, tempDirectory, string.Empty, sourceName, sourceBaseName));
            var outputPath = Path.Combine(tempDirectory, outputName);

            await WriteInputAsync(dwgStream, inputPath, cancellationToken).ConfigureAwait(false);

            var processResult = await RunConverterAsync(
                    inputPath,
                    outputPath,
                    tempDirectory,
                    sourceName,
                    sourceBaseName,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!File.Exists(outputPath))
            {
                throw new PlanLoadException(
                    $"External DWG converter '{ConverterName}' completed but did not create expected DXF output '{outputName}'.");
            }

            var bytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                throw new PlanLoadException(
                    $"External DWG converter '{ConverterName}' created an empty DXF output '{outputName}'.");
            }

            var properties = CreateProperties(processResult, bytes.Length, tempDirectory, inputPath, outputPath);
            return new DwgToDxfConversionResult(
                new MemoryStream(bytes),
                outputName,
                properties);
        }
        finally
        {
            if (this.options.DeleteTemporaryFiles)
            {
                TryDeleteDirectory(tempDirectory);
            }
        }
    }

    private async Task<ExternalProcessResult> RunConverterAsync(
        string inputPath,
        string outputPath,
        string tempDirectory,
        string sourceName,
        string sourceBaseName,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            startInfo.WorkingDirectory = options.WorkingDirectory!;
        }

        foreach (var argument in options.Arguments ?? Array.Empty<string>())
        {
            startInfo.ArgumentList.Add(ReplacePlaceholders(
                argument,
                inputPath,
                tempDirectory,
                outputPath,
                sourceName,
                sourceBaseName));
        }

        var stopwatch = Stopwatch.StartNew();
        using var process = StartProcess(startInfo);

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new PlanLoadException(
                $"External DWG converter '{ConverterName}' timed out after {options.Timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)} seconds.");
        }

        stopwatch.Stop();
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new PlanLoadException(
                $"External DWG converter '{ConverterName}' exited with code {process.ExitCode}. "
                + $"stderr: {Snippet(stderr)} stdout: {Snippet(stdout)}");
        }

        return new ExternalProcessResult(process.ExitCode, stopwatch.Elapsed, stdout, stderr);
    }

    private IReadOnlyDictionary<string, string> CreateProperties(
        ExternalProcessResult process,
        int outputBytes,
        string tempDirectory,
        string inputPath,
        string outputPath)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["executionMode"] = "external-process",
            ["executable"] = Path.GetFileName(options.ExecutablePath),
            ["exitCode"] = process.ExitCode.ToString(CultureInfo.InvariantCulture),
            ["durationMilliseconds"] = process.Duration.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["timeoutMilliseconds"] = options.Timeout.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture),
            ["outputBytes"] = outputBytes.ToString(CultureInfo.InvariantCulture),
            ["outputFileName"] = Path.GetFileName(outputPath)
        };

        if (!options.DeleteTemporaryFiles)
        {
            properties["tempDirectory"] = tempDirectory;
            properties["inputPath"] = inputPath;
            properties["outputPath"] = outputPath;
        }

        if (options.CaptureProcessOutputInProperties)
        {
            properties["stdout"] = Snippet(process.Stdout);
            properties["stderr"] = Snippet(process.Stderr);
        }

        foreach (var (key, value) in options.Properties ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(key) && value is not null)
            {
                properties[key.Trim()] = value;
            }
        }

        return properties;
    }

    private static async Task WriteInputAsync(
        Stream source,
        string inputPath,
        CancellationToken cancellationToken)
    {
        await using var file = File.Create(inputPath);
        await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
    }

    private static string ReplacePlaceholders(
        string value,
        string inputPath,
        string tempDirectory,
        string outputPath,
        string sourceName,
        string sourceBaseName) =>
        value
            .Replace(InputPlaceholder, inputPath, StringComparison.OrdinalIgnoreCase)
            .Replace(OutputPlaceholder, outputPath, StringComparison.OrdinalIgnoreCase)
            .Replace(OutputDirectoryPlaceholder, tempDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace(TempDirectoryPlaceholder, tempDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace(SourceNamePlaceholder, sourceName, StringComparison.OrdinalIgnoreCase)
            .Replace(SourceBaseNamePlaceholder, sourceBaseName, StringComparison.OrdinalIgnoreCase);

    private static void Validate(ExternalDwgToDxfConverterOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ExecutablePath))
        {
            throw new ArgumentException("External DWG converter executable path is required.", nameof(options));
        }

        if (options.Arguments is null || options.Arguments.Count == 0)
        {
            throw new ArgumentException("External DWG converter arguments are required.", nameof(options));
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException("External DWG converter timeout must be greater than zero.", nameof(options));
        }

        var arguments = string.Join(" ", options.Arguments);
        if (!arguments.Contains(InputPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("External DWG converter arguments must include the {input} placeholder.", nameof(options));
        }

        if (!arguments.Contains(OutputPlaceholder, StringComparison.OrdinalIgnoreCase)
            && !arguments.Contains(OutputDirectoryPlaceholder, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("External DWG converter arguments must include the {output} or {outputDir} placeholder.", nameof(options));
        }
    }

    private Process StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new PlanLoadException($"External DWG converter '{ConverterName}' could not be started.");
        }
        catch (Win32Exception exception)
        {
            throw new PlanLoadException(
                $"External DWG converter '{ConverterName}' could not be started: {exception.Message}",
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new PlanLoadException(
                $"External DWG converter '{ConverterName}' could not be started: {exception.Message}",
                exception);
        }
    }

    private static string SafeOutputFileName(string value)
    {
        var fileName = SafeFileName(Path.GetFileName(value));
        return Path.GetExtension(fileName).Equals(".dxf", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : $"{Path.GetFileNameWithoutExtension(fileName)}.dxf";
    }

    private static string SafeFileName(string value)
    {
        var clean = Clean(value) ?? "converted";
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            clean = clean.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(clean) ? "converted" : clean;
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Snippet(string? value)
    {
        var clean = Clean(value);
        if (clean is null)
        {
            return string.Empty;
        }

        return clean.Length <= 300 ? clean : clean[..300];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ExternalProcessResult(
        int ExitCode,
        TimeSpan Duration,
        string Stdout,
        string Stderr);
}
