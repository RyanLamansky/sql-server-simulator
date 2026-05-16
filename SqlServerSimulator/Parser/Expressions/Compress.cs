using System.IO.Compression;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>COMPRESS(expression)</c>: gzip-compresses a binary or character
/// argument and returns the deflated bytes as <c>varbinary(max)</c>. The
/// inverse of <see cref="Decompress"/>. SQL NULL input → SQL NULL output.
/// </summary>
/// <remarks>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/compress-transact-sql.
/// String inputs are encoded as UTF-16 LE (nchar/nvarchar) or CP1252
/// (char/varchar) before compression — matches real SQL Server's
/// observed-on-wire bytes when the column is one of those types. Empty
/// input still produces a valid (small) gzip stream so DECOMPRESS round-
/// trips correctly. Default GZipStream level is sufficient — real SQL
/// Server doesn't expose the compression level either.
/// </remarks>
internal sealed class Compress(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.Varbinary);

        var inputBytes = ExtractBytes(value);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(inputBytes, 0, inputBytes.Length);
        }
        return SqlValue.FromVarbinary(output.ToArray());
    }

    private static byte[] ExtractBytes(SqlValue value)
    {
        if (value.Type is VarbinarySqlType or BinarySqlType or ImageSqlType)
            return value.AsBytes;
        if (value.Type is NVarcharSqlType or NCharSqlType or NTextSqlType or SystemNameSqlType)
            return System.Text.Encoding.Unicode.GetBytes(value.AsString);
        // varchar / char / text → CP1252 to match SQL Server's column storage
        // (use the cached encoder hanging off CharSqlType so the CP1252 code-
        // pages provider is registered exactly once).
        if (value.Type is VarcharSqlType or CharSqlType or TextSqlType)
            return CharSqlType.Cp1252Encoder.GetBytes(value.AsString);
        // Fall-through: coerce via the value's string form (numeric / etc.).
        return System.Text.Encoding.Unicode.GetBytes(value.AsString);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Varbinary;

    internal override string DebugDisplay() => $"COMPRESS({this.operand.DebugDisplay()})";
}
