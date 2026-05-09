using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class MathScalarTests
{
    [TestMethod]
    public void Round_Decimal_HalfAwayFromZero()
    {
        AreEqual(1m, ExecuteScalar("select round(cast(0.5 as decimal(10,2)), 0)"));
        AreEqual(2m, ExecuteScalar("select round(cast(1.5 as decimal(10,2)), 0)"));
        AreEqual(3m, ExecuteScalar("select round(cast(2.5 as decimal(10,2)), 0)"));
        AreEqual(-1m, ExecuteScalar("select round(cast(-0.5 as decimal(10,2)), 0)"));
        AreEqual(-3m, ExecuteScalar("select round(cast(-2.5 as decimal(10,2)), 0)"));
    }

    [TestMethod]
    public void Round_Decimal_PreservesScale()
    {
        AreEqual(123.46m, ExecuteScalar("select round(cast(123.4567 as decimal(10,4)), 2)"));
        AreEqual(124m, ExecuteScalar("select round(cast(123.567 as decimal(10,3)), 0)"));
    }

    [TestMethod]
    public void Round_Decimal_NegativeLength()
    {
        AreEqual(130m, ExecuteScalar("select round(cast(127 as decimal(10,0)), -1)"));
        AreEqual(100m, ExecuteScalar("select round(cast(127 as decimal(10,0)), -2)"));
    }

    [TestMethod]
    public void Round_Int_NegativeLength()
    {
        AreEqual(130, ExecuteScalar<int>("select round(127, -1)"));
        AreEqual(100, ExecuteScalar<int>("select round(127, -2)"));
        AreEqual(-130, ExecuteScalar<int>("select round(-127, -1)"));
    }

    [TestMethod]
    public void Round_Float_HalfAwayFromZero()
    {
        AreEqual(1.0, ExecuteScalar("select round(cast(0.5 as float), 0)"));
        AreEqual(123.46, ExecuteScalar("select round(cast(123.4567 as float), 2)"));
    }

    [TestMethod]
    public void Round_TruncateMode()
    {
        AreEqual(123.45m, ExecuteScalar("select round(cast(123.4567 as decimal(10,4)), 2, 1)"));
        AreEqual(1m, ExecuteScalar("select round(cast(1.99 as decimal(10,2)), 0, 1)"));
    }

    [TestMethod]
    public void Round_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select round(cast(null as decimal(10,2)), 2)"));
        AreEqual(DBNull.Value, ExecuteScalar("select round(cast(1.5 as decimal(10,2)), cast(null as int))"));
    }

    [TestMethod]
    public void Round_LengthOutOfRange_ClampsToNoOp()
    {
        AreEqual(1.5m, ExecuteScalar("select round(cast(1.5 as decimal(10,2)), 50)"));
    }

    [TestMethod]
    public void Round_NonIntegerLength_RaisesMsg8116()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select round(cast(1.5 as decimal(10,2)), 'two')"));
        StartsWith("Argument data type varchar is invalid for argument 2 of round function.", ex.Message);
        AreEqual("8116", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Floor_Decimal()
    {
        AreEqual(1m, ExecuteScalar("select floor(cast(1.7 as decimal(10,2)))"));
        AreEqual(-2m, ExecuteScalar("select floor(cast(-1.3 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Floor_Float()
    {
        AreEqual(1.0, ExecuteScalar("select floor(cast(1.7 as float))"));
    }

    [TestMethod]
    public void Floor_Int_NoOp()
    {
        AreEqual(5, ExecuteScalar<int>("select floor(5)"));
    }

    [TestMethod]
    public void Floor_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select floor(cast(null as decimal(10,2)))"));
    }

    [TestMethod]
    public void Ceiling_Decimal()
    {
        AreEqual(2m, ExecuteScalar("select ceiling(cast(1.3 as decimal(10,2)))"));
        AreEqual(-1m, ExecuteScalar("select ceiling(cast(-1.7 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Ceiling_Float()
    {
        AreEqual(2.0, ExecuteScalar("select ceiling(cast(1.3 as float))"));
    }

    [TestMethod]
    public void Power_IntInt()
    {
        AreEqual(8, ExecuteScalar<int>("select power(2, 3)"));
        AreEqual(1, ExecuteScalar<int>("select power(2, 0)"));
    }

    [TestMethod]
    public void Power_DecimalInt()
    {
        AreEqual(6.25m, ExecuteScalar("select power(cast(2.5 as decimal(10,2)), 2)"));
    }

    [TestMethod]
    public void Power_FloatFloat()
    {
        AreEqual(6.25, ExecuteScalar("select power(cast(2.5 as float), 2)"));
    }

    [TestMethod]
    public void Power_IntWithFractionalExponent_TruncatesToInt()
    {
        // POWER(2, 0.5) ≈ 1.414... but result type follows base (int) → 1.
        AreEqual(1, ExecuteScalar<int>("select power(2, cast(0.5 as float))"));
    }

    [TestMethod]
    public void Power_IntWithNegativeExponent_TruncatesToZero()
    {
        AreEqual(0, ExecuteScalar<int>("select power(2, -1)"));
    }

    [TestMethod]
    public void Power_NegativeBaseFractionalExponent_RaisesMsg3623()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select power(cast(-2.0 as float), cast(0.5 as float))"));
        AreEqual("An invalid floating point operation occurred.", ex.Message);
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Power_ZeroNegativeExponent_RaisesMsg8134()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select power(cast(0 as float), -1)"));
        AreEqual("Divide by zero error encountered.", ex.Message);
        AreEqual("8134", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Power_IntOverflow_RaisesMsg232()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select power(cast(2 as int), 100)"));
        StartsWith("Arithmetic overflow error for type int, value =", ex.Message);
        AreEqual("232", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Power_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select power(cast(null as int), 2)"));
        AreEqual(DBNull.Value, ExecuteScalar("select power(2, cast(null as int))"));
    }

    [TestMethod]
    public void Sqrt_Float()
    {
        AreEqual(2.0, ExecuteScalar("select sqrt(4)"));
        AreEqual(0.0, ExecuteScalar("select sqrt(0)"));
    }

    [TestMethod]
    public void Sqrt_AlwaysFloatRegardlessOfInput()
    {
        // SqlClient returns int columns as int, float as double.
        AreEqual(2.0, ExecuteScalar("select sqrt(cast(4 as int))"));
        AreEqual(2.0, ExecuteScalar("select sqrt(cast(4 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Sqrt_Negative_RaisesMsg3623()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select sqrt(-1)"));
        AreEqual("An invalid floating point operation occurred.", ex.Message);
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Sqrt_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select sqrt(cast(null as int))"));
    }

    [TestMethod]
    public void Sign_Int()
    {
        AreEqual(1, ExecuteScalar<int>("select sign(5)"));
        AreEqual(-1, ExecuteScalar<int>("select sign(-5)"));
        AreEqual(0, ExecuteScalar<int>("select sign(0)"));
    }

    [TestMethod]
    public void Sign_Decimal_PreservesType()
    {
        AreEqual(-1m, ExecuteScalar("select sign(cast(-1.5 as decimal(10,2)))"));
        AreEqual(1m, ExecuteScalar("select sign(cast(1.5 as decimal(10,2)))"));
        AreEqual(0m, ExecuteScalar("select sign(cast(0 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Sign_BigInt()
    {
        AreEqual(-1L, ExecuteScalar("select sign(cast(-1 as bigint))"));
    }

    [TestMethod]
    public void Sign_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select sign(cast(null as int))"));
    }

    [TestMethod]
    public void Log_NaturalLog()
    {
        var result = (double)ExecuteScalar("select log(10)")!;
        AreEqual(Math.Log(10), result, 1e-12);
    }

    [TestMethod]
    public void Log_WithBase()
    {
        AreEqual(3.0, ExecuteScalar("select log(8, 2)"));
    }

    [TestMethod]
    public void Log_NonPositive_RaisesMsg3623()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select log(0)"));
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);

        ex = Throws<DbException>(() => ExecuteScalar("select log(-1)"));
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Log_BaseOne_RaisesMsg3623()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select log(10, 1)"));
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Log_Null()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select log(cast(null as int))"));
    }

    [TestMethod]
    public void Exp_Basic()
    {
        AreEqual(1.0, ExecuteScalar("select exp(0)"));
        var e = (double)ExecuteScalar("select exp(1)")!;
        AreEqual(Math.E, e, 1e-12);
    }

    [TestMethod]
    public void Exp_Overflow_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select exp(1000)"));
        StartsWith("Arithmetic overflow error converting expression to data type float", ex.Message);
        AreEqual("8115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Log10_Basic()
    {
        AreEqual(2.0, ExecuteScalar("select log10(100)"));
        var result = (double)ExecuteScalar("select log10(2)")!;
        AreEqual(Math.Log10(2), result, 1e-12);
    }

    [TestMethod]
    public void Log10_NonPositive_RaisesMsg3623()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select log10(0)"));
        AreEqual("3623", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Math_FromTableRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (price decimal(10,2))");
        _ = sim.ExecuteNonQuery("insert into t values (12.345), (-5.5), (0)");
        using var reader = sim.ExecuteReader("select floor(price), ceiling(price), round(price, 0), sign(price) from t order by price");
        IsTrue(reader.Read());
        AreEqual(-6m, reader.GetDecimal(0));
        AreEqual(-5m, reader.GetDecimal(1));
        AreEqual(-6m, reader.GetDecimal(2));
        AreEqual(-1m, reader.GetDecimal(3));

        IsTrue(reader.Read());
        AreEqual(0m, reader.GetDecimal(0));
        AreEqual(0m, reader.GetDecimal(1));

        IsTrue(reader.Read());
        AreEqual(12m, reader.GetDecimal(0));
        AreEqual(13m, reader.GetDecimal(1));
        AreEqual(12m, reader.GetDecimal(2));
        AreEqual(1m, reader.GetDecimal(3));

        IsFalse(reader.Read());
    }
}
