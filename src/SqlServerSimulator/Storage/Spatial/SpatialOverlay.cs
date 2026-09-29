using System.Numerics;

namespace SqlServerSimulator.Storage.Spatial;

/// <summary>The four set operations <c>STIntersection</c> / <c>STUnion</c> / <c>STDifference</c> / <c>STSymDifference</c> name.</summary>
internal enum SpatialOverlayOperation
{
    Intersection,
    Union,
    Difference,
    SymmetricDifference,
}

/// <summary>
/// One operand of an overlay, flattened onto the precision grid into the
/// three OGC component classes. A polygonal area is held as its rings alone:
/// the area is read by even-odd parity, which is exact for a valid polygon or
/// multipolygon, and a collection whose polygon members might overlap is
/// unioned into one area before it gets here.
/// </summary>
internal sealed class GridOperand
{
    public readonly List<GridPoint> Points = [];

    public readonly List<GridPoint[]> Lines = [];

    public readonly List<GridPoint[]> Rings = [];

    /// <summary>
    /// The member rings of a collection whose polygons were unioned into
    /// <see cref="Rings"/>. They take no part in the area, but they still node
    /// the arrangement: real overlays every member at once, so a result ring
    /// keeps a vertex wherever a member's edge meets it.
    /// </summary>
    public readonly List<GridPoint[]> Ghosts = [];

    public bool IsEmpty => this.Points.Count == 0 && this.Lines.Count == 0 && this.Rings.Count == 0;

    public bool HasArea => this.Rings.Count > 0;

    /// <summary>
    /// Flattens a shape onto the grid. Consecutive vertices that snap to the
    /// same grid point collapse, and a figure left with a single vertex drops
    /// to a point (a line) or disappears (a ring).
    /// </summary>
    public static GridOperand From(SpatialShape shape, SpatialPrecisionGrid grid)
    {
        var operand = new GridOperand();
        var polygonMembers = new List<SpatialShape>();
        operand.Collect(shape, grid, polygonMembers);
        if (polygonMembers.Count == 1)
        {
            AddRings(operand.Rings, polygonMembers[0], grid);
        }
        else if (polygonMembers.Count > 1)
        {
            // A collection's polygon members may overlap, so parity over their
            // rings would cancel the overlap out; union them first.
            foreach (var member in polygonMembers)
                AddRings(operand.Ghosts, member, grid);
            var area = new GridOperand();
            AddRings(area.Rings, polygonMembers[0], grid);
            for (var i = 1; i < polygonMembers.Count; i++)
            {
                var next = new GridOperand();
                AddRings(next.Rings, polygonMembers[i], grid);
                var merged = SpatialOverlay.Overlay(area, next, SpatialOverlayOperation.Union, grid);
                area = new GridOperand();
                foreach (var polygon in merged.Polygons)
                    area.Rings.AddRange(polygon);
            }
            operand.Rings.AddRange(area.Rings);
        }
        return operand;
    }

    private void Collect(SpatialShape shape, SpatialPrecisionGrid grid, List<SpatialShape> polygonMembers)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
                foreach (var figure in shape.Figures)
                {
                    foreach (var point in figure)
                        this.Points.Add(grid.Snap(point));
                }
                break;
            case SpatialShapeType.LineString:
                foreach (var figure in shape.Figures)
                    this.AddLine(figure, grid);
                break;
            case SpatialShapeType.Polygon:
                if (!shape.IsEmpty)
                    polygonMembers.Add(shape);
                break;
            case SpatialShapeType.MultiPolygon:
                // A valid multipolygon's members share no interior, so one
                // parity reads all of them.
                if (!shape.IsEmpty)
                    polygonMembers.Add(shape);
                break;
            default:
                foreach (var child in shape.Children)
                    this.Collect(child, grid, polygonMembers);
                break;
        }
    }

    private void AddLine(SpatialCoordinate[] figure, SpatialPrecisionGrid grid)
    {
        var points = Dedupe(figure, grid);
        if (points.Count == 1)
            this.Points.Add(points[0]);
        else if (points.Count > 1)
            this.Lines.Add([.. points]);
    }

    private static void AddRings(List<GridPoint[]> rings, SpatialShape shape, SpatialPrecisionGrid grid)
    {
        if (shape.Type == SpatialShapeType.MultiPolygon)
        {
            foreach (var child in shape.Children)
                AddRings(rings, child, grid);
            return;
        }
        foreach (var figure in shape.Figures)
        {
            var points = Dedupe(figure, grid);
            if (points.Count > 1 && points[0] == points[^1])
                points.RemoveAt(points.Count - 1);
            if (points.Count >= 3)
                rings.Add([.. points]);
        }
    }

    private static List<GridPoint> Dedupe(SpatialCoordinate[] figure, SpatialPrecisionGrid grid)
    {
        var points = new List<GridPoint>(figure.Length);
        foreach (var coordinate in figure)
        {
            var point = grid.Snap(coordinate);
            if (points.Count == 0 || points[^1] != point)
                points.Add(point);
        }
        return points;
    }
}

/// <summary>
/// An overlay result on the grid, before real's output conventions order it:
/// polygons as ring lists (shell first, rings open — no repeated last
/// vertex), lines as vertex runs, and isolated points.
/// </summary>
internal sealed class GridGeometry
{
    public readonly List<List<GridPoint[]>> Polygons = [];

    public readonly List<GridPoint[]> Lines = [];

    public readonly List<GridPoint> Points = [];

    public bool IsEmpty => this.Polygons.Count == 0 && this.Lines.Count == 0 && this.Points.Count == 0;
}

/// <summary>
/// The planar overlay engine behind the constructive operations: both operands
/// are noded against each other on the precision grid, every edge of the
/// resulting arrangement is labelled with where it lies relative to each
/// operand, and the result is read off those labels — faces for the area,
/// edges for the lines, nodes for the points.
/// </summary>
/// <remarks>
/// <para>All arithmetic is exact on the grid: orientation tests are 128-bit
/// cross products, and a crossing of two edges is the exact rational point
/// rounded onto the grid the way real rounds an input vertex. A rounded
/// crossing can move an edge far enough to meet another one, so noding
/// repeats until it settles.</para>
/// <para>An edge's sides are classified by even-odd parity against each
/// operand's rings, cast from the edge's midpoint; a ring running along the
/// edge itself flips the parity between the two sides.</para>
/// </remarks>
internal static class SpatialOverlay
{
    private const int ALine = 1;
    private const int BLine = 2;
    private const int ARing = 4;
    private const int BRing = 8;

    private struct Segment(GridPoint a, GridPoint b, int flags)
    {
        public GridPoint A = a;
        public GridPoint B = b;
        public int Flags = flags;
    }

    /// <summary>One undirected edge of the noded arrangement, stored from its lower to its upper endpoint in sweep order.</summary>
    private sealed class Edge(GridPoint low, GridPoint high, int flags)
    {
        public readonly GridPoint Low = low;
        public readonly GridPoint High = high;
        public int Flags = flags;
        public bool AreaLeftA;
        public bool AreaRightA;
        public bool AreaLeftB;
        public bool AreaRightB;
    }

    /// <summary>Runs one set operation over two shapes that share a grid.</summary>
    public static GridGeometry Overlay(GridOperand a, GridOperand b, SpatialOverlayOperation operation, SpatialPrecisionGrid grid)
    {
        var segments = new List<Segment>();
        AddSegments(segments, a, ALine, ARing);
        AddSegments(segments, b, BLine, BRing);
        var splitters = new List<GridPoint>(a.Points.Count + b.Points.Count);
        splitters.AddRange(a.Points);
        splitters.AddRange(b.Points);
        segments = Node(segments, splitters, grid);

        var edges = new Dictionary<(GridPoint, GridPoint), Edge>();
        foreach (var segment in segments)
        {
            var (low, high) = segment.A.CompareTo(segment.B) < 0 ? (segment.A, segment.B) : (segment.B, segment.A);
            // Line flags accumulate; ring flags cancel in pairs, so a fold a
            // rounded crossing leaves in one operand's ring — the ring running
            // out and back along the same piece — reads as no boundary at all,
            // which keeps the parity of every face consistent.
            if (edges.TryGetValue((low, high), out var existing))
                existing.Flags = ((existing.Flags | segment.Flags) & (ALine | BLine)) | ((existing.Flags ^ segment.Flags) & (ARing | BRing));
            else
                edges.Add((low, high), new Edge(low, high, segment.Flags));
        }

        var ringsA = new RingIndex(edges.Values, ARing);
        var ringsB = new RingIndex(edges.Values, BRing);
        foreach (var edge in edges.Values)
        {
            (edge.AreaLeftA, edge.AreaRightA) = ringsA.Sides(edge);
            (edge.AreaLeftB, edge.AreaRightB) = ringsB.Sides(edge);
        }

        var result = new GridGeometry();
        var boundary = new List<(GridPoint From, GridPoint To)>();
        var lineEdges = new List<Edge>();
        var covered = new HashSet<GridPoint>();
        foreach (var edge in edges.Values)
        {
            var left = Apply(operation, edge.AreaLeftA, edge.AreaLeftB);
            var right = Apply(operation, edge.AreaRightA, edge.AreaRightB);
            if (left != right)
            {
                boundary.Add(left ? (edge.Low, edge.High) : (edge.High, edge.Low));
                _ = covered.Add(edge.Low);
                _ = covered.Add(edge.High);
                continue;
            }
            if (left)
                continue;
            var onLineA = (edge.Flags & ALine) != 0;
            var onLineB = (edge.Flags & BLine) != 0;
            var inA = onLineA || edge.AreaLeftA || edge.AreaRightA;
            var inB = onLineB || edge.AreaLeftB || edge.AreaRightB;
            var keep = operation switch
            {
                SpatialOverlayOperation.Intersection => inA && inB,
                SpatialOverlayOperation.Union => onLineA || onLineB,
                SpatialOverlayOperation.Difference => onLineA && !inB,
                _ => (onLineA && !inB) || (onLineB && !inA),
            };
            if (keep)
            {
                lineEdges.Add(edge);
                _ = covered.Add(edge.Low);
                _ = covered.Add(edge.High);
            }
        }

        AssembleRings(boundary, result);
        AssembleLines(lineEdges, result);

        // Isolated points, and for an intersection the places the operands
        // meet in a single point.
        var nodes = new Dictionary<GridPoint, int>();
        foreach (var edge in edges.Values)
        {
            nodes[edge.Low] = nodes.GetValueOrDefault(edge.Low) | edge.Flags;
            nodes[edge.High] = nodes.GetValueOrDefault(edge.High) | edge.Flags;
        }
        var pointsA = new HashSet<GridPoint>(a.Points);
        var pointsB = new HashSet<GridPoint>(b.Points);
        foreach (var point in pointsA)
            nodes[point] = nodes.GetValueOrDefault(point);
        foreach (var point in pointsB)
            nodes[point] = nodes.GetValueOrDefault(point);
        var emitted = new HashSet<GridPoint>();
        foreach (var (node, flags) in nodes)
        {
            if (covered.Contains(node))
                continue;
            var isA = pointsA.Contains(node);
            var isB = pointsB.Contains(node);
            bool keep;
            switch (operation)
            {
                case SpatialOverlayOperation.Intersection:
                    if (flags == 0 && !isA && !isB)
                        continue;
                    keep = (isA || (flags & (ALine | ARing)) != 0 || ringsA.Contains(node))
                        && (isB || (flags & (BLine | BRing)) != 0 || ringsB.Contains(node));
                    break;
                case SpatialOverlayOperation.Union:
                    keep = isA || isB;
                    break;
                case SpatialOverlayOperation.Difference:
                    keep = isA && !InClosure(node, flags, isB, BLine | BRing, ringsB);
                    break;
                default:
                    keep = (isA && !InClosure(node, flags, isB, BLine | BRing, ringsB))
                        || (isB && !InClosure(node, flags, isA, ALine | ARing, ringsA));
                    break;
            }
            if (!keep)
                continue;
            // A point inside the result's area is part of it already.
            if (operation != SpatialOverlayOperation.Intersection
                && Apply(operation, ringsA.Contains(node), ringsB.Contains(node)))
            {
                continue;
            }
            if (emitted.Add(node))
                result.Points.Add(node);
        }
        return result;
    }

    private static bool InClosure(GridPoint node, int flags, bool isPoint, int componentFlags, RingIndex rings) =>
        isPoint || (flags & componentFlags) != 0 || rings.Contains(node);

    private static bool Apply(SpatialOverlayOperation operation, bool inA, bool inB) => operation switch
    {
        SpatialOverlayOperation.Intersection => inA && inB,
        SpatialOverlayOperation.Union => inA || inB,
        SpatialOverlayOperation.Difference => inA && !inB,
        _ => inA != inB,
    };

    private static void AddSegments(List<Segment> segments, GridOperand operand, int lineFlag, int ringFlag)
    {
        foreach (var line in operand.Lines)
        {
            for (var i = 1; i < line.Length; i++)
                segments.Add(new Segment(line[i - 1], line[i], lineFlag));
        }
        foreach (var ring in operand.Rings)
        {
            for (var i = 0; i < ring.Length; i++)
                segments.Add(new Segment(ring[i], ring[(i + 1) % ring.Length], ringFlag));
        }
        foreach (var ring in operand.Ghosts)
        {
            for (var i = 0; i < ring.Length; i++)
                segments.Add(new Segment(ring[i], ring[(i + 1) % ring.Length], 0));
        }
    }

    /// <summary>
    /// Nodes the segments: each is split at every vertex lying exactly on it,
    /// and — snap rounding — re-routed through the grid point of every
    /// rounded crossing whose unit square it passes through. Routing through
    /// the crossings' squares rather than their points alone leaves no two
    /// edges crossing unnoticed after rounding, so the faces the labelling
    /// reads are well formed; vertices split only where a segment runs exactly
    /// through them, which is what real does — a line ending a hair off
    /// another is left unjoined.
    /// </summary>
    private static List<Segment> Node(List<Segment> segments, List<GridPoint> splitters, SpatialPrecisionGrid grid)
    {
        // Re-routing a piece through a crossing's grid point moves it by up to
        // half a step, which can carry it across a vertex it passed a hair
        // from before — a crossing no pass over the written segments sees.
        // Left in, that piece is labelled by one midpoint while its two ends
        // lie on different sides of the other operand, and the result's
        // boundary stops closing. So noding repeats until no two pieces cross.
        var crossings = new HashSet<GridPoint>();
        for (var pass = 0; pass < MaxNodingPasses; pass++)
        {
            if (!CollectCrossings(segments, grid, crossings) && pass > 0)
                break;
            segments = Route(segments, splitters, crossings);
        }
        return segments;
    }

    /// <summary>A bound on <see cref="Node"/>'s passes, each settling the crossings the previous one's rounding introduced.</summary>
    private const int MaxNodingPasses = 8;

    /// <summary>Adds every proper crossing among <paramref name="segments"/> to <paramref name="crossings"/>, returning whether any was new.</summary>
    private static bool CollectCrossings(List<Segment> segments, SpatialPrecisionGrid grid, HashSet<GridPoint> crossings)
    {
        var added = false;
        var order = new int[segments.Count];
        for (var i = 0; i < order.Length; i++)
            order[i] = i;
        var minX = new long[segments.Count];
        for (var i = 0; i < segments.Count; i++)
            minX[i] = Math.Min(segments[i].A.X, segments[i].B.X);
        Array.Sort(order, (p, q) => minX[p].CompareTo(minX[q]));
        var active = new List<int>();
        foreach (var i in order)
        {
            var si = segments[i];
            var iMinX = minX[i];
            var iMinY = Math.Min(si.A.Y, si.B.Y);
            var iMaxY = Math.Max(si.A.Y, si.B.Y);
            var write = 0;
            for (var k = 0; k < active.Count; k++)
            {
                var j = active[k];
                var sj = segments[j];
                if (Math.Max(sj.A.X, sj.B.X) < iMinX)
                    continue;
                active[write++] = j;
                if (Math.Max(sj.A.Y, sj.B.Y) < iMinY || Math.Min(sj.A.Y, sj.B.Y) > iMaxY)
                    continue;
                if (ProperlyCross(si, sj))
                {
                    var crossing = Crossing(si.A, si.B, sj.A, sj.B);
                    if (crossings.Add(crossing))
                    {
                        grid.RecordComputed(crossing, ApproximateCrossing(si, sj));
                        added = true;
                    }
                }
            }
            active.RemoveRange(write, active.Count - write);
            active.Add(i);
        }
        return added;
    }

    /// <summary>Splits each segment at the vertices lying exactly on it and re-routes it through every crossing pixel it passes.</summary>
    private static List<Segment> Route(List<Segment> segments, List<GridPoint> splitters, HashSet<GridPoint> crossings)
    {
        var vertices = new HashSet<GridPoint>(splitters);
        foreach (var segment in segments)
        {
            _ = vertices.Add(segment.A);
            _ = vertices.Add(segment.B);
        }

        var hot = Sorted(crossings);
        var exact = Sorted(vertices);
        var noded = new List<Segment>(segments.Count * 2);
        var route = new List<GridPoint>();
        foreach (var segment in segments)
        {
            route.Clear();
            var lowX = Math.Min(segment.A.X, segment.B.X);
            var highX = Math.Max(segment.A.X, segment.B.X);
            var lowY = Math.Min(segment.A.Y, segment.B.Y);
            var highY = Math.Max(segment.A.Y, segment.B.Y);
            for (var k = FirstAtOrAbove(hot, lowX - 1); k < hot.Length && hot[k].X <= highX + 1; k++)
            {
                var pixel = hot[k];
                if (pixel.Y >= lowY - 1 && pixel.Y <= highY + 1 && PassesThrough(segment.A, segment.B, pixel))
                    route.Add(pixel);
            }
            for (var k = FirstAtOrAbove(exact, lowX); k < exact.Length && exact[k].X <= highX; k++)
            {
                var vertex = exact[k];
                if (vertex.Y >= lowY && vertex.Y <= highY && GridPoint.Orientation(segment.A, segment.B, vertex) == 0)
                    route.Add(vertex);
            }
            var dx = segment.B.X - segment.A.X;
            var dy = segment.B.Y - segment.A.Y;
            route.Sort((p, q) => (((Int128)(p.X - segment.A.X) * dx) + ((Int128)(p.Y - segment.A.Y) * dy))
                .CompareTo(((Int128)(q.X - segment.A.X) * dx) + ((Int128)(q.Y - segment.A.Y) * dy)));
            var from = segment.A;
            foreach (var point in route)
            {
                if (point == from || point == segment.B)
                    continue;
                noded.Add(new Segment(from, point, segment.Flags));
                from = point;
            }
            if (from != segment.B)
                noded.Add(new Segment(from, segment.B, segment.Flags));
        }
        return noded;
    }

    private static GridPoint[] Sorted(HashSet<GridPoint> points)
    {
        var array = new GridPoint[points.Count];
        points.CopyTo(array);
        Array.Sort(array, static (p, q) => p.X != q.X ? p.X.CompareTo(q.X) : p.Y.CompareTo(q.Y));
        return array;
    }

    private static int FirstAtOrAbove(GridPoint[] pixels, long x)
    {
        int low = 0, high = pixels.Length;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (pixels[mid].X < x)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    /// <summary>
    /// Whether the segment passes through the unit square centred on
    /// <paramref name="pixel"/>, boundary included — tested in doubled
    /// coordinates so every quantity stays integral.
    /// </summary>
    private static bool PassesThrough(GridPoint a, GridPoint b, GridPoint pixel)
    {
        Int128 ax = (Int128)a.X * 2, ay = (Int128)a.Y * 2, bx = (Int128)b.X * 2, by = (Int128)b.Y * 2;
        Int128 cx = (Int128)pixel.X * 2, cy = (Int128)pixel.Y * 2;
        if (Int128.Max(ax, bx) < cx - 1 || Int128.Min(ax, bx) > cx + 1 || Int128.Max(ay, by) < cy - 1 || Int128.Min(ay, by) > cy + 1)
            return false;
        var dx = bx - ax;
        var dy = by - ay;
        var positive = false;
        var negative = false;
        foreach (var (ox, oy) in (ReadOnlySpan<(int, int)>)[(-1, -1), (1, -1), (1, 1), (-1, 1)])
        {
            var side = Int128.Sign((dx * (cy + oy - ay)) - (dy * (cx + ox - ax)));
            positive |= side > 0;
            negative |= side < 0;
            if (side == 0 || (positive && negative))
                return true;
        }
        return false;
    }

    /// <summary>Whether two segments cross at a single point interior to both.</summary>
    private static bool ProperlyCross(Segment si, Segment sj) =>
        GridPoint.Orientation(si.A, si.B, sj.A) * GridPoint.Orientation(si.A, si.B, sj.B) < 0
        && GridPoint.Orientation(sj.A, sj.B, si.A) * GridPoint.Orientation(sj.A, sj.B, si.B) < 0;

    /// <summary>
    /// The crossing of two properly crossing segments, rounded to the nearest
    /// grid point. Unlike an input vertex's truncation this leaves a crossing
    /// that falls exactly on the grid where it is, which is what real reports.
    /// </summary>
    private static GridPoint Crossing(GridPoint a, GridPoint b, GridPoint c, GridPoint d)
    {
        var dx1 = b.X - a.X;
        var dy1 = b.Y - a.Y;
        var dx2 = d.X - c.X;
        var dy2 = d.Y - c.Y;
        var den = ((Int128)dx1 * dy2) - ((Int128)dy1 * dx2);
        var num = ((Int128)(c.X - a.X) * dy2) - ((Int128)(c.Y - a.Y) * dx2);
        BigInteger bigDen = den;
        BigInteger bigNum = num;
        if (bigDen.Sign < 0)
        {
            bigDen = -bigDen;
            bigNum = -bigNum;
        }
        return new GridPoint(RoundRational((a.X * bigDen) + (dx1 * bigNum), bigDen), RoundRational((a.Y * bigDen) + (dy1 * bigNum), bigDen));
    }

    /// <summary>
    /// The crossing as real reports it: real doesn't round a crossing onto
    /// the grid, it computes it in floating point on the grid and maps that
    /// back. Both segments are taken from their upper end in sweep order, and
    /// the one whose upper end is higher is followed: <c>P = A + t·(B - A)</c>
    /// with <c>t = ((C - A) × (D - C)) / ((B - A) × (D - C))</c>. Of eleven
    /// formulations tried against 400 random crossings (probed 2026-09-28
    /// against SQL Server 2025) — exact rational points, the determinant form,
    /// fused multiply-adds, other choices of base segment and direction — this
    /// one reproduces 78% to the last digit; no formulation tried explains the
    /// rest, which differ by an ulp or two.
    /// </summary>
    private static (double X, double Y) ApproximateCrossing(Segment si, Segment sj)
    {
        var (topI, bottomI) = si.A.CompareTo(si.B) > 0 ? (si.A, si.B) : (si.B, si.A);
        var (topJ, bottomJ) = sj.A.CompareTo(sj.B) > 0 ? (sj.A, sj.B) : (sj.B, sj.A);
        var ((top, bottom), (otherTop, otherBottom)) = topI.CompareTo(topJ) >= 0
            ? ((topI, bottomI), (topJ, bottomJ))
            : ((topJ, bottomJ), (topI, bottomI));
        double ax = top.X, ay = top.Y;
        double rx = bottom.X - ax, ry = bottom.Y - ay;
        double qx = otherBottom.X - otherTop.X, qy = otherBottom.Y - otherTop.Y;
        var den = (rx * qy) - (ry * qx);
        var t = (((otherTop.X - ax) * qy) - ((otherTop.Y - ay) * qx)) / den;
        return (ax + (t * rx), ay + (t * ry));
    }

    /// <summary><c>floor(n / d + 1/2)</c> for a positive <paramref name="d"/>.</summary>
    private static long RoundRational(BigInteger n, BigInteger d)
    {
        var numerator = (2 * n) + d;
        var denominator = 2 * d;
        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        return (long)(remainder.Sign < 0 ? quotient - 1 : quotient);
    }

    /// <summary>
    /// The ring edges of one operand, bucketed by y so a horizontal ray only
    /// visits the edges its height can reach.
    /// </summary>
    private sealed class RingIndex
    {
        private readonly int flag;
        private readonly List<Edge>[] buckets;
        private readonly HashSet<GridPoint> vertices = [];
        private readonly long minY;
        private readonly double bucketHeight;

        public RingIndex(Dictionary<(GridPoint, GridPoint), Edge>.ValueCollection edges, int flag)
        {
            this.flag = flag;
            var ring = new List<Edge>();
            foreach (var edge in edges)
            {
                if ((edge.Flags & flag) != 0)
                {
                    ring.Add(edge);
                    _ = this.vertices.Add(edge.Low);
                    _ = this.vertices.Add(edge.High);
                }
            }
            var count = Math.Max(1, (int)Math.Sqrt(ring.Count));
            this.buckets = new List<Edge>[count];
            for (var i = 0; i < count; i++)
                this.buckets[i] = [];
            if (ring.Count == 0)
                return;
            this.minY = long.MaxValue;
            var maxY = long.MinValue;
            foreach (var edge in ring)
            {
                this.minY = Math.Min(this.minY, edge.Low.Y);
                maxY = Math.Max(maxY, edge.High.Y);
            }
            this.bucketHeight = Math.Max(1, ((double)maxY - this.minY + 1) / count);
            foreach (var edge in ring)
            {
                var from = this.Bucket(edge.Low.Y);
                var to = this.Bucket(edge.High.Y);
                for (var i = from; i <= to; i++)
                    this.buckets[i].Add(edge);
            }
        }

        public bool IsEmpty => this.vertices.Count == 0;

        private int Bucket(double y) => Math.Clamp((int)((y - this.minY) / this.bucketHeight), 0, this.buckets.Length - 1);

        /// <summary>
        /// Crossings of the rightward ray from <c>(x2, y2) / 2</c>, cast just
        /// above that height, with every ring edge but <paramref name="skip"/>.
        /// </summary>
        private int Crossings(Int128 x2, Int128 y2, Edge? skip)
        {
            if (this.IsEmpty)
                return 0;
            var count = 0;
            var y = (double)y2 / 2;
            if (y < this.minY - 1 || y > this.minY + (this.bucketHeight * this.buckets.Length) + 1)
                return 0;
            foreach (var edge in this.buckets[this.Bucket(y)])
            {
                if (ReferenceEquals(edge, skip))
                    continue;
                var lowY = (Int128)edge.Low.Y * 2;
                var highY = (Int128)edge.High.Y * 2;
                // Low is below High in sweep order, so only the half-open straddle needs testing.
                if (!(lowY <= y2 && highY > y2))
                    continue;
                var cross = (2 * ((Int128)edge.High.X - edge.Low.X) * (y2 - lowY)) - ((highY - lowY) * (x2 - ((Int128)edge.Low.X * 2)));
                if (cross > 0)
                    count++;
            }
            return count;
        }

        /// <summary>Whether the operand's area holds each side of an edge.</summary>
        public (bool Left, bool Right) Sides(Edge edge)
        {
            if (this.IsEmpty)
                return (false, false);
            var count = this.Crossings((Int128)edge.Low.X + edge.High.X, (Int128)edge.Low.Y + edge.High.Y, edge);
            var along = (edge.Flags & this.flag) != 0 ? 1 : 0;
            var horizontal = edge.Low.Y == edge.High.Y;
            var left = horizontal ? count : count + along;
            var right = horizontal ? count + along : count;
            return ((left & 1) == 1, (right & 1) == 1);
        }

        /// <summary>Whether a point lies in the operand's area, boundary included.</summary>
        public bool Contains(GridPoint point) =>
            !this.IsEmpty && (this.vertices.Contains(point) || (this.Crossings((Int128)point.X * 2, (Int128)point.Y * 2, null) & 1) == 1);
    }

    /// <summary>
    /// Traces the result's rings from its boundary edges, each directed with
    /// the area on its left, and groups them into polygons.
    /// </summary>
    private static void AssembleRings(List<(GridPoint From, GridPoint To)> boundary, GridGeometry result)
    {
        if (boundary.Count == 0)
            return;
        var outgoing = new Dictionary<GridPoint, List<int>>();
        for (var i = 0; i < boundary.Count; i++)
        {
            if (!outgoing.TryGetValue(boundary[i].From, out var list))
                outgoing.Add(boundary[i].From, list = []);
            list.Add(i);
        }
        var used = new bool[boundary.Count];
        var shells = new List<GridPoint[]>();
        var holes = new List<GridPoint[]>();
        for (var start = 0; start < boundary.Count; start++)
        {
            if (used[start])
                continue;
            var ring = new List<GridPoint>();
            var current = start;
            while (!used[current])
            {
                used[current] = true;
                var (from, to) = boundary[current];
                ring.Add(from);
                var next = -1;
                foreach (var candidate in outgoing[to])
                {
                    if (used[candidate] && candidate != start)
                        continue;
                    if (next < 0 || ClockwiseBefore(from, to, boundary[candidate].To, boundary[next].To))
                        next = candidate;
                }
                if (next < 0)
                    break;
                current = next;
            }
            if (ring.Count < 3)
                continue;
            if (SignedArea(ring) > 0)
                shells.Add([.. ring]);
            else
                holes.Add([.. ring]);
        }
        var polygons = new List<List<GridPoint[]>>();
        foreach (var shell in shells)
            polygons.Add([shell]);
        foreach (var hole in holes)
        {
            var (sampleX, sampleY) = SamplePoint(hole);
            List<GridPoint[]>? owner = null;
            Int128 ownerArea = 0;
            foreach (var polygon in polygons)
            {
                if (!RingContains(polygon[0], sampleX, sampleY))
                    continue;
                var area = SignedArea(polygon[0]);
                if (owner is null || area < ownerArea)
                {
                    owner = polygon;
                    ownerArea = area;
                }
            }
            owner?.Add(hole);
        }
        result.Polygons.AddRange(polygons);
    }

    /// <summary>
    /// At a node reached along <c>from → at</c>, whether leaving towards
    /// <paramref name="p"/> comes before leaving towards <paramref name="q"/>
    /// turning clockwise from the way back. Taking the first such edge keeps
    /// each traced ring minimal, so rings touching at a vertex stay apart.
    /// </summary>
    private static bool ClockwiseBefore(GridPoint from, GridPoint at, GridPoint p, GridPoint q)
    {
        var rx = from.X - at.X;
        var ry = from.Y - at.Y;
        var rankP = ClockwiseRank(rx, ry, p.X - at.X, p.Y - at.Y);
        var rankQ = ClockwiseRank(rx, ry, q.X - at.X, q.Y - at.Y);
        if (rankP != rankQ)
            return rankP < rankQ;
        var cross = ((Int128)(p.X - at.X) * (q.Y - at.Y)) - ((Int128)(p.Y - at.Y) * (q.X - at.X));
        return cross < 0;
    }

    private static int ClockwiseRank(long rx, long ry, long dx, long dy)
    {
        var cross = ((Int128)rx * dy) - ((Int128)ry * dx);
        if (cross < 0)
            return 0;
        if (cross > 0)
            return 2;
        var dot = ((Int128)rx * dx) + ((Int128)ry * dy);
        return dot < 0 ? 1 : 3;
    }

    /// <summary>Twice the signed area, relative to the first vertex so the products stay in range.</summary>
    public static Int128 SignedArea(IReadOnlyList<GridPoint> ring)
    {
        Int128 sum = 0;
        var origin = ring[0];
        for (var i = 1; i + 1 < ring.Count; i++)
        {
            sum += ((Int128)(ring[i].X - origin.X) * (ring[i + 1].Y - origin.Y))
                - ((Int128)(ring[i + 1].X - origin.X) * (ring[i].Y - origin.Y));
        }
        return sum;
    }

    /// <summary>A doubled-coordinate point on a ring that no other ring can pass through: an edge's midpoint.</summary>
    private static (Int128 X, Int128 Y) SamplePoint(GridPoint[] ring) =>
        ((Int128)ring[0].X + ring[1].X, (Int128)ring[0].Y + ring[1].Y);

    /// <summary>Even-odd containment of a doubled-coordinate point in one ring.</summary>
    private static bool RingContains(GridPoint[] ring, Int128 x2, Int128 y2)
    {
        var inside = false;
        for (var i = 0; i < ring.Length; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Length];
            var (low, high) = a.Y <= b.Y ? (a, b) : (b, a);
            var lowY = (Int128)low.Y * 2;
            var highY = (Int128)high.Y * 2;
            if (!(lowY <= y2 && highY > y2))
                continue;
            var cross = (2 * ((Int128)high.X - low.X) * (y2 - lowY)) - ((highY - lowY) * (x2 - ((Int128)low.X * 2)));
            if (cross > 0)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>
    /// A line run under construction by the sweep in <see cref="AssembleLines"/>.
    /// Once two runs join, both forward to the joined one, each remembering
    /// which end of it their own still-open end became.
    /// </summary>
    private sealed class LineRun(List<GridPoint> points)
    {
        public List<GridPoint> Points = points;
        public LineRun? Forward;
        public bool ForwardToTail;
    }

    /// <summary>An open end of a run, waiting at the node its next edge leads down to.</summary>
    private readonly struct OpenEnd(LineRun run, bool atTail, int edge)
    {
        public readonly LineRun Run = run;
        public readonly bool AtTail = atTail;
        public readonly int Edge = edge;

        public OpenEnd Resolve()
        {
            var run = this.Run;
            var atTail = this.AtTail;
            while (run.Forward is { } forward)
            {
                atTail = run.ForwardToTail;
                run = forward;
            }
            return new OpenEnd(run, atTail, this.Edge);
        }
    }

    /// <summary>
    /// Chains the result's line edges into runs the way real's sweep does,
    /// which is what fixes each run's direction.
    /// </summary>
    /// <remarks>
    /// <para>Nodes are visited from the top of the sweep order down. At each
    /// node the edges arriving from above pair up among themselves in the
    /// sweep order of their far ends, as do the edges leading down, and a
    /// leftover of each kind pair with each other — so at a <c>+</c> the north
    /// edge pairs with the east one and the south with the west, and at a
    /// <c>T</c> the two lower edges pair while the stem ends its own run.</para>
    /// <para>A pair of edges both leading down starts a run whose tail follows
    /// the edge at the smaller angle from the +x axis; an edge leading down
    /// with no partner starts a run at its tail. A pair both arriving from
    /// above joins two runs: a tail meeting a head concatenates them as they
    /// stand, and two like ends put the run arriving at the larger angle
    /// first. Probe-derived against SQL Server 2025 (2026-09-28): the rule
    /// reproduces the direction of all 122 untouched polylines in a random
    /// sweep, including the ones whose direction no endpoint rule explains.</para>
    /// </remarks>
    private static void AssembleLines(List<Edge> lineEdges, GridGeometry result)
    {
        if (lineEdges.Count == 0)
            return;
        var incident = new Dictionary<GridPoint, List<int>>();
        for (var i = 0; i < lineEdges.Count; i++)
        {
            foreach (var end in (ReadOnlySpan<GridPoint>)[lineEdges[i].Low, lineEdges[i].High])
            {
                if (!incident.TryGetValue(end, out var list))
                    incident.Add(end, list = []);
                list.Add(i);
            }
        }
        var nodes = new List<GridPoint>(incident.Keys);
        nodes.Sort((p, q) => q.CompareTo(p));
        var waiting = new Dictionary<GridPoint, List<OpenEnd>>();
        var runs = new List<LineRun>();
        foreach (var node in nodes)
        {
            var edges = incident[node];
            edges.Sort((p, q) => Other(lineEdges[p], node).CompareTo(Other(lineEdges[q], node)));
            var arriving = waiting.Remove(node, out var ends) ? ends : [];
            OpenEnd? Arrival(int edge)
            {
                foreach (var end in arriving)
                {
                    if (end.Edge == edge)
                        return end.Resolve();
                }
                return null;
            }
            void Wait(OpenEnd end)
            {
                var far = Other(lineEdges[end.Edge], node);
                if (!waiting.TryGetValue(far, out var list))
                    waiting.Add(far, list = []);
                list.Add(end);
            }
            void Extend(OpenEnd end)
            {
                if (end.AtTail)
                    end.Run.Points.Add(node);
                else
                    end.Run.Points.Insert(0, node);
            }
            LineRun Start()
            {
                var run = new LineRun([node]);
                runs.Add(run);
                return run;
            }
            var ups = new List<int>();
            var downs = new List<int>();
            foreach (var edge in edges)
                (Other(lineEdges[edge], node).CompareTo(node) > 0 ? ups : downs).Add(edge);
            var pairs = new List<(int First, int Second)>();
            for (var i = 0; i + 1 < ups.Count; i += 2)
                pairs.Add((ups[i], ups[i + 1]));
            for (var i = 0; i + 1 < downs.Count; i += 2)
                pairs.Add((downs[i], downs[i + 1]));
            var leftUp = ups.Count % 2 == 1 ? ups[^1] : -1;
            var leftDown = downs.Count % 2 == 1 ? downs[^1] : -1;
            if (leftUp >= 0 && leftDown >= 0)
                pairs.Add((leftUp, leftDown));
            else if (leftUp >= 0)
                pairs.Add((leftUp, -1));
            else if (leftDown >= 0)
                pairs.Add((leftDown, -1));
            foreach (var (first, second) in pairs)
            {
                if (second < 0)
                {
                    if (Arrival(first) is { } alone)
                        Extend(alone);
                    else
                        Wait(new OpenEnd(Start(), atTail: true, first));
                    continue;
                }
                var arrivalFirst = Arrival(first);
                var arrivalSecond = Arrival(second);
                if (arrivalFirst is { } a && arrivalSecond is { } b)
                {
                    runs.Add(Join(a, b, node, lineEdges));
                }
                else if (arrivalFirst is { } through)
                {
                    Extend(through);
                    Wait(new OpenEnd(through.Run, through.AtTail, second));
                }
                else if (arrivalSecond is { } through2)
                {
                    Extend(through2);
                    Wait(new OpenEnd(through2.Run, through2.AtTail, first));
                }
                else
                {
                    var run = Start();
                    var firstIsTail = CompareAngle(Other(lineEdges[first], node), Other(lineEdges[second], node), node) < 0;
                    Wait(new OpenEnd(run, atTail: firstIsTail, first));
                    Wait(new OpenEnd(run, atTail: !firstIsTail, second));
                }
            }
        }
        foreach (var run in runs)
        {
            if (run.Forward is null && run.Points.Count > 1)
                result.Lines.Add([.. run.Points]);
        }
    }

    /// <summary>Joins the two runs arriving at <paramref name="node"/> into a new one, or closes a run meeting itself.</summary>
    private static LineRun Join(OpenEnd a, OpenEnd b, GridPoint node, List<Edge> lineEdges)
    {
        if (ReferenceEquals(a.Run, b.Run))
        {
            a.Run.Points.Add(node);
            a.Run.Points.Insert(0, node);
            return new LineRun([]);
        }
        List<GridPoint> points;
        LineRun head;
        LineRun tail;
        if (a.AtTail != b.AtTail)
        {
            (head, tail) = a.AtTail ? (a.Run, b.Run) : (b.Run, a.Run);
            points = [.. head.Points, node, .. tail.Points];
        }
        else
        {
            var angleOrder = CompareAngle(Other(lineEdges[a.Edge], node), Other(lineEdges[b.Edge], node), node);
            var (first, second) = angleOrder > 0 ? (a, b) : (b, a);
            head = first.Run;
            tail = second.Run;
            var leading = first.AtTail ? head.Points : Reversed(head.Points);
            var trailing = second.AtTail ? Reversed(tail.Points) : tail.Points;
            points = [.. leading, node, .. trailing];
        }
        var joined = new LineRun(points);
        head.Forward = joined;
        head.ForwardToTail = false;
        tail.Forward = joined;
        tail.ForwardToTail = true;
        return joined;
    }

    private static List<GridPoint> Reversed(List<GridPoint> points)
    {
        var copy = new List<GridPoint>(points);
        copy.Reverse();
        return copy;
    }

    /// <summary>Counter-clockwise angle order of two directions from <paramref name="origin"/>, starting at the +x axis.</summary>
    private static int CompareAngle(GridPoint p, GridPoint q, GridPoint origin)
    {
        var (px, py, qx, qy) = (p.X - origin.X, p.Y - origin.Y, q.X - origin.X, q.Y - origin.Y);
        var hp = py < 0 || (py == 0 && px < 0) ? 1 : 0;
        var hq = qy < 0 || (qy == 0 && qx < 0) ? 1 : 0;
        if (hp != hq)
            return hp.CompareTo(hq);
        var cross = ((Int128)px * qy) - ((Int128)py * qx);
        return cross > 0 ? -1 : cross < 0 ? 1 : 0;
    }

    private static GridPoint Other(Edge edge, GridPoint end) => edge.Low == end ? edge.High : edge.Low;
}
