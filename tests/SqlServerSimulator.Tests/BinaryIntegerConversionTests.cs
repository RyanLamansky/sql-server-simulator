using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Binary ↔ integer/money conversion and binary-operand arithmetic, probed
/// against SQL Server 2025 (2026-07-14). Covers the two SSMS connect-query
/// shapes (<c>CAST(0x0001 AS int)</c> and
/// <c>(@@microsoftversion / 0x1000000) &amp; 0xff</c>) that motivated the
/// feature. Rules:
/// <list type="bullet">
/// <item>binary → integer family: big-endian, left-truncate to the target
/// width, silent (never overflows).</item>
/// <item>binary → money/smallmoney: raw scale-4 units.</item>
/// <item>binary → decimal/numeric → Msg 8114; binary → float/real → Msg 529.</item>
/// <item>integer → binary(N): left-zero-pad/truncate to N; → varbinary(N):
/// native width, left-truncate only when N narrower.</item>
/// <item>arithmetic/bitwise with one binary + one integer operand converts
/// the binary side to the integer type.</item>
/// <item>binary + binary is concatenation; other binary-pair operators raise
/// Msg 402 (<c>- % &amp; | ^</c>) or Msg 8117 (<c>* /</c>).</item>
/// </list>
/// </summary>
[TestClass]
public sealed class BinaryIntegerConversionTests
{
    private static string Hex(string sql) =>
        "0x" + Convert.ToHexString((byte[])new Simulation().ExecuteScalar(sql)!);

    // ---- binary → integer family ------------------------------------------

    [TestMethod]
    public void Varbinary_ToInt_BigEndian()
        => AreEqual(258, new Simulation().ExecuteScalar<int>("select cast(0x0102 as int)"));

    [TestMethod]
    public void Varbinary_ToInt_LeftTruncatesToWidth()
        => AreEqual(33752069, new Simulation().ExecuteScalar<int>("select cast(0x0102030405 as int)"));

    [TestMethod]
    public void Varbinary_ToBigInt_LeftTruncatesToEightBytes()
        => AreEqual(217304205466536202, new Simulation().ExecuteScalar<long>("select cast(0x0102030405060708090A as bigint)"));

    [TestMethod]
    public void Varbinary_ToTinyInt_KeepsLastByte_NoOverflow()
        => AreEqual((byte)1, new Simulation().ExecuteScalar<byte>("select cast(0xFF01 as tinyint)"));

    [TestMethod]
    public void Varbinary_ToBit_LastByteZero_IsFalse()
        => IsFalse(new Simulation().ExecuteScalar<bool>("select cast(0x0100 as bit)"));

    [TestMethod]
    public void Varbinary_ToBit_LastByteNonZero_IsTrue()
        => IsTrue(new Simulation().ExecuteScalar<bool>("select cast(0x01 as bit)"));

    [TestMethod]
    public void Varbinary_Empty_ToInt_IsZero()
        => AreEqual(0, new Simulation().ExecuteScalar<int>("select cast(0x as int)"));

    [TestMethod]
    public void Varbinary_ToInt_TwosComplement()
        => AreEqual(-1, new Simulation().ExecuteScalar<int>("select cast(0xFFFFFFFF as int)"));

    // ---- binary → money / smallmoney --------------------------------------

    [TestMethod]
    public void Varbinary_ToMoney_RawScale4Units()
        => AreEqual(0.0001m, new Simulation().ExecuteScalar<decimal>("select cast(0x01 as money)"));

    [TestMethod]
    public void Varbinary_ToMoney_HundredUnits()
        => AreEqual(0.01m, new Simulation().ExecuteScalar<decimal>("select cast(0x0000000000000064 as money)"));

    [TestMethod]
    public void Varbinary_ToSmallMoney_RawScale4Units()
        => AreEqual(0.0001m, new Simulation().ExecuteScalar<decimal>("select cast(0x01 as smallmoney)"));

    // ---- binary → decimal / float rejections ------------------------------

    [TestMethod]
    public void Varbinary_ToDecimal_RaisesMsg8114()
        => new Simulation().AssertSqlError(
            "select cast(0x41 as decimal(10,2))", 8114,
            "Error converting data type varbinary to numeric.");

    [TestMethod]
    public void Varbinary_ToNumeric_RaisesMsg8114()
        => new Simulation().AssertSqlError("select cast(0x41 as numeric(10,2))", 8114);

    [TestMethod]
    public void Varbinary_ToFloat_RaisesMsg529()
        => new Simulation().AssertSqlError(
            "select cast(0x41 as float)", 529,
            "Explicit conversion from data type varbinary to float is not allowed.");

    [TestMethod]
    public void Varbinary_ToReal_RaisesMsg529()
        => new Simulation().AssertSqlError(
            "select cast(0x41 as real)", 529,
            "Explicit conversion from data type varbinary to real is not allowed.");

    // ---- integer → binary(N) / varbinary(N) -------------------------------

    [TestMethod]
    public void Int_ToBinary2_BigEndian()
        => AreEqual("0x0102", Hex("select cast(258 as binary(2))"));

    [TestMethod]
    public void Int_ToBinary4_LeftZeroPads()
        => AreEqual("0x00000102", Hex("select cast(258 as binary(4))"));

    [TestMethod]
    public void Int_ToVarbinary4_KeepsNativeWidth()
        => AreEqual("0x00000102", Hex("select cast(258 as varbinary(4))"));

    [TestMethod]
    public void Int_ToBinary1_LeftTruncates()
        => AreEqual("0x02", Hex("select cast(258 as binary(1))"));

    [TestMethod]
    public void NegativeInt_ToBinary4_TwosComplement()
        => AreEqual("0xFFFFFFFF", Hex("select cast(-1 as binary(4))"));

    [TestMethod]
    public void Int_ToBinary_DefaultLength30_ZeroPads()
        => AreEqual("0x" + new string('0', 56) + "0102", Hex("select cast(258 as binary)"));

    [TestMethod]
    public void Int_ToVarbinary_DefaultLength30_KeepsNativeWidth()
        => AreEqual("0x00000102", Hex("select cast(258 as varbinary)"));

    [TestMethod]
    public void TinyInt_ToVarbinary4_KeepsNativeOneByte()
        => AreEqual("0x01", Hex("select cast(cast(1 as tinyint) as varbinary(4))"));

    [TestMethod]
    public void TinyInt_ToBinary4_LeftZeroPads()
        => AreEqual("0x00000001", Hex("select cast(cast(1 as tinyint) as binary(4))"));

    [TestMethod]
    public void SmallInt_ToVarbinary1_LeftTruncates()
        => AreEqual("0x02", Hex("select cast(cast(258 as smallint) as varbinary(1))"));

    [TestMethod]
    public void BigInt_ToBinary8_BigEndian()
        => AreEqual("0x0000000000000102", Hex("select cast(cast(258 as bigint) as binary(8))"));

    // ---- arithmetic / bitwise: one binary + one integer -------------------

    [TestMethod]
    public void IntPlusVarbinary_StaysInt()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("select 1 + 0x01"));

    [TestMethod]
    public void BigIntDivideVarbinary_StaysBigInt()
        => AreEqual(2L, new Simulation().ExecuteScalar<long>("select cast(5 as bigint) / 0x02"));

    [TestMethod]
    public void TinyIntPlusVarbinary_StaysTinyInt()
        => AreEqual((byte)6, new Simulation().ExecuteScalar<byte>("select cast(5 as tinyint) + 0x01"));

    [TestMethod]
    public void IntBitwiseAndVarbinary_StaysInt()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select 255 & 0x01"));

    [TestMethod]
    public void IntTimesVarbinary_StaysInt()
        => AreEqual(6, new Simulation().ExecuteScalar<int>("select 3 * 0x02"));

    [TestMethod]
    public void IntMinusVarbinary_StaysInt()
        => AreEqual(4, new Simulation().ExecuteScalar<int>("select 5 - 0x01"));

    [TestMethod]
    public void VarbinaryEqualsInt_ConvertsBinarySide()
        => AreEqual("eq", new Simulation().ExecuteScalar("select case when 0x01 = 1 then 'eq' else 'ne' end"));

    // ---- binary + binary concatenation ------------------------------------

    [TestMethod]
    public void VarbinaryPlusVarbinary_Concatenates()
        => AreEqual("0x0101", Hex("select 0x01 + 0x01"));

    [TestMethod]
    public void BinaryPlusBinary_Concatenates()
        => AreEqual("0x010203", Hex("select cast(0x0102 as binary(2)) + cast(0x03 as binary(1))"));

    // ---- binary op binary errors ------------------------------------------

    [TestMethod]
    public void VarbinaryBitwiseAndVarbinary_RaisesMsg402()
        => new Simulation().AssertSqlError(
            "select 0x0F & 0xF0", 402,
            "The data types varbinary and varbinary are incompatible in the '&' operator.");

    [TestMethod]
    public void VarbinaryMinusVarbinary_RaisesMsg402()
        => new Simulation().AssertSqlError(
            "select 0x05 - 0x02", 402,
            "The data types varbinary and varbinary are incompatible in the subtract operator.");

    [TestMethod]
    public void VarbinaryModuloVarbinary_RaisesMsg402()
        => new Simulation().AssertSqlError(
            "select 0x05 % 0x02", 402,
            "The data types varbinary and varbinary are incompatible in the modulo operator.");

    [TestMethod]
    public void VarbinaryTimesVarbinary_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "select 0x05 * 0x02", 8117,
            "Operand data type varbinary is invalid for multiply operator.");

    [TestMethod]
    public void VarbinaryDivideVarbinary_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "select 0x05 / 0x02", 8117,
            "Operand data type varbinary is invalid for divide operator.");

    // ---- TRY_CAST ---------------------------------------------------------

    [TestMethod]
    public void TryCast_VarbinaryToInt_Succeeds()
        => AreEqual(258, new Simulation().ExecuteScalar<int>("select try_cast(0x0102 as int)"));

    [TestMethod]
    public void TryCast_VarbinaryToDecimal_SwallowsToNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select try_cast(0x41 as decimal(10,2))"));

    [TestMethod]
    public void TryCast_VarbinaryToFloat_StillRaisesMsg529()
        => new Simulation().AssertSqlError("select try_cast(0x41 as float)", 529);

    // ---- SSMS connect-query shapes ----------------------------------------

    [TestMethod]
    public void Cast0x0001AsInt_IsOne()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select cast(0x0001 as int)"));

    [TestMethod]
    public void MicrosoftVersion_DividedByDecimalConstant_IsSeventeen()
        => AreEqual(17, new Simulation().ExecuteScalar<int>("select @@microsoftversion / 16777216"));

    [TestMethod]
    public void MicrosoftVersion_DividedByHexConstant_IsSeventeen()
        => AreEqual(17, new Simulation().ExecuteScalar<int>("select @@microsoftversion / 0x1000000"));

    [TestMethod]
    public void MicrosoftVersion_HexDivideThenMask_IsSeventeen()
        => AreEqual(17, new Simulation().ExecuteScalar<int>("select (@@microsoftversion / 0x1000000) & 0xff"));

    // ---- hex nchar argument (varbinary → int now resolves) ----------------

    [TestMethod]
    public void NcharOfHexLiteral_ResolvesThroughVarbinaryToInt()
        => AreEqual("A", new Simulation().ExecuteScalar("select nchar(0x41)"));
}
