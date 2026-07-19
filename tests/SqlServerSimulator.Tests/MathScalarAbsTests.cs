using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class MathScalarAbsTests
{
    [TestMethod]
    public void Abs_Int()
    {
        AreEqual(5, ExecuteScalar<int>("select abs(-5)"));
        AreEqual(5, ExecuteScalar<int>("select abs(5)"));
        AreEqual(0, ExecuteScalar<int>("select abs(0)"));
    }

    [TestMethod]
    public void Abs_BigInt_PreservesType() => AreEqual(5L, ExecuteScalar("select abs(cast(-5 as bigint))"));

    [TestMethod]
    public void Abs_SmallInt_WidensToInt()
    {
        AreEqual(5, ExecuteScalar<int>("select abs(cast(-5 as smallint))"));
        AreEqual(32768, ExecuteScalar<int>("select abs(cast(-32768 as smallint))"));
    }

    [TestMethod]
    public void Abs_TinyInt_WidensToInt() => AreEqual(5, ExecuteScalar<int>("select abs(cast(5 as tinyint))"));

    [TestMethod]
    public void Abs_Decimal_PreservesPrecisionScale() => AreEqual(5.50m, ExecuteScalar("select abs(cast(-5.5 as decimal(10,2)))"));

    [TestMethod]
    public void Abs_Money_PreservesType() => AreEqual(5.5000m, ExecuteScalar("select abs(cast(-5.5 as money))"));

    [TestMethod]
    public void Abs_SmallMoney_WidensToMoney() => AreEqual(5.5000m, ExecuteScalar("select abs(cast(-5.5 as smallmoney))"));

    [TestMethod]
    public void Abs_Float() => AreEqual(5.5, ExecuteScalar("select abs(cast(-5.5 as float))"));

    [TestMethod]
    public void Abs_Real_WidensToFloat() => AreEqual(5.5, ExecuteScalar("select abs(cast(-5.5 as real))"));

    [TestMethod]
    public void Abs_Bit_WidensToFloat()
    {
        AreEqual(0.0, ExecuteScalar("select abs(cast(0 as bit))"));
        AreEqual(1.0, ExecuteScalar("select abs(cast(1 as bit))"));
    }

    [TestMethod]
    public void Abs_IntMinValue_RaisesMsg8115()
        => StartsWith("Arithmetic overflow error converting expression to data type int", AssertSqlError("select abs(cast(-2147483648 as int))", 8115).Message);

    [TestMethod]
    public void Abs_BigIntMinValue_RaisesMsg8115()
        => StartsWith("Arithmetic overflow error converting expression to data type bigint", AssertSqlError("select abs(cast(-9223372036854775808 as bigint))", 8115).Message);

    [TestMethod]
    [DataRow("abs(cast(null as int))")]
    [DataRow("abs(cast(null as decimal(10,2)))")]
    [DataRow("abs(cast(null as smallmoney))")]
    public void Abs_Null(string expr) => AreEqual(DBNull.Value, ExecuteScalar($"select {expr}"));

    [TestMethod]
    [DataRow("floor(cast(1.7 as smallmoney))", 1.0000)]
    [DataRow("ceiling(cast(1.3 as smallmoney))", 2.0000)]
    [DataRow("sign(cast(-1.5 as smallmoney))", -1.0000)]
    [DataRow("round(cast(1.5 as smallmoney), 0)", 2.0000)]
    public void OtherMathFunctions_SmallMoney_WidensToMoney(string expr, double expected)
        => AreEqual((decimal)expected, ExecuteScalar($"select {expr}"));

    [TestMethod]
    [DataRow("floor(cast(1.7 as real))", 1.0)]
    [DataRow("sign(cast(-1.5 as real))", -1.0)]
    [DataRow("round(cast(1.5 as real), 0)", 2.0)]
    public void OtherMathFunctions_Real_WidensToFloat(string expr, double expected)
        => AreEqual(expected, ExecuteScalar($"select {expr}"));

    [TestMethod]
    public void Floor_Bit_WidensToFloat()
    {
        AreEqual(1.0, ExecuteScalar("select floor(cast(1 as bit))"));
        AreEqual(0.0, ExecuteScalar("select floor(cast(0 as bit))"));
    }
}
