using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
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
        foreach (var type in new SqlType[] { SqlType.Int32, SqlType.BigInt, SqlType.SmallInt, SqlType.TinyInt, SqlType.Bit })
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
        HasCount(23, bytes);
        AreEqual(20, BitConverter.ToUInt16(bytes, 2)); // fixed-end = 4 + 16
        AreEqual(5, BitConverter.ToUInt16(bytes, 20)); // column count
    }

    [TestMethod]
    public void Encoder_RejectsValueTypeMismatch() =>
        Throws<ArgumentException>(() => RowEncoder.EncodeRow([SqlType.BigInt], [42])); // 42 is Int32, schema is BigInt

    [TestMethod]
    public void EightBits_ShareSingleByte()
    {
        // Eight consecutive bit columns should pack into one byte: alternating
        // true/false yields 0b01010101 = 0x55. Fixed-section length = 1.
        SqlType[] schema = [SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit, SqlType.Bit];
        SqlValue[] values =
        [
            SqlValue.FromBoolean(true), SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(true), SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(true), SqlValue.FromBoolean(false),
            SqlValue.FromBoolean(true), SqlValue.FromBoolean(false),
        ];
        var bytes = RowEncoder.EncodeRow(schema, values);
        AreEqual(0x55, bytes[4]);
        AreEqual(5, BitConverter.ToUInt16(bytes, 2)); // fixedEnd = 4 + 1 (one packed byte)

        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void NineBits_RollOverToSecondByte()
    {
        // 9 consecutive bits → ceil(9/8) = 2 bytes for the fixed section.
        var schema = new SqlType[9];
        var values = new SqlValue[9];
        for (var i = 0; i < 9; i++)
        {
            schema[i] = SqlType.Bit;
            values[i] = SqlValue.FromBoolean(true);
        }
        var bytes = RowEncoder.EncodeRow(schema, values);
        AreEqual(0xFF, bytes[4]); // first 8 bits all set
        AreEqual(0x01, bytes[5]); // ninth bit at position 0 of second byte
        AreEqual(6, BitConverter.ToUInt16(bytes, 2)); // fixedEnd = 4 + 2 packed bytes

        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void NonBitFixedColumn_StartsNewBitRun()
    {
        // [bit, bit, int, bit] uses 1 + 4 + 1 = 6 bytes; the third bit can't
        // share the first byte because the int between resets the run.
        SqlType[] schema = [SqlType.Bit, SqlType.Bit, SqlType.Int32, SqlType.Bit];
        SqlValue[] values = [SqlValue.FromBoolean(true), SqlValue.FromBoolean(true), 42, SqlValue.FromBoolean(true)];
        var bytes = RowEncoder.EncodeRow(schema, values);
        AreEqual(10, BitConverter.ToUInt16(bytes, 2)); // fixedEnd = 4 + (1 + 4 + 1)

        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void VarColumn_DoesNotResetBitRun()
    {
        // Variable-length columns don't live in the fixed section, so they
        // don't break a contiguous bit run. [bit, varchar, bit] still packs
        // both bits into the same shared byte.
        SqlType[] schema = [SqlType.Bit, SqlType.Varchar, SqlType.Bit];
        SqlValue[] values = [SqlValue.FromBoolean(true), SqlValue.FromVarchar("x"), SqlValue.FromBoolean(true)];
        var bytes = RowEncoder.EncodeRow(schema, values);
        AreEqual(5, BitConverter.ToUInt16(bytes, 2)); // fixedEnd = 4 + 1 (one packed byte for both bits)
        AreEqual(0b11, bytes[4]); // both bits set in shared byte

        var decoded = RowDecoder.DecodeRow(schema, bytes);
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void NullBit_StillOccupiesSlot()
    {
        // NULL bit columns reserve a bit slot (so subsequent bits stay
        // aligned); the NULL bitmap distinguishes them from false.
        SqlType[] schema = [SqlType.Bit, SqlType.Bit, SqlType.Bit];
        SqlValue[] values = [SqlValue.FromBoolean(true), SqlValue.Null(SqlType.Bit), SqlValue.FromBoolean(true)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        IsTrue(decoded[0].AsBoolean);
        IsTrue(decoded[1].IsNull);
        IsTrue(decoded[2].AsBoolean);
    }
}
