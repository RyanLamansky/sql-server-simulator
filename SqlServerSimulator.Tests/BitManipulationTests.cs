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
}
