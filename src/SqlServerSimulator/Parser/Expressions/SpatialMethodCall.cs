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
/// text/binary renderings — evaluate here, as do all three measures and the
/// topological surface of both spatial types, <c>geometry</c>'s
/// <c>STCentroid</c> / <c>STPointOnSurface</c> / <c>STIsSimple</c>,
/// <c>geography</c>'s <c>EnvelopeAngle</c> / <c>EnvelopeCenter</c>, and the
/// constructive operations. The members whose evaluation isn't built parse
/// cleanly (so CREATE VIEW / CREATE PROCEDURE bodies referencing them store
/// verbatim) and raise <see cref="NotSupportedException"/> at
/// <see cref="Run"/>.</para>
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

    /// <summary>
    /// Whether a member refuses to run against a stored-but-invalid instance.
    /// Real splits its surface sharply: the renderings, the ordinate reads,
    /// <c>STLength</c>, <c>STIsEmpty</c>, <c>STIsRing</c>, <c>STSrid</c>,
    /// <c>MakeValid</c> and <c>STIsValid</c> itself answer regardless, while
    /// everything structural or topological reports Msg 24144.
    /// </summary>
    private enum ValidityGate
    {
        Tolerant,
        Required,
    }

    /// <summary>
    /// Whether a member reads a curved instance as written. The rest read its
    /// linearization (<see cref="SpatialCurves.ForOperations"/>), which is what
    /// real's own operations do for every member without a curve path.
    /// </summary>
    private enum CurveReading
    {
        Linearized,
        AsWritten,
    }

    private readonly struct Member(MemberForm form, MemberScope scope, ResultKind result, ValidityGate gate = ValidityGate.Tolerant, CurveReading curves = CurveReading.Linearized)
    {
        public readonly MemberForm Form = form;
        public readonly MemberScope Scope = scope;
        public readonly ResultKind Result = result;
        public readonly ValidityGate Gate = gate;
        public readonly CurveReading Curves = curves;
    }

    private const ValidityGate Tolerant = ValidityGate.Tolerant;
    private const ValidityGate Required = ValidityGate.Required;
    private const CurveReading AsWritten = CurveReading.AsWritten;

    /// <summary>
    /// Every member the spatial types expose, with the form and owning type
    /// real enforces. Members whose evaluation isn't built still appear here
    /// so their form, scope and result type stay faithful; <see cref="Run"/>
    /// is what reports them unmodeled.
    /// </summary>
    private static readonly FrozenDictionary<string, Member> Members = new Dictionary<string, Member>(StringComparer.Ordinal)
    {
        // Properties.
        ["HasM"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Boolean, Tolerant, AsWritten),
        ["HasZ"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Boolean, Tolerant, AsWritten),
        ["Lat"] = new(MemberForm.Property, MemberScope.GeographyOnly, ResultKind.Float, Tolerant, AsWritten),
        ["Long"] = new(MemberForm.Property, MemberScope.GeographyOnly, ResultKind.Float, Tolerant, AsWritten),
        ["M"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Float, Tolerant, AsWritten),
        ["STSrid"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Integer, Tolerant, AsWritten),
        ["STX"] = new(MemberForm.Property, MemberScope.GeometryOnly, ResultKind.Float, Tolerant, AsWritten),
        ["STY"] = new(MemberForm.Property, MemberScope.GeometryOnly, ResultKind.Float, Tolerant, AsWritten),
        ["Z"] = new(MemberForm.Property, MemberScope.Both, ResultKind.Float, Tolerant, AsWritten),

        // Methods — structural.
        ["AsBinaryZM"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Binary, Tolerant, AsWritten),
        ["AsTextZM"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text, Tolerant, AsWritten),
        ["EnvelopeAngle"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Float, Required),
        ["EnvelopeCenter"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial, Required),
        ["InstanceOf"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required, AsWritten),
        ["IsValidDetailed"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text, Tolerant, AsWritten),
        ["MinDbCompatibilityLevel"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer, Tolerant, AsWritten),
        ["NumRings"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Integer, Required, AsWritten),
        ["ReorientObject"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial, Required, AsWritten),
        ["RingN"] = new(MemberForm.Method, MemberScope.GeographyOnly, ResultKind.Spatial, Required, AsWritten),
        ["STAsBinary"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Binary, Tolerant, AsWritten),
        ["STAsText"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text, Tolerant, AsWritten),
        ["STCentroid"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required),
        ["STCurveN"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STCurveToLine"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STDimension"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer, Required, AsWritten),
        ["STDistance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float, Required, AsWritten),
        ["STEndPoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STExteriorRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required, AsWritten),
        ["STGeometryN"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STGeometryType"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text, Required, AsWritten),
        ["STInteriorRingN"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required, AsWritten),
        ["STIsClosed"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required, AsWritten),
        ["STIsEmpty"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Tolerant, AsWritten),
        ["STIsRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean, Tolerant, AsWritten),
        ["STIsSimple"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean, Required),
        ["STNumCurves"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer, Required, AsWritten),
        ["STNumGeometries"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer, Required, AsWritten),
        ["STNumInteriorRing"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Integer, Required, AsWritten),
        ["STNumPoints"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Integer, Required, AsWritten),
        ["STPointN"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STPointOnSurface"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required),
        ["STStartPoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["ToString"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text, Tolerant, AsWritten),

        // Methods — topological predicates and validity.
        ["STContains"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["STCrosses"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean, Required),
        ["STDisjoint"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["STEquals"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["STIntersects"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["STIsValid"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Tolerant, AsWritten),
        ["STOverlaps"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["STRelate"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean, Required),
        ["STTouches"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Boolean, Required),
        ["STWithin"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),

        // Methods — the constructive operations, and the members that only parse.
        ["AsGml"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["BufferWithCurves"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["BufferWithTolerance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["CurveToLineWithTolerance"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["Filter"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Boolean, Required),
        ["MakeValid"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Tolerant, AsWritten),
        ["Reduce"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STArea"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float, Required, AsWritten),
        ["STAsGML"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Text),
        ["STBoundary"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required),
        ["STBuffer"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required, AsWritten),
        ["STConvexHull"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
        ["STDifference"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
        ["STEnvelope"] = new(MemberForm.Method, MemberScope.GeometryOnly, ResultKind.Spatial, Required, AsWritten),
        ["STIntersection"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
        ["STLength"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Float, Tolerant, AsWritten),
        ["STSymDifference"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
        ["STUnion"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
        ["ShortestLineTo"] = new(MemberForm.Method, MemberScope.Both, ResultKind.Spatial, Required),
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
    /// <see cref="Run"/> reports the latter as real does.
    /// </summary>
    public static bool IsKnownMemberName(string name) => Members.ContainsKey(name);

    /// <summary>
    /// Decides whether <c>&lt;qualifier&gt;.&lt;member&gt;</c> written over a
    /// FROM source is a spatial <i>column</i>'s property read rather than an
    /// <c>alias.column</c> reference. Nothing in the syntax separates them, so
    /// real settles it against the scope — and reports <b>Msg 326</b> when both
    /// readings bind, which is the case a source aliased like the spatial
    /// column and carrying a column named like the member produces.
    /// </summary>
    /// <remarks>
    /// <para>A name that binds neither way is left alone so the ordinary
    /// column-resolution error (Msg 4104 / 207) reports it, and a scope-less
    /// site — a CHECK constraint, a computed column, an UPDATE's SET list —
    /// simply never reaches the property reading.</para>
    /// <para>The leaf isn't consulted, because real doesn't: once the qualifier
    /// is a spatial column, an unrecognized member is Msg 6592 rather than a
    /// column failure. Real does refuse the four-part spelling
    /// (<c>dbo.t.Location.Lat</c>) outright, so the qualifier stops at two
    /// parts.</para>
    /// </remarks>
    public static bool BindsAsColumnProperty(MultiPartName qualifier, string member, ParserContext context)
    {
        if (context.ScopeSources is not { Length: > 0 } sources || qualifier.Count > 2)
            return false;
        var (columnSource, columnIndex) = TryFind(sources, qualifier);
        if (columnSource < 0 || sources[columnSource].Columns[columnIndex].Type is not SpatialSqlType)
            return false;
        if (TryFind(sources, qualifier.WithAddedPart(member)).SourceIndex < 0)
            return true;
        // Both readings bind. Real calls that ambiguous when the leaf names a
        // member, and reads the column when it doesn't.
        return IsKnownMemberName(member)
            ? throw SimulatedSqlException.AmbiguousSpatialPropertyOrColumn(qualifier.ToString(), member)
            : false;
    }

    /// <summary>
    /// Non-throwing column lookup: an ambiguous name reads as unbound here, and
    /// the fall-through to an ordinary column reference reports it.
    /// </summary>
    private static (int SourceIndex, int ColumnIndex) TryFind(FromSource[] sources, MultiPartName name)
    {
        try
        {
            return Selection.FindSourceColumn(sources, name);
        }
        catch (SimulatedSqlException)
        {
            return (-1, -1);
        }
    }

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
        var geography = type.IsGeography;
        if (AnswersBeforeValidity(runtime, value.Root, geography) is { } early)
            return early;
        if (geography && this.FullGlobeAnswer(runtime, value) is { } whole)
            return whole;
        RequireValidInstance(member, value, geography);
        if (member.Curves == CurveReading.Linearized && value.Root.IsCurved)
            value = new SpatialGeometry(value.Srid, SpatialCurves.ForOperations(value.Root, geography));
        var root = value.Root;
        return this.memberName switch
        {
            "AsBinaryZM" => SqlValue.FromVarbinary(SpatialWkb.Write(value, includeZM: true)),
            "AsTextZM" => Text(runtime, SpatialWktWriter.Write(value, includeZM: true)),
            "BufferWithCurves" => this.EvaluateBufferWithCurves(runtime, value, type),
            "BufferWithTolerance" => this.EvaluateBuffer(runtime, value, type, withTolerance: true),
            "CurveToLineWithTolerance" => this.EvaluateCurveToLine(runtime, value, type),
            "EnvelopeAngle" => SpatialEnvelope.Angle(root) is { } angle ? SqlValue.FromDouble(angle) : SqlValue.Null(SqlType.Float),
            "EnvelopeCenter" => Component(value, type, PointOf(SpatialEnvelope.Center(root))),
            // Without a spatial index, Filter is STIntersects (Microsoft's documented contract).
            "Filter" => Predicate(runtime, value, geography, SpatialPredicateKind.Intersects),
            "HasM" => SqlValue.FromBoolean(root.AnyHasM),
            "HasZ" => SqlValue.FromBoolean(root.AnyHasZ),
            "InstanceOf" => EvaluateInstanceOf(runtime, root, geography),
            // Real's report names the rule an invalid instance breaks; only the valid answer is modeled.
            "IsValidDetailed" => value.IsValidFor(geography)
                ? Text(runtime, "24400: Valid")
                : throw new NotSupportedException("IsValidDetailed's report for an invalid instance is not modeled."),
            "Lat" => Ordinate(root, static p => p.Y),
            "Long" => Ordinate(root, static p => p.X),
            "M" => Ordinate(root, static p => p.M),
            "MakeValid" => value.IsValidFor(geography)
                ? SqlValue.FromSpatial(value, geography)
                : geography
                    ? SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialGeodeticConstructive.MakeValid(SpatialCurves.ForOperations(root, geography))), isGeography: true)
                    : Constructed(value, SpatialSimplify.MakeValid(SpatialCurves.ForOperations(root, geography))),
            // Real reports the lowest database compatibility level that can
            // read the instance: 110 for anything version 1 of the
            // serialization can't hold, 100 otherwise.
            "MinDbCompatibilityLevel" => SqlValue.FromInt32(root.IsCurved || (geography && SpatialEnvelope.IsLargerThanAHemisphere(root)) ? 110 : 100),
            "NumRings" => root.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon ? SqlValue.FromInt32(root.Figures.Length) : SqlValue.Null(SqlType.Int32),
            "Reduce" => this.EvaluateReduce(runtime, value, type),
            "ReorientObject" => SqlValue.FromSpatial(new SpatialGeometry(value.Srid, Reorient(root)), geography),
            "RingN" => Component(value, type, RingAt(root, Index(runtime, geography, IndexKind.Ring), interiorOnly: false)),
            "STArea" => SqlValue.FromDouble(!geography ? SpatialMeasures.Area(root)
                : root.IsCurved ? SpatialCurves.GeographyMeasure(root, area: true) : SpatialMeasures.GeographyArea(root)),
            "STAsBinary" => SqlValue.FromVarbinary(SpatialWkb.Write(value, includeZM: false)),
            "STAsText" => Text(runtime, SpatialWktWriter.Write(value, includeZM: false)),
            "STBoundary" => Constructed(value, SpatialConstructive.Boundary(root)),
            "STBuffer" => this.EvaluateBuffer(runtime, value, type, withTolerance: false),
            "STCentroid" => Component(value, type, PointOf(SpatialCentroid.Centroid(root))),
            "STContains" => Predicate(runtime, value, geography, SpatialPredicateKind.Contains),
            "STConvexHull" => geography
                ? SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialGeodeticConstructive.ConvexHull(root)), isGeography: true)
                : Constructed(value, SpatialConstructive.ConvexHull(root)),
            "STCrosses" => Predicate(runtime, value, geography, SpatialPredicateKind.Crosses),
            "STCurveN" => Component(value, type, CurveAt(root, Index(runtime, geography, IndexKind.Curve))),
            "STCurveToLine" => CurveToLine(value, type, 0.001, relative: true),
            "STDifference" => this.EvaluateOverlay(runtime, value, type, SpatialOverlayOperation.Difference),
            "STDimension" => SqlValue.FromInt32(root.Dimension),
            "STDisjoint" => Predicate(runtime, value, geography, SpatialPredicateKind.Disjoint),
            "STDistance" => EvaluateDistance(runtime, value, geography),
            "STEndPoint" => Component(value, type, EndpointOf(root, first: false)),
            "STEnvelope" => Constructed(value, root.IsCurved ? SpatialCurves.Envelope(root) : SpatialConstructive.Envelope(root)),
            "STEquals" => Predicate(runtime, value, geography, SpatialPredicateKind.Equals),
            "STExteriorRing" => Component(value, type, RingAt(root, 1, interiorOnly: false)),
            "STGeometryN" => Component(value, type, GeometryAt(root, Index(runtime, geography, IndexKind.Geometry))),
            "STGeometryType" => Text(runtime, GeometryTypeName(root.Type)),
            "STInteriorRingN" => Component(value, type, RingAt(root, Index(runtime, geography, IndexKind.Ring) + 1, interiorOnly: true)),
            "STIntersection" => this.EvaluateOverlay(runtime, value, type, SpatialOverlayOperation.Intersection),
            "STIntersects" => Predicate(runtime, value, geography, SpatialPredicateKind.Intersects),
            "STIsClosed" => SqlValue.FromBoolean(IsClosed(root)),
            "STIsEmpty" => SqlValue.FromBoolean(root.IsEmpty),
            "STIsRing" => root.Type switch
            {
                SpatialShapeType.LineString => SqlValue.FromBoolean(IsClosed(root) && IsSimpleRing(root)),
                SpatialShapeType.CircularString or SpatialShapeType.CompoundCurve => SqlValue.FromBoolean(
                    IsClosed(root) && value.IsPlanarValid && SpatialSimplicity.IsSimple(SpatialCurves.ForOperations(root, isGeography: false))),
                _ => SqlValue.Null(SqlType.Bit),
            },
            "STIsSimple" => SqlValue.FromBoolean(SpatialSimplicity.IsSimple(root)),
            "STIsValid" => SqlValue.FromBoolean(value.IsValidFor(geography)),
            "STLength" => SqlValue.FromDouble(!geography ? SpatialMeasures.Length(root)
                : root.IsCurved ? SpatialCurves.GeographyMeasure(root, area: false) : SpatialMeasures.GeographyLength(root)),
            "STNumCurves" => SpatialCurves.Curves(root) is { } curves ? SqlValue.FromInt32(curves.Length) : SqlValue.Null(SqlType.Int32),
            "STNumGeometries" => SqlValue.FromInt32(GeometryCount(root)),
            "STNumInteriorRing" => root.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon
                ? SqlValue.FromInt32(Math.Max(0, root.Figures.Length - 1))
                : SqlValue.Null(SqlType.Int32),
            "STNumPoints" => SqlValue.FromInt32(root.PointCount),
            "STOverlaps" => Predicate(runtime, value, geography, SpatialPredicateKind.Overlaps),
            "STPointN" => Component(value, type, PointAt(root, Index(runtime, geography, IndexKind.Point))),
            "STPointOnSurface" => Component(value, type, PointOf(SpatialCentroid.PointOnSurface(root))),
            "STRelate" => EvaluateRelate(runtime, value, geography),
            "STSrid" => SqlValue.FromInt32(value.Srid),
            "STStartPoint" => Component(value, type, EndpointOf(root, first: true)),
            "STSymDifference" => this.EvaluateOverlay(runtime, value, type, SpatialOverlayOperation.SymmetricDifference),
            "STTouches" => Predicate(runtime, value, geography, SpatialPredicateKind.Touches),
            "STUnion" => this.EvaluateOverlay(runtime, value, type, SpatialOverlayOperation.Union),
            "STWithin" => Predicate(runtime, value, geography, SpatialPredicateKind.Within),
            "STX" => Ordinate(root, static p => p.X),
            "STY" => Ordinate(root, static p => p.Y),
            "ShortestLineTo" => this.EvaluateShortestLine(runtime, value, type),
            "ToString" => Text(runtime, SpatialWktWriter.Write(value, includeZM: true)),
            "Z" => Ordinate(root, static p => p.Z),
            _ => throw new NotSupportedException(
                $"Spatial instance {(member.Form == MemberForm.Method ? "method" : "property")} '.{this.memberName}' is not modeled."),
        };
    }

    /// <summary>
    /// The answers real settles from the instance's kind or the arguments
    /// before it asks whether the instance is valid: the ring and centroid
    /// members are NULL on a kind without rings, and <c>STCurveN</c> checks
    /// its index first. A curved instance's <c>STIsRing</c>, unlike a
    /// <c>LINESTRING</c>'s, is gated.
    /// </summary>
    private SqlValue? AnswersBeforeValidity(RuntimeContext runtime, SpatialShape root, bool isGeography)
    {
        var areal = root.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon;
        switch (this.memberName)
        {
            case "STCentroid":
                return areal || root.Type == SpatialShapeType.MultiPolygon ? null : SqlValue.Null(SqlType.Geometry);
            case "STCurveN":
                _ = this.Index(runtime, isGeography, IndexKind.Curve);
                return null;
            case "STExteriorRing":
            case "STInteriorRingN":
                return areal ? null : SqlValue.Null(isGeography ? SqlType.Geography : SqlType.Geometry);
            case "STIsRing":
                return root.IsCurved && !root.IsEmpty && !SpatialCurves.IsPlanarValid(root)
                    ? throw SimulatedSqlException.SpatialInstanceNotValid(isGeography)
                    : null;
            case "STNumInteriorRing":
                return areal ? null : SqlValue.Null(SqlType.Int32);
            default:
                return null;
        }
    }

    /// <summary>
    /// The members whose answer <c>FULLGLOBE</c> settles, as receiver or
    /// argument, all probed 2026-09-29 against SQL Server 2025: the whole globe
    /// has real's area constant and no length, buffers and hulls to itself,
    /// meets and contains every non-empty instance and lies within only
    /// itself, answers a union with itself, an intersection with the other
    /// operand and a difference with the other's complement.
    /// </summary>
    private SqlValue? FullGlobeAnswer(RuntimeContext runtime, SpatialGeometry value)
    {
        var root = value.Root;
        var receiverIsGlobe = root.Type == SpatialShapeType.FullGlobe;
        switch (GlobeRoleOf(this.memberName))
        {
            case GlobeRole.None:
                return null;
            case GlobeRole.Area:
                return receiverIsGlobe ? SqlValue.FromDouble(FullGlobeArea) : null;
            case GlobeRole.Length:
                return receiverIsGlobe ? SqlValue.FromDouble(0) : null;
            case GlobeRole.Itself:
                return receiverIsGlobe ? SqlValue.FromSpatial(value, isGeography: true) : null;
            default:
                break;
        }
        if (Operand(runtime, 0, value, isGeography: true, asWritten: true) is not { } argument)
            return null;
        var other = argument.Root;
        var argumentIsGlobe = other.Type == SpatialShapeType.FullGlobe;
        if (!receiverIsGlobe && !argumentIsGlobe)
            return null;
        var globe = SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialShape.Empty(SpatialShapeType.FullGlobe)), isGeography: true);
        var both = receiverIsGlobe && argumentIsGlobe;
        var meets = !root.IsEmpty && !other.IsEmpty;
        return this.memberName switch
        {
            "Filter" or "STIntersects" => SqlValue.FromBoolean(meets),
            "STDisjoint" => SqlValue.FromBoolean(!meets),
            "STContains" => SqlValue.FromBoolean(meets && receiverIsGlobe),
            "STWithin" => SqlValue.FromBoolean(meets && argumentIsGlobe),
            "STEquals" => SqlValue.FromBoolean(both),
            "STOverlaps" => SqlValue.FromBoolean(false),
            "STDistance" => meets ? SqlValue.FromDouble(0) : SqlValue.Null(SqlType.Float),
            "STUnion" => globe,
            "STIntersection" => receiverIsGlobe ? SqlValue.FromSpatial(new SpatialGeometry(value.Srid, other), isGeography: true) : SqlValue.FromSpatial(value, isGeography: true),
            _ when !receiverIsGlobe || both => SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialShape.Empty(SpatialShapeType.GeometryCollection)), isGeography: true),
            _ => SqlValue.FromSpatial(new SpatialGeometry(value.Srid, Complement(other)), isGeography: true),
        };
    }

    /// <summary>How a member treats <c>FULLGLOBE</c>.</summary>
    private enum GlobeRole
    {
        None,
        Area,
        Length,
        Itself,
        Binary,
    }

    private static GlobeRole GlobeRoleOf(string member) => member switch
    {
        "BufferWithCurves" => GlobeRole.Itself,
        "BufferWithTolerance" => GlobeRole.Itself,
        "Filter" => GlobeRole.Binary,
        "Reduce" => GlobeRole.Itself,
        "STArea" => GlobeRole.Area,
        "STBuffer" => GlobeRole.Itself,
        "STContains" => GlobeRole.Binary,
        "STConvexHull" => GlobeRole.Itself,
        "STDifference" => GlobeRole.Binary,
        "STDisjoint" => GlobeRole.Binary,
        "STDistance" => GlobeRole.Binary,
        "STEquals" => GlobeRole.Binary,
        "STIntersection" => GlobeRole.Binary,
        "STIntersects" => GlobeRole.Binary,
        "STLength" => GlobeRole.Length,
        "STOverlaps" => GlobeRole.Binary,
        "STSymDifference" => GlobeRole.Binary,
        "STUnion" => GlobeRole.Binary,
        "STWithin" => GlobeRole.Binary,
        _ => GlobeRole.None,
    };

    /// <summary>Real's <c>STArea()</c> for <c>FULLGLOBE</c>, probed 2026-09-29.</summary>
    private const double FullGlobeArea = 510065621710996.44;

    /// <summary>
    /// What the whole globe less an instance leaves: the globe itself when the
    /// instance holds no area, and the reversed ring of a single-ring polygon.
    /// </summary>
    private static SpatialShape Complement(SpatialShape shape)
    {
        if (shape.Dimension < 2)
            return SpatialShape.Empty(SpatialShapeType.FullGlobe);
        return shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon && shape.Figures.Length == 1
            ? Reorient(shape)
            : throw new NotSupportedException("The complement of a geography instance other than a single-ring polygon is not modeled.");
    }

    /// <summary>
    /// One of the eight topological predicates. NULL propagates from either
    /// operand and a spatial-reference mismatch reads NULL as well, matching
    /// real; an invalid <i>argument</i> raises 24144 the same way an invalid
    /// receiver does.
    /// </summary>
    private SqlValue Predicate(RuntimeContext runtime, SpatialGeometry value, bool isGeography, SpatialPredicateKind kind)
    {
        return Operand(runtime, 0, value, isGeography) is { } other
            ? SqlValue.FromBoolean(isGeography
                ? SpatialGeodeticRelate.Evaluate(kind, value.Root, other.Root)
                : SpatialRelate.Evaluate(kind, value.Root, other.Root))
            : SqlValue.Null(SqlType.Bit);
    }

    /// <summary>
    /// One of the four set operations. A NULL or differently-referenced
    /// operand yields NULL, as it does for the predicates; the result carries
    /// the receiver's SRID.
    /// </summary>
    private SqlValue EvaluateOverlay(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type, SpatialOverlayOperation operation)
    {
        if (Operand(runtime, 0, value, type.IsGeography) is not { } other)
            return SqlValue.Null(type);
        return type.IsGeography
            ? SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialGeodeticConstructive.Overlay(operation, value.Root, other.Root)), isGeography: true)
            : Constructed(value, SpatialConstructive.Overlay(operation, value.Root, other.Root));
    }

    /// <summary>
    /// Real refuses most of its instance surface on a stored-but-invalid value,
    /// asking the question in the terms of the receiver's own spatial type —
    /// planar for <c>geometry</c>, round-earth for <c>geography</c>.
    /// </summary>
    private static void RequireValidInstance(Member member, SpatialGeometry value, bool isGeography)
    {
        if (member.Gate == ValidityGate.Required && !value.IsValidFor(isGeography))
            throw SimulatedSqlException.SpatialInstanceNotValid(isGeography);
    }

    /// <summary>
    /// <c>STRelate</c> — the raw DE-9IM pattern match the eight named
    /// predicates are masks over. Real validates the pattern before anything
    /// else: nine characters (Msg 24109, counting a NULL as zero) drawn from
    /// <c>0 1 2 T F *</c> (Msg 24110, case-sensitive and zero-based).
    /// </summary>
    private SqlValue EvaluateRelate(RuntimeContext runtime, SpatialGeometry value, bool isGeography)
    {
        var pattern = this.arguments.Length > 1 ? this.arguments[1].Run(runtime) : SqlValue.Null(SqlType.Bit);
        var mask = pattern.IsNull ? string.Empty : pattern.AsString;
        if (mask.Length != 9)
            throw SimulatedSqlException.SpatialRelateMaskLength(isGeography, mask.Length);
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] is not ('0' or '1' or '2' or 'F' or 'T' or '*'))
                throw SimulatedSqlException.SpatialRelateMaskCharacter(isGeography, i, mask[i]);
        }
        return Operand(runtime, 0, value, isGeography) is { } other
            ? SqlValue.FromBoolean(SpatialRelate.Matches(SpatialRelate.Matrix(value.Root, other.Root), mask))
            : SqlValue.Null(SqlType.Bit);
    }

    /// <summary>
    /// Reads a comparison operand: NULL, a spatial reference that doesn't match
    /// the receiver's, or a non-spatial argument that won't parse as one all
    /// read as "no answer" and leave the caller returning NULL. An invalid
    /// argument raises 24144 the way an invalid receiver does, judged by the
    /// receiver's own spatial type.
    /// </summary>
    private SpatialGeometry? Operand(RuntimeContext runtime, int position, SpatialGeometry receiver, bool isGeography, bool asWritten = false)
    {
        if (this.arguments.Length <= position)
            return null;
        var argument = this.arguments[position].Run(runtime);
        if (argument.IsNull)
            return null;
        // Real accepts a string here and reads it as well-known text.
        var spatial = argument.Type is SpatialSqlType
            ? argument.AsSpatial
            : SpatialWktReader.Read(argument.AsString, SpatialGeometry.DefaultSridFor(isGeography), isGeography);
        if (spatial.Srid != receiver.Srid)
            return null;
        if (!spatial.IsValidFor(isGeography))
            throw SimulatedSqlException.SpatialInstanceNotValid(isGeography);
        return spatial.Root.IsCurved && !asWritten ? new SpatialGeometry(spatial.Srid, SpatialCurves.ForOperations(spatial.Root, isGeography)) : spatial;
    }

    /// <summary>
    /// <c>STDistance</c> — the closest approach between two instances of any
    /// shape, straight-line for <c>geometry</c> and along the great elliptic arc
    /// for <c>geography</c>. Instances that meet, and one containing the other,
    /// measure zero; a NULL, empty or differently-referenced operand yields
    /// NULL, matching real.
    /// </summary>
    private SqlValue EvaluateDistance(RuntimeContext runtime, SpatialGeometry value, bool isGeography)
    {
        if (Operand(runtime, 0, value, isGeography, asWritten: true) is not { } other)
            return SqlValue.Null(SqlType.Float);
        var root = value.Root;
        var otherRoot = other.Root;
        if (root.IsEmpty || otherRoot.IsEmpty)
            return SqlValue.Null(SqlType.Float);
        if (isGeography)
            return SqlValue.FromDouble(SpatialMeasures.GeographyDistance(SpatialCurves.ForOperations(root, isGeography), SpatialCurves.ForOperations(otherRoot, isGeography)));
        return SqlValue.FromDouble(root.IsCurved || otherRoot.IsCurved
            ? SpatialCurves.PlanarDistance(root, otherRoot)
            : SpatialMeasures.PlanarDistance(root, otherRoot));
    }

    /// <summary>
    /// <c>STBuffer(distance)</c> and <c>BufferWithTolerance(distance,
    /// tolerance, relative)</c>. Every argument refuses NULL with Msg 6569, and
    /// a tolerance that isn't positive is 24108. <c>STBuffer</c> is the
    /// tolerant form at 0.001 relative to the distance.
    /// </summary>
    private SqlValue EvaluateBuffer(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type, bool withTolerance)
    {
        var distance = this.RequiredArgument(runtime, 0, type).CoerceTo(SqlType.Float).AsDouble;
        var tolerance = 0.001;
        var relative = true;
        if (withTolerance)
        {
            tolerance = this.RequiredArgument(runtime, 1, type).CoerceTo(SqlType.Float).AsDouble;
            relative = this.RequiredArgument(runtime, 2, type).CoerceTo(SqlType.Bit).AsBoolean;
            if (!(tolerance > 0))
                throw SimulatedSqlException.SpatialBufferToleranceNotValid(type.IsGeography, tolerance);
        }
        return Buffered(value, type, distance, tolerance, relative);
    }

    /// <summary>
    /// The linearized buffer. A curved <c>geometry</c> instance is swept arc by
    /// arc, as real sweeps it; a curved <c>geography</c> one is read at the
    /// buffer's own tolerance rather than the operations' fine one.
    /// </summary>
    private static SqlValue Buffered(SpatialGeometry value, SpatialSqlType type, double distance, double tolerance, bool relative)
    {
        var root = value.Root;
        if (!type.IsGeography)
            return Constructed(value, SpatialBuffer.Buffer(root, distance, tolerance, relative));
        if (root.IsCurved)
            root = SpatialCurves.GeographyCurveToLine(root, relative ? tolerance * Math.Abs(distance) : tolerance, relative: false);
        return SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialGeodeticBuffer.Buffer(root, distance, tolerance, relative)), isGeography: true);
    }

    /// <summary>
    /// <c>BufferWithCurves(distance)</c>. A point's buffer is the curve
    /// polygon real answers; anything else answers the linearized buffer at
    /// <c>STBuffer</c>'s tolerance, where real's own answer is a curve polygon
    /// of the same outline. A NULL distance is Msg 6569.
    /// </summary>
    private SqlValue EvaluateBufferWithCurves(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type)
    {
        var distance = this.RequiredArgument(runtime, 0, type).CoerceTo(SqlType.Float).AsDouble;
        if (distance > 0 && value.Root.SinglePoint is { } point)
        {
            return SqlValue.FromSpatial(
                new SpatialGeometry(value.Srid, type.IsGeography ? SpatialGeodeticBuffer.CurveCircle(point, distance) : SpatialBuffer.CurveCircle(point, distance)),
                type.IsGeography);
        }
        return Buffered(value, type, distance, 0.001, relative: true);
    }

    /// <summary>
    /// <c>ShortestLineTo(other)</c>: NULL for a NULL, empty or
    /// differently-referenced operand, an empty line where the two meet.
    /// </summary>
    private SqlValue EvaluateShortestLine(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type)
    {
        if (Operand(runtime, 0, value, type.IsGeography) is not { } other)
            return SqlValue.Null(type);
        var line = type.IsGeography
            ? SpatialGeodeticShortestLine.ShortestLine(value.Root, other.Root)
            : SpatialConstructive.ShortestLine(value.Root, other.Root);
        return line is null ? SqlValue.Null(type) : SqlValue.FromSpatial(new SpatialGeometry(value.Srid, line), type.IsGeography);
    }

    /// <summary>
    /// <c>CurveToLineWithTolerance(tolerance, relative)</c>: a NULL argument is
    /// Msg 6569 and a tolerance that isn't positive 24152.
    /// </summary>
    private SqlValue EvaluateCurveToLine(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type)
    {
        var tolerance = this.RequiredArgument(runtime, 0, type).CoerceTo(SqlType.Float).AsDouble;
        var relative = this.RequiredArgument(runtime, 1, type).CoerceTo(SqlType.Bit).AsBoolean;
        return !(tolerance > 0)
            ? throw SimulatedSqlException.SpatialCurveToleranceNotValid(type.IsGeography, tolerance)
            : CurveToLine(value, type, tolerance, relative);
    }

    /// <summary>
    /// The instance with every arc linearized at <paramref name="tolerance"/>.
    /// Nothing left is <c>GEOMETRYCOLLECTION EMPTY</c>, and a linearization
    /// coarse enough to fold a ring onto itself comes back through
    /// <c>MakeValid</c>, as real's does.
    /// </summary>
    private static SqlValue CurveToLine(SpatialGeometry value, SpatialSqlType type, double tolerance, bool relative)
    {
        var shape = type.IsGeography
            ? SpatialCurves.GeographyCurveToLine(value.Root, tolerance, relative)
            : SpatialCurves.CurveToLine(value.Root, tolerance, relative);
        if (shape.IsEmpty)
            return SqlValue.FromSpatial(new SpatialGeometry(value.Srid, SpatialShape.Empty(SpatialShapeType.GeometryCollection)), type.IsGeography);
        var line = new SpatialGeometry(value.Srid, shape);
        if (!line.IsValidFor(type.IsGeography))
        {
            line = new SpatialGeometry(value.Srid, type.IsGeography
                ? SpatialGeodeticConstructive.MakeValid(shape)
                : SpatialSimplify.MakeValid(shape));
        }
        return SqlValue.FromSpatial(line, type.IsGeography);
    }

    /// <summary><c>STCurveN(n)</c>: the <paramref name="index"/>-th curve, or null past the count or on a kind without curves.</summary>
    private static SpatialShape? CurveAt(SpatialShape root, int index) =>
        SpatialCurves.Curves(root) is { } curves && index <= curves.Length ? curves[index - 1] : null;

    /// <summary><c>Reduce(tolerance)</c>: NULL is Msg 6569 and a negative tolerance 24125.</summary>
    private SqlValue EvaluateReduce(RuntimeContext runtime, SpatialGeometry value, SpatialSqlType type)
    {
        var tolerance = this.RequiredArgument(runtime, 0, type).CoerceTo(SqlType.Float).AsDouble;
        if (!(tolerance >= 0))
            throw SimulatedSqlException.SpatialReduceToleranceNotValid(type.IsGeography, tolerance);
        var reduced = value.Root.IsCurved
            ? SpatialCurves.Reduce(value.Root, tolerance, type.IsGeography)
            : SpatialSimplify.Reduce(value.Root, tolerance, type.IsGeography);
        return SqlValue.FromSpatial(new SpatialGeometry(value.Srid, reduced), type.IsGeography);
    }

    /// <summary>An argument real refuses as NULL with Msg 6569, numbered from 1.</summary>
    private SqlValue RequiredArgument(RuntimeContext runtime, int index, SpatialSqlType type)
    {
        var argument = index < this.arguments.Length ? this.arguments[index].Run(runtime) : SqlValue.Null(SqlType.Float);
        return argument.IsNull ? throw SimulatedSqlException.SpatialParameterNotNull(type.IsGeography, this.memberName, index + 1) : argument;
    }

    /// <summary>A constructed <c>geometry</c> result, carrying the receiver's SRID.</summary>
    private static SqlValue Constructed(SpatialGeometry value, SpatialShape shape) =>
        SqlValue.FromSpatial(new SpatialGeometry(value.Srid, shape), isGeography: false);

    private static SqlValue Text(RuntimeContext runtime, string value) =>
        SqlValue.FromNVarchar(NVarcharSqlType.Get(-1, runtime.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault), value);

    /// <summary>A single-ordinate property: defined only on a non-empty Point, NULL everywhere else.</summary>
    private static SqlValue Ordinate(SpatialShape root, Func<SpatialCoordinate, double?> select) =>
        root.SinglePoint is { } point && select(point) is { } ordinate
            ? SqlValue.FromDouble(ordinate)
            : SqlValue.Null(SqlType.Float);

    /// <summary>Wraps a computed coordinate as a Point shape, or null where the member has no answer.</summary>
    private static SpatialShape? PointOf(SpatialCoordinate? coordinate) =>
        coordinate is { } point ? SpatialShape.Leaf(SpatialShapeType.Point, [[point]]) : null;

    /// <summary>Wraps an extracted component, or NULL when the index selected nothing.</summary>
    private static SqlValue Component(SpatialGeometry value, SpatialSqlType type, SpatialShape? component) =>
        component is null ? SqlValue.Null(type) : SqlValue.FromSpatial(new SpatialGeometry(value.Srid, component), type.IsGeography);

    /// <summary>Which out-of-range failure an index argument reports.</summary>
    private enum IndexKind
    {
        Point,
        Geometry,
        Ring,
        Curve,
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
            IndexKind.Curve => SimulatedSqlException.SpatialCurveIndexTooSmall(isGeography, index),
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
    private static string[] OgcAncestry(SpatialShapeType type) => type switch
    {
        SpatialShapeType.Point => ["Point", "Geometry"],
        SpatialShapeType.LineString => ["LineString", "Curve", "Geometry"],
        SpatialShapeType.CircularString => ["CircularString", "Curve", "Geometry"],
        SpatialShapeType.CompoundCurve => ["CompoundCurve", "Curve", "Geometry"],
        SpatialShapeType.Polygon => ["Polygon", "Surface", "Geometry"],
        SpatialShapeType.CurvePolygon => ["CurvePolygon", "Surface", "Geometry"],
        SpatialShapeType.FullGlobe => ["FullGlobe", "Geometry"],
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
        if (root.Type is not (SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon) || index > root.Figures.Length || (interiorOnly && index < 2))
            return null;
        // A curve polygon's ring comes back as the curve it was written as.
        var ring = root.Figures[index - 1];
        return root.FigureType(index - 1) switch
        {
            SpatialFigureType.Arc => SpatialShape.Curve(SpatialShapeType.CircularString, [ring], [SpatialFigureType.Arc], [null]),
            SpatialFigureType.Composite => SpatialShape.Curve(SpatialShapeType.CompoundCurve, [ring], [SpatialFigureType.Composite], [root.Segments![index - 1]]),
            _ => SpatialShape.Leaf(SpatialShapeType.LineString, [ring]),
        };
    }

    /// <summary>
    /// <c>STStartPoint()</c> / <c>STEndPoint()</c>: the instance's first or last
    /// point in <c>STPointN</c> order, whatever its kind — a collection's
    /// included.
    /// </summary>
    private static SpatialShape? EndpointOf(SpatialShape root, bool first)
    {
        SpatialCoordinate? found = null;
        foreach (var point in root.Coordinates())
        {
            found = point;
            if (first)
                break;
        }
        return found is { } coordinate ? SpatialShape.Leaf(SpatialShapeType.Point, [[coordinate]]) : null;
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

    /// <summary>
    /// <c>ReorientObject()</c>: every polygon ring reversed, which flips the
    /// region it names. Lines and points come back as written, as on real.
    /// </summary>
    private static SpatialShape Reorient(SpatialShape shape)
    {
        if (shape.Type is SpatialShapeType.Polygon or SpatialShapeType.CurvePolygon)
        {
            var figures = new SpatialCoordinate[shape.Figures.Length][];
            var segments = shape.Segments is null ? null : new SpatialSegmentType[]?[shape.Figures.Length];
            for (var i = 0; i < shape.Figures.Length; i++)
            {
                if (shape.FigureType(i) == SpatialFigureType.Composite)
                {
                    var elements = SpatialCurves.Elements(shape, i);
                    elements.Reverse();
                    for (var e = 0; e < elements.Count; e++)
                        elements[e] = (elements[e].IsArc, [.. elements[e].Points.Reverse()]);
                    (figures[i], segments![i]) = SpatialCurves.Compose(elements);
                    continue;
                }
                figures[i] = [.. shape.Figures[i].Reverse()];
            }
            return new SpatialShape(shape.Type, figures, shape.Children, shape.FigureTypes, segments);
        }
        if (shape.Children.Length == 0)
            return shape;
        var children = new SpatialShape[shape.Children.Length];
        for (var i = 0; i < shape.Children.Length; i++)
            children[i] = Reorient(shape.Children[i]);
        return SpatialShape.Collection(shape.Type, children);
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

    internal override void Describe(NodeShape shape) => shape.Local(this.memberName).Local(this.writtenAsMethod).Child(this.target).Children(this.arguments);
}
