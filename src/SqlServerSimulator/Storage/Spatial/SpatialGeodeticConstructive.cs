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
