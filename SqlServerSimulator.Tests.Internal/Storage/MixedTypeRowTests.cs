using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

[TestClass]
public class MixedTypeRowTests
{
    [TestMethod]
    [DataRow(0L)]
    [DataRow(1L)]
    [DataRow(-1L)]
    [DataRow(long.MaxValue)]
    [DataRow(long.MinValue)]
    public void BigInt_RoundTrips(long value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.BigInt], RowEncoder.EncodeRow([SqlType.BigInt], [SqlValue.FromInt64(value)]));
        AreEqual(SqlValue.FromInt64(value), decoded[0]);
    }

    [TestMethod]
    [DataRow((short)0)]
    [DataRow((short)1)]
    [DataRow((short)-1)]
    [DataRow(short.MaxValue)]
    [DataRow(short.MinValue)]
    public void SmallInt_RoundTrips(short value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.SmallInt], RowEncoder.EncodeRow([SqlType.SmallInt], [SqlValue.FromInt16(value)]));
        AreEqual(SqlValue.FromInt16(value), decoded[0]);
    }

    [TestMethod]
    [DataRow((byte)0)]
    [DataRow((byte)1)]
    [DataRow((byte)127)]
    [DataRow(byte.MaxValue)]
    public void TinyInt_RoundTrips(byte value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.TinyInt], RowEncoder.EncodeRow([SqlType.TinyInt], [SqlValue.FromByte(value)]));
        AreEqual(SqlValue.FromByte(value), decoded[0]);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Bit_RoundTrips(bool value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.Bit], RowEncoder.EncodeRow([SqlType.Bit], [SqlValue.FromBoolean(value)]));
        AreEqual(SqlValue.FromBoolean(value), decoded[0]);
    }

    [TestMethod]
    public void EachType_NullRoundTrips()
    {
        foreach (var type in new[] { SqlType.Int32, SqlType.BigInt, SqlType.SmallInt, SqlType.TinyInt, SqlType.Bit })
        {
            var decoded = RowDecoder.DecodeRow([type], RowEncoder.EncodeRow([type], [SqlValue.Null(type)]));
            IsTrue(decoded[0].IsNull);
            AreSame(type, decoded[0].Type);
        }
    }

    [TestMethod]
    public void MixedRow_Int32BigIntBit_RoundTrips()
    {
        // Exercises offset arithmetic across a 4-byte, 8-byte, and 1-byte column.
        SqlType[] schema = [SqlType.Int32, SqlType.BigInt, SqlType.Bit];
        SqlValue[] values = [42, SqlValue.FromInt64(0x0102030405060708L), SqlValue.FromBoolean(true)];
        var bytes = RowEncoder.EncodeRow(schema, values);
        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void MixedRow_AllFixedWidths_RoundTrips()
    {
        // Every fixed-width type in one row, with a NULL in the middle.
        SqlType[] schema = [SqlType.TinyInt, SqlType.SmallInt, SqlType.Int32, SqlType.BigInt, SqlType.Bit];
        SqlValue[] values =
        [
            SqlValue.FromByte(255),
            SqlValue.Null(SqlType.SmallInt),
            int.MinValue,
            SqlValue.FromInt64(long.MaxValue),
            SqlValue.FromBoolean(false),
        ];
        var bytes = RowEncoder.EncodeRow(schema, values);
        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void MixedRow_Layout_FixedSectionLengthMatchesSum()
    {
        SqlType[] schema = [SqlType.TinyInt, SqlType.SmallInt, SqlType.Int32, SqlType.BigInt, SqlType.Bit];
        // 1 + 2 + 4 + 8 + 1 = 16 bytes of fixed data; plus 4 header + 2 column count + 1 NULL bitmap = 23.
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromByte(0), SqlValue.FromInt16(0), 0, SqlValue.FromInt64(0L), SqlValue.FromBoolean(false)]);
        AreEqual(23, bytes.Length);
        AreEqual(20, BitConverter.ToUInt16(bytes, 2)); // fixed-end = 4 + 16
        AreEqual(5, BitConverter.ToUInt16(bytes, 20)); // column count
    }

    [TestMethod]
    public void Bit_Decoder_RejectsInvalidByte()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Bit], [SqlValue.FromBoolean(true)]);
        bytes[4] = 0x02;
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Bit], bytes));
    }

    [TestMethod]
    public void Encoder_RejectsValueTypeMismatch() =>
        Throws<ArgumentException>(() => RowEncoder.EncodeRow([SqlType.BigInt], [42])); // 42 is Int32, schema is BigInt
}
