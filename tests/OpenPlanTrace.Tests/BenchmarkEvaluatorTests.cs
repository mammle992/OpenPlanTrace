namespace OpenPlanTrace.Tests;

public sealed class BenchmarkEvaluatorTests
{
    [Fact]
    public async Task Evaluate_PassesWhenScanMeetsSemanticExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "semantic-smoke",
            Name = "Semantic smoke plan",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinPages = 1,
                MinRegions = 2,
                MinDimensions = 1,
                MinAnnotations = 1,
                MinGridAxes = 1,
                MinWalls = 4,
                MinWallNodes = 4,
                MinWallEdges = 4,
                MinRooms = 1,
                MinOpenings = 1,
                MinObjects = 1,
                MaxDiagnosticErrors = 0,
                RequiredRegionKinds = new[] { RegionKind.Sheet, RegionKind.MainFloorPlan },
                RequiredAnnotationKinds = new[] { PlanAnnotationKind.GeneralNotes },
                RequiredGridLabels = new[] { "A" },
                RequiredOpeningTypes = new[] { OpeningType.Door },
                RequiredOpeningOperations = new[] { OpeningOperation.Hinged },
                RequiredObjectCategories = new[] { ObjectCategory.HVACEquipment },
                RequiredRoomLabels = new[] { "OFFICE" },
                ForbiddenRoomLabels = new[] { "BOILER ROOM" },
                RequiredLayerCategories = new[] { LayerCategory.Wall }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(12));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.True(benchmark.ScanSucceeded);
        Assert.Equal("semantic-smoke", benchmark.FixtureId);
        Assert.True(benchmark.Counts.Dimensions >= 1);
        Assert.True(benchmark.Counts.Annotations >= 1);
        Assert.True(benchmark.Counts.GridAxes >= 1);
        Assert.True(benchmark.Counts.Walls >= 4);
        Assert.True(benchmark.WallPlacement.TotalWallCount >= 4);
        Assert.Equal(result.WallGraph.Edges.Count, benchmark.WallPlacement.TopologyEdgeCount);
        Assert.True(benchmark.PassedAssertionCount > 1);
        Assert.DoesNotContain(benchmark.Assertions, assertion => !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_label.forbidden.BOILER ROOM" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesSourceReadinessExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreatePdfMetadataBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "source-readiness-case",
            SourcePath = "source-readiness.pdf",
            Expectations = new BenchmarkExpectations
            {
                RequiredSourceFormat = "pdf",
                RequiredSourceLoader = "PDF/PdfPig",
                RequiredSourceKind = "Pdf",
                RequiredEffectiveSourceKind = "Pdf",
                RequiredSourceIngestionPath = "pdf-vector",
                RequiredSourceReadinessStatus = "VectorGeometryReady",
                RequiredSourceGeometryBasis = "pdf-vector-geometry",
                RequireSourceVectorGeometryReady = true,
                RequireSourceExternalAdapter = false,
                RequireSourceOcr = false,
                RequireSourceLegalAdapterBacked = true,
                RequireDwgDerivedSource = false,
                RequireRasterDerivedSource = false,
                RequiredSourceEvidenceContains = new[] { "format=pdf", "loader=PDF/PdfPig" },
                ForbiddenSourceEvidenceContains = new[] { "dwg.converter" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_format.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_loader.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_ingestion_path.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_readiness_status.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_vector_geometry_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_legal_adapter_backed.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_evidence.required.1" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_evidence.forbidden.1" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsSourceReadinessExpectationsWhenProvenanceRegresses()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreatePdfMetadataBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "source-readiness-fail-case",
            SourcePath = "source-readiness.pdf",
            Expectations = new BenchmarkExpectations
            {
                RequiredSourceFormat = "dwg",
                RequiredSourceReadinessStatus = "DwgAdapterBacked",
                RequireSourceOcr = true,
                RequiredSourceEvidenceContains = new[] { "dwg.converter" },
                ForbiddenSourceEvidenceContains = new[] { "format=pdf" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_format.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_readiness_status.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_ocr.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_evidence.required.1" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "source_evidence.forbidden.1" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsWhenRequiredSignalsAreMissing()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "impossible-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinDimensions = 99,
                MinAnnotations = 99,
                MinAnnotationReferences = 99,
                MinGridAxes = 99,
                MinGridBaySpacings = 99,
                MinRooms = 99,
                MinRoomAdjacencies = 99,
                MinRoomClusters = 99,
                RequiredAnnotationKinds = new[] { PlanAnnotationKind.Legend },
                RequiredGridLabels = new[] { "Z" },
                RequiredObjectCategories = new[] { ObjectCategory.FireSafety },
                RequiredRoomLabels = new[] { "BOILER ROOM" },
                ForbiddenRoomLabels = new[] { "OFFICE" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(4));

        Assert.False(benchmark.Passed);
        Assert.True(benchmark.ScanSucceeded);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "dimensions.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotations.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotation_references.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "grid_axes.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "grid_bay_spacings.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotation.required.Legend" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "grid_label.required.Z" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "rooms.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_adjacencies.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_clusters.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "object_category.required.FireSafety" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_label.required.BOILER ROOM" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "room_label.forbidden.OFFICE" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_PassesDiagnosticAndPerformanceExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var requiredDiagnostic = Assert.Single(result.Diagnostics.Messages, message => message.Code == "dimensions.detected");
        var fixture = new BenchmarkFixture
        {
            Id = "diagnostic-performance-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MaxDurationMilliseconds = 500,
                RequiredDiagnosticCodes = new[] { requiredDiagnostic.Code },
                ForbiddenDiagnosticCodes = new[] { "scanner.internal_error" },
                StageExpectations = new[]
                {
                    new BenchmarkStageExpectation
                    {
                        Stage = "openings",
                        MaxDurationMilliseconds = 100,
                        MaxDiagnostics = 10,
                        MaxWarnings = 0,
                        MaxErrors = 0,
                        RequireDependencyReady = true,
                        MaxMissingRequiredReads = 0,
                        MaxMissingOptionalReads = 0,
                        RequireRuntimeRequiredReadsHaveData = true,
                        RequireRuntimeOptionalReadsHaveData = true,
                        MaxEmptyRequiredRuntimeReads = 0,
                        MaxEmptyOptionalRuntimeReads = 0,
                        RequireWritesOnlyDeclaredArtifacts = true,
                        MaxUndeclaredChangedArtifacts = 0,
                        MaxEmptyDeclaredOutputs = 0,
                        ArtifactExpectations = new[]
                        {
                            new BenchmarkStageArtifactExpectation
                            {
                                Artifact = PlanArtifactKind.Openings,
                                MinAfterCount = 1,
                                MaxAfterCount = 8,
                                MinDelta = 1,
                                MaxDelta = 8,
                                RequireChanged = true
                            }
                        }
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(42));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "duration.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "diagnostic.required.dimensions.detected" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "diagnostic.forbidden.scanner.internal_error" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.present" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.duration.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.dependency.ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.dependency.missing_required.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.runtime.required_reads_have_data.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.runtime.empty_required_reads.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.contract.writes_only_declared.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.contract.undeclared_changed.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.contract.empty_declared_outputs.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.artifact.Openings.visible" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.artifact.Openings.after.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.openings.artifact.Openings.delta.max" && assertion.Passed);
        Assert.Contains(
            benchmark.DiagnosticIssues,
            issue => issue.Code == requiredDiagnostic.Code
                && issue.Stage == requiredDiagnostic.Stage
                && issue.Count >= 1);
        Assert.Contains(
            benchmark.Stages,
            stage => string.Equals(stage.Stage, "openings", StringComparison.OrdinalIgnoreCase)
                && stage.DurationMilliseconds >= 0
                && stage.OutputCount >= 0);
        var openingStage = Assert.Single(benchmark.Stages, stage => stage.Stage == "openings");
        Assert.Equal("Opening detection", openingStage.DisplayName);
        Assert.Equal(nameof(PipelineStageKind.Topology), openingStage.Kind);
        Assert.True(openingStage.DependencyLevel > 0);
        Assert.True(openingStage.PreferredDependencyLevel >= openingStage.DependencyLevel);
        Assert.True(openingStage.IsDependencyReady);
        Assert.Contains(nameof(PlanArtifactKind.WallGraph), openingStage.Reads);
        Assert.Contains(nameof(PlanArtifactKind.Openings), openingStage.Writes);
        Assert.Contains("door-gap-detection", openingStage.Capabilities);
        Assert.Empty(openingStage.MissingRequiredReads);
        Assert.True(openingStage.RuntimeReadiness.RequiredReadsHaveData);
        Assert.False(openingStage.RuntimeReadiness.HasEmptyRequiredReads);
        Assert.Contains(PlanArtifactKind.WallGraph, openingStage.RuntimeReadiness.NonEmptyRequiredReads);
        Assert.True(openingStage.Contract.WritesOnlyDeclaredArtifacts);
        Assert.Contains(PlanArtifactKind.Openings, openingStage.Contract.DeclaredWrites);
        Assert.Contains(PlanArtifactKind.Openings, openingStage.Contract.ChangedArtifacts);
        Assert.Empty(openingStage.Contract.UndeclaredChangedArtifacts);
        Assert.NotEmpty(benchmark.ArtifactPlans);
        Assert.Contains(
            benchmark.ArtifactPlans,
            plan => plan.Artifact == nameof(PlanArtifactKind.Primitives)
                && plan.IsSourceArtifact
                && plan.IsConsumedByStage
                && plan.DependencyRole == "SourceInput"
                && plan.ConsumerStages.Contains("layer-analysis"));
        Assert.Contains(
            benchmark.ArtifactPlans,
            plan => plan.Artifact == nameof(PlanArtifactKind.WallGraph)
                && plan.IsProducedByStage
                && plan.IsConsumedByStage
                && plan.DependencyRole == "ProducedAndConsumed"
                && plan.ProducerStages.Contains("wall-graph")
                && plan.RequiredConsumerStages.Contains("openings")
                && plan.Evidence.Count > 0);
        Assert.NotEmpty(benchmark.ExecutionWaves);
        Assert.Contains(
            benchmark.ExecutionWaves,
            wave => wave.IsParallelCandidate
                && wave.ParallelReadiness == "Ready"
                && wave.RecommendedExecutionMode == "Parallel"
                && wave.SchedulingReasons.Count > 0);
        Assert.Contains(
            benchmark.ExecutionWaves,
            wave => wave.DirectDownstreamStageCount > 0
                && wave.DirectDownstreamStages.Count == wave.DirectDownstreamStageCount
                && wave.DownstreamReadArtifacts.Count > 0);
        Assert.Contains(
            benchmark.RerunImpacts,
            impact => impact.Artifact == nameof(PlanArtifactKind.Walls)
                && impact.HasImpact
                && impact.ImpactScope == "DerivedArtifact"
                && impact.DirectConsumerStages.Contains("wall-graph")
                && impact.AffectedStages.Contains("routing-layer")
                && impact.AffectedArtifacts.Contains(nameof(PlanArtifactKind.WallGraph)));
        Assert.Contains(
            benchmark.RerunPlans,
            plan => plan.PlanId == "wall-geometry"
                && plan.HasWork
                && plan.ChangedArtifacts.Contains(nameof(PlanArtifactKind.Walls))
                && plan.RerunStages.Contains("routing-layer")
                && plan.AffectedArtifacts.Contains(nameof(PlanArtifactKind.WallGraph)));
    }

    [Fact]
    public async Task Evaluate_AppliesGlobalPipelineHealthExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "pipeline-health-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                RequirePipelineDependencyReady = true,
                MaxPipelinePlanIssues = 0,
                MaxPipelinePlanWarnings = 0,
                MaxPipelinePlanErrors = 0,
                RequireAllStagesDependencyReady = true,
                RequireAllStagesWriteOnlyDeclaredArtifacts = true,
                MaxTotalUndeclaredChangedArtifacts = 0
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(12));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.dependency_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.plan_issues.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.plan_warnings.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.plan_errors.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.dependency_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.writes_only_declared.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.undeclared_changed_artifacts.max" && assertion.Passed);
        Assert.True(benchmark.PipelineHealth.DependencyReady);
        Assert.Equal(0, benchmark.PipelineHealth.PlanIssueCount);
        Assert.Equal(0, benchmark.PipelineHealth.NotDependencyReadyStageCount);
        Assert.True(benchmark.PipelineHealth.WritesOnlyDeclaredArtifacts);
        Assert.Equal(0, benchmark.PipelineHealth.UndeclaredChangedArtifactCount);
        Assert.True(benchmark.PipelineHealth.EmptyRequiredRuntimeReadCount > 0);
    }

    [Fact]
    public async Task Evaluate_FailsGlobalPipelineHealthExpectationsWhenStageTelemetryRegresses()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "pipeline-health-fail-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                RequireAllStagesRuntimeRequiredReadsHaveData = true,
                MaxTotalEmptyRequiredRuntimeReads = 0,
                MaxTotalEmptyDeclaredOutputs = 0
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(12));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.runtime_required_reads_have_data.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.runtime_empty_required_reads.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "pipeline.stages.empty_declared_outputs.max" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesFinalArtifactInventoryExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "artifact-inventory-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                ArtifactExpectations = new[]
                {
                    new BenchmarkArtifactExpectation
                    {
                        Artifact = PlanArtifactKind.Walls,
                        MinCount = 4,
                        RequirePresent = true
                    },
                    new BenchmarkArtifactExpectation
                    {
                        Artifact = PlanArtifactKind.WallGraph,
                        MinCount = 4,
                        RequirePresent = true
                    },
                    new BenchmarkArtifactExpectation
                    {
                        Artifact = PlanArtifactKind.SurfacePatterns,
                        MaxCount = 0,
                        RequirePresent = false
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(12));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(
            benchmark.ArtifactInventory,
            artifact => artifact.Artifact == PlanArtifactKind.Walls && artifact.Count >= 4);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "artifact_inventory.Walls.visible" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "artifact_inventory.Walls.count.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "artifact_inventory.SurfacePatterns.present.required" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsFinalArtifactInventoryExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "bad-artifact-inventory-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                ArtifactExpectations = new[]
                {
                    new BenchmarkArtifactExpectation
                    {
                        Artifact = PlanArtifactKind.Walls,
                        MaxCount = 1
                    },
                    new BenchmarkArtifactExpectation
                    {
                        Artifact = PlanArtifactKind.SurfacePatterns,
                        RequirePresent = true
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(12));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "artifact_inventory.Walls.count.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "artifact_inventory.SurfacePatterns.present.required" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsDiagnosticAndPerformanceExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "bad-diagnostic-performance-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MaxDurationMilliseconds = 1,
                RequiredDiagnosticCodes = new[] { "missing.required_diagnostic" },
                ForbiddenDiagnosticCodes = new[] { "dimensions.detected" },
                StageExpectations = new[]
                {
                    new BenchmarkStageExpectation
                    {
                        Stage = "missing-stage",
                        MaxDurationMilliseconds = 10
                    },
                    new BenchmarkStageExpectation
                    {
                        Stage = "dimensions",
                        MaxDurationMilliseconds = 0.001,
                        MaxDiagnostics = 0,
                        ArtifactExpectations = new[]
                        {
                            new BenchmarkStageArtifactExpectation
                            {
                                Artifact = PlanArtifactKind.Dimensions,
                                MaxAfterCount = 0,
                                MaxDelta = 0,
                                RequireChanged = false
                            }
                        }
                    },
                    new BenchmarkStageExpectation
                    {
                        Stage = "raster-extraction",
                        RequireRuntimeRequiredReadsHaveData = true,
                        MaxEmptyRequiredRuntimeReads = 0,
                        MaxEmptyDeclaredOutputs = 0
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(42));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "duration.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "diagnostic.required.missing.required_diagnostic" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "diagnostic.forbidden.dimensions.detected" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.missing-stage.present" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.dimensions.duration.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.dimensions.diagnostics.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.dimensions.artifact.Dimensions.after.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.dimensions.artifact.Dimensions.delta.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.dimensions.artifact.Dimensions.changed.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.raster-extraction.runtime.required_reads_have_data.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.raster-extraction.runtime.empty_required_reads.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "stage.raster-extraction.contract.empty_declared_outputs.max" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesScanQualityExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "quality-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinQualityGrade = PlanScanQualityGrade.Poor,
                MinQualityConfidence = 0.1,
                MaxQualityIssues = result.Quality.Issues.Count
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(result.Quality.Grade, benchmark.Counts.QualityGrade);
        Assert.Equal(result.Quality.OverallConfidence.Value, benchmark.Counts.QualityConfidence);
        Assert.Equal(result.Quality.RequiresReview, benchmark.Counts.QualityRequiresReview);
        Assert.Equal(result.Quality.Issues.Count, benchmark.Counts.QualityIssues);
        Assert.Equal(result.MeasurementConsistency.CheckedCount, benchmark.Counts.MeasurementCheckedCount);
        Assert.Equal(result.MeasurementConsistency.ConsistentCount, benchmark.Counts.MeasurementConsistentCount);
        Assert.Equal(result.MeasurementConsistency.OutlierCount, benchmark.Counts.MeasurementOutlierCount);
        Assert.Equal(result.MeasurementConsistency.SelectedMillimetersPerDrawingUnit, benchmark.Counts.MeasurementSelectedMillimetersPerDrawingUnit);
        Assert.Equal(result.MeasurementConsistency.MedianDimensionMillimetersPerDrawingUnit, benchmark.Counts.MeasurementMedianMillimetersPerDrawingUnit);
        Assert.Equal(result.MeasurementConsistency.DimensionScaleSpreadRatio, benchmark.Counts.MeasurementScaleSpreadRatio);
        Assert.Equal(result.MeasurementConsistency.Confidence.Value, benchmark.Counts.MeasurementConsistencyConfidence);
        Assert.Equal(result.Quality.Issues.Count, benchmark.QualityIssues.Sum(issue => issue.Count));
        foreach (var issue in result.Quality.Issues)
        {
            Assert.Contains(
                benchmark.QualityIssues,
                summary => summary.Code == issue.Code
                    && summary.Severity == issue.Severity
                    && summary.Stage == "quality");
        }

        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_grade.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_confidence.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issues.max" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesMeasurementQualityExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "measurement-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinMeasurementCheckedCount = 1,
                MinMeasurementConsistentCount = 1,
                MaxMeasurementOutlierCount = 0,
                MaxMeasurementOutlierRatio = 0,
                MaxMeasurementScaleSpreadRatio = 1.5
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_checked_count.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_consistent_count.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_outlier_count.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_outlier_ratio.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_scale_spread_ratio.max" && assertion.Passed);
    }

    [Fact]
    public void Evaluate_FailsMeasurementQualityExpectations()
    {
        var result = CreateMeasurementGateFailureResult();
        var fixture = new BenchmarkFixture
        {
            Id = "measurement-fail-case",
            SourcePath = "measurement-conflict.pdf",
            Expectations = new BenchmarkExpectations
            {
                MinMeasurementCheckedCount = 3,
                MinMeasurementConsistentCount = 2,
                MaxMeasurementOutlierCount = 0,
                MaxMeasurementOutlierRatio = 0.1,
                MaxMeasurementScaleSpreadRatio = 1.5
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_checked_count.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_consistent_count.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_outlier_count.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_outlier_ratio.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "measurement_scale_spread_ratio.max" && !assertion.Passed);
    }

    [Fact]
    public void Evaluate_AppliesScanReviewQueueExpectations()
    {
        var result = CreateScanReviewQueueGateResult();
        var fixture = new BenchmarkFixture
        {
            Id = "scan-review-queue-case",
            SourcePath = "review-workload.pdf",
            Expectations = new BenchmarkExpectations
            {
                MaxScanReviewQueueItems = 4,
                MaxScanReviewQueueKindCounts = new Dictionary<string, int>
                {
                    [ScanReviewQueueKinds.MeasurementOutlier] = 1,
                    [ScanReviewQueueKinds.ObjectGroupReview] = 1,
                    [ScanReviewQueueKinds.OpeningReview] = 1,
                    [ScanReviewQueueKinds.WallGraphGapReview] = 1
                },
                RequiredScanReviewQueueKinds = new[] { ScanReviewQueueKinds.WallGraphGapReview },
                ForbiddenScanReviewQueueKinds = new[] { ScanReviewQueueKinds.ObjectAggregateReview }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(4, benchmark.ScanReviewQueue.Count);
        Assert.Equal(1, benchmark.ScanReviewQueue.KindCounts[ScanReviewQueueKinds.MeasurementOutlier]);
        Assert.Equal(1, benchmark.ScanReviewQueue.KindCounts[ScanReviewQueueKinds.ObjectGroupReview]);
        Assert.Equal(1, benchmark.ScanReviewQueue.KindCounts[ScanReviewQueueKinds.OpeningReview]);
        Assert.Equal(1, benchmark.ScanReviewQueue.KindCounts[ScanReviewQueueKinds.WallGraphGapReview]);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "scan_review_queue_items.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.{ScanReviewQueueKinds.WallGraphGapReview}.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.required.{ScanReviewQueueKinds.WallGraphGapReview}" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.forbidden.{ScanReviewQueueKinds.ObjectAggregateReview}" && assertion.Passed);
    }

    [Fact]
    public void Evaluate_FailsScanReviewQueueExpectations()
    {
        var result = CreateScanReviewQueueGateResult();
        var fixture = new BenchmarkFixture
        {
            Id = "scan-review-queue-fail-case",
            SourcePath = "review-workload.pdf",
            Expectations = new BenchmarkExpectations
            {
                MaxScanReviewQueueItems = 1,
                MaxScanReviewQueueKindCounts = new Dictionary<string, int>
                {
                    [ScanReviewQueueKinds.WallGraphGapReview] = 0
                },
                RequiredScanReviewQueueKinds = new[] { ScanReviewQueueKinds.SuppressedWallPatternReview },
                ForbiddenScanReviewQueueKinds = new[] { ScanReviewQueueKinds.OpeningReview }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "scan_review_queue_items.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.{ScanReviewQueueKinds.WallGraphGapReview}.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.required.{ScanReviewQueueKinds.SuppressedWallPatternReview}" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == $"scan_review_queue_kind.forbidden.{ScanReviewQueueKinds.OpeningReview}" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesQualityIssueCodeExpectations()
    {
        var document = new PlanDocument(
            "empty",
            new[] { new PlanPage(1, new PlanSize(200, 120), Array.Empty<PlanPrimitive>()) });
        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var fixture = new BenchmarkFixture
        {
            Id = "quality-issue-code-case",
            SourcePath = "empty.pdf",
            Expectations = new BenchmarkExpectations
            {
                MaxScanRiskIssues = 0,
                RequiredQualityIssueCodes = new[] { "quality.no_primitives" },
                ForbiddenQualityIssueCodes = new[] { "quality.scan_risk.sheet_contamination" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_scan_risk_issues.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issue.required.quality.no_primitives" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issue.forbidden.quality.scan_risk.sheet_contamination" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsQualityIssueCodeExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var qualityIssue = new PlanScanQualityIssue(
            "quality.scan_risk.sheet_contamination",
            DiagnosticSeverity.Warning,
            "Synthetic scan-risk issue for benchmark gating.",
            Confidence.High,
            new Dictionary<string, string>());
        result = result with
        {
            Quality = result.Quality with
            {
                Issues = result.Quality.Issues.Concat(new[] { qualityIssue }).ToArray()
            }
        };
        var fixture = new BenchmarkFixture
        {
            Id = "quality-issue-code-fail-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MaxScanRiskIssues = 0,
                RequiredQualityIssueCodes = new[] { "quality.missing_expected_issue" },
                ForbiddenQualityIssueCodes = new[] { "quality.scan_risk.sheet_contamination" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_scan_risk_issues.max" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issue.required.quality.missing_expected_issue" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issue.forbidden.quality.scan_risk.sheet_contamination" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_AppliesImportReadinessExpectations()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var readiness = PlanImportReadiness.FromScanResult(result);
        var fixture = new BenchmarkFixture
        {
            Id = "import-readiness-case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinImportReadinessGrade = readiness.ParsedGrade,
                MinImportReadinessScore = Math.Max(0, readiness.Score - 0.001),
                RequireGeometryImportReady = readiness.ReadyForGeometryImport,
                RequireMetricImportReady = readiness.ReadyForMetricImport,
                RequireRoutingImportReady = readiness.ReadyForRoutingImport,
                AllowImportReview = true,
                ForbiddenImportIssueCodes = new[] { "placement.import.no_pages" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(readiness.Grade, benchmark.ImportReadiness.Grade);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_readiness_grade.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_readiness_score.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_geometry_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_metric_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_routing_ready.required" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_review.allowed" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_issue.forbidden.placement.import.no_pages" && assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsImportReadinessExpectations()
    {
        var document = new PlanDocument(
            "empty",
            new[] { new PlanPage(1, new PlanSize(200, 120), Array.Empty<PlanPrimitive>()) });
        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var fixture = new BenchmarkFixture
        {
            Id = "import-readiness-fail-case",
            SourcePath = "empty.pdf",
            Expectations = new BenchmarkExpectations
            {
                MinImportReadinessGrade = PlanImportReadinessGrade.Usable,
                MinImportReadinessScore = 0.8,
                RequireGeometryImportReady = true,
                RequireMetricImportReady = true,
                RequireRoutingImportReady = true,
                AllowImportReview = false,
                RequiredImportIssueCodes = new[] { "placement.import.missing_expected_issue" },
                ForbiddenImportIssueCodes = new[] { "placement.import.no_walls" }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_readiness_grade.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_readiness_score.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_geometry_ready.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_metric_ready.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_routing_ready.required" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_review.allowed" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_issue.required.placement.import.missing_expected_issue" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "import_issue.forbidden.placement.import.no_walls" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_FailsWhenScanQualityIsBelowExpectations()
    {
        var document = new PlanDocument(
            "empty",
            new[] { new PlanPage(1, new PlanSize(200, 120), Array.Empty<PlanPrimitive>()) });
        var result = await new OpenPlanTraceScanner().ScanAsync(document);
        var fixture = new BenchmarkFixture
        {
            Id = "quality-fail-case",
            SourcePath = "empty.pdf",
            Expectations = new BenchmarkExpectations
            {
                MinQualityGrade = PlanScanQualityGrade.Usable,
                MinQualityConfidence = 0.95,
                MaxQualityIssues = 0
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(10));

        Assert.False(benchmark.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_grade.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_confidence.min" && !assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "quality_issues.max" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_ComputesDetectorMetricsForExpectedTargets()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var office = Assert.Single(result.Rooms, room => room.Label == "OFFICE");
        var opening = Assert.Single(result.Openings);
        var dimension = Assert.Single(result.Dimensions);
        var fixture = new BenchmarkFixture
        {
            Id = "target-metrics",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                DimensionMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "overall-width",
                            Text = "4000 mm",
                            DimensionKind = DimensionKind.Linear,
                            DimensionOrientation = DimensionOrientation.Horizontal,
                            Bounds = dimension.Bounds,
                            MinIntersectionOverUnion = 0.95
                        }
                    },
                    MinPrecision = 1.0
                },
                RoomMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "office-room",
                            PageNumber = office.PageNumber,
                            Label = "OFFICE",
                            Bounds = office.Bounds,
                            MinIntersectionOverUnion = 0.95
                        }
                    },
                    MinPrecision = 1.0
                },
                OpeningMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "hinged-door",
                            OpeningType = OpeningType.Door,
                            OpeningOperation = OpeningOperation.Hinged,
                            Bounds = opening.Bounds,
                            MinIntersectionOverUnion = 0.95
                        }
                    },
                    MinPrecision = 1.0
                },
                ObjectMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "ahu",
                            ObjectCategory = ObjectCategory.HVACEquipment,
                            Text = "AHU"
                        }
                    },
                    MinPrecision = 1.0
                },
                RoutingObstacleMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "ahu-routing-obstacle",
                            ObjectCategory = ObjectCategory.HVACEquipment,
                            ObjectKind = ObjectCandidateKind.Symbol,
                            RoutingSourceKind = RoutingSourceKind.ObjectCandidate,
                            RoutingObstacleKind = RoutingObstacleKind.HardObstacle,
                            RoutingInfluence = ObjectRoutingInfluence.HardObstacle,
                            StructuralInfluence = ObjectStructuralInfluence.FixedEquipment,
                            SuppressesChildObjects = false
                        }
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(9));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(5, benchmark.Metrics.Count);
        var dimensionMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "dimensions");
        Assert.Equal(1, dimensionMetrics.MatchedCount);
        Assert.Equal(1.0, dimensionMetrics.Precision);
        Assert.Contains(dimensionMetrics.Matches, match => match.Evidence.Contains("dimension orientation Horizontal", StringComparison.Ordinal));
        var roomMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "rooms");
        Assert.Equal(1, roomMetrics.ExpectedCount);
        Assert.Equal(1, roomMetrics.MatchedCount);
        Assert.Equal(1.0, roomMetrics.Recall);
        Assert.Equal(1.0, roomMetrics.Precision);
        Assert.Contains(roomMetrics.Matches, match => match.TargetId == "office-room" && match.Matched);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "rooms.target_recall.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "rooms.target_precision.min" && assertion.Passed);
        Assert.Contains(benchmark.Metrics, metric => metric.Detector == "openings" && metric.F1 == 1.0);
        var objectMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "objects");
        Assert.Equal(1, objectMetrics.MatchedCount);
        Assert.Equal(1.0, objectMetrics.Precision);
        var routingObstacleMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "routing_obstacles");
        Assert.Equal(1, routingObstacleMetrics.MatchedCount);
        Assert.Contains(routingObstacleMetrics.Matches, match => match.Evidence.Contains("routing obstacle kind HardObstacle", StringComparison.Ordinal));
        Assert.Contains(routingObstacleMetrics.Matches, match => match.Evidence.Contains("structural influence FixedEquipment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_ComputesAnnotationReferenceMetricsForExpectedMarkers()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateAnnotationReferenceBenchmarkDocument());
        var block = Assert.Single(result.Annotations, annotation => annotation.Kind == PlanAnnotationKind.Keynotes);
        var item = Assert.Single(block.Items, item => item.Marker == "1");
        var reference = Assert.Single(item.References);
        var fixture = new BenchmarkFixture
        {
            Id = "annotation-reference-targets",
            SourcePath = "annotation-reference-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinAnnotations = 1,
                MinAnnotationReferences = 1,
                AnnotationReferenceMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "keynote-marker-1",
                            PageNumber = item.PageNumber,
                            Marker = "1",
                            Text = "1",
                            AnnotationKind = PlanAnnotationKind.Keynotes,
                            Bounds = reference.Bounds,
                            MinIntersectionOverUnion = 0.95
                        }
                    },
                    MinPrecision = 1.0
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(6));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(1, benchmark.Counts.AnnotationReferences);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotation_references.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotation_references.target_recall.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "annotation_references.target_precision.min" && assertion.Passed);
        var metrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "annotation_references");
        Assert.Equal(1, metrics.ExpectedCount);
        Assert.Equal(1, metrics.DetectedCount);
        Assert.Equal(1, metrics.MatchedCount);
        Assert.Equal(1.0, metrics.Recall);
        Assert.Equal(1.0, metrics.Precision);
        Assert.Contains(metrics.Matches, match => match.Evidence.Contains("marker '1'", StringComparison.Ordinal));
        Assert.Contains(metrics.Matches, match => match.Evidence.Contains("annotation kind Keynotes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_FailsDetectorMetricWhenTargetIsMissed()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "missed-target",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                RoomMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "boiler-room",
                            PageNumber = 1,
                            Label = "BOILER ROOM"
                        }
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(7));

        Assert.False(benchmark.Passed);
        var roomMetrics = Assert.Single(benchmark.Metrics);
        Assert.Equal("rooms", roomMetrics.Detector);
        Assert.Equal(1, roomMetrics.ExpectedCount);
        Assert.Equal(0, roomMetrics.MatchedCount);
        Assert.Equal(1, roomMetrics.MissedCount);
        Assert.Equal(0.0, roomMetrics.Recall);
        Assert.Contains(roomMetrics.Matches, match => match.TargetId == "boiler-room" && !match.Matched);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "rooms.target_recall.min" && !assertion.Passed);
    }

    [Fact]
    public async Task Evaluate_ComputesObjectGroupMetricsForRepeatedSymbols()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateObjectGroupBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "object-group-targets",
            SourcePath = "object-groups.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinObjectGroups = 1,
                MaxObjectGroups = 1,
                ObjectGroupMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "unknown-iso-tags",
                            Text = "ISO_TAG_71",
                            ObjectCategory = ObjectCategory.GenericSymbol,
                            MinCount = 2,
                            RequiresReview = true
                        }
                    },
                    MinPrecision = 1.0
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(5));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(1, benchmark.Counts.ObjectGroups);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "object_groups.min" && assertion.Passed);
        var metrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "object_groups");
        Assert.Equal(1, metrics.MatchedCount);
        Assert.Equal(1.0, metrics.Precision);
        Assert.Contains(metrics.Matches, match => match.Evidence.Contains("count >= 2", StringComparison.Ordinal));
        Assert.Contains(metrics.Matches, match => match.Evidence.Contains("requires review True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluate_MatchesDetectedTagsForObjectsAndObjectGroups()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateTaggedObjectGroupBenchmarkDocument());
        var taggedSymbols = result.ObjectCandidates
            .Where(candidate => candidate.SymbolName == "ISO_TAG_71")
            .OrderBy(candidate => candidate.Bounds.Left)
            .ToArray();
        Assert.Equal(2, taggedSymbols.Length);
        Assert.Equal("P-101", taggedSymbols[0].DetectedTag);
        Assert.Equal("TK-201", taggedSymbols[1].DetectedTag);
        var group = Assert.Single(result.ObjectGroups, item => item.SymbolName == "ISO_TAG_71");
        Assert.Equal(new[] { "P-101", "TK-201" }, group.DetectedTags);

        var fixture = new BenchmarkFixture
        {
            Id = "tagged-object-targets",
            SourcePath = "tagged-objects.dxf",
            Expectations = new BenchmarkExpectations
            {
                ObjectMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "pump-tag",
                            Text = "ISO_TAG_71",
                            ObjectCategory = ObjectCategory.Equipment,
                            DetectedTags = new[] { "P-101" }
                        },
                        new BenchmarkDetectionTarget
                        {
                            Id = "tank-tag",
                            Text = "ISO_TAG_71",
                            ObjectCategory = ObjectCategory.Equipment,
                            DetectedTags = new[] { "TK-201" }
                        }
                    }
                },
                ObjectGroupMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "tagged-process-symbols",
                            Text = "ISO_TAG_71",
                            ObjectCategory = ObjectCategory.Equipment,
                            MinCount = 2,
                            DetectedTags = new[] { "P-101", "TK-201" }
                        }
                    },
                    MinPrecision = 1.0
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(5));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        var objectMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "objects");
        Assert.Equal(2, objectMetrics.MatchedCount);
        Assert.True(objectMetrics.Precision >= 0.5);
        Assert.Equal(objectMetrics.ExtraCount, objectMetrics.ExtraDetections.Count);
        if (objectMetrics.ExtraCount > 0)
        {
            Assert.All(objectMetrics.ExtraDetections, extra =>
            {
                Assert.False(string.IsNullOrWhiteSpace(extra.DetectionId));
                Assert.False(string.IsNullOrWhiteSpace(extra.Evidence));
            });
        }

        Assert.Contains(objectMetrics.Matches, match => match.TargetId == "pump-tag" && match.Evidence.Contains("detected tags P-101", StringComparison.Ordinal));
        Assert.Contains(objectMetrics.Matches, match => match.TargetId == "tank-tag" && match.Evidence.Contains("detected tags TK-201", StringComparison.Ordinal));
        var groupMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "object_groups");
        Assert.Equal(1, groupMetrics.MatchedCount);
        Assert.Equal(groupMetrics.ExtraCount, groupMetrics.ExtraDetections.Count);
        Assert.Contains(groupMetrics.Matches, match => match.Evidence.Contains("detected tags P-101, TK-201", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_SeparatesReviewOnlyObjectGroupsFromPrecisionScoredExtras()
    {
        var result = CreateReviewOnlyObjectGroupMetricResult();
        Assert.Contains(result.ObjectGroups, group => group.SymbolName == "GENERIC_MARKER" && group.RequiresReview);

        var fixture = new BenchmarkFixture
        {
            Id = "review-only-object-groups",
            SourcePath = "review-only-object-groups.dxf",
            Expectations = new BenchmarkExpectations
            {
                ObjectGroupMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "tagged-process-symbols",
                            Text = "ISO_TAG_71",
                            ObjectCategory = ObjectCategory.Equipment,
                            MinCount = 2,
                            RequiresReview = false
                        }
                    },
                    MinPrecision = 1.0
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(5));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        var metrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "object_groups");
        Assert.Equal(2, metrics.DetectedCount);
        Assert.Equal(1, metrics.ScoredDetectionCount);
        Assert.Equal(1, metrics.ReviewOnlyDetectionCount);
        Assert.Equal(0, metrics.ExtraCount);
        Assert.Empty(metrics.ExtraDetections);
        var reviewOnly = Assert.Single(metrics.ReviewOnlyDetections);
        Assert.Equal("GenericSymbol", reviewOnly.Category);
        Assert.True(reviewOnly.RequiresReview);
        Assert.Equal(1.0, metrics.Precision);

        var run = BenchmarkRunResult.Create("review queue", new[] { benchmark });
        var queueItem = Assert.Single(run.ReviewQueue);
        Assert.Equal(BenchmarkReviewQueueKind.ReviewOnly, queueItem.Kind);
        Assert.Equal("review-only-object-groups", queueItem.FixtureId);
        Assert.Equal("object_groups", queueItem.Detector);
        Assert.Equal(reviewOnly.DetectionId, queueItem.Detection.DetectionId);
    }

    [Fact]
    public async Task Evaluate_ComputesObjectAggregateAndRoutingHintMetrics()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateCompoundVehicleBenchmarkDocument());
        var aggregate = Assert.Single(result.ObjectAggregates, item => item.Category == ObjectCategory.Vehicle);
        var vehicleChildren = result.ObjectCandidates
            .Where(candidate => aggregate.ChildObjectIds.Contains(candidate.Id, StringComparer.Ordinal))
            .ToArray();
        Assert.True(vehicleChildren.Length >= 5);
        Assert.All(vehicleChildren, child => Assert.Contains(child.Id, result.RoutingLayer.SuppressedObjectCandidateIds));
        Assert.Equal(vehicleChildren.Length, result.RoutingLayer.SuppressedObjects.Count);
        Assert.DoesNotContain(result.RoutingLayer.Obstacles, obstacle => vehicleChildren.Any(child => child.Id == obstacle.SourceId));
        var suppressedBody = Assert.Single(result.RoutingLayer.SuppressedObjects, item => item.CandidateLabel == "CAR_BODY");

        var expectedRoutingItemsWithoutVehicleChildObstacles =
            result.RoutingLayer.Barriers.Count
            + result.RoutingLayer.Passages.Count
            + result.RoutingLayer.RoomUseHints.Count;
        var fixture = new BenchmarkFixture
        {
            Id = "compound-vehicle-routing",
            SourcePath = "compound-vehicle.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinObjectAggregates = 1,
                MinRoutingSuppressedObjects = vehicleChildren.Length,
                MaxRoutingItems = expectedRoutingItemsWithoutVehicleChildObstacles,
                ObjectAggregateMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "vehicle-aggregate",
                            Label = "car",
                            ObjectCategory = ObjectCategory.Vehicle,
                            ObjectKind = ObjectCandidateKind.Vehicle,
                            MinCount = 5,
                            RoutingInfluence = ObjectRoutingInfluence.RoomUseEvidenceOnly,
                            StructuralInfluence = ObjectStructuralInfluence.None,
                            RoomUseKind = RoomUseKind.Parking,
                            SuppressesChildObjects = true
                        }
                    },
                    MinPrecision = 1.0
                },
                RoutingSuppressedObjectMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "car-body-suppressed-for-routing",
                            Bounds = suppressedBody.CandidateBounds,
                            MinIntersectionOverUnion = 0.95,
                            ObjectCandidateId = suppressedBody.ObjectCandidateId,
                            SuppressedByAggregateId = aggregate.Id,
                            SuppressionReason = RoutingSuppressionReason.AggregateRoomUseEvidenceOnly,
                            SuppressionAction = RoutingSuppressedObjectAction.UseAggregateRoomUseHint,
                            RoomUseHintId = suppressedBody.RoomUseHintId,
                            ObjectCategory = ObjectCategory.Vehicle,
                            ObjectKind = ObjectCandidateKind.Vehicle,
                            RoutingInfluence = ObjectRoutingInfluence.RoomUseEvidenceOnly,
                            StructuralInfluence = ObjectStructuralInfluence.None
                        }
                    }
                },
                RoutingBarrierMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "structural-routing-barrier",
                            RoutingSourceKind = RoutingSourceKind.Wall
                        }
                    }
                },
                RoutingRoomUseHintMetrics = new BenchmarkDetectorMetricExpectations
                {
                    Targets = new[]
                    {
                        new BenchmarkDetectionTarget
                        {
                            Id = "parking-room-use-hint",
                            RoutingSourceKind = RoutingSourceKind.ObjectAggregate,
                            RoomUseKind = RoomUseKind.Parking,
                            Bounds = aggregate.Bounds,
                            MinIntersectionOverUnion = 0.95
                        }
                    }
                }
            }
        };

        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(8));

        Assert.True(
            benchmark.Passed,
            string.Join("; ", benchmark.Assertions.Where(assertion => !assertion.Passed).Select(assertion => $"{assertion.Name}: {assertion.Actual}")));
        Assert.Equal(1, benchmark.Counts.ObjectAggregates);
        Assert.Equal(expectedRoutingItemsWithoutVehicleChildObstacles, benchmark.Counts.RoutingItems);
        Assert.Equal(vehicleChildren.Length, benchmark.Counts.RoutingSuppressedObjects);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "object_aggregates.min" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "routing_items.max" && assertion.Passed);
        Assert.Contains(benchmark.Assertions, assertion => assertion.Name == "routing_suppressed_objects.min" && assertion.Passed);
        var aggregateMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "object_aggregates");
        Assert.Equal(1, aggregateMetrics.MatchedCount);
        Assert.Contains(aggregateMetrics.Matches, match => match.Evidence.Contains("suppresses child objects True", StringComparison.Ordinal));
        Assert.Contains(aggregateMetrics.Matches, match => match.Evidence.Contains("room use kind Parking", StringComparison.Ordinal));
        var suppressedMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "routing_suppressed_objects");
        Assert.Equal(1, suppressedMetrics.MatchedCount);
        Assert.Contains(suppressedMetrics.Matches, match => match.Evidence.Contains($"object candidate id '{suppressedBody.ObjectCandidateId}'", StringComparison.Ordinal));
        Assert.Contains(suppressedMetrics.Matches, match => match.Evidence.Contains("suppression action UseAggregateRoomUseHint", StringComparison.Ordinal));
        var routingHintMetrics = Assert.Single(benchmark.Metrics, metric => metric.Detector == "routing_room_use_hints");
        Assert.Equal(1, routingHintMetrics.MatchedCount);
        Assert.Contains(routingHintMetrics.Matches, match => match.Evidence.Contains("routing source kind ObjectAggregate", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedScan_ReturnsFailedCaseWithScanAssertion()
    {
        var fixture = new BenchmarkFixture
        {
            Id = "missing-file",
            SourcePath = "missing.dwg"
        };

        var benchmark = PlanBenchmarkEvaluator.FailedScan(
            fixture,
            "Input file not found: missing.dwg",
            TimeSpan.FromMilliseconds(1));

        Assert.False(benchmark.Passed);
        Assert.False(benchmark.ScanSucceeded);
        Assert.Equal(BenchmarkCounts.Empty, benchmark.Counts);
        var assertion = Assert.Single(benchmark.Assertions);
        Assert.Equal("scan.completed", assertion.Name);
        Assert.False(assertion.Passed);
        Assert.Equal("Input file not found: missing.dwg", benchmark.ErrorMessage);
    }

    [Fact]
    public void SkippedFixture_ReturnsSkippedCaseWithoutFailingRun()
    {
        var fixture = new BenchmarkFixture
        {
            Id = "optional-pdf",
            SourcePath = "%USERPROFILE%/Downloads/missing.pdf",
            Optional = true,
            SkipReason = "Local provided PDF fixture is not available.",
            Properties = new Dictionary<string, string>
            {
                ["difficulty"] = "hard"
            }
        };

        var benchmark = PlanBenchmarkEvaluator.SkippedFixture(
            fixture,
            fixture.SkipReason!,
            TimeSpan.FromMilliseconds(2));
        var run = BenchmarkRunResult.Create("Optional PDFs", new[] { benchmark });
        var markdown = BenchmarkMarkdownReport.Create(run);

        Assert.True(benchmark.Skipped);
        Assert.False(benchmark.Passed);
        Assert.False(benchmark.ScanSucceeded);
        Assert.Empty(benchmark.Assertions);
        Assert.Equal(1, run.SkippedCaseCount);
        Assert.Equal(0, run.FailedCaseCount);
        Assert.True(run.Passed);
        Assert.Contains("SKIP", markdown);
        Assert.Contains("Local provided PDF fixture is not available.", markdown);
    }

    [Fact]
    public void BenchmarkManifest_DeserializesDetectorMetricTargets()
    {
        const string json = """
            {
              "name": "Metric fixture",
              "fixtures": [
                {
                  "id": "case-1",
                  "sourcePath": "sample.dxf",
                  "optional": true,
                  "skipReason": "Local fixture is optional.",
                  "expectations": {
                    "maxDurationMilliseconds": 2500,
                    "requirePipelineDependencyReady": true,
                    "maxPipelinePlanIssues": 0,
                    "maxPipelinePlanWarnings": 0,
                    "maxPipelinePlanErrors": 0,
                    "requireAllStagesDependencyReady": true,
                    "requireAllStagesRuntimeRequiredReadsHaveData": false,
                    "requireAllStagesRuntimeOptionalReadsHaveData": false,
                    "maxTotalEmptyRequiredRuntimeReads": 2,
                    "maxTotalEmptyOptionalRuntimeReads": 4,
                    "requireAllStagesWriteOnlyDeclaredArtifacts": true,
                    "maxTotalUndeclaredChangedArtifacts": 0,
                    "maxTotalEmptyDeclaredOutputs": 3,
                    "minQualityGrade": "Usable",
                    "minQualityConfidence": 0.7,
                    "maxQualityIssues": 2,
                    "maxScanRiskIssues": 1,
                    "maxScanReviewQueueItems": 12,
                    "maxScanReviewQueueKindCounts": {
                      "OpeningReview": 3,
                      "WallGraphGapReview": 2
                    },
                    "requiredScanReviewQueueKinds": ["OpeningReview"],
                    "forbiddenScanReviewQueueKinds": ["SuppressedWallPatternReview"],
                    "minMeasurementCheckedCount": 2,
                    "minMeasurementConsistentCount": 1,
                    "maxMeasurementOutlierCount": 3,
                    "maxMeasurementOutlierRatio": 0.25,
                    "maxMeasurementScaleSpreadRatio": 2.5,
                    "minAnnotationReferences": 1,
                    "maxAnnotationReferences": 3,
                    "minObjectAggregates": 1,
                    "maxObjectAggregates": 2,
                    "minRoutingItems": 4,
                    "maxRoutingItems": 12,
                    "minRoutingSuppressedObjects": 5,
                    "maxRoutingSuppressedObjects": 9,
                    "requiredQualityIssueCodes": ["quality.object_groups_require_review"],
                    "forbiddenQualityIssueCodes": ["quality.scan_risk.sheet_contamination"],
                    "minRoomClusters": 1,
                    "maxRoomClusters": 2,
                    "requiredDiagnosticCodes": ["openings.symbol_tick_candidates.detected"],
                    "forbiddenDiagnosticCodes": ["scanner.internal_error"],
                    "requiredRoomLabels": ["OFFICE"],
                    "forbiddenRoomLabels": ["6,0", "frostet sidelfelt"],
                    "stageExpectations": [
                      {
                        "stage": "openings",
                        "maxDurationMilliseconds": 100,
                        "maxDiagnostics": 5,
                        "maxWarnings": 0,
                        "maxErrors": 0,
                        "requireDependencyReady": true,
                        "maxMissingRequiredReads": 0,
                        "maxMissingOptionalReads": 0,
                        "requireRuntimeRequiredReadsHaveData": true,
                        "requireRuntimeOptionalReadsHaveData": true,
                        "maxEmptyRequiredRuntimeReads": 0,
                        "maxEmptyOptionalRuntimeReads": 0,
                        "requireWritesOnlyDeclaredArtifacts": true,
                        "maxUndeclaredChangedArtifacts": 0,
                        "maxEmptyDeclaredOutputs": 0,
                        "artifactExpectations": [
                          {
                            "artifact": "Openings",
                            "minBeforeCount": 0,
                            "maxBeforeCount": 0,
                            "minAfterCount": 1,
                            "maxAfterCount": 8,
                            "minDelta": 1,
                            "maxDelta": 8,
                            "requireChanged": true
                          }
                        ]
                      }
                    ],
                    "roomMetrics": {
                      "minRecall": 0.9,
                      "targets": [
                        {
                          "id": "office",
                          "pageNumber": 1,
                          "label": "OFFICE",
                          "bounds": { "x": 100, "y": 100, "width": 300, "height": 200 },
                          "minIntersectionOverUnion": 0.5,
                          "confidence": 0.82,
                          "sourcePrimitiveIds": ["room-label-1"],
                          "sourceLayers": ["A-ROOM-TEXT"],
                          "evidence": ["label text was inside room bounds"]
                        }
                      ]
                    },
                    "openingMetrics": {
                      "targets": [
                        {
                          "id": "door",
                          "openingType": "Door",
                          "openingOperation": "Hinged"
                        }
                      ]
                    },
                    "dimensionMetrics": {
                      "targets": [
                        {
                          "id": "overall-width",
                          "dimensionKind": "Linear",
                          "dimensionOrientation": "Horizontal",
                          "text": "4000 mm"
                        }
                      ]
                    },
                    "annotationReferenceMetrics": {
                      "targets": [
                        {
                          "id": "keynote-1",
                          "marker": "1",
                          "annotationKind": "Keynotes",
                          "text": "1"
                        }
                      ]
                    },
                    "objectGroupMetrics": {
                      "targets": [
                        {
                          "id": "unknown-tags",
                          "text": "ISO_TAG_71",
                          "objectCategory": "GenericSymbol",
                          "detectedTags": ["P-101", "P-102"],
                          "minCount": 2,
                          "requiresReview": true
                        }
                      ]
                    },
                    "objectAggregateMetrics": {
                      "targets": [
                        {
                          "id": "car-aggregate",
                          "label": "car",
                          "objectCategory": "Vehicle",
                          "objectKind": "Vehicle",
                          "minCount": 5,
                          "routingInfluence": "RoomUseEvidenceOnly",
                          "structuralInfluence": "None",
                          "roomUseKind": "Parking",
                          "suppressesChildObjects": true
                        }
                      ]
                    },
                    "routingObstacleMetrics": {
                      "targets": [
                        {
                          "id": "pump-obstacle",
                          "objectCategory": "Equipment",
                          "objectKind": "Symbol",
                          "routingSourceKind": "ObjectCandidate",
                          "routingObstacleKind": "HardObstacle",
                          "routingInfluence": "HardObstacle",
                          "structuralInfluence": "FixedEquipment",
                          "suppressesChildObjects": false
                        }
                      ]
                    },
                    "routingRoomUseHintMetrics": {
                      "targets": [
                        {
                          "id": "parking-hint",
                          "routingSourceKind": "ObjectAggregate",
                          "roomUseKind": "Parking"
                        }
                      ]
                    },
                    "routingSuppressedObjectMetrics": {
                      "targets": [
                        {
                          "id": "car-body-suppression",
                          "objectCandidateId": "car-body",
                          "suppressedByAggregateId": "aggregate-car-1",
                          "suppressionReason": "AggregateRoomUseEvidenceOnly",
                          "suppressionAction": "UseAggregateRoomUseHint",
                          "roomUseHintId": "routing-room-use:aggregate-car-1",
                          "objectCategory": "Vehicle",
                          "objectKind": "Vehicle",
                          "routingInfluence": "RoomUseEvidenceOnly",
                          "structuralInfluence": "None"
                        }
                      ]
                    }
                  }
                }
              ]
            }
            """;
        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());

        var manifest = System.Text.Json.JsonSerializer.Deserialize<BenchmarkManifest>(json, options)!;

        Assert.Equal(BenchmarkManifest.CurrentSchemaVersion, manifest.SchemaVersion);
        var fixture = Assert.Single(manifest.Fixtures);
        Assert.True(fixture.Optional);
        Assert.Equal("Local fixture is optional.", fixture.SkipReason);
        Assert.Equal(2500, fixture.Expectations.MaxDurationMilliseconds);
        Assert.True(fixture.Expectations.RequirePipelineDependencyReady);
        Assert.Equal(0, fixture.Expectations.MaxPipelinePlanIssues);
        Assert.Equal(0, fixture.Expectations.MaxPipelinePlanWarnings);
        Assert.Equal(0, fixture.Expectations.MaxPipelinePlanErrors);
        Assert.True(fixture.Expectations.RequireAllStagesDependencyReady);
        Assert.False(fixture.Expectations.RequireAllStagesRuntimeRequiredReadsHaveData);
        Assert.False(fixture.Expectations.RequireAllStagesRuntimeOptionalReadsHaveData);
        Assert.Equal(2, fixture.Expectations.MaxTotalEmptyRequiredRuntimeReads);
        Assert.Equal(4, fixture.Expectations.MaxTotalEmptyOptionalRuntimeReads);
        Assert.True(fixture.Expectations.RequireAllStagesWriteOnlyDeclaredArtifacts);
        Assert.Equal(0, fixture.Expectations.MaxTotalUndeclaredChangedArtifacts);
        Assert.Equal(3, fixture.Expectations.MaxTotalEmptyDeclaredOutputs);
        Assert.Equal(PlanScanQualityGrade.Usable, fixture.Expectations.MinQualityGrade);
        Assert.Equal(0.7, fixture.Expectations.MinQualityConfidence);
        Assert.Equal(2, fixture.Expectations.MaxQualityIssues);
        Assert.Equal(1, fixture.Expectations.MaxScanRiskIssues);
        Assert.Equal(12, fixture.Expectations.MaxScanReviewQueueItems);
        Assert.Equal(3, fixture.Expectations.MaxScanReviewQueueKindCounts["OpeningReview"]);
        Assert.Equal(2, fixture.Expectations.MaxScanReviewQueueKindCounts["WallGraphGapReview"]);
        Assert.Contains("OpeningReview", fixture.Expectations.RequiredScanReviewQueueKinds);
        Assert.Contains("SuppressedWallPatternReview", fixture.Expectations.ForbiddenScanReviewQueueKinds);
        Assert.Equal(2, fixture.Expectations.MinMeasurementCheckedCount);
        Assert.Equal(1, fixture.Expectations.MinMeasurementConsistentCount);
        Assert.Equal(3, fixture.Expectations.MaxMeasurementOutlierCount);
        Assert.Equal(0.25, fixture.Expectations.MaxMeasurementOutlierRatio);
        Assert.Equal(2.5, fixture.Expectations.MaxMeasurementScaleSpreadRatio);
        Assert.Equal(1, fixture.Expectations.MinAnnotationReferences);
        Assert.Equal(3, fixture.Expectations.MaxAnnotationReferences);
        Assert.Equal(1, fixture.Expectations.MinObjectAggregates);
        Assert.Equal(2, fixture.Expectations.MaxObjectAggregates);
        Assert.Equal(4, fixture.Expectations.MinRoutingItems);
        Assert.Equal(12, fixture.Expectations.MaxRoutingItems);
        Assert.Equal(5, fixture.Expectations.MinRoutingSuppressedObjects);
        Assert.Equal(9, fixture.Expectations.MaxRoutingSuppressedObjects);
        Assert.Contains("quality.object_groups_require_review", fixture.Expectations.RequiredQualityIssueCodes);
        Assert.Contains("quality.scan_risk.sheet_contamination", fixture.Expectations.ForbiddenQualityIssueCodes);
        Assert.Equal(1, fixture.Expectations.MinRoomClusters);
        Assert.Equal(2, fixture.Expectations.MaxRoomClusters);
        Assert.Contains("openings.symbol_tick_candidates.detected", fixture.Expectations.RequiredDiagnosticCodes);
        Assert.Contains("scanner.internal_error", fixture.Expectations.ForbiddenDiagnosticCodes);
        Assert.Contains("OFFICE", fixture.Expectations.RequiredRoomLabels);
        Assert.Contains("6,0", fixture.Expectations.ForbiddenRoomLabels);
        Assert.Contains("frostet sidelfelt", fixture.Expectations.ForbiddenRoomLabels);
        var stageExpectation = Assert.Single(fixture.Expectations.StageExpectations);
        Assert.Equal("openings", stageExpectation.Stage);
        Assert.Equal(100, stageExpectation.MaxDurationMilliseconds);
        Assert.Equal(5, stageExpectation.MaxDiagnostics);
        Assert.Equal(0, stageExpectation.MaxWarnings);
        Assert.Equal(0, stageExpectation.MaxErrors);
        Assert.True(stageExpectation.RequireDependencyReady);
        Assert.Equal(0, stageExpectation.MaxMissingRequiredReads);
        Assert.Equal(0, stageExpectation.MaxMissingOptionalReads);
        Assert.True(stageExpectation.RequireRuntimeRequiredReadsHaveData);
        Assert.True(stageExpectation.RequireRuntimeOptionalReadsHaveData);
        Assert.Equal(0, stageExpectation.MaxEmptyRequiredRuntimeReads);
        Assert.Equal(0, stageExpectation.MaxEmptyOptionalRuntimeReads);
        Assert.True(stageExpectation.RequireWritesOnlyDeclaredArtifacts);
        Assert.Equal(0, stageExpectation.MaxUndeclaredChangedArtifacts);
        Assert.Equal(0, stageExpectation.MaxEmptyDeclaredOutputs);
        var artifactExpectation = Assert.Single(stageExpectation.ArtifactExpectations);
        Assert.Equal(PlanArtifactKind.Openings, artifactExpectation.Artifact);
        Assert.Equal(0, artifactExpectation.MinBeforeCount);
        Assert.Equal(0, artifactExpectation.MaxBeforeCount);
        Assert.Equal(1, artifactExpectation.MinAfterCount);
        Assert.Equal(8, artifactExpectation.MaxAfterCount);
        Assert.Equal(1, artifactExpectation.MinDelta);
        Assert.Equal(8, artifactExpectation.MaxDelta);
        Assert.True(artifactExpectation.RequireChanged);
        Assert.Equal(0.9, fixture.Expectations.RoomMetrics.MinRecall);
        var roomTarget = Assert.Single(fixture.Expectations.RoomMetrics.Targets);
        Assert.Equal("office", roomTarget.Id);
        Assert.Equal("OFFICE", roomTarget.Label);
        Assert.Equal(new PlanRect(100, 100, 300, 200), roomTarget.Bounds);
        Assert.Equal(0.82, roomTarget.Confidence);
        Assert.Contains("room-label-1", roomTarget.SourcePrimitiveIds!);
        Assert.Contains("A-ROOM-TEXT", roomTarget.SourceLayers!);
        Assert.Contains("label text was inside room bounds", roomTarget.Evidence!);
        var openingTarget = Assert.Single(fixture.Expectations.OpeningMetrics.Targets);
        Assert.Equal(OpeningType.Door, openingTarget.OpeningType);
        Assert.Equal(OpeningOperation.Hinged, openingTarget.OpeningOperation);
        var dimensionTarget = Assert.Single(fixture.Expectations.DimensionMetrics.Targets);
        Assert.Equal(DimensionKind.Linear, dimensionTarget.DimensionKind);
        Assert.Equal(DimensionOrientation.Horizontal, dimensionTarget.DimensionOrientation);
        Assert.Equal("4000 mm", dimensionTarget.Text);
        var annotationReferenceTarget = Assert.Single(fixture.Expectations.AnnotationReferenceMetrics.Targets);
        Assert.Equal("1", annotationReferenceTarget.Marker);
        Assert.Equal(PlanAnnotationKind.Keynotes, annotationReferenceTarget.AnnotationKind);
        var objectGroupTarget = Assert.Single(fixture.Expectations.ObjectGroupMetrics.Targets);
        Assert.Equal("ISO_TAG_71", objectGroupTarget.Text);
        Assert.Equal(ObjectCategory.GenericSymbol, objectGroupTarget.ObjectCategory);
        Assert.Equal(new[] { "P-101", "P-102" }, objectGroupTarget.DetectedTags);
        Assert.Equal(2, objectGroupTarget.MinCount);
        Assert.True(objectGroupTarget.RequiresReview);
        var objectAggregateTarget = Assert.Single(fixture.Expectations.ObjectAggregateMetrics.Targets);
        Assert.Equal("car", objectAggregateTarget.Label);
        Assert.Equal(ObjectCategory.Vehicle, objectAggregateTarget.ObjectCategory);
        Assert.Equal(ObjectCandidateKind.Vehicle, objectAggregateTarget.ObjectKind);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, objectAggregateTarget.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, objectAggregateTarget.StructuralInfluence);
        Assert.Equal(RoomUseKind.Parking, objectAggregateTarget.RoomUseKind);
        Assert.True(objectAggregateTarget.SuppressesChildObjects.GetValueOrDefault());
        var routingObstacleTarget = Assert.Single(fixture.Expectations.RoutingObstacleMetrics.Targets);
        Assert.Equal(RoutingSourceKind.ObjectCandidate, routingObstacleTarget.RoutingSourceKind);
        Assert.Equal(RoutingObstacleKind.HardObstacle, routingObstacleTarget.RoutingObstacleKind);
        Assert.Equal(ObjectStructuralInfluence.FixedEquipment, routingObstacleTarget.StructuralInfluence);
        Assert.False(routingObstacleTarget.SuppressesChildObjects.GetValueOrDefault());
        var roomUseTarget = Assert.Single(fixture.Expectations.RoutingRoomUseHintMetrics.Targets);
        Assert.Equal(RoutingSourceKind.ObjectAggregate, roomUseTarget.RoutingSourceKind);
        Assert.Equal(RoomUseKind.Parking, roomUseTarget.RoomUseKind);
        var suppressedTarget = Assert.Single(fixture.Expectations.RoutingSuppressedObjectMetrics.Targets);
        Assert.Equal("car-body", suppressedTarget.ObjectCandidateId);
        Assert.Equal("aggregate-car-1", suppressedTarget.SuppressedByAggregateId);
        Assert.Equal(RoutingSuppressionReason.AggregateRoomUseEvidenceOnly, suppressedTarget.SuppressionReason);
        Assert.Equal(RoutingSuppressedObjectAction.UseAggregateRoomUseHint, suppressedTarget.SuppressionAction);
        Assert.Equal("routing-room-use:aggregate-car-1", suppressedTarget.RoomUseHintId);
        Assert.Equal(ObjectCategory.Vehicle, suppressedTarget.ObjectCategory);
        Assert.Equal(ObjectCandidateKind.Vehicle, suppressedTarget.ObjectKind);
        Assert.Equal(ObjectRoutingInfluence.RoomUseEvidenceOnly, suppressedTarget.RoutingInfluence);
        Assert.Equal(ObjectStructuralInfluence.None, suppressedTarget.StructuralInfluence);
    }

    [Fact]
    public async Task BenchmarkMarkdownReport_IncludesFixturePropertiesQualityAndFailures()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "report-case",
            Name = "Report Case",
            SourcePath = "semantic-smoke.dxf",
            Properties = new Dictionary<string, string>
            {
                ["difficulty"] = "easy",
                ["planType"] = "architectural"
            },
            Expectations = new BenchmarkExpectations
            {
                MinPages = 1,
                MinQualityGrade = PlanScanQualityGrade.Poor
            }
        };
        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(15));
        var run = BenchmarkRunResult.Create("Report Suite", new[] { benchmark });

        var markdown = BenchmarkMarkdownReport.Create(run);

        Assert.Contains("# OpenPlanTrace Benchmark Report", markdown);
        Assert.Contains("Report Suite", markdown);
        Assert.Contains("report-case: Report Case", markdown);
        Assert.Contains("easy", markdown);
        Assert.Contains("architectural", markdown);
        Assert.Contains("Quality:", markdown);
        Assert.Contains("Measurement QA:", markdown);
        Assert.Contains("Pipeline Health", markdown);
        Assert.Contains("Pipeline health:", markdown);
        Assert.Contains("Final artifact inventory:", markdown);
        Assert.Contains("WallGraph", markdown);
        Assert.Contains("routing suppressed objects", markdown);
        Assert.Contains("No failing benchmark assertions.", markdown);
    }

    [Fact]
    public async Task BenchmarkMarkdownReport_IncludesStageTelemetryWhenStageHealthNeedsReview()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "stage-telemetry-case",
            Name = "Stage Telemetry Case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinPages = 1
            }
        };
        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(15));
        var stage = new BenchmarkStageSummary(
            "opening-detection",
            DurationMilliseconds: 2,
            InputCount: 12,
            OutputCount: 1,
            DiagnosticCount: 0,
            InfoCount: 0,
            WarningCount: 0,
            ErrorCount: 0,
            DisplayName: "Opening detection",
            Kind: nameof(PipelineStageKind.Topology),
            DependencyLevel: 2,
            PreferredDependencyLevel: 2,
            Reads: new[] { nameof(PlanArtifactKind.WallGraph) },
            OptionalReads: Array.Empty<string>(),
            Writes: new[] { nameof(PlanArtifactKind.Openings) },
            Capabilities: new[] { "stage-telemetry-test" },
            IsDependencyReady: false,
            MissingRequiredReads: new[] { nameof(PlanArtifactKind.WallGraph) },
            MissingOptionalReads: Array.Empty<string>(),
            InputArtifacts: new[] { new PipelineArtifactSnapshot(PlanArtifactKind.WallGraph, 0) },
            OutputArtifacts: new[] { new PipelineArtifactSnapshot(PlanArtifactKind.Openings, 0) },
            ChangedArtifacts: new[] { new PipelineArtifactChange(PlanArtifactKind.Rooms, 0, 1) },
            ArtifactDeltas: new[]
            {
                new PipelineArtifactDelta(PlanArtifactKind.Openings, 0, 0, true),
                new PipelineArtifactDelta(PlanArtifactKind.Rooms, 0, 1, false)
            })
        {
            RuntimeReadiness = new PipelineStageRuntimeReadiness(
                RequiredReadsHaveData: false,
                HasEmptyRequiredReads: true,
                NonEmptyRequiredReads: Array.Empty<PlanArtifactKind>(),
                EmptyRequiredReads: new[] { PlanArtifactKind.WallGraph },
                OptionalReadsHaveData: true,
                HasEmptyOptionalReads: false,
                NonEmptyOptionalReads: Array.Empty<PlanArtifactKind>(),
                EmptyOptionalReads: Array.Empty<PlanArtifactKind>(),
                Evidence: new[] { "synthetic empty wall graph read" }),
            Contract = PipelineStageContract.From(
                new[] { PlanArtifactKind.Openings },
                new[] { PlanArtifactKind.Rooms })
        };
        var run = BenchmarkRunResult.Create(
            "Stage telemetry suite",
            new[] { benchmark with { Stages = new[] { stage } } });

        var markdown = BenchmarkMarkdownReport.Create(run);

        Assert.Contains("## Stage Telemetry", markdown);
        Assert.Contains("opening-detection", markdown);
        Assert.Contains("empty required reads WallGraph", markdown);
        Assert.Contains("undeclared changes Rooms", markdown);
        Assert.Contains("empty declared outputs Openings", markdown);
        Assert.Contains("missing required deps WallGraph", markdown);
    }

    [Fact]
    public async Task BenchmarkMarkdownReport_IncludesPipelinePlanIssueDetails()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(CreateBenchmarkDocument());
        var fixture = new BenchmarkFixture
        {
            Id = "plan-issue-case",
            Name = "Plan Issue Case",
            SourcePath = "semantic-smoke.dxf",
            Expectations = new BenchmarkExpectations
            {
                MinPages = 1
            }
        };
        var benchmark = PlanBenchmarkEvaluator.Evaluate(fixture, result, TimeSpan.FromMilliseconds(15));
        var run = BenchmarkRunResult.Create(
            "Pipeline issue suite",
            new[]
            {
                benchmark with
                {
                    PlanIssues = new[]
                    {
                        new BenchmarkPipelinePlanIssueSummary(
                            "pipeline.artifact.producer_after_required_consumer",
                            nameof(DiagnosticSeverity.Error),
                            "wall-consumer",
                            new[] { nameof(PlanArtifactKind.Walls) },
                            "Required artifact 'Walls' is consumed before its first planned producer runs.")
                    }
                }
            });

        var markdown = BenchmarkMarkdownReport.Create(run);

        Assert.Contains("## Pipeline Plan Issues", markdown);
        Assert.Contains("pipeline.artifact.producer_after_required_consumer", markdown);
        Assert.Contains("wall-consumer", markdown);
        Assert.Contains("Walls", markdown);
    }

    [Fact]
    public void BenchmarkManifest_RejectsUnsupportedSchemaVersion()
    {
        var manifest = new BenchmarkManifest
        {
            SchemaVersion = "openplantrace.benchmark-manifest.v99"
        };

        var exception = Assert.Throws<ArgumentException>(
            () => BenchmarkManifest.ValidateSchemaVersion(manifest));

        Assert.Contains(BenchmarkManifest.CurrentSchemaVersion, exception.Message);
    }

    private static PlanDocument CreateBenchmarkDocument() =>
        new(
            "benchmark-semantic-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("room-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("room-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("room-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        RoomText("room-name", "OFFICE", new PlanRect(250, 180, 80, 18)),
                        Symbol("ahu-1", "AHU_SUPPLY_FAN", "M-HVAC-EQPM", new PlanRect(280, 235, 42, 34)),
                        GridLine("grid-a", new PlanPoint(80, 70), new PlanPoint(80, 430)),
                        GridLabel("grid-label-a", "A", new PlanRect(70, 38, 20, 16)),
                        NoteText("notes-heading", "GENERAL NOTES", new PlanRect(560, 60, 120, 18)),
                        NoteText("notes-1", "1. KEEP ACCESS CLEAR", new PlanRect(560, 84, 150, 18)),
                        DimensionLine("dim-line-1", new PlanPoint(100, 420), new PlanPoint(500, 420)),
                        DimensionText("dim-text-1", "4000 mm", new PlanRect(244, 434, 72, 16)),
                        Wall("door-wall-left", new PlanPoint(560, 180), new PlanPoint(620, 180)),
                        Wall("door-wall-right", new PlanPoint(650, 180), new PlanPoint(740, 180)),
                        new ArcPrimitive(new PlanPoint(620, 180), 30, 0, Math.PI / 2)
                        {
                            SourceId = "door-swing",
                            Layer = "A-DOOR",
                            Source = Source("door-swing", "ARC", "A-DOOR")
                        }
                    })
            });

    private static PlanDocument CreatePdfMetadataBenchmarkDocument() =>
        CreateBenchmarkDocument() with
        {
            Metadata = new PlanMetadata
            {
                SourceName = "source-readiness.pdf",
                SourcePath = @"C:\plans\source-readiness.pdf",
                Properties = new Dictionary<string, string>
                {
                    ["format"] = "pdf",
                    ["loader"] = "PDF/PdfPig",
                    ["sourceKind"] = "Pdf",
                    ["effectiveSourceKind"] = "Pdf",
                    ["fileExtension"] = ".pdf",
                    ["contentType"] = "application/pdf",
                    ["pdf.imageOnlyPageCount"] = "0"
                }
            }
        };

    private static PlanDocument CreateObjectGroupBenchmarkDocument() =>
        new(
            "benchmark-object-groups",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("room-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("room-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("room-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        Symbol("unknown-tag-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        Symbol("unknown-tag-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(300, 220, 26, 26))
                    })
            });

    private static PlanDocument CreateTaggedObjectGroupBenchmarkDocument() =>
        new(
            "benchmark-tagged-object-groups",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("room-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("room-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("room-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        RoomText("process-room", "PROCESS", new PlanRect(260, 160, 80, 16)),
                        Symbol("pump-symbol-1", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(180, 180, 26, 26)),
                        TagText("pump-tag-1", "P-101", new PlanRect(212, 184, 48, 14)),
                        Symbol("pump-symbol-2", "ISO_TAG_71", "X-SYMBOLS", new PlanRect(300, 220, 26, 26)),
                        TagText("pump-tag-2", "TK-201", new PlanRect(332, 224, 58, 14))
                    })
            });

    private static PlanDocument CreateCompoundVehicleBenchmarkDocument() =>
        new(
            "benchmark-compound-vehicle-object",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(760, 520),
                    new PlanPrimitive[]
                    {
                        Wall("wall-top", new PlanPoint(80, 80), new PlanPoint(680, 80)),
                        Wall("wall-right", new PlanPoint(680, 80), new PlanPoint(680, 440)),
                        Wall("wall-bottom", new PlanPoint(680, 440), new PlanPoint(80, 440)),
                        Wall("wall-left", new PlanPoint(80, 440), new PlanPoint(80, 80)),
                        RoomText("garage-label", "GARASJE", new PlanRect(325, 130, 72, 16)),
                        Symbol("car-body", "CAR_BODY", "A-VEHICLE-CAR", new PlanRect(250, 245, 110, 42)),
                        Symbol("car-wheel-front-left", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(260, 238, 18, 18)),
                        Symbol("car-wheel-front-right", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(332, 238, 18, 18)),
                        Symbol("car-wheel-back-left", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(260, 276, 18, 18)),
                        Symbol("car-wheel-back-right", "CAR_WHEEL", "A-VEHICLE-CAR", new PlanRect(332, 276, 18, 18))
                    })
            });

    private static PlanDocument CreateAnnotationReferenceBenchmarkDocument() =>
        new(
            "benchmark-annotation-references",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        Wall("room-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        Wall("room-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        Wall("room-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        Wall("room-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        NoteText("keynotes-heading", "KEYNOTES", new PlanRect(560, 120, 120, 18)),
                        NoteText("keynote-1", "1. VERIFY ACCESS CLEARANCE", new PlanRect(560, 145, 190, 18)),
                        MarkerText("keynote-marker-1", "1", new PlanRect(220, 170, 10, 10)),
                        new RectanglePrimitive(new PlanRect(215, 165, 22, 22))
                        {
                            SourceId = "keynote-bubble-1",
                            Layer = "A-ANNO",
                            Source = Source("keynote-bubble-1", "LWPOLYLINE", "A-ANNO")
                        }
                    })
            });

    private static PlanDocument CreateMeasurementConflictDocument() =>
        new(
            "benchmark-measurement-conflict",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(800, 600),
                    new PlanPrimitive[]
                    {
                        PdfLine("wall-top", new PlanPoint(100, 100), new PlanPoint(500, 100)),
                        PdfLine("wall-right", new PlanPoint(500, 100), new PlanPoint(500, 350)),
                        PdfLine("wall-bottom", new PlanPoint(500, 350), new PlanPoint(100, 350)),
                        PdfLine("wall-left", new PlanPoint(100, 350), new PlanPoint(100, 100)),
                        PdfText("scale-text", "SCALE: 1:100", new PlanRect(620, 520, 90, 16)),
                        PdfLine("dim-line-a", new PlanPoint(100, 430), new PlanPoint(200, 430)),
                        PdfLine("dim-witness-a-left", new PlanPoint(100, 412), new PlanPoint(100, 448)),
                        PdfLine("dim-witness-a-right", new PlanPoint(200, 412), new PlanPoint(200, 448)),
                        PdfText("dim-text-a", "3000 mm", new PlanRect(124, 444, 70, 16)),
                        PdfLine("dim-line-b", new PlanPoint(280, 430), new PlanPoint(480, 430)),
                        PdfLine("dim-witness-b-left", new PlanPoint(280, 412), new PlanPoint(280, 448)),
                        PdfLine("dim-witness-b-right", new PlanPoint(480, 412), new PlanPoint(480, 448)),
                        PdfText("dim-text-b", "3000 mm", new PlanRect(344, 444, 70, 16))
                    })
            })
        {
            Metadata = new PlanMetadata
            {
                Properties = new Dictionary<string, string>
                {
                    ["format"] = "pdf"
                }
            }
        };

    private static PlanScanResult CreateReviewOnlyObjectGroupMetricResult()
    {
        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "benchmark-review-only-object-groups",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(100, 100),
                        Array.Empty<PlanPrimitive>())
                }),
            PlanLayerAnalysis.Empty,
            PlanCalibration.Empty,
            MeasurementConsistencyReport.Empty,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            Array.Empty<WallSegment>(),
            WallGraph.Empty,
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            new[]
            {
                new ObjectCandidateGroup(
                    "object-group:confirmed",
                    "symbol:iso_tag_71|category:Equipment|kind:Symbol|layers:x-symbols",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.Equipment,
                    2,
                    new PlanRect(10, 10, 20, 20),
                    new[] { 1 },
                    new[] { "object:1", "object:2" },
                    Array.Empty<string>(),
                    false,
                    Confidence.High,
                    new[] { "confirmed tagged equipment group" })
                {
                    Label = "ISO_TAG_71",
                    SymbolName = "ISO_TAG_71"
                },
                new ObjectCandidateGroup(
                    "object-group:review",
                    "symbol:generic_marker|category:GenericSymbol|kind:Symbol|layers:x-symbols",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.GenericSymbol,
                    2,
                    new PlanRect(60, 10, 10, 10),
                    new[] { 1 },
                    new[] { "object:3", "object:4" },
                    Array.Empty<string>(),
                    true,
                    Confidence.Medium,
                    new[] { "review recommended for generic/unknown symbol group" })
                {
                    Label = "GENERIC_MARKER",
                    SymbolName = "GENERIC_MARKER"
                }
            },
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()))
        {
            Quality = PlanScanQualityReport.Empty
        };
    }

    private static PlanScanResult CreateScanReviewQueueGateResult()
    {
        var measurement = new MeasurementConsistencyReport(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 30,
            MedianDimensionMillimetersPerDrawingUnit: 30,
            DimensionScaleSpreadRatio: null,
            Confidence: Confidence.Medium,
            Checks: new[]
            {
                MeasurementCheck(
                    "dimension-outlier",
                    MeasurementConsistencyStatus.Outlier,
                    dimensionMillimeters: 3000,
                    drawingLength: 200,
                    impliedMillimetersPerDrawingUnit: 15,
                    relativeError: 0.5)
            });

        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "benchmark-scan-review-queue",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(100, 100),
                        Array.Empty<PlanPrimitive>())
                }),
            PlanLayerAnalysis.Empty,
            new PlanCalibration(
                PlanMeasurementUnit.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                null,
                30,
                Confidence.High,
                Array.Empty<CalibrationEvidence>(),
                Array.Empty<CalibrationScaleGroup>()),
            measurement,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            Array.Empty<WallSegment>(),
            WallGraph.Empty,
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            new[]
            {
                new OpeningCandidate(
                    "opening-review",
                    1,
                    OpeningType.Door,
                    new PlanRect(20, 20, 10, 8),
                    Confidence.Medium)
                {
                    CenterLine = new PlanLineSegment(new PlanPoint(20, 24), new PlanPoint(30, 24)),
                    Operation = OpeningOperation.Unknown
                }
            },
            Array.Empty<ObjectCandidate>(),
            new[]
            {
                new ObjectCandidateGroup(
                    "object-group-review",
                    "symbol:generic|category:GenericSymbol|kind:Symbol",
                    ObjectCandidateKind.Symbol,
                    ObjectCategory.GenericSymbol,
                    3,
                    new PlanRect(40, 20, 10, 10),
                    new[] { 1 },
                    new[] { "object:1", "object:2", "object:3" },
                    Array.Empty<string>(),
                    true,
                    Confidence.Medium,
                    new[] { "review recommended for repeated unknown symbol group" })
            },
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                new[]
                {
                    new PlanDiagnostic(
                        "wall_graph.endpoint_gap.review",
                        DiagnosticSeverity.Warning,
                        "wall-graph",
                        "A wall graph endpoint needs review.")
                    {
                        Scope = DiagnosticScope.Detection,
                        PageNumber = 1,
                        Region = new PlanRect(60, 20, 12, 12),
                        Confidence = Confidence.Medium,
                        SourcePrimitiveIds = new[] { "wall-a", "wall-b" },
                        Properties = new Dictionary<string, string>
                        {
                            ["gapDistance"] = "12",
                            ["wallIds"] = "wall-a,wall-b"
                        }
                    }
                }))
        {
            Quality = PlanScanQualityReport.Empty
        };
    }

    private static PlanScanResult CreateMeasurementGateFailureResult()
    {
        var measurement = new MeasurementConsistencyReport(
            HasReliableCalibration: true,
            SelectedMillimetersPerDrawingUnit: 30,
            MedianDimensionMillimetersPerDrawingUnit: 22.5,
            DimensionScaleSpreadRatio: 2,
            Confidence: Confidence.Medium,
            Checks: new[]
            {
                MeasurementCheck(
                    "dimension-consistent",
                    MeasurementConsistencyStatus.Consistent,
                    dimensionMillimeters: 3000,
                    drawingLength: 100,
                    impliedMillimetersPerDrawingUnit: 30,
                    relativeError: 0),
                MeasurementCheck(
                    "dimension-outlier",
                    MeasurementConsistencyStatus.Outlier,
                    dimensionMillimeters: 3000,
                    drawingLength: 200,
                    impliedMillimetersPerDrawingUnit: 15,
                    relativeError: 0.5)
            });

        var now = DateTimeOffset.UtcNow;
        return new PlanScanResult(
            new PlanDocument(
                "benchmark-measurement-gates",
                new[]
                {
                    new PlanPage(
                        1,
                        new PlanSize(100, 100),
                        Array.Empty<PlanPrimitive>())
                }),
            PlanLayerAnalysis.Empty,
            new PlanCalibration(
                PlanMeasurementUnit.DrawingUnit,
                PlanMeasurementUnit.Millimeter,
                null,
                30,
                Confidence.High,
                Array.Empty<CalibrationEvidence>(),
                Array.Empty<CalibrationScaleGroup>()),
            measurement,
            Array.Empty<TitleBlockAnalysis>(),
            Array.Empty<DimensionAnnotation>(),
            Array.Empty<PlanAnnotationBlock>(),
            Array.Empty<GridAxis>(),
            Array.Empty<GridBaySpacing>(),
            Array.Empty<SheetRegion>(),
            Array.Empty<SurfacePatternCandidate>(),
            Array.Empty<WallSegment>(),
            WallGraph.Empty,
            Array.Empty<RoomRegion>(),
            RoomAdjacencyGraph.Empty,
            Array.Empty<OpeningCandidate>(),
            Array.Empty<ObjectCandidate>(),
            Array.Empty<ObjectCandidateGroup>(),
            Array.Empty<ObjectAggregate>(),
            new PipelineDiagnostics(
                now,
                now,
                Array.Empty<PipelineStageReport>(),
                Array.Empty<PlanDiagnostic>()))
        {
            Quality = PlanScanQualityReport.Empty
        };
    }

    private static MeasurementConsistencyCheck MeasurementCheck(
        string dimensionId,
        MeasurementConsistencyStatus status,
        double dimensionMillimeters,
        double drawingLength,
        double impliedMillimetersPerDrawingUnit,
        double relativeError) =>
        new(
            dimensionId,
            1,
            status,
            dimensionMillimeters,
            drawingLength,
            impliedMillimetersPerDrawingUnit,
            30,
            drawingLength * 30,
            dimensionMillimeters - (drawingLength * 30),
            relativeError,
            Confidence.Medium,
            new[] { dimensionId },
            new[] { $"Synthetic {status.ToString().ToLowerInvariant()} measurement check." });

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = Source(sourceId, "LINE", "A-WALL")
        };

    private static TextPrimitive RoomText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-ROOM-NAME",
            Source = Source(sourceId, "TEXT", "A-ROOM-NAME")
        };

    private static LinePrimitive DimensionLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "LINE", "A-DIM")
        };

    private static LinePrimitive GridLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-GRID",
            Source = Source(sourceId, "LINE", "A-GRID")
        };

    private static TextPrimitive GridLabel(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-GRID",
            Source = Source(sourceId, "TEXT", "A-GRID")
        };

    private static TextPrimitive DimensionText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-DIM",
            Source = Source(sourceId, "TEXT", "A-DIM")
        };

    private static LinePrimitive PdfLine(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Source = PdfSource(sourceId, "line")
        };

    private static TextPrimitive PdfText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Source = PdfSource(sourceId, "word")
        };

    private static TextPrimitive NoteText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-NOTE",
            Source = Source(sourceId, "TEXT", "A-NOTE")
        };

    private static TextPrimitive MarkerText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "A-ANNO",
            Source = Source(sourceId, "TEXT", "A-ANNO")
        };

    private static TextPrimitive TagText(string sourceId, string text, PlanRect bounds) =>
        new(text, bounds)
        {
            SourceId = sourceId,
            Layer = "P-TAG",
            Source = Source(sourceId, "TEXT", "P-TAG")
        };

    private static SymbolPrimitive Symbol(string sourceId, string name, string layer, PlanRect bounds) =>
        new(name, bounds)
        {
            SourceId = sourceId,
            Layer = layer,
            Source = Source(sourceId, "INSERT", layer, blockName: name)
        };

    private static PrimitiveSourceMetadata Source(
        string sourceId,
        string entityType,
        string layer,
        string? blockName = null) =>
        new()
        {
            SourceFormat = "test",
            SourceId = sourceId,
            EntityType = entityType,
            Layer = layer,
            BlockName = blockName,
            DrawingSpace = SourceDrawingSpace.Model
        };

    private static PrimitiveSourceMetadata PdfSource(string sourceId, string entityType) =>
        new()
        {
            SourceFormat = "pdf",
            SourceId = sourceId,
            EntityType = entityType,
            DrawingSpace = SourceDrawingSpace.Paper
        };
}
