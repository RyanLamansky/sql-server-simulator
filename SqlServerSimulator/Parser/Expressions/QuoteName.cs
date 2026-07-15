using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>QUOTENAME(name [, delimiter])</c>: surrounds the input with a
/// delimiter pair and doubles the closing delimiter character inside the
/// body to escape it. Returns <c>nvarchar(258)</c> regardless of input
/// collation — probe-confirmed.
/// </summary>
/// <remarks>
/// Probe-confirmed quirks (SQL Server 2025):
/// <list type="bullet">
/// <item><description>Default delimiter is <c>[</c>; the body's <c>]</c> gets doubled.</description></item>
/// <item><description>Delimiter pairs: <c>[</c>↔<c>]</c>, <c>(</c>↔<c>)</c>, <c>&lt;</c>↔<c>&gt;</c>, <c>{</c>↔<c>}</c>. Either side of a pair selects the pair. The <em>closing</em> character is what gets doubled inside the body — e.g. <c>QUOTENAME('a)b', '(')</c> → <c>(a))b)</c>.</description></item>
/// <item><description>Single-character delimiters that double as their own close: <c>"</c>, <c>'</c>, backtick.</description></item>
/// <item><description>Multi-character delimiter argument: first character wins.</description></item>
/// <item><description>NULL input or NULL delimiter → NULL. Unsupported delimiter character → NULL.</description></item>
/// <item><description>Input length &gt; 128 chars → NULL.</description></item>
/// </list>
/// </remarks>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/quotename-transact-sql</remarks>
internal sealed class QuoteName : Expression
{
    private const int MaxInputLength = 128;
    private readonly Expression name;
    private readonly Expression? delimiter;

    public QuoteName(ParserContext context)
    {
        this.name = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
            this.delimiter = Parse(context.MoveNextRequiredReturnSelf());
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var nameValue = this.name.Run(runtime);
        // The result carries the input argument's collation and coercibility
        // (real SQL Server propagates the operand's collation through string
        // functions). Pinning to the neutral SqlType.NVarchar instead would
        // give a coercible-default Baseline value that collides with a
        // database-default-collation literal under Msg 468 when the two are
        // concatenated (SMO's Urn: 'text' + QUOTENAME(sys-catalog sysname)).
        var resultType = ResultType(nameValue.Type);
        if (nameValue.IsNull)
            return SqlValue.Null(resultType);

        char open, close;
        if (this.delimiter is null)
        {
            (open, close) = ('[', ']');
        }
        else
        {
            var delimValue = this.delimiter.Run(runtime);
            if (delimValue.IsNull)
                return SqlValue.Null(resultType);

            // Multi-char delimiter argument: SQL Server picks the first character
            // (probe-confirmed: '<<' selects '<' which pairs with '>').
            var delimString = SqlType.IsStringCategory(delimValue.Type)
                ? delimValue.AsString
                : delimValue.CoerceTo(SqlType.NVarchar).AsString;
            if (delimString.Length == 0)
                return SqlValue.Null(resultType);

            if (!TryResolveDelimiterPair(delimString[0], out open, out close))
                return SqlValue.Null(resultType);
        }

        var nameString = nameValue.AsString;
        if (nameString.Length > MaxInputLength)
            return SqlValue.Null(resultType);

        // Closing-character doubling: only matters when open != close; when
        // they're identical (e.g. " or '), the same character gets doubled
        // anyway, so the same code path is correct without branching.
        var doubled = nameString.Contains(close, StringComparison.Ordinal)
            ? nameString.Replace(close.ToString(), $"{close}{close}", StringComparison.Ordinal)
            : nameString;
        return SqlValue.FromNVarchar(resultType, $"{open}{doubled}{close}");
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
        ResultType(this.name.GetSqlType(batch, resolveColumnType));

    /// <summary>
    /// <c>nvarchar(258)</c> carrying the input argument's collation and
    /// coercibility (Baseline / coercible-default for non-string inputs).
    /// </summary>
    private static NVarcharSqlType ResultType(SqlType inputType) =>
        NVarcharSqlType.Get(ResultLength, inputType.Collation ?? Collation.Baseline, inputType.Coercibility);

    private const int ResultLength = 258;

    /// <summary>
    /// Maps the user-supplied delimiter character to the <c>(open, close)</c>
    /// pair used for wrapping. Returns false for unrecognized characters
    /// (caller surfaces NULL). The 7 supported delimiter chars are
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    private static bool TryResolveDelimiterPair(char c, out char open, out char close)
    {
        switch (c)
        {
            case '[' or ']': open = '['; close = ']'; return true;
            case '(' or ')': open = '('; close = ')'; return true;
            case '<' or '>': open = '<'; close = '>'; return true;
            case '{' or '}': open = '{'; close = '}'; return true;
            case '"': open = '"'; close = '"'; return true;
            case '\'': open = '\''; close = '\''; return true;
            case '`': open = '`'; close = '`'; return true;
            default: open = '\0'; close = '\0'; return false;
        }
    }

    internal override string DebugDisplay() => this.delimiter is null
        ? $"QUOTENAME({this.name.DebugDisplay()})"
        : $"QUOTENAME({this.name.DebugDisplay()}, {this.delimiter.DebugDisplay()})";
}
