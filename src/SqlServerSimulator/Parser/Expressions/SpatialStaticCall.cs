using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Storage.Spatial;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Static-method call on the <c>geography</c> or <c>geometry</c> type:
/// <c>geography::Parse(wkt)</c>, <c>geography::STGeomFromText(wkt, srid)</c>,
/// <c>geometry::Point(x, y, srid)</c>, <c>geometry::STGeomFromWKB(bytes, srid)</c>
/// and the per-kind <c>ST<i>Kind</i>FromText</c> / <c>ST<i>Kind</i>FromWKB</c>
/// constructors. Recognized inline in <see cref="Expression.Parse"/>'s
/// binary-operator loop when a bare <see cref="Reference"/> named
/// <c>geography</c> or <c>geometry</c> is followed by the <c>::</c> token pair.
/// </summary>
/// <remarks>
/// The per-kind constructors bind the shape their name implies and report
/// Msg 24142 for any other label, matching real. <c>Point</c> takes its
/// coordinates in the type's own order — <c>(x, y)</c> for geometry,
/// <c>(latitude, longitude)</c> for geography — and both spell the result in
/// WKT's (longitude, latitude) order.
/// </remarks>
internal sealed class SpatialStaticCall : Expression
{
    private readonly SpatialSqlType type;
    private readonly string method;
    private readonly Expression[] arguments;

    private SpatialStaticCall(SpatialSqlType type, string method, Expression[] arguments)
    {
        this.type = type;
        this.method = method;
        this.arguments = arguments;
    }

    /// <summary>WKT label each per-kind text constructor requires, and the shape each binary one requires.</summary>
    private static (string Label, SpatialShapeType Type)? RequiredKind(string method) => method switch
    {
        "STGeomCollFromText" or "STGeomCollFromWKB" => ("GEOMETRYCOLLECTION", SpatialShapeType.GeometryCollection),
        "STLineFromText" or "STLineFromWKB" => ("LINESTRING", SpatialShapeType.LineString),
        "STMLineFromText" or "STMLineFromWKB" => ("MULTILINESTRING", SpatialShapeType.MultiLineString),
        "STMPointFromText" or "STMPointFromWKB" => ("MULTIPOINT", SpatialShapeType.MultiPoint),
        "STMPolyFromText" or "STMPolyFromWKB" => ("MULTIPOLYGON", SpatialShapeType.MultiPolygon),
        "STPointFromText" or "STPointFromWKB" => ("POINT", SpatialShapeType.Point),
        "STPolyFromText" or "STPolyFromWKB" => ("POLYGON", SpatialShapeType.Polygon),
        _ => null,
    };

    /// <summary>
    /// Parses the body following <c>geography::</c> or <c>geometry::</c>.
    /// Cursor enters on the method-name token; on return cursor sits on the
    /// closing <c>)</c>. Arguments parse fully so any name resolution failures
    /// surface eagerly; non-recognized static methods construct a placeholder
    /// instance whose <see cref="Run"/> throws.
    /// </summary>
    public static SpatialStaticCall Parse(SpatialSqlType type, ParserContext context)
    {
        var methodName = context.Token is Name name
            ? name.Value
            : throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

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
        return RequiredArgumentCount(methodName) is { } required && args.Count != required
            ? throw SimulatedSqlException.FunctionRequiresNArguments(methodName, required)
            : new SpatialStaticCall(type, methodName, [.. args]);
    }

    /// <summary>
    /// Argument count each constructor demands, checked at parse time the way
    /// real does (Msg 174, severity 15). Real reports the name with the
    /// caller's own casing here, unlike the built-in function path.
    /// </summary>
    private static int? RequiredArgumentCount(string method) =>
        method.Equals("Parse", StringComparison.OrdinalIgnoreCase) ? 1
        : method.Equals("Point", StringComparison.OrdinalIgnoreCase) ? 3
        : method.EndsWith("FromText", StringComparison.OrdinalIgnoreCase)
            || method.EndsWith("FromWKB", StringComparison.OrdinalIgnoreCase) ? 2
        : null;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var isGeography = this.type.IsGeography;
        var fromText = this.method.EndsWith("FromText", StringComparison.Ordinal);
        var fromWkb = this.method.EndsWith("FromWKB", StringComparison.Ordinal);

        if (this.method.Equals("Parse", StringComparison.Ordinal) || fromText)
        {
            var text = Argument(runtime, 0);
            if (text is null)
                return SqlValue.Null(this.type);
            if (text.Value.Type.Category != SqlTypeCategory.String)
                throw new NotSupportedException($"{this.type}::{this.method} expects a string argument; got {text.Value.Type}.");
            var srid = Srid(runtime, 1, isGeography);
            return srid is null
                ? SqlValue.Null(this.type)
                : SqlValue.FromSpatial(
                    SpatialWktReader.Read(text.Value.AsString, srid.Value, isGeography, RequiredKind(this.method)?.Label),
                    isGeography);
        }

        if (fromWkb)
        {
            var binary = Argument(runtime, 0);
            if (binary is null)
                return SqlValue.Null(this.type);
            var srid = Srid(runtime, 1, isGeography);
            return srid is null
                ? SqlValue.Null(this.type)
                : SqlValue.FromSpatial(
                    SpatialWkb.Read(binary.Value.AsBytes, srid.Value, isGeography, RequiredKind(this.method)?.Type),
                    isGeography);
        }

        if (this.method.Equals("Point", StringComparison.Ordinal) && this.arguments.Length == 3)
        {
            var first = Argument(runtime, 0);
            var second = Argument(runtime, 1);
            var srid = Srid(runtime, 2, isGeography);
            if (first is null || second is null || srid is null)
                return SqlValue.Null(this.type);
            var a = first.Value.CoerceTo(SqlType.Float).AsDouble;
            var b = second.Value.CoerceTo(SqlType.Float).AsDouble;
            // geography::Point takes (latitude, longitude); geometry::Point takes (x, y).
            var (x, y) = isGeography ? (b, a) : (a, b);
            return isGeography && (y < -90 || y > 90)
                ? throw SimulatedSqlException.SpatialLatitudeOutOfRange()
                : SqlValue.FromSpatial(
                    new SpatialGeometry(srid.Value, SpatialShape.Leaf(SpatialShapeType.Point, [[new SpatialCoordinate(x, y)]])),
                    isGeography);
        }

        throw new NotSupportedException($"{this.type}::{this.method} is not modeled.");
    }

    /// <summary>Evaluates argument <paramref name="index"/>, or returns null when it is absent or NULL.</summary>
    private SqlValue? Argument(RuntimeContext runtime, int index)
    {
        if (index >= this.arguments.Length)
            return null;
        var value = this.arguments[index].Run(runtime);
        return value.IsNull ? null : value;
    }

    /// <summary>The SRID argument, falling back to the type's default when the constructor takes none.</summary>
    private int? Srid(RuntimeContext runtime, int index, bool isGeography) =>
        index >= this.arguments.Length
            ? SpatialGeometry.DefaultSridFor(isGeography)
            : Argument(runtime, index) is { } value
                ? SpatialGeometry.ValidateSrid(ScalarArguments.CoerceToInt(value), isGeography)
                : null;

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => this.type;

    internal override string DebugDisplay()
    {
        var argDisplay = this.arguments.Length == 0 ? "" : string.Join(", ", this.arguments.Select(a => a.DebugDisplay()));
        return $"{this.type}::{this.method}({argDisplay})";
    }

    internal override bool ResultIsNullable(Func<MultiPartName, bool> resolveColumnNullable) =>
        this.arguments.Any(a => a.ResultIsNullable(resolveColumnNullable));

    internal override void VisitColumnReferences(Action<MultiPartName> visit)
    {
        foreach (var arg in this.arguments)
            arg.VisitColumnReferences(visit);
    }
}
