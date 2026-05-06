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
    /// Parses a full boolean predicate from the current position: a single
    /// comparison (the <see cref="CompareExpression"/> shapes), optionally
    /// chained with one or more <c>AND</c> clauses producing an
    /// <see cref="AndExpression"/> tree. Each chain element is itself a
    /// comparison; nested parens around predicates and <c>OR</c>/<c>NOT</c>
    /// at the boolean-combinator level aren't supported yet (they fall
    /// through to the comparison parser, which surfaces them as syntax
    /// errors). Follows the lookahead contract on <see cref="ParserContext"/>:
    /// on return, <see cref="ParserContext.Token"/> is the first token not
    /// consumed by the predicate.
    /// </summary>
    public static BooleanExpression Parse(ParserContext context)
    {
        var result = ParseSingle(Expression.Parse(context), context);
        while (context.Token is ReservedKeyword { Keyword: Keyword.And })
        {
            context.MoveNextRequired();
            var rhs = ParseSingle(Expression.Parse(context), context);
            result = new AndExpression(result, rhs);
        }
        return context.Token is ReservedKeyword { Keyword: Keyword.Or }
            ? throw new NotSupportedException("OR in boolean expressions.")
            : result;
    }

    /// <summary>
    /// Parses a single comparison/predicate (no AND/OR chaining): equality,
    /// inequality, ordered comparison, or LIKE/NOT LIKE. Caller must have
    /// already parsed the left side.
    /// </summary>
    private static BooleanExpression ParseSingle(Expression left, ParserContext context) => context.Token switch
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
        ReservedKeyword { Keyword: Keyword.Not } => context.GetNextRequired() switch
        {
            ReservedKeyword { Keyword: Keyword.Like } => ParseLike(left, context, negated: true),
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
    /// Evaluates the expression. Any NULL operand yields <c>false</c>
    /// (SQL UNKNOWN-in-WHERE semantics).
    /// </summary>
    /// <param name="getColumnValue">Provides the value for a column.</param>
    public abstract bool Run(Func<List<string>, SqlValue> getColumnValue);

    /// <summary>
    /// Diagnostic-only string rendering, surfaced via
    /// <see cref="DebuggerDisplayAttribute"/>. Production paths must not call
    /// this — same convention as <see cref="Expression.DebugDisplay"/>.
    /// </summary>
    internal abstract string DebugDisplay();

    /// <summary>
    /// Combines two boolean predicates with a logical AND. Short-circuits on
    /// the left side: if <c>left.Run</c> returns <c>false</c>, the right side
    /// isn't evaluated. NULL-as-false propagates naturally — both sides must
    /// run to true for the row to pass, matching SQL Server's WHERE-clause
    /// "NULL excludes the row" semantics.
    /// </summary>
    private sealed class AndExpression(BooleanExpression left, BooleanExpression right) : BooleanExpression
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            left.Run(getColumnValue) && right.Run(getColumnValue);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} AND {right.DebugDisplay()}";
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
        /// type, and invokes the comparator. Cross-category type pairs surface
        /// as <see cref="NotSupportedException"/> via
        /// <see cref="SqlType.Promote"/>. LOB-typed operands (<c>text</c>,
        /// <c>ntext</c>, <c>image</c>) raise Msg 402 rather than being routed
        /// through promotion — SQL Server rejects them in any comparison /
        /// equality slot, and the operator name is woven into the message via
        /// the caller-supplied <paramref name="operatorName"/>.
        /// </summary>
        protected static bool ComparePromoted(Expression left, Expression right, Func<List<string>, SqlValue> getColumnValue, string operatorName, Func<SqlValue, SqlValue, bool> compare)
        {
            var l = left.Run(getColumnValue);
            var r = right.Run(getColumnValue);
            if (l.Type.IsLob || r.Type.IsLob)
                throw SimulatedSqlException.IncompatibleDataTypesInOperator(l.Type, r.Type, operatorName);
            if (l.IsNull || r.IsNull)
                return false;

            if (l.Type == r.Type)
                return compare(l, r);

            var common = SqlType.Promote(l.Type, r.Type);
            return compare(l.CoerceTo(common), r.CoerceTo(common));
        }
    }

    private sealed class EqualityExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "equal to", static (l, r) => l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} = {right.DebugDisplay()}";
    }

    private sealed class InequalityExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "not equal to", static (l, r) => !l.Equals(r));

        internal override string DebugDisplay() => $"{left.DebugDisplay()} <> {right.DebugDisplay()}";
    }

    private sealed class GreaterThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "greater than", static (l, r) => l.CompareTo(r) > 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} > {right.DebugDisplay()}";
    }

    private sealed class GreaterThanOrEqualExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "greater than or equal to", static (l, r) => l.CompareTo(r) >= 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} >= {right.DebugDisplay()}";
    }

    private sealed class LessThanExpression(Expression left, Expression right) : CompareExpression(left, right)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
            ComparePromoted(left, right, getColumnValue, "less than", static (l, r) => l.CompareTo(r) < 0);

        internal override string DebugDisplay() => $"{left.DebugDisplay()} < {right.DebugDisplay()}";
    }

    private sealed class LessThanOrEqualExpression(Expression left, ParserContext context) : CompareExpression(left, context)
    {
        public override bool Run(Func<List<string>, SqlValue> getColumnValue) =>
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

        public override bool Run(Func<List<string>, SqlValue> getColumnValue)
        {
            var l = left.Run(getColumnValue);
            var r = right.Run(getColumnValue);
            if (l.IsNull || r.IsNull)
                return false;

            if (l.Type.Category != SqlTypeCategory.String || r.Type.Category != SqlTypeCategory.String)
                throw SimulatedSqlException.OperandTypeClash(l.Type, r.Type);

            char? escapeChar = null;
            if (this.escape is not null)
            {
                var e = this.escape.Run(getColumnValue);
                if (e.IsNull)
                    return false;
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
