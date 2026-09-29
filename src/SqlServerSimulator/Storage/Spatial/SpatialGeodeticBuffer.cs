namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>geography</c>'s <c>STBuffer</c> / <c>BufferWithTolerance</c>.
/// </summary>
/// <remarks>
/// <para>Real builds a point's buffer as <c>BufferWithCurves</c> does — a
/// curve polygon of two half circles through four control points — and then
/// linearizes each half as a <see cref="SpatialGeodeticArc"/> (probed
/// 2026-09-29 against SQL Server 2025). The control points sit at 45°, 135°,
/// 225° and 315° of a circle drawn on the unit sphere around the point,
/// reading latitude as a spherical angle, each at its own angle from the
/// point so it lies the distance away along the great elliptic arc; the ring
/// starts at the north-east one and runs counter-clockwise, and the first half
/// ends at the south-west one. Several points buffer separately and
/// union.</para>
/// <para>Each half takes the same power-of-two count of steps, the larger of
/// two counts. One follows the tolerance: the fewest steps whose deviation
/// estimate <c>K·d·θ²</c> stays within it, where <c>K</c> is 0.13866 at the
/// equator and grows as <c>(1 - e²sin²φ)^-1.5</c> toward the poles, and no
/// more than 64 however small the tolerance. The other follows the distance
/// alone: the fewest whose estimate <c>|sin 2ρ|/2·(1 - e²sin²φ)^1.5·θ²/8</c>
/// stays within 1e-6, with <c>ρ</c> the distance in radians of the major
/// semi-axis — which is what gives a 100 km buffer 512 sides and a
/// 10,000 km one, whose <c>ρ</c> sits at a right angle, only 128. Both were
/// fitted to real's step-count thresholds, bisected to twelve digits, to
/// within 3e-4 of the threshold distance.</para>
/// <para>A line or polygon is the union of the pieces it sweeps — bands,
/// turn arcs, caps and the polygon itself — taken through one projection.
/// Real's own outline for those comes from a curve construction whose side
/// and ring-start rules the probes don't pin down, so it matches real's area
/// rather than its vertices.</para>
/// </remarks>
internal static class SpatialGeodeticBuffer
{
    /// <summary>A distance past which real refuses the buffer as exceeding the full globe (Msg 6522 carrying 24207), bisected.</summary>
    private const double GlobeLimit = 19883466.2745;

    /// <summary>The tolerance estimate's coefficient at the equator, fitted to real's step-count thresholds.</summary>
    private const double ToleranceCoefficient = 0.1386560523;

    /// <summary>The largest angle a control point may sit from the point, beyond which the linearization's projection fails.</summary>
    private const double MaximumAngle = 80 * Math.PI / 180;

    public static SpatialShape Buffer(SpatialShape shape, double distance, double tolerance, bool relative)
    {
        if (distance > GlobeLimit)
            throw SimulatedSqlException.SpatialBufferExceedsGlobe();
        if (distance == 0)
            return shape;
        if (shape.IsEmpty || double.IsNaN(distance))
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var points = new List<SpatialCoordinate>();
        var lines = new List<List<SpatialCoordinate>>();
        var polygons = new List<SpatialShape>();
        Collect(shape, points, lines, polygons);
        if (distance < 0)
            return polygons.Count == 0 ? SpatialShape.Empty(SpatialShapeType.GeometryCollection) : Erode(polygons, -distance, tolerance, relative);
        if (lines.Count == 0 && polygons.Count == 0)
        {
            var result = Circle(points[0], distance, tolerance, relative);
            for (var i = 1; i < points.Count; i++)
                result = SpatialGeodeticConstructive.Overlay(SpatialOverlayOperation.Union, result, Circle(points[i], distance, tolerance, relative));
            return result;
        }
        var pieces = new List<SpatialShape>(polygons);
        foreach (var point in points)
            pieces.Add(Circle(point, distance, tolerance, relative));
        foreach (var line in lines)
            Sweep(line, closed: false, distance, tolerance, relative, pieces);
        foreach (var polygon in polygons)
        {
            foreach (var ring in polygon.Figures)
                Sweep(Distinct(ring, closed: true), closed: true, distance, tolerance, relative, pieces);
        }
        return Valid(UnionAll(pieces), distance);
    }

    /// <summary>
    /// Pieces meeting at a vertex can leave a sliver of a hole where their
    /// snapped edges part by a grid step; a hole far smaller than the
    /// buffer's own reach is dropped, and anything still invalid is rebuilt.
    /// </summary>
    private static SpatialShape Valid(SpatialShape shape, double distance)
    {
        if (SpatialGeodeticValidator.IsValid(shape))
            return shape;
        var trimmed = WithoutSlivers(shape, distance * distance * 1e-3);
        return SpatialGeodeticValidator.IsValid(trimmed) ? trimmed : SpatialGeodeticConstructive.MakeValid(trimmed);
    }

    private static SpatialShape WithoutSlivers(SpatialShape shape, double smallest)
    {
        if (shape.Type == SpatialShapeType.Polygon)
        {
            var rings = new List<SpatialCoordinate[]> { shape.Figures[0] };
            for (var i = 1; i < shape.Figures.Length; i++)
            {
                // A sliver's area is near zero read either way round.
                var hole = SpatialShape.Leaf(SpatialShapeType.Polygon, [shape.Figures[i]]);
                var reversed = SpatialShape.Leaf(SpatialShapeType.Polygon, [[.. shape.Figures[i].Reverse()]]);
                if (Math.Min(SpatialMeasures.GeographyArea(hole), SpatialMeasures.GeographyArea(reversed)) >= smallest)
                    rings.Add(shape.Figures[i]);
            }
            return SpatialShape.Leaf(SpatialShapeType.Polygon, [.. rings]);
        }
        if (shape.Children.Length == 0)
            return shape;
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = WithoutSlivers(shape.Children[i], smallest);
        return SpatialShape.Collection(shape.Type, children);
    }

    /// <summary>A negative buffer: each polygon less the band its rings sweep.</summary>
    private static SpatialShape Erode(List<SpatialShape> polygons, double distance, double tolerance, bool relative)
    {
        var bands = new List<SpatialShape>();
        foreach (var polygon in polygons)
        {
            foreach (var ring in polygon.Figures)
                Sweep(Distinct(ring, closed: true), closed: true, distance, tolerance, relative, bands);
        }
        var area = UnionAll(polygons);
        return bands.Count == 0 ? area : Valid(SpatialGeodeticConstructive.Overlay(SpatialOverlayOperation.Difference, area, UnionAll(bands)), distance);
    }

    private static SpatialShape UnionAll(List<SpatialShape> pieces) => SpatialGeodeticConstructive.UnionAll(pieces);

    private static void Collect(SpatialShape shape, List<SpatialCoordinate> points, List<List<SpatialCoordinate>> lines, List<SpatialShape> polygons)
    {
        if (shape.IsEmpty)
            return;
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                foreach (var figure in shape.Figures)
                    points.AddRange(figure);
                break;
            case SpatialShapeType.LineString:
                foreach (var figure in shape.Figures)
                {
                    var line = Distinct(figure, closed: false);
                    if (line.Count == 1)
                        points.Add(line[0]);
                    else
                        lines.Add(line);
                }
                break;
            case SpatialShapeType.Polygon:
                polygons.Add(shape);
                break;
            default:
                foreach (var child in shape.Children)
                    Collect(child, points, lines, polygons);
                break;
        }
    }

    /// <summary>A figure without consecutive repeats, and without a ring's closing repeat.</summary>
    private static List<SpatialCoordinate> Distinct(SpatialCoordinate[] figure, bool closed)
    {
        var points = new List<SpatialCoordinate>(figure.Length);
        foreach (var point in figure)
        {
            if (points.Count == 0 || points[^1].X != point.X || points[^1].Y != point.Y)
                points.Add(new SpatialCoordinate(point.X, point.Y));
        }
        if (closed && points.Count > 1 && points[0].X == points[^1].X && points[0].Y == points[^1].Y)
            points.RemoveAt(points.Count - 1);
        return points;
    }

    /// <summary>
    /// The pieces a line or ring sweeps: a band either side of each edge,
    /// bounded by the edge's offsets at its two ends, an arc filling each
    /// turn on its outer side, and at a line's free ends a half-circle cap.
    /// </summary>
    private static void Sweep(List<SpatialCoordinate> points, bool closed, double distance, double tolerance, bool relative, List<SpatialShape> pieces)
    {
        if (points.Count == 1)
        {
            pieces.Add(Circle(points[0], distance, tolerance, relative));
            return;
        }
        if (points.Count == 2 && closed)
            closed = false;
        var count = closed ? points.Count : points.Count - 1;
        var frames = new LocalFrame[points.Count];
        for (var i = 0; i < frames.Length; i++)
            frames[i] = new LocalFrame(points[i], distance);

        // Each edge's offsets at both ends, computed once so the band and the
        // fans that meet it share those corners exactly.
        var leaving = new double[count];
        var arriving = new double[count];
        var starts = new (SpatialCoordinate Right, SpatialCoordinate Left)[count];
        var ends = new (SpatialCoordinate Right, SpatialCoordinate Left)[count];
        for (var i = 0; i < count; i++)
        {
            var a = frames[i];
            var b = frames[(i + 1) % points.Count];
            leaving[i] = a.BearingTo(b.Origin);
            arriving[i] = b.BearingFrom(a.Origin);
            starts[i] = (a.Destination(leaving[i] - (Math.PI / 2)), a.Destination(leaving[i] + (Math.PI / 2)));
            ends[i] = (b.Destination(arriving[i] - (Math.PI / 2)), b.Destination(arriving[i] + (Math.PI / 2)));

            // A side is the curve at the distance from the edge, which bows
            // toward the edge off the chord joining its ends by about
            // L²·d / 8R²; it is stepped where that exceeds the tolerance.
            var length = SpatialGreatElliptic.Distance(a.Origin, b.Origin);
            var bow = length * length * distance / (8 * SpatialEllipsoid.SemiMajor * SpatialEllipsoid.SemiMajor);
            var allowance = relative ? tolerance * distance : tolerance;
            var sideSteps = 1;
            while (sideSteps < 1 << 12 && bow / (sideSteps * sideSteps) > allowance)
                sideSteps *= 2;
            var right = new List<SpatialCoordinate> { starts[i].Right };
            var left = new List<SpatialCoordinate> { ends[i].Left };
            for (var k = 1; k < sideSteps; k++)
            {
                var along = new LocalFrame(Along(a.Origin, b.Origin, (double)k / sideSteps), distance);
                var heading = along.BearingTo(b.Origin);
                right.Add(along.Destination(heading - (Math.PI / 2)));
            }
            for (var k = sideSteps - 1; k >= 1; k--)
            {
                var along = new LocalFrame(Along(a.Origin, b.Origin, (double)k / sideSteps), distance);
                var heading = along.BearingTo(b.Origin);
                left.Add(along.Destination(heading + (Math.PI / 2)));
            }
            // The edge's own ends sit on the band's short sides, so the fans
            // meeting the band there share those edges exactly rather than a
            // near-collinear copy that would leave a sliver between them.
            var ring = new List<SpatialCoordinate>(right) { ends[i].Right, b.Origin };
            ring.AddRange(left);
            ring.Add(starts[i].Left);
            ring.Add(a.Origin);
            ring.Add(starts[i].Right);
            pieces.Add(SpatialShape.Leaf(SpatialShapeType.Polygon, [[.. ring]]));
        }
        var steps = Steps(points[0].Y, distance, tolerance, relative);
        for (var i = closed ? 0 : 1; i < (closed ? points.Count : points.Count - 1); i++)
        {
            var before = (i - 1 + count) % count;
            var turn = Math.IEEERemainder(leaving[i] - arriving[before], 2 * Math.PI);
            if (Math.Abs(turn) < 1e-12)
                continue;
            // A left turn opens the right side and a right turn the left.
            pieces.Add(turn > 0
                ? Fan(frames[i], ends[before].Right, arriving[before] - (Math.PI / 2) + (turn / 2), starts[i].Right, turn, steps)
                : Fan(frames[i], ends[before].Left, arriving[before] + (Math.PI / 2) + (turn / 2), starts[i].Left, turn, steps));
        }
        if (!closed)
        {
            var last = count - 1;
            pieces.Add(Fan(frames[^1], ends[last].Right, arriving[last], ends[last].Left, Math.PI, steps));
            pieces.Add(Fan(frames[0], starts[0].Left, leaving[0] + Math.PI, starts[0].Right, Math.PI, steps));
        }
    }

    /// <summary>The point a fraction of the way along the arc between two points, on the unit sphere.</summary>
    private static SpatialCoordinate Along(SpatialCoordinate from, SpatialCoordinate to, double fraction)
    {
        static SpatialVector Unit(SpatialCoordinate point)
        {
            var lat = point.Y * SpatialEllipsoid.RadiansPerDegree;
            var lon = point.X * SpatialEllipsoid.RadiansPerDegree;
            return new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        }
        var a = Unit(from);
        var b = Unit(to);
        var angle = Math.Acos(Math.Clamp(a.Dot(b), -1, 1));
        var sine = Math.Sin(angle);
        var point = sine < 1e-15 ? a : (a * (Math.Sin((1 - fraction) * angle) / sine)) + (b * (Math.Sin(fraction * angle) / sine));
        return new SpatialCoordinate(
            Math.Atan2(point.Y, point.X) * SpatialEllipsoid.DegreesPerRadian,
            Math.Atan2(point.Z, point.AxialRadius) * SpatialEllipsoid.DegreesPerRadian);
    }

    /// <summary>
    /// The sector of a buffer's circle about a frame's point, from
    /// <paramref name="from"/> through the point on <paramref name="middle"/>'s
    /// bearing to <paramref name="to"/>, turning through
    /// <paramref name="sweep"/> (counter-clockwise when positive); its arc is
    /// linearized as real linearizes a circular arc, at the step density of a
    /// half circle.
    /// </summary>
    private static SpatialShape Fan(LocalFrame frame, SpatialCoordinate from, double middle, SpatialCoordinate to, double sweep, int halfSteps)
    {
        var steps = 1;
        while (steps < halfSteps && Math.PI / halfSteps * steps < Math.Abs(sweep))
            steps *= 2;
        steps = Math.Max(steps, 2);
        var arc = SpatialGeodeticArc.Linearize(from, frame.Destination(middle), to, steps);
        var ring = new SpatialCoordinate[arc.Length + 2];
        ring[0] = frame.Origin;
        arc.CopyTo(ring, 1);
        ring[^1] = frame.Origin;
        if (sweep < 0)
            Array.Reverse(ring);
        return SpatialShape.Leaf(SpatialShapeType.Polygon, [ring]);
    }

    /// <summary>The power-of-two step count for each half of a point's buffer.</summary>
    private static int Steps(double latitude, double distance, double tolerance, bool relative)
    {
        var sine = Math.Sin(latitude * SpatialEllipsoid.RadiansPerDegree);
        var shrink = 1 - (SpatialEllipsoid.EccentricitySquared * sine * sine);
        var shrinkPower = shrink * Math.Sqrt(shrink);
        var allowance = relative ? tolerance * distance : tolerance;
        var coefficient = ToleranceCoefficient / shrinkPower;
        var byTolerance = 1;
        while (byTolerance < 64 && coefficient * distance * Square(Math.PI / byTolerance) > allowance)
            byTolerance *= 2;
        var angle = distance / SpatialEllipsoid.SemiMajor;
        var reach = Math.Abs(Math.Sin(angle) * Math.Cos(angle)) * shrinkPower;
        var byDistance = 1;
        while (byDistance < 1 << 16 && reach * Square(Math.PI / byDistance) / 8 > 1e-6)
            byDistance *= 2;
        return Math.Max(byTolerance, byDistance);
    }

    private static double Square(double value) => value * value;

    /// <summary>
    /// <c>BufferWithCurves</c> of a point: the curve polygon real linearizes
    /// for <c>STBuffer</c> — two half circles through the control points at
    /// 45°, 135°, 225° and 315° about the point, starting at the north-east one.
    /// </summary>
    public static SpatialShape CurveCircle(SpatialCoordinate point, double distance)
    {
        var frame = new LocalFrame(point, distance) { Limit = Math.PI / 2 };
        var northern = frame.Reach(Math.PI / 4);
        var southern = frame.Reach(5 * Math.PI / 4);
        SpatialCoordinate[] ring =
        [
            frame.At(northern, Math.PI / 4), frame.At(northern, 3 * Math.PI / 4),
            frame.At(southern, 5 * Math.PI / 4), frame.At(southern, 7 * Math.PI / 4), frame.At(northern, Math.PI / 4),
        ];
        return SpatialShape.Curve(SpatialShapeType.CurvePolygon, [ring], [SpatialFigureType.Arc], [null]);
    }

    private static SpatialShape Circle(SpatialCoordinate point, double distance, double tolerance, bool relative)
    {
        var frame = new LocalFrame(point, distance);
        var northern = frame.Reach(Math.PI / 4);
        var southern = frame.Reach(5 * Math.PI / 4);
        var steps = Steps(point.Y, distance, tolerance, relative);
        var northEast = frame.At(northern, Math.PI / 4);
        var northWest = frame.At(northern, 3 * Math.PI / 4);
        var southWest = frame.At(southern, 5 * Math.PI / 4);
        var southEast = frame.At(southern, 7 * Math.PI / 4);
        if (steps == 1)
        {
            // A single step a half leaves a ring retracing one chord, which
            // real writes as that chord.
            return SpatialShape.Leaf(SpatialShapeType.LineString, [[northEast, southWest]]);
        }
        var first = SpatialGeodeticArc.Linearize(northEast, northWest, southWest, steps);
        var second = SpatialGeodeticArc.Linearize(southWest, southEast, northEast, steps);
        var ring = new SpatialCoordinate[first.Length + second.Length - 1];
        first.CopyTo(ring, 0);
        Array.Copy(second, 1, ring, first.Length, second.Length - 1);
        return SpatialShape.Leaf(SpatialShapeType.Polygon, [ring]);
    }

    /// <summary>
    /// The unit sphere's tangent frame at a point, reading latitude as a
    /// spherical angle — the frame real's buffer control points and round-earth
    /// envelope use — with bearings counter-clockwise from east.
    /// </summary>
    private sealed class LocalFrame
    {
        public readonly SpatialCoordinate Origin;
        private readonly SpatialVector center;
        private readonly SpatialVector east;
        private readonly SpatialVector north;
        private readonly SpatialShape origin;
        private readonly double distance;

        public LocalFrame(SpatialCoordinate point, double distance)
        {
            this.Origin = new SpatialCoordinate(point.X, point.Y);
            this.center = Unit(point);
            var east = new SpatialVector(0, 0, 1).Cross(this.center);
            this.east = east.Length < 1e-12 ? new SpatialVector(0, 1, 0) : east.Normalized;
            this.north = this.center.Cross(this.east).Normalized;
            this.origin = SpatialShape.Leaf(SpatialShapeType.Point, [[this.Origin]]);
            this.distance = distance;
        }

        private static SpatialVector Unit(SpatialCoordinate point)
        {
            var lat = point.Y * SpatialEllipsoid.RadiansPerDegree;
            var lon = point.X * SpatialEllipsoid.RadiansPerDegree;
            return new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
        }

        private const double ScaledReachBelow = 1000;

        /// <summary>The widest reach this frame answers before refusing it as unmodeled.</summary>
        public double Limit = MaximumAngle;

        public SpatialCoordinate At(double angle, double bearing)
        {
            var direction = (this.center * Math.Cos(angle)) + (((this.east * Math.Cos(bearing)) + (this.north * Math.Sin(bearing))) * Math.Sin(angle));
            return new SpatialCoordinate(
                Math.Atan2(direction.Y, direction.X) * SpatialEllipsoid.DegreesPerRadian,
                Math.Atan2(direction.Z, direction.AxialRadius) * SpatialEllipsoid.DegreesPerRadian);
        }

        /// <summary>
        /// The angle at which a point on the given bearing lies the distance
        /// away along the great elliptic arc; the ellipsoid makes it differ by
        /// bearing.
        /// </summary>
        public double Reach(double bearing)
        {
            // At small distances the distance's own rounding swamps the search;
            // the reach is proportional to the distance there, so it scales
            // from a kilometre's.
            if (this.distance < ScaledReachBelow)
                return new LocalFrame(this.Origin, ScaledReachBelow) { Limit = this.Limit }.Reach(bearing) * (this.distance / ScaledReachBelow);

            double low = 0, high = Math.Min(this.distance / SpatialEllipsoid.SemiMinor * 2, Math.PI);
            for (var i = 0; i < 200 && high - low > high * 1e-16; i++)
            {
                var mid = (low + high) / 2;
                var probe = SpatialShape.Leaf(SpatialShapeType.Point, [[this.At(mid, bearing)]]);
                if (SpatialMeasures.GeographyDistance(this.origin, probe) < this.distance)
                    low = mid;
                else
                    high = mid;
            }
            var reach = (low + high) / 2;
            return reach > this.Limit
                ? throw new NotSupportedException("geography '.STBuffer' reaching 80° or more from the instance is not modeled.")
                : reach;
        }

        /// <summary>The point the distance away on the given bearing.</summary>
        public SpatialCoordinate Destination(double bearing) => this.At(this.Reach(bearing), bearing);

        /// <summary>The bearing at which the arc toward <paramref name="point"/> leaves this frame's point.</summary>
        public double BearingTo(SpatialCoordinate point)
        {
            var target = Unit(point);
            var along = target - (this.center * target.Dot(this.center));
            return Math.Atan2(along.Dot(this.north), along.Dot(this.east));
        }

        /// <summary>The bearing at which the arc from <paramref name="point"/> arrives at this frame's point.</summary>
        public double BearingFrom(SpatialCoordinate point) => this.BearingTo(point) + Math.PI;
    }
}

/// <summary>
/// The linearization of a <c>geography</c> circular arc — the curve a
/// <c>CIRCULARSTRING</c> names through three points, and the one real's
/// buffers are drawn with.
/// </summary>
/// <remarks>
/// Real's arc is a circle in a gnomonic plane, stepped at equal angles about
/// its centre, and the plane is identified from the linearized points
/// (probed 2026-09-29 against SQL Server 2025, 1e-10° agreement on arcs up
/// to 40° across): points are read as the directions of their geocentric
/// positions, and the plane touches the unit sphere at the direction whose
/// geocentric latitude has the tangent <c>tan L / (1 - e²)</c>, where <c>L</c>
/// is the latitude of the circumcentre of the three points read on the unit
/// sphere with latitude as a spherical angle. The circle is the one through
/// the three projected points, and a point maps back to the geodetic
/// latitude of its direction.
/// </remarks>
internal static class SpatialGeodeticArc
{
    /// <summary>
    /// The arc from <paramref name="start"/> through <paramref name="middle"/>
    /// to <paramref name="end"/> in <paramref name="steps"/> equal steps, its
    /// ends exactly as given.
    /// </summary>
    public static SpatialCoordinate[] Linearize(SpatialCoordinate start, SpatialCoordinate middle, SpatialCoordinate end, int steps)
    {
        var frame = new Frame(start, middle, end);
        var points = new SpatialCoordinate[steps + 1];
        points[0] = new SpatialCoordinate(start.X, start.Y);
        for (var i = 1; i < steps; i++)
            points[i] = frame.At(frame.From + (i * frame.Sweep / steps));
        points[steps] = new SpatialCoordinate(end.X, end.Y);
        return points;
    }

    /// <summary>
    /// The arc's radius in metres — its radius on the gnomonic plane, which
    /// touches the unit sphere, scaled by the semi-major axis — and the angle
    /// it sweeps about its centre there.
    /// </summary>
    public static (double Radius, double Sweep) Measure(SpatialCoordinate start, SpatialCoordinate middle, SpatialCoordinate end)
    {
        var frame = new Frame(start, middle, end);
        return (frame.Radius * SpatialEllipsoid.SemiMajor, Math.Abs(frame.Sweep));
    }

    /// <summary>The gnomonic plane an arc is drawn in, and the circle it is on there.</summary>
    private readonly struct Frame
    {
        private readonly SpatialVector axis;
        private readonly SpatialVector east;
        private readonly SpatialVector north;
        private readonly double centerX;
        private readonly double centerY;
        public readonly double Radius;
        public readonly double From;
        public readonly double Sweep;

        public Frame(SpatialCoordinate start, SpatialCoordinate middle, SpatialCoordinate end)
        {
            var a = Spherical(start);
            var b = Spherical(middle);
            var c = Spherical(end);
            var ab = b - a;
            var ac = c - a;
            var normal = ab.Cross(ac);
            var circumcentre = a + (((normal.Cross(ab) * ac.SquaredLength) + (ac.Cross(normal) * ab.SquaredLength)) * (1 / (2 * normal.SquaredLength)));
            this.axis = new SpatialVector(circumcentre.X, circumcentre.Y, circumcentre.Z / (1 - SpatialEllipsoid.EccentricitySquared)).Normalized;
            var east = new SpatialVector(0, 0, 1).Cross(this.axis);
            this.east = east.Length < 1e-12 ? new SpatialVector(0, 1, 0) : east.Normalized;
            this.north = this.axis.Cross(this.east);

            var (ax, ay) = this.Project(start);
            var (bx, by) = this.Project(middle);
            var (cx, cy) = this.Project(end);
            var d = 2 * ((ax * (by - cy)) + (bx * (cy - ay)) + (cx * (ay - by)));
            var aa = (ax * ax) + (ay * ay);
            var bb = (bx * bx) + (by * by);
            var cc = (cx * cx) + (cy * cy);
            this.centerX = ((aa * (by - cy)) + (bb * (cy - ay)) + (cc * (ay - by))) / d;
            this.centerY = ((aa * (cx - bx)) + (bb * (ax - cx)) + (cc * (bx - ax))) / d;
            this.Radius = Math.Sqrt(((ax - this.centerX) * (ax - this.centerX)) + ((ay - this.centerY) * (ay - this.centerY)));
            this.From = Math.Atan2(ay - this.centerY, ax - this.centerX);
            var to = Math.Atan2(cy - this.centerY, cx - this.centerX);
            var counterClockwise = ((bx - ax) * (cy - ay)) - ((by - ay) * (cx - ax)) > 0;
            this.Sweep = counterClockwise ? Wrap(to - this.From) : -Wrap(this.From - to);
        }

        private (double X, double Y) Project(SpatialCoordinate point)
        {
            var direction = Geocentric(point);
            var scale = 1 / direction.Dot(this.axis);
            return (direction.Dot(this.east) * scale, direction.Dot(this.north) * scale);
        }

        /// <summary>The point on the circle at <paramref name="angle"/> about its centre.</summary>
        public SpatialCoordinate At(double angle)
        {
            var x = this.centerX + (this.Radius * Math.Cos(angle));
            var y = this.centerY + (this.Radius * Math.Sin(angle));
            var direction = this.axis + (this.east * x) + (this.north * y);
            return new SpatialCoordinate(
                Math.Atan2(direction.Y, direction.X) * SpatialEllipsoid.DegreesPerRadian,
                SpatialEllipsoid.GeodeticLatitude(direction) * SpatialEllipsoid.DegreesPerRadian);
        }
    }

    /// <summary>An angle folded into (0, 2π].</summary>
    private static double Wrap(double angle)
    {
        angle %= 2 * Math.PI;
        return angle <= 0 ? angle + (2 * Math.PI) : angle;
    }

    /// <summary>The unit vector reading latitude as a spherical angle.</summary>
    private static SpatialVector Spherical(SpatialCoordinate point)
    {
        var lat = point.Y * SpatialEllipsoid.RadiansPerDegree;
        var lon = point.X * SpatialEllipsoid.RadiansPerDegree;
        return new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    /// <summary>The direction of the point's position on the ellipsoid.</summary>
    private static SpatialVector Geocentric(SpatialCoordinate point)
    {
        var lat = point.Y * SpatialEllipsoid.RadiansPerDegree;
        var lon = point.X * SpatialEllipsoid.RadiansPerDegree;
        return new SpatialVector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), (1 - SpatialEllipsoid.EccentricitySquared) * Math.Sin(lat)).Normalized;
    }
}
