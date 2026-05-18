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
/// <c>docs/claude/spatial.md</c>. Special case:
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
    private static readonly HashSet<string> KnownMethodNames = new(StringComparer.Ordinal)
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
        if (this.methodName.Equals("ToString", StringComparison.Ordinal))
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
        name.Equals("ToString", StringComparison.Ordinal)
        || name.Equals("STAsText", StringComparison.Ordinal)
        || name.Equals("STGeometryType", StringComparison.Ordinal)
        || name.Equals("AsGml", StringComparison.Ordinal)
        || name.Equals("AsTextZM", StringComparison.Ordinal);

    private static bool IsBinaryResult(string name) =>
        name.Equals("STAsBinary", StringComparison.Ordinal)
        || name.Equals("AsBinaryZM", StringComparison.Ordinal);

    private static bool IsBooleanResult(string name) =>
        name.Equals("STContains", StringComparison.Ordinal)
        || name.Equals("STCrosses", StringComparison.Ordinal)
        || name.Equals("STDisjoint", StringComparison.Ordinal)
        || name.Equals("STEquals", StringComparison.Ordinal)
        || name.Equals("STIntersects", StringComparison.Ordinal)
        || name.Equals("STIsClosed", StringComparison.Ordinal)
        || name.Equals("STIsEmpty", StringComparison.Ordinal)
        || name.Equals("STIsRing", StringComparison.Ordinal)
        || name.Equals("STIsSimple", StringComparison.Ordinal)
        || name.Equals("STIsValid", StringComparison.Ordinal)
        || name.Equals("STOverlaps", StringComparison.Ordinal)
        || name.Equals("STTouches", StringComparison.Ordinal)
        || name.Equals("STWithin", StringComparison.Ordinal)
        || name.Equals("STRelate", StringComparison.Ordinal)
        || name.Equals("HasZ", StringComparison.Ordinal)
        || name.Equals("HasM", StringComparison.Ordinal)
        || name.Equals("InstanceOf", StringComparison.Ordinal);

    private static bool IsNumericResult(string name) =>
        name.Equals("STArea", StringComparison.Ordinal)
        || name.Equals("STDistance", StringComparison.Ordinal)
        || name.Equals("STLength", StringComparison.Ordinal)
        || name.Equals("STX", StringComparison.Ordinal)
        || name.Equals("STY", StringComparison.Ordinal)
        || name.Equals("STZ", StringComparison.Ordinal)
        || name.Equals("STM", StringComparison.Ordinal)
        || name.Equals("Lat", StringComparison.Ordinal)
        || name.Equals("Long", StringComparison.Ordinal)
        || name.Equals("EnvelopeAngle", StringComparison.Ordinal);

    private static bool IsIntegerResult(string name) =>
        name.Equals("STDimension", StringComparison.Ordinal)
        || name.Equals("STSrid", StringComparison.Ordinal)
        || name.Equals("STNumGeometries", StringComparison.Ordinal)
        || name.Equals("STNumInteriorRing", StringComparison.Ordinal)
        || name.Equals("STNumPoints", StringComparison.Ordinal)
        || name.Equals("NumRings", StringComparison.Ordinal)
        || name.Equals("MinDbCompatibilityLevel", StringComparison.Ordinal);

    internal override string DebugDisplay() => $"({this.target.DebugDisplay()}).{this.methodName}(…)";
}
