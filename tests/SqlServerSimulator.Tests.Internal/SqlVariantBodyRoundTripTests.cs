using SqlServerSimulator.Network;
using SqlServerSimulator.Storage;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>sql_variant</c> body writer and reader over every base type, asserted
/// as inverses.
/// </summary>
/// <remarks>
/// <para>
/// The two halves meet different clients: the server writes a body for any
/// <c>sql_variant</c> a query projects, and reads one for a <c>sql_variant</c>
/// RPC parameter or TVP column. SqlClient only ever <em>writes</em> the base
/// types its CLR values imply — a <see cref="DateTime"/> is always
/// <c>datetime</c>, a string always <c>nvarchar</c> — so the
/// <c>smalldatetime</c>, <c>datetime2</c> and <c>varchar</c> arms of the reader
/// are unreachable from the loopback tests even though the wire form is legal
/// and another driver can send it. Pairing the reader against the writer covers
/// them without inventing golden bytes: the writer's form is separately pinned
/// by the SqlClient round trips in <c>SqlVariantWireTests</c>, which is what
/// makes agreement here meaningful rather than merely self-consistent.
/// </para>
/// <para>
/// The undersized-body defect this guards against was a real one — the
/// <c>datetime2</c> and <c>datetimeoffset</c> writers sized their body without
/// the 3-byte day-number field, so building one threw mid-token and took the
/// connection down with it.
/// </para>
/// </remarks>
[TestClass]
public sealed class SqlVariantBodyRoundTripTests
{
    private static readonly Collation Latin1 = Collation.Baseline;

    private static (string Label, SqlValue Inner)[] BaseTypes() =>
    [
        ("bit", SqlValue.FromBoolean(true)),
        ("tinyint", SqlValue.FromByte(255)),
        ("smallint", SqlValue.FromInt16(-42)),
        ("int", SqlValue.FromInt32(4242)),
        ("bigint", SqlValue.FromInt64(9_000_000_000L)),
        ("real", SqlValue.FromSingle(1.25f)),
        ("float", SqlValue.FromDouble(3.5d)),
        ("smallmoney", SqlValue.FromMoney(SqlType.SmallMoney, 4.50m)),
        ("money", SqlValue.FromMoney(SqlType.Money, 19.99m)),
        ("uniqueidentifier", SqlValue.FromGuid(Guid.Parse("11111111-2222-3333-4444-555555555555"))),
        ("date", SqlValue.FromDate(new DateOnly(2024, 3, 15))),
        ("smalldatetime", SqlValue.FromSmallDateTime(new DateTime(2024, 3, 15, 13, 45, 0))),
        ("datetime", SqlValue.FromDateTime(new DateTime(2021, 6, 15, 13, 30, 45, 123))),
        ("time(0)", SqlValue.FromTime(SqlType.GetTime(0), new TimeSpan(13, 45, 12))),
        ("time(7)", SqlValue.FromTime(SqlType.GetTime(7), new TimeSpan(13, 45, 12).Add(TimeSpan.FromTicks(1234567)))),
        ("datetime2(0)", SqlValue.FromDateTime2(SqlType.GetDateTime2(0), new DateTime(2024, 3, 15, 13, 45, 12))),
        ("datetime2(3)", SqlValue.FromDateTime2(SqlType.GetDateTime2(3), new DateTime(2024, 3, 15, 13, 45, 12, 345))),
        ("datetime2(7)", SqlValue.FromDateTime2(SqlType.GetDateTime2(7), new DateTime(2024, 3, 15, 13, 45, 12).AddTicks(1234567))),
        ("datetimeoffset(3)", SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(3), new DateTimeOffset(new DateTime(2024, 3, 15, 13, 45, 12, 345), TimeSpan.FromMinutes(-480)))),
        ("datetimeoffset(7)", SqlValue.FromDateTimeOffset(SqlType.GetDateTimeOffset(7), new DateTimeOffset(new DateTime(2024, 3, 15, 13, 45, 12).AddTicks(1234567), TimeSpan.FromMinutes(330)))),
        ("decimal(12,3)", SqlValue.FromDecimal(DecimalSqlType.Get(12, 3), 123.456m)),
        ("varchar", SqlValue.FromString(VarcharSqlType.Get(40, Latin1, Coercibility.Implicit), "ansi text")),
        ("char", SqlValue.FromString(CharSqlType.Get(6, Latin1, Coercibility.Implicit), "abc   ")),
        ("nvarchar", SqlValue.FromString(NVarcharSqlType.Get(40, Latin1, Coercibility.Implicit), "café")),
        ("nchar", SqlValue.FromString(NCharSqlType.Get(6, Latin1, Coercibility.Implicit), "abc   ")),
        ("varbinary", SqlValue.FromVarbinary(VarbinarySqlType.Get(10), [1, 2, 3])),
        ("binary", SqlValue.FromBinary(BinarySqlType.Get(3), [1, 2, 3])),
    ];

    [TestMethod]
    public void EveryBaseType_ReaderInvertsWriter()
    {
        foreach (var (label, inner) in BaseTypes())
        {
            var decoded = TdsWireValue.ReadVariantBody(new TdsValueReader(VariantBody(inner)));
            AreEqual(inner.Type.SqlServerName, decoded.Type.SqlServerName, label);
            // Compared as variants rather than as bare values: a bare
            // SqlValue.Equals requires the two declared types to be the same
            // singleton, and a string's coercibility is not something the wire
            // form carries — the variant ordering asks only what the value is.
            IsTrue(SqlValue.FromVariant(inner).Equals(SqlValue.FromVariant(decoded)), $"{label}: {inner} != {decoded}");
        }
    }

    [TestMethod]
    public void NullVariant_IsAZeroTotalLength()
    {
        // Not a body at all: a non-NULL variant is always at least the 2-byte
        // type + prop-count header, so a total length of 0 is how NULL travels.
        var row = Row(SqlValue.Null(SqlType.SqlVariant));
        AreEqual(0xD1, row[0]);
        HasCount(5, row);
        AreEqual(0u, BitConverter.ToUInt32(row, 1));
    }

    /// <summary>The variant body of a one-column <c>sql_variant</c> ROW token,
    /// past the token byte and the 4-byte total length.</summary>
    private static byte[] VariantBody(SqlValue inner) => Row(SqlValue.FromVariant(inner))[5..];

    private static byte[] Row(SqlValue value)
    {
        var stream = new MemoryStream();
        var transport = new TdsPacketTransport(stream) { PacketSize = Tds.DefaultPacketSize };
        var writer = new TdsTokenWriter(transport);
        var result = new SimulatedSqlResultSet([SqlType.SqlVariant], ["v"], [[value]]);
        using (var cursor = result.CreateCursor())
        {
            while (cursor.MoveNext())
                TdsTypeCodec.WriteRow(writer, [SqlType.SqlVariant], cursor, [true]);
        }

        writer.FlushAsync(final: true, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        return stream.ToArray()[Tds.HeaderSize..];
    }
}
