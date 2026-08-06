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

    /// <summary>
    /// The exact-numeric arm computes in <see cref="double"/> against a single
    /// pre-multiplied ratio and comes back at <c>decimal(38, max(s, 18))</c>,
    /// so every digit is the double's own — probe-confirmed digit for digit.
    /// </summary>
    [TestMethod]
    [DataRow("degrees(cast(1.5 as decimal(10,2)))", "85.943669269623484297")]
    [DataRow("degrees(cast(2 as decimal(38,0)))", "114.591559026164645729")]
    [DataRow("degrees(cast(-0.79 as decimal(2,2)))", "-45.263665815335038189")]
    [DataRow("degrees(cast(33644737241.2066 as decimal(18,4)))", "1927701446747.762939453125000000")]
    [DataRow("degrees(cast(0.001 as decimal(25,22)))", "0.0572957795130823246965")]
    [DataRow("degrees(cast(1.5 as decimal(38,30)))", "85.943669269623484296971582807600")]
    [DataRow("radians(cast(180 as decimal(10,2)))", "3.141592653589793116")]
    [DataRow("radians(cast(1.5 as decimal(10,2)))", "0.026179938779914945")]
    [DataRow("radians(cast(1.5 as decimal(38,30)))", "0.026179938779914944946280996874")]
    public void DegreesAndRadians_OfDecimal_CarryTheDoublesOwnDigits(string expression, string expected)
        => AreEqual(expected, ExecuteScalar($"select cast({expression} as varchar(60))"));

    /// <summary>
    /// A result past <c>decimal(38, 18)</c> is real's arithmetic overflow —
    /// <c>DEGREES</c> tops out a little under 1e20 for that reason.
    /// </summary>
    [TestMethod]
    public void Degrees_ResultPastTheResultType_RaisesMsg8115()
        => AssertSqlError(
            "select degrees(cast('999999999999999999999' as decimal(38,0)))",
            8115,
            "Arithmetic overflow error converting expression to data type numeric.");

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
