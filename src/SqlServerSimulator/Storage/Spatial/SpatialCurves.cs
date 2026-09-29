namespace SqlServerSimulator.Storage.Spatial;

/// <summary>
/// One circular arc of a planar curve: the circle through its three control
/// points, walked from the first through the second to the third.
/// </summary>
/// <remarks>
/// Control points that don't fix a circle — collinear, or two of them
/// coinciding — make a straight segment from the first to the third, which is
/// how real measures and linearizes them (<c>CIRCULARSTRING(0 0, 1 1, 2 2)</c>
/// linearizes to <c>LINESTRING (0 0, 2 2)</c>). An arc returning to its own
/// start is <see cref="IsPoint"/>: real reports it degenerate and measures it 0.
/// </remarks>
internal readonly struct SpatialArc
{
    public readonly SpatialCoordinate Start;
    public readonly SpatialCoordinate Mid;
    public readonly SpatialCoordinate End;

    /// <summary>The control points fix no circle; the arc is the segment from <see cref="Start"/> to <see cref="End"/>.</summary>
    public readonly bool IsStraight;

    /// <summary>The arc ends where it starts.</summary>
    public readonly bool IsPoint;

    public readonly double CenterX;
    public readonly double CenterY;
    public readonly double Radius;

    /// <summary>Angle of <see cref="Start"/> about the centre, in radians.</summary>
    public readonly double StartAngle;

    /// <summary>Signed angle swept from <see cref="Start"/> to <see cref="End"/>: positive counter-clockwise.</summary>
    public readonly double Sweep;

    public SpatialArc(SpatialCoordinate start, SpatialCoordinate mid, SpatialCoordinate end)
    {
        this.Start = start;
        this.Mid = mid;
        this.End = end;
        this.IsPoint = start.X == end.X && start.Y == end.Y;
        // The circumcentre is taken relative to the first point, which keeps
        // the arithmetic at the scale of the arc rather than of its coordinates.
        double bx = mid.X - start.X, by = mid.Y - start.Y, cx = end.X - start.X, cy = end.Y - start.Y;
        var cross = (bx * cy) - (by * cx);
        if (this.IsPoint || cross == 0)
        {
            this.IsStraight = true;
            return;
        }
        var d = 2 * cross;
        var b2 = (bx * bx) + (by * by);
        var c2 = (cx * cx) + (cy * cy);
        this.CenterX = start.X + (((cy * b2) - (by * c2)) / d);
        this.CenterY = start.Y + (((bx * c2) - (cx * b2)) / d);
        this.Radius = Math.Sqrt(((start.X - this.CenterX) * (start.X - this.CenterX)) + ((start.Y - this.CenterY) * (start.Y - this.CenterY)));
        this.StartAngle = Math.Atan2(start.Y - this.CenterY, start.X - this.CenterX);
        var endAngle = Math.Atan2(end.Y - this.CenterY, end.X - this.CenterX);
        var ccw = Normalize(endAngle - this.StartAngle);
        this.Sweep = cross > 0 ? ccw : ccw - (2 * Math.PI);
    }

    /// <summary>The arc's length: its radius times the angle swept, or the chord for a straight one.</summary>
    public double Length => this.IsPoint
        ? 0
        : this.IsStraight
            ? Math.Sqrt(((this.End.X - this.Start.X) * (this.End.X - this.Start.X)) + ((this.End.Y - this.Start.Y) * (this.End.Y - this.Start.Y)))
            : this.Radius * Math.Abs(this.Sweep);

    /// <summary>
    /// The signed area between the chord and the arc, counter-clockwise
    /// positive — what a ring's shoelace sum over its segment endpoints misses.
    /// </summary>
    public double SegmentArea => this.IsStraight ? 0 : this.Radius * this.Radius / 2 * (this.Sweep - Math.Sin(this.Sweep));

    /// <summary>The point <paramref name="fraction"/> of the way along the arc by angle.</summary>
    public SpatialCoordinate At(double fraction)
    {
        if (this.IsStraight)
            return new SpatialCoordinate(this.Start.X + ((this.End.X - this.Start.X) * fraction), this.Start.Y + ((this.End.Y - this.Start.Y) * fraction), this.Start.Z, this.Start.M);
        var angle = this.StartAngle + (this.Sweep * fraction);
        return new SpatialCoordinate(this.CenterX + (this.Radius * Math.Cos(angle)), this.CenterY + (this.Radius * Math.Sin(angle)), this.Start.Z, Interpolate(this.Start.M, this.End.M, fraction));
    }

    /// <summary>Whether the arc passes through the direction <paramref name="angle"/> about its centre.</summary>
    public bool Covers(double angle)
    {
        var offset = Normalize(angle - this.StartAngle);
        return this.Sweep >= 0 ? offset <= this.Sweep : offset == 0 || offset - (2 * Math.PI) >= this.Sweep;
    }

    /// <summary>
    /// The number of equal steps real cuts this arc into for a tolerance:
    /// the fewest power of two whose deviation <i>estimate</i> <c>r·θ²/8</c>
    /// — the sagitta's leading term, not the sagitta — stays within it
    /// (fitted 404 of 404 against SQL Server 2025, 2026-09-29).
    /// </summary>
    public int Steps(double tolerance)
    {
        if (this.IsStraight)
            return 1;
        var steps = 1;
        while (steps < MaxSteps)
        {
            var theta = this.Sweep / steps;
            if (this.Radius * theta * theta / 8 <= tolerance)
                break;
            steps *= 2;
        }
        return steps;
    }

    /// <summary>A bound on <see cref="Steps"/>, which only a tolerance many orders below the arc's size reaches.</summary>
    private const int MaxSteps = 1 << 20;

    /// <summary>
    /// Appends the arc's points after <see cref="Start"/> — its interior steps,
    /// then <see cref="End"/> itself. The first half of the steps turns the
    /// start about the centre and the second half the end, so the steps meet
    /// in the middle rather than drifting toward the far end.
    /// </summary>
    public void AppendSteps(List<SpatialCoordinate> points, int steps)
    {
        if (!this.IsPoint && !this.IsStraight)
        {
            var step = this.Sweep / steps;
            double sx = this.Start.X - this.CenterX, sy = this.Start.Y - this.CenterY;
            double ex = this.End.X - this.CenterX, ey = this.End.Y - this.CenterY;
            for (var k = 1; k < steps; k++)
            {
                var fromStart = 2 * k <= steps;
                var angle = fromStart ? k * step : -(steps - k) * step;
                // A step landing on a quarter turn lands exactly: cos(π/2) is
                // 6e-17 rather than 0, which would leave the step a hair off
                // an axis-aligned control point real's own steps hit.
                double cos = Math.Cos(angle), sin = Math.Sin(angle);
                if (Math.Abs(cos) < 1e-15)
                    cos = 0;
                if (Math.Abs(sin) < 1e-15)
                    sin = 0;
                var (vx, vy) = fromStart ? (sx, sy) : (ex, ey);
                points.Add(new SpatialCoordinate(
                    this.CenterX + ((vx * cos) - (vy * sin)),
                    this.CenterY + ((vx * sin) + (vy * cos)),
                    this.Start.Z,
                    Interpolate(this.Start.M, this.End.M, (double)k / steps)));
            }
        }
        if (!this.IsPoint)
            points.Add(this.End);
    }

    /// <summary>
    /// The arc's bounding box: its endpoints, widened to the circle's extreme
    /// in every axis direction the arc passes through.
    /// </summary>
    public void Extend(ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        Include(this.Start, ref minX, ref maxX, ref minY, ref maxY);
        Include(this.End, ref minX, ref maxX, ref minY, ref maxY);
        if (this.IsStraight)
            return;
        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var angle = quadrant * Math.PI / 2;
            if (!this.Covers(angle))
                continue;
            var x = this.CenterX + (this.Radius * Math.Round(Math.Cos(angle)));
            var y = this.CenterY + (this.Radius * Math.Round(Math.Sin(angle)));
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }
    }

    private static void Include(SpatialCoordinate point, ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        minX = Math.Min(minX, point.X);
        maxX = Math.Max(maxX, point.X);
        minY = Math.Min(minY, point.Y);
        maxY = Math.Max(maxY, point.Y);
    }

    private static double? Interpolate(double? from, double? to, double fraction) =>
        from is { } a && to is { } b ? a + ((b - a) * fraction) : null;

    /// <summary>An angle folded into [0, 2π).</summary>
    public static double Normalize(double angle)
    {
        var folded = angle % (2 * Math.PI);
        return folded < 0 ? folded + (2 * Math.PI) : folded;
    }
}

/// <summary>
/// The curved kinds' walk and measures, and their linearization — what
/// <c>STCurveToLine()</c> and <c>CurveToLineWithTolerance()</c> answer and what
/// every operation without a curve-aware path reads instead.
/// </summary>
/// <remarks>
/// <para>A figure is a run of segments: a <see cref="SpatialFigureType.Line"/>
/// figure a straight segment between each pair of consecutive points, an
/// <see cref="SpatialFigureType.Arc"/> figure an arc through each overlapping
/// triple, and a <see cref="SpatialFigureType.Composite"/> figure whatever its
/// <see cref="SpatialSegmentType"/> run spells out.</para>
/// </remarks>
internal static class SpatialCurves
{
    /// <summary>
    /// The steps real cuts every arc into when an operation without a curve
    /// path of its own reads a curved instance — a fixed count whatever the
    /// arc's size or sweep: <c>STPointOnSurface</c> answers from the first of
    /// 2,048 steps on a 1° arc and a 350° one alike (probed 2026-09-29).
    /// </summary>
    public const int OperationSteps = 2048;

    /// <summary>One segment of a figure: a straight one from point <c>Start</c>, or an arc through <c>Start</c>, <c>Start + 1</c> and <c>Start + 2</c>.</summary>
    public readonly struct Segment(bool isArc, int start)
    {
        public readonly bool IsArc = isArc;
        public readonly int Start = start;
    }

    /// <summary>The segments of figure <paramref name="index"/> of <paramref name="shape"/>, in order.</summary>
    public static List<Segment> Segments(SpatialShape shape, int index)
    {
        var figure = shape.Figures[index];
        var segments = new List<Segment>();
        switch (shape.FigureType(index))
        {
            case SpatialFigureType.Arc:
                for (var i = 0; i + 2 < figure.Length; i += 2)
                    segments.Add(new Segment(true, i));
                break;
            case SpatialFigureType.Composite:
                var at = 0;
                foreach (var segment in shape.Segments![index]!)
                {
                    var isArc = segment is SpatialSegmentType.Arc or SpatialSegmentType.FirstArc;
                    segments.Add(new Segment(isArc, at));
                    at += isArc ? 2 : 1;
                }
                break;
            default:
                for (var i = 0; i + 1 < figure.Length; i++)
                    segments.Add(new Segment(false, i));
                break;
        }
        return segments;
    }

    /// <summary>
    /// A composite figure as the elements it was written as: each run the
    /// segment table opens with a <c>First</c> kind is one element — lines or
    /// arcs — holding its own copy of the point it shares with the previous.
    /// </summary>
    public static List<(bool IsArc, SpatialCoordinate[] Points)> Elements(SpatialShape shape, int index)
    {
        var points = shape.Figures[index];
        var segments = shape.Segments![index]!;
        var elements = new List<(bool, SpatialCoordinate[])>();
        var at = 0;
        var i = 0;
        while (i < segments.Length)
        {
            var isArc = segments[i] is SpatialSegmentType.Arc or SpatialSegmentType.FirstArc;
            var end = i + 1;
            while (end < segments.Length && segments[end] == (isArc ? SpatialSegmentType.Arc : SpatialSegmentType.Line))
                end++;
            var consumed = (end - i) * (isArc ? 2 : 1);
            elements.Add((isArc, points[at..(at + consumed + 1)]));
            at += consumed;
            i = end;
        }
        return elements;
    }

    /// <summary>Joins elements back into one composite figure — the inverse of <see cref="Elements"/>.</summary>
    public static (SpatialCoordinate[] Points, SpatialSegmentType[] Segments) Compose(List<(bool IsArc, SpatialCoordinate[] Points)> elements)
    {
        var points = new List<SpatialCoordinate>();
        var segments = new List<SpatialSegmentType>();
        foreach (var (isArc, element) in elements)
        {
            if (points.Count == 0)
                points.Add(element[0]);
            for (var i = 1; i < element.Length; i++)
                points.Add(element[i]);
            var count = isArc ? (element.Length - 1) / 2 : element.Length - 1;
            for (var i = 0; i < count; i++)
                segments.Add(isArc ? (i == 0 ? SpatialSegmentType.FirstArc : SpatialSegmentType.Arc) : (i == 0 ? SpatialSegmentType.FirstLine : SpatialSegmentType.Line));
        }
        return ([.. points], [.. segments]);
    }

    /// <summary>The arc a segment traces.</summary>
    public static SpatialArc ArcOf(SpatialCoordinate[] figure, Segment segment) =>
        new(figure[segment.Start], figure[segment.Start + 1], figure[segment.Start + 2]);

    /// <summary>Planar length of one figure, arcs measured along the circle.</summary>
    public static double FigureLength(SpatialShape shape, int index)
    {
        var figure = shape.Figures[index];
        var total = 0.0;
        foreach (var segment in Segments(shape, index))
        {
            if (segment.IsArc)
            {
                total += ArcOf(figure, segment).Length;
                continue;
            }
            var (from, to) = (figure[segment.Start], figure[segment.Start + 1]);
            total += Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));
        }
        return total;
    }

    /// <summary>
    /// Planar signed area a closed figure encloses, counter-clockwise positive:
    /// the shoelace sum over its segments' endpoints plus each arc's segment of
    /// the circle beyond its chord.
    /// </summary>
    public static double SignedFigureArea(SpatialShape shape, int index)
    {
        var figure = shape.Figures[index];
        var shoelace = 0.0;
        var arcs = 0.0;
        foreach (var segment in Segments(shape, index))
        {
            var from = figure[segment.Start];
            var to = figure[segment.Start + (segment.IsArc ? 2 : 1)];
            shoelace += (from.X * to.Y) - (to.X * from.Y);
            if (segment.IsArc)
                arcs += ArcOf(figure, segment).SegmentArea;
        }
        return (shoelace / 2) + arcs;
    }

    /// <summary>Appends an arc's points after its start — its interior steps, then its end.</summary>
    public delegate void ArcLinearizer(List<SpatialCoordinate> points, SpatialCoordinate start, SpatialCoordinate mid, SpatialCoordinate end);

    /// <summary>A planar linearizer cutting each arc into the steps <paramref name="steps"/> picks for it.</summary>
    public static ArcLinearizer Planar(Func<SpatialArc, int> steps) => (points, start, mid, end) =>
    {
        var arc = new SpatialArc(start, mid, end);
        arc.AppendSteps(points, steps(arc));
    };

    /// <summary>
    /// A round-earth linearizer: each arc is real's circle in a gnomonic plane
    /// (<see cref="SpatialGeodeticArc"/>), cut into the steps
    /// <paramref name="steps"/> picks for it. Control points that fix no circle
    /// make a single edge, and an arc ending where it starts adds nothing.
    /// </summary>
    public static ArcLinearizer Geodetic(Func<SpatialCoordinate, SpatialCoordinate, SpatialCoordinate, int> steps) => (points, start, mid, end) =>
    {
        if (start.X == end.X && start.Y == end.Y)
        {
            // Real accepts an arc returning to its start on geography; its
            // repeated end collapses before the round-earth validity checks.
            points.Add(end);
            return;
        }
        if ((start.X == mid.X && start.Y == mid.Y) || (mid.X == end.X && mid.Y == end.Y))
        {
            points.Add(end);
            return;
        }
        var line = SpatialGeodeticArc.Linearize(start, mid, end, steps(start, mid, end));
        for (var i = 1; i < line.Length - 1; i++)
        {
            if (double.IsNaN(line[i].X) || double.IsNaN(line[i].Y))
                continue;
            points.Add(new SpatialCoordinate(line[i].X, line[i].Y, start.Z, null));
        }
        points.Add(end);
    };

    /// <summary>The points figure <paramref name="index"/> traces once every arc is linearized.</summary>
    public static SpatialCoordinate[] LinearizeFigure(SpatialShape shape, int index, ArcLinearizer arcs)
    {
        var figure = shape.Figures[index];
        if (shape.FigureType(index) == SpatialFigureType.Line)
            return figure;
        var points = new List<SpatialCoordinate>(figure.Length * 4) { figure[0] };
        foreach (var segment in Segments(shape, index))
        {
            if (segment.IsArc)
                arcs(points, figure[segment.Start], figure[segment.Start + 1], figure[segment.Start + 2]);
            else
                points.Add(figure[segment.Start + 1]);
        }
        return [.. points];
    }

    /// <summary>
    /// The instance with every curved member replaced by its plain
    /// counterpart — a <c>CIRCULARSTRING</c> or <c>COMPOUNDCURVE</c> by a
    /// <c>LINESTRING</c>, a <c>CURVEPOLYGON</c> by a <c>POLYGON</c> — each arc
    /// linearized by <paramref name="steps"/>.
    /// </summary>
    public static SpatialShape Linearize(SpatialShape shape, ArcLinearizer steps)
    {
        switch (shape.Type)
        {
            case SpatialShapeType.CircularString:
            case SpatialShapeType.CompoundCurve:
                return shape.Figures.Length == 0
                    ? SpatialShape.Empty(SpatialShapeType.LineString)
                    : SpatialShape.Leaf(SpatialShapeType.LineString, [LinearizeFigure(shape, 0, steps)]);
            case SpatialShapeType.CurvePolygon:
                var rings = new SpatialCoordinate[shape.Figures.Length][];
                for (var i = 0; i < rings.Length; i++)
                    rings[i] = LinearizeFigure(shape, i, steps);
                return SpatialShape.Leaf(SpatialShapeType.Polygon, rings);
            default:
                if (!shape.IsCurved)
                    return shape;
                var children = new SpatialShape[shape.Children.Length];
                for (var i = 0; i < children.Length; i++)
                    children[i] = Linearize(shape.Children[i], steps);
                return SpatialShape.Collection(shape.Type, children);
        }
    }

    /// <summary>
    /// A curved <c>geography</c> instance's <c>STLength()</c> or <c>STArea()</c>.
    /// Real measures the arcs themselves, where a linearization falls short by
    /// the square of its step: 2,048 steps leave an arc's area 4e-7 low. Two
    /// linearizations a factor of two apart cancel that leading term
    /// (Richardson extrapolation), which lands within real's own measurement
    /// noise.
    /// </summary>
    public static double GeographyMeasure(SpatialShape shape, bool area)
    {
        var fine = Linearize(shape, Geodetic(static (_, _, _) => OperationSteps));
        var coarse = Linearize(shape, Geodetic(static (_, _, _) => OperationSteps / 2));
        double Measure(SpatialShape linear) => area ? SpatialMeasures.GeographyArea(linear) : SpatialMeasures.GeographyLength(linear);
        var f = Measure(fine);
        return f + ((f - Measure(coarse)) / 3);
    }

    private static readonly ArcLinearizer PlanarOperations = Planar(static arc => arc.IsStraight ? 1 : OperationSteps);

    private static readonly ArcLinearizer GeodeticOperations = Geodetic(static (_, _, _) => OperationSteps);

    /// <summary>The instance as the operations without a curve path of their own read it: every arc in <see cref="OperationSteps"/> steps.</summary>
    public static SpatialShape ForOperations(SpatialShape shape, bool isGeography) =>
        shape.IsCurved ? Linearize(shape, isGeography ? GeodeticOperations : PlanarOperations) : shape;

    /// <summary>
    /// Planar validity for a curved instance: an arc that returns to its own
    /// start degenerates to a point, which real reports invalid on
    /// <c>geometry</c> (and accepts on <c>geography</c>); everything else is
    /// judged on the linearization the operations read.
    /// </summary>
    public static bool IsPlanarValid(SpatialShape shape) =>
        !HasPointArc(shape) && SpatialValidator.IsValid(ForOperations(shape, isGeography: false));

    /// <summary>
    /// Round-earth validity for a curved instance, judged on its linearization.
    /// A curve whose every arc returns to its start linearizes to one repeated
    /// point, which real accepts on <c>geography</c> and is judged as that point.
    /// </summary>
    public static bool IsGeodeticValid(SpatialShape shape) =>
        SpatialGeodeticValidator.IsValid(CollapseRepeats(ForOperations(shape, isGeography: true)));

    private static SpatialShape CollapseRepeats(SpatialShape shape)
    {
        if (shape.Type == SpatialShapeType.LineString && shape.Figures.Length == 1 && Array.TrueForAll(shape.Figures[0], p => p.X == shape.Figures[0][0].X && p.Y == shape.Figures[0][0].Y))
            return SpatialShape.Leaf(SpatialShapeType.Point, [[shape.Figures[0][0]]]);
        if (shape.Children.Length == 0)
            return shape;
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = CollapseRepeats(shape.Children[i]);
        return SpatialShape.Collection(shape.Type, children);
    }

    private static bool HasPointArc(SpatialShape shape)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            if (shape.FigureType(i) == SpatialFigureType.Line)
                continue;
            var figure = shape.Figures[i];
            foreach (var segment in Segments(shape, i))
            {
                if (segment.IsArc && figure[segment.Start].X == figure[segment.Start + 2].X && figure[segment.Start].Y == figure[segment.Start + 2].Y)
                    return true;
            }
        }
        foreach (var child in shape.Children)
        {
            if (HasPointArc(child))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The instance's true bounding box — its points, widened to the circle's
    /// extreme wherever an arc passes an axis direction. A relative
    /// linearization tolerance scales by the larger of its two sides.
    /// </summary>
    public static (double MinX, double MaxX, double MinY, double MaxY) Extent(SpatialShape shape)
    {
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        Extend(shape, ref minX, ref maxX, ref minY, ref maxY);
        return (minX, maxX, minY, maxY);
    }

    private static void Extend(SpatialShape shape, ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            var figure = shape.Figures[i];
            foreach (var point in figure)
            {
                minX = Math.Min(minX, point.X);
                maxX = Math.Max(maxX, point.X);
                minY = Math.Min(minY, point.Y);
                maxY = Math.Max(maxY, point.Y);
            }
            if (shape.FigureType(i) == SpatialFigureType.Line)
                continue;
            foreach (var segment in Segments(shape, i))
            {
                if (segment.IsArc)
                    ArcOf(figure, segment).Extend(ref minX, ref maxX, ref minY, ref maxY);
            }
        }
        foreach (var child in shape.Children)
            Extend(child, ref minX, ref maxX, ref minY, ref maxY);
    }

    /// <summary>
    /// <c>CurveToLineWithTolerance(tolerance, relative)</c> and, at 0.001
    /// relative, <c>STCurveToLine()</c>: every arc cut into the fewest
    /// power-of-two steps whose deviation estimate stays within the tolerance,
    /// which a relative one scales by the larger side of the instance's true
    /// bounding box (fitted 198 of 198 against SQL Server 2025, 2026-09-29).
    /// </summary>
    public static SpatialShape CurveToLine(SpatialShape shape, double tolerance, bool relative)
    {
        if (relative)
        {
            var (minX, maxX, minY, maxY) = Extent(shape);
            tolerance *= Math.Max(maxX - minX, maxY - minY);
        }
        return WithoutZM(Linearize(shape, Planar(arc => arc.Steps(tolerance))));
    }

    /// <summary>The instance with every Z and M dropped, which is how real writes a linearization or a reduction.</summary>
    public static SpatialShape WithoutZM(SpatialShape shape)
    {
        if (!shape.AnyHasZ && !shape.AnyHasM)
            return shape;
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < figures.Length; i++)
        {
            var figure = shape.Figures[i];
            figures[i] = new SpatialCoordinate[figure.Length];
            for (var j = 0; j < figure.Length; j++)
                figures[i][j] = new SpatialCoordinate(figure[j].X, figure[j].Y);
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < children.Length; i++)
            children[i] = WithoutZM(shape.Children[i]);
        return new SpatialShape(shape.Type, figures, children, shape.FigureTypes, shape.Segments);
    }

    /// <summary>
    /// The round-earth <c>CurveToLineWithTolerance</c>: each arc cut into the
    /// fewest power-of-two steps whose deviation estimate — the arc's radius in
    /// metres on the gnomonic plane real draws it in, times <c>θ²/8</c> — stays
    /// within the tolerance in metres, and never more than 2,048. A relative
    /// tolerance scales by the instance's <c>EnvelopeAngle()</c> as a distance
    /// along the equator (fitted against SQL Server 2025, 2026-09-29: 191 of 193
    /// relative and every absolute step count; real's own <c>EnvelopeAngle</c>
    /// of a curve differs from its linearization's by a few percent, which is
    /// where the rest fall).
    /// </summary>
    public static SpatialShape GeographyCurveToLine(SpatialShape shape, double tolerance, bool relative)
    {
        if (relative)
        {
            var angle = SpatialEnvelope.Angle(ForOperations(shape, isGeography: true)) ?? 0;
            tolerance *= double.DegreesToRadians(angle) * SpatialEllipsoid.SemiMajor;
        }
        return WithoutZM(Linearize(shape, Geodetic((start, mid, end) =>
        {
            var (radius, sweep) = SpatialGeodeticArc.Measure(start, mid, end);
            var steps = 1;
            while (steps < GeodeticMaxSteps && radius * (sweep / steps) * (sweep / steps) / 8 > tolerance)
                steps *= 2;
            return steps;
        })));
    }

    /// <summary>The most steps real cuts a <c>geography</c> arc into, however small the tolerance.</summary>
    private const int GeodeticMaxSteps = 2048;

    /// <summary>
    /// <c>STEnvelope()</c> of a curved instance: its true bounding box, each
    /// arc's extent scaled away from zero by one part in 10¹², then padded on
    /// every side by a millionth of the box's larger side — the margin real
    /// puts round a curve's envelope, even one whose rings are all straight
    /// (probed 2026-09-29).
    /// </summary>
    public static SpatialShape Envelope(SpatialShape shape)
    {
        if (shape.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        double minX = double.PositiveInfinity, maxX = double.NegativeInfinity, minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
        ExtendScaled(shape, ref minX, ref maxX, ref minY, ref maxY);
        var pad = Math.Max(maxX - minX, maxY - minY) * EnvelopeMargin;
        minX -= pad;
        maxX += pad;
        minY -= pad;
        maxY += pad;
        return SpatialShape.Leaf(SpatialShapeType.Polygon,
        [[
            new SpatialCoordinate(minX, minY), new SpatialCoordinate(maxX, minY), new SpatialCoordinate(maxX, maxY),
            new SpatialCoordinate(minX, maxY), new SpatialCoordinate(minX, minY),
        ]]);
    }

    private const double EnvelopeMargin = 1e-6;

    private const double ArcExtentScale = 1 + 1e-12;

    private static void ExtendScaled(SpatialShape shape, ref double minX, ref double maxX, ref double minY, ref double maxY)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            var figure = shape.Figures[i];
            foreach (var segment in Segments(shape, i))
            {
                if (!segment.IsArc)
                {
                    foreach (var point in figure.AsSpan(segment.Start, 2))
                    {
                        minX = Math.Min(minX, point.X);
                        maxX = Math.Max(maxX, point.X);
                        minY = Math.Min(minY, point.Y);
                        maxY = Math.Max(maxY, point.Y);
                    }
                    continue;
                }
                double aMinX = double.PositiveInfinity, aMaxX = double.NegativeInfinity, aMinY = double.PositiveInfinity, aMaxY = double.NegativeInfinity;
                ArcOf(figure, segment).Extend(ref aMinX, ref aMaxX, ref aMinY, ref aMaxY);
                minX = Math.Min(minX, aMinX * ArcExtentScale);
                maxX = Math.Max(maxX, aMaxX * ArcExtentScale);
                minY = Math.Min(minY, aMinY * ArcExtentScale);
                maxY = Math.Max(maxY, aMaxY * ArcExtentScale);
            }
            if (figure.Length == 1)
            {
                minX = Math.Min(minX, figure[0].X);
                maxX = Math.Max(maxX, figure[0].X);
                minY = Math.Min(minY, figure[0].Y);
                maxY = Math.Max(maxY, figure[0].Y);
            }
        }
        foreach (var child in shape.Children)
            ExtendScaled(child, ref minX, ref maxX, ref minY, ref maxY);
    }

    /// <summary>
    /// <c>STDistance</c> with a curved operand, measured on the arcs themselves:
    /// zero where the operations' linearization meets or contains, and
    /// otherwise the least distance over every pair of points, straight
    /// segments and arcs — exact where the linearization would be off by up to
    /// its sagitta (real answers <c>√26 − 2</c> from <c>POINT(1 5)</c> to a
    /// circle of radius 2 about (2, 0), probed 2026-09-29).
    /// </summary>
    public static double PlanarDistance(SpatialShape a, SpatialShape b)
    {
        if (SpatialMeasures.PlanarDistance(ForOperations(a, isGeography: false), ForOperations(b, isGeography: false)) == 0)
            return 0;
        var left = Components(a);
        var right = Components(b);
        var best = double.PositiveInfinity;
        foreach (var x in left)
        {
            foreach (var y in right)
                best = Math.Min(best, Distance(x, y));
        }
        return best;
    }

    /// <summary>A point (<c>End</c> equal to <c>Start</c>), a straight segment, or an arc.</summary>
    private readonly struct Component(SpatialCoordinate start, SpatialCoordinate end, SpatialArc? arc)
    {
        public readonly SpatialCoordinate Start = start;
        public readonly SpatialCoordinate End = end;
        public readonly SpatialArc? Arc = arc;
    }

    private static List<Component> Components(SpatialShape shape)
    {
        var components = new List<Component>();
        Collect(shape, components);
        return components;
    }

    private static void Collect(SpatialShape shape, List<Component> components)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            var figure = shape.Figures[i];
            if (figure.Length == 1)
            {
                components.Add(new Component(figure[0], figure[0], null));
                continue;
            }
            foreach (var segment in Segments(shape, i))
            {
                if (!segment.IsArc)
                {
                    components.Add(new Component(figure[segment.Start], figure[segment.Start + 1], null));
                    continue;
                }
                var arc = ArcOf(figure, segment);
                components.Add(arc.IsStraight ? new Component(arc.Start, arc.End, null) : new Component(arc.Start, arc.End, arc));
            }
        }
        foreach (var child in shape.Children)
            Collect(child, components);
    }

    private static double Distance(Component x, Component y)
    {
        if (x.Arc is { } xa)
        {
            return y.Arc is { } ya
                ? ArcToArc(xa, ya)
                : Math.Min(Math.Min(PointToArc(y.Start, xa), PointToArc(y.End, xa)), SegmentToArc(y.Start, y.End, xa));
        }
        if (y.Arc is { } arc)
            return Math.Min(Math.Min(PointToArc(x.Start, arc), PointToArc(x.End, arc)), SegmentToArc(x.Start, x.End, arc));
        return SegmentToSegment(x.Start, x.End, y.Start, y.End);
    }

    private static double Between(SpatialCoordinate p, SpatialCoordinate q) => Math.Sqrt(((p.X - q.X) * (p.X - q.X)) + ((p.Y - q.Y) * (p.Y - q.Y)));

    private static double PointToSegment(SpatialCoordinate p, SpatialCoordinate a, SpatialCoordinate b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var length = (dx * dx) + (dy * dy);
        if (length == 0)
            return Between(p, a);
        var t = Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length, 0, 1);
        return Between(p, new SpatialCoordinate(a.X + (t * dx), a.Y + (t * dy)));
    }

    /// <summary>Two segments that don't cross come closest at an endpoint of one.</summary>
    private static double SegmentToSegment(SpatialCoordinate a, SpatialCoordinate b, SpatialCoordinate c, SpatialCoordinate d) =>
        Math.Min(Math.Min(PointToSegment(a, c, d), PointToSegment(b, c, d)), Math.Min(PointToSegment(c, a, b), PointToSegment(d, a, b)));

    /// <summary>The radial distance where the point's direction from the centre falls on the arc, else the nearer end.</summary>
    private static double PointToArc(SpatialCoordinate p, SpatialArc arc)
    {
        var ends = Math.Min(Between(p, arc.Start), Between(p, arc.End));
        double dx = p.X - arc.CenterX, dy = p.Y - arc.CenterY;
        var reach = Math.Sqrt((dx * dx) + (dy * dy));
        return reach > 0 && arc.Covers(Math.Atan2(dy, dx)) ? Math.Min(ends, Math.Abs(reach - arc.Radius)) : ends;
    }

    /// <summary>
    /// A segment and an arc that don't meet come closest at an end of either,
    /// or where the segment's line is nearest the centre when that foot is on
    /// the segment and its direction on the arc.
    /// </summary>
    private static double SegmentToArc(SpatialCoordinate a, SpatialCoordinate b, SpatialArc arc)
    {
        var best = Math.Min(PointToSegment(arc.Start, a, b), PointToSegment(arc.End, a, b));
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var length = (dx * dx) + (dy * dy);
        if (length == 0)
            return best;
        var t = (((arc.CenterX - a.X) * dx) + ((arc.CenterY - a.Y) * dy)) / length;
        if (t is > 0 and < 1)
        {
            var foot = new SpatialCoordinate(a.X + (t * dx), a.Y + (t * dy));
            double fx = foot.X - arc.CenterX, fy = foot.Y - arc.CenterY;
            var reach = Math.Sqrt((fx * fx) + (fy * fy));
            if (reach > arc.Radius && arc.Covers(Math.Atan2(fy, fx)))
                best = Math.Min(best, reach - arc.Radius);
        }
        return best;
    }

    /// <summary>
    /// Two arcs that don't meet come closest at an end of either, or along the
    /// line through their centres where both arcs cross it.
    /// </summary>
    private static double ArcToArc(SpatialArc x, SpatialArc y)
    {
        var best = Math.Min(Math.Min(PointToArc(x.Start, y), PointToArc(x.End, y)), Math.Min(PointToArc(y.Start, x), PointToArc(y.End, x)));
        var toward = Math.Atan2(y.CenterY - x.CenterY, y.CenterX - x.CenterX);
        foreach (var alongX in (ReadOnlySpan<double>)[toward, toward + Math.PI])
        {
            if (!x.Covers(alongX))
                continue;
            var p = new SpatialCoordinate(x.CenterX + (x.Radius * Math.Cos(alongX)), x.CenterY + (x.Radius * Math.Sin(alongX)));
            foreach (var alongY in (ReadOnlySpan<double>)[toward, toward + Math.PI])
            {
                if (y.Covers(alongY))
                    best = Math.Min(best, Between(p, new SpatialCoordinate(y.CenterX + (y.Radius * Math.Cos(alongY)), y.CenterY + (y.Radius * Math.Sin(alongY)))));
            }
        }
        return best;
    }

    /// <summary>
    /// <c>Reduce</c> over a curved instance. Real keeps every genuine arc as
    /// written and reduces only what is straight: a curve with no genuine arc
    /// left comes back as its plain kind, reduced — <c>CIRCULARSTRING(0 0, 1 1,
    /// 2 2)</c> as <c>LINESTRING (0 0, 2 2)</c>, a <c>CURVEPOLYGON</c> of line
    /// rings as a <c>POLYGON</c> — and a <c>COMPOUNDCURVE</c> of arcs alone as a
    /// <c>CIRCULARSTRING</c>. A collection keeps its members' kinds rather than
    /// turning into a Multi form, and an empty instance is
    /// <c>GEOMETRYCOLLECTION EMPTY</c> (probed 2026-09-29).
    /// </summary>
    public static SpatialShape Reduce(SpatialShape shape, double tolerance, bool isGeography) =>
        WithoutZM(ReduceCurved(shape, tolerance, isGeography));

    private static SpatialShape ReduceCurved(SpatialShape shape, double tolerance, bool isGeography)
    {
        if (shape.IsEmpty)
            return SpatialShape.Empty(SpatialShapeType.GeometryCollection);
        switch (shape.Type)
        {
            case SpatialShapeType.CircularString:
            case SpatialShapeType.CompoundCurve:
            case SpatialShapeType.CurvePolygon:
                if (!HasGenuineArc(shape))
                    return SpatialSimplify.Reduce(Linearize(shape, Planar(static _ => 1)), tolerance, isGeography);
                if (shape.Type == SpatialShapeType.CompoundCurve && AllArcs(shape))
                    return SpatialShape.Curve(SpatialShapeType.CircularString, shape.Figures, [SpatialFigureType.Arc], [null]);
                return shape;
            default:
                if (!shape.IsCurved)
                    return SpatialSimplify.Reduce(shape, tolerance, isGeography);
                var children = new List<SpatialShape>(shape.Children.Length);
                foreach (var child in shape.Children)
                {
                    if (!child.IsEmpty)
                        children.Add(ReduceCurved(child, tolerance, isGeography));
                }
                return SpatialShape.Collection(shape.Type, [.. children]);
        }
    }

    /// <summary>Whether any arc of the shape's own figures fixes a circle.</summary>
    private static bool HasGenuineArc(SpatialShape shape)
    {
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            foreach (var segment in Segments(shape, i))
            {
                if (segment.IsArc && !ArcOf(shape.Figures[i], segment).IsStraight)
                    return true;
            }
        }
        return false;
    }

    private static bool AllArcs(SpatialShape shape)
    {
        foreach (var segment in Segments(shape, 0))
        {
            if (!segment.IsArc)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The arc-by-arc curves <c>STCurveN</c> numbers: each arc of a
    /// <c>CIRCULARSTRING</c>, and each segment of a <c>COMPOUNDCURVE</c> —
    /// a straight one as a two-point <c>LINESTRING</c>. A <c>LINESTRING</c>
    /// numbers its segments the same way; other kinds have no curves.
    /// </summary>
    public static SpatialShape[]? Curves(SpatialShape shape)
    {
        if (shape.Type is not (SpatialShapeType.LineString or SpatialShapeType.CircularString or SpatialShapeType.CompoundCurve))
            return null;
        if (shape.Figures.Length == 0)
            return [];
        var figure = shape.Figures[0];
        var curves = new List<SpatialShape>();
        foreach (var segment in Segments(shape, 0))
        {
            curves.Add(segment.IsArc
                ? SpatialShape.Curve(SpatialShapeType.CircularString, [figure[segment.Start..(segment.Start + 3)]], [SpatialFigureType.Arc], [null])
                : SpatialShape.Leaf(SpatialShapeType.LineString, [figure[segment.Start..(segment.Start + 2)]]));
        }
        return [.. curves];
    }
}
