namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// One polygon of a round-earth operand: the ring ranges it owns, and points
/// known to be inside it.
/// </summary>
/// <remarks>
/// A geography ring puts its interior on the <b>left</b> of the direction it is
/// written, so a point a short step off a ring's left side is inside the polygon
/// by construction. That is what makes a clockwise square the whole globe less
/// itself rather than an error: its left side is the outside, and every distant
/// point is in it. Several such points are kept because the crossing count a
/// containment test runs from one of them can hit a degeneracy, and the next
/// reference then answers.
/// </remarks>
internal sealed class GeodeticArea(List<(int First, int Count)> rings, SpatialVector[] references)
{
    public readonly List<(int First, int Count)> Rings = rings;

    /// <summary>Interior points a containment test counts crossings from.</summary>
    public readonly SpatialVector[] References = references;
}

/// <summary>
/// One side of a round-earth relate comparison, flattened into the same three
/// OGC component classes <see cref="SpatialRelateOperand"/> uses — isolated
/// points, line edges and polygon rings — with every edge a great elliptic arc.
/// </summary>
internal sealed class GeodeticRelateOperand
{
    public readonly List<SpatialVector> Points = [];

    public readonly List<RoundEarthEdge> LineEdges = [];

    public readonly List<RoundEarthEdge> RingEdges = [];

    /// <summary>Mod-2 endpoint set of the line figures, which is a line's OGC boundary.</summary>
    public readonly List<SpatialVector> LineBoundary = [];

    /// <summary>One entry per polygon, so overlapping members of a collection each answer for themselves.</summary>
    public readonly List<GeodeticArea> Areas = [];

    /// <summary>Topological dimension, matching <c>STDimension()</c>: -1 for an empty instance.</summary>
    public readonly int Dimension;

    /// <summary>Centre of a sphere containing every point of the instance, arc bulges included.</summary>
    public readonly SpatialVector Centre;

    public readonly double Radius;

    /// <summary>
    /// Whether the instance's whole point set sits inside that sphere. It does
    /// not when a ring is wound so that its interior is the <i>outside</i> — a
    /// clockwise square names the globe less itself, and everything far away
    /// belongs to it.
    /// </summary>
    public readonly bool StaysInsideExtent;

    public GeodeticRelateOperand(SpatialShape shape)
    {
        this.Dimension = shape.Dimension;
        var endpoints = new List<SpatialVector>();
        Collect(shape, endpoints);
        foreach (var endpoint in endpoints)
        {
            var at = IndexOf(this.LineBoundary, endpoint);
            if (at < 0)
                this.LineBoundary.Add(endpoint);
            else
                this.LineBoundary.RemoveAt(at);
        }
        (this.Centre, this.Radius) = BoundingSphere();
        this.StaysInsideExtent = this.Areas.Count == 0 || !ReachesBeyondExtent();
    }

    /// <summary>
    /// True when the region reaches outside the bounding sphere. Every ring is
    /// inside that sphere, so the surface outside it carries no boundary and
    /// one probe there settles the whole of it — the antipode of the centre,
    /// which is as far outside as a surface point gets.
    /// </summary>
    private bool ReachesBeyondExtent()
    {
        // A sphere wide enough to swallow the ellipsoid leaves nowhere to
        // probe, and the shortcut it would authorize can never fire anyway.
        return this.Radius >= SpatialEllipsoid.SemiMajor
            || this.Centre.Length <= SpatialGeodeticTopology.NodeTolerance
            || RegionContains(SpatialEllipsoid.OntoSurface(this.Centre * -1));
    }

    public bool IsEmpty => this.Points.Count == 0 && this.LineEdges.Count == 0 && this.RingEdges.Count == 0;

    /// <summary>
    /// Dimension of the boundary: 1 once any polygon ring is present, 0 for a
    /// line whose mod-2 endpoint set isn't empty, and -1 when there is none.
    /// </summary>
    public int BoundaryDimension => this.RingEdges.Count > 0 ? 1 : this.LineBoundary.Count > 0 ? 0 : SpatialRelate.False;

    /// <summary>
    /// True when the two bounding spheres touch, which is the only way any cell
    /// but the four outer ones can be non-empty — unless one of the operands
    /// reaches beyond its own extent, in which case there is nothing to rule out.
    /// </summary>
    public bool ExtentMeets(GeodeticRelateOperand other)
    {
        if (this.IsEmpty || other.IsEmpty)
            return false;
        if (!this.StaysInsideExtent || !other.StaysInsideExtent)
            return true;
        var reach = this.Radius + other.Radius + SpatialGeodeticTopology.NodeTolerance;
        return (this.Centre - other.Centre).SquaredLength <= reach * reach;
    }

    /// <summary>Which of interior / boundary contain <paramref name="point"/>; neither means the exterior.</summary>
    public (bool Interior, bool Boundary) Locate(SpatialVector point)
    {
        var interior = false;
        var boundary = false;
        foreach (var isolated in this.Points)
        {
            if (SpatialGeodeticTopology.Same(isolated, point))
            {
                interior = true;
                break;
            }
        }
        if (SpatialGeodeticTopology.OnAnyEdge(point, this.LineEdges))
        {
            if (IndexOf(this.LineBoundary, point) >= 0)
                boundary = true;
            else
                interior = true;
        }
        if (this.RingEdges.Count > 0)
        {
            if (SpatialGeodeticTopology.OnAnyEdge(point, this.RingEdges))
                boundary = true;
            else if (RegionContains(point))
                interior = true;
        }
        return (interior, boundary);
    }

    /// <summary>
    /// Which side of an arrangement piece is this operand's area interior. A
    /// face point is never on a boundary, so this answers only
    /// <see cref="SpatialRelate.Interior"/> or <see cref="SpatialRelate.Exterior"/>.
    /// </summary>
    /// <remarks>
    /// Where the piece runs along one of this operand's own ring edges the
    /// answer is the ring's direction: a geography ring always puts its
    /// interior on its left, and a valid instance lets at most one ring cover
    /// the piece. That reading costs a lookup among the edges near the piece
    /// where the general test walks the whole ring set, which is what keeps a
    /// self-comparison of a many-vertex border from being quadratic.
    /// </remarks>
    public int RegionSide(in RoundEarthEdge piece, bool leftSide, SpatialVector probe)
    {
        if (this.RingEdges.Count == 0)
            return SpatialRelate.Exterior;
        var midpoint = piece.Arc.At(0.5);
        foreach (var edge in this.RingEdges)
        {
            if (!SpatialGeodeticTopology.Near(midpoint, edge) || !edge.Arc.Contains(midpoint))
                continue;
            var sameDirection = (piece.To - piece.From).Dot(edge.To - edge.From) > 0;
            return leftSide == sameDirection ? SpatialRelate.Interior : SpatialRelate.Exterior;
        }
        return RegionContains(probe) ? SpatialRelate.Interior : SpatialRelate.Exterior;
    }

    /// <summary>True when the point lies in the region any of the operand's polygons bounds.</summary>
    public bool RegionContains(SpatialVector point)
    {
        foreach (var area in this.Areas)
        {
            if (Contains(area, point))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether one polygon holds the point, asked from each of its interior
    /// reference points in turn until one gives a reliable crossing count.
    /// </summary>
    private bool Contains(GeodeticArea area, SpatialVector point)
    {
        foreach (var reference in area.References)
        {
            if (SpatialGeodeticTopology.SameSide(reference, point, this.RingEdges, area.Rings) is { } inside)
                return inside;
        }
        return false;
    }

    private static int IndexOf(List<SpatialVector> points, SpatialVector point)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (SpatialGeodeticTopology.Same(points[i], point))
                return i;
        }
        return -1;
    }

    private void Collect(SpatialShape shape, List<SpatialVector> lineEndpoints)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.Point:
            case SpatialShapeType.MultiPoint:
                foreach (var figure in shape.Figures)
                {
                    foreach (var coordinate in figure)
                        this.Points.Add(SpatialEllipsoid.ToCartesian(coordinate));
                }
                break;
            case SpatialShapeType.LineString:
            case SpatialShapeType.MultiLineString:
                foreach (var figure in shape.Figures)
                {
                    if (figure.Length == 0)
                        continue;
                    lineEndpoints.Add(SpatialEllipsoid.ToCartesian(figure[0]));
                    lineEndpoints.Add(SpatialEllipsoid.ToCartesian(figure[^1]));
                    AddRun(figure, this.LineEdges);
                }
                break;
            case SpatialShapeType.Polygon:
                CollectPolygon(shape);
                break;
            default:
                break;
        }
        foreach (var child in shape.Children)
            Collect(child, lineEndpoints);
    }

    private void CollectPolygon(SpatialShape shape)
    {
        var rings = new List<(int First, int Count)>();
        foreach (var figure in shape.Figures)
        {
            if (figure.Length == 0)
                continue;
            var first = this.RingEdges.Count;
            AddRun(figure, this.RingEdges);
            var count = this.RingEdges.Count - first;
            if (count > 0)
                rings.Add((first, count));
        }
        if (rings.Count == 0)
            return;
        this.Areas.Add(new(rings, InteriorReferences(rings[0])));
    }

    /// <summary>
    /// Points known to be inside the polygon: a short step off the left side of
    /// the first ring, taken at three places so a containment count that runs
    /// into a degeneracy from one of them can be re-asked from another.
    /// </summary>
    private SpatialVector[] InteriorReferences((int First, int Count) ring)
    {
        var references = new SpatialVector[3];
        for (var i = 0; i < references.Length; i++)
        {
            var edge = this.RingEdges[ring.First + (i * ring.Count / references.Length)];
            references[i] = SpatialGeodeticTopology.Offset(edge, 0.25 + (0.25 * i), leftSide: true);
        }
        return references;
    }

    private static void AddRun(SpatialCoordinate[] figure, List<RoundEarthEdge> into)
    {
        for (var i = 1; i < figure.Length; i++)
        {
            var from = SpatialEllipsoid.ToCartesian(figure[i - 1]);
            var to = SpatialEllipsoid.ToCartesian(figure[i]);
            if (!SpatialGeodeticTopology.Same(from, to))
                into.Add(new(from, to));
        }
    }

    /// <summary>
    /// A sphere containing the whole instance. Each edge is bounded by its
    /// chord's sphere grown by the arc's own bulge — an arc of chord <c>c</c>
    /// rises at most <c>R - √(R² - (c/2)²)</c> above its chord, and taking the
    /// ellipsoid's smallest radius for <c>R</c> keeps that on the safe side.
    /// </summary>
    private (SpatialVector Centre, double Radius) BoundingSphere()
    {
        var centres = new List<(SpatialVector Centre, double Radius)>(this.Points.Count + this.LineEdges.Count + this.RingEdges.Count);
        foreach (var point in this.Points)
            centres.Add((point, 0));
        foreach (var edge in this.LineEdges)
            centres.Add((edge.Centre, SpatialGeodeticTopology.Reach(edge)));
        foreach (var edge in this.RingEdges)
            centres.Add((edge.Centre, SpatialGeodeticTopology.Reach(edge)));
        if (centres.Count == 0)
            return (new(0, 0, 0), double.NegativeInfinity);
        var sum = new SpatialVector(0, 0, 0);
        foreach (var (centre, _) in centres)
            sum += centre;
        var mean = sum * (1.0 / centres.Count);
        var radius = 0.0;
        foreach (var (centre, reach) in centres)
            radius = Math.Max(radius, (centre - mean).Length + reach);
        return (mean, radius);
    }
}

/// <summary>
/// The DE-9IM engine behind <c>geography</c>'s topological predicates — the
/// round-earth counterpart of <see cref="SpatialRelate"/>.
/// </summary>
/// <remarks>
/// <para>The construction is the planar one's: every edge is noded against
/// every other, and the arrangement's nodes, edge pieces and adjacent faces are
/// each classified against both operands, a node contributing dimension 0 to
/// its cell, a piece 1 and a face 2. What changes is every primitive underneath
/// — an edge is a great elliptic arc, two edges meet where their central planes
/// cut the surface, and "inside" is a winding number rather than a crossing
/// parity, so a ring's written direction decides which side its interior is on.
/// </para>
/// <para>Real exposes no <c>STRelate</c> on <c>geography</c>, so the matrix is
/// internal and only the six predicates real does expose are reachable; the
/// masks are the same ones, since the predicates are defined the same way.
/// </para>
/// </remarks>
internal static class SpatialGeodeticRelate
{
    /// <summary>The nine intersection dimensions in row-major interior/boundary/exterior order.</summary>
    public static int[] Matrix(GeodeticRelateOperand left, GeodeticRelateOperand right)
    {
        var cells = new int[9];
        Array.Fill(cells, SpatialRelate.False);
        // Neither operand can cover the globe, so the two exteriors always
        // share surface — the same unconditional 2 the planar engine reports.
        cells[(SpatialRelate.Exterior * 3) + SpatialRelate.Exterior] = 2;
        if (!left.ExtentMeets(right))
        {
            cells[(SpatialRelate.Interior * 3) + SpatialRelate.Exterior] = left.Dimension;
            cells[(SpatialRelate.Boundary * 3) + SpatialRelate.Exterior] = left.BoundaryDimension;
            cells[(SpatialRelate.Exterior * 3) + SpatialRelate.Interior] = right.Dimension;
            cells[(SpatialRelate.Exterior * 3) + SpatialRelate.Boundary] = right.BoundaryDimension;
            return cells;
        }

        var edges = new List<RoundEarthEdge>(
            left.LineEdges.Count + left.RingEdges.Count + right.LineEdges.Count + right.RingEdges.Count);
        edges.AddRange(left.LineEdges);
        edges.AddRange(left.RingEdges);
        edges.AddRange(right.LineEdges);
        edges.AddRange(right.RingEdges);

        var nodes = new GeodeticNodes();
        foreach (var edge in edges)
        {
            _ = nodes.Add(edge.From);
            _ = nodes.Add(edge.To);
        }
        foreach (var point in left.Points)
            _ = nodes.Add(point);
        foreach (var point in right.Points)
            _ = nodes.Add(point);
        foreach (var (first, second) in SpatialGeodeticTopology.CandidatePairs(edges))
            SpatialGeodeticTopology.CollectIntersections(edges[first], edges[second], nodes);

        foreach (var node in nodes.Points)
            Bump(cells, left.Locate(node), right.Locate(node), 0);

        foreach (var piece in Split(edges, nodes))
        {
            var midpoint = piece.Arc.At(0.5);
            Bump(cells, left.Locate(midpoint), right.Locate(midpoint), 1);
            Face(cells, left, right, piece, leftSide: true);
            Face(cells, left, right, piece, leftSide: false);
        }
        return cells;
    }

    private static void Face(int[] cells, GeodeticRelateOperand left, GeodeticRelateOperand right, in RoundEarthEdge piece, bool leftSide)
    {
        var probe = SpatialGeodeticTopology.Offset(piece, 0.5, leftSide);
        var index = (left.RegionSide(piece, leftSide, probe) * 3) + right.RegionSide(piece, leftSide, probe);
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

    private static int[] Locations((bool Interior, bool Boundary) location) => location switch
    {
        (true, true) => [SpatialRelate.Interior, SpatialRelate.Boundary],
        (true, false) => [SpatialRelate.Interior],
        (false, true) => [SpatialRelate.Boundary],
        _ => [SpatialRelate.Exterior],
    };

    /// <summary>
    /// Cuts every edge at the nodes lying on it and returns the distinct pieces.
    /// Two edges covering the same stretch produce the same piece and the
    /// duplicate is dropped — a piece is a point set, not an occurrence.
    /// </summary>
    private static List<RoundEarthEdge> Split(List<RoundEarthEdge> edges, GeodeticNodes nodes)
    {
        var pieces = new List<RoundEarthEdge>();
        var seen = new HashSet<(int, int)>();
        var onEdge = new List<(double Fraction, int Node)>();
        foreach (var edge in edges)
        {
            onEdge.Clear();
            for (var i = 0; i < nodes.Points.Count; i++)
            {
                var node = nodes.Points[i];
                if (SpatialGeodeticTopology.Near(node, edge) && edge.Arc.Contains(node))
                    onEdge.Add((edge.Arc.FractionOf(node), i));
            }
            onEdge.Sort(static (x, y) => x.Fraction.CompareTo(y.Fraction));
            for (var i = 1; i < onEdge.Count; i++)
            {
                var from = nodes.Points[onEdge[i - 1].Node];
                var to = nodes.Points[onEdge[i].Node];
                if (SpatialGeodeticTopology.Same(from, to))
                    continue;
                var low = Math.Min(onEdge[i - 1].Node, onEdge[i].Node);
                var high = Math.Max(onEdge[i - 1].Node, onEdge[i].Node);
                if (seen.Add((low, high)))
                    pieces.Add(new(from, to));
            }
        }
        return pieces;
    }

    /// <summary>Evaluates one of the predicates real exposes on <c>geography</c> over a pair of instances.</summary>
    public static bool Evaluate(SpatialPredicateKind kind, SpatialShape a, SpatialShape b)
    {
        var left = new GeodeticRelateOperand(a);
        var right = new GeodeticRelateOperand(b);
        // Real answers true for two empty instances even though the matrix has
        // no non-empty interior cell for the STEquals pattern to match.
        if (kind == SpatialPredicateKind.Equals && left.IsEmpty && right.IsEmpty)
            return true;
        var cells = Matrix(left, right);
        return kind switch
        {
            SpatialPredicateKind.Contains => SpatialRelate.Matches(cells, "T*****FF*"),
            SpatialPredicateKind.Disjoint => Disjoint(cells),
            SpatialPredicateKind.Equals => SpatialRelate.Matches(cells, "T*F**FFF*"),
            SpatialPredicateKind.Intersects => !Disjoint(cells),
            SpatialPredicateKind.Overlaps => Overlaps(cells, left.Dimension, right.Dimension),
            _ => SpatialRelate.Matches(cells, "T*F**F***"),
        };
    }

    private static bool Disjoint(int[] cells) => SpatialRelate.Matches(cells, "FF*FF****");

    /// <summary>
    /// <c>STOverlaps</c> needs both operands at the same dimension, and a
    /// one-dimensional pair must share exactly a one-dimensional stretch — two
    /// lines meeting at a point overlap nothing.
    /// </summary>
    private static bool Overlaps(int[] cells, int leftDimension, int rightDimension) =>
        leftDimension == rightDimension
        && (leftDimension == 1 ? SpatialRelate.Matches(cells, "1*T***T**") : SpatialRelate.Matches(cells, "T*T***T**"));
}
