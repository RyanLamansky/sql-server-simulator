using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Instance-method call on an <c>xml</c> value: <c>expr.value(…)</c>,
/// <c>expr.nodes(…)</c>, <c>expr.query(…)</c>, <c>expr.exist(…)</c>, or
/// <c>expr.modify(…)</c>. Parses cleanly (so CREATE VIEW / CREATE
/// PROCEDURE bodies that reference XML methods can be stored verbatim);
/// raises <see cref="NotSupportedException"/> at <see cref="Run"/> time
/// with a wording naming the method, matching the skip-with-diagnostic
/// stance documented in <c>docs/claude/xml.md</c>.
/// </summary>
/// <remarks>
/// The closed accept-list (<c>value</c>, <c>nodes</c>, <c>query</c>,
/// <c>exist</c>, <c>modify</c>) is checked before falling through to the
/// existing multipart-Reference path so a column literally named (e.g.)
/// <c>value</c> followed by <c>.MethodName(...)</c> won't collide.
/// </remarks>
internal sealed class XmlMethodCall : Expression
{
    private readonly Expression target;
    private readonly string methodName;

    private XmlMethodCall(Expression target, string methodName)
    {
        this.target = target;
        this.methodName = methodName;
    }

    /// <summary>
    /// Returns true if <paramref name="name"/> matches one of the five XML
    /// instance method names. Used by the expression parser to take the
    /// throws-at-execute path instead of multipart-reference dispatch.
    /// </summary>
    public static bool IsKnownMethodName(string name) =>
        name.Equals("value", StringComparison.Ordinal)
        || name.Equals("nodes", StringComparison.Ordinal)
        || name.Equals("query", StringComparison.Ordinal)
        || name.Equals("exist", StringComparison.Ordinal)
        || name.Equals("modify", StringComparison.Ordinal);

    /// <summary>
    /// Parses <c>expr.MethodName(args)</c>. Cursor enters on <c>(</c>; on
    /// return cursor sits on the closing <c>)</c>. Arguments parse fully
    /// (so name resolution surfaces eagerly per the simulator's idiom)
    /// but they're discarded — runtime evaluation throws.
    /// </summary>
    public static XmlMethodCall Parse(Expression target, string methodName, ParserContext context)
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
        return new XmlMethodCall(target, methodName);
    }

    public override SqlValue Run(RuntimeContext runtime) =>
        throw new NotSupportedException(
            $"XML instance method '.{this.methodName}()' is not modeled.");

    /// <summary>
    /// Static result type, used by projection schema inference. Returns
    /// <c>xml</c> for the methods that produce xml (<c>query</c>,
    /// <c>nodes</c>) and <c>bit</c> for <c>exist</c>; <c>value</c> returns
    /// the requested target type but the simulator stubs it as
    /// nvarchar(MAX) since we never actually evaluate it. <c>modify</c>
    /// is statement-level in real SQL Server and has no result type;
    /// surfaces as xml here for static-typing safety since it can't be
    /// reached at execute anyway.
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.methodName.Equals("exist", StringComparison.Ordinal)
            ? SqlType.Bit
            : this.methodName.Equals("value", StringComparison.Ordinal)
                ? NVarcharSqlType.Get(-1, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault)
                : SqlType.Xml;

    internal override string DebugDisplay() => $"({this.target.DebugDisplay()}).{this.methodName}(…)";
}
