namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// <c>STBuffer</c> / <c>BufferWithTolerance</c> for <c>geometry</c>: the
/// instance swept by a disc, built the way real builds it — a curved outline
/// linearized to a tolerance — and assembled by the overlay engine.
/// </summary>
/// <remarks>
/// <para>Real computes <c>BufferWithCurves</c> first and linearizes it, and
/// the pieces here follow that outline (probe-derived, SQL Server 2025,
/// 2026-09-28): a point becomes four quarter arcs starting at the bottom; a
/// segment contributes the band either side of it; a vertex where a line or
/// ring turns contributes the arc between the two edges' offsets on the side
/// that opens up; and a line's free end gets a cap of two arcs meeting
/// straight ahead, each stopping a small angle δ short of the side offsets,
/// with a straight chord bridging that last gap. δ is π/512 while the
/// buffer's reach from the origin is 4 to 16 times the distance, and doubles
/// with every fourfold growth of that ratio — the curve outline's own
/// precision allowance.</para>
/// <para>Each arc is split into a power-of-two count of equal steps, the
/// fewest whose deviation estimate <c>r·θ²/8</c> — the sagitta's leading
/// term, not the sagitta itself — stays within the tolerance: every step-count
/// threshold real shows sits on that estimate to eight digits (bisected
/// 2026-09-28 against SQL Server 2025). <c>STBuffer</c> uses 0.001 of the
/// distance, which is 32 steps a quarter turn.</para>
/// <para>A negative distance erodes an area by the same pieces and yields
/// empty for a point or a line; a zero distance returns the instance as it
/// stands.</para>
/// </remarks>
internal static class SpatialBuffer
{
    public static SpatialShape Buffer(SpatialShape shape, double distance, double tolerance, bool relative)
    {
        if (distance == 0)
            return shape;
        if (shape.IsEmpty || double.IsNaN(distance))
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        var radius = Math.Abs(distance);
        var allowance = relative ? tolerance * radius : tolerance;
        var (minX, maxX, minY, maxY) = shape.IsCurved ? SpatialCurves.Extent(shape) : SpatialConstructive.Extent(shape);
        var reach = Math.Max(Math.Max(Math.Abs(minX - radius), Math.Abs(maxX + radius)), Math.Max(Math.Abs(minY - radius), Math.Abs(maxY + radius)));
        var builder = new PieceBuilder(radius, allowance, CapGap(reach / radius));
        var areas = new List<SpatialShape>();
        builder.Collect(shape, areas, distance > 0);
        if (builder.LowestArcCenter is { } center && builder.LowestArcBottom.CompareTo(builder.LowestCorner) < 0)
        {
            // The outline's lowest point lies inside an arc, and real starts
            // the curve polygon's ring there — which splits that arc in two.
            builder = new PieceBuilder(radius, allowance, CapGap(reach / radius)) { SplitCenter = center };
            areas.Clear();
            builder.Collect(shape, areas, distance > 0);
        }
        if (distance < 0 && areas.Count == 0)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);

        // Every vertex of a buffer is one the operation computed, so each
        // comes back through the grid, as real's do — an arc point landing a
        // hair off zero reads as zero.
        var grid = SpatialPrecisionGrid.Over(minX - radius, maxX + radius, minY - radius, maxY + radius);
        grid.RestoreOriginals = false;
        var swept = UnionAll(builder.Pieces, grid);
        GridGeometry result;
        if (distance > 0)
        {
            foreach (var area in areas)
                swept = Union(swept, GridOperand.From(area, grid), grid);
            result = SpatialOverlay.Overlay(swept, new GridOperand(), SpatialOverlayOperation.Union, grid);
        }
        else
        {
            var area = new GridOperand();
            foreach (var polygon in areas)
                area = Union(area, GridOperand.From(polygon, grid), grid);
            result = SpatialOverlay.Overlay(area, swept, SpatialOverlayOperation.Difference, grid);
        }
        return SpatialResultBuilder.Build(result, grid);
    }

    /// <summary>
    /// <c>BufferWithCurves</c> of a point: a curve polygon of two half circles
    /// through the bottom, right, top and left of the circle, starting at the
    /// bottom — the outline <see cref="Buffer"/> linearizes.
    /// </summary>
    public static SpatialShape CurveCircle(SpatialCoordinate point, double distance)
    {
        SpatialCoordinate[] ring =
        [
            new(point.X, point.Y - distance), new(point.X + distance, point.Y), new(point.X, point.Y + distance),
            new(point.X - distance, point.Y), new(point.X, point.Y - distance),
        ];
        return SpatialShape.Curve(SpatialShapeType.CurvePolygon, [ring], [SpatialFigureType.Arc], [null]);
    }

    /// <summary>The cap's short angle δ for a buffer reaching <paramref name="reach"/> distances from the origin.</summary>
    private static double CapGap(double reach)
    {
        var steps = Math.Floor(Math.Log2(Math.Sqrt(Math.Max(reach, double.Epsilon)) / 2));
        return Math.PI / 512 * Math.Pow(2, steps);
    }

    private static GridOperand UnionAll(List<SpatialCoordinate[]> pieces, SpatialPrecisionGrid grid)
    {
        if (pieces.Count == 0)
            return new GridOperand();
        var level = new List<GridOperand>(pieces.Count);
        foreach (var piece in pieces)
        {
            var operand = GridOperand.From(SpatialShape.Leaf(SpatialShapeType.Polygon, [piece]), grid);
            if (operand.HasArea)
                level.Add(operand);
        }
        while (level.Count > 1)
        {
            var next = new List<GridOperand>((level.Count + 1) / 2);
            for (var i = 0; i + 1 < level.Count; i += 2)
                next.Add(Union(level[i], level[i + 1], grid));
            if (level.Count % 2 == 1)
                next.Add(level[^1]);
            level = next;
        }
        return level.Count == 0 ? new GridOperand() : level[0];
    }

    private static GridOperand Union(GridOperand a, GridOperand b, SpatialPrecisionGrid grid)
    {
        var merged = SpatialOverlay.Overlay(a, b, SpatialOverlayOperation.Union, grid);
        var operand = new GridOperand();
        foreach (var polygon in merged.Polygons)
            operand.Rings.AddRange(polygon);
        return operand;
    }

    /// <summary>Collects the polygon pieces whose union is the buffer.</summary>
    private sealed class PieceBuilder(double radius, double allowance, double capGap)
    {
        public readonly List<SpatialCoordinate[]> Pieces = [];

        /// <summary>The centre of the arc to split at the outline's lowest point, once a first pass has found it.</summary>
        public SpatialCoordinate? SplitCenter;

        /// <summary>The lowest bottom of any arc that passes through its own lowest point, in sweep order, and that arc's centre.</summary>
        public (double Y, double X) LowestArcBottom = (double.PositiveInfinity, double.PositiveInfinity);

        public SpatialCoordinate? LowestArcCenter;

        /// <summary>The lowest vertex no arc produced — a band's corner, which is also where an arc meets it.</summary>
        public (double Y, double X) LowestCorner = (double.PositiveInfinity, double.PositiveInfinity);

        private void NoteCorner(SpatialCoordinate point)
        {
            if ((point.Y, point.X).CompareTo(this.LowestCorner) < 0)
                this.LowestCorner = (point.Y, point.X);
        }

        public void Collect(SpatialShape shape, List<SpatialShape> areas, bool grow)
        {
            switch (shape.Type)
            {
                case SpatialShapeType.Point:
                    foreach (var figure in shape.Figures)
                    {
                        foreach (var point in figure)
                        {
                            if (grow)
                                this.Circle(point);
                        }
                    }
                    break;
                case SpatialShapeType.LineString:
                    foreach (var figure in shape.Figures)
                    {
                        if (grow)
                            this.Line(Distinct(figure), closed: false);
                    }
                    break;
                case SpatialShapeType.Polygon:
                    if (shape.IsEmpty)
                        break;
                    areas.Add(shape);
                    foreach (var figure in shape.Figures)
                    {
                        var ring = Distinct(figure);
                        if (ring.Count > 1 && ring[0].X == ring[^1].X && ring[0].Y == ring[^1].Y)
                            ring.RemoveAt(ring.Count - 1);
                        this.Line(ring, closed: true);
                    }
                    break;
                case SpatialShapeType.CircularString:
                case SpatialShapeType.CompoundCurve:
                    if (grow && !shape.IsEmpty)
                        this.Curve(shape, 0, closed: false);
                    break;
                case SpatialShapeType.CurvePolygon:
                    if (shape.IsEmpty)
                        break;
                    // The area itself joins the pieces at the buffer's own
                    // tolerance; each ring is swept arc by arc.
                    areas.Add(SpatialCurves.Linearize(shape, SpatialCurves.Planar(arc => this.StepsAt(arc.Radius, arc.Sweep))));
                    for (var i = 0; i < shape.Figures.Length; i++)
                        this.Curve(shape, i, closed: true);
                    break;
                default:
                    foreach (var child in shape.Children)
                        this.Collect(child, areas, grow);
                    break;
            }
        }

        private static List<SpatialCoordinate> Distinct(SpatialCoordinate[] figure)
        {
            var points = new List<SpatialCoordinate>(figure.Length);
            foreach (var point in figure)
            {
                if (points.Count == 0 || points[^1].X != point.X || points[^1].Y != point.Y)
                    points.Add(new SpatialCoordinate(point.X, point.Y));
            }
            return points;
        }

        private int Steps(double sweep) => this.StepsAt(radius, sweep);

        public int StepsAt(double arcRadius, double sweep)
        {
            var steps = 1;
            while (steps < 1 << 20 && arcRadius * Square(sweep / steps) / 8 > allowance)
                steps *= 2;
            return steps;
        }

        /// <summary>
        /// Sweeps one curve figure segment by segment, the way real sweeps a
        /// curve rather than its linearization: a straight segment contributes
        /// its band, an arc the ring between the circles the distance inside
        /// and outside it — reaching the arc's centre once the distance passes
        /// its radius — and each vertex where the direction turns its fan,
        /// with caps at an open figure's ends.
        /// </summary>
        private void Curve(SpatialShape shape, int index, bool closed)
        {
            var figure = shape.Figures[index];
            var pieces = new List<(SpatialCoordinate Start, SpatialCoordinate End, double InX, double InY, double OutX, double OutY)>();
            foreach (var segment in SpatialCurves.Segments(shape, index))
            {
                if (!segment.IsArc)
                {
                    var (a, b) = (figure[segment.Start], figure[segment.Start + 1]);
                    if (a.X == b.X && a.Y == b.Y)
                        continue;
                    this.Band(a, b);
                    pieces.Add((a, b, b.X - a.X, b.Y - a.Y, b.X - a.X, b.Y - a.Y));
                    continue;
                }
                var arc = SpatialCurves.ArcOf(figure, segment);
                if (arc.IsPoint)
                    continue;
                if (arc.IsStraight)
                {
                    this.Band(arc.Start, arc.End);
                    pieces.Add((arc.Start, arc.End, arc.End.X - arc.Start.X, arc.End.Y - arc.Start.Y, arc.End.X - arc.Start.X, arc.End.Y - arc.Start.Y));
                    continue;
                }
                this.ArcBand(arc);
                var sign = Math.Sign(arc.Sweep);
                var endAngle = arc.StartAngle + arc.Sweep;
                pieces.Add((arc.Start, arc.End,
                    -sign * Math.Sin(arc.StartAngle), sign * Math.Cos(arc.StartAngle),
                    -sign * Math.Sin(endAngle), sign * Math.Cos(endAngle)));
            }
            if (pieces.Count == 0)
            {
                if (!closed)
                    this.Circle(new SpatialCoordinate(figure[0].X, figure[0].Y));
                return;
            }
            for (var i = closed ? 0 : 1; i < pieces.Count; i++)
            {
                var (_, _, _, _, arrivingX, arrivingY) = pieces[(i - 1 + pieces.Count) % pieces.Count];
                var (at, _, leavingX, leavingY, _, _) = pieces[i];
                this.JoinDirections(at, arrivingX, arrivingY, leavingX, leavingY);
            }
            if (!closed)
            {
                var (_, end, _, _, endX, endY) = pieces[^1];
                var (start, _, startX, startY, _, _) = pieces[0];
                this.CapDirection(end, endX, endY);
                this.CapDirection(start, -startX, -startY);
            }
        }

        /// <summary>The band either side of a straight segment, its short sides through the segment's ends.</summary>
        private void Band(SpatialCoordinate a, SpatialCoordinate b)
        {
            a = new SpatialCoordinate(a.X, a.Y);
            b = new SpatialCoordinate(b.X, b.Y);
            var (nx, ny) = Normal(a, b);
            SpatialCoordinate[] band =
            [
                Offset(a, nx, ny, radius), Offset(b, nx, ny, radius), b, Offset(b, nx, ny, -radius), Offset(a, nx, ny, -radius), a, Offset(a, nx, ny, radius),
            ];
            foreach (var corner in band)
                this.NoteCorner(corner);
            this.Pieces.Add(band);
        }

        /// <summary>The ring an arc sweeps: its circle's outer and inner offsets, joined along the radii through its ends.</summary>
        private void ArcBand(SpatialArc arc)
        {
            var center = new SpatialCoordinate(arc.CenterX, arc.CenterY);
            var ring = new List<SpatialCoordinate>();
            var outer = arc.Radius + radius;
            var outerSteps = this.StepsAt(outer, arc.Sweep);
            for (var i = 0; i <= outerSteps; i++)
                ring.Add(OnCircle(center, outer, arc.StartAngle + (arc.Sweep * i / outerSteps)));
            ring.Add(new SpatialCoordinate(arc.End.X, arc.End.Y));
            var inner = arc.Radius - radius;
            if (inner > 0)
            {
                var innerSteps = this.StepsAt(inner, arc.Sweep);
                for (var i = innerSteps; i >= 0; i--)
                    ring.Add(OnCircle(center, inner, arc.StartAngle + (arc.Sweep * i / innerSteps)));
            }
            else
            {
                ring.Add(center);
            }
            ring.Add(new SpatialCoordinate(arc.Start.X, arc.Start.Y));
            ring.Add(ring[0]);
            foreach (var corner in ring)
                this.NoteCorner(corner);
            this.Pieces.Add([.. ring]);
        }

        private static SpatialCoordinate OnCircle(SpatialCoordinate center, double circleRadius, double angle) =>
            new(center.X + (circleRadius * Math.Cos(angle)), center.Y + (circleRadius * Math.Sin(angle)));

        /// <summary>The fan filling the outside of a turn from direction <c>in</c> to direction <c>out</c> at <paramref name="at"/>.</summary>
        private void JoinDirections(SpatialCoordinate at, double inX, double inY, double outX, double outY)
        {
            at = new SpatialCoordinate(at.X, at.Y);
            var turn = Math.Atan2((inX * outY) - (inY * outX), (inX * outX) + (inY * outY));
            if (Math.Abs(turn) < 1e-12)
                return;
            var length = Math.Sqrt((inX * inX) + (inY * inY));
            var (n1x, n1y) = (-inY / length, inX / length);
            var start = turn > 0 ? Math.Atan2(-n1y, -n1x) : Math.Atan2(n1y, n1x);
            var fan = new List<SpatialCoordinate> { at };
            this.Arc(fan, at, start, turn, includeStart: true, anchor: start, lattice: turn / 256);
            fan.Add(at);
            if (fan.Count >= 4)
                this.Pieces.Add([.. fan]);
        }

        /// <summary>The rounded end past <paramref name="end"/> for a figure arriving in direction (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
        private void CapDirection(SpatialCoordinate end, double dx, double dy)
        {
            end = new SpatialCoordinate(end.X, end.Y);
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            this.Cap(new SpatialCoordinate(end.X - (dx / length), end.Y - (dy / length)), end);
        }

        /// <summary>
        /// Linearizes one arc. The arc holding the outline's lowest point is
        /// split there first — at the point nearest straight down of a lattice
        /// of step <paramref name="lattice"/> anchored on
        /// <paramref name="anchor"/>, which is where real's curve polygon starts
        /// its ring — and each part gets its own step count. A cap's lattice is
        /// its short angle δ from its forward direction, a join's is 1/256 of
        /// its sweep from its start (probed over 72 cap directions and 30 joins
        /// 2026-09-28 against SQL Server 2025).
        /// </summary>
        private void Arc(List<SpatialCoordinate> into, SpatialCoordinate center, double start, double sweep, bool includeStart, double anchor, double lattice)
        {
            if (Bottom(start, sweep) is { } bottom)
            {
                var point = (center.Y - radius, center.X);
                if (point.CompareTo(this.LowestArcBottom) < 0)
                {
                    this.LowestArcBottom = point;
                    this.LowestArcCenter = center;
                }
                if (this.SplitCenter is { } split && split.X == center.X && split.Y == center.Y)
                {
                    var at = anchor + (Math.Round((bottom - anchor) / lattice) * lattice);
                    if ((at - start) * Math.Sign(sweep) > 0 && (start + sweep - at) * Math.Sign(sweep) > 0)
                    {
                        this.Linearize(into, center, start, at - start, includeStart);
                        this.Linearize(into, center, at, start + sweep - at, includeStart: false);
                        return;
                    }
                }
            }
            this.Linearize(into, center, start, sweep, includeStart);
        }

        /// <summary>The angle straight down, as it falls strictly inside the arc, or null when the arc doesn't reach it.</summary>
        private static double? Bottom(double start, double sweep)
        {
            var low = Math.Min(start, start + sweep);
            var high = Math.Max(start, start + sweep);
            var down = -Math.PI / 2;
            down += Math.Ceiling((low - down) / (2 * Math.PI)) * 2 * Math.PI;
            return down > low && down < high ? down : null;
        }

        private static double Square(double value) => value * value;

        private void Linearize(List<SpatialCoordinate> into, SpatialCoordinate center, double start, double sweep, bool includeStart)
        {
            var steps = this.Steps(sweep);
            for (var i = includeStart ? 0 : 1; i <= steps; i++)
            {
                var angle = start + (sweep * i / steps);
                into.Add(new SpatialCoordinate(center.X + (radius * Math.Cos(angle)), center.Y + (radius * Math.Sin(angle))));
            }
        }

        private void Circle(SpatialCoordinate center)
        {
            var ring = new List<SpatialCoordinate>();
            for (var quarter = 0; quarter < 4; quarter++)
                this.Linearize(ring, center, (-Math.PI / 2) + (quarter * Math.PI / 2), Math.PI / 2, includeStart: quarter == 0);
            ring[^1] = ring[0];
            this.Pieces.Add([.. ring]);
        }

        private void Line(List<SpatialCoordinate> points, bool closed)
        {
            if (points.Count == 1)
            {
                if (!closed)
                    this.Circle(points[0]);
                return;
            }
            var count = closed ? points.Count : points.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                var (nx, ny) = Normal(a, b);
                // The segment's own ends sit on the band's short sides, so the
                // fans of the joins and caps, which meet the band there, share
                // those edges exactly rather than a snapped approximation of
                // them that would leave slivers between the pieces.
                SpatialCoordinate[] band =
                [
                    Offset(a, nx, ny, radius), Offset(b, nx, ny, radius), b, Offset(b, nx, ny, -radius), Offset(a, nx, ny, -radius), a, Offset(a, nx, ny, radius),
                ];
                foreach (var corner in band)
                    this.NoteCorner(corner);
                this.Pieces.Add(band);
            }
            for (var i = closed ? 0 : 1; i < (closed ? points.Count : points.Count - 1); i++)
            {
                var previous = points[(i - 1 + points.Count) % points.Count];
                var at = points[i];
                var next = points[(i + 1) % points.Count];
                this.Join(previous, at, next);
            }
            if (!closed)
            {
                this.Cap(points[^2], points[^1]);
                this.Cap(points[1], points[0]);
            }
        }

        private static (double X, double Y) Normal(SpatialCoordinate a, SpatialCoordinate b)
        {
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var length = Math.Sqrt((dx * dx) + (dy * dy));
            return (-dy / length, dx / length);
        }

        private static SpatialCoordinate Offset(SpatialCoordinate point, double nx, double ny, double by) =>
            new(point.X + (nx * by), point.Y + (ny * by));

        /// <summary>The arc filling the gap the two edges' offsets leave on the outside of a turn.</summary>
        private void Join(SpatialCoordinate previous, SpatialCoordinate at, SpatialCoordinate next)
        {
            var inX = at.X - previous.X;
            var inY = at.Y - previous.Y;
            var outX = next.X - at.X;
            var outY = next.Y - at.Y;
            var turn = Math.Atan2((inX * outY) - (inY * outX), (inX * outX) + (inY * outY));
            if (turn == 0)
                return;
            var (n1x, n1y) = Normal(previous, at);
            // A left turn opens the right side and a right turn the left, and
            // the offsets there swing through the turn angle.
            var start = turn > 0 ? Math.Atan2(-n1y, -n1x) : Math.Atan2(n1y, n1x);
            var fan = new List<SpatialCoordinate> { at };
            this.Arc(fan, at, start, turn, includeStart: true, anchor: start, lattice: turn / 256);
            fan.Add(at);
            if (fan.Count >= 4)
                this.Pieces.Add([.. fan]);
        }

        /// <summary>The rounded end past <paramref name="end"/>, arriving from <paramref name="from"/>.</summary>
        private void Cap(SpatialCoordinate from, SpatialCoordinate end)
        {
            var (nx, ny) = Normal(from, end);
            var ahead = Math.Atan2(end.Y - from.Y, end.X - from.X);
            var cap = new List<SpatialCoordinate> { end, Offset(end, nx, ny, -radius) };
            var quarter = (Math.PI / 2) - capGap;
            this.Arc(cap, end, ahead - quarter, quarter, includeStart: true, anchor: ahead, lattice: capGap);
            this.Arc(cap, end, ahead, quarter, includeStart: false, anchor: ahead, lattice: capGap);
            cap.Add(Offset(end, nx, ny, radius));
            cap.Add(end);
            this.Pieces.Add([.. cap]);
        }
    }
}
