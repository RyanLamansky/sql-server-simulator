using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHOOSE(index, val1, val2, ...)</c>: picks the 1-based <c>index</c>-th
/// value from the variadic argument list. NULL index or out-of-range index
/// (less than 1 or greater than the value count) returns NULL of the result
/// type. Result type is the promotion of all value arms (matching the
/// CASE / IIF branch-type-unification rule). Sibling of <see cref="Iif"/>;
/// EF Core 10 emits it for <c>ArrayIndex</c>-style projections over inline
/// arrays.
/// </summary>
internal sealed class Choose : Expression
{
    private readonly Expression indexExpr;
    private readonly Expression[] values;
    private SqlType? cachedResultType;

    public Choose(ParserContext context)
    {
        this.indexExpr = Parse(context);
        var list = new List<Expression>();
        while (context.Token is Tokens.Operator { Character: ',' })
            list.Add(Parse(context.MoveNextRequiredReturnSelf()));
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (list.Count == 0)
            throw SimulatedSqlException.FunctionRequiresNArguments("choose", 2);
        this.values = [.. list];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var resultType = this.cachedResultType ?? this.GetSqlType(runtime.Batch, _ => SqlType.Int32);
        var idxValue = this.indexExpr.Run(runtime);
        if (idxValue.IsNull)
            return SqlValue.Null(resultType);
        var idx = idxValue.CoerceTo(SqlType.Int32).AsInt32;
        if (idx < 1 || idx > this.values.Length)
            return SqlValue.Null(resultType);
        var picked = this.values[idx - 1].Run(runtime);
        return picked.IsNull
            ? SqlValue.Null(resultType)
            : picked.Type != resultType ? picked.CoerceTo(resultType) : picked;
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.values[0].GetSqlType(batch, resolveColumnType);
        for (var i = 1; i < this.values.Length; i++)
            t = SqlType.Promote(t, this.values[i].GetSqlType(batch, resolveColumnType));
        this.cachedResultType = t;
        return t;
    }

    internal override bool ResultReportsNumeric
    {
        get
        {
            foreach (var value in this.values)
            {
                if (value.ResultReportsNumeric)
                    return true;
            }
            return false;
        }
    }

    internal override string DebugDisplay() => $"CHOOSE({this.indexExpr.DebugDisplay()}, ...{this.values.Length} values)";
}
