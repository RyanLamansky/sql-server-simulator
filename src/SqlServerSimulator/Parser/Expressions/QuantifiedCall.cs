using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

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
        // answers here.
        if (name.Count >= 2)
        {
            // A CLR aggregate reads its own quantifier.
            if (context.Batch.TryResolveFunction(name, out var function) && function is Schemas.ClrAggregateFunction)
                return null;
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
                // Real names the quantifier, in lowercase, rather than the
                // OVER, once the clause has parsed.
                case ReservedKeyword { Keyword: Keyword.Over }:
                    WindowExpression.ParseClauseSyntax(context);
                    if (distinct)
                        throw SimulatedSqlException.SyntaxErrorNearText("distinct");
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
        Expression operand;
        using (ParserScope.Enter(ref context.StopExpressionAtBareColon, kind == AggregateKind.JsonObjectAgg))
        {
            context.MoveNextRequired();
            operand = Expression.Parse(context);
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var callEnd = context.Token.StartIndex;
        var closingParen = context.SaveCheckpoint();
        switch (context.GetNextOptional())
        {
            case UnquotedString { ContextualKeyword: ContextualKeyword.Within }:
                throw SimulatedSqlException.SyntaxErrorNear(context);
            // The call is refused only once its window clause has parsed, so a
            // syntax error inside that clause wins.
            case ReservedKeyword { Keyword: Keyword.Over }:
                WindowExpression.ParseClauseSyntax(context);
                if (kind is not null && distinct)
                    throw SimulatedSqlException.DistinctNotAllowedInOver();
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
            _ => OweInReport(SimulatedSqlException.InsufficientArgumentsToFunction(AggregateExpression.LowerNameOf(kind.GetValueOrDefault())), context, callEnd, operand),
        };
    }

    /// <summary>
    /// <see cref="Owe"/> for the Msg 313 of a quantified <c>STRING_AGG</c> or
    /// JSON aggregate, which a statement's binder report carries where the
    /// call's closing paren sits, and only while nothing ahead of that has
    /// failed, the call's own operand included, which binds as the call does
    /// (probed 2026-10-01 against SQL Server 2025: <c>STRING_AGG(DISTINCT s),
    /// x1</c> reports Msg 313 then Msg 207, <c>x1, STRING_AGG(DISTINCT s)</c>,
    /// a <c>HAVING</c> over <c>x1</c> and <c>STRING_AGG(DISTINCT x1)</c> the
    /// Msg 207 alone, an <c>ORDER BY</c> over <c>x1</c> both).
    /// </summary>
    private static RefusedAggregate OweInReport(SimulatedSqlException error, ParserContext context, int callEnd, Expression operand)
    {
        if (context.Batch.BindErrors is { } report && report.Covers(context.Token ?? context.LastToken))
            report.RecordUnlessPreceded(error, callEnd);
        else
            _ = Owe(error, context);
        return new RefusedAggregate(operand);
    }

    /// <summary>
    /// The stand-in for a refused quantified aggregate call, which binds its
    /// operand — so a name error there is reported — and is never evaluated:
    /// the statement fails with the call's own error. The operand is local
    /// state rather than a child, since no rule a walk applies (grouping,
    /// aggregate placement) reads a call real refuses.
    /// </summary>
    private sealed class RefusedAggregate(Expression operand) : Expression
    {
        public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(SqlType.Int32);

        public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            _ = operand.GetSqlType(batch, resolveColumnType);
            return SqlType.Int32;
        }

        internal override string DebugDisplay() => $"<refused>({operand.DebugDisplay()})";

        internal override void Describe(NodeShape shape) => shape.Local(operand);
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
}
