namespace SqlServerSimulator.Storage.Spatial;

/// <summary>One closed ring of a polygon, with the role and orientation the side tests read.</summary>
internal sealed class PlanarRing(PlanarPoint[] points, bool isShell)
{
    public readonly PlanarPoint[] Points = points;

    /// <summary>True for a polygon's exterior ring, false for an interior (hole) ring.</summary>
    public readonly bool IsShell = isShell;

    public readonly double SignedArea = SpatialTopology.SignedRingArea(points);

    /// <summary>
    /// True when the enclosing polygon's interior lies left of each edge as the
    /// ring is written: a counter-clockwise shell encloses its interior on the
    /// left, and a counter-clockwise hole encloses the polygon's *exterior*
    /// there, so the two roles read opposite.
    /// </summary>
    public bool InteriorOnLeft => (this.SignedArea > 0) == this.IsShell;
}

/// <summary>
/// One side of a relate comparison, flattened into the three OGC component
/// classes: isolated points, line segments and polygon rings.
/// </summary>
/// <remarks>
/// <para>Interior and boundary are the <b>per-class unions</b>, not a
/// normalized point set, which is what real does: in
/// <c>GEOMETRYCOLLECTION(POINT(0 0), LINESTRING(0 0, 2 2))</c> the origin is
/// reported as both interior (the point member) and boundary (the line's
/// endpoint) by <c>STRelate</c>.</para>
/// <para>A line component's boundary is the <b>mod-2</b> rule across every
/// line figure in the instance — a vertex shared by two figures is not a
/// boundary point, one shared by three is. A point in that boundary set is not
/// in the line interior even where another figure runs through it.</para>
/// </remarks>
internal sealed class SpatialRelateOperand
{
    public readonly List<PlanarPoint> Points = [];

    public readonly List<PlanarSegment> LineSegments = [];

    public readonly HashSet<PlanarPoint> LineBoundary = [];

    public readonly List<PlanarRing> Rings = [];

    public readonly List<PlanarSegment> RingSegments = [];

    /// <summary>Topological dimension, matching <c>STDimension()</c>: -1 for an empty instance.</summary>
    public readonly int Dimension;

    /// <summary>Extent of every coordinate the operand carries; inverted when the operand is empty, so it meets nothing.</summary>
    public readonly double MinX;
    public readonly double MinY;
    public readonly double MaxX;
    public readonly double MaxY;

    public SpatialRelateOperand(SpatialShape shape)
    {
        this.Dimension = shape.Dimension;
        var endpoints = new List<PlanarPoint>();
        Collect(shape, endpoints);
        foreach (var endpoint in endpoints)
        {
            if (!this.LineBoundary.Add(endpoint))
                _ = this.LineBoundary.Remove(endpoint);
        }
        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;
        void Extend(PlanarPoint point)
        {
            minX = Math.Min(minX, point.X);
            minY = Math.Min(minY, point.Y);
            maxX = Math.Max(maxX, point.X);
            maxY = Math.Max(maxY, point.Y);
        }

        foreach (var point in this.Points)
            Extend(point);
        foreach (var segment in this.LineSegments)
        {
            Extend(segment.A);
            Extend(segment.B);
        }
        foreach (var segment in this.RingSegments)
        {
            Extend(segment.A);
            Extend(segment.B);
        }
        this.MinX = minX;
        this.MinY = minY;
        this.MaxX = maxX;
        this.MaxY = maxY;
    }

    public bool IsEmpty => this.Points.Count == 0 && this.LineSegments.Count == 0 && this.RingSegments.Count == 0;

    /// <summary>
    /// Dimension of the boundary: 1 once any polygon ring is present, 0 for a
    /// line whose mod-2 endpoint set isn't empty, and -1 when there is none —
    /// a point set and a closed ring both have an empty boundary.
    /// </summary>
    public int BoundaryDimension => this.RingSegments.Count > 0 ? 1 : this.LineBoundary.Count > 0 ? 0 : SpatialRelate.False;

    /// <summary>True when the two extents touch or overlap, which is the only way any cell but the four outer ones can be non-empty.</summary>
    public bool ExtentMeets(SpatialRelateOperand other) =>
        this.MinX <= other.MaxX && other.MinX <= this.MaxX && this.MinY <= other.MaxY && other.MinY <= this.MaxY;

    private void Collect(SpatialShape shape, List<PlanarPoint> lineEndpoints)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.MultiPoint:
                foreach (var figure in shape.Figures)
                {
                    foreach (var coordinate in figure)
                        this.Points.Add(PlanarPoint.From(coordinate));
                }
                break;
            case SpatialShapeType.LineString:
            case SpatialShapeType.MultiLineString:
                foreach (var figure in shape.Figures)
                {
                    if (figure.Length == 0)
                        continue;
                    lineEndpoints.Add(PlanarPoint.From(figure[0]));
                    lineEndpoints.Add(PlanarPoint.From(figure[^1]));
                    AddRun(figure, this.LineSegments);
                }
                break;
            case SpatialShapeType.Polygon:
            case SpatialShapeType.MultiPolygon:
                for (var i = 0; i < shape.Figures.Length; i++)
                {
                    var figure = shape.Figures[i];
                    if (figure.Length == 0)
                        continue;
                    var ring = new PlanarPoint[figure.Length];
                    for (var j = 0; j < figure.Length; j++)
                        ring[j] = PlanarPoint.From(figure[j]);
                    this.Rings.Add(new(ring, isShell: i == 0));
                    AddRun(figure, this.RingSegments);
                }
                break;
            default:
                break;
        }
        foreach (var child in shape.Children)
            Collect(child, lineEndpoints);
    }

    private static void AddRun(SpatialCoordinate[] figure, List<PlanarSegment> into)
    {
        for (var i = 1; i < figure.Length; i++)
        {
            var segment = new PlanarSegment(PlanarPoint.From(figure[i - 1]), PlanarPoint.From(figure[i]));
            if (!segment.IsDegenerate)
                into.Add(segment);
        }
    }

    /// <summary>
    /// Which of interior / boundary contain <paramref name="p"/>. Both false
    /// means the point is in the exterior; both true is reachable, since the
    /// component classes aren't normalized against each other.
    /// </summary>
    public (bool Interior, bool Boundary) Locate(PlanarPoint p)
    {
        var interior = false;
        var boundary = false;
        foreach (var point in this.Points)
        {
            if (point == p)
            {
                interior = true;
                break;
            }
        }
        if (SpatialTopology.OnAnySegment(p, this.LineSegments))
        {
            if (this.LineBoundary.Contains(p))
                boundary = true;
            else
                interior = true;
        }
        if (this.RingSegments.Count > 0)
        {
            if (SpatialTopology.OnAnySegment(p, this.RingSegments))
                boundary = true;
            else if (SpatialTopology.IsInsideRings(p, this.RingSegments))
                interior = true;
        }
        return (interior, boundary);
    }

    /// <summary>
    /// Interior or exterior of the two-dimensional face touching
    /// <paramref name="piece"/> on the named side. A face point is never on a
    /// boundary, so this answers only <see cref="SpatialRelate.Interior"/> or
    /// <see cref="SpatialRelate.Exterior"/>.
    /// </summary>
    /// <remarks>
    /// When the piece runs along one of this operand's ring edges the two sides
    /// differ, and the covering ring's orientation names which is which — real
    /// rejects an instance whose rings share a one-dimensional stretch, so at
    /// most one ring can cover the piece. Otherwise the piece is off this
    /// operand's boundary entirely and both sides read the same as its midpoint.
    /// </remarks>
    public int AreaSide(PlanarSegment piece, bool leftSide)
    {
        if (this.RingSegments.Count == 0)
            return SpatialRelate.Exterior;
        var midpoint = piece.Midpoint;
        foreach (var ring in this.Rings)
        {
            for (var i = 1; i < ring.Points.Length; i++)
            {
                var from = ring.Points[i - 1];
                var to = ring.Points[i];
                if (from == to || !SpatialTopology.OnSegment(midpoint, from, to))
                    continue;
                var sameDirection = ((piece.B.X - piece.A.X) * (to.X - from.X)) + ((piece.B.Y - piece.A.Y) * (to.Y - from.Y)) > 0;
                var interiorOnLeft = sameDirection ? ring.InteriorOnLeft : !ring.InteriorOnLeft;
                return leftSide == interiorOnLeft ? SpatialRelate.Interior : SpatialRelate.Exterior;
            }
        }
        return SpatialTopology.IsInsideRings(midpoint, this.RingSegments) ? SpatialRelate.Interior : SpatialRelate.Exterior;
    }
}

/// <summary>The eight topological predicates real exposes as instance methods.</summary>
internal enum SpatialPredicateKind
{
    Contains,
    Crosses,
    Disjoint,
    Equals,
    Intersects,
    Overlaps,
    Touches,
    Within,
}

/// <summary>
/// The DE-9IM engine behind <c>geometry</c>'s topological predicates.
/// </summary>
/// <remarks>
/// <para><see cref="Matrix(SpatialShape, SpatialShape)"/> computes the nine intersection dimensions
/// directly rather than through a labelled overlay: every input segment is
/// noded against every other, and the resulting arrangement's nodes, edge
/// pieces and adjacent faces are each classified against both operands. A node
/// contributes dimension 0 to its cell, an edge piece 1, and a face 2 — the
/// exterior/exterior cell is 2 unconditionally, since the plane is unbounded.
/// The predicates are then mask matches over that matrix, which is exactly how
/// <c>STRelate</c> exposes them. Operands whose extents don't meet skip the
/// arrangement altogether, since each one's interior and boundary then sit
/// whole in the other's exterior.</para>
/// <para>Every operand reaching here is one real considers valid (an invalid
/// instance raises 24144 before dispatch), which is what lets the area tests
/// use the even-odd rule and lets a boundary piece have a single covering
/// ring.</para>
/// </remarks>
internal static class SpatialRelate
{
    public const int Interior = 0;
    public const int Boundary = 1;
    public const int Exterior = 2;

    /// <summary>Cell value for an empty intersection — DE-9IM's <c>F</c>.</summary>
    public const int False = -1;

    /// <summary>
    /// The nine intersection dimensions in row-major
    /// interior/boundary/exterior order, each -1 (empty) or 0 / 1 / 2.
    /// </summary>
    public static int[] Matrix(SpatialShape a, SpatialShape b) => Matrix(new SpatialRelateOperand(a), new SpatialRelateOperand(b));

    private static int[] Matrix(SpatialRelateOperand left, SpatialRelateOperand right)
    {
        var cells = new int[9];
        Array.Fill(cells, False);
        // Both exteriors are unbounded, so they always share a plane's worth of area.
        cells[(Exterior * 3) + Exterior] = 2;
        if (!left.ExtentMeets(right))
        {
            // Nothing of either operand can meet the other, so each one's
            // interior and boundary sit whole in the other's exterior. Taking
            // the shortcut keeps a spatial filter that misses from paying for a
            // full arrangement of a many-vertex border.
            cells[(Interior * 3) + Exterior] = left.Dimension;
            cells[(Boundary * 3) + Exterior] = left.BoundaryDimension;
            cells[(Exterior * 3) + Interior] = right.Dimension;
            cells[(Exterior * 3) + Boundary] = right.BoundaryDimension;
            return cells;
        }

        var segments = new List<PlanarSegment>(left.LineSegments.Count + left.RingSegments.Count
            + right.LineSegments.Count + right.RingSegments.Count);
        segments.AddRange(left.LineSegments);
        segments.AddRange(left.RingSegments);
        segments.AddRange(right.LineSegments);
        segments.AddRange(right.RingSegments);

        var nodes = new HashSet<PlanarPoint>();
        foreach (var segment in segments)
        {
            _ = nodes.Add(segment.A);
            _ = nodes.Add(segment.B);
        }
        foreach (var point in left.Points)
            _ = nodes.Add(point);
        foreach (var point in right.Points)
            _ = nodes.Add(point);
        NodeIntersections(segments, nodes);

        foreach (var node in nodes)
            Bump(cells, left.Locate(node), right.Locate(node), 0);

        foreach (var piece in Split(segments, nodes))
        {
            var midpoint = piece.Midpoint;
            Bump(cells, left.Locate(midpoint), right.Locate(midpoint), 1);
            Face(cells, left, right, piece, leftSide: true);
            Face(cells, left, right, piece, leftSide: false);
        }
        return cells;
    }

    private static void Face(int[] cells, SpatialRelateOperand left, SpatialRelateOperand right, PlanarSegment piece, bool leftSide)
    {
        var row = left.AreaSide(piece, leftSide);
        var column = right.AreaSide(piece, leftSide);
        var index = (row * 3) + column;
        if (cells[index] < 2)
            cells[index] = 2;
    }

    private static void Bump(int[] cells, (bool Interior, bool Boundary) left, (bool Interior, bool Boundary) right, int dimension)
    {
        foreach (var row in Locations(left))
        {
            foreach (var column in Locations(right))
            {
                var index = (row * 3) + column;
                if (cells[index] < dimension)
                    cells[index] = dimension;
            }
        }
    }

    /// <summary>The location indices a classification covers; neither flag set means the exterior.</summary>
    private static int[] Locations((bool Interior, bool Boundary) location) => location switch
    {
        (true, true) => [Interior, Boundary],
        (true, false) => [Interior],
        (false, true) => [Boundary],
        _ => [Exterior],
    };

    /// <summary>Adds every pairwise meeting point of the input segments to the node set.</summary>
    private static void NodeIntersections(List<PlanarSegment> segments, HashSet<PlanarPoint> nodes)
    {
        foreach (var (first, second) in SpatialTopology.CandidatePairs(segments))
            SpatialTopology.CollectIntersections(segments[first], segments[second], nodes);
    }

    /// <summary>
    /// Cuts every segment at the nodes lying on it and returns the distinct
    /// pieces. Two collinear inputs covering the same stretch produce the same
    /// piece, and the duplicate is dropped — a piece is a point set, not an
    /// occurrence.
    /// </summary>
    private static List<PlanarSegment> Split(List<PlanarSegment> segments, HashSet<PlanarPoint> nodes)
    {
        var ordered = nodes.ToArray();
        var pieces = new List<PlanarSegment>();
        var seen = new HashSet<(PlanarPoint, PlanarPoint)>();
        var onSegment = new List<PlanarPoint>();
        foreach (var segment in segments)
        {
            onSegment.Clear();
            foreach (var node in ordered)
            {
                if (node.X >= segment.MinX && node.X <= segment.MaxX
                    && node.Y >= segment.MinY && node.Y <= segment.MaxY
                    && SpatialTopology.OnSegment(node, segment))
                {
                    onSegment.Add(node);
                }
            }
            var alongX = Math.Abs(segment.B.X - segment.A.X) >= Math.Abs(segment.B.Y - segment.A.Y);
            var ascending = alongX ? segment.B.X > segment.A.X : segment.B.Y > segment.A.Y;
            onSegment.Sort((x, y) =>
            {
                var compared = alongX ? x.X.CompareTo(y.X) : x.Y.CompareTo(y.Y);
                if (compared == 0)
                    compared = alongX ? x.Y.CompareTo(y.Y) : x.X.CompareTo(y.X);
                return ascending ? compared : -compared;
            });
            for (var i = 1; i < onSegment.Count; i++)
            {
                var piece = new PlanarSegment(onSegment[i - 1], onSegment[i]);
                if (piece.IsDegenerate)
                    continue;
                var key = Ordered(piece);
                if (seen.Add(key))
                    pieces.Add(piece);
            }
        }
        return pieces;
    }

    /// <summary>Endpoint pair in a canonical order, so a piece and its reverse hash alike.</summary>
    private static (PlanarPoint, PlanarPoint) Ordered(PlanarSegment piece)
    {
        var forward = piece.A.X < piece.B.X || (piece.A.X == piece.B.X && piece.A.Y <= piece.B.Y);
        return forward ? (piece.A, piece.B) : (piece.B, piece.A);
    }

    /// <summary>
    /// Whether the matrix satisfies a DE-9IM pattern. <c>T</c> accepts any
    /// non-empty intersection, <c>F</c> only an empty one, a digit the exact
    /// dimension, and <c>*</c> anything.
    /// </summary>
    public static bool Matches(int[] cells, ReadOnlySpan<char> mask)
    {
        for (var i = 0; i < 9; i++)
        {
            var satisfied = mask[i] switch
            {
                '*' => true,
                'F' => cells[i] == False,
                'T' => cells[i] >= 0,
                _ => cells[i] == mask[i] - '0',
            };
            if (!satisfied)
                return false;
        }
        return true;
    }

    /// <summary>Evaluates one of the eight named predicates over a pair of instances.</summary>
    public static bool Evaluate(SpatialPredicateKind kind, SpatialShape a, SpatialShape b)
    {
        var left = new SpatialRelateOperand(a);
        var right = new SpatialRelateOperand(b);
        // Real answers true for two empty instances even though the matrix has
        // no non-empty interior cell for the STEquals pattern to match.
        if (kind == SpatialPredicateKind.Equals && left.IsEmpty && right.IsEmpty)
            return true;
        var cells = Matrix(left, right);
        return Evaluate(kind, cells, left.Dimension, right.Dimension);
    }

    private static bool Evaluate(SpatialPredicateKind kind, int[] cells, int leftDimension, int rightDimension) => kind switch
    {
        SpatialPredicateKind.Contains => Matches(cells, "T*****FF*"),
        SpatialPredicateKind.Crosses => Crosses(cells, leftDimension, rightDimension),
        SpatialPredicateKind.Disjoint => Disjoint(cells),
        SpatialPredicateKind.Equals => Matches(cells, "T*F**FFF*"),
        SpatialPredicateKind.Intersects => !Disjoint(cells),
        SpatialPredicateKind.Overlaps => Overlaps(cells, leftDimension, rightDimension),
        SpatialPredicateKind.Touches => Touches(cells, leftDimension, rightDimension),
        _ => Matches(cells, "T*F**F***"),
    };

    private static bool Disjoint(int[] cells) => Matches(cells, "FF*FF****");

    /// <summary>
    /// <c>STCrosses</c> is defined only when the receiver is the
    /// lower-dimensional operand, plus the line-on-line case — real does not
    /// symmetrize it, so a polygon crossed by a line answers false while the
    /// line answers true.
    /// </summary>
    private static bool Crosses(int[] cells, int leftDimension, int rightDimension) =>
        leftDimension == 1 && rightDimension == 1
            ? cells[(Interior * 3) + Interior] == 0
            : leftDimension < rightDimension && Matches(cells, "T*T***T**");

    /// <summary>
    /// <c>STOverlaps</c> needs both operands at the same dimension, and a
    /// one-dimensional pair must share exactly a one-dimensional stretch —
    /// two lines meeting at a point overlap nothing.
    /// </summary>
    private static bool Overlaps(int[] cells, int leftDimension, int rightDimension) =>
        leftDimension == rightDimension
        && (leftDimension == 1 ? Matches(cells, "1*T***T**") : Matches(cells, "T*T***T**"));

    /// <summary>
    /// <c>STTouches</c> asks for contact that misses both interiors. Two
    /// zero-dimensional operands can never touch, having no boundary of their
    /// own to meet through.
    /// </summary>
    private static bool Touches(int[] cells, int leftDimension, int rightDimension) =>
        (leftDimension != 0 || rightDimension != 0)
        && cells[(Interior * 3) + Interior] == False
        && (Matches(cells, "*T*******") || Matches(cells, "***T*****") || Matches(cells, "****T****"));
}
