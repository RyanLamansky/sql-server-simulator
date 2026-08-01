using System.Text.RegularExpressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// The match-modifier set SQL Server 2025's <c>REGEXP_*</c> flags argument
/// carries. Only <c>c</c> / <c>i</c> / <c>s</c> / <c>m</c> are accepted — real
/// rejects Oracle's <c>x</c> (free-spacing) — and the characters apply
/// left-to-right, so <c>'ic'</c> ends case-sensitive while <c>'ci'</c> ends
/// case-insensitive.
/// </summary>
internal readonly struct RegexFlags(bool ignoreCase, bool multiline, bool dotMatchesNewline)
{
    public readonly bool IgnoreCase = ignoreCase;

    public readonly bool Multiline = multiline;

    public readonly bool DotMatchesNewline = dotMatchesNewline;
}

/// <summary>
/// Argument handling shared by the <c>REGEXP_*</c> members: the strict string
/// operands, the numeric operands and their per-(function, argument) Msg 19301
/// states, and the flags argument.
/// </summary>
/// <remarks>
/// Real's validation order is probe-confirmed and load-bearing, because the
/// members differ in which check a caller sees first: an operand of the wrong
/// <i>type</i> raises Msg 8116 even when its value is NULL; a NULL operand of
/// the right type then short-circuits the whole call (so
/// <c>REGEXP_COUNT(NULL, '(')</c> is NULL rather than a pattern error); the
/// numeric range checks (Msg 19301) run next; the flags argument (Msg 19303)
/// after those; and the pattern compiles last.
/// </remarks>
internal static class RegexpArguments
{
    /// <summary>
    /// Reads a string operand — the input, the pattern, or
    /// <c>REGEXP_REPLACE</c>'s replacement. Unlike the rest of the string
    /// scalars these take no implicit conversion: real raises Msg 8116 for a
    /// numeric, binary or legacy-LOB operand rather than rendering it.
    /// </summary>
    public static SqlValue ReadStringArgument(Expression expression, RuntimeContext runtime, string functionLowerName, int argumentIndex)
    {
        var value = expression.Run(runtime);
        // The bare NULL keyword carries the parser's int placeholder type but
        // is accepted everywhere a string operand goes — real raises Msg 8116
        // only for an operand that genuinely types non-string, including a
        // typed `CAST(NULL AS int)`.
        return (SqlType.IsStringCategory(value.Type) && value.Type is not (TextSqlType or NTextSqlType))
            || Expression.IsUntypedNullLiteral(expression)
            ? value
            : throw SimulatedSqlException.InvalidArgumentDataType(value.Type.SqlServerName, argumentIndex, functionLowerName);
    }

    /// <summary>
    /// Evaluates an optional numeric operand. Reports <see langword="false"/>
    /// when the argument is absent (the caller keeps its default) or NULL (the
    /// caller returns NULL).
    /// </summary>
    public static bool TryReadNumericArgument(Expression? expression, RuntimeContext runtime, out int value)
    {
        value = 0;
        if (expression is null)
            return false;
        var evaluated = expression.Run(runtime);
        if (evaluated.IsNull)
            return false;
        value = ScalarArguments.CoerceToInt(evaluated);
        return true;
    }

    /// <summary>
    /// Raises Msg 19301 when <paramref name="value"/> is below
    /// <paramref name="rejectBelow"/>. <paramref name="reportedMinimum"/> is
    /// what real prints, which isn't always the bound it enforces — see
    /// <see cref="SimulatedSqlException.RegexArgumentBelowMinimum"/>.
    /// </summary>
    public static void RequireAtLeast(int value, int rejectBelow, int reportedMinimum, string argumentName, string functionUpperName, byte state)
    {
        if (value < rejectBelow)
            throw SimulatedSqlException.RegexArgumentBelowMinimum(argumentName, reportedMinimum, value, functionUpperName, state);
    }

    /// <summary>
    /// Parses the flags operand. Reports <see langword="false"/> when it's
    /// present but NULL (the caller returns NULL); an absent operand yields the
    /// default (case-sensitive, single-line, <c>.</c> excludes newline).
    /// </summary>
    public static bool TryReadFlags(Expression? expression, RuntimeContext runtime, string functionLowerName, int argumentIndex, out RegexFlags flags)
    {
        flags = default;
        if (expression is null)
            return true;
        var value = ReadStringArgument(expression, runtime, functionLowerName, argumentIndex);
        if (value.IsNull)
            return false;
        flags = ParseFlags(value.AsString);
        return true;
    }

    /// <summary>
    /// Maps a flags string to its modifier set. Characters apply in order, so a
    /// later <c>c</c> or <c>i</c> overrides an earlier one; anything outside
    /// <c>{c, i, s, m}</c> — including an uppercase spelling — raises Msg 19303
    /// quoting the whole string.
    /// </summary>
    public static RegexFlags ParseFlags(string flags)
    {
        var ignoreCase = false;
        var multiline = false;
        var dotMatchesNewline = false;
        foreach (var c in flags)
        {
            switch (c)
            {
                case 'c':
                    ignoreCase = false;
                    break;
                case 'i':
                    ignoreCase = true;
                    break;
                case 'm':
                    multiline = true;
                    break;
                case 's':
                    dotMatchesNewline = true;
                    break;
                default:
                    throw SimulatedSqlException.RegexInvalidFlags(flags);
            }
        }
        return new(ignoreCase, multiline, dotMatchesNewline);
    }

    /// <summary>
    /// Enumerates the pattern's non-overlapping matches over
    /// <paramref name="input"/> starting at the 0-based
    /// <paramref name="startIndex"/>, advancing past a zero-width match the way
    /// both engines do so an empty-matching pattern yields one match per
    /// position plus one at the end.
    /// </summary>
    public static IEnumerable<Match> Matches(Regex regex, string input, int startIndex)
    {
        if (startIndex > input.Length)
            yield break;
        var at = startIndex;
        while (at <= input.Length)
        {
            var match = regex.Match(input, at);
            if (!match.Success)
                yield break;
            yield return match;
            at = match.Length == 0 ? match.Index + 1 : match.Index + match.Length;
        }
    }

    /// <summary>
    /// The <paramref name="occurrence"/>-th (1-based) match at or after
    /// <paramref name="startIndex"/>, or null when there aren't that many.
    /// </summary>
    public static Match? NthMatch(Regex regex, string input, int startIndex, int occurrence)
    {
        var seen = 0;
        foreach (var match in Matches(regex, input, startIndex))
        {
            if (++seen == occurrence)
                return match;
        }
        return null;
    }

    /// <summary>
    /// The requested capture group of <paramref name="match"/>, or null when
    /// the pattern has no such group or the group didn't participate. Group 0
    /// is the whole match.
    /// </summary>
    public static Group? CaptureGroup(Match match, int group)
    {
        if (group < 0 || group >= match.Groups.Count)
            return null;
        var captured = match.Groups[group];
        return captured.Success ? captured : null;
    }
}
