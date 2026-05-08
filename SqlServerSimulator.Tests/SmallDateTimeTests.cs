using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>smalldatetime</c> type (4-byte storage,
/// 1-minute granularity, range 1900-01-01 through 2079-06-06 23:59).
/// CAST to/from string lives here; the broader CAST tests stay in
/// <see cref="CastTests"/>.
/// </summary>
[TestClass]
public sealed class SmallDateTimeTests
{
    [TestMethod]
    [DataRow("'2024-01-15 12:00:00'", "2024-01-15T12:00:00")]
    [DataRow("'2024-01-15T12:00:00'", "2024-01-15T12:00:00")]
    [DataRow("'2024-01-15'", "2024-01-15T00:00:00")]
    [DataRow("'20240115'", "2024-01-15T00:00:00")]
    [DataRow("'Jan 15 2024 12:00AM'", "2024-01-15T00:00:00")]
    [DataRow("'jan 15 2024 12:00am'", "2024-01-15T00:00:00")]
    [DataRow("'Jan 15 2024  1:30PM'", "2024-01-15T13:30:00")]
    [DataRow("'1/15/2024'", "2024-01-15T00:00:00")]
    [DataRow("'01/15/2024 12:00'", "2024-01-15T12:00:00")]
    [DataRow("'2024'", "2024-01-01T00:00:00")]
    [DataRow("''", "1900-01-01T00:00:00")]
    [DataRow("'12:30'", "1900-01-01T12:30:00")]
    public void Cast_StringToSmallDateTime(string input, string expectedIso)
    {
        var value = ExecuteScalar($"select cast({input} as smalldatetime)");
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    [DataRow("12:30:00", "12:30:00")]
    [DataRow("12:30:29", "12:30:00")]
    [DataRow("12:30:29.998", "12:30:00")]
    // .999 quantizes to the next 1/300s tick (the 30s mark), then rounds up
    // to the next minute — matching SQL Server's documented behavior.
    [DataRow("12:30:29.999", "12:31:00")]
    [DataRow("12:30:30", "12:31:00")]
    [DataRow("12:30:30.001", "12:31:00")]
    [DataRow("12:30:59.998", "12:31:00")]
    [DataRow("12:30:59.999", "12:31:00")]
    public void Cast_StringToSmallDateTime_RoundsToNearestMinute(string inputTime, string expectedTime)
    {
        var value = (DateTime)ExecuteScalar($"select cast('2024-01-15 {inputTime}' as smalldatetime)")!;
        AreEqual(
            DateTime.Parse($"2024-01-15T{expectedTime}", System.Globalization.CultureInfo.InvariantCulture),
            value);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_EndOfDayRollsForward()
    {
        // 23:59:29.999 quantizes to 23:59:30, which rolls to 00:00 of the
        // next day.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15 23:59:29.999' as smalldatetime)")!;
        AreEqual(new DateTime(2024, 1, 16, 0, 0, 0), value);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtAbsoluteMaxRollsOver_RaisesMsg242()
    {
        // 2079-06-06 23:59:29.999 would round to 2079-06-07 00:00 — past the
        // type's max — so SQL Server raises Msg 242.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('2079-06-06 23:59:29.999' as smalldatetime)"));
        AreEqual("The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_998AtAbsoluteMax_RoundsToValidLastMinute()
    {
        // 2079-06-06 23:59:29.998 quantizes down to 23:59:00 — the last
        // representable minute.
        var value = (DateTime)ExecuteScalar("select cast('2079-06-06 23:59:29.998' as smalldatetime)")!;
        AreEqual(new DateTime(2079, 6, 6, 23, 59, 0), value);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_BelowMin_RaisesMsg242()
    {
        // smalldatetime can't represent dates before 1900-01-01 (uint16 day count).
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('1899-12-31' as smalldatetime)"));
        AreEqual("The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtMin_Works()
    {
        var value = (DateTime)ExecuteScalar("select cast('1900-01-01' as smalldatetime)")!;
        AreEqual(new DateTime(1900, 1, 1), value);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_AboveMax_RaisesMsg242()
    {
        // 2079-06-07 is outside the uint16 day-count range for smalldatetime.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('2079-06-07' as smalldatetime)"));
        AreEqual("The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtAbsoluteMax_Works()
    {
        var value = (DateTime)ExecuteScalar("select cast('2079-06-06 23:59' as smalldatetime)")!;
        AreEqual(new DateTime(2079, 6, 6, 23, 59, 0), value);
    }

    [TestMethod]
    public void Cast_StringToSmallDateTime_BadFormat_RaisesMsg295()
    {
        // smalldatetime uses Msg 295 (distinct from Msg 241 used by every
        // other date/time target).
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('not-a-date' as smalldatetime)"));
        AreEqual("Conversion failed when converting character string to smalldatetime data type.", ex.Message);
    }

    [TestMethod]
    [DataRow("2024-01-15 12:00:00", "Jan 15 2024 12:00PM")]
    [DataRow("2024-01-15 00:00:00", "Jan 15 2024 12:00AM")]
    [DataRow("2024-01-15 13:30:00", "Jan 15 2024  1:30PM")]
    [DataRow("2024-01-05 13:00:00", "Jan  5 2024  1:00PM")]
    [DataRow("1900-01-01 00:00:00", "Jan  1 1900 12:00AM")]
    [DataRow("2079-06-06 23:59:00", "Jun  6 2079 11:59PM")]
    public void Cast_SmallDateTimeToVarchar_UsesLegacyFormat(string source, string expected) =>
        AreEqual(expected, ExecuteScalar($"select cast(cast('{source}' as smalldatetime) as varchar(50))"));

    [TestMethod]
    public void Cast_DateToSmallDateTime_FillsMidnight()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15' as date) as smalldatetime)")!;
        AreEqual(new DateTime(2024, 1, 15), value);
    }

    [TestMethod]
    public void Cast_SmallDateTimeToDate_DropsTime()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as date)")!;
        AreEqual(new DateTime(2024, 1, 15), value);
    }

    [TestMethod]
    public void Cast_SmallDateTimeToDateTime_PreservesValue()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:00' as smalldatetime) as datetime)")!;
        AreEqual(new DateTime(2024, 1, 15, 12, 30, 0), value);
    }

    [TestMethod]
    public void Cast_DateTimeToSmallDateTime_RoundsToMinute()
    {
        // The .997 datetime tick at 12:30:29 keeps the value below the 30s
        // half-up boundary, so it stays at 12:30.
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:29.997' as datetime) as smalldatetime)")!;
        AreEqual(new DateTime(2024, 1, 15, 12, 30, 0), value);
    }

    [TestMethod]
    public void Cast_DateTime2ToSmallDateTime_RoundsToMinute()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:30' as datetime2(3)) as smalldatetime)")!;
        AreEqual(new DateTime(2024, 1, 15, 12, 31, 0), value);
    }

    [TestMethod]
    public void Cast_TimeToSmallDateTime_FillsLegacyDate()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('13:30:00' as time(0)) as smalldatetime)")!;
        AreEqual(new DateTime(1900, 1, 1, 13, 30, 0), value);
    }

    [TestMethod]
    public void Cast_SmallDateTimeToTime_DropsDate()
    {
        var value = (TimeSpan)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as time(0))")!;
        AreEqual(new TimeSpan(13, 30, 0), value);
    }

    [TestMethod]
    public void Cast_SmallDateTimeToDateTimeOffset_AssumesUtcOffset()
    {
        var value = (DateTimeOffset)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as datetimeoffset(0))")!;
        AreEqual(new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero), value);
    }

    [TestMethod]
    public void CreateTable_SmallDateTimeColumn_RoundTripsRowsWithRoundedValues()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d smalldatetime)");
        _ = sim.ExecuteNonQuery("insert into t values ('1900-01-01'), ('2024-01-15 12:30:30'), ('2079-06-06 23:59')");
        using var reader = sim.ExecuteReader("select d from t");
        var rows = new List<DateTime>();
        while (reader.Read())
            rows.Add(reader.GetDateTime(0));
        HasCount(3, rows);
        AreEqual(new DateTime(1900, 1, 1), rows[0]);
        AreEqual(new DateTime(2024, 1, 15, 12, 31, 0), rows[1]);
        AreEqual(new DateTime(2079, 6, 6, 23, 59, 0), rows[2]);
    }

    [TestMethod]
    public void CreateTable_SmallDateTimeWithPrecisionParameter_RaisesMsg2716()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create table t (d smalldatetime(3))"));
        AreEqual("Column, parameter, or variable #1: Cannot specify a column width on data type smalldatetime.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_SmallDateTimeWithZeroPrecision_RaisesMsg1001()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create table t (d smalldatetime(0))"));
        AreEqual("Line 1: Length or precision specification 0 is invalid.", ex.Message);
    }

    [TestMethod]
    public void Parameter_SmallDateTime_AcceptsDateTimeValue()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d smalldatetime)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "insert into t values (@x)";
        var p = command.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = DbType.DateTime;
        // .001 rolls past the 30s half-up boundary → 12:31.
        p.Value = new DateTime(2024, 1, 15, 12, 30, 30, 1);
        _ = command.Parameters.Add(p);
        AreEqual(1, command.ExecuteNonQuery());
        AreEqual(new DateTime(2024, 1, 15, 12, 31, 0), sim.ExecuteScalar("select d from t"));
    }

    [TestMethod]
    public void Parameter_SmallDateTime_AcceptsDateOnlyValue()
    {
        // Date-only value lands at midnight on the smalldatetime field.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d smalldatetime)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "insert into t values (@x)";
        var p = command.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = DbType.DateTime;
        p.Value = new DateOnly(2024, 1, 15);
        _ = command.Parameters.Add(p);
        AreEqual(1, command.ExecuteNonQuery());
        AreEqual(new DateTime(2024, 1, 15, 0, 0, 0), sim.ExecuteScalar("select d from t"));
    }

    [TestMethod]
    public void Equality_TwoSmallDateTimeValuesAtSameMinute_AreEqual()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d smalldatetime)");
        // Both inputs round to 12:30:00.
        _ = sim.ExecuteNonQuery("insert into t values (1, '2024-01-15 12:30:00'), (2, '2024-01-15 12:30:15')");
        using var reader = sim.ExecuteReader("select id from t where d = cast('2024-01-15 12:30:00' as smalldatetime)");
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        HasCount(2, ids);
    }

    [TestMethod]
    [DataRow("0", "1900-01-01T00:00:00")]
    [DataRow("1", "1900-01-02T00:00:00")]
    [DataRow("65535", "2079-06-06T00:00:00")]
    public void Cast_IntToSmallDateTime_TreatsAsDaysSince1900(string input, string expectedIso) =>
        AreEqual(
            DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture),
            ExecuteScalar($"select cast({input} as smalldatetime)"));

    [TestMethod]
    public void Cast_NegativeIntToSmallDateTime_RaisesMsg8115()
    {
        // smalldatetime can't represent dates before 1900-01-01 (uint16 day count).
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(-1 as smalldatetime)"));
        AreEqual("Arithmetic overflow error converting expression to data type smalldatetime.", ex.Message);
    }

    [TestMethod]
    public void Cast_OversizedIntToSmallDateTime_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(65536 as smalldatetime)"));
        AreEqual("Arithmetic overflow error converting expression to data type smalldatetime.", ex.Message);
    }

    [TestMethod]
    public void Cast_SmallDateTimeToInt_ReturnsDaysSince1900()
    {
        AreEqual(0, ExecuteScalar("select cast(cast('1900-01-01' as smalldatetime) as int)"));
        AreEqual(14, ExecuteScalar("select cast(cast('1900-01-15' as smalldatetime) as int)"));
        // 12:00 = 0.5 days → rounds up to next day.
        AreEqual(15, ExecuteScalar("select cast(cast('1900-01-15 12:00:00' as smalldatetime) as int)"));
    }

    [TestMethod]
    public void Cast_SmallDateTimeToTinyint_OverflowRaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(cast('1900-09-14' as smalldatetime) as tinyint)"));
        AreEqual("Arithmetic overflow error converting expression to data type tinyint.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_SmallDateTimePlusInt_StaysSmallDateTime()
    {
        // `sd + int` returns smalldatetime (the date side wins regardless
        // of order). Time portion is preserved through the addition.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15 13:30:00' as smalldatetime) + 1")!;
        AreEqual(new DateTime(2024, 1, 16, 13, 30, 0), value);
    }

    [TestMethod]
    public void Arithmetic_SmallDateTimePlusSmallDateTime_SumDaysFromBase()
    {
        // Same legacy quirk as `dt + dt`: re-interpret the sum as
        // days-since-1900-01-01.
        var value = (DateTime)ExecuteScalar("select cast('1900-01-15' as smalldatetime) + cast('1900-01-10' as smalldatetime)")!;
        AreEqual(new DateTime(1900, 1, 24), value);
    }

    [TestMethod]
    public void Arithmetic_SmallDateTimeMinusInt_UnderflowRaisesMsg8115()
    {
        // Subtraction below 1900-01-01 raises Msg 8115 — smalldatetime's
        // uint16 day count can't go negative.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('1900-01-01' as smalldatetime) - 100"));
        AreEqual("Arithmetic overflow error converting expression to data type smalldatetime.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusSmallDateTime_ReturnsDateTime()
    {
        // Cross-family promotion picks datetime (highest precedence in the
        // legacy pair).
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast('1900-01-10' as smalldatetime)")!;
        AreEqual(new DateTime(2024, 1, 24), value);
    }

    [TestMethod]
    public void Ordering_SmallDateTimeValues_CompareByInstant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d smalldatetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '2024-01-15'), (2, '2024-01-16')");
        using var reader = sim.ExecuteReader("select id from t where d < cast('2024-01-16' as smalldatetime)");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }
}
