using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>REPLACE(input, oldValue, newValue)</c>: replaces every occurrence
/// of <c>oldValue</c> in <c>input</c> with <c>newValue</c>. Matching uses the
/// default collation (case-insensitive); the replaced segment is removed and
/// the new value substituted, even when its case differs from the match.
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/replace-transact-sql</remarks>
internal sealed class Replace : Expression
{
    private readonly Expression input;
    private readonly Expression oldValue;
    private readonly Expression newValue;

    public Replace(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.oldValue = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.newValue = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var i = input.Run(runtime);
        var o = oldValue.Run(runtime);
        var n = newValue.Run(runtime);
        if (i.IsNull || o.IsNull || n.IsNull)
            return SqlValue.Null(i.Type);
        if (!SqlType.IsStringCategory(i.Type))
            throw new NotSupportedException($"REPLACE expects a string first argument; got {i.Type}.");
        var replaced = i.AsString.Replace(o.AsString, n.AsString, StringComparison.InvariantCultureIgnoreCase);
        return SqlValue.FromString(i.Type, replaced);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => input.GetSqlType(resolveColumnType);

    internal override string DebugDisplay() => $"REPLACE({input.DebugDisplay()}, {oldValue.DebugDisplay()}, {newValue.DebugDisplay()})";
}
