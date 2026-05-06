using System.Diagnostics;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlServerSimulator.Parser;

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
        var result = ParseAnd(context);
        while (context.Token is ReservedKeyword { Keyword: Keyword.Or })
        {
            context.MoveNextRequired();
            result = new OrExpression(result, ParseAnd(context));
        }
        return result;
    }

    /// <summary>
    /// Mid-precedence level: zero or more <c>AND</c>-separated
    /// <see cref="ParseNot"/> chains.
    /// </summary>
    private static BooleanExpression ParseAnd(ParserContext context)
    {
        var result = ParseNot(context);
        while (context.Token is ReservedKeyword { Keyword: Keyword.And })
        {
            context.MoveNextRequired();
            result = new AndExpression(result, ParseNot(context));
        }
        return result;
    }

    /// <summary>
    /// Highest-precedence boolean combinator: a sequence of <c>NOT</c>
    /// prefixes wrapping a single atom. Stacking is allowed
    /// (<c>NOT NOT predicate</c>) — each layer adds a
    /// <see cref="NotExpression"/>.
    /// </summary>
    private static BooleanExpression ParseNot(ParserContext context)
    {
        if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
        {
            context.MoveNextRequired();
            return new NotExpression(ParseNot(context));
        }
        return ParseAtom(context);
    }

    /// <summary>
    /// Either a parenthesized sub-predicate, an <c>EXISTS (SELECT ...)</c>
    /// subquery, or a single comparison. A leading <c>(</c> at the atom
    /// level is unambiguously treated as a boolean group: the body is
    /// recursively parsed as a full predicate via <see cref="ParseOr"/> and
    /// the closing <c>)</c> is required. Arithmetic parens still work inside
    /// an expression operand (e.g. <c>where col = (a + 1)</c>) — the
    /// right-hand side hits <see cref="Expression.Parse"/> which has its own
    /// <c>Parenthesized</c> dispatch. The pattern that doesn't survive this
    /// choice is <c>where (arith) cmp rhs</c>; SQL Server accepts that, the
    /// simulator surfaces it as a syntax error.
    /// </summary>
    private static BooleanExpression ParseAtom(ParserContext context)
    {
        if (context.Token is Operator { Character: '(' })
        {
            context.MoveNextRequired();
            var inner = ParseOr(context);
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional(); // closing `)` is the predicate's last meaningful token; what follows may be end-of-input
            return inner;
        }
        return context.Token is ReservedKeyword { Keyword: Keyword.Exists }
            ? ParseExists(context)
            : ParseComparison(Expression.Parse(context), context);
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
        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Select })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var inner = Selection.Parse(context, depth: 1, outerTypeResolver: context.OuterTypeResolver);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new ExistsExpression(inner);
    }

    /// <summary>
    /// Parses a single comparison: equality, inequality, ordered comparison,
    /// or LIKE/NOT LIKE. Caller must have already parsed the left side.
    /// </summary>
    private static BooleanExpression ParseComparison(Expression left, ParserContext context) => context.Token switch
    {
        Operator { Character: '=' } => new EqualityExpression(left, context),
        Operator { Character: '>' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new GreaterThanOrEqualExpression(left, context),
            _ => new GreaterThanExpression(left, Expression.Parse(context))
        },
        Operator { Character: '<' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new LessThanOrEqualExpression(left, context),
            Operator { Character: '>' } => new InequalityExpression(left, context),
            _ => new LessThanExpression(left, Expression.Parse(context)),
        },
        Operator { Character: '!' } => context.GetNextRequired() switch
        {
            Operator { Character: '=' } => new InequalityExpression(left, context),
            Operator { Character: '>' } => new LessThanOrEqualExpression(left, context),
            Operator { Character: '<' } => new GreaterThanOrEqualExpression(left, context),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context)
        },
        ReservedKeyword { Keyword: Keyword.Like } => ParseLike(left, context, negated: false),
        ReservedKeyword { Keyword: Keyword.Is } => ParseIsNullSuffix(left, context),
        ReservedKeyword { Keyword: Keyword.In } => ParseInList(left, context, negated: false),
        ReservedKeyword { Keyword: Keyword.Not } => context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Like } => ParseLike(left, context, negated: true),
            ReservedKeyword { Keyword: Keyword.In } => ParseInList(left, context, negated: true),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        },
        _ => throw SimulatedSqlException.SyntaxErrorNear(context),
    };

    private static LikeExpression ParseLike(Expression left, ParserContext context, bool negated)
    {
        var pattern = Expression.Parse(context.MoveNextRequiredReturnSelf());
        Expression? escape = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Escape })
            escape = Expression.Parse(context.MoveNextRequiredReturnSelf());
        return new LikeExpression(left, pattern, escape, negated);
    }

    /// <summary>
    /// Parses the <c>IS [NOT] NULL</c> suffix after an expression. Entered
    /// with <see cref="ParserContext.Token"/> on the <c>IS</c> keyword;
    /// consumes <c>IS</c>, optional <c>NOT</c>, and the required <c>NULL</c>.
    /// Leaves the token on the next un-consumed token (typically a boolean
    /// combinator, comma, or end-of-input).
    /// </summary>
    private static IsNullExpression ParseIsNullSuffix(Expression left, ParserContext context)
    {
        var negated = false;
        var next = context.GetNextRequired();
        if (next is ReservedKeyword { Keyword: Keyword.Not })
        {
            negated = true;
            next = context.GetNextRequired();
        }
        if (next is not ReservedKeyword { Keyword: Keyword.Null })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();
        return new IsNullExpression(left, negated);
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
    /// Evaluates the predicate to SQL Server's three-valued logic:
    /// <c>true</c>, <c>false</c>, or <c>null</c> (UNKNOWN). NULL operands in a
    /// comparison surface as <c>null</c> here; <c>NOT</c>, <c>AND</c>, and
    /// <c>OR</c> propagate UNKNOWN per the standard truth tables. Callers
    /// decide what to do with UNKNOWN: WHERE / MERGE-ON treat it as exclude
    /// (only <c>true</c> rows pass); CHECK constraints treat it as pass
    /// (only an explicit <c>false</c> rejects the row).
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    public abstract bool? Run(Func<List<string>, SqlValue> getColumnValue);

    /// <summary>
    /// Diagnostic-only string rendering, surfaced via
    /// <see cref="DebuggerDisplayAttribute"/>. Production paths must not call
    /// this — same convention as <see cref="Expression.DebugDisplay"/>.
    /// </summary>
    internal abstract string DebugDisplay();

    /// <summary>
    /// Three-valued <c>AND</c>: <c>false AND x = false</c> regardless of
    /// <c>x</c>; <c>true AND x = x</c>; <c>NULL AND NULL = NULL</c>. Short-
    /// circuits when the left side is <c>false</c> — the right side isn't
    /// evaluated. SQL Server doesn't guarantee evaluation order, but it
    /// permits short-circuit; the simulator commits to it for predictability.
    /// </summary>
    private sealed class AndExpression(BooleanExpression left, BooleanExpression right) : BooleanExpression
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var l = left.Run(getColumnValue);
            if (l == false)
                return false;
            var r = right.Run(getColumnValue);
            return r == false
                ? false
                : l == true && r == true ? true : null;
        }

        internal override string DebugDisplay() => $"{left.DebugDisplay()} AND {right.DebugDisplay()}";
    }

    /// <summary>
    /// Three-valued <c>OR</c>: <c>true OR x = true</c> regardless of
    /// <c>x</c>; <c>false OR x = x</c>; <c>NULL OR NULL = NULL</c>. Short-
    /// circuits when the left side is <c>true</c>.
    /// </summary>
    private sealed class OrExpression(BooleanExpression left, BooleanExpression right) : BooleanExpression
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var l = left.Run(getColumnValue);
            if (l == true)
                return true;
            var r = right.Run(getColumnValue);
            return r == true
                ? true
                : l == false && r == false ? false : null;
        }

        internal override string DebugDisplay() => $"{left.DebugDisplay()} OR {right.DebugDisplay()}";
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
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            source.Run(getColumnValue).IsNull ^ negated;

        internal override string DebugDisplay() => $"{source.DebugDisplay()} IS {(negated ? "NOT NULL" : "NULL")}";
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
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var src = source.Run(getColumnValue);
            if (src.IsNull)
                return null;
            var sawNull = false;
            foreach (var candidate in candidates)
            {
                var c = candidate.Run(getColumnValue);
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
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            inner.Execute(getColumnValue).RowBytes.Any();

        internal override string DebugDisplay() => "EXISTS (...)";
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
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var src = source.Run(getColumnValue);
            if (src.IsNull)
                return null;

            var sawNull = false;
            var resultSet = inner.Execute(getColumnValue);
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
    }

    /// <summary>
    /// Three-valued <c>NOT</c>: <c>NOT true = false</c>, <c>NOT false = true</c>,
    /// <c>NOT NULL = NULL</c>. The NULL pass-through is what makes
    /// <c>WHERE NOT (col = X)</c> exclude NULL rows in SQL Server (NULL
    /// becomes UNKNOWN; UNKNOWN excludes from WHERE).
    /// </summary>
    private sealed class NotExpression(BooleanExpression inner) : BooleanExpression
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) => inner.Run(getColumnValue) switch
        {
            true => false,
            false => true,
            null => null,
        };

        internal override string DebugDisplay() => $"NOT {inner.DebugDisplay()}";
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

        private protected CompareExpression(Expression left, ParserContext context)
            : this(left, Expression.Parse(context.MoveNextRequiredReturnSelf()))
        {
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
        protected static bool? ComparePromoted(Expression left, Expression right, Func<List<string>, SqlValue> getColumnValue, string operatorName, Func<SqlValue, SqlValue, bool> compare) =>
            CompareValuesPromoted(left.Run(getColumnValue), right.Run(getColumnValue), operatorName, compare);
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
        if (l.Type.IsLob || r.Type.IsLob)
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(l.Type, r.Type, operatorName);
        if (l.IsNull || r.IsNull)
            return null;

        if (l.Type == r.Type)
            return compare(l, r);

        var common = SqlType.Promote(l.Type, r.Type);
        return compare(l.CoerceTo(common), r.CoerceTo(common));
    }

    private sealed class EqualityExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "equal to", static (l, r) => l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} = {right.DebugDisplay()}";
    }

    private sealed class InequalityExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "not equal to", static (l, r) => !l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <> {right.DebugDisplay()}";
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "greater than", static (l, r) => l.CompareTo(r) > 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} > {right.DebugDisplay()}";
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "greater than or equal to", static (l, r) => l.CompareTo(r) >= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} >= {right.DebugDisplay()}";
    }

    private sealed class LessThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "less than", static (l, r) => l.CompareTo(r) < 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} < {right.DebugDisplay()}";
    }

    private sealed class LessThanOrEqualExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool? Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "less than or equal to", static (l, r) => l.CompareTo(r) <= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <= {right.DebugDisplay()}";
    }

    /// <summary>
    /// SQL Server <c>LIKE</c> / <c>NOT LIKE</c> with optional <c>ESCAPE</c>
    /// clause. Pattern translation produces a .NET <see cref="Regex"/> rebuilt
    /// per evaluation; pre-compilation/caching is left for later. Trailing-space
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

        public override bool? Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var l = left.Run(getColumnValue);
            var r = right.Run(getColumnValue);
            if (l.IsNull || r.IsNull)
                return null;

            if (l.Type.Category != SqlTypeCategory.String || r.Type.Category != SqlTypeCategory.String)
                throw SimulatedSqlException.OperandTypeClash(l.Type, r.Type);

            char? escapeChar = null;
            if (this.escape is not null)
            {
                var e = this.escape.Run(getColumnValue);
                if (e.IsNull)
                    return null;
                if (e.Type.Category != SqlTypeCategory.String)
                    throw SimulatedSqlException.OperandTypeClash(l.Type, e.Type);
                var s = e.AsString;
                if (s.Length != 1)
                    throw SimulatedSqlException.InvalidEscapeCharacter(s);
                escapeChar = s[0];
            }

            var matched = LikeToRegex(r.AsString, escapeChar).IsMatch(l.AsString);
            return matched ^ this.negated;
        }

        /// <summary>
        /// Translates a SQL Server <c>LIKE</c> pattern to a <see cref="Regex"/>.
        /// Anchors with <c>^...[ ]*$</c> so the subject's trailing U+0020
        /// spaces are accepted (matching SQL Server's documented behavior;
        /// only literal space, not tab or LF/CR, is treated as a trailing
        /// blank). <see cref="RegexOptions.Singleline"/> makes <c>.</c> (the
        /// translation for <c>_</c>) and <c>.*</c> (for <c>%</c>) match
        /// newlines, matching the probed behavior.
        /// </summary>
        private static Regex LikeToRegex(string pattern, char? escapeChar)
        {
            var sb = new StringBuilder(pattern.Length + 8);
            _ = sb.Append('^');

            var i = 0;
            while (i < pattern.Length)
            {
                var c = pattern[i];

                if (escapeChar.HasValue && c == escapeChar.Value)
                {
                    // Escape consumes the next char as a literal. A trailing
                    // escape (nothing after) is itself taken literally; the
                    // probed `'a' LIKE 'a!' ESCAPE '!'` returned 0 because
                    // the resulting pattern is two chars (`a` + literal `!`)
                    // against a one-char subject.
                    if (i + 1 < pattern.Length)
                    {
                        _ = sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                        i += 2;
                    }
                    else
                    {
                        _ = sb.Append(Regex.Escape(c.ToString()));
                        i++;
                    }
                    continue;
                }

                switch (c)
                {
                    case '%':
                        _ = sb.Append(".*");
                        i++;
                        break;
                    case '_':
                        _ = sb.Append('.');
                        i++;
                        break;
                    case '[':
                        i = TranslateClass(pattern, i, sb);
                        break;
                    default:
                        _ = sb.Append(Regex.Escape(c.ToString()));
                        i++;
                        break;
                }
            }

            // \z (absolute end-of-string) rather than $ — .NET's $ matches
            // before a final \n even outside Multiline mode, which would let
            // 'abc\n' incorrectly match a no-trailing-space pattern.
            _ = sb.Append("[ ]*\\z");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        }

        /// <summary>
        /// Translates a single <c>[...]</c> character class starting at
        /// <paramref name="start"/> (the position of <c>[</c>). Returns the
        /// position after the closing <c>]</c>. Three never-match cases emit
        /// <c>(?!)</c> (always-fails lookahead): unterminated <c>[</c>, empty
        /// <c>[]</c>, and reversed ranges (<c>[c-a]</c>) — all confirmed by
        /// real-server probing. The <c>[^]</c> any-char form translates to
        /// <c>.</c> because .NET regex doesn't accept <c>[^]</c> directly.
        /// </summary>
        private static int TranslateClass(string pattern, int start, StringBuilder sb)
        {
            var contentStart = start + 1;
            var end = pattern.IndexOf(']', contentStart);
            if (end < 0)
            {
                _ = sb.Append("(?!)");
                return pattern.Length;
            }

            var content = pattern.AsSpan(contentStart, end - contentStart);
            if (content.IsEmpty)
            {
                _ = sb.Append("(?!)");
                return end + 1;
            }
            if (content is "^")
            {
                _ = sb.Append('.');
                return end + 1;
            }

            // Detect a reversed range like [c-a]: any range whose end char
            // sorts below its start char is treated as never-match. Ranges
            // are 3-char windows (X-Y) where the leading and trailing chars
            // aren't themselves at the start/end of the class (a leading or
            // trailing '-' is a literal hyphen, per SQL Server docs).
            for (var j = 1; j < content.Length - 1; j++)
            {
                if (content[j] == '-' && content[j - 1] > content[j + 1])
                {
                    _ = sb.Append("(?!)");
                    return end + 1;
                }
            }

            _ = sb.Append('[');
            foreach (var cc in content)
            {
                if (cc is '\\' or ']')
                    _ = sb.Append('\\');
                _ = sb.Append(cc);
            }
            _ = sb.Append(']');
            return end + 1;
        }

        internal override string DebugDisplay() => this.escape is null
            ? $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()}"
            : $"{left.DebugDisplay()} {(this.negated ? "NOT LIKE" : "LIKE")} {right.DebugDisplay()} ESCAPE {this.escape.DebugDisplay()}";
    }
}
