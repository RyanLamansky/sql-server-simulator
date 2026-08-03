using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>geometry</c>'s topological predicates and the validity pair behind them:
/// the DE-9IM matrix <c>STRelate</c> exposes, the eight named predicates as
/// masks over it, <c>STIsValid</c>, and the Msg 24144 gate an invalid instance
/// puts on most of the instance surface.
/// </summary>
/// <remarks>
/// The pinned matrices and predicate bits are SQL Server 2025's own, harvested
/// 2026-08-02 by driving a 71-shape square of pairs through the reference and
/// diffing it against the simulator; the residual disagreements are recorded
/// under Divergences in <c>docs/claude/spatial.md</c>.
/// </remarks>
[TestClass]
public sealed class SpatialPredicateTests
{
    private static readonly string[] PredicateNames =
        ["STIntersects", "STContains", "STWithin", "STTouches", "STCrosses", "STOverlaps", "STDisjoint", "STEquals"];

    private static object? Eval(string expression) => new Simulation().ExecuteScalar($"select {expression}");

    /// <summary>Evaluates a <c>bit</c>-yielding expression that is known not to be NULL.</summary>
    private static bool Test(string expression) => (bool)Eval(expression)!;

    /// <summary>
    /// Reads the nine intersection dimensions out of <c>STRelate</c> the only
    /// way real exposes them — one mask per cell per candidate dimension.
    /// </summary>
    private static string Matrix(string a, string b)
    {
        var select = new System.Text.StringBuilder("select ");
        for (var cell = 0; cell < 9; cell++)
        {
            if (cell > 0)
                _ = select.Append(" + ");
            _ = select.Append("case ");
            for (var dimension = 0; dimension <= 2; dimension++)
            {
                var mask = new string('*', 9).ToCharArray();
                mask[cell] = (char)('0' + dimension);
                _ = select.Append(
                    $"when geometry::Parse('{a}').STRelate(geometry::Parse('{b}'), '{new string(mask)}') = 1 then '{dimension}' ");
            }
            _ = select.Append("else 'F' end");
        }
        return (string)new Simulation().ExecuteScalar(select.ToString())!;
    }

    private static string PredicateBits(string a, string b)
    {
        var select = new System.Text.StringBuilder("select ''");
        foreach (var predicate in PredicateNames)
            _ = select.Append($" + cast(cast(geometry::Parse('{a}').{predicate}(geometry::Parse('{b}')) as int) as char(1))");
        return (string)new Simulation().ExecuteScalar(select.ToString())!;
    }

    /// <summary>
    /// The differential core: for each pair, the full DE-9IM matrix and all
    /// eight predicate bits, in <c>STIntersects / STContains / STWithin /
    /// STTouches / STCrosses / STOverlaps / STDisjoint / STEquals</c> order.
    /// </summary>
    [TestMethod]
    [DataRow("POINT(1 1)", "POINT(1 1)", "0FFFFFFF2", "11100001")]
    [DataRow("POINT(1 1)", "POINT(2 2)", "FF0FFF0F2", "00000010")]
    [DataRow("POINT(1 1)", "LINESTRING(0 0, 2 2)", "0FFFFF102", "10100000")]
    [DataRow("POINT(0 0)", "LINESTRING(0 0, 2 2)", "F0FFFF102", "10010000")]
    [DataRow("POINT(5 5)", "LINESTRING(0 0, 2 2)", "FF0FFF102", "00000010")]
    [DataRow("POINT(2 2)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "0FFFFF212", "10100000")]
    [DataRow("POINT(0 0)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "F0FFFF212", "10010000")]
    [DataRow("POINT(5 5)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "FF0FFF212", "00000010")]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(0 0, 1 1, 2 2)", "1FFF0FFF2", "11100001")]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(2 2, 0 0)", "1FFF0FFF2", "11100001")]
    [DataRow("LINESTRING(0 0, 2 0)", "LINESTRING(1 -1, 1 1)", "0F1FF0102", "10001000")]
    [DataRow("LINESTRING(0 0, 2 0)", "LINESTRING(2 0, 4 0)", "FF1F00102", "10010000")]
    [DataRow("LINESTRING(0 0, 2 0)", "LINESTRING(1 0, 3 0)", "1010F0102", "10000100")]
    [DataRow("LINESTRING(0 0, 2 0)", "LINESTRING(0 1, 2 1)", "FF1FF0102", "00000010")]
    [DataRow("LINESTRING(0 0, 4 0)", "LINESTRING(1 0, 3 0)", "101FF0FF2", "11000000")]
    [DataRow("LINESTRING(0 0, 2 0)", "LINESTRING(2 0, 2 2)", "FF1F00102", "10010000")]
    [DataRow("LINESTRING(-1 2, 5 2)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "101FF0212", "10001000")]
    [DataRow("LINESTRING(1 1, 3 3)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "1FF0FF212", "10100000")]
    [DataRow("LINESTRING(0 0, 0 4)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "F1FF0F212", "10010000")]
    [DataRow("LINESTRING(-1 0, -1 4)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "FF1FF0212", "00000010")]
    [DataRow("LINESTRING(0 2, 2 2)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "1FF00F212", "10100000")]
    [DataRow("LINESTRING(-2 0, 0 0)", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "FF1F00212", "10010000")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "2FFF1FFF2", "11100001")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((1 1,3 1,3 3,1 3,1 1))", "212FF1FF2", "11000000")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((2 2,6 2,6 6,2 6,2 2))", "212101212", "10000100")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((4 0,8 0,8 4,4 4,4 0))", "FF2F11212", "10010000")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((5 5,6 5,6 6,5 6,5 5))", "FF2FF1212", "00000010")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((0 0,2 0,2 2,0 2,0 0))", "212F11FF2", "11000000")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((4 4,6 4,6 6,4 6,4 4))", "FF2F01212", "10010000")]
    [DataRow("POLYGON((0 0,6 0,6 6,0 6,0 0),(2 2,4 2,4 4,2 4,2 2))", "POINT(3 3)", "FF2FF10F2", "00000010")]
    [DataRow("POLYGON((0 0,6 0,6 6,0 6,0 0),(2 2,4 2,4 4,2 4,2 2))", "POLYGON((2 2,4 2,4 4,2 4,2 2))", "FF2F112F2", "10010000")]
    [DataRow("POLYGON((0 0,6 0,6 6,0 6,0 0),(2 2,4 2,4 4,2 4,2 2))", "LINESTRING(3 3, 3 5)", "1020F1102", "10000000")]
    [DataRow("MULTIPOINT((0 0),(5 5))", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "F00FFF212", "10010000")]
    [DataRow("MULTIPOINT((1 1),(2 2))", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "0FFFFF212", "10100000")]
    [DataRow("MULTILINESTRING((0 0,1 1),(2 2,3 3))", "LINESTRING(0 0, 3 3)", "1FF00F1F2", "10100000")]
    [DataRow("MULTIPOLYGON(((0 0,1 0,1 1,0 1,0 0)),((2 2,3 2,3 3,2 3,2 2)))", "POINT(0.5 0.5)", "0F2FF1FF2", "11000000")]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((2 2,4 2,4 4,2 4,2 2)))", "POINT(2 2)", "FF20F1FF2", "10010000")]
    [DataRow("POINT EMPTY", "POINT(0 0)", "FFFFFF0F2", "00000010")]
    [DataRow("POINT EMPTY", "POINT EMPTY", "FFFFFFFF2", "00000011")]
    [DataRow("GEOMETRYCOLLECTION EMPTY", "POLYGON((0 0,4 0,4 4,0 4,0 0))", "FFFFFF212", "00000010")]
    [DataRow("POLYGON EMPTY", "POLYGON EMPTY", "FFFFFFFF2", "00000011")]
    [DataRow("GEOMETRYCOLLECTION(POINT(1 1),LINESTRING(0 0,2 2))", "POINT(1 1)", "0F1FF0FF2", "11000000")]
    [DataRow("GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,2 2))", "POINT(0 0)", "0F10F0FF2", "11000000")]
    [DataRow("GEOMETRYCOLLECTION(LINESTRING(0 0,2 0),LINESTRING(2 0,4 0))", "LINESTRING(0 0, 4 0)", "1FFF0FFF2", "11100001")]
    [DataRow("MULTILINESTRING((0 0,2 0),(2 0,4 0))", "POINT(2 0)", "0F1FF0FF2", "11000000")]
    [DataRow("MULTILINESTRING((0 0,2 0),(2 0,4 0),(2 0,2 2))", "POINT(2 0)", "FF10F0FF2", "10010000")]
    [DataRow("POINT(1 1 5)", "POINT(1 1)", "0FFFFFFF2", "11100001")]
    [DataRow("POINT(1 1 5 7)", "POINT(1 1)", "0FFFFFFF2", "11100001")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "LINESTRING(4 0, 4 4)", "FF2101FF2", "10010000")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "LINESTRING(0 0, 4 4)", "1F2F01FF2", "11000000")]
    [DataRow("POINT(0.1 0.3)", "LINESTRING(0 0, 3 1)", "FF0FFF102", "00000010")]
    public void PairMatrix_MatchesReference(string a, string b, string matrix, string bits)
    {
        AreEqual(matrix, Matrix(a, b), $"DE-9IM of {a} against {b}");
        AreEqual(bits, PredicateBits(a, b), $"predicates of {a} against {b}");
    }

    /// <summary>A predicate yields <c>bit</c>, and NULL on either side propagates rather than raising.</summary>
    [TestMethod]
    public void NullOperand_ReadsNull()
    {
        var sim = new Simulation();
        IsTrue((bool)sim.ExecuteScalar("select geometry::Parse('POINT(0 0)').STIntersects(geometry::Parse('POINT(0 0)'))")!);
        AreEqual(DBNull.Value, sim.ExecuteScalar("declare @g geometry = null; select geometry::Parse('POINT(0 0)').STIntersects(@g)"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("declare @g geometry = null; select @g.STIntersects(geometry::Parse('POINT(0 0)'))"));
    }

    /// <summary>Operands in different spatial reference systems aren't comparable; every predicate reads NULL rather than raising.</summary>
    [TestMethod]
    [DataRow("STIntersects")]
    [DataRow("STContains")]
    [DataRow("STDisjoint")]
    [DataRow("STEquals")]
    [DataRow("STRelate")]
    public void MismatchedSrid_ReadsNull(string member)
    {
        var argument = member == "STRelate" ? "geometry::STGeomFromText('POINT(0 0)', 4326), '*********'" : "geometry::STGeomFromText('POINT(0 0)', 4326)";
        AreEqual(DBNull.Value, Eval($"geometry::STGeomFromText('POINT(0 0)', 0).{member}({argument})"));
    }

    /// <summary>Real reads a string argument as well-known text rather than refusing it.</summary>
    [TestMethod]
    public void StringArgument_ParsesAsWellKnownText()
        => IsTrue(Test("geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))').STContains('POINT(2 2)')"));

    /// <summary>Z and M ordinates take no part in a predicate.</summary>
    [TestMethod]
    public void ZAndM_AreIgnored()
    {
        IsTrue(Test("geometry::Parse('POINT(1 1 5)').STEquals(geometry::Parse('POINT(1 1 99)'))"));
        IsTrue(Test("geometry::Parse('POINT(1 1 5 7)').STEquals(geometry::Parse('POINT(1 1)'))"));
    }

    /// <summary>
    /// A point on a polygon's border is contained by neither operand: the
    /// polygon intersects and touches it, but does not contain it.
    /// </summary>
    [TestMethod]
    public void BorderPoint_IntersectsAndTouchesButIsNotContained()
    {
        const string Polygon = "geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))')";
        IsTrue(Test($"{Polygon}.STIntersects(geometry::Parse('POINT(2 0)'))"));
        IsFalse(Test($"{Polygon}.STContains(geometry::Parse('POINT(2 0)'))"));
        IsTrue(Test($"{Polygon}.STTouches(geometry::Parse('POINT(2 0)'))"));
        IsFalse(Test($"{Polygon}.STDisjoint(geometry::Parse('POINT(2 0)'))"));
    }

    /// <summary><c>STWithin</c> is <c>STContains</c> with the operands swapped.</summary>
    [TestMethod]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POINT(2 2)")]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((1 1,3 1,3 3,1 3,1 1))")]
    [DataRow("LINESTRING(0 0, 4 0)", "LINESTRING(1 0, 3 0)")]
    public void WithinIsTheConverseOfContains(string container, string contained)
    {
        IsTrue(Test($"geometry::Parse('{container}').STContains(geometry::Parse('{contained}'))"));
        IsTrue(Test($"geometry::Parse('{contained}').STWithin(geometry::Parse('{container}'))"));
        IsFalse(Test($"geometry::Parse('{contained}').STContains(geometry::Parse('{container}'))"));
    }

    /// <summary>
    /// <c>STCrosses</c> is not symmetric: real defines it only when the
    /// receiver is the lower-dimensional operand, plus the line-on-line case.
    /// A line crossing a polygon answers true; the polygon answers false.
    /// </summary>
    [TestMethod]
    public void Crosses_RequiresTheReceiverToBeLowerDimensional()
    {
        IsTrue(Test("geometry::Parse('LINESTRING(-1 2, 5 2)').STCrosses(geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))'))"));
        IsFalse(Test("geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))').STCrosses(geometry::Parse('LINESTRING(-1 2, 5 2)'))"));
        IsTrue(Test("geometry::Parse('MULTIPOINT((2 2),(9 9))').STCrosses(geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))'))"));
        IsFalse(Test("geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))').STCrosses(geometry::Parse('MULTIPOINT((2 2),(9 9))'))"));
        // Two lines cross when their interiors meet at a point.
        IsTrue(Test("geometry::Parse('LINESTRING(0 0, 2 0)').STCrosses(geometry::Parse('LINESTRING(1 -1, 1 1)'))"));
        IsFalse(Test("geometry::Parse('LINESTRING(0 0, 2 0)').STCrosses(geometry::Parse('LINESTRING(2 0, 4 0)'))"));
    }

    /// <summary>
    /// <c>STOverlaps</c> needs matching dimensions, and a one-dimensional pair
    /// must share a stretch rather than a point.
    /// </summary>
    [TestMethod]
    public void Overlaps_NeedsMatchingDimensions()
    {
        IsTrue(Test("geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))').STOverlaps(geometry::Parse('POLYGON((2 2,6 2,6 6,2 6,2 2))'))"));
        IsTrue(Test("geometry::Parse('LINESTRING(0 0, 2 0)').STOverlaps(geometry::Parse('LINESTRING(1 0, 3 0)'))"));
        IsFalse(Test("geometry::Parse('LINESTRING(0 0, 2 0)').STOverlaps(geometry::Parse('LINESTRING(1 -1, 1 1)'))"));
        IsTrue(Test("geometry::Parse('MULTIPOINT((0 0),(1 1))').STOverlaps(geometry::Parse('MULTIPOINT((1 1),(2 2))'))"));
        IsFalse(Test("geometry::Parse('LINESTRING(-1 2, 5 2)').STOverlaps(geometry::Parse('POLYGON((0 0,4 0,4 4,0 4,0 0))'))"));
    }

    /// <summary>
    /// <c>STEquals</c> is topological, not vertex-wise: a redundant vertex and
    /// a reversed direction both compare equal, and so do two empty instances
    /// whatever their declared kinds.
    /// </summary>
    [TestMethod]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(0 0, 1 1, 2 2)", true)]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(2 2, 0 0)", true)]
    [DataRow("GEOMETRYCOLLECTION(LINESTRING(0 0,2 0),LINESTRING(2 0,4 0))", "LINESTRING(0 0, 4 0)", true)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0))", "POLYGON((4 4,0 4,0 0,4 0,4 4))", true)]
    [DataRow("POINT EMPTY", "POINT EMPTY", true)]
    [DataRow("POINT EMPTY", "POLYGON EMPTY", true)]
    [DataRow("POINT EMPTY", "POINT(0 0)", false)]
    [DataRow("LINESTRING(0 0, 2 2)", "LINESTRING(0 0, 2 1)", false)]
    public void Equals_IsTopological(string a, string b, bool expected)
        => AreEqual(expected, Test($"geometry::Parse('{a}').STEquals(geometry::Parse('{b}'))"));

    /// <summary>
    /// An empty instance is disjoint from everything, including another empty
    /// instance, and intersects nothing.
    /// </summary>
    [TestMethod]
    [DataRow("POINT EMPTY", "POLYGON((0 0,4 0,4 4,0 4,0 0))")]
    [DataRow("GEOMETRYCOLLECTION EMPTY", "POLYGON((0 0,4 0,4 4,0 4,0 0))")]
    [DataRow("POINT EMPTY", "POINT EMPTY")]
    public void Empty_IsDisjointFromEverything(string a, string b)
    {
        IsTrue(Test($"geometry::Parse('{a}').STDisjoint(geometry::Parse('{b}'))"));
        IsFalse(Test($"geometry::Parse('{a}').STIntersects(geometry::Parse('{b}'))"));
        IsFalse(Test($"geometry::Parse('{a}').STContains(geometry::Parse('{b}'))"));
        IsFalse(Test($"geometry::Parse('{a}').STTouches(geometry::Parse('{b}'))"));
    }

    /// <summary>
    /// A multi-figure line's boundary follows the mod-2 rule: a vertex two
    /// figures share is interior, one three figures share is boundary.
    /// </summary>
    [TestMethod]
    public void LineBoundary_FollowsTheModTwoRule()
    {
        IsTrue(Test("geometry::Parse('MULTILINESTRING((0 0,2 0),(2 0,4 0))').STContains(geometry::Parse('POINT(2 0)'))"));
        IsFalse(Test("geometry::Parse('MULTILINESTRING((0 0,2 0),(2 0,4 0),(2 0,2 2))').STContains(geometry::Parse('POINT(2 0)'))"));
        IsTrue(Test("geometry::Parse('MULTILINESTRING((0 0,2 0),(2 0,4 0),(2 0,2 2))').STTouches(geometry::Parse('POINT(2 0)'))"));
    }

    /// <summary>
    /// A collection's interior and boundary are the per-class unions, so a
    /// point member sitting on a line member's endpoint lands in both.
    /// </summary>
    [TestMethod]
    public void Collection_UnionsItsMembers()
    {
        AreEqual("0F10F0FF2", Matrix("GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,2 2))", "POINT(0 0)"));
        IsTrue(Test("geometry::Parse('GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,2 2))').STEquals(geometry::Parse('LINESTRING(0 0,2 2)'))"));
        IsTrue(Test("geometry::Parse('GEOMETRYCOLLECTION(POINT(9 9),POLYGON((0 0,4 0,4 4,0 4,0 0)))').STContains(geometry::Parse('POINT(2 2)'))"));
    }

    /// <summary>
    /// Collinearity is decided by a floating-point orientation determinant with
    /// a roundoff filter, which is what real does: a point whose cross product
    /// against an oblique segment is lost in rounding reads as on it, while one
    /// genuinely off an axis-aligned segment does not, however small the offset.
    /// </summary>
    [TestMethod]
    [DataRow("POINT(1.1666666666666665 0.5)", "LINESTRING(0 0, 7 3)", true)]
    [DataRow("POINT(0.1 0.3)", "LINESTRING(0 0, 1 3)", true)]
    [DataRow("POINT(0.7 2.1)", "LINESTRING(0 0, 1 3)", true)]
    [DataRow("POINT(1 1e-9)", "LINESTRING(0 0, 2 0)", false)]
    [DataRow("POINT(1 1e-18)", "LINESTRING(0 0, 2 0)", false)]
    public void NearCollinearPoint_FollowsTheRoundoffFilter(string point, string line, bool onLine)
        => AreEqual(onLine, Test($"geometry::Parse('{point}').STIntersects(geometry::Parse('{line}'))"));

    /// <summary><c>STIsValid</c> across the invalidity kinds real recognizes.</summary>
    [TestMethod]
    [DataRow("POLYGON((0 0, 2 2, 0 2, 2 0, 0 0))", 0)]
    [DataRow("POLYGON((0 0, 2 0, 2 2, 0 2, 0 0))", 1)]
    [DataRow("LINESTRING(0 0, 2 2, 2 0, 0 2)", 1)]
    [DataRow("LINESTRING(0 0, 1 1, 0 0)", 0)]
    [DataRow("LINESTRING(0 0, 0 0)", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(5 5,6 5,6 6,5 6,5 5))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,3 1,3 3,1 3,1 1),(2 2,3 2,3 3,2 3,2 2))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 0,2 0,2 2,0 2,0 0))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((1 1,3 1,3 3,1 3,1 1)))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((2 0,4 0,4 2,2 2,2 0)))", 0)]
    [DataRow("MULTIPOINT((0 0),(0 0))", 1)]
    [DataRow("MULTILINESTRING((0 0,2 2),(0 0,2 2))", 0)]
    [DataRow("POLYGON((0 0,0 0,0 0,0 0))", 0)]
    [DataRow("POLYGON((0 0,2 0,2 0,0 0,0 0))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 0,4 0,4 4,0 4,0 0))", 0)]
    [DataRow("GEOMETRYCOLLECTION(POINT(0 0),POINT(0 0))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(1 1,2 1,2 2,1 2,1 1))", 0)]
    [DataRow("LINESTRING(0 0,1 1,1 1,2 2)", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 1,2 1,2 3,0 3,0 1))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((2 2,4 2,4 4,2 4,2 2)))", 1)]
    [DataRow("POLYGON((0 0, 4 0, 4 4, 0 4, 0 0, 0 0))", 1)]
    [DataRow("POLYGON((0 0,2 0,1 2,0 0),(0 0,2 0,1 1,0 0))", 0)]
    [DataRow("LINESTRING(0 0, 2 0, 1 0)", 0)]
    [DataRow("LINESTRING(0 0, 2 0, 1 1)", 1)]
    [DataRow("LINESTRING(0 0, 2 0, 2 0)", 0)]
    [DataRow("LINESTRING(0 0, 2 0, 0 0, 2 0)", 0)]
    [DataRow("LINESTRING(0 0, 2 2, 2 0, 0 2, 0 0)", 1)]
    [DataRow("MULTILINESTRING((0 0,2 0),(1 0,3 0))", 0)]
    [DataRow("MULTILINESTRING((0 0,2 0),(2 0,4 0))", 1)]
    [DataRow("MULTILINESTRING((0 0,2 0),(1 -1,1 1))", 1)]
    [DataRow("MULTIPOINT((0 0),(1 1))", 1)]
    [DataRow("POINT EMPTY", 1)]
    [DataRow("POLYGON EMPTY", 1)]
    [DataRow("GEOMETRYCOLLECTION EMPTY", 1)]
    [DataRow("GEOMETRYCOLLECTION(POLYGON((0 0, 2 2, 0 2, 2 0, 0 0)))", 0)]
    [DataRow("GEOMETRYCOLLECTION(POLYGON((0 0,2 0,2 2,0 2,0 0)),POLYGON((1 1,3 1,3 3,1 3,1 1)))", 1)]
    [DataRow("GEOMETRYCOLLECTION(LINESTRING(0 0,2 0),LINESTRING(0 0,2 0))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(3 1,3.5 1,3.5 2,3 2,3 1))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(2 2,3 2,3 3,2 3,2 2))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 0,2 2,2 0,0 0))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,4 0,4 4,0 4,0 0)),((1 1,2 1,2 2,1 2,1 1)))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 0,3 0,3 2,1 2,1 0))", 0)]
    [DataRow("LINESTRING(0 0,1 1,2 2,1 1)", 0)]
    [DataRow("MULTIPOINT EMPTY", 1)]
    [DataRow("POLYGON((0 0,1 1,2 0,3 1,4 0,4 4,0 4,0 0))", 1)]
    [DataRow("POLYGON((0 0,2 0,2 2,1 2,1 1,3 1,3 3,0 3,0 0))", 0)]
    [DataRow("LINESTRING(0 0, 0 0, 2 0)", 1)]
    [DataRow("LINESTRING(0 0, 2 0, 2 0, 4 0)", 1)]
    [DataRow("LINESTRING(0 0, 2 0, 2 0, 2 0)", 0)]
    [DataRow("LINESTRING(0 0, 1 1, 1 1, 2 2)", 1)]
    [DataRow("LINESTRING(0 0, 2 2, 0 0)", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(2 1,3 1,3 2,2 2,2 1))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 1,1 1,1 3,0 3,0 1))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 2,1 1,2 2,1 3,0 2))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 2,2 2,2 4,0 4,0 0))", 1)]
    [DataRow("POLYGON((0 0,2 0,2 2,0 2,0 0),(0 0,1 1,1 0,0 0))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(2 2,3 2,3 3,2 3,2 2),(1 2,2 2,2 3,1 3,1 2))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((0 0,-2 0,-2 -2,0 -2,0 0)))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(1 1,0.5 0.5,1.5 0.5,1 1))", 1)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 2,2 1,4 2,2 3,0 2))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(0 1,2 2,0 3,0 1))", 0)]
    [DataRow("POLYGON((0 0,4 0,4 4,0 4,0 0),(1 1,2 1,2 2,1 2,1 1),(2 2,3 2,3 3,2 3,2 2),(2 1,3 1,3 2,2 2,2 1),(1 2,2 2,2 3,1 3,1 2))", 0)]
    [DataRow("MULTIPOLYGON(((0 0,2 0,2 2,0 2,0 0)),((0 2,2 2,2 4,0 4,0 2)))", 0)]
    public void IsValid_MatchesReference(string wkt, int expected)
        => AreEqual(expected == 1, Test($"geometry::STGeomFromText('{wkt}', 0).STIsValid()"));

    /// <summary>
    /// Most of the instance surface refuses a stored-but-invalid instance with
    /// Msg 24144, wrapped in the usual Msg 6522 envelope.
    /// </summary>
    [TestMethod]
    [DataRow("STArea()")]
    [DataRow("STDimension()")]
    [DataRow("STGeometryType()")]
    [DataRow("STNumPoints()")]
    [DataRow("STPointN(1)")]
    [DataRow("STStartPoint()")]
    [DataRow("STEndPoint()")]
    [DataRow("STIsClosed()")]
    [DataRow("STNumGeometries()")]
    [DataRow("STGeometryN(1)")]
    [DataRow("STExteriorRing()")]
    [DataRow("STNumInteriorRing()")]
    [DataRow("InstanceOf('Polygon')")]
    [DataRow("STIntersects(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STContains(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STWithin(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STDisjoint(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STEquals(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STTouches(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STCrosses(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STOverlaps(geometry::Parse('POINT(0 0)'))")]
    [DataRow("STRelate(geometry::Parse('POINT(0 0)'), 'T********')")]
    [DataRow("STDistance(geometry::Parse('POINT(9 9)'))")]
    public void InvalidInstance_RaisesMsg24144(string member)
    {
        var ex = new Simulation().AssertSqlError(
            $"select cast(geometry::STGeomFromText('POLYGON((0 0, 2 2, 0 2, 2 0, 0 0))', 0).{member} as nvarchar(50))",
            6522);
        Assert.Contains(
            "System.ArgumentException: 24144: This operation cannot be completed because the instance is not valid. "
            + "Use MakeValid to convert the instance to a valid instance. Note that MakeValid may cause the points of "
            + "a geometry instance to shift slightly.",
            ex.Message);
    }

    /// <summary>An invalid <i>argument</i> raises 24144 the same way an invalid receiver does.</summary>
    [TestMethod]
    public void InvalidArgument_RaisesMsg24144()
        => _ = new Simulation().AssertSqlError(
            "select geometry::Parse('POINT(0 0)').STIntersects(geometry::STGeomFromText('POLYGON((0 0, 2 2, 0 2, 2 0, 0 0))', 0))",
            6522);

    /// <summary>
    /// The members real answers from regardless of validity: the renderings,
    /// the ordinate reads, <c>STLength</c>, <c>STIsEmpty</c>, <c>STIsRing</c>,
    /// <c>STSrid</c> and <c>STIsValid</c> itself.
    /// </summary>
    [TestMethod]
    public void InvalidInstance_StillAnswersTheTolerantMembers()
    {
        const string Bowtie = "geometry::STGeomFromText('POLYGON((0 0, 2 2, 0 2, 2 0, 0 0))', 0)";
        AreEqual("POLYGON ((0 0, 2 2, 0 2, 2 0, 0 0))", Eval($"{Bowtie}.STAsText()"));
        AreEqual("POLYGON ((0 0, 2 2, 0 2, 2 0, 0 0))", Eval($"{Bowtie}.ToString()"));
        IsFalse(Test($"{Bowtie}.STIsEmpty()"));
        AreEqual(DBNull.Value, Eval($"{Bowtie}.STIsRing()"));
        AreEqual(0, Eval($"{Bowtie}.STSrid"));
        IsFalse(Test($"{Bowtie}.STIsValid()"));
        AreEqual(9.65685424949238d, (double)Eval($"{Bowtie}.STLength()")!, 1e-9);
    }

    /// <summary>Real validates <c>STRelate</c>'s pattern before it looks at the operands.</summary>
    [TestMethod]
    public void RelateMask_MustBeNineCharacters()
    {
        var ex = new Simulation().AssertSqlError(
            "select geometry::Parse('POINT(0 0)').STRelate(geometry::Parse('POINT(0 0)'), '0FFFFFFF')", 6522);
        Assert.Contains(
            "System.FormatException: 24109: The intersectionPatternMatrix argument to STRelate is not valid. "
            + "This argument must contain exactly 9 characters, but the string provided has 8 characters.",
            ex.Message);
    }

    /// <summary>A NULL pattern reports zero characters rather than reading as NULL.</summary>
    [TestMethod]
    public void RelateMask_NullReportsZeroCharacters()
    {
        var ex = new Simulation().AssertSqlError(
            "select geometry::Parse('POINT(0 0)').STRelate(geometry::Parse('POINT(0 0)'), null)", 6522);
        Assert.Contains("has 0 characters", ex.Message);
    }

    /// <summary>The pattern alphabet is <c>0 1 2 T F *</c>, case-sensitive, and real reports the zero-based position.</summary>
    [TestMethod]
    [DataRow("0FFFFFFFX", 8, 'X')]
    [DataRow("t********", 0, 't')]
    [DataRow("3********", 0, '3')]
    public void RelateMask_RejectsForeignCharacters(string mask, int position, char character)
    {
        var ex = new Simulation().AssertSqlError(
            $"select geometry::Parse('POINT(0 0)').STRelate(geometry::Parse('POINT(0 0)'), '{mask}')", 6522);
        Assert.Contains(
            $"System.FormatException: 24110: Character {position} ({character}) of the intersectionPatternMatrix "
            + "argument to STRelate is not valid. This argument must only contain the characters 0, 1, 2, T, F, and *.",
            ex.Message);
    }

    /// <summary>
    /// <c>STCrosses</c>, <c>STTouches</c>, <c>STRelate</c> and <c>STIsSimple</c>
    /// belong to <c>geometry</c> alone; naming one on a <c>geography</c>
    /// receiver is the CLR method-not-found error, not an unmodeled feature.
    /// </summary>
    [TestMethod]
    [DataRow("STCrosses(geography::Parse('POINT(0 0)'))")]
    [DataRow("STTouches(geography::Parse('POINT(0 0)'))")]
    [DataRow("STRelate(geography::Parse('POINT(0 0)'), 'T********')")]
    [DataRow("STIsSimple()")]
    public void GeographyOnlyRejectsTheGeometryOnlyPredicates(string member)
        => _ = new Simulation().AssertSqlError($"select geography::Parse('POINT(0 0)').{member}", 6506);

    /// <summary>A predicate over a stored column reaches the same engine as one over a literal.</summary>
    [TestMethod]
    public void PredicateOverStoredColumn_Evaluates()
        => AreEqual(2, new Simulation().ExecuteScalar("""
            create table dbo.plots (id int not null primary key, shape geometry not null);
            insert dbo.plots values
                (1, geometry::Parse('POLYGON((0 0,2 0,2 2,0 2,0 0))')),
                (2, geometry::Parse('POLYGON((1 1,3 1,3 3,1 3,1 1))')),
                (3, geometry::Parse('POLYGON((9 9,10 9,10 10,9 10,9 9))'));
            select count(*) from dbo.plots where shape.STIntersects(geometry::Parse('POINT(1.5 1.5)')) = 1
            """));
}
