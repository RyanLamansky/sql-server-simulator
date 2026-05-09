using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the legacy <c>datetime</c> type (1/300-second tick
/// granularity, range 1753-01-01 through 9999-12-31 23:59:59.997).
/// </summary>
[TestClass]
public sealed class LegacyDateTimeTests
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
    [DataRow("'01/15/2024 12:00:00'", "2024-01-15T12:00:00")]
    [DataRow("'2024'", "2024-01-01T00:00:00")]
    [DataRow("''", "1900-01-01T00:00:00")]
    public void Cast_StringToDateTime(string input, string expectedIso)
        => AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), ExecuteScalar($"select cast({input} as datetime)"));

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 0)]
    [DataRow(2, 1)]
    [DataRow(3, 1)]
    [DataRow(4, 1)]
    [DataRow(5, 2)]
    [DataRow(6, 2)]
    [DataRow(7, 2)]
    [DataRow(8, 2)]
    [DataRow(9, 3)]
    [DataRow(10, 3)]
    [DataRow(11, 3)]
    [DataRow(12, 4)]
    public void Cast_StringToDateTime_RoundsToNearest1_300Tick(int inputMs, int expectedTickIndex)
    {
        // SQL Server's legacy datetime stores 1/300-second ticks; sub-tick ms inputs round half-up to nearest tick.
        // .NET's DateTime.Millisecond won't match SQL's displayed .003/.007/.010; the canonical tick value does.
        var value = (DateTime)ExecuteScalar($"select cast('2024-01-15 12:00:00.{inputMs:D3}' as datetime)")!;
        var expectedTimeTicks = expectedTickIndex * 10_000_000L / 300;
        AreEqual(new DateTime(2024, 1, 15).Ticks + (12 * TimeSpan.TicksPerHour) + expectedTimeTicks, value.Ticks);
    }

    [TestMethod]
    public void Cast_StringToDateTime_999RollsToNextSecond()
        => AreEqual(new DateTime(2024, 1, 15, 12, 0, 1), (DateTime)ExecuteScalar("select cast('2024-01-15 12:00:00.999' as datetime)")!);

    [TestMethod]
    public void Cast_StringToDateTime_AtAbsoluteMaxRollsOver_RaisesMsg242()
        => AssertSqlMessage("select cast('9999-12-31 23:59:59.999' as datetime)",
            "The conversion of a varchar data type to a datetime data type resulted in an out-of-range value.");

    [TestMethod]
    public void Cast_StringToDateTime_998AtAbsoluteMax_RoundsToValidLastTick()
    {
        // .998 rounds half-up to tick 25_919_999 (last tick of day) → materializes as 23:59:59.9966666 in .NET.
        var value = (DateTime)ExecuteScalar("select cast('9999-12-31 23:59:59.998' as datetime)")!;
        var expected = new DateTime(9999, 12, 31).AddTicks(25_919_999L * TimeSpan.TicksPerSecond / 300);
        AreEqual(expected, value);
    }

    [TestMethod]
    public void Cast_StringToDateTime_BelowMin_RaisesMsg242()
        => AssertSqlMessage("select cast('1752-12-31' as datetime)",
            "The conversion of a varchar data type to a datetime data type resulted in an out-of-range value.");

    [TestMethod]
    public void Cast_StringToDateTime_AtMin_Works()
        => AreEqual(new DateTime(1753, 1, 1), (DateTime)ExecuteScalar("select cast('1753-01-01' as datetime)")!);

    [TestMethod]
    public void Cast_StringToDateTime_BadFormat_RaisesMsg241()
        => AssertSqlMessage("select cast('not-a-date' as datetime)",
            "Conversion failed when converting date and/or time from character string.");

    [TestMethod]
    [DataRow("2024-01-15 12:00:00", "Jan 15 2024 12:00PM")]
    [DataRow("2024-01-15 00:00:00", "Jan 15 2024 12:00AM")]
    [DataRow("2024-01-15 13:30:00", "Jan 15 2024  1:30PM")]
    [DataRow("2024-01-05 12:00:00", "Jan  5 2024 12:00PM")]
    [DataRow("2024-01-05 13:00:00", "Jan  5 2024  1:00PM")]
    [DataRow("2024-01-15 23:00:00", "Jan 15 2024 11:00PM")]
    [DataRow("1753-01-01 00:00:00", "Jan  1 1753 12:00AM")]
    public void Cast_DateTimeToVarchar_UsesLegacyFormat(string source, string expected) =>
        AreEqual(expected, ExecuteScalar($"select cast(cast('{source}' as datetime) as varchar(50))"));

    [TestMethod]
    public void Cast_DateToDateTime_FillsMidnight()
        => AreEqual(new DateTime(2024, 1, 15), (DateTime)ExecuteScalar("select cast(cast('2024-01-15' as date) as datetime)")!);

    [TestMethod]
    public void Cast_DateTimeToDate_DropsTime()
        => AreEqual(new DateTime(2024, 1, 15), (DateTime)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as datetime) as date)")!);

    [TestMethod]
    public void Cast_DateTimeToDateTime2_PreservesValue()
    {
        // datetime → datetime2(7) is lossless; .997 input rounds to tick 299 = 9_966_666 ticks past second.
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:00:00.997' as datetime) as datetime2(7))")!;
        var expectedTicks = new DateTime(2024, 1, 15, 12, 0, 0).Ticks + (299L * 10_000_000 / 300);
        AreEqual(expectedTicks, value.Ticks);
    }

    [TestMethod]
    public void Cast_DateTime2ToDateTime_RoundsToTick()
        => AreEqual(new DateTime(2024, 1, 15, 12, 0, 0, 500),
            (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:00:00.500' as datetime2(3)) as datetime)")!);

    [TestMethod]
    public void Cast_TimeToDateTime_FillsLegacyDate()
        => AreEqual(new DateTime(1900, 1, 1, 13, 30, 0),
            (DateTime)ExecuteScalar("select cast(cast('13:30:00' as time(0)) as datetime)")!);

    [TestMethod]
    public void Cast_DateTimeToTime_DropsDate()
        => AreEqual(new TimeSpan(13, 30, 45),
            (TimeSpan)ExecuteScalar("select cast(cast('2024-01-15 13:30:45' as datetime) as time(0))")!);

    [TestMethod]
    public void Cast_DateTimeToDateTimeOffset_AssumesUtcOffset()
        => AreEqual(new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero),
            (DateTimeOffset)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as datetime) as datetimeoffset(0))")!);

    [TestMethod]
    public void CreateTable_DateTimeColumn_RoundTripsRowsWithRoundedValues()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d datetime)");
        _ = sim.ExecuteNonQuery("insert into t values ('1900-01-01'), ('2024-01-15 12:00:00.998')");
        using var reader = sim.ExecuteReader("select d from t");
        var rows = new List<DateTime>();
        while (reader.Read())
            rows.Add(reader.GetDateTime(0));
        HasCount(2, rows);
        AreEqual(new DateTime(1900, 1, 1), rows[0]);
        // .998 rounds half-up; lands at 9_966_666 100-ns ticks past 12:00:00 (.NET sees ms=996).
        AreEqual(new DateTime(2024, 1, 15, 12, 0, 0).AddTicks(9_966_666), rows[1]);
    }

    [TestMethod]
    public void CreateTable_DateTimeWithPrecisionParameter_RaisesMsg2716()
        => AssertSqlMessage("create table t (d datetime(3))",
            "Column, parameter, or variable #1: Cannot specify a column width on data type datetime.");

    [TestMethod]
    public void CreateTable_DateTimeWithZeroPrecision_RaisesMsg1001()
        => AssertSqlMessage("create table t (d datetime(0))", "Line 1: Length or precision specification 0 is invalid.");

    [TestMethod]
    public void Parameter_DateTime_AcceptsDbTypeDateTime()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @x";
        var p = command.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = DbType.DateTime;
        p.Value = new DateTime(2024, 1, 15, 12, 0, 0, 999);
        _ = command.Parameters.Add(p);
        // .999 ms rolls to next-second .000 per the rounding rule.
        AreEqual(new DateTime(2024, 1, 15, 12, 0, 1, 0), command.ExecuteScalar());
    }

    [TestMethod]
    public void Equality_TwoDateTimeValues_AtSameTickAreEqual()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d datetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '2024-01-15 12:00:00.997'), (2, '2024-01-15 12:00:00.997')");
        using var reader = sim.ExecuteReader("select id from t where d = cast('2024-01-15 12:00:00.997' as datetime)");
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        HasCount(2, ids);
    }

    [TestMethod]
    public void Equality_DifferentTicksAreUnequal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d datetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '2024-01-15 12:00:00.000'), (2, '2024-01-15 12:00:00.003')");
        using var reader = sim.ExecuteReader("select id from t where d = cast('2024-01-15 12:00:00.000' as datetime)");
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        HasCount(1, ids);
        AreEqual(1, ids[0]);
    }

    [TestMethod]
    [DataRow("0", "1900-01-01T00:00:00")]
    [DataRow("1", "1900-01-02T00:00:00")]
    [DataRow("-1", "1899-12-31T00:00:00")]
    [DataRow("-53690", "1753-01-01T00:00:00")]
    [DataRow("2958463", "9999-12-31T00:00:00")]
    [DataRow("45000", "2023-03-17T00:00:00")]
    public void Cast_IntToDateTime_TreatsAsDaysSince1900(string input, string expectedIso)
        => AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture),
            (DateTime)ExecuteScalar($"select cast({input} as datetime)")!);

    [TestMethod]
    public void Cast_IntToDateTime_BelowMin_RaisesMsg8115()
        => AssertSqlMessage("select cast(-53691 as datetime)",
            "Arithmetic overflow error converting expression to data type datetime.");

    [TestMethod]
    [DataRow("0.5", "1900-01-01T12:00:00")]
    [DataRow("1.25", "1900-01-02T06:00:00")]
    [DataRow("1.5", "1900-01-02T12:00:00")]
    [DataRow("-0.5", "1899-12-31T12:00:00")]
    public void Cast_FractionalDecimalToDateTime_PicksUpTimeOfDay(string input, string expectedIso)
        => AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture),
            (DateTime)ExecuteScalar($"select cast({input} as datetime)")!);

    [TestMethod]
    public void Cast_FractionalFloatToDateTime_PicksUpTimeOfDay()
        => AreEqual(new DateTime(1900, 1, 2, 6, 0, 0),
            (DateTime)ExecuteScalar("select cast(cast(1.25 as float) as datetime)")!);

    [TestMethod]
    public void Cast_FractionalToSmallDateTime_PicksUpTimeOfDay()
        => AreEqual(new DateTime(1900, 1, 1, 12, 0, 0),
            (DateTime)ExecuteScalar("select cast(0.5 as smalldatetime)")!);

    [TestMethod]
    public void Cast_IntToDateTime_AboveMax_RaisesMsg8115()
        => AssertSqlMessage("select cast(2958464 as datetime)",
            "Arithmetic overflow error converting expression to data type datetime.");

    [TestMethod]
    public void Cast_BigintToDateTime_FarOutOfRange_RaisesMsg8115()
        => AssertSqlMessage("select cast(cast(100000000 as bigint) as datetime)",
            "Arithmetic overflow error converting expression to data type datetime.");

    [TestMethod]
    [DataRow("'1900-01-01'", 0)]
    [DataRow("'1900-01-02'", 1)]
    [DataRow("'1899-12-31'", -1)]
    [DataRow("'2024-01-15'", 45304)]
    public void Cast_DateTimeToInt_ReturnsDaysSince1900(string seed, int expectedDays) =>
        AreEqual(expectedDays, ExecuteScalar($"select cast(cast({seed} as datetime) as int)"));

    [TestMethod]
    [DataRow("06:00:00", 0)]
    [DataRow("11:59:59.998", 0)]    // quantizes to .997 tick → under half-day → down
    [DataRow("11:59:59.999", 1)]    // quantizes to next-second tick → exactly half-day → up
    [DataRow("12:00:00", 1)]
    [DataRow("18:00:00", 1)]
    public void Cast_DateTimeToInt_RoundsHalfAwayFromZero(string time, int expectedDays) =>
        AreEqual(expectedDays, ExecuteScalar($"select cast(cast('1900-01-01 {time}' as datetime) as int)"));

    [TestMethod]
    public void Cast_NegativeDateTimeToInt_RoundsTowardMoreNegative()
    {
        // 1899-12-31 12:00:00 = -0.5 days → -1 (away from zero).
        AreEqual(-1, ExecuteScalar("select cast(cast('1899-12-31 12:00:00' as datetime) as int)"));
        // 1899-12-30 18:00:00 = -1.25 days → -1 (toward zero).
        AreEqual(-1, ExecuteScalar("select cast(cast('1899-12-30 18:00:00' as datetime) as int)"));
    }

    [TestMethod]
    public void Cast_DateTimeToTinyint_OverflowRaisesMsg8115()
        => AssertSqlMessage("select cast(cast('1900-09-14' as datetime) as tinyint)",
            "Arithmetic overflow error converting expression to data type tinyint.");

    [TestMethod]
    public void Cast_DateTimeToBit_NonZeroIsTrue()
    {
        IsFalse(ExecuteScalar<bool>("select cast(cast('1900-01-01' as datetime) as bit)"));
        IsTrue(ExecuteScalar<bool>("select cast(cast('1900-01-02' as datetime) as bit)"));
    }

    [TestMethod]
    public void Cast_BitToDateTime_ZeroAndOneAreFirstTwoDays()
    {
        AreEqual(new DateTime(1900, 1, 1), ExecuteScalar("select cast(cast(0 as bit) as datetime)"));
        AreEqual(new DateTime(1900, 1, 2), ExecuteScalar("select cast(cast(1 as bit) as datetime)"));
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusInt_AddsDays()
        => AreEqual(new DateTime(2024, 1, 16), (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + 1")!);

    [TestMethod]
    public void Arithmetic_IntPlusDateTime_AddsDays()
        => AreEqual(new DateTime(2024, 1, 16), (DateTime)ExecuteScalar("select 1 + cast('2024-01-15' as datetime)")!);

    [TestMethod]
    public void Arithmetic_DateTimeMinusInt_SubtractsDays()
        => AreEqual(new DateTime(2024, 1, 14), (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) - 1")!);

    [TestMethod]
    public void Arithmetic_DateTimePlusInt_PreservesTimeOfDay()
        => AreEqual(new DateTime(2024, 1, 16, 13, 30, 0),
            (DateTime)ExecuteScalar("select cast('2024-01-15 13:30:00' as datetime) + 1")!);

    [TestMethod]
    public void Arithmetic_DateTimePlusBigInt_StaysDateTime()
        => AreEqual(new DateTime(2024, 1, 16),
            (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast(1 as bigint)")!);

    [TestMethod]
    public void Arithmetic_DateTimePlusDateTime_SumDaysFromBase()
        => AreEqual(new DateTime(2148, 1, 24),
            (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast('2024-01-10' as datetime)")!);

    [TestMethod]
    public void Arithmetic_DateTimeMinusDateTime_DiffDaysFromBase()
        => AreEqual(new DateTime(1900, 1, 6),
            (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) - cast('2024-01-10' as datetime)")!);

    [TestMethod]
    public void Arithmetic_DateTimePlus_OverflowRaisesMsg8115()
        => AssertSqlMessage("select cast('9999-12-30' as datetime) + 100",
            "Arithmetic overflow error converting expression to data type datetime.");

    [TestMethod]
    public void Arithmetic_DateTimePlus_NullIntReturnsNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select cast('2024-01-15' as datetime) + cast(null as int)"));

    [TestMethod]
    public void Arithmetic_NullDateTimePlusInt_ReturnsNull()
        => AreEqual(DBNull.Value, ExecuteScalar("select cast(null as datetime) + 1"));

    [TestMethod]
    public void Ordering_DateTimeValues_CompareByInstant()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int, d datetime)");
        _ = sim.ExecuteNonQuery("insert into t values (1, '2024-01-15'), (2, '2024-01-16')");
        using var reader = sim.ExecuteReader("select id from t where d < cast('2024-01-16' as datetime)");
        IsTrue(reader.Read());
        AreEqual(1, reader.GetInt32(0));
        IsFalse(reader.Read());
    }
}
