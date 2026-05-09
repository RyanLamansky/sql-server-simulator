using System.Data.Common;
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
    public void Abs_BigInt_PreservesType()
    {
        AreEqual(5L, ExecuteScalar("select abs(cast(-5 as bigint))"));
    }

    [TestMethod]
    public void Abs_SmallInt_WidensToInt()
    {
        // Server-side type: SELECT INTO #t reports `int`. SqlClient's
        // ExecuteScalar surfaces the value as int regardless.
        AreEqual(5, ExecuteScalar<int>("select abs(cast(-5 as smallint))"));
        // smallint.MinValue widens through int and survives.
        AreEqual(32768, ExecuteScalar<int>("select abs(cast(-32768 as smallint))"));
    }

    [TestMethod]
    public void Abs_TinyInt_WidensToInt()
    {
        AreEqual(5, ExecuteScalar<int>("select abs(cast(5 as tinyint))"));
    }

    [TestMethod]
    public void Abs_Decimal_PreservesPrecisionScale()
    {
        AreEqual(5.50m, ExecuteScalar("select abs(cast(-5.5 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Abs_Money_PreservesType()
    {
        AreEqual(5.5000m, ExecuteScalar("select abs(cast(-5.5 as money))"));
    }

    [TestMethod]
    public void Abs_SmallMoney_WidensToMoney()
    {
        // Server-side type: smallmoney input → money result.
        AreEqual(5.5000m, ExecuteScalar("select abs(cast(-5.5 as smallmoney))"));
    }

    [TestMethod]
    public void Abs_Float()
    {
        AreEqual(5.5, ExecuteScalar("select abs(cast(-5.5 as float))"));
    }

    [TestMethod]
    public void Abs_Real_WidensToFloat()
    {
        // Server-side type: real input → float result.
        AreEqual(5.5, ExecuteScalar("select abs(cast(-5.5 as real))"));
    }

    [TestMethod]
    public void Abs_Bit_WidensToFloat()
    {
        // SQL Server's quirky widening: ABS(bit) → float (probe-confirmed).
        AreEqual(0.0, ExecuteScalar("select abs(cast(0 as bit))"));
        AreEqual(1.0, ExecuteScalar("select abs(cast(1 as bit))"));
    }

    [TestMethod]
    public void Abs_IntMinValue_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select abs(cast(-2147483648 as int))"));
        StartsWith("Arithmetic overflow error converting expression to data type int", ex.Message);
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Abs_BigIntMinValue_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select abs(cast(-9223372036854775808 as bigint))"));
        StartsWith("Arithmetic overflow error converting expression to data type bigint", ex.Message);
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Abs_Null_TypedNullOfWidenedType()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select abs(cast(null as int))"));
        AreEqual(DBNull.Value, ExecuteScalar("select abs(cast(null as decimal(10,2)))"));
        AreEqual(DBNull.Value, ExecuteScalar("select abs(cast(null as smallmoney))"));
    }

    // The widening rule is shared across ABS / FLOOR / CEILING / ROUND /
    // SIGN; verify the previously-shipped functions also widen smallmoney
    // / real / bit per the probe.

    [TestMethod]
    public void Floor_SmallMoney_WidensToMoney()
    {
        AreEqual(1.0000m, ExecuteScalar("select floor(cast(1.7 as smallmoney))"));
    }

    [TestMethod]
    public void Floor_Real_WidensToFloat()
    {
        AreEqual(1.0, ExecuteScalar("select floor(cast(1.7 as real))"));
    }

    [TestMethod]
    public void Floor_Bit_WidensToFloat()
    {
        AreEqual(1.0, ExecuteScalar("select floor(cast(1 as bit))"));
        AreEqual(0.0, ExecuteScalar("select floor(cast(0 as bit))"));
    }

    [TestMethod]
    public void Ceiling_SmallMoney_WidensToMoney()
    {
        AreEqual(2.0000m, ExecuteScalar("select ceiling(cast(1.3 as smallmoney))"));
    }

    [TestMethod]
    public void Sign_SmallMoney_WidensToMoney()
    {
        AreEqual(-1.0000m, ExecuteScalar("select sign(cast(-1.5 as smallmoney))"));
    }

    [TestMethod]
    public void Sign_Real_WidensToFloat()
    {
        AreEqual(-1.0, ExecuteScalar("select sign(cast(-1.5 as real))"));
    }

    [TestMethod]
    public void Round_SmallMoney_WidensToMoney()
    {
        AreEqual(2.0000m, ExecuteScalar("select round(cast(1.5 as smallmoney), 0)"));
    }

    [TestMethod]
    public void Round_Real_WidensToFloat()
    {
        AreEqual(2.0, ExecuteScalar("select round(cast(1.5 as real), 0)"));
    }
}
