using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Column assignment (INSERT / UPDATE), variable assignment (<c>SET @v</c>),
/// and CAST all report a numeric narrowing overflow with the same
/// source-type-keyed error family — probe-confirmed against SQL Server 2025
/// (2026-07-31): <c>tinyint</c>/<c>smallint</c>/<c>int</c> sources raise the
/// value-bearing Msg 220 (tinyint state 2, smallint state 1), <c>float</c>/
/// <c>real</c> raise the value-bearing Msg 232 (six fractional digits,
/// states 1/2/3 by target), <c>money</c> splinters into Msg 232 / 220 / 237
/// per target, and <c>bigint</c> / <c>decimal</c> / <c>smallmoney</c>
/// sources keep the generic Msg 8115. The CAST-side cells live in
/// <see cref="CastTests"/>; this class covers the assignment paths.
/// </summary>
[TestClass]
public sealed class ConversionOverflowTests
{
    [TestMethod]
    public void Insert_IntIntoTinyint_RaisesMsg220WithValue()
    {
        var ex = new Simulation().AssertSqlError("create table t (c tinyint); insert t values (300)", 220);
        AreEqual("Arithmetic overflow error for data type tinyint, value = 300.", ex.Message);
        AreEqual((byte)2, ex.State);
    }

    [TestMethod]
    public void Insert_IntIntoSmallint_RaisesMsg220State1()
    {
        var ex = new Simulation().AssertSqlError("create table t (c smallint); insert t values (70000)", 220);
        AreEqual("Arithmetic overflow error for data type smallint, value = 70000.", ex.Message);
        AreEqual((byte)1, ex.State);
    }

    [TestMethod]
    public void Insert_NegativeIntoTinyint_RaisesMsg220WithValue() =>
        new Simulation().AssertSqlError(
            "create table t (c tinyint); insert t values (-1)",
            220, "Arithmetic overflow error for data type tinyint, value = -1.");

    [TestMethod]
    public void Update_IntIntoTinyint_RaisesMsg220WithValue() =>
        new Simulation().AssertSqlError(
            "create table t (c tinyint); insert t values (2); update t set c = 300",
            220, "Arithmetic overflow error for data type tinyint, value = 300.");

    [TestMethod]
    public void Insert_VariableExpression_RaisesMsg220WithComputedValue() =>
        new Simulation().AssertSqlError(
            "create table t (c tinyint); declare @x int = 150; insert t values (@x + @x)",
            220, "Arithmetic overflow error for data type tinyint, value = 300.");

    [TestMethod]
    public void Insert_BigintIntoInt_KeepsMsg8115() =>
        Contains("data type int", new Simulation().AssertSqlError(
            "create table t (c int); declare @b bigint = 3000000000; insert t values (@b)", 8115).Message);

    [TestMethod]
    public void Insert_FloatIntoSmallint_RaisesMsg232WithValue()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (c smallint); declare @f float = 70000; insert t values (@f)", 232);
        AreEqual("Arithmetic overflow error for type smallint, value = 70000.000000.", ex.Message);
        AreEqual((byte)2, ex.State);
    }

    [TestMethod]
    public void Insert_MoneyIntoSmallint_RaisesMsg220WithTickValue()
    {
        var ex = new Simulation().AssertSqlError(
            "create table t (c smallint); declare @m money = 70000; insert t values (@m)", 220);
        AreEqual("Arithmetic overflow error for data type smallint, value = 700000000.", ex.Message);
        AreEqual((byte)7, ex.State);
    }

    [TestMethod]
    public void SetVariable_FloatIntoTinyint_RaisesMsg232()
    {
        var ex = new Simulation().AssertSqlError("declare @v tinyint, @f float; set @f = 300; set @v = @f", 232);
        AreEqual("Arithmetic overflow error for type tinyint, value = 300.000000.", ex.Message);
        AreEqual((byte)1, ex.State);
    }

    [TestMethod]
    public void SetVariable_IntIntoTinyint_RaisesMsg220() =>
        new Simulation().AssertSqlError(
            "declare @v tinyint; set @v = 300",
            220, "Arithmetic overflow error for data type tinyint, value = 300.");
}
