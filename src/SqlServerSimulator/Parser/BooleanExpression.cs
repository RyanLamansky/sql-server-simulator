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
        return new OrExpression([.. operands]);
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
        return new AndExpression([.. operands]);
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
    /// != !&lt; !&gt; LIKE IS IN BETWEEN NOT + - * / % &amp; | ^) flips
    /// into the value-LHS path (<see cref="Expression.Parse"/> handles
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
            var inner = ParseOr(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional(); // closing `)` is the predicate's last meaningful token; what follows may be end-of-input
            return inner;
        }
        return context.Token switch
        {
            // Full-text predicates aren't modeled — raise NotSupportedException
            // at parse time so apps see a loud failure rather than a silent
            // miss. See [docs/claude/full-text.md] for skip-with-diagnostic.
            ReservedKeyword { Keyword: Keyword.Contains or Keyword.FreeText } predicate
                => throw new NotSupportedException(
                    $"Full-text search predicates ({predicate.Keyword.ToString().ToUpperInvariant()}) are not modeled."),
            ReservedKeyword { Keyword: Keyword.Exists } => ParseExists(context),
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
                        var isValueLhs = context.Token is Operator { Character: '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '&' or '|' or '^' }
                            or ReservedKeyword { Keyword: Keyword.Like or Keyword.Is or Keyword.In or Keyword.Between or Keyword.Not };
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
        var inner = Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
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
            // The "near 'X'" suffix is the current token (the one that should have
            // been a comparison op); for paren-wrapped value cases like
            // `IF (1) select`, real SQL Server reports the post-paren token where
            // the simulator reports the in-paren token, a minor positional gap.
            default:
                throw SimulatedSqlException.NonBooleanInConditionContext(context.Token);
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
            var inner = Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
            if (inner.Schema.Length != 1)
                throw SimulatedSqlException.SubqueryNotIntroducedWithExists();
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return new QuantifiedComparisonExpression(left, op, kind, inner);
        }

        // Regular comparison: RHS is a value expression.
        var right = Expression.Parse(context);
        return op switch
        {
            ComparisonOp.Equal => new EqualityExpression(left, right),
            ComparisonOp.NotEqual => new InequalityExpression(left, right),
            ComparisonOp.Less => new LessThanExpression(left, right),
            ComparisonOp.LessOrEqual => new LessThanOrEqualExpression(left, right),
            ComparisonOp.Greater => new GreaterThanExpression(left, right),
            _ => new GreaterThanOrEqualExpression(left, right),
        };
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
            var inner = Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
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
        return new InExpression(left, [.. candidates], negated);
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
    private static BetweenExpression ParseBetween(Expression left, ParserContext context, bool negated)
    {
        context.MoveNextRequired();
        var lower = Expression.Parse(context);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.And })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var upper = Expression.Parse(context);
        return new BetweenExpression(left, lower, upper, negated);
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

        internal override string DebugDisplay() => string.Join(" OR ", operands.Select(o => o.DebugDisplay()));

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            foreach (var operand in operands)
                operand.VisitOperandExpressions(visitor);
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
    private sealed class InExpression(Expression source, Expression[] candidates, bool negated) : BooleanExpression
    {
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
    /// up the chain.
    /// </summary>
    private sealed class ExistsExpression(Selection inner) : BooleanExpression
    {
        public override bool? Run(RuntimeContext runtime) =>
            inner.Execute(runtime.Batch, runtime.ResolveColumn).RowBytes.Any();

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
        public override bool? Run(RuntimeContext runtime)
        {
            var v = value.Run(runtime);
            var lo = lower.Run(runtime);
            var hi = upper.Run(runtime);
            var ge = CompareValuesPromoted(v, lo, "greater than or equal to", static (l, r) => l.CompareTo(r) >= 0);
            var le = CompareValuesPromoted(v, hi, "less than or equal to", static (l, r) => l.CompareTo(r) <= 0);
            var inRange = ge == false || le == false ? false
                : ge == true && le == true ? true
                : (bool?)null;
            return negated
                ? inRange switch { true => false, false => true, _ => null }
                : inRange;
        }

        internal override string DebugDisplay() =>
            $"{value.DebugDisplay()} {(negated ? "NOT BETWEEN" : "BETWEEN")} {lower.DebugDisplay()} AND {upper.DebugDisplay()}";

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(value);
            visitor(lower);
            visitor(upper);
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
    /// false. Re-executes the inner plan per outer row, threading the
    /// caller's resolver for correlated references.
    /// </summary>
    private sealed class InSubqueryExpression(Expression source, Selection inner, bool negated) : BooleanExpression
    {
        public override bool? Run(RuntimeContext runtime)
        {
            var src = source.Run(runtime);
            if (src.IsNull)
                return null;

            var sawNull = false;
            var resultSet = inner.Execute(runtime.Batch, runtime.ResolveColumn);
            foreach (var rowBytes in resultSet.RowBytes)
            {
                var rowValue = RowDecoder.DecodeColumn(resultSet.Schema, rowBytes, 0);
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

        internal override string DebugDisplay() => $"{source.DebugDisplay()} {(negated ? "NOT IN" : "IN")} (...)";

        // Only the LHS source is a reachable Expression operand; the subquery
        // side is a Selection (handled by its own machinery).
        internal override void VisitOperandExpressions(Action<Expression> visitor) => visitor(source);
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

            var anyRow = false;
            var sawUnknown = false;
            var sawDefinitiveTrue = false;
            var sawDefinitiveFalse = false;

            var resultSet = inner.Execute(runtime.Batch, runtime.ResolveColumn);
            foreach (var rowBytes in resultSet.RowBytes)
            {
                anyRow = true;
                var rowValue = RowDecoder.DecodeColumn(resultSet.Schema, rowBytes, 0);

                var perRow = CompareValuesPromoted(lhs, rowValue, operatorName, compare);
                if (perRow == true)
                    sawDefinitiveTrue = true;
                else if (perRow == false)
                    sawDefinitiveFalse = true;
                else
                    sawUnknown = true;
            }

            return !anyRow
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
        public override bool? Run(RuntimeContext runtime) => inner.Run(runtime) switch
        {
            true => false,
            false => true,
            null => null,
        };

        internal override string DebugDisplay() => $"NOT {inner.DebugDisplay()}";

        internal override void VisitOperandExpressions(Action<Expression> visitor) => inner.VisitOperandExpressions(visitor);
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
        protected readonly Expression left, right;

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
        /// equality slot, and the operator name is woven into the message via
        /// the caller-supplied <paramref name="operatorName"/>.
        /// </summary>
        protected static bool? ComparePromoted(Expression left, Expression right, RuntimeContext runtime, string operatorName, Func<SqlValue, SqlValue, bool> compare) =>
            CompareValuesPromoted(left.Run(runtime), right.Run(runtime), operatorName, compare);

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(this.left);
            visitor(this.right);
        }

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
    internal static bool? CompareValuesPromoted(SqlValue l, SqlValue r, string operatorName, Func<SqlValue, SqlValue, bool> compare)
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
        // check fires at bind time in real SQL Server (before NULL operand
        // short-circuits); the simulator mirrors that ordering so a
        // NULL-bearing row in a mixed-collation join surfaces the conflict
        // rather than silently filtering. Same-type pairs already share a
        // collation by virtue of the SqlType being interned per-collation.
        if (l.Type != r.Type && l.Type.Category == SqlTypeCategory.String && r.Type.Category == SqlTypeCategory.String
            && Collation.Resolve(l.Type, r.Type) is null)
        {
            // Probe-confirmed wording order: right operand's collation first,
            // left operand's collation second.
            throw SimulatedSqlException.CollationConflict(
                r.Type.Collation!.Name,
                l.Type.Collation!.Name,
                operatorName);
        }

        if (l.IsNull || r.IsNull)
            return null;

        if (l.Type == r.Type)
            return compare(l, r);

        var common = SqlType.Promote(l.Type, r.Type);
        return compare(l.CoerceTo(common), r.CoerceTo(common));
    }

    private sealed class EqualityExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(left, right, runtime, "equal to", static (l, r) => l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} = {right.DebugDisplay()}";

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
            ComparePromoted(left, right, runtime, "not equal to", static (l, r) => !l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <> {right.DebugDisplay()}";

        protected override string? FilterOperator => "<>";
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(RuntimeContext runtime) =>
            ComparePromoted(left, right, runtime, "greater than", static (l, r) => l.CompareTo(r) > 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} > {right.DebugDisplay()}";

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
            ComparePromoted(left, right, runtime, "greater than or equal to", static (l, r) => l.CompareTo(r) >= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} >= {right.DebugDisplay()}";

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
            ComparePromoted(left, right, runtime, "less than", static (l, r) => l.CompareTo(r) < 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} < {right.DebugDisplay()}";

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
            ComparePromoted(left, right, runtime, "less than or equal to", static (l, r) => l.CompareTo(r) <= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <= {right.DebugDisplay()}";

        protected override string? FilterOperator => "<=";

        internal override bool TryGetRangeOperands([NotNullWhen(true)] out Expression? l, out RangeComparison op, [NotNullWhen(true)] out Expression? r)
        {
            (l, op, r) = (left, RangeComparison.LessOrEqual, right);
            return true;
        }
    }

    /// <summary>
    /// SQL Server <c>LIKE</c> / <c>NOT LIKE</c> with optional <c>ESCAPE</c>
    /// clause. Pattern translation flows through <see cref="LikePatternBuilder"/>
    /// (shared with <c>PATINDEX</c>); the resulting regex is rebuilt per
    /// evaluation, pre-compilation/caching is left for later. Trailing-space
    /// behavior (subject's leftover U+0020 spaces accepted but pattern's must
    /// match) is encoded by anchoring the regex with <c>[ ]*$</c>. Wildcards
    /// inside <c>[...]</c> classes are taken literally; <c>[]</c> and reversed
    /// ranges (<c>[c-a]</c>) and unterminated <c>[</c> all produce never-match
    /// translations to mirror SQL Server's silent failure.
    /// </summary>
    private sealed class LikeExpression(Expression left, Expression right, Expression? escape, bool negated) : CompareExpression(left, right)
    {
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
            var resolved = Collation.Resolve(l.Type, r.Type)
                ?? throw SimulatedSqlException.CollationConflict(
                    r.Type.Collation!.Name,
                    l.Type.Collation!.Name,
                    "like");

            var matched = LikePatternBuilder.BuildAnchored(r.AsString, escapeChar, resolved.Collation.CaseSensitive).IsMatch(l.AsString);
            return matched ^ this.negated;
        }

        internal override string DebugDisplay() => this.escape is null
            ? $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()}"
            : $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()} ESCAPE {this.escape.DebugDisplay()}";

        internal override void VisitOperandExpressions(Action<Expression> visitor)
        {
            visitor(this.left);
            visitor(this.right);
            if (this.escape is not null)
                visitor(this.escape);
        }
    }
}
