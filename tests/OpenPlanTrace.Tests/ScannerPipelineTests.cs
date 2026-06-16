namespace OpenPlanTrace.Tests;

public sealed class ScannerPipelineTests
{
    [Fact]
    public async Task ScanAsync_DetectsCoreFloorplanOutputs()
    {
        var document = new PlanDocument(
            "synthetic-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(500, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 100), new PlanPoint(500, 400))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(500, 400), new PlanPoint(100, 400))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 400), new PlanPoint(100, 100))) { SourceId = "wall-left" },
                        new RectanglePrimitive(new PlanRect(700, 650, 260, 120)) { SourceId = "title-grid" },
                        new TextPrimitive("PROJECT OPENPLANTRACE", new PlanRect(720, 670, 150, 18)) { SourceId = "title-project" },
                        new TextPrimitive("A-101", new PlanRect(720, 700, 50, 18)) { SourceId = "title-sheet" },
                        new TextPrimitive("REV 01", new PlanRect(840, 700, 50, 18)) { SourceId = "title-revision" },
                        new TextPrimitive("GENERAL NOTES", new PlanRect(650, 120, 130, 18)) { SourceId = "notes-heading" },
                        new TextPrimitive("VERIFY ALL DIMENSIONS", new PlanRect(650, 145, 180, 18)) { SourceId = "notes-body" },
                        new TextPrimitive("12'-0\"", new PlanRect(170, 520, 60, 18)) { SourceId = "dim-1" },
                        new TextPrimitive("8'-0\"", new PlanRect(300, 520, 60, 18)) { SourceId = "dim-2" }
                    })
            });

        var scanner = new OpenPlanTraceScanner();
        var result = await scanner.ScanAsync(document);

        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Sheet);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.MainFloorPlan);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.TitleBlock);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Notes);
        Assert.Contains(result.SheetRegions, region => region.Kind == RegionKind.Dimensions);
        Assert.Contains(result.Annotations, annotation => annotation.Kind == PlanAnnotationKind.GeneralNotes);
        Assert.True(result.Walls.Count >= 4);
        Assert.True(result.WallGraph.Nodes.Count >= 4);
        Assert.True(result.WallGraph.Edges.Count >= 4);
        Assert.Contains(result.Rooms, room => room.Bounds.Width > 350 && room.Bounds.Height > 250);
        Assert.Contains(result.Rooms, room => room.Boundary.Count >= 4);
        Assert.False(result.Diagnostics.HasErrors);
    }

    [Fact]
    public async Task ScanAsync_CropsMainFloorplanToDenseContentAndLeavesDimensionTextOutside()
    {
        var dimensionText = new TextPrimitive("8 400", new PlanRect(300, 570, 60, 16)) { SourceId = "dim-text-8400" };
        var secondDimensionText = new TextPrimitive("13 000", new PlanRect(420, 570, 70, 16)) { SourceId = "dim-text-13000" };
        var document = new PlanDocument(
            "region-crop-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(1000, 800),
                    new PlanPrimitive[]
                    {
                        new RectanglePrimitive(new PlanRect(0, 0, 1000, 800)) { SourceId = "sheet-frame" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 150), new PlanPoint(540, 150))) { SourceId = "plan-wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(540, 150), new PlanPoint(540, 430))) { SourceId = "plan-wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(540, 430), new PlanPoint(250, 430))) { SourceId = "plan-wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 430), new PlanPoint(250, 150))) { SourceId = "plan-wall-left" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(390, 150), new PlanPoint(390, 430))) { SourceId = "plan-wall-middle" },
                        new TextPrimitive("Sov 10.7 m2", new PlanRect(430, 255, 80, 16)) { SourceId = "room-label" },
                        dimensionText,
                        secondDimensionText,
                        new RectanglePrimitive(new PlanRect(720, 620, 240, 140)) { SourceId = "title-grid" },
                        new TextPrimitive("Prosjekt OpenPlanTrace", new PlanRect(735, 640, 150, 16)) { SourceId = "title-project" },
                        new TextPrimitive("Malestokk 1:100", new PlanRect(735, 685, 120, 16)) { SourceId = "title-scale" },
                        new TextPrimitive("Dato 2026-06-08", new PlanRect(735, 725, 120, 16)) { SourceId = "title-date" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        var main = Assert.Single(result.SheetRegions.Where(region => region.Kind == RegionKind.MainFloorPlan));
        Assert.True(main.Bounds.Left > 180, $"Main region should crop empty left sheet space, got {main.Bounds}.");
        Assert.True(main.Bounds.Top > 100, $"Main region should crop empty top sheet space, got {main.Bounds}.");
        Assert.True(main.Bounds.Right < 620, $"Main region should stop near floorplan content, got {main.Bounds}.");
        Assert.True(main.Bounds.Bottom < 520, $"Main region should leave exterior dimension text outside, got {main.Bounds}.");
        Assert.False(main.Bounds.Contains(dimensionText.Bounds.Center), "Exterior dimension text should not be inside the main floorplan region.");
        Assert.Contains(
            result.SheetRegions,
            region => region.Kind == RegionKind.Dimensions && region.Bounds.Contains(secondDimensionText.Bounds.Center));
        Assert.Contains(result.Diagnostics.Messages, message => message.Code == "layout.main_region.content_refined");
    }

    [Fact]
    public async Task ScanAsync_ClassifiesDoorOpeningWhenGapHasArc()
    {
        var document = new PlanDocument(
            "synthetic-opening",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(600, 400),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(220, 100))) { SourceId = "wall-left-run" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(250, 100), new PlanPoint(400, 100))) { SourceId = "wall-right-run" },
                        new ArcPrimitive(new PlanPoint(220, 100), 30, 0, Math.PI / 2) { SourceId = "door-swing" }
                    })
            });

        var scanner = new OpenPlanTraceScanner();
        var result = await scanner.ScanAsync(document);

        Assert.Contains(result.Openings, opening => opening.Type == OpeningType.Door);
        Assert.Contains(result.Openings, opening => opening.Operation == OpeningOperation.Hinged);
    }

    [Fact]
    public async Task ScanAsync_ReportsPipelineStageProgressWhenObserverIsProvided()
    {
        var document = new PlanDocument(
            "progress-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 300),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(220, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 100), new PlanPoint(220, 200))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 200), new PlanPoint(100, 200))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 200), new PlanPoint(100, 100))) { SourceId = "wall-left" }
                    })
            });
        var progress = new CapturingProgress();

        await new OpenPlanTraceScanner().ScanAsync(document, progress: progress);

        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Started && item.StageName == "wall-graph");
        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Completed && item.StageName == "wall-topology-preparation");
        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Completed && item.StageName == "wall-graph");
        Assert.Contains(progress.Events, item => item.Kind == PipelineStageProgressKind.Completed && item.StageName == "wall-type-refinement");
        Assert.All(
            progress.Events.Where(item => item.Kind == PipelineStageProgressKind.Completed),
            item => Assert.True(item.OutputDetectionCount >= item.InputDetectionCount));
    }

    [Fact]
    public async Task ScanAsync_AttachesPipelineStageMetadataToDiagnostics()
    {
        var document = new PlanDocument(
            "metadata-plan",
            new[]
            {
                new PlanPage(
                    1,
                    new PlanSize(400, 300),
                    new PlanPrimitive[]
                    {
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 100), new PlanPoint(220, 100))) { SourceId = "wall-top" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 100), new PlanPoint(220, 200))) { SourceId = "wall-right" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(220, 200), new PlanPoint(100, 200))) { SourceId = "wall-bottom" },
                        new LinePrimitive(new PlanLineSegment(new PlanPoint(100, 200), new PlanPoint(100, 100))) { SourceId = "wall-left" }
                    })
            });

        var result = await new OpenPlanTraceScanner().ScanAsync(document);

        Assert.Equal(
            PipelineStageMetadataCatalog.All.Select(metadata => metadata.Stage),
            result.Diagnostics.StageReports.Select(report => report.Stage));
        Assert.True(result.Diagnostics.ExecutionPlan.IsDependencyReady);
        Assert.Empty(result.Diagnostics.ExecutionPlan.Issues);
        Assert.Equal("fixed-stage-chain", result.Diagnostics.ExecutionPlan.ExecutionModel);
        Assert.Contains(result.Diagnostics.StageReports, report => report.Stage == "wall-type-refinement");
        Assert.Contains(PlanArtifactKind.Primitives, result.Diagnostics.ExecutionPlan.SourceArtifacts);
        Assert.NotEmpty(result.Diagnostics.ExecutionPlan.ExecutionWaves);
        Assert.Equal(
            result.Diagnostics.ExecutionPlan.Stages.Select(stage => stage.DependencyLevel).Distinct().Count(),
            result.Diagnostics.ExecutionPlan.ExecutionWaves.Count);
        Assert.Contains(
            result.Diagnostics.ExecutionPlan.ExecutionWaves,
            wave => wave.IsParallelCandidate
                && wave.StageCount > 1
                && wave.WriteConflictArtifacts.Count == 0
                && wave.IntraWaveDependencies.Count == 0
                && wave.ParallelReadiness == "Ready"
                && wave.RecommendedExecutionMode == "Parallel");
        Assert.Contains(
            result.Diagnostics.ExecutionPlan.ExecutionWaves,
            wave => wave.DirectDownstreamStageCount > 0
                && wave.DirectDownstreamStages.Count == wave.DirectDownstreamStageCount
                && wave.DownstreamReadArtifacts.Count > 0
                && wave.SchedulingReasons.Any(reason => reason.Contains("later stage", StringComparison.OrdinalIgnoreCase)));
        Assert.NotEmpty(result.Diagnostics.ExecutionPlan.RerunImpacts);
        Assert.NotEmpty(result.Diagnostics.ExecutionPlan.ArtifactPlans);
        var primitiveArtifactPlan = Assert.Single(result.Diagnostics.ExecutionPlan.ArtifactPlans, plan => plan.Artifact == PlanArtifactKind.Primitives);
        Assert.True(primitiveArtifactPlan.IsSourceArtifact);
        Assert.False(primitiveArtifactPlan.IsProducedByStage);
        Assert.True(primitiveArtifactPlan.IsConsumedByStage);
        Assert.Equal("SourceInput", primitiveArtifactPlan.DependencyRole);
        Assert.Contains("layer-analysis", primitiveArtifactPlan.RequiredConsumerStages);
        Assert.True(primitiveArtifactPlan.FirstConsumerWave > 0);
        var wallGraphArtifactPlan = Assert.Single(result.Diagnostics.ExecutionPlan.ArtifactPlans, plan => plan.Artifact == PlanArtifactKind.WallGraph);
        Assert.False(wallGraphArtifactPlan.IsSourceArtifact);
        Assert.True(wallGraphArtifactPlan.IsProducedByStage);
        Assert.True(wallGraphArtifactPlan.IsConsumedByStage);
        Assert.False(wallGraphArtifactPlan.IsTerminalArtifact);
        Assert.Equal("ProducedAndConsumed", wallGraphArtifactPlan.DependencyRole);
        Assert.Contains("wall-graph", wallGraphArtifactPlan.ProducerStages);
        Assert.Contains("openings", wallGraphArtifactPlan.RequiredConsumerStages);
        Assert.Contains("routing-layer", wallGraphArtifactPlan.RequiredConsumerStages);
        Assert.True(wallGraphArtifactPlan.FirstProducerOrder > 0);
        Assert.True(wallGraphArtifactPlan.LastConsumerOrder >= wallGraphArtifactPlan.FirstProducerOrder);
        Assert.NotEmpty(wallGraphArtifactPlan.Evidence);
        var wallImpact = Assert.Single(result.Diagnostics.ExecutionPlan.RerunImpacts, impact => impact.Artifact == PlanArtifactKind.Walls);
        Assert.True(wallImpact.HasImpact);
        Assert.Equal("DerivedArtifact", wallImpact.ImpactScope);
        Assert.Contains("wall-evidence", wallImpact.ProducerStages);
        Assert.Contains("wall-graph", wallImpact.DirectConsumerStages);
        Assert.Contains("routing-layer", wallImpact.AffectedStages);
        Assert.Contains(PlanArtifactKind.WallGraph, wallImpact.AffectedArtifacts);
        Assert.True(wallImpact.FirstAffectedWave > 0);
        var primitiveImpact = Assert.Single(result.Diagnostics.ExecutionPlan.RerunImpacts, impact => impact.Artifact == PlanArtifactKind.Primitives);
        Assert.True(primitiveImpact.IsSourceArtifact);
        Assert.Equal("SourceArtifact", primitiveImpact.ImpactScope);
        Assert.Contains("layer-analysis", primitiveImpact.DirectConsumerStages);
        Assert.Contains("routing-layer", primitiveImpact.AffectedStages);
        Assert.NotEmpty(result.Diagnostics.ExecutionPlan.RerunPlans);
        var wallPlan = Assert.Single(result.Diagnostics.ExecutionPlan.RerunPlans, plan => plan.PlanId == "wall-geometry");
        Assert.True(wallPlan.HasWork);
        Assert.Contains(PlanArtifactKind.Walls, wallPlan.ChangedArtifacts);
        Assert.Contains("wall-graph", wallPlan.DirectConsumerStages);
        Assert.Contains("routing-layer", wallPlan.RerunStages);
        Assert.Contains(PlanArtifactKind.WallGraph, wallPlan.AffectedArtifacts);
        Assert.True(wallPlan.FirstRerunWave > 0);
        Assert.True(wallPlan.LastRerunWave >= wallPlan.FirstRerunWave);
        var sourcePlan = Assert.Single(result.Diagnostics.ExecutionPlan.RerunPlans, plan => plan.PlanId == "source-primitives");
        Assert.Contains(PlanArtifactKind.Primitives, sourcePlan.ChangedSourceArtifacts);
        Assert.True(sourcePlan.RerunStageCount >= wallPlan.RerunStageCount);
        var customPlan = result.Diagnostics.ExecutionPlan.CreateRerunPlan(
            "manual-wall-room",
            "Manual wall and room correction",
            new[] { PlanArtifactKind.Walls, PlanArtifactKind.Rooms });
        Assert.Contains(PlanArtifactKind.Walls, customPlan.ChangedArtifacts);
        Assert.Contains(PlanArtifactKind.Rooms, customPlan.ChangedArtifacts);
        Assert.Equal(customPlan.RerunStages.Count, customPlan.RerunStages.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("routing-layer", customPlan.RerunStages);
        Assert.All(result.Diagnostics.ExecutionPlan.Stages, stage =>
        {
            Assert.True(stage.DependencyLevel > 0);
            Assert.Equal(stage.DependencyLevel, stage.ExecutionWave);
            Assert.True(stage.PreferredDependencyLevel >= stage.DependencyLevel);
        });
        Assert.All(result.Diagnostics.StageReports, report =>
        {
            Assert.False(string.IsNullOrWhiteSpace(report.Metadata.DisplayName));
            Assert.NotEmpty(report.Metadata.Reads);
            Assert.NotEmpty(report.Metadata.Writes);
            Assert.NotEmpty(report.Metadata.Capabilities);
            Assert.NotEmpty(report.RuntimeReadiness.Evidence);
            Assert.Equal(
                report.RuntimeReadiness.EmptyRequiredReads.Count > 0,
                report.RuntimeReadiness.HasEmptyRequiredReads);
            Assert.NotEmpty(report.OutputReadiness.Evidence);
            Assert.Equal(
                report.OutputReadiness.EmptyDeclaredOutputs.Count > 0,
                report.OutputReadiness.HasEmptyDeclaredOutputs);
            Assert.Equal(
                report.OutputReadiness.UndeclaredChangedArtifacts.Count > 0,
                report.OutputReadiness.HasUndeclaredChanges);
            Assert.True(
                report.Contract.WritesOnlyDeclaredArtifacts,
                $"{report.Stage} changed undeclared artifacts: {string.Join(", ", report.Contract.UndeclaredChangedArtifacts)}");
            Assert.Equal(
                report.Metadata.Writes.OrderBy(artifact => artifact.ToString()),
                report.Contract.DeclaredWrites);
            Assert.All(report.InputArtifacts.Concat(report.OutputArtifacts), artifact =>
            {
                Assert.False(string.IsNullOrWhiteSpace(artifact.StateKey));
                Assert.True(artifact.Revision > 0);
                Assert.NotEmpty(artifact.Evidence);
            });
        });

        var wallGraph = Assert.Single(result.Diagnostics.StageReports, report => report.Stage == "wall-graph");
        Assert.Equal(PipelineStageKind.Topology, wallGraph.Metadata.Kind);
        Assert.Contains(PlanArtifactKind.Walls, wallGraph.Metadata.Reads);
        Assert.Contains(PlanArtifactKind.WallEvidence, wallGraph.Metadata.Reads);
        Assert.Contains(PlanArtifactKind.WallTopologyPreparation, wallGraph.Metadata.Reads);
        Assert.Contains(PlanArtifactKind.WallGraph, wallGraph.Metadata.Writes);
        Assert.Contains(PlanArtifactKind.TopologySpans, wallGraph.Metadata.Writes);
        Assert.Contains(wallGraph.InputArtifacts, artifact => artifact.Artifact == PlanArtifactKind.Walls && artifact.Count > 0);
        Assert.Contains(wallGraph.InputArtifacts, artifact => artifact.Artifact == PlanArtifactKind.WallTopologyPreparation && artifact.Count > 0);
        Assert.Contains(wallGraph.OutputArtifacts, artifact => artifact.Artifact == PlanArtifactKind.WallGraph && artifact.Count > 0);
        Assert.True(wallGraph.RuntimeReadiness.RequiredReadsHaveData);
        Assert.Empty(wallGraph.RuntimeReadiness.EmptyRequiredReads);
        Assert.Contains(PlanArtifactKind.Walls, wallGraph.RuntimeReadiness.NonEmptyRequiredReads);
        Assert.Contains(PlanArtifactKind.WallEvidence, wallGraph.RuntimeReadiness.NonEmptyRequiredReads);
        Assert.Contains(PlanArtifactKind.WallTopologyPreparation, wallGraph.RuntimeReadiness.NonEmptyRequiredReads);
        Assert.True(wallGraph.OutputReadiness.DeclaredOutputsHaveData);
        Assert.False(wallGraph.OutputReadiness.HasEmptyDeclaredOutputs);
        Assert.Contains(PlanArtifactKind.WallGraph, wallGraph.OutputReadiness.NonEmptyDeclaredOutputs);
        Assert.Contains(PlanArtifactKind.TopologySpans, wallGraph.OutputReadiness.ChangedDeclaredOutputs);
        Assert.Empty(wallGraph.OutputReadiness.UndeclaredChangedArtifacts);
        Assert.Contains(
            wallGraph.ChangedArtifacts,
            artifact => artifact.Artifact == PlanArtifactKind.WallGraph
                && artifact.BeforeCount == 0
                && artifact.AfterCount > 0
                && artifact.Delta > 0);
        Assert.Contains(
            wallGraph.ArtifactDeltas,
            artifact => artifact.Artifact == PlanArtifactKind.WallGraph
                && artifact.IsDeclaredWrite
                && artifact.ChangeKind == PipelineArtifactDeltaKind.Created
                && artifact.BeforeCount == 0
                && artifact.AfterCount > 0
                && artifact.Changed);
        Assert.Contains(
            wallGraph.ArtifactDeltas,
            artifact => artifact.Artifact == PlanArtifactKind.TopologySpans
                && artifact.IsDeclaredWrite
                && artifact.ChangeKind == PipelineArtifactDeltaKind.Created
                && artifact.IsPresent);
        Assert.True(wallGraph.Contract.WritesOnlyDeclaredArtifacts);
        Assert.Contains(PlanArtifactKind.WallGraph, wallGraph.Contract.ChangedArtifacts);
        Assert.Contains(PlanArtifactKind.WallGraph, wallGraph.Contract.DeclaredWrites);
        Assert.Empty(wallGraph.Contract.UndeclaredChangedArtifacts);

        var wallGraphPlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "wall-graph");
        var wallTopologyPreparationPlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "wall-topology-preparation");
        var wallDetectionPlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "walls");
        Assert.True(wallGraphPlan.DependencyLevel > wallDetectionPlan.DependencyLevel);
        Assert.True(wallGraphPlan.DependencyLevel > wallTopologyPreparationPlan.DependencyLevel);

        var routingLayer = Assert.Single(result.Diagnostics.StageReports, report => report.Stage == "routing-layer");
        Assert.Equal(PipelineStageKind.Topology, routingLayer.Metadata.Kind);
        Assert.Contains(PlanArtifactKind.WallGraph, routingLayer.Metadata.Reads);
        Assert.Contains(PlanArtifactKind.ObjectAggregates, routingLayer.Metadata.Reads);
        Assert.Contains(PlanArtifactKind.RoutingBarriers, routingLayer.Metadata.Writes);
        Assert.Contains(PlanArtifactKind.RoutingPassages, routingLayer.Metadata.Writes);
        Assert.Contains(PlanArtifactKind.RoutingIgnoredObjects, routingLayer.Metadata.Writes);
        Assert.Contains(
            routingLayer.OutputArtifacts,
            artifact => artifact.Artifact == PlanArtifactKind.RoutingBarriers);
        Assert.Contains(
            routingLayer.ChangedArtifacts,
            artifact => artifact.Artifact == PlanArtifactKind.RoutingBarriers
                && artifact.BeforeCount == 0
                && artifact.AfterCount > 0
                && artifact.Delta > 0);
        Assert.Contains(
            routingLayer.ArtifactDeltas,
            artifact => artifact.Artifact == PlanArtifactKind.RoutingBarriers
                && artifact.IsDeclaredWrite
                && !artifact.IsEmptyDeclaredOutput);

        var routingPlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "routing-layer");
        var objectAggregatePlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "object-aggregates");
        Assert.True(routingPlan.DependencyLevel > objectAggregatePlan.DependencyLevel);

        var calibrationPlan = Assert.Single(result.Diagnostics.ExecutionPlan.Stages, stage => stage.Stage == "calibration");
        Assert.True(calibrationPlan.PreferredDependencyLevel > calibrationPlan.DependencyLevel);

        var rasterExtraction = Assert.Single(result.Diagnostics.StageReports, report => report.Stage == "raster-extraction");
        Assert.False(rasterExtraction.RuntimeReadiness.RequiredReadsHaveData);
        Assert.Contains(PlanArtifactKind.RasterImages, rasterExtraction.RuntimeReadiness.EmptyRequiredReads);
        Assert.Contains(PlanArtifactKind.Document, rasterExtraction.RuntimeReadiness.NonEmptyRequiredReads);
    }

    [Fact]
    public void PipelineStageContract_FlagsUndeclaredArtifactChanges()
    {
        var contract = PipelineStageContract.From(
            new[] { PlanArtifactKind.Walls, PlanArtifactKind.Diagnostics },
            new[] { PlanArtifactKind.Walls, PlanArtifactKind.Rooms });

        Assert.False(contract.WritesOnlyDeclaredArtifacts);
        Assert.Contains(PlanArtifactKind.Rooms, contract.UndeclaredChangedArtifacts);
        Assert.Contains(PlanArtifactKind.Diagnostics, contract.DeclaredUnchangedArtifacts);
        Assert.DoesNotContain(PlanArtifactKind.Walls, contract.UndeclaredChangedArtifacts);
    }

    [Fact]
    public void ArtifactDeltas_ReportDeclaredOutputsAndUndeclaredChanges()
    {
        var before = new Dictionary<PlanArtifactKind, int>
        {
            [PlanArtifactKind.Walls] = 4,
            [PlanArtifactKind.Diagnostics] = 1
        };
        var after = new Dictionary<PlanArtifactKind, int>
        {
            [PlanArtifactKind.Walls] = 6,
            [PlanArtifactKind.Diagnostics] = 1,
            [PlanArtifactKind.Rooms] = 2,
            [PlanArtifactKind.Openings] = 0
        };

        var deltas = PipelineArtifactDelta.FromCounts(
            before,
            after,
            new[] { PlanArtifactKind.Walls, PlanArtifactKind.Openings },
            new[] { PlanArtifactKind.Walls, PlanArtifactKind.Rooms });

        Assert.Contains(
            deltas,
            delta => delta.Artifact == PlanArtifactKind.Walls
                && delta.IsDeclaredWrite
                && delta.ChangeKind == PipelineArtifactDeltaKind.Increased
                && delta.BeforeCount == 4
                && delta.AfterCount == 6
                && delta.Delta == 2);
        Assert.Contains(
            deltas,
            delta => delta.Artifact == PlanArtifactKind.Openings
                && delta.IsDeclaredWrite
                && delta.IsEmptyDeclaredOutput
                && delta.ChangeKind == PipelineArtifactDeltaKind.Unchanged);
        Assert.Contains(
            deltas,
            delta => delta.Artifact == PlanArtifactKind.Rooms
                && !delta.IsDeclaredWrite
                && delta.ChangeKind == PipelineArtifactDeltaKind.Created
                && delta.BeforeCount == 0
                && delta.AfterCount == 2);
    }

    [Fact]
    public void PipelineExecutionPlan_ReportsMissingRequiredArtifactsForInvalidOrder()
    {
        var plan = PipelineExecutionPlan.FromStages(new[] { PipelineStageMetadataCatalog.Get("wall-graph") });

        Assert.False(plan.IsDependencyReady);
        var issue = Assert.Single(plan.Issues, issue => issue.Code == "pipeline.stage.required_artifacts_missing");
        Assert.Equal("wall-graph", issue.Stage);
        Assert.Contains(PlanArtifactKind.Walls, issue.Artifacts);
        Assert.Contains(PlanArtifactKind.WallTopologyPreparation, issue.Artifacts);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.artifact.producer_missing"
                && issue.Stage == "wall-graph"
                && issue.Severity == DiagnosticSeverity.Error
                && issue.Artifacts.Contains(PlanArtifactKind.Walls));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.artifact.producer_missing"
                && issue.Stage == "wall-graph"
                && issue.Severity == DiagnosticSeverity.Error
                && issue.Artifacts.Contains(PlanArtifactKind.WallEvidence));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.artifact.producer_missing"
                && issue.Stage == "wall-graph"
                && issue.Severity == DiagnosticSeverity.Error
                && issue.Artifacts.Contains(PlanArtifactKind.WallTopologyPreparation));
        var stage = Assert.Single(plan.Stages);
        Assert.Equal(1, stage.DependencyLevel);
        Assert.Equal(1, stage.ExecutionWave);
        Assert.Equal(1, stage.PreferredDependencyLevel);
    }

    [Fact]
    public void PipelineExecutionPlan_FlagsArtifactProducerOrderAndOwnershipProblems()
    {
        var plan = PipelineExecutionPlan.FromStages(new[]
        {
            StageMetadata(
                "wall-consumer",
                PipelineStageKind.Topology,
                new[] { PlanArtifactKind.Walls },
                new[] { PlanArtifactKind.WallGraph }),
            StageMetadata(
                "wall-producer-a",
                PipelineStageKind.Geometry,
                new[] { PlanArtifactKind.Primitives },
                new[] { PlanArtifactKind.Walls }),
            StageMetadata(
                "wall-producer-b",
                PipelineStageKind.Geometry,
                new[] { PlanArtifactKind.Primitives },
                new[] { PlanArtifactKind.Walls })
        });

        Assert.True(plan.HasErrors);
        Assert.True(plan.HasWarnings);
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.stage.required_artifacts_missing"
                && issue.Stage == "wall-consumer"
                && issue.Artifacts.Contains(PlanArtifactKind.Walls));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.artifact.producer_after_required_consumer"
                && issue.Stage == "wall-consumer"
                && issue.Severity == DiagnosticSeverity.Error
                && issue.Artifacts.Contains(PlanArtifactKind.Walls));
        Assert.Contains(
            plan.Issues,
            issue => issue.Code == "pipeline.artifact.multiple_producers"
                && issue.Stage == "wall-producer-a"
                && issue.Severity == DiagnosticSeverity.Warning
                && issue.Artifacts.Contains(PlanArtifactKind.Walls));

        var wallPlan = Assert.Single(plan.ArtifactPlans, artifact => artifact.Artifact == PlanArtifactKind.Walls);
        Assert.True(wallPlan.HasMultipleProducers);
        Assert.Equal(new[] { "wall-producer-a", "wall-producer-b" }, wallPlan.ProducerStages);
        Assert.Equal(new[] { "wall-consumer" }, wallPlan.RequiredConsumerStages);
        Assert.Equal("ProducedAndConsumed", wallPlan.DependencyRole);
    }

    private static PipelineStageMetadata StageMetadata(
        string stage,
        PipelineStageKind kind,
        IReadOnlyList<PlanArtifactKind> reads,
        IReadOnlyList<PlanArtifactKind> writes) =>
        new(
            stage,
            stage,
            kind,
            reads,
            writes,
            new[] { "test-stage" });

    private sealed class CapturingProgress : IProgress<PipelineStageProgress>
    {
        public List<PipelineStageProgress> Events { get; } = new();

        public void Report(PipelineStageProgress value) => Events.Add(value);
    }

}
