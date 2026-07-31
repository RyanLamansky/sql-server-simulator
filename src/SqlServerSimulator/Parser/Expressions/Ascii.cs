using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ASCII(character_expression)</c>: returns the first byte of the
/// first character of the input, encoded in the argument collation's code
/// page.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>NULL input → NULL.</item>
/// <item>Empty string → NULL.</item>
/// <item>Unicode input (<c>N'…'</c>) is converted to the collation's code page before the byte is read; representable characters like <c>N'€'</c> return their CP1252 byte (128 for €); unrepresentable Unicode (emoji etc.) returns 63 via the <c>'?'</c> replacement fallback.</item>
/// <item>The code page is the argument's, not always CP1252: <c>ASCII</c> of a <c>Turkish_CI_AS</c> column holding <c>Ğ</c> is 208 (its CP1254 byte), where CP1252 would give 63.</item>
/// <item>Under a DBCS code page the result is the <em>first</em> byte of a two-byte character — <c>ASCII</c> of a <c>Japanese_XJIS_140_CI_AS</c> column holding <c>こ</c> (CP932 <c>0x82B1</c>) is 130.</item>
/// <item>Non-string inputs (int / decimal / etc.) are implicitly converted to <c>varchar</c> first, so <c>ASCII(65)</c> returns 54 (the ASCII byte for <c>'6'</c>, the first char of the string <c>"65"</c>) — not 65.</item>
/// </list>
/// </remarks>
internal sealed class Ascii(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = source.Run(runtime);
        StringScalars.RejectLegacyLob(v, "ascii");
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var isString = SqlType.IsStringCategory(v.Type);
        var s = isString ? v.AsString : v.CoerceTo(SqlType.Varchar).AsString;
        if (s.Length == 0)
            return SqlValue.Null(SqlType.Int32);
        // Widest single-character encoding across the supported code pages is
        // 4 bytes (UTF-8 astral, which a lone surrogate can't reach anyway).
        Span<byte> encoded = stackalloc byte[4];
        var encoding = (isString ? v.Type.Collation : null) ?? Collation.Baseline;
        var written = encoding.StorageEncoding.GetBytes(s.AsSpan(0, 1), encoded);
        return SqlValue.FromInt32(written == 0 ? 0 : encoded[0]);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"ASCII({this.source.DebugDisplay()})";
}
