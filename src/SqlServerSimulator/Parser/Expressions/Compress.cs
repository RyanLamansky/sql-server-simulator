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
/// input compresses to no bytes at all, as real's does. Real's deflate
/// encoder isn't zlib's, so past a few bytes the compressed payload differs
/// though it inflates to the same input (probed 2026-09-25 against SQL
/// Server 2025).
/// </remarks>
internal sealed class Compress(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.VarbinaryMax);

        var inputBytes = ExtractBytes(value);
        if (inputBytes.Length == 0)
            return SqlValue.FromVarbinary(SqlType.VarbinaryMax, []);
        using var output = new MemoryStream();
        using (var gz = new GZipStream(output, CompressionMode.Compress, leaveOpen: true))
        {
            gz.Write(inputBytes, 0, inputBytes.Length);
        }
        // Real's header names the fastest compression (XFL 4) and a FAT
        // file system (OS 0) where zlib's names neither and Unix (probed
        // 2026-09-25 against SQL Server 2025).
        var compressed = output.ToArray();
        compressed[8] = 4;
        compressed[9] = 0;
        return SqlValue.FromVarbinary(SqlType.VarbinaryMax, compressed);
    }

    private static byte[] ExtractBytes(SqlValue value)
    {
        if (value.Type is VarbinarySqlType or BinarySqlType or ImageSqlType)
            return value.AsBytes;
        if (value.Type is NVarcharSqlType or NCharSqlType or NTextSqlType or SystemNameSqlType)
            return SystemNameSqlType.Utf16LeBytes(value.AsString);
        // varchar / char / text → the collation's own code page, matching the
        // bytes SQL Server stores and therefore compresses.
        if (value.Type is VarcharSqlType or CharSqlType or TextSqlType)
            return (value.Type.Collation ?? Collation.Baseline).StorageEncoding.GetBytes(value.AsString);
        // Every other type was refused while compiling.
        return SystemNameSqlType.Utf16LeBytes(value.AsString);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.RequireStringArgument(this.operand, this.operand.GetSqlType(batch, resolveColumnType), "Compress", 1, acceptsBinary: true, acceptsLegacyLob: false);
        return SqlType.VarbinaryMax;
    }

    internal override string DebugDisplay() => $"COMPRESS({this.operand.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.operand);
}
