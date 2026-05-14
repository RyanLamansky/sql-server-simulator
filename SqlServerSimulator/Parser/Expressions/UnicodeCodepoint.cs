using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>UNICODE(ncharacter_expression)</c>: returns the UTF-16 code-unit
/// value of the first character of the input. Mirror of <see cref="Ascii"/>
/// in input-handling shape; the only divergence is the byte vs code-unit
/// extraction step.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>NULL → NULL; empty string → NULL.</item>
/// <item>Non-string inputs implicitly stringify, so <c>UNICODE(65)</c> returns 54 (<c>'6'</c>) not 65.</item>
/// <item>Supplementary code points (above U+FFFF, e.g. <c>N'😀'</c>) under the default non-SC collation return the high surrogate value (55357 for 😀), not the full Unicode code point — matches the simulator's "default collation only" stance documented in CLAUDE.md. An SC-collation-aware variant returning 128512 would need explicit collation modeling.</item>
/// </list>
/// </remarks>
internal sealed class UnicodeCodepoint(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = source.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var s = SqlType.IsStringCategory(v.Type) ? v.AsString : v.CoerceTo(SqlType.Varchar).AsString;
        return s.Length == 0 ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(s[0]);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    internal override string DebugDisplay() => $"UNICODE({this.source.DebugDisplay()})";
}
