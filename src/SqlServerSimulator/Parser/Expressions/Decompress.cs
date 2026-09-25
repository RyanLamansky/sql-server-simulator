using System.IO.Compression;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>DECOMPRESS(varbinary)</c>: gunzips a <c>varbinary</c> argument
/// previously produced by <c>COMPRESS</c> (or any standards-compliant gzip
/// producer) and returns the inflated bytes as <c>varbinary(max)</c>.
/// SQL NULL input → SQL NULL output.
/// </summary>
/// <remarks>
/// Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/decompress-transact-sql.
/// Real SQL Server pairs this with <c>COMPRESS</c> (gzip-compress). Output is
/// always the raw inflated bytes — callers cast to <c>nvarchar</c> /
/// <c>varchar</c> when the compressed payload was textual (WWI's
/// <c>Website.VehicleTemperatures</c> does <c>CAST(DECOMPRESS(…) AS nvarchar(1000))</c>).
/// Bytes that aren't a gzip stream, or whose trailer disagrees with the
/// contents, raise Msg 9826 — what <see cref="GZipStream"/> reports as an
/// <see cref="InvalidDataException"/>.
/// </remarks>
internal sealed class Decompress(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var value = this.operand.Run(runtime);
        if (value.IsNull)
            return SqlValue.Null(SqlType.VarbinaryMax);

        var compressed = value.AsBytes;
        // Probed 2026-09-25 against SQL Server 2025: no bytes decompress to
        // none, bytes that don't open with gzip's magic are corrupt, and a
        // stream too short to hold its own header and trailer is NULL — where
        // GZipStream answers the first two with nothing and the third with
        // what it could inflate.
        if (compressed.Length == 0)
            return SqlValue.FromVarbinary(SqlType.VarbinaryMax, []);
        if (compressed[0] != 0x1F || (compressed.Length > 1 && compressed[1] != 0x8B))
            throw SimulatedSqlException.CorruptedDataForDecompress();
        if (compressed.Length < 18)
            return SqlValue.Null(SqlType.VarbinaryMax);
        try
        {
            using var input = new MemoryStream(compressed, writable: false);
            using var gz = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gz.CopyTo(output);
            return SqlValue.FromVarbinary(SqlType.VarbinaryMax, output.ToArray());
        }
        catch (InvalidDataException)
        {
            throw SimulatedSqlException.CorruptedDataForDecompress();
        }
    }

    /// <summary>
    /// Only a binary decompresses — a string, an <c>image</c> or anything
    /// else is Msg 8116 while compiling (probed 2026-09-25 against SQL Server
    /// 2025).
    /// </summary>
    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var type = this.operand.GetSqlType(batch, resolveColumnType);
        if (type is not (BinarySqlType or VarbinarySqlType) && !IsUntypedNullLiteral(this.operand))
            throw SimulatedSqlException.InvalidArgumentDataType(SqlType.OperandName(type, this.operand), 1, "Decompress");
        return SqlType.VarbinaryMax;
    }

    internal override string DebugDisplay() => $"DECOMPRESS({this.operand.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.operand);
}
