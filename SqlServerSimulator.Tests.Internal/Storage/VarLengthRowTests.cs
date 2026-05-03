using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Internal-only tests. If a behavior is reachable through SQL, write it in
/// SqlServerSimulator.Tests instead — public-API tests survive refactors and
/// catch regressions the way users will.
/// </summary>
[TestClass]
public class VarLengthRowTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("hello")]
    [DataRow("a quick brown fox")]
    public void Varchar_Ascii_RoundTrips(string value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar(value)]));
        AreEqual(SqlValue.FromVarchar(value), decoded[0]);
    }

    [TestMethod]
    [DataRow("café")]              // every char is in CP1252 (é = 0xE9)
    [DataRow("naïve résumé")]      // ï and é are in CP1252
    [DataRow("€uro")]              // € = 0x80 in CP1252
    public void Varchar_Cp1252_RoundTrips(string value)
    {
        // The simulator's varchar uses Windows-1252 (matching the default
        // SQL_Latin1_General_CP1_CI_AS collation); characters in CP1252
        // round-trip exactly.
        var decoded = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar(value)]));
        AreEqual(value, decoded[0].AsString);
    }

    [TestMethod]
    [DataRow("日本語", "???")]              // CJK: each char replaced with '?'
    [DataRow("🎉 emoji 🚀", "?? emoji ??")] // surrogate pairs each become two replacement bytes
    [DataRow("Ω", "?")]                    // Greek omega isn't in CP1252
    public void Varchar_OutOfCp1252_LossilyReplaced(string input, string expected)
    {
        // SQL Server's CP1252 collation can't represent characters outside
        // Windows-1252; they're silently replaced with '?'. The simulator
        // mirrors that lossy behavior — authentic over desirable.
        var decoded = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar(input)]));
        AreEqual(expected, decoded[0].AsString);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("hello")]
    [DataRow("café")]
    [DataRow("🎉 emoji 🚀")]      // surrogate pairs in UTF-16 LE
    public void NVarchar_RoundTrips(string value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.NVarchar], RowEncoder.EncodeRow([SqlType.NVarchar], [SqlValue.FromNVarchar(value)]));
        AreEqual(value, decoded[0].AsString);
    }

    [TestMethod]
    public void Varchar_Null_RoundTrips()
    {
        var decoded = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.Null(SqlType.Varchar)]));
        IsTrue(decoded[0].IsNull);
        AreSame(SqlType.Varchar, decoded[0].Type);
    }

    [TestMethod]
    public void NVarchar_Null_RoundTrips()
    {
        var decoded = RowDecoder.DecodeRow([SqlType.NVarchar], RowEncoder.EncodeRow([SqlType.NVarchar], [SqlValue.Null(SqlType.NVarchar)]));
        IsTrue(decoded[0].IsNull);
        AreSame(SqlType.NVarchar, decoded[0].Type);
    }

    [TestMethod]
    public void Varchar_EmptyString_DistinctFromNull()
    {
        var empty = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar("")]));
        var nul = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.Null(SqlType.Varchar)]));
        IsFalse(empty[0].IsNull);
        AreEqual("", empty[0].AsString);
        IsTrue(nul[0].IsNull);
    }

    [TestMethod]
    public void MixedRow_IntVarcharBigInt_RoundTrips()
    {
        SqlType[] schema = [SqlType.Int32, SqlType.Varchar, SqlType.BigInt];
        SqlValue[] values = [42, SqlValue.FromVarchar("hello"), SqlValue.FromInt64(1234567890123L)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        CollectionAssert.AreEqual(values, decoded);
    }

    [TestMethod]
    public void MultiVar_VarcharNVarchar_RoundTrips()
    {
        SqlType[] schema = [SqlType.Varchar, SqlType.NVarchar];
        SqlValue[] values = [SqlValue.FromVarchar("ascii"), SqlValue.FromNVarchar("café")];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        AreEqual("ascii", decoded[0].AsString);
        AreEqual("café", decoded[1].AsString);
    }

    [TestMethod]
    public void MultiVar_NullSandwich_RoundTrips()
    {
        // Var-NULL between two var-non-NULLs: encoder must write the offset entry
        // for the NULL column equal to the previous column's end.
        SqlType[] schema = [SqlType.Varchar, SqlType.Varchar, SqlType.Varchar];
        SqlValue[] values = [SqlValue.FromVarchar("A"), SqlValue.Null(SqlType.Varchar), SqlValue.FromVarchar("B")];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        AreEqual("A", decoded[0].AsString);
        IsTrue(decoded[1].IsNull);
        AreEqual("B", decoded[2].AsString);
    }

    [TestMethod]
    public void AllVar_AllNull_RoundTrips()
    {
        SqlType[] schema = [SqlType.Varchar, SqlType.NVarchar];
        SqlValue[] values = [SqlValue.Null(SqlType.Varchar), SqlValue.Null(SqlType.NVarchar)];
        var decoded = RowDecoder.DecodeRow(schema, RowEncoder.EncodeRow(schema, values));
        IsTrue(decoded[0].IsNull);
        IsTrue(decoded[1].IsNull);
    }

    [TestMethod]
    public void TagA_HasVarBit_WhenSchemaContainsVarColumn()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32, SqlType.Varchar], [42, SqlValue.FromVarchar("x")]);
        AreEqual(0x30, bytes[0]); // 0x10 | 0x20
    }

    [TestMethod]
    public void TagA_NoVarBit_WhenSchemaIsAllFixed()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Int32], [42]);
        AreEqual(0x10, bytes[0]);
    }

    [TestMethod]
    public void EncodedSingleVarchar_LayoutMatchesSpec()
    {
        // schema = [Varchar], value = "hi" (UTF-8 = 0x68 0x69, 2 bytes).
        //   [0]    TagA: 0x30
        //   [1]    TagB: 0x00
        //   [2-3]  fixed-end = 0x0004 (no fixed columns)
        //   [4-5]  column count = 0x0001
        //   [6]    NULL bitmap = 0x00
        //   [7-8]  V count = 0x0001
        //   [9-10] offset array entry 0 = absolute end = 13
        //   [11-12] var data: "hi"
        var bytes = RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar("hi")]);
        AreEqual(13, bytes.Length);
        AreEqual(0x30, bytes[0]);
        AreEqual(0x00, bytes[1]);
        AreEqual(4, BitConverter.ToUInt16(bytes, 2));
        AreEqual(1, BitConverter.ToUInt16(bytes, 4));
        AreEqual(0x00, bytes[6]);
        AreEqual(1, BitConverter.ToUInt16(bytes, 7));
        AreEqual(13, BitConverter.ToUInt16(bytes, 9));
        AreEqual((byte)'h', bytes[11]);
        AreEqual((byte)'i', bytes[12]);
    }

    [TestMethod]
    public void LargeString_RoundTrips()
    {
        // Exercises offsets larger than 1 byte to confirm UInt16 LE handling.
        var value = new string('x', 1024);
        var decoded = RowDecoder.DecodeRow([SqlType.Varchar], RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar(value)]));
        AreEqual(value, decoded[0].AsString);
    }

    [TestMethod]
    public void Decoder_RejectsMissingVarBitWhenSchemaHasVar()
    {
        var bytes = RowEncoder.EncodeRow([SqlType.Varchar], [SqlValue.FromVarchar("x")]);
        bytes[0] = 0x10; // strip the var bit
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow([SqlType.Varchar], bytes));
    }

    [TestMethod]
    public void Decoder_RejectsRegressingVarOffset()
    {
        // Two varchar columns; tamper with the second offset to be smaller than the first.
        SqlType[] schema = [SqlType.Varchar, SqlType.Varchar];
        var bytes = RowEncoder.EncodeRow(schema, [SqlValue.FromVarchar("AA"), SqlValue.FromVarchar("BB")]);
        // Find the offset array (right after var count). Offset array spans 4 bytes: 2 entries × 2 bytes.
        // Locate it: TagA(1) + TagB(1) + fixedEnd(2) + columnCount(2) + nullBitmap(1) + varCount(2) = 9. Offsets at [9..13).
        _ = BitConverter.TryWriteBytes(bytes.AsSpan(11, 2), (ushort)0); // make second offset zero
        _ = Throws<InvalidDataException>(() => RowDecoder.DecodeRow(schema, bytes));
    }

    [TestMethod]
    public void FromVarchar_RejectsNullArgument() =>
        Throws<ArgumentNullException>(() => SqlValue.FromVarchar(null!));

    [TestMethod]
    public void FromNVarchar_RejectsNullArgument() =>
        Throws<ArgumentNullException>(() => SqlValue.FromNVarchar(null!));

    [TestMethod]
    [DataRow("")]
    [DataRow("dbo")]
    [DataRow("café")]
    [DataRow("🎉")]
    public void SystemName_RoundTrips(string value)
    {
        var decoded = RowDecoder.DecodeRow([SqlType.SystemName], RowEncoder.EncodeRow([SqlType.SystemName], [SqlValue.FromSystemName(value)]));
        AreSame(SqlType.SystemName, decoded[0].Type);
        AreEqual(value, decoded[0].AsString);
    }

    [TestMethod]
    public void SystemName_Null_RoundTrips()
    {
        var decoded = RowDecoder.DecodeRow([SqlType.SystemName], RowEncoder.EncodeRow([SqlType.SystemName], [SqlValue.Null(SqlType.SystemName)]));
        IsTrue(decoded[0].IsNull);
        AreSame(SqlType.SystemName, decoded[0].Type);
    }

    [TestMethod]
    public void Varbinary_RoundTrips()
    {
        var bytes = new byte[] { 0x00, 0x7F, 0x80, 0xFF, 0xDE, 0xAD, 0xBE, 0xEF };
        var decoded = RowDecoder.DecodeRow([SqlType.Varbinary], RowEncoder.EncodeRow([SqlType.Varbinary], [SqlValue.FromVarbinary(bytes)]));
        AreSame(SqlType.Varbinary, decoded[0].Type);
        CollectionAssert.AreEqual(bytes, decoded[0].AsBytes);
    }

    [TestMethod]
    public void Varbinary_Empty_RoundTrips()
    {
        var decoded = RowDecoder.DecodeRow([SqlType.Varbinary], RowEncoder.EncodeRow([SqlType.Varbinary], [SqlValue.FromVarbinary([])]));
        AreEqual(0, decoded[0].AsBytes.Length);
    }

    [TestMethod]
    public void Varbinary_Null_RoundTrips()
    {
        var decoded = RowDecoder.DecodeRow([SqlType.Varbinary], RowEncoder.EncodeRow([SqlType.Varbinary], [SqlValue.Null(SqlType.Varbinary)]));
        IsTrue(decoded[0].IsNull);
        AreSame(SqlType.Varbinary, decoded[0].Type);
    }

    [TestMethod]
    public void Varbinary_EquatesByContent()
    {
        // Two byte arrays with identical contents must compare equal as
        // SqlValues — SQL Server compares varbinary by content, not reference.
        var a = SqlValue.FromVarbinary([0x01, 0x02, 0x03]);
        var b = SqlValue.FromVarbinary([0x01, 0x02, 0x03]);
        AreEqual(a, b);
        AreEqual(a.GetHashCode(), b.GetHashCode());
    }

    [TestMethod]
    public void FromVarbinary_RejectsNullArgument() =>
        Throws<ArgumentNullException>(() => SqlValue.FromVarbinary(null!));
}
