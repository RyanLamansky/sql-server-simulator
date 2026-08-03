using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COALESCE(expr1, expr2, ...)</c>: returns the first non-NULL
/// argument; NULL only if all arguments are NULL. Result type is the
/// highest-precedence type among the operands per
/// <see cref="SqlType.Promote"/>; NULL operands contribute their type for
/// promotion purposes. EF Core emits this around aggregate
/// expressions to substitute a default for empty-input NULLs.
/// </summary>
internal sealed class Coalesce : Expression
{
    private readonly Expression[] arguments;
    private SqlType? cachedResultType;

    public Coalesce(ParserContext context)
    {
        List<Expression> args = [Expression.Parse(context)];
        while (context.Token is Tokens.Operator { Character: ',' })
        {
            context.MoveNextRequired();
            args.Add(Expression.Parse(context));
        }
        if (args.Count < 2)
            throw new NotSupportedException("COALESCE requires at least two arguments.");
        this.arguments = [.. args];
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        SqlValue value = default;
        for (var i = 0; i < this.arguments.Length; i++)
        {
            value = this.arguments[i].Run(runtime);
            if (!value.IsNull)
                return this.cachedResultType is { } target && value.Type != target ? value.CoerceTo(target) : value;
        }
        return value; // all NULL — return the last (typed-NULL) result
    }

    // Untyped-NULL arguments yield to the typed arguments (so
    // `COALESCE(NULL, 'z')` is varchar, not the int that a bare NULL's
    // placeholder type would poison the promote with) and integer-literal
    // arguments size by digit count against a decimal sibling — both handled
    // by the shared PromoteValueArms seam.
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        this.cachedResultType = PromoteValueArms(this.arguments, batch, resolveColumnType);
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

    /// <summary>
    /// COALESCE takes its nullability from the CASE it desugars to —
    /// <c>CASE WHEN a IS NOT NULL THEN a … ELSE last END</c> — so it is NOT
    /// NULL when every argument is, where <c>ISNULL</c> needs only one of its
    /// two (the classic ISNULL-vs-COALESCE metadata quirk:
    /// <c>COALESCE(nullable_col, 0)</c> is nullable, <c>ISNULL(nullable_col, 0)</c>
    /// is not). Real folds each <c>IS NOT NULL</c> test whose argument is a
    /// written constant first, which drops a constant-NULL argument out of the
    /// walk and lets a constant non-NULL one answer for the whole call —
    /// <c>COALESCE(NULL, 5)</c> and <c>COALESCE(5, nullable_col)</c> both
    /// project NOT NULL (probe-confirmed against SQL Server 2025).
    /// <para>Each surviving argument additionally answers for the conversion
    /// the arm unification put on it, so <c>COALESCE(&lt;decimal(9, 2) col&gt;, 0)</c>
    /// is nullable on the int literal's account alone — see
    /// <see cref="Expression.ArmConversionIsNullable"/>.</para>
    /// </summary>
    internal override bool ResultIsNullable(NullabilityContext context)
    {
        var promoted = context.TypeOf(this);
        for (var i = 0; i < this.arguments.Length - 1; i++)
        {
            if (context.TryFold(this.arguments[i], out var folded))
            {
                if (!folded.IsNull)
                    return ArmConversionIsNullable(this.arguments[i], promoted, context);
                continue;
            }

            if (this.arguments[i].ResultIsNullable(context) || ArmConversionIsNullable(this.arguments[i], promoted, context))
                return true;
        }

        // The last argument is the desugared CASE's ELSE: it contributes its
        // own nullability rather than an IS NOT NULL test.
        return this.arguments[^1].ResultIsNullable(context) || ArmConversionIsNullable(this.arguments[^1], promoted, context);
    }

    internal override string DebugDisplay() => $"COALESCE({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";

    // Real desugars COALESCE to a CASE and folds an all-literal one, so
    // `ORDER BY COALESCE(NULL, 1)` is Msg 408 while `ORDER BY ISNULL(NULL, 1)`
    // (a runtime call) sorts — probe-confirmed.
    private protected override bool IsStructuralConstant
    {
        get
        {
            foreach (var argument in this.arguments)
            {
                if (!argument.IsWrittenConstant)
                    return false;
            }
            return true;
        }
    }
}
