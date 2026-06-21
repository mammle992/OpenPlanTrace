namespace OpenPlanTrace.Tests;

public sealed class BenchmarkComparisonTests
{
    [Fact]
    public void Compare_DetectsRegressionSignalsAndCountDeltas()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "case-1",
                    passed: true,
                    failedAssertions: 0,
                    durationMilliseconds: 100,
                    walls: 10,
                    rooms: 2,
                    openings: 3,
                    objects: 4,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.92,
                    qualityIssues: 1,
                    diagnosticErrors: 0)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "case-1",
                    passed: false,
                    failedAssertions: 2,
                    durationMilliseconds: 340,
                    walls: 8,
                    rooms: 1,
                    openings: 3,
                    objects: 4,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityConfidence: 0.72,
                    qualityIssues: 3,
                    diagnosticErrors: 1)
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                QualityConfidenceRegressionThreshold = 0.05,
                DurationRegressionRatio = 2,
                DurationRegressionMinimumMilliseconds = 100
            });

        Assert.False(comparison.Passed);
        Assert.Equal(1, comparison.MatchedCaseCount);
        Assert.Equal(0, comparison.AddedCaseCount);
        Assert.Equal(0, comparison.RemovedCaseCount);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.failed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "assertions.failed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.confidence" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "quality.issues" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "diagnostics.errors" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "duration" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var caseComparison = Assert.Single(comparison.Cases);
        Assert.Equal(BenchmarkComparisonCaseStatus.Matched, caseComparison.Status);
        Assert.Equal(-2, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "walls").Delta);
        Assert.Equal(-1, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "rooms").Delta);
        Assert.Equal(-1, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "roomClusters").Delta);
    }

    [Fact]
    public void Compare_DetectsMeasurementConsistencyRegressionSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 5,
                    measurementConsistent: 5,
                    measurementOutliers: 0,
                    measurementSelectedScale: 50,
                    measurementMedianScale: 50,
                    measurementSpread: 1.1,
                    measurementConfidence: 0.82)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 5,
                    measurementConsistent: 2,
                    measurementOutliers: 3,
                    measurementSelectedScale: 50,
                    measurementMedianScale: 76,
                    measurementSpread: 2.4,
                    measurementConfidence: 0.79)
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                MeasurementOutlierRatioRegressionThreshold = 0.1,
                MeasurementSpreadRegressionMinimumDelta = 0.25,
                MeasurementSpreadRegressionRatio = 1.25
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal => signal.Code == "measurement.outlier_ratio" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "measurement.spread" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        var caseComparison = Assert.Single(comparison.Cases);
        Assert.Equal(3, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "measurementOutliers").Delta);
        Assert.Equal(-3, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "measurementConsistent").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("measurement.spread", markdown);
        Assert.Contains("measurementOutliers +3", markdown);
    }

    [Fact]
    public void Compare_DetectsStageArtifactGrowthAndShrinkSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "wall-graph-growth",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("wall-graph", (PlanArtifactKind.WallGraph, 0, 20), (PlanArtifactKind.TopologySpans, 0, 20)) }),
                Case(
                    "object-candidate-shrink",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("object-candidates", (PlanArtifactKind.ObjectCandidates, 0, 100)) }),
                Case(
                    "routing-growth",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("routing-layer", (PlanArtifactKind.RoutingBarriers, 0, 12), (PlanArtifactKind.RoutingIgnoredObjects, 0, 10)) })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "wall-graph-growth",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("wall-graph", (PlanArtifactKind.WallGraph, 0, 75), (PlanArtifactKind.TopologySpans, 0, 72)) }),
                Case(
                    "object-candidate-shrink",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("object-candidates", (PlanArtifactKind.ObjectCandidates, 0, 20)) }),
                Case(
                    "routing-growth",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[] { Stage("routing-layer", (PlanArtifactKind.RoutingBarriers, 0, 60), (PlanArtifactKind.RoutingIgnoredObjects, 0, 65)) })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                StageArtifactRegressionMinimumDelta = 20,
                StageArtifactRegressionRatio = 2
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-graph-growth"
            && signal.Code == "stage_artifact.after.wall_graph.wallgraph"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-graph-growth"
            && signal.Code == "stage_artifact.delta.wall_graph.wallgraph"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "object-candidate-shrink"
            && signal.Code == "stage_artifact.after.object_candidates.objectcandidates"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "routing-growth"
            && signal.Code == "stage_artifact.after.routing_layer.routingbarriers"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "routing-growth"
            && signal.Code == "stage_artifact.delta.routing_layer.routingignoredobjects"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var growthCase = comparison.Cases.Single(item => item.FixtureId == "wall-graph-growth");
        Assert.Equal(55, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "stage.wall-graph.WallGraph.after").Delta);
        Assert.Equal(52, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "stage.wall-graph.TopologySpans.delta").Delta);
        var routingCase = comparison.Cases.Single(item => item.FixtureId == "routing-growth");
        Assert.Equal(48, Assert.Single(routingCase.CountDeltas, delta => delta.Name == "stage.routing-layer.RoutingBarriers.after").Delta);
        Assert.Equal(55, Assert.Single(routingCase.CountDeltas, delta => delta.Name == "stage.routing-layer.RoutingIgnoredObjects.delta").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("stage_artifact.after.wall_graph.wallgraph", markdown);
        Assert.Contains("stage_artifact.after.routing_layer.routingbarriers", markdown);
        Assert.Contains("stage.wall-graph.WallGraph.after +55", markdown);
        Assert.Contains("stage.routing-layer.RoutingBarriers.after +48", markdown);
    }

    [Fact]
    public void Compare_DetectsStageRuntimeReadinessRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "runtime-readiness-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithRuntimeReadiness(
                            "kvemo-visual-ai",
                            StageRuntimeReadiness(
                                nonEmptyRequiredReads: new[] { PlanArtifactKind.ObjectCandidates },
                                nonEmptyOptionalReads: new[] { PlanArtifactKind.RasterImages }))
                    }),
                Case(
                    "runtime-readiness-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithRuntimeReadiness(
                            "room-adjacency",
                            StageRuntimeReadiness(
                                nonEmptyRequiredReads: new[] { PlanArtifactKind.Rooms },
                                emptyRequiredReads: new[] { PlanArtifactKind.Openings },
                                emptyOptionalReads: new[] { PlanArtifactKind.Annotations }))
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "runtime-readiness-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithRuntimeReadiness(
                            "kvemo-visual-ai",
                            StageRuntimeReadiness(
                                emptyRequiredReads: new[] { PlanArtifactKind.ObjectCandidates },
                                emptyOptionalReads: new[] { PlanArtifactKind.RasterImages }))
                    }),
                Case(
                    "runtime-readiness-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithRuntimeReadiness(
                            "room-adjacency",
                            StageRuntimeReadiness(
                                nonEmptyRequiredReads: new[] { PlanArtifactKind.Rooms, PlanArtifactKind.Openings },
                                nonEmptyOptionalReads: new[] { PlanArtifactKind.Annotations }))
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "runtime-readiness-regression"
            && signal.Code == "stage_runtime_readiness.empty_required_reads.kvemo_visual_ai"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "runtime-readiness-regression"
            && signal.Code == "stage_runtime_readiness.empty_optional_reads.kvemo_visual_ai"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "runtime-readiness-improvement"
            && signal.Code == "stage_runtime_readiness.empty_required_reads.room_adjacency"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "runtime-readiness-improvement"
            && signal.Code == "stage_runtime_readiness.empty_optional_reads.room_adjacency"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var regressionCase = comparison.Cases.Single(item => item.FixtureId == "runtime-readiness-regression");
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "stage.kvemo-visual-ai.runtimeReadiness.emptyRequiredReads").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "stage.kvemo-visual-ai.runtimeReadiness.emptyOptionalReads").Delta);
        var improvementCase = comparison.Cases.Single(item => item.FixtureId == "runtime-readiness-improvement");
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "stage.room-adjacency.runtimeReadiness.emptyRequiredReads").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "stage.room-adjacency.runtimeReadiness.emptyOptionalReads").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("stage_runtime_readiness.empty_required_reads.kvemo_visual_ai", markdown);
        Assert.Contains("stage.kvemo-visual-ai.runtimeReadiness.emptyRequiredReads +1", markdown);
    }

    [Fact]
    public void Compare_DetectsStageContractRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "contract-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithContract(
                            "opening-detection",
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.Openings },
                                new[] { PlanArtifactKind.Openings }),
                            (PlanArtifactKind.Openings, 0, 2))
                    }),
                Case(
                    "contract-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithContract(
                            "wall-topology",
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.TopologySpans },
                                new[] { PlanArtifactKind.TopologySpans, PlanArtifactKind.Rooms }),
                            (PlanArtifactKind.TopologySpans, 0, 0),
                            (PlanArtifactKind.Rooms, 0, 1))
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "contract-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithContract(
                            "opening-detection",
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.Openings },
                                new[] { PlanArtifactKind.Openings, PlanArtifactKind.Rooms }),
                            (PlanArtifactKind.Openings, 0, 0),
                            (PlanArtifactKind.Rooms, 0, 1))
                    }),
                Case(
                    "contract-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithContract(
                            "wall-topology",
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.TopologySpans },
                                new[] { PlanArtifactKind.TopologySpans }),
                            (PlanArtifactKind.TopologySpans, 0, 4))
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-regression"
            && signal.Code == "stage_contract.writes_only_declared.opening_detection"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-regression"
            && signal.Code == "stage_contract.undeclared_changed_artifacts.opening_detection"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-regression"
            && signal.Code == "stage_contract.empty_declared_outputs.opening_detection"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-improvement"
            && signal.Code == "stage_contract.writes_only_declared.wall_topology"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-improvement"
            && signal.Code == "stage_contract.undeclared_changed_artifacts.wall_topology"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "contract-improvement"
            && signal.Code == "stage_contract.empty_declared_outputs.wall_topology"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var regressionCase = comparison.Cases.Single(item => item.FixtureId == "contract-regression");
        Assert.Equal(-1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "stage.opening-detection.contract.writesOnlyDeclaredArtifacts").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "stage.opening-detection.contract.undeclaredChangedArtifacts").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "stage.opening-detection.contract.emptyDeclaredOutputs").Delta);
        var improvementCase = comparison.Cases.Single(item => item.FixtureId == "contract-improvement");
        Assert.Equal(1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "stage.wall-topology.contract.writesOnlyDeclaredArtifacts").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "stage.wall-topology.contract.undeclaredChangedArtifacts").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "stage.wall-topology.contract.emptyDeclaredOutputs").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("stage_contract.undeclared_changed_artifacts.opening_detection", markdown);
        Assert.Contains("stage.opening-detection.contract.emptyDeclaredOutputs +1", markdown);
    }

    [Fact]
    public void Compare_UsesOutputReadinessForEmptyDeclaredOutputSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "output-readiness-source",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithOutputReadiness(
                            "openings",
                            StageOutputReadiness(
                                emptyDeclaredOutputs: new[] { PlanArtifactKind.Openings },
                                unchangedDeclaredOutputs: new[] { PlanArtifactKind.Openings }),
                            (PlanArtifactKind.Openings, 0, 2))
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "output-readiness-source",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithOutputReadiness(
                            "openings",
                            StageOutputReadiness(
                                nonEmptyDeclaredOutputs: new[] { PlanArtifactKind.Openings },
                                changedDeclaredOutputs: new[] { PlanArtifactKind.Openings }),
                            (PlanArtifactKind.Openings, 0, 0))
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "output-readiness-source"
            && signal.Code == "stage_contract.empty_declared_outputs.openings"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        var comparisonCase = Assert.Single(comparison.Cases);
        Assert.Equal(-1, Assert.Single(comparisonCase.CountDeltas, delta => delta.Name == "stage.openings.contract.emptyDeclaredOutputs").Delta);
        Assert.Equal(-1, Assert.Single(comparisonCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyDeclaredOutputs").Delta);
    }

    [Fact]
    public void Compare_DetectsArtifactPlanGraphRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "artifact-plan-regression",
                    passed: true,
                    failedAssertions: 0,
                    artifactPlans: new[]
                    {
                        ArtifactPlan(
                            PlanArtifactKind.Walls,
                            producers: new[] { "walls" },
                            requiredConsumers: new[] { "wall-graph" }),
                        ArtifactPlan(
                            PlanArtifactKind.WallGraph,
                            producers: new[] { "wall-graph" },
                            requiredConsumers: new[] { "openings" })
                    }),
                Case(
                    "artifact-plan-improvement",
                    passed: true,
                    failedAssertions: 0,
                    artifactPlans: new[]
                    {
                        ArtifactPlan(
                            PlanArtifactKind.Openings,
                            producers: new[] { "openings" },
                            requiredConsumers: Array.Empty<string>(),
                            terminal: true)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "artifact-plan-regression",
                    passed: true,
                    failedAssertions: 0,
                    artifactPlans: new[]
                    {
                        ArtifactPlan(
                            PlanArtifactKind.Walls,
                            producers: Array.Empty<string>(),
                            requiredConsumers: Array.Empty<string>(),
                            terminal: true,
                            role: "UnproducedRead"),
                        ArtifactPlan(
                            PlanArtifactKind.WallGraph,
                            producers: new[] { "wall-graph", "wall-repair" },
                            requiredConsumers: new[] { "openings" },
                            multipleProducers: true)
                    }),
                Case(
                    "artifact-plan-improvement",
                    passed: true,
                    failedAssertions: 0,
                    artifactPlans: new[]
                    {
                        ArtifactPlan(
                            PlanArtifactKind.Openings,
                            producers: new[] { "openings" },
                            requiredConsumers: new[] { "rooms" },
                            terminal: false)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.producer_lost.walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.required_consumers_lost.walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.terminal.walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.multiple_producers.wallgraph"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.required_consumers_gained.openings"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "artifact_plan.no_longer_terminal.openings"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var regressionCase = Assert.Single(comparison.Cases, item => item.FixtureId == "artifact-plan-regression");
        Assert.Equal(-1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "artifactPlan.Walls.producerCount").Delta);
        Assert.Equal(-1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "artifactPlan.Walls.requiredConsumerCount").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "artifactPlan.Walls.terminal").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "artifactPlan.WallGraph.multipleProducers").Delta);
        var improvementCase = Assert.Single(comparison.Cases, item => item.FixtureId == "artifact-plan-improvement");
        Assert.Equal(1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "artifactPlan.Openings.requiredConsumerCount").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "artifactPlan.Openings.terminal").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("## Artifact Plan Graph", markdown);
        Assert.Contains("artifactPlan.Walls.producerCount", markdown);
        Assert.Contains("artifactPlan.Openings.requiredConsumerCount", markdown);
    }

    [Fact]
    public void Compare_DetectsPipelinePlanIssueRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "plan-issue-regression",
                    passed: true,
                    failedAssertions: 0),
                Case(
                    "plan-issue-improvement",
                    passed: true,
                    failedAssertions: 0,
                    planIssues: new[]
                    {
                        PlanIssue(
                            "pipeline.artifact.multiple_producers",
                            DiagnosticSeverity.Warning,
                            "wall-producer-a",
                            PlanArtifactKind.Walls)
                    }),
                Case(
                    "plan-issue-severity",
                    passed: true,
                    failedAssertions: 0,
                    planIssues: new[]
                    {
                        PlanIssue(
                            "pipeline.artifact.producer_after_required_consumer",
                            DiagnosticSeverity.Warning,
                            "wall-consumer",
                            PlanArtifactKind.Walls)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "plan-issue-regression",
                    passed: true,
                    failedAssertions: 0,
                    planIssues: new[]
                    {
                        PlanIssue(
                            "pipeline.artifact.producer_missing",
                            DiagnosticSeverity.Error,
                            "wall-graph",
                            PlanArtifactKind.Walls)
                    }),
                Case(
                    "plan-issue-improvement",
                    passed: true,
                    failedAssertions: 0),
                Case(
                    "plan-issue-severity",
                    passed: true,
                    failedAssertions: 0,
                    planIssues: new[]
                    {
                        PlanIssue(
                            "pipeline.artifact.producer_after_required_consumer",
                            DiagnosticSeverity.Error,
                            "wall-consumer",
                            PlanArtifactKind.Walls)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "plan-issue-regression"
            && signal.Code == "pipeline_plan_issue.added.pipeline_artifact_producer_missing_wall_graph_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "plan-issue-improvement"
            && signal.Code == "pipeline_plan_issue.removed.pipeline_artifact_multiple_producers_wall_producer_a_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "plan-issue-severity"
            && signal.Code == "pipeline_plan_issue.severity.pipeline_artifact_producer_after_required_consumer_wall_consumer_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var regressionCase = comparison.Cases.Single(item => item.FixtureId == "plan-issue-regression");
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "planIssue.totalCount").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "planIssue.errorCount").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "planIssue.pipeline_artifact_producer_missing.count").Delta);
        var improvementCase = comparison.Cases.Single(item => item.FixtureId == "plan-issue-improvement");
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "planIssue.warningCount").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "planIssue.pipeline_artifact_multiple_producers.count").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("## Pipeline Plan Issues", markdown);
        Assert.Contains("planIssue.pipeline_artifact_producer_missing.count", markdown);
        Assert.Contains("planIssue.warningCount", markdown);
    }

    [Fact]
    public void Compare_DetectsAggregatePipelineHealthRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "pipeline-health-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithPipelineHealth(
                            "wall-graph",
                            dependencyReady: true,
                            StageRuntimeReadiness(nonEmptyRequiredReads: new[] { PlanArtifactKind.Walls }),
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.WallGraph },
                                new[] { PlanArtifactKind.WallGraph }),
                            (PlanArtifactKind.WallGraph, 0, 4, true))
                    }),
                Case(
                    "pipeline-health-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithPipelineHealth(
                            "visual-ai",
                            dependencyReady: false,
                            StageRuntimeReadiness(
                                emptyRequiredReads: new[] { PlanArtifactKind.ObjectCandidates },
                                emptyOptionalReads: new[] { PlanArtifactKind.RasterImages }),
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.VisualAiClassifications },
                                new[] { PlanArtifactKind.VisualAiClassifications, PlanArtifactKind.Rooms }),
                            (PlanArtifactKind.VisualAiClassifications, 0, 0, true),
                            (PlanArtifactKind.Rooms, 0, 1, false))
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "pipeline-health-regression",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithPipelineHealth(
                            "wall-graph",
                            dependencyReady: false,
                            StageRuntimeReadiness(
                                emptyRequiredReads: new[] { PlanArtifactKind.Walls },
                                emptyOptionalReads: new[] { PlanArtifactKind.Dimensions }),
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.WallGraph },
                                new[] { PlanArtifactKind.WallGraph, PlanArtifactKind.Rooms }),
                            (PlanArtifactKind.WallGraph, 0, 0, true),
                            (PlanArtifactKind.Rooms, 0, 1, false))
                    }),
                Case(
                    "pipeline-health-improvement",
                    passed: true,
                    failedAssertions: 0,
                    stages: new[]
                    {
                        StageWithPipelineHealth(
                            "visual-ai",
                            dependencyReady: true,
                            StageRuntimeReadiness(
                                nonEmptyRequiredReads: new[] { PlanArtifactKind.ObjectCandidates },
                                nonEmptyOptionalReads: new[] { PlanArtifactKind.RasterImages }),
                            PipelineStageContract.From(
                                new[] { PlanArtifactKind.VisualAiClassifications },
                                new[] { PlanArtifactKind.VisualAiClassifications }),
                            (PlanArtifactKind.VisualAiClassifications, 0, 2, true))
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        foreach (var code in new[]
                 {
                     "pipeline_health.not_dependency_ready_stages",
                     "pipeline_health.empty_required_runtime_reads",
                     "pipeline_health.empty_optional_runtime_reads",
                     "pipeline_health.contract_violation_stages",
                     "pipeline_health.undeclared_changed_artifacts",
                     "pipeline_health.empty_declared_outputs"
                 })
        {
            Assert.Contains(comparison.Signals, signal =>
                signal.FixtureId == "pipeline-health-regression"
                && signal.Code == code
                && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
            Assert.Contains(comparison.Signals, signal =>
                signal.FixtureId == "pipeline-health-improvement"
                && signal.Code == code
                && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        }

        var regressionCase = comparison.Cases.Single(item => item.FixtureId == "pipeline-health-regression");
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.notDependencyReadyStages").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyRequiredRuntimeReads").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyOptionalRuntimeReads").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.contractViolationStages").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.undeclaredChangedArtifacts").Delta);
        Assert.Equal(1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyDeclaredOutputs").Delta);
        var improvementCase = comparison.Cases.Single(item => item.FixtureId == "pipeline-health-improvement");
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.notDependencyReadyStages").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyRequiredRuntimeReads").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyOptionalRuntimeReads").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.contractViolationStages").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.undeclaredChangedArtifacts").Delta);
        Assert.Equal(-1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "pipelineHealth.emptyDeclaredOutputs").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("## Pipeline Health", markdown);
        Assert.Contains("pipeline_health.empty_required_runtime_reads", markdown);
        Assert.Contains("pipelineHealth.emptyRequiredRuntimeReads", markdown);
        Assert.Contains("pipeline-health-regression", markdown);
        Assert.Contains("pipeline-health-improvement", markdown);
        Assert.Contains("| `pipelineHealth.emptyDeclaredOutputs` | 0 | 1 | +1 |", markdown);
        Assert.Contains("| `pipelineHealth.emptyDeclaredOutputs` | 1 | 0 | -1 |", markdown);
    }

    [Fact]
    public void Compare_DetectsRerunPlanRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "rerun-scope-regression",
                    passed: true,
                    failedAssertions: 0,
                    rerunPlans: new[]
                    {
                        RerunPlan(
                            "wall-geometry",
                            new[] { "wall-graph", "rooms" },
                            new[] { nameof(PlanArtifactKind.WallGraph), nameof(PlanArtifactKind.Rooms) },
                            "WaveOrderedWithParallelCandidates")
                    }),
                Case(
                    "rerun-scope-improvement",
                    passed: true,
                    failedAssertions: 0,
                    rerunPlans: new[]
                    {
                        RerunPlan(
                            "objects",
                            new[] { "object-candidates", "object-groups", "routing-layer" },
                            new[] { nameof(PlanArtifactKind.ObjectCandidates), nameof(PlanArtifactKind.ObjectGroups), nameof(PlanArtifactKind.RoutingBarriers) },
                            "WaveOrderedSequential")
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "rerun-scope-regression",
                    passed: true,
                    failedAssertions: 0,
                    rerunPlans: new[]
                    {
                        RerunPlan(
                            "wall-geometry",
                            new[] { "wall-graph", "rooms", "openings", "routing-layer" },
                            new[] { nameof(PlanArtifactKind.WallGraph), nameof(PlanArtifactKind.Rooms), nameof(PlanArtifactKind.Openings), nameof(PlanArtifactKind.RoutingBarriers) },
                            "WaveOrderedSequential")
                    }),
                Case(
                    "rerun-scope-improvement",
                    passed: true,
                    failedAssertions: 0,
                    rerunPlans: new[]
                    {
                        RerunPlan(
                            "objects",
                            new[] { "object-aggregates" },
                            new[] { nameof(PlanArtifactKind.ObjectAggregates) },
                            "WaveOrderedWithParallelCandidates")
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        foreach (var code in new[]
                 {
                     "rerun_plan.rerun_stages.wall_geometry",
                     "rerun_plan.affected_artifacts.wall_geometry",
                     "rerun_plan.wave_span.wall_geometry",
                     "rerun_plan.execution_mode.wall_geometry"
                 })
        {
            Assert.Contains(comparison.Signals, signal =>
                signal.FixtureId == "rerun-scope-regression"
                && signal.Code == code
                && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        }

        foreach (var code in new[]
                 {
                     "rerun_plan.rerun_stages.objects",
                     "rerun_plan.affected_artifacts.objects",
                     "rerun_plan.wave_span.objects",
                     "rerun_plan.execution_mode.objects"
                 })
        {
            Assert.Contains(comparison.Signals, signal =>
                signal.FixtureId == "rerun-scope-improvement"
                && signal.Code == code
                && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        }

        var regressionCase = comparison.Cases.Single(item => item.FixtureId == "rerun-scope-regression");
        Assert.Equal(2, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "rerunPlan.wall-geometry.rerunStages").Delta);
        Assert.Equal(2, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "rerunPlan.wall-geometry.affectedArtifacts").Delta);
        Assert.Equal(2, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "rerunPlan.wall-geometry.waveSpan").Delta);
        Assert.Equal(-1, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "rerunPlan.wall-geometry.modeRank").Delta);
        Assert.Equal(2, Assert.Single(regressionCase.CountDeltas, delta => delta.Name == "rerunPlan.totalRerunStages").Delta);

        var improvementCase = comparison.Cases.Single(item => item.FixtureId == "rerun-scope-improvement");
        Assert.Equal(-2, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "rerunPlan.objects.rerunStages").Delta);
        Assert.Equal(-2, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "rerunPlan.objects.affectedArtifacts").Delta);
        Assert.Equal(-2, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "rerunPlan.objects.waveSpan").Delta);
        Assert.Equal(1, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "rerunPlan.objects.modeRank").Delta);
        Assert.Equal(-2, Assert.Single(improvementCase.CountDeltas, delta => delta.Name == "rerunPlan.totalRerunStages").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("## Rerun Plans", markdown);
        Assert.Contains("rerun_plan.rerun_stages.wall_geometry", markdown);
        Assert.Contains("rerun_plan.execution_mode.objects", markdown);
        Assert.Contains("| `rerunPlan.wall-geometry.rerunStages` | 2 | 4 | +2 |", markdown);
        Assert.Contains("| `rerunPlan.objects.rerunStages` | 3 | 1 | -2 |", markdown);
    }

    [Fact]
    public void Compare_DetectsFinalArtifactInventoryRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "wall-graph-lost",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: Inventory(
                        (PlanArtifactKind.Walls, 24),
                        (PlanArtifactKind.WallGraph, 24),
                        (PlanArtifactKind.Rooms, 4),
                        (PlanArtifactKind.RoutingBarriers, 24))),
                Case(
                    "object-noise-shrink",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: Inventory(
                        (PlanArtifactKind.ObjectCandidates, 120),
                        (PlanArtifactKind.RoutingIgnoredObjects, 80)))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "wall-graph-lost",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: Inventory(
                        (PlanArtifactKind.Walls, 24),
                        (PlanArtifactKind.WallGraph, 0),
                        (PlanArtifactKind.Rooms, 4),
                        (PlanArtifactKind.RoutingBarriers, 90))),
                Case(
                    "object-noise-shrink",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: Inventory(
                        (PlanArtifactKind.ObjectCandidates, 30),
                        (PlanArtifactKind.RoutingIgnoredObjects, 20)))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                FinalArtifactRegressionMinimumDelta = 20,
                FinalArtifactRegressionRatio = 2
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-graph-lost"
            && signal.Code == "artifact_inventory.missing.wallgraph"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-graph-lost"
            && signal.Code == "artifact_inventory.growth.routingbarriers"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "object-noise-shrink"
            && signal.Code == "artifact_inventory.shrink.objectcandidates"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "object-noise-shrink"
            && signal.Code == "artifact_inventory.shrink.routingignoredobjects"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var lostCase = comparison.Cases.Single(item => item.FixtureId == "wall-graph-lost");
        Assert.Equal(-24, Assert.Single(lostCase.CountDeltas, delta => delta.Name == "artifact.WallGraph.count").Delta);
        Assert.Equal(66, Assert.Single(lostCase.CountDeltas, delta => delta.Name == "artifact.RoutingBarriers.count").Delta);
        var shrinkCase = comparison.Cases.Single(item => item.FixtureId == "object-noise-shrink");
        Assert.Equal(-90, Assert.Single(shrinkCase.CountDeltas, delta => delta.Name == "artifact.ObjectCandidates.count").Delta);
        Assert.Equal(-60, Assert.Single(shrinkCase.CountDeltas, delta => delta.Name == "artifact.RoutingIgnoredObjects.count").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("artifact_inventory.missing.wallgraph", markdown);
        Assert.Contains("artifact_inventory.growth.routingbarriers", markdown);
        Assert.Contains("artifact.WallGraph.count -24", markdown);
        Assert.Contains("artifact.RoutingBarriers.count +66", markdown);
    }

    [Fact]
    public void Compare_DetectsFinalArtifactStateChangesWhenCountsMatch()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "wall-state-change",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: new[]
                    {
                        new PipelineArtifactSnapshot(
                            PlanArtifactKind.Walls,
                            24,
                            stateKey: "Walls:24:baseline",
                            revision: 100),
                        new PipelineArtifactSnapshot(
                            PlanArtifactKind.WallGraph,
                            32,
                            stateKey: "WallGraph:32:same",
                            revision: 200)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "wall-state-change",
                    passed: true,
                    failedAssertions: 0,
                    artifactInventory: new[]
                    {
                        new PipelineArtifactSnapshot(
                            PlanArtifactKind.Walls,
                            24,
                            stateKey: "Walls:24:candidate",
                            revision: 113),
                        new PipelineArtifactSnapshot(
                            PlanArtifactKind.WallGraph,
                            32,
                            stateKey: "WallGraph:32:same",
                            revision: 200)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.True(comparison.Passed);
        var signal = Assert.Single(comparison.Signals, item => item.Code == "artifact_inventory.state_key.walls");
        Assert.Equal("wall-state-change", signal.FixtureId);
        Assert.Equal(BenchmarkComparisonSignalSeverity.Info, signal.Severity);
        Assert.Contains("baseline", signal.Baseline);
        Assert.Contains("candidate", signal.Candidate);

        var caseComparison = Assert.Single(comparison.Cases);
        Assert.Equal(0, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "artifact.Walls.count").Delta);
        Assert.Equal(13, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "artifact.Walls.revision").Delta);
        Assert.Equal(0, Assert.Single(caseComparison.CountDeltas, delta => delta.Name == "artifact.WallGraph.revision").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("artifact_inventory.state_key.walls", markdown);
    }

    [Fact]
    public void Compare_DetectsQualityReviewRequirementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "quality-review-added",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: false),
                Case(
                    "quality-review-cleared",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: true)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "quality-review-added",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: true),
                Case(
                    "quality-review-cleared",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityRequiresReview: false)
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "quality-review-added"
            && signal.Code == "quality.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "quality-review-cleared"
            && signal.Code == "quality.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
    }

    [Fact]
    public void Compare_DetectsScanReviewQueueRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "review-growth",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.ObjectGroupReview, 5),
                        (ScanReviewQueueKinds.WallGraphGapReview, 3))),
                Case(
                    "review-shrink",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.OpeningReview, 12),
                        (ScanReviewQueueKinds.ObjectGroupReview, 8)))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "review-growth",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.ObjectGroupReview, 6),
                        (ScanReviewQueueKinds.SuppressedWallPatternReview, 5),
                        (ScanReviewQueueKinds.WallGraphGapReview, 13))),
                Case(
                    "review-shrink",
                    passed: true,
                    failedAssertions: 0,
                    scanReviewQueue: ReviewQueue(
                        (ScanReviewQueueKinds.OpeningReview, 2),
                        (ScanReviewQueueKinds.ObjectGroupReview, 3)))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScanReviewQueueItemRegressionMinimumDelta = 5,
                ScanReviewQueueItemRegressionRatio = 1.5,
                ScanReviewQueueKindRegressionMinimumDelta = 5,
                ScanReviewQueueKindRegressionRatio = 1.5
            });

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.items"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.kind.wallgraphgapreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-growth"
            && signal.Code == "scan_review_queue.kind.suppressedwallpatternreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-shrink"
            && signal.Code == "scan_review_queue.items"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "review-shrink"
            && signal.Code == "scan_review_queue.kind.openingreview"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var growthCase = comparison.Cases.Single(item => item.FixtureId == "review-growth");
        Assert.Equal(16, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueueItems").Delta);
        Assert.Equal(10, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueue.WallGraphGapReview").Delta);
        Assert.Equal(5, Assert.Single(growthCase.CountDeltas, delta => delta.Name == "scanReviewQueue.SuppressedWallPatternReview").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("scan_review_queue.items", markdown);
        Assert.Contains("scanReviewQueueItems +16", markdown);
    }

    [Fact]
    public void Compare_DetectsImportReadinessRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "metric-loss",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 4,
                    measurementConsistent: 4,
                    measurementOutliers: 0),
                Case(
                    "geometry-recovery",
                    passed: true,
                    failedAssertions: 0,
                    walls: 0,
                    rooms: 0,
                    importReadiness: Readiness(walls: 0, rooms: 0, measurementOutliers: 0))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "metric-loss",
                    passed: true,
                    failedAssertions: 0,
                    measurementChecked: 4,
                    measurementConsistent: 2,
                    measurementOutliers: 2),
                Case(
                    "geometry-recovery",
                    passed: true,
                    failedAssertions: 0,
                    walls: 6,
                    rooms: 2,
                    importReadiness: Readiness(walls: 6, rooms: 2, measurementOutliers: 0))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ImportReadinessScoreRegressionThreshold = 0.01
            });

        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.metric_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.review_required"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "metric-loss"
            && signal.Code == "import_readiness.score"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "geometry-recovery"
            && signal.Code == "import_readiness.geometry_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "geometry-recovery"
            && signal.Code == "import_readiness.routing_ready"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("import_readiness.metric_ready", markdown);
        Assert.Contains("import_readiness.geometry_ready", markdown);
    }

    [Fact]
    public void Compare_DetectsWallPlacementRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "medium-plan",
                    passed: true,
                    failedAssertions: 0,
                    wallPlacement: WallPlacement(
                        readyWalls: 28,
                        reviewWalls: 8,
                        isolatedFragments: 2,
                        blockedRepairs: 0,
                        highSeverityRepairs: 0)),
                Case(
                    "wall-recovery",
                    passed: true,
                    failedAssertions: 0,
                    wallPlacement: WallPlacement(
                        readyWalls: 10,
                        reviewWalls: 12,
                        isolatedFragments: 7,
                        blockedRepairs: 2,
                        highSeverityRepairs: 2))
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "medium-plan",
                    passed: true,
                    failedAssertions: 0,
                    wallPlacement: WallPlacement(
                        readyWalls: 24,
                        reviewWalls: 15,
                        isolatedFragments: 7,
                        blockedRepairs: 2,
                        highSeverityRepairs: 1)),
                Case(
                    "wall-recovery",
                    passed: true,
                    failedAssertions: 0,
                    wallPlacement: WallPlacement(
                        readyWalls: 14,
                        reviewWalls: 5,
                        isolatedFragments: 2,
                        blockedRepairs: 0,
                        highSeverityRepairs: 0))
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                WallPlacementReadyWallRegressionMinimumDelta = 1,
                WallPlacementReviewWallRegressionMinimumDelta = 1,
                WallPlacementFragmentRegressionMinimumDelta = 1,
                WallPlacementRepairRegressionMinimumDelta = 1
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "medium-plan"
            && signal.Code == "wall_placement.ready_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "medium-plan"
            && signal.Code == "wall_placement.review_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "medium-plan"
            && signal.Code == "wall_placement.isolated_fragments"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "medium-plan"
            && signal.Code == "wall_placement.topology_blocked_repairs"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-recovery"
            && signal.Code == "wall_placement.ready_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.FixtureId == "wall-recovery"
            && signal.Code == "wall_placement.review_walls"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var mediumCase = comparison.Cases.Single(item => item.FixtureId == "medium-plan");
        Assert.Equal(-4, Assert.Single(mediumCase.CountDeltas, delta => delta.Name == "wallPlacement.placementReadyWallCount").Delta);
        Assert.Equal(5, Assert.Single(mediumCase.CountDeltas, delta => delta.Name == "wallPlacement.isolatedFragmentComponentCount").Delta);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("## Wall Placement", markdown);
        Assert.Contains("wall_placement.ready_walls", markdown);
        Assert.Contains("wallPlacement.placementReadyWallCount", markdown);
        Assert.Contains("| -4 |", markdown);
    }

    [Fact]
    public void Compare_DetectsDetectorMetricRegressionAndImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "tagged-industrial",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 4, detected: 4, matched: 4),
                        Metric("openings", expected: 2, detected: 5, matched: 1)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "tagged-industrial",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 4, detected: 5, matched: 2),
                        Metric("openings", expected: 2, detected: 2, matched: 2)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                DetectorRecallRegressionThreshold = 0.1,
                DetectorPrecisionRegressionThreshold = 0.1,
                DetectorF1RegressionThreshold = 0.1
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.recall"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression
            && signal.Baseline!.Contains("matched 4/4", StringComparison.Ordinal)
            && signal.Candidate!.Contains("matched 2/4", StringComparison.Ordinal));
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.precision"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.object_groups.f1"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.recall"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.precision"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal =>
            signal.Code == "detector_metric.openings.f1"
            && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("detector_metric.object_groups.recall", markdown);
        Assert.Contains("detector_metric.openings.precision", markdown);
    }

    [Fact]
    public void Compare_SkipsPrecisionSignalsForSpotCheckMetrics()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-spot-check",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 1, detected: 2, matched: 1, precisionScoring: false)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-spot-check",
                    passed: true,
                    failedAssertions: 0,
                    metrics: new[]
                    {
                        Metric("object_groups", expected: 1, detected: 8, matched: 1, precisionScoring: false)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "detector_metric.object_groups.precision");
        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "detector_metric.object_groups.f1");
        Assert.DoesNotContain(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections");
    }

    [Fact]
    public void Compare_DetectsScoreboardReadinessRegressionSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.95,
                    metrics: new[]
                    {
                        Metric("walls", expected: 6, detected: 6, matched: 6),
                        Metric("rooms", expected: 4, detected: 4, matched: 4),
                        Metric("openings", expected: 3, detected: 3, matched: 3)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "industrial-hard",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Usable,
                    qualityConfidence: 0.78,
                    metrics: new[]
                    {
                        Metric("walls", expected: 6, detected: 12, matched: 3),
                        Metric("rooms", expected: 4, detected: 2, matched: 1),
                        Metric("openings", expected: 3, detected: 8, matched: 1)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScoreboardOverallRegressionThreshold = 0.01,
                ScoreboardConsumerReadinessRegressionThreshold = 0.01
            });

        Assert.False(comparison.Passed);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.downstream_ready" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.overall" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.consumer_readiness" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.missed_targets" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("scoreboard.consumer_readiness", markdown);
        Assert.Contains("scoreboard.missed_targets", markdown);
    }

    [Fact]
    public void Compare_DetectsScoreboardReadinessImprovementSignals()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case(
                    "object-heavy",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.ReviewRequired,
                    qualityConfidence: 0.62,
                    metrics: new[]
                    {
                        Metric("object_aggregates", expected: 4, detected: 8, matched: 1)
                    })
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case(
                    "object-heavy",
                    passed: true,
                    failedAssertions: 0,
                    qualityGrade: PlanScanQualityGrade.Strong,
                    qualityConfidence: 0.94,
                    metrics: new[]
                    {
                        Metric("object_aggregates", expected: 4, detected: 4, matched: 4)
                    })
            });

        var comparison = BenchmarkComparisonResult.Compare(
            baseline,
            candidate,
            new BenchmarkComparisonOptions
            {
                ScoreboardOverallRegressionThreshold = 0.01,
                ScoreboardConsumerReadinessRegressionThreshold = 0.01
            });

        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.grade" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.overall" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.consumer_readiness" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.missed_targets" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "scoreboard.extra_detections" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
    }

    [Fact]
    public void Compare_ReportsAddedRemovedAndRecoveredCases()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[]
            {
                Case("recovered", passed: false, failedAssertions: 1),
                Case("removed", passed: true, failedAssertions: 0)
            });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[]
            {
                Case("recovered", passed: true, failedAssertions: 0),
                Case("added", passed: true, failedAssertions: 0)
            });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.False(comparison.Passed);
        Assert.Equal(1, comparison.MatchedCaseCount);
        Assert.Equal(1, comparison.AddedCaseCount);
        Assert.Equal(1, comparison.RemovedCaseCount);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.recovered" && signal.Severity == BenchmarkComparisonSignalSeverity.Improvement);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.added" && signal.Severity == BenchmarkComparisonSignalSeverity.Info);
        Assert.Contains(comparison.Signals, signal => signal.Code == "case.removed" && signal.Severity == BenchmarkComparisonSignalSeverity.Regression);
        Assert.Contains(comparison.Cases, item => item.FixtureId == "added" && item.Status == BenchmarkComparisonCaseStatus.Added);
        Assert.Contains(comparison.Cases, item => item.FixtureId == "removed" && item.Status == BenchmarkComparisonCaseStatus.Removed);
    }

    [Fact]
    public void Compare_TreatsSkippedOptionalCaseAsInformational()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[] { Case("optional-pdf", passed: true, failedAssertions: 0) });
        var skipped = PlanBenchmarkEvaluator.SkippedFixture(
            new BenchmarkFixture
            {
                Id = "optional-pdf",
                SourcePath = "%USERPROFILE%/Downloads/missing.pdf",
                Optional = true
            },
            "Optional PDF fixture was unavailable.",
            TimeSpan.FromMilliseconds(1));
        var candidate = BenchmarkRunResult.Create("candidate", new[] { skipped });

        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        Assert.True(comparison.Passed);
        Assert.Equal(0, comparison.RegressionCount);
        var signal = Assert.Single(comparison.Signals);
        Assert.Equal("case.skipped", signal.Code);
        Assert.Equal(BenchmarkComparisonSignalSeverity.Info, signal.Severity);
        var caseComparison = Assert.Single(comparison.Cases);
        Assert.False(caseComparison.BaselineSkipped);
        Assert.True(caseComparison.CandidateSkipped);
        Assert.Equal("Optional PDF fixture was unavailable.", caseComparison.CandidateSkipReason);
        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);
        Assert.Contains("PASS -> SKIP", markdown);
    }

    [Fact]
    public void MarkdownReport_IncludesRegressionSignalsAndCaseDeltas()
    {
        var baseline = BenchmarkRunResult.Create(
            "baseline",
            new[] { Case("case-1", passed: true, failedAssertions: 0, walls: 10) });
        var candidate = BenchmarkRunResult.Create(
            "candidate",
            new[] { Case("case-1", passed: false, failedAssertions: 1, walls: 8) });
        var comparison = BenchmarkComparisonResult.Compare(baseline, candidate);

        var markdown = BenchmarkComparisonMarkdownReport.Create(comparison);

        Assert.Contains("# OpenPlanTrace Benchmark Comparison", markdown);
        Assert.Contains("Status: REGRESSION", markdown);
        Assert.Contains("case.failed", markdown);
        Assert.Contains("walls -2", markdown);
    }

    private static BenchmarkCaseResult Case(
        string fixtureId,
        bool passed,
        int failedAssertions,
        double durationMilliseconds = 100,
        int walls = 10,
        int rooms = 1,
        int openings = 1,
        int objects = 1,
        PlanScanQualityGrade qualityGrade = PlanScanQualityGrade.Usable,
        double qualityConfidence = 0.75,
        bool? qualityRequiresReview = null,
        int qualityIssues = 0,
        int diagnosticErrors = 0,
        int measurementChecked = 0,
        int measurementConsistent = 0,
        int measurementOutliers = 0,
        double? measurementSelectedScale = null,
        double? measurementMedianScale = null,
        double? measurementSpread = null,
        double measurementConfidence = 0,
        BenchmarkWallPlacementSummary? wallPlacement = null,
        IReadOnlyList<BenchmarkDetectorMetrics>? metrics = null,
        PlanImportReadiness? importReadiness = null,
        ScanReviewQueueSummary? scanReviewQueue = null,
        IReadOnlyList<BenchmarkStageSummary>? stages = null,
        IReadOnlyList<PipelineArtifactSnapshot>? artifactInventory = null,
        IReadOnlyList<BenchmarkArtifactPlanSummary>? artifactPlans = null,
        IReadOnlyList<BenchmarkPipelinePlanIssueSummary>? planIssues = null,
        IReadOnlyList<BenchmarkRerunPlanSummary>? rerunPlans = null)
    {
        var assertions = new List<BenchmarkAssertionResult>
        {
            new("scan.completed", true, "scan succeeds", "scan succeeded", "Input was scanned successfully.")
        };
        for (var index = 0; index < failedAssertions; index++)
        {
            assertions.Add(new BenchmarkAssertionResult(
                $"failure.{index + 1}",
                false,
                "expected",
                "actual",
                "Synthetic benchmark failure."));
        }

        return new BenchmarkCaseResult(
            fixtureId,
            fixtureId,
            $"{fixtureId}.pdf",
            passed,
            true,
            durationMilliseconds,
            Counts(
                walls,
                rooms,
                openings,
                objects,
                qualityGrade,
                qualityConfidence,
                qualityRequiresReview,
                qualityIssues,
                diagnosticErrors,
                measurementChecked,
                measurementConsistent,
                measurementOutliers,
                measurementSelectedScale,
                measurementMedianScale,
                measurementSpread,
                measurementConfidence),
            assertions,
            passed ? null : "Synthetic failure.")
        {
            Metrics = metrics ?? Array.Empty<BenchmarkDetectorMetrics>(),
            ImportReadiness = importReadiness ?? Readiness(walls, rooms, measurementOutliers),
            WallPlacement = wallPlacement ?? WallPlacement(readyWalls: walls, structuralComponents: Math.Min(1, walls), topologyEdges: walls),
            ScanReviewQueue = scanReviewQueue ?? ScanReviewQueueSummary.Empty,
            Stages = stages ?? Array.Empty<BenchmarkStageSummary>(),
            ArtifactInventory = artifactInventory ?? Array.Empty<PipelineArtifactSnapshot>(),
            ArtifactPlans = artifactPlans ?? Array.Empty<BenchmarkArtifactPlanSummary>(),
            PlanIssues = planIssues ?? Array.Empty<BenchmarkPipelinePlanIssueSummary>(),
            RerunPlans = rerunPlans ?? Array.Empty<BenchmarkRerunPlanSummary>()
        };
    }

    private static IReadOnlyList<PipelineArtifactSnapshot> Inventory(params (PlanArtifactKind Artifact, int Count)[] counts) =>
        counts.Select(item => new PipelineArtifactSnapshot(item.Artifact, item.Count)).ToArray();

    private static BenchmarkArtifactPlanSummary ArtifactPlan(
        PlanArtifactKind artifact,
        IReadOnlyList<string> producers,
        IReadOnlyList<string> requiredConsumers,
        IReadOnlyList<string>? optionalConsumers = null,
        bool source = false,
        bool terminal = false,
        bool multipleProducers = false,
        string? role = null)
    {
        optionalConsumers ??= Array.Empty<string>();
        var consumers = requiredConsumers.Concat(optionalConsumers).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var produced = producers.Count > 0;
        return new BenchmarkArtifactPlanSummary(
            Artifact: artifact.ToString(),
            IsSourceArtifact: source,
            IsProducedByStage: produced,
            IsConsumedByStage: consumers.Length > 0,
            IsTerminalArtifact: terminal || consumers.Length == 0,
            ProducerStages: producers,
            RequiredConsumerStages: requiredConsumers,
            OptionalConsumerStages: optionalConsumers,
            ConsumerStages: consumers,
            ProducerCount: producers.Count,
            ConsumerCount: consumers.Length,
            FirstProducerWave: produced ? 1 : 0,
            LastProducerWave: produced ? Math.Max(1, producers.Count) : 0,
            FirstConsumerWave: consumers.Length > 0 ? 2 : 0,
            LastConsumerWave: consumers.Length > 0 ? 1 + consumers.Length : 0,
            HasMultipleProducers: multipleProducers || producers.Count > 1,
            HasRequiredConsumers: requiredConsumers.Count > 0,
            DependencyRole: role ?? (source ? "SourceInput" : consumers.Length > 0 ? "ProducedAndConsumed" : "ProducedTerminal"),
            Evidence: new[] { "synthetic artifact plan" });
    }

    private static BenchmarkPipelinePlanIssueSummary PlanIssue(
        string code,
        DiagnosticSeverity severity,
        string stage,
        params PlanArtifactKind[] artifacts) =>
        new(
            code,
            severity.ToString(),
            stage,
            artifacts.Select(artifact => artifact.ToString()).ToArray(),
            $"Synthetic {code} issue.");

    private static BenchmarkRerunPlanSummary RerunPlan(
        string planId,
        IReadOnlyList<string> rerunStages,
        IReadOnlyList<string> affectedArtifacts,
        string mode,
        bool hasWork = true)
    {
        var waves = Enumerable.Range(2, rerunStages.Count).ToArray();
        return new BenchmarkRerunPlanSummary(
            PlanId: planId,
            DisplayName: planId,
            ChangedArtifacts: new[] { nameof(PlanArtifactKind.Walls) },
            ChangedSourceArtifacts: Array.Empty<string>(),
            DirectConsumerStages: rerunStages.Take(1).ToArray(),
            RerunStages: rerunStages,
            RerunWaves: waves,
            AffectedArtifacts: affectedArtifacts,
            FirstRerunWave: waves.Length == 0 ? -1 : waves[0],
            LastRerunWave: waves.Length == 0 ? -1 : waves[^1],
            RerunStageCount: rerunStages.Count,
            AffectedArtifactCount: affectedArtifacts.Count,
            HasWork: hasWork,
            RecommendedExecutionMode: mode,
            Evidence: new[] { "synthetic rerun plan" });
    }

    private static BenchmarkStageSummary Stage(
        string stage,
        params (PlanArtifactKind Artifact, int BeforeCount, int AfterCount)[] changes) =>
        new(
            stage,
            DurationMilliseconds: 1,
            InputCount: 0,
            OutputCount: 0,
            DiagnosticCount: 0,
            InfoCount: 0,
            WarningCount: 0,
            ErrorCount: 0,
            DisplayName: stage,
            Kind: nameof(PipelineStageKind.Topology),
            DependencyLevel: 1,
            PreferredDependencyLevel: 1,
            Reads: Array.Empty<string>(),
            OptionalReads: Array.Empty<string>(),
            Writes: changes.Select(change => change.Artifact.ToString()).ToArray(),
            Capabilities: Array.Empty<string>(),
            IsDependencyReady: true,
            MissingRequiredReads: Array.Empty<string>(),
            MissingOptionalReads: Array.Empty<string>(),
            InputArtifacts: changes
                .Select(change => new PipelineArtifactSnapshot(change.Artifact, change.BeforeCount))
                .ToArray(),
            OutputArtifacts: changes
                .Select(change => new PipelineArtifactSnapshot(change.Artifact, change.AfterCount))
                .ToArray(),
            ChangedArtifacts: changes
                .Select(change => new PipelineArtifactChange(change.Artifact, change.BeforeCount, change.AfterCount))
                .ToArray(),
            ArtifactDeltas: changes
                .Select(change => new PipelineArtifactDelta(change.Artifact, change.BeforeCount, change.AfterCount, true))
                .ToArray());

    private static BenchmarkStageSummary StageWithRuntimeReadiness(
        string stage,
        PipelineStageRuntimeReadiness runtimeReadiness,
        params (PlanArtifactKind Artifact, int BeforeCount, int AfterCount)[] changes) =>
        Stage(stage, changes) with
        {
            RuntimeReadiness = runtimeReadiness
        };

    private static BenchmarkStageSummary StageWithContract(
        string stage,
        PipelineStageContract contract,
        params (PlanArtifactKind Artifact, int BeforeCount, int AfterCount)[] changes) =>
        Stage(stage, changes) with
        {
            Contract = contract
        };

    private static BenchmarkStageSummary StageWithOutputReadiness(
        string stage,
        PipelineStageOutputReadiness outputReadiness,
        params (PlanArtifactKind Artifact, int BeforeCount, int AfterCount)[] changes) =>
        Stage(stage, changes) with
        {
            OutputReadiness = outputReadiness
        };

    private static BenchmarkStageSummary StageWithPipelineHealth(
        string stage,
        bool dependencyReady,
        PipelineStageRuntimeReadiness runtimeReadiness,
        PipelineStageContract contract,
        params (PlanArtifactKind Artifact, int BeforeCount, int AfterCount, bool IsDeclaredWrite)[] deltas) =>
        Stage(stage) with
        {
            IsDependencyReady = dependencyReady,
            MissingRequiredReads = dependencyReady ? Array.Empty<string>() : new[] { nameof(PlanArtifactKind.Walls) },
            RuntimeReadiness = runtimeReadiness,
            Contract = contract,
            ArtifactDeltas = deltas
                .Select(delta => new PipelineArtifactDelta(
                    delta.Artifact,
                    delta.BeforeCount,
                    delta.AfterCount,
                    delta.IsDeclaredWrite))
                .ToArray()
        };

    private static PipelineStageRuntimeReadiness StageRuntimeReadiness(
        IReadOnlyList<PlanArtifactKind>? nonEmptyRequiredReads = null,
        IReadOnlyList<PlanArtifactKind>? emptyRequiredReads = null,
        IReadOnlyList<PlanArtifactKind>? nonEmptyOptionalReads = null,
        IReadOnlyList<PlanArtifactKind>? emptyOptionalReads = null)
    {
        var nonEmptyRequired = nonEmptyRequiredReads?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var emptyRequired = emptyRequiredReads?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var nonEmptyOptional = nonEmptyOptionalReads?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var emptyOptional = emptyOptionalReads?.ToArray() ?? Array.Empty<PlanArtifactKind>();

        return new PipelineStageRuntimeReadiness(
            emptyRequired.Length == 0,
            emptyRequired.Length > 0,
            nonEmptyRequired,
            emptyRequired,
            emptyOptional.Length == 0,
            emptyOptional.Length > 0,
            nonEmptyOptional,
            emptyOptional,
            new[] { "synthetic runtime readiness" });
    }

    private static PipelineStageOutputReadiness StageOutputReadiness(
        IReadOnlyList<PlanArtifactKind>? nonEmptyDeclaredOutputs = null,
        IReadOnlyList<PlanArtifactKind>? emptyDeclaredOutputs = null,
        IReadOnlyList<PlanArtifactKind>? changedDeclaredOutputs = null,
        IReadOnlyList<PlanArtifactKind>? unchangedDeclaredOutputs = null,
        IReadOnlyList<PlanArtifactKind>? undeclaredChangedArtifacts = null)
    {
        var nonEmpty = nonEmptyDeclaredOutputs?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var empty = emptyDeclaredOutputs?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var changed = changedDeclaredOutputs?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var unchanged = unchangedDeclaredOutputs?.ToArray() ?? Array.Empty<PlanArtifactKind>();
        var undeclared = undeclaredChangedArtifacts?.ToArray() ?? Array.Empty<PlanArtifactKind>();

        return new PipelineStageOutputReadiness(
            empty.Length == 0,
            empty.Length > 0,
            nonEmpty,
            empty,
            changed,
            unchanged,
            undeclared.Length > 0,
            undeclared,
            new[] { "synthetic output readiness" });
    }

    private static ScanReviewQueueSummary ReviewQueue(params (string Kind, int Count)[] counts)
    {
        var kindCounts = counts
            .Where(item => item.Count > 0 && !string.IsNullOrWhiteSpace(item.Kind))
            .GroupBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);
        var total = kindCounts.Values.Sum();
        return new ScanReviewQueueSummary(
            total,
            kindCounts,
            total == 0
                ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    [DiagnosticSeverity.Info.ToString()] = total
                });
    }

    private static PlanImportReadiness Readiness(
        int walls,
        int rooms,
        int measurementOutliers)
    {
        var geometryReady = walls > 0 && rooms > 0;
        var metricReady = geometryReady && measurementOutliers == 0;
        var routingReady = geometryReady;
        var score = geometryReady
            ? metricReady ? 0.96 : 0.78
            : 0.35;
        var grade = !geometryReady
            ? "Blocked"
            : metricReady
                ? "Strong"
                : "ReviewRequired";

        return new PlanImportReadiness(
            grade,
            score,
            geometryReady,
            metricReady,
            routingReady,
            !metricReady,
            metricReady
                ? Array.Empty<string>()
                : new[] { geometryReady ? "placement.import.measurement_outliers" : "placement.import.low_coordinate_ready_ratio" },
            metricReady
                ? Array.Empty<string>()
                : new[] { geometryReady ? "placement.measurement_outliers.require_review" : "placement.import.low_coordinate_ready_ratio" },
            new[] { metricReady ? "Synthetic import-ready fixture." : "Synthetic import readiness requires review." },
            new[] { $"synthetic import readiness {grade}" });
    }

    private static BenchmarkWallPlacementSummary WallPlacement(
        int readyWalls,
        int reviewWalls = 0,
        int rejectedNoiseWalls = 0,
        int acceptedWalls = 0,
        int reviewDecisionWalls = 0,
        int rejectedWalls = 0,
        int structuralComponents = 1,
        int mainStructuralComponents = 1,
        int secondaryStructuralComponents = 0,
        int objectLikeComponents = 0,
        int isolatedFragments = 0,
        int topologyEdges = 0,
        int repairCandidates = 0,
        int blockedRepairs = 0,
        int endpointGapRepairs = 0,
        int endpointOverrunRepairs = 0,
        int highSeverityRepairs = 0)
    {
        var totalWalls = Math.Max(readyWalls + reviewWalls + rejectedNoiseWalls, readyWalls);
        var acceptedCount = acceptedWalls == 0 ? readyWalls : acceptedWalls;
        var reviewDecisionCount = reviewDecisionWalls == 0 ? reviewWalls : reviewDecisionWalls;
        var rejectedCount = rejectedWalls == 0 ? rejectedNoiseWalls : rejectedWalls;
        var repairCount = repairCandidates == 0
            ? blockedRepairs + endpointGapRepairs + endpointOverrunRepairs
            : repairCandidates;

        return new BenchmarkWallPlacementSummary(
            totalWalls,
            readyWalls,
            reviewWalls,
            rejectedNoiseWalls,
            acceptedCount,
            reviewDecisionCount,
            rejectedCount,
            structuralComponents,
            mainStructuralComponents,
            secondaryStructuralComponents,
            objectLikeComponents,
            isolatedFragments,
            topologyEdges,
            repairCount,
            blockedRepairs,
            endpointGapRepairs,
            endpointOverrunRepairs,
            highSeverityRepairs);
    }

    private static BenchmarkDetectorMetrics Metric(
        string detector,
        int expected,
        int detected,
        int matched,
        bool precisionScoring = true)
    {
        var missed = Math.Max(0, expected - matched);
        var extra = Math.Max(0, detected - matched);
        var recall = expected == 0 ? 1.0 : matched / (double)expected;
        var precision = detected == 0 ? expected == 0 ? 1.0 : 0.0 : matched / (double)detected;
        var f1 = precision + recall <= 0 ? 0 : (2 * precision * recall) / (precision + recall);
        var matches = Enumerable.Range(0, expected)
            .Select(index => new BenchmarkTargetMatchResult(
                index,
                $"target-{index + 1}",
                index < matched,
                index < matched ? $"detection-{index + 1}" : null,
                index < matched ? 1 : 0,
                index < matched ? "synthetic match" : "synthetic miss"))
            .ToArray();

        return new BenchmarkDetectorMetrics(
            detector,
            expected,
            detected,
            matched,
            missed,
            extra,
            recall,
            precision,
            f1,
            matches)
        {
            PrecisionScoringEnabled = precisionScoring
        };
    }

    private static BenchmarkCounts Counts(
        int walls,
        int rooms,
        int openings,
        int objects,
        PlanScanQualityGrade qualityGrade,
        double qualityConfidence,
        bool? qualityRequiresReview,
        int qualityIssues,
        int diagnosticErrors,
        int measurementChecked,
        int measurementConsistent,
        int measurementOutliers,
        double? measurementSelectedScale,
        double? measurementMedianScale,
        double? measurementSpread,
        double measurementConfidence) =>
        new(
            Pages: 1,
            Regions: 4,
            Dimensions: 0,
            Annotations: 0,
            AnnotationReferences: 0,
            GridAxes: 0,
            GridBaySpacings: 0,
            SurfacePatterns: 0,
            Walls: walls,
            WallNodes: walls * 2,
            WallEdges: walls,
            Rooms: rooms,
            RoomAdjacencies: 0,
            RoomClusters: rooms,
            Openings: openings,
            Objects: objects,
            ObjectGroups: 0,
            ObjectAggregates: 0,
            RoutingItems: 0,
            RoutingSuppressedObjects: 0,
            Diagnostics: diagnosticErrors,
            DiagnosticWarnings: 0,
            DiagnosticErrors: diagnosticErrors,
            QualityGrade: qualityGrade,
            QualityConfidence: qualityConfidence,
            QualityRequiresReview: qualityRequiresReview ?? qualityGrade < PlanScanQualityGrade.Usable,
            QualityIssues: qualityIssues,
            HasReliableCalibration: true,
            MeasurementCheckedCount: measurementChecked,
            MeasurementConsistentCount: measurementConsistent,
            MeasurementOutlierCount: measurementOutliers,
            MeasurementSelectedMillimetersPerDrawingUnit: measurementSelectedScale,
            MeasurementMedianMillimetersPerDrawingUnit: measurementMedianScale,
            MeasurementScaleSpreadRatio: measurementSpread,
            MeasurementConsistencyConfidence: measurementConfidence);
}
