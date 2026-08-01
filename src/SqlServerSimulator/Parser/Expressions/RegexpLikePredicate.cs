using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// <c>REGEXP_LIKE(string, pattern [, flags])</c> — true when the pattern
/// matches anywhere in the string. A predicate, not a scalar: real reserves the
/// name as a keyword at compatibility level 170, so <c>SELECT
/// REGEXP_LIKE('abc', 'a.c')</c> raises Msg 156 and the construct is legal only
/// where a boolean is expected (WHERE / HAVING / IF / CASE WHEN / CHECK).
/// </summary>
/// <remarks>
/// <para>
/// The arity is enforced by the grammar rather than by Msg 189 — real raises
/// Msg 102 near the offending token for a fourth argument or a bare
/// <c>REGEXP_LIKE(x)</c>, unlike the four scalars, which report Msg 189.
/// </para>
/// <para>
/// A NULL in any of the three arguments yields UNKNOWN, so
/// <c>NOT REGEXP_LIKE(NULL, 'a')</c> is UNKNOWN too — probe-confirmed against
/// SQL Server 2025.
/// </para>
/// <para>
/// At compatibility level 160 and below the whole construct is absent: the
/// tokenizer leaves the name unreserved and the call falls to Msg 195,
/// <c>'REGEXP_LIKE' is not a recognized built-in function name.</c>
/// </para>
/// </remarks>
internal sealed class RegexpLikePredicate : BooleanExpression
{
    private readonly Expression input;
    private readonly Expression pattern;
    private readonly Expression? flags;

    private RegexpLikePredicate(Expression input, Expression pattern, Expression? flags)
    {
        this.input = input;
        this.pattern = pattern;
        this.flags = flags;
    }

    /// <summary>
    /// Parses <c>REGEXP_LIKE ( string , pattern [, flags] )</c> with the cursor
    /// on the <c>REGEXP_LIKE</c> keyword; on return the cursor sits on the
    /// first token past the closing <c>)</c>.
    /// </summary>
    public static new BooleanExpression Parse(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var input = Expression.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var pattern = Expression.Parse(context.MoveNextRequiredReturnSelf());

        Expression? flags = null;
        if (context.Token is Operator { Character: ',' })
            flags = Expression.Parse(context.MoveNextRequiredReturnSelf());

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new RegexpLikePredicate(input, pattern, flags);
    }

    public override bool? Run(RuntimeContext runtime)
    {
        var text = RegexpArguments.ReadStringArgument(this.input, runtime, "regexp_like", argumentIndex: 1);
        var patternValue = RegexpArguments.ReadStringArgument(this.pattern, runtime, "regexp_like", argumentIndex: 2);
        return text.IsNull || patternValue.IsNull
            || !RegexpArguments.TryReadFlags(this.flags, runtime, "regexp_like", argumentIndex: 3, out var flagSet)
            ? null
            : RegexDialect.Compile(patternValue.AsString, flagSet, RegexCallSite.Scalar).IsMatch(text.AsString);
    }

    internal override void VisitOperandExpressions(Action<Expression> visitor)
    {
        visitor(this.input);
        visitor(this.pattern);
        if (this.flags is not null)
            visitor(this.flags);
    }

    internal override string DebugDisplay() => this.flags is null
        ? $"REGEXP_LIKE({this.input.DebugDisplay()}, {this.pattern.DebugDisplay()})"
        : $"REGEXP_LIKE({this.input.DebugDisplay()}, {this.pattern.DebugDisplay()}, {this.flags.DebugDisplay()})";
}
