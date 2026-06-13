using System.Text;

namespace OpenPlanTrace.Tests;

public sealed class DwgToDxfPlanDocumentLoaderTests
{
    [Fact]
    public async Task LoadAsync_UsesSuppliedConverterAndDxfLoaderWithoutNativeDwgParsing()
    {
        var converter = new RecordingDwgConverter(CreateMinimalDxf());
        var loader = new DwgToDxfPlanDocumentLoader(converter);
        await using var dwgStream = new MemoryStream([0x44, 0x57, 0x47]);
        var source = PlanSourceDescriptor.FromFilePath(@"C:\plans\industrial.dwg");

        var document = await loader.LoadAsync(dwgStream, source);

        Assert.Equal("industrial.dwg", document.Id);
        Assert.Equal("industrial.dwg", document.Metadata.SourceName);
        Assert.Equal(@"C:\plans\industrial.dwg", document.Metadata.SourcePath);
        Assert.Equal("dwg", document.Metadata.Properties["format"]);
        Assert.Equal("Dwg", document.Metadata.Properties["sourceKind"]);
        Assert.Equal("Dwg", document.Metadata.Properties["effectiveSourceKind"]);
        Assert.Equal(".dwg", document.Metadata.Properties["fileExtension"]);
        Assert.Equal("dwg-to-dxf", document.Metadata.Properties["dwg.conversion"]);
        Assert.Equal("RecordingDWG", document.Metadata.Properties["dwg.converter"]);
        Assert.Equal("DXF/IxMilia", document.Metadata.Properties["dwg.dxfLoader"]);
        Assert.Equal("dxf", document.Metadata.Properties["dwg.intermediateFormat"]);
        Assert.Equal("DXF/IxMilia", document.Metadata.Properties["dwg.intermediateLoader"]);
        Assert.Equal("AC1032", document.Metadata.Properties["dwg.converter.inputVersion"]);
        Assert.Contains(document.Pages.SelectMany(page => page.Primitives), primitive => primitive is LinePrimitive);

        Assert.True(converter.WasCalled);
        Assert.Equal(PlanSourceKind.Dwg, converter.LastSource?.Kind);
    }

    [Fact]
    public void CanLoad_OnlyAcceptsDwgSources()
    {
        var loader = new DwgToDxfPlanDocumentLoader(new RecordingDwgConverter(CreateMinimalDxf()));

        Assert.True(loader.CanLoad(PlanSourceDescriptor.FromFileNameOrExtension(".dwg")));
        Assert.False(loader.CanLoad(PlanSourceDescriptor.FromFileNameOrExtension(".dxf")));
        Assert.Contains(PlanSourceKind.Dwg, loader.SupportedSourceKinds);
        Assert.DoesNotContain(PlanSourceKind.Dxf, loader.SupportedSourceKinds);
    }

    [Fact]
    public void Constructor_RejectsDelegatedLoaderThatDoesNotSupportDxf()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new DwgToDxfPlanDocumentLoader(
                new RecordingDwgConverter(CreateMinimalDxf()),
                new RecordingLoader("pdf-loader", PlanSourceKind.Pdf)));

        Assert.Contains("DXF", exception.Message);
    }

    [Fact]
    public void CapabilityCatalog_ReportsDwgRegisteredWhenBridgeLoaderIsRegistered()
    {
        var registry = new PlanDocumentLoaderRegistry(
            new IPlanDocumentLoader[]
            {
                new DwgToDxfPlanDocumentLoader(new RecordingDwgConverter(CreateMinimalDxf()))
            });

        var dwg = registry.GetCapability(PlanSourceDescriptor.FromFileNameOrExtension(".dwg"));

        Assert.True(dwg.CanLoad);
        Assert.Equal(PlanSourceSupportStatus.Registered, dwg.Status);
        Assert.Contains("DWG-to-DXF/RecordingDWG", dwg.RegisteredLoaderNames);
    }

    [Fact]
    public async Task ExternalDwgToDxfConverter_RunsConfiguredCommandAndReturnsAuditableDxf()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var command = CreateExternalConverterCommand(tempDirectory, CreateMinimalDxf());
            var converter = new ExternalDwgToDxfConverter(
                new ExternalDwgToDxfConverterOptions
                {
                    ConverterName = "ScriptedDWG",
                    ExecutablePath = command.ExecutablePath,
                    Arguments = command.Arguments,
                    Timeout = TimeSpan.FromSeconds(10),
                    Properties = new Dictionary<string, string>
                    {
                        ["licenseBoundary"] = "test-script"
                    }
                });
            await using var dwgStream = new MemoryStream([0x44, 0x57, 0x47]);
            var source = PlanSourceDescriptor.FromFilePath(Path.Combine(tempDirectory, "plant-room.dwg"));

            await using var conversion = await converter.ConvertAsync(dwgStream, source);
            using var reader = new StreamReader(conversion.DxfStream, Encoding.ASCII);
            var dxf = await reader.ReadToEndAsync();

            Assert.Equal("plant-room.dxf", conversion.DxfName);
            Assert.Contains("LINE", dxf);
            Assert.Equal("external-process", conversion.Properties["executionMode"]);
            Assert.Equal("0", conversion.Properties["exitCode"]);
            Assert.Equal("plant-room.dxf", conversion.Properties["outputFileName"]);
            Assert.Equal("test-script", conversion.Properties["licenseBoundary"]);
            Assert.True(int.Parse(conversion.Properties["outputBytes"]) > 0);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task LoadAsync_CanUseExternalProcessConverterWithoutRegisteringNativeDwgParser()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var command = CreateExternalConverterCommand(tempDirectory, CreateMinimalDxf());
            var converter = new ExternalDwgToDxfConverter(
                new ExternalDwgToDxfConverterOptions
                {
                    ConverterName = "ScriptedDWG",
                    ExecutablePath = command.ExecutablePath,
                    Arguments = command.Arguments,
                    Timeout = TimeSpan.FromSeconds(10)
                });
            var loader = new DwgToDxfPlanDocumentLoader(converter);
            await using var dwgStream = new MemoryStream([0x44, 0x57, 0x47]);
            var source = PlanSourceDescriptor.FromFilePath(Path.Combine(tempDirectory, "plant-room.dwg"));

            var document = await loader.LoadAsync(dwgStream, source);

            Assert.Equal("plant-room.dwg", document.Id);
            Assert.Equal("dwg", document.Metadata.Properties["format"]);
            Assert.Equal("dwg-to-dxf", document.Metadata.Properties["dwg.conversion"]);
            Assert.Equal("ScriptedDWG", document.Metadata.Properties["dwg.converter"]);
            Assert.Equal("external-process", document.Metadata.Properties["dwg.converter.executionMode"]);
            Assert.Equal("plant-room.dxf", document.Metadata.Properties["dwg.convertedDxfName"]);
            Assert.Contains(document.Pages.SelectMany(page => page.Primitives), primitive => primitive is LinePrimitive);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public async Task ExternalDwgToDxfConverter_ReportsFailingCommand()
    {
        var tempDirectory = CreateTempDirectory();
        try
        {
            var command = CreateFailingExternalConverterCommand(tempDirectory);
            var converter = new ExternalDwgToDxfConverter(
                new ExternalDwgToDxfConverterOptions
                {
                    ConverterName = "FailingDWG",
                    ExecutablePath = command.ExecutablePath,
                    Arguments = command.Arguments,
                    Timeout = TimeSpan.FromSeconds(10)
                });

            var exception = await Assert.ThrowsAsync<PlanLoadException>(async () =>
            {
                await using var dwgStream = new MemoryStream([0x44, 0x57, 0x47]);
                await using var _ = await converter.ConvertAsync(
                    dwgStream,
                    PlanSourceDescriptor.FromFilePath(Path.Combine(tempDirectory, "broken.dwg")));
            });

            Assert.Contains("exited with code", exception.Message);
            Assert.Contains("converter failed", exception.Message);
        }
        finally
        {
            TryDeleteDirectory(tempDirectory);
        }
    }

    [Fact]
    public void ExternalDwgToDxfConverter_RequiresExplicitInputAndOutputPlaceholders()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ExternalDwgToDxfConverter(
                new ExternalDwgToDxfConverterOptions
                {
                    ConverterName = "BadConfig",
                    ExecutablePath = "converter",
                    Arguments = new[] { "{input}" }
                }));

        Assert.Contains("{output}", exception.Message);
    }

    private static string CreateMinimalDxf() =>
        """
        0
        SECTION
        2
        ENTITIES
        0
        LINE
        8
        WALLS
        10
        0
        20
        0
        11
        100
        21
        0
        0
        ENDSEC
        0
        EOF
        """;

    private static ExternalCommand CreateExternalConverterCommand(string tempDirectory, string dxf)
    {
        var fixturePath = Path.Combine(tempDirectory, "fixture.dxf");
        File.WriteAllText(fixturePath, dxf, Encoding.ASCII);

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempDirectory, "dwg-converter.cmd");
            File.WriteAllText(
                scriptPath,
                """
                @echo off
                copy /Y "%~3" "%~2" >nul
                if errorlevel 1 exit /b 4
                exit /b 0
                """,
                Encoding.ASCII);

            return new ExternalCommand(
                "cmd.exe",
                new[] { "/c", scriptPath, "{input}", "{output}", fixturePath });
        }

        var shellScriptPath = Path.Combine(tempDirectory, "dwg-converter.sh");
        File.WriteAllText(
            shellScriptPath,
            """
            #!/bin/sh
            cp "$3" "$2"
            """,
            Encoding.ASCII);

        return new ExternalCommand(
            "/bin/sh",
            new[] { shellScriptPath, "{input}", "{output}", fixturePath });
    }

    private static ExternalCommand CreateFailingExternalConverterCommand(string tempDirectory)
    {
        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(tempDirectory, "dwg-converter-fail.cmd");
            File.WriteAllText(
                scriptPath,
                """
                @echo off
                echo converter failed 1>&2
                exit /b 7
                """,
                Encoding.ASCII);

            return new ExternalCommand(
                "cmd.exe",
                new[] { "/c", scriptPath, "{input}", "{output}" });
        }

        var shellScriptPath = Path.Combine(tempDirectory, "dwg-converter-fail.sh");
        File.WriteAllText(
            shellScriptPath,
            """
            #!/bin/sh
            echo converter failed >&2
            exit 7
            """,
            Encoding.ASCII);

        return new ExternalCommand(
            "/bin/sh",
            new[] { shellScriptPath, "{input}", "{output}" });
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"openplantrace-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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

    private sealed record ExternalCommand(
        string ExecutablePath,
        IReadOnlyList<string> Arguments);

    private sealed class RecordingDwgConverter : IDwgToDxfConverter
    {
        private readonly string dxf;

        public RecordingDwgConverter(string dxf)
        {
            this.dxf = dxf;
        }

        public string ConverterName => "RecordingDWG";

        public bool WasCalled { get; private set; }

        public PlanSourceDescriptor? LastSource { get; private set; }

        public ValueTask<DwgToDxfConversionResult> ConvertAsync(
            Stream dwgStream,
            PlanSourceDescriptor source,
            PlanLoadOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            LastSource = source;
            var stream = new MemoryStream(Encoding.ASCII.GetBytes(dxf));
            var result = new DwgToDxfConversionResult(
                stream,
                "industrial.converted.dxf",
                new Dictionary<string, string>
                {
                    ["inputVersion"] = "AC1032"
                });

            return ValueTask.FromResult(result);
        }
    }

    private sealed class RecordingLoader : PlanDocumentLoaderBase
    {
        public RecordingLoader(string formatName, params PlanSourceKind[] supportedSourceKinds)
            : base(formatName, supportedSourceKinds)
        {
        }

        public override ValueTask<PlanDocument> LoadAsync(
            Stream stream,
            PlanSourceDescriptor source,
            PlanLoadOptions? options = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new PlanDocument("recording", Array.Empty<PlanPage>()));
    }
}
