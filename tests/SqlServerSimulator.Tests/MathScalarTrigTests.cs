using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class MathScalarTrigTests
{
    [TestMethod]
    public void Sin_OfZero_ReturnsZero() => AreEqual(0.0, ExecuteScalar("select sin(cast(0 as float))"));

    [TestMethod]
    public void Cos_OfZero_ReturnsOne() => AreEqual(1.0, ExecuteScalar("select cos(cast(0 as float))"));

    [TestMethod]
    public void Tan_OfZero_ReturnsZero() => AreEqual(0.0, ExecuteScalar("select tan(cast(0 as float))"));

    [TestMethod]
    public void Asin_OfOne_ReturnsHalfPi()
        => AreEqual(Math.PI / 2, ExecuteScalar("select asin(cast(1 as float))"));

    [TestMethod]
    public void Asin_OfNegativeOne_ReturnsNegativeHalfPi()
        => AreEqual(-Math.PI / 2, ExecuteScalar("select asin(cast(-1 as float))"));

    [TestMethod]
    public void Acos_OfOne_ReturnsZero() => AreEqual(0.0, ExecuteScalar("select acos(cast(1 as float))"));

    [TestMethod]
    public void Acos_OfNegativeOne_ReturnsPi()
        => AreEqual(Math.PI, ExecuteScalar("select acos(cast(-1 as float))"));

    [TestMethod]
    public void Atan_OfOne_ReturnsQuarterPi()
        => AreEqual(Math.PI / 4, ExecuteScalar("select atan(cast(1 as float))"));

    [TestMethod]
    public void Atn2_OfOneOne_ReturnsQuarterPi()
        => AreEqual(Math.PI / 4, ExecuteScalar("select atn2(cast(1 as float), cast(1 as float))"));

    [TestMethod]
    public void Atn2_OfZeroZero_RaisesMsg3623()
        => AssertSqlError("select atn2(cast(0 as float), cast(0 as float))", 3623, "An invalid floating point operation occurred.");

    [TestMethod]
    public void Cot_OfOne_ReturnsCotangentOne()
    {
        var v = (double)ExecuteScalar("select cot(cast(1 as float))")!;
        IsLessThan(1e-12, Math.Abs(v - (1.0 / Math.Tan(1.0))));
    }

    [TestMethod]
    public void Cot_OfZero_RaisesMsg3623()
        => AssertSqlError("select cot(cast(0 as float))", 3623, "An invalid floating point operation occurred.");

    [TestMethod]
    public void Pi_ReturnsPiAsFloat() => AreEqual(Math.PI, ExecuteScalar("select pi()"));

    [TestMethod]
    public void Pi_WithArgument_RaisesMsg174()
        => AssertSqlError("select pi(1)", 174, "The pi function requires 0 argument(s).");

    [TestMethod]
    public void Sin_TooFewArgs_RaisesMsg174()
        => AssertSqlError("select sin()", 174, "The sin function requires 1 argument(s).");

    [TestMethod]
    public void Sin_TooManyArgs_RaisesMsg174()
        => AssertSqlError("select sin(1, 2)", 174, "The sin function requires 1 argument(s).");

    [TestMethod]
    public void Atn2_OneArg_RaisesMsg174()
        => AssertSqlError("select atn2(1)", 174, "The atn2 function requires 2 argument(s).");

    [TestMethod]
    public void Atn2_ThreeArgs_RaisesMsg174()
        => AssertSqlError("select atn2(1, 2, 3)", 174, "The atn2 function requires 2 argument(s).");

    [TestMethod]
    public void Asin_OutOfDomain_RaisesMsg3623()
    {
        AssertSqlError("select asin(cast(2 as float))", 3623, "An invalid floating point operation occurred.");
        AssertSqlError("select asin(cast(-2 as float))", 3623, "An invalid floating point operation occurred.");
    }

    [TestMethod]
    public void Acos_OutOfDomain_RaisesMsg3623()
    {
        AssertSqlError("select acos(cast(2 as float))", 3623, "An invalid floating point operation occurred.");
        AssertSqlError("select acos(cast(-2 as float))", 3623, "An invalid floating point operation occurred.");
    }

    [TestMethod]
    public void Sin_NullInput_PropagatesNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select sin(cast(null as float))"));

    [TestMethod]
    public void Atn2_NullFirst_PropagatesNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select atn2(cast(null as float), cast(1 as float))"));

    [TestMethod]
    public void Atn2_NullSecond_PropagatesNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select atn2(cast(1 as float), cast(null as float))"));

    [TestMethod]
    public void Sin_AcceptsAnyNumericInput()
    {
        AreEqual(Math.Sin(1), ExecuteScalar("select sin(cast(1 as int))"));
        AreEqual(Math.Sin(1), ExecuteScalar("select sin(cast(1 as bigint))"));
        AreEqual(Math.Sin(1), ExecuteScalar("select sin(cast(1 as decimal(10,2)))"));
        AreEqual(Math.Sin(1), ExecuteScalar("select sin(cast(1 as money))"));
    }

    [TestMethod]
    public void Square_OfTwoFloat_ReturnsFour()
        => AreEqual(4.0, ExecuteScalar("select square(cast(2 as float))"));

    [TestMethod]
    public void Square_OfDecimal_WidensToFloat()
        => AreEqual(12.25, ExecuteScalar("select square(cast(3.5 as decimal(10,2)))"));

    [TestMethod]
    public void Square_OfInt_WidensToFloat()
        => AreEqual(2500000000.0, ExecuteScalar("select square(cast(50000 as int))"));

    [TestMethod]
    public void Square_Overflow_RaisesMsg8115()
        => AssertSqlError("select square(cast(1e200 as float))", 8115, "Arithmetic overflow error converting expression to data type float.");

    [TestMethod]
    public void Degrees_OfPi_ReturnsOneHundredEighty()
        => AreEqual(180.0, ExecuteScalar("select degrees(pi())"));

    [TestMethod]
    public void Radians_OfOneEighty_ReturnsPi()
        => AreEqual(Math.PI, ExecuteScalar("select radians(cast(180 as float))"));

    [TestMethod]
    public void Degrees_OfInt_PreservesIntTypeAndTruncates()
    {
        AreEqual(57, ExecuteScalar<int>("select degrees(cast(1 as int))"));
        AreEqual(20626, ExecuteScalar<int>("select degrees(cast(360 as int))"));
        AreEqual(-57, ExecuteScalar<int>("select degrees(cast(-1 as int))"));
    }

    [TestMethod]
    public void Degrees_OfBigint_PreservesBigint()
        => AreEqual(57L, ExecuteScalar<long>("select degrees(cast(1 as bigint))"));

    [TestMethod]
    public void Degrees_IntOverflow_RaisesMsg8115Int()
        => AssertSqlError("select degrees(cast(2147483646 as int))", 8115, "Arithmetic overflow error converting expression to data type int.");

    [TestMethod]
    public void Degrees_OfDecimal_WidensToDecimal38_18()
    {
        // SQL Server's decimal arithmetic uses higher-precision intermediate
        // accumulators than .NET's 96-bit decimal, so the trailing digits
        // diverge past the 14th decimal place. Real value: 85.94366926962348429...;
        // simulator value: 85.94366926962348131... (matches at 13 digits).
        var v = (decimal)ExecuteScalar("select degrees(cast(1.5 as decimal(10,2)))")!;
        IsLessThan(1e-12m, Math.Abs(85.94366926962348m - v));
    }

    [TestMethod]
    public void Radians_OfDecimal_WidensToDecimal38_18()
    {
        var v = (decimal)ExecuteScalar("select radians(cast(180 as decimal(10,2)))")!;
        // 180 * pi / 180 = pi rendered as decimal(38, 18). Tolerance to absorb
        // .NET-vs-SQL-Server decimal-arithmetic precision differences.
        IsLessThan(1e-12m, Math.Abs(3.141592653589793m - v));
    }

    [TestMethod]
    public void Degrees_PreservesScaleAboveEighteen()
    {
        // Input scale 22 > 18 → result decimal(38, 22). The .NET decimal storage
        // accommodates scale 22 for small values; the encoder's Pow10(22) ≈ 1e22
        // fits inside decimal.MaxValue (7.92e28). Larger scales (e.g. > 28)
        // exceed .NET decimal's range entirely — that's the pre-existing
        // 28-digit-max quirk noted in CLAUDE.md, not specific to DEGREES.
        var v = (decimal)ExecuteScalar("select degrees(cast(0.001 as decimal(25,22)))")!;
        var scale = (decimal.GetBits(v)[3] >> 16) & 0xFF;
        IsLessThanOrEqualTo(22, scale);
    }

    [TestMethod]
    public void Degrees_OfMoney_StaysMoney()
    {
        var v = (decimal)ExecuteScalar("select degrees(cast(1 as money))")!;
        // money has scale 4; 1 radian in degrees ≈ 57.2958.
        AreEqual(57.2958m, v);
    }

    [TestMethod]
    public void Degrees_TooFewArgs_RaisesMsg174()
        => AssertSqlError("select degrees()", 174, "The degrees function requires 1 argument(s).");

    [TestMethod]
    public void Radians_TooManyArgs_RaisesMsg174()
        => AssertSqlError("select radians(1, 2)", 174, "The radians function requires 1 argument(s).");

    [TestMethod]
    public void Cos_OfPi_ReturnsNegativeOne()
        => AreEqual(-1.0, ExecuteScalar("select cos(pi())"));

    [TestMethod]
    public void Square_OfNullFloat_PropagatesNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select square(cast(null as float))"));
}
