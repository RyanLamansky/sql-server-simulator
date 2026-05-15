using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Instance-method call on a <c>geography</c> or <c>geometry</c> value:
/// <c>expr.STDistance(other)</c>, <c>expr.STAsText()</c>, <c>expr.ToString()</c>,
/// etc. Parses cleanly so CREATE VIEW / CREATE PROCEDURE bodies that reference
/// spatial methods store verbatim; raises <see cref="NotSupportedException"/>
/// at <see cref="Run"/> time naming the method, matching the
/// skip-with-diagnostic stance documented in
/// <c>docs/claude/bacpac-prerequisites.md</c>. Special case:
/// <c>.ToString()</c> returns the stored WKT (the simulator's storage form),
/// which is the only method whose result is recoverable from the degraded
/// in-memory representation.
/// </summary>
/// <remarks>
/// The accept-list is broad: every OGC predicate / accessor exposed by both
/// geography and geometry, plus the common Microsoft extensions
/// (<c>Lat</c> / <c>Long</c> / <c>MakeValid</c> / <c>Reduce</c> / ...). The
/// list is matched before the existing multipart-Reference fallthrough so a
/// dotted-method shape on a spatial-typed LHS dispatches here rather than
/// failing at the trailing <c>(</c>. Names outside the list fall through and
/// surface as Msg 207 (invalid column) just like real SQL Server's
/// "method not found" path.
/// </remarks>
internal sealed class SpatialMethodCall : Expression
{
    private static readonly HashSet<string> KnownMethodNames = new(Collation.Default)
    {
        "ToString",
        "STAsText",
        "STAsBinary",
        "STAsGML",
        "AsGml",
        "AsTextZM",
        "AsBinaryZM",
        "STArea",
        "STBoundary",
        "STBuffer",
        "STCentroid",
        "STContains",
        "STConvexHull",
        "STCrosses",
        "STDifference",
        "STDimension",
        "STDisjoint",
        "STDistance",
        "STEndpoint",
        "STEnvelope",
        "STEquals",
        "STExteriorRing",
        "STGeometryN",
        "STGeometryType",
        "STInteriorRingN",
        "STIntersection",
        "STIntersects",
        "STIsClosed",
        "STIsEmpty",
        "STIsRing",
        "STIsSimple",
        "STIsValid",
        "STLength",
        "STNumGeometries",
        "STNumInteriorRing",
        "STNumPoints",
        "STOverlaps",
        "STPointN",
        "STPointOnSurface",
        "STRelate",
        "STSrid",
        "STStartPoint",
        "STSymDifference",
        "STTouches",
        "STUnion",
        "STWithin",
        "STX",
        "STY",
        "STZ",
        "STM",
        "Lat",
        "Long",
        "MakeValid",
        "Reduce",
        "Filter",
        "HasZ",
        "HasM",
        "BufferWithTolerance",
        "BufferWithCurves",
        "MinDbCompatibilityLevel",
        "RingN",
        "NumRings",
        "ReorientObject",
        "CurveToLineWithTolerance",
        "ShortestLineTo",
        "EnvelopeAngle",
        "EnvelopeCenter",
        "InstanceOf",
    };

    private readonly Expression target;
    private readonly string methodName;

    private SpatialMethodCall(Expression target, string methodName)
    {
        this.target = target;
        this.methodName = methodName;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> matches a known OGC / Microsoft
    /// spatial instance-method name. Checked before falling through to
    /// multipart-Reference dispatch in the expression parser's dotted-name
    /// loop.
    /// </summary>
    public static bool IsKnownMethodName(string name) => KnownMethodNames.Contains(name);

    /// <summary>
    /// Parses <c>expr.MethodName(args)</c>. Cursor enters on <c>(</c>; on
    /// return cursor sits on the closing <c>)</c>. Arguments parse fully so
    /// name resolution surfaces eagerly; arg values themselves are discarded
    /// because runtime evaluation always throws (except <c>.ToString()</c>,
    /// which takes no args).
    /// </summary>
    public static SpatialMethodCall Parse(Expression target, string methodName, ParserContext context)
    {
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: ')' })
        {
            _ = Expression.Parse(context);
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                _ = Expression.Parse(context);
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
        return new SpatialMethodCall(target, methodName);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (Collation.Default.Equals(this.methodName, "ToString"))
        {
            var value = this.target.Run(runtime);
            return value.IsNull
                ? SqlValue.Null(NVarcharSqlType.MaxForm)
                : SqlValue.FromNVarchar(value.AsString);
        }
        throw new NotSupportedException(
            $"Spatial instance method '.{this.methodName}()' is not modeled.");
    }

    /// <summary>
    /// Static result type used by projection-schema inference. Boolean-yielding
    /// OGC predicates return <c>bit</c>; numeric accessors return <c>float</c>
    /// (real SQL Server uses <c>float</c> for most spatial measurements);
    /// geometry-yielding constructors return the same spatial type as the
    /// receiver; everything else falls back to <c>nvarchar(MAX)</c> so the
    /// projection planner can still compute schema even though Run never
    /// succeeds.
    /// </summary>
    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType)
    {
        return IsStringResult(this.methodName) ? NVarcharSqlType.MaxForm
            : IsBinaryResult(this.methodName) ? VarbinarySqlType.MaxForm
            : IsBooleanResult(this.methodName) ? SqlType.Bit
            : IsNumericResult(this.methodName) ? SqlType.Float
            : IsIntegerResult(this.methodName) ? SqlType.Int32
            : this.target.GetSqlType(resolveColumnType) is SpatialSqlType spatial ? spatial : NVarcharSqlType.MaxForm;
    }

    private static bool IsStringResult(string name) =>
        Collation.Default.Equals(name, "ToString")
        || Collation.Default.Equals(name, "STAsText")
        || Collation.Default.Equals(name, "STGeometryType")
        || Collation.Default.Equals(name, "AsGml")
        || Collation.Default.Equals(name, "AsTextZM");

    private static bool IsBinaryResult(string name) =>
        Collation.Default.Equals(name, "STAsBinary")
        || Collation.Default.Equals(name, "AsBinaryZM");

    private static bool IsBooleanResult(string name) =>
        Collation.Default.Equals(name, "STContains")
        || Collation.Default.Equals(name, "STCrosses")
        || Collation.Default.Equals(name, "STDisjoint")
        || Collation.Default.Equals(name, "STEquals")
        || Collation.Default.Equals(name, "STIntersects")
        || Collation.Default.Equals(name, "STIsClosed")
        || Collation.Default.Equals(name, "STIsEmpty")
        || Collation.Default.Equals(name, "STIsRing")
        || Collation.Default.Equals(name, "STIsSimple")
        || Collation.Default.Equals(name, "STIsValid")
        || Collation.Default.Equals(name, "STOverlaps")
        || Collation.Default.Equals(name, "STTouches")
        || Collation.Default.Equals(name, "STWithin")
        || Collation.Default.Equals(name, "STRelate")
        || Collation.Default.Equals(name, "HasZ")
        || Collation.Default.Equals(name, "HasM")
        || Collation.Default.Equals(name, "InstanceOf");

    private static bool IsNumericResult(string name) =>
        Collation.Default.Equals(name, "STArea")
        || Collation.Default.Equals(name, "STDistance")
        || Collation.Default.Equals(name, "STLength")
        || Collation.Default.Equals(name, "STX")
        || Collation.Default.Equals(name, "STY")
        || Collation.Default.Equals(name, "STZ")
        || Collation.Default.Equals(name, "STM")
        || Collation.Default.Equals(name, "Lat")
        || Collation.Default.Equals(name, "Long")
        || Collation.Default.Equals(name, "EnvelopeAngle");

    private static bool IsIntegerResult(string name) =>
        Collation.Default.Equals(name, "STDimension")
        || Collation.Default.Equals(name, "STSrid")
        || Collation.Default.Equals(name, "STNumGeometries")
        || Collation.Default.Equals(name, "STNumInteriorRing")
        || Collation.Default.Equals(name, "STNumPoints")
        || Collation.Default.Equals(name, "NumRings")
        || Collation.Default.Equals(name, "MinDbCompatibilityLevel");

    internal override string DebugDisplay() => $"({this.target.DebugDisplay()}).{this.methodName}(…)";
}
