using System.Globalization;

namespace OpenPlanTrace;

internal sealed class OpeningDetectionStage : IPipelineStage
{
    public string Name => "openings";

    public ValueTask ExecuteAsync(ScanContext context, CancellationToken cancellationToken)
    {
        foreach (var page in context.Document.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var structuralWalls = WallTopologyFilter.StructuralWallsForPage(
                context,
                page.Number,
                out var excludedComponents,
                out var excludedEvidenceAssessments);
            WallTopologyFilter.AddStructuralTopologyExclusionDiagnostic(context, Name, page.Number, excludedComponents);
            WallTopologyFilter.AddRejectedWallEvidenceExclusionDiagnostic(context, Name, page.Number, excludedEvidenceAssessments);

            var axisWalls = structuralWalls
                .Select(wall => AxisWall.TryCreate(wall, context.Options.GeometryTolerance.Distance))
                .Where(axis => axis is not null)
                .Select(axis => axis!)
                .GroupBy(axis => new
                {
                    axis.Orientation,
                    CoordinateBucket = Math.Round(axis.Coordinate / Math.Max(0.1, context.Options.WallSnapTolerance))
                });

            foreach (var group in axisWalls)
            {
                foreach (var gap in EnumerateWallGaps(group, context.Options))
                {
                    var bounds = CreateOpeningBounds(gap.Previous, gap.Next, context.Options);
                    var centerLine = CreateOpeningCenterLine(gap.Previous, gap.Next);
                    var orientation = gap.Previous.Orientation == WallOrientation.Horizontal
                        ? OpeningOrientation.Horizontal
                        : OpeningOrientation.Vertical;
                    var classification = ClassifyOpening(page, context, bounds, centerLine, orientation);
                    var hostWallIds = new[] { gap.Previous.Wall.Id, gap.Next.Wall.Id };
                    var openingId = $"page:{page.Number}:opening:{context.Openings.Count + 1}";
                    var sourceRegionId = gap.Previous.Wall.SourceRegionId ?? gap.Next.Wall.SourceRegionId;
                    var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(page.Number, bounds, sourceRegionId);
                    var placement = CreateOpeningPlacement(
                        centerLine,
                        new[] { gap.Previous.Wall, gap.Next.Wall },
                        gap.Previous.Wall.Id,
                        context.Calibration,
                        scaleGroup,
                        classification.Confidence);

                    context.Openings.Add(
                        new OpeningCandidate(
                            openingId,
                            page.Number,
                            classification.Type,
                            bounds,
                            classification.Confidence)
                        {
                            WallId = gap.Previous.Wall.Id,
                            AdjacentWallIds = hostWallIds,
                            HostWallIds = hostWallIds,
                            CenterLine = centerLine,
                            Orientation = orientation,
                            Operation = classification.Operation,
                            HingeSide = classification.HingeSide,
                            SwingSide = classification.SwingSide,
                            SwingDirection = classification.SwingDirection,
                            HingePoint = classification.HingePoint,
                            Placement = placement,
                            SourcePrimitiveIds = classification.SourceIds,
                            Evidence = classification.Evidence,
                            WidthMillimeters = context.Calibration.ToMillimeters(gap.Width, scaleGroup),
                            MeasurementScaleGroupId = scaleGroup?.Id
                        });

                    if (classification.Confidence.Value < 0.6)
                    {
                        context.AddDiagnostic(
                            "openings.low_confidence_candidate",
                            DiagnosticSeverity.Info,
                            Name,
                            "A wall gap was classified with low confidence and should be reviewed.",
                            page.Number,
                            bounds,
                            classification.Confidence,
                            scope: DiagnosticScope.Detection,
                            sourcePrimitiveIds: classification.SourceIds,
                            properties: new Dictionary<string, string>
                            {
                                ["openingId"] = openingId,
                                ["openingType"] = classification.Type.ToString(),
                                ["operation"] = classification.Operation.ToString(),
                                ["gapDrawingWidth"] = Math.Round(centerLine.Length, 3).ToString(),
                                ["hostWallIds"] = string.Join(",", hostWallIds)
                            });
                    }
                }
            }

            AddSymbolDrivenOpenings(page, context, structuralWalls);
        }

        return ValueTask.CompletedTask;
    }

    private static IEnumerable<WallGapCandidate> EnumerateWallGaps(
        IEnumerable<AxisWall> walls,
        ScannerOptions options)
    {
        var ordered = walls
            .OrderBy(axis => axis.Start)
            .ThenByDescending(axis => axis.End)
            .ToArray();

        if (ordered.Length < 2)
        {
            yield break;
        }

        var tolerance = Math.Max(options.WallSnapTolerance, options.WallMergeTolerance);
        var frontier = ordered[0];

        for (var index = 1; index < ordered.Length; index++)
        {
            var current = ordered[index];
            if (current.Start <= frontier.End + tolerance)
            {
                if (current.End > frontier.End)
                {
                    frontier = current;
                }

                continue;
            }

            var gap = current.Start - frontier.End;
            if (gap >= options.MinOpeningGap && gap <= options.MaxOpeningGap)
            {
                yield return new WallGapCandidate(frontier, current, gap);
            }

            if (current.End > frontier.End)
            {
                frontier = current;
            }
        }
    }

    private static PlanRect CreateOpeningBounds(AxisWall previous, AxisWall next, ScannerOptions options)
    {
        var inflation = Math.Max(previous.Wall.Thickness, next.Wall.Thickness) + options.WallSnapTolerance;

        if (previous.Orientation == WallOrientation.Horizontal)
        {
            return PlanRect.FromEdges(
                    Math.Min(previous.End, next.Start),
                    Math.Min(previous.Coordinate, next.Coordinate),
                    Math.Max(previous.End, next.Start),
                    Math.Max(previous.Coordinate, next.Coordinate))
                .Inflate(0, inflation);
        }

        return PlanRect.FromEdges(
                Math.Min(previous.Coordinate, next.Coordinate),
                Math.Min(previous.End, next.Start),
                Math.Max(previous.Coordinate, next.Coordinate),
                Math.Max(previous.End, next.Start))
            .Inflate(inflation, 0);
    }

    private static PlanLineSegment CreateOpeningCenterLine(AxisWall previous, AxisWall next) =>
        previous.Orientation == WallOrientation.Horizontal
            ? new PlanLineSegment(new PlanPoint(previous.End, previous.Coordinate), new PlanPoint(next.Start, next.Coordinate))
            : new PlanLineSegment(new PlanPoint(previous.Coordinate, previous.End), new PlanPoint(next.Coordinate, next.Start));

    private static void AddSymbolDrivenOpenings(
        PlanPage page,
        ScanContext context,
        IReadOnlyList<WallSegment> structuralWalls)
    {
        var axisWalls = structuralWalls
            .Select(wall => AxisWall.TryCreate(wall, context.Options.GeometryTolerance.Distance))
            .Where(axis => axis is not null)
            .Select(axis => axis!)
            .ToArray();

        if (axisWalls.Length == 0)
        {
            return;
        }

        var consumedSourceIds = new HashSet<string>(
            context.Openings
                .Where(opening => opening.PageNumber == page.Number)
                .SelectMany(opening => opening.SourcePrimitiveIds),
            StringComparer.Ordinal);

        AddArcDrivenDoorOpenings(page, context, axisWalls, consumedSourceIds);

        var symbolLines = EnumerateShortLineSymbols(page, context).ToArray();
        if (symbolLines.Length < 2)
        {
            return;
        }

        var addedSymbolOpenings = new List<OpeningCandidate>();

        foreach (var wall in axisWalls)
        {
            var ticks = symbolLines
                .Where(line => IsPerpendicularOpeningTick(wall, line, context.Options))
                .Select(line => new ProjectedOpeningSymbol(
                    line,
                    wall.Wall.CenterLine.ProjectParameter(line.Line.Segment.Midpoint) * wall.Wall.CenterLine.Length))
                .Where(item => item.Along >= -context.Options.WallSnapTolerance
                    && item.Along <= wall.Wall.CenterLine.Length + context.Options.WallSnapTolerance)
                .OrderBy(item => item.Along)
                .ToArray();

            for (var index = 1; index < ticks.Length; index++)
            {
                var previous = ticks[index - 1];
                var next = ticks[index];
                var width = next.Along - previous.Along;
                if (width < context.Options.MinOpeningGap || width > context.Options.MaxOpeningGap)
                {
                    continue;
                }

                if (consumedSourceIds.Contains(previous.Symbol.SourceId)
                    || consumedSourceIds.Contains(next.Symbol.SourceId))
                {
                    continue;
                }

                if (!AreCompatibleOpeningTicks(wall, previous.Symbol, next.Symbol, context.Options))
                {
                    continue;
                }

                var centerLine = OpeningLineFromProjection(wall.Wall.CenterLine, previous.Along, next.Along);
                if (HasNearbyOpening(context.Openings, page.Number, centerLine, context.Options))
                {
                    continue;
                }

                var sourceIds = new[] { previous.Symbol.SourceId, next.Symbol.SourceId };
                var bounds = PlanRect
                    .Union(new[] { previous.Symbol.Line.Segment.Bounds, next.Symbol.Line.Segment.Bounds, centerLine.Bounds })
                    .Inflate(Math.Max(wall.Wall.Thickness, context.Options.WallSnapTolerance))
                    .ClampTo(page.Bounds);
                var orientation = wall.Orientation == WallOrientation.Horizontal
                    ? OpeningOrientation.Horizontal
                    : OpeningOrientation.Vertical;
                var openingId = $"page:{page.Number}:opening:{context.Openings.Count + 1}";
                var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(
                    page.Number,
                    bounds,
                    wall.Wall.SourceRegionId);
                var confidence = new Confidence(0.58);
                var placement = CreateOpeningPlacement(
                    centerLine,
                    new[] { wall.Wall },
                    wall.Wall.Id,
                    context.Calibration,
                    scaleGroup,
                    confidence);

                var opening = new OpeningCandidate(
                        openingId,
                        page.Number,
                        OpeningType.Window,
                        bounds,
                        confidence)
                    {
                        WallId = wall.Wall.Id,
                        AdjacentWallIds = new[] { wall.Wall.Id },
                        HostWallIds = new[] { wall.Wall.Id },
                        CenterLine = centerLine,
                        Orientation = orientation,
                        Operation = OpeningOperation.Fixed,
                        Placement = placement,
                        SourcePrimitiveIds = sourceIds,
                        Evidence = new[]
                        {
                            $"paired perpendicular opening ticks {Math.Round(width, 3)} drawing units apart",
                            $"host wall {wall.Wall.Id}",
                            $"orientation {orientation}"
                        },
                        WidthMillimeters = context.Calibration.ToMillimeters(width, scaleGroup),
                        MeasurementScaleGroupId = scaleGroup?.Id
                    };

                context.Openings.Add(opening);
                addedSymbolOpenings.Add(opening);

                consumedSourceIds.Add(previous.Symbol.SourceId);
                consumedSourceIds.Add(next.Symbol.SourceId);
                index++;
            }
        }

        if (addedSymbolOpenings.Count > 0)
        {
            context.AddDiagnostic(
                "openings.symbol_tick_candidates.detected",
                DiagnosticSeverity.Info,
                "openings",
                $"Detected {addedSymbolOpenings.Count} opening candidate(s) from paired short tick geometry.",
                page.Number,
                PlanRect.Union(addedSymbolOpenings.Select(opening => opening.Bounds)),
                Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: addedSymbolOpenings.SelectMany(opening => opening.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["candidateCount"] = addedSymbolOpenings.Count.ToString(),
                    ["openingType"] = OpeningType.Window.ToString(),
                    ["operation"] = OpeningOperation.Fixed.ToString()
                });
        }
    }

    private static void AddArcDrivenDoorOpenings(
        PlanPage page,
        ScanContext context,
        IReadOnlyList<AxisWall> axisWalls,
        HashSet<string> consumedSourceIds)
    {
        var arcs = EnumerateDoorSwingArcs(page, context).ToArray();
        if (arcs.Length == 0)
        {
            return;
        }

        var lineSymbols = EnumerateShortLineSymbols(page, context).ToArray();
        var detections = new List<ArcDoorDetection>();

        foreach (var arc in arcs)
        {
            if (consumedSourceIds.Contains(arc.SourceId))
            {
                continue;
            }

            var leafLines = FindNearbyDoorLeafLines(arc, lineSymbols, context.Options).ToArray();
            var sourceLooksLikeOpening = LooksLikeOpeningSource(arc.Arc);
            if (!sourceLooksLikeOpening && leafLines.Length == 0)
            {
                continue;
            }

            var candidate = axisWalls
                .Select(wall => ArcDoorCandidate.TryCreate(wall, arc, leafLines, sourceLooksLikeOpening, context.Options))
                .Where(item => item is not null)
                .Select(item => item!)
                .OrderByDescending(item => item.Score)
                .FirstOrDefault();

            if (candidate is null)
            {
                continue;
            }

            detections.Add(new ArcDoorDetection(arc, candidate, sourceLooksLikeOpening));
        }

        var added = new List<OpeningCandidate>();
        var consumedArcIds = new HashSet<string>(StringComparer.Ordinal);
        AddDoubleSwingArcOpenings(page, context, detections, consumedSourceIds, consumedArcIds, added);

        foreach (var detection in detections)
        {
            if (consumedArcIds.Contains(detection.Arc.SourceId)
                || consumedSourceIds.Contains(detection.Arc.SourceId)
                || HasNearbyOpening(context.Openings, page.Number, detection.Candidate.CenterLine, context.Options))
            {
                continue;
            }

            var opening = CreateSingleSwingArcOpening(page, context, detection);

            context.Openings.Add(opening);
            added.Add(opening);
            foreach (var sourceId in opening.SourcePrimitiveIds)
            {
                consumedSourceIds.Add(sourceId);
            }
        }

        if (added.Count > 0)
        {
            context.AddDiagnostic(
                "openings.arc_door_candidates.detected",
                DiagnosticSeverity.Info,
                "openings",
                $"Detected {added.Count} swing-arc door candidate(s).",
                page.Number,
                PlanRect.Union(added.Select(opening => opening.Bounds)),
                Confidence.Medium,
                scope: DiagnosticScope.Detection,
                sourcePrimitiveIds: added.SelectMany(opening => opening.SourcePrimitiveIds),
                properties: new Dictionary<string, string>
                {
                    ["candidateCount"] = added.Count.ToString(),
                    ["openingType"] = OpeningType.Door.ToString(),
                    ["operation"] = added.Select(opening => opening.Operation).Distinct().Count() == 1
                        ? added[0].Operation.ToString()
                        : "Mixed",
                    ["hingedCount"] = added.Count(opening => opening.Operation == OpeningOperation.Hinged).ToString(),
                    ["doubleSwingCount"] = added.Count(opening => opening.Operation == OpeningOperation.DoubleSwing).ToString()
                });
        }
    }

    private static void AddDoubleSwingArcOpenings(
        PlanPage page,
        ScanContext context,
        IReadOnlyList<ArcDoorDetection> detections,
        HashSet<string> consumedSourceIds,
        HashSet<string> consumedArcIds,
        List<OpeningCandidate> added)
    {
        for (var firstIndex = 0; firstIndex < detections.Count; firstIndex++)
        {
            var first = detections[firstIndex];
            if (consumedArcIds.Contains(first.Arc.SourceId) || consumedSourceIds.Contains(first.Arc.SourceId))
            {
                continue;
            }

            ArcDoorDetection? bestSecond = null;
            double bestScore = double.NegativeInfinity;
            for (var secondIndex = firstIndex + 1; secondIndex < detections.Count; secondIndex++)
            {
                var second = detections[secondIndex];
                if (consumedArcIds.Contains(second.Arc.SourceId) || consumedSourceIds.Contains(second.Arc.SourceId))
                {
                    continue;
                }

                if (!CanPairAsDoubleSwing(first, second, context.Options, out var score))
                {
                    continue;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestSecond = second;
                }
            }

            if (bestSecond is null)
            {
                continue;
            }

            var opening = CreateDoubleSwingArcOpening(page, context, first, bestSecond);
            if (HasNearbyOpening(context.Openings, page.Number, opening.CenterLine, context.Options))
            {
                continue;
            }

            context.Openings.Add(opening);
            added.Add(opening);
            foreach (var sourceId in opening.SourcePrimitiveIds)
            {
                consumedSourceIds.Add(sourceId);
            }

            consumedArcIds.Add(first.Arc.SourceId);
            consumedArcIds.Add(bestSecond.Arc.SourceId);
        }
    }

    private static bool CanPairAsDoubleSwing(
        ArcDoorDetection first,
        ArcDoorDetection second,
        ScannerOptions options,
        out double score)
    {
        score = 0;
        if (!string.Equals(first.Candidate.Wall.Wall.Id, second.Candidate.Wall.Wall.Id, StringComparison.Ordinal)
            || first.Candidate.Orientation != second.Candidate.Orientation)
        {
            return false;
        }

        var wallLine = first.Candidate.Wall.Wall.CenterLine;
        var wallLength = Math.Max(1, wallLine.Length);
        var firstHinge = wallLine.ProjectParameter(first.Candidate.HingePoint) * wallLength;
        var secondHinge = wallLine.ProjectParameter(second.Candidate.HingePoint) * wallLength;
        var width = Math.Abs(secondHinge - firstHinge);
        if (width < options.MinOpeningGap || width > options.MaxOpeningGap)
        {
            return false;
        }

        var midpoint = (firstHinge + secondHinge) / 2.0;
        var firstLeafEnd = wallLine.ProjectParameter(first.Candidate.CenterLine.End) * wallLength;
        var secondLeafEnd = wallLine.ProjectParameter(second.Candidate.CenterLine.End) * wallLength;
        var midpointTolerance = Math.Max(options.WallSnapTolerance * 4, options.MinOpeningGap * 0.45);
        if (Math.Abs(firstLeafEnd - midpoint) > midpointTolerance
            || Math.Abs(secondLeafEnd - midpoint) > midpointTolerance)
        {
            return false;
        }

        var radiusDelta = Math.Abs(first.Arc.Arc.Radius - second.Arc.Arc.Radius);
        if (radiusDelta > Math.Max(options.WallSnapTolerance * 2, Math.Min(first.Arc.Arc.Radius, second.Arc.Arc.Radius) * 0.25))
        {
            return false;
        }

        score = first.Candidate.Score
            + second.Candidate.Score
            + (first.Candidate.Swing.SwingSide == second.Candidate.Swing.SwingSide ? 0.08 : 0)
            - Math.Min(0.12, radiusDelta / Math.Max(1, Math.Max(first.Arc.Arc.Radius, second.Arc.Arc.Radius)));
        return true;
    }

    private static OpeningCandidate CreateSingleSwingArcOpening(
        PlanPage page,
        ScanContext context,
        ArcDoorDetection detection)
    {
        var candidate = detection.Candidate;
        var sourceIds = new[] { detection.Arc.SourceId }
            .Concat(candidate.LeafSourceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var bounds = PlanRect
            .Union(new[] { detection.Arc.Arc.Bounds, candidate.CenterLine.Bounds }
                .Concat(candidate.LeafBounds))
            .Inflate(Math.Max(candidate.Wall.Wall.Thickness, context.Options.WallSnapTolerance))
            .ClampTo(page.Bounds);
        var evidence = new List<string>
        {
            "door swing arc attached to host wall",
            $"arc radius {Math.Round(detection.Arc.Arc.Radius, 3)} drawing units",
            $"host wall {candidate.Wall.Wall.Id}",
            $"orientation {candidate.Orientation}"
        };

        if (detection.SourceLooksLikeOpening)
        {
            evidence.Add("door/opening layer or source hint");
        }

        if (candidate.LeafSourceIds.Count > 0)
        {
            evidence.Add($"{candidate.LeafSourceIds.Count} nearby door leaf line(s)");
        }

        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(
            page.Number,
            bounds,
            candidate.Wall.Wall.SourceRegionId);
        var placement = CreateOpeningPlacement(
            candidate.CenterLine,
            new[] { candidate.Wall.Wall },
            candidate.Wall.Wall.Id,
            context.Calibration,
            scaleGroup,
            candidate.Confidence);

        return new OpeningCandidate(
                $"page:{page.Number}:opening:{context.Openings.Count + 1}",
                page.Number,
                OpeningType.Door,
                bounds,
                candidate.Confidence)
            {
                WallId = candidate.Wall.Wall.Id,
                AdjacentWallIds = new[] { candidate.Wall.Wall.Id },
                HostWallIds = new[] { candidate.Wall.Wall.Id },
                CenterLine = candidate.CenterLine,
                Orientation = candidate.Orientation,
                Operation = OpeningOperation.Hinged,
                HingeSide = OpeningHingeSide.StartJamb,
                SwingSide = candidate.Swing.SwingSide,
                SwingDirection = candidate.Swing.SwingDirection,
                HingePoint = candidate.HingePoint,
                Placement = placement,
                SourcePrimitiveIds = sourceIds,
                Evidence = evidence,
                WidthMillimeters = context.Calibration.ToMillimeters(candidate.CenterLine.Length, scaleGroup),
                MeasurementScaleGroupId = scaleGroup?.Id
            };
    }

    private static OpeningCandidate CreateDoubleSwingArcOpening(
        PlanPage page,
        ScanContext context,
        ArcDoorDetection first,
        ArcDoorDetection second)
    {
        var wallLine = first.Candidate.Wall.Wall.CenterLine;
        var wallLength = Math.Max(1, wallLine.Length);
        var firstHinge = wallLine.ProjectParameter(first.Candidate.HingePoint) * wallLength;
        var secondHinge = wallLine.ProjectParameter(second.Candidate.HingePoint) * wallLength;
        var startAlong = Math.Min(firstHinge, secondHinge);
        var endAlong = Math.Max(firstHinge, secondHinge);
        var centerLine = OpeningLineFromProjection(wallLine, startAlong, endAlong);
        var sourceIds = new[] { first.Arc.SourceId, second.Arc.SourceId }
            .Concat(first.Candidate.LeafSourceIds)
            .Concat(second.Candidate.LeafSourceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var sourceBounds = new[]
            {
                first.Arc.Arc.Bounds,
                second.Arc.Arc.Bounds,
                centerLine.Bounds
            }
            .Concat(first.Candidate.LeafBounds)
            .Concat(second.Candidate.LeafBounds);
        var bounds = PlanRect
            .Union(sourceBounds)
            .Inflate(Math.Max(first.Candidate.Wall.Wall.Thickness, context.Options.WallSnapTolerance))
            .ClampTo(page.Bounds);
        var swingSide = first.Candidate.Swing.SwingSide == second.Candidate.Swing.SwingSide
            ? first.Candidate.Swing.SwingSide
            : OpeningSwingSide.Unknown;
        var confidence = new Confidence(Math.Clamp(
            ((first.Candidate.Confidence.Value + second.Candidate.Confidence.Value) / 2.0) + 0.08,
            0.62,
            0.88));
        var evidence = new List<string>
        {
            "paired door swing arcs formed one double-swing opening",
            $"arc radii {Math.Round(first.Arc.Arc.Radius, 3)} and {Math.Round(second.Arc.Arc.Radius, 3)} drawing units",
            $"host wall {first.Candidate.Wall.Wall.Id}",
            $"opening width {Math.Round(centerLine.Length, 3)} drawing units",
            $"orientation {first.Candidate.Orientation}"
        };

        if (first.SourceLooksLikeOpening || second.SourceLooksLikeOpening)
        {
            evidence.Add("door/opening layer or source hint");
        }

        var leafCount = first.Candidate.LeafSourceIds
            .Concat(second.Candidate.LeafSourceIds)
            .Distinct(StringComparer.Ordinal)
            .Count();
        if (leafCount > 0)
        {
            evidence.Add($"{leafCount} nearby door leaf line(s)");
        }

        var scaleGroup = context.Calibration.SelectMeasurementScaleGroup(
            page.Number,
            bounds,
            first.Candidate.Wall.Wall.SourceRegionId);
        var placement = CreateOpeningPlacement(
            centerLine,
            new[] { first.Candidate.Wall.Wall },
            first.Candidate.Wall.Wall.Id,
            context.Calibration,
            scaleGroup,
            confidence);

        return new OpeningCandidate(
                $"page:{page.Number}:opening:{context.Openings.Count + 1}",
                page.Number,
                OpeningType.Door,
                bounds,
                confidence)
            {
                WallId = first.Candidate.Wall.Wall.Id,
                AdjacentWallIds = new[] { first.Candidate.Wall.Wall.Id },
                HostWallIds = new[] { first.Candidate.Wall.Wall.Id },
                CenterLine = centerLine,
                Orientation = first.Candidate.Orientation,
                Operation = OpeningOperation.DoubleSwing,
                HingeSide = OpeningHingeSide.Unknown,
                SwingSide = swingSide,
                SwingDirection = OpeningSwingDirection.Unknown,
                HingePoint = null,
                Placement = placement,
                SourcePrimitiveIds = sourceIds,
                Evidence = evidence,
                WidthMillimeters = context.Calibration.ToMillimeters(centerLine.Length, scaleGroup),
                MeasurementScaleGroupId = scaleGroup?.Id
            };
    }

    private static OpeningPlacement CreateOpeningPlacement(
        PlanLineSegment openingLine,
        IReadOnlyList<WallSegment> hostWalls,
        string? primaryHostWallId,
        PlanCalibration calibration,
        CalibrationScaleGroup? scaleGroup,
        Confidence openingConfidence)
    {
        var referenceLine = CreatePlacementReferenceLine(openingLine, hostWalls);
        var referenceLength = referenceLine.Length;
        if (referenceLength <= double.Epsilon)
        {
            referenceLine = openingLine;
            referenceLength = Math.Max(1, openingLine.Length);
        }

        var startParameter = referenceLine.ProjectParameter(openingLine.Start);
        var endParameter = referenceLine.ProjectParameter(openingLine.End);
        var centerParameter = referenceLine.ProjectParameter(openingLine.Midpoint);
        var startOffset = startParameter * referenceLength;
        var endOffset = endParameter * referenceLength;
        var centerOffset = centerParameter * referenceLength;
        var projectedStart = referenceLine.PointAt(startParameter);
        var projectedEnd = referenceLine.PointAt(endParameter);
        var length = Math.Abs(endOffset - startOffset);
        var crossOffset = Math.Max(
            referenceLine.DistanceToPoint(openingLine.Start),
            referenceLine.DistanceToPoint(openingLine.End));
        var along = referenceLine.Vector.Normalize();
        var normal = new PlanVector(-along.Y, along.X).Normalize();
        var depth = PlacementDepth(hostWalls, openingLine);
        var halfDepth = depth / 2.0;
        var footprintCorners = new[]
        {
            projectedStart + (normal * -halfDepth),
            projectedEnd + (normal * -halfDepth),
            projectedEnd + (normal * halfDepth),
            projectedStart + (normal * halfDepth)
        };
        var footprintBounds = BoundsForPoints(footprintCorners);
        var startJambLine = new PlanLineSegment(footprintCorners[0], footprintCorners[3]);
        var endJambLine = new PlanLineSegment(footprintCorners[1], footprintCorners[2]);
        var crossPenalty = Math.Min(0.25, crossOffset / Math.Max(1, openingLine.Length) * 0.25);
        var placementConfidence = new Confidence(Math.Clamp(
            Math.Min(0.95, openingConfidence.Value + 0.08)
            - crossPenalty
            + (hostWalls.Count == 1 ? 0.03 : 0),
            0.35,
            0.98));
        var anchorWallIds = hostWalls
            .Select(wall => wall.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var evidence = new List<string>
        {
            anchorWallIds.Length == 0
                ? "placement reference line fell back to opening centerline"
                : $"placement reference line derived from {anchorWallIds.Length} host wall segment(s)",
            $"opening projected to reference offsets {Format(startOffset)} -> {Format(endOffset)} drawing units",
            $"opening footprint uses {Format(depth)} drawing unit wall-normal depth",
            $"placement cross-wall offset {Format(crossOffset)} drawing units"
        };

        if (scaleGroup?.MillimetersPerDrawingUnit is > 0)
        {
            evidence.Add($"placement offsets calibrated with scale group {scaleGroup.Id}");
        }
        else if (calibration.MillimetersPerDrawingUnit is > 0 && !calibration.ScaleGroups.Any(group => group.MillimetersPerDrawingUnit is > 0))
        {
            evidence.Add("placement offsets calibrated with document scale");
        }
        else
        {
            evidence.Add("placement millimeter offsets unavailable without unambiguous calibration");
        }

        return new OpeningPlacement(
            primaryHostWallId,
            anchorWallIds,
            referenceLine,
            projectedStart,
            projectedEnd,
            startOffset,
            endOffset,
            centerOffset,
            length,
            footprintBounds,
            footprintCorners,
            startJambLine,
            endJambLine,
            depth,
            calibration.ToMillimeters(depth, scaleGroup),
            calibration.ToMillimeters(startOffset, scaleGroup),
            calibration.ToMillimeters(endOffset, scaleGroup),
            calibration.ToMillimeters(centerOffset, scaleGroup),
            calibration.ToMillimeters(length, scaleGroup),
            startParameter,
            endParameter,
            centerParameter,
            along,
            normal,
            crossOffset,
            placementConfidence,
            evidence);
    }

    private static double PlacementDepth(
        IReadOnlyList<WallSegment> hostWalls,
        PlanLineSegment openingLine)
    {
        var hostDepth = hostWalls
            .Select(wall => wall.Thickness)
            .Where(thickness => thickness > 0)
            .DefaultIfEmpty(0)
            .Max();
        if (hostDepth > 0)
        {
            return hostDepth;
        }

        return Math.Max(1, openingLine.Length * 0.08);
    }

    private static PlanRect BoundsForPoints(IReadOnlyList<PlanPoint> points)
    {
        if (points.Count == 0)
        {
            return PlanRect.Empty;
        }

        return PlanRect.FromEdges(
            points.Min(point => point.X),
            points.Min(point => point.Y),
            points.Max(point => point.X),
            points.Max(point => point.Y));
    }

    private static PlanLineSegment CreatePlacementReferenceLine(
        PlanLineSegment openingLine,
        IReadOnlyList<WallSegment> hostWalls)
    {
        var firstWall = hostWalls.FirstOrDefault();
        if (firstWall is null)
        {
            return openingLine;
        }

        var baseLine = firstWall.CenterLine;
        if (baseLine.Length <= double.Epsilon || hostWalls.Count == 1)
        {
            return baseLine.Length <= double.Epsilon ? openingLine : baseLine;
        }

        var parameters = hostWalls
            .SelectMany(wall => new[] { wall.CenterLine.Start, wall.CenterLine.End })
            .Select(baseLine.ProjectParameter)
            .ToArray();
        if (parameters.Length == 0)
        {
            return baseLine;
        }

        var min = parameters.Min();
        var max = parameters.Max();
        return Math.Abs(max - min) <= double.Epsilon
            ? baseLine
            : new PlanLineSegment(baseLine.PointAt(min), baseLine.PointAt(max));
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static IEnumerable<OpeningSymbolArc> EnumerateDoorSwingArcs(PlanPage page, ScanContext context)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            var sourceId = context.PrimitiveId(page.Number, index, primitive);
            ArcPrimitive? arc = primitive switch
            {
                ArcPrimitive directArc => directArc,
                PolylinePrimitive polyline when DoorSwingArcRecovery.TryRecoverFromPolyline(
                    polyline,
                    context.Options,
                    DoorSwingArcRecoveryProfile.OpeningDetection,
                    out var recoveredArc) => recoveredArc,
                _ => null
            };

            if (arc is null)
            {
                continue;
            }

            if (arc.Radius < context.Options.MinOpeningGap * 0.75
                || arc.Radius > context.Options.MaxOpeningGap * 1.25
                || Math.Abs(arc.SweepAngleRadians) < Math.PI / 6
                || Math.Abs(arc.SweepAngleRadians) > Math.PI * 1.25)
            {
                continue;
            }

            yield return new OpeningSymbolArc(arc, sourceId);
        }
    }

    private static IEnumerable<OpeningSymbolLine> FindNearbyDoorLeafLines(
        OpeningSymbolArc arc,
        IReadOnlyList<OpeningSymbolLine> lineSymbols,
        ScannerOptions options)
    {
        var search = arc.Arc.Bounds.Inflate(Math.Max(options.WallSnapTolerance * 2, options.MinOpeningGap * 0.5));
        var hingeTolerance = Math.Max(4, options.WallSnapTolerance * 2);

        foreach (var line in lineSymbols)
        {
            if (!line.Line.Segment.Bounds.Intersects(search))
            {
                continue;
            }

            var touchesHinge = line.Line.Segment.Start.DistanceTo(arc.Arc.Center) <= hingeTolerance
                || line.Line.Segment.End.DistanceTo(arc.Arc.Center) <= hingeTolerance;
            if (touchesHinge || LooksLikeOpeningSource(line.Line))
            {
                yield return line;
            }
        }
    }

    private static IEnumerable<OpeningSymbolLine> EnumerateShortLineSymbols(PlanPage page, ScanContext context)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            if (page.Primitives[index] is not LinePrimitive line)
            {
                continue;
            }

            if (line.Segment.Length < context.Options.MinOpeningGap * 0.4
                || line.Segment.Length > context.Options.MaxOpeningGap * 1.5)
            {
                continue;
            }

            yield return new OpeningSymbolLine(line, context.PrimitiveId(page.Number, index, line));
        }
    }

    private static bool IsPerpendicularOpeningTick(
        AxisWall wall,
        OpeningSymbolLine symbol,
        ScannerOptions options)
    {
        var tolerance = options.GeometryTolerance.Distance;
        var perpendicular = wall.Orientation == WallOrientation.Horizontal
            ? symbol.Line.Segment.IsVertical(tolerance)
            : symbol.Line.Segment.IsHorizontal(tolerance);

        if (!perpendicular)
        {
            return false;
        }

        var distance = wall.Wall.CenterLine.DistanceToPoint(symbol.Line.Segment.Midpoint);
        var maxDistance = Math.Max(4, (wall.Wall.Thickness / 2.0) + options.WallSnapTolerance + 1);
        if (distance > maxDistance)
        {
            return false;
        }

        var maxTickLength = Math.Max(
            options.MinOpeningGap,
            (wall.Wall.Thickness * 4.0) + (options.WallSnapTolerance * 2.0));
        if (symbol.Line.Segment.Length > maxTickLength)
        {
            return false;
        }

        return symbol.Line.Segment.Bounds.Intersects(wall.Wall.Bounds.Inflate(options.WallSnapTolerance));
    }

    private static bool AreCompatibleOpeningTicks(
        AxisWall wall,
        OpeningSymbolLine first,
        OpeningSymbolLine second,
        ScannerOptions options)
    {
        var shorter = Math.Min(first.Line.Segment.Length, second.Line.Segment.Length);
        var longer = Math.Max(first.Line.Segment.Length, second.Line.Segment.Length);
        if (shorter / Math.Max(1, longer) < 0.55)
        {
            return false;
        }

        var firstDistance = wall.Wall.CenterLine.DistanceToPoint(first.Line.Segment.Midpoint);
        var secondDistance = wall.Wall.CenterLine.DistanceToPoint(second.Line.Segment.Midpoint);
        if (Math.Abs(firstDistance - secondDistance) > Math.Max(2, options.WallSnapTolerance))
        {
            return false;
        }

        return first.Line.Segment.Bounds.Intersects(wall.Wall.Bounds.Inflate(options.WallSnapTolerance))
            && second.Line.Segment.Bounds.Intersects(wall.Wall.Bounds.Inflate(options.WallSnapTolerance));
    }

    private static PlanLineSegment OpeningLineFromProjection(PlanLineSegment wallLine, double startDistance, double endDistance)
    {
        var length = Math.Max(1, wallLine.Length);
        return new PlanLineSegment(
            wallLine.PointAt(Math.Clamp(startDistance / length, 0, 1)),
            wallLine.PointAt(Math.Clamp(endDistance / length, 0, 1)));
    }

    private static bool HasNearbyOpening(
        IEnumerable<OpeningCandidate> openings,
        int pageNumber,
        PlanLineSegment centerLine,
        ScannerOptions options)
    {
        var tolerance = Math.Max(options.WallSnapTolerance * 2, options.MinOpeningGap * 0.5);
        return openings
            .Where(opening => opening.PageNumber == pageNumber)
            .Any(opening => opening.CenterLine.Midpoint.DistanceTo(centerLine.Midpoint) <= tolerance
                || opening.Bounds.Intersects(centerLine.Bounds.Inflate(tolerance)));
    }

    private static OpeningClassification ClassifyOpening(
        PlanPage page,
        ScanContext context,
        PlanRect bounds,
        PlanLineSegment centerLine,
        OpeningOrientation orientation)
    {
        var search = bounds.Inflate(context.Options.MaxOpeningGap * 0.25);
        var nearby = NearbyOpeningPrimitives(page, context, search, centerLine, orientation).ToArray();
        var arcs = nearby.Where(item => item.Primitive is ArcPrimitive).ToArray();
        var lines = nearby.Where(item => item.Primitive is LinePrimitive).ToArray();
        var sourceIds = nearby.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).ToArray();
        var evidence = new List<string>
        {
            $"gap width {Math.Round(centerLine.Length, 3)} drawing units",
            $"orientation {orientation}"
        };

        if (arcs.Length > 0)
        {
            evidence.Add(arcs.Length == 1 ? "door swing arc" : $"{arcs.Length} door swing arcs");
            var swing = InferSwing((ArcPrimitive)arcs[0].Primitive, centerLine, orientation);
            return new OpeningClassification(
                OpeningType.Door,
                arcs.Length > 1 ? OpeningOperation.DoubleSwing : OpeningOperation.Hinged,
                new Confidence(arcs.Length > 1 ? 0.83 : 0.8),
                sourceIds,
                evidence,
                swing.HingeSide,
                swing.SwingSide,
                swing.SwingDirection,
                swing.HingePoint);
        }

        var layerHint = ResolveLayerHint(nearby);
        var parallelLeafLines = lines
            .Where(item => IsLineParallelToOpening((LinePrimitive)item.Primitive, orientation, context.Options.GeometryTolerance.Distance))
            .ToArray();

        if (layerHint == OpeningLayerHint.PocketDoor && parallelLeafLines.Length > 0)
        {
            evidence.Add("pocket door hint with parallel pocket/track line");
            return new OpeningClassification(
                OpeningType.Door,
                OpeningOperation.PocketSliding,
                new Confidence(0.73),
                sourceIds,
                evidence);
        }

        if (layerHint == OpeningLayerHint.Door && parallelLeafLines.Length > 0)
        {
            evidence.Add("door layer hint with parallel leaf/track line");
            return new OpeningClassification(
                OpeningType.Door,
                OpeningOperation.Sliding,
                new Confidence(0.68),
                sourceIds,
                evidence);
        }

        if (layerHint == OpeningLayerHint.Window || lines.Length >= 2)
        {
            evidence.Add(layerHint == OpeningLayerHint.Window ? "window layer hint" : "multiple short opening lines");
            return new OpeningClassification(
                OpeningType.Window,
                OpeningOperation.Fixed,
                new Confidence(layerHint == OpeningLayerHint.Window ? 0.68 : 0.62),
                sourceIds,
                evidence);
        }

        evidence.Add("unclassified wall gap");
        return new OpeningClassification(
            OpeningType.GenericOpening,
            OpeningOperation.PassThrough,
            new Confidence(0.5),
            sourceIds,
            evidence);
    }

    private static IEnumerable<NearbyPrimitive> NearbyOpeningPrimitives(
        PlanPage page,
        ScanContext context,
        PlanRect search,
        PlanLineSegment centerLine,
        OpeningOrientation orientation)
    {
        for (var index = 0; index < page.Primitives.Count; index++)
        {
            var primitive = page.Primitives[index];
            if (!primitive.Bounds.Intersects(search))
            {
                continue;
            }

            switch (primitive)
            {
                case ArcPrimitive:
                    yield return new NearbyPrimitive(primitive, context.PrimitiveId(page.Number, index, primitive));
                    break;

                case LinePrimitive line
                    when line.Segment.Length <= context.Options.MaxOpeningGap * 1.5
                        && line.Segment.Length >= context.Options.MinOpeningGap * 0.4
                        && centerLine.DistanceToPoint(line.Segment.Midpoint) <= context.Options.MaxOpeningGap:
                    yield return new NearbyPrimitive(primitive, context.PrimitiveId(page.Number, index, primitive));
                    break;

                case PolylinePrimitive polyline
                    when DoorSwingArcRecovery.TryRecoverFromPolyline(
                            polyline,
                            context.Options,
                            DoorSwingArcRecoveryProfile.OpeningDetection,
                            out var recoveredArc)
                        && LooksLikeOpeningSource(polyline)
                        && centerLine.DistanceToPoint(recoveredArc.Center) <= context.Options.MaxOpeningGap:
                    yield return new NearbyPrimitive(recoveredArc, context.PrimitiveId(page.Number, index, primitive));
                    break;

                case SymbolPrimitive symbol when LooksLikeOpeningHint(symbol.Name):
                    yield return new NearbyPrimitive(primitive, context.PrimitiveId(page.Number, index, primitive));
                    break;

                default:
                    if (LooksLikeOpeningSource(primitive))
                    {
                        yield return new NearbyPrimitive(primitive, context.PrimitiveId(page.Number, index, primitive));
                    }

                    break;
            }
        }
    }

    private static OpeningSwingInfo InferSwing(
        ArcPrimitive arc,
        PlanLineSegment centerLine,
        OpeningOrientation orientation)
    {
        var startDistance = arc.Center.DistanceTo(centerLine.Start);
        var endDistance = arc.Center.DistanceTo(centerLine.End);
        var hingeSide = startDistance <= endDistance ? OpeningHingeSide.StartJamb : OpeningHingeSide.EndJamb;
        var hingePoint = hingeSide == OpeningHingeSide.StartJamb ? centerLine.Start : centerLine.End;

        var midAngle = arc.StartAngleRadians + (arc.SweepAngleRadians / 2.0);
        var swingPoint = new PlanPoint(
            arc.Center.X + (Math.Cos(midAngle) * arc.Radius),
            arc.Center.Y + (Math.Sin(midAngle) * arc.Radius));

        var swingSide = orientation == OpeningOrientation.Horizontal
            ? swingPoint.Y >= centerLine.Start.Y ? OpeningSwingSide.PositiveCoordinateSide : OpeningSwingSide.NegativeCoordinateSide
            : swingPoint.X >= centerLine.Start.X ? OpeningSwingSide.PositiveCoordinateSide : OpeningSwingSide.NegativeCoordinateSide;

        var swingDirection = arc.SweepAngleRadians < 0
            ? OpeningSwingDirection.Clockwise
            : OpeningSwingDirection.CounterClockwise;

        return new OpeningSwingInfo(hingeSide, swingSide, swingDirection, hingePoint);
    }

    private static bool IsLineParallelToOpening(
        LinePrimitive line,
        OpeningOrientation orientation,
        double tolerance) =>
        orientation switch
        {
            OpeningOrientation.Horizontal => line.Segment.IsHorizontal(tolerance),
            OpeningOrientation.Vertical => line.Segment.IsVertical(tolerance),
            _ => false
        };

    private static OpeningLayerHint ResolveLayerHint(IEnumerable<NearbyPrimitive> nearby)
    {
        var hint = OpeningLayerHint.None;
        foreach (var item in nearby)
        {
            var text = string.Join(
                " ",
                item.Primitive.Layer,
                item.Primitive.Source.Layer,
                item.Primitive.Source.BlockName,
                item.Primitive.Source.EntityType);

            if (text.Contains("pocket", StringComparison.OrdinalIgnoreCase))
            {
                return OpeningLayerHint.PocketDoor;
            }

            if (text.Contains("window", StringComparison.OrdinalIgnoreCase)
                || text.Contains("glass", StringComparison.OrdinalIgnoreCase))
            {
                hint = OpeningLayerHint.Window;
            }

            if (text.Contains("door", StringComparison.OrdinalIgnoreCase)
                || text.Contains("dor", StringComparison.OrdinalIgnoreCase)
                || text.Contains("slid", StringComparison.OrdinalIgnoreCase)
                || text.Contains("pocket", StringComparison.OrdinalIgnoreCase))
            {
                return OpeningLayerHint.Door;
            }
        }

        return hint;
    }

    private static bool LooksLikeOpeningSource(PlanPrimitive primitive)
    {
        var text = string.Join(
            " ",
            primitive.Layer,
            primitive.Source.Layer,
            primitive.Source.BlockName,
            primitive.Source.EntityType);

        return LooksLikeOpeningHint(text);
    }

    private static bool LooksLikeOpeningHint(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && (text.Contains("door", StringComparison.OrdinalIgnoreCase)
            || text.Contains("dor", StringComparison.OrdinalIgnoreCase)
            || text.Contains("window", StringComparison.OrdinalIgnoreCase)
            || text.Contains("glass", StringComparison.OrdinalIgnoreCase)
            || text.Contains("slid", StringComparison.OrdinalIgnoreCase)
            || text.Contains("pocket", StringComparison.OrdinalIgnoreCase));

    private enum WallOrientation
    {
        Horizontal,
        Vertical
    }

    private enum OpeningLayerHint
    {
        None,
        Door,
        PocketDoor,
        Window
    }

    private sealed record OpeningClassification(
        OpeningType Type,
        OpeningOperation Operation,
        Confidence Confidence,
        IReadOnlyList<string> SourceIds,
        IReadOnlyList<string> Evidence,
        OpeningHingeSide HingeSide = OpeningHingeSide.Unknown,
        OpeningSwingSide SwingSide = OpeningSwingSide.Unknown,
        OpeningSwingDirection SwingDirection = OpeningSwingDirection.Unknown,
        PlanPoint? HingePoint = null);

    private sealed record OpeningSwingInfo(
        OpeningHingeSide HingeSide,
        OpeningSwingSide SwingSide,
        OpeningSwingDirection SwingDirection,
        PlanPoint HingePoint);

    private sealed record NearbyPrimitive(PlanPrimitive Primitive, string SourceId);

    private sealed record WallGapCandidate(AxisWall Previous, AxisWall Next, double Width);

    private sealed record OpeningSymbolLine(LinePrimitive Line, string SourceId);

    private sealed record OpeningSymbolArc(ArcPrimitive Arc, string SourceId);

    private sealed record ArcDoorDetection(
        OpeningSymbolArc Arc,
        ArcDoorCandidate Candidate,
        bool SourceLooksLikeOpening);

    private sealed record ProjectedOpeningSymbol(OpeningSymbolLine Symbol, double Along);

    private sealed record ArcDoorCandidate(
        AxisWall Wall,
        PlanLineSegment CenterLine,
        OpeningOrientation Orientation,
        PlanPoint HingePoint,
        OpeningSwingInfo Swing,
        Confidence Confidence,
        double Score,
        IReadOnlyList<string> LeafSourceIds,
        IReadOnlyList<PlanRect> LeafBounds)
    {
        public static ArcDoorCandidate? TryCreate(
            AxisWall wall,
            OpeningSymbolArc arc,
            IReadOnlyList<OpeningSymbolLine> leafLines,
            bool sourceLooksLikeOpening,
            ScannerOptions options)
        {
            var wallLength = wall.Wall.CenterLine.Length;
            if (wallLength <= double.Epsilon)
            {
                return null;
            }

            var wallDistance = wall.Wall.CenterLine.DistanceToPoint(arc.Arc.Center);
            var maxWallDistance = Math.Max(5, (wall.Wall.Thickness / 2.0) + options.WallSnapTolerance + 2);
            if (wallDistance > maxWallDistance)
            {
                return null;
            }

            var hingeParameter = wall.Wall.CenterLine.ProjectParameter(arc.Arc.Center);
            var hingeAlong = hingeParameter * wallLength;
            if (hingeAlong < -options.WallSnapTolerance || hingeAlong > wallLength + options.WallSnapTolerance)
            {
                return null;
            }

            hingeAlong = Math.Clamp(hingeAlong, 0, wallLength);
            var direction = InferDoorDirection(wall, arc.Arc, leafLines, hingeAlong, wallLength, options);
            if (direction is null)
            {
                return null;
            }

            var width = Math.Clamp(arc.Arc.Radius, options.MinOpeningGap, options.MaxOpeningGap);
            var endAlong = hingeAlong + (direction.Value * width);
            if (endAlong < 0 || endAlong > wallLength)
            {
                var opposite = hingeAlong - (direction.Value * width);
                if (opposite >= 0 && opposite <= wallLength)
                {
                    endAlong = opposite;
                }
                else
                {
                    endAlong = Math.Clamp(endAlong, 0, wallLength);
                }
            }

            if (Math.Abs(endAlong - hingeAlong) < options.MinOpeningGap * 0.75)
            {
                return null;
            }

            var centerLine = OpeningLineFromProjection(wall.Wall.CenterLine, hingeAlong, endAlong);
            var orientation = wall.Orientation == WallOrientation.Horizontal
                ? OpeningOrientation.Horizontal
                : OpeningOrientation.Vertical;
            var swing = InferSwing(arc.Arc, centerLine, orientation);
            var matchingLeafLines = leafLines
                .Where(line => IsDoorLeafEvidence(wall, arc.Arc, line, hingeAlong, options))
                .DistinctBy(line => line.SourceId)
                .ToArray();
            var confidence = new Confidence(Math.Clamp(
                0.58
                + (sourceLooksLikeOpening ? 0.08 : 0)
                + (matchingLeafLines.Length > 0 ? 0.08 : 0)
                + Math.Min(0.08, (1.0 - (wallDistance / Math.Max(1, maxWallDistance))) * 0.08),
                0.54,
                0.78));
            var score = confidence.Value
                + Math.Min(0.12, matchingLeafLines.Length * 0.04)
                - Math.Min(0.12, wallDistance / Math.Max(1, maxWallDistance) * 0.08);

            return new ArcDoorCandidate(
                wall,
                centerLine,
                orientation,
                centerLine.Start,
                swing,
                confidence,
                score,
                matchingLeafLines.Select(line => line.SourceId).ToArray(),
                matchingLeafLines.Select(line => line.Line.Bounds).ToArray());
        }

        private static int? InferDoorDirection(
            AxisWall wall,
            ArcPrimitive arc,
            IReadOnlyList<OpeningSymbolLine> leafLines,
            double hingeAlong,
            double wallLength,
            ScannerOptions options)
        {
            var leafDirection = StrongestProjectionDirection(
                leafLines.SelectMany(line => new[] { line.Line.Segment.Start, line.Line.Segment.End }),
                wall,
                hingeAlong,
                options.MinOpeningGap * 0.35);
            if (leafDirection is not null)
            {
                return leafDirection;
            }

            var arcDirection = StrongestProjectionDirection(
                new[]
                {
                    ArcPoint(arc, arc.StartAngleRadians),
                    ArcPoint(arc, arc.StartAngleRadians + arc.SweepAngleRadians)
                },
                wall,
                hingeAlong,
                options.MinOpeningGap * 0.35);
            if (arcDirection is not null)
            {
                return arcDirection;
            }

            if (hingeAlong + arc.Radius <= wallLength + options.WallSnapTolerance)
            {
                return 1;
            }

            if (hingeAlong - arc.Radius >= -options.WallSnapTolerance)
            {
                return -1;
            }

            return null;
        }

        private static int? StrongestProjectionDirection(
            IEnumerable<PlanPoint> points,
            AxisWall wall,
            double hingeAlong,
            double minimumDelta)
        {
            var wallLength = Math.Max(1, wall.Wall.CenterLine.Length);
            var bestDelta = 0.0;
            foreach (var point in points)
            {
                var projected = wall.Wall.CenterLine.ProjectParameter(point) * wallLength;
                var delta = projected - hingeAlong;
                if (Math.Abs(delta) > Math.Abs(bestDelta))
                {
                    bestDelta = delta;
                }
            }

            return Math.Abs(bestDelta) >= minimumDelta
                ? Math.Sign(bestDelta)
                : null;
        }

        private static bool IsDoorLeafEvidence(
            AxisWall wall,
            ArcPrimitive arc,
            OpeningSymbolLine line,
            double hingeAlong,
            ScannerOptions options)
        {
            var hingeTolerance = Math.Max(4, options.WallSnapTolerance * 2);
            var touchesHinge = line.Line.Segment.Start.DistanceTo(arc.Center) <= hingeTolerance
                || line.Line.Segment.End.DistanceTo(arc.Center) <= hingeTolerance;
            if (touchesHinge)
            {
                return true;
            }

            if (!LooksLikeOpeningSource(line.Line))
            {
                return false;
            }

            var wallLength = Math.Max(1, wall.Wall.CenterLine.Length);
            var delta = Math.Abs((wall.Wall.CenterLine.ProjectParameter(line.Line.Segment.Midpoint) * wallLength) - hingeAlong);
            return delta <= Math.Max(options.MaxOpeningGap, arc.Radius * 1.2);
        }

        private static PlanPoint ArcPoint(ArcPrimitive arc, double angle) =>
            new(
                arc.Center.X + (Math.Cos(angle) * arc.Radius),
                arc.Center.Y + (Math.Sin(angle) * arc.Radius));
    }

    private sealed record AxisWall(
        WallSegment Wall,
        WallOrientation Orientation,
        double Coordinate,
        double Start,
        double End)
    {
        public static AxisWall? TryCreate(WallSegment wall, double tolerance)
        {
            if (wall.CenterLine.IsHorizontal(tolerance))
            {
                return new AxisWall(
                    wall,
                    WallOrientation.Horizontal,
                    (wall.CenterLine.Start.Y + wall.CenterLine.End.Y) / 2.0,
                    Math.Min(wall.CenterLine.Start.X, wall.CenterLine.End.X),
                    Math.Max(wall.CenterLine.Start.X, wall.CenterLine.End.X));
            }

            if (wall.CenterLine.IsVertical(tolerance))
            {
                return new AxisWall(
                    wall,
                    WallOrientation.Vertical,
                    (wall.CenterLine.Start.X + wall.CenterLine.End.X) / 2.0,
                    Math.Min(wall.CenterLine.Start.Y, wall.CenterLine.End.Y),
                    Math.Max(wall.CenterLine.Start.Y, wall.CenterLine.End.Y));
            }

            return null;
        }
    }
}
