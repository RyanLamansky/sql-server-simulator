using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>ISDATE(expression)</c>. Validates against the
/// legacy <c>datetime</c> range (1753-9999); modern <c>date</c> / <c>time</c>
/// / <c>datetimeoffset</c> raise Msg 8116; integer input gets implicitly
/// stringified before parse.
/// </summary>
[TestClass]
public sealed class IsDateTests
{
    [TestMethod] public void IsoDate_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('2026-05-12')"));
    [TestMethod] public void YyyyMmDd_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('20260512')"));
    [TestMethod] public void DateAndTime_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('2026-05-12 13:45')"));
    [TestMethod] public void TimeOnly_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('12:34:56')"));

    [TestMethod] public void InvalidMonth_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE('2026-13-01')"));
    [TestMethod] public void InvalidDay_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE('2026-02-30')"));
    [TestMethod] public void NonDate_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE('not a date')"));
    [TestMethod] public void Empty_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE('')"));

    /// <summary>Pre-1753 dates are outside the legacy datetime range; ISDATE rejects.</summary>
    [TestMethod] public void PreLegacyMinYear_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE('1700-01-01')"));
    [TestMethod] public void LegacyMinYear_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('1753-01-01')"));
    [TestMethod] public void LegacyMaxYear_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE('9999-12-31')"));

    [TestMethod] public void Null_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISDATE(NULL)"));

    [TestMethod] public void IntegerYyyyMmDd_AcceptedViaStringCoercion() => AreEqual(1, ExecuteScalar<int>("select ISDATE(20260512)"));
    [TestMethod] public void IntegerOutOfBounds_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE(99999999)"));
    [TestMethod] public void IntegerZero_Rejected() => AreEqual(0, ExecuteScalar<int>("select ISDATE(0)"));
    [TestMethod] public void IntegerOne_RejectedBelowYearFloor() => AreEqual(0, ExecuteScalar<int>("select ISDATE(1)"));

    [TestMethod] public void DateTimeInput_Accepted() => AreEqual(1, ExecuteScalar<int>("select ISDATE(getdate())"));

    /// <summary>
    /// Probe-confirmed: modern date/time/datetimeoffset are explicitly rejected
    /// with Msg 8116 — ISDATE intentionally lives in the legacy datetime domain.
    /// </summary>
    [TestMethod]
    public void DateInput_RaisesMsg8116()
        => AssertSqlError("select ISDATE(cast('2026-05-12' as date))", 8116);

    [TestMethod]
    public void TimeInput_RaisesMsg8116()
        => AssertSqlError("select ISDATE(cast('12:34:56' as time))", 8116);

    [TestMethod]
    public void DateTimeOffsetInput_RaisesMsg8116()
        => AssertSqlError("select ISDATE(SYSDATETIMEOFFSET())", 8116);

    [TestMethod] public void FloatInput_ReturnsZero() => AreEqual(0, ExecuteScalar<int>("select ISDATE(cast(1.5 as float))"));
}
