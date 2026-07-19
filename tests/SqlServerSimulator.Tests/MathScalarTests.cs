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
    public void Round_LengthOutOfRange_ClampsToNoOp()
        => AreEqual(1.5m, ExecuteScalar("select round(cast(1.5 as decimal(10,2)), 50)"));

    [TestMethod]
    public void Round_NonIntegerLength_RaisesMsg8116()
        => AssertSqlError("select round(cast(1.5 as decimal(10,2)), 'two')", 8116, "Argument data type varchar is invalid for argument 2 of round function.");

    [TestMethod]
    public void Floor_Decimal()
    {
        AreEqual(1m, ExecuteScalar("select floor(cast(1.7 as decimal(10,2)))"));
        AreEqual(-2m, ExecuteScalar("select floor(cast(-1.3 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Floor_Float() => AreEqual(1.0, ExecuteScalar("select floor(cast(1.7 as float))"));

    [TestMethod]
    public void Floor_Int_NoOp() => AreEqual(5, ExecuteScalar<int>("select floor(5)"));

    [TestMethod]
    public void Ceiling_Decimal()
    {
        AreEqual(2m, ExecuteScalar("select ceiling(cast(1.3 as decimal(10,2)))"));
        AreEqual(-1m, ExecuteScalar("select ceiling(cast(-1.7 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Ceiling_Float() => AreEqual(2.0, ExecuteScalar("select ceiling(cast(1.3 as float))"));

    [TestMethod]
    public void Power_IntInt()
    {
        AreEqual(8, ExecuteScalar<int>("select power(2, 3)"));
        AreEqual(1, ExecuteScalar<int>("select power(2, 0)"));
    }

    [TestMethod]
    public void Power_DecimalInt() => AreEqual(6.25m, ExecuteScalar("select power(cast(2.5 as decimal(10,2)), 2)"));

    [TestMethod]
    public void Power_FloatFloat() => AreEqual(6.25, ExecuteScalar("select power(cast(2.5 as float), 2)"));

    [TestMethod]
    public void Power_IntWithFractionalExponent_TruncatesToInt()
        => AreEqual(1, ExecuteScalar<int>("select power(2, cast(0.5 as float))"));

    [TestMethod]
    public void Power_IntWithNegativeExponent_TruncatesToZero()
        => AreEqual(0, ExecuteScalar<int>("select power(2, -1)"));

    [TestMethod]
    public void Power_NegativeBaseFractionalExponent_RaisesMsg3623()
        => AssertSqlError("select power(cast(-2.0 as float), cast(0.5 as float))", 3623, "An invalid floating point operation occurred.");

    [TestMethod]
    public void Power_ZeroNegativeExponent_RaisesMsg8134()
        => AssertSqlError("select power(cast(0 as float), -1)", 8134, "Divide by zero error encountered.");

    [TestMethod]
    public void Power_IntOverflow_RaisesMsg232()
        => StartsWith("Arithmetic overflow error for type int, value =", AssertSqlError("select power(cast(2 as int), 100)", 232).Message);

    [TestMethod]
    public void Sqrt_Float()
    {
        AreEqual(2.0, ExecuteScalar("select sqrt(4)"));
        AreEqual(0.0, ExecuteScalar("select sqrt(0)"));
    }

    [TestMethod]
    public void Sqrt_AlwaysFloatRegardlessOfInput()
    {
        AreEqual(2.0, ExecuteScalar("select sqrt(cast(4 as int))"));
        AreEqual(2.0, ExecuteScalar("select sqrt(cast(4 as decimal(10,2)))"));
    }

    [TestMethod]
    public void Sqrt_Negative_RaisesMsg3623()
        => AssertSqlError("select sqrt(-1)", 3623, "An invalid floating point operation occurred.");

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
    public void Sign_BigInt() => AreEqual(-1L, ExecuteScalar("select sign(cast(-1 as bigint))"));

    [TestMethod]
    public void Log_NaturalLog() => AreEqual(Math.Log(10), (double)ExecuteScalar("select log(10)")!, 1e-12);

    [TestMethod]
    public void Log_WithBase() => AreEqual(3.0, ExecuteScalar("select log(8, 2)"));

    [TestMethod]
    [DataRow("log(0)")]
    [DataRow("log(-1)")]
    [DataRow("log(10, 1)")]
    [DataRow("log10(0)")]
    public void Log_DomainError_RaisesMsg3623(string expr) => AssertSqlError($"select {expr}", 3623);

    [TestMethod]
    public void Exp_Basic()
    {
        AreEqual(1.0, ExecuteScalar("select exp(0)"));
        AreEqual(Math.E, (double)ExecuteScalar("select exp(1)")!, 1e-12);
    }

    [TestMethod]
    public void Exp_Overflow_RaisesMsg8115()
        => StartsWith("Arithmetic overflow error converting expression to data type float", AssertSqlError("select exp(1000)", 8115).Message);

    [TestMethod]
    public void Log10_Basic()
    {
        AreEqual(2.0, ExecuteScalar("select log10(100)"));
        AreEqual(Math.Log10(2), (double)ExecuteScalar("select log10(2)")!, 1e-12);
    }

    [TestMethod]
    [DataRow("round(cast(null as decimal(10,2)), 2)")]
    [DataRow("round(cast(1.5 as decimal(10,2)), cast(null as int))")]
    [DataRow("floor(cast(null as decimal(10,2)))")]
    [DataRow("power(cast(null as int), 2)")]
    [DataRow("power(2, cast(null as int))")]
    [DataRow("sqrt(cast(null as int))")]
    [DataRow("sign(cast(null as int))")]
    [DataRow("log(cast(null as int))")]
    public void NullPropagation(string expr) => AreEqual(DBNull.Value, ExecuteScalar($"select {expr}"));

    [TestMethod]
    public void Math_FromTableRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (price decimal(10,2));
            insert t values (12.345), (-5.5), (0)
            """);
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
