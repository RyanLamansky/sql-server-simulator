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

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var t = this.arguments[0].GetSqlType(batch, resolveColumnType);
        for (var i = 1; i < this.arguments.Length; i++)
            t = SqlType.Promote(t, this.arguments[i].GetSqlType(batch, resolveColumnType));
        this.cachedResultType = t;
        return t;
    }

    internal override string DebugDisplay() => $"COALESCE({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
