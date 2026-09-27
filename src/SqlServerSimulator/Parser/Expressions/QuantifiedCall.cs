using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// A call whose argument list opens with <c>DISTINCT</c> or <c>ALL</c>, which
/// real's grammar reads as an aggregate call whatever the name — so the name's
/// own grammar never sees the quantifier, and the call is refused by what the
/// name turns out to be rather than by a syntax error at the keyword (probed
/// 2026-09-27 against SQL Server 2025).
/// </summary>
internal static class QuantifiedCall
{
    /// <summary>
    /// Parses a quantified call once the opening <c>(</c> is consumed, with
    /// <see cref="ParserContext.Token"/> on the quantifier. Answers null when
    /// the name's own parser takes the quantifier: an aggregate that accepts
    /// <c>DISTINCT</c>, <c>APPROX_COUNT_DISTINCT</c>'s redundant <c>ALL</c>,
    /// and the functions with a grammar of their own (<c>COALESCE</c>,
    /// <c>CONVERT</c>, <c>LEFT</c> …), which refuse the keyword where it
    /// stands. Every other shape raises, except a schema-qualified call in
    /// skip mode, which binds only when it runs and answers a placeholder
    /// with the cursor on the call's last token.
    /// </summary>
    public static Expression? Parse(Reference reference, ParserContext context)
    {
        var name = reference.ReferencedName;
        var distinct = context.Token is ReservedKeyword { Keyword: Keyword.Distinct };

        // A schema-qualified name is a user-defined aggregate, which takes a
        // whole argument list, and an absent one is Msg 208 while binding —
        // even when the name is a scalar function, since only an aggregate
        // answers here. No CLR aggregate is modeled, so none is ever found.
        if (name.Count >= 2)
        {
            context.MoveNextRequired();
            _ = Expression.Parse(context);
            while (context.Token is Operator { Character: ',' })
            {
                context.MoveNextRequired();
                _ = Expression.Parse(context);
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var closing = context.SaveCheckpoint();
            switch (context.GetNextOptional())
            {
                case UnquotedString { ContextualKeyword: ContextualKeyword.Within }:
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                // Real names the quantifier, in lowercase, rather than the OVER.
                case ReservedKeyword { Keyword: Keyword.Over } when distinct:
                    throw SimulatedSqlException.SyntaxErrorNearText("distinct");
                case ReservedKeyword { Keyword: Keyword.Over }:
                    SkipWindowClause(context);
                    break;
                default:
                    context.RestoreCheckpoint(closing);
                    break;
            }
            return context.Batch.IsSkipping
                ? Value.UntypedNullPlaceholder()
                : Owe(SimulatedSqlException.InvalidObjectName(name, 214), context);
        }

        Span<char> upper = stackalloc char[name.Leaf.Length];
        _ = name.Leaf.ToUpperInvariant(upper);
        AggregateKind? kind;
        switch (upper)
        {
            case "APPROX_COUNT_DISTINCT":
                if (!distinct)
                    return null;
                kind = AggregateKind.ApproxCountDistinct;
                break;
            case "APPROX_PERCENTILE_CONT":
                kind = AggregateKind.ApproxPercentileCont;
                break;
            case "APPROX_PERCENTILE_DISC":
                kind = AggregateKind.ApproxPercentileDisc;
                break;
            case "AVG":
            case "CHECKSUM_AGG":
            case "COALESCE":
            case "CONVERT":
            case "COUNT":
            case "COUNT_BIG":
                return null;
            case "JSON_ARRAYAGG":
                kind = AggregateKind.JsonArrayAgg;
                break;
            case "JSON_OBJECTAGG":
                kind = AggregateKind.JsonObjectAgg;
                break;
            case "LEFT":
            case "MAX":
            case "MIN":
            case "NULLIF":
            case "PRODUCT":
            case "RIGHT":
            case "STDEV":
            case "STDEVP":
                return null;
            case "STRING_AGG":
                kind = AggregateKind.StringAgg;
                break;
            case "SUM":
            case "TRY_CONVERT":
            case "VAR":
            case "VARP":
                return null;
            default:
                kind = null;
                break;
        }

        // One operand, then the closing paren: a second argument, a
        // JSON_OBJECTAGG key's colon or an in-paren ORDER BY is a syntax error
        // at that token.
        var savedFlag = context.StopExpressionAtBareColon;
        context.StopExpressionAtBareColon = kind == AggregateKind.JsonObjectAgg;
        try
        {
            context.MoveNextRequired();
            _ = Expression.Parse(context);
        }
        finally
        {
            context.StopExpressionAtBareColon = savedFlag;
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var closingParen = context.SaveCheckpoint();
        switch (context.GetNextOptional())
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Within }:
                throw SimulatedSqlException.SyntaxErrorNear(context);
            case ReservedKeyword { Keyword: Keyword.Over } when kind is not null && distinct:
                throw SimulatedSqlException.DistinctNotAllowedInOver();
            // Otherwise the call is refused only once its window clause has
            // parsed, so a syntax error inside that clause wins.
            case ReservedKeyword { Keyword: Keyword.Over }:
                SkipWindowClause(context);
                break;
            default:
                context.RestoreCheckpoint(closingParen);
                break;
        }

        // Both refusals raised while parsing come first; the rest are binding
        // errors, which a syntax error later in the statement outranks.
        return kind switch
        {
            null => throw SimulatedSqlException.UnrecognizedAggregateFunction(name.Leaf),
            AggregateKind.ApproxCountDistinct => throw SimulatedSqlException.ApproxCountDistinctRefusesDistinct(),
            AggregateKind.ApproxPercentileCont => Owe(SimulatedSqlException.PercentileInputNotConstant("APPROX_PERCENTILE_CONT"), context),
            AggregateKind.ApproxPercentileDisc => Owe(SimulatedSqlException.PercentileInputNotConstant("APPROX_PERCENTILE_DISC"), context),
            _ => Owe(SimulatedSqlException.InsufficientArgumentsToFunction(AggregateExpression.LowerNameOf(kind.GetValueOrDefault())), context),
        };
    }

    /// <summary>
    /// Holds a binding refusal until the statement has parsed (see
    /// <see cref="ParserContext.PendingBindError"/>) and answers a placeholder
    /// for the call, or throws it where no query expression will flush it.
    /// </summary>
    private static Value Owe(SimulatedSqlException error, ParserContext context)
    {
        if (context.SecurableSink is null)
            throw error;
        context.PendingBindError ??= error;
        return Value.UntypedNullPlaceholder();
    }

    /// <summary>
    /// Consumes an <c>OVER</c> clause for its syntax alone, from the
    /// <c>OVER</c> keyword to its closing paren or window name, raising Msg 102
    /// at the batch's last token when the parens never balance.
    /// </summary>
    private static void SkipWindowClause(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            return;
        var depth = 1;
        while (depth > 0)
        {
            switch (context.GetNextRequired())
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                case Operator { Character: ')' }:
                    depth--;
                    break;
            }
        }
    }
}
