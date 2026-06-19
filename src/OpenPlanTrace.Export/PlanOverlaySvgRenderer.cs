using System.Globalization;
using System.Net;
using System.Text;

namespace OpenPlanTrace.Export;

public static class PlanOverlaySvgRenderer
{
    private const double QaPanelGap = 16.0;
    private const double QaPanelMargin = 12.0;

    public static string RenderPage(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new SvgOverlayRenderOptions();

        var page = result.Document.Pages.FirstOrDefault(candidate => candidate.Number == pageNumber)
            ?? throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} was not found.");

        var pageWidth = page.Size.Width;
        var pageHeight = page.Size.Height;
        var sidePanelWidth = QaSidePanelWidth(page);
        var reservesQaPanel = options.IncludeLegend || options.IncludeDiagnostics;
        var width = reservesQaPanel
            ? pageWidth + QaPanelGap + sidePanelWidth + QaPanelMargin
            : pageWidth;
        var height = reservesQaPanel
            ? Math.Max(pageHeight, QaPanelRequiredHeight(result, page, options))
            : pageHeight;
        var builder = new StringBuilder();

        builder.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{N(width)}" height="{N(height)}" viewBox="0 0 {N(width)} {N(height)}" role="img" aria-label="OpenPlanTrace overlay for page {page.Number}" data-profile="{Esc(SvgOverlayRenderOptions.ProfileName(options.Profile))}">""");
        builder.AppendLine("<defs>");
        builder.AppendLine("<style>");
        builder.AppendLine("""
            .sheet-bg { fill: var(--background, #ffffff); }
            .sheet-image { image-rendering: auto; }
            .region { fill: rgba(20, 124, 114, 0.045); stroke: #147c72; stroke-width: 1.1; vector-effect: non-scaling-stroke; }
            .region-title { fill: rgba(201, 124, 24, 0.11); stroke: #c97c18; }
            .region-secondary { fill: rgba(120, 84, 168, 0.09); stroke: #7854a8; }
            .dimension { fill: rgba(120, 84, 168, 0.045); stroke: #7854a8; stroke-width: 0.85; stroke-dasharray: 5 4; vector-effect: non-scaling-stroke; }
            .dimension-line { stroke: #7854a8; stroke-width: 0.95; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .annotation { fill: rgba(37, 135, 180, 0.055); stroke: #2587b4; stroke-width: 0.85; stroke-dasharray: 3 3; vector-effect: non-scaling-stroke; }
            .annotation-reference { fill: rgba(25, 105, 166, 0.10); stroke: #1969a6; stroke-width: 1; vector-effect: non-scaling-stroke; }
            .annotation-reference-link { stroke: #1969a6; stroke-width: 0.8; stroke-dasharray: 4 4; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .grid-axis { stroke: #6b7c1f; stroke-width: 0.8; stroke-dasharray: 8 6; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .grid-bay { stroke: #437f97; stroke-width: 0.75; stroke-dasharray: 3 5; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .grid-label { font: 12px Segoe UI, Arial, sans-serif; fill: #6b7c1f; paint-order: stroke; stroke: #ffffff; stroke-width: 2; stroke-linejoin: round; }
            .wall-component { fill: rgba(25, 105, 166, 0.025); stroke: #1969a6; stroke-width: 0.95; stroke-dasharray: 8 6; vector-effect: non-scaling-stroke; }
            .wall-component-object { fill: rgba(201, 124, 24, 0.04); stroke: #c97c18; stroke-dasharray: 5 4; }
            .wall-component-fragment { fill: rgba(196, 61, 61, 0.035); stroke: #7854a8; stroke-dasharray: 3 4; }
            .wall { stroke: #c43d3d; stroke-width: 0.72; stroke-linecap: butt; fill: none; vector-effect: non-scaling-stroke; }
            .wall-main, .wall-secondary { stroke: #b82f42; }
            .wall-object-like { stroke: #c97c18; stroke-width: 0.58; stroke-dasharray: 5 4; }
            .wall-fragment { stroke: #7854a8; stroke-width: 0.48; stroke-dasharray: 3 5; }
            .wall-excluded { stroke-width: 0.42; stroke-dasharray: 2 6; }
            .wall-topology-span { stroke: #7a5f18; stroke-width: 1.35; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .wall-topology-span-exterior { stroke: #0f4fb8; stroke-width: 1.85; }
            .wall-topology-span-interior { stroke: #0f7a48; stroke-width: 1.45; }
            .wall-topology-span-review { stroke-dasharray: 3 2; }
            .wall-topology-span-excluded { stroke: #7854a8; stroke-width: 0.85; stroke-dasharray: 2 5; }
            .wall-topology-span-review-only { stroke: #a65f00; stroke-width: 1.05; stroke-dasharray: 3 3; }
            .wall-body-footprint { fill: rgba(15, 79, 184, 0.10); stroke: #0f4fb8; stroke-width: 0.78; vector-effect: non-scaling-stroke; }
            .wall-body-footprint-interior { fill: rgba(15, 122, 72, 0.10); stroke: #0f7a48; }
            .wall-body-footprint-review { fill: rgba(166, 95, 0, 0.075); stroke: #a65f00; stroke-dasharray: 3 3; }
            .wall-body-footprint-excluded { fill: rgba(120, 84, 168, 0.06); stroke: #7854a8; stroke-dasharray: 2 5; }
            .wall-graph-repair { stroke: #d04b24; stroke-width: 1.35; stroke-linecap: round; stroke-dasharray: 4 3; fill: none; vector-effect: non-scaling-stroke; }
            .wall-graph-repair-low { stroke: #d97706; }
            .wall-graph-repair-medium { stroke: #d04b24; stroke-width: 1.55; }
            .wall-graph-repair-high { stroke: #b42318; stroke-width: 1.8; }
            .wall-graph-repair-marker { fill: #ffffff; stroke: #d04b24; stroke-width: 0.75; vector-effect: non-scaling-stroke; }
            .wall-graph-repair-target { fill: #d04b24; }
            .node { fill: rgba(255,255,255,0.65); stroke: #b82f42; stroke-width: 0.42; vector-effect: non-scaling-stroke; }
            .room { fill: rgba(63, 143, 87, 0.075); stroke: #3f8f57; stroke-width: 0.95; vector-effect: non-scaling-stroke; }
            .room-cluster { fill: rgba(47, 125, 104, 0.035); stroke: #2f7d68; stroke-width: 1; stroke-dasharray: 9 6; vector-effect: non-scaling-stroke; }
            .room-adjacency { stroke: #2f7d68; stroke-width: 0.85; stroke-dasharray: 5 5; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .opening { fill: rgba(56, 111, 195, 0.10); stroke: #386fc3; stroke-width: 0.95; vector-effect: non-scaling-stroke; }
            .object { fill: rgba(201, 124, 24, 0.075); stroke: #c97c18; stroke-width: 0.9; vector-effect: non-scaling-stroke; }
            .object-aggregate { fill: rgba(143, 95, 18, 0.045); stroke: #8f5f12; stroke-width: 0.75; stroke-dasharray: 6 3; vector-effect: non-scaling-stroke; }
            .surface-pattern { fill: rgba(15, 107, 120, 0.055); stroke: #0f6b78; stroke-width: 0.9; stroke-dasharray: 5 4; vector-effect: non-scaling-stroke; }
            .surface-pattern-label { font: 700 10px Segoe UI, Arial, sans-serif; fill: #0a5360; paint-order: stroke; stroke: #ffffff; stroke-width: 2.4; stroke-linejoin: round; }
            .routing-barrier { stroke: #0a5360; stroke-width: 1.55; stroke-linecap: round; stroke-dasharray: 2 3; fill: none; vector-effect: non-scaling-stroke; }
            .routing-passage { stroke: #15803d; stroke-width: 1.8; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .routing-obstacle { fill: rgba(120, 84, 168, 0.12); stroke: #7854a8; stroke-width: 1.05; stroke-dasharray: 4 3; vector-effect: non-scaling-stroke; }
            .routing-obstacle-hard { fill: rgba(201, 124, 24, 0.14); stroke: #9a5d12; }
            .routing-obstacle-structural { fill: rgba(196, 61, 61, 0.13); stroke: #b82f42; }
            .routing-room-use { fill: rgba(15, 107, 120, 0.11); stroke: #0f6b78; stroke-width: 1; stroke-dasharray: 1 4; vector-effect: non-scaling-stroke; }
            .label { font: 12px Segoe UI, Arial, sans-serif; fill: #191a1f; paint-order: stroke; stroke: #ffffff; stroke-width: 2; stroke-linejoin: round; }
            .legend-bg { fill: rgba(255,255,255,0.92); stroke: #d7d9de; }
            .legend-text { font: 12px Segoe UI, Arial, sans-serif; fill: #191a1f; }
            .diagnostic-bg { fill: rgba(255,255,255,0.9); stroke: #d7d9de; }
            .diagnostic { font: 11px Segoe UI, Arial, sans-serif; fill: #6f7480; }
            """);
        builder.AppendLine("</style>");
        builder.AppendLine("</defs>");

        builder.AppendLine($"""<rect class="sheet-bg" x="0" y="0" width="{N(width)}" height="{N(height)}" style="--background:{Esc(options.BackgroundColor)}" />""");

        if (!string.IsNullOrWhiteSpace(options.BackgroundImageHref))
        {
            builder.AppendLine($"""<image class="sheet-image" href="{Esc(options.BackgroundImageHref!)}" x="0" y="0" width="{N(pageWidth)}" height="{N(pageHeight)}" preserveAspectRatio="none" opacity="{N(Math.Clamp(options.BackgroundImageOpacity, 0, 1))}"><title>Source PDF page background for alignment QA</title></image>""");
        }

        if (options.IncludeRooms)
        {
            builder.AppendLine("""<g id="rooms">""");
            foreach (var room in result.Rooms.Where(room => room.PageNumber == page.Number))
            {
                var title = room.UseKind == RoomUseKind.Unknown
                    ? room.Label ?? room.Id
                    : $"{room.UseKind} {room.Label ?? room.Id}";

                if (room.Boundary.Count >= 3)
                {
                    AppendPolygon(builder, room.Boundary, "room", title, room.Confidence);
                    if (!string.IsNullOrWhiteSpace(room.Label))
                    {
                        builder.AppendLine($"""<text class="label" x="{N(room.Bounds.Center.X)}" y="{N(room.Bounds.Center.Y)}" text-anchor="middle">{Esc(room.Label!)}</text>""");
                    }
                }
                else
                {
                    AppendRect(builder, room.Bounds, "room", title, room.Confidence);
                }
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeRegions)
        {
            builder.AppendLine("""<g id="regions">""");
            foreach (var region in result.SheetRegions.Where(region => region.PageNumber == page.Number))
            {
                var cssClass = region.Kind switch
                {
                    RegionKind.TitleBlock => "region region-title",
                    RegionKind.Notes or RegionKind.Dimensions or RegionKind.KeyPlan or RegionKind.Legend => "region region-secondary",
                    _ => "region"
                };
                AppendRect(builder, region.Bounds, cssClass, region.Label ?? region.Kind.ToString(), region.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeRoomClusters)
        {
            builder.AppendLine("""<g id="room-clusters">""");
            foreach (var cluster in result.RoomAdjacencyGraph.Clusters.Where(cluster => cluster.PageNumber == page.Number))
            {
                var title = cluster.RoomLabels.Count == 0
                    ? $"{cluster.Kind} {cluster.Id}"
                    : $"{cluster.Kind} {string.Join(" + ", cluster.RoomLabels)}";
                AppendRect(builder, cluster.Bounds, "room-cluster", title, cluster.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeRoomAdjacency)
        {
            builder.AppendLine("""<g id="room-adjacency">""");
            var roomsById = result.Rooms
                .Where(room => room.PageNumber == page.Number)
                .ToDictionary(room => room.Id, StringComparer.Ordinal);

            foreach (var edge in result.RoomAdjacencyGraph.Edges.Where(edge => edge.PageNumber == page.Number))
            {
                if (!roomsById.TryGetValue(edge.FirstRoomId, out var first)
                    || !roomsById.TryGetValue(edge.SecondRoomId, out var second))
                {
                    continue;
                }

                builder.AppendLine($"""<line class="room-adjacency" x1="{N(first.Bounds.Center.X)}" y1="{N(first.Bounds.Center.Y)}" x2="{N(second.Bounds.Center.X)}" y2="{N(second.Bounds.Center.Y)}" opacity="{N(Opacity(edge.Confidence))}"><title>{Esc($"{edge.Kind} {edge.FirstRoomLabel ?? edge.FirstRoomId} to {edge.SecondRoomLabel ?? edge.SecondRoomId}")}</title></line>""");
            }

            builder.AppendLine("</g>");
        }

        if (options.IncludeDimensions)
        {
            builder.AppendLine("""<g id="dimensions">""");
            foreach (var dimension in result.Dimensions.Where(dimension => dimension.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    dimension.Bounds,
                    "dimension",
                    $"{dimension.NormalizedText} {dimension.Orientation} {dimension.Id}",
                    dimension.Confidence);

                if (dimension.DimensionLine is { } line)
                {
                    builder.AppendLine($"""<line class="dimension-line" x1="{N(line.Start.X)}" y1="{N(line.Start.Y)}" x2="{N(line.End.X)}" y2="{N(line.End.Y)}" opacity="{N(Opacity(dimension.Confidence))}"><title>{Esc($"{dimension.NormalizedText} {dimension.Id}")}</title></line>""");
                }
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeAnnotations)
        {
            builder.AppendLine("""<g id="annotations">""");
            foreach (var annotation in result.Annotations.Where(annotation => annotation.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    annotation.Bounds,
                    "annotation",
                    $"{annotation.Kind} {annotation.Label ?? annotation.Id}",
                    annotation.Confidence);
            }
            builder.AppendLine("</g>");

            builder.AppendLine("""<g id="annotation-references">""");
            foreach (var annotation in result.Annotations.Where(annotation => annotation.PageNumber == page.Number))
            {
                foreach (var item in annotation.Items.Where(item => item.PageNumber == page.Number))
                {
                    foreach (var reference in item.References)
                    {
                        builder.AppendLine($"""<line class="annotation-reference-link" x1="{N(item.Bounds.Center.X)}" y1="{N(item.Bounds.Center.Y)}" x2="{N(reference.Bounds.Center.X)}" y2="{N(reference.Bounds.Center.Y)}" opacity="{N(Opacity(reference.Confidence))}"><title>{Esc($"{annotation.Kind} {item.Marker ?? item.Id} reference {reference.Marker}")}</title></line>""");
                        AppendRect(
                            builder,
                            reference.Bounds,
                            "annotation-reference",
                            $"{annotation.Kind} {item.Marker ?? item.Id} plan marker {reference.Marker}",
                            reference.Confidence);
                    }
                }
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeGridAxes)
        {
            builder.AppendLine("""<g id="grid-axes">""");
            foreach (var axis in result.GridAxes.Where(axis => axis.PageNumber == page.Number))
            {
                builder.AppendLine($"""<line class="grid-axis" x1="{N(axis.Line.Start.X)}" y1="{N(axis.Line.Start.Y)}" x2="{N(axis.Line.End.X)}" y2="{N(axis.Line.End.Y)}" opacity="{N(Opacity(axis.Confidence))}"><title>{Esc($"{axis.Label ?? axis.Id} {axis.Orientation} - confidence {N(axis.Confidence.Value)}")}</title></line>""");
                if (!string.IsNullOrWhiteSpace(axis.Label))
                {
                    var labelPoint = GridLabelPoint(axis);
                    builder.AppendLine($"""<text class="grid-label" x="{N(labelPoint.X)}" y="{N(labelPoint.Y)}" text-anchor="middle">{Esc(axis.Label!)}</text>""");
                }
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeGridBaySpacings)
        {
            builder.AppendLine("""<g id="grid-bays">""");
            foreach (var bay in result.GridBaySpacings.Where(bay => bay.PageNumber == page.Number))
            {
                builder.AppendLine($"""<line class="grid-bay" x1="{N(bay.Line.Start.X)}" y1="{N(bay.Line.Start.Y)}" x2="{N(bay.Line.End.X)}" y2="{N(bay.Line.End.Y)}" opacity="{N(Opacity(bay.Confidence))}"><title>{Esc(GridBayTitle(bay))}</title></line>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeOpenings)
        {
            builder.AppendLine("""<g id="openings">""");
            foreach (var opening in result.Openings.Where(opening => opening.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    opening.Bounds,
                    "opening",
                    $"{opening.Type} {opening.Operation} {opening.Orientation} {opening.Id}",
                    opening.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeObjects)
        {
            builder.AppendLine("""<g id="objects">""");
            foreach (var candidate in result.ObjectCandidates.Where(candidate => candidate.PageNumber == page.Number))
            {
                var title = string.IsNullOrWhiteSpace(candidate.Label)
                    ? $"{candidate.Category} {candidate.Kind}"
                    : $"{candidate.Category} {candidate.Label}";
                AppendRect(builder, candidate.Bounds, "object", title, candidate.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeObjectAggregates)
        {
            builder.AppendLine("""<g id="object-aggregates">""");
            foreach (var aggregate in result.ObjectAggregates.Where(aggregate => aggregate.PageNumber == page.Number))
            {
                var title = $"{aggregate.Category} {aggregate.Label ?? aggregate.Kind.ToString()} ({aggregate.ChildObjectCount} child objects; routing {aggregate.RoutingInfluence}; room evidence {aggregate.RoomUseEvidence})";
                AppendRect(builder, aggregate.Bounds, "object-aggregate", title, aggregate.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeSurfacePatterns)
        {
            builder.AppendLine("""<g id="surface-patterns">""");
            foreach (var pattern in result.SurfacePatterns.Where(pattern => pattern.PageNumber == page.Number))
            {
                var title = $"{pattern.Kind} {pattern.Orientation} ({pattern.LineCount} lines; topology excluded {pattern.ExcludedFromStructuralTopology})";
                AppendRect(builder, pattern.Bounds, "surface-pattern", title, pattern.Confidence);
                builder.AppendLine($"""<text class="surface-pattern-label" x="{N(pattern.Bounds.X + 4)}" y="{N(Math.Max(10, pattern.Bounds.Y + 12))}"><title>{Esc(title)}</title>{Esc(SurfacePatternLabel(pattern))}</text>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeRoutingLayer)
        {
            builder.AppendLine("""<g id="routing-layer">""");
            foreach (var barrier in result.RoutingLayer.Barriers.Where(barrier => barrier.PageNumber == page.Number))
            {
                builder.AppendLine($"""<line class="routing-barrier" x1="{N(barrier.CenterLine.Start.X)}" y1="{N(barrier.CenterLine.Start.Y)}" x2="{N(barrier.CenterLine.End.X)}" y2="{N(barrier.CenterLine.End.Y)}" opacity="{N(Opacity(barrier.Confidence))}"><title>{Esc($"{barrier.SourceId} routing barrier; component {barrier.WallComponentId ?? "-"}")}</title></line>""");
            }

            foreach (var passage in result.RoutingLayer.Passages.Where(passage => passage.PageNumber == page.Number))
            {
                builder.AppendLine($"""<line class="routing-passage" x1="{N(passage.CenterLine.Start.X)}" y1="{N(passage.CenterLine.Start.Y)}" x2="{N(passage.CenterLine.End.X)}" y2="{N(passage.CenterLine.End.Y)}" opacity="{N(Opacity(passage.Confidence))}"><title>{Esc($"{passage.Type} {passage.Operation}; width {N(passage.DrawingWidth)} drawing units")}</title></line>""");
            }

            foreach (var obstacle in result.RoutingLayer.Obstacles.Where(obstacle => obstacle.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    obstacle.Bounds,
                    RoutingObstacleCssClass(obstacle.ObstacleKind),
                    $"{obstacle.Label ?? obstacle.Category.ToString()} {obstacle.RoutingInfluence} from {obstacle.SourceKind} {obstacle.SourceId}",
                    obstacle.Confidence);
            }

            foreach (var hint in result.RoutingLayer.RoomUseHints.Where(hint => hint.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    hint.Bounds,
                    "routing-room-use",
                    $"{hint.RoomUseKind} room-use hint from {hint.SourceKind} {hint.SourceId}",
                    hint.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallComponents)
        {
            builder.AppendLine("""<g id="wall-components">""");
            foreach (var component in result.WallGraph.Components.Where(component => component.PageNumber == page.Number))
            {
                AppendRect(
                    builder,
                    component.Bounds,
                    ComponentCssClass(component.Kind),
                    $"{component.Kind} {component.Id} ({component.WallCount} walls)",
                    component.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallBodyFootprints)
        {
            builder.AppendLine("""<g id="wall-body-footprints">""");
            var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
            var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
            var topologySpans = WallTopologySpanVisibility.BuildVisibleTopologySpans(result, page.Number, options);
            foreach (var footprint in WallBodyFootprintBuilder.FromPlacementSolidSpans(result, topologySpans)
                .Where(footprint => footprint.PageNumber == page.Number))
            {
                componentByWallId.TryGetValue(footprint.WallId, out var component);
                wallEvidenceAssessments.TryGetValue(footprint.WallId, out var assessment);
                var title = WallBodyFootprintTitle(footprint, component, assessment);
                AppendPolygon(
                    builder,
                    footprint.Polygon,
                    WallBodyFootprintCssClass(footprint, component, assessment),
                    title,
                    footprint.Confidence);
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallTopologySpans)
        {
            builder.AppendLine("""<g id="wall-topology-spans">""");
            var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
            var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
            var topologySpans = WallTopologySpanVisibility.BuildVisibleTopologySpans(result, page.Number, options);
            foreach (var span in topologySpans)
            {
                componentByWallId.TryGetValue(span.WallId, out var component);
                wallEvidenceAssessments.TryGetValue(span.WallId, out var assessment);
                var title = WallTopologySpanTitle(span, component, assessment);
                builder.AppendLine($"""<line class="{WallTopologySpanCssClass(span, component, assessment)}" x1="{N(span.CenterLine.Start.X)}" y1="{N(span.CenterLine.Start.Y)}" x2="{N(span.CenterLine.End.X)}" y2="{N(span.CenterLine.End.Y)}" opacity="{N(Opacity(span.Confidence))}"><title>{Esc(title)}</title></line>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallGraphRepairs)
        {
            builder.AppendLine("""<g id="wall-graph-repairs">""");
            foreach (var candidate in result.WallGraph.RepairCandidates.Where(candidate => candidate.PageNumber == page.Number))
            {
                var title = WallGraphRepairCandidateTitle(candidate);
                builder.AppendLine($"""<line class="{WallGraphRepairCssClass(candidate)}" x1="{N(candidate.RepairLine.Start.X)}" y1="{N(candidate.RepairLine.Start.Y)}" x2="{N(candidate.RepairLine.End.X)}" y2="{N(candidate.RepairLine.End.Y)}" opacity="{N(Opacity(candidate.Confidence))}"><title>{Esc(title)}</title></line>""");
                builder.AppendLine($"""<circle class="wall-graph-repair-marker" cx="{N(candidate.SourcePoint.X)}" cy="{N(candidate.SourcePoint.Y)}" r="1.8"><title>{Esc(title)}</title></circle>""");
                builder.AppendLine($"""<circle class="wall-graph-repair-marker wall-graph-repair-target" cx="{N(candidate.TargetPoint.X)}" cy="{N(candidate.TargetPoint.Y)}" r="1.55"><title>{Esc(title)}</title></circle>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWalls)
        {
            builder.AppendLine("""<g id="walls">""");
            var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
            var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
            foreach (var wall in result.Walls.Where(wall => wall.PageNumber == page.Number))
            {
                componentByWallId.TryGetValue(wall.Id, out var component);
                wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
                var title = WallTitle(wall, component, assessment);
                builder.AppendLine($"""<line class="{WallCssClass(component, assessment)}" x1="{N(wall.CenterLine.Start.X)}" y1="{N(wall.CenterLine.Start.Y)}" x2="{N(wall.CenterLine.End.X)}" y2="{N(wall.CenterLine.End.Y)}" opacity="{N(WallOpacity(wall.Confidence, component, assessment))}"><title>{Esc(title)}</title></line>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallNodes)
        {
            builder.AppendLine("""<g id="wall-nodes">""");
            foreach (var node in result.WallGraph.Nodes.Where(node => node.PageNumber == page.Number))
            {
                builder.AppendLine($"""<circle class="node" cx="{N(node.Position.X)}" cy="{N(node.Position.Y)}" r="0.95" opacity="{N(NodeOpacity(node.Confidence))}"><title>{Esc($"{node.Kind} {node.Id}")}</title></circle>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeLegend)
        {
            AppendLegend(builder, result, page, options);
        }

        if (options.IncludeDiagnostics)
        {
            AppendDiagnostics(builder, result, page, options);
        }

        builder.AppendLine("</svg>");
        return builder.ToString();
    }

    private static void AppendRect(
        StringBuilder builder,
        PlanRect rect,
        string cssClass,
        string title,
        Confidence confidence)
    {
        if (rect.IsEmpty)
        {
            return;
        }

        builder.AppendLine($"""<rect class="{Esc(cssClass)}" x="{N(rect.X)}" y="{N(rect.Y)}" width="{N(rect.Width)}" height="{N(rect.Height)}" opacity="{N(Opacity(confidence))}"><title>{Esc(title)} - confidence {N(confidence.Value)}</title></rect>""");
    }

    private static void AppendPolygon(
        StringBuilder builder,
        IReadOnlyList<PlanPoint> points,
        string cssClass,
        string title,
        Confidence confidence)
    {
        if (points.Count < 3)
        {
            return;
        }

        var encodedPoints = string.Join(" ", points.Select(point => $"{N(point.X)},{N(point.Y)}"));
        builder.AppendLine($"""<polygon class="{Esc(cssClass)}" points="{Esc(encodedPoints)}" opacity="{N(Opacity(confidence))}"><title>{Esc(title)} - confidence {N(confidence.Value)}</title></polygon>""");
    }

    private static void AppendLegend(
        StringBuilder builder,
        PlanScanResult result,
        PlanPage page,
        SvgOverlayRenderOptions options)
    {
        var lineHeight = 18.0;
        var rows = LegendRows(result, page, options);

        var legendWidth = Math.Min(260.0, QaSidePanelWidth(page));
        var legendHeight = 18 + (rows.Length * lineHeight);
        var x = QaSidePanelX(page);
        var y = QaPanelMargin;
        builder.AppendLine($"""<g id="legend" transform="translate({N(x)} {N(y)})">""");
        builder.AppendLine($"""<rect class="legend-bg" x="0" y="0" width="{N(legendWidth)}" height="{N(legendHeight)}" rx="6" />""");

        for (var index = 0; index < rows.Length; index++)
        {
            builder.AppendLine($"""<text class="legend-text" x="10" y="{N(22 + (index * lineHeight))}">{Esc(rows[index])}</text>""");
        }

        builder.AppendLine("</g>");
    }

    private static string[] LegendRows(PlanScanResult result, PlanPage page, SvgOverlayRenderOptions options)
    {
        var rows = new List<string>
        {
            $"Page {page.Number}",
            $"Profile {SvgOverlayRenderOptions.ProfileName(options.Profile)}",
        };

        if (options.Profile == SvgOverlayRenderProfile.StructuralReview)
        {
            rows.Add("Hidden objects/routing/nodes");
        }
        else if (options.Profile == SvgOverlayRenderProfile.PlacementReview)
        {
            rows.Add("Placement-ready wall spans");
        }

        var visibleTopologySpanCount = WallTopologySpanCount(result, page.Number, options);
        var hiddenTopologySpanCount = HiddenNonPlacementTopologySpanCount(result, page.Number, options);
        var wallReadiness = WallPlacementReadinessSummary.From(result, page.Number);
        var repairCandidateCount = result.WallGraph.RepairCandidates.Count(candidate => candidate.PageNumber == page.Number);
        var blockingRepairCandidateCount = result.WallGraph.RepairCandidates.Count(candidate =>
            candidate.PageNumber == page.Number
            && candidate.ImportImpact == WallGraphRepairImportImpact.TopologyImportBlocked);
        rows.AddRange(new[]
        {
            $"{result.SheetRegions.Count(region => region.PageNumber == page.Number)} regions",
            $"{result.Dimensions.Count(dimension => dimension.PageNumber == page.Number)} dimensions",
            $"{result.Annotations.Count(annotation => annotation.PageNumber == page.Number)} annotations",
            $"{result.Annotations.Where(annotation => annotation.PageNumber == page.Number).SelectMany(annotation => annotation.Items).Sum(item => item.References.Count)} annotation refs",
            $"{result.GridAxes.Count(axis => axis.PageNumber == page.Number)} grid axes",
            $"{result.GridBaySpacings.Count(bay => bay.PageNumber == page.Number)} grid bays",
            $"{result.WallGraph.Components.Count(component => component.PageNumber == page.Number)} wall components",
            $"{result.Walls.Count(wall => wall.PageNumber == page.Number)} walls",
            $"{wallReadiness.PlacementReadyWallCount} placement-ready walls",
            $"{wallReadiness.PlacementOmittedWallCount} omitted/review walls",
            $"{WallBodyFootprintCount(result, page.Number, options)} wall body footprints",
            $"{visibleTopologySpanCount} visible topology spans",
            $"{hiddenTopologySpanCount} hidden non-placement topology spans",
            $"{TopologyExcludedWallCount(result, page.Number)} topology-excluded walls",
            $"{repairCandidateCount} wall graph repairs ({blockingRepairCandidateCount} blocking)",
            $"{result.Rooms.Count(room => room.PageNumber == page.Number)} rooms",
            $"{result.RoomAdjacencyGraph.Clusters.Count(cluster => cluster.PageNumber == page.Number)} room clusters",
            $"{result.RoomAdjacencyGraph.Edges.Count(edge => edge.PageNumber == page.Number)} room links",
            $"{result.Openings.Count(opening => opening.PageNumber == page.Number)} openings",
            $"{result.ObjectCandidates.Count(candidate => candidate.PageNumber == page.Number)} objects",
            $"{result.ObjectAggregates.Count(aggregate => aggregate.PageNumber == page.Number)} object aggregates",
            $"{result.SurfacePatterns.Count(pattern => pattern.PageNumber == page.Number)} surface patterns",
            $"{RoutingItemCount(result.RoutingLayer, page.Number)} routing items",
            CalibrationLabel(result.Calibration)
        });
        rows.AddRange(wallReadiness.TopOmissionRows(maxRows: 5));

        return rows.ToArray();
    }

    private static void AppendDiagnostics(
        StringBuilder builder,
        PlanScanResult result,
        PlanPage page,
        SvgOverlayRenderOptions options)
    {
        var messages = result.Diagnostics.Messages
            .Where(message => message.PageNumber is null || message.PageNumber == page.Number)
            .Take(4)
            .ToArray();

        if (messages.Length == 0)
        {
            return;
        }

        var panelWidth = QaSidePanelWidth(page);
        var panelHeight = 18 + (messages.Length * 16);
        var x = QaSidePanelX(page);
        var y = options.IncludeLegend
            ? QaPanelMargin + 18 + (LegendRows(result, page, options).Length * 18.0) + QaPanelMargin
            : QaPanelMargin;
        builder.AppendLine($"""<g id="diagnostics" transform="translate({N(x)} {N(y)})">""");
        builder.AppendLine($"""<rect class="diagnostic-bg" x="0" y="0" width="{N(panelWidth)}" height="{N(panelHeight)}" rx="6" />""");

        for (var index = 0; index < messages.Length; index++)
        {
            var message = Shorten($"{messages[index].Severity}: {messages[index].Stage} - {messages[index].Message}", 62);
            builder.AppendLine($"""<text class="diagnostic" x="10" y="{N(22 + (index * 16))}">{Esc(message)}</text>""");
        }

        builder.AppendLine("</g>");
    }

    private static double QaSidePanelX(PlanPage page) =>
        page.Size.Width + QaPanelGap;

    private static double QaSidePanelWidth(PlanPage page) =>
        Math.Clamp(page.Size.Width * 0.28, 240.0, 360.0);

    private static double QaPanelRequiredHeight(
        PlanScanResult result,
        PlanPage page,
        SvgOverlayRenderOptions options)
    {
        var height = QaPanelMargin;

        if (options.IncludeLegend)
        {
            height += 18 + (LegendRows(result, page, options).Length * 18.0) + QaPanelMargin;
        }

        if (options.IncludeDiagnostics)
        {
            var messageCount = result.Diagnostics.Messages
                .Count(message => message.PageNumber is null || message.PageNumber == page.Number);
            if (messageCount > 0)
            {
                height += 18 + (Math.Min(messageCount, 4) * 16.0) + QaPanelMargin;
            }
        }

        return height;
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, Math.Max(0, maxLength - 3)), "...");

    private static double Opacity(Confidence confidence) =>
        Math.Clamp(0.32 + (confidence.Value * 0.68), 0.32, 1.0);

    private static double WallOpacity(
        Confidence confidence,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var opacity = Opacity(confidence);
        return WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            ? Math.Max(0.12, opacity * 0.32)
            : opacity;
    }

    private static double NodeOpacity(Confidence confidence) =>
        Math.Clamp(0.18 + (confidence.Value * 0.34), 0.18, 0.52);

    private static string ComponentCssClass(WallGraphComponentKind kind) =>
        kind switch
        {
            WallGraphComponentKind.ObjectLikeIsland => "wall-component wall-component-object",
            WallGraphComponentKind.IsolatedFragment => "wall-component wall-component-fragment",
            _ => "wall-component"
        };

    private static string WallCssClass(
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment) =>
        component?.Kind switch
        {
            WallGraphComponentKind.MainStructural => WallCssClass("wall wall-main", component, evidenceAssessment),
            WallGraphComponentKind.SecondaryStructural => WallCssClass("wall wall-secondary", component, evidenceAssessment),
            WallGraphComponentKind.ObjectLikeIsland => WallCssClass("wall wall-object-like", component, evidenceAssessment),
            WallGraphComponentKind.IsolatedFragment => WallCssClass("wall wall-fragment", component, evidenceAssessment),
            _ => WallCssClass("wall", component, evidenceAssessment)
        };

    private static string WallCssClass(
        string cssClass,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment) =>
        WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            ? $"{cssClass} wall-excluded"
            : cssClass;

    private static string WallTitle(
        WallSegment wall,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var topologyExcluded = WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment);
        var componentText = component is null
            ? "no component"
            : $"{component.Kind}; component {component.Id}";
        var evidenceText = evidenceAssessment is null
            ? "no wall evidence assessment"
            : $"wall evidence {evidenceAssessment.Decision} {evidenceAssessment.Category}";

        return $"{wall.Id} ({componentText}; topology excluded {topologyExcluded}; {evidenceText})";
    }

    private static string WallTopologySpanCssClass(
        WallGraphTopologySpan span,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var classes = new List<string> { "wall-topology-span" };

        switch (span.SourceWall?.WallType)
        {
            case WallType.Exterior:
                classes.Add("wall-topology-span-exterior");
                break;
            case WallType.Interior:
                classes.Add("wall-topology-span-interior");
                break;
        }

        if (evidenceAssessment?.RequiresReview == true
            || evidenceAssessment?.Decision == WallEvidenceDecision.Review)
        {
            classes.Add("wall-topology-span-review");
        }

        if (WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment))
        {
            classes.Add("wall-topology-span-excluded");
        }
        else if (!WallTopologySpanVisibility.IsPlacementReadyStructuralSpan(component, evidenceAssessment))
        {
            classes.Add("wall-topology-span-review-only");
        }

        return string.Join(" ", classes);
    }

    private static string WallTopologySpanTitle(
        WallGraphTopologySpan span,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var componentText = component is null
            ? "no component"
            : $"{component.Kind}; component {component.Id}";
        var evidenceText = evidenceAssessment is null
            ? "no wall evidence assessment"
            : $"wall evidence {evidenceAssessment.Decision} {evidenceAssessment.Category}";
        var wallType = span.SourceWall?.WallType.ToString() ?? "Unknown";
        var offsets = span.SourceWallStartOffsetDrawingUnits is { } startOffset
            && span.SourceWallEndOffsetDrawingUnits is { } endOffset
                ? $"; source offsets {N(startOffset)} -> {N(endOffset)} drawing units"
                : string.Empty;

        return $"clean wall topology span {span.Id}; source wall {span.WallId}; {wallType}; {componentText}; {evidenceText}; length {N(span.DrawingLength)} drawing units{offsets}";
    }

    private static string WallBodyFootprintCssClass(
        WallBodyFootprint footprint,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var classes = new List<string> { "wall-body-footprint" };

        if (footprint.SourceWall.WallType == WallType.Interior)
        {
            classes.Add("wall-body-footprint-interior");
        }

        if (evidenceAssessment?.RequiresReview == true
            || evidenceAssessment?.Decision == WallEvidenceDecision.Review)
        {
            classes.Add("wall-body-footprint-review");
        }

        if (WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, evidenceAssessment)
            || !WallTopologySpanVisibility.IsPlacementReadyStructuralSpan(component, evidenceAssessment))
        {
            classes.Add("wall-body-footprint-excluded");
        }

        return string.Join(" ", classes);
    }

    private static string WallBodyFootprintTitle(
        WallBodyFootprint footprint,
        WallGraphComponent? component,
        WallEvidenceWallAssessment? evidenceAssessment)
    {
        var componentText = component is null
            ? "no component"
            : $"{component.Kind}; component {component.Id}";
        var evidenceText = evidenceAssessment is null
            ? "no wall evidence assessment"
            : $"wall evidence {evidenceAssessment.Decision} {evidenceAssessment.Category}";

        return $"wall body footprint {footprint.Id}; source wall {footprint.WallId}; {footprint.SourceWall.WallType}; {componentText}; {evidenceText}; thickness {N(footprint.ThicknessDrawingUnits)} drawing units; body from {footprint.GeometrySource}";
    }

    private static string WallGraphRepairCssClass(WallGraphRepairCandidate candidate)
    {
        var severity = candidate.Severity.ToString().ToLowerInvariant();
        return $"wall-graph-repair wall-graph-repair-{severity}";
    }

    private static string WallGraphRepairCandidateTitle(WallGraphRepairCandidate candidate) =>
        $"{candidate.Kind} {candidate.SuggestedAction}; {candidate.Severity} severity; {candidate.ImportImpact}; gap {N(candidate.GapDistance)} drawing units; safe {N(candidate.SafeSnapDistance)}; review limit {N(candidate.ReviewDistanceLimit)}; walls {string.Join(", ", candidate.WallIds)}";

    private static string RoutingObstacleCssClass(RoutingObstacleKind kind) =>
        kind switch
        {
            RoutingObstacleKind.HardObstacle => "routing-obstacle routing-obstacle-hard",
            RoutingObstacleKind.StructuralBarrier => "routing-obstacle routing-obstacle-structural",
            _ => "routing-obstacle"
        };

    private static int TopologyExcludedWallCount(PlanScanResult result, int pageNumber) =>
        CountTopologyExcludedWalls(result, pageNumber);

    private static int WallTopologySpanCount(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options) =>
        WallTopologySpanVisibility.BuildVisibleTopologySpans(result, pageNumber, options).Count;

    private static int WallBodyFootprintCount(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options) =>
        WallBodyFootprintBuilder
            .FromPlacementSolidSpans(result, WallTopologySpanVisibility.BuildVisibleTopologySpans(result, pageNumber, options))
            .Count(footprint => footprint.PageNumber == pageNumber);

    private static int HiddenNonPlacementTopologySpanCount(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions options) =>
        WallTopologySpanVisibility.BuildHiddenNonPlacementTopologySpans(result, pageNumber, options).Count;

    private static int CountTopologyExcludedWalls(PlanScanResult result, int pageNumber)
    {
        var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
        var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
        return result.Walls
            .Where(wall => wall.PageNumber == pageNumber)
            .Count(wall =>
            {
                componentByWallId.TryGetValue(wall.Id, out var component);
                wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
                return WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, assessment);
            });
    }

    private sealed record WallPlacementReadinessSummary(
        int PlacementReadyWallCount,
        int PlacementOmittedWallCount,
        IReadOnlyDictionary<string, int> OmissionCounts)
    {
        public static WallPlacementReadinessSummary From(PlanScanResult result, int pageNumber)
        {
            var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
            var wallEvidenceAssessments = WallEvidenceExportHelpers.BuildAssessmentLookup(result.WallEvidenceMap);
            var reviewReasonsByWallId = WallReviewReasonMerger.Merge(
                BuildWallReviewReasons(result.Diagnostics.Messages),
                WallPlacementContextGuards.BuildReviewReasons(result));
            var repairCandidatesByWallId = BuildWallGraphRepairCandidateLookup(result.WallGraph.RepairCandidates);
            var topologySpansByWallId = WallTopologySpanVisibility
                .BuildRegularizedPlacementTopologySpans(result)
                .Where(span => span.PageNumber == pageNumber)
                .GroupBy(span => span.WallId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var omissionCodes = new List<string>();
            var readyCount = 0;

            foreach (var wall in result.Walls.Where(wall => wall.PageNumber == pageNumber))
            {
                componentByWallId.TryGetValue(wall.Id, out var component);
                wallEvidenceAssessments.TryGetValue(wall.Id, out var assessment);
                var repairCandidates = repairCandidatesByWallId.TryGetValue(wall.Id, out var wallRepairCandidates)
                    ? wallRepairCandidates
                    : Array.Empty<WallGraphRepairCandidate>();
                var reviewReasons = reviewReasonsByWallId.TryGetValue(wall.Id, out var wallReviewReasons)
                    ? wallReviewReasons
                    : Array.Empty<string>();
                var combinedReviewReasons = reviewReasons
                    .Concat(repairCandidates.Where(candidate => candidate.RequiresReview).Select(WallGraphRepairReviewReason))
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                var topologySpans = topologySpansByWallId.TryGetValue(wall.Id, out var spans)
                    ? spans
                    : Array.Empty<WallGraphTopologySpan>();
                var excludedFromStructuralTopology =
                    WallEvidenceExportHelpers.IsExcludedFromStructuralTopology(component, assessment);
                var reliability = PlacementReliability.ForWall(
                    wall,
                    result.Calibration,
                    component,
                    assessment,
                    combinedReviewReasons);
                var omission = PlacementWallOmissionExport.From(
                    wall,
                    component,
                    assessment,
                    reliability,
                    topologySpans,
                    excludedFromStructuralTopology,
                    repairCandidates,
                    combinedReviewReasons);

                if (omission is null && reliability.ReadyForCoordinatePlacement)
                {
                    readyCount++;
                }
                else if (omission is not null)
                {
                    omissionCodes.Add(omission.Code);
                }
            }

            var omissionCounts = omissionCodes
                .GroupBy(code => code, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            return new WallPlacementReadinessSummary(readyCount, omissionCodes.Count, omissionCounts);
        }

        public IEnumerable<string> TopOmissionRows(int maxRows) =>
            OmissionCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                .Take(maxRows)
                .Select(pair => $"omit: {OmissionLabel(pair.Key)} {pair.Value}");
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> BuildWallReviewReasons(
        IReadOnlyList<PlanDiagnostic> diagnostics)
    {
        var reasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var diagnostic in diagnostics.Where(message => string.Equals(
                     message.Code,
                     "wall_graph.surface_pattern_wall_overlap.review",
                     StringComparison.Ordinal)))
        {
            if (!diagnostic.Properties.TryGetValue("wallId", out var wallId)
                || string.IsNullOrWhiteSpace(wallId))
            {
                continue;
            }

            if (!reasons.TryGetValue(wallId, out var wallReasons))
            {
                wallReasons = new List<string>();
                reasons[wallId] = wallReasons;
            }

            var surfacePatternId = diagnostic.Properties.TryGetValue("surfacePatternId", out var patternId)
                && !string.IsNullOrWhiteSpace(patternId)
                    ? patternId
                    : "unknown";
            var overlap = diagnostic.Properties.TryGetValue("wallOverlapRatio", out var ratio)
                && !string.IsNullOrWhiteSpace(ratio)
                    ? $" at wall overlap ratio {ratio}"
                    : string.Empty;
            wallReasons.Add($"wall overlaps non-structural surface/detail pattern {surfacePatternId}{overlap}");
        }

        return reasons.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<WallGraphRepairCandidate>> BuildWallGraphRepairCandidateLookup(
        IReadOnlyList<WallGraphRepairCandidate> candidates)
    {
        var lookup = new Dictionary<string, List<WallGraphRepairCandidate>>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            foreach (var wallId in WallGraphRepairCandidateWallIds(candidate).Distinct(StringComparer.Ordinal))
            {
                if (!lookup.TryGetValue(wallId, out var wallCandidates))
                {
                    wallCandidates = new List<WallGraphRepairCandidate>();
                    lookup[wallId] = wallCandidates;
                }

                wallCandidates.Add(candidate);
            }
        }

        return lookup.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<WallGraphRepairCandidate>)pair.Value
                .DistinctBy(candidate => candidate.Id, StringComparer.Ordinal)
                .OrderBy(candidate => candidate.Id, StringComparer.Ordinal)
                .ToArray(),
            StringComparer.Ordinal);
    }

    private static IEnumerable<string> WallGraphRepairCandidateWallIds(WallGraphRepairCandidate candidate)
    {
        foreach (var wallId in candidate.WallIds)
        {
            if (!string.IsNullOrWhiteSpace(wallId))
            {
                yield return wallId;
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.HostWallId))
        {
            yield return candidate.HostWallId;
        }
    }

    private static string WallGraphRepairReviewReason(WallGraphRepairCandidate candidate)
    {
        var action = candidate.SuggestedAction switch
        {
            WallGraphRepairAction.TrimEndpointOverrun => "endpoint-overrun trim",
            WallGraphRepairAction.SnapEndpointToWall => "endpoint-to-wall snap",
            WallGraphRepairAction.SnapEndpointToEndpoint => "endpoint-to-endpoint snap",
            _ => candidate.SuggestedAction.ToString()
        };

        return string.Create(
            CultureInfo.InvariantCulture,
            $"wall graph repair candidate {candidate.Id} requires review for {action} ({candidate.Kind}, {candidate.ImportImpact}, {candidate.GapDistance:0.###} drawing units)");
    }

    private static string OmissionLabel(string code) =>
        code switch
        {
            "duplicate_wall_face" => "duplicate faces",
            "isolated_fragment" => "isolated fragments",
            "no_clean_topology_spans" => "no clean spans",
            "object_like_linework" => "object linework",
            "rejected_wall_evidence" => "rejected evidence",
            "secondary_without_room_boundary_support" => "secondary no room",
            "topology_import_blocked" => "blocked repairs",
            "wall_evidence_review_required" => "review evidence",
            _ => code.Replace('_', ' ')
        };

    private static int RoutingItemCount(PlanRoutingLayer routingLayer, int pageNumber) =>
        routingLayer.Barriers.Count(item => item.PageNumber == pageNumber)
        + routingLayer.Passages.Count(item => item.PageNumber == pageNumber)
        + routingLayer.Obstacles.Count(item => item.PageNumber == pageNumber)
        + routingLayer.RoomUseHints.Count(item => item.PageNumber == pageNumber);

    private static IReadOnlyDictionary<string, WallGraphComponent> BuildWallComponentLookup(
        IReadOnlyList<WallGraphComponent> components)
    {
        var lookup = new Dictionary<string, WallGraphComponent>(StringComparer.Ordinal);
        foreach (var component in components)
        {
            foreach (var wallId in component.WallIds)
            {
                if (!string.IsNullOrWhiteSpace(wallId))
                {
                    lookup[wallId] = component;
                }
            }
        }

        return lookup;
    }

    private static PlanPoint GridLabelPoint(GridAxis axis) =>
        axis.Orientation == GridAxisOrientation.Horizontal
            ? new PlanPoint(axis.Line.Start.X, axis.Line.Start.Y - 6)
            : new PlanPoint(axis.Line.Start.X, axis.Line.Start.Y - 6);

    private static string GridBayTitle(GridBaySpacing bay)
    {
        var label = $"{bay.FirstAxisLabel ?? bay.FirstAxisId} to {bay.SecondAxisLabel ?? bay.SecondAxisId}";
        var distance = bay.DistanceMeters is > 0
            ? $"{N(bay.DistanceMeters.Value)} m"
            : $"{N(bay.DrawingDistance)} drawing units";
        return $"{label} {bay.AxisOrientation} bay - {distance} - confidence {N(bay.Confidence.Value)}";
    }

    private static string SurfacePatternLabel(SurfacePatternCandidate pattern)
    {
        var prefix = pattern.Kind == SurfacePatternKind.DenseOrthogonalGrid ? "SP grid" : "SP band";
        var suffix = pattern.Id.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return int.TryParse(suffix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? $"{prefix} {number}"
            : prefix;
    }

    private static string CalibrationLabel(PlanCalibration calibration) =>
        calibration.MillimetersPerDrawingUnit is > 0
            ? $"Scale {N(calibration.MillimetersPerDrawingUnit.Value)} mm/unit"
            : "Scale not calibrated";

    private static string N(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Esc(string value) =>
        WebUtility.HtmlEncode(value);
}
