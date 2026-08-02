using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL Server 2022+ <c>GREATEST(expr1, expr2, …)</c> / <c>LEAST(…)</c>: the
/// horizontal (across-arguments) maximum / minimum. Every argument is promoted
/// to the single highest-data-type-precedence result type (per
/// <see cref="SqlType.Promote"/>), NULL arguments are skipped, and the result
/// is NULL only when every argument is NULL — probe-confirmed against SQL
/// Server 2025: <c>GREATEST(1, NULL, 3)</c> → 3, <c>GREATEST(NULL, NULL)</c> →
/// NULL, <c>GREATEST(1.5, 2)</c> → 2 as <c>numeric</c>, and
/// <c>GREATEST('a','b',3)</c> raises Msg 245 (the mixed set promotes to int, so
/// 'a' fails to convert) exactly as the runtime coercion below produces.
/// </summary>
/// <remarks>
/// The <c>isLeast</c> flag selects minimum vs maximum, riding the one class per
/// the built-in-variant convention. Reference:
/// https://learn.microsoft.com/en-us/sql/t-sql/functions/logical-functions-greatest-transact-sql
/// </remarks>
internal sealed class GreatestLeast : Expression
{
    private readonly Expression[] arguments;
    private readonly bool isLeast;
    private SqlType? cachedResultType;

    public GreatestLeast(ParserContext context, bool isLeast)
    {
        this.isLeast = isLeast;
        List<Expression> args = [Expression.Parse(context)];
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            args.Add(Expression.Parse(context));
        }
        // Real SQL Server requires at least one argument (Msg 174 otherwise);
        // a bare `GREATEST()` already fails the Expression.Parse above.
        this.arguments = [.. args];
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.arguments[0].GetSqlType(batch, resolveColumnType);
        for (var i = 1; i < this.arguments.Length; i++)
            t = SqlType.Promote(t, this.arguments[i].GetSqlType(batch, resolveColumnType));
        this.cachedResultType = t;
        return t;
    }

    internal override bool ResultReportsNumeric
    {
        get
        {
            foreach (var argument in this.arguments)
            {
                if (argument.ResultReportsNumeric)
                    return true;
            }
            return false;
        }
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Result type resolves from the runtime argument types (promoted) —
        // computed here rather than from the GetSqlType cache because Run is
        // reachable when GetSqlType wasn't called (a nested function whose own
        // GetSqlType doesn't cascade into operand types).
        var values = new SqlValue[this.arguments.Length];
        var resultType = this.cachedResultType;
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = this.arguments[i].Run(runtime);
            resultType = resultType is null ? values[i].Type : SqlType.Promote(resultType, values[i].Type);
        }

        SqlValue best = default;
        var haveBest = false;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i].IsNull)
                continue;
            var coerced = values[i].Type == resultType ? values[i] : values[i].CoerceTo(resultType!);
            if (!haveBest)
            {
                best = coerced;
                haveBest = true;
                continue;
            }
            var comparison = coerced.CompareTo(best);
            if (this.isLeast ? comparison < 0 : comparison > 0)
                best = coerced;
        }

        return haveBest ? best : SqlValue.Null(resultType!);
    }

    internal override bool ResultIsNullable(NullabilityContext context)
    {
        foreach (var argument in this.arguments)
        {
            if (argument.ResultIsNullable(context))
                return true;
        }
        return false;
    }

    internal override string DebugDisplay() =>
        $"{(this.isLeast ? "LEAST" : "GREATEST")}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
