using System.Globalization;
using System.Net;
using System.Text;

namespace OpenPlanTrace.Export;

public static class PlanOverlaySvgRenderer
{
    public static string RenderPage(
        PlanScanResult result,
        int pageNumber,
        SvgOverlayRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        options ??= new SvgOverlayRenderOptions();

        var page = result.Document.Pages.FirstOrDefault(candidate => candidate.Number == pageNumber)
            ?? throw new ArgumentOutOfRangeException(nameof(pageNumber), $"Page {pageNumber} was not found.");

        var width = page.Size.Width;
        var height = page.Size.Height;
        var builder = new StringBuilder();

        builder.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{N(width)}" height="{N(height)}" viewBox="0 0 {N(width)} {N(height)}" role="img" aria-label="OpenPlanTrace overlay for page {page.Number}">""");
        builder.AppendLine("<defs>");
        builder.AppendLine("<style>");
        builder.AppendLine("""
            .sheet-bg { fill: var(--background, #ffffff); }
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
            .wall { stroke: #c43d3d; stroke-width: 1.15; stroke-linecap: round; fill: none; vector-effect: non-scaling-stroke; }
            .wall-main, .wall-secondary { stroke: #b82f42; }
            .wall-object-like { stroke: #c97c18; stroke-width: 0.95; stroke-dasharray: 5 4; }
            .wall-fragment { stroke: #7854a8; stroke-width: 0.8; stroke-dasharray: 3 5; }
            .node { fill: rgba(255,255,255,0.65); stroke: #b82f42; stroke-width: 0.75; vector-effect: non-scaling-stroke; }
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

        if (options.IncludeWalls)
        {
            builder.AppendLine("""<g id="walls">""");
            var componentByWallId = BuildWallComponentLookup(result.WallGraph.Components);
            foreach (var wall in result.Walls.Where(wall => wall.PageNumber == page.Number))
            {
                componentByWallId.TryGetValue(wall.Id, out var component);
                var title = component is null
                    ? wall.Id
                    : $"{wall.Id} ({component.Kind}; component {component.Id}; topology excluded {component.ExcludedFromStructuralTopology})";
                builder.AppendLine($"""<line class="{WallCssClass(component)}" x1="{N(wall.CenterLine.Start.X)}" y1="{N(wall.CenterLine.Start.Y)}" x2="{N(wall.CenterLine.End.X)}" y2="{N(wall.CenterLine.End.Y)}" opacity="{N(Opacity(wall.Confidence))}"><title>{Esc(title)}</title></line>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeWallNodes)
        {
            builder.AppendLine("""<g id="wall-nodes">""");
            foreach (var node in result.WallGraph.Nodes.Where(node => node.PageNumber == page.Number))
            {
                builder.AppendLine($"""<circle class="node" cx="{N(node.Position.X)}" cy="{N(node.Position.Y)}" r="2" opacity="{N(NodeOpacity(node.Confidence))}"><title>{Esc($"{node.Kind} {node.Id}")}</title></circle>""");
            }
            builder.AppendLine("</g>");
        }

        if (options.IncludeLegend)
        {
            AppendLegend(builder, result, page);
        }

        if (options.IncludeDiagnostics)
        {
            AppendDiagnostics(builder, result, page);
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

    private static void AppendLegend(StringBuilder builder, PlanScanResult result, PlanPage page)
    {
        var lineHeight = 18.0;
        var rows = LegendRows(result, page);

        var legendWidth = 220.0;
        var legendHeight = 18 + (rows.Length * lineHeight);
        var legendPosition = BestPanelPosition(result, page, legendWidth, legendHeight);
        var x = legendPosition.X;
        var y = legendPosition.Y;
        builder.AppendLine($"""<g id="legend" transform="translate({N(x)} {N(y)})">""");
        builder.AppendLine($"""<rect class="legend-bg" x="0" y="0" width="{N(legendWidth)}" height="{N(legendHeight)}" rx="6" />""");

        for (var index = 0; index < rows.Length; index++)
        {
            builder.AppendLine($"""<text class="legend-text" x="10" y="{N(22 + (index * lineHeight))}">{Esc(rows[index])}</text>""");
        }

        builder.AppendLine("</g>");
    }

    private static string[] LegendRows(PlanScanResult result, PlanPage page) =>
        new[]
        {
            $"Page {page.Number}",
            $"{result.SheetRegions.Count(region => region.PageNumber == page.Number)} regions",
            $"{result.Dimensions.Count(dimension => dimension.PageNumber == page.Number)} dimensions",
            $"{result.Annotations.Count(annotation => annotation.PageNumber == page.Number)} annotations",
            $"{result.Annotations.Where(annotation => annotation.PageNumber == page.Number).SelectMany(annotation => annotation.Items).Sum(item => item.References.Count)} annotation refs",
            $"{result.GridAxes.Count(axis => axis.PageNumber == page.Number)} grid axes",
            $"{result.GridBaySpacings.Count(bay => bay.PageNumber == page.Number)} grid bays",
            $"{result.WallGraph.Components.Count(component => component.PageNumber == page.Number)} wall components",
            $"{result.Walls.Count(wall => wall.PageNumber == page.Number)} walls",
            $"{TopologyExcludedWallCount(result, page.Number)} topology-excluded walls",
            $"{result.Rooms.Count(room => room.PageNumber == page.Number)} rooms",
            $"{result.RoomAdjacencyGraph.Clusters.Count(cluster => cluster.PageNumber == page.Number)} room clusters",
            $"{result.RoomAdjacencyGraph.Edges.Count(edge => edge.PageNumber == page.Number)} room links",
            $"{result.Openings.Count(opening => opening.PageNumber == page.Number)} openings",
            $"{result.ObjectCandidates.Count(candidate => candidate.PageNumber == page.Number)} objects",
            $"{result.ObjectAggregates.Count(aggregate => aggregate.PageNumber == page.Number)} object aggregates",
            $"{result.SurfacePatterns.Count(pattern => pattern.PageNumber == page.Number)} surface patterns",
            $"{RoutingItemCount(result.RoutingLayer, page.Number)} routing items",
            CalibrationLabel(result.Calibration)
        };

    private static PlanPoint BestPanelPosition(
        PlanScanResult result,
        PlanPage page,
        double panelWidth,
        double panelHeight)
    {
        const double margin = 12;
        var candidates = new[]
        {
            new PlanPoint(margin, margin),
            new PlanPoint(Math.Max(margin, page.Size.Width - panelWidth - margin), margin),
            new PlanPoint(margin, Math.Max(margin, page.Size.Height - panelHeight - margin)),
            new PlanPoint(Math.Max(margin, page.Size.Width - panelWidth - margin), Math.Max(margin, page.Size.Height - panelHeight - margin))
        };
        var contentBounds = LegendAvoidanceBounds(result, page).ToArray();

        return candidates
            .OrderBy(point => PanelOverlapScore(new PlanRect(point.X, point.Y, panelWidth, panelHeight), contentBounds))
            .ThenBy(point => point.Y)
            .ThenBy(point => point.X)
            .First();
    }

    private static PlanRect LegendPanelBounds(PlanScanResult result, PlanPage page)
    {
        const double lineHeight = 18.0;
        const double legendWidth = 220.0;
        var legendHeight = 18 + (LegendRows(result, page).Length * lineHeight);
        var legendPosition = BestPanelPosition(result, page, legendWidth, legendHeight);
        return new PlanRect(legendPosition.X, legendPosition.Y, legendWidth, legendHeight);
    }

    private static PlanPoint BestDiagnosticsPanelPosition(
        PlanScanResult result,
        PlanPage page,
        double panelWidth,
        double panelHeight)
    {
        const double margin = 12;
        var candidates = new[]
        {
            new PlanPoint(margin, margin),
            new PlanPoint(Math.Max(margin, page.Size.Width - panelWidth - margin), margin),
            new PlanPoint(margin, Math.Max(margin, page.Size.Height - panelHeight - margin)),
            new PlanPoint(Math.Max(margin, page.Size.Width - panelWidth - margin), Math.Max(margin, page.Size.Height - panelHeight - margin))
        };
        var contentBounds = LegendAvoidanceBounds(result, page)
            .Append(LegendPanelBounds(result, page))
            .ToArray();

        return candidates
            .OrderBy(point => PanelOverlapScore(new PlanRect(point.X, point.Y, panelWidth, panelHeight), contentBounds))
            .ThenByDescending(point => point.Y)
            .ThenBy(point => point.X)
            .First();
    }

    private static IEnumerable<PlanRect> LegendAvoidanceBounds(PlanScanResult result, PlanPage page)
    {
        foreach (var region in result.SheetRegions.Where(region =>
            region.PageNumber == page.Number
            && region.Kind is RegionKind.MainFloorPlan or RegionKind.TitleBlock or RegionKind.Dimensions))
        {
            yield return region.Bounds;
        }

        foreach (var wall in result.Walls.Where(wall => wall.PageNumber == page.Number))
        {
            yield return wall.Bounds;
        }

        foreach (var room in result.Rooms.Where(room => room.PageNumber == page.Number))
        {
            yield return room.Bounds;
        }

        foreach (var opening in result.Openings.Where(opening => opening.PageNumber == page.Number))
        {
            yield return opening.Bounds;
        }

        foreach (var aggregate in result.ObjectAggregates.Where(aggregate => aggregate.PageNumber == page.Number))
        {
            yield return aggregate.Bounds;
        }

        foreach (var obstacle in result.RoutingLayer.Obstacles.Where(obstacle => obstacle.PageNumber == page.Number))
        {
            yield return obstacle.Bounds;
        }

        foreach (var hint in result.RoutingLayer.RoomUseHints.Where(hint => hint.PageNumber == page.Number))
        {
            yield return hint.Bounds;
        }
    }

    private static double PanelOverlapScore(PlanRect panel, IReadOnlyList<PlanRect> contentBounds)
    {
        var score = 0.0;
        foreach (var bounds in contentBounds)
        {
            if (bounds.IsEmpty || !panel.Intersects(bounds))
            {
                continue;
            }

            score += panel.OverlapArea(bounds);
        }

        return score;
    }

    private static void AppendDiagnostics(StringBuilder builder, PlanScanResult result, PlanPage page)
    {
        var messages = result.Diagnostics.Messages
            .Where(message => message.PageNumber is null || message.PageNumber == page.Number)
            .Take(4)
            .ToArray();

        if (messages.Length == 0)
        {
            return;
        }

        var panelWidth = Math.Min(520, Math.Max(180, page.Size.Width - 24));
        var panelHeight = 18 + (messages.Length * 16);
        var position = BestDiagnosticsPanelPosition(result, page, panelWidth, panelHeight);
        var x = position.X;
        var y = position.Y;
        builder.AppendLine($"""<g id="diagnostics" transform="translate({N(x)} {N(y)})">""");
        builder.AppendLine($"""<rect class="diagnostic-bg" x="0" y="0" width="{N(panelWidth)}" height="{N(panelHeight)}" rx="6" />""");

        for (var index = 0; index < messages.Length; index++)
        {
            var message = Shorten($"{messages[index].Severity}: {messages[index].Stage} - {messages[index].Message}", 96);
            builder.AppendLine($"""<text class="diagnostic" x="10" y="{N(22 + (index * 16))}">{Esc(message)}</text>""");
        }

        builder.AppendLine("</g>");
    }

    private static string Shorten(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : string.Concat(value.AsSpan(0, Math.Max(0, maxLength - 3)), "...");

    private static double Opacity(Confidence confidence) =>
        Math.Clamp(0.32 + (confidence.Value * 0.68), 0.32, 1.0);

    private static double NodeOpacity(Confidence confidence) =>
        Math.Clamp(0.25 + (confidence.Value * 0.45), 0.25, 0.7);

    private static string ComponentCssClass(WallGraphComponentKind kind) =>
        kind switch
        {
            WallGraphComponentKind.ObjectLikeIsland => "wall-component wall-component-object",
            WallGraphComponentKind.IsolatedFragment => "wall-component wall-component-fragment",
            _ => "wall-component"
        };

    private static string WallCssClass(WallGraphComponent? component) =>
        component?.Kind switch
        {
            WallGraphComponentKind.MainStructural => "wall wall-main",
            WallGraphComponentKind.SecondaryStructural => "wall wall-secondary",
            WallGraphComponentKind.ObjectLikeIsland => "wall wall-object-like",
            WallGraphComponentKind.IsolatedFragment => "wall wall-fragment",
            _ => "wall"
        };

    private static string RoutingObstacleCssClass(RoutingObstacleKind kind) =>
        kind switch
        {
            RoutingObstacleKind.HardObstacle => "routing-obstacle routing-obstacle-hard",
            RoutingObstacleKind.StructuralBarrier => "routing-obstacle routing-obstacle-structural",
            _ => "routing-obstacle"
        };

    private static int TopologyExcludedWallCount(PlanScanResult result, int pageNumber) =>
        result.WallGraph.Components
            .Where(component => component.PageNumber == pageNumber && component.ExcludedFromStructuralTopology)
            .SelectMany(component => component.WallIds)
            .Distinct(StringComparer.Ordinal)
            .Count();

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
