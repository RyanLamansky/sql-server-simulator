using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The four ordering comparison operators, exposed by
/// <see cref="BooleanExpression.TryGetRangeOperands"/> so the index-seek planner
/// can recognize a range predicate (<c>col &gt; v</c>, <c>col BETWEEN lo AND hi</c>)
/// on a leading key column without reaching into the private comparison hierarchy.
/// </summary>
internal enum RangeComparison
{
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
}

/// <summary>
/// A specific type of expression used in WHERE clauses and similar branching scenarios.
/// </summary>
[DebuggerDisplay("{DebugDisplay(),nq}")]
internal abstract class BooleanExpression
{
    private protected BooleanExpression()
    {
    }

    /// <summary>
    /// Renders this predicate into the normalized form SQL Server stores in
    /// <c>sys.indexes.filter_definition</c> for a filtered index — columns
    /// bracketed, numeric constants parenthesized, strings quoted, operators
    /// space-free (<c>[status]=(1)</c>), and <c>AND</c> / <c>IS [NOT] NULL</c> /
    /// <c>IN</c> uppercase-spaced — or returns <c>null</c> when the predicate
    /// falls outside the renderable filtered-predicate grammar (an <c>AND</c> of
    /// comparison / <c>IS [NOT] NULL</c> / <c>IN</c> over <c>column &lt;op&gt;
    /// constant</c>). Those excluded shapes — <c>OR</c>, <c>NOT</c>, <c>BETWEEN</c>,
    /// function calls — are exactly the ones real SQL Server rejects in a filtered
    /// index, so reporting <c>null</c> for them never hides a definition a real
    /// server would have stored. The whole predicate is wrapped in one outer pair
    /// of parens.
    /// </summary>
    internal string? RenderFilterDefinition(BatchContext batch)
    {
        var sb = new StringBuilder("(");
        if (!this.TryAppendFilterDefinition(sb, batch))
            return null;
        _ = sb.Append(')');
        return sb.ToString();
    }

    // Appends this predicate's canonical fragment to `sb`, or returns false when
    // the node isn't part of the renderable filtered-predicate grammar. Default:
    // not renderable (OR / NOT / DISTINCT FROM / EXISTS / BETWEEN / quantified).
    private protected virtual bool TryAppendFilterDefinition(StringBuilder sb, BatchContext batch) => false;

    // Renders one comparison / IN operand: a single-part column reference as
    // [name], or an otherwise constant-foldable side as its literal. Returns
    // false for anything that isn't a bare column or a constant.
    private static bool TryAppendFilterOperand(StringBuilder sb, Expression operand, BatchContext batch)
    {
        while (operand is Parenthesized paren)
            operand = paren.Wrapped;

        if (operand is Reference reference && reference.ReferencedName.Count == 1)
        {
            _ = sb.Append('[').Append(reference.ReferencedName.Leaf).Append(']');
            return true;
        }

        SqlValue value;
        try
        {
            value = operand.Run(new RuntimeContext(static _ => throw new NotSupportedException(), batch));
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return false;
        }

        if (FormatFilterLiteral(value) is not { } literal)
            return false;
        _ = sb.Append(literal);
        return true;
    }

    // Formats a constant as SQL Server renders it inside filter_definition:
    // strings quoted (N-prefixed when the literal is national / Unicode), numerics
    // parenthesized in invariant culture preserving the literal's scale. Returns
    // null for a NULL literal or a type with no canonical filter rendering
    // (dates / binary / guid — rare in a filtered predicate), bailing the whole
    // render to a null definition.
    private static string? FormatFilterLiteral(SqlValue value)
    {
        if (value.IsNull)
            return null;

        var type = value.Type;
        if (type.Category == SqlTypeCategory.String)
        {
            var prefix = type is NVarcharSqlType or NCharSqlType ? "N" : string.Empty;
            return $"{prefix}'{value.AsString.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        var text = type switch
        {
            _ when type == SqlType.Int32 => value.AsInt32.ToString(CultureInfo.InvariantCulture),
            _ when type == SqlType.BigInt => value.AsInt64.ToString(CultureInfo.InvariantCulture),
            _ when type == SqlType.SmallInt => value.AsInt16.ToString(CultureInfo.InvariantCulture),
            _ when type == SqlType.TinyInt => value.AsByte.ToString(CultureInfo.InvariantCulture),
            _ when type == SqlType.Bit => value.AsBoolean ? "1" : "0",
            _ when type == SqlType.Money || type == SqlType.SmallMoney => value.AsMoney.ToString("F4", CultureInfo.InvariantCulture),
            _ when type == SqlType.Float => value.AsDouble.ToString("G15", CultureInfo.InvariantCulture),
            _ when type == SqlType.Real => value.AsSingle.ToString("G7", CultureInfo.InvariantCulture),
            DecimalSqlType d => value.AsDecimal.ToString($"F{d.scale}", CultureInfo.InvariantCulture),
            _ => null,
        };
        return text is null ? null : $"({text})";
    }

    /// <summary>
    /// Parses a full boolean predicate using SQL Server's standard
    /// precedence: <c>OR</c> binds loosest, then <c>AND</c>, then <c>NOT</c>,
    /// with parens grouping any sub-predicate. The atom level is a single
    /// comparison (the <see cref="CompareExpression"/> shapes). Follows the
    /// lookahead contract on <see cref="ParserContext"/>: on return,
    /// <see cref="ParserContext.Token"/> is the first token not consumed by
    /// the predicate.
    /// </summary>
    public static BooleanExpression Parse(ParserContext context) => ParseOr(context);

    /// <summary>
    /// Lowest-precedence level: zero or more <c>OR</c>-separated
    /// <see cref="ParseAnd"/> chains.
    /// </summary>
    private static BooleanExpression ParseOr(ParserContext context)
    {
        var first = ParseAnd(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Or })
            return first;
        // Collect the whole OR chain into one n-ary node so a flat
        // `p1 OR p2 OR … OR pN` predicate of any length evaluates in a loop
        // rather than recursing per term (the shape that stack-overflowed a
        // long WHERE chain at Run time).
        var operands = new List<BooleanExpression> { first };
        while (context.Token is ReservedKeyword { Keyword: Keyword.Or })
        {
            context.MoveNextRequired();
            operands.Add(ParseAnd(context));
        }
        var chain = new OrExpression([.. operands]);
        return FoldConstantChain(chain, operands, context, absorbing: true);
    }

    /// <summary>
    /// Mid-precedence level: zero or more <c>AND</c>-separated
    /// <see cref="ParseNot"/> chains, collected into one n-ary node.
    /// </summary>
    private static BooleanExpression ParseAnd(ParserContext context)
    {
        var first = ParseNot(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
            return first;
        var operands = new List<BooleanExpression> { first };
        while (context.Token is ReservedKeyword { Keyword: Keyword.And })
        {
            context.MoveNextRequired();
            operands.Add(ParseNot(context));
        }
        var chain = new AndExpression([.. operands]);
        return FoldConstantChain(chain, operands, context, absorbing: false);
    }

    /// <summary>
    /// Real SQL Server's compile-time collapse of an <c>AND</c> / <c>OR</c>
    /// chain carrying an <em>absorbing</em> written-constant operand:
    /// <c>x AND FALSE</c> is FALSE and <c>x OR TRUE</c> is TRUE whatever
    /// <c>x</c> is, so real settles the chain while algebrizing and the other
    /// operands leave the tree with it. Probe-confirmed to be
    /// position-independent (<c>1 = 0 AND &lt;overflow&gt;</c> and
    /// <c>&lt;overflow&gt; AND 1 = 0</c> both answer no rows) and
    /// context-free — it holds under <c>NOT</c>, inside a <c>CASE WHEN</c> and
    /// in a CHECK constraint, where <c>CHECK (1 = 0 AND x / 0 = 1)</c> rejects
    /// the row rather than raising Msg 8134.
    /// <para>
    /// The collapse happens ahead of the GROUP BY containment pass, which is
    /// what lets <c>HAVING 1 = 0 AND b &gt; 1</c> answer no rows over an
    /// ungrouped <c>b</c> where <c>HAVING b &gt; 1</c> alone is Msg 8121 — so
    /// the folded chain hides its operands from
    /// <see cref="VisitSurvivingOperandExpressions"/> while still binding them
    /// (an unknown column inside the dropped operand is still Msg 207).
    /// </para>
    /// </summary>
    /// <param name="chain">The assembled n-ary node, kept as the fold's source.</param>
    /// <param name="operands">The chain's operands, scanned for the absorbing constant.</param>
    /// <param name="context">Parse context, supplying the batch a constant folds against.</param>
    /// <param name="absorbing">The value that absorbs the chain: <see langword="true"/> for <c>OR</c>, <see langword="false"/> for <c>AND</c>.</param>
    private static BooleanExpression FoldConstantChain(BooleanExpression chain, List<BooleanExpression> operands, ParserContext context, bool absorbing)
    {
        foreach (var operand in operands)
        {
            if (TryFoldWrittenConstant(operand, context) == absorbing)
                return new ConstantFoldedPredicate(absorbing, chain);
        }
        return chain;
    }

    /// <summary>
    /// Evaluates <paramref name="predicate"/> at compile time when it is a
    /// written constant, mirroring real's own folding: a fold that <em>raises</em>
    /// leaves the predicate standing for runtime (probe-confirmed —
    /// <c>WHERE 1 / 0 = 1</c> reports Msg 8134 per row, while
    /// <c>WHERE 1 / 0 = 1 AND 1 = 0</c> answers no rows because the chain
    /// collapsed first). Returns <see langword="null"/> for UNKNOWN as well as
    /// for "didn't fold" — every caller treats the two alike.
    /// </summary>
    private static bool? TryFoldWrittenConstant(BooleanExpression predicate, ParserContext context)
    {
        if (!predicate.IsWrittenConstant)
            return null;
        try
        {
            // A written constant reaches no column, so the resolver is
            // unreachable rather than merely unused.
            return predicate.Run(new RuntimeContext(static _ => throw new NotSupportedException(), context.Batch));
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the one simplification a <b>filter</b> position licenses that a
    /// value position doesn't: WHERE / HAVING / ON / a positioned DML predicate
    /// keep only the rows a predicate answers TRUE for, so a predicate real can
    /// see is <em>never</em> TRUE while compiling settles the filter without
    /// the rest of it ever running. That is what makes
    /// <c>WHERE NULL &gt; a AND &lt;overflowing expression&gt;</c> and
    /// <c>WHERE &lt;overflowing expression&gt; BETWEEN NULL AND 5</c> answer no
    /// rows on real where the expression alone raises Msg 8115.
    /// <para>
    /// Unlike <see cref="FoldConstantChain"/>'s absorbing collapse this is
    /// <em>not</em> context-free — a constant-UNKNOWN operand is FALSE-or-UNKNOWN
    /// rather than a definite value, which a CHECK constraint (where UNKNOWN
    /// passes and FALSE rejects) and a <c>NOT</c> both distinguish. So it is
    /// applied only at the filter sites, never inside the predicate's own tree,
    /// and the dropped operands stay visible to
    /// <see cref="VisitSurvivingOperandExpressions"/>: real still reports
    /// Msg 8121 for the ungrouped <c>b</c> in
    /// <c>HAVING NULL &lt;&gt; b AND b &gt; 1</c>, while
    /// <c>HAVING NULL &lt;&gt; b</c> alone — folded by the comparison rule, not
    /// this one — reports nothing.
    /// </para>
    /// </summary>
    internal static BooleanExpression SimplifyForFilter(BooleanExpression predicate, ParserContext context)
    {
        if (predicate is ConstantFoldedPredicate)
            return predicate;
        // A filter that is itself a written constant folds here rather than in
        // the tree: `WHERE 1 = 0`, `HAVING NULL IS NOT NULL` and
        // `HAVING NOT NULL = NULL` carry no chain for the absorbing collapse to
        // work on, and the constant they fold to is only actionable because a
        // filter wants TRUE — a CHECK constraint reads the UNKNOWN ones the
        // other way.
        return predicate.IsNeverTrue || (ConstantFolding.TryFoldPredicate(predicate, context, out var folded) && folded != true)
            ? new FilterNeverTruePredicate(predicate)
            : predicate;
    }

    /// <summary>
    /// Highest-precedence boolean combinator: a sequence of <c>NOT</c>
    /// prefixes wrapping a single atom. Stacking is allowed
    /// (<c>NOT NOT predicate</c>) — each layer adds a
    /// <see cref="NotExpression"/>. This is the only per-term-recursive parse
    /// path in the boolean grammar (the AND / OR chains loop), and boolean
    /// parenthesization re-enters it once per level, so the stack probe here
    /// bounds both NOT-stacking and paren-nesting to a graceful Msg 8631
    /// instead of a fatal overflow.
    /// </summary>
    private static BooleanExpression ParseNot(ParserContext context)
    {
        Expression.EnsureParseStack();
        // Collapse a run of NOT prefixes: three-valued NOT is an involution
        // (NOT NOT p ≡ p for true / false / UNKNOWN alike), so an even count
        // is the identity and an odd count is a single negation. Counting in a
        // loop — rather than recursing per NOT — keeps `NOT NOT … p` of any
        // length from building a deep NotExpression spine that would overflow
        // at Run time.
        var negations = 0;
        while (context.Token is ReservedKeyword { Keyword: Keyword.Not })
        {
            negations++;
            context.MoveNextRequired();
        }
        var atom = ParseAtom(context);
        return negations % 2 == 1 ? new NotExpression(atom) : atom;
    }

    /// <summary>
    /// Either a parenthesized sub-predicate, an <c>EXISTS (SELECT ...)</c>
    /// subquery, or a single comparison. A leading <c>(</c> at the atom
    /// level is ambiguous between two shapes: <c>(boolean_predicate)</c>
    /// (a redundantly-grouped sub-predicate) and <c>(value_expression) cmp
    /// rhs</c> (a parens-wrapped value expression sitting on the LHS of a
    /// comparison). <see cref="LookaheadValueLhs"/> peeks the token
    /// immediately after the matching <c>)</c> and routes via the operator
    /// it finds: a comparison or arithmetic operator (= &lt; &gt; &lt;&gt;
    /// != !&lt; !&gt; LIKE IS IN BETWEEN NOT + - * / % &amp; | ^) — or a
    /// <c>COLLATE</c> postfix, which only a character <em>value</em> takes —
    /// flips into the value-LHS path (<see cref="Expression.Parse"/> handles
    /// <c>Parenthesized</c> via its own grouped-expression dispatch);
    /// anything else stays on the boolean-group path. DACFx emits the
    /// value-LHS shape in CHECK constraints (e.g. WWI's
    /// <c>((case_sum) = (1))</c>); user SQL like <c>WHERE (a + b) = 5</c>
    /// likewise needs it. The boolean-group path remains the default for
    /// <c>WHERE (col = 5) AND ...</c> shapes.
    /// </summary>
    private static BooleanExpression ParseAtom(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' })
        {
            if (LookaheadValueLhs(context))
                return ParseComparison(Expression.Parse(context), context);

            context.MoveNextRequired();
            BooleanExpression inner;
            context.BooleanGroupDepth++;
            try
            {
                inner = ParseOr(context);
            }
            finally
            {
                context.BooleanGroupDepth--;
            }
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional(); // closing `)` is the predicate's last meaningful token; what follows may be end-of-input
            return inner;
        }
        return context.Token switch
        {
            // CONTAINS / FREETEXT are boolean-only (real reserves both names),
            // so they bind here rather than in ResolveBuiltIn. See
            // [docs/claude/full-text.md] for the modeled search grammar.
            ReservedKeyword { Keyword: Keyword.Contains or Keyword.FreeText }
                => Expressions.FullTextPredicate.Parse(context),
            ReservedKeyword { Keyword: Keyword.Exists } => ParseExists(context),
            // REGEXP_LIKE is reserved (at compatibility level 170, where it
            // ships) and boolean-only — real raises Msg 156 for
            // `SELECT REGEXP_LIKE(a, b)`, so it binds here rather than in
            // ResolveBuiltIn. Below 170 the tokenizer leaves the name
            // unreserved and the call falls to Msg 195.
            ReservedKeyword { Keyword: Keyword.Regexp_Like } => Expressions.RegexpLikePredicate.Parse(context),
            // `UPDATE(col)` is a trigger-body predicate, not a scalar — real
            // raises Msg 156 for `SELECT UPDATE(col)`, so it binds here rather
            // than in ResolveBuiltIn.
            ReservedKeyword { Keyword: Keyword.Update } => Expressions.UpdatePredicate.Parse(context),
            _ => ParseComparison(Expression.Parse(context), context),
        };
    }

    /// <summary>
    /// Token-only lookahead that disambiguates <c>(boolean_predicate)</c>
    /// from <c>(value_expression) cmp rhs</c> without parsing either side.
    /// Entered with <see cref="ParserContext.Token"/> on the opening
    /// <c>(</c>. Scans forward tracking paren nesting until the matching
    /// <c>)</c>, peeks the token immediately following it, then restores
    /// the cursor to the opening <c>(</c> before returning. A scan that
    /// reaches end-of-input without balance is treated as boolean-group
    /// (returns <c>false</c>) — the regular parser then surfaces the
    /// underlying syntax error with the normal "near 'X'" wording.
    /// </summary>
    private static bool LookaheadValueLhs(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        var depth = 1;
        while (context.MoveNext())
        {
            switch (context.Token)
            {
                case Operator { Character: '(' }:
                    depth++;
                    break;
                // A top-level ',' inside the outer parens means the shape
                // isn't a single value expression — it's a row-constructor
                // (`(a, b) IN (...)`), a function argument list, or
                // similar. Route to the boolean-group path so the existing
                // grammar surfaces its own "near ','" Msg 4145 rather than
                // partially consuming the first element.
                case Operator { Character: ',' } when depth == 1:
                    context.RestoreCheckpoint(checkpoint);
                    return false;
                case Operator { Character: ')' }:
                    if (--depth == 0)
                    {
                        context.MoveNextOptional();
                        // COLLATE is a postfix only a character *value* takes, so
                        // its presence settles the shape on its own — the predicate
                        // it belongs to (`= 'x'`, `LIKE 'x%'`, `IN (…)`, `IS NULL`,
                        // `BETWEEN`) comes after the collation name, out of this
                        // one-token peek's reach. Real refuses the same postfix on a
                        // parenthesized *boolean* (Msg 156 near 'COLLATE'), so
                        // routing that shape here loses nothing it accepted.
                        var isValueLhs = context.Token is Operator { Character: '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' }
                            or ReservedKeyword { Keyword: Keyword.Like or Keyword.Is or Keyword.In or Keyword.Between or Keyword.Not or Keyword.Collate };
                        context.RestoreCheckpoint(checkpoint);
                        return isValueLhs;
                    }
                    break;
            }
        }
        context.RestoreCheckpoint(checkpoint);
        return false;
    }

    /// <summary>
    /// Parses <c>EXISTS (SELECT ...)</c>. Entered with
    /// <see cref="ParserContext.Token"/> on the <c>EXISTS</c> keyword;
    /// consumes through the closing <c>)</c> and leaves the token on the
    /// next un-consumed token. Unlike <c>IN (SELECT ...)</c>, EXISTS doesn't
    /// constrain the inner SELECT's column count — it counts rows only.
    /// </summary>
    private static ExistsExpression ParseExists(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // Redundant parentheses around the subquery are legal at any depth —
        // EXISTS((SELECT ...)), EXISTS(((SELECT ...))), … (probe-confirmed
        // against SQL Server 2025; DacFx emits the doubly-parenthesized form
        // in its extended-properties reverse-engineering query). Consume the
        // extra opening parens and demand a matching close-paren count.
        var extraParens = 0;
        while (context.GetNextRequired() is Operator { Character: '(' })
            extraParens++;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        // EXISTS counts rows and throws the projection away, so its select list
        // never has to settle an output collation (probe-confirmed).
        context.ProjectionDiscarded = true;
        var inner = Expression.ParseSubqueryRejectingNextValueFor(context);
        context.ProjectionDiscarded = false;
        for (var i = 0; i <= extraParens; i++)
        {
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
        context.SubqueriesParsed++;
        return new ExistsExpression(inner);
    }

    /// <summary>
    /// Parses a single comparison: equality, inequality, ordered comparison,
    /// LIKE/NOT LIKE, IS [NOT] NULL, [NOT] IN, or a quantified subquery
    /// comparison (<c>op {ANY|SOME|ALL} (SELECT ...)</c>). Caller must have
    /// already parsed the left side; on entry <see cref="ParserContext.Token"/>
    /// is the comparison operator or keyword. On return, the token is the
    /// first token not consumed by the predicate atom.
    /// </summary>
    private static BooleanExpression ParseComparison(Expression left, ParserContext context)
    {
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Like }:
                return ParseLike(left, context, negated: false);
            case ReservedKeyword { Keyword: Keyword.Is }:
                return ParseIsSuffix(left, context);
            case ReservedKeyword { Keyword: Keyword.In }:
                return ParseInList(left, context, negated: false);
            case ReservedKeyword { Keyword: Keyword.Between }:
                return ParseBetween(left, context, negated: false);
            case ReservedKeyword { Keyword: Keyword.Not }:
                return context.GetNextRequired() switch
                {
                    ReservedKeyword { Keyword: Keyword.Like } => ParseLike(left, context, negated: true),
                    ReservedKeyword { Keyword: Keyword.In } => ParseInList(left, context, negated: true),
                    ReservedKeyword { Keyword: Keyword.Between } => ParseBetween(left, context, negated: true),
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
        }

        // Identify the binary-comparison operator and advance past its 1- or 2-
        // character form. After the switch, context.Token is positioned on the
        // first token of the RHS (a value expression, or ANY/SOME/ALL keyword
        // introducing a quantified-subquery comparison).
        ComparisonOp op;
        switch (context.Token)
        {
            case Operator { Character: '=' }:
                op = ComparisonOp.Equal;
                context.MoveNextRequired();
                break;
            case Operator { Character: '>' }:
                context.MoveNextRequired();
                if (context.Token is Operator { Character: '=' })
                {
                    op = ComparisonOp.GreaterOrEqual;
                    context.MoveNextRequired();
                }
                else
                {
                    op = ComparisonOp.Greater;
                }
                break;
            case Operator { Character: '<' }:
                context.MoveNextRequired();
                switch (context.Token)
                {
                    case Operator { Character: '=' }:
                        op = ComparisonOp.LessOrEqual;
                        context.MoveNextRequired();
                        break;
                    case Operator { Character: '>' }:
                        op = ComparisonOp.NotEqual;
                        context.MoveNextRequired();
                        break;
                    default:
                        op = ComparisonOp.Less;
                        break;
                }
                break;
            case Operator { Character: '!' }:
                context.MoveNextRequired();
                op = context.Token switch
                {
                    Operator { Character: '=' } => ComparisonOp.NotEqual,
                    Operator { Character: '>' } => ComparisonOp.LessOrEqual,
                    Operator { Character: '<' } => ComparisonOp.GreaterOrEqual,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextRequired();
                break;
            // No comparison / LIKE / IS / IN / NOT-IN / NOT-LIKE following the LHS:
            // the user wrote a value-typed expression where a boolean predicate
            // was expected (IF / WHERE / HAVING / ON / CASE-WHEN / CHECK). Probe-
            // confirmed (2026-05-11) that real SQL Server raises Msg 4145 here,
            // not Msg 102 — the wording specifically calls out non-boolean type.
            // The "near 'X'" suffix is the token following the whole non-boolean
            // expression — which the factory reads off the context, since the
            // parens of a paren-wrapped value (`IF (1) PRINT 'x'`) were consumed
            // on the way in and real names what follows them.
            default:
                throw SimulatedSqlException.NonBooleanInConditionContext(context);
        }

        // Quantified comparison: <op> {ANY|SOME|ALL} (SELECT ...). Probe-
        // confirmed (2026-05-13) that real SQL Server only accepts this form
        // in predicate position; using it in a SELECT-list expression slot
        // raises Msg 102 at the operator. The simulator naturally inherits
        // that restriction because the quantified form is reachable only
        // through BooleanExpression's ParseAtom path.
        if (context.Token is ReservedKeyword { Keyword: var quantifier } &&
            quantifier is Keyword.Any or Keyword.Some or Keyword.All)
        {
            var kind = quantifier == Keyword.All ? QuantifiedKind.All : QuantifiedKind.Any;
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            var inner = Expression.ParseSubqueryRejectingNextValueFor(context);
            context.SubqueriesParsed++;
            if (inner.Schema.Length != 1)
                throw SimulatedSqlException.SubqueryNotIntroducedWithExists();
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return new QuantifiedComparisonExpression(left, op, kind, inner);
        }

        // Regular comparison: RHS is a value expression.
        var right = Expression.Parse(context);
        BooleanExpression comparison = op switch
        {
            ComparisonOp.Equal => new EqualityExpression(left, right),
            ComparisonOp.NotEqual => new InequalityExpression(left, right),
            ComparisonOp.Less => new LessThanExpression(left, right),
            ComparisonOp.LessOrEqual => new LessThanOrEqualExpression(left, right),
            ComparisonOp.Greater => new GreaterThanExpression(left, right),
            _ => new GreaterThanOrEqualExpression(left, right),
        };
        // A comparison against the NULL constant is UNKNOWN for every value the
        // other side could take, so real settles it while compiling and never
        // looks at that side again — see FoldToUnknown. LIKE, IS [NOT] NULL,
        // IS [NOT] DISTINCT FROM and the quantified subquery forms are parsed
        // elsewhere and deliberately don't fold: probing shows real evaluates
        // `<expr> LIKE NULL` and answers an empty `NULL <> ALL (…)` subquery
        // exactly (TRUE, not UNKNOWN).
        return Expression.IsNullConstant(left) || Expression.IsNullConstant(right)
            ? FoldToUnknown(comparison)
            : comparison;
    }

    /// <summary>
    /// Wraps a predicate real SQL Server settles as UNKNOWN while compiling —
    /// a comparison against a NULL constant, and the <c>IN</c> / <c>BETWEEN</c>
    /// shapes that reduce to one. The operands stay for binding but never run,
    /// which is the observable behavior: real answers no rows for
    /// <c>WHERE NULL &gt; &lt;overflowing expression&gt;</c> and reports nothing
    /// for the ungrouped <c>b</c> in <c>HAVING NULL &lt;&gt; b</c>, yet still
    /// reports Msg 207 for <c>WHERE NULL &gt; &lt;unknown column&gt;</c>.
    /// </summary>
    private static ConstantFoldedPredicate FoldToUnknown(BooleanExpression comparison) => new(null, comparison);

    /// <summary>
    /// Re-runs the UNKNOWN fold above against the <em>evaluated</em> constant
    /// rather than the written <c>NULL</c> keyword, which is the reading real
    /// gives a <c>HAVING</c> and only a <c>HAVING</c>.
    /// <para>
    /// Probed apart in both directions: <c>HAVING CAST(NULL AS int) / 17 = b</c>
    /// and <c>HAVING b NOT IN (CAST(NULL AS int) / 44 - 73)</c> report nothing
    /// for an ungrouped <c>b</c> and answer no rows over a bad WHERE, while the
    /// same comparison in a <c>WHERE</c> still raises the other side's error
    /// (<c>WHERE CAST(NULL AS int) / 17 &gt; &lt;overflowing expression&gt;</c>
    /// is Msg 8115) — and the WHERE half is a <em>plan</em> reading there, since
    /// adding <c>DISTINCT</c>, a <c>GROUP BY</c>, a <c>TOP</c> or a join to that
    /// statement flips it to no rows. A HAVING carries a grouping by
    /// construction, so its reading is the stable one and it is the only site
    /// this runs at.
    /// </para>
    /// <para>
    /// Applied per comparison rather than to the clause as a whole, matching
    /// real: <c>HAVING &lt;folded&gt; AND b &gt; 1</c> still reports Msg 8121 for
    /// the surviving conjunct.
    /// </para>
    /// </summary>
    internal virtual BooleanExpression SettleFoldedNullComparisons(ParserContext context) => this;

    /// <summary>Applies <see cref="SettleFoldedNullComparisons"/> to every operand of a chain.</summary>
    private static BooleanExpression[] SettleOperands(BooleanExpression[] operands, ParserContext context)
    {
        var settled = new BooleanExpression[operands.Length];
        for (var i = 0; i < operands.Length; i++)
            settled[i] = operands[i].SettleFoldedNullComparisons(context);
        return settled;
    }

    /// <summary>Whether every element of an <c>IN</c> list folds to NULL while compiling.</summary>
    private static bool AllFoldToNull(Expression[] candidates, ParserContext context)
    {
        foreach (var candidate in candidates)
        {
            if (!ConstantFolding.FoldsToNull(candidate, context))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The six binary comparison operators, with the T-SQL synonyms (<c>!=</c>,
    /// <c>!&gt;</c>, <c>!&lt;</c>) folded into their canonical forms at parse
    /// time (so the runtime only sees six shapes regardless of source spelling).
    /// </summary>
    internal enum ComparisonOp
    {
        Equal,
        NotEqual,
        Less,
        LessOrEqual,
        Greater,
        GreaterOrEqual,
    }

    /// <summary>
    /// Direction of a quantified comparison subquery. <c>SOME</c> is a pure
    /// synonym of <c>ANY</c> in SQL Server, collapsed to <see cref="Any"/> at
    /// parse time.
    /// </summary>
    internal enum QuantifiedKind
    {
        Any,
        All,
    }

    private static LikeExpression ParseLike(Expression left, ParserContext context, bool negated)
    {
        var pattern = Expression.Parse(context.MoveNextRequiredReturnSelf());
        Expression? escape = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Escape })
            escape = Expression.Parse(context.MoveNextRequiredReturnSelf());
        return new LikeExpression(left, pattern, escape, negated);
    }

    /// <summary>
    /// Parses an <c>IS</c> suffix after an expression — either <c>IS [NOT] NULL</c>
    /// (returns <see cref="IsNullExpression"/>) or <c>IS [NOT] DISTINCT FROM rhs</c>
    /// (returns <see cref="DistinctFromExpression"/>, SQL Server 2022+ NULL-safe
    /// equality). Entered with <see cref="ParserContext.Token"/> on the <c>IS</c>
    /// keyword; consumes <c>IS</c>, optional <c>NOT</c>, then dispatches by the
    /// next token. Leaves the token on the next un-consumed token.
    /// </summary>
    private static BooleanExpression ParseIsSuffix(Expression left, ParserContext context)
    {
        var negated = false;
        var next = context.GetNextRequired();
        if (next is ReservedKeyword { Keyword: Keyword.Not })
        {
            negated = true;
            next = context.GetNextRequired();
        }
        switch (next)
        {
            case ReservedKeyword { Keyword: Keyword.Null }:
                context.MoveNextOptional();
                return new IsNullExpression(left, negated);
            case ReservedKeyword { Keyword: Keyword.Distinct }:
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.From })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                return new DistinctFromExpression(left, Expression.Parse(context), negated);
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }
    }

    /// <summary>
    /// Parses the <c>[NOT] IN (...)</c> suffix after an expression. Entered
    /// with <see cref="ParserContext.Token"/> on the <c>IN</c> keyword;
    /// consumes <c>IN</c>, the opening <c>(</c>, either a comma-separated
    /// expression list or a single nested <c>SELECT</c>, and the closing
    /// <c>)</c>. Leaves the token on the next un-consumed token. The
    /// subquery form requires the inner SELECT to project exactly one column
    /// (Msg 116); SQL Server only relaxes this for EXISTS.
    /// </summary>
    private static BooleanExpression ParseInList(Expression left, ParserContext context, bool negated)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        if (context.Token is ReservedKeyword { Keyword: Keyword.Select })
        {
            var inner = Expression.ParseSubqueryRejectingNextValueFor(context);
            if (inner.Schema.Length != 1)
                throw SimulatedSqlException.SubqueryNotIntroducedWithExists();
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            context.SubqueriesParsed++;
            return new InSubqueryExpression(left, inner, negated);
        }

        var candidates = new List<Expression> { Expression.Parse(context) };
        while (context.Token is Operator { Character: ',' })
        {
            context.MoveNextRequired();
            candidates.Add(Expression.Parse(context));
        }
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        var inList = new InExpression(left, [.. candidates], negated, AnySelfReference(left, candidates));
        // `x IN (…)` is a chain of equalities, so it folds on the same rule the
        // comparison shapes do — but only where every equality it stands for is
        // against a NULL constant, negation included (`x NOT IN (NULL)` is
        // UNKNOWN just as `x IN (NULL)` is). One non-NULL element leaves a
        // comparison real evaluates, and it raises that side's error
        // (probe-confirmed: `<overflow> IN (NULL)` answers rows,
        // `<overflow> IN (NULL, 1)` is Msg 8115).
        // A list carrying *one* NULL constant beside other elements folds no
        // further than "never TRUE under NOT" — see InExpression.IsNeverFalse.
        return Expression.IsNullConstant(left) || AllNullConstants(candidates)
            ? FoldToUnknown(inList)
            : inList;
    }

    /// <summary>Whether every element of an <c>IN</c> list is a NULL constant.</summary>
    private static bool AllNullConstants(List<Expression> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!Expression.IsNullConstant(candidate))
                return false;
        }
        return true;
    }

    /// <summary>Whether any element of an <c>IN</c> list is a NULL constant.</summary>
    private static bool AnyNullConstant(Expression[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (Expression.IsNullConstant(candidate))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether an <c>IN</c> list carries the list's own left operand written
    /// again — <c>x IN (…, x, …)</c>, which is exactly <c>x IS NOT NULL</c>:
    /// a non-NULL <c>x</c> matches itself, and a NULL one leaves every
    /// comparison UNKNOWN. So the list is TRUE-or-UNKNOWN, never FALSE, and the
    /// negation is never TRUE — probe-confirmed in both spellings and either
    /// element position (<c>WHERE x NOT IN (x / 0, x)</c> and
    /// <c>WHERE x NOT IN (x, x / 0)</c> both answer no rows on real).
    /// <para>
    /// Matched on a whole column reference read through parentheses, which is
    /// the only spelling that provably names the same column; anything else
    /// leaves the list standing.
    /// </para>
    /// </summary>
    private static bool AnySelfReference(Expression source, List<Expression> candidates)
    {
        if (SelfReferenceName(source) is not { } name)
            return false;
        foreach (var candidate in candidates)
        {
            if (SelfReferenceName(candidate) is { } other && other.Count == name.Count && BuiltInToken.Equals(other.ToString(), name.ToString()))
                return true;
        }
        return false;
    }

    /// <summary>The column a bare (possibly parenthesized) reference names, or null for anything else.</summary>
    private static MultiPartName? SelfReferenceName(Expression expression)
    {
        while (expression is Parenthesized parenthesized)
            expression = parenthesized.Wrapped;
        return expression is Reference reference ? reference.ReferencedName : null;
    }

    /// <summary>
    /// Parses the <c>[NOT] BETWEEN lower AND upper</c> suffix after an
    /// expression. Entered with <see cref="ParserContext.Token"/> on the
    /// <c>BETWEEN</c> keyword; consumes <c>BETWEEN</c>, the lower expression,
    /// the required <c>AND</c>, and the upper expression. Both bounds are
    /// plain value expressions (not boolean predicates) — <see cref="Expression.Parse"/>
    /// naturally stops at the trailing <c>AND</c> keyword because <c>AND</c>
    /// is a boolean combinator at higher precedence, so a trailing <c>AND
    /// other-predicate</c> falls back to the surrounding
    /// <see cref="ParseAnd"/> loop.
    /// </summary>
    private static BooleanExpression ParseBetween(Expression left, ParserContext context, bool negated)
    {
        context.MoveNextRequired();
        var lower = Expression.Parse(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var upper = Expression.Parse(context);
        var between = new BetweenExpression(left, lower, upper, negated);
        // A NULL-constant *subject* makes both halves of the range UNKNOWN, so
        // the whole thing folds like a comparison — and so do NULL constants in
        // *both* bound slots, which leave `UNKNOWN AND UNKNOWN` however the
        // subject reads. Both are UNKNOWN under NOT as well, so the fold is
        // context-free: probe-confirmed by real answering 'F' for all four
        // negation spellings of `CASE WHEN 5 BETWEEN NULL AND NULL …`, by
        // `CHECK (x BETWEEN NULL AND NULL)` and `CHECK (NOT x BETWEEN NULL AND
        // NULL)` both admitting the row, and by
        // `HAVING NOT c NOT BETWEEN NULL AND NULL` answering no rows over an
        // ungrouped `c` where one NULL bound is Msg 8121.
        if (Expression.IsNullConstant(left) || (Expression.IsNullConstant(lower) && Expression.IsNullConstant(upper)))
            return FoldToUnknown(between);
        // *One* NULL-constant bound doesn't fold: it leaves
        // `UNKNOWN AND (value <= upper)`, which is FALSE when the surviving half
        // is — probe-confirmed by real answering 'T' for
        // `CASE WHEN 2 NOT BETWEEN NULL AND 1 …` and by
        // `CHECK (x BETWEEN NULL AND 5)` rejecting x = 10. Those reach the
        // filter-only simplification through IsNeverTrue instead.
        return FoldConstantRangeHalf(between, left, lower, upper, negated, context);
    }

    /// <summary>
    /// Applies the absorbing collapse <see cref="FoldConstantChain"/> runs over
    /// a written <c>AND</c> to the implicit one a range carries: <c>value
    /// BETWEEN lower AND upper</c> is <c>value &gt;= lower AND value &lt;=
    /// upper</c>, so a half that folds to FALSE settles the range whatever the
    /// other half would have done. Probe-confirmed context-free and
    /// position-independent: <c>84 BETWEEN 27 / 0 AND 61</c> reads FALSE
    /// (Msg 8134 for the same halves in the other order, where the raising half
    /// decides first), the <c>NOT</c> spelling reads TRUE, and
    /// <c>CHECK (84 BETWEEN x / 0 AND 61)</c> rejects the row with Msg 547
    /// rather than raising.
    /// </summary>
    private static BooleanExpression FoldConstantRangeHalf(
        BetweenExpression between, Expression value, Expression lower, Expression upper, bool negated, ParserContext context)
    {
        if (!value.IsWrittenConstant)
            return between;
        var lowerIsFalse = lower.IsWrittenConstant
            && TryFoldWrittenConstant(new GreaterThanOrEqualExpression(value, lower), context) == false;
        var upperIsFalse = upper.IsWrittenConstant
            && TryFoldWrittenConstant(new LessThanOrEqualExpression(value, upper), context) == false;
        return lowerIsFalse || upperIsFalse ? new ConstantFoldedPredicate(negated, between) : between;
    }

    /// <summary>
    /// Evaluates the predicate to SQL Server's three-valued logic:
    /// <c>true</c>, <c>false</c>, or <c>null</c> (UNKNOWN). NULL operands in a
    /// comparison surface as <c>null</c> here; <c>NOT</c>, <c>AND</c>, and
    /// <c>OR</c> propagate UNKNOWN per the standard truth tables. Callers
    /// decide what to do with UNKNOWN: WHERE / MERGE-ON treat it as exclude
    /// (only <c>true</c> rows pass); CHECK constraints treat it as pass
    /// (only an explicit <c>false</c> rejects the row).
    /// </summary>
    /// <param name="runtime">Runtime context: column resolver plus batch state.</param>
    public abstract bool? Run(RuntimeContext runtime);

    /// <summary>
    /// Diagnostic-only string rendering, surfaced via
    /// <see cref="DebuggerDisplayAttribute"/>. Production paths must not call
    /// this — same convention as <see cref="Expression.DebugDisplay"/>.
    /// </summary>
    internal abstract string DebugDisplay();

    /// <summary>
    /// Visits every top-level <see cref="Expression"/> operand carried by
    /// this predicate, recursing into nested <see cref="BooleanExpression"/>
    /// children (e.g. <c>AND</c> / <c>OR</c> / <c>NOT</c>) so the visitor
    /// only ever sees Expression nodes. Used by CREATE TABLE's inline-CHECK
    /// validator to enumerate column references via the standard
    /// <see cref="Expression.GetSqlType"/> resolver — the Expression-side
    /// walk is unchanged because every <c>Reference</c> already feeds the
    /// resolver, so callers only need to drive the BooleanExpression-side
    /// traversal from here.
    /// </summary>
    internal abstract void VisitOperandExpressions(Action<Expression> visitor);

    /// <summary>
    /// The predicate half of <see cref="Expression.ParallelSafe"/> — true when
    /// this predicate may be evaluated on a worker thread while the statement
    /// that owns it executes. Default is <b>deny</b>, so the subquery-bearing
    /// predicates (<c>EXISTS</c>, <c>IN (SELECT …)</c>, <c>ANY</c> / <c>ALL</c>)
    /// and every predicate kind nobody has proved decline without needing to
    /// name themselves.
    /// <para>
    /// The nesting operators (<c>AND</c> / <c>OR</c> / <c>NOT</c>) recurse
    /// through their <see cref="BooleanExpression"/> operands directly rather
    /// than through <see cref="VisitOperandExpressions"/>: that walk flattens
    /// to Expression leaves, so an <c>EXISTS</c> operand — which carries no
    /// Expression leaves at all — would contribute nothing and read as safe.
    /// </para>
    /// </summary>
    internal virtual bool ParallelSafe => false;

    /// <summary>
    /// <see cref="ParallelSafe"/> answered from this predicate's own
    /// Expression operands, for the leaf kinds that carry no nested predicate.
    /// </summary>
    private protected bool OperandExpressionsParallelSafe
    {
        get
        {
            var safe = true;
            this.VisitOperandExpressions(operand => safe = safe && operand.ParallelSafe);
            return safe;
        }
    }

    /// <summary><see cref="ParallelSafe"/> folded over a predicate array.</summary>
    private protected static bool AllParallelSafe(BooleanExpression[] operands)
    {
        foreach (var operand in operands)
        {
            if (!operand.ParallelSafe)
                return false;
        }
        return true;
    }

    /// <summary>
    /// The operands still standing after compile-time folding — the tree real
    /// SQL Server runs its GROUP BY containment pass over, which is why
    /// <c>HAVING NULL &lt;&gt; b</c> and <c>HAVING 1 = 0 AND b &gt; 1</c> report
    /// nothing for an ungrouped <c>b</c> while <c>HAVING b &gt; 1</c> is
    /// Msg 8121. Identical to <see cref="VisitOperandExpressions"/> everywhere
    /// except a folded node, so every other consumer of the operand walk —
    /// inline-CHECK validation, view updatability, read-column recording —
    /// keeps seeing the written predicate.
    /// </summary>
    internal virtual void VisitSurvivingOperandExpressions(Action<Expression> visitor) =>
        this.VisitOperandExpressions(visitor);

    /// <summary>
    /// Whether this predicate is known while compiling to never evaluate TRUE
    /// (it is constant FALSE or constant UNKNOWN, or an <c>AND</c> carrying
    /// such an operand). Read only by <see cref="SimplifyForFilter"/>: a value
    /// position still has to tell FALSE from UNKNOWN, so the knowledge licenses
    /// nothing outside a filter.
    /// </summary>
    internal virtual bool IsNeverTrue => false;

    /// <summary>
    /// The mirror of <see cref="IsNeverTrue"/>: the predicate is known while
    /// compiling to never evaluate FALSE (it is constant TRUE or constant
    /// UNKNOWN). Read only through <c>NOT</c> — <c>NOT p</c> is never TRUE
    /// exactly when <c>p</c> is never FALSE — which is what settles the
    /// double-negated shapes real settles: <c>WHERE NOT x NOT BETWEEN NULL AND
    /// &lt;bad&gt;</c> and <c>WHERE NOT x IN (&lt;bad&gt;, NULL)</c> both answer
    /// no rows there while the operand alone raises.
    /// </summary>
    internal virtual bool IsNeverFalse => false;

    /// <summary>
    /// The predicate-side counterpart of <see cref="Expression.IsWrittenConstant"/>:
    /// true when real folds this condition to a constant while compiling.
    /// Read by <see cref="NullabilityContext.TryFoldCondition"/>, which is what
    /// lets a <c>CASE</c> / <c>IIF</c> arm guarded by a folded condition drop
    /// out of (or take over) the projection's nullability.
    /// </summary>
    /// <remarks>
    /// The default is a conservative <see langword="false"/> and each shape
    /// that answers from its own operands opts in — a blanket
    /// <see cref="VisitOperandExpressions"/> walk would wrongly call
    /// <c>1 = 1 AND EXISTS (…)</c> constant, since the subquery shapes carry
    /// operands the walk never reaches.
    /// </remarks>
    internal virtual bool IsWrittenConstant => false;

    /// <summary>Whether every element opts into <see cref="IsWrittenConstant"/>.</summary>
    private static bool AllWrittenConstant(BooleanExpression[] operands)
    {
        foreach (var operand in operands)
        {
            if (!operand.IsWrittenConstant)
                return false;
        }
        return true;
    }

    /// <summary>Whether every element is an <see cref="Expression.IsWrittenConstant"/>.</summary>
    private static bool AllWrittenConstant(Expression[] operands)
    {
        foreach (var operand in operands)
        {
            if (!operand.IsWrittenConstant)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Compile-time bind of the predicate, the <see cref="BooleanExpression"/>
    /// counterpart to <see cref="Expression.GetSqlType"/>. Resolves every
    /// operand's static type — which surfaces an unknown column's Msg 207 and
    /// each built-in's own argument-type errors without a row in hand — and
    /// applies the rules that need both operands' types, namely the
    /// cross-collation Msg 468 gate. Real SQL Server binds a predicate while
    /// compiling, so an empty rowset reports the same errors a populated one
    /// does; running this at parse is what puts the simulator on that footing.
    /// The default handles a leaf that carries only operands; combinators
    /// (<c>AND</c> / <c>OR</c> / <c>NOT</c>) and the comparison shapes override
    /// so each node's own pairing rule runs.
    /// </summary>
    /// <param name="batch">The active batch context, threaded to <see cref="Expression.GetSqlType"/>.</param>
    /// <param name="resolveColumnType">Callback mapping a column name to its declared type; raises Msg 207 when nothing resolves.</param>
    internal virtual void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.VisitOperandExpressions(operand => _ = operand.GetSqlType(batch, resolveColumnType));

    /// <summary>
    /// Raises **Msg 468** when two string operands of a comparison carry
    /// collations that can't be resolved to one. Shared by the compile-time
    /// <see cref="Bind"/> path and the per-value
    /// <see cref="CompareValuesPromoted"/> so the two can't drift; real
    /// reports this while compiling, and the wording names the right operand's
    /// collation first (probe-confirmed). Also driven from
    /// <see cref="Expressions.CaseExpression"/>, whose simple form compares its
    /// input against each <c>WHEN</c> value with the same <c>=</c> semantics.
    /// </summary>
    internal static void RequireResolvableCollation(SqlType left, SqlType right, string operatorName)
    {
        if (left.Category != SqlTypeCategory.String || right.Category != SqlTypeCategory.String)
            return;
        // A comparison needs a definite collation to compare under, so an
        // operand that arrived carrying an unresolved one reports Msg 4191
        // naming this comparison — the producing operator that couldn't settle
        // it isn't named at all (probe-confirmed against SQL Server 2025).
        UnresolvedCollation.Require(left, right, operatorName);
        if (left != right && Collation.Resolve(left, right) is null)
            throw SimulatedSqlException.CollationConflict(right.Collation!.Name, left.Collation!.Name, operatorName);
    }

    /// <summary>
    /// <see cref="Bind"/> helper for a comparison shape: types both sides
    /// through <paramref name="resolveColumnType"/> and runs
    /// <see cref="RequireResolvableCollation"/> over the pair.
    /// </summary>
    private static void BindComparison(Expression left, Expression right, BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType, string operatorName) =>
        RequireResolvableCollation(
            left.GetSqlType(batch, resolveColumnType),
            right.GetSqlType(batch, resolveColumnType),
            operatorName);

    /// <summary>
    /// Flattens a top-level <c>AND</c> chain into its individual conjuncts,
    /// appending each to <paramref name="sink"/>. A non-<c>AND</c> predicate
    /// contributes itself. Used by the join planner to pull equi-join key
    /// equalities out of an <c>ON</c> predicate; <c>OR</c> / <c>NOT</c> /
    /// comparison nodes are opaque leaves here (only the outermost <c>AND</c>
    /// spine splits), so <c>a.x = b.y OR …</c> stays a single conjunct.
    /// </summary>
    internal virtual void CollectConjuncts(List<BooleanExpression> sink) => sink.Add(this);

    /// <summary>
    /// Combines two predicates with a three-valued <c>AND</c>. Used to
    /// synthesize an <c>ON</c> predicate from the WHERE conjuncts when a
    /// comma / <c>CROSS JOIN</c> is rewritten into an equi-join — the result
    /// re-splits cleanly through <see cref="CollectConjuncts"/>, so the join
    /// planner recovers the individual key equalities.
    /// </summary>
    internal static BooleanExpression And(BooleanExpression left, BooleanExpression right) => new AndExpression([left, right]);

    /// <summary>
    /// Rebuilds <paramref name="predicate"/> with each of its operands replaced
    /// by <paramref name="rebind"/>'s result, or returns null when the predicate
    /// isn't one of the rebuildable shapes or any one operand declines. Lets the
    /// WHERE pushdown (<c>Selection.Execution.PredicatePushdown.cs</c>) move a
    /// conjunct into a view / derived-table body, whose columns are the same
    /// values under different names, without reaching into this hierarchy.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shapes are exactly the ones the pushdown's residual invariant needs:
    /// a comparison (<c>=</c> / <c>&lt;</c> / <c>&lt;=</c> / <c>&gt;</c> /
    /// <c>&gt;=</c>), a non-negated <c>BETWEEN</c>, and the equality family an
    /// <c>IN</c> list or an OR-of-equalities decomposes into. Every one of them
    /// is NULL-rejecting, so the rebuilt predicate reads UNKNOWN — never TRUE —
    /// over a row whose operand columns are all NULL. <c>IS NULL</c>,
    /// <c>IS NOT DISTINCT FROM</c> and a mixed <c>OR</c> chain are absent for
    /// that reason, not for lack of a constructor.
    /// </para>
    /// <para>
    /// A predicate real settled while compiling declines outright: its operands
    /// never run, so rebuilding one would run what real doesn't.
    /// </para>
    /// </remarks>
    internal static BooleanExpression? TryRebindOperands(BooleanExpression predicate, Func<Expression, Expression?> rebind)
    {
        if (predicate.IsWrittenConstant || predicate.IsNeverTrue)
            return null;

        if (predicate.TryGetEqualityOperands(out var equalLeft, out var equalRight))
        {
            return rebind(equalLeft) is { } left && rebind(equalRight) is { } right
                ? new EqualityExpression(left, right)
                : null;
        }

        if (predicate.TryGetRangeOperands(out var rangeLeft, out var op, out var rangeRight))
        {
            return rebind(rangeLeft) is not { } left || rebind(rangeRight) is not { } right
                ? null
                : op switch
                {
                    RangeComparison.Greater => new GreaterThanExpression(left, right),
                    RangeComparison.GreaterOrEqual => new GreaterThanOrEqualExpression(left, right),
                    RangeComparison.Less => new LessThanExpression(left, right),
                    _ => new LessThanOrEqualExpression(left, right),
                };
        }

        if (predicate.TryGetBetweenOperands(out var value, out var lower, out var upper))
        {
            return rebind(value) is { } subject && rebind(lower) is { } low && rebind(upper) is { } high
                ? new BetweenExpression(subject, low, high, negated: false)
                : null;
        }

        if (predicate.TryGetEqualityFamily(out var pairs))
        {
            var equalities = new BooleanExpression[pairs.Count];
            for (var i = 0; i < pairs.Count; i++)
            {
                if (rebind(pairs[i].Left) is not { } left || rebind(pairs[i].Right) is not { } right)
                    return null;
                equalities[i] = new EqualityExpression(left, right);
            }
            return equalities.Length == 1 ? equalities[0] : new OrExpression(equalities);
        }

        return null;
    }

    /// <summary>
    /// Flattens a top-level <c>OR</c> chain into its individual disjunct terms,
    /// appending each to <paramref name="sink"/>. A non-<c>OR</c> predicate
    /// contributes itself. The mirror of <see cref="CollectConjuncts"/>, used to
    /// recognize the keyset-pagination staircase
    /// (<c>a &gt; @x OR (a = @x AND b &gt; @y)</c>).
    /// </summary>
    internal virtual void CollectDisjuncts(List<BooleanExpression> sink) => sink.Add(this);

    /// <summary>
    /// Exposes the two operands when this predicate is an equality comparison
    /// (<c>=</c>); returns false for every other node. Lets the join planner
    /// recognize <c>left = right</c> conjuncts without reaching into the
    /// private comparison-subclass hierarchy.
    /// </summary>
    internal virtual bool TryGetEqualityOperands([NotNullWhen(true)] out Expression? left, [NotNullWhen(true)] out Expression? right)
    {
        left = null;
        right = null;
        return false;
    }

    /// <summary>
    /// Decomposes this predicate into a list of equality operand pairs when it
    /// is logically a chain of equalities OR'd together — i.e. an <c>IN</c>
    /// list (<c>x IN (a, b, c)</c> → three pairs of <c>(x, a)</c> /
    /// <c>(x, b)</c> / <c>(x, c)</c>) or an <c>OR</c>-tree whose leaves are
    /// all equality comparisons (<c>x = a OR x = b</c> → two pairs). The
    /// caller decides whether the pairs share a common operand and whether
    /// the other side is row-invariant — this method only exposes the shape.
    /// Returns false for anything else (including <c>NOT IN</c>, mixed-shape
    /// OR-trees, and ordinary leaves). Equivalent for <c>=</c>-semantics:
    /// every form treats a NULL on either side of a leaf as UNKNOWN, so the
    /// equality-seek path's existing NULL-skip applies identically.
    /// </summary>
    internal virtual bool TryGetEqualityFamily([NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
    {
        pairs = null;
        return false;
    }

    /// <summary>
    /// Exposes the left side of a non-negated <c>expr IN (SELECT …)</c>, whose
    /// value set exists only once the subquery has run. The index-seek planner
    /// asks first — the subject has to be an indexable column of the source it
    /// is narrowing — and only then materializes through
    /// <see cref="TryMaterializeProbeFamily"/>, so a body whose values could
    /// never drive a seek is never executed early for that purpose.
    /// </summary>
    internal virtual bool TryGetSubqueryProbeSubject([NotNullWhen(true)] out Expression? subject)
    {
        subject = null;
        return false;
    }

    /// <summary>
    /// The equality family an <b>uncorrelated</b> <c>IN (SELECT …)</c>
    /// decomposes into once its values are materialized — one
    /// <c>&lt;subject&gt; = &lt;value&gt;</c> pair per non-NULL value — so a
    /// read whose subject is indexed drives from the values (a seek each)
    /// instead of scanning the outer and probing every row against them.
    /// Declines for <c>NOT IN</c> (whose matches are the complement), for a
    /// correlated body, and past <paramref name="cap"/> values. The
    /// materialization goes through the statement's own subquery memo, so the
    /// per-row path reuses it rather than running the body twice; NULL values
    /// are left out (they equi-match nothing) and the untouched <c>IN</c>
    /// conjunct stays in the residual WHERE, which is what keeps the
    /// three-valued answer exact.
    /// </summary>
    internal virtual bool TryMaterializeProbeFamily(
        BatchContext batch,
        Func<MultiPartName, SqlValue> outerResolver,
        SqlType subjectType,
        int cap,
        [NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
    {
        pairs = null;
        return false;
    }

    /// <summary>
    /// Exposes the operands and operator when this predicate is an ordering
    /// comparison (<c>&gt;</c> / <c>&gt;=</c> / <c>&lt;</c> / <c>&lt;=</c>);
    /// returns false otherwise. Lets the index-seek planner recognize a range
    /// bound (<c>col &gt; v</c> or <c>v &lt; col</c>) on a key column. The caller
    /// normalizes which side is the column and flips the operator accordingly.
    /// </summary>
    internal virtual bool TryGetRangeOperands([NotNullWhen(true)] out Expression? left, out RangeComparison op, [NotNullWhen(true)] out Expression? right)
    {
        left = null;
        right = null;
        op = RangeComparison.Greater;
        return false;
    }

    /// <summary>
    /// Exposes the value and the inclusive lower / upper bounds when this
    /// predicate is a non-negated <c>value BETWEEN lower AND upper</c>; returns
    /// false otherwise (<c>NOT BETWEEN</c> is the non-contiguous complement, so
    /// it declines). Lets the index-seek planner treat BETWEEN as a two-sided
    /// inclusive range on a key column.
    /// </summary>
    internal virtual bool TryGetBetweenOperands([NotNullWhen(true)] out Expression? value, [NotNullWhen(true)] out Expression? lower, [NotNullWhen(true)] out Expression? upper)
    {
        value = null;
        lower = null;
        upper = null;
        return false;
    }

    /// <summary>
    /// A predicate real SQL Server settled to a constant while compiling: its
    /// <see cref="Run"/> answers that constant and the written operands never
    /// evaluate. Two rules build one — a comparison against a NULL constant
    /// (UNKNOWN) and an <c>AND</c> / <c>OR</c> chain absorbed by a written
    /// constant (FALSE / TRUE).
    /// <para>
    /// The fold lands between name resolution and the GROUP BY containment
    /// pass, which is exactly what real does: <c>WHERE NULL &gt; &lt;unknown
    /// column&gt;</c> is still Msg 207 (so <see cref="Bind"/> forwards), while
    /// the ungrouped column in <c>HAVING NULL &lt;&gt; b</c> reports nothing (so
    /// <see cref="VisitSurvivingOperandExpressions"/> stops here). Every other
    /// operand walk forwards, leaving inline-CHECK validation and read-column
    /// recording reading the predicate as written.
    /// </para>
    /// </summary>
    private sealed class ConstantFoldedPredicate(bool? value, BooleanExpression folded) : BooleanExpression
    {
        internal override bool IsWrittenConstant => true;

        internal override bool IsNeverTrue => value != true;

        internal override bool IsNeverFalse => value != false;

        public override bool? Run(RuntimeContext runtime) => value;

        internal override string DebugDisplay() =>
            $"{value switch { true => "TRUE", false => "FALSE", _ => "UNKNOWN" }} /* {folded.DebugDisplay()} */";

        internal override bool ParallelSafe => folded.ParallelSafe;

        internal override void VisitOperandExpressions(Action<Expression> visitor) => folded.VisitOperandExpressions(visitor);

        // The fold took these out of the tree before the containment pass ran.
        internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor) { }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            folded.Bind(batch, resolveColumnType);

        // The equality shape stays readable so `catalog_column = NULL` still
        // seeks empty rather than materializing the view: the seek planners
        // only ever pair a bare column with the *other* side, which for an
        // equality fold is the NULL constant itself, so nothing the fold
        // promised not to evaluate can be reached through here. The range and
        // equality-family shapes stay hidden — their operands are arbitrary
        // expressions a planner would evaluate for a row-independent bound,
        // which is exactly what folding took off the table.
        internal override bool TryGetEqualityOperands([NotNullWhen(true)] out Expression? left, [NotNullWhen(true)] out Expression? right) =>
            folded.TryGetEqualityOperands(out left, out right);
    }

    /// <summary>
    /// A filter predicate <see cref="SimplifyForFilter"/> found can never be
    /// TRUE, so the filter keeps no row and nothing under it has to run. Only
    /// <see cref="Run"/> changes: the wrapped predicate still binds and still
    /// offers its operands to every walk, because real reports the ungrouped
    /// column in <c>HAVING NULL &lt;&gt; b AND b &gt; 1</c> even though it
    /// evaluates neither conjunct.
    /// </summary>
    private sealed class FilterNeverTruePredicate(BooleanExpression inner) : BooleanExpression
    {
        internal override bool IsWrittenConstant => inner.IsWrittenConstant;

        internal override bool IsNeverTrue => true;

        public override bool? Run(RuntimeContext runtime) => false;

        internal override string DebugDisplay() => $"FALSE /* {inner.DebugDisplay()} */";

        internal override bool ParallelSafe => inner.ParallelSafe;

        internal override void VisitOperandExpressions(Action<Expression> visitor) => inner.VisitOperandExpressions(visitor);

        internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor) =>
            inner.VisitSurvivingOperandExpressions(visitor);

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            inner.Bind(batch, resolveColumnType);
    }

    /// <summary>
    /// Three-valued <c>AND</c> over an n-ary operand list (a whole flat
    /// <c>p1 AND p2 AND … AND pN</c> chain collapses to one node, so
    /// evaluation loops instead of recursing per term): <c>false AND x =
    /// false</c> regardless of <c>x</c>; <c>true AND x = x</c>; <c>NULL AND
    /// NULL = NULL</c>. Short-circuits on the first <c>false</c> operand —
    /// later operands aren't evaluated. SQL Server doesn't guarantee
    /// evaluation order, but it permits short-circuit; the simulator commits
    /// to it (left-to-right) for predictability.
    /// </summary>
    private sealed class AndExpression(BooleanExpression[] operands) : BooleanExpression
    {
        internal override bool ParallelSafe => AllParallelSafe(operands);

        internal override bool IsWrittenConstant => AllWrittenConstant(operands);

        // One conjunct that can't be TRUE is enough: AND is TRUE only when
        // every operand is.
        internal override bool IsNeverTrue
        {
            get
            {
                foreach (var operand in operands)
                {
                    if (operand.IsNeverTrue)
                        return true;
                }
                return false;
            }
        }

        // AND is FALSE as soon as any operand is, so it takes *every* operand
        // declining FALSE to keep the chain off it.
        internal override bool IsNeverFalse
        {
            get
            {
                foreach (var operand in operands)
                {
                    if (!operand.IsNeverFalse)
                        return false;
                }
                return true;
            }
        }

        public override bool? Run(RuntimeContext runtime)
        {
            var result = (bool?)true;
            foreach (var operand in operands)
            {
                var value = operand.Run(runtime);
                if (value == false)
                    return false;
                result = result == true && value == true ? true : null;
            }
            return result;
        }

        internal override string DebugDisplay() => string.Join(" AND ", operands.Select(o => o.DebugDisplay()));

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            foreach (var operand in operands)
                operand.VisitOperandExpressions(visitor);
        }

        internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor)
        {
            foreach (var operand in operands)
                operand.VisitSurvivingOperandExpressions(visitor);
        }

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            new AndExpression(SettleOperands(operands, context));

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            foreach (var operand in operands)
                operand.Bind(batch, resolveColumnType);
        }

        internal override void CollectConjuncts(List<BooleanExpression> sink)
        {
            foreach (var operand in operands)
                operand.CollectConjuncts(sink);
        }

        private protected override bool TryAppendFilterDefinition(StringBuilder sb, BatchContext batch)
        {
            for (var i = 0; i < operands.Length; i++)
            {
                if (i > 0)
                    _ = sb.Append(" AND ");
                if (!operands[i].TryAppendFilterDefinition(sb, batch))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Three-valued <c>OR</c> over an n-ary operand list (see
    /// <see cref="AndExpression"/> for why the chain is flattened): <c>true OR
    /// x = true</c> regardless of <c>x</c>; <c>false OR x = x</c>; <c>NULL OR
    /// NULL = NULL</c>. Short-circuits on the first <c>true</c> operand.
    /// </summary>
    private sealed class OrExpression(BooleanExpression[] operands) : BooleanExpression
    {
        internal override bool ParallelSafe => AllParallelSafe(operands);

        internal override bool IsWrittenConstant => AllWrittenConstant(operands);

        // The mirror of AND's pair: OR is TRUE as soon as any operand is (so it
        // takes every operand declining TRUE to keep the chain off it), and
        // FALSE only when every operand is. Probe-confirmed on the first —
        // `WHERE (x BETWEEN NULL AND 5) OR (x BETWEEN NULL AND <bad>)` answers
        // no rows on real where the operand alone raises.
        internal override bool IsNeverTrue
        {
            get
            {
                foreach (var operand in operands)
                {
                    if (!operand.IsNeverTrue)
                        return false;
                }
                return true;
            }
        }

        internal override bool IsNeverFalse
        {
            get
            {
                foreach (var operand in operands)
                {
                    if (operand.IsNeverFalse)
                        return true;
                }
                return false;
            }
        }

        public override bool? Run(RuntimeContext runtime)
        {
            var result = (bool?)false;
            foreach (var operand in operands)
            {
                var value = operand.Run(runtime);
                if (value == true)
                    return true;
                result = result == false && value == false ? false : null;
            }
            return result;
        }

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            new OrExpression(SettleOperands(operands, context));

        internal override string DebugDisplay() => string.Join(" OR ", operands.Select(o => o.DebugDisplay()));

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            foreach (var operand in operands)
                operand.VisitOperandExpressions(visitor);
        }

        internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor)
        {
            foreach (var operand in operands)
                operand.VisitSurvivingOperandExpressions(visitor);
        }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            foreach (var operand in operands)
                operand.Bind(batch, resolveColumnType);
        }

        internal override void CollectDisjuncts(List<BooleanExpression> sink)
        {
            foreach (var operand in operands)
                operand.CollectDisjuncts(sink);
        }

        // Flatten the OR chain into leaf equality pairs. Succeeds only when
        // EVERY operand is itself an equality family — a single equality
        // compare (one pair), a nested OR node (recursed via the virtual on
        // the child), or an IN list (multiple pairs against a common LHS). One
        // non-equality operand aborts the whole walk so the predicate falls
        // back to scan.
        internal override bool TryGetEqualityFamily([NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
        {
            var sink = new List<(Expression Left, Expression Right)>();
            foreach (var operand in operands)
            {
                if (operand.TryGetEqualityOperands(out var le, out var re))
                {
                    sink.Add((le, re));
                }
                else if (operand.TryGetEqualityFamily(out var nested))
                {
                    sink.AddRange(nested);
                }
                else
                {
                    pairs = null;
                    return false;
                }
            }
            pairs = sink;
            return true;
        }
    }

    /// <summary>
    /// <c>expr IS [NOT] NULL</c>: definitively resolves UNKNOWN to true /
    /// false. Distinct from <c>expr = NULL</c> (which would be UNKNOWN per
    /// three-valued logic): <c>IS NULL</c> tests the actual nullity of the
    /// value and never returns <c>null</c> itself. The result of
    /// <c>IS NOT NULL</c> is just the negation; standard NOT behavior
    /// doesn't apply because there's no UNKNOWN to propagate.
    /// </summary>
    private sealed class IsNullExpression(Expression source, bool negated) : BooleanExpression
    {
        internal override bool ParallelSafe => source.ParallelSafe;

        internal override bool IsWrittenConstant => source.IsWrittenConstant;

        public override bool? Run(RuntimeContext runtime) =>
            source.Run(runtime).IsNull ^ negated;

        internal override string DebugDisplay() => $"{source.DebugDisplay()} IS {(negated ? "NOT NULL" : "NULL")}";

        internal override void VisitOperandExpressions(Action<Expression> visitor) => visitor(source);

        private protected override bool TryAppendFilterDefinition(StringBuilder sb, BatchContext batch)
        {
            if (!TryAppendFilterOperand(sb, source, batch))
                return false;
            _ = sb.Append(negated ? " IS NOT NULL" : " IS NULL");
            return true;
        }
    }

    /// <summary>
    /// <c>expr IS [NOT] DISTINCT FROM rhs</c> — SQL Server 2022 NULL-safe
    /// comparison. Like <see cref="IsNullExpression"/>, never returns UNKNOWN:
    /// <c>both NULL → not distinct</c>, <c>exactly one NULL → distinct</c>,
    /// <c>both non-null → distinct iff unequal</c>. Type promotion follows the
    /// regular comparison path via <see cref="CompareValuesPromoted"/>; a
    /// genuinely uncoercible operand pair (e.g. <c>'hello' is distinct from 5</c>)
    /// still surfaces the underlying Msg 245 / Msg 402 from the comparator,
    /// matching real SQL Server's behavior.
    /// </summary>
    private sealed class DistinctFromExpression(Expression left, Expression right, bool negated) : BooleanExpression
    {
        internal override bool ParallelSafe => this.OperandExpressionsParallelSafe;

        internal override bool IsWrittenConstant => left.IsWrittenConstant && right.IsWrittenConstant;

        public override bool? Run(RuntimeContext runtime)
        {
            var l = left.Run(runtime);
            var r = right.Run(runtime);
            var distinct = (l.IsNull, r.IsNull) switch
            {
                (true, true) => false,
                (true, false) or (false, true) => true,
                _ => CompareValuesPromoted(l, r, "not equal to", static (a, b) => !a.Equals(b)) == true,
            };
            return distinct ^ negated;
        }

        internal override string DebugDisplay() =>
            $"{left.DebugDisplay()} IS {(negated ? "NOT " : "")}DISTINCT FROM {right.DebugDisplay()}";

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(left);
            visitor(right);
        }

        // Real names the operator "is not" here, not the "not equal to" the
        // runtime comparator borrows (probe-confirmed on
        // `x IS DISTINCT FROM y` across two collations).
        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            BindComparison(left, right, batch, resolveColumnType, "is not");
    }

    /// <summary>
    /// <c>expr [NOT] IN (v1, v2, ...)</c>: equivalent to a chain of
    /// promote-and-equal comparisons OR-combined (or AND-of-not for
    /// <c>NOT IN</c>). NULL semantics follow the desugared form: a NULL
    /// left side returns UNKNOWN; a non-NULL left with no match but a NULL
    /// element in the list returns UNKNOWN (the NULL might have been a
    /// match); a non-NULL left with no match and no NULL element returns
    /// the negated flag. Subquery form <c>IN (SELECT ...)</c> isn't modeled.
    /// </summary>
    private sealed class InExpression(Expression source, Expression[] candidates, bool negated, bool selfReferenced) : BooleanExpression
    {
        internal override bool ParallelSafe => this.OperandExpressionsParallelSafe;

        internal override bool IsWrittenConstant => source.IsWrittenConstant && AllWrittenConstant(candidates);

        // A NULL constant among the elements contributes an UNKNOWN equality to
        // the chain the list stands for, so a list that matches nothing reads
        // UNKNOWN rather than FALSE: `x IN (…, NULL, …)` is TRUE or UNKNOWN and
        // `x NOT IN (…, NULL, …)` is FALSE or UNKNOWN. Probe-confirmed on both
        // halves — real answers no rows for `WHERE x NOT IN (<bad>, NULL)` and
        // for the `NOT x IN (…)` spelling, and raises the operand's own error
        // for the un-negated `WHERE x IN (<bad>, NULL)`.
        // The list's own left operand written again reads the same way (see
        // AnySelfReference), so it answers both properties too — but only the
        // negated half matches real: it settles `x NOT IN (x / 0, x)` in either
        // element order, while the un-negated `x IN (x / 0, x)` raises there
        // for one written order and answers for the other.
        internal override bool IsNeverTrue => negated && (AnyNullConstant(candidates) || selfReferenced);

        internal override bool IsNeverFalse => !negated && (AnyNullConstant(candidates) || selfReferenced);

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            ConstantFolding.FoldsToNull(source, context) || AllFoldToNull(candidates, context)
                ? FoldToUnknown(this)
                : this;

        public override bool? Run(RuntimeContext runtime)
        {
            var src = source.Run(runtime);
            if (src.IsNull)
                return null;
            var sawNull = false;
            foreach (var candidate in candidates)
            {
                var c = candidate.Run(runtime);
                if (c.IsNull)
                {
                    sawNull = true;
                    continue;
                }
                if (CompareValuesPromoted(src, c, "equal to", static (l, r) => l.Equals(r)) == true)
                    return !negated;
            }
            return sawNull ? null : negated;
        }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            var sourceType = source.GetSqlType(batch, resolveColumnType);
            foreach (var candidate in candidates)
                RequireResolvableCollation(sourceType, candidate.GetSqlType(batch, resolveColumnType), "equal to");
        }

        internal override string DebugDisplay()
        {
            var keyword = negated ? "NOT IN" : "IN";
            return $"{source.DebugDisplay()} {keyword} ({string.Join(", ", candidates.Select(c => c.DebugDisplay()))})";
        }

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(source);
            foreach (var candidate in candidates)
                visitor(candidate);
        }

        private protected override bool TryAppendFilterDefinition(StringBuilder sb, BatchContext batch)
        {
            // NOT IN isn't part of the filtered-index grammar (real SQL Server
            // rejects it), so only the positive form renders.
            if (negated || candidates.Length == 0 || !TryAppendFilterOperand(sb, source, batch))
                return false;
            _ = sb.Append(" IN (");
            for (var i = 0; i < candidates.Length; i++)
            {
                if (i > 0)
                    _ = sb.Append(", ");
                if (!TryAppendFilterOperand(sb, candidates[i], batch))
                    return false;
            }
            _ = sb.Append(')');
            return true;
        }

        // `source IN (c1, c2, ...)` is logically `source=c1 OR source=c2 OR ...`,
        // so for the equality-seek path it decomposes into one pair per candidate
        // against the shared LHS. NOT IN doesn't decompose into positive
        // equalities (it's an AND-of-inequalities) and falls through to scan.
        internal override bool TryGetEqualityFamily([NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
        {
            if (negated)
            {
                pairs = null;
                return false;
            }
            pairs = new List<(Expression Left, Expression Right)>(candidates.Length);
            foreach (var candidate in candidates)
                pairs.Add((source, candidate));
            return true;
        }
    }

    /// <summary>
    /// <c>EXISTS (SELECT ...)</c>: true iff the inner SELECT returns at
    /// least one row. Two-valued (never UNKNOWN — row-count semantics) and
    /// indifferent to the inner row's column values, including NULLs.
    /// Re-executes the inner plan per outer row, threading the caller's
    /// resolver as the inner's outer scope so correlated references resolve
    /// up the chain; an inner plan that never reaches for that resolver runs
    /// once per statement instead (see <see cref="UncorrelatedSubqueryCache"/>).
    /// </summary>
    private sealed class ExistsExpression(Selection inner) : BooleanExpression
    {
        public override bool? Run(RuntimeContext runtime)
        {
            PermissionEnforcement.CheckSubqueryReads(runtime.Batch, inner);

            // Past the per-row threshold an equi-correlated body answers from
            // the key set its decorrelated plan built once; a NULL outer key
            // equi-matches nothing, so the body returns no row for it.
            if (SemiJoinProbe.Open(runtime, inner) is { } index
                && index.TryProbeKey(runtime, inner.SemiJoin!, out var key, out var keyHasNull))
            {
                return !keyHasNull && index.ContainsKey(key);
            }

            var memo = UncorrelatedSubqueryCache.Open(runtime, this);
            if (memo.Result is { } cached)
                return (bool)cached;

            var any = inner.Execute(runtime.Batch, memo.ResolverFor(runtime)).RowBytes.Any();
            memo.Remember(runtime, this, any);
            return any;
        }

        internal override string DebugDisplay() => "EXISTS (...)";

        // No top-level Expression operands — the subquery's references are
        // unreachable from this validator (and a subquery in inline CHECK
        // raises Msg 1046 in real SQL Server anyway).
        internal override void VisitOperandExpressions(Action<Expression> visitor) { }
    }

    /// <summary>
    /// <c>value [NOT] BETWEEN lower AND upper</c>: equivalent to
    /// <c>value &gt;= lower AND value &lt;= upper</c> (with the result negated
    /// for the NOT form). Both bounds inclusive (probe-confirmed against
    /// SQL Server 2025 — <c>5 between 1 and 5</c> is true at both endpoints).
    /// Reversed bounds (low &gt; high) produce a definite false; NULL in any
    /// operand position propagates per the standard three-valued AND/NOT
    /// truth tables. <c>value</c> is evaluated once per row even though the
    /// desugaring would suggest two evaluations.
    /// </summary>
    private sealed class BetweenExpression(Expression value, Expression lower, Expression upper, bool negated) : BooleanExpression
    {
        internal override bool ParallelSafe => this.OperandExpressionsParallelSafe;

        private readonly StringCoercionMemo subjectPromotion = new(), lowerPromotion = new(), upperPromotion = new();

        internal override bool IsWrittenConstant =>
            value.IsWrittenConstant && lower.IsWrittenConstant && upper.IsWrittenConstant;

        // A NULL-constant bound makes its half of the range UNKNOWN, so the
        // whole range is UNKNOWN or FALSE — never TRUE. The negated form can be
        // TRUE (real answers 'T' for `2 NOT BETWEEN NULL AND 1`), so it declines.
        internal override bool IsNeverTrue =>
            !negated && (Expression.IsNullConstant(lower) || Expression.IsNullConstant(upper));

        // The same UNKNOWN-or-FALSE range read through the negation: TRUE or
        // UNKNOWN, never FALSE. That is what makes the doubly-negated
        // `NOT x NOT BETWEEN NULL AND <bad>` never TRUE, which real settles
        // without evaluating the bound (probe-confirmed, both bound positions).
        internal override bool IsNeverFalse =>
            negated && (Expression.IsNullConstant(lower) || Expression.IsNullConstant(upper));

        public override bool? Run(RuntimeContext runtime)
        {
            var v = value.Run(runtime);
            var ge = CompareValuesPromoted(
                v, lower.Run(runtime), "greater than or equal to", static (l, r) => l.CompareTo(r) >= 0,
                this.subjectPromotion, this.lowerPromotion);
            // Real evaluates the range as the `value >= lower AND value <= upper`
            // it desugars to, left to right, and stops once the lower half
            // answers false — so `b BETWEEN 99999 AND b / 0` answers no rows
            // where `b BETWEEN 0 AND b / 0` is Msg 8134 (probe-confirmed, and
            // the same under NOT). An UNKNOWN lower half still needs the upper:
            // UNKNOWN AND FALSE is FALSE, which is what makes
            // `CHECK (x BETWEEN NULL AND 5)` reject x = 10 rather than pass it.
            var inRange = ge == false
                ? false
                : CompareValuesPromoted(
                    v, upper.Run(runtime), "less than or equal to", static (l, r) => l.CompareTo(r) <= 0,
                    this.subjectPromotion, this.upperPromotion) switch
                {
                    false => false,
                    true => ge,
                    _ => null,
                };
            return negated
                ? inRange switch { true => false, false => true, _ => null }
                : inRange;
        }

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            ConstantFolding.FoldsToNull(value, context)
                || (ConstantFolding.FoldsToNull(lower, context) && ConstantFolding.FoldsToNull(upper, context))
                ? FoldToUnknown(this)
                : this;

        internal override string DebugDisplay() =>
            $"{value.DebugDisplay()} {(negated ? "NOT BETWEEN" : "BETWEEN")} {lower.DebugDisplay()} AND {upper.DebugDisplay()}";

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(value);
            visitor(lower);
            visitor(upper);
        }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            // Same order Run compares in, so the lower bound's conflict is the
            // one reported (probe-confirmed: real names "greater than or equal
            // to" for a BETWEEN whose every operand conflicts).
            var valueType = value.GetSqlType(batch, resolveColumnType);
            RequireResolvableCollation(valueType, lower.GetSqlType(batch, resolveColumnType), "greater than or equal to");
            RequireResolvableCollation(valueType, upper.GetSqlType(batch, resolveColumnType), "less than or equal to");
        }

        internal override bool TryGetBetweenOperands([NotNullWhen(true)] out Expression? v, [NotNullWhen(true)] out Expression? lo, [NotNullWhen(true)] out Expression? hi)
        {
            // NOT BETWEEN is the non-contiguous complement (two open ranges), not
            // a single seekable range, so only the positive form exposes bounds.
            (v, lo, hi) = (value, lower, upper);
            return !negated;
        }
    }

    /// <summary>
    /// <c>expr [NOT] IN (SELECT ...)</c>: equivalent to a chain of
    /// promote-and-equal comparisons against each row of the single-column
    /// inner SELECT, OR-combined (or AND-of-not for <c>NOT IN</c>). NULL
    /// semantics mirror the literal-list <see cref="InExpression"/> exactly:
    /// NULL LHS → UNKNOWN; non-NULL LHS with a match → true (NULLs in the
    /// list don't change the answer); non-NULL LHS with no match but at
    /// least one NULL row → UNKNOWN; non-NULL LHS, no match, no NULL row →
    /// false. A correlated inner re-executes per outer row, threading the
    /// caller's resolver; one that never reads the outer row runs once per
    /// statement (see <see cref="UncorrelatedSubqueryCache"/>) and every later
    /// row probes the materialized values — through a hash set when the two
    /// sides promote within one type family, which turns the membership test
    /// from a scan of the inner result into a single lookup.
    /// </summary>
    private sealed class InSubqueryExpression(Expression source, Selection inner, bool negated) : BooleanExpression
    {
        public override bool? Run(RuntimeContext runtime)
        {
            var src = source.Run(runtime);

            PermissionEnforcement.CheckSubqueryReads(runtime.Batch, inner);

            // A NULL left side settles on the body's *emptiness* alone, not on
            // its values: `x IN (S)` is an OR of `x = s` over S, and an OR over
            // no elements is FALSE whatever x is, while one over a non-empty S
            // is UNKNOWN because every comparison against NULL is. Probed
            // against SQL Server 2025: `NULL IN (SELECT v FROM t WHERE 1 = 0)`
            // is FALSE (`NOT IN` TRUE) and the same over a non-empty body is
            // UNKNOWN — including per correlation key, so a NULL-keyed outer
            // row whose group is empty answers `NOT IN` TRUE.
            if (src.IsNull)
                return this.NullLeftSide(runtime, src.Type);

            // Past the per-row threshold an equi-correlated body answers from
            // the per-key value groups its decorrelated plan built once. A NULL
            // outer key — or one no inner row carries — makes the body's result
            // empty for this row, which is a definite miss however many NULLs
            // other keys' groups hold.
            if (inner.SemiJoin is { ProjectsValue: true } shape
                && SemiJoinProbe.Open(runtime, inner) is { } index
                && index.TryProbeKey(runtime, shape, out var key, out var keyHasNull))
            {
                return keyHasNull || !index.TryGetGroup(key, out var group)
                    ? negated
                    : this.TestGroup(group, src);
            }

            var memo = UncorrelatedSubqueryCache.Open(runtime, this);
            if (memo.Result is { } cached)
                return this.Test((InnerColumnValues)cached, src);
            if (memo.Probe is not { } probe)
                return this.ScanPerRow(runtime, src);

            var values = new InnerColumnValues(inner.Execute(runtime.Batch, probe.Resolver), src.Type);
            memo.Remember(runtime, this, values);
            return this.Test(values, src);
        }

        /// <summary>
        /// The answer for a NULL left side: <c>negated</c> (FALSE for
        /// <c>IN</c>, TRUE for <c>NOT IN</c>) when the body produced no row,
        /// UNKNOWN when it produced any. Routed through the same three sources
        /// of inner rows the value path uses — the decorrelated per-key index,
        /// the statement's materialized memo, and the per-row execution — so
        /// the transform and the memo answer identically here too.
        /// <para>
        /// The per-row execution reads one row rather than none, which is where
        /// this parts company with real: real needs the body's <em>shape</em>
        /// and not its values, so a projection that raises (<c>SELECT 1/0</c>)
        /// answers UNKNOWN there and raises here. Its <c>WHERE</c> raises on
        /// both, since emptiness can't be known without evaluating it.
        /// </para>
        /// </summary>
        private bool? NullLeftSide(RuntimeContext runtime, SqlType sourceType)
        {
            // Decorrelated: a key no inner row carries — a NULL-component key
            // included — selects nothing, which is the empty case. A key that
            // found a group selected at least one row, NULL projection or not.
            if (inner.SemiJoin is { ProjectsValue: true } shape
                && SemiJoinProbe.Open(runtime, inner) is { } index
                && index.TryProbeKey(runtime, shape, out var key, out var keyHasNull))
            {
                return keyHasNull || !index.TryGetGroup(key, out _) ? negated : null;
            }

            var memo = UncorrelatedSubqueryCache.Open(runtime, this);
            if (memo.Result is { } cached)
                return IsEmpty((InnerColumnValues)cached) ? negated : null;
            if (memo.Probe is not { } probe)
            {
                foreach (var _ in inner.Execute(runtime.Batch, runtime.ResolveColumn).RowBytes)
                    return null;
                return negated;
            }

            var values = new InnerColumnValues(inner.Execute(runtime.Batch, probe.Resolver), sourceType);
            memo.Remember(runtime, this, values);
            return IsEmpty(values) ? negated : null;
        }

        private static bool IsEmpty(InnerColumnValues values) => values.Values.Length == 0 && !values.SawNull;

        /// <summary>
        /// The per-outer-row execution a correlated inner takes: stream the
        /// inner result against this row's outer values, short-circuiting on
        /// the first match.
        /// </summary>
        private bool? ScanPerRow(RuntimeContext runtime, SqlValue src)
        {
            var sawNull = false;
            var resultSet = inner.Execute(runtime.Batch, runtime.ResolveColumn);
            var columns = RowDecoder.ColumnsFor(resultSet.Schema);
            foreach (var rowBytes in resultSet.RowBytes)
            {
                var rowValue = RowDecoder.DecodeColumn(columns, rowBytes, 0);
                if (rowValue.IsNull)
                {
                    sawNull = true;
                    continue;
                }
                if (CompareValuesPromoted(src, rowValue, "equal to", static (l, r) => l.Equals(r)) == true)
                    return !negated;
            }
            return sawNull ? null : negated;
        }

        /// <summary>
        /// Tests one LHS value against an already-materialized inner result:
        /// a hash lookup when the probe set covers this LHS type, otherwise the
        /// same promote-and-compare walk <see cref="ScanPerRow"/> runs.
        /// </summary>
        private bool? Test(InnerColumnValues values, SqlValue src)
        {
            if (values.Hashed is { } hashed && ReferenceEquals(values.HashedFor, src.Type))
            {
                // The scan reaches this through CompareValuesPromoted once per
                // value; the hashed path owes the same Msg 468 / 4191 gate.
                RequireResolvableCollation(src.Type, values.ColumnType, "equal to");
                return hashed.Contains(src.Type == values.Promoted ? src : src.CoerceTo(values.Promoted))
                    ? !negated
                    : values.SawNull ? null : negated;
            }

            foreach (var value in values.Values)
            {
                if (CompareValuesPromoted(src, value, "equal to", static (l, r) => l.Equals(r)) == true)
                    return !negated;
            }
            return values.SawNull ? null : negated;
        }

        /// <summary>
        /// Tests one LHS value against the inner rows the semi-join index holds
        /// under this row's correlation key — the same promote-and-compare walk
        /// <see cref="ScanPerRow"/> runs over the rows that key's correlated
        /// execution would have produced, including the NULL rule
        /// (<see cref="SemiJoinGroup.SawNull"/> is per key, so a NULL under some
        /// other key can't taint this one).
        /// </summary>
        private bool? TestGroup(SemiJoinGroup group, SqlValue src)
        {
            foreach (var value in group.Values)
            {
                if (CompareValuesPromoted(src, value, "equal to", static (l, r) => l.Equals(r)) == true)
                    return !negated;
            }
            return group.SawNull ? null : negated;
        }

        internal override string DebugDisplay() => $"{source.DebugDisplay()} {(negated ? "NOT IN" : "IN")} (...)";

        internal override bool TryGetSubqueryProbeSubject([NotNullWhen(true)] out Expression? subject)
        {
            subject = source;
            return !negated;
        }

        internal override bool TryMaterializeProbeFamily(
            BatchContext batch,
            Func<MultiPartName, SqlValue> outerResolver,
            SqlType subjectType,
            int cap,
            [NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
        {
            pairs = null;
            if (negated)
                return false;

            var runtime = new RuntimeContext(outerResolver, batch);
            var memo = UncorrelatedSubqueryCache.Open(runtime, this);
            InnerColumnValues values;
            if (memo.Result is { } cached)
            {
                values = (InnerColumnValues)cached;
            }
            else if (memo.Probe is { } probe)
            {
                try
                {
                    values = new InnerColumnValues(inner.Execute(runtime.Batch, probe.Resolver), subjectType);
                }
                catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
                {
                    // The body raises for this statement either way; let the
                    // per-row evaluation be what surfaces it, in row order.
                    return false;
                }

                memo.Remember(runtime, this, values);
                // A body that read the enclosing row (or drew a per-call-varying
                // built-in) produced values good for one row only.
                if (!probe.CanReplay(runtime))
                    return false;
            }
            else
            {
                // Already known to need per-row execution.
                return false;
            }

            if (values.Values.Length == 0 || values.Values.Length > cap)
                return false;

            pairs = new List<(Expression Left, Expression Right)>(values.Values.Length);
            foreach (var value in values.Values)
                pairs.Add((source, Value.NonLiteral(value)));
            return true;
        }

        // Only the LHS source is a reachable Expression operand; the subquery
        // side is a Selection (handled by its own machinery).
        internal override void VisitOperandExpressions(Action<Expression> visitor) => visitor(source);

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            RequireResolvableCollation(source.GetSqlType(batch, resolveColumnType), inner.Schema[0], "equal to");
    }

    /// <summary>
    /// The single-column rows of an <c>IN (SELECT …)</c> inner plan, decoded
    /// once and held for the statement's remaining outer rows. NULL rows fold
    /// into <see cref="SawNull"/> — they never match, and one of them is what
    /// turns a miss into UNKNOWN.
    /// </summary>
    private sealed class InnerColumnValues
    {
        /// <summary>Every non-NULL inner value, in the order the plan produced them.</summary>
        internal readonly SqlValue[] Values;

        /// <summary>Whether any inner row was NULL.</summary>
        internal readonly bool SawNull;

        /// <summary>The inner projection's declared type, which every entry in <see cref="Values"/> carries.</summary>
        internal readonly SqlType ColumnType;

        /// <summary>The type both sides compare under once <see cref="Hashed"/> applies.</summary>
        internal readonly SqlType Promoted;

        /// <summary>
        /// <see cref="Values"/> promoted to <see cref="Promoted"/> and hashed
        /// for O(1) membership, or <see langword="null"/> when the pair doesn't
        /// qualify (see <see cref="QualifiesForHashing"/>) — in which case the
        /// caller walks <see cref="Values"/> instead.
        /// </summary>
        internal readonly HashSet<SqlValue>? Hashed;

        /// <summary>The LHS type <see cref="Hashed"/> was built against; an LHS of any other type falls back to the walk.</summary>
        internal readonly SqlType? HashedFor;

        internal InnerColumnValues(SimulatedSqlResultSet resultSet, SqlType sourceType)
        {
            this.ColumnType = resultSet.Schema[0];
            var columns = RowDecoder.ColumnsFor(resultSet.Schema);
            var values = new List<SqlValue>();
            foreach (var rowBytes in resultSet.RowBytes)
            {
                var value = RowDecoder.DecodeColumn(columns, rowBytes, 0);
                if (value.IsNull)
                    this.SawNull = true;
                else
                    values.Add(value);
            }

            this.Values = [.. values];
            this.Promoted = this.ColumnType;
            if (this.Values.Length == 0 || !QualifiesForHashing(sourceType, this.ColumnType))
                return;

            this.Promoted = SqlType.Promote(sourceType, this.ColumnType);
            this.Hashed = BuildProbeSet(this.Values, this.Promoted);
            if (this.Hashed is not null)
                this.HashedFor = sourceType;
        }

        /// <summary>
        /// Whether the two sides can be hashed against each other: promotion has
        /// to stay inside one type family, so that coercing a value to the
        /// common type is a widening rather than the value-dependent conversion
        /// a cross-family pair (<c>int</c> against <c>varchar</c>) would run —
        /// which the row-order walk has to keep raising where it raises today.
        /// The <see cref="SqlTypeCategory.Other"/> family is excluded outright:
        /// <c>sql_variant</c> carries its own per-value type, and the binary
        /// types compare across declared lengths that their hash doesn't share.
        /// </summary>
        private static bool QualifiesForHashing(SqlType source, SqlType column) =>
            source.Category == column.Category
                && source.Category != SqlTypeCategory.Other
                && !source.IsLob
                && !column.IsLob;

        /// <summary>
        /// Hashes every value at <paramref name="promoted"/>, or gives up
        /// (returning <see langword="null"/>) if any value won't sit there —
        /// a value the promoted type can't hold has to raise where the
        /// row-order walk would raise it, not while a probe set is being built.
        /// </summary>
        private static HashSet<SqlValue>? BuildProbeSet(SqlValue[] values, SqlType promoted)
        {
            var set = new HashSet<SqlValue>(values.Length);
            foreach (var value in values)
            {
                SqlValue coerced;
                try
                {
                    coerced = value.Type == promoted ? value : value.CoerceTo(promoted);
                }
                catch (SimulatedSqlException)
                {
                    return null;
                }

                // Equality and hash both key on the type instance, so a
                // coercion that landed somewhere other than the promoted type
                // would probe against a set it can't match.
                if (coerced.Type != promoted)
                    return null;
                _ = set.Add(coerced);
            }
            return set;
        }
    }

    /// <summary>
    /// Quantified-subquery comparison: <c>lhs op {ANY|SOME|ALL} (SELECT col FROM ...)</c>.
    /// Probe-confirmed (2026-05-13) semantics:
    /// <list type="bullet">
    /// <item><c>ALL</c> over the empty subquery is vacuously <c>true</c>;
    /// <c>ANY</c> over the empty subquery is vacuously <c>false</c>. Both ignore
    /// LHS NULL when the inner is empty.</item>
    /// <item>Once at least one inner row exists, NULL on either side of any
    /// per-row comparison turns that row's result into UNKNOWN.</item>
    /// <item><c>ALL</c>: if any row's comparison is <c>false</c>, result is
    /// <c>false</c>; otherwise UNKNOWN if any was NULL-tainted, else <c>true</c>.</item>
    /// <item><c>ANY</c> / <c>SOME</c>: if any row's comparison is <c>true</c>,
    /// result is <c>true</c>; otherwise UNKNOWN if any was NULL-tainted, else
    /// <c>false</c>.</item>
    /// </list>
    /// </summary>
    private sealed class QuantifiedComparisonExpression(Expression left, ComparisonOp op, QuantifiedKind kind, Selection inner) : BooleanExpression
    {
        public override bool? Run(RuntimeContext runtime)
        {
            var lhs = left.Run(runtime);
            var (operatorName, compare) = GetComparator(op);

            PermissionEnforcement.CheckSubqueryReads(runtime.Batch, inner);
            var memo = UncorrelatedSubqueryCache.Open(runtime, this);
            if (memo.Result is { } cached)
                return this.Combine((SqlValue[])cached, lhs, operatorName, compare);

            var resultSet = inner.Execute(runtime.Batch, memo.ResolverFor(runtime));
            var columns = RowDecoder.ColumnsFor(resultSet.Schema);
            var values = new List<SqlValue>();
            foreach (var rowBytes in resultSet.RowBytes)
                values.Add(RowDecoder.DecodeColumn(columns, rowBytes, 0));

            var materialized = values.ToArray();
            memo.Remember(runtime, this, materialized);
            return this.Combine(materialized, lhs, operatorName, compare);
        }

        /// <summary>
        /// Folds the per-row comparisons into the quantifier's answer. NULL
        /// inner values reach <see cref="CompareValuesPromoted"/> like any
        /// other, since it is their UNKNOWN result that taints the fold.
        /// </summary>
        private bool? Combine(SqlValue[] values, SqlValue lhs, string operatorName, Func<SqlValue, SqlValue, bool> compare)
        {
            var sawUnknown = false;
            var sawDefinitiveTrue = false;
            var sawDefinitiveFalse = false;

            foreach (var value in values)
            {
                var perRow = CompareValuesPromoted(lhs, value, operatorName, compare);
                if (perRow == true)
                    sawDefinitiveTrue = true;
                else if (perRow == false)
                    sawDefinitiveFalse = true;
                else
                    sawUnknown = true;
            }

            return values.Length == 0
                ? kind == QuantifiedKind.All
                : kind == QuantifiedKind.All
                    ? (sawDefinitiveFalse ? false : sawUnknown ? null : true)
                    : (sawDefinitiveTrue ? true : sawUnknown ? null : false);
        }

        internal override string DebugDisplay()
        {
            var opText = op switch
            {
                ComparisonOp.Equal => "=",
                ComparisonOp.NotEqual => "<>",
                ComparisonOp.Less => "<",
                ComparisonOp.LessOrEqual => "<=",
                ComparisonOp.Greater => ">",
                _ => ">=",
            };
            var kindText = kind == QuantifiedKind.All ? "ALL" : "ANY";
            return $"{left.DebugDisplay()} {opText} {kindText} (...)";
        }

        internal override void VisitOperandExpressions(Action<Expression> visitor) => visitor(left);

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            RequireResolvableCollation(left.GetSqlType(batch, resolveColumnType), inner.Schema[0], GetComparator(op).OperatorName);

        private static (string OperatorName, Func<SqlValue, SqlValue, bool> Compare) GetComparator(ComparisonOp op) => op switch
        {
            ComparisonOp.Equal => ("equal to", static (l, r) => l.Equals(r)),
            ComparisonOp.NotEqual => ("not equal to", static (l, r) => !l.Equals(r)),
            ComparisonOp.Less => ("less than", static (l, r) => l.CompareTo(r) < 0),
            ComparisonOp.LessOrEqual => ("less than or equal to", static (l, r) => l.CompareTo(r) <= 0),
            ComparisonOp.Greater => ("greater than", static (l, r) => l.CompareTo(r) > 0),
            _ => ("greater than or equal to", static (l, r) => l.CompareTo(r) >= 0),
        };
    }

    /// <summary>
    /// Three-valued <c>NOT</c>: <c>NOT true = false</c>, <c>NOT false = true</c>,
    /// <c>NOT NULL = NULL</c>. The NULL pass-through is what makes
    /// <c>WHERE NOT (col = X)</c> exclude NULL rows in SQL Server (NULL
    /// becomes UNKNOWN; UNKNOWN excludes from WHERE).
    /// </summary>
    private sealed class NotExpression(BooleanExpression inner) : BooleanExpression
    {
        internal override bool IsWrittenConstant => inner.IsWrittenConstant;

        // Three-valued NOT swaps the two verdicts and leaves UNKNOWN alone, so
        // each side reads the other's off the operand.
        internal override bool IsNeverTrue => inner.IsNeverFalse;

        internal override bool IsNeverFalse => inner.IsNeverTrue;

        public override bool? Run(RuntimeContext runtime) => inner.Run(runtime) switch
        {
            true => false,
            false => true,
            null => null,
        };

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            new NotExpression(inner.SettleFoldedNullComparisons(context));

        internal override string DebugDisplay() => $"NOT {inner.DebugDisplay()}";

        internal override bool ParallelSafe => inner.ParallelSafe;

        internal override void VisitOperandExpressions(Action<Expression> visitor) => inner.VisitOperandExpressions(visitor);

        // A fold under a NOT still took its operands out of the tree — real
        // reports nothing for the ungrouped `b` in
        // `HAVING NOT (1 = 0 AND b > 1)`.
        internal override void VisitSurvivingOperandExpressions(Action<Expression> visitor) =>
            inner.VisitSurvivingOperandExpressions(visitor);

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => inner.Bind(batch, resolveColumnType);
    }

    /// <summary>
    /// Common base for the binary-comparison subclasses (=, &lt;&gt;, &lt;,
    /// &gt;, &lt;=, &gt;=, LIKE). Holds the parsed left/right
    /// <see cref="Expression"/>s and the shared promote-and-compare helper;
    /// non-comparison <see cref="BooleanExpression"/>s (currently
    /// <see cref="AndExpression"/> and <see cref="LikeExpression"/>) bring
    /// their own field shapes.
    /// </summary>
    private abstract class CompareExpression : BooleanExpression
    {
        internal override bool ParallelSafe => this.OperandExpressionsParallelSafe;

        protected readonly Expression left, right;
        private readonly StringCoercionMemo leftPromotion = new(), rightPromotion = new();

        private protected CompareExpression(Expression left, Expression right)
        {
            this.left = left;
            this.right = right;
        }

        /// <summary>
        /// Evaluates both sides, applies SQL Server type promotion to a common
        /// type, and invokes the comparator. NULL operands return <c>null</c>
        /// (the SQL UNKNOWN result of comparing with NULL) — callers decide
        /// what UNKNOWN means in their context. Cross-category type pairs
        /// surface as <see cref="NotSupportedException"/> via
        /// <see cref="SqlType.Promote"/>. LOB-typed operands (<c>text</c>,
        /// <c>ntext</c>, <c>image</c>) raise Msg 402 rather than being routed
        /// through promotion — SQL Server rejects them in any comparison /
        /// equality slot, and <see cref="OperatorName"/> is woven into the
        /// message.
        /// </summary>
        protected bool? ComparePromoted(RuntimeContext runtime, Func<SqlValue, SqlValue, bool> compare) =>
            CompareValuesPromoted(
                this.left.Run(runtime),
                this.right.Run(runtime),
                this.OperatorName,
                compare,
                this.leftPromotion,
                this.rightPromotion);

        /// <summary>
        /// The name real SQL Server weaves into a Msg 402 / Msg 468 raised
        /// from this comparison ("equal to", "less than", "like", …). One
        /// declaration per subclass keeps the runtime comparator and the
        /// compile-time <see cref="Bind"/> naming the same operator.
        /// </summary>
        protected abstract string OperatorName { get; }

        internal override bool IsWrittenConstant => this.left.IsWrittenConstant && this.right.IsWrittenConstant;

        internal override BooleanExpression SettleFoldedNullComparisons(ParserContext context) =>
            ConstantFolding.FoldsToNull(this.left, context) || ConstantFolding.FoldsToNull(this.right, context)
                ? FoldToUnknown(this)
                : this;

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(this.left);
            visitor(this.right);
        }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            BindComparison(this.left, this.right, batch, resolveColumnType, this.OperatorName);

        // The space-free operator token this comparison renders into a filtered-
        // index definition (=, <>, >, >=, <, <=), or null for shapes with no
        // canonical filter rendering (LIKE) — those bail the whole render.
        protected virtual string? FilterOperator => null;

        private protected override bool TryAppendFilterDefinition(StringBuilder sb, BatchContext batch)
        {
            if (this.FilterOperator is not { } op || !TryAppendFilterOperand(sb, this.left, batch))
                return false;
            _ = sb.Append(op);
            return TryAppendFilterOperand(sb, this.right, batch);
        }
    }

    /// <summary>
    /// Value-level promote-and-compare shared by <see cref="CompareExpression"/>
    /// (post-Run) and <see cref="InExpression"/> (which evaluates each
    /// list-element separately and short-circuits on the first match).
    /// Same NULL / LOB / promotion rules as the
    /// <see cref="CompareExpression.ComparePromoted"/> path.
    /// </summary>
    internal static bool? CompareValuesPromoted(
        SqlValue l,
        SqlValue r,
        string operatorName,
        Func<SqlValue, SqlValue, bool> compare,
        StringCoercionMemo? leftMemo = null,
        StringCoercionMemo? rightMemo = null)
    {
        // Either operand sql_variant: real converts a base-typed side UP to
        // sql_variant (probe-confirmed as CONVERT_IMPLICIT(sql_variant, …) in
        // the plan) and compares by datatype-family rank then value within the
        // family — so a string variant is less than any exact-numeric value
        // and never equal to it, cross-family comparison is value-blind, and
        // no comparison error is possible (variant nvarchar 'abc' vs int 5 is
        // simply 'lt', never Msg 245). The SqlValue variant arms implement
        // the family rules; wrap the base side and apply the operator to the
        // pair.
        if (l.Type is SqlVariantSqlType || r.Type is SqlVariantSqlType)
        {
            if (l.IsNull || r.IsNull)
                return null;
            if (l.Type.IsLob || r.Type.IsLob)
                throw SimulatedSqlException.IncompatibleDataTypesInOperator(l.Type, r.Type, operatorName);
            if (l.Type is not SqlVariantSqlType)
                l = SqlValue.FromVariant(l);
            if (r.Type is not SqlVariantSqlType)
                r = SqlValue.FromVariant(r);
            return compare(l, r);
        }

        if (l.Type.IsLob || r.Type.IsLob)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(l.Type, r.Type, operatorName);

        // Cross-collation operand pair: pick the higher-coercibility side's
        // collation; same rank but different collation raises Msg 468. The
        // conflict is normally caught at parse by <see cref="Bind"/>; this
        // per-value repeat covers the operand pairs a static type can't
        // predict (a sql_variant, a value whose type the runtime narrowed).
        // It sits ahead of the NULL short-circuit so the two phases agree.
        RequireResolvableCollation(l.Type, r.Type, operatorName);

        if (l.IsNull || r.IsNull)
            return null;

        if (l.Type == r.Type)
            return compare(l, r);

        // A written operand — a literal date, a parameter — is the same string
        // instance on every row, so the promotion coercion its side needs is one
        // call memoized rather than one parse per row; see
        // <see cref="StringCoercionMemo"/> for why identity is the right key and
        // why a missing memo just means the old behavior.
        var common = SqlType.Promote(l.Type, r.Type);
        return compare(
            leftMemo is null ? l.CoerceTo(common) : leftMemo.Coerce(l, common),
            rightMemo is null ? r.CoerceTo(common) : rightMemo.Coerce(r, common));
    }

    private sealed class EqualityExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} = {right.DebugDisplay()}";

        protected override string OperatorName => "equal to";

        protected override string? FilterOperator => "=";

        internal override bool TryGetEqualityOperands([NotNullWhen(true)] out Expression? l, [NotNullWhen(true)] out Expression? r)
        {
            l = left;
            r = right;
            return true;
        }
    }

    private sealed class InequalityExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => !l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <> {right.DebugDisplay()}";

        protected override string OperatorName => "not equal to";

        protected override string? FilterOperator => "<>";
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => l.CompareTo(r) > 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} > {right.DebugDisplay()}";

        protected override string OperatorName => "greater than";

        protected override string? FilterOperator => ">";

        internal override bool TryGetRangeOperands([NotNullWhen(true)] out Expression? l, out RangeComparison op, [NotNullWhen(true)] out Expression? r)
        {
            (l, op, r) = (left, RangeComparison.Greater, right);
            return true;
        }
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => l.CompareTo(r) >= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} >= {right.DebugDisplay()}";

        protected override string OperatorName => "greater than or equal to";

        protected override string? FilterOperator => ">=";

        internal override bool TryGetRangeOperands([NotNullWhen(true)] out Expression? l, out RangeComparison op, [NotNullWhen(true)] out Expression? r)
        {
            (l, op, r) = (left, RangeComparison.GreaterOrEqual, right);
            return true;
        }
    }

    private sealed class LessThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => l.CompareTo(r) < 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} < {right.DebugDisplay()}";

        protected override string OperatorName => "less than";

        protected override string? FilterOperator => "<";

        internal override bool TryGetRangeOperands([NotNullWhen(true)] out Expression? l, out RangeComparison op, [NotNullWhen(true)] out Expression? r)
        {
            (l, op, r) = (left, RangeComparison.Less, right);
            return true;
        }
    }

    private sealed class LessThanOrEqualExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(runtime, static (l, r) => l.CompareTo(r) <= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <= {right.DebugDisplay()}";

        protected override string OperatorName => "less than or equal to";

        protected override string? FilterOperator => "<=";

        internal override bool TryGetRangeOperands([NotNullWhen(true)] out Expression? l, out RangeComparison op, [NotNullWhen(true)] out Expression? r)
        {
            (l, op, r) = (left, RangeComparison.LessOrEqual, right);
            return true;
        }
    }

    /// <summary>
    /// SQL Server <c>LIKE</c> / <c>NOT LIKE</c> with optional <c>ESCAPE</c>
    /// clause. Pattern compilation and matching flow through
    /// <see cref="LikeMatcher"/> (shared with <c>PATINDEX</c>), behind this
    /// node's own <see cref="LikeMatcher.Cache"/> so a per-row predicate
    /// compiles its pattern once rather than once per row. The match reads the
    /// resolved collation's full comparison semantics — case, accent, kana and
    /// width — and the trailing-space rule the operand types decide; wildcards
    /// inside <c>[...]</c> classes are taken literally, and <c>[]</c> and an
    /// unterminated <c>[</c> never match, mirroring SQL Server's silent
    /// failure.
    /// </summary>
    private sealed class LikeExpression(Expression left, Expression right, Expression? escape, bool negated) : CompareExpression(left, right)
    {
        private readonly LikeMatcher.Cache patterns = new(forPatIndex: false);
        private readonly Expression? escape = escape;
        private readonly bool negated = negated;

        public override bool? Run(RuntimeContext runtime)
        {
            var l = left.Run(runtime);
            var r = right.Run(runtime);
            if (l.IsNull || r.IsNull)
                return null;

            // A non-string operand is implicitly converted to varchar, matching
            // real SQL Server (probe-confirmed: int / decimal / float / date /
            // datetime / uniqueidentifier / bit all LIKE-match their default
            // string form — `[pub_date] LIKE '2024%'` is what an ORM emits).
            // CoerceTo raises the appropriate conversion error for a type with
            // no varchar representation.
            if (l.Type.Category != SqlTypeCategory.String)
                l = l.CoerceTo(SqlType.Varchar);
            if (r.Type.Category != SqlTypeCategory.String)
                r = r.CoerceTo(SqlType.Varchar);

            char? escapeChar = null;
            if (this.escape is not null)
            {
                var e = this.escape.Run(runtime);
                if (e.IsNull)
                    return null;
                if (e.Type.Category != SqlTypeCategory.String)
                    throw SimulatedSqlException.OperandTypeClash(l.Type, e.Type);
                var s = e.AsString;
                if (s.Length != 1)
                    throw SimulatedSqlException.InvalidEscapeCharacter(s);
                escapeChar = s[0];
            }

            // Resolve the effective collation from each operand's runtime
            // type (column refs carry Implicit-rank collation; COLLATE
            // postfix yields Explicit). Same-rank conflict raises Msg 468
            // (probe-confirmed verbatim wording, "like" as the operator
            // name); higher rank wins otherwise.
            UnresolvedCollation.Require(l.Type, r.Type, this.OperatorName);
            var resolved = Collation.Resolve(l.Type, r.Type)
                ?? throw SimulatedSqlException.CollationConflict(
                    r.Type.Collation!.Name,
                    l.Type.Collation!.Name,
                    this.OperatorName);

            // Trailing-space slack is the non-Unicode family's alone: real
            // accepts a varchar subject's leftover U+0020 and refuses an
            // nvarchar one's, and a single nvarchar operand decides it for the
            // pair (probe-confirmed both ways, and through a char / nchar
            // column's own ANSI padding).
            var slack = !SqlType.IsNationalStringCategory(l.Type) && !SqlType.IsNationalStringCategory(r.Type);
            var matched = this.patterns.Get(r.AsString, escapeChar, resolved.Collation).IsMatch(l.AsString, slack);
            return matched ^ this.negated;
        }

        internal override string DebugDisplay() => this.escape is null
            ? $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()}"
            : $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()} ESCAPE {this.escape.DebugDisplay()}";

        protected override string OperatorName => "like";

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(this.left);
            visitor(this.right);
            if (this.escape is not null)
                visitor(this.escape);
        }

        internal override void Bind(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        {
            base.Bind(batch, resolveColumnType);
            _ = this.escape?.GetSqlType(batch, resolveColumnType);
        }
    }
}
