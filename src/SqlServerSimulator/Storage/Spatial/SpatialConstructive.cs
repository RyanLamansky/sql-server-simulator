namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Entry points for <c>geometry</c>'s constructive operations, over the
/// planar overlay engine and the precision grid.
/// </summary>
internal static class SpatialConstructive
{
    /// <summary>One of the four set operations between two planar instances.</summary>
    public static SpatialShape Overlay(SpatialOverlayOperation operation, SpatialShape a, SpatialShape b)
    {
        var grid = SpatialPrecisionGrid.Over(a, b);
        var result = SpatialOverlay.Overlay(GridOperand.From(a, grid), GridOperand.From(b, grid), operation, grid);
        return SpatialResultBuilder.Build(result, grid);
    }

    /// <summary>
    /// <c>STEnvelope()</c>: the axis-aligned bounding rectangle, written from
    /// its lower-left corner counter-clockwise. An axis the instance doesn't
    /// span is widened by 2e-8 of the coordinate either way (1e-8 at zero), so
    /// a point or an axis-parallel line still yields a polygon — probed against
    /// SQL Server 2025, 2026-09-28.
    /// </summary>
    public static SpatialShape Envelope(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var (minX, maxX, minY, maxY) = Extent(shape);
        (minX, maxX) = Widen(minX, maxX);
        (minY, maxY) = Widen(minY, maxY);
        return SpatialShape.Leaf(SpatialShapeType.Polygon,
        [[
            new SpatialCoordinate(minX, minY),
            new SpatialCoordinate(maxX, minY),
            new SpatialCoordinate(maxX, maxY),
            new SpatialCoordinate(minX, maxY),
            new SpatialCoordinate(minX, minY),
        ]]);
    }

    private static (double Min, double Max) Widen(double min, double max)
    {
        if (min != max)
            return (min, max);
        if (min == 0)
            return (-1e-8, 1e-8);
        var margin = Math.Abs(min) * 2e-8;
        return (min - margin, min + margin);
    }

    public static (double MinX, double MaxX, double MinY, double MaxY) Extent(SpatialShape shape)
    {
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        foreach (var point in shape.Coordinates())
        {
            minX = Math.Min(minX, point.X);
            maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y);
            maxY = Math.Max(maxY, point.Y);
        }
        return (minX, maxX, minY, maxY);
    }

    /// <summary>
    /// <c>STConvexHull()</c>, as a Graham scan on the precision grid.
    /// </summary>
    /// <remarks>
    /// The scan pivots on the vertex reaching furthest right (then lowest),
    /// sorts the rest counter-clockwise around it keeping only the furthest of
    /// any that line up with the pivot, and keeps a vertex lying exactly on a
    /// hull edge. On the grid that last rule only bites where the line stays
    /// exactly straight after snapping — an axis-parallel edge — which is why
    /// real keeps a midpoint on a diagonal edge and drops one on a vertical
    /// one. The result starts at the pivot. This reproduces all 200 hulls of a
    /// random integer point sweep against SQL Server 2025 (2026-09-28).
    /// </remarks>
    public static SpatialShape ConvexHull(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var grid = SpatialPrecisionGrid.Over(shape);
        var distinct = new HashSet<GridPoint>();
        var points = new List<GridPoint>();
        foreach (var coordinate in shape.Coordinates())
        {
            var point = grid.Snap(coordinate);
            if (distinct.Add(point))
                points.Add(point);
        }
        var pivot = points[0];
        foreach (var point in points)
        {
            if (point.X > pivot.X || (point.X == pivot.X && point.Y < pivot.Y))
                pivot = point;
        }
        var rest = new List<GridPoint>(points.Count);
        foreach (var point in points)
        {
            if (point != pivot)
                rest.Add(point);
        }
        rest.Sort((p, q) =>
        {
            var turn = GridPoint.Orientation(pivot, p, q);
            return turn != 0 ? -turn : Distance(pivot, p).CompareTo(Distance(pivot, q));
        });
        var ordered = new List<GridPoint>(rest.Count);
        foreach (var point in rest)
        {
            if (ordered.Count > 0 && GridPoint.Orientation(pivot, ordered[^1], point) == 0)
                ordered[^1] = point;
            else
                ordered.Add(point);
        }
        var hull = new List<GridPoint> { pivot };
        foreach (var point in ordered)
        {
            while (hull.Count >= 2 && GridPoint.Orientation(hull[^2], hull[^1], point) < 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        if (hull.Count == 1)
            return SpatialShape.Leaf(SpatialShapeType.Point, [[grid.ToCoordinate(hull[0])]]);
        if (hull.Count == 2)
            return SpatialShape.Leaf(SpatialShapeType.LineString, [[grid.ToCoordinate(hull[0]), grid.ToCoordinate(hull[1])]]);
        var ring = new SpatialCoordinate[hull.Count + 1];
        for (var i = 0; i < hull.Count; i++)
            ring[i] = grid.ToCoordinate(hull[i]);
        ring[^1] = ring[0];
        return SpatialShape.Leaf(SpatialShapeType.Polygon, [ring]);
    }

    private static Int128 Distance(GridPoint a, GridPoint b)
    {
        Int128 dx = b.X - a.X;
        Int128 dy = b.Y - a.Y;
        return (dx * dx) + (dy * dy);
    }

    /// <summary>
    /// <c>ShortestLineTo(other)</c>: the segment from the receiver's point
    /// nearest the other instance to the other's point nearest it, or an empty
    /// line when the two meet. Ties go to the first pair found walking the
    /// receiver's components in order and, for each, the other's — so of two
    /// squares facing each other across a gap, the line runs between their
    /// lowest facing corners (probed 2026-09-28 against SQL Server 2025). A
    /// point's foot on a segment is <c>A + t·(B - A)</c>, whose last digit
    /// real's text carries (<c>3.0000000000000004</c>).
    /// </summary>
    public static SpatialShape? ShortestLine(SpatialShape a, SpatialShape b)
    {
        if (a.IsEmpty || b.IsEmpty)
            return null;
        if (SpatialRelate.Evaluate(SpatialPredicateKind.Intersects, a, b))
            return SpatialShape.Empty(SpatialShapeType.LineString);
        var left = new List<(SpatialCoordinate A, SpatialCoordinate B)>();
        var right = new List<(SpatialCoordinate A, SpatialCoordinate B)>();
        Pieces(a, left);
        Pieces(b, right);
        var best = double.PositiveInfinity;
        (SpatialCoordinate From, SpatialCoordinate To) line = default;
        void Consider(SpatialCoordinate from, SpatialCoordinate to)
        {
            var dx = to.X - from.X;
            var dy = to.Y - from.Y;
            var squared = (dx * dx) + (dy * dy);
            if (squared < best)
            {
                best = squared;
                line = (from, to);
            }
        }
        foreach (var (p, q) in left)
        {
            foreach (var (r, t) in right)
            {
                Consider(p, Foot(p, r, t));
                if (q != p)
                    Consider(q, Foot(q, r, t));
                Consider(Foot(r, p, q), r);
                if (t != r)
                    Consider(Foot(t, p, q), t);
            }
        }
        return SpatialShape.Leaf(SpatialShapeType.LineString, [[line.From, line.To]]);
    }

    /// <summary>Every isolated point (as a zero-length piece) and every edge beneath a shape, in figure order.</summary>
    private static void Pieces(SpatialShape shape, List<(SpatialCoordinate A, SpatialCoordinate B)> pieces)
    {
        foreach (var figure in shape.Figures)
        {
            if (figure.Length == 1)
            {
                pieces.Add((Plain(figure[0]), Plain(figure[0])));
                continue;
            }
            for (var i = 1; i < figure.Length; i++)
                pieces.Add((Plain(figure[i - 1]), Plain(figure[i])));
        }
        foreach (var child in shape.Children)
            Pieces(child, pieces);
    }

    private static SpatialCoordinate Plain(SpatialCoordinate point) => new(point.X, point.Y);

    /// <summary>The point of segment <c>a b</c> nearest <paramref name="p"/>.</summary>
    private static SpatialCoordinate Foot(SpatialCoordinate p, SpatialCoordinate a, SpatialCoordinate b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var squared = (dx * dx) + (dy * dy);
        if (squared == 0)
            return a;
        var t = (((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / squared;
        return t <= 0 ? a : t >= 1 ? b : new SpatialCoordinate(a.X + (t * dx), a.Y + (t * dy));
    }

    /// <summary>
    /// <c>STBoundary()</c>. A polygon's boundary is its rings as lines, each
    /// normalized the way an overlay writes a polygon's rings; a line's is the
    /// mod-2 set of its figures' endpoints; a point's is empty. A collection
    /// holding polygons unions its members' boundaries through the overlay.
    /// </summary>
    public static SpatialShape Boundary(SpatialShape shape)
    {
        var grid = SpatialPrecisionGrid.Over(shape);
        var operand = new GridOperand();
        var hasArea = false;
        CollectBoundary(shape, grid, operand, ref hasArea);
        var endpoints = new Dictionary<GridPoint, int>();
        foreach (var line in operand.Lines)
        {
            if (line[0] == line[^1] && line.Length > 2)
                continue;
            endpoints[line[0]] = endpoints.GetValueOrDefault(line[0]) + 1;
            endpoints[line[^1]] = endpoints.GetValueOrDefault(line[^1]) + 1;
        }
        var geometry = new GridGeometry();
        if (!hasArea)
        {
            // The boundary points come out in the order of the lines real's
            // sweep writes, each line's start before its end.
            var swept = SpatialOverlay.Overlay(operand, new GridOperand(), SpatialOverlayOperation.Union, grid);
            var emitted = new HashSet<GridPoint>();
            foreach (var line in SpatialResultBuilder.OrderedLines(swept))
            {
                foreach (var end in (ReadOnlySpan<GridPoint>)[line[0], line[^1]])
                {
                    if ((endpoints.GetValueOrDefault(end) & 1) == 1 && emitted.Add(end))
                        geometry.Points.Add(end);
                }
            }
            return SpatialResultBuilder.BuildInOrder(geometry, grid);
        }
        foreach (var (point, count) in endpoints)
        {
            if ((count & 1) == 1)
                geometry.Points.Add(point);
        }
        if (shape.Type is SpatialShapeType.Polygon or SpatialShapeType.MultiPolygon)
        {
            foreach (var polygon in shape.Type == SpatialShapeType.Polygon ? [shape] : shape.Children)
            {
                var rings = new List<GridPoint[]>();
                foreach (var figure in polygon.Figures)
                {
                    if (figure.Length > 3)
                        rings.Add(Snap(figure, grid, closed: true));
                }
                if (rings.Count > 0)
                    geometry.Polygons.Add(rings);
            }
            return SpatialResultBuilder.BuildRingsAsLines(geometry, grid);
        }
        // A collection holding an area: the boundary of the union of its
        // members, which is how real reads it — a line crossing into a polygon
        // ends where it meets the ring.
        var union = SpatialOverlay.Overlay(GridOperand.From(shape, grid), new GridOperand(), SpatialOverlayOperation.Union, grid);
        var result = new GridGeometry();
        result.Polygons.AddRange(union.Polygons);
        var counts = new Dictionary<GridPoint, int>();
        foreach (var line in union.Lines)
        {
            if (line[0] == line[^1])
                continue;
            counts[line[0]] = counts.GetValueOrDefault(line[0]) + 1;
            counts[line[^1]] = counts.GetValueOrDefault(line[^1]) + 1;
        }
        foreach (var (point, count) in counts)
        {
            if ((count & 1) == 1)
                result.Points.Add(point);
        }
        return SpatialResultBuilder.BuildRingsAsLines(result, grid);
    }

    private static void CollectBoundary(SpatialShape shape, SpatialPrecisionGrid grid, GridOperand operand, ref bool hasArea)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.LineString:
                foreach (var figure in shape.Figures)
                {
                    if (figure.Length > 1)
                        operand.Lines.Add(Snap(figure, grid, closed: false));
                }
                break;
            case SpatialShapeType.Polygon:
                foreach (var figure in shape.Figures)
                {
                    if (figure.Length > 3)
                    {
                        operand.Rings.Add(Snap(figure, grid, closed: true));
                        hasArea = true;
                    }
                }
                break;
            default:
                foreach (var child in shape.Children)
                    CollectBoundary(child, grid, operand, ref hasArea);
                break;
        }
    }

    private static GridPoint[] Snap(SpatialCoordinate[] figure, SpatialPrecisionGrid grid, bool closed)
    {
        var points = new List<GridPoint>(figure.Length);
        foreach (var coordinate in figure)
        {
            var point = grid.Snap(coordinate);
            if (points.Count == 0 || points[^1] != point)
                points.Add(point);
        }
        if (closed && points.Count > 1 && points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        return [.. points];
    }
}

/// <summary>
/// <c>Reduce</c> and <c>MakeValid</c> for <c>geometry</c>.
/// </summary>
internal static class SpatialSimplify
{
    /// <summary>
    /// <c>Reduce(tolerance)</c>: Douglas–Peucker on every line and ring,
    /// measuring each vertex's distance to the <i>segment</i> between the
    /// kept ends (not the infinite line through them) and keeping it only when
    /// that distance exceeds the tolerance, so a tolerance of 0 still drops an
    /// exactly collinear vertex. A ring runs from its first vertex back to
    /// itself, which is where its simplification is anchored.
    /// </summary>
    /// <remarks>
    /// A collection whose members are all one kind comes back as that kind's
    /// Multi form, nested collections flattened. A ring that collapses below
    /// four vertices leaves an invalid instance, which real then repairs the
    /// way <see cref="MakeValid"/> does — probed against SQL Server 2025,
    /// 2026-09-28.
    /// </remarks>
    public static SpatialShape Reduce(SpatialShape shape, double tolerance, bool isGeography = false)
    {
        var leaves = new List<SpatialShape>();
        var collapsed = false;
        Collect(shape, tolerance, leaves, ref collapsed, isGeography);
        SpatialShape result;
        if (shape.Type is SpatialShapeType.GeometryCollection or SpatialShapeType.MultiPoint or SpatialShapeType.MultiLineString or SpatialShapeType.MultiPolygon)
        {
            if (leaves.Count == 0)
            {
                result = SpatialShape.Empty(shape.Type);
            }
            else
            {
                var kind = leaves[0].Type;
                var uniform = leaves.TrueForAll(leaf => leaf.Type == kind);
                result = SpatialShape.Collection(!uniform ? SpatialShapeType.GeometryCollection : kind switch
                {
                    SpatialShapeType.Point => SpatialShapeType.MultiPoint,
                    SpatialShapeType.LineString => SpatialShapeType.MultiLineString,
                    _ => SpatialShapeType.MultiPolygon,
                }, [.. leaves]);
            }
        }
        else
        {
            result = leaves.Count == 1 ? leaves[0] : shape;
        }
        return !collapsed ? result : isGeography ? SpatialGeodeticConstructive.MakeValid(result) : MakeValid(result);
    }

    private static void Collect(SpatialShape shape, double tolerance, List<SpatialShape> leaves, ref bool collapsed, bool isGeography)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.LineString:
            case SpatialShapeType.Polygon:
                if (shape.IsEmpty)
                {
                    if (shape.Type != SpatialShapeType.Point || leaves.Count == 0)
                        leaves.Add(shape);
                    return;
                }
                var figures = new SpatialCoordinate[shape.Figures.Length][];
                for (var i = 0; i < figures.Length; i++)
                {
                    figures[i] = shape.Type == SpatialShapeType.Point ? shape.Figures[i] : Simplify(shape.Figures[i], tolerance, isGeography);
                    if (shape.Type == SpatialShapeType.Polygon && figures[i].Length < 4)
                        collapsed = true;
                }
                leaves.Add(SpatialShape.Leaf(shape.Type, figures));
                return;
            default:
                foreach (var child in shape.Children)
                    Collect(child, tolerance, leaves, ref collapsed, isGeography);
                return;
        }
    }

    private static SpatialCoordinate[] Simplify(SpatialCoordinate[] figure, double tolerance, bool isGeography)
    {
        if (figure.Length < 3)
            return figure;
        var keep = new bool[figure.Length];
        keep[0] = true;
        keep[^1] = true;
        var pending = new Stack<(int From, int To)>();
        pending.Push((0, figure.Length - 1));
        while (pending.Count > 0)
        {
            var (from, to) = pending.Pop();
            var farthest = -1;
            var distance = -1.0;
            for (var i = from + 1; i < to; i++)
            {
                var d = isGeography ? ArcDistance(figure[i], figure[from], figure[to]) : SegmentDistance(figure[i], figure[from], figure[to]);
                if (d > distance)
                {
                    distance = d;
                    farthest = i;
                }
            }
            if (farthest < 0 || !(distance > tolerance))
                continue;
            keep[farthest] = true;
            pending.Push((farthest, to));
            pending.Push((from, farthest));
        }
        var result = new List<SpatialCoordinate>(figure.Length);
        for (var i = 0; i < figure.Length; i++)
        {
            if (keep[i])
                result.Add(figure[i]);
        }
        return [.. result];
    }

    /// <summary>A vertex's distance from the great elliptic arc between two others, metres — geography's Reduce measure.</summary>
    private static double ArcDistance(SpatialCoordinate p, SpatialCoordinate a, SpatialCoordinate b) =>
        SpatialMeasures.GeographyDistance(
            SpatialShape.Leaf(SpatialShapeType.Point, [[p]]),
            a.X == b.X && a.Y == b.Y ? SpatialShape.Leaf(SpatialShapeType.Point, [[a]]) : SpatialShape.Leaf(SpatialShapeType.LineString, [[a, b]]));

    private static double SegmentDistance(SpatialCoordinate p, SpatialCoordinate a, SpatialCoordinate b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        var t = lengthSquared == 0 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / lengthSquared, 0, 1);
        var ex = p.X - a.X - (t * dx);
        var ey = p.Y - a.Y - (t * dy);
        return Math.Sqrt((ex * ex) + (ey * ey));
    }

    /// <summary>
    /// <c>MakeValid()</c>: a valid instance comes back untouched; an invalid
    /// one is rebuilt by the overlay engine — each polygon's area read by
    /// even-odd parity over its rings, so a bow-tie splits into its two
    /// triangles, overlapping members merged, lines noded where they cross or
    /// retrace. Unlike the other constructive operations, every vertex of the
    /// rebuilt instance has been through the precision grid, so real's
    /// last-digit shift shows even on the input's own vertices.
    /// </summary>
    public static SpatialShape MakeValid(SpatialShape shape)
    {
        var grid = SpatialPrecisionGrid.Over(shape);
        grid.RestoreOriginals = false;
        var members = new List<SpatialShape>();
        Flatten(shape, members);
        var collection = SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. members]);
        var result = SpatialOverlay.Overlay(GridOperand.From(collection, grid), new GridOperand(), SpatialOverlayOperation.Union, grid);
        return SpatialResultBuilder.Build(result, grid);
    }

    private static void Flatten(SpatialShape shape, List<SpatialShape> members)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.LineString:
                members.Add(shape);
                return;
            case SpatialShapeType.Polygon:
            case SpatialShapeType.MultiPolygon:
                // The rings' parity is the area — overlapping members of an
                // invalid multipolygon cancel, as they do on real — and each
                // ring also stands as a line, so the stretch of a ring that
                // encloses nothing survives as one while the rest is absorbed
                // into the area's boundary.
                var rings = new List<SpatialShape>();
                CollectRings(shape, rings, members);
                if (rings.Count > 0)
                    members.Add(rings.Count == 1 ? rings[0] : SpatialShape.Collection(SpatialShapeType.MultiPolygon, [.. rings]));
                return;
            default:
                foreach (var child in shape.Children)
                    Flatten(child, members);
                return;
        }
    }

    private static void CollectRings(SpatialShape shape, List<SpatialShape> rings, List<SpatialShape> lines)
    {
        if (shape.Type == SpatialShapeType.MultiPolygon)
        {
            foreach (var child in shape.Children)
                CollectRings(child, rings, lines);
            return;
        }
        var kept = new List<SpatialCoordinate[]>();
        foreach (var figure in shape.Figures)
        {
            if (figure.Length > 1)
                lines.Add(SpatialShape.Leaf(SpatialShapeType.LineString, [figure]));
            if (figure.Length >= 4)
                kept.Add(figure);
        }
        if (kept.Count > 0)
            rings.Add(SpatialShape.Leaf(SpatialShapeType.Polygon, [.. kept]));
    }
}
