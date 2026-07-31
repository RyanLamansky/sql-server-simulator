using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>UNICODE(ncharacter_expression)</c>: returns the first character's
/// 16-bit code-unit value under non-<c>_SC_</c> collations and the full
/// Unicode codepoint (combining a leading surrogate pair into its 32-bit
/// scalar) under <c>_SC_</c> collations. Mirror of <see cref="Ascii"/> in
/// input-handling shape; the only divergence is the unit of extraction.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-21):
/// <list type="bullet">
/// <item>NULL → NULL; empty string → NULL.</item>
/// <item>Non-string inputs implicitly stringify, so <c>UNICODE(65)</c> returns 54 (<c>'6'</c>) not 65.</item>
/// <item><c>UNICODE(N'😀')</c> returns 55357 (the high surrogate, U+D83D) under non-SC collations and 128512 (U+1F600, the full codepoint) under <c>_SC_</c>.</item>
/// </list>
/// </remarks>
internal sealed class UnicodeCodepoint(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = source.Run(runtime);
        StringScalars.RejectLegacyLob(v, "unicode");
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var s = SqlType.IsStringCategory(v.Type) ? v.AsString : v.CoerceTo(SqlType.Varchar).AsString;
        return s.Length == 0
            ? SqlValue.Null(SqlType.Int32)
            : SqlValue.FromInt32(v.Type.Collation?.IsSupplementaryCharacterAware == true
                ? SupplementaryCharacters.LeadingCodepoint(s)
                : s[0]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"UNICODE({this.source.DebugDisplay()})";
}
