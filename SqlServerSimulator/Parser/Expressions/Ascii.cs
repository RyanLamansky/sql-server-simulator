using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ASCII(character_expression)</c>: returns the CP1252 byte value
/// of the first character of the input.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>NULL input → NULL.</item>
/// <item>Empty string → NULL.</item>
/// <item>Unicode input (<c>N'…'</c>) is converted to CP1252 before the byte is read; representable characters like <c>N'€'</c> return their CP1252 byte (128 for €); unrepresentable Unicode (emoji etc.) returns 63 via CP1252's <c>'?'</c> replacement fallback (matches the simulator's existing CP1252 collation quirk).</item>
/// <item>Non-string inputs (int / decimal / etc.) are implicitly converted to <c>varchar</c> first, so <c>ASCII(65)</c> returns 54 (the ASCII byte for <c>'6'</c>, the first char of the string <c>"65"</c>) — not 65.</item>
/// </list>
/// </remarks>
internal sealed class Ascii(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = source.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var s = SqlType.IsStringCategory(v.Type) ? v.AsString : v.CoerceTo(SqlType.Varchar).AsString;
        if (s.Length == 0)
            return SqlValue.Null(SqlType.Int32);
        Span<byte> firstByte = stackalloc byte[1];
        _ = CharSqlType.Cp1252Encoder.GetBytes(s.AsSpan(0, 1), firstByte);
        return SqlValue.FromInt32(firstByte[0]);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"ASCII({this.source.DebugDisplay()})";
}
