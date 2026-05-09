using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the <c>smalldatetime</c> type (4-byte storage,
/// 1-minute granularity, range 1900-01-01 through 2079-06-06 23:59).
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
        => AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture),
            ExecuteScalar($"select cast({input} as smalldatetime)"));

    [TestMethod]
    [DataRow("12:30:00", "12:30:00")]
    [DataRow("12:30:29", "12:30:00")]
    [DataRow("12:30:29.998", "12:30:00")]
    [DataRow("12:30:29.999", "12:31:00")]    // quantizes to 30s tick → rounds up
    [DataRow("12:30:30", "12:31:00")]
    [DataRow("12:30:30.001", "12:31:00")]
    [DataRow("12:30:59.998", "12:31:00")]
    [DataRow("12:30:59.999", "12:31:00")]
    public void Cast_StringToSmallDateTime_RoundsToNearestMinute(string inputTime, string expectedTime)
        => AreEqual(
            DateTime.Parse($"2024-01-15T{expectedTime}", System.Globalization.CultureInfo.InvariantCulture),
            (DateTime)ExecuteScalar($"select cast('2024-01-15 {inputTime}' as smalldatetime)")!);

    [TestMethod]
    public void Cast_StringToSmallDateTime_EndOfDayRollsForward()
        => AreEqual(new DateTime(2024, 1, 16, 0, 0, 0),
            (DateTime)ExecuteScalar("select cast('2024-01-15 23:59:29.999' as smalldatetime)")!);

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtAbsoluteMaxRollsOver_RaisesMsg242()
        => AssertSqlMessage("select cast('2079-06-06 23:59:29.999' as smalldatetime)",
            "The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.");

    [TestMethod]
    public void Cast_StringToSmallDateTime_998AtAbsoluteMax_RoundsToValidLastMinute()
        => AreEqual(new DateTime(2079, 6, 6, 23, 59, 0),
            (DateTime)ExecuteScalar("select cast('2079-06-06 23:59:29.998' as smalldatetime)")!);

    [TestMethod]
    public void Cast_StringToSmallDateTime_BelowMin_RaisesMsg242()
        => AssertSqlMessage("select cast('1899-12-31' as smalldatetime)",
            "The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.");

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtMin_Works()
        => AreEqual(new DateTime(1900, 1, 1), (DateTime)ExecuteScalar("select cast('1900-01-01' as smalldatetime)")!);

    [TestMethod]
    public void Cast_StringToSmallDateTime_AboveMax_RaisesMsg242()
        => AssertSqlMessage("select cast('2079-06-07' as smalldatetime)",
            "The conversion of a varchar data type to a smalldatetime data type resulted in an out-of-range value.");

    [TestMethod]
    public void Cast_StringToSmallDateTime_AtAbsoluteMax_Works()
        => AreEqual(new DateTime(2079, 6, 6, 23, 59, 0),
            (DateTime)ExecuteScalar("select cast('2079-06-06 23:59' as smalldatetime)")!);

    [TestMethod]
    public void Cast_StringToSmallDateTime_BadFormat_RaisesMsg295()
    {
        // smalldatetime uses Msg 295 (distinct from Msg 241 used by every other date/time target).
        AssertSqlMessage("select cast('not-a-date' as smalldatetime)",
        "Conversion failed when converting character string to smalldatetime data type.");
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
        => AreEqual(new DateTime(2024, 1, 15),
            (DateTime)ExecuteScalar("select cast(cast('2024-01-15' as date) as smalldatetime)")!);

    [TestMethod]
    public void Cast_SmallDateTimeToDate_DropsTime()
        => AreEqual(new DateTime(2024, 1, 15),
            (DateTime)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as date)")!);

    [TestMethod]
    public void Cast_SmallDateTimeToDateTime_PreservesValue()
        => AreEqual(new DateTime(2024, 1, 15, 12, 30, 0),
            (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:00' as smalldatetime) as datetime)")!);

    [TestMethod]
    public void Cast_DateTimeToSmallDateTime_RoundsToMinute()
    {
        // The .997 datetime tick at 12:30:29 stays below 30s half-up boundary → 12:30.
        AreEqual(new DateTime(2024, 1, 15, 12, 30, 0),
        (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:29.997' as datetime) as smalldatetime)")!);
    }

    [TestMethod]
    public void Cast_DateTime2ToSmallDateTime_RoundsToMinute()
        => AreEqual(new DateTime(2024, 1, 15, 12, 31, 0),
            (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:30:30' as datetime2(3)) as smalldatetime)")!);

    [TestMethod]
    public void Cast_TimeToSmallDateTime_FillsLegacyDate()
        => AreEqual(new DateTime(1900, 1, 1, 13, 30, 0),
            (DateTime)ExecuteScalar("select cast(cast('13:30:00' as time(0)) as smalldatetime)")!);

    [TestMethod]
    public void Cast_SmallDateTimeToTime_DropsDate()
        => AreEqual(new TimeSpan(13, 30, 0),
            (TimeSpan)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as time(0))")!);

    [TestMethod]
    public void Cast_SmallDateTimeToDateTimeOffset_AssumesUtcOffset()
        => AreEqual(new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero),
            (DateTimeOffset)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as smalldatetime) as datetimeoffset(0))")!);

    [TestMethod]
    public void CreateTable_SmallDateTimeColumn_RoundTripsRowsWithRoundedValues()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (d smalldatetime);
            insert t values ('1900-01-01'), ('2024-01-15 12:30:30'), ('2079-06-06 23:59')
            """);
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
        => AssertSqlMessage("create table t (d smalldatetime(3))",
            "Column, parameter, or variable #1: Cannot specify a column width on data type smalldatetime.");

    [TestMethod]
    public void CreateTable_SmallDateTimeWithZeroPrecision_RaisesMsg1001()
        => AssertSqlMessage("create table t (d smalldatetime(0))",
            "Line 1: Length or precision specification 0 is invalid.");

    [TestMethod]
    public void Parameter_SmallDateTime_AcceptsDateTimeValue()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "create table t (d smalldatetime);insert t values (@x)";
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
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "create table t (d smalldatetime);insert t values (@x)";
        var p = command.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = DbType.DateTime;
        p.Value = new DateOnly(2024, 1, 15);
        _ = command.Parameters.Add(p);
        AreEqual(1, command.ExecuteNonQuery());
        AreEqual(new DateTime(2024, 1, 15, 0, 0, 0), sim.ExecuteScalar("select d from t"));
    }

    // Both inputs round to 12:30:00.
    [TestMethod]
    public void Equality_TwoSmallDateTimeValuesAtSameMinute_AreEqual()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, d smalldatetime);
            insert t values (1, '2024-01-15 12:30:00'), (2, '2024-01-15 12:30:15')
            """);
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
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture),
            ExecuteScalar($"select cast({input} as smalldatetime)"));

    [TestMethod]
    public void Cast_NegativeIntToSmallDateTime_RaisesMsg8115()
        => AssertSqlMessage("select cast(-1 as smalldatetime)",
            "Arithmetic overflow error converting expression to data type smalldatetime.");

    [TestMethod]
    public void Cast_OversizedIntToSmallDateTime_RaisesMsg8115()
        => AssertSqlMessage("select cast(65536 as smalldatetime)",
            "Arithmetic overflow error converting expression to data type smalldatetime.");

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
        => AssertSqlMessage("select cast(cast('1900-09-14' as smalldatetime) as tinyint)",
            "Arithmetic overflow error converting expression to data type tinyint.");

    [TestMethod]
    public void Arithmetic_SmallDateTimePlusInt_StaysSmallDateTime()
        => AreEqual(new DateTime(2024, 1, 16, 13, 30, 0),
            (DateTime)ExecuteScalar("select cast('2024-01-15 13:30:00' as smalldatetime) + 1")!);

    [TestMethod]
    public void Arithmetic_SmallDateTimePlusSmallDateTime_SumDaysFromBase()
    {
        // Same legacy quirk as `dt + dt`: re-interpret sum as days-since-1900-01-01.
        AreEqual(new DateTime(1900, 1, 24),
        (DateTime)ExecuteScalar("select cast('1900-01-15' as smalldatetime) + cast('1900-01-10' as smalldatetime)")!);
    }

    [TestMethod]
    public void Arithmetic_SmallDateTimeMinusInt_UnderflowRaisesMsg8115()
        => AssertSqlMessage("select cast('1900-01-01' as smalldatetime) - 100",
            "Arithmetic overflow error converting expression to data type smalldatetime.");

    [TestMethod]
    public void Arithmetic_DateTimePlusSmallDateTime_ReturnsDateTime()
        => AreEqual(new DateTime(2024, 1, 24),
            (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast('1900-01-10' as smalldatetime)")!);

    [TestMethod]
    public void Ordering_SmallDateTimeValues_CompareByInstant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, d smalldatetime);
            insert t values (1, '2024-01-15'), (2, '2024-01-16')
            """);
        using var reader = sim.ExecuteReader("select id from t where d < cast('2024-01-16' as smalldatetime)");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }
}
