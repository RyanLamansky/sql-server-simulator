using SqlServerSimulator.Network;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Byte-level regression guard for the NOT NULL fixed-width result-column wire
/// form. Real SQL Server 2025 (captured cleartext via a tee proxy, 2026-07-22)
/// sends a NOT NULL column of a fixed-width family as the FIXEDLENTYPE token
/// (INT4 / BIT / MONEY / DATETIME / …) — a single byte with no max-length byte,
/// flags 0x08 — and its ROW value raw with no length prefix. A nullable column
/// keeps the N-variant token (INTN / BITN / MONEYN / DATETIMN) plus a
/// max-length byte, flags 0x09, and its ROW value length-prefixed. SqlClient
/// tolerated the old always-N-variant form, but the native ODBC driver follows
/// the TDS spec and desyncs when a fixed token's value carries no prefix, so
/// the token must match real. Golden bytes for <c>int</c>:
/// <code>
/// NOT NULL  COLMETADATA 81 01 00 00000000 08 00 38    | ROW d1 05 00 00 00
/// nullable  COLMETADATA 81 01 00 00000000 09 00 26 04 | ROW d1 04 05 00 00 00
/// </code>
/// </summary>
[TestClass]
public sealed class FixedLenTokenWireTests
{
    /// <summary>One fixed-width family: its N-variant token, FIXEDLENTYPE token, and raw value width.</summary>
    private sealed record FixedFamily(string Label, SqlType Type, SqlValue Value, byte NVariantToken, byte FixedToken, byte Width);

    private static FixedFamily[] Families() =>
    [
        new("int", SqlType.Int32, SqlValue.FromInt32(5), 0x26, 0x38, 4),
        new("bigint", SqlType.BigInt, SqlValue.FromInt64(5), 0x26, 0x7F, 8),
        new("bit", SqlType.Bit, SqlValue.FromBoolean(true), 0x68, 0x32, 1),
        new("money", SqlType.Money, SqlValue.FromMoney(SqlType.Money, 1.00m), 0x6E, 0x3C, 8),
        new("datetime", SqlType.DateTime, SqlValue.FromDateTime(new DateTime(2020, 1, 2, 3, 4, 5)), 0x6F, 0x3D, 8),
    ];

    [TestMethod]
    public void NotNullFixedColumn_UsesFixedLenToken_NoLengthByte()
    {
        foreach (var f in Families())
        {
            var meta = ColMetadata(f.Type, notNull: true);
            AreEqual(0x81, meta[0], f.Label);            // COLMETADATA token
            AreEqual(0x01, meta[1], f.Label);            // one column
            AreEqual(0x08, meta[7], f.Label);            // flags: fNullable=0
            AreEqual(f.FixedToken, meta[9], f.Label);    // FIXEDLENTYPE token
            AreEqual((byte)1, meta[10], f.Label);        // next byte is the name length — no max-length byte
        }
    }

    [TestMethod]
    public void NullableFixedColumn_KeepsNVariantToken_WithLengthByte()
    {
        foreach (var f in Families())
        {
            var meta = ColMetadata(f.Type, notNull: false);
            AreEqual(0x09, meta[7], f.Label);            // flags: fNullable=1
            AreEqual(f.NVariantToken, meta[9], f.Label); // N-variant token
            AreEqual(f.Width, meta[10], f.Label);        // max-length byte
            AreEqual((byte)1, meta[11], f.Label);        // then the name length
        }
    }

    [TestMethod]
    public void NotNullRow_WritesRawValue_NoPrefix()
    {
        foreach (var f in Families())
        {
            var notNull = Row(f.Type, f.Value, notNull: true);
            var nullable = Row(f.Type, f.Value, notNull: false);

            AreEqual(0xD1, notNull[0], f.Label);         // ROW token
            HasCount(f.Width + 1, notNull, f.Label);     // token + raw value, no prefix

            AreEqual(0xD1, nullable[0], f.Label);
            AreEqual(f.Width, nullable[1], f.Label);     // nullable carries the length prefix
            HasCount(f.Width + 2, nullable, f.Label);

            // The raw payload bytes are identical either way — only the prefix differs.
            CollectionAssert.AreEqual(notNull[1..], nullable[2..], f.Label);
        }
    }

    private static byte[] ColMetadata(SqlType type, bool notNull) =>
        Encode(writer => TdsTypeCodec.WriteColMetadata(writer, [type], ["c"], notNull ? [false] : [true]));

    private static byte[] Row(SqlType type, SqlValue value, bool notNull) => Encode(writer =>
    {
        var result = new SimulatedSqlResultSet([type], ["c"], [[value]]);
        using var cursor = result.CreateCursor();
        while (cursor.MoveNext())
            TdsTypeCodec.WriteRow(writer, [type], cursor, notNull ? [false] : [true]);
    });

    /// <summary>Runs the writer, flushes one tabular-result packet, and returns its payload (past the 8-byte header).</summary>
    private static byte[] Encode(Action<TdsTokenWriter> write)
    {
        var stream = new MemoryStream();
        var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
        var writer = new TdsTokenWriter(transport);
        write(writer);
        writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return stream.ToArray()[Tds.HeaderSize..];
    }
}
