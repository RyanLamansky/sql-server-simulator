using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using System.Collections.Frozen;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// Computes <c>sys.sql_modules.inline_type</c> and
/// <c>sys.sql_modules.is_inlineable</c> — the scalar-UDF-inlining pair real
/// SQL Server reports for every module row.
/// </summary>
/// <remarks>
/// <para>
/// <b>The shape.</b> Probe-confirmed against SQL Server 2025: an inline TVF
/// reports 1 / 1, a plain scalar function reports 1 / 1, and a procedure,
/// view, DML or DDL trigger, or multi-statement TVF reports 0 / 0. Neither
/// column is compatibility-level gated — a scalar function created at level
/// 140 still reports 1 / 1, and lowering the level after the fact doesn't move
/// it; the level gates whether the optimizer actually inlines, not what the
/// catalog records.
/// </para>
/// <para>
/// <b>The split between the two.</b> <c>is_inlineable</c> answers whether the
/// body <em>could</em> be inlined; <c>inline_type</c> answers whether it
/// <em>would</em> be. They part only on <c>WITH INLINE = OFF</c>, which reports
/// 0 / 1 — an option the simulator's <c>CREATE FUNCTION</c> grammar doesn't
/// accept, so the two answers coincide here.
/// </para>
/// <para>
/// <b>The covered disqualifier subset.</b> Real's full rule list is long and
/// version-dependent; the analysis below covers the constructs probed against
/// SQL Server 2025, and a body whose only disqualifying construct sits outside
/// that set reports 1 where real reports 0. Covered:
/// </para>
/// <list type="bullet">
/// <item><description>a time-dependent intrinsic — <c>GETDATE</c>,
/// <c>GETUTCDATE</c>, <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>,
/// <c>SYSDATETIMEOFFSET</c>, <c>CURRENT_TIMESTAMP</c> (the other session and
/// metadata scalars — <c>@@SPID</c>, <c>USER_ID</c>, <c>OBJECT_ID</c>,
/// <c>ERROR_NUMBER</c> — all probed inlineable);</description></item>
/// <item><description><c>@@ROWCOUNT</c>;</description></item>
/// <item><description>more than one <c>RETURN</c> statement;</description></item>
/// <item><description>a <c>WHILE</c> loop;</description></item>
/// <item><description>a table variable (<c>DECLARE @t TABLE</c>);</description></item>
/// <item><description>recursion — the body naming the function itself;</description></item>
/// <item><description>a non-<c>CALLER</c> <c>WITH EXECUTE AS</c> clause
/// (<c>CALLER</c> itself probed inlineable);</description></item>
/// <item><description>an XML data-type method (<c>.value()</c> /
/// <c>.nodes()</c> / <c>.query()</c> / <c>.exist()</c> /
/// <c>.modify()</c>);</description></item>
/// <item><description>variable accumulation in a <c>SELECT</c> that reads a
/// table — <c>SELECT @v = @v + col FROM t</c>. Plain assignment
/// (<c>SELECT @v = col FROM t</c>) and self-reference without a <c>FROM</c>
/// (<c>SELECT @v = @v + 1</c>) are both probed inlineable, so the rule needs
/// both halves.</description></item>
/// </list>
/// <para>
/// Probed <em>inlineable</em> despite looking otherwise, so deliberately not
/// disqualifying: <c>WITH SCHEMABINDING</c>, reading a table, <c>IF</c> /
/// <c>ELSE</c>, <c>CASE</c>, multiple <c>DECLARE</c>s and <c>SET</c>s, a
/// nested <c>BEGIN</c> / <c>END</c>, <c>TOP</c> with <c>ORDER BY</c>, a
/// subquery aggregate, <c>WITH RETURNS NULL ON NULL INPUT</c>, and calling
/// another user function — even one that is itself not inlineable
/// (inlineability is not transitive).
/// </para>
/// <para>
/// The scan re-tokenizes the stored body per read, the same mechanism
/// <see cref="ModuleDeterminism"/> uses and for the same reason: scalar bodies
/// are stored as source text, so there is no tree to visit at CREATE time.
/// </para>
/// </remarks>
internal static class ModuleInlining
{
    /// <summary>
    /// The intrinsics whose answer moves with the clock. Real refuses to
    /// inline a body reaching any of them; the rest of the nondeterministic
    /// catalog (session state, metadata lookups, the <c>ERROR_*</c> family)
    /// is probed inlineable, so this set is deliberately narrower than
    /// <see cref="ModuleDeterminism"/>'s.
    /// </summary>
    private static readonly FrozenSet<string> TimeDependentIntrinsics = new[]
    {
        "GETDATE",
        "GETUTCDATE",
        "SYSDATETIME",
        "SYSDATETIMEOFFSET",
        "SYSUTCDATETIME",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>xml</c> data-type methods. A body invoking one is not
    /// inlineable (probe-confirmed on <c>.value()</c>).
    /// </summary>
    private static readonly FrozenSet<string> XmlMethods = new[]
    {
        "exist",
        "modify",
        "nodes",
        "query",
        "value",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The <c>(inline_type, is_inlineable)</c> pair for
    /// <paramref name="module"/>'s <c>sys.sql_modules</c> row.
    /// </summary>
    internal static (bool InlineType, bool IsInlineable) Evaluate(SchemaObject module) => module switch
    {
        InlineTableValuedFunction => (true, true),
        // A CLR routine has no sys.sql_modules row at all, so it never
        // reaches here; the guard keeps the token scan off an empty body.
        ClrScalarFunction => (false, false),
        ScalarFunction scalar => IsInlineableScalar(scalar) ? (true, true) : (false, false),
        _ => (false, false),
    };

    private static bool IsInlineableScalar(ScalarFunction function) =>
        (function.ExecuteAsClause is null || function.ExecuteAsClause.Equals("CALLER", StringComparison.OrdinalIgnoreCase))
        && Scan(function.BodyText, function.Name);

    /// <summary>
    /// Re-tokenizes <paramref name="body"/> and reports whether it stayed
    /// clear of every covered disqualifier. <paramref name="functionName"/> is
    /// the function's own leaf name, so a body naming itself is caught as
    /// recursion.
    /// </summary>
    private static bool Scan(string body, string functionName)
    {
        List<Token> tokens = [];
        var index = 0;
        while (Tokenizer.NextToken(body, ref index, Collation.Baseline) is { } token)
        {
            if (token is not (Whitespace or Comment))
                tokens.Add(token);
        }

        var intrinsics = TimeDependentIntrinsics.GetAlternateLookup<ReadOnlySpan<char>>();
        var xmlMethods = XmlMethods.GetAlternateLookup<ReadOnlySpan<char>>();
        var returns = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case ReservedKeyword { Keyword: Keyword.Current_Timestamp }:
                case ReservedKeyword { Keyword: Keyword.While }:
                    return false;
                case ReservedKeyword { Keyword: Keyword.Return } when ++returns > 1:
                    return false;
                // DECLARE @t TABLE — the table-variable form. A scalar
                // DECLARE is followed by a type name, never the TABLE keyword.
                case ReservedKeyword { Keyword: Keyword.Declare }
                    when i + 2 < tokens.Count
                        && tokens[i + 1] is AtPrefixedString
                        && tokens[i + 2] is ReservedKeyword { Keyword: Keyword.Table }:
                    return false;
                case ReservedKeyword { Keyword: Keyword.Select } when SelectAccumulatesIntoVariable(tokens, i):
                    return false;
                case DoubleAtPrefixedString atat when atat.Span.Equals("ROWCOUNT", StringComparison.OrdinalIgnoreCase):
                    return false;
                case Name name:
                    {
                        var isCall = i + 1 < tokens.Count && tokens[i + 1] is Operator { Character: '(' };
                        var isMember = i > 0 && tokens[i - 1] is Operator { Character: '.' };
                        if (isCall && isMember && xmlMethods.Contains(name.Span))
                            return false;
                        // An unqualified call is always a built-in: real
                        // rejects a bare user-function call outright.
                        if (isCall && !isMember && intrinsics.Contains(name.Span))
                            return false;
                        // Recursion: the body naming the function itself,
                        // necessarily through a schema qualifier.
                        if (isMember && Collation.Baseline.Equals(name.Value, functionName))
                            return false;
                        break;
                    }
            }
        }

        return true;
    }

    /// <summary>
    /// Whether the <c>SELECT</c> whose keyword sits at
    /// <paramref name="selectIndex"/> accumulates into a variable while
    /// reading a table — real's <c>SELECT @v = @v + col FROM t</c>
    /// disqualifier. Both halves are required: the select list must assign to
    /// a variable it also reads, and the statement must carry a <c>FROM</c>.
    /// </summary>
    private static bool SelectAccumulatesIntoVariable(List<Token> tokens, int selectIndex)
    {
        // The select list runs to the statement's own FROM (depth 0 — a
        // subquery's FROM sits inside parens) or to the statement's end.
        var depth = 0;
        var listEnd = tokens.Count;
        var hasFrom = false;
        for (var i = selectIndex + 1; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case Operator { Character: '(' }:
                    depth++;
                    continue;
                case Operator { Character: ')' }:
                    depth--;
                    continue;
                case Operator { Character: ';' } when depth == 0:
                    listEnd = i;
                    break;
                case ReservedKeyword { Keyword: Keyword.From } when depth == 0:
                    listEnd = i;
                    hasFrom = true;
                    break;
                default:
                    continue;
            }

            break;
        }

        if (!hasFrom)
            return false;

        // An assignment `@v = … @v …` inside the select list: the same
        // variable on both sides of one `=`.
        for (var i = selectIndex + 1; i + 1 < listEnd; i++)
        {
            if (tokens[i] is not AtPrefixedString assigned || tokens[i + 1] is not Operator { Character: '=' })
                continue;
            for (var j = i + 2; j < listEnd; j++)
            {
                if (tokens[j] is Operator { Character: ',' })
                    break;
                if (tokens[j] is AtPrefixedString read && read.Span.SequenceEqual(assigned.Span))
                    return true;
            }
        }

        return false;
    }
}
