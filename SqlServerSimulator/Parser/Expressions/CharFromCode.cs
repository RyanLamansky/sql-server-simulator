using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>CHAR(integer_expression)</c>: returns the single CP1252 character
/// corresponding to the input byte code (0–255), as <c>char(1)</c>.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>Result type is <c>char(1)</c>, not <c>varchar(1)</c> — <c>sql_variant_property(CHAR(65), 'basetype')</c> returns <c>'char'</c>.</item>
/// <item>NULL input → NULL.</item>
/// <item>Out-of-range input (negative or &gt; 255) → NULL.</item>
/// <item>Non-integer inputs (decimal / float / string of digits) are implicitly converted to int with truncation, so <c>CHAR(65.7)</c> and <c>CHAR('65')</c> both return <c>'A'</c>.</item>
/// <item><c>CHAR(0)</c> returns a single <c>0x00</c> byte (the NUL character), not NULL — common idiom for embedding null bytes.</item>
/// </list>
/// </remarks>
internal sealed class CharFromCode(ParserContext context) : Expression
{
    private static readonly CharSqlType Char1 = CharSqlType.Get(1);
    private readonly Expression code = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = code.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(Char1);
        var n = v.CoerceTo(SqlType.Int32).AsInt32;
        if (n is < 0 or > 255)
            return SqlValue.Null(Char1);
        ReadOnlySpan<byte> oneByte = [(byte)n];
        return SqlValue.FromChar(Char1, CharSqlType.Cp1252Encoder.GetString(oneByte));
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => Char1;

    internal override string DebugDisplay() => $"CHAR({this.code.DebugDisplay()})";
}
