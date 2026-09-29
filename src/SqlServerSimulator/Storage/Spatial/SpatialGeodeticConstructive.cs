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
/// normalized sum of the operands' directions, or at the centre of the
/// smallest cap holding them where the sum leaves a vertex too near the
/// horizon.</para>
/// <para>A set operation's result is then written in real's order, the sweep
/// of a plane whose primary axis is the Earth-centred y axis (see
/// <see cref="InRealOrder"/>); a hull pivots on its southernmost, then
/// westernmost vertex, in a frame turned so the sweep runs west to east
/// (probe-derived, SQL Server 2025, 2026-09-28).</para>
/// <para>A gnomonic projection holds less than a hemisphere, so operands
/// reaching 89.5° or more from their common centre raise
/// <see cref="NotSupportedException"/>; a polygon larger than a hemisphere
/// is taken through its complement (see <see cref="Overlay"/>).</para>
/// </remarks>
internal sealed class SpatialGeodeticConstructive
{
    /// <summary>The largest angle from the projection centre a vertex may sit at: cos 89.5°.</summary>
    private const double MinimumCosine = 0.0087265354983739347;

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

    /// <summary>
    /// Builds the projection over the given shapes, or raises when they span
    /// too much of the globe. The plane touches at the normalized sum of the
    /// shapes' directions; when that leaves a vertex too near the horizon, it
    /// touches at the centre of the smallest cap holding them instead.
    /// </summary>
    private static SpatialGeodeticConstructive Over(params SpatialShape[] shapes)
    {
        var directions = new List<SpatialVector>();
        double x = 0, y = 0, z = 0;
        foreach (var shape in shapes)
        {
            foreach (var point in shape.Coordinates())
            {
                var direction = SpatialEllipsoid.ToCartesian(point).Normalized;
                directions.Add(direction);
                x += direction.X;
                y += direction.Y;
                z += direction.Z;
            }
        }
        var sum = new SpatialVector(x, y, z);
        if (sum.Length >= 1e-9 && Fits(sum.Normalized, directions))
            return new SpatialGeodeticConstructive(sum.Normalized);
        var centre = sum.Length >= 1e-9 ? sum.Normalized : directions[0];
        // Bădoiu–Clarkson: step toward the farthest direction by a shrinking
        // share, which converges on the smallest enclosing cap's centre.
        for (var round = 1; round <= 2000; round++)
        {
            var farthest = directions[0];
            foreach (var direction in directions)
            {
                if (direction.Dot(centre) < farthest.Dot(centre))
                    farthest = direction;
            }
            var next = centre + ((farthest - centre) * (1.0 / (round + 1)));
            if (next.Length < 1e-12)
                break;
            centre = next.Normalized;
        }
        return Fits(centre, directions) ? new SpatialGeodeticConstructive(centre) : throw TooWide();
    }

    private static bool Fits(SpatialVector centre, List<SpatialVector> directions)
    {
        foreach (var direction in directions)
        {
            if (direction.Dot(centre) < MinimumCosine)
                return false;
        }
        return true;
    }

    private static NotSupportedException TooWide() =>
        new("geography constructive operations over instances reaching 89.5° or more from their common centre are not modeled.");

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

    /// <summary>
    /// One of the four set operations between two round-earth instances. A
    /// polygon naming more than a hemisphere is the complement of the small
    /// polygons its reversed rings enclose, and the operation is rewritten
    /// over that complement — <c>¬X ∩ B = B − X</c>, <c>¬X ∪ B = ¬(X − B)</c>
    /// and so on — so every projection holds only small regions.
    /// </summary>
    public static SpatialShape Overlay(SpatialOverlayOperation operation, SpatialShape a, SpatialShape b)
    {
        if (a.IsEmpty && b.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var x = Complement(a);
        var y = Complement(b);
        if (x is null && y is null)
            return Direct(operation, a, b);
        if (x is not null && y is not null)
        {
            return operation switch
            {
                SpatialOverlayOperation.Intersection => Negate(Direct(SpatialOverlayOperation.Union, x, y), null),
                SpatialOverlayOperation.Union => Negate(Direct(SpatialOverlayOperation.Intersection, x, y), null),
                SpatialOverlayOperation.Difference => Direct(SpatialOverlayOperation.Difference, y, x),
                _ => Direct(SpatialOverlayOperation.SymmetricDifference, x, y),
            };
        }
        // One side is the complement of a small region; the other stands as written.
        var small = x ?? y!;
        var other = x is null ? a : b;
        var complementFirst = x is not null;
        var (areal, lower) = Split(other);
        SpatialShape? Inside() => lower is null ? null : Direct(SpatialOverlayOperation.Intersection, lower, small);
        return operation switch
        {
            SpatialOverlayOperation.Intersection => Direct(SpatialOverlayOperation.Difference, other, small),
            // The other side's lines node the complement's rings where they
            // cross, as real's do, so the small region is always overlaid with
            // the whole of it and only the areas are negated.
            SpatialOverlayOperation.Union => Negate(Direct(SpatialOverlayOperation.Difference, small, other), InsideUncovered()),
            SpatialOverlayOperation.Difference when complementFirst => Negate(Direct(SpatialOverlayOperation.Union, small, other), null),
            SpatialOverlayOperation.Difference => Direct(SpatialOverlayOperation.Intersection, other, small),
            _ => Negate(areal is null ? Direct(SpatialOverlayOperation.Union, small, other) : Direct(SpatialOverlayOperation.SymmetricDifference, small, other), Inside()),
        };

        SpatialShape? InsideUncovered() =>
            Inside() is { } inside && areal is not null ? Direct(SpatialOverlayOperation.Difference, inside, areal) : Inside();
    }

    /// <summary>The small region a polygon larger than a hemisphere leaves out, or null for any other instance.</summary>
    private static SpatialShape? Complement(SpatialShape shape)
    {
        if (shape.Type != SpatialShapeType.Polygon || shape.IsEmpty || SpatialMeasures.GeographyArea(shape) <= SpatialEllipsoid.SurfaceArea / 2)
            return null;
        var pieces = new SpatialShape[shape.Figures.Length];
        for (var i = 0; i < pieces.Length; i++)
            pieces[i] = SpatialShape.Leaf(SpatialShapeType.Polygon, [Reversed(shape.Figures[i])]);
        return pieces.Length == 1 ? pieces[0] : SpatialShape.Collection(SpatialShapeType.MultiPolygon, pieces);
    }

    private static SpatialCoordinate[] Reversed(SpatialCoordinate[] ring)
    {
        var reversed = new SpatialCoordinate[ring.Length];
        for (var i = 0; i < ring.Length; i++)
            reversed[i] = ring[ring.Length - 1 - i];
        return reversed;
    }

    /// <summary>An instance's polygons and its points and lines, each as one shape or null when there are none.</summary>
    private static (SpatialShape? Areal, SpatialShape? Lower) Split(SpatialShape shape)
    {
        var polygons = new List<SpatialShape>();
        var others = new List<SpatialShape>();
        void Walk(SpatialShape part)
        {
            switch (part.Type)
            {
                case SpatialShapeType.Polygon:
                    if (!part.IsEmpty)
                        polygons.Add(part);
                    break;
                case SpatialShapeType.Point:
                case SpatialShapeType.LineString:
                    if (!part.IsEmpty)
                        others.Add(part);
                    break;
                default:
                    foreach (var child in part.Children)
                        Walk(child);
                    break;
            }
        }
        Walk(shape);
        return (
            polygons.Count == 0 ? null : SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. polygons]),
            others.Count == 0 ? null : SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. others]));
    }

    /// <summary>
    /// The complement of a result's areas — one polygon whose rings are the
    /// areas' shells reversed, plus each hole as a polygon of its own — with
    /// <paramref name="extra"/>'s points and lines alongside.
    /// </summary>
    private static SpatialShape Negate(SpatialShape areas, SpatialShape? extra)
    {
        var (areal, _) = Split(areas);
        if (areal is null)
            throw new NotSupportedException("geography results covering the whole globe (FULLGLOBE) are not modeled.");
        var shells = new List<SpatialCoordinate[]>();
        var holes = new List<SpatialShape>();
        foreach (var polygon in areal.Children)
        {
            shells.Add(Reversed(polygon.Figures[0]));
            for (var i = 1; i < polygon.Figures.Length; i++)
                holes.Add(SpatialShape.Leaf(SpatialShapeType.Polygon, [Reversed(polygon.Figures[i])]));
        }
        var outside = SpatialShape.Leaf(SpatialShapeType.Polygon, [.. shells]);
        var result = holes.Count == 0 ? outside : SpatialShape.Collection(SpatialShapeType.MultiPolygon, [outside, .. holes]);
        if (extra is null || extra.IsEmpty)
            return result;
        var (_, lower) = Split(extra);
        return lower is null ? result : SpatialShape.Collection(SpatialShapeType.GeometryCollection, [result, .. lower.Children]);
    }

    /// <summary>
    /// The union of many small pieces in one projection — a buffer's bands,
    /// fans and caps — folded pairwise by the planar engine so every crossing
    /// is computed in the one frame rather than re-projected at each step.
    /// </summary>
    public static SpatialShape UnionAll(List<SpatialShape> pieces)
    {
        if (pieces.Count == 0)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var projection = Over([.. pieces]);
        var level = new List<SpatialShape>(pieces.Count);
        foreach (var piece in pieces)
            level.Add(projection.Map(piece, forward: true));
        while (level.Count > 1)
        {
            var next = new List<SpatialShape>((level.Count + 1) / 2);
            for (var i = 0; i + 1 < level.Count; i += 2)
                next.Add(SpatialConstructive.Overlay(SpatialOverlayOperation.Union, level[i], level[i + 1]));
            if (level.Count % 2 == 1)
                next.Add(level[^1]);
            level = next;
        }
        return InRealOrder(projection.Map(SpatialConstructive.Overlay(SpatialOverlayOperation.Union, level[0], SpatialShape.Empty(SpatialShapeType.GeometryCollection)), forward: false));
    }

    /// <summary>A set operation between two instances that each fit one projection.</summary>
    private static SpatialShape Direct(SpatialOverlayOperation operation, SpatialShape a, SpatialShape b)
    {
        if (a.IsEmpty && b.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var projection = Over(a, b);
        var result = SpatialConstructive.Overlay(operation, projection.Map(a, forward: true), projection.Map(b, forward: true));
        return InRealOrder(projection.Map(result, forward: false));
    }

    /// <summary>
    /// Rewrites a result in real's output order: the sweep of the gnomonic
    /// plane at the result's centre whose primary axis is the Earth-centred y
    /// axis, pointing along it for a result centred north of the equator and
    /// against it otherwise, so a ring starts at the vertex least (or most)
    /// along that axis — its westernmost near the prime meridian in the north,
    /// its northernmost at longitude 90° (probed 2026-09-29 against SQL Server
    /// 2025). The pass re-reads the finished result through the planar engine
    /// in that frame and maps each vertex back by lookup, and a collection's
    /// points and lines then precede its polygons, as real's usually do.
    /// </summary>
    private static SpatialShape InRealOrder(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return shape;
        double x = 0, y = 0, z = 0;
        foreach (var point in shape.Coordinates())
        {
            var direction = Spherical(point);
            x += direction.X;
            y += direction.Y;
            z += direction.Z;
        }
        var sum = new SpatialVector(x, y, z);
        if (sum.Length < 1e-9)
            return shape;
        var centre = sum.Normalized;
        var sign = centre.Z > 0 ? 1.0 : -1.0;
        var axis = new SpatialVector(0, 1, 0);
        var up = (axis - (centre * axis.Dot(centre))) * sign;
        if (up.Length < 1e-9)
            return shape;
        up = up.Normalized;
        var across = up.Cross(centre);
        var originals = new Dictionary<(double X, double Y), SpatialCoordinate>();
        SpatialCoordinate Forward(SpatialCoordinate point)
        {
            var direction = Spherical(point);
            var scale = 1 / direction.Dot(centre);
            var projected = new SpatialCoordinate(direction.Dot(across) * scale, direction.Dot(up) * scale);
            _ = originals.TryAdd((projected.X, projected.Y), new SpatialCoordinate(point.X, point.Y));
            return projected;
        }
        var moved = false;
        SpatialCoordinate Back(SpatialCoordinate point)
        {
            if (originals.TryGetValue((point.X, point.Y), out var original))
                return original;
            moved = true;
            return point;
        }
        var planar = Rewrite(shape, Forward);
        var ordered = Rewrite(SpatialConstructive.Overlay(SpatialOverlayOperation.Union, planar, SpatialShape.Empty(SpatialShapeType.GeometryCollection)), Back);
        if (!moved)
            return LowerDimensionsFirst(ordered);
        // Two vertices a hair apart can meet on the re-read's grid; the plain
        // turned frame, which maps back by arithmetic, orders those instead.
        var turned = Turn(shape, forward: true);
        return Turn(SpatialConstructive.Overlay(SpatialOverlayOperation.Union, turned, SpatialShape.Empty(SpatialShapeType.GeometryCollection)), forward: false);
    }

    /// <summary>
    /// A collection's points and lines ahead of its polygons, each group kept
    /// in sweep order — real's usual arrangement of a mixed result.
    /// </summary>
    private static SpatialShape LowerDimensionsFirst(SpatialShape shape)
    {
        if (shape.Type != SpatialShapeType.GeometryCollection)
            return shape;
        var ordered = new List<SpatialShape>(shape.Children.Length);
        foreach (var child in shape.Children)
        {
            if (child.Type is not (SpatialShapeType.Polygon or SpatialShapeType.MultiPolygon))
                ordered.Add(child);
        }
        foreach (var child in shape.Children)
        {
            if (child.Type is SpatialShapeType.Polygon or SpatialShapeType.MultiPolygon)
                ordered.Add(child);
        }
        return SpatialShape.Collection(SpatialShapeType.GeometryCollection, [.. ordered]);
    }

    private static SpatialVector Spherical(SpatialCoordinate point)
    {
        var lat = point.Y * SpatialEllipsoid.RadiansPerDegree;
        var lon = point.X * SpatialEllipsoid.RadiansPerDegree;
        return new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    private static SpatialShape Rewrite(SpatialShape shape, Func<SpatialCoordinate, SpatialCoordinate> map)
    {
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < figures.Length; i++)
        {
            var figure = shape.Figures[i];
            var mapped = new SpatialCoordinate[figure.Length];
            for (var j = 0; j < figure.Length; j++)
                mapped[j] = map(figure[j]);
            figures[i] = mapped;
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = Rewrite(shape.Children[i], map);
        return new SpatialShape(shape.Type, figures, children);
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
