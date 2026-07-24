using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server keeps the narrow integer type through arithmetic instead of
/// widening, so a result outside the operand width raises Msg 8115 rather than
/// wrapping. All expectations probe-confirmed against SQL Server 2025
/// (17.0.1125.2) on 2026-07-24.
/// </summary>
[TestClass]
public sealed class IntegerArithmeticOverflowTests
{
    [TestMethod]
    [DataRow("cast(255 as tinyint) + cast(1 as tinyint)", "tinyint")]
    [DataRow("cast(0 as tinyint) - cast(1 as tinyint)", "tinyint")]
    [DataRow("cast(200 as tinyint) * cast(2 as tinyint)", "tinyint")]
    [DataRow("cast(32767 as smallint) + cast(1 as smallint)", "smallint")]
    [DataRow("cast(2147483647 as int) + cast(1 as int)", "int")]
    [DataRow("cast(-2147483648 as int) - cast(1 as int)", "int")]
    [DataRow("cast(2147483647 as int) * cast(2 as int)", "int")]
    [DataRow("cast(9223372036854775807 as bigint) + cast(1 as bigint)", "bigint")]
    public void Overflow_RaisesMsg8115NamingTheOperandType(string expression, string type) =>
        new Simulation().AssertSqlError(
            $"select {expression}",
            8115,
            $"Arithmetic overflow error converting expression to data type {type}.");

    [TestMethod]
    [DataRow("cast(-2147483648 as int) / cast(-1 as int)")]
    [DataRow("cast(-2147483648 as int) % cast(-1 as int)")]
    public void MinValueOverNegativeOne_RaisesMsg8115(string expression) =>
        // The one division that overflows: |int.MinValue| exceeds int.MaxValue.
        // The CLR traps this before the narrowing does, so it exercises the
        // compute-side catch rather than the checked cast.
        new Simulation().AssertSqlError(
            $"select {expression}",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void UnaryMinusOfIntMinValue_RaisesMsg8115() =>
        // Unary minus runs as 0 - operand, and the literal 0 is int-typed, so
        // the result stays int and overflows. (A tinyint operand promotes to
        // int the same way, which is why -cast(200 as tinyint) is -200 rather
        // than an overflow.)
        new Simulation().AssertSqlError(
            "select -cast(-2147483648 as int)",
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void Overflow_ReportsState2() =>
        AreEqual((byte)2, new Simulation().AssertSqlError("select cast(2147483647 as int) + cast(1 as int)", 8115).State);

    [TestMethod]
    [DataRow("cast(2147483647 as int) + cast(1 as bigint)", 2147483648L)]
    [DataRow("cast(200 as tinyint) * cast(2 as int)", 400)]
    public void MixedWidthPair_PromotesBeforeArithmetic_NoOverflow(string expression, object expected) =>
        // Promotion happens first, so the wider operand's range applies and the
        // same values that overflow a same-width pair compute cleanly.
        AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("cast(32767 as smallint) + cast(1 as int)", 32768)]
    [DataRow("cast(255 as tinyint) + cast(1 as smallint)", (short)256)]
    public void NarrowPlusWider_TakesTheWiderType(string expression, object expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void SumOverInt_OverflowStillRaisesMsg8115() =>
        // Pre-existing SumAggregator coverage, asserted here so the aggregate
        // and per-row arithmetic paths stay pinned to the same wording.
        new Simulation().AssertSqlError(
            """
            create table t (v int);
            insert t values (2147483647), (1);
            select sum(v) from t
            """,
            8115,
            "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void BitwiseOperators_DoNotOverflow() =>
        // & | ^ can't produce a value outside the operand width, so the checked
        // narrowing never fires for them.
        AreEqual((byte)236, new Simulation().ExecuteScalar("select cast(200 as tinyint) | cast(100 as tinyint)"));
}
