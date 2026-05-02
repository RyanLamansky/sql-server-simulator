using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

[TestClass]
public class RowRoundTripTests
{
    [TestMethod]
    [DataRow(1)]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    [DataRow(int.MinValue)]
    public void SingleColumn_Int32_RoundTrips(int value)
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [value]);
        var decoded = RowDecoder.DecodeRow([SqlType.Int32], bytes);
        AreEqual(1, decoded.Length);
        AreEqual(SqlValue.FromInt32(value), decoded[0]);
    }

    [TestMethod]
    public void SingleColumn_Null_RoundTrips()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [SqlValue.Null(SqlType.Int32)]);
        var decoded = RowDecoder.DecodeRow([SqlType.Int32], bytes);
        AreEqual(1, decoded.Length);
        IsTrue(decoded[0].IsNull);
        AreEqual(SqlType.Int32, decoded[0].Type);
    }

    [TestMethod]
    public void TwoColumns_BothPresent_RoundTrips()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Int32];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, [1, 2]));
        CollectionAssert.AreEqual(new SqlValue[] { 1, 2 }, decoded);
    }

    [TestMethod]
    public void TwoColumns_FirstNull_RoundTrips()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Int32];
        SqlValue[] values = [SqlValue.Null(SqlType.Int32), 1];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void TwoColumns_SecondNull_RoundTrips()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Int32];
        SqlValue[] values = [1, SqlValue.Null(SqlType.Int32)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void ThreeColumns_AllNull_RoundTrips()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Int32, SqlType.Int32];
        SqlValue[] values = [SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32), SqlValue.Null(SqlType.Int32)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void EightColumns_BitmapByteBoundary_RoundTrips()
    {
        // Exactly 8 columns => 1 byte of NULL bitmap. Alternating null/value tests
        // the bit ordering within the byte (column i ↔ bit i mod 8).
        SqlType[] schema = [SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32];
        var n = SqlValue.Null(SqlType.Int32);
        SqlValue[] values = [1, n, 2, n, 3, n, 4, n];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void NineColumns_MultiByteBitmap_RoundTrips()
    {
        // 9 columns => 2 bytes of NULL bitmap; the 9th column lives in the second byte.
        SqlType[] schema = [SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.Int32];
        SqlValue[] values = [10, 20, 30, 40, 50, 60, 70, 80, SqlValue.Null(SqlType.Int32)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void Encoder_RejectsEmptySchema() =>
        Throws<ArgumentException>(() => RowEncoder.EncodeRow([], []));

    [TestMethod]
    public void Encoder_RejectsSchemaValueLengthMismatch() =>
        Throws<ArgumentException>(() => RowEncoder.EncodeRow([SqlType.Int32, SqlType.Int32], [1]));

    [TestMethod]
    public void EncodedSingleColumnRow_HasLength11()
    {
        // 4 (header) + 4 (one Int32) + 2 (column count) + 1 (NULL bitmap byte) = 11.
        AreEqual(11, RowEncoder.EncodeRow([SqlType.Int32], [42]).Length);
        AreEqual(11, RowEncoder.EncodeRow([SqlType.Int32], [SqlValue.Null(SqlType.Int32)]).Length);
    }

    [TestMethod]
    public void EncodedTwoColumnRow_HasLength15()
    {
        // 4 (header) + 8 (two Int32s) + 2 (column count) + 1 (NULL bitmap byte) = 15.
        AreEqual(15, RowEncoder.EncodeRow([SqlType.Int32, SqlType.Int32], [1, 2]).Length);
    }

    [TestMethod]
    public void EncodedSingleColumnRow_MatchesDocumentedLayout()
    {
        // Guards the byte-level layout described on RowEncoder.EncodeRow.
        // See RowEncoder's <remarks> for the public references that informed the
        // structural shape; the specific bit values are simulator-defined.
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [0x04030201]);
        AreEqual(0x10, bytes[0]);                                       // TagA
        AreEqual(0x00, bytes[1]);                                       // TagB
        AreEqual(8, BitConverter.ToUInt16(bytes, 2));                   // fixed-length end offset
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, bytes[4..8]); // int LE
        AreEqual(1, BitConverter.ToUInt16(bytes, 8));                   // column count
        AreEqual(0x00, bytes[10]);                                      // NULL bitmap (not null)
    }

    [TestMethod]
    public void EncodedTwoColumnRow_MatchesDocumentedLayout()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32, SqlType.Int32], [0x04030201, 0x08070605]);
        AreEqual(0x10, bytes[0]);                                       // TagA
        AreEqual(0x00, bytes[1]);                                       // TagB
        AreEqual(12, BitConverter.ToUInt16(bytes, 2));                  // fixed-length end offset (4 + 2*4)
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02, 0x03, 0x04 }, bytes[4..8]);  // first int LE
        CollectionAssert.AreEqual(new byte[] { 0x05, 0x06, 0x07, 0x08 }, bytes[8..12]); // second int LE
        AreEqual(2, BitConverter.ToUInt16(bytes, 12));                  // column count
        AreEqual(0x00, bytes[14]);                                      // NULL bitmap
    }

    [TestMethod]
    public void EncodedNullRow_SetsNullBitmapBit0()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [SqlValue.Null(SqlType.Int32)]);
        AreEqual(0x01, bytes[10]);
    }

    [TestMethod]
    public void EncodedTwoColumnRow_SecondNullSetsBit1()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32, SqlType.Int32], [1, SqlValue.Null(SqlType.Int32)]);
        AreEqual(0x02, bytes[14]);
    }

    [TestMethod]
    public void Decoder_RejectsTruncatedRow()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [1]).AsSpan(0, 10).ToArray();
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Int32], bytes));
    }

    [TestMethod]
    public void Decoder_RejectsBadTagA()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [1]);
        bytes[0] = 0x00;
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Int32], bytes));
    }

    [TestMethod]
    public void Decoder_RejectsColumnCountMismatch()
    {
        // The decoder cross-checks the declared column count against the schema length.
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [1]);
        bytes[8] = 0x02;
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Int32], bytes));
    }

    [TestMethod]
    public void Decoder_RejectsFixedEndMismatch()
    {
        // Fixed-length end offset is derived from the schema; tampering should be rejected.
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [1]);
        bytes[2] = 0x09;
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Int32], bytes));
    }
}
