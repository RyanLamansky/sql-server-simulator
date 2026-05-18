using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Static-method call on the <c>geography</c> or <c>geometry</c> type:
/// <c>geography::Parse(wkt)</c>, <c>geography::STGeomFromText(wkt, srid)</c>,
/// <c>geometry::Point(x, y, srid)</c>, etc. Recognized inline in
/// <see cref="Expression.Parse"/>'s binary-operator loop when a bare
/// <see cref="Reference"/> named <c>geography</c> or <c>geometry</c> is
/// followed by the <c>::</c> token pair. Constructors that accept a WKT
/// argument stash that string as the spatial value's payload; constructors
/// that compute the WKT from numeric inputs (e.g. <c>Point(x, y, srid)</c>)
/// synthesize a WKT string from the arguments. All other static methods
/// raise <see cref="NotSupportedException"/> at <see cref="Run"/>.
/// </summary>
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
        return new SpatialStaticCall(type, methodName, [.. args]);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        if (this.method.Equals("Parse", StringComparison.Ordinal)
            || this.method.Equals("STGeomFromText", StringComparison.Ordinal))
        {
            if (this.arguments.Length == 0)
                throw new NotSupportedException($"{this.type}::{this.method} requires at least one argument.");
            var wktValue = this.arguments[0].Run(runtime);
            if (wktValue.IsNull)
                return SqlValue.Null(this.type);
            var wkt = wktValue.Type.Category == SqlTypeCategory.String
                ? wktValue.AsString
                : throw new NotSupportedException($"{this.type}::{this.method} expects a string argument; got {wktValue.Type}.");
            return this.type == SqlType.Geography ? SqlValue.FromGeography(wkt) : SqlValue.FromGeometry(wkt);
        }
        if (this.method.Equals("Point", StringComparison.Ordinal) && this.arguments.Length == 3)
        {
            var a = this.arguments[0].Run(runtime);
            var b = this.arguments[1].Run(runtime);
            if (a.IsNull || b.IsNull)
                return SqlValue.Null(this.type);
            var x = a.Type == SqlType.Float ? a.AsDouble : (double)a.AsDecimal;
            var y = b.Type == SqlType.Float ? b.AsDouble : (double)b.AsDecimal;
            var wkt = $"POINT ({x} {y})";
            return this.type == SqlType.Geography ? SqlValue.FromGeography(wkt) : SqlValue.FromGeometry(wkt);
        }
        throw new NotSupportedException($"{this.type}::{this.method} is not modeled.");
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => this.type;

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
