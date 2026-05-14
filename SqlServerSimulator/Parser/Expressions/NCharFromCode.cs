using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>NCHAR(integer_expression)</c>: returns the single UTF-16 character
/// corresponding to the input code unit (0–65535), as <c>nchar(1)</c>.
/// Mirror of <see cref="CharFromCode"/>'s range / coercion / NULL rules,
/// scaled to BMP code units.
/// </summary>
/// <remarks>
/// Probe-confirmed against SQL Server 2025 (2026-05-14):
/// <list type="bullet">
/// <item>Result type is <c>nchar(1)</c> — <c>sql_variant_property</c> returns <c>'nchar'</c>; <c>datalength</c> is 2 (one UTF-16 code unit).</item>
/// <item>Out-of-range inputs (negative, or above 65535) return NULL under the default non-SC collation. Supplementary code points like <c>NCHAR(128512)</c> (😀) return NULL rather than the surrogate pair — matches the simulator's "default collation only" stance; an SC-collation-aware variant returning <c>nvarchar(2)</c> would need explicit collation modeling.</item>
/// <item>Non-integer inputs implicitly truncate-to-int (<c>NCHAR('65')</c> → 'A').</item>
/// </list>
/// </remarks>
internal sealed class NCharFromCode(ParserContext context) : Expression
{
    private static readonly NCharSqlType NChar1 = NCharSqlType.Get(1);
    private readonly Expression code = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = code.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(NChar1);
        var n = v.CoerceTo(SqlType.Int32).AsInt32;
        return n is < 0 or > 65535
            ? SqlValue.Null(NChar1)
            : SqlValue.FromNChar(NChar1, ((char)n).ToString());
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => NChar1;

    internal override string DebugDisplay() => $"NCHAR({this.code.DebugDisplay()})";
}
