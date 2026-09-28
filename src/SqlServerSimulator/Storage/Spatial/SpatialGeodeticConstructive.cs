namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geography</c>'s constructive operations, by way of a gnomonic
/// projection that turns every great elliptic arc into a straight line so the
/// planar overlay engine can run on it.
/// </summary>
/// <remarks>
/// <para>A <c>geography</c> edge is the curve cut from the ellipsoid by the
/// plane through its two ends and the centre, so projecting each point's
/// direction from the centre onto a tangent plane — the gnomonic projection —
/// maps every edge onto the straight segment between its projected ends. The
/// planar engine's crossings are then exactly the arcs' crossings, and mapping
/// back is the inverse projection. The plane touches the ellipsoid at the
/// normalized sum of the operands' directions.</para>
/// <para>The plane is turned so that its sweep order runs west to east, then
/// north to south, which is the order real's output follows: a ring starts at
/// its westernmost (then northernmost) vertex, a polygon lists before one
/// further west, and a hull pivots on its southernmost (then westernmost)
/// vertex (probe-derived, SQL Server 2025, 2026-09-28). The turn is a
/// rotation, so a ring's direction, and with it which side is the interior,
/// is kept.</para>
/// <para>A gnomonic projection holds less than a hemisphere, so operands
/// reaching 80° or more from their common centre, and a polygon whose ring
/// runs clockwise — a region larger than a hemisphere — raise
/// <see cref="NotSupportedException"/>.</para>
/// </remarks>
internal sealed class SpatialGeodeticConstructive
{
    /// <summary>The largest angle from the projection centre a vertex may sit at: cos 80°.</summary>
    private const double MinimumCosine = 0.17364817766693041;

    private readonly SpatialVector center;
    private readonly SpatialVector east;
    private readonly SpatialVector north;
    private readonly Dictionary<(double X, double Y), SpatialCoordinate> originals = [];

    private SpatialGeodeticConstructive(SpatialVector center)
    {
        this.center = center;
        var axis = new SpatialVector(0, 0, 1);
        var east = axis.Cross(center);
        if (east.Length < 1e-12)
            east = new SpatialVector(0, 1, 0);
        this.east = east.Normalized;
        this.north = center.Cross(this.east).Normalized;
    }

    /// <summary>Builds the projection over the given shapes, or raises when they span too much of the globe.</summary>
    private static SpatialGeodeticConstructive Over(params SpatialShape[] shapes)
    {
        double x = 0, y = 0, z = 0;
        foreach (var shape in shapes)
        {
            foreach (var point in shape.Coordinates())
            {
                var direction = SpatialEllipsoid.ToCartesian(point).Normalized;
                x += direction.X;
                y += direction.Y;
                z += direction.Z;
            }
        }
        var sum = new SpatialVector(x, y, z);
        if (sum.Length < 1e-9)
            throw TooWide();
        var projection = new SpatialGeodeticConstructive(sum.Normalized);
        foreach (var shape in shapes)
        {
            foreach (var point in shape.Coordinates())
            {
                if (SpatialEllipsoid.ToCartesian(point).Normalized.Dot(projection.center) < MinimumCosine)
                    throw TooWide();
            }
        }
        return projection;
    }

    private static NotSupportedException TooWide() =>
        new("geography constructive operations over instances reaching 80° or more from their common centre are not modeled.");

    private SpatialCoordinate Project(SpatialCoordinate point)
    {
        var direction = SpatialEllipsoid.ToCartesian(point).Normalized;
        var scale = 1 / direction.Dot(this.center);
        var eastward = direction.Dot(this.east) * scale;
        var northward = direction.Dot(this.north) * scale;
        var projected = new SpatialCoordinate(-northward, eastward);
        _ = this.originals.TryAdd((projected.X, projected.Y), new SpatialCoordinate(point.X, point.Y));
        return projected;
    }

    private SpatialCoordinate Unproject(SpatialCoordinate point)
    {
        if (this.originals.TryGetValue((point.X, point.Y), out var original))
            return original;
        var direction = this.center + (this.east * point.Y) + (this.north * -point.X);
        var longitude = double.RadiansToDegrees(Math.Atan2(direction.Y, direction.X));
        var latitude = double.RadiansToDegrees(SpatialEllipsoid.GeodeticLatitude(direction));
        return new SpatialCoordinate(longitude, latitude);
    }

    private SpatialShape Map(SpatialShape shape, bool forward)
    {
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < figures.Length; i++)
        {
            var figure = shape.Figures[i];
            var mapped = new SpatialCoordinate[figure.Length];
            for (var j = 0; j < figure.Length; j++)
                mapped[j] = forward ? this.Project(figure[j]) : this.Unproject(figure[j]);
            figures[i] = mapped;
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = this.Map(shape.Children[i], forward);
        var result = new SpatialShape(shape.Type, figures, children);
        if (forward)
            RequireCounterClockwiseShells(result);
        return result;
    }

    /// <summary>A projected shell running clockwise names the region beyond the ring — more than a hemisphere.</summary>
    private static void RequireCounterClockwiseShells(SpatialShape shape)
    {
        if (shape.Type == SpatialShapeType.Polygon && shape.Figures.Length > 0 && shape.Figures[0].Length > 3)
        {
            var ring = shape.Figures[0];
            var area = 0.0;
            for (var i = 0; i + 1 < ring.Length; i++)
                area += (ring[i].X * ring[i + 1].Y) - (ring[i + 1].X * ring[i].Y);
            if (area < 0)
                throw new NotSupportedException("geography constructive operations over a polygon larger than a hemisphere are not modeled.");
        }
    }

    /// <summary>One of the four set operations between two round-earth instances.</summary>
    public static SpatialShape Overlay(SpatialOverlayOperation operation, SpatialShape a, SpatialShape b)
    {
        if (a.IsEmpty && b.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var projection = Over(a, b);
        var result = SpatialConstructive.Overlay(operation, projection.Map(a, forward: true), projection.Map(b, forward: true));
        return InRealOrder(projection.Map(result, forward: false));
    }

    /// <summary>
    /// Rewrites a result in real's output order, which follows the plain
    /// coordinates rather than the projection: turned so the sweep runs west
    /// to east and then north to south, a ring starts at its westernmost,
    /// then northernmost vertex. The pass re-reads the finished result through
    /// the planar engine in that frame, which orders it without moving a
    /// vertex.
    /// </summary>
    private static SpatialShape InRealOrder(SpatialShape shape)
    {
        var turned = Turn(shape, forward: true);
        return Turn(SpatialConstructive.Overlay(SpatialOverlayOperation.Union, turned, SpatialShape.Empty(SpatialShapeType.GeometryCollection)), forward: false);
    }

    private static SpatialShape Turn(SpatialShape shape, bool forward)
    {
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < figures.Length; i++)
        {
            var figure = shape.Figures[i];
            var mapped = new SpatialCoordinate[figure.Length];
            for (var j = 0; j < figure.Length; j++)
                mapped[j] = forward ? new SpatialCoordinate(-figure[j].Y, figure[j].X) : new SpatialCoordinate(figure[j].Y, -figure[j].X);
            figures[i] = mapped;
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = Turn(shape.Children[i], forward);
        return new SpatialShape(shape.Type, figures, children);
    }

    /// <summary>
    /// <c>MakeValid()</c> on the round earth: the planar rebuild of the
    /// projected instance, mapped back and written in real's order.
    /// </summary>
    public static SpatialShape MakeValid(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return shape;
        var projection = Over(shape);
        return InRealOrder(projection.Map(SpatialSimplify.MakeValid(projection.Map(shape, forward: true)), forward: false));
    }

    /// <summary><c>STConvexHull()</c> on the round earth: the planar hull of the projected vertices.</summary>
    public static SpatialShape ConvexHull(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var projection = Over(shape);
        var hull = projection.Map(SpatialConstructive.ConvexHull(projection.Map(shape, forward: true)), forward: false);
        return hull.Type == SpatialShapeType.Point ? hull : Turn(SpatialConstructive.ConvexHull(Turn(hull, forward: true)), forward: false);
    }
}

/// <summary>
/// <c>geography</c>'s <c>STBuffer</c> / <c>BufferWithTolerance</c> for points.
/// </summary>
/// <remarks>
/// Real draws a point's buffer as a circle in the gnomonic plane centred on
/// the point, sized so the four arc ends — at 45°, 135°, 225° and 315° of the
/// local east–north frame — lie exactly the distance away along the great
/// elliptic arc, and starts the ring at the north-east one; the points between
/// the arc ends sit on the planar circle, so they fall slightly short of the
/// distance on a large buffer (probed 2026-09-28 against SQL Server 2025).
/// Several points buffer separately and union.
/// </remarks>
internal static class SpatialGeodeticBuffer
{
    public static SpatialShape Buffer(SpatialShape shape, double distance, double tolerance, bool relative)
    {
        if (distance == 0)
            return shape;
        if (shape.IsEmpty || !(distance > 0))
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var points = new List<SpatialCoordinate>();
        if (!CollectPoints(shape, points))
            throw new NotSupportedException("geography '.STBuffer' of a line or polygon is not modeled.");
        var result = Circle(points[0], distance, tolerance, relative);
        for (var i = 1; i < points.Count; i++)
            result = SpatialGeodeticConstructive.Overlay(SpatialOverlayOperation.Union, result, Circle(points[i], distance, tolerance, relative));
        return result;
    }

    private static bool CollectPoints(SpatialShape shape, List<SpatialCoordinate> points)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                foreach (var figure in shape.Figures)
                    points.AddRange(figure);
                return true;
            case SpatialShapeType.MultiPoint:
            case SpatialShapeType.GeometryCollection:
                foreach (var child in shape.Children)
                {
                    if (!CollectPoints(child, points))
                        return false;
                }
                return true;
            default:
                return shape.IsEmpty;
        }
    }

    private static SpatialShape Circle(SpatialCoordinate point, double distance, double tolerance, bool relative)
    {
        // The plane is the unit sphere's, reading latitude as a spherical
        // angle — the frame real's round-earth envelope uses too.
        var lat = double.DegreesToRadians(point.Y);
        var lon = double.DegreesToRadians(point.X);
        var center = new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        var axis = new SpatialVector(0, 0, 1);
        var east = axis.Cross(center);
        east = east.Length < 1e-12 ? new SpatialVector(0, 1, 0) : east.Normalized;
        var north = center.Cross(east).Normalized;
        var origin = SpatialShape.Leaf(SpatialShapeType.Point, [[new SpatialCoordinate(point.X, point.Y)]]);

        SpatialCoordinate At(double e, double n)
        {
            var direction = center + (east * e) + (north * n);
            return new SpatialCoordinate(
                double.RadiansToDegrees(Math.Atan2(direction.Y, direction.X)),
                double.RadiansToDegrees(Math.Atan2(direction.Z, direction.AxialRadius)));
        }

        // The planar radius whose north-east point lies the distance away.
        var diagonal = Math.Sqrt(0.5);
        double low = 0, high = Math.Tan(Math.Min(distance / SpatialEllipsoid.SemiMinor * 2, 1.4));
        for (var i = 0; i < 200 && high - low > high * 1e-16; i++)
        {
            var mid = (low + high) / 2;
            var probe = SpatialShape.Leaf(SpatialShapeType.Point, [[At(mid * diagonal, mid * diagonal)]]);
            if (SpatialMeasures.GeographyDistance(origin, probe) < distance)
                low = mid;
            else
                high = mid;
        }
        var radius = (low + high) / 2;
        var allowance = relative ? tolerance : tolerance / distance;
        var steps = 1;
        while (steps < 1 << 20 && Math.Pow(Math.PI / 2 / steps, 2) / 8 > allowance)
            steps *= 2;
        var ring = new SpatialCoordinate[(4 * steps) + 1];
        for (var i = 0; i < 4 * steps; i++)
        {
            var angle = (Math.PI / 4) + (i * Math.PI / 2 / steps);
            ring[i] = At(radius * Math.Cos(angle), radius * Math.Sin(angle));
        }
        ring[^1] = ring[0];
        return SpatialShape.Leaf(SpatialShapeType.Polygon, [ring]);
    }
}
