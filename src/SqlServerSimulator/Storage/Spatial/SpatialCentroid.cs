namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// The two representative points <c>geometry</c> reports: <c>STCentroid()</c>'s
/// area-weighted centre of mass and <c>STPointOnSurface()</c>'s point that lies
/// in the instance.
/// </summary>
/// <remarks>
/// <para><b>Centroid.</b> Real answers only for a <c>Polygon</c> or a
/// <c>MultiPolygon</c>: a point, a line and — probe-confirmed — a
/// <c>GEOMETRYCOLLECTION</c> whose every member is a polygon all read NULL. The
/// answer is the moment sum over the rings, the exterior ring adding and every
/// interior ring subtracting whichever way each was written.</para>
/// <para><b>Point on surface.</b> Real's own pick, for a polygon with no
/// interior ring, is the centroid of the <b>ear at the ring's topmost — then
/// rightmost — vertex</b>, and that is what is reproduced here; a
/// <c>MultiPolygon</c> picks the member reaching furthest right, then furthest
/// up, and applies the same rule inside it. Where that ear isn't a triangle of
/// the polygon's own interior the answer falls back to a scanline point, which
/// keeps the guarantee (the point lies in the instance) without matching real's
/// pick — see <c>docs/claude/spatial.md</c> for the cases.</para>
/// <para>A non-areal instance has its own rules, all probe-derived: a line
/// reports the midpoint of its <i>first segment</i> rather than its halfway
/// point, a <c>MultiPoint</c> / <c>MultiLineString</c> reports its first
/// member's answer, and a <c>GEOMETRYCOLLECTION</c> holding no polygon reports
/// its <i>last</i> member's.</para>
/// </remarks>
internal static class SpatialCentroid
{
    /// <summary>
    /// <c>STCentroid()</c>, or null where real answers NULL — every kind but
    /// Polygon and MultiPolygon, and an empty instance of either.
    /// </summary>
    public static SpatialCoordinate? Centroid(SpatialShape shape)
    {
        SpatialShape[] polygons;
        switch (shape.Type)
        {
            case SpatialShapeType.Polygon:
                polygons = [shape];
                break;
            case SpatialShapeType.MultiPolygon:
                polygons = shape.Children;
                break;
            default:
                return null;
        }

        var area = 0.0;
        var momentX = 0.0;
        var momentY = 0.0;
        foreach (var polygon in polygons)
            Accumulate(polygon, ref area, ref momentX, ref momentY);
        return area == 0 ? null : new SpatialCoordinate(momentX / area, momentY / area);
    }

    /// <summary>
    /// Adds one polygon's signed area and first moments. Each ring's shoelace
    /// sign is normalized so the exterior ring contributes positively and every
    /// interior ring negatively, which is what makes a hole subtract however it
    /// was wound — the planar measures read orientation the same way.
    /// </summary>
    private static void Accumulate(SpatialShape polygon, ref double area, ref double momentX, ref double momentY)
    {
        for (var i = 0; i < polygon.Figures.Length; i++)
        {
            var figure = polygon.Figures[i];
            if (figure.Length < 4)
                continue;
            var twiceArea = 0.0;
            var sumX = 0.0;
            var sumY = 0.0;
            for (var j = 0; j < figure.Length - 1; j++)
            {
                var p = figure[j];
                var q = figure[j + 1];
                var cross = (p.X * q.Y) - (q.X * p.Y);
                twiceArea += cross;
                sumX += (p.X + q.X) * cross;
                sumY += (p.Y + q.Y) * cross;
            }
            if (twiceArea == 0)
                continue;
            var sign = (i == 0 ? 1 : -1) * Math.Sign(twiceArea);
            area += sign * twiceArea / 2;
            momentX += sign * sumX / 6;
            momentY += sign * sumY / 6;
        }
    }

    /// <summary><c>STPointOnSurface()</c>, or null for an empty instance.</summary>
    public static SpatialCoordinate? PointOnSurface(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return null;
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.MultiPoint:
                foreach (var coordinate in shape.Coordinates())
                    return coordinate;
                return null;
            case SpatialShapeType.LineString:
            case SpatialShapeType.MultiLineString:
                return FirstSegmentMidpoint(shape);
            case SpatialShapeType.Polygon:
                return InArea([shape]);
            case SpatialShapeType.MultiPolygon:
                return InArea(shape.Children);
            case SpatialShapeType.GeometryCollection:
                var polygons = new List<SpatialShape>();
                CollectPolygons(shape, polygons);
                if (polygons.Count > 0)
                    return InArea(polygons);
                for (var i = shape.Children.Length - 1; i >= 0; i--)
                {
                    if (!shape.Children[i].IsEmpty)
                        return PointOnSurface(shape.Children[i]);
                }
                return null;
            default:
                return null;
        }
    }

    private static void CollectPolygons(SpatialShape shape, List<SpatialShape> into)
    {
        if (shape.Type == SpatialShapeType.Polygon)
        {
            if (!shape.IsEmpty)
                into.Add(shape);
            return;
        }
        foreach (var child in shape.Children)
            CollectPolygons(child, into);
    }

    /// <summary>
    /// Midpoint of the instance's first non-degenerate segment. Real reports
    /// that rather than the halfway point of the whole curve —
    /// <c>LINESTRING(0 0, 10 0, 11 0)</c> answers <c>POINT (5 0)</c>.
    /// </summary>
    private static SpatialCoordinate? FirstSegmentMidpoint(SpatialShape shape)
    {
        foreach (var figure in shape.Figures)
        {
            for (var i = 1; i < figure.Length; i++)
            {
                if (figure[i - 1].X != figure[i].X || figure[i - 1].Y != figure[i].Y)
                    return new((figure[i - 1].X / 2) + (figure[i].X / 2), (figure[i - 1].Y / 2) + (figure[i].Y / 2));
            }
        }
        foreach (var child in shape.Children)
        {
            if (FirstSegmentMidpoint(child) is { } midpoint)
                return midpoint;
        }
        return null;
    }

    /// <summary>
    /// A point in the area the polygons bound. The member reaching furthest
    /// right — then furthest up — is the one real answers from, which is the
    /// only ordering that explains a <c>MultiPolygon</c>'s answer staying put
    /// when its members are written the other way round.
    /// </summary>
    private static SpatialCoordinate? InArea(IReadOnlyList<SpatialShape> polygons)
    {
        SpatialShape? chosen = null;
        var bestX = double.NegativeInfinity;
        var bestY = double.NegativeInfinity;
        foreach (var polygon in polygons)
        {
            if (polygon.IsEmpty || polygon.Figures.Length == 0)
                continue;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var coordinate in polygon.Coordinates())
            {
                maxX = Math.Max(maxX, coordinate.X);
                maxY = Math.Max(maxY, coordinate.Y);
            }
            if (chosen is null || maxX > bestX || (maxX == bestX && maxY > bestY))
            {
                chosen = polygon;
                bestX = maxX;
                bestY = maxY;
            }
        }
        return chosen is null ? null : InPolygon(chosen);
    }

    private static SpatialCoordinate? InPolygon(SpatialShape polygon)
    {
        var rings = new List<PlanarPoint[]>();
        foreach (var figure in polygon.Figures)
        {
            var ring = Collapse(figure);
            if (ring.Length >= 4)
                rings.Add(ring);
        }
        if (rings.Count == 0)
            return null;

        var segments = new List<PlanarSegment>();
        foreach (var ring in rings)
        {
            for (var i = 1; i < ring.Length; i++)
                segments.Add(new(ring[i - 1], ring[i]));
        }
        return EarPoint(rings, segments) ?? ScanlinePoint(rings, segments);
    }

    /// <summary>Drops consecutive repeats and the closing duplicate, leaving the ring's distinct vertex cycle plus its closing point.</summary>
    private static PlanarPoint[] Collapse(SpatialCoordinate[] figure)
    {
        var kept = new List<PlanarPoint>(figure.Length);
        foreach (var coordinate in figure)
        {
            var point = PlanarPoint.From(coordinate);
            if (kept.Count == 0 || kept[^1] != point)
                kept.Add(point);
        }
        return [.. kept];
    }

    /// <summary>
    /// The centroid of the ear at the exterior ring's topmost — then rightmost
    /// — vertex, when that triangle is one the polygon's own interior holds.
    /// Null hands the caller to the scanline fallback.
    /// </summary>
    private static SpatialCoordinate? EarPoint(List<PlanarPoint[]> rings, List<PlanarSegment> segments)
    {
        var shell = rings[0];
        // The closing point repeats the first, so the distinct cycle is one shorter.
        var count = shell.Length - 1;
        if (count < 3)
            return null;
        var apex = 0;
        for (var i = 1; i < count; i++)
        {
            if (shell[i].Y > shell[apex].Y || (shell[i].Y == shell[apex].Y && shell[i].X > shell[apex].X))
                apex = i;
        }
        var a = shell[(apex + count - 1) % count];
        var b = shell[apex];
        var c = shell[(apex + 1) % count];
        if (SpatialTopology.Orientation(a, b, c) == 0)
            return null;
        foreach (var ring in rings)
        {
            for (var i = 0; i < ring.Length - 1; i++)
            {
                var vertex = ring[i];
                if (vertex != a && vertex != b && vertex != c && InTriangle(vertex, a, b, c))
                    return null;
            }
        }
        var centroid = new PlanarPoint((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3);
        return SpatialTopology.OnAnySegment(centroid, segments) || !SpatialTopology.IsInsideRings(centroid, segments)
            ? null
            : new SpatialCoordinate(centroid.X, centroid.Y);
    }

    /// <summary>True when <paramref name="p"/> lies inside the triangle or on its boundary.</summary>
    private static bool InTriangle(PlanarPoint p, PlanarPoint a, PlanarPoint b, PlanarPoint c)
    {
        var first = SpatialTopology.Orientation(a, b, p);
        var second = SpatialTopology.Orientation(b, c, p);
        var third = SpatialTopology.Orientation(c, a, p);
        var negative = first < 0 || second < 0 || third < 0;
        var positive = first > 0 || second > 0 || third > 0;
        return !(negative && positive);
    }

    /// <summary>
    /// A point on the horizontal line just below the polygon's topmost vertex,
    /// in the rightmost interior span the line crosses. The rings come from an
    /// instance real considers valid, so the even-odd pairing of the crossings
    /// names exactly the interior.
    /// </summary>
    private static SpatialCoordinate? ScanlinePoint(List<PlanarPoint[]> rings, List<PlanarSegment> segments)
    {
        var top = double.NegativeInfinity;
        foreach (var ring in rings)
        {
            foreach (var point in ring)
                top = Math.Max(top, point.Y);
        }
        var below = double.NegativeInfinity;
        foreach (var ring in rings)
        {
            foreach (var point in ring)
            {
                if (point.Y < top)
                    below = Math.Max(below, point.Y);
            }
        }
        if (double.IsNegativeInfinity(below))
            return null;
        var y = (top / 2) + (below / 2);
        if (y <= below || y >= top)
            return null;

        var crossings = new List<double>();
        foreach (var segment in segments)
        {
            var a = segment.A;
            var b = segment.B;
            if ((a.Y > y) != (b.Y > y))
                crossings.Add(a.X + ((b.X - a.X) * (y - a.Y) / (b.Y - a.Y)));
        }
        if (crossings.Count < 2)
            return null;
        crossings.Sort();
        // Crossings alternate entering and leaving the interior; the last pair
        // is the rightmost span.
        var last = crossings.Count - (crossings.Count % 2);
        return new SpatialCoordinate((crossings[last - 2] / 2) + (crossings[last - 1] / 2), y);
    }
}
