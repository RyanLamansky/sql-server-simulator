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
    private readonly Expression code = Parse(context);

    private static NCharSqlType NChar1For(BatchContext batch) =>
        NCharSqlType.Get(1, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var nchar1 = NChar1For(runtime.Batch);
        var v = code.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(nchar1);
        var n = StringScalars.CoerceLengthArgument(v);
        return n is < 0 or > 65535
            ? SqlValue.Null(nchar1)
            : SqlValue.FromNChar(nchar1, ((char)n).ToString());
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => NChar1For(batch);

    internal override string DebugDisplay() => $"NCHAR({this.code.DebugDisplay()})";
}
