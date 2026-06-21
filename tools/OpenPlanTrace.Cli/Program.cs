using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.ML.OnnxRuntime;
using OpenPlanTrace;
using OpenPlanTrace.Ai;
using OpenPlanTrace.Dxf;
using OpenPlanTrace.Export;
using OpenPlanTrace.Pdf;

return await OpenPlanTraceCli.RunAsync(args);

internal static class OpenPlanTraceCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            WriteUsage();
            return 0;
        }

        return args[0].ToLowerInvariant() switch
        {
            "scan" => await RunScanAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "batch" => await RunBatchAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "batch-compare" => await RunBatchCompareAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "benchmark" => await RunBenchmarkAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "benchmark-draft" => await RunBenchmarkDraftAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "benchmark-compare" => await RunBenchmarkCompareAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "inspect" => await RunInspectAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "formats" => RunFormats(args.Skip(1).ToArray()),
            "schema" => await RunSchemaAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "validate" => await RunValidateAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "kvemo-report" => await RunKvemoReportAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "kvemo-profile-template" or "kvemo-crops-to-profile" =>
                await RunKvemoProfileTemplateAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "corrections-to-profile" or "object-corrections-to-profile" =>
                await RunCorrectionsToProfileAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            _ => UnknownCommand(args[0])
        };
    }

    private static async Task<int> RunInspectAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteInspectUsage();
            return args.Length == 0 ? 2 : 0;
        }

        InspectArguments parsed;
        try
        {
            parsed = InspectArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteInspectUsage();
            return 2;
        }

        if (parsed.InputPath is null)
        {
            Console.Error.WriteLine("Missing input file.");
            WriteInspectUsage();
            return 2;
        }

        if (!File.Exists(parsed.InputPath))
        {
            Console.Error.WriteLine($"Input file not found: {parsed.InputPath}");
            return 2;
        }

        var source = PlanSourceDescriptor.FromFilePath(parsed.InputPath);
        var registry = CreateLoaderRegistry();
        if (registry.FindLoader(source) is null)
        {
            Console.Error.WriteLine(UnsupportedSourceMessage(source));
            return 3;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            await using var stream = File.OpenRead(parsed.InputPath);
            var document = await registry.LoadAsync(stream, source).ConfigureAwait(false);
            stopwatch.Stop();
            var result = PlanDocumentInspectionResult.From(
                parsed.InputPath,
                source,
                document,
                stopwatch.Elapsed,
                parsed.TextSampleLimit);

            if (parsed.Json)
            {
                Console.WriteLine(JsonSerializer.Serialize(result, CreateInspectJsonOptions(parsed.PrettyJson)));
            }
            else
            {
                WriteInspectSummary(result);
            }

            return 0;
        }
        catch (PlanLoadException exception)
        {
            Console.Error.WriteLine(exception.Message);
            var capability = registry.GetCapability(source);
            if (!capability.CanLoad)
            {
                Console.Error.WriteLine(UnsupportedSourceMessage(source, capability));
            }
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Inspect failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunScanAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteScanUsage();
            return args.Length == 0 ? 2 : 0;
        }

        ScanArguments parsed;
        try
        {
            parsed = ScanArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteScanUsage();
            return 2;
        }

        if (parsed.InputPath is null)
        {
            Console.Error.WriteLine("Missing input file.");
            WriteScanUsage();
            return 2;
        }

        if (!File.Exists(parsed.InputPath))
        {
            Console.Error.WriteLine($"Input file not found: {parsed.InputPath}");
            return 2;
        }

        if (parsed.OutDirectory is not null)
        {
            Directory.CreateDirectory(parsed.OutDirectory);
            parsed.JsonPath ??= Path.Combine(parsed.OutDirectory, "scan.json");
            parsed.CompactScanPath ??= Path.Combine(parsed.OutDirectory, "scan.compact.json");
            parsed.CompactScanGZipPath ??= Path.Combine(parsed.OutDirectory, "scan.compact.json.gz");
            parsed.GeoJsonPath ??= Path.Combine(parsed.OutDirectory, "scan.geojson");
            parsed.PlacementPath ??= Path.Combine(parsed.OutDirectory, "placement.json");
            parsed.SvgDirectory ??= Path.Combine(parsed.OutDirectory, "overlays");
            parsed.VisualSnapshotPath ??= Path.Combine(parsed.OutDirectory, "visual-snapshot.json");
        }

        ScannerOptions scannerOptions;
        try
        {
            scannerOptions = CreateScannerOptions(parsed);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var engine = CreateEngine();

        try
        {
            var result = await engine.ScanFileAsync(
                parsed.InputPath,
                scannerOptions: scannerOptions,
                progress: parsed.TraceStages ? new ConsoleStageProgress() : null).ConfigureAwait(false);

            if (parsed.JsonPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
                await using var jsonStream = File.Create(parsed.JsonPath);
                await PlanTraceJsonExporter.WriteAsync(
                        result,
                        jsonStream,
                        new PlanTraceJsonExportOptions { WriteIndented = parsed.PrettyJson }).ConfigureAwait(false);
            }

            if (parsed.CompactScanPath is not null || parsed.CompactScanGZipPath is not null)
            {
                var compactJson = PlanTraceCompactJsonExporter.Serialize(result);

                if (parsed.CompactScanPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.CompactScanPath))!);
                    await File.WriteAllTextAsync(parsed.CompactScanPath, compactJson).ConfigureAwait(false);
                }

                if (parsed.CompactScanGZipPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.CompactScanGZipPath))!);
                    await using var fileStream = File.Create(parsed.CompactScanGZipPath);
                    await using var gzipStream = new GZipStream(fileStream, CompressionLevel.SmallestSize);
                    var bytes = Encoding.UTF8.GetBytes(compactJson);
                    await gzipStream.WriteAsync(bytes).ConfigureAwait(false);
                }
            }

            if (parsed.ObjectLabelTemplatePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.ObjectLabelTemplatePath))!);
                await using var profileStream = File.Create(parsed.ObjectLabelTemplatePath);
                var profile = ObjectLabelProfileTemplateBuilder.FromScanResult(result);
                await ObjectLabelProfileJsonSerializer.WriteAsync(
                        profile,
                        profileStream,
                        parsed.PrettyJson)
                    .ConfigureAwait(false);
            }

            if (parsed.ObjectReviewDatasetPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.ObjectReviewDatasetPath))!);
                await using var datasetStream = File.Create(parsed.ObjectReviewDatasetPath);
                var dataset = ObjectReviewDatasetBuilder.FromScanResult(result);
                await ObjectReviewDatasetJsonSerializer.WriteAsync(
                        dataset,
                        datasetStream,
                        parsed.PrettyJson)
                    .ConfigureAwait(false);
            }

            if (parsed.ObjectCorrectionTemplatePath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.ObjectCorrectionTemplatePath))!);
                await using var correctionStream = File.Create(parsed.ObjectCorrectionTemplatePath);
                var correctionDataset = ObjectCorrectionDatasetBuilder.FromScanResult(result);
                await ObjectCorrectionDatasetJsonSerializer.WriteAsync(
                        correctionDataset,
                        correctionStream,
                        parsed.PrettyJson)
                    .ConfigureAwait(false);
            }

            if (parsed.GeoJsonPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.GeoJsonPath))!);
                await using var geoJsonStream = File.Create(parsed.GeoJsonPath);
                await PlanTraceGeoJsonExporter.WriteAsync(
                        result,
                        geoJsonStream,
                        new PlanTraceGeoJsonExportOptions { WriteIndented = parsed.PrettyJson })
                    .ConfigureAwait(false);
            }

            if (parsed.PlacementPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.PlacementPath))!);
                await using var placementStream = File.Create(parsed.PlacementPath);
                await PlanPlacementJsonExporter.WriteAsync(
                        result,
                        placementStream,
                        new PlanPlacementJsonExportOptions { WriteIndented = parsed.PrettyJson })
                    .ConfigureAwait(false);
            }

            var svgPathsByPage = new Dictionary<int, string>();

            if (parsed.SvgPath is not null)
            {
                var pageNumber = parsed.PageNumber ?? result.Document.Pages.First().Number;
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.SvgPath))!);
                File.WriteAllText(
                    parsed.SvgPath,
                    PlanOverlaySvgRenderer.RenderPage(
                        result,
                        pageNumber,
                        CreateSvgOverlayRenderOptions(parsed, parsed.SvgPath, pageNumber)));
                svgPathsByPage[pageNumber] = SnapshotArtifactPath(parsed.VisualSnapshotPath, parsed.SvgPath);
            }

            if (parsed.SvgDirectory is not null)
            {
                Directory.CreateDirectory(parsed.SvgDirectory);
                foreach (var page in result.Document.Pages)
                {
                    var svgPath = Path.Combine(parsed.SvgDirectory, $"page-{page.Number}.svg");
                    File.WriteAllText(
                        svgPath,
                        PlanOverlaySvgRenderer.RenderPage(
                            result,
                            page.Number,
                            CreateSvgOverlayRenderOptions(parsed, svgPath, page.Number)));
                    svgPathsByPage[page.Number] = SnapshotArtifactPath(parsed.VisualSnapshotPath, svgPath);
                }
            }

            if (parsed.VisualSnapshotPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.VisualSnapshotPath))!);
                await using var snapshotStream = File.Create(parsed.VisualSnapshotPath);
                await PlanOverlaySnapshotJsonExporter.WriteAsync(
                        result,
                        snapshotStream,
                        new PlanOverlaySnapshotJsonExportOptions { WriteIndented = parsed.PrettyJson },
                        svgPathsByPage,
                        SvgOverlayRenderOptions.ForProfile(parsed.SvgProfile))
                    .ConfigureAwait(false);
            }

            WriteSummary(result, parsed);
            return result.Diagnostics.HasErrors ? 1 : 0;
        }
        catch (PlanLoadException exception)
        {
            var source = PlanSourceDescriptor.FromFilePath(parsed.InputPath);
            var capability = CreateLoaderRegistry().GetCapability(source);
            Console.Error.WriteLine(exception.Message);
            if (!capability.CanLoad)
            {
                Console.Error.WriteLine(UnsupportedSourceMessage(source, capability));
            }
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Scan failed: {exception.Message}");
            return 1;
        }
        finally
        {
            DisposeScannerOptions(scannerOptions);
        }
    }

    private static async Task<int> RunBatchAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteBatchUsage();
            return args.Length == 0 ? 2 : 0;
        }

        BatchArguments parsed;
        try
        {
            parsed = BatchArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteBatchUsage();
            return 2;
        }

        if (parsed.ManifestPath is not null)
        {
            var manifestPath = ResolveBatchManifestPath(parsed.ManifestPath);
            if (!File.Exists(manifestPath))
            {
                Console.Error.WriteLine($"Batch manifest not found: {manifestPath}");
                return 2;
            }

            BatchScanManifest manifest;
            try
            {
                await using var stream = File.OpenRead(manifestPath);
                manifest = await JsonSerializer.DeserializeAsync<BatchScanManifest>(
                        stream,
                        CreateBatchJsonOptions())
                    .ConfigureAwait(false)
                    ?? new BatchScanManifest();
                BatchScanManifest.ValidateSchemaVersion(manifest);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Could not read batch manifest: {exception.Message}");
                return 2;
            }

            var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))
                ?? Directory.GetCurrentDirectory();
            ApplyBatchManifest(parsed, manifest, manifestDirectory);
        }

        if (parsed.Inputs.Count == 0)
        {
            Console.Error.WriteLine("Missing batch input file or directory.");
            WriteBatchUsage();
            return 2;
        }

        if (parsed.OutDirectory is null)
        {
            Console.Error.WriteLine("Missing --out-dir for batch output.");
            WriteBatchUsage();
            return 2;
        }

        Directory.CreateDirectory(parsed.OutDirectory);
        parsed.JsonPath ??= Path.Combine(parsed.OutDirectory, "batch.json");

        var inputs = ResolveBatchInputs(parsed.Inputs, parsed.Recursive);
        var registry = CreateLoaderRegistry();
        ScannerOptions scannerOptions;
        try
        {
            scannerOptions = CreateScannerOptions(parsed);
            ValidateBatchExecutionOptions(parsed);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        try
        {
            var maxDegreeOfParallelism = parsed.MaxDegreeOfParallelism ?? 1;
            var retryCount = parsed.RetryCount ?? 0;
            var workItems = inputs
                .Select((input, index) => new BatchScanWorkItem(index, index + 1, input))
                .ToArray();
            var results = new BatchScanItemResult[workItems.Length];

            if (maxDegreeOfParallelism == 1)
            {
                foreach (var item in workItems)
                {
                    results[item.Index] = await ProcessBatchItemAsync(
                            item,
                            parsed,
                            scannerOptions,
                            registry,
                            retryCount)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                await Parallel.ForEachAsync(
                        workItems,
                        new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism },
                        async (item, _) =>
                        {
                            results[item.Index] = await ProcessBatchItemAsync(
                                    item,
                                    parsed,
                                    scannerOptions,
                                    registry,
                                    retryCount)
                                .ConfigureAwait(false);
                        })
                    .ConfigureAwait(false);
            }

            var batch = BatchScanRunResult.Create(
                parsed.OutDirectory,
                maxDegreeOfParallelism,
                retryCount,
                results);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
            await using (var output = File.Create(parsed.JsonPath))
            {
                await JsonSerializer.SerializeAsync(
                        output,
                        batch,
                        CreateBatchJsonOptions(parsed.PrettyJson))
                    .ConfigureAwait(false);
            }

            WriteBatchSummary(batch, parsed);
            return batch.Passed ? 0 : 1;
        }
        finally
        {
            DisposeScannerOptions(scannerOptions);
        }
    }

    private static async Task<int> RunBatchCompareAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteBatchCompareUsage();
            return args.Length == 0 ? 2 : 0;
        }

        BatchCompareArguments parsed;
        try
        {
            parsed = BatchCompareArguments.Parse(args);
            ValidateBatchCompareArguments(parsed);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteBatchCompareUsage();
            return 2;
        }

        if (parsed.BaselinePath is null || parsed.CandidatePath is null)
        {
            Console.Error.WriteLine("Missing baseline and candidate batch result paths.");
            WriteBatchCompareUsage();
            return 2;
        }

        if (!File.Exists(parsed.BaselinePath))
        {
            Console.Error.WriteLine($"Baseline batch result not found: {parsed.BaselinePath}");
            return 2;
        }

        if (!File.Exists(parsed.CandidatePath))
        {
            Console.Error.WriteLine($"Candidate batch result not found: {parsed.CandidatePath}");
            return 2;
        }

        BatchScanRunResult baseline;
        BatchScanRunResult candidate;
        try
        {
            baseline = await ReadBatchRunAsync(parsed.BaselinePath).ConfigureAwait(false);
            candidate = await ReadBatchRunAsync(parsed.CandidatePath).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read batch result: {exception.Message}");
            return 2;
        }

        var comparison = BatchScanComparisonResult.Compare(
            baseline,
            candidate,
            new BatchScanComparisonOptions
            {
                QualityConfidenceRegressionThreshold = parsed.QualityConfidenceDropThreshold,
                DurationRegressionRatio = parsed.DurationRegressionRatio,
                DurationRegressionMinimumMilliseconds = parsed.DurationRegressionMinimumMilliseconds,
                DiagnosticErrorIncreaseThreshold = parsed.DiagnosticErrorIncreaseThreshold,
                VisualIssueIncreaseThreshold = parsed.VisualIssueIncreaseThreshold
            });

        if (parsed.JsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
            await File.WriteAllTextAsync(
                    parsed.JsonPath,
                    JsonSerializer.Serialize(comparison, CreateBatchJsonOptions(parsed.PrettyJson)))
                .ConfigureAwait(false);
        }

        if (parsed.MarkdownPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.MarkdownPath))!);
            await File.WriteAllTextAsync(
                    parsed.MarkdownPath,
                    BatchScanComparisonMarkdownReport.Create(comparison))
                .ConfigureAwait(false);
        }

        WriteBatchCompareSummary(comparison, parsed);
        return comparison.Passed || parsed.NoFailOnRegression ? 0 : 1;
    }

    private static async ValueTask<BatchScanItemResult> ProcessBatchItemAsync(
        BatchScanWorkItem item,
        BatchArguments parsed,
        ScannerOptions scannerOptions,
        PlanDocumentLoaderRegistry registry,
        int retryCount)
    {
        var stopwatch = Stopwatch.StartNew();
        var source = PlanSourceDescriptor.FromFilePath(item.InputPath);
        var capability = registry.GetCapability(source);

        if (!File.Exists(item.InputPath))
        {
            stopwatch.Stop();
            return BatchScanItemResult.Failed(
                item.ItemNumber,
                item.InputPath,
                source,
                BatchScanItemStatus.Missing,
                "Input file not found.",
                stopwatch.Elapsed,
                attemptCount: 0);
        }

        if (registry.FindLoader(source) is null)
        {
            stopwatch.Stop();
            return BatchScanItemResult.Failed(
                item.ItemNumber,
                item.InputPath,
                source,
                BatchScanItemStatus.Unsupported,
                UnsupportedSourceMessage(source, capability),
                stopwatch.Elapsed,
                attemptCount: 0,
                sourceCapability: capability);
        }

        var itemDirectory = CreateBatchItemDirectory(parsed.OutDirectory!, item.InputPath, item.ItemNumber);
        var scanJsonPath = Path.Combine(itemDirectory, "scan.json");
        var geoJsonPath = parsed.GeoJson ? Path.Combine(itemDirectory, "scan.geojson") : null;
        var placementJsonPath = Path.Combine(itemDirectory, "placement.json");
        var overlayDirectory = parsed.NoSvg ? null : Path.Combine(itemDirectory, "overlays");
        var visualSnapshotPath = Path.Combine(itemDirectory, "visual-snapshot.json");
        Exception? lastException = null;
        var maxAttempts = retryCount + 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(itemDirectory);
                var engine = new OpenPlanTraceEngine(registry);
                var scan = await engine.ScanFileAsync(
                        item.InputPath,
                        scannerOptions: scannerOptions)
                    .ConfigureAwait(false);

                await using (var jsonStream = File.Create(scanJsonPath))
                {
                    await PlanTraceJsonExporter.WriteAsync(
                            scan,
                            jsonStream,
                            new PlanTraceJsonExportOptions { WriteIndented = parsed.PrettyJson })
                        .ConfigureAwait(false);
                }

                if (geoJsonPath is not null)
                {
                    await using var geoJsonStream = File.Create(geoJsonPath);
                    await PlanTraceGeoJsonExporter.WriteAsync(
                            scan,
                            geoJsonStream,
                            new PlanTraceGeoJsonExportOptions { WriteIndented = parsed.PrettyJson })
                        .ConfigureAwait(false);
                }

                await using (var placementStream = File.Create(placementJsonPath))
                {
                    await PlanPlacementJsonExporter.WriteAsync(
                            scan,
                            placementStream,
                            new PlanPlacementJsonExportOptions { WriteIndented = parsed.PrettyJson })
                        .ConfigureAwait(false);
                }

                if (overlayDirectory is not null)
                {
                    Directory.CreateDirectory(overlayDirectory);
                    var svgPathsByPage = new Dictionary<int, string>();
                    foreach (var page in scan.Document.Pages)
                    {
                        var svgPath = Path.Combine(overlayDirectory, $"page-{page.Number}.svg");
                        File.WriteAllText(
                            svgPath,
                            PlanOverlaySvgRenderer.RenderPage(
                                scan,
                                page.Number,
                                SvgOverlayRenderOptions.ForProfile(parsed.SvgProfile)));
                        svgPathsByPage[page.Number] = SnapshotArtifactPath(visualSnapshotPath, svgPath);
                    }

                    await using var snapshotStream = File.Create(visualSnapshotPath);
                    await PlanOverlaySnapshotJsonExporter.WriteAsync(
                            scan,
                            snapshotStream,
                            new PlanOverlaySnapshotJsonExportOptions { WriteIndented = parsed.PrettyJson },
                            svgPathsByPage,
                            SvgOverlayRenderOptions.ForProfile(parsed.SvgProfile))
                        .ConfigureAwait(false);
                }
                else
                {
                    await using var snapshotStream = File.Create(visualSnapshotPath);
                    await PlanOverlaySnapshotJsonExporter.WriteAsync(
                            scan,
                            snapshotStream,
                            new PlanOverlaySnapshotJsonExportOptions { WriteIndented = parsed.PrettyJson })
                        .ConfigureAwait(false);
                }

                var snapshot = PlanOverlaySnapshot.From(
                    scan,
                    overlayDirectory is null
                        ? null
                        : scan.Document.Pages.ToDictionary(
                            page => page.Number,
                            page => SnapshotArtifactPath(
                                visualSnapshotPath,
                                Path.Combine(overlayDirectory, $"page-{page.Number}.svg"))),
                    SvgOverlayRenderOptions.ForProfile(parsed.SvgProfile));

                stopwatch.Stop();
                return BatchScanItemResult.FromScan(
                    item.ItemNumber,
                    item.InputPath,
                    source,
                    scan,
                    scanJsonPath,
                    geoJsonPath,
                    placementJsonPath,
                    overlayDirectory,
                    visualSnapshotPath,
                    snapshot,
                    stopwatch.Elapsed,
                    attempt);
            }
            catch (PlanLoadException exception)
            {
                lastException = exception;
            }
            catch (Exception exception)
            {
                lastException = exception;
            }
        }

        stopwatch.Stop();
        var message = lastException is PlanLoadException loadException
            ? capability.CanLoad
                ? loadException.Message
                : $"{loadException.Message} {UnsupportedSourceMessage(source, capability)}".Trim()
            : lastException?.Message ?? "Batch item failed.";
        if (maxAttempts > 1)
        {
            message = $"{message} Failed after {maxAttempts} attempts.";
        }

        return BatchScanItemResult.Failed(
            item.ItemNumber,
            item.InputPath,
            source,
            BatchScanItemStatus.Failed,
            message,
            stopwatch.Elapsed,
            maxAttempts,
            capability);
    }

    private static async Task<int> RunBenchmarkAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteBenchmarkUsage();
            return args.Length == 0 ? 2 : 0;
        }

        BenchmarkArguments parsed;
        try
        {
            parsed = BenchmarkArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteBenchmarkUsage();
            return 2;
        }

        if (parsed.ManifestPath is null)
        {
            Console.Error.WriteLine("Missing benchmark manifest path.");
            WriteBenchmarkUsage();
            return 2;
        }

        var manifestPath = ResolveManifestPath(parsed.ManifestPath);
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Benchmark manifest not found: {manifestPath}");
            return 2;
        }

        BenchmarkManifest manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<BenchmarkManifest>(
                    stream,
                    CreateBenchmarkJsonOptions())
                .ConfigureAwait(false)
                ?? new BenchmarkManifest();
            BenchmarkManifest.ValidateSchemaVersion(manifest);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read benchmark manifest: {exception.Message}");
            return 2;
        }

        var manifestDirectory = Path.GetDirectoryName(Path.GetFullPath(manifestPath)) ?? Directory.GetCurrentDirectory();
        ScannerOptions scannerOptions;
        try
        {
            scannerOptions = CreateScannerOptions(parsed);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        var engine = CreateEngine();
        var results = new List<BenchmarkCaseResult>();

        foreach (var fixture in manifest.Fixtures ?? Array.Empty<BenchmarkFixture>())
        {
            var fixturePath = ResolveFixturePath(manifestDirectory, fixture.SourcePath);
            var stopwatch = Stopwatch.StartNew();

            if (!File.Exists(fixturePath))
            {
                stopwatch.Stop();
                if (fixture.Optional)
                {
                    results.Add(PlanBenchmarkEvaluator.SkippedFixture(
                        fixture with { SourcePath = fixturePath },
                        OptionalFixtureSkipReason(fixture, fixturePath),
                        stopwatch.Elapsed));
                    continue;
                }

                results.Add(PlanBenchmarkEvaluator.FailedScan(
                    fixture with { SourcePath = fixturePath },
                    $"Input file not found: {fixturePath}",
                    stopwatch.Elapsed));
                continue;
            }

            try
            {
                var scan = await engine.ScanFileAsync(
                        fixturePath,
                        scannerOptions: scannerOptions)
                    .ConfigureAwait(false);
                stopwatch.Stop();
                results.Add(PlanBenchmarkEvaluator.Evaluate(
                    fixture with { SourcePath = fixturePath },
                    scan,
                    stopwatch.Elapsed));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                results.Add(PlanBenchmarkEvaluator.FailedScan(
                    fixture with { SourcePath = fixturePath },
                    exception.Message,
                    stopwatch.Elapsed));
            }
        }

        var run = BenchmarkRunResult.Create(manifest.Name, results);

        if (parsed.JsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
            await using var output = File.Create(parsed.JsonPath);
            await JsonSerializer.SerializeAsync(
                    output,
                    run,
                    CreateBenchmarkJsonOptions(parsed.PrettyJson))
                .ConfigureAwait(false);
        }

        if (parsed.MarkdownPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.MarkdownPath))!);
            await File.WriteAllTextAsync(
                    parsed.MarkdownPath,
                    BenchmarkMarkdownReport.Create(run))
                .ConfigureAwait(false);
        }

        WriteBenchmarkSummary(run, parsed);
        return run.Passed ? 0 : 1;
    }

    private static async Task<int> RunBenchmarkDraftAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteBenchmarkDraftUsage();
            return args.Length == 0 ? 2 : 0;
        }

        BenchmarkDraftArguments parsed;
        try
        {
            parsed = BenchmarkDraftArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteBenchmarkDraftUsage();
            return 2;
        }

        if (parsed.ScanJsonPath is null)
        {
            Console.Error.WriteLine("Missing scan JSON path.");
            WriteBenchmarkDraftUsage();
            return 2;
        }

        if (!File.Exists(parsed.ScanJsonPath))
        {
            Console.Error.WriteLine($"Scan JSON not found: {parsed.ScanJsonPath}");
            return 2;
        }

        BenchmarkManifest manifest;
        try
        {
            await using var stream = File.OpenRead(parsed.ScanJsonPath);
            using var scanJson = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            manifest = BenchmarkManifestDraftBuilder.FromScanJson(
                scanJson,
                new BenchmarkManifestDraftOptions
                {
                    FixtureId = parsed.FixtureId ?? Path.GetFileNameWithoutExtension(parsed.ScanJsonPath),
                    FixtureName = parsed.FixtureName,
                    ManifestName = parsed.ManifestName,
                    SourcePath = parsed.SourcePath ?? string.Empty,
                    Optional = parsed.Optional,
                    SkipReason = parsed.SkipReason,
                    MaxTargetsPerDetector = parsed.MaxTargetsPerDetector,
                    TargetRecall = parsed.TargetRecall,
                    TargetPrecision = parsed.TargetPrecision,
                    IncludeBounds = parsed.IncludeBounds
                });
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or JsonException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not draft benchmark manifest: {exception.Message}");
            return 2;
        }

        var json = JsonSerializer.Serialize(manifest, CreateBenchmarkJsonOptions(parsed.PrettyJson));
        if (parsed.JsonPath is null)
        {
            Console.WriteLine(json);
        }
        else if (parsed.JsonPath is { } jsonPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonPath))!);
            await File.WriteAllTextAsync(jsonPath, json).ConfigureAwait(false);
            var fixture = manifest.Fixtures.Count == 1 ? manifest.Fixtures[0] : null;
            Console.WriteLine(
                $"Wrote benchmark draft: {jsonPath}"
                + (fixture is null ? string.Empty : $" fixture={fixture.Id} source={fixture.SourcePath}"));
        }

        if (parsed.ReviewMarkdownPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.ReviewMarkdownPath))!);
            await File.WriteAllTextAsync(
                    parsed.ReviewMarkdownPath,
                    BenchmarkManifestDraftMarkdownReport.Create(manifest))
                .ConfigureAwait(false);
            var message = $"Wrote benchmark draft review: {parsed.ReviewMarkdownPath}";
            if (parsed.JsonPath is null)
            {
                Console.Error.WriteLine(message);
            }
            else
            {
                Console.WriteLine(message);
            }
        }

        return 0;
    }

    private static async Task<int> RunBenchmarkCompareAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteBenchmarkCompareUsage();
            return args.Length == 0 ? 2 : 0;
        }

        BenchmarkCompareArguments parsed;
        try
        {
            parsed = BenchmarkCompareArguments.Parse(args);
            ValidateBenchmarkCompareArguments(parsed);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteBenchmarkCompareUsage();
            return 2;
        }

        if (parsed.BaselinePath is null || parsed.CandidatePath is null)
        {
            Console.Error.WriteLine("Missing baseline and candidate benchmark result paths.");
            WriteBenchmarkCompareUsage();
            return 2;
        }

        if (!File.Exists(parsed.BaselinePath))
        {
            Console.Error.WriteLine($"Baseline benchmark result not found: {parsed.BaselinePath}");
            return 2;
        }

        if (!File.Exists(parsed.CandidatePath))
        {
            Console.Error.WriteLine($"Candidate benchmark result not found: {parsed.CandidatePath}");
            return 2;
        }

        BenchmarkRunResult baseline;
        BenchmarkRunResult candidate;
        try
        {
            baseline = await ReadBenchmarkRunAsync(parsed.BaselinePath).ConfigureAwait(false);
            candidate = await ReadBenchmarkRunAsync(parsed.CandidatePath).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Could not read benchmark result: {exception.Message}");
            return 2;
        }

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                QualityConfidenceRegressionThreshold = parsed.QualityConfidenceDropThreshold,
                DurationRegressionRatio = parsed.DurationRegressionRatio,
                DurationRegressionMinimumMilliseconds = parsed.DurationRegressionMinimumMilliseconds,
                DetectorRecallRegressionThreshold = parsed.DetectorRecallDropThreshold,
                DetectorPrecisionRegressionThreshold = parsed.DetectorPrecisionDropThreshold,
                DetectorF1RegressionThreshold = parsed.DetectorF1DropThreshold
            });

        if (parsed.JsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
            await File.WriteAllTextAsync(
                    parsed.JsonPath,
                    JsonSerializer.Serialize(comparison, CreateBenchmarkJsonOptions(parsed.PrettyJson)))
                .ConfigureAwait(false);
        }

        if (parsed.MarkdownPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.MarkdownPath))!);
            await File.WriteAllTextAsync(
                    parsed.MarkdownPath,
                    BenchmarkComparisonMarkdownReport.Create(comparison))
                .ConfigureAwait(false);
        }

        WriteBenchmarkCompareSummary(comparison, parsed);
        return comparison.Passed || parsed.NoFailOnRegression ? 0 : 1;
    }

    private static int RunFormats(string[] args)
    {
        if (args.Any(IsHelp))
        {
            WriteFormatsUsage();
            return 0;
        }

        FormatsArguments parsed;
        try
        {
            parsed = FormatsArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteFormatsUsage();
            return 2;
        }

        var capabilities = CreateLoaderRegistry().GetCapabilities();
        if (parsed.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(capabilities, CreateFormatsJsonOptions(parsed.PrettyJson)));
            return 0;
        }

        Console.WriteLine("OpenPlanTrace input format capabilities:");
        foreach (var capability in capabilities.Where(item => item.Kind != PlanSourceKind.Unknown))
        {
            var extensions = capability.Extensions.Count == 0 ? "-" : string.Join(", ", capability.Extensions);
            var loaders = capability.RegisteredLoaderNames.Count == 0 ? "-" : string.Join(", ", capability.RegisteredLoaderNames);
            Console.WriteLine($"  {capability.Key,-20} {capability.Status,-23} extensions {extensions}; loaders {loaders}");
            Console.WriteLine($"    {capability.Message}");
            if (capability.Status != PlanSourceSupportStatus.Registered)
            {
                Console.WriteLine($"    Adapter: {capability.AdapterRequirement}");
                Console.WriteLine($"    Licensing: {capability.LicenseNote}");
            }
        }

        return 0;
    }

    private static async Task<int> RunSchemaAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteSchemaUsage();
            return args.Length == 0 ? 2 : 0;
        }

        SchemaArguments parsed;
        try
        {
            parsed = SchemaArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteSchemaUsage();
            return 2;
        }

        var schema = ResolveSchema(parsed.SchemaName!);
        if (schema is null)
        {
            Console.Error.WriteLine($"Unknown schema: {parsed.SchemaName}");
            WriteSchemaUsage();
            return 2;
        }

        if (parsed.JsonPath is not null)
        {
            var fullPath = Path.GetFullPath(parsed.JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, schema.Json).ConfigureAwait(false);
            Console.WriteLine($"Schema: {fullPath}");
            return 0;
        }

        Console.WriteLine(schema.Json);
        return 0;
    }

    private static async Task<BenchmarkRunResult> ReadBenchmarkRunAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<BenchmarkRunResult>(
                stream,
                CreateBenchmarkJsonOptions())
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Benchmark result JSON was empty.");
    }

    private static async Task<BatchScanRunResult> ReadBatchRunAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var result = await JsonSerializer.DeserializeAsync<BatchScanRunResult>(
                stream,
                CreateBatchJsonOptions())
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException("Batch result JSON was empty.");

        if (!string.Equals(result.SchemaVersion, BatchScanRunResult.CurrentSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Unsupported batch result schemaVersion '{result.SchemaVersion}'. Expected '{BatchScanRunResult.CurrentSchemaVersion}'.");
        }

        return result;
    }

    private static async Task<int> RunKvemoReportAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteKvemoReportUsage();
            return args.Length == 0 ? 2 : 0;
        }

        KvemoReportArguments parsed;
        try
        {
            parsed = KvemoReportArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteKvemoReportUsage();
            return 2;
        }

        if (parsed.ManifestPath is null)
        {
            Console.Error.WriteLine("Missing Kvemo crop manifest path.");
            WriteKvemoReportUsage();
            return 2;
        }

        if (!File.Exists(parsed.ManifestPath))
        {
            Console.Error.WriteLine($"Kvemo crop manifest not found: {parsed.ManifestPath}");
            return 2;
        }

        try
        {
            var report = await KvemoCropManifestReport.ReadAsync(parsed.ManifestPath).ConfigureAwait(false);
            if (parsed.JsonPath is not null)
            {
                var fullPath = Path.GetFullPath(parsed.JsonPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(
                        fullPath,
                        JsonSerializer.Serialize(report, CreateKvemoReportJsonOptions(parsed.PrettyJson)))
                    .ConfigureAwait(false);
                Console.WriteLine($"Kvemo report JSON: {fullPath}");
                return 0;
            }

            WriteKvemoReportSummary(report);
            return report.InvalidEntryCount == 0 ? 0 : 1;
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine($"Kvemo report failed: {exception.Message}");
            return 1;
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Kvemo report failed: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunValidateAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteValidateUsage();
            return args.Length == 0 ? 2 : 0;
        }

        ValidateArguments parsed;
        try
        {
            parsed = ValidateArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteValidateUsage();
            return 2;
        }

        if (parsed.InputPath is null)
        {
            Console.Error.WriteLine("Missing validation input path.");
            WriteValidateUsage();
            return 2;
        }

        if (!File.Exists(parsed.InputPath))
        {
            Console.Error.WriteLine($"Validation input not found: {parsed.InputPath}");
            return 2;
        }

        ArtifactValidationResult result;
        try
        {
            result = ValidateArtifact(
                parsed.InputPath,
                await File.ReadAllTextAsync(parsed.InputPath).ConfigureAwait(false),
                parsed.Kind,
                parsed.Deep);
        }
        catch (Exception exception)
        {
            result = new ArtifactValidationResult(
                parsed.InputPath,
                parsed.Kind ?? "auto",
                null,
                false,
                new[]
                {
                    new ArtifactValidationMessage("error", $"Validation failed: {exception.Message}")
                });
        }

        if (parsed.JsonPath is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(parsed.JsonPath))!);
            await File.WriteAllTextAsync(
                    parsed.JsonPath,
                    JsonSerializer.Serialize(result, CreateValidationJsonOptions(parsed.PrettyJson)))
                .ConfigureAwait(false);
        }

        WriteValidationSummary(result, parsed);
        return result.Valid ? 0 : 1;
    }

    private static async Task<int> RunCorrectionsToProfileAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteCorrectionsToProfileUsage();
            return args.Length == 0 ? 2 : 0;
        }

        CorrectionProfileArguments parsed;
        try
        {
            parsed = CorrectionProfileArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteCorrectionsToProfileUsage();
            return 2;
        }

        if (parsed.InputPath is null)
        {
            Console.Error.WriteLine("Missing object correction dataset input path.");
            WriteCorrectionsToProfileUsage();
            return 2;
        }

        if (!File.Exists(parsed.InputPath))
        {
            Console.Error.WriteLine($"Object correction dataset not found: {parsed.InputPath}");
            return 2;
        }

        ObjectLabelProfile profile;
        try
        {
            var dataset = ObjectCorrectionDataset.ParseJson(
                await File.ReadAllTextAsync(parsed.InputPath).ConfigureAwait(false));
            profile = dataset.ToObjectLabelProfile(
                new ObjectCorrectionLabelProfileOptions
                {
                    Name = parsed.Name,
                    Version = parsed.Version,
                    IncludeConfirmed = parsed.IncludeConfirmed,
                    IncludeCorrected = parsed.IncludeCorrected
                });
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not convert object corrections: {exception.Message}");
            return 2;
        }

        if (profile.Rules.Count == 0)
        {
            Console.Error.WriteLine(
                "No reusable object label rules were produced. Mark correction actions Confirmed or Corrected and use MatchingSignature or MatchingSymbolAndLayer apply scopes.");
        }

        if (parsed.JsonPath is not null)
        {
            var fullPath = Path.GetFullPath(parsed.JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var stream = File.Create(fullPath);
            await ObjectLabelProfileJsonSerializer.WriteAsync(profile, stream, parsed.PrettyJson).ConfigureAwait(false);
            Console.WriteLine($"Object label profile: {fullPath}");
            Console.WriteLine($"Rules: {profile.Rules.Count}");
            return 0;
        }

        Console.WriteLine(ObjectLabelProfileJsonSerializer.Serialize(profile, parsed.PrettyJson));
        return 0;
    }

    private static async Task<int> RunKvemoProfileTemplateAsync(string[] args)
    {
        if (args.Length == 0 || args.Any(IsHelp))
        {
            WriteKvemoProfileTemplateUsage();
            return args.Length == 0 ? 2 : 0;
        }

        KvemoProfileTemplateArguments parsed;
        try
        {
            parsed = KvemoProfileTemplateArguments.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            WriteKvemoProfileTemplateUsage();
            return 2;
        }

        if (parsed.ManifestPath is null)
        {
            Console.Error.WriteLine("Missing Kvemo crop manifest path.");
            WriteKvemoProfileTemplateUsage();
            return 2;
        }

        if (!File.Exists(parsed.ManifestPath))
        {
            Console.Error.WriteLine($"Kvemo crop manifest not found: {parsed.ManifestPath}");
            return 2;
        }

        KvemoCropManifestLabelProfileResult result;
        try
        {
            result = await KvemoCropManifestLabelProfileBuilder.ReadAsync(
                    parsed.ManifestPath,
                    new KvemoCropManifestLabelProfileOptions
                    {
                        Name = parsed.Name,
                        Version = parsed.Version,
                        IncludeCropOnly = parsed.IncludeCropOnly,
                        IncludeClassified = parsed.IncludeClassified,
                        IncludeHardNegativeReviews = parsed.IncludeHardNegativeReviews,
                        MaxRules = parsed.MaxRules
                    })
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not build Kvemo object label profile template: {exception.Message}");
            return 2;
        }

        if (result.InvalidEntryCount > 0)
        {
            Console.Error.WriteLine($"Kvemo crop manifest contains {result.InvalidEntryCount} invalid entr{(result.InvalidEntryCount == 1 ? "y" : "ies")}.");
            foreach (var issue in result.Issues.Where(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)).Take(10))
            {
                Console.Error.WriteLine($"  line {issue.LineNumber}: {issue.Message}");
            }

            return 1;
        }

        if (result.RuleCount == 0)
        {
            Console.Error.WriteLine("No reusable draft object label rules were produced from the Kvemo crop manifest.");
        }

        if (parsed.JsonPath is not null)
        {
            var fullPath = Path.GetFullPath(parsed.JsonPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using var stream = File.Create(fullPath);
            await ObjectLabelProfileJsonSerializer.WriteAsync(result.Profile, stream, parsed.PrettyJson).ConfigureAwait(false);
            Console.WriteLine($"Object label profile template: {fullPath}");
            Console.WriteLine($"Rules: {result.RuleCount}");
            Console.WriteLine($"Skipped entries: {result.SkippedEntryCount}");
            return 0;
        }

        Console.WriteLine(ObjectLabelProfileJsonSerializer.Serialize(result.Profile, parsed.PrettyJson));
        return 0;
    }

    private static SchemaContent? ResolveSchema(string schemaName)
    {
        if (string.Equals(schemaName, "scan", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("scan", PlanTraceJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "scan-compact", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "compact-scan", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "scan.compact", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("scan-compact", PlanTraceCompactJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "object-review-dataset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "object-review", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("object-review-dataset", ObjectReviewDatasetJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "object-correction-dataset", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "object-corrections", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("object-correction-dataset", ObjectCorrectionDatasetJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "benchmark-manifest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("benchmark-manifest", BenchmarkManifestJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "benchmark-result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "benchmark-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "benchmark-output", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("benchmark-result", BenchmarkRunResultJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "benchmark-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "benchmark-compare", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("benchmark-comparison", BenchmarkComparisonJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "viewer-benchmark-review-session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "benchmark-review-session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "review-session", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "viewer-review-session", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("viewer-benchmark-review-session", ViewerBenchmarkReviewSessionJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "batch-manifest", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "batch", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("batch-manifest", BatchScanManifestJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "batch-result", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "batch-run", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "batch-summary", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("batch-result", BatchScanRunResultJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "batch-comparison", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "batch-compare", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("batch-comparison", BatchScanComparisonJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "layer-profile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "layers", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("layer-profile", LayerCategoryProfileJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "object-label-profile", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "object-labels", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("object-label-profile", ObjectLabelProfileJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "kvemo-crops", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "visual-ai-crops", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("kvemo-crops", VisualAiCropManifestJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "placement", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "placement-export", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "consumer-placement", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("placement", PlanPlacementJsonSchema.ReadCurrent());
        }

        if (string.Equals(schemaName, "visual-snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "overlay-snapshot", StringComparison.OrdinalIgnoreCase)
            || string.Equals(schemaName, "visual-qa", StringComparison.OrdinalIgnoreCase))
        {
            return new SchemaContent("visual-snapshot", PlanOverlaySnapshotJsonSchema.ReadCurrent());
        }

        return null;
    }

    private static ArtifactValidationResult ValidateArtifact(
        string inputPath,
        string json,
        string? requestedKind,
        bool deep = false)
    {
        if (IsKvemoCropsValidationKind(requestedKind)
            || (requestedKind is null && inputPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            return ValidateKvemoCropManifest(inputPath, json);
        }

        var messages = new List<ArtifactValidationMessage>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            return new ArtifactValidationResult(
                inputPath,
                requestedKind ?? "auto",
                null,
                false,
                new[] { new ArtifactValidationMessage("error", $"JSON is invalid: {exception.Message}") });
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ArtifactValidationResult(
                    inputPath,
                    requestedKind ?? "auto",
                    null,
                    false,
                    new[] { new ArtifactValidationMessage("error", "Artifact root must be a JSON object.") });
            }

            var schemaVersion = ReadStringProperty(document.RootElement, "schemaVersion");
            var kind = ResolveValidationKind(requestedKind, document.RootElement, schemaVersion);
            if (kind is null)
            {
                messages.Add(new ArtifactValidationMessage(
                "error",
                    "Could not infer artifact kind. Provide --kind scan, scan-compact, object-review-dataset, object-correction-dataset, benchmark-manifest, benchmark-result, benchmark-comparison, viewer-benchmark-review-session, batch-manifest, batch-result, batch-comparison, layer-profile, object-label-profile, kvemo-crops, placement, visual-snapshot, or geojson."));
                return new ArtifactValidationResult(inputPath, "unknown", schemaVersion, false, messages);
            }

            ValidateTopLevelContract(kind, document.RootElement, messages);
            ValidateByKind(kind, json, document.RootElement, messages);
            if (deep)
            {
                ValidateDeepArtifact(inputPath, kind, document.RootElement, messages);
            }

            if (messages.Count == 0)
            {
                messages.Add(new ArtifactValidationMessage("info", "Artifact matches the current OpenPlanTrace contract checks."));
            }

            return new ArtifactValidationResult(
                inputPath,
                kind,
                schemaVersion,
                !messages.Any(message => string.Equals(message.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                messages);
        }
    }

    private static ArtifactValidationResult ValidateKvemoCropManifest(
        string inputPath,
        string json)
    {
        var messages = new List<ArtifactValidationMessage>();
        var entryCount = 0;
        var classifiedCount = 0;
        var lineNumber = 0;

        using var reader = new StringReader(json);
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"line {lineNumber}: JSON is invalid: {exception.Message}"));
                continue;
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"line {lineNumber}: Kvemo crop entry root must be a JSON object."));
                    continue;
                }

                entryCount++;
                var entryMessages = new List<ArtifactValidationMessage>();
                ValidateTopLevelContract("kvemo-crops", document.RootElement, entryMessages);
                ValidateKvemoCropEntry(document.RootElement, entryMessages);
                foreach (var message in entryMessages)
                {
                    messages.Add(message with { Message = $"line {lineNumber}: {message.Message}" });
                }

                if (document.RootElement.TryGetProperty("classification", out var classification)
                    && classification.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    classifiedCount++;
                }
            }
        }

        if (entryCount == 0)
        {
            messages.Add(new ArtifactValidationMessage("error", "Kvemo crop manifest contains no JSONL entries."));
        }

        if (messages.Count == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "info",
                $"Kvemo crop manifest contains {entryCount} valid entr{(entryCount == 1 ? "y" : "ies")} ({classifiedCount} model-classified)."));
        }

        return new ArtifactValidationResult(
            inputPath,
            "kvemo-crops",
            VisualAiCropManifestEntry.CurrentSchemaVersion,
            !messages.Any(message => string.Equals(message.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            messages);
    }

    private static void ValidateKvemoCropEntry(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidateSchemaVersion(
            root,
            VisualAiCropManifestEntry.CurrentSchemaVersion,
            "Kvemo crop manifest entry",
            messages);
        RequireConstStringProperty(root, "Kvemo crop manifest entry", "engine", "Kvemo", messages);
        RequireConstStringProperty(root, "Kvemo crop manifest entry", "coordinateSpace", "page", messages);
        RequireConstStringProperty(root, "Kvemo crop manifest entry", "coordinateOrigin", "top-left", messages);
        RequireConstStringProperty(root, "Kvemo crop manifest entry", "coordinateYAxisDirection", "down", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "documentId", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "detectionId", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "detectionKind", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "reviewKey", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "visualSimilarityKey", messages);
        RequireNonEmptyStringProperty(root, "Kvemo crop manifest entry", "sourceKind", messages);
        ValidateRequiredNullableStringProperty(root, "Kvemo crop manifest entry", "sourceWallComponentId", messages);
        ValidateRequiredNullableStringProperty(root, "Kvemo crop manifest entry", "sourceWallComponentKind", messages);
        ValidateOptionalProvenanceCountArrayProperty(root, "Kvemo crop manifest entry", "sourceKindCounts", messages);
        ValidateOptionalStringArrayProperty(root, "Kvemo crop manifest entry", "sourceWallComponentIds", messages);
        ValidateOptionalProvenanceCountArrayProperty(root, "Kvemo crop manifest entry", "sourceWallComponentKindCounts", messages);
        ValidateRequiredStringArrayProperty(root, "Kvemo crop manifest entry", "detectedTags", messages);
        ValidateRequiredStringArrayProperty(root, "Kvemo crop manifest entry", "nearbyText", messages);
        ValidateRequiredStringArrayProperty(root, "Kvemo crop manifest entry", "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(root, "Kvemo crop manifest entry", "evidence", messages);
        ValidateRequiredStringArrayProperty(root, "Kvemo crop manifest entry", "reviewReasons", messages);
        ValidateOptionalRatioProperty(root, "Kvemo crop manifest entry", "objectToCropAreaRatio", messages);
        ValidateOptionalRatioProperty(root, "Kvemo crop manifest entry", "cropToPageAreaRatio", messages);
        ValidateOptionalRatioProperty(root, "Kvemo crop manifest entry", "deterministicConfidence", messages);

        if (!root.TryGetProperty("imageFingerprint", out var fingerprint)
            || fingerprint.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Kvemo crop manifest entry requires an object imageFingerprint."));
            return;
        }

        RequireHash64Property(fingerprint, "Kvemo crop manifest entry imageFingerprint", "averageHash64", messages);
        RequireHash64Property(fingerprint, "Kvemo crop manifest entry imageFingerprint", "differenceHash64", messages);
        ValidateOptionalRatioProperty(fingerprint, "Kvemo crop manifest entry imageFingerprint", "inkRatio", messages);
        RequireNonEmptyStringProperty(fingerprint, "Kvemo crop manifest entry imageFingerprint", "densityBucket", messages);
        RequireNonEmptyStringProperty(fingerprint, "Kvemo crop manifest entry imageFingerprint", "aspectBucket", messages);
        RequireNonEmptyStringProperty(fingerprint, "Kvemo crop manifest entry imageFingerprint", "similarityKey", messages);

        var visualSimilarityKey = ReadStringProperty(root, "visualSimilarityKey");
        var fingerprintSimilarityKey = ReadStringProperty(fingerprint, "similarityKey");
        if (!string.IsNullOrWhiteSpace(visualSimilarityKey)
            && !string.IsNullOrWhiteSpace(fingerprintSimilarityKey)
            && !string.Equals(visualSimilarityKey, fingerprintSimilarityKey, StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Kvemo crop manifest entry visualSimilarityKey must match imageFingerprint.similarityKey."));
        }
    }

    private static void ValidatePlacementExport(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidateSchemaVersion(
            root,
            PlanPlacementExport.CurrentSchemaVersion,
            "placement export",
            messages);

        RequireNonEmptyStringProperty(root, "Placement export", "scanSchemaVersion", messages);

        if (TryReadObjectProperty(root, "document", "Placement export", messages, out var document))
        {
            ValidatePlacementDocument(document, messages);
        }

        if (TryReadObjectProperty(root, "coordinateSystem", "Placement export", messages, out var coordinateSystem))
        {
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "coordinateSpace", "OpenPlanTracePageCoordinates", messages);
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "origin", "TopLeft", messages);
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "xAxisDirection", "Right", messages);
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "yAxisDirection", "Down", messages);
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "coordinateOrder", "x,y", messages);
            RequireNonEmptyStringProperty(coordinateSystem, "Placement coordinateSystem", "geometryBasis", messages);
            RequireNonEmptyStringProperty(coordinateSystem, "Placement coordinateSystem", "boundsKind", messages);
            RequireConstStringProperty(coordinateSystem, "Placement coordinateSystem", "precision", "double", messages);
            RequireNonEmptyStringProperty(coordinateSystem, "Placement coordinateSystem", "realWorldUnit", messages);
            ValidateRequiredNullableNonNegativeNumberProperty(coordinateSystem, "Placement coordinateSystem", "millimetersPerDrawingUnit", messages);
            RequireNonEmptyStringProperty(coordinateSystem, "Placement coordinateSystem", "note", messages);

            var pageFrames = ReadArrayProperty(coordinateSystem, "pageFrames", "Placement coordinateSystem", messages);
            for (var index = 0; index < pageFrames.Length; index++)
            {
                ValidatePlacementPageFrame(pageFrames[index], $"Placement coordinateSystem.pageFrames[{index}]", messages);
            }
        }

        if (TryReadObjectProperty(root, "calibration", "Placement export", messages, out var calibration))
        {
            RequireNonEmptyStringProperty(calibration, "Placement calibration", "drawingUnit", messages);
            RequireNonEmptyStringProperty(calibration, "Placement calibration", "realWorldUnit", messages);
            RequireNonEmptyStringProperty(calibration, "Placement calibration", "metricCoordinateStatus", messages);
            TryReadNonNegativeIntegerProperty(calibration, "Placement calibration", "measurementCheckedCount", messages);
            TryReadNonNegativeIntegerProperty(calibration, "Placement calibration", "measurementOutlierCount", messages);
        }

        if (TryReadObjectProperty(root, "qualityGate", "Placement export", messages, out var qualityGate))
        {
            RequireNonEmptyStringProperty(qualityGate, "Placement qualityGate", "coordinateTrust", messages);
            RequireNonEmptyStringProperty(qualityGate, "Placement qualityGate", "metricTrust", messages);
            ValidateRequiredRatioProperty(qualityGate, "Placement qualityGate", "qualityConfidence", messages);
            ValidateBooleanProperty(qualityGate, "Placement qualityGate", "readyForCoordinatePlacement", messages);
            ValidateBooleanProperty(qualityGate, "Placement qualityGate", "readyForMetricPlacement", messages);
            ValidateBooleanProperty(qualityGate, "Placement qualityGate", "requiresReview", messages);
            ValidateBooleanProperty(qualityGate, "Placement qualityGate", "hasReliableCalibration", messages);
            ValidateRequiredStringArrayProperty(qualityGate, "Placement qualityGate", "evidence", messages);
        }

        var pages = ReadArrayProperty(root, "pages", "Placement export", messages);
        if (pages.Length == 0)
        {
            messages.Add(new ArtifactValidationMessage("warning", "Placement export contains no pages."));
        }

        for (var index = 0; index < pages.Length; index++)
        {
            ValidatePlacementPage(pages[index], $"Placement pages[{index}]", messages);
        }

        var walls = ValidatePlacementArray(root, "walls", "wall", ValidatePlacementWall, messages);
        var rooms = ValidatePlacementArray(root, "rooms", "room", ValidatePlacementRoom, messages);
        var openings = ValidatePlacementArray(root, "openings", "opening", ValidatePlacementOpening, messages);
        var objectAggregates = ValidatePlacementArray(root, "objectAggregates", "object aggregate", ValidatePlacementObjectAggregate, messages);
        var repairCandidates = ReadArrayProperty(root, "wallGraphRepairCandidates", "Placement export", messages);

        if (TryReadObjectProperty(root, "wallGraph", "Placement export", messages, out var wallGraph))
        {
            ValidatePlacementWallGraph(wallGraph, repairCandidates.Length, messages);
        }

        var routingLayer = default(JsonElement);
        var hasRoutingLayer = false;
        if (TryReadObjectProperty(root, "routingLayer", "Placement export", messages, out routingLayer))
        {
            hasRoutingLayer = true;
            ValidatePlacementRoutingLayer(routingLayer, messages);
        }

        var issues = ReadArrayProperty(root, "issues", "Placement export", messages);
        ValidatePlacementIssues(issues, messages);

        if (TryReadObjectProperty(root, "summary", "Placement export", messages, out var summary))
        {
            ValidatePlacementSummary(
                summary,
                pages,
                walls,
                rooms,
                openings,
                objectAggregates,
                hasRoutingLayer ? routingLayer : default,
                hasRoutingLayer,
                issues,
                messages);
        }
    }

    private static void ValidatePlacementDocument(
        JsonElement item,
        ICollection<ArtifactValidationMessage> messages)
    {
        const string Prefix = "Placement document";

        RequireNonEmptyStringProperty(item, Prefix, "id", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "sourceName", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "sourcePath", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "sourceFormat", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "loader", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "sourceKind", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "effectiveSourceKind", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "clipboardContentKind", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "fileExtension", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "contentType", messages);
        ValidateBooleanProperty(item, Prefix, "isDwgDerived", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "dwgConversion", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "dwgConverter", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "dwgIntermediateFormat", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "dwgIntermediateLoader", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "rasterAdapter", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "rasterExtractor", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "rasterExtractorVersion", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "rasterModelName", messages);
        ValidateRequiredNullableStringProperty(item, Prefix, "rasterModelVersion", messages);
        ValidateNullableEnumProperty<PlanSourceKind>(item, Prefix, "sourceKind", messages);
        ValidateNullableEnumProperty<PlanSourceKind>(item, Prefix, "effectiveSourceKind", messages);
        ValidateNullableEnumProperty<PlanSourceKind>(item, Prefix, "clipboardContentKind", messages);

        if (ValidateRequiredStringMapProperty(item, Prefix, "properties", messages, out var properties))
        {
            ValidatePlacementDocumentPropertyMirror(item, properties, "format", "sourceFormat", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "loader", "loader", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "sourceKind", "sourceKind", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "effectiveSourceKind", "effectiveSourceKind", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "clipboardContentKind", "clipboardContentKind", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "fileExtension", "fileExtension", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "contentType", "contentType", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "dwg.conversion", "dwgConversion", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "dwg.converter", "dwgConverter", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "dwg.intermediateFormat", "dwgIntermediateFormat", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "dwg.intermediateLoader", "dwgIntermediateLoader", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "raster.adapter", "rasterAdapter", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "raster.extractor", "rasterExtractor", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "raster.extractorVersion", "rasterExtractorVersion", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "raster.modelName", "rasterModelName", messages);
            ValidatePlacementDocumentPropertyMirror(item, properties, "raster.modelVersion", "rasterModelVersion", messages);
        }

        var sourceFormat = ReadStringProperty(item, "sourceFormat");
        var isDwgDerived = ReadBooleanProperty(item, "isDwgDerived");
        var dwgConversion = ReadStringProperty(item, "dwgConversion");
        if (isDwgDerived == false
            && string.Equals(sourceFormat, "dwg", StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement document isDwgDerived must be true when sourceFormat is 'dwg'."));
        }

        if (isDwgDerived == true
            && !string.Equals(sourceFormat, "dwg", StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(dwgConversion))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement document isDwgDerived requires sourceFormat 'dwg' or a non-empty dwgConversion."));
        }
    }

    private static void ValidatePlacementDocumentPropertyMirror(
        JsonElement document,
        JsonElement properties,
        string propertyKey,
        string documentPropertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        var documentValue = ReadStringProperty(document, documentPropertyName);
        var propertyValue = ReadStringProperty(properties, propertyKey);
        if (!string.IsNullOrWhiteSpace(documentValue)
            && !string.IsNullOrWhiteSpace(propertyValue)
            && !string.Equals(documentValue, propertyValue, StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Placement document {documentPropertyName} must match properties['{propertyKey}']."));
        }
    }

    private static void ValidatePlacementSummary(
        JsonElement summary,
        JsonElement[] pages,
        JsonElement[] walls,
        JsonElement[] rooms,
        JsonElement[] openings,
        JsonElement[] objectAggregates,
        JsonElement routingLayer,
        bool hasRoutingLayer,
        JsonElement[] issues,
        ICollection<ArtifactValidationMessage> messages)
    {
        const string Prefix = "Placement summary";

        var barriers = hasRoutingLayer ? ReadArrayProperty(routingLayer, "barriers", "Placement routingLayer", messages) : Array.Empty<JsonElement>();
        var passages = hasRoutingLayer ? ReadArrayProperty(routingLayer, "passages", "Placement routingLayer", messages) : Array.Empty<JsonElement>();
        var obstacles = hasRoutingLayer ? ReadArrayProperty(routingLayer, "obstacles", "Placement routingLayer", messages) : Array.Empty<JsonElement>();
        var roomUseHints = hasRoutingLayer ? ReadArrayProperty(routingLayer, "roomUseHints", "Placement routingLayer", messages) : Array.Empty<JsonElement>();
        var suppressedObjects = hasRoutingLayer ? ReadArrayProperty(routingLayer, "suppressedObjects", "Placement routingLayer", messages) : Array.Empty<JsonElement>();
        var suppressedObjectCandidateIds = hasRoutingLayer ? ReadStringArrayForDeep(routingLayer, "suppressedObjectCandidateIds").ToArray() : Array.Empty<string>();
        var routingItemCount = barriers.Length + passages.Length + obstacles.Length + roomUseHints.Length + suppressedObjects.Length;
        var structuralWalls = walls
            .Where(wall => ReadBooleanProperty(wall, "excludedFromStructuralTopology") != true)
            .ToArray();
        var placementReadyWallCount = PlacementReadyWallCount(walls);
        var placementOmittedWallCount = PlacementOmittedWallCount(walls);
        var wallTopologySpanCount = WallSpanCount(walls, "topologySpans");
        var wallSolidSpanCount = WallSpanCount(walls, "solidSpans");
        var wallPlacementOmissionCounts = WallPlacementOmissionCounts(walls);
        var reliabilityTrackedEntityCount = walls.Length + rooms.Length + openings.Length + objectAggregates.Length;
        var coordinateReadyEntityCount = ReliabilityCount(walls, "readyForCoordinatePlacement")
            + ReliabilityCount(rooms, "readyForCoordinatePlacement")
            + ReliabilityCount(openings, "readyForCoordinatePlacement")
            + ReliabilityCount(objectAggregates, "readyForCoordinatePlacement");
        var metricReadyEntityCount = ReliabilityCount(walls, "readyForMetricPlacement")
            + ReliabilityCount(rooms, "readyForMetricPlacement")
            + ReliabilityCount(openings, "readyForMetricPlacement")
            + ReliabilityCount(objectAggregates, "readyForMetricPlacement");
        var reviewRequiredEntityCount = ReliabilityCount(structuralWalls, "requiresReview")
            + ReliabilityCount(rooms, "requiresReview")
            + ReliabilityCount(openings, "requiresReview")
            + ReliabilityCount(objectAggregates, "requiresReview");

        AddExpectedIntegerMessage(Prefix, "pageCount", pages.Length, summary, messages);
        TryReadNonNegativeIntegerProperty(summary, Prefix, "mainFloorplanRegionCount", messages);
        AddExpectedIntegerMessage(Prefix, "wallCount", walls.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "structuralWallCount", structuralWalls.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "excludedWallCount", walls.Length - structuralWalls.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "placementReadyWallCount", placementReadyWallCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "placementOmittedWallCount", placementOmittedWallCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "wallTopologySpanCount", wallTopologySpanCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "wallSolidSpanCount", wallSolidSpanCount, summary, messages);
        ValidateExpectedIntegerMap(Prefix, "wallPlacementOmissionCounts", wallPlacementOmissionCounts, summary, messages);
        AddExpectedIntegerMessage(Prefix, "roomCount", rooms.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "openingCount", openings.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "anchoredOpeningCount", openings.Count(HasPlacementObject), summary, messages);
        AddExpectedIntegerMessage(Prefix, "unanchoredOpeningCount", openings.Count(opening => !HasPlacementObject(opening)), summary, messages);
        AddExpectedIntegerMessage(Prefix, "objectAggregateCount", objectAggregates.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "suppressedChildObjectCount", suppressedObjectCandidateIds.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingBarrierCount", barriers.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingPassageCount", passages.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingObstacleCount", obstacles.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingRoomUseHintCount", roomUseHints.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingSuppressedObjectCount", suppressedObjects.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "routingItemCount", routingItemCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "totalPlacementEntityCount", walls.Length + rooms.Length + openings.Length + objectAggregates.Length + routingItemCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "reliabilityTrackedEntityCount", reliabilityTrackedEntityCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "coordinateReadyEntityCount", coordinateReadyEntityCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "metricReadyEntityCount", metricReadyEntityCount, summary, messages);
        AddExpectedIntegerMessage(Prefix, "reviewRequiredEntityCount", reviewRequiredEntityCount, summary, messages);
        if (metricReadyEntityCount > coordinateReadyEntityCount)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{Prefix} metricReadyEntityCount cannot exceed coordinateReadyEntityCount."));
        }

        ValidateRequiredRatioProperty(summary, Prefix, "coordinateReadyRatio", messages);
        ValidateRequiredRatioProperty(summary, Prefix, "metricReadyRatio", messages);
        AddExpectedIntegerMessage(Prefix, "issueCount", issues.Length, summary, messages);
        AddExpectedIntegerMessage(Prefix, "infoIssueCount", issues.Count(issue => ReadStringProperty(issue, "severity") == DiagnosticSeverity.Info.ToString()), summary, messages);
        AddExpectedIntegerMessage(Prefix, "warningIssueCount", issues.Count(issue => ReadStringProperty(issue, "severity") == DiagnosticSeverity.Warning.ToString()), summary, messages);
        AddExpectedIntegerMessage(Prefix, "errorIssueCount", issues.Count(issue => ReadStringProperty(issue, "severity") == DiagnosticSeverity.Error.ToString()), summary, messages);
        TryReadNonNegativeIntegerProperty(summary, Prefix, "sourcePrimitiveReferenceCount", messages);
        TryReadNonNegativeIntegerProperty(summary, Prefix, "uniqueSourcePrimitiveReferenceCount", messages);
        ValidateRequiredStringArrayProperty(summary, Prefix, "evidence", messages);

        if (TryReadObjectProperty(summary, "importReadiness", Prefix, messages, out var importReadiness))
        {
            ValidatePlacementImportReadiness(importReadiness, messages);
        }

        var pageSummaries = ReadArrayProperty(summary, "pageSummaries", Prefix, messages);
        if (pageSummaries.Length != pages.Length)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{Prefix} pageSummaries must contain one entry per page."));
        }

        for (var index = 0; index < pageSummaries.Length; index++)
        {
            ValidatePlacementPageSummary(
                pageSummaries[index],
                $"Placement summary pageSummaries[{index}]",
                walls,
                rooms,
                openings,
                objectAggregates,
                barriers,
                passages,
                obstacles,
                roomUseHints,
                suppressedObjects,
                issues,
                messages);
        }
    }

    private static void ValidatePlacementImportReadiness(
        JsonElement importReadiness,
        ICollection<ArtifactValidationMessage> messages)
    {
        const string Prefix = "Placement summary importReadiness";

        RequireNonEmptyStringProperty(importReadiness, Prefix, "grade", messages);
        ValidateRequiredRatioProperty(importReadiness, Prefix, "score", messages);
        ValidateBooleanProperty(importReadiness, Prefix, "readyForGeometryImport", messages);
        ValidateBooleanProperty(importReadiness, Prefix, "readyForMetricImport", messages);
        ValidateBooleanProperty(importReadiness, Prefix, "readyForRoutingImport", messages);
        ValidateBooleanProperty(importReadiness, Prefix, "requiresReview", messages);
        ValidateRequiredStringArrayProperty(importReadiness, Prefix, "blockingIssueCodes", messages);
        ValidateRequiredStringArrayProperty(importReadiness, Prefix, "reviewIssueCodes", messages);
        ValidateRequiredStringArrayProperty(importReadiness, Prefix, "recommendedActions", messages);
        ValidateRequiredStringArrayProperty(importReadiness, Prefix, "evidence", messages);

        var readyForGeometry = ReadBooleanProperty(importReadiness, "readyForGeometryImport");
        var readyForMetric = ReadBooleanProperty(importReadiness, "readyForMetricImport");
        var readyForRouting = ReadBooleanProperty(importReadiness, "readyForRoutingImport");
        if (readyForMetric == true && readyForGeometry != true)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement summary importReadiness readyForMetricImport cannot be true unless readyForGeometryImport is true."));
        }

        if (readyForRouting == true && readyForGeometry != true)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement summary importReadiness readyForRoutingImport cannot be true unless readyForGeometryImport is true."));
        }
    }

    private static void ValidatePlacementPageSummary(
        JsonElement summary,
        string prefix,
        JsonElement[] walls,
        JsonElement[] rooms,
        JsonElement[] openings,
        JsonElement[] objectAggregates,
        JsonElement[] barriers,
        JsonElement[] passages,
        JsonElement[] obstacles,
        JsonElement[] roomUseHints,
        JsonElement[] suppressedObjects,
        JsonElement[] issues,
        ICollection<ArtifactValidationMessage> messages)
    {
        var pageNumber = TryReadPositiveIntegerProperty(summary, prefix, "pageNumber", messages);
        ValidatePlacementRectProperty(summary, prefix, "pageBounds", messages);
        ValidateRequiredNullableRectProperty(summary, prefix, "mainFloorplanBounds", messages);
        ValidateRequiredNullableRectProperty(summary, prefix, "detectionBounds", messages);
        ValidateRequiredNullableRectProperty(summary, prefix, "detectionBoundsMillimeters", messages);

        if (pageNumber is null)
        {
            return;
        }

        var pageWalls = walls.Where(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value).ToArray();
        var pageStructuralWalls = pageWalls
            .Where(wall => ReadBooleanProperty(wall, "excludedFromStructuralTopology") != true)
            .ToArray();
        var placementReadyWallCount = PlacementReadyWallCount(pageWalls);
        var placementOmittedWallCount = PlacementOmittedWallCount(pageWalls);
        var wallTopologySpanCount = WallSpanCount(pageWalls, "topologySpans");
        var wallSolidSpanCount = WallSpanCount(pageWalls, "solidSpans");
        var wallPlacementOmissionCounts = WallPlacementOmissionCounts(pageWalls);
        var pageRooms = rooms.Where(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value).ToArray();
        var pageOpenings = openings.Where(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value).ToArray();
        var pageObjectAggregates = objectAggregates.Where(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value).ToArray();
        var routingItemCount = barriers.Count(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value)
            + passages.Count(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value)
            + obstacles.Count(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value)
            + roomUseHints.Count(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value)
            + suppressedObjects.Count(item => ReadInt32Property(item, "pageNumber") == pageNumber.Value);

        AddExpectedIntegerMessage(prefix, "wallCount", pageWalls.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "structuralWallCount", pageStructuralWalls.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "excludedWallCount", pageWalls.Length - pageStructuralWalls.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "placementReadyWallCount", placementReadyWallCount, summary, messages);
        AddExpectedIntegerMessage(prefix, "placementOmittedWallCount", placementOmittedWallCount, summary, messages);
        AddExpectedIntegerMessage(prefix, "wallTopologySpanCount", wallTopologySpanCount, summary, messages);
        AddExpectedIntegerMessage(prefix, "wallSolidSpanCount", wallSolidSpanCount, summary, messages);
        ValidateExpectedIntegerMap(prefix, "wallPlacementOmissionCounts", wallPlacementOmissionCounts, summary, messages);
        AddExpectedIntegerMessage(prefix, "roomCount", pageRooms.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "openingCount", pageOpenings.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "anchoredOpeningCount", pageOpenings.Count(HasPlacementObject), summary, messages);
        AddExpectedIntegerMessage(prefix, "unanchoredOpeningCount", pageOpenings.Count(opening => !HasPlacementObject(opening)), summary, messages);
        AddExpectedIntegerMessage(prefix, "objectAggregateCount", pageObjectAggregates.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "routingItemCount", routingItemCount, summary, messages);
        AddExpectedIntegerMessage(prefix, "reliabilityTrackedEntityCount", pageWalls.Length + pageRooms.Length + pageOpenings.Length + pageObjectAggregates.Length, summary, messages);
        AddExpectedIntegerMessage(prefix, "coordinateReadyEntityCount", ReliabilityCount(pageWalls, "readyForCoordinatePlacement") + ReliabilityCount(pageRooms, "readyForCoordinatePlacement") + ReliabilityCount(pageOpenings, "readyForCoordinatePlacement") + ReliabilityCount(pageObjectAggregates, "readyForCoordinatePlacement"), summary, messages);
        AddExpectedIntegerMessage(prefix, "metricReadyEntityCount", ReliabilityCount(pageWalls, "readyForMetricPlacement") + ReliabilityCount(pageRooms, "readyForMetricPlacement") + ReliabilityCount(pageOpenings, "readyForMetricPlacement") + ReliabilityCount(pageObjectAggregates, "readyForMetricPlacement"), summary, messages);
        AddExpectedIntegerMessage(prefix, "reviewRequiredEntityCount", ReliabilityCount(pageStructuralWalls, "requiresReview") + ReliabilityCount(pageRooms, "requiresReview") + ReliabilityCount(pageOpenings, "requiresReview") + ReliabilityCount(pageObjectAggregates, "requiresReview"), summary, messages);
        AddExpectedIntegerMessage(prefix, "issueCount", issues.Count(issue => ReadInt32Property(issue, "pageNumber") == pageNumber.Value), summary, messages);
    }

    private static int ReliabilityCount(JsonElement[] items, string propertyName) =>
        items.Count(item =>
            item.TryGetProperty("reliability", out var reliability)
            && reliability.ValueKind == JsonValueKind.Object
            && ReadBooleanProperty(reliability, propertyName) == true);

    private static int PlacementReadyWallCount(JsonElement[] walls) =>
        walls.Count(wall => !HasPlacementOmission(wall)
            && wall.TryGetProperty("reliability", out var reliability)
            && reliability.ValueKind == JsonValueKind.Object
            && ReadBooleanProperty(reliability, "readyForCoordinatePlacement") == true);

    private static int PlacementOmittedWallCount(JsonElement[] walls) =>
        walls.Count(HasPlacementOmission);

    private static int WallSpanCount(JsonElement[] walls, string propertyName) =>
        walls.Sum(wall =>
            wall.TryGetProperty(propertyName, out var spans) && spans.ValueKind == JsonValueKind.Array
                ? spans.GetArrayLength()
                : 0);

    private static IReadOnlyDictionary<string, int> WallPlacementOmissionCounts(JsonElement[] walls) =>
        walls
            .Select(PlacementOmissionCode)
            .OfType<string>()
            .GroupBy(code => code, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    private static bool HasPlacementOmission(JsonElement wall) =>
        wall.TryGetProperty("placementOmission", out var omission)
        && omission.ValueKind == JsonValueKind.Object;

    private static string? PlacementOmissionCode(JsonElement wall)
    {
        if (!HasPlacementOmission(wall))
        {
            return null;
        }

        var omission = wall.GetProperty("placementOmission");
        var code = ReadStringProperty(omission, "code");
        return string.IsNullOrWhiteSpace(code) ? "(missing)" : code;
    }

    private static bool HasPlacementObject(JsonElement opening) =>
        opening.TryGetProperty("placement", out var placement)
        && placement.ValueKind == JsonValueKind.Object;

    private static int? ReadInt32Property(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number))
        {
            return null;
        }

        return number;
    }

    private static JsonElement[] ValidatePlacementArray(
        JsonElement root,
        string propertyName,
        string itemName,
        Action<JsonElement, string, ICollection<ArtifactValidationMessage>> validateItem,
        ICollection<ArtifactValidationMessage> messages)
    {
        var items = ReadArrayProperty(root, propertyName, "Placement export", messages);
        for (var index = 0; index < items.Length; index++)
        {
            validateItem(items[index], $"Placement {itemName}s[{index}]", messages);
        }

        return items;
    }

    private static void ValidatePlacementIssues(
        JsonElement[] issues,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < issues.Length; index++)
        {
            var item = issues[index];
            var prefix = $"Placement issues[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                continue;
            }

            RequireNonEmptyStringProperty(item, prefix, "code", messages);
            var severity = ReadStringProperty(item, "severity");
            if (severity is null || !Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} has an unknown severity '{severity ?? "(missing)"}'."));
            }

            RequireNonEmptyStringProperty(item, prefix, "message", messages);
            RequireNonEmptyStringProperty(item, prefix, "recommendedAction", messages);
            ValidateRequiredNullableStringProperty(item, prefix, "itemId", messages);
            ValidateRequiredNullableRectProperty(item, prefix, "bounds", messages);
            ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
            ValidateRequiredStringMapProperty(item, prefix, "properties", messages, out _);

            var pageNumber = default(int?);
            if (!item.TryGetProperty("pageNumber", out var pageNumberElement))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires integer or null pageNumber."));
            }
            else if (pageNumberElement.ValueKind != JsonValueKind.Null)
            {
                if (pageNumberElement.ValueKind != JsonValueKind.Number
                    || !pageNumberElement.TryGetInt32(out var number)
                    || number < 1)
                {
                    messages.Add(new ArtifactValidationMessage("error", $"{prefix} pageNumber must be a positive integer or null."));
                }
                else
                {
                    pageNumber = number;
                }
            }

            var pageNumbers = ReadArrayProperty(item, "pageNumbers", prefix, messages);
            var parsedPageNumbers = new List<int>();
            for (var pageIndex = 0; pageIndex < pageNumbers.Length; pageIndex++)
            {
                var pageItem = pageNumbers[pageIndex];
                if (pageItem.ValueKind != JsonValueKind.Number
                    || !pageItem.TryGetInt32(out var page)
                    || page < 1)
                {
                    messages.Add(new ArtifactValidationMessage("error", $"{prefix} pageNumbers[{pageIndex}] must be a positive integer."));
                    continue;
                }

                parsedPageNumbers.Add(page);
            }

            if (pageNumber is int pageNumberValue
                && parsedPageNumbers.Count > 0
                && !parsedPageNumbers.Contains(pageNumberValue))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} pageNumbers must include pageNumber when pageNumber is set."));
            }

            if (!item.TryGetProperty("confidence", out var confidence))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires ratio or null confidence."));
            }
            else if (confidence.ValueKind != JsonValueKind.Null)
            {
                if (confidence.ValueKind != JsonValueKind.Number
                    || !confidence.TryGetDouble(out var value)
                    || !double.IsFinite(value)
                    || value < 0
                    || value > 1)
                {
                    messages.Add(new ArtifactValidationMessage("error", $"{prefix} confidence must be a ratio between 0 and 1 or null."));
                }
            }
        }
    }

    private static void ValidatePlacementPage(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        ValidatePositiveNumberProperty(item, prefix, "width", messages);
        ValidatePositiveNumberProperty(item, prefix, "height", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
    }

    private static void ValidatePlacementPageFrame(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        ValidatePositiveNumberProperty(item, prefix, "width", messages);
        ValidatePositiveNumberProperty(item, prefix, "height", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNumberArrayProperty(item, prefix, "pageToNormalizedTransform", expectedCount: 6, messages);
        ValidateRequiredNumberArrayProperty(item, prefix, "normalizedToPageTransform", expectedCount: 6, messages);
    }

    private static void ValidatePlacementWall(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementEntityShell(item, prefix, messages))
        {
            return;
        }

        ValidatePlacementLineProperty(item, prefix, "centerLine", messages);
        ValidateOptionalPlacementWallTopologySpans(item, prefix, messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingLength", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "thicknessDrawingUnits", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidatePlacementReliabilityProperty(item, prefix, messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidateOptionalPlacementWallTopologySpans(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!item.TryGetProperty("topologySpans", out var spans))
        {
            return;
        }

        if (spans.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} topologySpans must be an array."));
            return;
        }

        var index = 0;
        foreach (var span in spans.EnumerateArray())
        {
            ValidatePlacementWallTopologySpan(span, $"{prefix}.topologySpans[{index}]", messages);
            index++;
        }
    }

    private static void ValidatePlacementWallTopologySpan(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        RequireNonEmptyStringProperty(item, prefix, "wallGraphEdgeId", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        RequireNonEmptyStringProperty(item, prefix, "wallId", messages);
        RequireNonEmptyStringProperty(item, prefix, "fromNodeId", messages);
        RequireNonEmptyStringProperty(item, prefix, "toNodeId", messages);
        ValidatePlacementLineProperty(item, prefix, "centerLine", messages);
        ValidateRequiredNullableLineProperty(item, prefix, "centerLineMillimeters", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingLength", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "lengthMeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "thicknessDrawingUnits", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "thicknessMillimeters", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementWallGraph(
        JsonElement item,
        int repairCandidateCount,
        ICollection<ArtifactValidationMessage> messages)
    {
        const string Prefix = "Placement wallGraph";
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{Prefix} must be an object."));
            return;
        }

        var nodes = ReadArrayProperty(item, "nodes", Prefix, messages);
        var edges = ReadArrayProperty(item, "edges", Prefix, messages);
        var components = ReadArrayProperty(item, "components", Prefix, messages);
        for (var index = 0; index < nodes.Length; index++)
        {
            ValidatePlacementWallGraphNode(nodes[index], $"{Prefix}.nodes[{index}]", messages);
        }

        for (var index = 0; index < edges.Length; index++)
        {
            ValidatePlacementWallGraphEdge(edges[index], $"{Prefix}.edges[{index}]", messages);
        }

        for (var index = 0; index < components.Length; index++)
        {
            ValidatePlacementWallGraphComponent(components[index], $"{Prefix}.components[{index}]", messages);
        }

        ValidateRequiredStringArrayProperty(item, Prefix, "repairCandidateIds", messages);
        ValidateRequiredStringArrayProperty(item, Prefix, "evidence", messages);

        if (TryReadObjectProperty(item, "summary", Prefix, messages, out var summary))
        {
            ValidatePlacementWallGraphSummary(summary, nodes.Length, edges.Length, components.Length, repairCandidateCount, messages);
        }
    }

    private static void ValidatePlacementWallGraphSummary(
        JsonElement item,
        int nodeCount,
        int edgeCount,
        int componentCount,
        int repairCandidateCount,
        ICollection<ArtifactValidationMessage> messages)
    {
        const string Prefix = "Placement wallGraph.summary";
        var reportedNodeCount = TryReadNonNegativeIntegerProperty(item, Prefix, "nodeCount", messages);
        var reportedEdgeCount = TryReadNonNegativeIntegerProperty(item, Prefix, "edgeCount", messages);
        var reportedComponentCount = TryReadNonNegativeIntegerProperty(item, Prefix, "componentCount", messages);
        var reportedRepairCandidateCount = TryReadNonNegativeIntegerProperty(item, Prefix, "repairCandidateCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "mainStructuralComponentCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "secondaryStructuralComponentCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "objectLikeComponentCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "isolatedFragmentComponentCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "structuralEdgeCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "excludedEdgeCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "highSeverityRepairCandidateCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "reviewRepairCandidateCount", messages);
        TryReadNonNegativeIntegerProperty(item, Prefix, "blockingRepairCandidateCount", messages);

        if (reportedNodeCount is not null && reportedNodeCount != nodeCount)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{Prefix} nodeCount must equal wallGraph.nodes length."));
        }

        if (reportedEdgeCount is not null && reportedEdgeCount != edgeCount)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{Prefix} edgeCount must equal wallGraph.edges length."));
        }

        if (reportedComponentCount is not null && reportedComponentCount != componentCount)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{Prefix} componentCount must equal wallGraph.components length."));
        }

        if (reportedRepairCandidateCount is not null && reportedRepairCandidateCount != repairCandidateCount)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{Prefix} repairCandidateCount must equal wallGraphRepairCandidates length."));
        }
    }

    private static void ValidatePlacementWallGraphNode(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        ValidatePlacementPointProperty(item, prefix, "position", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "positionMillimeters", messages);
        RequireNonEmptyStringProperty(item, prefix, "kind", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "degree", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "directions", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementWallGraphEdge(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        RequireNonEmptyStringProperty(item, prefix, "fromNodeId", messages);
        RequireNonEmptyStringProperty(item, prefix, "toNodeId", messages);
        RequireNonEmptyStringProperty(item, prefix, "wallId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "wallComponentId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "wallComponentKind", messages);
        ValidateBooleanProperty(item, prefix, "excludedFromStructuralTopology", messages);
        ValidateRequiredNullableLineProperty(item, prefix, "centerLine", messages);
        ValidateRequiredNullableLineProperty(item, prefix, "centerLineMillimeters", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingLength", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "lengthMeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "thicknessDrawingUnits", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "thicknessMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementWallGraphComponent(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        RequireNonEmptyStringProperty(item, prefix, "kind", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "wallIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "nodeIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "edgeIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "wallCount", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "nodeCount", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "edgeCount", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingLength", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "lengthMeters", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateBooleanProperty(item, prefix, "excludedFromStructuralTopology", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoom(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementEntityShell(item, prefix, messages))
        {
            return;
        }

        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidatePlacementPointProperty(item, prefix, "center", messages);
        if (!item.TryGetProperty("boundary", out var boundary) || boundary.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array boundary."));
        }

        ValidateRequiredStringArrayProperty(item, prefix, "wallIds", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingArea", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidatePlacementReliabilityProperty(item, prefix, messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementOpening(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementEntityShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "type", messages);
        RequireNonEmptyStringProperty(item, prefix, "operation", messages);
        RequireNonEmptyStringProperty(item, prefix, "orientation", messages);
        ValidatePlacementLineProperty(item, prefix, "centerLine", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingWidth", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidatePlacementReliabilityProperty(item, prefix, messages);
        ValidateRequiredStringArrayProperty(item, prefix, "hostWallIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "connectedRoomIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "connectedRoomLabels", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "roomAdjacencyIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
        ValidatePlacementOpeningRoomConnections(item, prefix, messages);

        var placementStatus = ReadStringProperty(item, "placementStatus");
        if (placementStatus is not "Anchored" and not "Unanchored")
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} placementStatus must be Anchored or Unanchored."));
        }

        var hasPlacement = item.TryGetProperty("placement", out var placement)
            && placement.ValueKind is JsonValueKind.Object;
        if (placementStatus == "Anchored" && !hasPlacement)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} placementStatus is Anchored but placement is missing."));
        }

        if (placementStatus == "Unanchored" && hasPlacement)
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{prefix} placementStatus is Unanchored but placement is present."));
        }

        if (hasPlacement)
        {
            ValidatePlacementOpeningPlacement(placement, $"{prefix}.placement", messages);
        }
    }

    private static void ValidatePlacementOpeningRoomConnections(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!item.TryGetProperty("connectedRoomLinks", out var links) || links.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} requires an array connectedRoomLinks."));
            return;
        }

        var index = 0;
        foreach (var link in links.EnumerateArray())
        {
            var linkPrefix = $"{prefix}.connectedRoomLinks[{index}]";
            if (link.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{linkPrefix} must be an object."));
                index++;
                continue;
            }

            RequireNonEmptyStringProperty(link, linkPrefix, "roomId", messages);
            ValidateRequiredNullableStringProperty(link, linkPrefix, "roomLabel", messages);
            RequireNonEmptyStringProperty(link, linkPrefix, "roomUseKind", messages);
            ValidateRequiredStringArrayProperty(link, linkPrefix, "roomAdjacencyIds", messages);
            RequireNonEmptyStringProperty(link, linkPrefix, "side", messages);
            var side = ReadStringProperty(link, "side");
            if (side is not null
                && side is not "Unknown" and not "PositiveNormalSide" and not "NegativeNormalSide" and not "OnOpeningLine")
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{linkPrefix} side must be Unknown, PositiveNormalSide, NegativeNormalSide, or OnOpeningLine."));
            }

            ValidateRequiredNullablePointProperty(link, linkPrefix, "roomSidePoint", messages);
            ValidateRequiredNullablePointProperty(link, linkPrefix, "nearestBoundaryPoint", messages);
            ValidateNumberProperty(link, linkPrefix, "signedDistanceFromOpening", messages);
            ValidateNonNegativeNumberProperty(link, linkPrefix, "distanceToOpening", messages);
            ValidateBooleanProperty(link, linkPrefix, "sharesHostWall", messages);
            ValidateRequiredRatioProperty(link, linkPrefix, "confidence", messages);
            ValidateRequiredStringArrayProperty(link, linkPrefix, "evidence", messages);
            index++;
        }
    }

    private static void ValidatePlacementObjectAggregate(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementEntityShell(item, prefix, messages))
        {
            return;
        }

        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidatePlacementPointProperty(item, prefix, "center", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "centerMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        RequireNonEmptyStringProperty(item, prefix, "category", messages);
        RequireNonEmptyStringProperty(item, prefix, "kind", messages);
        RequireNonEmptyStringProperty(item, prefix, "routingInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "structuralInfluence", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "childObjectCount", messages);
        ValidateBooleanProperty(item, prefix, "suppressChildObjectsForRouting", messages);
        ValidateBooleanProperty(item, prefix, "requiresReview", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidatePlacementReliabilityProperty(item, prefix, messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoutingLayer(
        JsonElement item,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidatePlacementRoutingArray(item, "barriers", "barrier", ValidatePlacementRoutingBarrier, messages);
        ValidatePlacementRoutingArray(item, "passages", "passage", ValidatePlacementRoutingPassage, messages);
        ValidatePlacementRoutingArray(item, "obstacles", "obstacle", ValidatePlacementRoutingObstacle, messages);
        ValidatePlacementRoutingArray(item, "roomUseHints", "room use hint", ValidatePlacementRoutingRoomUseHint, messages);
        ValidatePlacementRoutingArray(item, "suppressedObjects", "suppressed object", ValidatePlacementRoutingSuppressedObject, messages);
        ValidatePlacementRoutingArray(item, "ignoredObjects", "ignored object", ValidatePlacementRoutingIgnoredObject, messages);
        ValidateRequiredStringArrayProperty(item, "Placement routingLayer", "suppressedObjectCandidateIds", messages);
        ValidateRequiredStringArrayProperty(item, "Placement routingLayer", "ignoredObjectCandidateIds", messages);
        ValidateRequiredStringArrayProperty(item, "Placement routingLayer", "evidence", messages);
    }

    private static void ValidatePlacementRoutingArray(
        JsonElement root,
        string propertyName,
        string itemName,
        Action<JsonElement, string, ICollection<ArtifactValidationMessage>> validateItem,
        ICollection<ArtifactValidationMessage> messages)
    {
        var items = ReadArrayProperty(root, propertyName, "Placement routingLayer", messages);
        for (var index = 0; index < items.Length; index++)
        {
            validateItem(items[index], $"Placement routingLayer.{propertyName}[{index}] {itemName}", messages);
        }
    }

    private static void ValidatePlacementRoutingBarrier(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "sourceId", messages);
        RequireNonEmptyStringProperty(item, prefix, "sourceKind", messages);
        ValidatePlacementLineProperty(item, prefix, "centerLine", messages);
        ValidateRequiredNullableLineProperty(item, prefix, "centerLineMillimeters", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "thickness", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingLength", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "lengthMeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "thicknessMillimeters", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "measurementScaleGroupId", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "wallComponentId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "wallComponentKind", messages);
        ValidateBooleanProperty(item, prefix, "excludedFromStructuralTopology", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoutingPassage(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "sourceId", messages);
        RequireNonEmptyStringProperty(item, prefix, "sourceKind", messages);
        RequireNonEmptyStringProperty(item, prefix, "type", messages);
        RequireNonEmptyStringProperty(item, prefix, "operation", messages);
        RequireNonEmptyStringProperty(item, prefix, "orientation", messages);
        ValidatePlacementLineProperty(item, prefix, "centerLine", messages);
        ValidateRequiredNullableLineProperty(item, prefix, "centerLineMillimeters", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidateNonNegativeNumberProperty(item, prefix, "drawingWidth", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "widthMillimeters", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "measurementScaleGroupId", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "hostWallIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "connectedRoomIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "connectedRoomLabels", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "roomAdjacencyIds", messages);
        ValidatePlacementOpeningRoomConnections(item, prefix, messages);
        ValidateRequiredPlacementOrNullProperty(item, prefix, "placement", messages);
        ValidateRoutingPassagePlacementReadiness(item, prefix, messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidateRoutingPassagePlacementReadiness(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var placementStatus = ReadStringProperty(item, "placementStatus");
        if (placementStatus is not "Anchored" and not "Unanchored")
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} placementStatus must be Anchored or Unanchored."));
        }

        ValidateBooleanProperty(item, prefix, "readyForCoordinatePlacement", messages);
        ValidateBooleanProperty(item, prefix, "requiresReview", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "reviewReasons", messages);

        var hasPlacement = item.TryGetProperty("placement", out var placement)
            && placement.ValueKind == JsonValueKind.Object;
        var readyForCoordinatePlacement = ReadBooleanProperty(item, "readyForCoordinatePlacement");
        var requiresReview = ReadBooleanProperty(item, "requiresReview");
        var reviewReasons = ReadStringArrayForDeep(item, "reviewReasons").ToArray();

        if (placementStatus == "Anchored" && !hasPlacement)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} placementStatus is Anchored but placement is missing."));
        }

        if (placementStatus == "Unanchored" && hasPlacement)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} placementStatus is Unanchored but placement is present."));
        }

        if (readyForCoordinatePlacement == true && !hasPlacement)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} readyForCoordinatePlacement cannot be true when placement is missing."));
        }

        if (readyForCoordinatePlacement == false && requiresReview == false)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} requiresReview must be true when readyForCoordinatePlacement is false."));
        }

        if (requiresReview == true && reviewReasons.Length == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} reviewReasons must explain why requiresReview is true."));
        }

        if (requiresReview == false && reviewReasons.Length > 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{prefix} reviewReasons should be empty when requiresReview is false."));
        }
    }

    private static void ValidatePlacementRoutingObstacle(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "sourceId", messages);
        RequireNonEmptyStringProperty(item, prefix, "sourceKind", messages);
        RequireNonEmptyStringProperty(item, prefix, "obstacleKind", messages);
        RequireNonEmptyStringProperty(item, prefix, "routingInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "structuralInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "category", messages);
        RequireNonEmptyStringProperty(item, prefix, "objectKind", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidatePlacementPointProperty(item, prefix, "center", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "centerMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "label", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomLabel", messages);
        ValidateBooleanProperty(item, prefix, "suppressesChildObjects", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "childObjectIds", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoutingRoomUseHint(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "sourceId", messages);
        RequireNonEmptyStringProperty(item, prefix, "sourceKind", messages);
        RequireNonEmptyStringProperty(item, prefix, "roomUseKind", messages);
        ValidatePlacementRectProperty(item, prefix, "bounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "boundsMillimeters", messages);
        ValidatePlacementPointProperty(item, prefix, "center", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "centerMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomLabel", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoutingSuppressedObject(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "objectCandidateId", messages);
        RequireNonEmptyStringProperty(item, prefix, "suppressedByAggregateId", messages);
        RequireNonEmptyStringProperty(item, prefix, "reason", messages);
        RequireNonEmptyStringProperty(item, prefix, "action", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "replacementRoutingObstacleId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomUseHintId", messages);
        RequireNonEmptyStringProperty(item, prefix, "aggregateRoutingInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "aggregateStructuralInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "candidateCategory", messages);
        RequireNonEmptyStringProperty(item, prefix, "candidateKind", messages);
        ValidatePlacementRectProperty(item, prefix, "candidateBounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "candidateBoundsMillimeters", messages);
        ValidatePlacementPointProperty(item, prefix, "candidateCenter", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "candidateCenterMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "candidateLabel", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomLabel", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static void ValidatePlacementRoutingIgnoredObject(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!ValidatePlacementRoutingItemShell(item, prefix, messages))
        {
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "objectCandidateId", messages);
        RequireNonEmptyStringProperty(item, prefix, "reason", messages);
        RequireNonEmptyStringProperty(item, prefix, "routingInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "structuralInfluence", messages);
        RequireNonEmptyStringProperty(item, prefix, "candidateCategory", messages);
        RequireNonEmptyStringProperty(item, prefix, "candidateKind", messages);
        RequireNonEmptyStringProperty(item, prefix, "candidateSourceKind", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "sourceWallComponentId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "sourceWallComponentKind", messages);
        ValidatePlacementRectProperty(item, prefix, "candidateBounds", messages);
        ValidateRequiredNullableRectProperty(item, prefix, "candidateBoundsMillimeters", messages);
        ValidatePlacementPointProperty(item, prefix, "candidateCenter", messages);
        ValidateRequiredNullablePointProperty(item, prefix, "candidateCenterMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(item, prefix, "millimetersPerDrawingUnit", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "candidateLabel", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomLabel", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "suppressedObjectId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "suppressedByAggregateId", messages);
        ValidateRequiredNullableStringProperty(item, prefix, "roomUseHintId", messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);
    }

    private static bool ValidatePlacementRoutingItemShell(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return false;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        return true;
    }

    private static void ValidateRequiredPlacementOrNullProperty(
        JsonElement root,
        string prefix,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var placement))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} requires object or null {propertyName}."));
            return;
        }

        if (placement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (placement.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {propertyName} must be an object or null."));
            return;
        }

        ValidatePlacementOpeningPlacement(placement, $"{prefix}.{propertyName}", messages);
    }

    private static void ValidatePlacementOpeningPlacement(
        JsonElement placement,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidateRequiredNullableStringProperty(placement, prefix, "hostWallId", messages);
        ValidateRequiredStringArrayProperty(placement, prefix, "anchorWallIds", messages);
        ValidatePlacementLineProperty(placement, prefix, "referenceLine", messages);
        ValidatePlacementPointProperty(placement, prefix, "startPoint", messages);
        ValidatePlacementPointProperty(placement, prefix, "endPoint", messages);
        ValidateNumberProperty(placement, prefix, "startOffsetDrawingUnits", messages);
        ValidateNumberProperty(placement, prefix, "endOffsetDrawingUnits", messages);
        ValidateNumberProperty(placement, prefix, "centerOffsetDrawingUnits", messages);
        ValidateNonNegativeNumberProperty(placement, prefix, "lengthDrawingUnits", messages);
        ValidateRequiredNullableNumberProperty(placement, prefix, "startOffsetMillimeters", messages);
        ValidateRequiredNullableNumberProperty(placement, prefix, "endOffsetMillimeters", messages);
        ValidateRequiredNullableNumberProperty(placement, prefix, "centerOffsetMillimeters", messages);
        ValidateRequiredNullableNonNegativeNumberProperty(placement, prefix, "lengthMillimeters", messages);
        ValidateNumberProperty(placement, prefix, "hostWallStartParameter", messages);
        ValidateNumberProperty(placement, prefix, "hostWallEndParameter", messages);
        ValidateNumberProperty(placement, prefix, "hostWallCenterParameter", messages);
        ValidatePlacementPointProperty(placement, prefix, "alongVector", messages);
        ValidatePlacementPointProperty(placement, prefix, "normalVector", messages);
        ValidateNonNegativeNumberProperty(placement, prefix, "crossWallOffsetDrawingUnits", messages);
        ValidateRequiredRatioProperty(placement, prefix, "confidence", messages);
        ValidateRequiredStringArrayProperty(placement, prefix, "evidence", messages);
    }

    private static bool ValidatePlacementEntityShell(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return false;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        ValidatePositiveIntegerProperty(item, prefix, "pageNumber", messages);
        return true;
    }

    private static void ValidatePlacementReliabilityProperty(
        JsonElement root,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!TryReadObjectProperty(root, "reliability", prefix, messages, out var reliability))
        {
            return;
        }

        ValidateBooleanProperty(reliability, $"{prefix}.reliability", "readyForCoordinatePlacement", messages);
        ValidateBooleanProperty(reliability, $"{prefix}.reliability", "readyForMetricPlacement", messages);
        ValidateBooleanProperty(reliability, $"{prefix}.reliability", "requiresReview", messages);
        ValidateRequiredRatioProperty(reliability, $"{prefix}.reliability", "confidence", messages);
        ValidateRequiredStringArrayProperty(reliability, $"{prefix}.reliability", "reasons", messages);
    }

    private static void ValidatePlacementPointProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var point) || point.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{displayName} requires object {propertyName}."));
            return;
        }

        foreach (var coordinate in new[] { "x", "y" })
        {
            if (!point.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }
    }

    private static void ValidatePlacementLineProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var line) || line.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{displayName} requires object {propertyName}."));
            return;
        }

        ValidatePlacementPointProperty(line, $"{displayName} {propertyName}", "start", messages);
        ValidatePlacementPointProperty(line, $"{displayName} {propertyName}", "end", messages);
    }

    private static void ValidatePlacementRectProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var rect) || rect.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{displayName} requires object {propertyName}."));
            return;
        }

        foreach (var coordinate in new[] { "x", "y", "width", "height" })
        {
            if (!rect.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }
    }

    private static void ValidateVisualSnapshot(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidateSchemaVersion(
            root,
            PlanOverlaySnapshot.CurrentSchemaVersion,
            "visual snapshot",
            messages);
        RequireNonEmptyStringProperty(root, "Visual snapshot", "documentId", messages);
        RequireConstStringProperty(root, "Visual snapshot", "coordinateSpace", "OpenPlanTracePageCoordinates", messages);
        RequireConstStringProperty(root, "Visual snapshot", "origin", "TopLeft", messages);
        RequireConstStringProperty(root, "Visual snapshot", "xAxisDirection", "Right", messages);
        RequireConstStringProperty(root, "Visual snapshot", "yAxisDirection", "Down", messages);
        RequireConstStringProperty(root, "Visual snapshot", "unit", "DrawingUnit", messages);
        RequireNonEmptyStringProperty(root, "Visual snapshot", "scanSchemaVersion", messages);
        ValidateRequiredRatioProperty(root, "Visual snapshot", "qualityConfidence", messages);
        var reviewQueueCount = TryReadNonNegativeIntegerProperty(root, "Visual snapshot", "reviewQueueCount", messages);
        ValidateCountBreakdownProperty(root, "Visual snapshot", "reviewQueueKindBreakdown", messages);
        ValidateCountBreakdownProperty(root, "Visual snapshot", "reviewQueueSeverityBreakdown", messages);

        var rootIssues = ValidateVisualSnapshotIssues(root, "Visual snapshot", "issues", messages);
        if (!root.TryGetProperty("pages", out var pages) || pages.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Visual snapshot requires an array pages."));
            return;
        }

        var pageItems = pages.EnumerateArray().ToArray();
        if (pageItems.Length == 0)
        {
            messages.Add(new ArtifactValidationMessage("warning", "Visual snapshot contains no pages."));
        }

        var pageIssueCount = 0;
        for (var index = 0; index < pageItems.Length; index++)
        {
            var page = pageItems[index];
            var prefix = $"Visual snapshot pages[{index}]";
            if (page.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                continue;
            }

            ValidatePositiveIntegerProperty(page, prefix, "pageNumber", messages);
            ValidatePositiveNumberProperty(page, prefix, "width", messages);
            ValidatePositiveNumberProperty(page, prefix, "height", messages);
            ValidateSnapshotRectProperty(page, prefix, "pageBounds", allowEmpty: false, messages);
            ValidateSnapshotRectProperty(page, prefix, "detectionBounds", allowEmpty: true, messages);
            ValidateRequiredRatioProperty(page, prefix, "detectionCoverage", messages);
            var drawableItemCount = TryReadNonNegativeIntegerProperty(page, prefix, "drawableItemCount", messages);
            TryReadNonNegativeIntegerProperty(page, prefix, "primitiveCount", messages);
            var pageReviewQueueCount = TryReadNonNegativeIntegerProperty(page, prefix, "reviewQueueCount", messages);
            ValidateCountBreakdownProperty(page, prefix, "reviewQueueKindBreakdown", messages);
            ValidateCountBreakdownProperty(page, prefix, "reviewQueueSeverityBreakdown", messages);

            var layerCountSum = 0;
            if (!page.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array layers."));
            }
            else
            {
                var layerItems = layers.EnumerateArray().ToArray();
                if (layerItems.Length == 0)
                {
                    messages.Add(new ArtifactValidationMessage("warning", $"{prefix} contains no layer summaries."));
                }

                for (var layerIndex = 0; layerIndex < layerItems.Length; layerIndex++)
                {
                    layerCountSum += ValidateVisualSnapshotLayer(
                        layerItems[layerIndex],
                        $"{prefix}.layers[{layerIndex}]",
                        messages);
                }
            }

            var pageReviewQueueItems = ReadArrayProperty(page, "reviewQueue", prefix, messages);
            for (var reviewIndex = 0; reviewIndex < pageReviewQueueItems.Length; reviewIndex++)
            {
                ValidateVisualSnapshotReviewQueueItem(
                    pageReviewQueueItems[reviewIndex],
                    $"{prefix}.reviewQueue[{reviewIndex}]",
                    messages);
            }

            if (pageReviewQueueCount is not null && pageReviewQueueCount.Value != pageReviewQueueItems.Length)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} reviewQueueCount must equal reviewQueue length ({pageReviewQueueItems.Length})."));
            }

            if (drawableItemCount is not null && drawableItemCount.Value != layerCountSum)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} drawableItemCount must equal the sum of layer counts ({layerCountSum})."));
            }

            pageIssueCount += ValidateVisualSnapshotIssues(page, prefix, "issues", messages);
        }

        if (reviewQueueCount is not null)
        {
            var pageReviewQueueCountSum = pageItems
                .Where(page => page.ValueKind == JsonValueKind.Object)
                .Sum(page => ReadNonNegativeIntegerPropertyOrZero(page, "reviewQueueCount"));
            if (reviewQueueCount.Value != pageReviewQueueCountSum)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Visual snapshot reviewQueueCount must equal summed page reviewQueueCount ({pageReviewQueueCountSum})."));
            }
        }

        if (rootIssues != pageIssueCount)
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"Visual snapshot top-level issues count ({rootIssues}) differs from page issue count ({pageIssueCount})."));
        }
    }

    private static int ValidateVisualSnapshotLayer(
        JsonElement layer,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (layer.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return 0;
        }

        RequireNonEmptyStringProperty(layer, prefix, "name", messages);
        var count = TryReadNonNegativeIntegerProperty(layer, prefix, "count", messages) ?? 0;
        ValidateSnapshotRectProperty(layer, prefix, "bounds", allowEmpty: true, messages);
        ValidateSnapshotRectProperty(layer, prefix, "normalizedBounds", allowEmpty: true, messages);
        ValidateNonNegativeNumberProperty(layer, prefix, "normalizedDensity", messages);
        ValidateOptionalRatioProperty(layer, prefix, "averageConfidence", messages);
        ValidateOptionalRatioProperty(layer, prefix, "minimumConfidence", messages);
        ValidateOptionalRatioProperty(layer, prefix, "maximumConfidence", messages);
        if (count > 0)
        {
            foreach (var propertyName in new[] { "averageConfidence", "minimumConfidence", "maximumConfidence" })
            {
                if (!layer.TryGetProperty(propertyName, out var value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} {propertyName} must be present when count is greater than 0."));
                }
            }
        }

        if (!layer.TryGetProperty("breakdown", out var breakdown) || breakdown.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an object breakdown."));
            return count;
        }

        foreach (var property in breakdown.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var breakdownCount)
                || breakdownCount < 0)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix}.breakdown.{property.Name} must be a non-negative integer."));
            }
        }

        return count;
    }

    private static void ValidateVisualSnapshotReviewQueueItem(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, prefix, "id", messages);
        RequireNonEmptyStringProperty(item, prefix, "kind", messages);
        RequireNonEmptyStringProperty(item, prefix, "detector", messages);
        RequireNonEmptyStringProperty(item, prefix, "itemId", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "pageNumber", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "sourcePrimitiveCount", messages);
        TryReadNonNegativeIntegerProperty(item, prefix, "sourceLayerCount", messages);
        ValidateSnapshotRectProperty(item, prefix, "bounds", allowEmpty: true, messages);
        ValidateRequiredRatioProperty(item, prefix, "confidence", messages);
        RequireNonEmptyStringProperty(item, prefix, "recommendedAction", messages);
        ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);

        if (!item.TryGetProperty("priority", out var priority)
            || priority.ValueKind != JsonValueKind.Number
            || !priority.TryGetInt32(out _))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} priority must be an integer."));
        }

        var severity = ReadStringProperty(item, "severity");
        if (string.IsNullOrWhiteSpace(severity))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} severity is required."));
        }
    }

    private static void ValidateCountBreakdownProperty(
        JsonElement root,
        string prefix,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var breakdown) || breakdown.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an object {propertyName}."));
            return;
        }

        foreach (var property in breakdown.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var count)
                || count < 0)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix}.{propertyName}.{property.Name} must be a non-negative integer."));
            }
        }
    }

    private static int ValidateVisualSnapshotIssues(
        JsonElement root,
        string prefix,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array {propertyName}."));
            return 0;
        }

        var issueItems = issues.EnumerateArray().ToArray();
        for (var index = 0; index < issueItems.Length; index++)
        {
            var issue = issueItems[index];
            var issuePrefix = $"{prefix}.{propertyName}[{index}]";
            if (issue.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{issuePrefix} must be an object."));
                continue;
            }

            RequireNonEmptyStringProperty(issue, issuePrefix, "code", messages);
            RequireNonEmptyStringProperty(issue, issuePrefix, "message", messages);
            ValidatePositiveIntegerProperty(issue, issuePrefix, "pageNumber", messages);
            var severity = ReadStringProperty(issue, "severity");
            if (severity is not "info" and not "warning" and not "error")
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{issuePrefix} severity must be info, warning, or error."));
            }
        }

        return issueItems.Length;
    }

    private static void ValidateSnapshotRectProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        bool allowEmpty,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var rect) || rect.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{displayName} requires object {propertyName}."));
            return;
        }

        foreach (var coordinate in new[]
                 {
                     "x",
                     "y",
                     "width",
                     "height",
                     "left",
                     "top",
                     "right",
                     "bottom",
                     "centerX",
                     "centerY",
                     "area"
                 })
        {
            if (!rect.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }

        if (!rect.TryGetProperty("width", out var widthElement)
            || !rect.TryGetProperty("height", out var heightElement)
            || !widthElement.TryGetDouble(out var width)
            || !heightElement.TryGetDouble(out var height))
        {
            return;
        }

        var isEmpty = width < 0 || height < 0;
        if (isEmpty && !allowEmpty)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} cannot be empty."));
        }

        if (isEmpty && (Math.Abs(width + 1) > 0.001 || Math.Abs(height + 1) > 0.001))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} empty {propertyName} must use width=-1 and height=-1."));
        }

        if (!isEmpty && (width < 0 || height < 0))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} width/height must be non-negative unless the rect is empty."));
        }

        if (rect.TryGetProperty("area", out var areaElement)
            && areaElement.TryGetDouble(out var area)
            && area < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName}.area must be non-negative."));
        }
    }

    private static void ValidateTopLevelContract(
        string kind,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        foreach (var requiredProperty in RequiredTopLevelProperties(kind))
        {
            if (!root.TryGetProperty(requiredProperty, out _))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Missing required top-level property '{requiredProperty}'."));
            }
        }
    }

    private static void ValidateByKind(
        string kind,
        string json,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        try
        {
            switch (kind)
            {
                case "scan":
                    ValidateSchemaVersion(
                        root,
                        PlanTraceExport.CurrentSchemaVersion,
                        "scan",
                        messages);
                    break;
                case "scan-compact":
                    ValidateCompactScan(root, messages);
                    break;
                case "object-review-dataset":
                    ValidateSchemaVersion(
                        root,
                        ObjectReviewDataset.CurrentSchemaVersion,
                        "object review dataset",
                        messages);
                    _ = JsonSerializer.Deserialize<ObjectReviewDataset>(
                        json,
                        CreateValidationJsonOptions(writeIndented: false));
                    break;
                case "object-correction-dataset":
                    ValidateSchemaVersion(
                        root,
                        ObjectCorrectionDataset.CurrentSchemaVersion,
                        "object correction dataset",
                        messages);
                    _ = ObjectCorrectionDataset.ParseJson(json);
                    break;
                case "benchmark-manifest":
                    var manifest = JsonSerializer.Deserialize<BenchmarkManifest>(
                        json,
                        CreateBenchmarkJsonOptions(writeIndented: false))
                        ?? new BenchmarkManifest();
                    BenchmarkManifest.ValidateSchemaVersion(manifest);
                    ValidateBenchmarkManifest(manifest, messages);
                    break;
                case "benchmark-result":
                    ValidateSchemaVersion(
                        root,
                        BenchmarkRunResult.CurrentSchemaVersion,
                        "benchmark result",
                        messages);
                    ValidateBenchmarkResult(root, messages);
                    break;
                case "benchmark-comparison":
                    ValidateSchemaVersion(
                        root,
                        BenchmarkComparisonResult.CurrentSchemaVersion,
                        "benchmark comparison",
                        messages);
                    ValidateBenchmarkComparison(root, messages);
                    break;
                case "viewer-benchmark-review-session":
                    ValidateSchemaVersion(
                        root,
                        ViewerBenchmarkReviewSessionJsonSchema.CurrentSchemaVersion,
                        "viewer benchmark review session",
                        messages);
                    ValidateViewerBenchmarkReviewSession(root, messages);
                    break;
                case "batch-manifest":
                    var batchManifest = JsonSerializer.Deserialize<BatchScanManifest>(
                        json,
                        CreateBatchJsonOptions(writeIndented: false))
                        ?? new BatchScanManifest();
                    BatchScanManifest.ValidateSchemaVersion(batchManifest);
                    ValidateBatchManifest(batchManifest, messages);
                    break;
                case "batch-result":
                    ValidateSchemaVersion(
                        root,
                        BatchScanRunResult.CurrentSchemaVersion,
                        "batch result",
                        messages);
                    ValidateBatchResult(root, messages);
                    break;
                case "batch-comparison":
                    ValidateSchemaVersion(
                        root,
                        BatchScanComparisonResult.CurrentSchemaVersion,
                        "batch comparison",
                        messages);
                    ValidateBatchComparison(root, messages);
                    break;
                case "layer-profile":
                    _ = LayerCategoryProfile.ParseJson(json);
                    break;
                case "object-label-profile":
                    _ = ObjectLabelProfile.ParseJson(json);
                    break;
                case "kvemo-crops":
                    ValidateKvemoCropEntry(root, messages);
                    break;
                case "placement":
                    ValidatePlacementExport(root, messages);
                    break;
                case "visual-snapshot":
                    ValidateVisualSnapshot(root, messages);
                    break;
                case "geojson":
                    ValidateSchemaVersion(
                        root,
                        PlanTraceGeoJsonExporter.CurrentSchemaVersion,
                        "GeoJSON export",
                        messages);
                    ValidateGeoJson(root, messages);
                    break;
                default:
                    messages.Add(new ArtifactValidationMessage("error", $"Unknown artifact kind '{kind}'."));
                    break;
            }
        }
        catch (Exception exception)
        {
            messages.Add(new ArtifactValidationMessage("error", exception.Message));
        }
    }

    private static void ValidateSchemaVersion(
        JsonElement root,
        string expectedSchemaVersion,
        string displayName,
        ICollection<ArtifactValidationMessage> messages)
    {
        var schemaVersion = ReadStringProperty(root, "schemaVersion");
        if (schemaVersion is null)
        {
            return;
        }

        if (!string.Equals(schemaVersion, expectedSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Unsupported {displayName} schemaVersion '{schemaVersion}'. Expected '{expectedSchemaVersion}'."));
        }
    }

    private static void ValidateCompactScan(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidateSchemaVersion(
            root,
            PlanTraceCompactJsonExporter.CurrentSchemaVersion,
            "compact scan",
            messages);

        var sourceSchemaVersion = ReadStringProperty(root, "sourceSchemaVersion");
        if (!string.Equals(sourceSchemaVersion, PlanTraceExport.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Unsupported compact scan sourceSchemaVersion '{sourceSchemaVersion ?? "(missing)"}'. Expected '{PlanTraceExport.CurrentSchemaVersion}'."));
        }

        var encoding = ReadStringProperty(root, "encoding");
        if (!string.Equals(encoding, "shape-string-token-v1", StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Unsupported compact scan encoding '{encoding ?? "(missing)"}'. Expected 'shape-string-token-v1'."));
        }

        if (!root.TryGetProperty("dictionary", out var dictionary)
            || dictionary.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", "Compact scan requires object dictionary."));
            return;
        }

        ReadArrayProperty(dictionary, "stringPrefixes", "Compact scan dictionary", messages);
        ReadArrayProperty(dictionary, "strings", "Compact scan dictionary", messages);
        ReadArrayProperty(dictionary, "shapes", "Compact scan dictionary", messages);

        try
        {
            using var output = new MemoryStream();
            using (var writer = new Utf8JsonWriter(output))
            {
                PlanTraceCompactJsonExporter.ExpandToScanJson(root, writer);
            }

            using var expanded = JsonDocument.Parse(output.ToArray());
            ValidateSchemaVersion(
                expanded.RootElement,
                PlanTraceExport.CurrentSchemaVersion,
                "expanded compact scan",
                messages);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Compact scan token tree could not be expanded: {exception.Message}"));
        }
    }

    private static void ValidateBenchmarkManifest(
        BenchmarkManifest manifest,
        ICollection<ArtifactValidationMessage> messages)
    {
        var fixtures = manifest.Fixtures ?? Array.Empty<BenchmarkFixture>();
        if (fixtures.Count == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                "Benchmark manifest contains no fixtures."));
        }

        for (var index = 0; index < fixtures.Count; index++)
        {
            var fixture = fixtures[index];
            var prefix = string.IsNullOrWhiteSpace(fixture.Id)
                ? $"fixture #{index + 1}"
                : $"fixture '{fixture.Id}'";

            if (string.IsNullOrWhiteSpace(fixture.Id))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires a non-empty id."));
            }

            if (string.IsNullOrWhiteSpace(fixture.SourcePath))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires a non-empty sourcePath."));
            }

            if (fixture.Expectations is null)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an expectations object."));
            }
            else
            {
                ValidateBenchmarkExpectations(prefix, fixture.Expectations, messages);
            }
        }
    }

    private static void ValidateBenchmarkResult(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        var cases = ReadArrayProperty(root, "cases", "benchmark result", messages);
        var reviewQueue = ReadArrayProperty(root, "reviewQueue", "benchmark result", messages);
        if (!TryReadObjectProperty(root, "scoreboard", "benchmark result", messages, out var scoreboard))
        {
            return;
        }

        var passedCases = cases.Count(item => ReadBooleanProperty(item, "passed") == true && ReadBooleanProperty(item, "skipped") != true);
        var skippedCases = cases.Count(item => ReadBooleanProperty(item, "skipped") == true);
        var failedCases = cases.Length - passedCases - skippedCases;
        var passedAssertions = cases.Sum(item => CountAssertions(item, passed: true));
        var failedAssertions = cases.Sum(item => CountAssertions(item, passed: false));
        var failedScans = cases.Count(item => ReadBooleanProperty(item, "scanSucceeded") == false && ReadBooleanProperty(item, "skipped") != true);
        var detectorMetrics = cases
            .SelectMany(item => item.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array
                ? metrics.EnumerateArray()
                : Enumerable.Empty<JsonElement>())
            .ToArray();
        var expectedTargets = detectorMetrics.Sum(item => ReadNonNegativeIntegerPropertyOrZero(item, "expectedCount"));
        var matchedTargets = detectorMetrics.Sum(item => ReadNonNegativeIntegerPropertyOrZero(item, "matchedCount"));
        var missedTargets = detectorMetrics.Sum(item => ReadNonNegativeIntegerPropertyOrZero(item, "missedCount"));
        var extraDetections = detectorMetrics
            .Where(item => ReadBooleanProperty(item, "precisionScoringEnabled") == true)
            .Sum(item => ReadNonNegativeIntegerPropertyOrZero(item, "extraCount"));

        AddExpectedIntegerMessage("benchmark result", "caseCount", cases.Length, root, messages);
        AddExpectedIntegerMessage("benchmark result", "reviewQueueCount", reviewQueue.Length, root, messages);
        AddExpectedIntegerMessage("benchmark result", "passedCaseCount", passedCases, root, messages);
        AddExpectedIntegerMessage("benchmark result", "failedCaseCount", failedCases, root, messages);
        AddExpectedIntegerMessage("benchmark result", "skippedCaseCount", skippedCases, root, messages);
        AddExpectedIntegerMessage("benchmark result", "passedAssertionCount", passedAssertions, root, messages);
        AddExpectedIntegerMessage("benchmark result", "failedAssertionCount", failedAssertions, root, messages);

        ValidateSchemaVersion(
            scoreboard,
            BenchmarkScoreboard.CurrentSchemaVersion,
            "benchmark result scoreboard",
            messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "caseCount", cases.Length, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "scoredCaseCount", cases.Length - skippedCases, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "skippedCaseCount", skippedCases, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "failedScanCount", failedScans, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "failedAssertionCount", failedAssertions, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "expectedTargetCount", expectedTargets, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "matchedTargetCount", matchedTargets, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "missedTargetCount", missedTargets, scoreboard, messages);
        AddExpectedIntegerMessage("benchmark result scoreboard", "extraDetectionCount", extraDetections, scoreboard, messages);

        var grade = ReadStringProperty(scoreboard, "grade");
        if (grade is null || !Enum.TryParse<BenchmarkScoreGrade>(grade, ignoreCase: false, out _))
        {
            messages.Add(new ArtifactValidationMessage("error", $"Benchmark result scoreboard has an unknown grade '{grade ?? "(missing)"}'."));
        }

        ValidateOptionalRatioProperty(scoreboard, "benchmark result scoreboard", "overallScore", messages);
        ValidateOptionalRatioProperty(scoreboard, "benchmark result scoreboard", "consumerReadinessScore", messages);

        for (var index = 0; index < cases.Length; index++)
        {
            ValidateBenchmarkResultCase(cases[index], index, messages);
        }

        for (var index = 0; index < reviewQueue.Length; index++)
        {
            ValidateBenchmarkReviewQueueItem(reviewQueue[index], index, messages);
        }

        ValidateBenchmarkResultScoreboard(scoreboard, messages);
    }

    private static void ValidateBenchmarkResultCase(
        JsonElement item,
        int index,
        ICollection<ArtifactValidationMessage> messages)
    {
        var prefix = $"Benchmark result cases[{index}]";
        RequireNonEmptyStringProperty(item, prefix, "fixtureId", messages);

        if (!item.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an object counts."));
        }
        else
        {
            ValidateBenchmarkCounts(counts, prefix, messages);
        }

        var assertions = ReadArrayProperty(item, "assertions", prefix, messages);
        var passedAssertions = assertions.Count(assertion => ReadBooleanProperty(assertion, "passed") == true);
        var failedAssertions = assertions.Length - passedAssertions;
        AddExpectedIntegerMessage(prefix, "passedAssertionCount", passedAssertions, item, messages);
        AddExpectedIntegerMessage(prefix, "failedAssertionCount", failedAssertions, item, messages);

        if (item.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Array)
        {
            var metricItems = metrics.EnumerateArray().ToArray();
            for (var metricIndex = 0; metricIndex < metricItems.Length; metricIndex++)
            {
                ValidateBenchmarkMetric(metricItems[metricIndex], $"{prefix}.metrics[{metricIndex}]", messages);
            }
        }
        else
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array metrics."));
        }

        ValidateBenchmarkIssueSummaries(
            ReadArrayProperty(item, "qualityIssues", prefix, messages),
            $"{prefix}.qualityIssues",
            messages);
        ValidateBenchmarkIssueSummaries(
            ReadArrayProperty(item, "diagnosticIssues", prefix, messages),
            $"{prefix}.diagnosticIssues",
            messages);
        ValidateBenchmarkStageSummaries(
            ReadArrayProperty(item, "stages", prefix, messages),
            $"{prefix}.stages",
            messages);

        if (TryReadObjectProperty(item, "importReadiness", prefix, messages, out var importReadiness))
        {
            ValidateBenchmarkImportReadiness(importReadiness, $"{prefix}.importReadiness", messages);
        }
    }

    private static void ValidateBenchmarkImportReadiness(
        JsonElement importReadiness,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        RequireNonEmptyStringProperty(importReadiness, prefix, "grade", messages);
        ValidateRequiredRatioProperty(importReadiness, prefix, "score", messages);
        ValidateBooleanProperty(importReadiness, prefix, "readyForGeometryImport", messages);
        ValidateBooleanProperty(importReadiness, prefix, "readyForMetricImport", messages);
        ValidateBooleanProperty(importReadiness, prefix, "readyForRoutingImport", messages);
        ValidateBooleanProperty(importReadiness, prefix, "requiresReview", messages);
        ValidateRequiredStringArrayProperty(importReadiness, prefix, "blockingIssueCodes", messages);
        ValidateRequiredStringArrayProperty(importReadiness, prefix, "reviewIssueCodes", messages);
        ValidateRequiredStringArrayProperty(importReadiness, prefix, "recommendedActions", messages);
        ValidateRequiredStringArrayProperty(importReadiness, prefix, "evidence", messages);

        var readyForGeometry = ReadBooleanProperty(importReadiness, "readyForGeometryImport");
        var readyForMetric = ReadBooleanProperty(importReadiness, "readyForMetricImport");
        var readyForRouting = ReadBooleanProperty(importReadiness, "readyForRoutingImport");
        if (readyForMetric == true && readyForGeometry != true)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} readyForMetricImport cannot be true unless readyForGeometryImport is true."));
        }

        if (readyForRouting == true && readyForGeometry != true)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} readyForRoutingImport cannot be true unless readyForGeometryImport is true."));
        }
    }

    private static void ValidateBenchmarkCounts(
        JsonElement counts,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        foreach (var propertyName in new[]
                 {
                     "pages",
                     "regions",
                     "dimensions",
                     "annotations",
                     "annotationReferences",
                     "gridAxes",
                     "gridBaySpacings",
                     "walls",
                     "wallNodes",
                     "wallEdges",
                     "rooms",
                     "roomAdjacencies",
                     "roomClusters",
                     "openings",
                     "objects",
                     "objectGroups",
                     "objectAggregates",
                     "routingItems",
                     "routingSuppressedObjects",
                     "diagnostics",
                     "diagnosticWarnings",
                     "diagnosticErrors",
                     "qualityIssues",
                     "measurementCheckedCount",
                     "measurementConsistentCount",
                     "measurementOutlierCount"
                 })
        {
            TryReadNonNegativeIntegerProperty(counts, $"{prefix}.counts", propertyName, messages);
        }

        var grade = ReadStringProperty(counts, "qualityGrade");
        if (grade is null || !Enum.TryParse<PlanScanQualityGrade>(grade, ignoreCase: false, out _))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix}.counts has an unknown qualityGrade '{grade ?? "(missing)"}'."));
        }

        ValidateOptionalRatioProperty(counts, $"{prefix}.counts", "qualityConfidence", messages);
        ValidateOptionalRatioProperty(counts, $"{prefix}.counts", "measurementConsistencyConfidence", messages);
    }

    private static void ValidateBenchmarkMetric(
        JsonElement metric,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        RequireNonEmptyStringProperty(metric, prefix, "detector", messages);
        var expected = TryReadNonNegativeIntegerProperty(metric, prefix, "expectedCount", messages);
        var detected = TryReadNonNegativeIntegerProperty(metric, prefix, "detectedCount", messages);
        var matched = TryReadNonNegativeIntegerProperty(metric, prefix, "matchedCount", messages);
        var missed = TryReadNonNegativeIntegerProperty(metric, prefix, "missedCount", messages);
        var extra = TryReadNonNegativeIntegerProperty(metric, prefix, "extraCount", messages);
        var scored = TryReadNonNegativeIntegerProperty(metric, prefix, "scoredDetectionCount", messages);
        var reviewOnly = TryReadNonNegativeIntegerProperty(metric, prefix, "reviewOnlyDetectionCount", messages);
        ValidateOptionalRatioProperty(metric, prefix, "recall", messages);
        ValidateOptionalRatioProperty(metric, prefix, "precision", messages);
        ValidateOptionalRatioProperty(metric, prefix, "f1", messages);
        if (!metric.TryGetProperty("precisionScoringEnabled", out var precisionScoring)
            || precisionScoring.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires a boolean precisionScoringEnabled."));
        }

        if (expected is not null && matched is not null && missed is not null && expected.Value - matched.Value != missed.Value)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} missedCount should equal expectedCount - matchedCount."));
        }

        if (scored is not null && matched is not null && extra is not null && scored.Value - matched.Value != extra.Value)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} extraCount should equal scoredDetectionCount - matchedCount."));
        }

        if (detected is not null && scored is not null && reviewOnly is not null && scored.Value + reviewOnly.Value != detected.Value)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} detectedCount should equal scoredDetectionCount + reviewOnlyDetectionCount."));
        }

        ReadArrayProperty(metric, "matches", prefix, messages);
        var extraDetections = ReadArrayProperty(metric, "extraDetections", prefix, messages);
        if (extra is not null && extraDetections.Length != extra.Value)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} extraDetections length should equal extraCount."));
        }

        for (var index = 0; index < extraDetections.Length; index++)
        {
            ValidateBenchmarkDetectionSummary(extraDetections[index], $"{prefix}.extraDetections[{index}]", messages);
        }

        var reviewOnlyDetections = ReadArrayProperty(metric, "reviewOnlyDetections", prefix, messages);
        if (reviewOnly is not null && reviewOnlyDetections.Length != reviewOnly.Value)
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} reviewOnlyDetections length should equal reviewOnlyDetectionCount."));
        }

        for (var index = 0; index < reviewOnlyDetections.Length; index++)
        {
            ValidateBenchmarkDetectionSummary(reviewOnlyDetections[index], $"{prefix}.reviewOnlyDetections[{index}]", messages);
        }
    }

    private static void ValidateBenchmarkDetectionSummary(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        RequireNonEmptyStringProperty(item, prefix, "detectionId", messages);
        ReadArrayProperty(item, "detectedTags", prefix, messages);
        if (string.IsNullOrWhiteSpace(ReadStringProperty(item, "evidence")))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires non-empty evidence."));
        }

        if (item.TryGetProperty("bounds", out var bounds)
            && bounds.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            ValidateNullableRectProperty(item, prefix, "bounds", messages);
        }
    }

    private static void ValidateBenchmarkReviewQueueItem(
        JsonElement item,
        int index,
        ICollection<ArtifactValidationMessage> messages)
    {
        var prefix = $"Benchmark result reviewQueue[{index}]";
        RequireNonEmptyStringProperty(item, prefix, "fixtureId", messages);
        RequireNonEmptyStringProperty(item, prefix, "detector", messages);
        RequireNonEmptyStringProperty(item, prefix, "recommendedAction", messages);

        var kind = ReadStringProperty(item, "kind");
        if (kind is null || !Enum.TryParse<BenchmarkReviewQueueKind>(kind, ignoreCase: false, out _))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} has an unknown kind '{kind ?? "(missing)"}'."));
        }

        if (!item.TryGetProperty("precisionScoringEnabled", out var precisionScoring)
            || precisionScoring.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires a boolean precisionScoringEnabled."));
        }

        if (!TryReadObjectProperty(item, "detection", prefix, messages, out var detection))
        {
            return;
        }

        ValidateBenchmarkDetectionSummary(detection, $"{prefix}.detection", messages);
    }

    private static void ValidateBenchmarkIssueSummaries(
        JsonElement[] issues,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < issues.Length; index++)
        {
            var item = issues[index];
            var itemPrefix = $"{prefix}[{index}]";
            RequireNonEmptyStringProperty(item, itemPrefix, "code", messages);
            var severity = ReadStringProperty(item, "severity");
            if (severity is null || !Enum.TryParse<DiagnosticSeverity>(severity, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{itemPrefix} has an unknown severity '{severity ?? "(missing)"}'."));
            }

            TryReadNonNegativeIntegerProperty(item, itemPrefix, "count", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "sourcePrimitiveCount", messages);
            ValidateOptionalRatioProperty(item, itemPrefix, "maxConfidence", messages);
            ReadArrayProperty(item, "pageNumbers", itemPrefix, messages);
            ReadArrayProperty(item, "sourcePrimitiveIds", itemPrefix, messages);
            if (!item.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{itemPrefix} requires an object properties."));
            }
        }
    }

    private static void ValidateBenchmarkStageSummaries(
        JsonElement[] stages,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < stages.Length; index++)
        {
            var item = stages[index];
            var itemPrefix = $"{prefix}[{index}]";
            RequireNonEmptyStringProperty(item, itemPrefix, "stage", messages);
            ValidateOptionalStringProperty(item, itemPrefix, "displayName", messages);
            ValidateOptionalStringProperty(item, itemPrefix, "kind", messages);
            ValidateOptionalNonNegativeIntegerProperty(item, itemPrefix, "dependencyLevel", messages);
            ValidateOptionalNonNegativeIntegerProperty(item, itemPrefix, "preferredDependencyLevel", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "reads", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "optionalReads", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "writes", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "capabilities", messages);
            ValidateOptionalBooleanProperty(item, itemPrefix, "isDependencyReady", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "missingRequiredReads", messages);
            ValidateOptionalStringArrayProperty(item, itemPrefix, "missingOptionalReads", messages);
            ValidateArtifactSnapshots(
                ReadArrayProperty(item, "inputArtifacts", itemPrefix, messages),
                $"{itemPrefix}.inputArtifacts",
                messages);
            ValidateArtifactSnapshots(
                ReadArrayProperty(item, "outputArtifacts", itemPrefix, messages),
                $"{itemPrefix}.outputArtifacts",
                messages);
            ValidateArtifactChanges(
                ReadArrayProperty(item, "changedArtifacts", itemPrefix, messages),
                $"{itemPrefix}.changedArtifacts",
                messages);
            ValidateNonNegativeNumberProperty(item, itemPrefix, "durationMilliseconds", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "inputCount", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "outputCount", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "diagnosticCount", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "infoCount", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "warningCount", messages);
            TryReadNonNegativeIntegerProperty(item, itemPrefix, "errorCount", messages);
        }
    }

    private static void ValidateArtifactSnapshots(
        JsonElement[] snapshots,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < snapshots.Length; index++)
        {
            var itemPrefix = $"{prefix}[{index}]";
            ValidateEnumProperty<PlanArtifactKind>(snapshots[index], itemPrefix, "artifact", messages);
            TryReadNonNegativeIntegerProperty(snapshots[index], itemPrefix, "count", messages);
        }
    }

    private static void ValidateArtifactChanges(
        JsonElement[] changes,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < changes.Length; index++)
        {
            var item = changes[index];
            var itemPrefix = $"{prefix}[{index}]";
            ValidateEnumProperty<PlanArtifactKind>(item, itemPrefix, "artifact", messages);
            var before = TryReadNonNegativeIntegerProperty(item, itemPrefix, "beforeCount", messages);
            var after = TryReadNonNegativeIntegerProperty(item, itemPrefix, "afterCount", messages);
            var delta = TryReadIntegerProperty(item, itemPrefix, "delta", messages);
            if (before is not null && after is not null && delta is not null && after.Value - before.Value != delta.Value)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{itemPrefix} delta should equal afterCount - beforeCount."));
            }
        }
    }

    private static void ValidateOptionalStringProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a string when present."));
        }
    }

    private static void ValidateOptionalNonNegativeIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a non-negative integer when present."));
        }
    }

    private static void ValidateOptionalBooleanProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a boolean when present."));
        }
    }

    private static void ValidateBenchmarkResultScoreboard(
        JsonElement scoreboard,
        ICollection<ArtifactValidationMessage> messages)
    {
        var cases = ReadArrayProperty(scoreboard, "cases", "benchmark result scoreboard", messages);
        var detectors = ReadArrayProperty(scoreboard, "detectors", "benchmark result scoreboard", messages);
        var failures = ReadArrayProperty(scoreboard, "failureBuckets", "benchmark result scoreboard", messages);
        ReadArrayProperty(scoreboard, "recommendedNextActions", "benchmark result scoreboard", messages);

        for (var index = 0; index < cases.Length; index++)
        {
            var item = cases[index];
            var prefix = $"Benchmark result scoreboard cases[{index}]";
            RequireNonEmptyStringProperty(item, prefix, "fixtureId", messages);
            var grade = ReadStringProperty(item, "grade");
            if (grade is null || !Enum.TryParse<BenchmarkScoreGrade>(grade, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} has an unknown grade '{grade ?? "(missing)"}'."));
            }

            ValidateOptionalRatioProperty(item, prefix, "overallScore", messages);
            ValidateOptionalRatioProperty(item, prefix, "targetF1", messages);
            ValidateOptionalRatioProperty(item, prefix, "targetRecall", messages);
            ValidateOptionalRatioProperty(item, prefix, "targetPrecision", messages);
            ValidateOptionalRatioProperty(item, prefix, "assertionReliability", messages);
            ValidateOptionalRatioProperty(item, prefix, "scanQualityScore", messages);
            ValidateOptionalRatioProperty(item, prefix, "measurementScore", messages);
            ValidateOptionalRatioProperty(item, prefix, "importReadinessScore", messages);
            ValidateBooleanProperty(item, prefix, "readyForGeometryImport", messages);
            ValidateBooleanProperty(item, prefix, "readyForMetricImport", messages);
            ValidateBooleanProperty(item, prefix, "readyForRoutingImport", messages);
            var readyForGeometry = ReadBooleanProperty(item, "readyForGeometryImport");
            var readyForMetric = ReadBooleanProperty(item, "readyForMetricImport");
            var readyForRouting = ReadBooleanProperty(item, "readyForRoutingImport");
            if (readyForMetric == true && readyForGeometry != true)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} readyForMetricImport cannot be true unless readyForGeometryImport is true."));
            }

            if (readyForRouting == true && readyForGeometry != true)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} readyForRoutingImport cannot be true unless readyForGeometryImport is true."));
            }
        }

        for (var index = 0; index < detectors.Length; index++)
        {
            var item = detectors[index];
            var prefix = $"Benchmark result scoreboard detectors[{index}]";
            RequireNonEmptyStringProperty(item, prefix, "detector", messages);
            var grade = ReadStringProperty(item, "grade");
            if (grade is null || !Enum.TryParse<BenchmarkScoreGrade>(grade, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} has an unknown grade '{grade ?? "(missing)"}'."));
            }

            ValidateOptionalRatioProperty(item, prefix, "score", messages);
            ValidateOptionalRatioProperty(item, prefix, "recall", messages);
            ValidateOptionalRatioProperty(item, prefix, "precision", messages);
            ValidateOptionalRatioProperty(item, prefix, "f1", messages);
        }

        for (var index = 0; index < failures.Length; index++)
        {
            var item = failures[index];
            var prefix = $"Benchmark result scoreboard failureBuckets[{index}]";
            RequireNonEmptyStringProperty(item, prefix, "code", messages);
            var severity = ReadStringProperty(item, "severity");
            if (severity is null || !Enum.TryParse<BenchmarkFailureSeverity>(severity, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} has an unknown severity '{severity ?? "(missing)"}'."));
            }

            TryReadNonNegativeIntegerProperty(item, prefix, "count", messages);
            ReadArrayProperty(item, "evidence", prefix, messages);
            ReadArrayProperty(item, "targetIds", prefix, messages);
        }
    }

    private static void ValidateBenchmarkComparison(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("cases", out var cases) || cases.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Benchmark comparison requires a cases array."));
            return;
        }

        if (!root.TryGetProperty("signals", out var signals) || signals.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Benchmark comparison requires a signals array."));
            return;
        }

        var caseItems = cases.EnumerateArray().ToArray();
        var signalItems = signals.EnumerateArray().ToArray();
        var matched = caseItems.Count(item => ReadStringProperty(item, "status") == BenchmarkComparisonCaseStatus.Matched.ToString());
        var added = caseItems.Count(item => ReadStringProperty(item, "status") == BenchmarkComparisonCaseStatus.Added.ToString());
        var removed = caseItems.Count(item => ReadStringProperty(item, "status") == BenchmarkComparisonCaseStatus.Removed.ToString());
        var regressions = signalItems.Count(item => ReadStringProperty(item, "severity") == BenchmarkComparisonSignalSeverity.Regression.ToString());
        var improvements = signalItems.Count(item => ReadStringProperty(item, "severity") == BenchmarkComparisonSignalSeverity.Improvement.ToString());

        AddExpectedIntegerMessage("benchmark comparison", "matchedCaseCount", matched, root, messages);
        AddExpectedIntegerMessage("benchmark comparison", "addedCaseCount", added, root, messages);
        AddExpectedIntegerMessage("benchmark comparison", "removedCaseCount", removed, root, messages);
        AddExpectedIntegerMessage("benchmark comparison", "regressionCount", regressions, root, messages);
        AddExpectedIntegerMessage("benchmark comparison", "improvementCount", improvements, root, messages);

        for (var index = 0; index < caseItems.Length; index++)
        {
            var item = caseItems[index];
            if (string.IsNullOrWhiteSpace(ReadStringProperty(item, "fixtureId")))
            {
                messages.Add(new ArtifactValidationMessage("error", $"Benchmark comparison cases[{index}] requires a fixtureId."));
            }

            var status = ReadStringProperty(item, "status");
            if (status is null || !Enum.TryParse<BenchmarkComparisonCaseStatus>(status, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"Benchmark comparison cases[{index}] has an unknown status '{status ?? "(missing)"}'."));
            }
        }

        for (var index = 0; index < signalItems.Length; index++)
        {
            var item = signalItems[index];
            if (string.IsNullOrWhiteSpace(ReadStringProperty(item, "fixtureId")))
            {
                messages.Add(new ArtifactValidationMessage("error", $"Benchmark comparison signals[{index}] requires a fixtureId."));
            }

            if (string.IsNullOrWhiteSpace(ReadStringProperty(item, "code")))
            {
                messages.Add(new ArtifactValidationMessage("error", $"Benchmark comparison signals[{index}] requires a code."));
            }

            var severity = ReadStringProperty(item, "severity");
            if (severity is null || !Enum.TryParse<BenchmarkComparisonSignalSeverity>(severity, ignoreCase: false, out _))
            {
                messages.Add(new ArtifactValidationMessage("error", $"Benchmark comparison signals[{index}] has an unknown severity '{severity ?? "(missing)"}'."));
            }
        }
    }

    private static void ValidateBatchComparison(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Batch comparison requires an items array."));
            return;
        }

        if (!root.TryGetProperty("signals", out var signals) || signals.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Batch comparison requires a signals array."));
            return;
        }

        var itemElements = items.EnumerateArray().ToArray();
        var signalElements = signals.EnumerateArray().ToArray();
        var matched = itemElements.Count(item => ReadStringProperty(item, "status") == BatchScanComparisonItemStatus.Matched.ToString());
        var added = itemElements.Count(item => ReadStringProperty(item, "status") == BatchScanComparisonItemStatus.Added.ToString());
        var removed = itemElements.Count(item => ReadStringProperty(item, "status") == BatchScanComparisonItemStatus.Removed.ToString());
        var statusChanges = itemElements.Count(HasBatchStatusChange);
        var regressions = signalElements.Count(item => ReadStringProperty(item, "severity") == BatchScanComparisonSignalSeverity.Regression.ToString());
        var improvements = signalElements.Count(item => ReadStringProperty(item, "severity") == BatchScanComparisonSignalSeverity.Improvement.ToString());
        var infos = signalElements.Count(item => ReadStringProperty(item, "severity") == BatchScanComparisonSignalSeverity.Info.ToString());

        AddExpectedIntegerMessage("batch comparison", "matchedItemCount", matched, root, messages);
        AddExpectedIntegerMessage("batch comparison", "addedItemCount", added, root, messages);
        AddExpectedIntegerMessage("batch comparison", "removedItemCount", removed, root, messages);
        AddExpectedIntegerMessage("batch comparison", "statusChangeCount", statusChanges, root, messages);
        AddExpectedIntegerMessage("batch comparison", "regressionCount", regressions, root, messages);
        AddExpectedIntegerMessage("batch comparison", "improvementCount", improvements, root, messages);
        AddExpectedIntegerMessage("batch comparison", "infoCount", infos, root, messages);

        for (var index = 0; index < itemElements.Length; index++)
        {
            var item = itemElements[index];
            var prefix = $"Batch comparison items[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                continue;
            }

            RequireNonEmptyStringProperty(item, prefix, "key", messages);
            ValidateEnumProperty<BatchScanComparisonItemStatus>(item, prefix, "status", messages);
            ValidateNullableStringProperty(item, prefix, "baselineScanJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "candidateScanJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "baselineVisualSnapshotPath", messages);
            ValidateNullableStringProperty(item, prefix, "candidateVisualSnapshotPath", messages);
            ValidateNullableStringProperty(item, prefix, "baselineGeoJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "candidateGeoJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "baselinePlacementJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "candidatePlacementJsonPath", messages);
            ValidateNullableStringProperty(item, prefix, "baselineOverlayDirectory", messages);
            ValidateNullableStringProperty(item, prefix, "candidateOverlayDirectory", messages);
            ValidateNullableEnumProperty<BatchScanItemStatus>(item, prefix, "baselineStatus", messages);
            ValidateNullableEnumProperty<BatchScanItemStatus>(item, prefix, "candidateStatus", messages);
            ValidateNullableIntegerProperty(item, prefix, "baselineDiagnosticErrors", messages);
            ValidateNullableIntegerProperty(item, prefix, "candidateDiagnosticErrors", messages);
            ValidateNullableIntegerProperty(item, prefix, "baselineVisualIssueCount", messages);
            ValidateNullableIntegerProperty(item, prefix, "candidateVisualIssueCount", messages);
            ValidateNullableIntegerProperty(item, prefix, "baselineVisualErrorIssueCount", messages);
            ValidateNullableIntegerProperty(item, prefix, "candidateVisualErrorIssueCount", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "addedVisualIssueCodes", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "removedVisualIssueCodes", messages);

            if (item.TryGetProperty("deltas", out var deltas) && deltas.ValueKind == JsonValueKind.Array)
            {
                var deltaItems = deltas.EnumerateArray().ToArray();
                for (var deltaIndex = 0; deltaIndex < deltaItems.Length; deltaIndex++)
                {
                    var delta = deltaItems[deltaIndex];
                    var deltaPrefix = $"{prefix}.deltas[{deltaIndex}]";
                    RequireNonEmptyStringProperty(delta, deltaPrefix, "name", messages);
                    RequireNonEmptyStringProperty(delta, deltaPrefix, "unit", messages);
                }
            }
            else
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array deltas."));
            }

            if (item.TryGetProperty("signals", out var itemSignals) && itemSignals.ValueKind == JsonValueKind.Array)
            {
                ValidateBatchComparisonSignals(itemSignals.EnumerateArray().ToArray(), $"{prefix}.signals", messages);
            }
            else
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires an array signals."));
            }
        }

        ValidateBatchComparisonSignals(signalElements, "Batch comparison signals", messages);
    }

    private static bool HasBatchStatusChange(JsonElement item)
    {
        if (ReadStringProperty(item, "status") != BatchScanComparisonItemStatus.Matched.ToString())
        {
            return false;
        }

        var baselineStatus = ReadStringProperty(item, "baselineStatus");
        var candidateStatus = ReadStringProperty(item, "candidateStatus");
        return !string.IsNullOrWhiteSpace(baselineStatus)
            && !string.IsNullOrWhiteSpace(candidateStatus)
            && !string.Equals(baselineStatus, candidateStatus, StringComparison.Ordinal);
    }

    private static void ValidateBatchComparisonSignals(
        IReadOnlyList<JsonElement> signals,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        for (var index = 0; index < signals.Count; index++)
        {
            var item = signals[index];
            var signalPrefix = $"{prefix}[{index}]";
            RequireNonEmptyStringProperty(item, signalPrefix, "key", messages);
            RequireNonEmptyStringProperty(item, signalPrefix, "code", messages);
            RequireNonEmptyStringProperty(item, signalPrefix, "message", messages);
            ValidateEnumProperty<BatchScanComparisonSignalSeverity>(item, signalPrefix, "severity", messages);
        }
    }

    private static void ValidateViewerBenchmarkReviewSession(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (TryReadObjectProperty(root, "manifest", "viewer benchmark review session manifest", messages, out var manifest)
            && TryReadObjectProperty(root, "summary", "viewer benchmark review session summary", messages, out var summary))
        {
            var manifestTargetCount = TryReadNonNegativeIntegerProperty(
                manifest,
                "viewer benchmark review session manifest",
                "targetCount",
                messages);
            var activeTargetCount = TryReadNonNegativeIntegerProperty(
                summary,
                "viewer benchmark review session summary",
                "activeTargetCount",
                messages);
            TryReadNonNegativeIntegerProperty(
                manifest,
                "viewer benchmark review session manifest",
                "fixtureCount",
                messages);

            foreach (var fieldName in new[]
                     {
                         "filteredTargetCount",
                         "acceptedCount",
                         "rejectedCount",
                         "needsReviewCount",
                         "unreviewedCount",
                         "missingBoundsCount",
                         "lowConfidenceCount",
                         "missingEvidenceCount"
                     })
            {
                TryReadNonNegativeIntegerProperty(
                    summary,
                    "viewer benchmark review session summary",
                    fieldName,
                    messages);
            }

            if (manifestTargetCount is not null && activeTargetCount is not null && manifestTargetCount != activeTargetCount)
            {
                messages.Add(new ArtifactValidationMessage(
                    "warning",
                    "Viewer benchmark review session manifest targetCount does not match summary activeTargetCount."));
            }

            if (TryReadObjectProperty(summary, "filters", "viewer benchmark review session filters", messages, out var filters))
            {
                foreach (var propertyName in new[] { "query", "detector", "status", "issue", "page" })
                {
                    if (ReadStringProperty(filters, propertyName) is null)
                    {
                        messages.Add(new ArtifactValidationMessage(
                            "error",
                            $"Viewer benchmark review session filters requires a string {propertyName}."));
                    }
                }
            }
        }

        if (TryReadObjectProperty(root, "scan", "viewer benchmark review session scan snapshot", messages, out var scan))
        {
            TryReadNonNegativeIntegerProperty(
                scan,
                "viewer benchmark review session scan snapshot",
                "pageCount",
                messages);
            ValidateOptionalRatioProperty(
                scan,
                "viewer benchmark review session scan snapshot",
                "qualityConfidence",
                messages);

            if (TryReadObjectProperty(scan, "diagnostics", "viewer benchmark review session scan diagnostics", messages, out var diagnostics))
            {
                foreach (var propertyName in new[]
                         {
                             "infoCount",
                             "warningCount",
                             "errorCount",
                             "stageCount"
                         })
                {
                    TryReadNonNegativeIntegerProperty(
                        diagnostics,
                        "viewer benchmark review session scan diagnostics",
                        propertyName,
                        messages);
                }
            }
        }

        var decisions = ReadArrayProperty(root, "decisions", "viewer benchmark review session", messages);
        var boundsEdits = ReadArrayProperty(root, "boundsEdits", "viewer benchmark review session", messages);
        var addedTargets = ReadArrayProperty(root, "addedTargets", "viewer benchmark review session", messages);
        var deletedTargets = ReadArrayProperty(root, "deletedTargets", "viewer benchmark review session", messages);
        var reviewIssues = ReadArrayProperty(root, "reviewIssues", "viewer benchmark review session", messages);

        if (root.TryGetProperty("summary", out var summaryForCounts) && summaryForCounts.ValueKind == JsonValueKind.Object)
        {
            AddExpectedIntegerMessage(
                "viewer benchmark review session summary",
                "addedTargetCount",
                addedTargets.Length,
                summaryForCounts,
                messages);
            AddExpectedIntegerMessage(
                "viewer benchmark review session summary",
                "deletedTargetCount",
                deletedTargets.Length,
                summaryForCounts,
                messages);
            AddExpectedIntegerMessage(
                "viewer benchmark review session summary",
                "boundsEditedCount",
                boundsEdits.Length,
                summaryForCounts,
                messages);
        }

        for (var index = 0; index < decisions.Length; index++)
        {
            ValidateReviewSessionTarget(decisions[index], $"decisions[{index}]", messages, reviewedDecisionOnly: true);
        }

        for (var index = 0; index < boundsEdits.Length; index++)
        {
            ValidateReviewSessionBoundsEdit(boundsEdits[index], $"boundsEdits[{index}]", messages);
        }

        for (var index = 0; index < addedTargets.Length; index++)
        {
            ValidateReviewSessionTarget(addedTargets[index], $"addedTargets[{index}]", messages);
            if (!addedTargets[index].TryGetProperty("manifestTarget", out var manifestTarget)
                || manifestTarget.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Viewer benchmark review session addedTargets[{index}] requires a manifestTarget object."));
            }

            if (addedTargets[index].TryGetProperty("isAdded", out var isAdded)
                && isAdded.ValueKind is JsonValueKind.False)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Viewer benchmark review session addedTargets[{index}] must have isAdded=true."));
            }
        }

        for (var index = 0; index < deletedTargets.Length; index++)
        {
            ValidateReviewSessionTarget(deletedTargets[index], $"deletedTargets[{index}]", messages);
        }

        for (var index = 0; index < reviewIssues.Length; index++)
        {
            ValidateReviewSessionTarget(reviewIssues[index], $"reviewIssues[{index}]", messages);
            if (!reviewIssues[index].TryGetProperty("flags", out var flags)
                || flags.ValueKind != JsonValueKind.Array)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Viewer benchmark review session reviewIssues[{index}] requires a flags array."));
                continue;
            }

            foreach (var flag in flags.EnumerateArray())
            {
                var flagValue = flag.ValueKind == JsonValueKind.String ? flag.GetString() : null;
                if (!IsKnownReviewSessionFlag(flagValue))
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"Viewer benchmark review session reviewIssues[{index}] has an unknown flag '{flagValue ?? "(missing)"}'."));
                }
            }
        }
    }

    private static void ValidateReviewSessionBoundsEdit(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Viewer benchmark review session {prefix} must be an object."));
            return;
        }

        RequireNonEmptyStringProperty(item, $"Viewer benchmark review session {prefix}", "reviewKey", messages);
        ValidateNullablePageNumberProperty(item, $"Viewer benchmark review session {prefix}", "pageNumber", messages);
        ValidateNullableRectProperty(item, $"Viewer benchmark review session {prefix}", "bounds", messages);

        if (item.TryGetProperty("target", out var target) && target.ValueKind != JsonValueKind.Null)
        {
            ValidateReviewSessionTarget(target, $"{prefix}.target", messages);
        }
    }

    private static void ValidateReviewSessionTarget(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages,
        bool reviewedDecisionOnly = false)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Viewer benchmark review session {prefix} must be an object."));
            return;
        }

        var display = $"Viewer benchmark review session {prefix}";
        RequireNonEmptyStringProperty(item, display, "reviewKey", messages);
        RequireNonEmptyStringProperty(item, display, "detectorKey", messages);

        var detectorKey = ReadStringProperty(item, "detectorKey");
        if (!IsKnownReviewSessionDetectorKey(detectorKey))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{display} has an unknown detectorKey '{detectorKey ?? "(missing)"}'."));
        }

        var decision = ReadStringProperty(item, "decision");
        if (!IsKnownReviewSessionDecision(decision))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{display} has an unknown decision '{decision ?? "(missing)"}'."));
        }
        else if (reviewedDecisionOnly && string.Equals(decision, "unreviewed", StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{display} is in decisions but has decision 'unreviewed'."));
        }

        ValidateNullablePageNumberProperty(item, display, "pageNumber", messages);
        ValidateNullablePageNumberProperty(item, display, "originalPageNumber", messages);
        ValidateNullableRectProperty(item, display, "bounds", messages);
        ValidateNullableRectProperty(item, display, "originalBounds", messages);
        ValidateOptionalRatioProperty(item, display, "confidence", messages);
        ValidateRequiredStringArrayProperty(item, display, "sourceLayers", messages);
        ValidateRequiredStringArrayProperty(item, display, "sourcePrimitiveIds", messages);
        ValidateRequiredStringArrayProperty(item, display, "evidence", messages);
    }

    private static bool TryReadObjectProperty(
        JsonElement root,
        string propertyName,
        string displayName,
        ICollection<ArtifactValidationMessage> messages,
        out JsonElement value)
    {
        if (!root.TryGetProperty(propertyName, out value) || value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires an object {propertyName}."));
            return false;
        }

        return true;
    }

    private static JsonElement[] ReadArrayProperty(
        JsonElement root,
        string propertyName,
        string displayName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires an array {propertyName}."));
            return Array.Empty<JsonElement>();
        }

        return property.EnumerateArray().ToArray();
    }

    private static int CountAssertions(JsonElement caseResult, bool passed)
    {
        if (!caseResult.TryGetProperty("assertions", out var assertions)
            || assertions.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var assertionItems = assertions.EnumerateArray().ToArray();
        var passedCount = assertionItems.Count(assertion => ReadBooleanProperty(assertion, "passed") == true);
        return passed
            ? passedCount
            : assertionItems.Length - passedCount;
    }

    private static int? TryReadNonNegativeIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return null;
        }

        if (!value.TryGetInt32(out var number) || number < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a non-negative integer."));
            return null;
        }

        return number;
    }

    private static int? TryReadIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return null;
        }

        if (!value.TryGetInt32(out var number))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be an integer."));
            return null;
        }

        return number;
    }

    private static int? TryReadPositiveIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return null;
        }

        if (!value.TryGetInt32(out var number) || number <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a positive integer."));
            return null;
        }

        return number;
    }

    private static void ValidatePositiveIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return;
        }

        if (!value.TryGetInt32(out var number) || number <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a positive integer."));
        }
    }

    private static void ValidatePositiveNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return;
        }

        if (!value.TryGetDouble(out var number) || number <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a positive number."));
        }
    }

    private static void ValidateBooleanProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a boolean."));
        }
    }

    private static TEnum? ValidateEnumProperty<TEnum>(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
        where TEnum : struct, Enum
    {
        var value = ReadStringProperty(root, propertyName);
        if (string.IsNullOrWhiteSpace(value)
            || !Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} has an unknown value '{value ?? "(missing)"}'."));
            return null;
        }

        return parsed;
    }

    private static TEnum? ValidateNullableEnumProperty<TEnum>(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
        where TEnum : struct, Enum
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a string or null."));
            return null;
        }

        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text)
            || !Enum.TryParse<TEnum>(text, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} has an unknown value '{text ?? "(missing)"}'."));
            return null;
        }

        return parsed;
    }

    private static int? ValidateNullableIntegerProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be an integer or null."));
            return null;
        }

        return number;
    }

    private static string? ValidateNullableStringProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a string or null."));
            return null;
        }

        return value.GetString();
    }

    private static void ValidateRequiredNullableStringProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires string or null {propertyName}."));
            return;
        }

        if (value.ValueKind is not JsonValueKind.String and not JsonValueKind.Null)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a string or null."));
        }
    }

    private static void ValidateRequiredNullableLineProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires object or null {propertyName}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be an object or null."));
            return;
        }

        ValidatePlacementPointProperty(value, $"{displayName} {propertyName}", "start", messages);
        ValidatePlacementPointProperty(value, $"{displayName} {propertyName}", "end", messages);
    }

    private static void ValidateRequiredNullableRectProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires bounds object or null {propertyName}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a bounds object or null."));
            return;
        }

        foreach (var coordinate in new[] { "x", "y", "width", "height" })
        {
            if (!value.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }
    }

    private static void ValidateRequiredNullablePointProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires point object or null {propertyName}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a point object or null."));
            return;
        }

        foreach (var coordinate in new[] { "x", "y" })
        {
            if (!value.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }
    }

    private static void ValidateNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return;
        }

        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a finite number."));
        }
    }

    private static void ValidateRequiredNullableNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric or null {propertyName}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a finite number or null."));
        }
    }

    private static void ValidateRequiredNullableNonNegativeNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires non-negative numeric or null {propertyName}."));
            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number)
            || number < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a non-negative finite number or null."));
        }
    }

    private static void ValidateNonNegativeNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires numeric {propertyName}."));
            return;
        }

        if (!value.TryGetDouble(out var number) || number < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a non-negative number."));
        }
    }

    private static void RequireNonEmptyStringProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(ReadStringProperty(root, propertyName)))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires a non-empty {propertyName}."));
        }
    }

    private static void RequireConstStringProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        string expected,
        ICollection<ArtifactValidationMessage> messages)
    {
        var actual = ReadStringProperty(root, propertyName);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be '{expected}'."));
        }
    }

    private static void RequireHash64Property(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        var value = ReadStringProperty(root, propertyName);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length != 16
            || value.Any(character => !Uri.IsHexDigit(character))
            || value.Any(char.IsUpper))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires lowercase 16-character hex {propertyName}."));
        }
    }

    private static void ValidateRequiredStringArrayProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires an array {propertyName}."));
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}[{index}] must be a string."));
            }

            index++;
        }
    }

    private static void ValidateOptionalStringArrayProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be an array when present."));
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}[{index}] must be a string."));
            }

            index++;
        }
    }

    private static void ValidateOptionalProvenanceCountArrayProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be an array when present."));
            return;
        }

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            var prefix = $"{displayName} {propertyName}[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                index++;
                continue;
            }

            RequireNonEmptyStringProperty(item, prefix, "value", messages);
            if (!item.TryGetProperty("count", out var count)
                || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt32(out var number)
                || number < 0)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires a non-negative integer count."));
            }

            index++;
        }
    }

    private static bool ValidateRequiredStringMapProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages,
        out JsonElement value)
    {
        if (!root.TryGetProperty(propertyName, out value) || value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires an object {propertyName}."));
            return false;
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}['{property.Name}'] must be a string."));
            }
        }

        return true;
    }

    private static void ValidateRequiredNumberArrayProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        int expectedCount,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} requires an array {propertyName}."));
            return;
        }

        var items = value.EnumerateArray().ToArray();
        if (items.Length != expectedCount)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must contain exactly {expectedCount} numbers."));
        }

        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ValueKind != JsonValueKind.Number
                || !items[index].TryGetDouble(out var number)
                || !double.IsFinite(number))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}[{index}] must be a finite number."));
            }
        }
    }

    private static void ValidateNullablePageNumberProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var pageNumber)
            || pageNumber < 1)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be null or an integer page number greater than 0."));
        }
    }

    private static void ValidateNullableRectProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be null or a bounds object."));
            return;
        }

        foreach (var coordinate in new[] { "x", "y", "width", "height" })
        {
            if (!value.TryGetProperty(coordinate, out var coordinateValue)
                || coordinateValue.ValueKind != JsonValueKind.Number)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be numeric."));
            }
        }

        foreach (var coordinate in new[] { "width", "height" })
        {
            if (value.TryGetProperty(coordinate, out var coordinateValue)
                && coordinateValue.ValueKind == JsonValueKind.Number
                && coordinateValue.TryGetDouble(out var number)
                && number < 0)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{displayName} {propertyName}.{coordinate} must be non-negative."));
            }
        }
    }

    private static void ValidateOptionalRatioProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || number is < 0 or > 1)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be null or a number between 0 and 1."));
        }
    }

    private static void ValidateRequiredRatioProperty(
        JsonElement root,
        string displayName,
        string propertyName,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || number is < 0 or > 1)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{displayName} {propertyName} must be a number between 0 and 1."));
        }
    }

    private static bool IsKnownReviewSessionDetectorKey(string? value) =>
        value is "regionMetrics"
            or "dimensionMetrics"
            or "annotationMetrics"
            or "annotationReferenceMetrics"
            or "gridAxisMetrics"
            or "wallMetrics"
            or "roomMetrics"
            or "openingMetrics"
            or "objectMetrics"
            or "objectGroupMetrics"
            or "objectAggregateMetrics"
            or "routingBarrierMetrics"
            or "routingPassageMetrics"
            or "routingObstacleMetrics"
            or "routingRoomUseHintMetrics"
            or "routingSuppressedObjectMetrics"
            or "layerMetrics";

    private static bool IsKnownReviewSessionDecision(string? value) =>
        value is "accepted"
            or "rejected"
            or "needsReview"
            or "unreviewed";

    private static bool IsKnownReviewSessionFlag(string? value) =>
        value is "missing_bounds"
            or "low_confidence"
            or "missing_evidence"
            or "unreviewed"
            or "rejected"
            or "needs_review"
            or "added"
            or "bounds_edited";

    private static void ValidateBatchManifest(
        BatchScanManifest manifest,
        ICollection<ArtifactValidationMessage> messages)
    {
        var inputs = manifest.Inputs ?? Array.Empty<string>();
        if (inputs.Count == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Batch manifest requires at least one input path."));
        }

        for (var index = 0; index < inputs.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(inputs[index]))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Batch manifest input #{index + 1} must be non-empty."));
            }
        }

        ValidateStringList("layerProfiles", manifest.LayerProfiles, messages);
        ValidateStringList("objectLabelProfiles", manifest.ObjectLabelProfiles, messages);
        AddPositiveMessage("batch manifest", "maxDegreeOfParallelism", manifest.MaxDegreeOfParallelism, messages);
        AddNonNegativeMessage("batch manifest", "retryCount", manifest.RetryCount, messages);

        var overrides = manifest.LayerCategoryOverrides ?? Array.Empty<LayerCategoryOverride>();
        for (var index = 0; index < overrides.Count; index++)
        {
            var layerOverride = overrides[index];
            if (string.IsNullOrWhiteSpace(layerOverride.Pattern))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Batch manifest layerCategoryOverrides #{index + 1} requires a non-empty pattern."));
            }
        }

        if (manifest.ScannerOptions is not null)
        {
            ValidateBatchScannerOptions(manifest.ScannerOptions, messages);
        }
    }

    private static void ValidateStringList(
        string propertyName,
        IReadOnlyList<string>? values,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (values is null)
        {
            return;
        }

        for (var index = 0; index < values.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(values[index]))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Batch manifest {propertyName} #{index + 1} must be non-empty."));
            }
        }
    }

    private static void ValidateBatchScannerOptions(
        BatchScannerOptions options,
        ICollection<ArtifactValidationMessage> messages)
    {
        AddNonNegativeMessage("batch scannerOptions", "sheetMargin", options.SheetMargin, messages);
        AddNonNegativeMessage("batch scannerOptions", "minWallLength", options.MinWallLength, messages);
        AddNonNegativeMessage("batch scannerOptions", "minWallFragmentLength", options.MinWallFragmentLength, messages);
        AddNonNegativeMessage("batch scannerOptions", "maxWallFragmentGap", options.MaxWallFragmentGap, messages);
        AddNonNegativeMessage("batch scannerOptions", "maxWallCandidateSeedsPerPage", options.MaxWallCandidateSeedsPerPage, messages);
        AddNonNegativeMessage("batch scannerOptions", "wallMergeTolerance", options.WallMergeTolerance, messages);
        AddNonNegativeMessage("batch scannerOptions", "wallSnapTolerance", options.WallSnapTolerance, messages);
        AddNonNegativeMessage("batch scannerOptions", "wallThickness", options.WallThickness, messages);
        AddNonNegativeMessage("batch scannerOptions", "minOpeningGap", options.MinOpeningGap, messages);
        AddNonNegativeMessage("batch scannerOptions", "maxOpeningGap", options.MaxOpeningGap, messages);
        AddNonNegativeMessage("batch scannerOptions", "objectNearbyTextSearchRadius", options.ObjectNearbyTextSearchRadius, messages);
        AddNonNegativeMessage("batch scannerOptions", "maxNearbyTextPerObject", options.MaxNearbyTextPerObject, messages);
    }

    private static void ValidateBatchResult(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        ValidatePositiveIntegerProperty(root, "Batch result", "maxDegreeOfParallelism", messages);
        TryReadNonNegativeIntegerProperty(root, "Batch result", "retryCount", messages);

        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "Batch result requires an array items."));
            return;
        }

        var seenItemNumbers = new HashSet<int>();
        var itemElements = items.EnumerateArray().ToArray();
        for (var index = 0; index < itemElements.Length; index++)
        {
            var item = itemElements[index];
            var prefix = $"Batch result items[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                continue;
            }

            var itemNumber = TryReadPositiveIntegerProperty(item, prefix, "itemNumber", messages);
            if (itemNumber is not null && !seenItemNumbers.Add(itemNumber.Value))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} itemNumber must be unique."));
            }

            RequireNonEmptyStringProperty(item, prefix, "inputPath", messages);
            ValidateEnumProperty<PlanSourceKind>(item, prefix, "sourceKind", messages);
            ValidateEnumProperty<PlanSourceKind>(item, prefix, "effectiveSourceKind", messages);
            var status = ValidateEnumProperty<BatchScanItemStatus>(item, prefix, "status", messages);
            var attemptCount = TryReadNonNegativeIntegerProperty(item, prefix, "attemptCount", messages);
            ValidateNonNegativeNumberProperty(item, prefix, "durationMilliseconds", messages);

            if (!item.TryGetProperty("counts", out var counts) || counts.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires object counts."));
                counts = default;
            }
            else
            {
                ValidateBatchResultCounts(counts, $"{prefix}.counts", messages);
            }

            if (!item.TryGetProperty("visualSnapshot", out var visualSnapshot)
                || visualSnapshot.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires object visualSnapshot."));
                visualSnapshot = default;
            }
            else
            {
                ValidateBatchVisualSnapshotSummary(visualSnapshot, $"{prefix}.visualSnapshot", messages);
            }

            if (status is BatchScanItemStatus.Succeeded or BatchScanItemStatus.CompletedWithErrors)
            {
                RequireNonEmptyStringProperty(item, prefix, "scanJsonPath", messages);
                RequireNonEmptyStringProperty(item, prefix, "visualSnapshotPath", messages);
                if (attemptCount is 0)
                {
                    messages.Add(new ArtifactValidationMessage("error", $"{prefix} attemptCount must be greater than 0 for scanned items."));
                }

                var pageCount = counts.ValueKind == JsonValueKind.Object
                    ? TryReadNonNegativeIntegerProperty(counts, $"{prefix}.counts", "pages", messages)
                    : null;
                var visualPageCount = visualSnapshot.ValueKind == JsonValueKind.Object
                    ? TryReadNonNegativeIntegerProperty(visualSnapshot, $"{prefix}.visualSnapshot", "pageCount", messages)
                    : null;
                if (pageCount is not null
                    && visualPageCount is not null
                    && pageCount.Value != visualPageCount.Value)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} visualSnapshot.pageCount must match counts.pages."));
                }
            }
            else if (status is not null)
            {
                RequireNonEmptyStringProperty(item, prefix, "errorMessage", messages);
            }
        }
    }

    private static void ValidateBatchResultCounts(
        JsonElement counts,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        foreach (var propertyName in new[]
                 {
                     "pages",
                     "regions",
                     "titleBlocks",
                     "dimensions",
                     "annotations",
                     "gridAxes",
                     "gridBaySpacings",
                     "walls",
                     "wallNodes",
                     "wallEdges",
                     "rooms",
                     "roomAdjacencies",
                     "roomClusters",
                     "openings",
                     "objects",
                     "objectGroups",
                     "objectAggregates",
                     "routingItems",
                     "diagnostics",
                     "diagnosticWarnings",
                     "diagnosticErrors"
                 })
        {
            TryReadNonNegativeIntegerProperty(counts, prefix, propertyName, messages);
        }

        RequireNonEmptyStringProperty(counts, prefix, "qualityGrade", messages);
        ValidateRequiredRatioProperty(counts, prefix, "qualityConfidence", messages);
        if (!counts.TryGetProperty("requiresReview", out var requiresReview)
            || requiresReview.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            messages.Add(new ArtifactValidationMessage("error", $"{prefix} requires boolean requiresReview."));
        }
    }

    private static void ValidateBatchVisualSnapshotSummary(
        JsonElement summary,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var schemaVersion = ReadStringProperty(summary, "schemaVersion");
        RequireNonEmptyStringProperty(summary, prefix, "schemaVersion", messages);
        if (schemaVersion is not "-"
            && !string.Equals(schemaVersion, PlanOverlaySnapshot.CurrentSchemaVersion, StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} schemaVersion must be '-' or {PlanOverlaySnapshot.CurrentSchemaVersion}."));
        }

        TryReadNonNegativeIntegerProperty(summary, prefix, "pageCount", messages);
        TryReadNonNegativeIntegerProperty(summary, prefix, "layerCount", messages);
        TryReadNonNegativeIntegerProperty(summary, prefix, "drawableItemCount", messages);
        var issueCount = TryReadNonNegativeIntegerProperty(summary, prefix, "issueCount", messages);
        var warningIssueCount = TryReadNonNegativeIntegerProperty(summary, prefix, "warningIssueCount", messages);
        var errorIssueCount = TryReadNonNegativeIntegerProperty(summary, prefix, "errorIssueCount", messages);
        ValidateRequiredRatioProperty(summary, prefix, "maxDetectionCoverage", messages);
        ValidateRequiredStringArrayProperty(summary, prefix, "issueCodes", messages);

        if (issueCount is not null
            && warningIssueCount is not null
            && errorIssueCount is not null
            && warningIssueCount.Value + errorIssueCount.Value > issueCount.Value)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} warningIssueCount + errorIssueCount cannot exceed issueCount."));
        }
    }

    private static void ValidateDeepArtifact(
        string inputPath,
        string kind,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (kind == "batch-result")
        {
            ValidateBatchResultReferences(inputPath, root, messages);
            return;
        }

        if (kind == "batch-comparison")
        {
            ValidateBatchComparisonReferences(inputPath, root, messages);
            return;
        }

        if (kind == "placement")
        {
            ValidateDeepPlacementExport(root, messages);
            return;
        }

        if (kind == "scan")
        {
            ValidateDeepScanExport(root, messages);
            return;
        }

        messages.Add(new ArtifactValidationMessage(
            "info",
            $"Deep validation has no extra checks for artifact kind '{kind}'."));
    }

    private static void ValidateDeepScanExport(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!TryReadObjectProperty(root, "diagnostics", "Scan export", messages, out var diagnostics))
        {
            return;
        }

        var expectedCounts = BuildScanArtifactExpectedCounts(root, diagnostics);
        var inventory = ReadArrayProperty(diagnostics, "artifactInventory", "Scan diagnostics", messages);
        var availableArtifactCount = TryReadNonNegativeIntegerProperty(diagnostics, "Scan diagnostics", "availableArtifactCount", messages);
        var emptyArtifactCount = TryReadNonNegativeIntegerProperty(diagnostics, "Scan diagnostics", "emptyArtifactCount", messages);
        var importCriticalArtifactCount = TryReadNonNegativeIntegerProperty(diagnostics, "Scan diagnostics", "importCriticalArtifactCount", messages);
        var missingImportCriticalArtifactCount = TryReadNonNegativeIntegerProperty(diagnostics, "Scan diagnostics", "missingImportCriticalArtifactCount", messages);

        var seenArtifacts = new HashSet<string>(StringComparer.Ordinal);
        var inventoryCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var presentCount = 0;
        var emptyCount = 0;
        var criticalCount = 0;
        var missingCriticalCount = 0;

        for (var index = 0; index < inventory.Length; index++)
        {
            var item = inventory[index];
            var prefix = $"Scan diagnostics.artifactInventory[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"{prefix} must be an object."));
                continue;
            }

            RequireNonEmptyStringProperty(item, prefix, "artifact", messages);
            RequireNonEmptyStringProperty(item, prefix, "importance", messages);
            RequireNonEmptyStringProperty(item, prefix, "readinessImpact", messages);
            ValidateBooleanProperty(item, prefix, "isPresent", messages);
            ValidateBooleanProperty(item, prefix, "isSourceArtifact", messages);
            ValidateBooleanProperty(item, prefix, "isImportCritical", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "producerStages", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "consumerStages", messages);
            ValidateRequiredStringArrayProperty(item, prefix, "evidence", messages);

            var artifact = ReadStringProperty(item, "artifact");
            var count = TryReadNonNegativeIntegerProperty(item, prefix, "count", messages);
            var isPresent = ReadBooleanProperty(item, "isPresent");
            var isImportCritical = ReadBooleanProperty(item, "isImportCritical");
            if (string.IsNullOrWhiteSpace(artifact) || count is null)
            {
                continue;
            }

            if (!seenArtifacts.Add(artifact))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} duplicates artifact '{artifact}'."));
            }

            inventoryCounts[artifact] = count.Value;
            if (isPresent == true)
            {
                presentCount++;
            }
            else
            {
                emptyCount++;
            }

            if (isImportCritical == true)
            {
                criticalCount++;
                if (isPresent != true)
                {
                    missingCriticalCount++;
                }
            }

            if (isPresent is not null && isPresent.Value != (count.Value > 0))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} isPresent must match whether count is greater than 0."));
            }

            if (expectedCounts.TryGetValue(artifact, out var expected) && expected != count.Value)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} artifact '{artifact}' count should be {expected} based on exported scan content, got {count.Value}."));
            }
        }

        foreach (var expected in expectedCounts.Where(item => item.Value > 0))
        {
            if (!inventoryCounts.ContainsKey(expected.Key))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"Scan diagnostics.artifactInventory is missing artifact '{expected.Key}' with expected count {expected.Value}."));
            }
        }

        AddExpectedInventoryTotalMessage("availableArtifactCount", availableArtifactCount, presentCount, messages);
        AddExpectedInventoryTotalMessage("emptyArtifactCount", emptyArtifactCount, emptyCount, messages);
        AddExpectedInventoryTotalMessage("importCriticalArtifactCount", importCriticalArtifactCount, criticalCount, messages);
        AddExpectedInventoryTotalMessage("missingImportCriticalArtifactCount", missingImportCriticalArtifactCount, missingCriticalCount, messages);

        messages.Add(new ArtifactValidationMessage(
            "info",
            "Scan deep validation checked final artifact inventory totals, required inventory item fields, present/empty flags, duplicate artifacts, and exported-content count consistency."));
    }

    private static IReadOnlyDictionary<string, int> BuildScanArtifactExpectedCounts(
        JsonElement root,
        JsonElement diagnostics)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["Document"] = TryGetObject(root, "document", out _) ? 1 : 0,
            ["Pages"] = CountArray(root, "pages"),
            ["Primitives"] = SumArrayIntegerProperty(root, "pages", "primitiveCount", CountArray(root, "primitiveSources")),
            ["Layers"] = CountNestedArray(root, "layerAnalysis", "layers"),
            ["SheetRegions"] = CountArray(root, "regions"),
            ["TitleBlocks"] = CountArray(root, "titleBlocks"),
            ["Calibration"] = CountNestedArray(root, "calibration", "evidence")
                + CountNestedArray(root, "calibration", "scaleGroups"),
            ["Dimensions"] = CountArray(root, "dimensions"),
            ["Annotations"] = CountArray(root, "annotations") + CountChildArrays(root, "annotations", "items"),
            ["GridAxes"] = CountArray(root, "gridAxes"),
            ["GridBays"] = CountArray(root, "gridBaySpacings"),
            ["MeasurementConsistency"] = CountNestedArray(root, "measurementConsistency", "checks"),
            ["DimensionChains"] = CountDiagnosticsByStage(diagnostics, "dimension-chains"),
            ["SurfacePatterns"] = CountArray(root, "surfacePatterns"),
            ["Walls"] = CountArray(root, "walls"),
            ["WallGraph"] = CountNestedArray(root, "wallGraph", "nodes")
                + CountNestedArray(root, "wallGraph", "edges")
                + CountNestedArray(root, "wallGraph", "components")
                + CountNestedArray(root, "wallGraph", "repairCandidates"),
            ["TopologySpans"] = CountNestedArray(root, "wallGraph", "edges"),
            ["Openings"] = CountArray(root, "openings"),
            ["Rooms"] = CountArray(root, "rooms"),
            ["RoomAdjacency"] = CountNestedArray(root, "roomAdjacencyGraph", "edges")
                + CountNestedArray(root, "roomAdjacencyGraph", "clusters"),
            ["ObjectCandidates"] = CountArray(root, "objects"),
            ["ObjectGroups"] = CountArray(root, "objectGroups"),
            ["ObjectAggregates"] = CountArray(root, "objectAggregates"),
            ["RoutingBarriers"] = CountNestedArray(root, "routingLayer", "barriers"),
            ["RoutingPassages"] = CountNestedArray(root, "routingLayer", "passages"),
            ["RoutingObstacles"] = CountNestedArray(root, "routingLayer", "obstacles"),
            ["RoutingRoomUseHints"] = CountNestedArray(root, "routingLayer", "roomUseHints"),
            ["RoutingSuppressedObjects"] = CountNestedArray(root, "routingLayer", "suppressedObjects"),
            ["RoutingIgnoredObjects"] = CountNestedArray(root, "routingLayer", "ignoredObjects"),
            ["VisualAiClassifications"] = CountObjectsWithObjectProperty(root, "objects", "visualAi")
                + CountObjectsWithObjectProperty(root, "objectGroups", "visualAi"),
            ["LayerConsistency"] = CountDiagnosticsByStage(diagnostics, "layer-consistency"),
            ["Diagnostics"] = CountArray(diagnostics, "messages")
        };

        return counts;
    }

    private static void AddExpectedInventoryTotalMessage(
        string propertyName,
        int? actual,
        int expected,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (actual is not null && actual.Value != expected)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Scan diagnostics {propertyName} should be {expected} based on artifactInventory."));
        }
    }

    private static bool TryGetObject(JsonElement root, string propertyName, out JsonElement value) =>
        root.TryGetProperty(propertyName, out value) && value.ValueKind == JsonValueKind.Object;

    private static int CountArray(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.Array
            ? property.GetArrayLength()
            : 0;

    private static int CountNestedArray(JsonElement root, string objectPropertyName, string arrayPropertyName) =>
        TryGetObject(root, objectPropertyName, out var item)
            ? CountArray(item, arrayPropertyName)
            : 0;

    private static int CountChildArrays(JsonElement root, string arrayPropertyName, string childArrayPropertyName)
    {
        if (!root.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return array.EnumerateArray().Sum(item => CountArray(item, childArrayPropertyName));
    }

    private static int SumArrayIntegerProperty(
        JsonElement root,
        string arrayPropertyName,
        string integerPropertyName,
        int fallback)
    {
        if (!root.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return fallback;
        }

        var total = 0;
        var found = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty(integerPropertyName, out var property)
                || property.ValueKind != JsonValueKind.Number
                || !property.TryGetInt32(out var value)
                || value < 0)
            {
                continue;
            }

            total += value;
            found = true;
        }

        return found ? total : fallback;
    }

    private static int CountObjectsWithObjectProperty(
        JsonElement root,
        string arrayPropertyName,
        string objectPropertyName)
    {
        if (!root.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return array
            .EnumerateArray()
            .Count(item => item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty(objectPropertyName, out var property)
                && property.ValueKind == JsonValueKind.Object);
    }

    private static int CountDiagnosticsByStage(JsonElement diagnostics, string stage)
    {
        if (!diagnostics.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return messages
            .EnumerateArray()
            .Count(message => message.ValueKind == JsonValueKind.Object
                && string.Equals(ReadStringProperty(message, "stage"), stage, StringComparison.Ordinal));
    }

    private static void ValidateDeepPlacementExport(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        var pages = BuildPlacementPageMap(root, messages);
        if (pages.Count == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement deep validation requires at least one page."));
            return;
        }

        ValidateDeepPlacementCoordinateFrames(root, pages, messages);

        var wallIds = ValidateDeepPlacementEntities(
            root,
            "walls",
            "wall",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementWall(item, prefix, messages));
        var roomIds = ValidateDeepPlacementEntities(
            root,
            "rooms",
            "room",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementRoom(item, prefix, wallIds, messages));
        var objectAggregateIds = ValidateDeepPlacementEntities(
            root,
            "objectAggregates",
            "object aggregate",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementObjectAggregate(item, prefix, messages));
        var openingIds = ValidateDeepPlacementEntities(
            root,
            "openings",
            "opening",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementOpening(item, prefix, wallIds, roomIds, messages));

        ValidateDeepPlacementRoutingLayer(root, pages, wallIds, roomIds, openingIds, objectAggregateIds, messages);
        ValidateDeepPlacementQualityGate(root, messages);
        ValidateDeepPlacementIssueReferences(root, wallIds, messages);

        messages.Add(new ArtifactValidationMessage(
            "info",
            "Placement deep validation checked coordinate frames, page references, positive geometry, page containment, metric coordinate consistency, wall lengths, opening anchors, placement offsets, routing references, suppression links, and issue-to-entity references."));
    }

    private static void ValidateDeepPlacementIssueReferences(
        JsonElement root,
        IReadOnlySet<string> wallIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var wallLookup = BuildDeepPlacementEntityLookup(root, "walls");
        var issueItems = issues.EnumerateArray().ToArray();
        for (var index = 0; index < issueItems.Length; index++)
        {
            var issue = issueItems[index];
            var prefix = $"Placement issues[{index}]";
            if (issue.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var code = ReadStringProperty(issue, "code");
            if (!string.Equals(code, "placement.review.surface_pattern_wall_overlap", StringComparison.Ordinal))
            {
                continue;
            }

            ValidateDeepSurfacePatternWallOverlapIssue(issue, prefix, wallIds, wallLookup, messages);
        }
    }

    private static void ValidateDeepSurfacePatternWallOverlapIssue(
        JsonElement issue,
        string prefix,
        IReadOnlySet<string> wallIds,
        IReadOnlyDictionary<string, JsonElement> wallLookup,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!issue.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} surface-pattern wall overlap issue requires object properties."));
            return;
        }

        var wallId = ReadStringProperty(properties, "wallId");
        if (string.IsNullOrWhiteSpace(wallId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} surface-pattern wall overlap issue requires properties.wallId."));
            return;
        }

        if (!wallIds.Contains(wallId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} surface-pattern wall overlap issue references missing wall '{wallId}'."));
            return;
        }

        var surfacePatternId = ReadStringProperty(properties, "surfacePatternId");
        if (string.IsNullOrWhiteSpace(surfacePatternId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} surface-pattern wall overlap issue requires properties.surfacePatternId."));
        }

        var wallOverlapRatio = ReadRatioPropertyForDeep(properties, "wallOverlapRatio");
        if (wallOverlapRatio is null || wallOverlapRatio.Value < 0 || wallOverlapRatio.Value > 1)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} surface-pattern wall overlap issue requires properties.wallOverlapRatio between 0 and 1."));
        }

        if (!wallLookup.TryGetValue(wallId, out var wall)
            || !wall.TryGetProperty("reliability", out var reliability)
            || reliability.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} referenced wall '{wallId}' must include reliability."));
            return;
        }

        if (ReadBooleanProperty(reliability, "requiresReview") != true)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} referenced wall '{wallId}' reliability.requiresReview must be true."));
        }

        var reasons = ReadStringArrayForDeep(reliability, "reasons").ToArray();
        var hasSurfacePatternReason = reasons.Any(reason =>
            reason.Contains("surface/detail pattern", StringComparison.OrdinalIgnoreCase)
            && (string.IsNullOrWhiteSpace(surfacePatternId) || reason.Contains(surfacePatternId, StringComparison.Ordinal)));
        if (!hasSurfacePatternReason)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} referenced wall '{wallId}' reliability.reasons must include the surface/detail pattern overlap evidence."));
        }
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildDeepPlacementEntityLookup(
        JsonElement root,
        string propertyName)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (!root.TryGetProperty(propertyName, out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "id");
            if (!string.IsNullOrWhiteSpace(id) && !result.ContainsKey(id))
            {
                result[id] = item;
            }
        }

        return result;
    }

    private static double? ReadRatioPropertyForDeep(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var number)
            && double.IsFinite(number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed)
            && double.IsFinite(parsed))
        {
            return parsed;
        }

        return null;
    }

    private static IReadOnlyDictionary<int, PlacementValidationPage> BuildPlacementPageMap(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        var result = new Dictionary<int, PlacementValidationPage>();
        var pages = ReadArrayProperty(root, "pages", "Placement export", messages);
        for (var index = 0; index < pages.Length; index++)
        {
            var page = pages[index];
            var prefix = $"Placement pages[{index}]";
            if (page.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var pageNumber = ReadPositiveIntegerForDeep(page, "pageNumber");
            var width = ReadPositiveDoubleForDeep(page, "width");
            var height = ReadPositiveDoubleForDeep(page, "height");
            if (pageNumber is null || width is null || height is null)
            {
                continue;
            }

            if (!result.TryAdd(pageNumber.Value, new PlacementValidationPage(pageNumber.Value, width.Value, height.Value)))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} duplicates pageNumber {pageNumber.Value}."));
            }

            if (TryReadValidationRect(page, "bounds", out var bounds))
            {
                if (bounds.Width <= 0 || bounds.Height <= 0)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} bounds width and height must be positive."));
                }

                if (Math.Abs(bounds.X) > PlacementValidationTolerance
                    || Math.Abs(bounds.Y) > PlacementValidationTolerance
                    || Math.Abs(bounds.Width - width.Value) > PlacementValidationTolerance
                    || Math.Abs(bounds.Height - height.Value) > PlacementValidationTolerance)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} bounds must match page origin and size."));
                }
            }
        }

        return result;
    }

    private static void ValidateDeepPlacementCoordinateFrames(
        JsonElement root,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("coordinateSystem", out var coordinateSystem)
            || coordinateSystem.ValueKind != JsonValueKind.Object
            || !coordinateSystem.TryGetProperty("pageFrames", out var pageFrames)
            || pageFrames.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var seenFrames = new HashSet<int>();
        var frameItems = pageFrames.EnumerateArray().ToArray();
        for (var index = 0; index < frameItems.Length; index++)
        {
            var frame = frameItems[index];
            var prefix = $"Placement coordinateSystem.pageFrames[{index}]";
            if (frame.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var pageNumber = ReadPositiveIntegerForDeep(frame, "pageNumber");
            var width = ReadPositiveDoubleForDeep(frame, "width");
            var height = ReadPositiveDoubleForDeep(frame, "height");
            if (pageNumber is null || width is null || height is null)
            {
                continue;
            }

            if (!seenFrames.Add(pageNumber.Value))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} duplicates pageNumber {pageNumber.Value}."));
            }

            if (!pages.TryGetValue(pageNumber.Value, out var page))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} pageNumber {pageNumber.Value} does not exist in placement pages."));
                continue;
            }

            if (Math.Abs(width.Value - page.Width) > PlacementValidationTolerance
                || Math.Abs(height.Value - page.Height) > PlacementValidationTolerance)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} width/height must match placement page {page.PageNumber}."));
            }

            if (TryReadValidationRect(frame, "bounds", out var bounds))
            {
                if (Math.Abs(bounds.X) > PlacementValidationTolerance
                    || Math.Abs(bounds.Y) > PlacementValidationTolerance
                    || Math.Abs(bounds.Width - page.Width) > PlacementValidationTolerance
                    || Math.Abs(bounds.Height - page.Height) > PlacementValidationTolerance)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} bounds must match placement page origin and size."));
                }
            }

            ValidateDeepPlacementTransform(
                frame,
                "pageToNormalizedTransform",
                new[] { SafeInverse(page.Width), 0d, 0d, SafeInverse(page.Height), 0d, 0d },
                prefix,
                messages);
            ValidateDeepPlacementTransform(
                frame,
                "normalizedToPageTransform",
                new[] { page.Width, 0d, 0d, page.Height, 0d, 0d },
                prefix,
                messages);
        }

        foreach (var missingPageNumber in pages.Keys.Where(pageNumber => !seenFrames.Contains(pageNumber)).Order())
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"Placement coordinateSystem.pageFrames is missing pageNumber {missingPageNumber}."));
        }
    }

    private static void ValidateDeepPlacementTransform(
        JsonElement root,
        string propertyName,
        IReadOnlyList<double> expected,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!TryReadValidationNumberArray(root, propertyName, expected.Count, out var actual))
        {
            return;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var tolerance = Math.Max(1e-9, Math.Abs(expected[index]) * 1e-6);
            if (Math.Abs(actual[index] - expected[index]) > tolerance)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} {propertyName}[{index}] does not match the page-frame transform implied by page size."));
            }
        }
    }

    private static HashSet<string> ValidateDeepPlacementEntities(
        JsonElement root,
        string propertyName,
        string itemName,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        ICollection<ArtifactValidationMessage> messages,
        Action<JsonElement, string> validateItem)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = ReadArrayProperty(root, propertyName, "Placement export", messages);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var prefix = $"Placement {itemName}s[{index}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "id");
            if (!string.IsNullOrWhiteSpace(id) && !ids.Add(id))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} id '{id}' is duplicated in {propertyName}."));
            }

            ValidateDeepPlacementEntityGeometry(item, prefix, pages, messages);
            validateItem(item, prefix);
        }

        return ids;
    }

    private static void ValidateDeepPlacementEntityGeometry(
        JsonElement item,
        string prefix,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        ICollection<ArtifactValidationMessage> messages)
    {
        var pageNumber = ReadPositiveIntegerForDeep(item, "pageNumber");
        var hasPage = false;
        var page = default(PlacementValidationPage);
        if (pageNumber is not null && !pages.TryGetValue(pageNumber.Value, out page))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} pageNumber {pageNumber.Value} does not exist in placement pages."));
        }
        else if (pageNumber is not null)
        {
            hasPage = true;
        }

        if (!TryReadValidationRect(item, "bounds", out var bounds))
        {
            return;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} bounds width and height must be positive."));
        }

        if (hasPage && !RectInsidePage(bounds, page))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} bounds are outside page {page.PageNumber}."));
        }
    }

    private static void ValidateDeepPlacementWall(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!TryReadValidationLine(item, "centerLine", out var centerLine))
        {
            return;
        }

        var lineLength = centerLine.Length;
        if (lineLength <= PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} centerLine must have positive length."));
        }

        var drawingLength = ReadNonNegativeDoubleForDeep(item, "drawingLength");
        if (drawingLength is not null && Math.Abs(drawingLength.Value - lineLength) > Math.Max(0.25, lineLength * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{prefix} drawingLength differs from centerLine length."));
        }

        ValidateDeepPlacementMetricLine(item, "centerLine", "centerLineMillimeters", prefix, messages);
        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementRoom(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> wallIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.TryGetProperty("boundary", out var boundary) && boundary.ValueKind == JsonValueKind.Array)
        {
            var validPointCount = boundary.EnumerateArray().Count(point => TryReadValidationPoint(point, out _));
            if (validPointCount > 0 && validPointCount < 3)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} boundary must contain at least three valid points when present."));
            }
        }

        foreach (var wallId in ReadStringArrayForDeep(item, "wallIds"))
        {
            if (!wallIds.Contains(wallId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} wallIds references missing wall '{wallId}'."));
            }
        }

        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
        ValidateDeepPlacementMetricPoint(item, "center", "centerMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementObjectAggregate(
        JsonElement item,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (item.TryGetProperty("childObjectIds", out var childObjectIds) && childObjectIds.ValueKind == JsonValueKind.Array)
        {
            var actualCount = childObjectIds.EnumerateArray().Count(child => child.ValueKind == JsonValueKind.String);
            var declaredCount = ReadNonNegativeIntegerForDeep(item, "childObjectCount");
            if (declaredCount is not null && declaredCount.Value != actualCount)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} childObjectCount must match childObjectIds length."));
            }
        }

        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
        ValidateDeepPlacementMetricPoint(item, "center", "centerMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementOpening(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> wallIds,
        IReadOnlySet<string> roomIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var hostWallIds = ReadStringArrayForDeep(item, "hostWallIds").ToArray();
        foreach (var wallId in hostWallIds)
        {
            if (!wallIds.Contains(wallId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} hostWallIds references missing wall '{wallId}'."));
            }
        }

        var connectedRoomIds = ReadStringArrayForDeep(item, "connectedRoomIds").ToHashSet(StringComparer.Ordinal);
        foreach (var roomId in connectedRoomIds)
        {
            if (!roomIds.Contains(roomId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} connectedRoomIds references missing room '{roomId}'."));
            }
        }

        if (item.TryGetProperty("connectedRoomLinks", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var link in links.EnumerateArray())
            {
                var linkPrefix = $"{prefix}.connectedRoomLinks[{index}]";
                var roomId = ReadStringProperty(link, "roomId");
                if (!string.IsNullOrWhiteSpace(roomId))
                {
                    if (!roomIds.Contains(roomId))
                    {
                        messages.Add(new ArtifactValidationMessage(
                            "error",
                            $"{linkPrefix} roomId references missing room '{roomId}'."));
                    }

                    if (!connectedRoomIds.Contains(roomId))
                    {
                        messages.Add(new ArtifactValidationMessage(
                            "error",
                            $"{linkPrefix} roomId must also appear in connectedRoomIds."));
                    }
                }

                index++;
            }
        }

        var placementStatus = ReadStringProperty(item, "placementStatus");
        var hasPlacement = item.TryGetProperty("placement", out var placement)
            && placement.ValueKind == JsonValueKind.Object;
        if (placementStatus == "Anchored")
        {
            if (hostWallIds.Length == 0)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} is Anchored but hostWallIds is empty."));
            }

            if (hasPlacement)
            {
                ValidateDeepOpeningPlacement(placement, prefix, hostWallIds, wallIds, messages);
            }
        }

        ValidateDeepPlacementMetricLine(item, "centerLine", "centerLineMillimeters", prefix, messages);
        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
        ValidateDeepPlacementMetricPoint(item, "hingePoint", "hingePointMillimeters", prefix, messages);
    }

    private static void ValidateDeepOpeningPlacement(
        JsonElement placement,
        string openingPrefix,
        IReadOnlyCollection<string> openingHostWallIds,
        IReadOnlySet<string> wallIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var prefix = $"{openingPrefix}.placement";
        var hostWallId = ReadStringProperty(placement, "hostWallId");
        var anchorWallIds = ReadStringArrayForDeep(placement, "anchorWallIds").ToArray();
        if (string.IsNullOrWhiteSpace(hostWallId) && anchorWallIds.Length == 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} must include hostWallId or anchorWallIds."));
        }

        if (!string.IsNullOrWhiteSpace(hostWallId))
        {
            if (!wallIds.Contains(hostWallId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} hostWallId references missing wall '{hostWallId}'."));
            }

            if (!openingHostWallIds.Contains(hostWallId, StringComparer.Ordinal))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} hostWallId must also appear in opening hostWallIds."));
            }
        }

        foreach (var anchorWallId in anchorWallIds)
        {
            if (!wallIds.Contains(anchorWallId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} anchorWallIds references missing wall '{anchorWallId}'."));
            }
        }

        if (!TryReadValidationLine(placement, "referenceLine", out var referenceLine))
        {
            return;
        }

        if (referenceLine.Length <= PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} referenceLine must have positive length."));
            return;
        }

        var hasStartPoint = TryReadValidationPointProperty(placement, "startPoint", out var startPoint);
        var hasEndPoint = TryReadValidationPointProperty(placement, "endPoint", out var endPoint);
        var startOffset = ReadNonNegativeDoubleForDeep(placement, "startOffsetDrawingUnits");
        var endOffset = ReadNonNegativeDoubleForDeep(placement, "endOffsetDrawingUnits");
        var centerOffset = ReadNonNegativeDoubleForDeep(placement, "centerOffsetDrawingUnits");
        var length = ReadNonNegativeDoubleForDeep(placement, "lengthDrawingUnits");
        if (startOffset is null || endOffset is null || centerOffset is null || length is null)
        {
            return;
        }

        if (startOffset.Value > endOffset.Value + PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} startOffsetDrawingUnits must be less than or equal to endOffsetDrawingUnits."));
        }

        var expectedLength = endOffset.Value - startOffset.Value;
        if (Math.Abs(expectedLength - length.Value) > Math.Max(0.25, Math.Abs(expectedLength) * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} lengthDrawingUnits must match endOffsetDrawingUnits - startOffsetDrawingUnits."));
        }

        if (centerOffset.Value < startOffset.Value - PlacementValidationTolerance
            || centerOffset.Value > endOffset.Value + PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} centerOffsetDrawingUnits must lie between start and end offsets."));
        }

        var startParameter = ReadDoubleForDeep(placement, "hostWallStartParameter");
        var endParameter = ReadDoubleForDeep(placement, "hostWallEndParameter");
        var centerParameter = ReadDoubleForDeep(placement, "hostWallCenterParameter");
        ValidatePlacementParameter(startParameter, $"{prefix} hostWallStartParameter", messages);
        ValidatePlacementParameter(endParameter, $"{prefix} hostWallEndParameter", messages);
        ValidatePlacementParameter(centerParameter, $"{prefix} hostWallCenterParameter", messages);
        if (startParameter is not null && endParameter is not null && startParameter.Value > endParameter.Value + 0.01)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} hostWallStartParameter must be less than or equal to hostWallEndParameter."));
        }

        if (TryReadValidationVectorProperty(placement, "alongVector", out var alongVector)
            && TryReadValidationVectorProperty(placement, "normalVector", out var normalVector))
        {
            ValidateUnitVector(alongVector, $"{prefix} alongVector", messages);
            ValidateUnitVector(normalVector, $"{prefix} normalVector", messages);
            if (Math.Abs((alongVector.X * normalVector.X) + (alongVector.Y * normalVector.Y)) > 0.1)
            {
                messages.Add(new ArtifactValidationMessage(
                    "warning",
                    $"{prefix} alongVector and normalVector should be perpendicular."));
            }
        }

        if (hasStartPoint)
        {
            ValidateProjectedOffset(referenceLine, startPoint, startOffset.Value, $"{prefix} startPoint", messages);
        }

        if (hasEndPoint)
        {
            ValidateProjectedOffset(referenceLine, endPoint, endOffset.Value, $"{prefix} endPoint", messages);
        }

        ValidateDeepOpeningFootprint(placement, prefix, hasStartPoint ? startPoint : null, hasEndPoint ? endPoint : null, messages);
    }

    private static void ValidateDeepOpeningFootprint(
        JsonElement placement,
        string prefix,
        PlacementValidationPoint? startPoint,
        PlacementValidationPoint? endPoint,
        ICollection<ArtifactValidationMessage> messages)
    {
        var hasAnyFootprintProperty =
            placement.TryGetProperty("footprintBounds", out _)
            || placement.TryGetProperty("footprintCorners", out _)
            || placement.TryGetProperty("startJambLine", out _)
            || placement.TryGetProperty("endJambLine", out _)
            || placement.TryGetProperty("depthDrawingUnits", out _);
        if (!hasAnyFootprintProperty)
        {
            return;
        }

        if (!TryReadValidationRect(placement, "footprintBounds", out var footprintBounds))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} footprintBounds must be a valid rect when opening footprint geometry is present."));
            return;
        }

        if (footprintBounds.Width < 0 || footprintBounds.Height < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} footprintBounds width and height must be non-negative."));
        }

        if (!TryReadValidationPointArray(placement, "footprintCorners", expectedCount: 4, out var corners))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} footprintCorners must contain exactly four valid points."));
            return;
        }

        foreach (var corner in corners)
        {
            if (!PointInsideRect(corner, footprintBounds, PlacementValidationTolerance))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} footprintCorners must lie inside footprintBounds."));
                break;
            }
        }

        if (!TryReadValidationLine(placement, "startJambLine", out var startJambLine))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} startJambLine must be a valid line when opening footprint geometry is present."));
            return;
        }

        if (!TryReadValidationLine(placement, "endJambLine", out var endJambLine))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} endJambLine must be a valid line when opening footprint geometry is present."));
            return;
        }

        ValidatePointNear(startJambLine.Start, corners[0], $"{prefix} startJambLine.start", messages);
        ValidatePointNear(startJambLine.End, corners[3], $"{prefix} startJambLine.end", messages);
        ValidatePointNear(endJambLine.Start, corners[1], $"{prefix} endJambLine.start", messages);
        ValidatePointNear(endJambLine.End, corners[2], $"{prefix} endJambLine.end", messages);

        var depth = ReadNonNegativeDoubleForDeep(placement, "depthDrawingUnits");
        if (depth is null)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} depthDrawingUnits must be non-negative when opening footprint geometry is present."));
            return;
        }

        if (Math.Abs(startJambLine.Length - depth.Value) > Math.Max(0.25, Math.Max(1, depth.Value) * 0.03)
            || Math.Abs(endJambLine.Length - depth.Value) > Math.Max(0.25, Math.Max(1, depth.Value) * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} depthDrawingUnits must match start/end jamb line lengths."));
        }

        if (startPoint is { } start)
        {
            ValidatePointNear(Midpoint(startJambLine), start, $"{prefix} startJambLine midpoint", messages);
        }

        if (endPoint is { } end)
        {
            ValidatePointNear(Midpoint(endJambLine), end, $"{prefix} endJambLine midpoint", messages);
        }
    }

    private static void ValidateDeepPlacementMetricRect(
        JsonElement item,
        string drawingPropertyName,
        string metricPropertyName,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var scale = ReadPositiveDoubleForDeep(item, "millimetersPerDrawingUnit", allowNull: true);
        if (scale is null
            || !TryReadValidationRect(item, drawingPropertyName, out var drawing)
            || !TryReadValidationRect(item, metricPropertyName, out var metric))
        {
            return;
        }

        ValidateScaledValue(drawing.X, metric.X, scale.Value, $"{prefix} {metricPropertyName}.x", messages);
        ValidateScaledValue(drawing.Y, metric.Y, scale.Value, $"{prefix} {metricPropertyName}.y", messages);
        ValidateScaledValue(drawing.Width, metric.Width, scale.Value, $"{prefix} {metricPropertyName}.width", messages);
        ValidateScaledValue(drawing.Height, metric.Height, scale.Value, $"{prefix} {metricPropertyName}.height", messages);
    }

    private static void ValidateDeepPlacementMetricPoint(
        JsonElement item,
        string drawingPropertyName,
        string metricPropertyName,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var scale = ReadPositiveDoubleForDeep(item, "millimetersPerDrawingUnit", allowNull: true);
        if (scale is null
            || !TryReadValidationPointProperty(item, drawingPropertyName, out var drawing)
            || !TryReadValidationPointProperty(item, metricPropertyName, out var metric))
        {
            return;
        }

        ValidateScaledValue(drawing.X, metric.X, scale.Value, $"{prefix} {metricPropertyName}.x", messages);
        ValidateScaledValue(drawing.Y, metric.Y, scale.Value, $"{prefix} {metricPropertyName}.y", messages);
    }

    private static void ValidateDeepPlacementMetricLine(
        JsonElement item,
        string drawingPropertyName,
        string metricPropertyName,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var scale = ReadPositiveDoubleForDeep(item, "millimetersPerDrawingUnit", allowNull: true);
        if (scale is null
            || !TryReadValidationLine(item, drawingPropertyName, out var drawing)
            || !TryReadValidationLine(item, metricPropertyName, out var metric))
        {
            return;
        }

        ValidateScaledValue(drawing.Start.X, metric.Start.X, scale.Value, $"{prefix} {metricPropertyName}.start.x", messages);
        ValidateScaledValue(drawing.Start.Y, metric.Start.Y, scale.Value, $"{prefix} {metricPropertyName}.start.y", messages);
        ValidateScaledValue(drawing.End.X, metric.End.X, scale.Value, $"{prefix} {metricPropertyName}.end.x", messages);
        ValidateScaledValue(drawing.End.Y, metric.End.Y, scale.Value, $"{prefix} {metricPropertyName}.end.y", messages);
    }

    private static void ValidateScaledValue(
        double drawingValue,
        double metricValue,
        double millimetersPerDrawingUnit,
        string label,
        ICollection<ArtifactValidationMessage> messages)
    {
        var expected = drawingValue * millimetersPerDrawingUnit;
        var tolerance = Math.Max(0.5, Math.Abs(expected) * 0.0001);
        if (Math.Abs(metricValue - expected) > tolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{label} must equal drawing coordinate multiplied by millimetersPerDrawingUnit."));
        }
    }

    private static void ValidateDeepPlacementRoutingLayer(
        JsonElement root,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        IReadOnlySet<string> wallIds,
        IReadOnlySet<string> roomIds,
        IReadOnlySet<string> openingIds,
        IReadOnlySet<string> objectAggregateIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("routingLayer", out var routingLayer)
            || routingLayer.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var barrierIds = ValidateDeepPlacementRoutingEntities(
            routingLayer,
            "barriers",
            "routing barrier",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementRoutingBarrier(item, prefix, wallIds, messages));
        var passageIds = ValidateDeepPlacementRoutingEntities(
            routingLayer,
            "passages",
            "routing passage",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementRoutingPassage(item, prefix, wallIds, roomIds, openingIds, messages));
        var obstacleIds = ValidateDeepPlacementRoutingEntities(
            routingLayer,
            "obstacles",
            "routing obstacle",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementRoutingObstacle(item, prefix, roomIds, objectAggregateIds, messages));
        var roomUseHintIds = ValidateDeepPlacementRoutingEntities(
            routingLayer,
            "roomUseHints",
            "routing room-use hint",
            pages,
            messages,
            validateItem: (item, prefix) => ValidateDeepPlacementRoutingRoomUseHint(item, prefix, roomIds, objectAggregateIds, messages));

        ValidateDeepPlacementRoutingSuppressedObjects(
            routingLayer,
            pages,
            objectAggregateIds,
            obstacleIds,
            roomUseHintIds,
            messages);
        ValidateDeepPlacementRoutingIgnoredObjects(
            routingLayer,
            pages,
            objectAggregateIds,
            roomUseHintIds,
            messages);

        if (barrierIds.Count > 0 && !barrierIds.Any(id => id.StartsWith("routing-barrier:", StringComparison.Ordinal)))
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                "Placement routingLayer barriers use non-standard IDs; downstream consumers can still use them, but routing-barrier:<wallId> is preferred."));
        }

        _ = passageIds;
    }

    private static HashSet<string> ValidateDeepPlacementRoutingEntities(
        JsonElement routingLayer,
        string propertyName,
        string itemName,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        ICollection<ArtifactValidationMessage> messages,
        Action<JsonElement, string> validateItem)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var items = ReadArrayProperty(routingLayer, propertyName, "Placement routingLayer", messages);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var prefix = $"Placement routingLayer.{propertyName}[{index}] {itemName}";
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "id");
            if (!string.IsNullOrWhiteSpace(id) && !ids.Add(id))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} id '{id}' is duplicated in routingLayer.{propertyName}."));
            }

            ValidateDeepPlacementEntityGeometry(item, prefix, pages, messages);
            validateItem(item, prefix);
        }

        return ids;
    }

    private static void ValidateDeepPlacementRoutingBarrier(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> wallIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var sourceKind = ReadStringProperty(item, "sourceKind");
        var sourceId = ReadStringProperty(item, "sourceId");
        if (sourceKind == "Wall" && !string.IsNullOrWhiteSpace(sourceId) && !wallIds.Contains(sourceId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} sourceId references missing wall '{sourceId}'."));
        }

        if (!TryReadValidationLine(item, "centerLine", out var centerLine))
        {
            return;
        }

        if (centerLine.Length <= PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} centerLine must have positive length."));
        }

        var drawingLength = ReadNonNegativeDoubleForDeep(item, "drawingLength");
        if (drawingLength is not null && Math.Abs(drawingLength.Value - centerLine.Length) > Math.Max(0.25, centerLine.Length * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{prefix} drawingLength differs from centerLine length."));
        }

        ValidateDeepPlacementMetricLine(item, "centerLine", "centerLineMillimeters", prefix, messages);
        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementRoutingPassage(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> wallIds,
        IReadOnlySet<string> roomIds,
        IReadOnlySet<string> openingIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var sourceKind = ReadStringProperty(item, "sourceKind");
        var sourceId = ReadStringProperty(item, "sourceId");
        if (sourceKind == "Opening" && !string.IsNullOrWhiteSpace(sourceId) && !openingIds.Contains(sourceId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} sourceId references missing opening '{sourceId}'."));
        }

        foreach (var wallId in ReadStringArrayForDeep(item, "hostWallIds"))
        {
            if (!wallIds.Contains(wallId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} hostWallIds references missing wall '{wallId}'."));
            }
        }

        var connectedRoomIds = ReadStringArrayForDeep(item, "connectedRoomIds").ToHashSet(StringComparer.Ordinal);
        foreach (var roomId in connectedRoomIds)
        {
            if (!roomIds.Contains(roomId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} connectedRoomIds references missing room '{roomId}'."));
            }
        }

        if (item.TryGetProperty("connectedRoomLinks", out var links) && links.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var link in links.EnumerateArray())
            {
                var linkPrefix = $"{prefix}.connectedRoomLinks[{index}]";
                var roomId = ReadStringProperty(link, "roomId");
                if (!string.IsNullOrWhiteSpace(roomId))
                {
                    if (!roomIds.Contains(roomId))
                    {
                        messages.Add(new ArtifactValidationMessage(
                            "error",
                            $"{linkPrefix} roomId references missing room '{roomId}'."));
                    }

                    if (!connectedRoomIds.Contains(roomId))
                    {
                        messages.Add(new ArtifactValidationMessage(
                            "error",
                            $"{linkPrefix} roomId must also appear in connectedRoomIds."));
                    }
                }

                index++;
            }
        }

        if (TryReadValidationLine(item, "centerLine", out var centerLine)
            && centerLine.Length <= PlacementValidationTolerance)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} centerLine must have positive length."));
        }

        var placementValidationMessageCount = messages.Count;
        if (item.TryGetProperty("placement", out var placement)
            && placement.ValueKind == JsonValueKind.Object)
        {
            ValidateDeepOpeningPlacement(
                placement,
                prefix,
                ReadStringArrayForDeep(item, "hostWallIds").ToArray(),
                wallIds,
                messages);
        }

        if (ReadBooleanProperty(item, "readyForCoordinatePlacement") == true
            && messages.Count > placementValidationMessageCount)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} readyForCoordinatePlacement is true but placement failed coordinate-readiness checks."));
        }

        ValidateDeepPlacementMetricLine(item, "centerLine", "centerLineMillimeters", prefix, messages);
        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementRoutingObstacle(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> roomIds,
        IReadOnlySet<string> objectAggregateIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var sourceKind = ReadStringProperty(item, "sourceKind");
        var sourceId = ReadStringProperty(item, "sourceId");
        if (sourceKind == "ObjectAggregate"
            && !string.IsNullOrWhiteSpace(sourceId)
            && !objectAggregateIds.Contains(sourceId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} sourceId references missing object aggregate '{sourceId}'."));
        }

        var roomId = ReadStringProperty(item, "roomId");
        if (!string.IsNullOrWhiteSpace(roomId) && !roomIds.Contains(roomId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} roomId references missing room '{roomId}'."));
        }

        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
        ValidateDeepPlacementMetricPoint(item, "center", "centerMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementRoutingRoomUseHint(
        JsonElement item,
        string prefix,
        IReadOnlySet<string> roomIds,
        IReadOnlySet<string> objectAggregateIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var sourceKind = ReadStringProperty(item, "sourceKind");
        var sourceId = ReadStringProperty(item, "sourceId");
        if (sourceKind == "Room" && !string.IsNullOrWhiteSpace(sourceId) && !roomIds.Contains(sourceId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} sourceId references missing room '{sourceId}'."));
        }

        if (sourceKind == "ObjectAggregate"
            && !string.IsNullOrWhiteSpace(sourceId)
            && !objectAggregateIds.Contains(sourceId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} sourceId references missing object aggregate '{sourceId}'."));
        }

        var roomId = ReadStringProperty(item, "roomId");
        if (!string.IsNullOrWhiteSpace(roomId) && !roomIds.Contains(roomId))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} roomId references missing room '{roomId}'."));
        }

        ValidateDeepPlacementMetricRect(item, "bounds", "boundsMillimeters", prefix, messages);
        ValidateDeepPlacementMetricPoint(item, "center", "centerMillimeters", prefix, messages);
    }

    private static void ValidateDeepPlacementRoutingSuppressedObjects(
        JsonElement routingLayer,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        IReadOnlySet<string> objectAggregateIds,
        IReadOnlySet<string> obstacleIds,
        IReadOnlySet<string> roomUseHintIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var suppressedObjectCandidateIds = ReadStringArrayForDeep(routingLayer, "suppressedObjectCandidateIds")
            .ToHashSet(StringComparer.Ordinal);
        var items = ReadArrayProperty(routingLayer, "suppressedObjects", "Placement routingLayer", messages);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var prefix = $"Placement routingLayer.suppressedObjects[{index}] routing suppressed object";
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "id");
            if (!string.IsNullOrWhiteSpace(id) && !ids.Add(id))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} id '{id}' is duplicated in routingLayer.suppressedObjects."));
            }

            ValidateDeepPlacementSuppressedObjectGeometry(item, prefix, pages, messages);

            var objectCandidateId = ReadStringProperty(item, "objectCandidateId");
            if (!string.IsNullOrWhiteSpace(objectCandidateId)
                && !suppressedObjectCandidateIds.Contains(objectCandidateId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} objectCandidateId '{objectCandidateId}' must appear in routingLayer.suppressedObjectCandidateIds."));
            }

            var aggregateId = ReadStringProperty(item, "suppressedByAggregateId");
            if (!string.IsNullOrWhiteSpace(aggregateId) && !objectAggregateIds.Contains(aggregateId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} suppressedByAggregateId references missing object aggregate '{aggregateId}'."));
            }

            var replacementObstacleId = ReadStringProperty(item, "replacementRoutingObstacleId");
            if (!string.IsNullOrWhiteSpace(replacementObstacleId) && !obstacleIds.Contains(replacementObstacleId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} replacementRoutingObstacleId references missing routing obstacle '{replacementObstacleId}'."));
            }

            var roomUseHintId = ReadStringProperty(item, "roomUseHintId");
            if (!string.IsNullOrWhiteSpace(roomUseHintId) && !roomUseHintIds.Contains(roomUseHintId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} roomUseHintId references missing routing room-use hint '{roomUseHintId}'."));
            }

            ValidateDeepPlacementMetricRect(item, "candidateBounds", "candidateBoundsMillimeters", prefix, messages);
            ValidateDeepPlacementMetricPoint(item, "candidateCenter", "candidateCenterMillimeters", prefix, messages);
        }
    }

    private static void ValidateDeepPlacementRoutingIgnoredObjects(
        JsonElement routingLayer,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        IReadOnlySet<string> objectAggregateIds,
        IReadOnlySet<string> roomUseHintIds,
        ICollection<ArtifactValidationMessage> messages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var ignoredObjectCandidateIds = ReadStringArrayForDeep(routingLayer, "ignoredObjectCandidateIds")
            .ToHashSet(StringComparer.Ordinal);
        var items = ReadArrayProperty(routingLayer, "ignoredObjects", "Placement routingLayer", messages);
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            var prefix = $"Placement routingLayer.ignoredObjects[{index}] routing ignored object";
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = ReadStringProperty(item, "id");
            if (!string.IsNullOrWhiteSpace(id) && !ids.Add(id))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} id '{id}' is duplicated in routingLayer.ignoredObjects."));
            }

            ValidateDeepPlacementSuppressedObjectGeometry(item, prefix, pages, messages);

            var objectCandidateId = ReadStringProperty(item, "objectCandidateId");
            if (!string.IsNullOrWhiteSpace(objectCandidateId)
                && !ignoredObjectCandidateIds.Contains(objectCandidateId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} objectCandidateId '{objectCandidateId}' must appear in routingLayer.ignoredObjectCandidateIds."));
            }

            var aggregateId = ReadStringProperty(item, "suppressedByAggregateId");
            if (!string.IsNullOrWhiteSpace(aggregateId) && !objectAggregateIds.Contains(aggregateId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} suppressedByAggregateId references missing object aggregate '{aggregateId}'."));
            }

            var roomUseHintId = ReadStringProperty(item, "roomUseHintId");
            if (!string.IsNullOrWhiteSpace(roomUseHintId) && !roomUseHintIds.Contains(roomUseHintId))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} roomUseHintId references missing routing room-use hint '{roomUseHintId}'."));
            }

            ValidateDeepPlacementMetricRect(item, "candidateBounds", "candidateBoundsMillimeters", prefix, messages);
            ValidateDeepPlacementMetricPoint(item, "candidateCenter", "candidateCenterMillimeters", prefix, messages);
        }
    }

    private static void ValidateDeepPlacementSuppressedObjectGeometry(
        JsonElement item,
        string prefix,
        IReadOnlyDictionary<int, PlacementValidationPage> pages,
        ICollection<ArtifactValidationMessage> messages)
    {
        var pageNumber = ReadPositiveIntegerForDeep(item, "pageNumber");
        var hasPage = false;
        var page = default(PlacementValidationPage);
        if (pageNumber is not null && !pages.TryGetValue(pageNumber.Value, out page))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} pageNumber {pageNumber.Value} does not exist in placement pages."));
        }
        else if (pageNumber is not null)
        {
            hasPage = true;
        }

        if (!TryReadValidationRect(item, "candidateBounds", out var bounds))
        {
            return;
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} candidateBounds width and height must be positive."));
        }

        if (hasPage && !RectInsidePage(bounds, page))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} candidateBounds are outside page {page.PageNumber}."));
        }
    }

    private static void ValidateDeepPlacementQualityGate(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!TryReadObjectProperty(root, "qualityGate", "Placement export", messages, out var qualityGate)
            || !TryReadObjectProperty(root, "calibration", "Placement export", messages, out var calibration))
        {
            return;
        }

        var readyForMetric = ReadBooleanProperty(qualityGate, "readyForMetricPlacement");
        var hasReliableCalibration = ReadBooleanProperty(qualityGate, "hasReliableCalibration");
        var millimetersPerDrawingUnit = ReadPositiveDoubleForDeep(calibration, "millimetersPerDrawingUnit", allowNull: true);
        if (readyForMetric == true
            && (hasReliableCalibration != true || millimetersPerDrawingUnit is null))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                "Placement qualityGate readyForMetricPlacement requires reliable calibration and positive millimetersPerDrawingUnit."));
        }
    }

    private const double PlacementValidationTolerance = 0.5;

    private static bool RectInsidePage(
        PlacementValidationRect rect,
        PlacementValidationPage page) =>
        rect.X >= -PlacementValidationTolerance
        && rect.Y >= -PlacementValidationTolerance
        && rect.Right <= page.Width + PlacementValidationTolerance
        && rect.Bottom <= page.Height + PlacementValidationTolerance;

    private static double SafeInverse(double value) =>
        Math.Abs(value) < double.Epsilon ? 0d : 1d / value;

    private static int? ReadPositiveIntegerForDeep(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number <= 0)
        {
            return null;
        }

        return number;
    }

    private static int? ReadNonNegativeIntegerForDeep(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var number)
            || number < 0)
        {
            return null;
        }

        return number;
    }

    private static double? ReadPositiveDoubleForDeep(
        JsonElement root,
        string propertyName,
        bool allowNull = false)
    {
        var value = ReadDoubleForDeep(root, propertyName, allowNull);
        return value is > 0 ? value : null;
    }

    private static double? ReadNonNegativeDoubleForDeep(JsonElement root, string propertyName)
    {
        var value = ReadDoubleForDeep(root, propertyName);
        return value is >= 0 ? value : null;
    }

    private static double? ReadDoubleForDeep(
        JsonElement root,
        string propertyName,
        bool allowNull = false)
    {
        if (!root.TryGetProperty(propertyName, out var value)
            || (allowNull && value.ValueKind == JsonValueKind.Null)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || !double.IsFinite(number))
        {
            return null;
        }

        return number;
    }

    private static IEnumerable<string> ReadStringArrayForDeep(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(item.GetString()))
            {
                yield return item.GetString()!;
            }
        }
    }

    private static bool TryReadValidationRect(
        JsonElement root,
        string propertyName,
        out PlacementValidationRect rect)
    {
        rect = default;
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var x = ReadDoubleForDeep(value, "x");
        var y = ReadDoubleForDeep(value, "y");
        var width = ReadDoubleForDeep(value, "width");
        var height = ReadDoubleForDeep(value, "height");
        if (x is null || y is null || width is null || height is null)
        {
            return false;
        }

        rect = new PlacementValidationRect(x.Value, y.Value, width.Value, height.Value);
        return true;
    }

    private static bool TryReadValidationPointProperty(
        JsonElement root,
        string propertyName,
        out PlacementValidationPoint point)
    {
        point = default;
        return root.TryGetProperty(propertyName, out var value)
            && TryReadValidationPoint(value, out point);
    }

    private static bool TryReadValidationPoint(
        JsonElement value,
        out PlacementValidationPoint point)
    {
        point = default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var x = ReadDoubleForDeep(value, "x");
        var y = ReadDoubleForDeep(value, "y");
        if (x is null || y is null)
        {
            return false;
        }

        point = new PlacementValidationPoint(x.Value, y.Value);
        return true;
    }

    private static bool TryReadValidationPointArray(
        JsonElement root,
        string propertyName,
        int expectedCount,
        out PlacementValidationPoint[] points)
    {
        points = Array.Empty<PlacementValidationPoint>();
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = value.EnumerateArray().ToArray();
        if (items.Length != expectedCount)
        {
            return false;
        }

        var result = new PlacementValidationPoint[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            if (!TryReadValidationPoint(items[index], out result[index]))
            {
                return false;
            }
        }

        points = result;
        return true;
    }

    private static bool TryReadValidationLine(
        JsonElement root,
        string propertyName,
        out PlacementValidationLine line)
    {
        line = default;
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadValidationPointProperty(value, "start", out var start)
            || !TryReadValidationPointProperty(value, "end", out var end))
        {
            return false;
        }

        line = new PlacementValidationLine(start, end);
        return true;
    }

    private static bool TryReadValidationVectorProperty(
        JsonElement root,
        string propertyName,
        out PlacementValidationVector vector)
    {
        vector = default;
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var x = ReadDoubleForDeep(value, "x");
        var y = ReadDoubleForDeep(value, "y");
        if (x is null || y is null)
        {
            return false;
        }

        vector = new PlacementValidationVector(x.Value, y.Value);
        return true;
    }

    private static bool TryReadValidationNumberArray(
        JsonElement root,
        string propertyName,
        int expectedCount,
        out double[] values)
    {
        values = Array.Empty<double>();
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = array.EnumerateArray().ToArray();
        if (items.Length != expectedCount)
        {
            return false;
        }

        var result = new double[items.Length];
        for (var index = 0; index < items.Length; index++)
        {
            if (items[index].ValueKind != JsonValueKind.Number
                || !items[index].TryGetDouble(out var number)
                || !double.IsFinite(number))
            {
                return false;
            }

            result[index] = number;
        }

        values = result;
        return true;
    }

    private static void ValidatePlacementParameter(
        double? parameter,
        string label,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (parameter is null)
        {
            return;
        }

        if (parameter.Value < -0.05 || parameter.Value > 1.05)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{label} must be between 0 and 1, allowing only small tolerance."));
        }
    }

    private static void ValidateUnitVector(
        PlacementValidationVector vector,
        string label,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (Math.Abs(vector.Length - 1) > 0.05)
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{label} should be unit length."));
        }
    }

    private static void ValidateProjectedOffset(
        PlacementValidationLine referenceLine,
        PlacementValidationPoint point,
        double expectedOffset,
        string label,
        ICollection<ArtifactValidationMessage> messages)
    {
        var dx = referenceLine.End.X - referenceLine.Start.X;
        var dy = referenceLine.End.Y - referenceLine.Start.Y;
        var length = referenceLine.Length;
        if (length <= PlacementValidationTolerance)
        {
            return;
        }

        var ux = dx / length;
        var uy = dy / length;
        var px = point.X - referenceLine.Start.X;
        var py = point.Y - referenceLine.Start.Y;
        var projectedOffset = (px * ux) + (py * uy);
        if (Math.Abs(projectedOffset - expectedOffset) > Math.Max(0.75, length * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{label} projected offset differs from placement offset."));
        }

        var perpendicularDistance = Math.Abs((px * -uy) + (py * ux));
        if (perpendicularDistance > Math.Max(0.75, length * 0.03))
        {
            messages.Add(new ArtifactValidationMessage(
                "warning",
                $"{label} is not close to placement referenceLine."));
        }
    }

    private static bool PointInsideRect(
        PlacementValidationPoint point,
        PlacementValidationRect rect,
        double tolerance) =>
        point.X >= rect.X - tolerance
        && point.X <= rect.Right + tolerance
        && point.Y >= rect.Y - tolerance
        && point.Y <= rect.Bottom + tolerance;

    private static PlacementValidationPoint Midpoint(PlacementValidationLine line) =>
        new((line.Start.X + line.End.X) / 2.0, (line.Start.Y + line.End.Y) / 2.0);

    private static void ValidatePointNear(
        PlacementValidationPoint actual,
        PlacementValidationPoint expected,
        string label,
        ICollection<ArtifactValidationMessage> messages)
    {
        var dx = actual.X - expected.X;
        var dy = actual.Y - expected.Y;
        if (Math.Sqrt((dx * dx) + (dy * dy)) > Math.Max(0.25, PlacementValidationTolerance))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{label} must match opening footprint corner geometry."));
        }
    }

    private readonly record struct PlacementValidationPage(
        int PageNumber,
        double Width,
        double Height);

    private readonly record struct PlacementValidationRect(
        double X,
        double Y,
        double Width,
        double Height)
    {
        public double Right => X + Width;

        public double Bottom => Y + Height;
    }

    private readonly record struct PlacementValidationPoint(
        double X,
        double Y);

    private readonly record struct PlacementValidationVector(
        double X,
        double Y)
    {
        public double Length => Math.Sqrt((X * X) + (Y * Y));
    }

    private readonly record struct PlacementValidationLine(
        PlacementValidationPoint Start,
        PlacementValidationPoint End)
    {
        public double Length
        {
            get
            {
                var dx = End.X - Start.X;
                var dy = End.Y - Start.Y;
                return Math.Sqrt((dx * dx) + (dy * dy));
            }
        }
    }

    private static void ValidateBatchComparisonReferences(
        string comparisonPath,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(comparisonPath))
            ?? Directory.GetCurrentDirectory();
        var itemElements = items.EnumerateArray().ToArray();
        for (var index = 0; index < itemElements.Length; index++)
        {
            var item = itemElements[index];
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            ValidateBatchComparisonSideReferences(
                item,
                "baseline",
                $"Batch comparison items[{index}].baseline",
                baseDirectory,
                messages);
            ValidateBatchComparisonSideReferences(
                item,
                "candidate",
                $"Batch comparison items[{index}].candidate",
                baseDirectory,
                messages);
        }
    }

    private static void ValidateBatchComparisonSideReferences(
        JsonElement item,
        string side,
        string prefix,
        string baseDirectory,
        ICollection<ArtifactValidationMessage> messages)
    {
        var status = ReadStringProperty(item, $"{side}Status");
        var shouldHaveScanArtifacts = status is "Succeeded" or "CompletedWithErrors";
        var scanProperty = $"{side}ScanJsonPath";
        var visualProperty = $"{side}VisualSnapshotPath";
        var geoJsonProperty = $"{side}GeoJsonPath";
        var placementProperty = $"{side}PlacementJsonPath";
        var overlayProperty = $"{side}OverlayDirectory";

        string? scanPath = null;
        string? visualSnapshotPath = null;
        if (shouldHaveScanArtifacts)
        {
            scanPath = ValidateReferencedArtifactPath(
                item,
                scanProperty,
                "scan",
                prefix,
                baseDirectory,
                messages);
            visualSnapshotPath = ValidateReferencedArtifactPath(
                item,
                visualProperty,
                "visual-snapshot",
                prefix,
                baseDirectory,
                messages);
        }
        else
        {
            scanPath = ValidateOptionalReferencedArtifactPath(
                item,
                scanProperty,
                "scan",
                prefix,
                baseDirectory,
                messages);
            visualSnapshotPath = ValidateOptionalReferencedArtifactPath(
                item,
                visualProperty,
                "visual-snapshot",
                prefix,
                baseDirectory,
                messages);
        }

        _ = ValidateOptionalReferencedArtifactPath(
            item,
            geoJsonProperty,
            "geojson",
            prefix,
            baseDirectory,
            messages);
        _ = ValidateOptionalReferencedArtifactPath(
            item,
            placementProperty,
            "placement",
            prefix,
            baseDirectory,
            messages);
        ValidateBatchResultReferencedDirectory(
            item,
            overlayProperty,
            prefix,
            baseDirectory,
            messages);

        if (scanPath is not null && visualSnapshotPath is not null)
        {
            CompareReferencedScanAndVisualPageCounts(
                scanPath,
                visualSnapshotPath,
                prefix,
                messages);
        }

        if (visualSnapshotPath is not null)
        {
            CompareReferencedComparisonVisualIssueCount(
                item,
                side,
                visualSnapshotPath,
                prefix,
                messages);
            ValidateVisualSnapshotSvgLinks(visualSnapshotPath, prefix, messages);
        }
    }

    private static void ValidateBatchResultReferences(
        string batchResultPath,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(batchResultPath))
            ?? Directory.GetCurrentDirectory();
        var itemElements = items.EnumerateArray().ToArray();
        for (var index = 0; index < itemElements.Length; index++)
        {
            var item = itemElements[index];
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var prefix = $"Batch result items[{index}]";
            var status = ReadStringProperty(item, "status");
            if (status is not "Succeeded" and not "CompletedWithErrors")
            {
                continue;
            }

            var scanPath = ValidateReferencedArtifactPath(
                item,
                "scanJsonPath",
                "scan",
                prefix,
                baseDirectory,
                messages);
            var visualSnapshotPath = ValidateReferencedArtifactPath(
                item,
                "visualSnapshotPath",
                "visual-snapshot",
                prefix,
                baseDirectory,
                messages);

            if (ReadStringProperty(item, "geoJsonPath") is { Length: > 0 })
            {
                _ = ValidateReferencedArtifactPath(
                    item,
                    "geoJsonPath",
                    "geojson",
                    prefix,
                    baseDirectory,
                    messages);
            }

            string? placementPath = null;
            if (ReadStringProperty(item, "placementJsonPath") is { Length: > 0 })
            {
                placementPath = ValidateReferencedArtifactPath(
                    item,
                    "placementJsonPath",
                    "placement",
                    prefix,
                    baseDirectory,
                    messages);
            }

            ValidateBatchResultReferencedDirectory(
                item,
                "overlayDirectory",
                prefix,
                baseDirectory,
                messages);

            var expectedPages = item.TryGetProperty("counts", out var counts) && counts.ValueKind == JsonValueKind.Object
                ? ReadNonNegativeIntegerPropertyOrZero(counts, "pages")
                : 0;
            if (scanPath is not null)
            {
                CompareReferencedPageCount(
                    scanPath,
                    "scan",
                    "pages",
                    expectedPages,
                    $"{prefix}.scanJsonPath",
                    messages);
            }

            if (visualSnapshotPath is not null)
            {
                CompareReferencedPageCount(
                    visualSnapshotPath,
                    "visual snapshot",
                    "pages",
                    expectedPages,
                    $"{prefix}.visualSnapshotPath",
                    messages);
                CompareReferencedVisualIssueCount(
                    item,
                    visualSnapshotPath,
                    prefix,
                    messages);
                ValidateVisualSnapshotSvgLinks(visualSnapshotPath, prefix, messages);
            }

            if (placementPath is not null)
            {
                CompareReferencedPageCount(
                    placementPath,
                    "placement",
                    "pages",
                    expectedPages,
                    $"{prefix}.placementJsonPath",
                    messages);
            }
        }
    }

    private static string? ValidateReferencedArtifactPath(
        JsonElement item,
        string propertyName,
        string expectedKind,
        string prefix,
        string baseDirectory,
        ICollection<ArtifactValidationMessage> messages)
    {
        var value = ReadStringProperty(item, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} requires non-empty {propertyName} for deep validation."));
            return null;
        }

        var path = ResolveValidationPath(baseDirectory, value);
        if (!File.Exists(path))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {propertyName} does not exist: {path}"));
            return null;
        }

        ArtifactValidationResult nestedResult;
        try
        {
            nestedResult = ValidateArtifact(
                path,
                File.ReadAllText(path),
                expectedKind,
                deep: false);
        }
        catch (Exception exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {propertyName} could not be validated: {exception.Message}"));
            return path;
        }

        foreach (var nestedMessage in nestedResult.Messages.Where(message =>
                     !string.Equals(message.Severity, "info", StringComparison.OrdinalIgnoreCase)))
        {
            messages.Add(new ArtifactValidationMessage(
                nestedMessage.Severity,
                $"{prefix} {propertyName}: {nestedMessage.Message}"));
        }

        return path;
    }

    private static string? ValidateOptionalReferencedArtifactPath(
        JsonElement item,
        string propertyName,
        string expectedKind,
        string prefix,
        string baseDirectory,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(ReadStringProperty(item, propertyName)))
        {
            return null;
        }

        return ValidateReferencedArtifactPath(
            item,
            propertyName,
            expectedKind,
            prefix,
            baseDirectory,
            messages);
    }

    private static void ValidateBatchResultReferencedDirectory(
        JsonElement item,
        string propertyName,
        string prefix,
        string baseDirectory,
        ICollection<ArtifactValidationMessage> messages)
    {
        var value = ReadStringProperty(item, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var path = ResolveValidationPath(baseDirectory, value);
        if (!Directory.Exists(path))
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {propertyName} does not exist: {path}"));
        }
    }

    private static void CompareReferencedScanAndVisualPageCounts(
        string scanPath,
        string visualSnapshotPath,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var scanPageCount = ReadReferencedArrayLength(scanPath, "scan", "pages", $"{prefix}.scanJsonPath", messages);
        var visualPageCount = ReadReferencedArrayLength(visualSnapshotPath, "visual snapshot", "pages", $"{prefix}.visualSnapshotPath", messages);
        if (scanPageCount is not null
            && visualPageCount is not null
            && scanPageCount.Value != visualPageCount.Value)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot page count {visualPageCount.Value} does not match scan page count {scanPageCount.Value}."));
        }
    }

    private static void CompareReferencedPageCount(
        string path,
        string displayName,
        string pagePropertyName,
        int expectedPages,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(pagePropertyName, out var pages)
                || pages.ValueKind != JsonValueKind.Array)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} {displayName} requires a {pagePropertyName} array for deep page-count checks."));
                return;
            }

            var actualPages = pages.GetArrayLength();
            if (expectedPages != actualPages)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} {displayName} page count {actualPages} does not match batch counts.pages {expectedPages}."));
            }
        }
        catch (JsonException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {displayName} JSON could not be parsed for deep checks: {exception.Message}"));
        }
        catch (IOException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {displayName} could not be read for deep checks: {exception.Message}"));
        }
    }

    private static int? ReadReferencedArrayLength(
        string path,
        string displayName,
        string propertyName,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(propertyName, out var array)
                || array.ValueKind != JsonValueKind.Array)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} {displayName} requires a {propertyName} array for deep checks."));
                return null;
            }

            return array.GetArrayLength();
        }
        catch (JsonException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {displayName} JSON could not be parsed for deep checks: {exception.Message}"));
            return null;
        }
        catch (IOException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {displayName} could not be read for deep checks: {exception.Message}"));
            return null;
        }
    }

    private static void CompareReferencedVisualIssueCount(
        JsonElement item,
        string visualSnapshotPath,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!item.TryGetProperty("visualSnapshot", out var visualSummary)
            || visualSummary.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var expectedIssueCount = ReadNonNegativeIntegerPropertyOrZero(visualSummary, "issueCount");
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(visualSnapshotPath));
            var actualIssueCount = document.RootElement.TryGetProperty("issues", out var issues)
                && issues.ValueKind == JsonValueKind.Array
                    ? issues.GetArrayLength()
                    : 0;
            if (actualIssueCount != expectedIssueCount)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} visualSnapshot.issueCount {expectedIssueCount} does not match referenced visual snapshot issues length {actualIssueCount}."));
            }
        }
        catch (JsonException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot JSON could not be parsed for issue-count checks: {exception.Message}"));
        }
        catch (IOException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot could not be read for issue-count checks: {exception.Message}"));
        }
    }

    private static void CompareReferencedComparisonVisualIssueCount(
        JsonElement item,
        string side,
        string visualSnapshotPath,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        var propertyName = $"{side}VisualIssueCount";
        if (!item.TryGetProperty(propertyName, out var expectedElement)
            || expectedElement.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (expectedElement.ValueKind != JsonValueKind.Number
            || !expectedElement.TryGetInt32(out var expectedIssueCount)
            || expectedIssueCount < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} {propertyName} must be a non-negative integer or null for deep issue-count checks."));
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(visualSnapshotPath));
            var actualIssueCount = document.RootElement.TryGetProperty("issues", out var issues)
                && issues.ValueKind == JsonValueKind.Array
                    ? issues.GetArrayLength()
                    : 0;
            if (actualIssueCount != expectedIssueCount)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{prefix} {propertyName} {expectedIssueCount} does not match referenced visual snapshot issues length {actualIssueCount}."));
            }
        }
        catch (JsonException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot JSON could not be parsed for issue-count checks: {exception.Message}"));
        }
        catch (IOException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot could not be read for issue-count checks: {exception.Message}"));
        }
    }

    private static void ValidateVisualSnapshotSvgLinks(
        string visualSnapshotPath,
        string prefix,
        ICollection<ArtifactValidationMessage> messages)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(visualSnapshotPath));
            if (!document.RootElement.TryGetProperty("pages", out var pages)
                || pages.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var visualSnapshotDirectory = Path.GetDirectoryName(Path.GetFullPath(visualSnapshotPath))
                ?? Directory.GetCurrentDirectory();
            var pageItems = pages.EnumerateArray().ToArray();
            for (var pageIndex = 0; pageIndex < pageItems.Length; pageIndex++)
            {
                var svgPath = ReadStringProperty(pageItems[pageIndex], "svgPath");
                if (string.IsNullOrWhiteSpace(svgPath))
                {
                    continue;
                }

                var resolvedPath = ResolveValidationPath(visualSnapshotDirectory, svgPath);
                if (!File.Exists(resolvedPath))
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{prefix} visualSnapshot pages[{pageIndex}].svgPath does not exist: {resolvedPath}"));
                }
            }
        }
        catch (JsonException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot JSON could not be parsed for SVG link checks: {exception.Message}"));
        }
        catch (IOException exception)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{prefix} visual snapshot could not be read for SVG link checks: {exception.Message}"));
        }
    }

    private static string ResolveValidationPath(string baseDirectory, string value) =>
        Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(baseDirectory, value));

    private static void ValidateGeoJson(
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || !string.Equals(type.GetString(), "FeatureCollection", StringComparison.Ordinal))
        {
            messages.Add(new ArtifactValidationMessage("error", "GeoJSON export type must be 'FeatureCollection'."));
        }

        if (!root.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
        {
            messages.Add(new ArtifactValidationMessage("error", "GeoJSON export requires a features array."));
            return;
        }

        var index = 0;
        foreach (var feature in features.EnumerateArray())
        {
            index++;
            if (feature.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"GeoJSON feature #{index} must be an object."));
                continue;
            }

            if (!feature.TryGetProperty("type", out var featureType)
                || featureType.ValueKind != JsonValueKind.String
                || !string.Equals(featureType.GetString(), "Feature", StringComparison.Ordinal))
            {
                messages.Add(new ArtifactValidationMessage("error", $"GeoJSON feature #{index} type must be 'Feature'."));
            }

            if (!feature.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            {
                messages.Add(new ArtifactValidationMessage("error", $"GeoJSON feature #{index} requires a properties object."));
            }
        }
    }

    private static void ValidateBenchmarkExpectations(
        string fixturePrefix,
        BenchmarkExpectations expectations,
        ICollection<ArtifactValidationMessage> messages)
    {
        foreach (var property in typeof(BenchmarkExpectations).GetProperties())
        {
            if (property.PropertyType == typeof(int?))
            {
                var value = (int?)property.GetValue(expectations);
                if (value < 0)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{fixturePrefix} expectation {property.Name} must be non-negative."));
                }
            }
            else if (property.PropertyType == typeof(double?))
            {
                var value = (double?)property.GetValue(expectations);
                if (value < 0)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{fixturePrefix} expectation {property.Name} must be non-negative."));
                }
            }
        }

        AddRatioMessage(fixturePrefix, "minQualityConfidence", expectations.MinQualityConfidence, messages);

        foreach (var stage in expectations.StageExpectations ?? Array.Empty<BenchmarkStageExpectation>())
        {
            if (string.IsNullOrWhiteSpace(stage.Stage))
            {
                messages.Add(new ArtifactValidationMessage("error", $"{fixturePrefix} has a stage expectation with an empty stage."));
            }

            AddNonNegativeMessage(fixturePrefix, "stage maxDurationMilliseconds", stage.MaxDurationMilliseconds, messages);
            AddNonNegativeMessage(fixturePrefix, "stage maxDiagnostics", stage.MaxDiagnostics, messages);
            AddNonNegativeMessage(fixturePrefix, "stage maxWarnings", stage.MaxWarnings, messages);
            AddNonNegativeMessage(fixturePrefix, "stage maxErrors", stage.MaxErrors, messages);

            foreach (var artifact in stage.ArtifactExpectations ?? Array.Empty<BenchmarkStageArtifactExpectation>())
            {
                var artifactPrefix = $"{fixturePrefix} stage '{stage.Stage}' artifact '{artifact.Artifact}'";
                if (artifact.Artifact == PlanArtifactKind.Unknown)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{fixturePrefix} stage '{stage.Stage}' artifact expectation requires a known artifact."));
                }

                AddNonNegativeMessage(artifactPrefix, "minBeforeCount", artifact.MinBeforeCount, messages);
                AddNonNegativeMessage(artifactPrefix, "maxBeforeCount", artifact.MaxBeforeCount, messages);
                AddNonNegativeMessage(artifactPrefix, "minAfterCount", artifact.MinAfterCount, messages);
                AddNonNegativeMessage(artifactPrefix, "maxAfterCount", artifact.MaxAfterCount, messages);
                AddRangeMessage(artifactPrefix, "beforeCount", artifact.MinBeforeCount, artifact.MaxBeforeCount, messages);
                AddRangeMessage(artifactPrefix, "afterCount", artifact.MinAfterCount, artifact.MaxAfterCount, messages);
                AddRangeMessage(artifactPrefix, "delta", artifact.MinDelta, artifact.MaxDelta, messages);
            }
        }

        foreach (var (name, metric) in BenchmarkMetrics(expectations))
        {
            if (metric is null)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{fixturePrefix} {name} must be an object when present."));
                continue;
            }

            AddRatioMessage(fixturePrefix, $"{name}.minRecall", metric.MinRecall, messages);
            AddRatioMessage(fixturePrefix, $"{name}.minPrecision", metric.MinPrecision, messages);

            var targets = metric.Targets ?? Array.Empty<BenchmarkDetectionTarget>();
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var targetName = $"{name}.targets[{index}]";
                AddNonNegativeMessage(fixturePrefix, $"{targetName}.minCount", target.MinCount, messages);
                AddNonNegativeMessage(fixturePrefix, $"{targetName}.maxCenterDistance", target.MaxCenterDistance, messages);
                AddRatioMessage(fixturePrefix, $"{targetName}.minIntersectionOverUnion", target.MinIntersectionOverUnion, messages);

                if (target.PageNumber is not null && target.PageNumber < 1)
                {
                    messages.Add(new ArtifactValidationMessage(
                        "error",
                        $"{fixturePrefix} {targetName}.pageNumber must be 1 or greater."));
                }
            }
        }
    }

    private static IEnumerable<(string Name, BenchmarkDetectorMetricExpectations? Metric)> BenchmarkMetrics(
        BenchmarkExpectations expectations)
    {
        yield return ("regionMetrics", expectations.RegionMetrics);
        yield return ("dimensionMetrics", expectations.DimensionMetrics);
        yield return ("annotationMetrics", expectations.AnnotationMetrics);
        yield return ("annotationReferenceMetrics", expectations.AnnotationReferenceMetrics);
        yield return ("gridAxisMetrics", expectations.GridAxisMetrics);
        yield return ("wallMetrics", expectations.WallMetrics);
        yield return ("roomMetrics", expectations.RoomMetrics);
        yield return ("openingMetrics", expectations.OpeningMetrics);
        yield return ("objectMetrics", expectations.ObjectMetrics);
        yield return ("objectGroupMetrics", expectations.ObjectGroupMetrics);
        yield return ("objectAggregateMetrics", expectations.ObjectAggregateMetrics);
        yield return ("routingBarrierMetrics", expectations.RoutingBarrierMetrics);
        yield return ("routingPassageMetrics", expectations.RoutingPassageMetrics);
        yield return ("routingObstacleMetrics", expectations.RoutingObstacleMetrics);
        yield return ("routingRoomUseHintMetrics", expectations.RoutingRoomUseHintMetrics);
        yield return ("routingSuppressedObjectMetrics", expectations.RoutingSuppressedObjectMetrics);
        yield return ("layerMetrics", expectations.LayerMetrics);
    }

    private static void AddNonNegativeMessage(
        string fixturePrefix,
        string fieldName,
        int? value,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (value < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} must be non-negative."));
        }
    }

    private static void AddNonNegativeMessage(
        string fixturePrefix,
        string fieldName,
        double? value,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (value < 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} must be non-negative."));
        }
    }

    private static void AddRatioMessage(
        string fixturePrefix,
        string fieldName,
        double? value,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (value is < 0 or > 1)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} must be between 0 and 1."));
        }
    }

    private static void AddRangeMessage(
        string fixturePrefix,
        string fieldName,
        int? minValue,
        int? maxValue,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (minValue is not null && maxValue is not null && minValue.Value > maxValue.Value)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} minimum cannot be greater than maximum."));
        }
    }

    private static void AddPositiveMessage(
        string fixturePrefix,
        string fieldName,
        int? value,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (value is <= 0)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} must be greater than 0."));
        }
    }

    private static void AddExpectedIntegerMessage(
        string fixturePrefix,
        string fieldName,
        int expected,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(fieldName, out var value) || value.ValueKind != JsonValueKind.Number)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} requires numeric {fieldName}."));
            return;
        }

        if (!value.TryGetInt32(out var actual) || actual != expected)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} {fieldName} should be {expected}."));
        }
    }

    private static void ValidateExpectedIntegerMap(
        string fixturePrefix,
        string fieldName,
        IReadOnlyDictionary<string, int> expected,
        JsonElement root,
        ICollection<ArtifactValidationMessage> messages)
    {
        if (!root.TryGetProperty(fieldName, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            messages.Add(new ArtifactValidationMessage(
                "error",
                $"{fixturePrefix} requires object {fieldName}."));
            return;
        }

        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetInt32(out var count)
                || count < 0)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{fixturePrefix} {fieldName}.{property.Name} must be a non-negative integer."));
                continue;
            }

            actual[property.Name] = count;
        }

        foreach (var pair in expected)
        {
            if (!actual.TryGetValue(pair.Key, out var actualCount) || actualCount != pair.Value)
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{fixturePrefix} {fieldName}.{pair.Key} should be {pair.Value}."));
            }
        }

        foreach (var pair in actual)
        {
            if (!expected.ContainsKey(pair.Key))
            {
                messages.Add(new ArtifactValidationMessage(
                    "error",
                    $"{fixturePrefix} {fieldName}.{pair.Key} is not expected from wall placement omissions."));
            }
        }
    }

    private static IReadOnlyList<string> RequiredTopLevelProperties(string kind)
    {
        var schema = kind switch
        {
            "scan" => PlanTraceJsonSchema.ReadCurrent(),
            "scan-compact" => PlanTraceCompactJsonSchema.ReadCurrent(),
            "object-review-dataset" => ObjectReviewDatasetJsonSchema.ReadCurrent(),
            "object-correction-dataset" => ObjectCorrectionDatasetJsonSchema.ReadCurrent(),
            "benchmark-manifest" => BenchmarkManifestJsonSchema.ReadCurrent(),
            "benchmark-result" => BenchmarkRunResultJsonSchema.ReadCurrent(),
            "benchmark-comparison" => BenchmarkComparisonJsonSchema.ReadCurrent(),
            "viewer-benchmark-review-session" => ViewerBenchmarkReviewSessionJsonSchema.ReadCurrent(),
            "batch-manifest" => BatchScanManifestJsonSchema.ReadCurrent(),
            "batch-result" => BatchScanRunResultJsonSchema.ReadCurrent(),
            "batch-comparison" => BatchScanComparisonJsonSchema.ReadCurrent(),
            "layer-profile" => LayerCategoryProfileJsonSchema.ReadCurrent(),
            "object-label-profile" => ObjectLabelProfileJsonSchema.ReadCurrent(),
            "kvemo-crops" => VisualAiCropManifestJsonSchema.ReadCurrent(),
            "placement" => PlanPlacementJsonSchema.ReadCurrent(),
            "visual-snapshot" => PlanOverlaySnapshotJsonSchema.ReadCurrent(),
            "geojson" => null,
            _ => null
        };

        if (kind == "geojson")
        {
            return new[] { "schemaVersion", "type", "features" };
        }

        if (schema is null)
        {
            return Array.Empty<string>();
        }

        using var schemaDocument = JsonDocument.Parse(schema);
        return schemaDocument.RootElement
            .GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static string? ResolveValidationKind(
        string? requestedKind,
        JsonElement root,
        string? schemaVersion)
    {
        if (!string.IsNullOrWhiteSpace(requestedKind))
        {
            var normalized = NormalizeValidationKind(requestedKind);
            if (normalized is not null)
            {
                return normalized;
            }
        }

        return schemaVersion?.Trim().ToLowerInvariant() switch
        {
            PlanTraceExport.CurrentSchemaVersion => "scan",
            PlanTraceCompactJsonExporter.CurrentSchemaVersion => "scan-compact",
            ObjectReviewDataset.CurrentSchemaVersion => "object-review-dataset",
            ObjectCorrectionDataset.CurrentSchemaVersion => "object-correction-dataset",
            BenchmarkManifest.CurrentSchemaVersion => "benchmark-manifest",
            BenchmarkRunResult.CurrentSchemaVersion => "benchmark-result",
            BenchmarkComparisonResult.CurrentSchemaVersion => "benchmark-comparison",
            ViewerBenchmarkReviewSessionJsonSchema.CurrentSchemaVersion => "viewer-benchmark-review-session",
            BatchScanManifest.CurrentSchemaVersion => "batch-manifest",
            BatchScanRunResult.CurrentSchemaVersion => "batch-result",
            BatchScanComparisonResult.CurrentSchemaVersion => "batch-comparison",
            LayerCategoryProfile.CurrentSchemaVersion => "layer-profile",
            ObjectLabelProfile.CurrentSchemaVersion => "object-label-profile",
            PlanPlacementExport.CurrentSchemaVersion => "placement",
            PlanOverlaySnapshot.CurrentSchemaVersion => "visual-snapshot",
            PlanTraceGeoJsonExporter.CurrentSchemaVersion => "geojson",
            _ => InferValidationKind(root)
        };
    }

    private static string? NormalizeValidationKind(string kind) =>
        kind.Trim().ToLowerInvariant() switch
        {
            "scan" => "scan",
            "scan-compact" or "compact-scan" or "scan.compact" => "scan-compact",
            "object-review-dataset" or "object-review" => "object-review-dataset",
            "object-correction-dataset" or "object-corrections" => "object-correction-dataset",
            "benchmark-manifest" or "benchmark" => "benchmark-manifest",
            "benchmark-result" or "benchmark-run" or "benchmark-output" => "benchmark-result",
            "benchmark-comparison" or "benchmark-compare" => "benchmark-comparison",
            "viewer-benchmark-review-session" or "benchmark-review-session" or "review-session" or "viewer-review-session" => "viewer-benchmark-review-session",
            "batch-manifest" or "batch" => "batch-manifest",
            "batch-result" or "batch-run" or "batch-summary" => "batch-result",
            "batch-comparison" or "batch-compare" => "batch-comparison",
            "layer-profile" or "layers" => "layer-profile",
            "object-label-profile" or "object-labels" => "object-label-profile",
            "kvemo-crops" or "visual-ai-crops" => "kvemo-crops",
            "placement" or "placement-export" or "consumer-placement" => "placement",
            "visual-snapshot" or "overlay-snapshot" or "visual-qa" => "visual-snapshot",
            "geojson" or "geo-json" => "geojson",
            "auto" => null,
            _ => throw new ArgumentException($"Unknown validation kind: {kind}")
        };

    private static bool IsKvemoCropsValidationKind(string? kind) =>
        kind is not null
        && string.Equals(NormalizeValidationKind(kind), "kvemo-crops", StringComparison.Ordinal);

    private static string? InferValidationKind(JsonElement root)
    {
        if (string.Equals(
                ReadStringProperty(root, "schemaVersion"),
                VisualAiCropManifestEntry.CurrentSchemaVersion,
                StringComparison.Ordinal)
            && root.TryGetProperty("imageFingerprint", out _)
            && root.TryGetProperty("visualSimilarityKey", out _))
        {
            return "kvemo-crops";
        }

        if (root.TryGetProperty("fixtures", out _))
        {
            return "benchmark-manifest";
        }

        if (root.TryGetProperty("cases", out _)
            && root.TryGetProperty("scoreboard", out _)
            && (root.TryGetProperty("caseCount", out _)
                || root.TryGetProperty("passedCaseCount", out _)
                || root.TryGetProperty("failedAssertionCount", out _)))
        {
            return "benchmark-result";
        }

        if (root.TryGetProperty("baselineCaseCount", out _)
            && root.TryGetProperty("candidateCaseCount", out _)
            && root.TryGetProperty("signals", out _)
            && root.TryGetProperty("cases", out _))
        {
            return "benchmark-comparison";
        }

        if (root.TryGetProperty("manifest", out _)
            && root.TryGetProperty("summary", out _)
            && (root.TryGetProperty("decisions", out _)
                || root.TryGetProperty("boundsEdits", out _)
                || root.TryGetProperty("addedTargets", out _)
                || root.TryGetProperty("deletedTargets", out _)))
        {
            return "viewer-benchmark-review-session";
        }

        if (root.TryGetProperty("inputs", out _)
            && (root.TryGetProperty("outputDirectory", out _)
                || root.TryGetProperty("scannerOptions", out _)
                || root.TryGetProperty("writeGeoJson", out _)))
        {
            return "batch-manifest";
        }

        if (root.TryGetProperty("generatedAt", out _)
            && root.TryGetProperty("items", out _)
            && root.TryGetProperty("maxDegreeOfParallelism", out _)
            && root.TryGetProperty("retryCount", out _))
        {
            return "batch-result";
        }

        if (root.TryGetProperty("overrides", out _))
        {
            return "layer-profile";
        }

        if (root.TryGetProperty("rules", out _))
        {
            return "object-label-profile";
        }

        if (root.TryGetProperty("groups", out _) && root.TryGetProperty("ungroupedCandidates", out _))
        {
            return "object-review-dataset";
        }

        if (root.TryGetProperty("actions", out _)
            && (root.TryGetProperty("sourceReviewDatasetSchemaVersion", out _)
                || root.TryGetProperty("createdAt", out _)))
        {
            return "object-correction-dataset";
        }

        if (root.TryGetProperty("document", out _)
            && root.TryGetProperty("pages", out _)
            && root.TryGetProperty("diagnostics", out _))
        {
            return "scan";
        }

        if (root.TryGetProperty("sourceSchemaVersion", out _)
            && root.TryGetProperty("dictionary", out _)
            && root.TryGetProperty("data", out _))
        {
            return "scan-compact";
        }

        if (root.TryGetProperty("coordinateSpace", out _)
            && root.TryGetProperty("scanSchemaVersion", out _)
            && root.TryGetProperty("pages", out _)
            && root.TryGetProperty("issues", out _))
        {
            return "visual-snapshot";
        }

        if (root.TryGetProperty("type", out var type)
            && type.ValueKind == JsonValueKind.String
            && string.Equals(type.GetString(), "FeatureCollection", StringComparison.Ordinal)
            && root.TryGetProperty("features", out _))
        {
            return "geojson";
        }

        return null;
    }

    private static string? ReadStringProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : property.ToString();
    }

    private static bool? ReadBooleanProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.True
            ? true
            : property.ValueKind == JsonValueKind.False
                ? false
                : null;
    }

    private static int ReadNonNegativeIntegerPropertyOrZero(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Number
            || !property.TryGetInt32(out var value)
            || value < 0)
        {
            return 0;
        }

        return value;
    }

    private static OpenPlanTraceEngine CreateEngine()
    {
        var registry = CreateLoaderRegistry();
        return new OpenPlanTraceEngine(registry);
    }

    private static PlanDocumentLoaderRegistry CreateLoaderRegistry()
    {
        return new PlanDocumentLoaderRegistry(new IPlanDocumentLoader[]
        {
            new PdfPigPlanDocumentLoader(),
            new IxMiliaDxfPlanDocumentLoader()
        });
    }

    private static ScannerOptions CreateScannerOptions(ScanArguments parsed)
    {
        ValidateVisualAiOptions(parsed);
        var visualAiClassifier = CreateVisualAiClassifier(parsed);
        var visualAiCropSink = CreateVisualAiCropSink(parsed);
        var visualAiEnabled = visualAiClassifier is not null || visualAiCropSink is not null;
        var visualAiInputWidth = parsed.VisualAiInputWidth ?? 224;
        var visualAiInputHeight = parsed.VisualAiInputHeight ?? 224;

        return new ScannerOptions
        {
            SheetMargin = parsed.SheetMargin ?? 12,
            MinWallLength = parsed.MinWallLength ?? 24,
            MinWallFragmentLength = parsed.MinWallFragmentLength ?? 4,
            MaxWallFragmentGap = parsed.MaxWallFragmentGap ?? 6,
            MaxWallCandidateSeedsPerPage = parsed.MaxWallCandidateSeedsPerPage ?? 15000,
            WallMergeTolerance = parsed.WallMergeTolerance ?? 2.5,
            WallSnapTolerance = parsed.WallSnapTolerance ?? 3,
            DefaultWallThickness = parsed.WallThickness ?? 4,
            MinOpeningGap = parsed.MinOpeningGap ?? 8,
            MaxOpeningGap = parsed.MaxOpeningGap ?? 70,
            ObjectNearbyTextSearchRadius = parsed.ObjectNearbyTextSearchRadius ?? 90,
            MaxNearbyTextPerObject = parsed.MaxNearbyTextPerObject ?? 5,
            LayerCategoryOverrides = ResolveLayerCategoryOverrides(parsed.LayerProfilePaths, parsed.LayerCategoryOverrides),
            ObjectLabelRules = ResolveObjectLabelRules(parsed.ObjectLabelProfilePaths),
            EnableVisualAiClassification = visualAiEnabled,
            VisualAiClassifier = visualAiClassifier,
            VisualAiCropProvider = visualAiEnabled
                ? new PrimitiveVectorVisualAiCropProvider(visualAiInputWidth, visualAiInputHeight, parsed.VisualAiIncludeTextBounds)
                : null,
            VisualAiCropSink = visualAiCropSink,
            MaxVisualAiCropsPerScan = parsed.VisualAiMaxCrops ?? 200,
            MinVisualAiConfidence = parsed.VisualAiMinConfidence ?? 0.35,
            VisualAiTopK = parsed.VisualAiTopK ?? 5,
            VisualAiCropPadding = parsed.VisualAiCropPadding ?? 18
        };
    }

    private static void ValidateVisualAiOptions(IVisualAiCliArguments parsed)
    {
        var visualAiInputWidth = parsed.VisualAiInputWidth ?? 224;
        var visualAiInputHeight = parsed.VisualAiInputHeight ?? 224;
        if (visualAiInputWidth <= 0 || visualAiInputHeight <= 0)
        {
            throw new ArgumentException("Kvemo input width and height must be positive.");
        }

        if (parsed.VisualAiTopK is <= 0)
        {
            throw new ArgumentException("Kvemo top-k must be positive.");
        }

        if (parsed.VisualAiMaxCrops is < 0)
        {
            throw new ArgumentException("Kvemo max crop count cannot be negative.");
        }

        if (parsed.VisualAiMinConfidence is < 0 or > 1)
        {
            throw new ArgumentException("Kvemo minimum confidence must be between 0 and 1.");
        }

        if (parsed.VisualAiCropPadding is < 0)
        {
            throw new ArgumentException("Kvemo crop padding cannot be negative.");
        }
    }

    private static IVisualAiObjectClassifier? CreateVisualAiClassifier(IVisualAiCliArguments parsed)
    {
        if (!parsed.HasVisualAiModelOptions)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(parsed.VisualAiModelPath))
        {
            throw new ArgumentException("--kvemo-model is required when Kvemo model options are used.");
        }

        if (!File.Exists(parsed.VisualAiModelPath))
        {
            throw new ArgumentException($"Kvemo model file not found: {parsed.VisualAiModelPath}");
        }

        if (!string.IsNullOrWhiteSpace(parsed.VisualAiLabelsPath) && !File.Exists(parsed.VisualAiLabelsPath))
        {
            throw new ArgumentException($"Kvemo labels file not found: {parsed.VisualAiLabelsPath}");
        }

        try
        {
            return new OnnxVisualAiClassifier(new OnnxVisualAiClassifierOptions
            {
                ModelPath = parsed.VisualAiModelPath!,
                LabelsPath = parsed.VisualAiLabelsPath,
                InputName = parsed.VisualAiInputName,
                OutputName = parsed.VisualAiOutputName,
                ModelName = parsed.VisualAiModelName,
                ModelVersion = parsed.VisualAiModelVersion,
                InputWidth = parsed.VisualAiInputWidth ?? 224,
                InputHeight = parsed.VisualAiInputHeight ?? 224,
                ChannelsFirst = !parsed.VisualAiChannelsLast,
                TopK = parsed.VisualAiTopK ?? 5,
                Mean = parsed.VisualAiMean ?? new[] { 0.485f, 0.456f, 0.406f },
                StandardDeviation = parsed.VisualAiStandardDeviation ?? new[] { 0.229f, 0.224f, 0.225f }
            });
        }
        catch (Exception exception) when (exception is ArgumentException
            or FileNotFoundException
            or InvalidOperationException
            or OnnxRuntimeException)
        {
            throw new ArgumentException($"Could not initialize Visual AI ONNX model: {exception.Message}", exception);
        }
    }

    private static IVisualAiCropSink? CreateVisualAiCropSink(IVisualAiCliArguments parsed) =>
        string.IsNullOrWhiteSpace(parsed.VisualAiCropDirectory)
            ? null
            : new DirectoryVisualAiCropSink(parsed.VisualAiCropDirectory);

    private static void DisposeScannerOptions(ScannerOptions scannerOptions)
    {
        if (scannerOptions.VisualAiClassifier is IDisposable classifier)
        {
            classifier.Dispose();
        }

        if (scannerOptions.VisualAiCropProvider is IDisposable cropProvider)
        {
            cropProvider.Dispose();
        }

        if (scannerOptions.VisualAiCropSink is IDisposable cropSink)
        {
            cropSink.Dispose();
        }
    }

    private static ScannerOptions CreateScannerOptions(BenchmarkArguments parsed) =>
        new()
        {
            SheetMargin = parsed.SheetMargin ?? 12,
            MinWallLength = parsed.MinWallLength ?? 24,
            MinWallFragmentLength = parsed.MinWallFragmentLength ?? 4,
            MaxWallFragmentGap = parsed.MaxWallFragmentGap ?? 6,
            MaxWallCandidateSeedsPerPage = parsed.MaxWallCandidateSeedsPerPage ?? 15000,
            WallMergeTolerance = parsed.WallMergeTolerance ?? 2.5,
            WallSnapTolerance = parsed.WallSnapTolerance ?? 3,
            DefaultWallThickness = parsed.WallThickness ?? 4,
            MinOpeningGap = parsed.MinOpeningGap ?? 8,
            MaxOpeningGap = parsed.MaxOpeningGap ?? 70,
            ObjectNearbyTextSearchRadius = parsed.ObjectNearbyTextSearchRadius ?? 90,
            MaxNearbyTextPerObject = parsed.MaxNearbyTextPerObject ?? 5,
            LayerCategoryOverrides = ResolveLayerCategoryOverrides(parsed.LayerProfilePaths, parsed.LayerCategoryOverrides),
            ObjectLabelRules = ResolveObjectLabelRules(parsed.ObjectLabelProfilePaths)
        };

    private static ScannerOptions CreateScannerOptions(BatchArguments parsed)
    {
        ValidateVisualAiOptions(parsed);
        var visualAiClassifier = CreateVisualAiClassifier(parsed);
        var visualAiCropSink = CreateVisualAiCropSink(parsed);
        var visualAiEnabled = visualAiClassifier is not null || visualAiCropSink is not null;
        var visualAiInputWidth = parsed.VisualAiInputWidth ?? 224;
        var visualAiInputHeight = parsed.VisualAiInputHeight ?? 224;

        return new ScannerOptions
        {
            SheetMargin = parsed.SheetMargin ?? 12,
            MinWallLength = parsed.MinWallLength ?? 24,
            MinWallFragmentLength = parsed.MinWallFragmentLength ?? 4,
            MaxWallFragmentGap = parsed.MaxWallFragmentGap ?? 6,
            MaxWallCandidateSeedsPerPage = parsed.MaxWallCandidateSeedsPerPage ?? 15000,
            WallMergeTolerance = parsed.WallMergeTolerance ?? 2.5,
            WallSnapTolerance = parsed.WallSnapTolerance ?? 3,
            DefaultWallThickness = parsed.WallThickness ?? 4,
            MinOpeningGap = parsed.MinOpeningGap ?? 8,
            MaxOpeningGap = parsed.MaxOpeningGap ?? 70,
            ObjectNearbyTextSearchRadius = parsed.ObjectNearbyTextSearchRadius ?? 90,
            MaxNearbyTextPerObject = parsed.MaxNearbyTextPerObject ?? 5,
            LayerCategoryOverrides = ResolveLayerCategoryOverrides(parsed.LayerProfilePaths, parsed.LayerCategoryOverrides),
            ObjectLabelRules = ResolveObjectLabelRules(parsed.ObjectLabelProfilePaths),
            EnableVisualAiClassification = visualAiEnabled,
            VisualAiClassifier = visualAiClassifier,
            VisualAiCropProvider = visualAiEnabled
                ? new PrimitiveVectorVisualAiCropProvider(visualAiInputWidth, visualAiInputHeight, parsed.VisualAiIncludeTextBounds)
                : null,
            VisualAiCropSink = visualAiCropSink,
            MaxVisualAiCropsPerScan = parsed.VisualAiMaxCrops ?? 200,
            MinVisualAiConfidence = parsed.VisualAiMinConfidence ?? 0.35,
            VisualAiTopK = parsed.VisualAiTopK ?? 5,
            VisualAiCropPadding = parsed.VisualAiCropPadding ?? 18
        };
    }

    private static void WriteSummary(PlanScanResult result, ScanArguments parsed)
    {
        Console.WriteLine($"Document: {result.Document.Id}");
        Console.WriteLine($"Pages: {result.Document.Pages.Count}");
        Console.WriteLine($"Regions: {result.SheetRegions.Count}");
        Console.WriteLine($"Title blocks: {result.TitleBlocks.Count}");
        Console.WriteLine($"Dimensions: {result.Dimensions.Count}");
        Console.WriteLine($"Annotations: {result.Annotations.Count}");
        Console.WriteLine($"Grid axes: {result.GridAxes.Count}");
        Console.WriteLine($"Grid bay spacings: {result.GridBaySpacings.Count}");
        Console.WriteLine($"Surface patterns: {result.SurfacePatterns.Count}");
        Console.WriteLine($"Walls: {result.Walls.Count}");
        Console.WriteLine($"Graph nodes: {result.WallGraph.Nodes.Count}");
        Console.WriteLine($"Graph edges: {result.WallGraph.Edges.Count}");
        Console.WriteLine($"Rooms: {result.Rooms.Count}");
        Console.WriteLine($"Room adjacencies: {result.RoomAdjacencyGraph.Edges.Count}");
        Console.WriteLine($"Room clusters: {result.RoomAdjacencyGraph.Clusters.Count}");
        Console.WriteLine($"Openings: {result.Openings.Count}");
        Console.WriteLine($"Objects: {result.ObjectCandidates.Count}");
        Console.WriteLine($"Object groups: {result.ObjectGroups.Count}");
        Console.WriteLine($"Object aggregates: {result.ObjectAggregates.Count}");
        Console.WriteLine($"Routing items: {RoutingItemCount(result.RoutingLayer)}");
        if (parsed.HasVisualAiOptions || result.ObjectCandidates.Any(candidate => candidate.VisualAi is not null) || result.ObjectGroups.Any(group => group.VisualAi is not null))
        {
            Console.WriteLine(
                $"Kvemo: {result.ObjectCandidates.Count(candidate => candidate.VisualAi is not null)} object(s), "
                + $"{result.ObjectGroups.Count(group => group.VisualAi is not null)} group(s)"
                + (parsed.VisualAiModelPath is null ? string.Empty : $" using {Path.GetFileName(parsed.VisualAiModelPath)}"));
        }

        if (parsed.VisualAiCropDirectory is not null)
        {
            Console.WriteLine($"Kvemo crops: {Path.GetFullPath(parsed.VisualAiCropDirectory)}");
        }

        Console.WriteLine(result.Calibration.MillimetersPerDrawingUnit is > 0
            ? $"Calibration: {result.Calibration.MillimetersPerDrawingUnit:0.###} mm/drawing-unit ({result.Calibration.Confidence.Value:0.##} confidence)"
            : $"Calibration: unavailable ({result.Calibration.Evidence.Count} evidence items)");
        WriteMeasurementConsistencySummary(result);
        WriteTitleBlockSummary(result);
        Console.WriteLine(
            $"Quality: {result.Quality.Grade} ({result.Quality.OverallConfidence.Value:0.##} confidence, review required: {result.Quality.RequiresReview})");
        Console.WriteLine($"Diagnostics: {result.Diagnostics.Messages.Count} messages, {result.Diagnostics.Duration.TotalMilliseconds:0.##} ms");

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }

        if (parsed.CompactScanPath is not null)
        {
            Console.WriteLine($"Compact scan JSON: {Path.GetFullPath(parsed.CompactScanPath)}");
        }

        if (parsed.CompactScanGZipPath is not null)
        {
            Console.WriteLine($"Compact scan gzip: {Path.GetFullPath(parsed.CompactScanGZipPath)}");
        }

        if (parsed.GeoJsonPath is not null)
        {
            Console.WriteLine($"GeoJSON: {Path.GetFullPath(parsed.GeoJsonPath)}");
        }

        if (parsed.PlacementPath is not null)
        {
            Console.WriteLine($"Placement: {Path.GetFullPath(parsed.PlacementPath)}");
        }

        if (parsed.SvgPath is not null)
        {
            Console.WriteLine($"SVG: {Path.GetFullPath(parsed.SvgPath)}");
        }

        if (parsed.SvgDirectory is not null)
        {
            Console.WriteLine($"SVG directory: {Path.GetFullPath(parsed.SvgDirectory)}");
        }

        if (parsed.SvgPath is not null || parsed.SvgDirectory is not null)
        {
            Console.WriteLine($"SVG profile: {SvgOverlayRenderOptions.ProfileName(parsed.SvgProfile)}");
        }

        if (parsed.VisualSnapshotPath is not null)
        {
            Console.WriteLine($"Visual snapshot: {Path.GetFullPath(parsed.VisualSnapshotPath)}");
        }

        if (parsed.ObjectLabelTemplatePath is not null)
        {
            Console.WriteLine($"Object label template: {Path.GetFullPath(parsed.ObjectLabelTemplatePath)}");
        }
    }

    private static int RoutingItemCount(PlanRoutingLayer routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count;

    private static string SnapshotArtifactPath(string? snapshotPath, string artifactPath)
    {
        var artifactFullPath = Path.GetFullPath(artifactPath);
        if (string.IsNullOrWhiteSpace(snapshotPath))
        {
            return artifactFullPath;
        }

        var snapshotDirectory = Path.GetDirectoryName(Path.GetFullPath(snapshotPath));
        return string.IsNullOrWhiteSpace(snapshotDirectory)
            ? artifactFullPath
            : Path.GetRelativePath(snapshotDirectory, artifactFullPath);
    }

    internal static SvgOverlayRenderOptions CreateSvgOverlayRenderOptions(
        ScanArguments parsed,
        string svgPath,
        int pageNumber)
    {
        var options = SvgOverlayRenderOptions.ForProfile(parsed.SvgProfile);
        var backgroundPath = FindSvgBackgroundImagePath(parsed, pageNumber);
        if (backgroundPath is null)
        {
            return options;
        }

        return options with
        {
            BackgroundImageHref = parsed.EmbedSvgBackgroundImage
                ? SvgBackgroundDataUri(backgroundPath)
                : RelativeSvgHref(svgPath, backgroundPath),
            BackgroundImageOpacity = parsed.SvgBackgroundImageOpacity
        };
    }

    private static string? FindSvgBackgroundImagePath(ScanArguments parsed, int pageNumber)
    {
        if (parsed.SvgBackgroundImagePath is not null)
        {
            return parsed.SvgBackgroundImagePath;
        }

        if (parsed.SvgBackgroundImageDirectory is null)
        {
            return null;
        }

        foreach (var extension in new[] { ".png", ".jpg", ".jpeg", ".webp" })
        {
            var path = Path.Combine(parsed.SvgBackgroundImageDirectory, $"page-{pageNumber}{extension}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string SvgBackgroundDataUri(string artifactPath)
    {
        var mediaType = Path.GetExtension(artifactPath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream"
        };
        var bytes = File.ReadAllBytes(artifactPath);
        return $"data:{mediaType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string RelativeSvgHref(string svgPath, string artifactPath)
    {
        var svgDirectory = Path.GetDirectoryName(Path.GetFullPath(svgPath)) ?? Directory.GetCurrentDirectory();
        var relative = Path.GetRelativePath(svgDirectory, Path.GetFullPath(artifactPath));
        return string.Join(
            "/",
            relative
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Where(part => part.Length > 0)
                .Select(Uri.EscapeDataString));
    }

    private static void WriteInspectSummary(PlanDocumentInspectionResult result)
    {
        Console.WriteLine($"Document: {result.DocumentId}");
        Console.WriteLine($"Source: {result.SourceKind} ({result.FileName})");
        Console.WriteLine($"Pages: {result.PageCount}");
        Console.WriteLine($"Primitives: {result.PrimitiveCount}");
        Console.WriteLine($"Load time: {result.LoadDurationMilliseconds:0.##} ms");
        foreach (var page in result.Pages)
        {
            Console.WriteLine(
                $"  Page {page.PageNumber}: {page.Width:0.##} x {page.Height:0.##}, {page.PrimitiveCount} primitives"
                + $" ({FormatPrimitiveCounts(page.KindCounts)})");
        }

        if (result.TextSamples.Count > 0)
        {
            Console.WriteLine("Text samples:");
            foreach (var sample in result.TextSamples)
            {
                Console.WriteLine(
                    $"  p{sample.PageNumber} {sample.Text} "
                    + $"@ {sample.Bounds.X:0.##},{sample.Bounds.Y:0.##},{sample.Bounds.Width:0.##},{sample.Bounds.Height:0.##}");
            }
        }
    }

    private static string FormatPrimitiveCounts(IReadOnlyDictionary<PlanPrimitiveKind, int> counts) =>
        counts.Count == 0
            ? "none"
            : string.Join(", ", counts.OrderBy(item => item.Key).Select(item => $"{item.Key}:{item.Value}"));

    private static void WriteMeasurementConsistencySummary(PlanScanResult result)
    {
        var report = result.MeasurementConsistency;
        if (report.Checks.Count == 0)
        {
            Console.WriteLine("Measurement QA: unavailable");
            return;
        }

        var median = report.MedianDimensionMillimetersPerDrawingUnit is > 0
            ? $"{report.MedianDimensionMillimetersPerDrawingUnit:0.###} mm/drawing-unit median"
            : "no median scale";
        Console.WriteLine($"Measurement QA: {report.ConsistentCount} consistent, {report.OutlierCount} outliers / {report.CheckedCount} checked ({median})");
    }

    private static void WriteTitleBlockSummary(PlanScanResult result)
    {
        var titleBlock = result.TitleBlocks
            .OrderBy(titleBlock => titleBlock.PageNumber)
            .ThenByDescending(titleBlock => titleBlock.Confidence.Value)
            .FirstOrDefault();

        if (titleBlock is null)
        {
            Console.WriteLine("Title metadata: unavailable");
            return;
        }

        var parts = new[]
        {
            titleBlock.SheetNumber is null ? null : $"sheet {titleBlock.SheetNumber}",
            titleBlock.SheetTitle,
            titleBlock.ProjectName is null ? null : $"project {titleBlock.ProjectName}",
            titleBlock.Revision is null ? null : $"rev {titleBlock.Revision}",
            titleBlock.IssueDate is null ? null : $"date {titleBlock.IssueDate}",
            titleBlock.Scale is null ? null : $"scale {titleBlock.Scale}"
        }.Where(part => !string.IsNullOrWhiteSpace(part));

        Console.WriteLine($"Title metadata: {string.Join(" | ", parts)} ({titleBlock.Fields.Count} fields, {titleBlock.Confidence.Value:0.##} confidence)");
    }

    private static void WriteBenchmarkSummary(BenchmarkRunResult run, BenchmarkArguments parsed)
    {
        Console.WriteLine($"Benchmark: {run.Name ?? "OpenPlanTrace"}");
        Console.WriteLine($"Cases: {run.PassedCaseCount} passed, {run.FailedCaseCount} failed, {run.SkippedCaseCount} skipped / {run.CaseCount}");
        Console.WriteLine($"Assertions: {run.PassedAssertionCount} passed, {run.FailedAssertionCount} failed");
        Console.WriteLine($"Review queue: {run.ReviewQueueCount} item(s)");
        Console.WriteLine(
            $"Readiness: {run.Scoreboard.Grade} overall {run.Scoreboard.OverallScore:0.###}, downstream {run.Scoreboard.ConsumerReadinessScore:0.###}, ready {(run.Scoreboard.ReadyForDownstreamUse ? "yes" : "no")}");
        Console.WriteLine(
            $"Truth targets: {run.Scoreboard.MatchedTargetCount}/{run.Scoreboard.ExpectedTargetCount} matched, {run.Scoreboard.MissedTargetCount} missed, {run.Scoreboard.ExtraDetectionCount} extra");

        foreach (var item in run.Cases)
        {
            var status = item.Skipped ? "SKIP" : item.Passed ? "PASS" : "FAIL";
            var label = string.IsNullOrWhiteSpace(item.FixtureName)
                ? item.FixtureId
                : $"{item.FixtureId} - {item.FixtureName}";
            var duration = item.DurationMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);
            if (item.Skipped)
            {
                Console.WriteLine($"  [SKIP] {label}: {item.SkipReason ?? "Fixture skipped."}");
                continue;
            }

            Console.WriteLine($"  [{status}] {label}: {item.PassedAssertionCount}/{item.Assertions.Count} assertions, {duration} ms, quality {item.Counts.QualityGrade} {item.Counts.QualityConfidence:0.##}");
            foreach (var metric in item.Metrics)
            {
                Console.WriteLine(
                    $"    {metric.Detector}: {metric.MatchedCount}/{metric.ExpectedCount} targets, recall {metric.Recall:0.###}, precision {metric.Precision:0.###}, extra {metric.ExtraCount}");
            }

            foreach (var failure in item.Assertions.Where(assertion => !assertion.Passed).Take(5))
            {
                Console.WriteLine($"    - {failure.Name}: expected {failure.Expected}, actual {failure.Actual}");
            }
        }

        foreach (var detector in run.Scoreboard.Detectors
                     .OrderBy(item => item.Grade)
                     .ThenBy(item => item.F1)
                     .Take(8))
        {
            Console.WriteLine(
                $"  [DETECTOR] {detector.Detector}: {detector.Grade}, f1 {detector.F1:0.###}, recall {detector.Recall:0.###}, precision {detector.Precision:0.###}, extra {detector.ExtraCount}");
        }

        foreach (var bucket in run.Scoreboard.FailureBuckets
                     .OrderByDescending(item => item.Severity)
                     .ThenBy(item => item.FixtureId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .Take(8))
        {
            Console.WriteLine(
                $"  [{bucket.Severity}] {bucket.FixtureId ?? "suite"} {bucket.Code}: {bucket.Message}");
        }

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }

        if (parsed.MarkdownPath is not null)
        {
            Console.WriteLine($"Markdown: {Path.GetFullPath(parsed.MarkdownPath)}");
        }
    }

    private static void WriteBenchmarkCompareSummary(
        BenchmarkComparisonResult comparison,
        BenchmarkCompareArguments parsed)
    {
        Console.WriteLine($"Benchmark comparison: {comparison.BaselineName ?? "baseline"} -> {comparison.CandidateName ?? "candidate"}");
        Console.WriteLine($"Status: {(comparison.Passed ? "PASS" : "REGRESSION")}");
        Console.WriteLine($"Cases: {comparison.MatchedCaseCount} matched, {comparison.AddedCaseCount} added, {comparison.RemovedCaseCount} removed");
        Console.WriteLine($"Signals: {comparison.RegressionCount} regressions, {comparison.ImprovementCount} improvements");

        foreach (var signal in comparison.Signals
                     .Where(signal => signal.Severity == BenchmarkComparisonSignalSeverity.Regression)
                     .Take(10))
        {
            Console.WriteLine($"  [REGRESSION] {signal.FixtureId} {signal.Code}: {signal.Baseline ?? "-"} -> {signal.Candidate ?? "-"}");
        }

        foreach (var signal in comparison.Signals
                     .Where(signal => signal.Severity == BenchmarkComparisonSignalSeverity.Improvement)
                     .Take(5))
        {
            Console.WriteLine($"  [IMPROVEMENT] {signal.FixtureId} {signal.Code}: {signal.Baseline ?? "-"} -> {signal.Candidate ?? "-"}");
        }

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }

        if (parsed.MarkdownPath is not null)
        {
            Console.WriteLine($"Markdown: {Path.GetFullPath(parsed.MarkdownPath)}");
        }
    }

    private static void WriteValidationSummary(ArtifactValidationResult result, ValidateArguments parsed)
    {
        Console.WriteLine($"{(result.Valid ? "[PASS]" : "[FAIL]")} {result.ArtifactPath}");
        Console.WriteLine($"  kind: {result.Kind}");
        Console.WriteLine($"  schemaVersion: {result.SchemaVersion ?? "-"}");
        if (parsed.Deep)
        {
            Console.WriteLine("  deep: enabled");
        }

        foreach (var message in result.Messages)
        {
            Console.WriteLine($"  {message.Severity}: {message.Message}");
        }

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }
    }

    private static void WriteBatchSummary(BatchScanRunResult run, BatchArguments parsed)
    {
        Console.WriteLine($"Batch: {run.SucceededCount} succeeded, {run.CompletedWithErrorsCount} completed with diagnostic errors, {run.FailedCount} failed / {run.ItemCount}");
        Console.WriteLine($"Outputs: {Path.GetFullPath(parsed.OutDirectory!)}");
        Console.WriteLine($"Execution: parallel {run.MaxDegreeOfParallelism}, retries {run.RetryCount}");
        if (parsed.VisualAiCropDirectory is not null)
        {
            Console.WriteLine($"Kvemo crops: {Path.GetFullPath(parsed.VisualAiCropDirectory)}");
        }

        foreach (var item in run.Items)
        {
            var duration = item.DurationMilliseconds.ToString("0.##", CultureInfo.InvariantCulture);
            var quality = item.Counts.QualityGrade == "-"
                ? string.Empty
                : $" quality {item.Counts.QualityGrade} {item.Counts.QualityConfidence:0.##}";
            var visual = item.VisualSnapshot.SchemaVersion == "-"
                ? string.Empty
                : $" visual {item.VisualSnapshot.DrawableItemCount} items, {item.VisualSnapshot.IssueCount} issue(s)";
            var attempts = item.AttemptCount > 1 ? $" attempts {item.AttemptCount}" : string.Empty;
            Console.WriteLine($"  [{item.Status}] {Path.GetFileName(item.InputPath)} - {duration} ms{quality}{visual}{attempts}");
            if (!string.IsNullOrWhiteSpace(item.ErrorMessage))
            {
                Console.WriteLine($"    {item.ErrorMessage}");
            }

            if (item.VisualSnapshot.IssueCodes.Count > 0)
            {
                Console.WriteLine($"    Visual issues: {string.Join(", ", item.VisualSnapshot.IssueCodes)}");
            }

            if (item.SourceCapability is not null)
            {
                Console.WriteLine(
                    $"    Capability: {item.SourceCapability.Key} {item.SourceCapability.Status}; adapter {item.SourceCapability.AdapterRequirement}");
                Console.WriteLine($"    Licensing: {item.SourceCapability.LicenseNote}");
            }
        }

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }
    }

    private static void WriteBatchCompareSummary(
        BatchScanComparisonResult comparison,
        BatchCompareArguments parsed)
    {
        Console.WriteLine($"Batch comparison: {comparison.BaselineOutputDirectory ?? "baseline"} -> {comparison.CandidateOutputDirectory ?? "candidate"}");
        Console.WriteLine($"Status: {(comparison.Passed ? "PASS" : "REGRESSION")}");
        Console.WriteLine($"Items: {comparison.MatchedItemCount} matched, {comparison.AddedItemCount} added, {comparison.RemovedItemCount} removed");
        Console.WriteLine($"Signals: {comparison.RegressionCount} regressions, {comparison.ImprovementCount} improvements, {comparison.InfoCount} info, {comparison.StatusChangeCount} status change(s)");
        Console.WriteLine(
            $"Deltas: diagnostic errors {FormatSignedInteger(comparison.DiagnosticErrorDelta)}, "
            + $"visual issues {FormatSignedInteger(comparison.VisualIssueDelta)}, "
            + $"quality avg {FormatSignedDouble(comparison.QualityConfidenceAverageDelta)}, "
            + $"duration {comparison.TotalDurationDeltaMilliseconds:0.##} ms");

        foreach (var signal in comparison.Signals
                     .Where(signal => signal.Severity == BatchScanComparisonSignalSeverity.Regression)
                     .Take(10))
        {
            Console.WriteLine($"  [REGRESSION] {signal.Key} {signal.Code}: {signal.Baseline ?? "-"} -> {signal.Candidate ?? "-"}");
        }

        foreach (var signal in comparison.Signals
                     .Where(signal => signal.Severity == BatchScanComparisonSignalSeverity.Improvement)
                     .Take(5))
        {
            Console.WriteLine($"  [IMPROVEMENT] {signal.Key} {signal.Code}: {signal.Baseline ?? "-"} -> {signal.Candidate ?? "-"}");
        }

        if (parsed.JsonPath is not null)
        {
            Console.WriteLine($"JSON: {Path.GetFullPath(parsed.JsonPath)}");
        }

        if (parsed.MarkdownPath is not null)
        {
            Console.WriteLine($"Markdown: {Path.GetFullPath(parsed.MarkdownPath)}");
        }
    }

    private static string FormatSignedInteger(int value) =>
        value >= 0
            ? $"+{value.ToString(CultureInfo.InvariantCulture)}"
            : value.ToString(CultureInfo.InvariantCulture);

    private static string FormatSignedDouble(double value) =>
        value >= 0
            ? $"+{value.ToString("0.###", CultureInfo.InvariantCulture)}"
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static void WriteKvemoReportSummary(KvemoCropManifestReport report)
    {
        Console.WriteLine($"Kvemo crop manifest: {Path.GetFullPath(report.ManifestPath)}");
        Console.WriteLine($"Entries: {report.EntryCount} total, {report.CropOnlyEntryCount} crop-only, {report.ClassifiedEntryCount} classified, {report.InvalidEntryCount} invalid");
        Console.WriteLine($"Average object/crop area ratio: {report.AverageObjectToCropAreaRatio:0.###}");
        Console.WriteLine($"Source primitives referenced: {report.SourcePrimitiveTotal}");
        if (!string.IsNullOrWhiteSpace(report.FirstImagePath))
        {
            Console.WriteLine($"First image: {report.FirstImagePath}");
        }

        WriteReportCounts("Priorities", report.ByReviewPriority);
        WriteReportCounts("Training use", report.BySuggestedTrainingUse);
        WriteReportCounts("Detection kinds", report.ByDetectionKind);
        WriteReportCounts("Categories", report.ByCategory);
        WriteReportCounts("Source kind memberships", report.BySourceKind);
        WriteReportCounts("Wall component kind memberships", report.BySourceWallComponentKind);
        WriteReportCounts("Top visual similarity keys", report.TopVisualSimilarityKeys);
        WriteReportCounts("Top review reasons", report.TopReviewReasons);

        if (report.InvalidEntryCount > 0)
        {
            Console.WriteLine("Invalid entries:");
            foreach (var invalid in report.InvalidEntries.Take(10))
            {
                Console.WriteLine($"  line {invalid.LineNumber}: {invalid.Message}");
            }
        }
    }

    private static void WriteReportCounts(string label, IReadOnlyList<KvemoReportCount> counts)
    {
        var value = counts.Count == 0
            ? "-"
            : string.Join(", ", counts.Select(item => $"{item.Value}:{item.Count}"));
        Console.WriteLine($"{label}: {value}");
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        WriteUsage();
        return 2;
    }

    private static bool IsHelp(string value) =>
        value is "-h" or "--help" or "help" or "/?";

    private static void WriteUsage()
    {
        Console.WriteLine("OpenPlanTrace CLI");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  scan <input> [options]      Scan a PDF or DXF and export results");
        Console.WriteLine("  batch <inputs> --out-dir    Scan many PDF/DXF files and write per-file outputs");
        Console.WriteLine("  batch --manifest <file>     Scan a schema-versioned batch manifest");
        Console.WriteLine("  batch-compare <old> <new>   Compare two batch result JSON files");
        Console.WriteLine("  benchmark <manifest.json>   Run a benchmark fixture manifest");
        Console.WriteLine("  benchmark-draft <scan.json> Draft a benchmark manifest from scan JSON");
        Console.WriteLine("  benchmark-compare <old> <new>");
        Console.WriteLine("                              Compare two benchmark result JSON files");
        Console.WriteLine("  inspect <input>             Load a source and report normalized primitive counts");
        Console.WriteLine("  formats                     List supported and planned input formats");
        Console.WriteLine("  schema scan                 Print or write the current scan JSON schema");
        Console.WriteLine("  schema scan-compact         Print or write the compact scan JSON schema");
        Console.WriteLine("  schema object-review-dataset");
        Console.WriteLine("                              Print or write the current object review dataset schema");
        Console.WriteLine("  schema object-correction-dataset");
        Console.WriteLine("                              Print or write the current object correction dataset schema");
        Console.WriteLine("  schema benchmark-manifest   Print or write the current benchmark manifest schema");
        Console.WriteLine("  schema benchmark-result     Print or write the current benchmark result schema");
        Console.WriteLine("  schema benchmark-comparison Print or write the benchmark comparison schema");
        Console.WriteLine("  schema viewer-benchmark-review-session");
        Console.WriteLine("                              Print or write the viewer benchmark review-session schema");
        Console.WriteLine("  schema batch-manifest       Print or write the current batch manifest schema");
        Console.WriteLine("  schema batch-result         Print or write the current batch result schema");
        Console.WriteLine("  schema batch-comparison     Print or write the batch comparison schema");
        Console.WriteLine("  schema layer-profile        Print or write the current layer profile schema");
        Console.WriteLine("  schema object-label-profile Print or write the current object label profile schema");
        Console.WriteLine("  schema kvemo-crops          Print or write the Kvemo crop JSONL entry schema");
        Console.WriteLine("  schema placement            Print or write the downstream placement export schema");
        Console.WriteLine("  schema visual-snapshot      Print or write the visual QA snapshot schema");
        Console.WriteLine("  validate <artifact.json>    Validate a schema-versioned OpenPlanTrace JSON artifact");
        Console.WriteLine("  kvemo-report <kvemo-crops.jsonl>");
        Console.WriteLine("                              Summarize Kvemo crop priority/training signals");
        Console.WriteLine("  kvemo-profile-template <kvemo-crops.jsonl>");
        Console.WriteLine("                              Draft object-label-profile rules from Kvemo crop review keys");
        Console.WriteLine("  corrections-to-profile <object-corrections.json>");
        Console.WriteLine("                              Convert reviewed object corrections into a label profile");
        Console.WriteLine();
        Console.WriteLine("Run 'openplantrace scan --help' for scan options.");
    }

    private static void WriteInspectUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace inspect <input.pdf|input.dxf> [--json] [--compact-json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json                    Write loader inspection metadata as JSON");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine("  --text-samples [count]    Include extracted text samples for loader/debug review");
    }

    private static void WriteScanUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace scan <input.pdf|input.dxf> [--json result.json] [--geojson result.geojson] [--placement placement.json] [--svg page.svg] [--svg-dir overlays] [--visual-snapshot snapshot.json] [--out-dir scan-output]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write schema-versioned scan JSON");
        Console.WriteLine("  --compact-scan <path>     Write dictionary/shape-encoded compact scan JSON");
        Console.WriteLine("  --compact-scan-gzip <path> Write gzipped compact scan JSON");
        Console.WriteLine("  --geojson <path>          Write page-coordinate GeoJSON feature collection");
        Console.WriteLine("  --placement <path>        Write compact downstream placement JSON with coordinates, metric transforms, routing, and trust gates");
        Console.WriteLine("  --svg <path>              Write one SVG overlay");
        Console.WriteLine("  --svg-dir <directory>     Write one SVG overlay per page");
        Console.WriteLine("  --svg-profile <name>      SVG overlay profile: placement-review (default), wall-qa, structural-review, or full");
        Console.WriteLine("  --svg-background <path>   Embed one page background image in the SVG for alignment QA");
        Console.WriteLine("  --svg-background-dir <d>  Embed page-N.png/jpg/webp backgrounds in per-page SVG overlays");
        Console.WriteLine("  --svg-background-embed    Store SVG background images as data URIs for portable/headless QA screenshots");
        Console.WriteLine("  --svg-background-opacity <0..1>  Background image opacity for SVG alignment QA (default 0.68)");
        Console.WriteLine("  --visual-snapshot <path>  Write visual QA snapshot JSON with per-page overlay counts, bounds, and issues");
        Console.WriteLine("  --out-dir <directory>     Write scan.json, scan.compact.json, scan.compact.json.gz, scan.geojson, placement.json, overlays/page-N.svg, and visual-snapshot.json");
        Console.WriteLine("  --page <number>           Page used with --svg, default first page");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine("  --trace-stages            Print scanner stage start/completion timing to stderr");
        Console.WriteLine("  --min-wall-length <n>     Override scanner wall length threshold");
        Console.WriteLine("  --min-wall-fragment <n>   Override shortest wall fragment accepted for merging");
        Console.WriteLine("  --max-wall-fragment-gap <n> Override maximum gap healed between wall fragments");
        Console.WriteLine("  --max-wall-candidates <n> Cap wall candidate seeds per page for very dense vector PDFs");
        Console.WriteLine("  --wall-snap <n>           Override wall graph snap tolerance");
        Console.WriteLine("  --wall-merge <n>          Override wall merge tolerance");
        Console.WriteLine("  --wall-thickness <n>      Override default wall thickness");
        Console.WriteLine("  --min-opening-gap <n>     Override minimum opening gap");
        Console.WriteLine("  --max-opening-gap <n>     Override maximum opening gap");
        Console.WriteLine("  --object-nearby-text-radius <n> Override search radius for object tag/nearby text evidence");
        Console.WriteLine("  --max-nearby-text-per-object <n> Override nearby text items retained per object");
        Console.WriteLine("  --sheet-margin <n>        Override sheet margin");
        Console.WriteLine("  --layer-profile <path>    Load layer category overrides from a JSON profile; repeatable");
        Console.WriteLine("  --layer-category <pattern>=<category>");
        Console.WriteLine("                            Override layer classification; repeatable, supports '*' wildcards");
        Console.WriteLine("  --object-label-profile <path>");
        Console.WriteLine("                            Load deterministic object/symbol labels from a JSON profile; repeatable");
        Console.WriteLine("  --object-label-template <path>");
        Console.WriteLine("                            Write editable object-label profile JSON from detected object groups");
        Console.WriteLine("  --object-review-dataset <path>");
        Console.WriteLine("                            Write deterministic object/symbol review queue JSON from detected groups");
        Console.WriteLine("  --object-correction-template <path>");
        Console.WriteLine("                            Write editable human correction dataset JSON from detected object groups");
        Console.WriteLine();
        Console.WriteLine("Kvemo / Visual AI options:");
        Console.WriteLine("  --kvemo-model <path>      Enable real ONNX visual object classification");
        Console.WriteLine("  --kvemo-labels <path>     One label per model output; optional 'label|ObjectCategory'");
        Console.WriteLine("  --kvemo-crop-dir <dir>    Export Kvemo PNG crops plus kvemo-crops.jsonl, works without a model");
        Console.WriteLine("  --kvemo-input-width <n>   Override model/crop input width, default 224");
        Console.WriteLine("  --kvemo-input-height <n>  Override model/crop input height, default 224");
        Console.WriteLine("  --kvemo-input-name <name> Override ONNX input tensor name");
        Console.WriteLine("  --kvemo-output-name <name> Override ONNX output tensor name");
        Console.WriteLine("  --kvemo-channels-last     Use NHWC input layout instead of default NCHW");
        Console.WriteLine("  --kvemo-include-text-bounds Include nearby text boxes in crop images; off by default");
        Console.WriteLine("  --kvemo-mean <r,g,b>      Override RGB normalization mean, default ImageNet");
        Console.WriteLine("  --kvemo-std <r,g,b>       Override RGB normalization std-dev, default ImageNet");
        Console.WriteLine("  --kvemo-top-k <n>         Number of alternatives retained, default 5");
        Console.WriteLine("  --kvemo-max-crops <n>     Maximum object/group crops processed per scan, default 200");
        Console.WriteLine("  --kvemo-min-confidence <n> Confidence threshold for applying labels, default 0.35");
        Console.WriteLine("  --kvemo-crop-padding <n>  Drawing-unit padding around object crops, default 18");
        Console.WriteLine("  --kvemo-model-name <name> Metadata written to JSON evidence");
        Console.WriteLine("  --kvemo-model-version <version> Metadata written to JSON evidence");
        Console.WriteLine("                            The old --visual-ai-* names are still accepted as aliases.");
    }

    private static void WriteBatchUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace batch <file-or-directory>... --out-dir batch-output [--recursive] [--json batch.json] [--geojson] [--no-svg]");
        Console.WriteLine("  openplantrace batch --manifest batch-manifest.json [--out-dir override-output] [--json override-batch.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --manifest <path>         Load inputs, output behavior, profiles, and scanner options from a batch manifest");
        Console.WriteLine("  --out-dir <directory>     Required output directory for per-file scan folders");
        Console.WriteLine("  --json <path>             Write batch summary JSON, default <out-dir>/batch.json");
        Console.WriteLine("  --geojson                 Write scan.geojson beside each per-file scan.json");
        Console.WriteLine("  --recursive               Recurse into input directories");
        Console.WriteLine("  --no-svg                  Do not write per-page SVG overlays; visual-snapshot.json is still written without SVG links");
        Console.WriteLine("  --svg-profile <name>      SVG overlay profile: placement-review (default), wall-qa, structural-review, or full");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine("  --parallel <n>            Scan up to n batch items at once; default 1");
        Console.WriteLine("  --retries <n>             Retry failed scan/load attempts n times; default 0");
        Console.WriteLine("  --min-wall-length <n>     Override scanner wall length threshold");
        Console.WriteLine("  --min-wall-fragment <n>   Override shortest wall fragment accepted for merging");
        Console.WriteLine("  --max-wall-fragment-gap <n> Override maximum gap healed between wall fragments");
        Console.WriteLine("  --max-wall-candidates <n> Cap wall candidate seeds per page for very dense vector PDFs");
        Console.WriteLine("  --wall-snap <n>           Override wall graph snap tolerance");
        Console.WriteLine("  --wall-merge <n>          Override wall merge tolerance");
        Console.WriteLine("  --wall-thickness <n>      Override default wall thickness");
        Console.WriteLine("  --min-opening-gap <n>     Override minimum opening gap");
        Console.WriteLine("  --max-opening-gap <n>     Override maximum opening gap");
        Console.WriteLine("  --object-nearby-text-radius <n> Override search radius for object tag/nearby text evidence");
        Console.WriteLine("  --max-nearby-text-per-object <n> Override nearby text items retained per object");
        Console.WriteLine("  --sheet-margin <n>        Override sheet margin");
        Console.WriteLine("  --layer-profile <path>    Load layer category overrides from a JSON profile; repeatable");
        Console.WriteLine("  --layer-category <pattern>=<category>");
        Console.WriteLine("                            Override layer classification; repeatable, supports '*' wildcards");
        Console.WriteLine("  --object-label-profile <path>");
        Console.WriteLine("                            Load deterministic object/symbol labels from a JSON profile; repeatable");
        Console.WriteLine();
        Console.WriteLine("Kvemo / Visual AI options:");
        Console.WriteLine("  --kvemo-crop-dir <dir>    Export batch-wide Kvemo PNG crops plus kvemo-crops.jsonl");
        Console.WriteLine("  --kvemo-model <path>      Enable real ONNX visual object classification");
        Console.WriteLine("  --kvemo-labels <path>     One label per model output; optional 'label|ObjectCategory'");
        Console.WriteLine("  --kvemo-input-width <n>   Override model/crop input width, default 224");
        Console.WriteLine("  --kvemo-input-height <n>  Override model/crop input height, default 224");
        Console.WriteLine("  --kvemo-max-crops <n>     Maximum object/group crops processed per scan, default 200");
        Console.WriteLine("  --kvemo-crop-padding <n>  Drawing-unit padding around object crops, default 18");
        Console.WriteLine("  --kvemo-include-text-bounds Include nearby text boxes in crop images; off by default");
        Console.WriteLine("  --kvemo-min-confidence <n> Confidence threshold for applying labels, default 0.35");
        Console.WriteLine("  --kvemo-top-k <n>         Number of alternatives retained, default 5");
        Console.WriteLine();
        Console.WriteLine("Batch directories include PDF, DXF, DWG, raster image, and SVG-like plan inputs. DWG/raster/vector files are reported honestly as unsupported unless an adapter is registered.");
    }

    private static void WriteBatchCompareUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace batch-compare <baseline-batch.json> <candidate-batch.json> [--json comparison.json] [--markdown comparison.md]");
        Console.WriteLine();
        Console.WriteLine("Compares two schema-versioned batch result JSON files without rescanning sources.");
        Console.WriteLine("Regression signals include missing items, worse scan status, new diagnostic errors, more visual snapshot issues, quality-confidence drops, detector counts dropping to zero, and large duration increases.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>                 Write batch comparison JSON");
        Console.WriteLine("  --markdown <path>             Write a human-readable Markdown comparison report");
        Console.WriteLine("  --compact-json                Disable pretty JSON");
        Console.WriteLine("  --quality-confidence-drop <n> Confidence drop threshold, default 0.05");
        Console.WriteLine("  --diagnostic-error-increase <n> Error-count increase threshold, default 1");
        Console.WriteLine("  --visual-issue-increase <n>   Visual issue-count increase threshold, default 1");
        Console.WriteLine("  --duration-ratio <n>          Duration regression ratio, default 1.5");
        Console.WriteLine("  --duration-min-ms <n>         Minimum duration delta before ratio is considered, default 250");
        Console.WriteLine("  --no-fail-on-regression       Return exit code 0 even when regression signals are found");
    }

    private static void WriteBenchmarkUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace benchmark <manifest.json|fixture-directory> [--json result.json] [--markdown report.md]");
        Console.WriteLine();
        Console.WriteLine("Manifest format:");
        Console.WriteLine("  { \"name\": \"Smoke\", \"fixtures\": [{ \"id\": \"case-1\", \"sourcePath\": \"plan.pdf\", \"expectations\": { \"minWalls\": 4 } }] }");
        Console.WriteLine("  Expectations can include count ranges, scan quality gates, detector targets, diagnostic-code gates, total duration, and per-stage duration/diagnostic limits.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write benchmark result JSON");
        Console.WriteLine("  --markdown <path>         Write a human-readable Markdown benchmark report");
        Console.WriteLine("                            Reports include a truth-set readiness scoreboard with detector grades and failure buckets");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine("  --min-wall-length <n>     Override scanner wall length threshold");
        Console.WriteLine("  --min-wall-fragment <n>   Override shortest wall fragment accepted for merging");
        Console.WriteLine("  --max-wall-fragment-gap <n> Override maximum gap healed between wall fragments");
        Console.WriteLine("  --max-wall-candidates <n> Cap wall candidate seeds per page for very dense vector PDFs");
        Console.WriteLine("  --wall-snap <n>           Override wall graph snap tolerance");
        Console.WriteLine("  --wall-merge <n>          Override wall merge tolerance");
        Console.WriteLine("  --wall-thickness <n>      Override default wall thickness");
        Console.WriteLine("  --min-opening-gap <n>     Override minimum opening gap");
        Console.WriteLine("  --max-opening-gap <n>     Override maximum opening gap");
        Console.WriteLine("  --object-nearby-text-radius <n> Override search radius for object tag/nearby text evidence");
        Console.WriteLine("  --max-nearby-text-per-object <n> Override nearby text items retained per object");
        Console.WriteLine("  --sheet-margin <n>        Override sheet margin");
        Console.WriteLine("  --layer-profile <path>    Load layer category overrides from a JSON profile; repeatable");
        Console.WriteLine("  --layer-category <pattern>=<category>");
        Console.WriteLine("                            Override layer classification; repeatable, supports '*' wildcards");
        Console.WriteLine("  --object-label-profile <path>");
        Console.WriteLine("                            Load deterministic object/symbol labels from a JSON profile; repeatable");
    }

    private static void WriteBenchmarkDraftUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace benchmark-draft <scan.json> --source-path <plan.pdf|plan.dxf|plan.dwg|fixture> [--json benchmark.draft.json]");
        Console.WriteLine();
        Console.WriteLine("The draft is a normal benchmark manifest generated from measured scan output. Review and tighten it before treating it as ground truth.");
        Console.WriteLine("When scan JSON contains object aggregates or routing-layer semantics, generated targets preserve child-object suppression, routing influence, obstacle kind, source kind, and room-use evidence.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --source-path <path>          Source fixture path to place in the generated manifest");
        Console.WriteLine("  --fixture-id <id>             Fixture id, default scan JSON file name");
        Console.WriteLine("  --fixture-name <name>         Human-readable fixture name");
        Console.WriteLine("  --name <name>                 Manifest name");
        Console.WriteLine("  --json <path>                 Write benchmark manifest JSON; otherwise write to stdout");
        Console.WriteLine("  --review-markdown <path>      Write a human-readable draft target review report");
        Console.WriteLine("  --compact-json                Disable pretty JSON");
        Console.WriteLine("  --optional                    Mark generated fixture optional");
        Console.WriteLine("  --skip-reason <text>          Optional fixture skip reason");
        Console.WriteLine("  --max-targets-per-detector <n> Cap generated metric targets per detector, default 8");
        Console.WriteLine("  --target-recall <n>           Recall floor for generated targets, default 1.0");
        Console.WriteLine("  --target-precision <n>        Optional precision floor for generated targets");
        Console.WriteLine("  --no-bounds                   Do not include target bounds");
    }

    private static void WriteBenchmarkCompareUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace benchmark-compare <baseline-result.json> <candidate-result.json> [--json comparison.json] [--markdown comparison.md]");
        Console.WriteLine();
        Console.WriteLine("Regression signals include missing cases, newly failed cases/assertions, detector metric drops, quality grade/confidence drops, new quality issues, diagnostic error increases, measurement drift, and large duration increases.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>                 Write benchmark comparison JSON");
        Console.WriteLine("  --markdown <path>             Write a human-readable Markdown comparison report");
        Console.WriteLine("  --compact-json                Disable pretty JSON");
        Console.WriteLine("  --quality-confidence-drop <n> Confidence drop threshold, default 0.05");
        Console.WriteLine("  --detector-recall-drop <n>    Detector recall drop threshold, default 0.05");
        Console.WriteLine("  --detector-precision-drop <n> Detector precision drop threshold, default 0.05");
        Console.WriteLine("  --detector-f1-drop <n>        Detector F1 drop threshold, default 0.05");
        Console.WriteLine("  --duration-ratio <n>          Duration regression ratio, default 1.5");
        Console.WriteLine("  --duration-min-ms <n>         Minimum duration delta before ratio is considered, default 250");
        Console.WriteLine("  --no-fail-on-regression       Return exit code 0 even when regression signals are found");
    }

    private static void WriteFormatsUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace formats [--json] [--compact-json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json                    Write format capability metadata as JSON");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
    }

    private static void WriteSchemaUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace schema scan [--json openplantrace.scan.schema.json]");
        Console.WriteLine("  openplantrace schema object-review-dataset [--json openplantrace.object-review-dataset.schema.json]");
        Console.WriteLine("  openplantrace schema object-correction-dataset [--json openplantrace.object-correction-dataset.schema.json]");
        Console.WriteLine("  openplantrace schema benchmark-manifest [--json openplantrace.benchmark-manifest.schema.json]");
        Console.WriteLine("  openplantrace schema benchmark-result [--json openplantrace.benchmark-result.schema.json]");
        Console.WriteLine("  openplantrace schema benchmark-comparison [--json openplantrace.benchmark-comparison.schema.json]");
        Console.WriteLine("  openplantrace schema viewer-benchmark-review-session [--json openplantrace.viewer-benchmark-review-session.schema.json]");
        Console.WriteLine("  openplantrace schema batch-manifest [--json openplantrace.batch-manifest.schema.json]");
        Console.WriteLine("  openplantrace schema batch-result [--json openplantrace.batch-result.schema.json]");
        Console.WriteLine("  openplantrace schema batch-comparison [--json openplantrace.batch-comparison.schema.json]");
        Console.WriteLine("  openplantrace schema layer-profile [--json openplantrace.layer-profile.schema.json]");
        Console.WriteLine("  openplantrace schema object-label-profile [--json openplantrace.object-label-profile.schema.json]");
        Console.WriteLine("  openplantrace schema kvemo-crops [--json openplantrace.kvemo-crops.schema.json]");
        Console.WriteLine("  openplantrace schema placement [--json openplantrace.placement.schema.json]");
        Console.WriteLine("  openplantrace schema visual-snapshot [--json openplantrace.visual-snapshot.schema.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write the embedded schema to a file instead of stdout");
        Console.WriteLine("  --out <path>              Alias for --json");
    }

    private static void WriteValidateUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace validate <artifact.json> [--kind auto|scan|object-review-dataset|object-correction-dataset|benchmark-manifest|benchmark-result|benchmark-comparison|viewer-benchmark-review-session|batch-manifest|batch-result|batch-comparison|layer-profile|object-label-profile|kvemo-crops|placement|visual-snapshot|geojson] [--deep] [--json validation.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --kind <kind>             Override schemaVersion-based kind detection");
        Console.WriteLine("  --deep                    For batch results/comparisons, also validate referenced scan, visual-snapshot, GeoJSON, overlay, and SVG files");
        Console.WriteLine("  --json <path>             Write validation result JSON");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
    }

    private static void WriteKvemoReportUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace kvemo-report <kvemo-crops.jsonl> [--json report.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write report JSON instead of a text summary");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
    }

    private static void WriteKvemoProfileTemplateUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace kvemo-profile-template <kvemo-crops.jsonl> [--json object-label-profile.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write object label profile JSON to a file instead of stdout");
        Console.WriteLine("  --name <name>             Override output profile name");
        Console.WriteLine("  --version <version>       Override output profile version");
        Console.WriteLine("  --crop-only               Include only crop-only entries without model classification");
        Console.WriteLine("  --classified-only         Include only entries with real model classifications");
        Console.WriteLine("  --include-hard-negatives  Include hard-negative-review crops as draft rules");
        Console.WriteLine("  --max-rules <n>           Limit generated draft rule count");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine();
        Console.WriteLine("Generated rules are draft review rules. They preserve Kvemo review keys, signatures, tags, layers, model evidence when present, and must be edited before being treated as confirmed knowledge.");
    }

    private static void WriteCorrectionsToProfileUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  openplantrace corrections-to-profile <object-corrections.json> [--json object-label-profile.json]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --json <path>             Write object label profile JSON to a file instead of stdout");
        Console.WriteLine("  --name <name>             Override output profile name");
        Console.WriteLine("  --version <version>       Override output profile version");
        Console.WriteLine("  --confirmed-only          Include only Confirmed correction actions");
        Console.WriteLine("  --corrected-only          Include only Corrected correction actions");
        Console.WriteLine("  --compact-json            Disable pretty JSON");
        Console.WriteLine();
        Console.WriteLine("Only reviewed Confirmed/Corrected actions with MatchingSignature or MatchingSymbolAndLayer apply scopes become reusable rules.");
    }

    private static string ResolveManifestPath(string path) =>
        Directory.Exists(path) ? Path.Combine(path, "benchmark.json") : path;

    private static string ResolveBatchManifestPath(string path) =>
        Directory.Exists(path) ? Path.Combine(path, "batch.json") : path;

    private static string ResolveFixturePath(string manifestDirectory, string sourcePath) =>
        ResolveManifestRelativePath(manifestDirectory, sourcePath);

    private static void ApplyBatchManifest(
        BatchArguments parsed,
        BatchScanManifest manifest,
        string manifestDirectory)
    {
        var hasCommandLineOutputDirectory = parsed.OutDirectory is not null;

        foreach (var input in manifest.Inputs ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(input))
            {
                parsed.Inputs.Add(ResolveManifestRelativePath(manifestDirectory, input));
            }
        }

        if (parsed.OutDirectory is null && !string.IsNullOrWhiteSpace(manifest.OutputDirectory))
        {
            parsed.OutDirectory = ResolveManifestRelativePath(manifestDirectory, manifest.OutputDirectory);
        }

        if (parsed.JsonPath is null
            && !hasCommandLineOutputDirectory
            && !string.IsNullOrWhiteSpace(manifest.SummaryJsonPath))
        {
            parsed.JsonPath = ResolveManifestRelativePath(manifestDirectory, manifest.SummaryJsonPath);
        }

        if (manifest.Recursive)
        {
            parsed.Recursive = true;
        }

        if (manifest.WriteSvg is false)
        {
            parsed.NoSvg = true;
        }

        if (manifest.WriteGeoJson is true)
        {
            parsed.GeoJson = true;
        }

        parsed.MaxDegreeOfParallelism ??= manifest.MaxDegreeOfParallelism;
        parsed.RetryCount ??= manifest.RetryCount;

        AddManifestRelativePaths(parsed.LayerProfilePaths, manifest.LayerProfiles, manifestDirectory);
        AddManifestRelativePaths(parsed.ObjectLabelProfilePaths, manifest.ObjectLabelProfiles, manifestDirectory);

        foreach (var layerOverride in manifest.LayerCategoryOverrides ?? Array.Empty<LayerCategoryOverride>())
        {
            parsed.LayerCategoryOverrides.Add(layerOverride);
        }

        ApplyBatchScannerOptions(parsed, manifest.ScannerOptions);
    }

    private static void AddManifestRelativePaths(
        ICollection<string> target,
        IReadOnlyList<string>? manifestPaths,
        string manifestDirectory)
    {
        if (manifestPaths is null)
        {
            return;
        }

        foreach (var path in manifestPaths)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                target.Add(ResolveManifestRelativePath(manifestDirectory, path));
            }
        }
    }

    private static void ApplyBatchScannerOptions(
        BatchArguments parsed,
        BatchScannerOptions? options)
    {
        if (options is null)
        {
            return;
        }

        parsed.SheetMargin ??= options.SheetMargin;
        parsed.MinWallLength ??= options.MinWallLength;
        parsed.MinWallFragmentLength ??= options.MinWallFragmentLength;
        parsed.MaxWallFragmentGap ??= options.MaxWallFragmentGap;
        parsed.MaxWallCandidateSeedsPerPage ??= options.MaxWallCandidateSeedsPerPage;
        parsed.WallMergeTolerance ??= options.WallMergeTolerance;
        parsed.WallSnapTolerance ??= options.WallSnapTolerance;
        parsed.WallThickness ??= options.WallThickness;
        parsed.MinOpeningGap ??= options.MinOpeningGap;
        parsed.MaxOpeningGap ??= options.MaxOpeningGap;
        parsed.ObjectNearbyTextSearchRadius ??= options.ObjectNearbyTextSearchRadius;
        parsed.MaxNearbyTextPerObject ??= options.MaxNearbyTextPerObject;
    }

    private static void ValidateBatchExecutionOptions(BatchArguments parsed)
    {
        if (parsed.MaxDegreeOfParallelism is <= 0)
        {
            throw new ArgumentException("--parallel must be greater than 0.");
        }

        if (parsed.RetryCount is < 0)
        {
            throw new ArgumentException("--retries must be non-negative.");
        }
    }

    private static void ValidateBenchmarkCompareArguments(BenchmarkCompareArguments parsed)
    {
        if (parsed.QualityConfidenceDropThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--quality-confidence-drop must be between 0 and 1.");
        }

        if (parsed.DurationRegressionRatio <= 1)
        {
            throw new ArgumentException("--duration-ratio must be greater than 1.");
        }

        if (parsed.DurationRegressionMinimumMilliseconds < 0)
        {
            throw new ArgumentException("--duration-min-ms must be non-negative.");
        }

        if (parsed.DetectorRecallDropThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--detector-recall-drop must be between 0 and 1.");
        }

        if (parsed.DetectorPrecisionDropThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--detector-precision-drop must be between 0 and 1.");
        }

        if (parsed.DetectorF1DropThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--detector-f1-drop must be between 0 and 1.");
        }
    }

    private static void ValidateBatchCompareArguments(BatchCompareArguments parsed)
    {
        if (parsed.QualityConfidenceDropThreshold is < 0 or > 1)
        {
            throw new ArgumentException("--quality-confidence-drop must be between 0 and 1.");
        }

        if (parsed.DurationRegressionRatio <= 1)
        {
            throw new ArgumentException("--duration-ratio must be greater than 1.");
        }

        if (parsed.DurationRegressionMinimumMilliseconds < 0)
        {
            throw new ArgumentException("--duration-min-ms must be non-negative.");
        }

        if (parsed.DiagnosticErrorIncreaseThreshold < 1)
        {
            throw new ArgumentException("--diagnostic-error-increase must be greater than 0.");
        }

        if (parsed.VisualIssueIncreaseThreshold < 1)
        {
            throw new ArgumentException("--visual-issue-increase must be greater than 0.");
        }
    }

    private static string ResolveManifestRelativePath(string manifestDirectory, string path)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(path);
        return Path.IsPathRooted(expandedPath)
            ? expandedPath
            : Path.GetFullPath(Path.Combine(manifestDirectory, expandedPath));
    }

    private static string OptionalFixtureSkipReason(BenchmarkFixture fixture, string fixturePath)
    {
        if (!string.IsNullOrWhiteSpace(fixture.SkipReason))
        {
            return fixture.SkipReason!;
        }

        return $"Optional fixture source was not found: {fixturePath}";
    }

    private static IReadOnlyList<string> ResolveBatchInputs(IReadOnlyList<string> inputs, bool recursive)
    {
        var paths = new List<string>();
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        foreach (var input in inputs)
        {
            if (Directory.Exists(input))
            {
                paths.AddRange(
                    Directory.EnumerateFiles(input, "*.*", searchOption)
                        .Where(IsBatchCandidateExtension)
                        .Select(Path.GetFullPath));
                continue;
            }

            paths.Add(Path.GetFullPath(input));
        }

        return paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsBatchCandidateExtension(string path) =>
        PlanSourceDescriptor.FromFilePath(path).Kind is
            PlanSourceKind.Pdf
            or PlanSourceKind.Dxf
            or PlanSourceKind.Dwg
            or PlanSourceKind.RasterImage
            or PlanSourceKind.VectorImage;

    private static string CreateBatchItemDirectory(string outputDirectory, string inputPath, int itemNumber) =>
        Path.Combine(outputDirectory, $"{itemNumber:0000}-{SafeFileName(Path.GetFileNameWithoutExtension(inputPath))}");

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(character =>
            invalid.Contains(character) || char.IsWhiteSpace(character) ? '-' : character).ToArray());
        cleaned = cleaned.Trim('-');
        return string.IsNullOrWhiteSpace(cleaned) ? "scan" : cleaned;
    }

    private static string UnsupportedSourceMessage(PlanSourceDescriptor source) =>
        UnsupportedSourceMessage(source, CreateLoaderRegistry().GetCapability(source));

    private static string UnsupportedSourceMessage(
        PlanSourceDescriptor source,
        PlanSourceCapability capability)
    {
        if (capability.Kind == PlanSourceKind.Unknown)
        {
            return capability.Message;
        }

        var parts = new List<string>
        {
            capability.Message
        };

        if (!string.IsNullOrWhiteSpace(capability.AdapterRequirement))
        {
            parts.Add($"Adapter: {capability.AdapterRequirement}");
        }

        if (!string.IsNullOrWhiteSpace(capability.LicenseNote))
        {
            parts.Add($"Licensing: {capability.LicenseNote}");
        }

        if (source.Kind != source.EffectiveKind)
        {
            parts.Add($"Source wrapper: {source.Kind}; effective content: {source.EffectiveKind}.");
        }

        return string.Join(" ", parts);
    }

    private static JsonSerializerOptions CreateBenchmarkJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateBatchJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateFormatsJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateInspectJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateValidationJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static JsonSerializerOptions CreateKvemoReportJsonOptions(bool writeIndented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = writeIndented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    internal static IReadOnlyList<LayerCategoryOverride> ResolveLayerCategoryOverrides(
        IReadOnlyList<string> layerProfilePaths,
        IReadOnlyList<LayerCategoryOverride> inlineOverrides)
    {
        if (layerProfilePaths.Count == 0 && inlineOverrides.Count == 0)
        {
            return Array.Empty<LayerCategoryOverride>();
        }

        var overrides = new List<LayerCategoryOverride>();
        overrides.AddRange(inlineOverrides);

        foreach (var profilePath in layerProfilePaths)
        {
            var profile = ReadLayerCategoryProfile(profilePath);
            overrides.AddRange(profile.Overrides);
        }

        return overrides;
    }

    private static LayerCategoryProfile ReadLayerCategoryProfile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Layer profile not found: {path}");
        }

        try
        {
            return LayerCategoryProfile.ParseJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Could not read layer profile '{path}': {exception.Message}", exception);
        }
    }

    internal static IReadOnlyList<ObjectLabelRule> ResolveObjectLabelRules(
        IReadOnlyList<string> objectLabelProfilePaths)
    {
        if (objectLabelProfilePaths.Count == 0)
        {
            return Array.Empty<ObjectLabelRule>();
        }

        var rules = new List<ObjectLabelRule>();
        foreach (var profilePath in objectLabelProfilePaths)
        {
            var profile = ReadObjectLabelProfile(profilePath);
            rules.AddRange(profile.Rules);
        }

        return rules;
    }

    private static ObjectLabelProfile ReadObjectLabelProfile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ArgumentException($"Object label profile not found: {path}");
        }

        try
        {
            return ObjectLabelProfile.ParseJson(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            throw new ArgumentException($"Could not read object label profile '{path}': {exception.Message}", exception);
        }
    }

    internal static LayerCategoryOverride ParseLayerCategoryOverride(string value)
    {
        var separator = value.LastIndexOf('=');
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new ArgumentException($"Invalid --layer-category value: {value}. Use <layer-pattern>=<category>.");
        }

        var pattern = value[..separator].Trim();
        var categoryText = value[(separator + 1)..].Trim();
        if (pattern.Length == 0)
        {
            throw new ArgumentException("--layer-category requires a non-empty layer pattern.");
        }

        if (!Enum.TryParse<LayerCategory>(categoryText, ignoreCase: true, out var category))
        {
            throw new ArgumentException($"Invalid layer category '{categoryText}'. Valid categories: {string.Join(", ", Enum.GetNames<LayerCategory>())}.");
        }

        return new LayerCategoryOverride(pattern, category);
    }

    internal static SvgOverlayRenderProfile ParseSvgOverlayProfile(string value)
    {
        if (SvgOverlayRenderOptions.TryParseProfile(value, out var profile))
        {
            return profile;
        }

        throw new ArgumentException(
            $"Invalid SVG overlay profile '{value}'. Valid profiles: placement-review, wall-qa, structural-review, full.");
    }
}

internal sealed record SchemaContent(string Name, string Json);

internal sealed record ArtifactValidationResult(
    string ArtifactPath,
    string Kind,
    string? SchemaVersion,
    bool Valid,
    IReadOnlyList<ArtifactValidationMessage> Messages);

internal sealed record ArtifactValidationMessage(
    string Severity,
    string Message);

internal sealed record KvemoCropManifestReport(
    string ManifestPath,
    int EntryCount,
    int CropOnlyEntryCount,
    int ClassifiedEntryCount,
    int InvalidEntryCount,
    int SourcePrimitiveTotal,
    double AverageObjectToCropAreaRatio,
    string? FirstImagePath,
    IReadOnlyList<KvemoReportCount> ByReviewPriority,
    IReadOnlyList<KvemoReportCount> BySuggestedTrainingUse,
    IReadOnlyList<KvemoReportCount> ByDetectionKind,
    IReadOnlyList<KvemoReportCount> ByCategory,
    IReadOnlyList<KvemoReportCount> BySourceKind,
    IReadOnlyList<KvemoReportCount> BySourceWallComponentKind,
    IReadOnlyList<KvemoReportCount> TopVisualSimilarityKeys,
    IReadOnlyList<KvemoReportCount> TopReviewReasons,
    IReadOnlyList<KvemoInvalidManifestEntry> InvalidEntries)
{
    public static async Task<KvemoCropManifestReport> ReadAsync(string manifestPath)
    {
        var byPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byTrainingUse = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byDetectionKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byCategory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bySourceKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var bySourceWallComponentKind = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byVisualSimilarityKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var byReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var invalid = new List<KvemoInvalidManifestEntry>();
        var entryCount = 0;
        var classified = 0;
        var cropOnly = 0;
        var sourcePrimitiveTotal = 0;
        var objectToCropRatioSum = 0.0;
        string? firstImagePath = null;

        var lineNumber = 0;
        foreach (var line in await File.ReadAllLinesAsync(manifestPath).ConfigureAwait(false))
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException exception)
            {
                invalid.Add(new KvemoInvalidManifestEntry(lineNumber, exception.Message));
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (!TryReadString(root, "schemaVersion", out var schemaVersion)
                    || !string.Equals(schemaVersion, VisualAiCropManifestEntry.CurrentSchemaVersion, StringComparison.Ordinal))
                {
                    invalid.Add(new KvemoInvalidManifestEntry(lineNumber, $"Unexpected schemaVersion '{schemaVersion ?? "(missing)"}'."));
                    continue;
                }

                entryCount++;
                Increment(byPriority, ReadString(root, "reviewPriority"));
                Increment(byTrainingUse, ReadString(root, "suggestedTrainingUse"));
                Increment(byDetectionKind, ReadString(root, "detectionKind"));
                Increment(byCategory, ReadString(root, "category"));
                if (!IncrementProvenanceCounts(root, "sourceKindCounts", bySourceKind))
                {
                    Increment(bySourceKind, ReadString(root, "sourceKind"));
                }

                if (!IncrementProvenanceCounts(root, "sourceWallComponentKindCounts", bySourceWallComponentKind))
                {
                    IncrementNullable(bySourceWallComponentKind, ReadNullableString(root, "sourceWallComponentKind"));
                }
                if (TryReadString(root, "visualSimilarityKey", out var visualSimilarityKey))
                {
                    Increment(byVisualSimilarityKey, visualSimilarityKey);
                }

                if (firstImagePath is null)
                {
                    firstImagePath = ReadString(root, "imagePath");
                }

                if (root.TryGetProperty("classification", out var classification)
                    && classification.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    classified++;
                }
                else
                {
                    cropOnly++;
                }

                if (TryReadDouble(root, "objectToCropAreaRatio", out var objectToCropRatio))
                {
                    objectToCropRatioSum += objectToCropRatio;
                }

                if (root.TryGetProperty("sourceEvidence", out var sourceEvidence)
                    && TryReadInt(sourceEvidence, "primitiveCount", out var primitiveCount))
                {
                    sourcePrimitiveTotal += primitiveCount;
                }

                if (root.TryGetProperty("reviewReasons", out var reviewReasons)
                    && reviewReasons.ValueKind == JsonValueKind.Array)
                {
                    foreach (var reason in reviewReasons.EnumerateArray())
                    {
                        if (reason.ValueKind == JsonValueKind.String)
                        {
                            Increment(byReason, reason.GetString());
                        }
                    }
                }
            }
        }

        return new KvemoCropManifestReport(
            Path.GetFullPath(manifestPath),
            entryCount,
            cropOnly,
            classified,
            invalid.Count,
            sourcePrimitiveTotal,
            entryCount == 0 ? 0 : Math.Round(objectToCropRatioSum / entryCount, 6),
            firstImagePath,
            ToCounts(byPriority),
            ToCounts(byTrainingUse),
            ToCounts(byDetectionKind),
            ToCounts(byCategory),
            ToCounts(bySourceKind),
            ToCounts(bySourceWallComponentKind),
            ToCounts(byVisualSimilarityKey, take: 8),
            ToCounts(byReason, take: 8),
            invalid);
    }

    private static bool TryReadString(JsonElement root, string propertyName, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string ReadString(JsonElement root, string propertyName) =>
        TryReadString(root, propertyName, out var value) ? value! : "(missing)";

    private static string? ReadNullableString(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool TryReadDouble(JsonElement root, string propertyName, out double value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetDouble(out value);
    }

    private static bool TryReadInt(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        return root.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static void Increment(IDictionary<string, int> counts, string? value)
    {
        var key = string.IsNullOrWhiteSpace(value) ? "(missing)" : value.Trim();
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static void IncrementNullable(IDictionary<string, int> counts, string? value)
    {
        var key = string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
        counts[key] = counts.TryGetValue(key, out var count) ? count + 1 : 1;
    }

    private static bool IncrementProvenanceCounts(
        JsonElement root,
        string propertyName,
        IDictionary<string, int> counts)
    {
        if (!root.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var added = false;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, "value", out var value)
                || !TryReadInt(item, "count", out var count)
                || count <= 0)
            {
                continue;
            }

            var key = value!.Trim();
            counts[key] = counts.TryGetValue(key, out var current) ? current + count : count;
            added = true;
        }

        return added;
    }

    private static IReadOnlyList<KvemoReportCount> ToCounts(
        IReadOnlyDictionary<string, int> counts,
        int? take = null)
    {
        var ordered = counts
            .OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => new KvemoReportCount(item.Key, item.Value));
        return (take is null ? ordered : ordered.Take(take.Value)).ToArray();
    }
}

internal sealed record KvemoReportCount(string Value, int Count);

internal sealed record KvemoInvalidManifestEntry(int LineNumber, string Message);

internal sealed class SchemaArguments
{
    public string? SchemaName { get; set; }

    public string? JsonPath { get; set; }

    public static SchemaArguments Parse(string[] args)
    {
        var parsed = new SchemaArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                case "--out":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.SchemaName is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.SchemaName = arg;
                    break;
            }
        }

        if (parsed.SchemaName is null)
        {
            throw new ArgumentException("Missing schema name.");
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class ValidateArguments
{
    public string? InputPath { get; set; }

    public string? Kind { get; set; }

    public string? JsonPath { get; set; }

    public bool Deep { get; set; }

    public bool PrettyJson { get; set; } = true;

    public static ValidateArguments Parse(string[] args)
    {
        var parsed = new ValidateArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--kind":
                    parsed.Kind = ReadValue(args, ref index, arg);
                    break;
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--deep":
                    parsed.Deep = true;
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.InputPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.InputPath = arg;
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class KvemoReportArguments
{
    public string? ManifestPath { get; set; }

    public string? JsonPath { get; set; }

    public bool PrettyJson { get; set; } = true;

    public static KvemoReportArguments Parse(string[] args)
    {
        var parsed = new KvemoReportArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.ManifestPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.ManifestPath = arg;
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class KvemoProfileTemplateArguments
{
    public string? ManifestPath { get; set; }

    public string? JsonPath { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public bool IncludeCropOnly { get; set; } = true;

    public bool IncludeClassified { get; set; } = true;

    public bool IncludeHardNegativeReviews { get; set; }

    public int? MaxRules { get; set; }

    public bool PrettyJson { get; set; } = true;

    public static KvemoProfileTemplateArguments Parse(string[] args)
    {
        var parsed = new KvemoProfileTemplateArguments();
        var cropOnly = false;
        var classifiedOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--name":
                    parsed.Name = ReadValue(args, ref index, arg);
                    break;
                case "--version":
                    parsed.Version = ReadValue(args, ref index, arg);
                    break;
                case "--crop-only":
                    cropOnly = true;
                    break;
                case "--classified-only":
                    classifiedOnly = true;
                    break;
                case "--include-hard-negatives":
                    parsed.IncludeHardNegativeReviews = true;
                    break;
                case "--max-rules":
                    parsed.MaxRules = ReadPositiveInt(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.ManifestPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.ManifestPath = arg;
                    break;
            }
        }

        if (cropOnly && classifiedOnly)
        {
            throw new ArgumentException("--crop-only and --classified-only cannot be combined.");
        }

        parsed.IncludeCropOnly = !classifiedOnly;
        parsed.IncludeClassified = !cropOnly;
        return parsed;
    }

    private static int ReadPositiveInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            throw new ArgumentException($"{option} must be a positive integer.");
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class CorrectionProfileArguments
{
    public string? InputPath { get; set; }

    public string? JsonPath { get; set; }

    public string? Name { get; set; }

    public string? Version { get; set; }

    public bool IncludeConfirmed { get; set; } = true;

    public bool IncludeCorrected { get; set; } = true;

    public bool PrettyJson { get; set; } = true;

    public static CorrectionProfileArguments Parse(string[] args)
    {
        var parsed = new CorrectionProfileArguments();
        var confirmedOnly = false;
        var correctedOnly = false;

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--name":
                    parsed.Name = ReadValue(args, ref index, arg);
                    break;
                case "--version":
                    parsed.Version = ReadValue(args, ref index, arg);
                    break;
                case "--confirmed-only":
                    confirmedOnly = true;
                    break;
                case "--corrected-only":
                    correctedOnly = true;
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.InputPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.InputPath = arg;
                    break;
            }
        }

        if (confirmedOnly && correctedOnly)
        {
            throw new ArgumentException("--confirmed-only and --corrected-only cannot be combined.");
        }

        parsed.IncludeConfirmed = !correctedOnly;
        parsed.IncludeCorrected = !confirmedOnly;
        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }
}

internal sealed class FormatsArguments
{
    public bool Json { get; set; }

    public bool PrettyJson { get; set; } = true;

    public static FormatsArguments Parse(string[] args)
    {
        var parsed = new FormatsArguments();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--json":
                    parsed.Json = true;
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {arg}");
            }
        }

        return parsed;
    }
}

internal sealed class InspectArguments
{
    public string? InputPath { get; set; }

    public bool Json { get; set; }

    public bool PrettyJson { get; set; } = true;

    public int TextSampleLimit { get; set; }

    public static InspectArguments Parse(string[] args)
    {
        var parsed = new InspectArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.Json = true;
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--text-samples":
                    parsed.TextSampleLimit = 80;
                    if (index + 1 < args.Length
                        && !args[index + 1].StartsWith("-", StringComparison.Ordinal)
                        && int.TryParse(args[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var limit))
                    {
                        parsed.TextSampleLimit = Math.Max(1, limit);
                        index++;
                    }
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.InputPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.InputPath = arg;
                    break;
            }
        }

        return parsed;
    }
}

internal sealed class ConsoleStageProgress : IProgress<PipelineStageProgress>
{
    public void Report(PipelineStageProgress value)
    {
        if (value.Kind == PipelineStageProgressKind.Started)
        {
            Console.Error.WriteLine($"[stage:start] {value.StageName} detections={value.InputDetectionCount}");
            return;
        }

        Console.Error.WriteLine(
            $"[stage:done] {value.StageName} {value.Duration.TotalMilliseconds:0.##} ms"
            + $" detections={value.InputDetectionCount}->{value.OutputDetectionCount}"
            + $" diagnostics={value.DiagnosticCount}");
    }
}

internal interface IVisualAiCliArguments
{
    string? VisualAiModelPath { get; }

    string? VisualAiLabelsPath { get; }

    string? VisualAiCropDirectory { get; }

    string? VisualAiInputName { get; }

    string? VisualAiOutputName { get; }

    string? VisualAiModelName { get; }

    string? VisualAiModelVersion { get; }

    int? VisualAiInputWidth { get; }

    int? VisualAiInputHeight { get; }

    int? VisualAiTopK { get; }

    int? VisualAiMaxCrops { get; }

    double? VisualAiMinConfidence { get; }

    double? VisualAiCropPadding { get; }

    bool VisualAiChannelsLast { get; }

    bool VisualAiIncludeTextBounds { get; }

    IReadOnlyList<float>? VisualAiMean { get; }

    IReadOnlyList<float>? VisualAiStandardDeviation { get; }

    bool HasVisualAiOptions { get; }

    bool HasVisualAiModelOptions { get; }
}

internal sealed class ScanArguments : IVisualAiCliArguments
{
    public string? InputPath { get; set; }

    public string? JsonPath { get; set; }

    public string? CompactScanPath { get; set; }

    public string? CompactScanGZipPath { get; set; }

    public string? SvgPath { get; set; }

    public string? SvgDirectory { get; set; }

    public SvgOverlayRenderProfile SvgProfile { get; set; } = SvgOverlayRenderProfile.PlacementReview;

    public string? SvgBackgroundImagePath { get; set; }

    public string? SvgBackgroundImageDirectory { get; set; }

    public bool EmbedSvgBackgroundImage { get; set; }

    public double SvgBackgroundImageOpacity { get; set; } = 0.68;

    public string? GeoJsonPath { get; set; }

    public string? PlacementPath { get; set; }

    public string? VisualSnapshotPath { get; set; }

    public string? OutDirectory { get; set; }

    public string? ObjectLabelTemplatePath { get; set; }

    public string? ObjectReviewDatasetPath { get; set; }

    public string? ObjectCorrectionTemplatePath { get; set; }

    public int? PageNumber { get; set; }

    public bool PrettyJson { get; set; } = true;

    public bool TraceStages { get; set; }

    public double? MinWallLength { get; set; }

    public double? MinWallFragmentLength { get; set; }

    public double? MaxWallFragmentGap { get; set; }

    public int? MaxWallCandidateSeedsPerPage { get; set; }

    public double? WallSnapTolerance { get; set; }

    public double? WallMergeTolerance { get; set; }

    public double? WallThickness { get; set; }

    public double? MinOpeningGap { get; set; }

    public double? MaxOpeningGap { get; set; }

    public double? ObjectNearbyTextSearchRadius { get; set; }

    public int? MaxNearbyTextPerObject { get; set; }

    public double? SheetMargin { get; set; }

    public List<string> LayerProfilePaths { get; } = new();

    public List<LayerCategoryOverride> LayerCategoryOverrides { get; } = new();

    public List<string> ObjectLabelProfilePaths { get; } = new();

    public string? VisualAiModelPath { get; set; }

    public string? VisualAiLabelsPath { get; set; }

    public string? VisualAiCropDirectory { get; set; }

    public string? VisualAiInputName { get; set; }

    public string? VisualAiOutputName { get; set; }

    public string? VisualAiModelName { get; set; }

    public string? VisualAiModelVersion { get; set; }

    public int? VisualAiInputWidth { get; set; }

    public int? VisualAiInputHeight { get; set; }

    public int? VisualAiTopK { get; set; }

    public int? VisualAiMaxCrops { get; set; }

    public double? VisualAiMinConfidence { get; set; }

    public double? VisualAiCropPadding { get; set; }

    public bool VisualAiChannelsLast { get; set; }

    public bool VisualAiIncludeTextBounds { get; set; }

    public IReadOnlyList<float>? VisualAiMean { get; set; }

    public IReadOnlyList<float>? VisualAiStandardDeviation { get; set; }

    public bool HasVisualAiOptions =>
        VisualAiModelPath is not null
        || VisualAiLabelsPath is not null
        || VisualAiCropDirectory is not null
        || VisualAiInputName is not null
        || VisualAiOutputName is not null
        || VisualAiModelName is not null
        || VisualAiModelVersion is not null
        || VisualAiInputWidth is not null
        || VisualAiInputHeight is not null
        || VisualAiTopK is not null
        || VisualAiMaxCrops is not null
        || VisualAiMinConfidence is not null
        || VisualAiCropPadding is not null
        || VisualAiChannelsLast
        || VisualAiIncludeTextBounds
        || VisualAiMean is not null
        || VisualAiStandardDeviation is not null;

    public bool HasVisualAiModelOptions =>
        VisualAiModelPath is not null
        || VisualAiLabelsPath is not null
        || VisualAiInputName is not null
        || VisualAiOutputName is not null
        || VisualAiModelName is not null
        || VisualAiModelVersion is not null
        || VisualAiChannelsLast
        || VisualAiMean is not null
        || VisualAiStandardDeviation is not null;

    public static ScanArguments Parse(string[] args)
    {
        var parsed = new ScanArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-scan":
                    parsed.CompactScanPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-scan-gzip":
                case "--compact-scan-gz":
                    parsed.CompactScanGZipPath = ReadValue(args, ref index, arg);
                    break;
                case "--svg":
                    parsed.SvgPath = ReadValue(args, ref index, arg);
                    break;
                case "--svg-dir":
                    parsed.SvgDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--svg-profile":
                    parsed.SvgProfile = OpenPlanTraceCli.ParseSvgOverlayProfile(ReadValue(args, ref index, arg));
                    break;
                case "--svg-background":
                case "--svg-background-image":
                    parsed.SvgBackgroundImagePath = ReadValue(args, ref index, arg);
                    break;
                case "--svg-background-dir":
                case "--svg-background-image-dir":
                    parsed.SvgBackgroundImageDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--svg-background-embed":
                case "--embed-svg-background":
                    parsed.EmbedSvgBackgroundImage = true;
                    break;
                case "--svg-background-opacity":
                    parsed.SvgBackgroundImageOpacity = Math.Clamp(ReadDouble(args, ref index, arg), 0, 1);
                    break;
                case "--geojson":
                    parsed.GeoJsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--placement":
                    parsed.PlacementPath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-snapshot":
                    parsed.VisualSnapshotPath = ReadValue(args, ref index, arg);
                    break;
                case "--out-dir":
                    parsed.OutDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--page":
                    parsed.PageNumber = ReadInt(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--trace-stages":
                    parsed.TraceStages = true;
                    break;
                case "--min-wall-length":
                    parsed.MinWallLength = ReadDouble(args, ref index, arg);
                    break;
                case "--min-wall-fragment":
                    parsed.MinWallFragmentLength = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-fragment-gap":
                    parsed.MaxWallFragmentGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-candidates":
                    parsed.MaxWallCandidateSeedsPerPage = ReadInt(args, ref index, arg);
                    break;
                case "--wall-snap":
                    parsed.WallSnapTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-merge":
                    parsed.WallMergeTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-thickness":
                    parsed.WallThickness = ReadDouble(args, ref index, arg);
                    break;
                case "--min-opening-gap":
                    parsed.MinOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-opening-gap":
                    parsed.MaxOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--object-nearby-text-radius":
                    parsed.ObjectNearbyTextSearchRadius = ReadDouble(args, ref index, arg);
                    break;
                case "--max-nearby-text-per-object":
                    parsed.MaxNearbyTextPerObject = ReadInt(args, ref index, arg);
                    break;
                case "--sheet-margin":
                    parsed.SheetMargin = ReadDouble(args, ref index, arg);
                    break;
                case "--layer-profile":
                    parsed.LayerProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                case "--layer-category":
                    parsed.LayerCategoryOverrides.Add(OpenPlanTraceCli.ParseLayerCategoryOverride(ReadValue(args, ref index, arg)));
                    break;
                case "--object-label-profile":
                    parsed.ObjectLabelProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                case "--object-label-template":
                    parsed.ObjectLabelTemplatePath = ReadValue(args, ref index, arg);
                    break;
                case "--object-review-dataset":
                    parsed.ObjectReviewDatasetPath = ReadValue(args, ref index, arg);
                    break;
                case "--object-correction-template":
                    parsed.ObjectCorrectionTemplatePath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-model":
                case "--kvemo-model":
                    parsed.VisualAiModelPath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-labels":
                case "--kvemo-labels":
                    parsed.VisualAiLabelsPath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-crop-dir":
                case "--kvemo-crop-dir":
                    parsed.VisualAiCropDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-input-name":
                case "--kvemo-input-name":
                    parsed.VisualAiInputName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-output-name":
                case "--kvemo-output-name":
                    parsed.VisualAiOutputName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-model-name":
                case "--kvemo-model-name":
                    parsed.VisualAiModelName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-model-version":
                case "--kvemo-model-version":
                    parsed.VisualAiModelVersion = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-input-width":
                case "--kvemo-input-width":
                    parsed.VisualAiInputWidth = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-input-height":
                case "--kvemo-input-height":
                    parsed.VisualAiInputHeight = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-top-k":
                case "--kvemo-top-k":
                    parsed.VisualAiTopK = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-max-crops":
                case "--kvemo-max-crops":
                    parsed.VisualAiMaxCrops = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-min-confidence":
                case "--kvemo-min-confidence":
                    parsed.VisualAiMinConfidence = ReadDouble(args, ref index, arg);
                    break;
                case "--visual-ai-crop-padding":
                case "--kvemo-crop-padding":
                    parsed.VisualAiCropPadding = ReadDouble(args, ref index, arg);
                    break;
                case "--visual-ai-channels-last":
                case "--kvemo-channels-last":
                    parsed.VisualAiChannelsLast = true;
                    break;
                case "--visual-ai-include-text-bounds":
                case "--kvemo-include-text-bounds":
                    parsed.VisualAiIncludeTextBounds = true;
                    break;
                case "--visual-ai-mean":
                case "--kvemo-mean":
                    parsed.VisualAiMean = ReadFloatTriplet(args, ref index, arg);
                    break;
                case "--visual-ai-std":
                case "--kvemo-std":
                    parsed.VisualAiStandardDeviation = ReadFloatTriplet(args, ref index, arg);
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    parsed.InputPath ??= arg;
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }

    private static IReadOnlyList<float> ReadFloatTriplet(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new ArgumentException($"Invalid RGB triplet for {option}: {value}");
        }

        return parts
            .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"Invalid RGB triplet for {option}: {value}"))
            .ToArray();
    }

}

internal sealed class BatchArguments : IVisualAiCliArguments
{
    public List<string> Inputs { get; } = new();

    public string? ManifestPath { get; set; }

    public string? OutDirectory { get; set; }

    public string? JsonPath { get; set; }

    public bool Recursive { get; set; }

    public bool NoSvg { get; set; }

    public SvgOverlayRenderProfile SvgProfile { get; set; } = SvgOverlayRenderProfile.PlacementReview;

    public bool GeoJson { get; set; }

    public bool PrettyJson { get; set; } = true;

    public int? MaxDegreeOfParallelism { get; set; }

    public int? RetryCount { get; set; }

    public double? MinWallLength { get; set; }

    public double? MinWallFragmentLength { get; set; }

    public double? MaxWallFragmentGap { get; set; }

    public int? MaxWallCandidateSeedsPerPage { get; set; }

    public double? WallSnapTolerance { get; set; }

    public double? WallMergeTolerance { get; set; }

    public double? WallThickness { get; set; }

    public double? MinOpeningGap { get; set; }

    public double? MaxOpeningGap { get; set; }

    public double? ObjectNearbyTextSearchRadius { get; set; }

    public int? MaxNearbyTextPerObject { get; set; }

    public double? SheetMargin { get; set; }

    public List<string> LayerProfilePaths { get; } = new();

    public List<LayerCategoryOverride> LayerCategoryOverrides { get; } = new();

    public List<string> ObjectLabelProfilePaths { get; } = new();

    public string? VisualAiModelPath { get; set; }

    public string? VisualAiLabelsPath { get; set; }

    public string? VisualAiCropDirectory { get; set; }

    public string? VisualAiInputName { get; set; }

    public string? VisualAiOutputName { get; set; }

    public string? VisualAiModelName { get; set; }

    public string? VisualAiModelVersion { get; set; }

    public int? VisualAiInputWidth { get; set; }

    public int? VisualAiInputHeight { get; set; }

    public int? VisualAiTopK { get; set; }

    public int? VisualAiMaxCrops { get; set; }

    public double? VisualAiMinConfidence { get; set; }

    public double? VisualAiCropPadding { get; set; }

    public bool VisualAiChannelsLast { get; set; }

    public bool VisualAiIncludeTextBounds { get; set; }

    public IReadOnlyList<float>? VisualAiMean { get; set; }

    public IReadOnlyList<float>? VisualAiStandardDeviation { get; set; }

    public bool HasVisualAiOptions =>
        VisualAiModelPath is not null
        || VisualAiLabelsPath is not null
        || VisualAiCropDirectory is not null
        || VisualAiInputName is not null
        || VisualAiOutputName is not null
        || VisualAiModelName is not null
        || VisualAiModelVersion is not null
        || VisualAiInputWidth is not null
        || VisualAiInputHeight is not null
        || VisualAiTopK is not null
        || VisualAiMaxCrops is not null
        || VisualAiMinConfidence is not null
        || VisualAiCropPadding is not null
        || VisualAiChannelsLast
        || VisualAiIncludeTextBounds
        || VisualAiMean is not null
        || VisualAiStandardDeviation is not null;

    public bool HasVisualAiModelOptions =>
        VisualAiModelPath is not null
        || VisualAiLabelsPath is not null
        || VisualAiInputName is not null
        || VisualAiOutputName is not null
        || VisualAiModelName is not null
        || VisualAiModelVersion is not null
        || VisualAiChannelsLast
        || VisualAiMean is not null
        || VisualAiStandardDeviation is not null;

    public static BatchArguments Parse(string[] args)
    {
        var parsed = new BatchArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--manifest":
                    parsed.ManifestPath = ReadValue(args, ref index, arg);
                    break;
                case "--out-dir":
                    parsed.OutDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--recursive":
                    parsed.Recursive = true;
                    break;
                case "--no-svg":
                    parsed.NoSvg = true;
                    break;
                case "--svg-profile":
                    parsed.SvgProfile = OpenPlanTraceCli.ParseSvgOverlayProfile(ReadValue(args, ref index, arg));
                    break;
                case "--geojson":
                    parsed.GeoJson = true;
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--parallel":
                case "--max-parallel":
                    parsed.MaxDegreeOfParallelism = ReadInt(args, ref index, arg);
                    break;
                case "--retries":
                case "--retry-count":
                    parsed.RetryCount = ReadInt(args, ref index, arg);
                    break;
                case "--min-wall-length":
                    parsed.MinWallLength = ReadDouble(args, ref index, arg);
                    break;
                case "--min-wall-fragment":
                    parsed.MinWallFragmentLength = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-fragment-gap":
                    parsed.MaxWallFragmentGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-candidates":
                    parsed.MaxWallCandidateSeedsPerPage = ReadInt(args, ref index, arg);
                    break;
                case "--wall-snap":
                    parsed.WallSnapTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-merge":
                    parsed.WallMergeTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-thickness":
                    parsed.WallThickness = ReadDouble(args, ref index, arg);
                    break;
                case "--min-opening-gap":
                    parsed.MinOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-opening-gap":
                    parsed.MaxOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--object-nearby-text-radius":
                    parsed.ObjectNearbyTextSearchRadius = ReadDouble(args, ref index, arg);
                    break;
                case "--max-nearby-text-per-object":
                    parsed.MaxNearbyTextPerObject = ReadInt(args, ref index, arg);
                    break;
                case "--sheet-margin":
                    parsed.SheetMargin = ReadDouble(args, ref index, arg);
                    break;
                case "--layer-profile":
                    parsed.LayerProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                case "--layer-category":
                    parsed.LayerCategoryOverrides.Add(OpenPlanTraceCli.ParseLayerCategoryOverride(ReadValue(args, ref index, arg)));
                    break;
                case "--object-label-profile":
                    parsed.ObjectLabelProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                case "--visual-ai-model":
                case "--kvemo-model":
                    parsed.VisualAiModelPath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-labels":
                case "--kvemo-labels":
                    parsed.VisualAiLabelsPath = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-crop-dir":
                case "--kvemo-crop-dir":
                    parsed.VisualAiCropDirectory = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-input-name":
                case "--kvemo-input-name":
                    parsed.VisualAiInputName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-output-name":
                case "--kvemo-output-name":
                    parsed.VisualAiOutputName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-model-name":
                case "--kvemo-model-name":
                    parsed.VisualAiModelName = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-model-version":
                case "--kvemo-model-version":
                    parsed.VisualAiModelVersion = ReadValue(args, ref index, arg);
                    break;
                case "--visual-ai-input-width":
                case "--kvemo-input-width":
                    parsed.VisualAiInputWidth = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-input-height":
                case "--kvemo-input-height":
                    parsed.VisualAiInputHeight = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-top-k":
                case "--kvemo-top-k":
                    parsed.VisualAiTopK = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-max-crops":
                case "--kvemo-max-crops":
                    parsed.VisualAiMaxCrops = ReadInt(args, ref index, arg);
                    break;
                case "--visual-ai-min-confidence":
                case "--kvemo-min-confidence":
                    parsed.VisualAiMinConfidence = ReadDouble(args, ref index, arg);
                    break;
                case "--visual-ai-crop-padding":
                case "--kvemo-crop-padding":
                    parsed.VisualAiCropPadding = ReadDouble(args, ref index, arg);
                    break;
                case "--visual-ai-channels-last":
                case "--kvemo-channels-last":
                    parsed.VisualAiChannelsLast = true;
                    break;
                case "--visual-ai-include-text-bounds":
                case "--kvemo-include-text-bounds":
                    parsed.VisualAiIncludeTextBounds = true;
                    break;
                case "--visual-ai-mean":
                case "--kvemo-mean":
                    parsed.VisualAiMean = ReadFloatTriplet(args, ref index, arg);
                    break;
                case "--visual-ai-std":
                case "--kvemo-std":
                    parsed.VisualAiStandardDeviation = ReadFloatTriplet(args, ref index, arg);
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    parsed.Inputs.Add(arg);
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }

    private static int ReadInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");
    }

    private static IReadOnlyList<float> ReadFloatTriplet(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            throw new ArgumentException($"Invalid RGB triplet for {option}: {value}");
        }

        return parts
            .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : throw new ArgumentException($"Invalid RGB triplet for {option}: {value}"))
            .ToArray();
    }
}

internal sealed class BenchmarkDraftArguments
{
    public string? ScanJsonPath { get; set; }

    public string? SourcePath { get; set; }

    public string? FixtureId { get; set; }

    public string? FixtureName { get; set; }

    public string? ManifestName { get; set; }

    public string? JsonPath { get; set; }

    public string? ReviewMarkdownPath { get; set; }

    public bool PrettyJson { get; set; } = true;

    public bool Optional { get; set; }

    public string? SkipReason { get; set; }

    public int MaxTargetsPerDetector { get; set; } = 8;

    public double TargetRecall { get; set; } = 1.0;

    public double? TargetPrecision { get; set; }

    public bool IncludeBounds { get; set; } = true;

    public static BenchmarkDraftArguments Parse(string[] args)
    {
        var parsed = new BenchmarkDraftArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--source-path":
                    parsed.SourcePath = ReadValue(args, ref index, arg);
                    break;
                case "--fixture-id":
                    parsed.FixtureId = ReadValue(args, ref index, arg);
                    break;
                case "--fixture-name":
                    parsed.FixtureName = ReadValue(args, ref index, arg);
                    break;
                case "--name":
                    parsed.ManifestName = ReadValue(args, ref index, arg);
                    break;
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--review-markdown":
                case "--markdown":
                    parsed.ReviewMarkdownPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--optional":
                    parsed.Optional = true;
                    break;
                case "--skip-reason":
                    parsed.SkipReason = ReadValue(args, ref index, arg);
                    break;
                case "--max-targets-per-detector":
                    parsed.MaxTargetsPerDetector = ReadInt(args, ref index, arg);
                    break;
                case "--target-recall":
                    parsed.TargetRecall = ReadDouble(args, ref index, arg);
                    break;
                case "--target-precision":
                    parsed.TargetPrecision = ReadDouble(args, ref index, arg);
                    break;
                case "--no-bounds":
                    parsed.IncludeBounds = false;
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.ScanJsonPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.ScanJsonPath = arg;
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static int ReadInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }
}

internal sealed class BenchmarkCompareArguments
{
    public string? BaselinePath { get; set; }

    public string? CandidatePath { get; set; }

    public string? JsonPath { get; set; }

    public string? MarkdownPath { get; set; }

    public bool PrettyJson { get; set; } = true;

    public bool NoFailOnRegression { get; set; }

    public double QualityConfidenceDropThreshold { get; set; } = 0.05;

    public double DurationRegressionRatio { get; set; } = 1.5;

    public double DurationRegressionMinimumMilliseconds { get; set; } = 250;

    public double DetectorRecallDropThreshold { get; set; } = 0.05;

    public double DetectorPrecisionDropThreshold { get; set; } = 0.05;

    public double DetectorF1DropThreshold { get; set; } = 0.05;

    public static BenchmarkCompareArguments Parse(string[] args)
    {
        var parsed = new BenchmarkCompareArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--markdown":
                    parsed.MarkdownPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--no-fail-on-regression":
                    parsed.NoFailOnRegression = true;
                    break;
                case "--quality-confidence-drop":
                    parsed.QualityConfidenceDropThreshold = ReadDouble(args, ref index, arg);
                    break;
                case "--duration-ratio":
                    parsed.DurationRegressionRatio = ReadDouble(args, ref index, arg);
                    break;
                case "--duration-min-ms":
                    parsed.DurationRegressionMinimumMilliseconds = ReadDouble(args, ref index, arg);
                    break;
                case "--detector-recall-drop":
                    parsed.DetectorRecallDropThreshold = ReadDouble(args, ref index, arg);
                    break;
                case "--detector-precision-drop":
                    parsed.DetectorPrecisionDropThreshold = ReadDouble(args, ref index, arg);
                    break;
                case "--detector-f1-drop":
                    parsed.DetectorF1DropThreshold = ReadDouble(args, ref index, arg);
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.BaselinePath is null)
                    {
                        parsed.BaselinePath = arg;
                    }
                    else if (parsed.CandidatePath is null)
                    {
                        parsed.CandidatePath = arg;
                    }
                    else
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }
}

internal sealed record BatchScanWorkItem(int Index, int ItemNumber, string InputPath);

internal enum BatchScanItemStatus
{
    Succeeded = 0,
    CompletedWithErrors,
    Missing,
    Unsupported,
    Failed
}

internal sealed record BatchScanRunResult(
    string SchemaVersion,
    DateTimeOffset GeneratedAt,
    string? OutputDirectory,
    int MaxDegreeOfParallelism,
    int RetryCount,
    IReadOnlyList<BatchScanItemResult> Items)
{
    public const string CurrentSchemaVersion = "openplantrace.batch.v5";

    public int ItemCount => Items.Count;

    public int SucceededCount => Items.Count(item => item.Status == BatchScanItemStatus.Succeeded);

    public int CompletedWithErrorsCount => Items.Count(item => item.Status == BatchScanItemStatus.CompletedWithErrors);

    public int MissingCount => Items.Count(item => item.Status == BatchScanItemStatus.Missing);

    public int UnsupportedCount => Items.Count(item => item.Status == BatchScanItemStatus.Unsupported);

    public int FailedScanCount => Items.Count(item => item.Status == BatchScanItemStatus.Failed);

    public int FailedCount => Items.Count(item => item.Status is BatchScanItemStatus.Missing or BatchScanItemStatus.Unsupported or BatchScanItemStatus.Failed);

    public bool Passed => FailedCount == 0 && CompletedWithErrorsCount == 0;

    public static BatchScanRunResult Create(
        string? outputDirectory,
        int maxDegreeOfParallelism,
        int retryCount,
        IEnumerable<BatchScanItemResult> items) =>
        new(CurrentSchemaVersion, DateTimeOffset.UtcNow, outputDirectory, maxDegreeOfParallelism, retryCount, items.ToArray());
}

internal sealed record BatchScanItemResult(
    int ItemNumber,
    string InputPath,
    string? FileName,
    PlanSourceKind SourceKind,
    PlanSourceKind EffectiveSourceKind,
    BatchScanItemStatus Status,
    int AttemptCount,
    double DurationMilliseconds,
    BatchScanCounts Counts,
    string? ScanJsonPath,
    string? GeoJsonPath,
    string? PlacementJsonPath,
    string? OverlayDirectory,
    string? VisualSnapshotPath,
    BatchVisualSnapshotSummary VisualSnapshot,
    string? ErrorMessage,
    PlanSourceCapability? SourceCapability)
{
    public static BatchScanItemResult FromScan(
        int itemNumber,
        string inputPath,
        PlanSourceDescriptor source,
        PlanScanResult scan,
        string scanJsonPath,
        string? geoJsonPath,
        string? placementJsonPath,
        string? overlayDirectory,
        string visualSnapshotPath,
        PlanOverlaySnapshot visualSnapshot,
        TimeSpan duration,
        int attemptCount) =>
        new(
            itemNumber,
            Path.GetFullPath(inputPath),
            Path.GetFileName(inputPath),
            source.Kind,
            source.EffectiveKind,
            scan.Diagnostics.ErrorCount > 0 ? BatchScanItemStatus.CompletedWithErrors : BatchScanItemStatus.Succeeded,
            attemptCount,
            duration.TotalMilliseconds,
            BatchScanCounts.From(scan),
            Path.GetFullPath(scanJsonPath),
            geoJsonPath is null ? null : Path.GetFullPath(geoJsonPath),
            placementJsonPath is null ? null : Path.GetFullPath(placementJsonPath),
            overlayDirectory is null ? null : Path.GetFullPath(overlayDirectory),
            Path.GetFullPath(visualSnapshotPath),
            BatchVisualSnapshotSummary.From(visualSnapshot),
            scan.Diagnostics.ErrorCount > 0 ? "Scan completed with diagnostic errors." : null,
            null);

    public static BatchScanItemResult Failed(
        int itemNumber,
        string inputPath,
        PlanSourceDescriptor source,
        BatchScanItemStatus status,
        string errorMessage,
        TimeSpan duration,
        int attemptCount,
        PlanSourceCapability? sourceCapability = null) =>
        new(
            itemNumber,
            Path.GetFullPath(inputPath),
            Path.GetFileName(inputPath),
            source.Kind,
            source.EffectiveKind,
            status,
            attemptCount,
            duration.TotalMilliseconds,
            BatchScanCounts.Empty,
            null,
            null,
            null,
            null,
            null,
            BatchVisualSnapshotSummary.Empty,
            errorMessage,
            sourceCapability);
}

internal sealed record BatchVisualSnapshotSummary(
    string SchemaVersion,
    int PageCount,
    int LayerCount,
    int DrawableItemCount,
    int IssueCount,
    int WarningIssueCount,
    int ErrorIssueCount,
    double MaxDetectionCoverage,
    IReadOnlyList<string> IssueCodes)
{
    public static BatchVisualSnapshotSummary From(PlanOverlaySnapshot snapshot)
    {
        var issues = snapshot.Issues ?? Array.Empty<PlanOverlaySnapshotIssue>();
        return new BatchVisualSnapshotSummary(
            snapshot.SchemaVersion,
            snapshot.Pages.Count,
            snapshot.Pages.Sum(page => page.Layers.Count),
            snapshot.Pages.Sum(page => page.DrawableItemCount),
            issues.Count,
            issues.Count(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
            issues.Count(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            snapshot.Pages.Count == 0 ? 0 : snapshot.Pages.Max(page => page.DetectionCoverage),
            issues
                .Select(issue => issue.Code)
                .Where(code => !string.IsNullOrWhiteSpace(code))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    public static BatchVisualSnapshotSummary Empty { get; } =
        new("-", 0, 0, 0, 0, 0, 0, 0, Array.Empty<string>());
}

internal sealed record BatchScanCounts(
    int Pages,
    int Regions,
    int TitleBlocks,
    int Dimensions,
    int Annotations,
    int GridAxes,
    int GridBaySpacings,
    int SurfacePatterns,
    int Walls,
    int WallNodes,
    int WallEdges,
    int Rooms,
    int RoomAdjacencies,
    int RoomClusters,
    int Openings,
    int Objects,
    int ObjectGroups,
    int ObjectAggregates,
    int RoutingItems,
    int Diagnostics,
    int DiagnosticWarnings,
    int DiagnosticErrors,
    string QualityGrade,
    double QualityConfidence,
    bool RequiresReview)
{
    public static BatchScanCounts From(PlanScanResult scan) =>
        new(
            scan.Document.Pages.Count,
            scan.SheetRegions.Count,
            scan.TitleBlocks.Count,
            scan.Dimensions.Count,
            scan.Annotations.Count,
            scan.GridAxes.Count,
            scan.GridBaySpacings.Count,
            scan.SurfacePatterns.Count,
            scan.Walls.Count,
            scan.WallGraph.Nodes.Count,
            scan.WallGraph.Edges.Count,
            scan.Rooms.Count,
            scan.RoomAdjacencyGraph.Edges.Count,
            scan.RoomAdjacencyGraph.Clusters.Count,
            scan.Openings.Count,
            scan.ObjectCandidates.Count,
            scan.ObjectGroups.Count,
            scan.ObjectAggregates.Count,
            CountRoutingItems(scan.RoutingLayer),
            scan.Diagnostics.Messages.Count,
            scan.Diagnostics.WarningCount,
            scan.Diagnostics.ErrorCount,
            scan.Quality.Grade.ToString(),
            scan.Quality.OverallConfidence.Value,
            scan.Quality.RequiresReview);

    public static BatchScanCounts Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "-", 0, true);

    private static int CountRoutingItems(PlanRoutingLayer routingLayer) =>
        routingLayer.Barriers.Count
        + routingLayer.Passages.Count
        + routingLayer.Obstacles.Count
        + routingLayer.RoomUseHints.Count;
}

internal sealed record PlanDocumentInspectionResult(
    string SchemaVersion,
    string InputPath,
    string? FileName,
    PlanSourceKind SourceKind,
    PlanSourceKind EffectiveSourceKind,
    string DocumentId,
    int PageCount,
    int PrimitiveCount,
    double LoadDurationMilliseconds,
    IReadOnlyDictionary<PlanPrimitiveKind, int> KindCounts,
    IReadOnlyDictionary<string, int> SourceFormatCounts,
    IReadOnlyDictionary<string, int> LayerCounts,
    IReadOnlyList<InspectionTextSample> TextSamples,
    IReadOnlyList<PlanPageInspectionResult> Pages)
{
    public const string CurrentSchemaVersion = "openplantrace.inspect.v1";

    public static PlanDocumentInspectionResult From(
        string inputPath,
        PlanSourceDescriptor source,
        PlanDocument document,
        TimeSpan loadDuration,
        int textSampleLimit = 0)
    {
        var pages = document.Pages
            .Select(PlanPageInspectionResult.From)
            .ToArray();
        var primitives = document.Pages.SelectMany(page => page.Primitives).ToArray();
        var textSamples = textSampleLimit <= 0
            ? Array.Empty<InspectionTextSample>()
            : document.Pages
                .SelectMany(page => page.Primitives.OfType<TextPrimitive>()
                    .OrderBy(text => text.Bounds.Top)
                    .ThenBy(text => text.Bounds.Left)
                    .Select(text => new InspectionTextSample(
                        page.Number,
                        text.Text,
                        text.Bounds,
                        text.SourceId ?? text.Source.SourceId,
                        text.Source.Layer ?? text.Layer,
                        text.FontSize)))
                .Take(textSampleLimit)
                .ToArray();

        return new PlanDocumentInspectionResult(
            CurrentSchemaVersion,
            Path.GetFullPath(inputPath),
            Path.GetFileName(inputPath),
            source.Kind,
            source.EffectiveKind,
            document.Id,
            document.Pages.Count,
            primitives.Length,
            loadDuration.TotalMilliseconds,
            CountKinds(primitives),
            CountSourceFormats(primitives),
            CountLayers(primitives),
            textSamples,
            pages);
    }

    internal static IReadOnlyDictionary<PlanPrimitiveKind, int> CountKinds(IEnumerable<PlanPrimitive> primitives) =>
        primitives
            .GroupBy(primitive => primitive.Kind)
            .OrderBy(group => group.Key)
            .ToDictionary(group => group.Key, group => group.Count());

    private static IReadOnlyDictionary<string, int> CountSourceFormats(IEnumerable<PlanPrimitive> primitives) =>
        primitives
            .Select(primitive => Clean(primitive.Source.SourceFormat) ?? "(unknown)")
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> CountLayers(IEnumerable<PlanPrimitive> primitives) =>
        primitives
            .Select(primitive => Clean(primitive.Source.Layer) ?? Clean(primitive.Layer) ?? "(none)")
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed record InspectionTextSample(
    int PageNumber,
    string Text,
    PlanRect Bounds,
    string? SourceId,
    string? Layer,
    double FontSize);

internal sealed record PlanPageInspectionResult(
    int PageNumber,
    double Width,
    double Height,
    int PrimitiveCount,
    IReadOnlyDictionary<PlanPrimitiveKind, int> KindCounts,
    IReadOnlyDictionary<string, int> SourceFormatCounts,
    IReadOnlyDictionary<string, int> LayerCounts)
{
    public static PlanPageInspectionResult From(PlanPage page)
    {
        var primitives = page.Primitives.ToArray();
        return new PlanPageInspectionResult(
            page.Number,
            page.Size.Width,
            page.Size.Height,
            primitives.Length,
            PlanDocumentInspectionResult.CountKinds(primitives),
            CountSourceFormats(primitives),
            CountLayers(primitives));
    }

    private static IReadOnlyDictionary<string, int> CountSourceFormats(IEnumerable<PlanPrimitive> primitives) =>
        primitives
            .Select(primitive => Clean(primitive.Source.SourceFormat) ?? "(unknown)")
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> CountLayers(IEnumerable<PlanPrimitive> primitives) =>
        primitives
            .Select(primitive => Clean(primitive.Source.Layer) ?? Clean(primitive.Layer) ?? "(none)")
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal sealed class BenchmarkArguments
{
    public string? ManifestPath { get; set; }

    public string? JsonPath { get; set; }

    public string? MarkdownPath { get; set; }

    public bool PrettyJson { get; set; } = true;

    public double? MinWallLength { get; set; }

    public double? MinWallFragmentLength { get; set; }

    public double? MaxWallFragmentGap { get; set; }

    public int? MaxWallCandidateSeedsPerPage { get; set; }

    public double? WallSnapTolerance { get; set; }

    public double? WallMergeTolerance { get; set; }

    public double? WallThickness { get; set; }

    public double? MinOpeningGap { get; set; }

    public double? MaxOpeningGap { get; set; }

    public double? ObjectNearbyTextSearchRadius { get; set; }

    public int? MaxNearbyTextPerObject { get; set; }

    public double? SheetMargin { get; set; }

    public List<string> LayerProfilePaths { get; } = new();

    public List<LayerCategoryOverride> LayerCategoryOverrides { get; } = new();

    public List<string> ObjectLabelProfilePaths { get; } = new();

    public static BenchmarkArguments Parse(string[] args)
    {
        var parsed = new BenchmarkArguments();

        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];

            switch (arg)
            {
                case "--json":
                    parsed.JsonPath = ReadValue(args, ref index, arg);
                    break;
                case "--markdown":
                    parsed.MarkdownPath = ReadValue(args, ref index, arg);
                    break;
                case "--compact-json":
                    parsed.PrettyJson = false;
                    break;
                case "--min-wall-length":
                    parsed.MinWallLength = ReadDouble(args, ref index, arg);
                    break;
                case "--min-wall-fragment":
                    parsed.MinWallFragmentLength = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-fragment-gap":
                    parsed.MaxWallFragmentGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-wall-candidates":
                    parsed.MaxWallCandidateSeedsPerPage = ReadInt(args, ref index, arg);
                    break;
                case "--wall-snap":
                    parsed.WallSnapTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-merge":
                    parsed.WallMergeTolerance = ReadDouble(args, ref index, arg);
                    break;
                case "--wall-thickness":
                    parsed.WallThickness = ReadDouble(args, ref index, arg);
                    break;
                case "--min-opening-gap":
                    parsed.MinOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--max-opening-gap":
                    parsed.MaxOpeningGap = ReadDouble(args, ref index, arg);
                    break;
                case "--object-nearby-text-radius":
                    parsed.ObjectNearbyTextSearchRadius = ReadDouble(args, ref index, arg);
                    break;
                case "--max-nearby-text-per-object":
                    parsed.MaxNearbyTextPerObject = ReadInt(args, ref index, arg);
                    break;
                case "--sheet-margin":
                    parsed.SheetMargin = ReadDouble(args, ref index, arg);
                    break;
                case "--layer-profile":
                    parsed.LayerProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                case "--layer-category":
                    parsed.LayerCategoryOverrides.Add(OpenPlanTraceCli.ParseLayerCategoryOverride(ReadValue(args, ref index, arg)));
                    break;
                case "--object-label-profile":
                    parsed.ObjectLabelProfilePaths.Add(ReadValue(args, ref index, arg));
                    break;
                default:
                    if (arg.StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new ArgumentException($"Unknown option: {arg}");
                    }

                    if (parsed.ManifestPath is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }

                    parsed.ManifestPath = arg;
                    break;
            }
        }

        return parsed;
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static double ReadDouble(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid number for {option}: {value}");
    }

    private static int ReadInt(string[] args, ref int index, string option)
    {
        var value = ReadValue(args, ref index, option);
        return int.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid integer for {option}: {value}");
    }
}
