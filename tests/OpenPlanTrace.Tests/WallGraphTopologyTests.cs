using System.Text.Json;

namespace OpenPlanTrace.Tests;

public sealed class WallGraphTopologyTests
{
    [Fact]
    public async Task ScanAsync_ClassifiesRectangularWallGraphCorners()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-corners",
                Wall("top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("left", new PlanPoint(100, 260), new PlanPoint(100, 100))));

        var cornerNodes = result.WallGraph.Nodes.Where(node => node.Kind == WallNodeKind.Corner).ToArray();

        Assert.Equal(4, cornerNodes.Length);
        Assert.All(cornerNodes, node => Assert.Equal(2, node.Degree));
        Assert.All(cornerNodes, node => Assert.Contains(node.Evidence, item => item.Contains("classified Corner", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesTWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-t-junction",
                Wall("horizontal", new PlanPoint(100, 100), new PlanPoint(420, 100)),
                Wall("vertical-stem", new PlanPoint(250, 100), new PlanPoint(250, 280))));

        var node = Assert.Single(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.TJunction);

        Assert.Equal(3, node.Degree);
        Assert.Contains("East", node.Directions);
        Assert.Contains("West", node.Directions);
        Assert.Contains("South", node.Directions);
    }

    [Fact]
    public async Task ScanAsync_InferenceConnectsNearTouchWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-near-touch-t-junction",
                Wall("horizontal-short", new PlanPoint(100, 100), new PlanPoint(195, 100)),
                Wall("vertical-host", new PlanPoint(200, 60), new PlanPoint(200, 140))));

        var node = Assert.Single(result.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && Math.Abs(node.Position.X - 200) <= 0.5
            && Math.Abs(node.Position.Y - 100) <= 0.5);

        Assert.Equal(3, node.Degree);
        Assert.Contains("North", node.Directions);
        Assert.Contains("South", node.Directions);
        Assert.Contains("West", node.Directions);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.near_touch_junctions.inferred"
                && diagnostic.Properties["inferredJunctionCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_AutoSnapsSupportedEndpointToWallGapInsideSafeTolerance()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-safe-endpoint-gap-auto-snap",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-near", new PlanPoint(200, 108), new PlanPoint(200, 220))));

        var node = Assert.Single(result.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && Math.Abs(node.Position.X - 200) <= 0.5
            && Math.Abs(node.Position.Y - 100) <= 0.5);

        Assert.Equal(3, node.Degree);
        Assert.Empty(result.WallGraph.RepairCandidates);
        var snappedPartition = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("partition-near"));
        Assert.Equal(200, snappedPartition.CenterLine.Start.X, precision: 1);
        Assert.Equal(100, snappedPartition.CenterLine.Start.Y, precision: 1);
        Assert.Equal(200, snappedPartition.CenterLine.End.X, precision: 1);
        Assert.Equal(220, snappedPartition.CenterLine.End.Y, precision: 1);
        Assert.Contains(
            snappedPartition.Evidence,
            item => item.Contains("snapped 1 near-touch endpoint gap", StringComparison.Ordinal));
        var evidenceSegment = Assert.Single(result.WallEvidenceMap.Segments, segment => segment.WallId == snappedPartition.Id);
        Assert.Equal(200, evidenceSegment.Line.Start.X, precision: 1);
        Assert.Equal(100, evidenceSegment.Line.Start.Y, precision: 1);
        Assert.Equal(200, evidenceSegment.Line.End.X, precision: 1);
        Assert.Equal(220, evidenceSegment.Line.End.Y, precision: 1);
        Assert.True(evidenceSegment.Bounds.Contains(new PlanPoint(200, 100), tolerance: 0.5));
        Assert.Contains(
            evidenceSegment.Evidence,
            item => item.Contains("wall evidence geometry synchronized", StringComparison.Ordinal));
        var evidenceAssessment = Assert.Single(result.WallEvidenceMap.WallAssessments, assessment => assessment.WallId == snappedPartition.Id);
        Assert.True(evidenceAssessment.Bounds.Contains(new PlanPoint(200, 100), tolerance: 0.5));
        Assert.Contains(
            evidenceAssessment.Evidence,
            item => item.Contains("wall evidence geometry synchronized", StringComparison.Ordinal));
        Assert.DoesNotContain(result.WallGraph.Nodes, graphNode =>
            graphNode.Position.DistanceTo(new PlanPoint(200, 108)) <= 0.5);
        Assert.DoesNotContain(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.review");
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_evidence.geometry_synchronized"
                && diagnostic.Properties["synchronizedWallCount"] == "1");
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.near_touch_junctions.inferred");
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["snappedEndpointGapCount"] == "1"
                && diagnostic.Properties["normalizedWallSegmentCount"] == "1");
    }

    [Fact]
    public async Task WallGraphStage_AutoSnapsPairedEndpointToWallGapsJustBeyondSingleSafeTolerance()
    {
        var hostWall = DetectedWall("wall-host", new PlanPoint(100, 60), new PlanPoint(100, 180));
        var upperFace = DetectedWall("wall-paired-upper-face", new PlanPoint(110, 100), new PlanPoint(240, 100));
        var lowerFace = DetectedWall("wall-paired-lower-face", new PlanPoint(110, 112), new PlanPoint(240, 112));
        var context = new ScanContext(
            Document("wall-paired-endpoint-gap-auto-snap"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { hostWall, upperFace, lowerFace });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { hostWall.Id, lowerFace.Id, upperFace.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { hostWall.Id, lowerFace.Id, upperFace.Id },
            Array.Empty<string>(),
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(context.WallGraph.RepairCandidates);
        var normalizedUpper = Assert.Single(context.Walls, wall => wall.Id == upperFace.Id);
        var normalizedLower = Assert.Single(context.Walls, wall => wall.Id == lowerFace.Id);

        Assert.Equal(100, normalizedUpper.CenterLine.Start.X, precision: 1);
        Assert.Equal(100, normalizedUpper.CenterLine.Start.Y, precision: 1);
        Assert.Equal(100, normalizedLower.CenterLine.Start.X, precision: 1);
        Assert.Equal(112, normalizedLower.CenterLine.Start.Y, precision: 1);
        Assert.Contains(context.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && node.Position.DistanceTo(new PlanPoint(100, 100)) <= 0.5);
        Assert.Contains(context.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && node.Position.DistanceTo(new PlanPoint(100, 112)) <= 0.5);
        Assert.DoesNotContain(context.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(110, 100)) <= 0.5
            || node.Position.DistanceTo(new PlanPoint(110, 112)) <= 0.5);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.paired_support_snapped"
                && diagnostic.Properties["pairedEndpointSnapCount"] == "2"
                && diagnostic.Properties["singleEndpointSafeSnapTolerance"] == "8"
                && diagnostic.Properties["pairedEndpointSnapTolerance"] == "12");
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["snappedEndpointGapCount"] == "2"
                && diagnostic.Properties["normalizedWallSegmentCount"] == "2");
    }

    [Fact]
    public async Task WallGraphStage_DoesNotAutoSnapPairedEndpointGapsNearOpeningEvidence()
    {
        var hostWall = DetectedWall("wall-host", new PlanPoint(100, 60), new PlanPoint(100, 180));
        var upperFace = DetectedWall("wall-door-upper-face", new PlanPoint(110, 100), new PlanPoint(240, 100));
        var lowerFace = DetectedWall("wall-door-lower-face", new PlanPoint(110, 112), new PlanPoint(240, 112));
        var context = new ScanContext(
            Document(
                "wall-paired-endpoint-gap-door-veto",
                new ArcPrimitive(new PlanPoint(105, 106), 12, 0, Math.PI / 2)
                {
                    SourceId = "door-swing",
                    Layer = "A-DOOR",
                    Source = new PrimitiveSourceMetadata
                    {
                        SourceFormat = "test",
                        SourceId = "door-swing",
                        EntityType = "ARC",
                        Layer = "A-DOOR",
                        DrawingSpace = SourceDrawingSpace.Model
                    }
                }),
            new ScannerOptions());
        context.Walls.AddRange(new[] { hostWall, upperFace, lowerFace });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { hostWall.Id, lowerFace.Id, upperFace.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { hostWall.Id, lowerFace.Id, upperFace.Id },
            Array.Empty<string>(),
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var retainedUpper = Assert.Single(context.Walls, wall => wall.Id == upperFace.Id);
        var retainedLower = Assert.Single(context.Walls, wall => wall.Id == lowerFace.Id);

        Assert.Equal(110, retainedUpper.CenterLine.Start.X, precision: 1);
        Assert.Equal(110, retainedLower.CenterLine.Start.X, precision: 1);
        Assert.DoesNotContain(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.paired_support_snapped");
        Assert.Contains(context.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(110, 100)) <= 0.5);
        Assert.Contains(context.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(110, 112)) <= 0.5);
    }

    [Fact]
    public async Task WallGraphStage_DoesNotAutoRepairReviewRequiredWallCoordinates()
    {
        var hostWall = DetectedWall("wall-host", new PlanPoint(100, 100), new PlanPoint(320, 100));
        var reviewWall = DetectedWall("wall-review-partition", new PlanPoint(200, 108), new PlanPoint(200, 220));
        var context = new ScanContext(
            Document("wall-review-coordinate-repair-gate"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { hostWall, reviewWall });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { hostWall.Id, reviewWall.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { hostWall.Id },
            new[] { reviewWall.Id },
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var retainedReviewWall = Assert.Single(context.Walls, wall => wall.Id == reviewWall.Id);

        Assert.Equal(200, retainedReviewWall.CenterLine.Start.X, precision: 1);
        Assert.Equal(108, retainedReviewWall.CenterLine.Start.Y, precision: 1);
        Assert.Equal(200, retainedReviewWall.CenterLine.End.X, precision: 1);
        Assert.Equal(220, retainedReviewWall.CenterLine.End.Y, precision: 1);
        Assert.DoesNotContain(
            retainedReviewWall.Evidence,
            item => item.Contains("snapped", StringComparison.OrdinalIgnoreCase)
                || item.Contains("trimmed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.junctions.review_trust_gated"
                && diagnostic.Properties["suppressedJunctionPairCount"] == "1"
                && diagnostic.Properties["acceptedGraphWallCount"] == "1"
                && diagnostic.Properties["reviewGraphWallCount"] == "1"
                && diagnostic.Properties["automaticCoordinateRepairWallCount"] == "1"
                && diagnostic.Properties["wallIds"] == reviewWall.Id);
    }

    [Fact]
    public async Task WallGraphStage_UsesTrustedMediumReviewWallBodyAsCoordinateRepairSupport()
    {
        var acceptedHost = DetectedWall("wall-accepted-host", new PlanPoint(100, 100), new PlanPoint(320, 100));
        var reviewPartition = DetectedWall("wall-review-medium-partition", new PlanPoint(200, 100), new PlanPoint(200, 220))
            with
            {
                WallType = WallType.Interior
            };
        var context = new ScanContext(
            Document("wall-trusted-review-coordinate-repair-support"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { acceptedHost, reviewPartition });
        context.WallEvidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            new[]
            {
                TrustedReviewBand(reviewPartition)
            },
            new[]
            {
                Assessment(acceptedHost, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody, Confidence.High),
                Assessment(reviewPartition, WallEvidenceDecision.Review, WallEvidenceCategory.MediumWallBody, new Confidence(0.82))
            });

        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);
        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Contains(reviewPartition.Id, context.WallTopologyPreparation.ReviewGraphWallIds);
        Assert.DoesNotContain(reviewPartition.Id, context.WallTopologyPreparation.AutomaticCoordinateRepairWallIds);

        var node = Assert.Single(context.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && node.Position.DistanceTo(new PlanPoint(200, 100)) <= 0.5);

        Assert.Equal(3, node.Degree);
        Assert.Equal(2, context.WallGraph.Edges.Count(edge => edge.WallId == acceptedHost.Id));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.coordinate_repair.trusted_review_support"
                && diagnostic.Properties["trustedReviewCoordinateRepairWallCount"] == "1"
                && diagnostic.Properties["wallIds"] == reviewPartition.Id);
        Assert.DoesNotContain(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.junctions.review_trust_gated");
    }

    [Fact]
    public async Task WallGraphStage_PromotesTrustedMainStructuralMediumPairedWallsForPlacement()
    {
        var top = DetectedWall("wall-top", new PlanPoint(100, 100), new PlanPoint(320, 100)) with { WallType = WallType.Exterior };
        var bottom = DetectedWall("wall-bottom", new PlanPoint(100, 220), new PlanPoint(320, 220)) with { WallType = WallType.Exterior };
        var reviewPartition = DetectedWall("wall-review-short-paired", new PlanPoint(200, 100), new PlanPoint(200, 220)) with { WallType = WallType.Interior };
        var context = new ScanContext(
            Document("wall-main-structural-medium-promotion"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { top, bottom, reviewPartition });
        context.WallEvidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            new[]
            {
                TrustedReviewBand(reviewPartition)
            },
            new[]
            {
                Assessment(top, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody, Confidence.High),
                Assessment(bottom, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody, Confidence.High),
                MediumPairedReviewAssessment(reviewPartition)
            });

        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);
        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var promoted = Assert.Single(context.WallEvidenceMap.WallAssessments, assessment => assessment.WallId == reviewPartition.Id);
        Assert.True(promoted.PlacementReady);
        Assert.False(promoted.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Accept, promoted.Decision);
        Assert.Contains(
            promoted.Evidence,
            item => item.Contains("promoted to placement-ready by main structural graph component", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_evidence.main_structural_medium_walls_promoted"
                && diagnostic.Properties["promotedWallCount"] == "1"
                && diagnostic.Properties["wallIds"] == reviewPartition.Id);
    }

    [Fact]
    public async Task WallGraphStage_DoesNotPromoteDuplicateMainStructuralMediumWallForPlacement()
    {
        var top = DetectedWall("wall-top", new PlanPoint(100, 100), new PlanPoint(320, 100)) with { WallType = WallType.Exterior };
        var bottom = DetectedWall("wall-bottom", new PlanPoint(100, 220), new PlanPoint(320, 220)) with { WallType = WallType.Exterior };
        var duplicateReview = DetectedWall("wall-duplicate-review", new PlanPoint(200, 100), new PlanPoint(200, 220)) with { WallType = WallType.Interior };
        var context = new ScanContext(
            Document("wall-main-structural-medium-promotion-blocked"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { top, bottom, duplicateReview });
        context.WallEvidenceMap = new WallEvidenceMap(
            Array.Empty<WallEvidenceSegment>(),
            new[]
            {
                TrustedReviewBand(duplicateReview)
            },
            new[]
            {
                Assessment(top, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody, Confidence.High),
                Assessment(bottom, WallEvidenceDecision.Accept, WallEvidenceCategory.StrongWallBody, Confidence.High),
                MediumPairedReviewAssessment(
                    duplicateReview,
                    "wall evidence: duplicate wall-face line already represented by stronger paired wall body wall-top; keep for review but block exact placement")
            });

        await new WallTopologyPreparationStage().ExecuteAsync(context, CancellationToken.None);
        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var retainedReview = Assert.Single(context.WallEvidenceMap.WallAssessments, assessment => assessment.WallId == duplicateReview.Id);
        Assert.False(retainedReview.PlacementReady);
        Assert.True(retainedReview.RequiresReview);
        Assert.Equal(WallEvidenceDecision.Review, retainedReview.Decision);
        Assert.DoesNotContain(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_evidence.main_structural_medium_walls_promoted");
    }

    [Fact]
    public async Task WallGraphStage_DoesNotUseReviewRequiredWallsAsAutoRepairSupport()
    {
        var reviewHostWall = DetectedWall("wall-review-host", new PlanPoint(100, 100), new PlanPoint(320, 100));
        var acceptedPartition = DetectedWall("wall-accepted-partition", new PlanPoint(200, 108), new PlanPoint(200, 220));
        var context = new ScanContext(
            Document("wall-review-support-coordinate-repair-gate"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { reviewHostWall, acceptedPartition });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { acceptedPartition.Id, reviewHostWall.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { acceptedPartition.Id },
            new[] { reviewHostWall.Id },
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var retainedPartition = Assert.Single(context.Walls, wall => wall.Id == acceptedPartition.Id);

        Assert.Equal(200, retainedPartition.CenterLine.Start.X, precision: 1);
        Assert.Equal(108, retainedPartition.CenterLine.Start.Y, precision: 1);
        Assert.Equal(200, retainedPartition.CenterLine.End.X, precision: 1);
        Assert.Equal(220, retainedPartition.CenterLine.End.Y, precision: 1);
        Assert.DoesNotContain(
            retainedPartition.Evidence,
            item => item.Contains("snapped", StringComparison.OrdinalIgnoreCase)
                || item.Contains("trimmed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_evidence.geometry_synchronized");
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.coordinate_repair.review_support_excluded"
                && diagnostic.Properties["excludedSupportWallCount"] == "1"
                && diagnostic.Properties["acceptedGraphWallCount"] == "1"
                && diagnostic.Properties["reviewGraphWallCount"] == "1"
                && diagnostic.Properties["automaticCoordinateRepairWallCount"] == "1"
                && diagnostic.Properties["wallIds"] == reviewHostWall.Id);
    }

    [Fact]
    public async Task WallGraphStage_DoesNotCreateEndpointGapRepairCandidatesForReviewWalls()
    {
        var top = DetectedWall("wall-room-top", new PlanPoint(100, 100), new PlanPoint(320, 100));
        var right = DetectedWall("wall-room-right", new PlanPoint(320, 100), new PlanPoint(320, 260));
        var bottom = DetectedWall("wall-room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260));
        var left = DetectedWall("wall-room-left", new PlanPoint(100, 260), new PlanPoint(100, 100));
        var reviewPartition = DetectedWall("wall-review-partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220));
        var context = new ScanContext(
            Document("wall-review-gap-repair-candidate-gate"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { top, right, bottom, left, reviewPartition });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { bottom.Id, left.Id, reviewPartition.Id, right.Id, top.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { bottom.Id, left.Id, right.Id, top.Id },
            new[] { reviewPartition.Id },
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(context.WallGraph.RepairCandidates);
        Assert.DoesNotContain(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.review");
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.review_candidate_trust_gated"
                && diagnostic.Properties["suppressedEndpointGapCandidateCount"] == "1"
                && diagnostic.Properties["reviewGraphWallCount"] == "1"
                && diagnostic.Properties["automaticCoordinateRepairWallCount"] == "4"
                && diagnostic.Properties["wallIds"] == reviewPartition.Id);
    }

    [Fact]
    public async Task WallGraphStage_DoesNotLetReviewWallsSplitTrustedTopologyJunctions()
    {
        var hostWall = DetectedWall("wall-accepted-host", new PlanPoint(100, 100), new PlanPoint(320, 100));
        var reviewPartition = DetectedWall("wall-review-touching-partition", new PlanPoint(200, 100), new PlanPoint(200, 220));
        var context = new ScanContext(
            Document("wall-review-junction-split-gate"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { hostWall, reviewPartition });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { hostWall.Id, reviewPartition.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { hostWall.Id },
            new[] { reviewPartition.Id },
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(1, context.WallGraph.Edges.Count(edge => edge.WallId == hostWall.Id));
        Assert.Equal(1, context.WallGraph.Edges.Count(edge => edge.WallId == reviewPartition.Id));
        Assert.DoesNotContain(context.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && node.Position.DistanceTo(new PlanPoint(200, 100)) <= 0.5);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.junctions.review_trust_gated"
                && diagnostic.Properties["suppressedJunctionPairCount"] == "1"
                && diagnostic.Properties["acceptedGraphWallCount"] == "1"
                && diagnostic.Properties["reviewGraphWallCount"] == "1"
                && diagnostic.Properties["automaticCoordinateRepairWallCount"] == "1"
                && diagnostic.Properties["wallIds"] == reviewPartition.Id);
    }

    [Fact]
    public async Task WallGraphStage_AutoSnapsTrustedEndpointToWallGapJustBeyondSingleSafeTolerance()
    {
        var hostWall = DetectedWall("wall-trusted-host", new PlanPoint(100, 60), new PlanPoint(100, 180))
            with
            {
                DetectionKind = WallDetectionKind.ParallelLinePair,
                Evidence = new[] { "parallel wall-face pair", "wall evidence assessment: StrongWallBody / placement-ready / confidence 0.91" }
            };
        var partition = DetectedWall("wall-trusted-gap-partition", new PlanPoint(111, 100), new PlanPoint(240, 100))
            with
            {
                Confidence = new Confidence(0.72),
                Evidence = new[] { "single wall-length vector run", "wall evidence assessment: MediumWallBody / placement-ready / confidence 0.72" }
            };
        var context = new ScanContext(
            Document("wall-trusted-endpoint-gap-auto-snap"),
            new ScannerOptions());
        context.Walls.AddRange(new[] { hostWall, partition });
        context.WallTopologyPreparation = new WallTopologyPreparation(
            new[] { hostWall.Id, partition.Id },
            Array.Empty<WallTopologyRejectedWall>(),
            new[] { hostWall.Id, partition.Id },
            Array.Empty<string>(),
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        Assert.Empty(context.WallGraph.RepairCandidates);
        var normalizedPartition = Assert.Single(context.Walls, wall => wall.Id == partition.Id);

        Assert.Equal(100, normalizedPartition.CenterLine.Start.X, precision: 1);
        Assert.Equal(100, normalizedPartition.CenterLine.Start.Y, precision: 1);
        Assert.Contains(context.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.TJunction
            && node.Position.DistanceTo(new PlanPoint(100, 100)) <= 0.5);
        Assert.DoesNotContain(context.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(111, 100)) <= 0.5);
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.trusted_endpoint_snapped"
                && diagnostic.Properties["trustedEndpointSnapCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_QueuesReviewForEndpointGapJustOutsideSafeAutoSnapTolerance()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-endpoint-gap-just-outside-auto-snap",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 108.4), new PlanPoint(200, 220))));

        Assert.DoesNotContain(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.TJunction);
        var repairCandidate = Assert.Single(result.WallGraph.RepairCandidates);

        Assert.Equal(WallGraphRepairCandidateKind.EndpointToWall, repairCandidate.Kind);
        Assert.Equal(8.4, repairCandidate.GapDistance, precision: 1);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gap.review"
                && diagnostic.Properties["gapDistance"] == "8.4");
    }

    [Fact]
    public async Task ScanAsync_TrimsTinyWallGraphEndpointOverrunsAtSupportedJunctions()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-overrun-normalized",
                Wall("top-overrun", new PlanPoint(84, 100), new PlanPoint(320, 100)),
                Wall("left", new PlanPoint(100, 100), new PlanPoint(100, 260))));

        Assert.DoesNotContain(result.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(84, 100)) <= 0.5);
        var corner = Assert.Single(result.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.Corner
            && Math.Abs(node.Position.X - 100) <= 0.5
            && Math.Abs(node.Position.Y - 100) <= 0.5);
        var trimmedWall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("top-overrun"));

        Assert.Equal(2, corner.Degree);
        Assert.Contains("East", corner.Directions);
        Assert.Contains("South", corner.Directions);
        Assert.DoesNotContain("West", corner.Directions);
        Assert.Equal(100, trimmedWall.CenterLine.Start.X, precision: 1);
        Assert.Equal(100, trimmedWall.CenterLine.Start.Y, precision: 1);
        Assert.Equal(320, trimmedWall.CenterLine.End.X, precision: 1);
        Assert.Contains(
            trimmedWall.Evidence,
            item => item.Contains("trimmed 1 supported endpoint overrun", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["trimmedEndpointOverrunCount"] == "1"
                && diagnostic.Properties["normalizedWallSegmentCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_TrimsModerateWallGraphEndpointOverrunsWhenPerpendicularJunctionIsSupported()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-moderate-overrun-normalized",
                Wall("top-overrun", new PlanPoint(60, 100), new PlanPoint(320, 100)),
                Wall("left", new PlanPoint(100, 100), new PlanPoint(100, 260))));

        var trimmedWall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("top-overrun"));
        var corner = Assert.Single(result.WallGraph.Nodes, node =>
            node.Kind == WallNodeKind.Corner
            && Math.Abs(node.Position.X - 100) <= 0.5
            && Math.Abs(node.Position.Y - 100) <= 0.5);

        Assert.Equal(100, trimmedWall.CenterLine.Start.X, precision: 1);
        Assert.Equal(100, trimmedWall.CenterLine.Start.Y, precision: 1);
        Assert.Equal(320, trimmedWall.CenterLine.End.X, precision: 1);
        Assert.Equal(2, corner.Degree);
        Assert.DoesNotContain(result.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(60, 100)) <= 0.5);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["trimmedEndpointOverrunCount"] == "1");
    }

    [Fact]
    public async Task ScanAsync_QueuesReviewForLongEndpointOverrunBeyondSafeAutoTrimTolerance()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-long-overrun-review",
                Wall("top-long-overrun", new PlanPoint(20, 100), new PlanPoint(320, 100)),
                Wall("left", new PlanPoint(100, 100), new PlanPoint(100, 260))));

        var overrunWall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("top-long-overrun"));
        var repairCandidate = Assert.Single(result.WallGraph.RepairCandidates);

        Assert.Equal(20, overrunWall.CenterLine.Start.X, precision: 1);
        Assert.DoesNotContain(result.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(20, 100)) <= 0.5);
        Assert.Equal(WallGraphRepairCandidateKind.EndpointOverrun, repairCandidate.Kind);
        Assert.Equal(WallGraphRepairAction.TrimEndpointOverrun, repairCandidate.SuggestedAction);
        Assert.Equal(WallGraphRepairApplicability.ReviewAndApplySuggestedTrim, repairCandidate.Applicability);
        Assert.Equal(80, repairCandidate.GapDistance, precision: 1);
        Assert.True(repairCandidate.SafeSnapDistance < repairCandidate.GapDistance);
        Assert.Equal(20, repairCandidate.SourcePoint.X, precision: 1);
        Assert.Equal(100, repairCandidate.SourcePoint.Y, precision: 1);
        Assert.Equal(100, repairCandidate.TargetPoint.X, precision: 1);
        Assert.Equal(100, repairCandidate.TargetPoint.Y, precision: 1);
        Assert.Contains("top-long-overrun", repairCandidate.SourcePrimitiveIds);
        Assert.Contains("left", repairCandidate.SourcePrimitiveIds);
        Assert.Contains(
            repairCandidate.Evidence,
            item => item.Contains("possible overextended wall endpoint", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_overruns.detected"
                && diagnostic.Properties["overrunCount"] == "1");
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_overrun.review"
                && diagnostic.Properties["suggestedAction"] == "TrimEndpointOverrun"
                && diagnostic.Properties["overrunDistance"] == "80");
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["suppressedEndpointOverrunTailEdgeCount"] == "1");
    }

    [Fact]
    public async Task PlacementExporter_IncludesEndpointOverrunTrimRepairCandidate()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-long-overrun-placement-export",
                Wall("top-long-overrun", new PlanPoint(20, 100), new PlanPoint(320, 100)),
                Wall("left", new PlanPoint(100, 100), new PlanPoint(100, 260))));

        result = result with
        {
            Calibration = result.Calibration with
            {
                MillimetersPerDrawingUnit = 10,
                Confidence = Confidence.High
            }
        };

        using var parsed = JsonDocument.Parse(PlanPlacementJsonExporter.Serialize(result));
        var root = parsed.RootElement;
        var candidate = Assert.Single(root.GetProperty("wallGraphRepairCandidates").EnumerateArray());
        var wall = Assert.Single(root.GetProperty("walls").EnumerateArray(), item =>
            item.GetProperty("sourcePrimitiveIds").EnumerateArray().Any(id => id.GetString() == "top-long-overrun"));

        Assert.Equal("EndpointOverrun", candidate.GetProperty("kind").GetString());
        Assert.Equal("TrimEndpointOverrun", candidate.GetProperty("suggestedAction").GetString());
        Assert.Equal("ReviewAndApplySuggestedTrim", candidate.GetProperty("applicability").GetString());
        Assert.Equal(80, candidate.GetProperty("gapDistanceDrawingUnits").GetDouble(), precision: 1);
        Assert.Equal(800, candidate.GetProperty("gapDistanceMillimeters").GetDouble(), precision: 1);
        Assert.Equal(200, candidate.GetProperty("sourcePointMillimeters").GetProperty("x").GetDouble(), precision: 1);
        Assert.Equal(1000, candidate.GetProperty("targetPointMillimeters").GetProperty("x").GetDouble(), precision: 1);
        Assert.Contains("endpoint-overrun trim", candidate.GetProperty("recommendedAction").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            candidate.GetProperty("id").GetString(),
            wall.GetProperty("wallGraphRepairCandidateIds").EnumerateArray().Select(item => item.GetString()));
        Assert.True(wall.GetProperty("reliability").GetProperty("requiresReview").GetBoolean());
        Assert.Contains(
            wall.GetProperty("reliability").GetProperty("reasons").EnumerateArray().Select(item => item.GetString()),
            reason => reason is not null
                && reason.Contains(candidate.GetProperty("id").GetString()!, StringComparison.Ordinal)
                && reason.Contains(candidate.GetProperty("importImpact").GetString()!, StringComparison.Ordinal)
                && reason.Contains("endpoint-overrun trim", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ScanAsync_DoesNotTrimWallEndpointOverrunWhenOuterEndpointIsConnected()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-connected-tail-not-trimmed",
                Wall("top", new PlanPoint(60, 100), new PlanPoint(320, 100)),
                Wall("left-end", new PlanPoint(60, 100), new PlanPoint(60, 260)),
                Wall("partition", new PlanPoint(100, 100), new PlanPoint(100, 260))));

        var wall = Assert.Single(result.Walls, wall => wall.SourcePrimitiveIds.Contains("top"));

        Assert.Equal(60, wall.CenterLine.Start.X, precision: 1);
        Assert.Contains(result.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(60, 100)) <= 0.5
            && node.Degree >= 2);
    }

    [Fact]
    public async Task ScanAsync_ConnectsOverlappingCollinearWallFragmentsIntoOneComponent()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-collinear-overlap-normalized",
                Wall("left-fragment", new PlanPoint(100, 100), new PlanPoint(260, 100)),
                Wall("right-fragment", new PlanPoint(220, 101.5), new PlanPoint(360, 101.5))),
            new ScannerOptions
            {
                WallMergeTolerance = 0,
                MaxWallFragmentGap = 0
            });

        var component = Assert.Single(result.WallGraph.Components);

        Assert.Equal(2, component.WallCount);
        Assert.Contains("left-fragment", component.SourcePrimitiveIds);
        Assert.Contains("right-fragment", component.SourcePrimitiveIds);
        Assert.Contains(result.WallGraph.Nodes, node =>
            Math.Abs(node.Position.X - 220) <= 0.5
            && Math.Abs(node.Position.Y - 100.75) <= 0.75);
        Assert.Contains(result.WallGraph.Nodes, node =>
            Math.Abs(node.Position.X - 260) <= 0.5
            && Math.Abs(node.Position.Y - 100.75) <= 0.75);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.topology.normalized"
                && diagnostic.Properties["collinearJunctionCount"] == "2");
    }

    [Fact]
    public async Task ScanAsync_QueuesReviewForUnresolvedWallEndpointGap()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-review",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        Assert.DoesNotContain(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.TJunction);
        var diagnostic = Assert.Single(result.Diagnostics.Messages, diagnostic =>
            diagnostic.Code == "wall_graph.endpoint_gap.review");
        var repairCandidate = Assert.Single(result.WallGraph.RepairCandidates);

        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.PageNumber);
        Assert.Equal("EndpointToWall", diagnostic.Properties["gapKind"]);
        Assert.Equal("12", diagnostic.Properties["gapDistance"]);
        Assert.Equal("8", diagnostic.Properties["safeSnapDistance"]);
        Assert.Equal("18", diagnostic.Properties["reviewDistanceLimit"]);
        Assert.Equal("4", diagnostic.Properties["excessDistanceBeyondSafeSnap"]);
        Assert.Equal("Medium", diagnostic.Properties["severity"]);
        Assert.Equal("TopologyReviewRequired", diagnostic.Properties["importImpact"]);
        Assert.Equal("ReviewAndApplySuggestedSnap", diagnostic.Properties["applicability"]);
        Assert.Equal(repairCandidate.Id, diagnostic.Properties["repairCandidateId"]);
        Assert.Equal("SnapEndpointToWall", diagnostic.Properties["suggestedAction"]);
        Assert.Contains("room-top", diagnostic.SourcePrimitiveIds);
        Assert.Contains("partition-gap", diagnostic.SourcePrimitiveIds);
        Assert.NotNull(diagnostic.Region);
        Assert.Equal(WallGraphRepairCandidateKind.EndpointToWall, repairCandidate.Kind);
        Assert.Equal(WallGraphRepairAction.SnapEndpointToWall, repairCandidate.SuggestedAction);
        Assert.Equal(WallGraphRepairSeverity.Medium, repairCandidate.Severity);
        Assert.Equal(WallGraphRepairImportImpact.TopologyReviewRequired, repairCandidate.ImportImpact);
        Assert.Equal(WallGraphRepairApplicability.ReviewAndApplySuggestedSnap, repairCandidate.Applicability);
        Assert.Equal(12, repairCandidate.GapDistance);
        Assert.Equal(8, repairCandidate.SafeSnapDistance);
        Assert.Equal(18, repairCandidate.ReviewDistanceLimit);
        Assert.Equal(4, repairCandidate.ExcessDistanceBeyondSafeSnap);
        Assert.True(repairCandidate.RequiresReview);
        Assert.Contains("room-top", repairCandidate.SourcePrimitiveIds);
        Assert.Contains("partition-gap", repairCandidate.SourcePrimitiveIds);
        Assert.Equal(repairCandidate.SourcePoint, repairCandidate.RepairLine.Start);
        Assert.Equal(repairCandidate.TargetPoint, repairCandidate.RepairLine.End);
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.endpoint_gaps.detected"
                && diagnostic.Properties["gapCount"] == "1");
    }

    [Fact]
    public async Task JsonExporter_IncludesWallGraphRepairCandidates()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-repair-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var candidate = Assert.Single(parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("repairCandidates")
            .EnumerateArray());

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal("EndpointToWall", candidate.GetProperty("kind").GetString());
        Assert.Equal("SnapEndpointToWall", candidate.GetProperty("suggestedAction").GetString());
        Assert.Equal("Medium", candidate.GetProperty("severity").GetString());
        Assert.Equal("TopologyReviewRequired", candidate.GetProperty("importImpact").GetString());
        Assert.Equal("ReviewAndApplySuggestedSnap", candidate.GetProperty("applicability").GetString());
        Assert.Equal(12, candidate.GetProperty("gapDistance").GetDouble());
        Assert.Equal(8, candidate.GetProperty("safeSnapDistance").GetDouble());
        Assert.Equal(18, candidate.GetProperty("reviewDistanceLimit").GetDouble());
        Assert.Equal(4, candidate.GetProperty("excessDistanceBeyondSafeSnap").GetDouble());
        Assert.True(candidate.GetProperty("requiresReview").GetBoolean());
        Assert.Equal(200, candidate.GetProperty("sourcePoint").GetProperty("x").GetDouble());
        Assert.Equal(112, candidate.GetProperty("sourcePoint").GetProperty("y").GetDouble());
        Assert.Equal(200, candidate.GetProperty("targetPoint").GetProperty("x").GetDouble());
        Assert.Equal(100, candidate.GetProperty("targetPoint").GetProperty("y").GetDouble());
        Assert.Contains("room-top", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("partition-gap", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task PlacementExporter_IncludesWallGraphRepairCandidatesWithMetricCoordinates()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-gap-placement-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("partition-gap", new PlanPoint(200, 112), new PlanPoint(200, 220))));

        result = result with
        {
            Calibration = result.Calibration with
            {
                MillimetersPerDrawingUnit = 10,
                Confidence = Confidence.High
            }
        };

        var json = PlanPlacementJsonExporter.Serialize(
            result,
            new PlanPlacementJsonExportOptions { WriteIndented = false });
        using var parsed = JsonDocument.Parse(json);
        var root = parsed.RootElement;
        var candidate = Assert.Single(root.GetProperty("wallGraphRepairCandidates").EnumerateArray());

        Assert.Equal(1, root.GetProperty("summary").GetProperty("wallGraphRepairCandidateCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("pageSummaries")[0].GetProperty("wallGraphRepairCandidateCount").GetInt32());
        Assert.Equal("EndpointToWall", candidate.GetProperty("kind").GetString());
        Assert.Equal("SnapEndpointToWall", candidate.GetProperty("suggestedAction").GetString());
        Assert.Equal("Medium", candidate.GetProperty("severity").GetString());
        Assert.Equal("TopologyReviewRequired", candidate.GetProperty("importImpact").GetString());
        Assert.Equal("ReviewAndApplySuggestedSnap", candidate.GetProperty("applicability").GetString());
        Assert.Equal(12, candidate.GetProperty("gapDistanceDrawingUnits").GetDouble());
        Assert.Equal(120, candidate.GetProperty("gapDistanceMillimeters").GetDouble());
        Assert.Equal(8, candidate.GetProperty("safeSnapDistanceDrawingUnits").GetDouble());
        Assert.Equal(80, candidate.GetProperty("safeSnapDistanceMillimeters").GetDouble());
        Assert.Equal(18, candidate.GetProperty("reviewDistanceLimitDrawingUnits").GetDouble());
        Assert.Equal(180, candidate.GetProperty("reviewDistanceLimitMillimeters").GetDouble());
        Assert.Equal(4, candidate.GetProperty("excessDistanceBeyondSafeSnapDrawingUnits").GetDouble());
        Assert.Equal(40, candidate.GetProperty("excessDistanceBeyondSafeSnapMillimeters").GetDouble());
        Assert.Equal(2000, candidate.GetProperty("sourcePointMillimeters").GetProperty("x").GetDouble());
        Assert.Equal(1120, candidate.GetProperty("sourcePointMillimeters").GetProperty("y").GetDouble());
        Assert.Equal(2000, candidate.GetProperty("targetPointMillimeters").GetProperty("x").GetDouble());
        Assert.Equal(1000, candidate.GetProperty("targetPointMillimeters").GetProperty("y").GetDouble());
        Assert.True(candidate.GetProperty("requiresReview").GetBoolean());
        Assert.Contains("endpoint-to-wall", candidate.GetProperty("recommendedAction").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("room-top", candidate.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task ScanAsync_ClassifiesCrossingWallGraphJunction()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-crossing",
                Wall("horizontal", new PlanPoint(100, 100), new PlanPoint(420, 100)),
                Wall("vertical", new PlanPoint(250, 40), new PlanPoint(250, 260))));

        var node = Assert.Single(result.WallGraph.Nodes, node => node.Kind == WallNodeKind.Crossing);

        Assert.Equal(4, node.Degree);
        Assert.Contains("North", node.Directions);
        Assert.Contains("East", node.Directions);
        Assert.Contains("South", node.Directions);
        Assert.Contains("West", node.Directions);
    }

    [Fact]
    public async Task ScanAsync_KeepsDominantJunctionCoordinateWhenNearDuplicateLineWouldDriftNode()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-crossing-dominant-coordinate",
                Wall("horizontal", new PlanPoint(100, 100), new PlanPoint(420, 100)),
                Wall("vertical-primary", new PlanPoint(250, 40), new PlanPoint(250, 260)),
                Wall("nearby-detail", new PlanPoint(251.6, 80), new PlanPoint(251.6, 140))),
            new ScannerOptions
            {
                WallMergeTolerance = 0,
                MaxWallFragmentGap = 0
            });

        var node = Assert.Single(result.WallGraph.Nodes, node =>
            node.Position.DistanceTo(new PlanPoint(250, 100)) <= 0.05
            && node.Kind == WallNodeKind.Crossing);

        Assert.Equal(250, node.Position.X, precision: 2);
        Assert.Equal(100, node.Position.Y, precision: 2);
        Assert.Contains(
            node.Evidence,
            item => item.Contains("position resolved from", StringComparison.Ordinal));
        Assert.Contains(
            node.Evidence,
            item => item.Contains("snap observation spread", StringComparison.Ordinal));
        Assert.DoesNotContain(result.WallGraph.Nodes, candidate =>
            Math.Abs(candidate.Position.X - 250.8) <= 0.1
            && Math.Abs(candidate.Position.Y - 100) <= 0.1);
    }

    [Fact]
    public async Task ScanAsync_SummarizesWallGraphComponentsForStructuralReview()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-components",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("table-top", new PlanPoint(420, 160), new PlanPoint(455, 160)),
                Wall("table-right", new PlanPoint(455, 160), new PlanPoint(455, 185)),
                Wall("table-bottom", new PlanPoint(455, 185), new PlanPoint(420, 185)),
                Wall("table-left", new PlanPoint(420, 185), new PlanPoint(420, 160))));

        var main = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.MainStructural);
        var objectLike = Assert.Single(result.WallGraph.Components, component => component.Kind == WallGraphComponentKind.ObjectLikeIsland);

        Assert.Equal(4, main.WallCount);
        Assert.Equal(4, objectLike.WallCount);
        Assert.Equal(4, objectLike.EdgeCount);
        Assert.Contains("table-top", objectLike.SourcePrimitiveIds);
        Assert.Contains(objectLike.Evidence, item => item.Contains("possible object", StringComparison.Ordinal));
        Assert.Contains(
            result.Diagnostics.Messages,
            diagnostic => diagnostic.Code == "wall_graph.object_like_components.review"
                && diagnostic.Properties["objectLikeIslandCount"] == "1");
    }

    [Fact]
    public async Task WallGraphStage_DemotesCompactStairDetailComponentFromPlacementTopology()
    {
        var mainWalls = new[]
        {
            DetectedWall("main-top", new PlanPoint(80, 80), new PlanPoint(620, 80)),
            DetectedWall("main-right", new PlanPoint(620, 80), new PlanPoint(620, 430)),
            DetectedWall("main-bottom", new PlanPoint(620, 430), new PlanPoint(80, 430)),
            DetectedWall("main-left", new PlanPoint(80, 430), new PlanPoint(80, 80))
        };
        var detailWalls = new[]
        {
            DetectedWall("stair-left", new PlanPoint(150, 470), new PlanPoint(150, 590)),
            DetectedWall("stair-right", new PlanPoint(210, 470), new PlanPoint(210, 590)),
            DetectedWall("stair-riser-1", new PlanPoint(150, 470), new PlanPoint(210, 470)),
            DetectedWall("stair-riser-2", new PlanPoint(150, 490), new PlanPoint(210, 490)),
            DetectedWall("stair-riser-3", new PlanPoint(150, 510), new PlanPoint(210, 510)),
            DetectedWall("stair-riser-4", new PlanPoint(150, 530), new PlanPoint(210, 530)),
            DetectedWall("stair-riser-5", new PlanPoint(150, 550), new PlanPoint(210, 550)),
            DetectedWall("stair-riser-6", new PlanPoint(150, 570), new PlanPoint(210, 570)),
            DetectedWall("stair-riser-7", new PlanPoint(150, 590), new PlanPoint(210, 590)),
            DetectedWall("stair-diagonal-1", new PlanPoint(150, 470), new PlanPoint(210, 490)),
            DetectedWall("stair-diagonal-2", new PlanPoint(150, 490), new PlanPoint(210, 510)),
            DetectedWall("stair-diagonal-3", new PlanPoint(150, 510), new PlanPoint(210, 530)),
            DetectedWall("stair-diagonal-4", new PlanPoint(150, 530), new PlanPoint(210, 550)),
            DetectedWall("stair-diagonal-5", new PlanPoint(150, 550), new PlanPoint(210, 570))
        };
        var context = new ScanContext(
            new PlanDocument(
                "stair-detail-component",
                new[] { new PlanPage(1, new PlanSize(800, 640), Array.Empty<PlanPrimitive>()) }),
            new ScannerOptions());
        context.Walls.AddRange(mainWalls.Concat(detailWalls));
        context.WallTopologyPreparation = new WallTopologyPreparation(
            context.Walls.Select(wall => wall.Id).ToArray(),
            Array.Empty<WallTopologyRejectedWall>(),
            context.Walls.Select(wall => wall.Id).ToArray(),
            Array.Empty<string>(),
            Array.Empty<string>());

        await new WallGraphStage().ExecuteAsync(context, CancellationToken.None);

        var stairComponent = Assert.Single(
            context.WallGraph.Components,
            component => detailWalls.All(wall => component.WallIds.Contains(wall.Id)));

        Assert.Equal(WallGraphComponentKind.ObjectLikeIsland, stairComponent.Kind);
        Assert.True(stairComponent.ExcludedFromStructuralTopology);
        Assert.Contains(stairComponent.Evidence, item => item.Contains("stair/detail", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            context.Diagnostics.Build().Messages,
            diagnostic => diagnostic.Code == "wall_graph.object_like_components.review"
                && diagnostic.Properties["objectLikeIslandCount"] == "1");
    }

    [Fact]
    public async Task JsonExporter_IncludesWallNodeTopologyFields()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-node-export",
                Wall("top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("right", new PlanPoint(320, 100), new PlanPoint(320, 260))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var node = parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("nodes")
            .EnumerateArray()
            .First(item => item.GetProperty("kind").GetString() == "Corner");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(2, node.GetProperty("degree").GetInt32());
        Assert.True(node.GetProperty("directions").GetArrayLength() >= 2);
        Assert.Contains("classified Corner", node.GetProperty("evidence").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task JsonExporter_IncludesWallGraphComponents()
    {
        var result = await new OpenPlanTraceScanner().ScanAsync(
            Document(
                "wall-component-export",
                Wall("room-top", new PlanPoint(100, 100), new PlanPoint(320, 100)),
                Wall("room-right", new PlanPoint(320, 100), new PlanPoint(320, 260)),
                Wall("room-bottom", new PlanPoint(320, 260), new PlanPoint(100, 260)),
                Wall("room-left", new PlanPoint(100, 260), new PlanPoint(100, 100)),
                Wall("symbol-top", new PlanPoint(420, 160), new PlanPoint(455, 160)),
                Wall("symbol-right", new PlanPoint(455, 160), new PlanPoint(455, 185)),
                Wall("symbol-bottom", new PlanPoint(455, 185), new PlanPoint(420, 185)),
                Wall("symbol-left", new PlanPoint(420, 185), new PlanPoint(420, 160))));

        var json = PlanTraceJsonExporter.Serialize(result);
        using var parsed = JsonDocument.Parse(json);
        var components = parsed.RootElement
            .GetProperty("wallGraph")
            .GetProperty("components")
            .EnumerateArray()
            .ToArray();
        var objectLike = Assert.Single(components, component => component.GetProperty("kind").GetString() == "ObjectLikeIsland");

        Assert.Equal(PlanTraceExport.CurrentSchemaVersion, parsed.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(4, objectLike.GetProperty("wallCount").GetInt32());
        Assert.Equal(4, objectLike.GetProperty("edgeCount").GetInt32());
        Assert.Contains("symbol-top", objectLike.GetProperty("sourcePrimitiveIds").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("A-WALL", objectLike.GetProperty("sourceLayers").EnumerateArray().Select(item => item.GetString()));
        Assert.True(objectLike.GetProperty("bounds").GetProperty("width").GetDouble() > 0);
    }

    private static PlanDocument Document(string id, params PlanPrimitive[] primitives) =>
        new(
            id,
            new[]
            {
                new PlanPage(1, new PlanSize(600, 400), primitives)
            });

    private static LinePrimitive Wall(string sourceId, PlanPoint start, PlanPoint end) =>
        new(new PlanLineSegment(start, end))
        {
            SourceId = sourceId,
            Layer = "A-WALL",
            Source = new PrimitiveSourceMetadata
            {
                SourceFormat = "test",
                SourceId = sourceId,
                EntityType = "LINE",
                Layer = "A-WALL",
                DrawingSpace = SourceDrawingSpace.Model
            }
        };

    private static WallEvidenceBand TrustedReviewBand(WallSegment wall) =>
        new(
            $"wall-evidence-band:{wall.Id}",
            wall.PageNumber,
            new PlanLineSegment(wall.CenterLine.Start.Translate(-4, 0), wall.CenterLine.End.Translate(-4, 0)),
            new PlanLineSegment(wall.CenterLine.Start.Translate(4, 0), wall.CenterLine.End.Translate(4, 0)),
            wall.CenterLine,
            8,
            1,
            new Confidence(0.82),
            wall.Id,
            wall.SourcePrimitiveIds,
            new[] { "parallel wall-face pair", "trusted medium wall body test evidence" });

    private static WallEvidenceWallAssessment Assessment(
        WallSegment wall,
        WallEvidenceDecision decision,
        WallEvidenceCategory category,
        Confidence confidence)
    {
        var evidence = category == WallEvidenceCategory.MediumWallBody && decision == WallEvidenceDecision.Review
            ? new[]
            {
                "parallel wall-face pair",
                "layer (unlayered) classified Dimension (0.24)",
                "wall type interior: supported wall evidence inside exterior envelope",
                "wall evidence: short unlayered parallel-face candidate is supported by only one distinct structural wall"
            }
            : new[] { "parallel wall-face pair", "trusted medium wall body test evidence" };

        return new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            category,
            confidence,
            decision == WallEvidenceDecision.Accept,
            decision != WallEvidenceDecision.Accept,
            decision == WallEvidenceDecision.Reject,
            wall.SourcePrimitiveIds,
            evidence)
        {
            Decision = decision
        };
    }

    private static WallEvidenceWallAssessment MediumPairedReviewAssessment(
        WallSegment wall,
        params string[] extraEvidence)
    {
        var evidence = new[]
        {
            "parallel wall-face pair",
            "face separation 6 drawing units",
            "pair score 0.85",
            "overlap ratio 1",
            "wall type interior: supported wall evidence inside exterior envelope",
            "wall evidence: short unlayered parallel-face candidate has clustered support but fewer than two distinct structural wall connections"
        }
            .Concat(extraEvidence)
            .ToArray();

        return new WallEvidenceWallAssessment(
            wall.Id,
            wall.PageNumber,
            wall.Bounds,
            WallEvidenceCategory.MediumWallBody,
            new Confidence(0.85),
            PlacementReady: false,
            RequiresReview: true,
            RejectedAsNoise: false,
            wall.SourcePrimitiveIds,
            evidence)
        {
            Decision = WallEvidenceDecision.Review,
            ScoreBreakdown = new WallEvidenceScoreBreakdown(
                0.52,
                0,
                0.52,
                0.32,
                0,
                0.20,
                0,
                0,
                0,
                new[] { "parallel-face wall pair", "both endpoints supported by structural context" },
                new[] { "not placement-ready without review" })
        };
    }

    private static WallSegment DetectedWall(string id, PlanPoint start, PlanPoint end) =>
        new(
            id,
            1,
            new PlanLineSegment(start, end),
            4,
            Confidence.High)
        {
            SourcePrimitiveIds = new[] { id },
            Evidence = new[] { "test detected wall" }
        };
}
