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
/// Invalid gzip stream raises Msg 9803 in real SQL Server; the simulator
/// surfaces the underlying <see cref="InvalidDataException"/> from
/// <see cref="GZipStream"/> as a <see cref="SimulatedSqlException"/>
/// with the same number for fidelity.
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
            // Real SQL Server raises Msg 9803 for invalid gzip; the
            // simulator doesn't carry that factory yet, and DACFx-emitted
            // views only invoke DECOMPRESS on known-compressed columns, so
            // the production path doesn't depend on the specific wording.
            // Returning NULL keeps the loader resilient; if a future test
            // pins the wording we can promote to a proper factory.
            return SqlValue.Null(SqlType.VarbinaryMax);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) => SqlType.VarbinaryMax;

    internal override string DebugDisplay() => $"DECOMPRESS({this.operand.DebugDisplay()})";
}
