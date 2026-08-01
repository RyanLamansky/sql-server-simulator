using System.Text;
using System.Text.RegularExpressions;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The two rowset-returning <c>REGEXP_*</c> members SQL Server 2025 ships —
/// <c>REGEXP_MATCHES(string, pattern [, flags])</c> and
/// <c>REGEXP_SPLIT_TO_TABLE(string, pattern [, flags])</c> — built as
/// <see cref="Selection"/> factories in the same shape as <c>STRING_SPLIT</c>
/// so the FROM-source machinery (alias, lateral re-execution per outer row,
/// JOIN / APPLY composition) is reused unchanged.
/// </summary>
/// <remarks>
/// <para>
/// Both are gated on compatibility level 170: at 160 and below real reports
/// <strong>Msg 208</strong>, <c>Invalid object name 'REGEXP_MATCHES'.</c>, so
/// the FROM dispatch only recognizes the names at 170 and lets the ordinary
/// object-name path produce that error otherwise. The four scalars carry no
/// such gate.
/// </para>
/// <para>
/// Probe-confirmed schemas (SQL Server 2025):
/// </para>
/// <list type="bullet">
/// <item><description><c>REGEXP_MATCHES</c> → <c>(match_id bigint,
/// start_position int, end_position int, match_value <i>input_string_type</i>,
/// substring_matches varchar(max))</c>. <c>substring_matches</c> is
/// <c>varchar(max)</c> whatever the input's family.</description></item>
/// <item><description><c>REGEXP_SPLIT_TO_TABLE</c> → <c>(value
/// <i>input_string_type</i>, ordinal bigint)</c>.</description></item>
/// <item><description>A NULL in any argument yields an empty result set rather
/// than a NULL row.</description></item>
/// <item><description>Arity is enforced as a table-valued function's — Msg 313
/// / Msg 8144, not Msg 189.</description></item>
/// </list>
/// </remarks>
partial class Selection
{
    /// <summary>
    /// True when <paramref name="name"/> is one of the two REGEXP rowset
    /// members <i>and</i> the active database is at compatibility level 170,
    /// which is where real ships them. Below 170 the caller lets the name reach
    /// the ordinary object-name path so it raises Msg 208 the way real does.
    /// </summary>
    private static bool IsRegexpRowsetName(string name, ParserContext context) =>
        context.CurrentDatabase.CompatibilityLevel >= CompatibilityLevel.Sql170
        && (string.Equals(name, "REGEXP_MATCHES", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "REGEXP_SPLIT_TO_TABLE", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses <c>REGEXP_MATCHES(string, pattern [, flags])</c>. Enters with the
    /// cursor on the function name; on return it sits on the first token past
    /// the closing <c>)</c>.
    /// </summary>
    public static Selection ParseRegexpMatches(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var (input, pattern, flags, inputType) = ParseRegexpRowsetArguments(context, outerTypeResolver, "REGEXP_MATCHES");
        SqlType[] schema = [SqlType.BigInt, SqlType.Int32, SqlType.Int32, inputType, SqlType.VarcharMax];
        string[] columnNames = ["match_id", "start_position", "end_position", "match_value", "substring_matches"];
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateRegexpMatchRows(input, pattern, flags, schema, inputType, batch, outerResolver));
    }

    /// <summary>
    /// Parses <c>REGEXP_SPLIT_TO_TABLE(string, pattern [, flags])</c>. Enters
    /// with the cursor on the function name; on return it sits on the first
    /// token past the closing <c>)</c>.
    /// </summary>
    public static Selection ParseRegexpSplitToTable(ParserContext context, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var (input, pattern, flags, inputType) = ParseRegexpRowsetArguments(context, outerTypeResolver, "REGEXP_SPLIT_TO_TABLE");
        SqlType[] schema = [inputType, SqlType.BigInt];
        string[] columnNames = ["value", "ordinal"];
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            (batch, outerResolver) => EnumerateRegexpSplitRows(input, pattern, flags, schema, inputType, batch, outerResolver));
    }

    /// <summary>
    /// Shared <c>(string, pattern [, flags])</c> argument parse plus the
    /// parse-time input-family decision that fixes the projected schema.
    /// </summary>
    private static (Expression Input, Expression Pattern, Expression? Flags, SqlType InputType) ParseRegexpRowsetArguments(
        ParserContext context,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        string functionName)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        var input = Expression.Parse(context);
        if (context.Token is not Operator { Character: ',' })
            throw SimulatedSqlException.InsufficientArgumentsToFunction(functionName, state: 3);
        var pattern = Expression.Parse(context.MoveNextRequiredReturnSelf());

        Expression? flags = null;
        if (context.Token is Operator { Character: ',' })
            flags = Expression.Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Operator { Character: ',' })
            throw SimulatedSqlException.TooManyArgumentsToFunction(functionName, state: 3);
        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextOptional();

        var inputType = input.GetSqlType(context.Batch, outerTypeResolver ?? (_ => SqlType.NVarchar));
        return (input, pattern, flags, SqlType.IsStringCategory(inputType) ? inputType : SqlType.NVarchar);
    }

    /// <summary>
    /// Evaluates the shared arguments for a rowset member. Reports
    /// <see langword="false"/> when any of them is NULL, which real answers with
    /// an empty result set.
    /// </summary>
    private static bool TryPrepareRegexpRowset(
        Expression input,
        Expression pattern,
        Expression? flags,
        string functionLowerName,
        RuntimeContext runtime,
        out string text,
        out Regex regex)
    {
        text = string.Empty;
        regex = null!;
        var inputValue = RegexpArguments.ReadStringArgument(input, runtime, functionLowerName, argumentIndex: 1);
        var patternValue = RegexpArguments.ReadStringArgument(pattern, runtime, functionLowerName, argumentIndex: 2);
        if (inputValue.IsNull || patternValue.IsNull)
            return false;
        if (!RegexpArguments.TryReadFlags(flags, runtime, functionLowerName, argumentIndex: 3, out var flagSet))
            return false;
        text = inputValue.AsString;
        regex = RegexDialect.Compile(patternValue.AsString, flagSet, RegexCallSite.Rowset);
        return true;
    }

    private static IEnumerable<byte[]> EnumerateRegexpMatchRows(
        Expression input,
        Expression pattern,
        Expression? flags,
        SqlType[] schema,
        SqlType matchValueType,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        if (!TryPrepareRegexpRowset(input, pattern, flags, "regexp_matches", runtime, out var text, out var regex))
            yield break;

        var matchId = 1L;
        foreach (var match in RegexpArguments.Matches(regex, text, 0))
        {
            // A zero-width match reports the same value for both positions,
            // clamped to the input's length — so an empty match past the last
            // character reports the length rather than length + 1, and
            // `REGEXP_MATCHES('', '')` reports 0 / 0. A non-empty match reports
            // its first and last character positions, both 1-based.
            var start = match.Length == 0 ? Math.Min(match.Index + 1, text.Length) : match.Index + 1;
            SqlValue[] values =
            [
                SqlValue.FromInt64(matchId++),
                SqlValue.FromInt32(start),
                SqlValue.FromInt32(match.Length == 0 ? start : match.Index + match.Length),
                SqlValue.FromString(matchValueType, match.Value),
                SqlValue.FromString(SqlType.VarcharMax, RenderSubstringMatches(match, text.Length)),
            ];
            yield return RowEncoder.EncodeRow(schema, values);
        }
    }

    /// <summary>
    /// Renders the <c>substring_matches</c> JSON array: one object per capture
    /// group with its 1-based start and length, or <c>null</c> members for a
    /// group that didn't participate. A pattern with no capture groups reports
    /// the whole match as the single entry, and a zero-length capture's start
    /// is clamped to <paramref name="inputLength"/> the same way the
    /// <c>start_position</c> column's is — both probe-confirmed.
    /// </summary>
    private static string RenderSubstringMatches(Match match, int inputLength)
    {
        var json = new StringBuilder("[");
        var first = match.Groups.Count > 1 ? 1 : 0;
        for (var i = first; i < match.Groups.Count; i++)
        {
            if (i > first)
                _ = json.Append(',');
            var group = match.Groups[i];
            if (!group.Success)
            {
                _ = json.Append("{\"value\":null,\"start\":null,\"length\":null}");
                continue;
            }
            _ = json.Append("{\"value\":");
            JsonValueRender.AppendJsonString(json, group.Value);
            _ = json.Append(",\"start\":")
                .Append(group.Length == 0 ? Math.Min(group.Index + 1, inputLength) : group.Index + 1)
                .Append(",\"length\":").Append(group.Length).Append('}');
        }
        return json.Append(']').ToString();
    }

    private static IEnumerable<byte[]> EnumerateRegexpSplitRows(
        Expression input,
        Expression pattern,
        Expression? flags,
        SqlType[] schema,
        SqlType valueColumnType,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var runtime = new RuntimeContext(outerResolver ?? (n => throw SimulatedSqlException.InvalidColumnName(n)), batch);
        if (!TryPrepareRegexpRowset(input, pattern, flags, "regexp_split_to_table", runtime, out var text, out var regex))
            yield break;

        var ordinal = 1L;
        foreach (var segment in SplitSegments(regex, text))
        {
            SqlValue[] values = [SqlValue.FromString(valueColumnType, segment), SqlValue.FromInt64(ordinal++)];
            yield return RowEncoder.EncodeRow(schema, values);
        }
    }

    /// <summary>
    /// The separator enumeration <c>REGEXP_SPLIT_TO_TABLE</c> runs on, which is
    /// <i>not</i> the one the scalars use: a zero-width match landing exactly
    /// where the previous match ended is discarded rather than reported. That
    /// one rule is the whole difference between
    /// <c>REGEXP_COUNT('aXbXc', 'X*')</c> = 6 and
    /// <c>REGEXP_SPLIT_TO_TABLE('aXbXc', 'X*')</c> splitting into just
    /// <c>a</c> / <c>b</c> / <c>c</c> — probe-confirmed against SQL Server 2025.
    /// </summary>
    private static IEnumerable<Match> SeparatorMatches(Regex regex, string text)
    {
        var at = 0;
        var previousEnd = -1;
        while (at <= text.Length)
        {
            var match = regex.Match(text, at);
            if (!match.Success)
                yield break;
            var accept = true;
            if (match.Length == 0)
            {
                accept = match.Index != previousEnd;
                at = match.Index + 1;
            }
            else
            {
                at = match.Index + match.Length;
            }
            previousEnd = match.Index + match.Length;
            if (accept)
                yield return match;
        }
    }

    /// <summary>
    /// The split algorithm real implements over
    /// <see cref="SeparatorMatches"/>: every match is a separator, except that
    /// a match ending at position 0 contributes no leading empty segment, and a
    /// final segment is emitted only when the last separator didn't end at the
    /// input's end. That combination is why
    /// <c>REGEXP_SPLIT_TO_TABLE('abc', '')</c> yields three single-character
    /// rows rather than five, and why <c>(',a,', ',')</c> yields an empty row on
    /// both ends.
    /// </summary>
    private static List<string> SplitSegments(Regex regex, string text)
    {
        var segments = new List<string>();
        var begin = 0;
        var end = 0;
        foreach (var match in SeparatorMatches(regex, text))
        {
            end = match.Index;
            if (match.Index + match.Length != 0)
                segments.Add(text[begin..end]);
            begin = match.Index + match.Length;
        }
        if (end != text.Length)
            segments.Add(text[begin..]);
        return segments;
    }
}
