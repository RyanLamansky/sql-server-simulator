namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// Planar measurements over a <see cref="SpatialShape"/> — the <c>geometry</c>
/// type's <c>STArea()</c> and <c>STLength()</c>.
/// </summary>
/// <remarks>
/// <para>Everything here is flat-earth Cartesian and exact for the shapes the
/// model carries. The round-earth counterparts are a separate problem: real
/// measures <c>geography</c> along the <b>great elliptic arc</b> rather than
/// the geodesic (see <c>docs/claude/spatial.md</c>), so they need their own
/// implementation rather than a coordinate swap.</para>
/// <para>Both measures recurse through Multi* and GeometryCollection members,
/// and both report 0 rather than NULL for a shape of the wrong dimension —
/// a Point has no length and no area, a LineString no area.</para>
/// </remarks>
internal static class SpatialMeasures
{
    /// <summary>
    /// Total planar area: each polygon contributes its exterior ring's area
    /// less its interior rings'. Ring orientation doesn't matter — the
    /// shoelace sum is taken absolute per ring, which is what lets a polygon
    /// written clockwise measure the same as one written counter-clockwise.
    /// </summary>
    public static double Area(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon)
        {
            for (var i = 0; i < shape.Figures.Length; i++)
            {
                var ring = Math.Abs(SignedRingArea(shape.Figures[i]));
                total += i == 0 ? ring : -ring;
            }
        }
        foreach (var child in shape.Children)
            total += Area(child);
        return total;
    }

    /// <summary>
    /// Total planar length: the perimeter of every ring of every polygon plus
    /// the length of every line figure. A polygon's <c>STLength()</c> is its
    /// boundary, which is why the ring walk is shared with the line walk.
    /// </summary>
    public static double Length(SpatialShape shape)
    {
        var total = 0.0;
        if (shape.Type is not (SpatialShapeType.Point or SpatialShapeType.MultiPoint))
        {
            foreach (var figure in shape.Figures)
                total += FigureLength(figure);
        }
        foreach (var child in shape.Children)
            total += Length(child);
        return total;
    }

    /// <summary>Shoelace sum over a closed ring; the sign carries the ring's orientation.</summary>
    private static double SignedRingArea(SpatialCoordinate[] ring)
    {
        if (ring.Length < 4)
            return 0;
        var sum = 0.0;
        for (var i = 0; i < ring.Length - 1; i++)
            sum += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
        return sum / 2.0;
    }

    private static double FigureLength(SpatialCoordinate[] points)
    {
        var total = 0.0;
        for (var i = 1; i < points.Length; i++)
        {
            var dx = points[i].X - points[i - 1].X;
            var dy = points[i].Y - points[i - 1].Y;
            total += Math.Sqrt((dx * dx) + (dy * dy));
        }
        return total;
    }
}
