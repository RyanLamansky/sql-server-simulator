using System.Collections.Frozen;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Member access on a <c>geography</c> or <c>geometry</c> value — both the
/// method form <c>expr.STPointN(2)</c> and the property form <c>expr.STX</c>.
/// </summary>
/// <remarks>
/// <para>Real distinguishes the two forms strictly: a method name written
/// without parentheses reports Msg 6592 ("could not find property or field")
/// and a property written with them reports the same, while a member the
/// other spatial type owns reports Msg 6592 for a property and Msg 6506 for a
/// method. <see cref="Members"/> is the catalog those checks read.</para>
/// <para>Structural members — the accessors, counts, component extractors and
/// text/binary renderings — evaluate here. The measures, predicates and
/// constructive operations parse cleanly (so CREATE VIEW / CREATE PROCEDURE
/// bodies referencing them store verbatim) and raise
/// <see cref="NotSupportedException"/> at <see cref="Run"/>.</para>
/// </remarks>
internal sealed class SpatialMethodCall : Expression
{
    /// <summary>Whether a member is written with an argument list.</summary>
    private enum MemberForm
    {
        Property,
        Method,
    }

    /// <summary>Which spatial types expose a member.</summary>
    private enum MemberScope
    {
        Both,
        GeographyOnly,
        GeometryOnly,
    }

    /// <summary>What a member yields, before the receiver's own type is substituted for <see cref="ResultKind.Spatial"/>.</summary>
    private enum ResultKind
    {
        Text,
        Binary,
        Integer,
        Float,
        Boolean,
        Spatial,
    }

    private readonly record struct Member(MemberForm Form, MemberScope Scope, ResultKind Result);

    /// <summary>
    /// Every member the spatial types expose, with the form and owning type
    /// real enforces. Members whose evaluation isn't built still appear here
    /// so their form, scope and result type stay faithful; <see cref="Run"/>
    /// is what reports them unmodeled.
    /// </summary>
    private static readonly FrozenDictionary<string, Member> Members = new Dictionary<string, Member>(StringComparer.Ordinal)
    {
        // Properties.
        ["HasM"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Boolean),
        ["HasZ"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Boolean),
        ["Lat"] = new(MemberForm.Property, MemberScope.GeographyOnly, ResultKind.Float),
        ["Long"] = new(MemberForm.Property, MemberScope.GeographyOnly, ResultKind.Float),
        ["M"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Float),
        ["STSrid"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Integer),
        ["STX"] = new(MemberForm.Property, MemberScope.GeometryOnly, ResultKind.Float),
        ["STY"] = new(MemberForm.Property, MemberScope.GeometryOnly, ResultKind.Float),
        ["Z"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Float),

        // Methods — structural.
        ["AsBinaryZM"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Binary),
        ["AsTextZM"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["InstanceOf"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["MinDbCompatibilityLevel"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer),
        ["NumRings"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Integer),
        ["ReorientObject"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial),
        ["RingN"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial),
        ["STAsBinary"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Binary),
        ["STAsText"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["STDimension"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer),
        ["STDistance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float),
        ["STEndPoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STExteriorRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial),
        ["STGeometryN"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STGeometryType"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["STInteriorRingN"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial),
        ["STIsClosed"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STIsEmpty"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STIsRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STNumGeometries"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer),
        ["STNumInteriorRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Integer),
        ["STNumPoints"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer),
        ["STPointN"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STStartPoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["ToString"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),

        // Methods — parse-only; measures, predicates and constructive operations.
        ["AsGml"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["BufferWithCurves"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["BufferWithTolerance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["CurveToLineWithTolerance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["EnvelopeAngle"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Float),
        ["EnvelopeCenter"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial),
        ["Filter"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["MakeValid"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["Reduce"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STArea"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float),
        ["STAsGML"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["STBoundary"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial),
        ["STBuffer"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STCentroid"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial),
        ["STContains"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STConvexHull"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STCrosses"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STDifference"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STDisjoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STEnvelope"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial),
        ["STEquals"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STIntersection"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STIntersects"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STIsSimple"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STIsValid"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STLength"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float),
        ["STOverlaps"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["STPointOnSurface"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STRelate"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STSymDifference"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STTouches"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean),
        ["STUnion"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
        ["STWithin"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean),
        ["ShortestLineTo"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private readonly Expression target;
    private readonly string memberName;
    private readonly Expression[] arguments;
    private readonly bool writtenAsMethod;

    private SpatialMethodCall(Expression target, string memberName, Expression[] arguments, bool writtenAsMethod)
    {
        this.target = target;
        this.memberName = memberName;
        this.arguments = arguments;
        this.writtenAsMethod = writtenAsMethod;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> names a member written with an
    /// argument list. Checked before falling through to multipart-Reference
    /// dispatch in the expression parser's dotted-name loop.
    /// </summary>
    public static bool IsKnownMethodName(string name) => Members.TryGetValue(name, out var member) && member.Form == MemberForm.Method;

    /// <summary>
    /// Returns true if <paramref name="name"/> names any spatial member. The
    /// parser uses this for the no-argument-list shape, which covers both a
    /// genuine property and a method someone wrote without parentheses —
    /// <see cref="Run"/> reports the latter as real does. Dispatch is limited
    /// to receivers that can't be a table qualifier, since <c>t.Lat</c> is far
    /// more likely to be a column.
    /// </summary>
    public static bool IsKnownMemberName(string name) => Members.ContainsKey(name);

    /// <summary>
    /// Parses <c>expr.MemberName(args)</c>. Cursor enters on <c>(</c>; on
    /// return cursor sits on the closing <c>)</c>.
    /// </summary>
    public static SpatialMethodCall Parse(Expression target, string memberName, ParserContext context)
    {
        var args = new List<Expression>();
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            args.Add(Expression.Parse(context));
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                args.Add(Expression.Parse(context));
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return new SpatialMethodCall(target, memberName, [.. args], writtenAsMethod: true);
    }

    /// <summary>Builds the property form <c>expr.MemberName</c>; the parser has already consumed the name.</summary>
    public static SpatialMethodCall Property(Expression target, string memberName) =>
        new(target, memberName, [], writtenAsMethod: false);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var receiver = this.target.Run(runtime);
        var type = receiver.Type as SpatialSqlType
            ?? throw new NotSupportedException($"'.{this.memberName}' is not a member of {receiver.Type}.");
        var member = ValidateMember(type);

        return receiver.IsNull
            ? SqlValue.Null(ResultType(member.Result, type, runtime.Batch))
            : Evaluate(runtime, receiver.AsSpatial, type, member);
    }

    /// <summary>
    /// Enforces the form and owning type real enforces, reporting Msg 6592 for
    /// a property mismatch and Msg 6506 for a method the type doesn't own.
    /// </summary>
    private Member ValidateMember(SpatialSqlType type)
    {
        if (!Members.TryGetValue(this.memberName, out var member))
            throw ReportMissing(type);
        if (member.Form == MemberForm.Method != this.writtenAsMethod)
            throw ReportMissing(type);
        var allowed = member.Scope switch
        {
            MemberScope.GeographyOnly => type.IsGeography,
            MemberScope.GeometryOnly => !type.IsGeography,
            _ => true,
        };
        return allowed ? member : throw ReportMissing(type);
    }

    private SimulatedSqlException ReportMissing(SpatialSqlType type) => this.writtenAsMethod
        ? SimulatedSqlException.ClrMethodNotFound(this.memberName, type.ClrTypeName)
        : SimulatedSqlException.ClrPropertyNotFound(this.memberName, type.ClrTypeName);

    private SqlValue Evaluate(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type, Member member)
    {
        var root = value.Root;
        var geography = type.IsGeography;
        return this.memberName switch
        {
            "AsBinaryZM" => SqlValue.FromVarbinary(SpatialWkb.Write(value, includeZM: true)),
            "AsTextZM" => Text(runtime, SpatialWktWriter.Write(value, includeZM: true)),
            "HasM" => SqlValue.FromBoolean(root.AnyHasM),
            "HasZ" => SqlValue.FromBoolean(root.AnyHasZ),
            "InstanceOf" => EvaluateInstanceOf(runtime, root, geography),
            "Lat" => Ordinate(root, static p => p.Y),
            "Long" => Ordinate(root, static p => p.X),
            "M" => Ordinate(root, static p => p.M),
            // Real reports the lowest database compatibility level that can
            // read the instance; 100 for every shape the simulator models.
            "MinDbCompatibilityLevel" => SqlValue.FromInt32(100),
            "NumRings" => root.Type == SpatialShapeType.Polygon ? SqlValue.FromInt32(root.Figures.Length) : SqlValue.Null(SqlType.Int32),
            "ReorientObject" => SqlValue.FromSpatial(new SpatialGeometry(value.Srid, Reorient(root)), geography),
            "RingN" => Component(value, type, RingAt(root, Index(runtime, geography, IndexKind.Ring), interiorOnly: false)),
            "STArea" => geography
                ? throw GeographyMeasureNotModeled("STArea")
                : SqlValue.FromDouble(SpatialMeasures.Area(root)),
            "STAsBinary" => SqlValue.FromVarbinary(SpatialWkb.Write(value, includeZM: false)),
            "STAsText" => Text(runtime, SpatialWktWriter.Write(value, includeZM: false)),
            "STDimension" => SqlValue.FromInt32(root.Dimension),
            "STDistance" => EvaluateDistance(runtime, value, geography),
            "STEndPoint" => Component(value, type, EndpointOf(root, first: false)),
            "STExteriorRing" => Component(value, type, RingAt(root, 1, interiorOnly: false)),
            "STGeometryN" => Component(value, type, GeometryAt(root, Index(runtime, geography, IndexKind.Geometry))),
            "STGeometryType" => Text(runtime, GeometryTypeName(root.Type)),
            "STInteriorRingN" => Component(value, type, RingAt(root, Index(runtime, geography, IndexKind.Ring) + 1, interiorOnly: true)),
            "STIsClosed" => SqlValue.FromBoolean(IsClosed(root)),
            "STIsEmpty" => SqlValue.FromBoolean(root.IsEmpty),
            "STIsRing" => root.Type == SpatialShapeType.LineString
                ? SqlValue.FromBoolean(IsClosed(root) && IsSimpleRing(root))
                : SqlValue.Null(SqlType.Bit),
            "STLength" => SqlValue.FromDouble(geography
                ? SpatialMeasures.GeographyLength(root)
                : SpatialMeasures.Length(root)),
            "STNumGeometries" => SqlValue.FromInt32(GeometryCount(root)),
            "STNumInteriorRing" => SqlValue.FromInt32(root.Type == SpatialShapeType.Polygon ? Math.Max(0, root.Figures.Length - 1) : 0),
            "STNumPoints" => SqlValue.FromInt32(root.PointCount),
            "STPointN" => Component(value, type, PointAt(root, Index(runtime, geography, IndexKind.Point))),
            "STSrid" => SqlValue.FromInt32(value.Srid),
            "STStartPoint" => Component(value, type, EndpointOf(root, first: true)),
            "STX" => Ordinate(root, static p => p.X),
            "STY" => Ordinate(root, static p => p.Y),
            "ToString" => Text(runtime, SpatialWktWriter.Write(value, includeZM: true)),
            "Z" => Ordinate(root, static p => p.Z),
            _ => throw new NotSupportedException(
                $"Spatial instance {(member.Form == MemberForm.Method ? "method" : "property")} '.{this.memberName}' is not modeled."),
        };
    }

    /// <summary>
    /// <c>STDistance</c> between two points — round-earth along the great
    /// elliptic arc, planar as straight-line. A NULL or empty operand yields
    /// NULL, matching real. Distance between shapes that aren't both points
    /// needs closest-approach geometry and stays unmodeled.
    /// </summary>
    private SqlValue EvaluateDistance(RuntimeContext runtime, SpatialGeometry value, bool isGeography)
    {
        if (this.arguments.Length == 0)
            return SqlValue.Null(SqlType.Float);
        var other = this.arguments[0].Run(runtime);
        if (other.IsNull || other.Type is not SpatialSqlType)
            return SqlValue.Null(SqlType.Float);
        var otherValue = other.AsSpatial;
        // Operands in different spatial reference systems aren't comparable;
        // real answers NULL rather than raising (probe-confirmed).
        if (otherValue.Srid != value.Srid)
            return SqlValue.Null(SqlType.Float);
        var root = value.Root;
        var otherRoot = otherValue.Root;
        if (root.IsEmpty || otherRoot.IsEmpty)
            return SqlValue.Null(SqlType.Float);
        if (root.SinglePoint is not { } from || otherRoot.SinglePoint is not { } to)
        {
            throw new NotSupportedException(
                "Spatial instance method '.STDistance()' is modeled only between two points; other shapes need closest-approach geometry.");
        }

        if (isGeography)
            return SqlValue.FromDouble(SpatialGreatElliptic.Distance(from, to));
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        return SqlValue.FromDouble(Math.Sqrt((dx * dx) + (dy * dy)));
    }

    private static SqlValue Text(RuntimeContext runtime, string value) =>
        SqlValue.FromNVarchar(NVarcharSqlType.Get(-1, runtime.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault), value);

    /// <summary>A single-ordinate property: defined only on a non-empty Point, NULL everywhere else.</summary>
    private static SqlValue Ordinate(SpatialShape root, Func<SpatialCoordinate, double?> select) =>
        root.SinglePoint is { } point && select(point) is { } ordinate
            ? SqlValue.FromDouble(ordinate)
            : SqlValue.Null(SqlType.Float);

    /// <summary>Wraps an extracted component, or NULL when the index selected nothing.</summary>
    private static SqlValue Component(SpatialGeometry value, SpatialSqlType type, SpatialShape? component) =>
        component is null ? SqlValue.Null(type) : SqlValue.FromSpatial(new SpatialGeometry(value.Srid, component), type.IsGeography);

    /// <summary>Which out-of-range failure an index argument reports.</summary>
    private enum IndexKind
    {
        Point,
        Geometry,
        Ring,
    }

    /// <summary>
    /// Reads the 1-based index argument. Real raises its own out-of-range
    /// failure below 1 but returns NULL above the count, so only the low side
    /// is an error here.
    /// </summary>
    private int Index(RuntimeContext runtime, bool isGeography, IndexKind kind)
    {
        var index = this.arguments.Length == 0 ? 0 : ScalarArguments.CoerceToInt(this.arguments[0].Run(runtime));
        return index >= 1 ? index : throw kind switch
        {
            IndexKind.Point => SimulatedSqlException.SpatialPointIndexTooSmall(isGeography, index),
            IndexKind.Geometry => SimulatedSqlException.SpatialGeometryIndexTooSmall(isGeography, index),
            _ => SimulatedSqlException.SpatialRingIndexTooSmall(isGeography, index),
        };
    }

    /// <summary>
    /// The OGC type names <c>InstanceOf</c> accepts. <c>FullGlobe</c> is
    /// geography-only — naming it against a <c>geometry</c> instance is an
    /// invalid argument (Msg 24105) rather than a false answer.
    /// </summary>
    private static readonly FrozenSet<string> OgcTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CircularString", "CompoundCurve", "Curve", "CurvePolygon", "Geometry", "GeometryCollection", "LineString",
        "MultiCurve", "MultiLineString", "MultiPoint", "MultiPolygon", "MultiSurface", "Point", "Polygon", "Surface",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private SqlValue EvaluateInstanceOf(RuntimeContext runtime, SpatialShape root, bool isGeography)
    {
        var argument = this.arguments.Length == 0 ? SqlValue.Null(SqlType.Bit) : this.arguments[0].Run(runtime);
        if (argument.IsNull)
            return SqlValue.Null(SqlType.Bit);
        var wanted = argument.AsString;
        var known = OgcTypeNames.Contains(wanted)
            || (isGeography && wanted.Equals("FullGlobe", StringComparison.OrdinalIgnoreCase));
        if (!known)
            throw SimulatedSqlException.SpatialInvalidInstanceOfType(isGeography, wanted);
        foreach (var name in OgcAncestry(root.Type))
        {
            if (name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                return SqlValue.FromBoolean(true);
        }
        return SqlValue.FromBoolean(false);
    }

    /// <summary>
    /// The OGC type names an instance answers <c>InstanceOf</c> to — its own
    /// kind plus every supertype. The root is <c>Geometry</c> for both spatial
    /// types; <c>Geography</c> is not a name real recognizes here.
    /// </summary>
    /// <summary>
    /// The round-earth measures need the great elliptic arc real measures
    /// along — not the geodesic, and not a coordinate swap over the planar
    /// code — so they stay unmodeled while the planar ones ship. See
    /// <c>docs/claude/spatial.md</c>.
    /// </summary>
    private static NotSupportedException GeographyMeasureNotModeled(string member) =>
        new($"Spatial instance method '.{member}()' is not modeled for geography (it needs ellipsoidal polygon area).");

    private static string[] OgcAncestry(SpatialShapeType type) => type switch
    {
        SpatialShapeType.Point => ["Point", "Geometry"],
        SpatialShapeType.LineString => ["LineString", "Curve", "Geometry"],
        SpatialShapeType.CircularString => ["CircularString", "Curve", "Geometry"],
        SpatialShapeType.CompoundCurve => ["CompoundCurve", "Curve", "Geometry"],
        SpatialShapeType.Polygon => ["Polygon", "Surface", "Geometry"],
        SpatialShapeType.CurvePolygon => ["CurvePolygon", "Surface", "Geometry"],
        SpatialShapeType.FullGlobe => ["FullGlobe", "Surface", "Geometry"],
        SpatialShapeType.MultiPoint => ["MultiPoint", "GeometryCollection", "Geometry"],
        SpatialShapeType.MultiLineString => ["MultiLineString", "MultiCurve", "GeometryCollection", "Geometry"],
        SpatialShapeType.MultiPolygon => ["MultiPolygon", "MultiSurface", "GeometryCollection", "Geometry"],
        _ => ["GeometryCollection", "Geometry"],
    };

    /// <summary>The spelling <c>STGeometryType()</c> reports — Pascal-cased, not the WKT label.</summary>
    private static string GeometryTypeName(SpatialShapeType type) => type switch
    {
        SpatialShapeType.Point => "Point",
        SpatialShapeType.LineString => "LineString",
        SpatialShapeType.Polygon => "Polygon",
        SpatialShapeType.MultiPoint => "MultiPoint",
        SpatialShapeType.MultiLineString => "MultiLineString",
        SpatialShapeType.MultiPolygon => "MultiPolygon",
        SpatialShapeType.GeometryCollection => "GeometryCollection",
        SpatialShapeType.CircularString => "CircularString",
        SpatialShapeType.CompoundCurve => "CompoundCurve",
        SpatialShapeType.CurvePolygon => "CurvePolygon",
        _ => "FullGlobe",
    };

    /// <summary>
    /// <c>STNumGeometries()</c>: a collection reports its member count, and any
    /// other kind reports 1 when non-empty and 0 when empty.
    /// </summary>
    private static int GeometryCount(SpatialShape root) => root.Type switch
    {
        SpatialShapeType.MultiPoint or SpatialShapeType.MultiLineString
            or SpatialShapeType.MultiPolygon or SpatialShapeType.GeometryCollection => root.Children.Length,
        _ => root.IsEmpty ? 0 : 1,
    };

    private static SpatialShape? GeometryAt(SpatialShape root, int index)
    {
        return root.Type is SpatialShapeType.MultiPoint or SpatialShapeType.MultiLineString
            or SpatialShapeType.MultiPolygon or SpatialShapeType.GeometryCollection
            ? index <= root.Children.Length ? root.Children[index - 1] : null
            : index == 1 && !root.IsEmpty ? root : null;
    }

    /// <summary>The <paramref name="index"/>-th coordinate in figure order, wrapped as a Point.</summary>
    private static SpatialShape? PointAt(SpatialShape root, int index)
    {
        var remaining = index;
        foreach (var point in root.Coordinates())
        {
            if (--remaining == 0)
                return SpatialShape.Leaf(SpatialShapeType.Point, [[point]]);
        }
        return null;
    }

    /// <summary>
    /// A polygon ring as a LineString. <paramref name="interiorOnly"/> shifts
    /// the caller's 1-based interior index past the exterior ring, which is
    /// how <c>STInteriorRingN</c> differs from geography's <c>RingN</c>.
    /// </summary>
    private static SpatialShape? RingAt(SpatialShape root, int index, bool interiorOnly)
    {
        return root.Type != SpatialShapeType.Polygon || index > root.Figures.Length || (interiorOnly && index < 2)
            ? null
            : SpatialShape.Leaf(SpatialShapeType.LineString, [root.Figures[index - 1]]);
    }

    private static SpatialShape? EndpointOf(SpatialShape root, bool first)
    {
        if (root.Figures.Length == 0 || root.Figures[0].Length == 0)
            return null;
        var figure = first ? root.Figures[0] : root.Figures[^1];
        return SpatialShape.Leaf(SpatialShapeType.Point, [[first ? figure[0] : figure[^1]]]);
    }

    /// <summary>
    /// <c>STIsClosed()</c>: every figure starts and ends at the same point.
    /// An empty instance, a Point and a mixed GeometryCollection all report
    /// false on real.
    /// </summary>
    private static bool IsClosed(SpatialShape shape)
    {
        if (shape.IsEmpty || shape.Type is SpatialShapeType.Point or SpatialShapeType.MultiPoint)
            return false;
        foreach (var figure in shape.Figures)
        {
            if (figure.Length < 2 || figure[0].X != figure[^1].X || figure[0].Y != figure[^1].Y)
                return false;
        }
        foreach (var child in shape.Children)
        {
            if (!IsClosed(child))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The non-self-intersection half of <c>STIsRing()</c>, checked only for
    /// the repeated-vertex case a ring can be tested for without a full
    /// segment-intersection pass.
    /// </summary>
    private static bool IsSimpleRing(SpatialShape shape)
    {
        var figure = shape.Figures[0];
        var seen = new HashSet<(double, double)>();
        for (var i = 0; i < figure.Length - 1; i++)
        {
            if (!seen.Add((figure[i].X, figure[i].Y)))
                return false;
        }
        return true;
    }

    /// <summary>Reverses every figure's point order — geography's <c>ReorientObject()</c>, which flips ring orientation.</summary>
    private static SpatialShape Reorient(SpatialShape shape)
    {
        var figures = new SpatialCoordinate[shape.Figures.Length][];
        for (var i = 0; i < shape.Figures.Length; i++)
        {
            var source = shape.Figures[i];
            var reversed = new SpatialCoordinate[source.Length];
            for (var j = 0; j < source.Length; j++)
                reversed[j] = source[source.Length - 1 - j];
            figures[i] = reversed;
        }
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < shape.Children.Length; i++)
            children[i] = Reorient(shape.Children[i]);
        return new SpatialShape(shape.Type, figures, children);
    }

    /// <summary>
    /// Static result type used by projection-schema inference. Boolean-yielding
    /// members return <c>bit</c>, measurements <c>float</c>, counts and the
    /// SRID <c>int</c>, renderings <c>nvarchar(MAX)</c> / <c>varbinary(MAX)</c>,
    /// and component extractors the receiver's own spatial type.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var receiver = this.target.GetSqlType(batch, resolveColumnType);
        return receiver is not SpatialSqlType spatial
            ? NVarcharSqlType.Get(-1, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault)
            : ResultType(ValidateMember(spatial).Result, spatial, batch);
    }

    private static SqlType ResultType(ResultKind kind, SpatialSqlType receiver, BatchContext batch) => kind switch
    {
        ResultKind.Text => NVarcharSqlType.Get(-1, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault),
        ResultKind.Binary => VarbinarySqlType.MaxForm,
        ResultKind.Integer => SqlType.Int32,
        ResultKind.Float => SqlType.Float,
        ResultKind.Boolean => SqlType.Bit,
        _ => receiver,
    };

    internal override string DebugDisplay() => this.writtenAsMethod
        ? $"({this.target.DebugDisplay()}).{this.memberName}(…)"
        : $"({this.target.DebugDisplay()}).{this.memberName}";

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        this.target.VisitColumnReferences(visit);
        foreach (var argument in this.arguments)
            argument.VisitColumnReferences(visit);
    }
}
