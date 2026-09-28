namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Turns a grid result into the shape real returns, in real's output order.
/// </summary>
/// <remarks>
/// <para>Real emits its components in <b>descending sweep order</b> of each
/// component's lowest vertex — lowest meaning least y, then least x — so the
/// component reaching lowest comes last (probe-derived, SQL Server 2025,
/// 2026-09-28). Inside a polygon the order is reversed: a ring starts at its
/// own lowest vertex, the shell runs counter-clockwise and each hole
/// clockwise, and the holes are listed lowest first. A line keeps the direction
/// the overlay's sweep gave it.</para>
/// <para>A single kind of component yields that kind, or its Multi form when
/// there are several; mixed kinds yield a <c>GEOMETRYCOLLECTION</c> whose
/// members are the components themselves, and nothing at all yields
/// <c>GEOMETRYCOLLECTION EMPTY</c>.</para>
/// </remarks>
internal static class SpatialResultBuilder
{
    private sealed class Component(SpatialShapeType type, GridPoint key, SpatialCoordinate[][] figures)
    {
        public readonly SpatialShapeType Type = type;
        public readonly GridPoint Key = key;
        public readonly SpatialCoordinate[][] Figures = figures;
    }

    public static SpatialShape Build(GridGeometry geometry, SpatialPrecisionGrid grid)
    {
        var components = new List<Component>();
        foreach (var polygon in geometry.Polygons)
        {
            var shell = Normalize(polygon[0], counterClockwise: true);
            var holes = new List<GridPoint[]>();
            for (var i = 1; i < polygon.Count; i++)
                holes.Add(Normalize(polygon[i], counterClockwise: false));
            holes.Sort((p, q) => p[0].CompareTo(q[0]));
            var figures = new SpatialCoordinate[holes.Count + 1][];
            figures[0] = Closed(shell, grid);
            for (var i = 0; i < holes.Count; i++)
                figures[i + 1] = Closed(holes[i], grid);
            components.Add(new Component(SpatialShapeType.Polygon, shell[0], figures));
        }
        foreach (var line in geometry.Lines)
            components.Add(new Component(SpatialShapeType.LineString, LowestOf(line), [Map(line, grid)]));
        foreach (var point in geometry.Points)
            components.Add(new Component(SpatialShapeType.Point, point, [[grid.ToCoordinate(point)]]));

        components.Sort((p, q) => q.Key.CompareTo(p.Key));
        return Assemble(components);
    }

    /// <summary>The result's lines in the order real writes them.</summary>
    public static List<GridPoint[]> OrderedLines(GridGeometry geometry)
    {
        var lines = new List<(GridPoint Key, GridPoint[] Line)>();
        foreach (var line in geometry.Lines)
            lines.Add((LowestOf(line), line));
        lines.Sort((p, q) => q.Key.CompareTo(p.Key));
        return lines.ConvertAll(static entry => entry.Line);
    }

    /// <summary>Points kept in the order given, for a result whose order is decided elsewhere.</summary>
    public static SpatialShape BuildInOrder(GridGeometry geometry, SpatialPrecisionGrid grid)
    {
        var components = new List<Component>();
        foreach (var point in geometry.Points)
            components.Add(new Component(SpatialShapeType.Point, point, [[grid.ToCoordinate(point)]]));
        return Assemble(components);
    }

    private static GridPoint LowestOf(GridPoint[] line)
    {
        var key = line[0];
        foreach (var point in line)
        {
            if (point.CompareTo(key) < 0)
                key = point;
        }
        return key;
    }

    /// <summary>
    /// A polygonal instance's boundary: each ring as a line, oriented and
    /// started as the ring would be written in an overlay result, all of them
    /// in the usual descending order.
    /// </summary>
    public static SpatialShape BuildRingsAsLines(GridGeometry geometry, SpatialPrecisionGrid grid)
    {
        var components = new List<Component>();
        foreach (var polygon in geometry.Polygons)
        {
            for (var i = 0; i < polygon.Count; i++)
            {
                var ring = Normalize(polygon[i], counterClockwise: i == 0);
                components.Add(new Component(SpatialShapeType.LineString, ring[0], [Closed(ring, grid)]));
            }
        }
        foreach (var point in geometry.Points)
            components.Add(new Component(SpatialShapeType.Point, point, [[grid.ToCoordinate(point)]]));
        components.Sort((p, q) => q.Key.CompareTo(p.Key));
        return Assemble(components);
    }

    private static SpatialShape Assemble(List<Component> components)
    {
        if (components.Count == 0)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        if (components.Count == 1)
            return SpatialShape.Leaf(components[0].Type, components[0].Figures);
        var kind = components[0].Type;
        var uniform = true;
        foreach (var component in components)
            uniform &= component.Type == kind;
        var members = new SpatialShape[components.Count];
        for (var i = 0; i < members.Length; i++)
            members[i] = SpatialShape.Leaf(components[i].Type, components[i].Figures);
        if (!uniform)
            return SpatialShape.Collection(SpatialShapeType.GeometryCollection, members);
        return SpatialShape.Collection(kind switch
        {
            SpatialShapeType.Point => SpatialShapeType.MultiPoint,
            SpatialShapeType.LineString => SpatialShapeType.MultiLineString,
            _ => SpatialShapeType.MultiPolygon,
        }, members);
    }

    /// <summary>Rotates an open ring to start at its lowest vertex and orients it as asked.</summary>
    public static GridPoint[] Normalize(GridPoint[] ring, bool counterClockwise)
    {
        var area = SpatialOverlay.SignedArea(ring);
        var source = (area > 0) == counterClockwise ? ring : [.. ring.Reverse()];
        var start = 0;
        for (var i = 1; i < source.Length; i++)
        {
            if (source[i].CompareTo(source[start]) < 0)
                start = i;
        }
        var result = new GridPoint[source.Length];
        for (var i = 0; i < source.Length; i++)
            result[i] = source[(start + i) % source.Length];
        return result;
    }

    private static SpatialCoordinate[] Closed(GridPoint[] ring, SpatialPrecisionGrid grid)
    {
        var figure = new SpatialCoordinate[ring.Length + 1];
        for (var i = 0; i < ring.Length; i++)
            figure[i] = grid.ToCoordinate(ring[i]);
        figure[^1] = figure[0];
        return figure;
    }

    private static SpatialCoordinate[] Map(GridPoint[] run, SpatialPrecisionGrid grid)
    {
        var figure = new SpatialCoordinate[run.Length];
        for (var i = 0; i < run.Length; i++)
            figure[i] = grid.ToCoordinate(run[i]);
        return figure;
    }
}
