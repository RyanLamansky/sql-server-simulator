using System.Text;
using System.Text.RegularExpressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>Which of the four <c>REGEXP_*</c> scalars a <see cref="RegexpScalar"/> node is.</summary>
internal enum RegexpScalarKind
{
    /// <summary><c>REGEXP_COUNT(string, pattern [, start [, flags]])</c> → <c>int</c>.</summary>
    Count,

    /// <summary><c>REGEXP_INSTR(string, pattern [, start [, occurrence [, return_option [, flags [, group]]]]])</c> → <c>int</c>.</summary>
    Instr,

    /// <summary><c>REGEXP_REPLACE(string, pattern [, replacement [, start [, occurrence [, flags]]]])</c>.</summary>
    Replace,

    /// <summary><c>REGEXP_SUBSTR(string, pattern [, start [, occurrence [, flags [, group]]]])</c>.</summary>
    Substr,
}

/// <summary>
/// The four <c>REGEXP_*</c> scalars SQL Server 2025 ships. They share an
/// argument shape, a validation order and a dialect, so one node carries all
/// four behind <see cref="RegexpScalarKind"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the <c>REGEXP_LIKE</c> predicate and the two rowset members, the
/// scalars are <b>not</b> gated on compatibility level — probe-confirmed
/// available at 130 / 150 / 160 as well as 170.
/// </para>
/// <para>
/// Semantics, all probe-confirmed against SQL Server 2025 (17.0.4065.4):
/// </para>
/// <list type="bullet">
/// <item><description><c>occurrence</c> is 1-based and counts from
/// <c>start</c>; <c>REGEXP_REPLACE</c> alone accepts 0, meaning every
/// match.</description></item>
/// <item><description><c>REGEXP_REPLACE</c>'s replacement uses Oracle's
/// backslash backreferences (<c>\1</c>…<c>\9</c>, out-of-range → empty);
/// <c>$</c> is literal, <c>\\</c> is one backslash, and any other
/// backslash escape passes through with its backslash intact.</description></item>
/// <item><description>An empty <i>pattern</i> makes <c>REGEXP_REPLACE</c> a
/// no-op even though <c>x*</c> — which also matches empty — replaces at every
/// position.</description></item>
/// <item><description><c>REGEXP_INSTR</c>'s <c>return_option</c> is the one
/// argument that does <i>not</i> propagate NULL: a NULL there behaves as 0
/// (report the match's start).</description></item>
/// <item><description>Result types: <c>int</c> for COUNT / INSTR; the input's
/// family at container width (<c>varchar(8000)</c> / <c>nvarchar(4000)</c>, or
/// MAX carried through) for REPLACE, independent of the replacement's family;
/// the input's own declared width for SUBSTR.</description></item>
/// </list>
/// </remarks>
/// <seealso href="https://learn.microsoft.com/en-us/sql/t-sql/functions/regexp-count-transact-sql"/>
internal sealed class RegexpScalar : Expression
{
    private readonly RegexpScalarKind kind;
    private readonly Expression[] arguments;

    private RegexpScalar(RegexpScalarKind kind, Expression[] arguments)
    {
        this.kind = kind;
        this.arguments = arguments;
    }

    /// <summary>Lowercase name real uses in Msg 189 / Msg 8116.</summary>
    private static string LowerNameFor(RegexpScalarKind kind) => kind switch
    {
        RegexpScalarKind.Count => "regexp_count",
        RegexpScalarKind.Instr => "regexp_instr",
        RegexpScalarKind.Replace => "regexp_replace",
        _ => "regexp_substr",
    };

    /// <summary>Uppercase name real uses in Msg 19301.</summary>
    private string UpperName => this.kind switch
    {
        RegexpScalarKind.Count => "REGEXP_COUNT",
        RegexpScalarKind.Instr => "REGEXP_INSTR",
        RegexpScalarKind.Replace => "REGEXP_REPLACE",
        _ => "REGEXP_SUBSTR",
    };

    /// <summary>
    /// Parses the argument list with the cursor just past the function name's
    /// <c>(</c>-introducing token, enforcing real's per-function arity through
    /// Msg 189.
    /// </summary>
    public static RegexpScalar ParseCall(ParserContext context, RegexpScalarKind kind)
    {
        var maximum = kind switch
        {
            RegexpScalarKind.Count => 4,
            RegexpScalarKind.Instr => 7,
            _ => 6,
        };
        List<Expression> parsed = [Expression.Parse(context)];
        while (context.Token is Tokens.Operator { Character: ',' })
            parsed.Add(Expression.Parse(context.MoveNextRequiredReturnSelf()));

        return parsed.Count is < 2 || parsed.Count > maximum
            ? throw SimulatedSqlException.FunctionArgumentCountRange(LowerNameFor(kind), 2, maximum)
            : new RegexpScalar(kind, [.. parsed]);
    }

    private Expression? Argument(int index) => index < this.arguments.Length ? this.arguments[index] : null;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var lower = LowerNameFor(this.kind);
        var input = RegexpArguments.ReadStringArgument(this.arguments[0], runtime, lower, argumentIndex: 1);
        var pattern = RegexpArguments.ReadStringArgument(this.arguments[1], runtime, lower, argumentIndex: 2);
        var resultType = this.ResolveResultType(input.Type, runtime.Batch);

        // REGEXP_REPLACE's replacement sits at argument 3, shifting its own
        // numeric arguments one slot right relative to the other three. The
        // two-argument form defaults it to the empty string, so the call
        // deletes every match.
        var replacement = string.Empty;
        var replacementIsNull = false;
        if (this.kind == RegexpScalarKind.Replace && this.Argument(2) is { } replacementExpression)
        {
            var value = RegexpArguments.ReadStringArgument(replacementExpression, runtime, lower, argumentIndex: 3);
            replacementIsNull = value.IsNull;
            replacement = replacementIsNull ? string.Empty : value.AsString;
        }

        if (input.IsNull || pattern.IsNull || replacementIsNull)
            return SqlValue.Null(resultType);

        var offset = this.kind == RegexpScalarKind.Replace ? 1 : 0;
        var start = 1;
        if (this.Argument(2 + offset) is not null && !RegexpArguments.TryReadNumericArgument(this.Argument(2 + offset), runtime, out start))
            return SqlValue.Null(resultType);
        var occurrence = this.kind == RegexpScalarKind.Replace ? 0 : 1;
        if (this.kind != RegexpScalarKind.Count
            && this.Argument(3 + offset) is not null
            && !RegexpArguments.TryReadNumericArgument(this.Argument(3 + offset), runtime, out occurrence))
        {
            return SqlValue.Null(resultType);
        }

        // REGEXP_INSTR interleaves return_option (4) and group (6) around its
        // flags argument (5); the other members put flags last but for SUBSTR's
        // trailing group (5).
        var returnOption = 0;
        var group = 0;
        var flagsIndex = this.kind switch
        {
            RegexpScalarKind.Count => 3,
            RegexpScalarKind.Instr => 5,
            RegexpScalarKind.Replace => 5,
            _ => 4,
        };
        if (this.kind == RegexpScalarKind.Instr)
        {
            // A NULL return_option is the family's one non-propagating
            // argument — real reads it as the default 0.
            _ = RegexpArguments.TryReadNumericArgument(this.Argument(4), runtime, out returnOption);
            if (this.Argument(6) is not null && !RegexpArguments.TryReadNumericArgument(this.Argument(6), runtime, out group))
                return SqlValue.Null(resultType);
        }
        else if (this.kind == RegexpScalarKind.Substr
            && this.Argument(5) is not null
            && !RegexpArguments.TryReadNumericArgument(this.Argument(5), runtime, out group))
        {
            return SqlValue.Null(resultType);
        }

        this.ValidateNumericArguments(start, occurrence, returnOption, group);

        if (!RegexpArguments.TryReadFlags(this.Argument(flagsIndex), runtime, lower, flagsIndex + 1, out var flags))
            return SqlValue.Null(resultType);

        var patternText = pattern.AsString;
        var text = input.AsString;
        var startIndex = start - 1;

        // An empty pattern makes REGEXP_REPLACE a no-op on real even though it
        // matches at every position for the other three members.
        if (this.kind == RegexpScalarKind.Replace && patternText.Length == 0)
            return SqlValue.FromString(resultType, text);

        var regex = RegexDialect.Compile(patternText, flags, RegexCallSite.Scalar);
        return this.kind switch
        {
            RegexpScalarKind.Count => SqlValue.FromInt32(RegexpArguments.Matches(regex, text, startIndex).Count()),
            RegexpScalarKind.Instr => SqlValue.FromInt32(Position(regex, text, startIndex, occurrence, returnOption, group)),
            RegexpScalarKind.Replace => SqlValue.FromString(resultType, Truncate(Replace(regex, text, replacement, startIndex, occurrence), resultType)),
            _ => Substring(regex, text, startIndex, occurrence, group, resultType),
        };
    }

    /// <summary>
    /// Applies each numeric argument's Msg 19301 gate. The reported minimum and
    /// the enforced bound differ for two of them, mirroring real's own wording.
    /// </summary>
    private void ValidateNumericArguments(int start, int occurrence, int returnOption, int group)
    {
        var upper = this.UpperName;
        switch (this.kind)
        {
            case RegexpScalarKind.Count:
                RegexpArguments.RequireAtLeast(start, 1, 1, "START", upper, 1);
                break;
            case RegexpScalarKind.Instr:
                RegexpArguments.RequireAtLeast(start, 1, 1, "START", upper, 3);
                RegexpArguments.RequireAtLeast(occurrence, 1, 1, "OCCURRENCE", upper, 4);
                // Real accepts only 0 and 1 here but words the rejection as a
                // lower bound, so an out-of-set value reports the same text.
                if (returnOption is not (0 or 1))
                    throw SimulatedSqlException.RegexArgumentBelowMinimum("RETURN_OPTION", 0, returnOption, upper, 6);
                RegexpArguments.RequireAtLeast(group, 0, 1, "GROUP", upper, 5);
                break;
            case RegexpScalarKind.Replace:
                RegexpArguments.RequireAtLeast(start, 1, 1, "START", upper, 1);
                RegexpArguments.RequireAtLeast(occurrence, 0, 0, "OCCURRENCE", upper, 2);
                break;
            default:
                RegexpArguments.RequireAtLeast(start, 1, 1, "START", upper, 7);
                RegexpArguments.RequireAtLeast(occurrence, 1, 1, "OCCURRENCE", upper, 8);
                RegexpArguments.RequireAtLeast(group, 0, 0, "GROUP", upper, 9);
                break;
        }
    }

    /// <summary>
    /// <c>REGEXP_INSTR</c>'s result: the 1-based position of the requested
    /// occurrence's group, or one past its last character when
    /// <paramref name="returnOption"/> is 1. No match — or a group the pattern
    /// doesn't have — reports 0.
    /// </summary>
    private static int Position(Regex regex, string text, int startIndex, int occurrence, int returnOption, int group)
    {
        var match = RegexpArguments.NthMatch(regex, text, startIndex, occurrence);
        return match is null || RegexpArguments.CaptureGroup(match, group) is not { } captured
            ? 0
            : returnOption == 0 ? captured.Index + 1 : captured.Index + captured.Length + 1;
    }

    /// <summary>
    /// <c>REGEXP_SUBSTR</c>'s result: the matched text of the requested group,
    /// or NULL when there's no such occurrence or group.
    /// </summary>
    private static SqlValue Substring(Regex regex, string text, int startIndex, int occurrence, int group, SqlType resultType)
    {
        var match = RegexpArguments.NthMatch(regex, text, startIndex, occurrence);
        return match is not null && RegexpArguments.CaptureGroup(match, group) is { } captured
            ? SqlValue.FromString(resultType, captured.Value)
            : SqlValue.Null(resultType);
    }

    /// <summary>
    /// Clips a grown <c>REGEXP_REPLACE</c> result to its declared width. Real
    /// truncates silently rather than raising Msg 8152 — probe-confirmed: a
    /// 5000-character <c>varchar</c> input whose every character doubles comes
    /// back 8000 characters long, and the <c>nvarchar</c> form comes back 4000.
    /// A MAX-form input has no bound.
    /// </summary>
    private static string Truncate(string value, SqlType resultType) =>
        StringScalars.IsMaxForm(resultType) || value.Length <= StringScalars.FamilyCap(resultType)
            ? value
            : value[..StringScalars.FamilyCap(resultType)];

    /// <summary>
    /// <c>REGEXP_REPLACE</c>'s result. Text before <paramref name="startIndex"/>
    /// is preserved verbatim; <paramref name="occurrence"/> 0 replaces every
    /// match from there on, any other value replaces exactly that one.
    /// </summary>
    private static string Replace(Regex regex, string text, string replacement, int startIndex, int occurrence)
    {
        var result = new StringBuilder(text[..Math.Min(startIndex, text.Length)]);
        var copiedTo = Math.Min(startIndex, text.Length);
        var seen = 0;
        foreach (var match in RegexpArguments.Matches(regex, text, startIndex))
        {
            seen++;
            if (occurrence != 0 && seen != occurrence)
                continue;
            _ = result.Append(text, copiedTo, match.Index - copiedTo);
            AppendExpandedReplacement(result, replacement, match);
            copiedTo = match.Index + match.Length;
            if (occurrence != 0)
                break;
        }
        return result.Append(text, copiedTo, text.Length - copiedTo).ToString();
    }

    /// <summary>
    /// Expands the Oracle-style replacement grammar: <c>\1</c>…<c>\9</c> insert
    /// a capture group (empty when the pattern has no such group), <c>\\</c> is
    /// one literal backslash, and every other backslash escape — including
    /// <c>\0</c> and a trailing lone backslash — is copied through unchanged.
    /// <c>$</c> carries no meaning.
    /// </summary>
    private static void AppendExpandedReplacement(StringBuilder result, string replacement, Match match)
    {
        for (var i = 0; i < replacement.Length; i++)
        {
            if (replacement[i] != '\\' || i + 1 >= replacement.Length)
            {
                _ = result.Append(replacement[i]);
                continue;
            }
            var next = replacement[i + 1];
            if (next == '\\')
            {
                _ = result.Append('\\');
                i++;
                continue;
            }
            if (next is < '1' or > '9')
            {
                _ = result.Append(replacement[i]);
                continue;
            }
            var group = next - '0';
            if (group < match.Groups.Count && match.Groups[group].Success)
                _ = result.Append(match.Groups[group].Value);
            i++;
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        this.ResolveResultType(this.arguments[0].GetSqlType(batch, resolveColumnType), batch);

    /// <summary>
    /// COUNT / INSTR project <c>int</c>. REPLACE can grow, so it projects the
    /// input family's container width; SUBSTR can only shrink, so it keeps the
    /// input's declared width. Either carries MAX through unchanged.
    /// </summary>
    private SqlType ResolveResultType(SqlType inputType, BatchContext batch)
    {
        if (this.kind is RegexpScalarKind.Count or RegexpScalarKind.Instr)
            return SqlType.Int32;
        if (StringScalars.IsMaxForm(inputType))
            return inputType;
        if (this.kind == RegexpScalarKind.Replace)
            return StringScalars.ContainerResultType(inputType, batch);
        var width = StringScalars.DeclaredWidth(inputType);
        return width > 0
            ? StringScalars.SizedResultType(inputType, width, batch)
            : StringScalars.ContainerResultType(inputType, batch);
    }

    internal override string DebugDisplay() =>
        $"{this.UpperName}({string.Join(", ", this.arguments.Select(a => a.DebugDisplay()))})";
}
