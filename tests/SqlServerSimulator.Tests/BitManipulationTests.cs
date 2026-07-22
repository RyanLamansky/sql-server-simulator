using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the SQL Server 2022+ bit-manipulation family: <c>BIT_COUNT</c>,
/// <c>GET_BIT</c>, <c>SET_BIT</c>, <c>LEFT_SHIFT</c>, <c>RIGHT_SHIFT</c>.
/// Probe-confirmed against SQL Server 2025 (2026-05-22): RIGHT_SHIFT uses
/// LOGICAL (unsigned) shift semantics — <c>RIGHT_SHIFT(-16, 2)</c> returns
/// <c>1073741820</c>, not <c>-4</c>.
/// </summary>
[TestClass]
public sealed class BitManipulationTests
{
    [TestMethod]
    public void BitCount_Seven_ReturnsThree()
        => AreEqual(3L, new Simulation().ExecuteScalar("select bit_count(cast(7 as int))"));

    [TestMethod]
    public void BitCount_NegativeOneInt_Returns32()
        => AreEqual(32L, new Simulation().ExecuteScalar("select bit_count(cast(-1 as int))"));

    [TestMethod]
    public void BitCount_NegativeOneBigint_Returns64()
        => AreEqual(64L, new Simulation().ExecuteScalar("select bit_count(cast(-1 as bigint))"));

    [TestMethod]
    public void BitCount_TinyintMax_Returns8()
        => AreEqual(8L, new Simulation().ExecuteScalar("select bit_count(cast(255 as tinyint))"));

    [TestMethod]
    public void BitCount_Null_RaisesMsg8116()
        => new Simulation().AssertSqlError("select bit_count(null)", 8116);

    [TestMethod]
    public void GetBit_LowBitSet_ReturnsTrue()
        => IsTrue((bool)new Simulation().ExecuteScalar("select get_bit(cast(7 as int), 0)")!);

    [TestMethod]
    public void GetBit_HighBitClear_ReturnsFalse()
        => IsFalse((bool)new Simulation().ExecuteScalar("select get_bit(cast(7 as int), 3)")!);

    [TestMethod]
    public void GetBit_OutOfRange_RaisesMsg8120()
        => new Simulation().AssertSqlError("select get_bit(cast(8 as int), 32)", 8120);

    [TestMethod]
    public void SetBit_SetsHighBit()
        => AreEqual(8, new Simulation().ExecuteScalar("select set_bit(cast(0 as int), 3)"));

    [TestMethod]
    public void SetBit_ClearWithValue_Works()
        => AreEqual(7, new Simulation().ExecuteScalar("select set_bit(cast(15 as int), 3, 0)"));

    [TestMethod]
    public void LeftShift_PreservesType_Tinyint()
        => AreEqual((byte)128, new Simulation().ExecuteScalar("select left_shift(cast(1 as tinyint), 7)"));

    [TestMethod]
    public void LeftShift_OverflowsTinyint_WrapsToZero()
        => AreEqual((byte)0, new Simulation().ExecuteScalar("select left_shift(cast(1 as tinyint), 8)"));

    [TestMethod]
    public void RightShift_PositiveInt_Works()
        => AreEqual(4, new Simulation().ExecuteScalar("select right_shift(cast(16 as int), 2)"));

    [TestMethod]
    public void RightShift_NegativeInt_LogicalShift()
        => AreEqual(1073741820, new Simulation().ExecuteScalar("select right_shift(cast(-16 as int), 2)"));

    [TestMethod]
    public void LeftShift_Bigint_LargeShift()
        => AreEqual(1073741824L, new Simulation().ExecuteScalar("select left_shift(cast(1 as bigint), 30)"));

    // Unary ~ (bitwise NOT). Probe-confirmed against SQL Server 2025
    // (2026-07-15): result keeps the operand's exact type, bit flips, NULL
    // propagates, non-integer operands raise Msg 8117, and ~ binds tighter
    // than every binary operator.

    [TestMethod]
    public void BitwiseNot_Int_ReturnsOnesComplement()
        => AreEqual(-2, new Simulation().ExecuteScalar("select ~1"));

    [TestMethod]
    public void BitwiseNot_BitOne_FlipsToZero()
        => IsFalse((bool)new Simulation().ExecuteScalar("select ~cast(1 as bit)")!);

    [TestMethod]
    public void BitwiseNot_BitZero_FlipsToOne()
        => IsTrue((bool)new Simulation().ExecuteScalar("select ~cast(0 as bit)")!);

    [TestMethod]
    public void BitwiseNot_TinyintZero_Returns255PreservingType()
        => AreEqual((byte)255, new Simulation().ExecuteScalar("select ~cast(0 as tinyint)"));

    [TestMethod]
    public void BitwiseNot_Smallint_PreservesType()
        => AreEqual((short)-6, new Simulation().ExecuteScalar("select ~cast(5 as smallint)"));

    [TestMethod]
    public void BitwiseNot_Bigint_PreservesType()
        => AreEqual(-6L, new Simulation().ExecuteScalar("select ~cast(5 as bigint)"));

    [TestMethod]
    public void BitwiseNot_Null_Propagates()
        => IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("select ~cast(null as int)"));

    [TestMethod]
    public void BitwiseNot_DoubleApplication_RoundTrips()
        => AreEqual(5, new Simulation().ExecuteScalar("select ~~5"));

    // Precedence: ~ is SQL Server's highest-precedence operator, so it binds
    // to the leftmost primary — `~2 + 1` is `(~2) + 1`, not `~(2 + 1)`.

    [TestMethod]
    public void BitwiseNot_BindsTighterThanAdd()
        => AreEqual(-2, new Simulation().ExecuteScalar("select ~2 + 1"));

    [TestMethod]
    public void BitwiseNot_BindsTighterThanMultiply()
        => AreEqual(-9, new Simulation().ExecuteScalar("select ~2 * 3"));

    [TestMethod]
    public void BitwiseNot_BindsTighterThanBitwiseAnd()
        => AreEqual(1, new Simulation().ExecuteScalar("select ~2 & 3"));

    [TestMethod]
    public void BitwiseNot_SinksAcrossMixedChain()
        => AreEqual(9, new Simulation().ExecuteScalar("select ~2 + 3 * 4"));

    [TestMethod]
    public void BitwiseNot_ParenthesizedOperand_AppliesToWhole()
        => AreEqual(-4, new Simulation().ExecuteScalar("select ~(2 + 1)"));

    [TestMethod]
    public void BitwiseNot_DecimalLiteral_RaisesMsg8117()
        => new Simulation().AssertSqlError("select ~1.5", 8117);

    [TestMethod]
    public void BitwiseNot_Decimal_RaisesMsg8117WithTypeName()
        => new Simulation().AssertSqlError(
            "select ~cast(1.5 as decimal(3,1))", 8117,
            "Operand data type decimal is invalid for '~' operator.");

    [TestMethod]
    public void BitwiseNot_Float_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "select ~cast(1 as float)", 8117,
            "Operand data type float is invalid for '~' operator.");

    [TestMethod]
    public void BitwiseNot_String_RaisesMsg8117()
        => new Simulation().AssertSqlError(
            "select ~'a'", 8117,
            "Operand data type varchar is invalid for '~' operator.");

    // The `<<` / `>>` shift operators share the LEFT_SHIFT / RIGHT_SHIFT
    // engine (probe-confirmed identical to the functions against SQL Server
    // 2025, 2026-07-21). Precedence sits at the `+ - & | ^` level, below
    // `* / %`, left-associative.

    [TestMethod]
    public void LeftShiftOperator_FiveByOne_Returns10()
        => AreEqual(10, new Simulation().ExecuteScalar("select 5 << 1"));

    [TestMethod]
    public void RightShiftOperator_TwentyByTwo_Returns5()
        => AreEqual(5, new Simulation().ExecuteScalar("select 20 >> 2"));

    // (5 << 1) + 1 = 11 — `<<` binds tighter than `+`.
    [TestMethod]
    public void ShiftOperator_BindsTighterThanAddition()
        => AreEqual(11, new Simulation().ExecuteScalar("select 5 << 1 + 1"));

    // (4 | 1) << 2 = 20 — `|` and `<<` are the same precedence, left to right.
    [TestMethod]
    public void ShiftOperator_SharesLevelWithBitwiseOr_LeftAssociative()
        => AreEqual(20, new Simulation().ExecuteScalar("select 4 | 1 << 2"));

    // (2 * 3) << 1 = 12 — `*` binds tighter than `<<`.
    [TestMethod]
    public void ShiftOperator_MultiplicationBindsTighter()
        => AreEqual(12, new Simulation().ExecuteScalar("select 2 * 3 << 1"));

    [TestMethod]
    public void LeftShiftOperator_Bigint_StaysBigint()
        => AreEqual(10L, new Simulation().ExecuteScalar("select cast(5 as bigint) << 1"));

    [TestMethod]
    public void LeftShiftOperator_ShiftBeyondWidth_ReturnsZero()
        => AreEqual(0, new Simulation().ExecuteScalar("select 5 << 33"));

    // A lone `<` remains a comparison operator (not a shift) — `a < 30`
    // filters, proving the doubled-adjacent gate leaves comparisons alone.
    [TestMethod]
    public void Comparison_LessThan_StillParsesAsBoolean()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table s (a int); insert s values (10),(20),(50); select count(*) from s where a < 30"));
}
