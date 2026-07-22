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

    internal override string DebugDisplay() => $"COALESCE({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
