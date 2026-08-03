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

    // An integer-literal argument sizes by digit count against a decimal
    // sibling, the same rule the CASE family's arm unification applies:
    // `GREATEST(<decimal(9, 2) col>, 1)` is decimal(9, 2) where
    // `GREATEST(<decimal(9, 2) col>, 2147483647)` widens to decimal(12, 2)
    // (probe-confirmed against SQL Server 2025).
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var branches = new (SqlType, int)[this.arguments.Length];
        for (var i = 0; i < this.arguments.Length; i++)
            branches[i] = (this.arguments[i].GetSqlType(batch, resolveColumnType), IntegerLiteralDigits(this.arguments[i]));
        this.cachedResultType = SqlType.PromoteBranches(branches);
        return this.cachedResultType;
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
        var values = new SqlValue[this.arguments.Length];
        for (var i = 0; i < values.Length; i++)
            values[i] = this.arguments[i].Run(runtime);

        // The parse-time result type stands when there is one: it carries the
        // integer-literal digit sizing, which promoting the runtime values
        // would widen back out (an argument arriving as int would push
        // decimal(9, 2) to decimal(12, 2) and mismatch the declared schema).
        // Promotion over the runtime types is the fallback for the paths that
        // reach Run without a GetSqlType — a nested function whose own
        // GetSqlType doesn't cascade into operand types.
        var resultType = this.cachedResultType;
        if (resultType is null)
        {
            foreach (var value in values)
                resultType = resultType is null ? value.Type : SqlType.Promote(resultType, value.Type);
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

    // NOT NULL when every argument is — and when none of them needs a
    // value-altering conversion to reach the promoted result type, the same
    // rule the CASE family carries (Expression.ArmConversionIsNullable).
    internal override bool ResultIsNullable(NullabilityContext context)
    {
        var promoted = context.TypeOf(this);
        foreach (var argument in this.arguments)
        {
            if (argument.ResultIsNullable(context) || ArmConversionIsNullable(argument, promoted, context))
                return true;
        }
        return false;
    }

    internal override string DebugDisplay() =>
        $"{(this.isLeast ? "LEAST" : "GREATEST")}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
