using System.Data;
using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the legacy <c>datetime</c> type (1/300-second tick
/// granularity, range 1753-01-01 through 9999-12-31 23:59:59.997).
/// CAST-to/from-string lives here; the broader CAST tests stay in
/// <see cref="CastTests"/>.
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
    {
        var value = ExecuteScalar($"select cast({input} as datetime)");
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), value);
    }

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
        // SQL Server's legacy datetime stores 1/300-second ticks; sub-tick
        // millisecond inputs round half-up to the nearest tick boundary.
        // Tick K maps to (K * 10_000_000 / 300) 100-ns ticks (with truncation),
        // so the on-the-wire .NET DateTime.Millisecond doesn't match SQL's
        // displayed .003/.007/.010 — the underlying canonical value does.
        var value = (DateTime)ExecuteScalar($"select cast('2024-01-15 12:00:00.{inputMs:D3}' as datetime)")!;
        var expectedTimeTicks = expectedTickIndex * 10_000_000L / 300;
        AreEqual(new DateTime(2024, 1, 15).Ticks + (12 * TimeSpan.TicksPerHour) + expectedTimeTicks, value.Ticks);
    }

    [TestMethod]
    public void Cast_StringToDateTime_999RollsToNextSecond()
    {
        // .999 ms is closer to the next-second .000 tick than to .997 of the
        // current second; SQL Server rounds it up.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15 12:00:00.999' as datetime)")!;
        AreEqual(new DateTime(2024, 1, 15, 12, 0, 1), value);
    }

    [TestMethod]
    public void Cast_StringToDateTime_AtAbsoluteMaxRollsOver_RaisesMsg242()
    {
        // 9999-12-31 23:59:59.999 would round to 10000-01-01 00:00:00 — past
        // the type's max — so SQL Server raises Msg 242 instead of clamping.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('9999-12-31 23:59:59.999' as datetime)"));
        AreEqual("The conversion of a varchar data type to a datetime data type resulted in an out-of-range value.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringToDateTime_998AtAbsoluteMax_RoundsToValidLastTick()
    {
        // .998 rounds half-up to tick 25_919_999 (the last tick of the day),
        // which materializes as 9999-12-31 23:59:59.9966666 in .NET (SQL
        // Server formats this as ".997" via convert(..., 121)).
        var value = (DateTime)ExecuteScalar("select cast('9999-12-31 23:59:59.998' as datetime)")!;
        var expected = new DateTime(9999, 12, 31).AddTicks(25_919_999L * TimeSpan.TicksPerSecond / 300);
        AreEqual(expected, value);
    }

    [TestMethod]
    public void Cast_StringToDateTime_BelowMin_RaisesMsg242()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('1752-12-31' as datetime)"));
        AreEqual("The conversion of a varchar data type to a datetime data type resulted in an out-of-range value.", ex.Message);
    }

    [TestMethod]
    public void Cast_StringToDateTime_AtMin_Works()
    {
        var value = (DateTime)ExecuteScalar("select cast('1753-01-01' as datetime)")!;
        AreEqual(new DateTime(1753, 1, 1), value);
    }

    [TestMethod]
    public void Cast_StringToDateTime_BadFormat_RaisesMsg241()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('not-a-date' as datetime)"));
        AreEqual("Conversion failed when converting date and/or time from character string.", ex.Message);
    }

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
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15' as date) as datetime)")!;
        AreEqual(new DateTime(2024, 1, 15), value);
    }

    [TestMethod]
    public void Cast_DateTimeToDate_DropsTime()
    {
        // Reader surfaces date as DateTime at midnight.
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as datetime) as date)")!;
        AreEqual(new DateTime(2024, 1, 15), value);
    }

    [TestMethod]
    public void Cast_DateTimeToDateTime2_PreservesValue()
    {
        // datetime → datetime2(7) is lossless: the 1/300-tick value is
        // already canonical at 100-ns resolution. .997 input rounds to
        // tick 299, which materializes at 9_966_666 ticks past the second.
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:00:00.997' as datetime) as datetime2(7))")!;
        var expectedTicks = new DateTime(2024, 1, 15, 12, 0, 0).Ticks + (299L * 10_000_000 / 300);
        AreEqual(expectedTicks, value.Ticks);
    }

    [TestMethod]
    public void Cast_DateTime2ToDateTime_RoundsToTick()
    {
        // datetime2(3) value of .500 ms is exactly at tick 150 (since
        // 150 × 1/300 = .500); no rounding artifact.
        var value = (DateTime)ExecuteScalar("select cast(cast('2024-01-15 12:00:00.500' as datetime2(3)) as datetime)")!;
        AreEqual(new DateTime(2024, 1, 15, 12, 0, 0, 500), value);
    }

    [TestMethod]
    public void Cast_TimeToDateTime_FillsLegacyDate()
    {
        var value = (DateTime)ExecuteScalar("select cast(cast('13:30:00' as time(0)) as datetime)")!;
        AreEqual(new DateTime(1900, 1, 1, 13, 30, 0), value);
    }

    [TestMethod]
    public void Cast_DateTimeToTime_DropsDate()
    {
        var value = (TimeSpan)ExecuteScalar("select cast(cast('2024-01-15 13:30:45' as datetime) as time(0))")!;
        AreEqual(new TimeSpan(13, 30, 45), value);
    }

    [TestMethod]
    public void Cast_DateTimeToDateTimeOffset_AssumesUtcOffset()
    {
        var value = (DateTimeOffset)ExecuteScalar("select cast(cast('2024-01-15 13:30:00' as datetime) as datetimeoffset(0))")!;
        AreEqual(new DateTimeOffset(2024, 1, 15, 13, 30, 0, TimeSpan.Zero), value);
    }

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
        AreEqual(2, rows.Count);
        AreEqual(new DateTime(1900, 1, 1), rows[0]);
        // .998 rounds half-up to the next tick boundary, materializing at
        // 9_966_666 100-ns ticks past 12:00:00 (SQL Server displays this as
        // ".997" via convert(..., 121); .NET's millisecond view is 996).
        var expected = new DateTime(2024, 1, 15, 12, 0, 0).AddTicks(9_966_666);
        AreEqual(expected, rows[1]);
    }

    [TestMethod]
    public void CreateTable_DateTimeWithPrecisionParameter_RaisesMsg2716()
    {
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create table t (d datetime(3))"));
        AreEqual("Column, parameter, or variable #1: Cannot specify a column width on data type datetime.", ex.Message);
    }

    [TestMethod]
    public void CreateTable_DateTimeWithZeroPrecision_RaisesMsg1001()
    {
        // Length-or-precision-zero check fires before the column-width check.
        var ex = Throws<DbException>(() => new Simulation().ExecuteNonQuery("create table t (d datetime(0))"));
        AreEqual("Line 1: Length or precision specification 0 is invalid.", ex.Message);
    }

    [TestMethod]
    public void Parameter_DateTime_AcceptsDbTypeDateTime()
    {
        // DbType.DateTime explicitly opts into legacy datetime.
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select @x";
        var p = command.CreateParameter();
        p.ParameterName = "@x";
        p.DbType = DbType.DateTime;
        p.Value = new DateTime(2024, 1, 15, 12, 0, 0, 999);
        _ = command.Parameters.Add(p);
        // The .999 ms rolls to next-second .000 per the rounding rule.
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
        AreEqual(2, ids.Count);
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
        AreEqual(1, ids.Count);
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
    {
        // Legacy datetime accepts integer casts as days-since-1900-01-01.
        var value = (DateTime)ExecuteScalar($"select cast({input} as datetime)")!;
        AreEqual(DateTime.Parse(expectedIso, System.Globalization.CultureInfo.InvariantCulture), value);
    }

    [TestMethod]
    public void Cast_IntToDateTime_BelowMin_RaisesMsg8115()
    {
        // -53691 is one day before 1753-01-01 — legacy datetime's minimum
        // — so SQL Server raises arithmetic-overflow rather than the
        // varchar-source Msg 242.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(-53691 as datetime)"));
        AreEqual("Arithmetic overflow error converting expression to data type datetime.", ex.Message);
    }

    [TestMethod]
    public void Cast_IntToDateTime_AboveMax_RaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(2958464 as datetime)"));
        AreEqual("Arithmetic overflow error converting expression to data type datetime.", ex.Message);
    }

    [TestMethod]
    public void Cast_BigintToDateTime_FarOutOfRange_RaisesMsg8115()
    {
        // Pick a value comfortably past datetime's MaxDayCount (2,958,463)
        // that still fits in int — keeps the literal-parse path simple.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(cast(100000000 as bigint) as datetime)"));
        AreEqual("Arithmetic overflow error converting expression to data type datetime.", ex.Message);
    }

    [TestMethod]
    [DataRow("'1900-01-01'", 0)]
    [DataRow("'1900-01-02'", 1)]
    [DataRow("'1899-12-31'", -1)]
    [DataRow("'2024-01-15'", 45304)]
    public void Cast_DateTimeToInt_ReturnsDaysSince1900(string seed, int expectedDays) =>
        AreEqual(expectedDays, ExecuteScalar($"select cast(cast({seed} as datetime) as int)"));

    [TestMethod]
    [DataRow("06:00:00", 0)]
    // 11:59:59.998 quantizes to 11:59:59.997 (under half-day) → rounds down.
    [DataRow("11:59:59.998", 0)]
    // 11:59:59.999 quantizes to next-second tick → exactly half-day → up.
    [DataRow("11:59:59.999", 1)]
    [DataRow("12:00:00", 1)]
    [DataRow("18:00:00", 1)]
    public void Cast_DateTimeToInt_RoundsHalfAwayFromZero(string time, int expectedDays) =>
        AreEqual(expectedDays, ExecuteScalar($"select cast(cast('1900-01-01 {time}' as datetime) as int)"));

    [TestMethod]
    public void Cast_NegativeDateTimeToInt_RoundsTowardMoreNegative()
    {
        // 1899-12-31 12:00:00 = -0.5 days → rounds to -1 (away from zero).
        AreEqual(-1, ExecuteScalar("select cast(cast('1899-12-31 12:00:00' as datetime) as int)"));
        // 1899-12-30 18:00:00 = -1.25 days → -0.25 fractional → -1 (toward zero).
        AreEqual(-1, ExecuteScalar("select cast(cast('1899-12-30 18:00:00' as datetime) as int)"));
    }

    [TestMethod]
    public void Cast_DateTimeToTinyint_OverflowRaisesMsg8115()
    {
        // Day 256 = 1900-09-14, which doesn't fit in tinyint.
        var ex = Throws<DbException>(() => ExecuteScalar("select cast(cast('1900-09-14' as datetime) as tinyint)"));
        AreEqual("Arithmetic overflow error converting expression to data type tinyint.", ex.Message);
    }

    [TestMethod]
    public void Cast_DateTimeToBit_NonZeroIsTrue()
    {
        AreEqual(false, ExecuteScalar("select cast(cast('1900-01-01' as datetime) as bit)"));
        AreEqual(true, ExecuteScalar("select cast(cast('1900-01-02' as datetime) as bit)"));
    }

    [TestMethod]
    public void Cast_BitToDateTime_ZeroAndOneAreFirstTwoDays()
    {
        AreEqual(new DateTime(1900, 1, 1), ExecuteScalar("select cast(cast(0 as bit) as datetime)"));
        AreEqual(new DateTime(1900, 1, 2), ExecuteScalar("select cast(cast(1 as bit) as datetime)"));
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusInt_AddsDays()
    {
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + 1")!;
        AreEqual(new DateTime(2024, 1, 16), value);
    }

    [TestMethod]
    public void Arithmetic_IntPlusDateTime_AddsDays()
    {
        var value = (DateTime)ExecuteScalar("select 1 + cast('2024-01-15' as datetime)")!;
        AreEqual(new DateTime(2024, 1, 16), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimeMinusInt_SubtractsDays()
    {
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) - 1")!;
        AreEqual(new DateTime(2024, 1, 14), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusInt_PreservesTimeOfDay()
    {
        // Adding a whole-day integer doesn't disturb the time portion.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15 13:30:00' as datetime) + 1")!;
        AreEqual(new DateTime(2024, 1, 16, 13, 30, 0), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusBigInt_StaysDateTime()
    {
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast(1 as bigint)")!;
        AreEqual(new DateTime(2024, 1, 16), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlusDateTime_SumDaysFromBase()
    {
        // SQL Server's legacy `dt + dt` quirk: re-interprets the sum of
        // the two day-counts as days-since-1900-01-01. `'2024-01-15' +
        // '2024-01-10'` lands at 90,605 days from 1900 = 2148-01-24.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) + cast('2024-01-10' as datetime)")!;
        AreEqual(new DateTime(2148, 1, 24), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimeMinusDateTime_DiffDaysFromBase()
    {
        // `'2024-01-15' - '2024-01-10'` = 5 days → re-interpret as
        // 5 days from 1900-01-01 = 1900-01-06.
        var value = (DateTime)ExecuteScalar("select cast('2024-01-15' as datetime) - cast('2024-01-10' as datetime)")!;
        AreEqual(new DateTime(1900, 1, 6), value);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlus_OverflowRaisesMsg8115()
    {
        var ex = Throws<DbException>(() => ExecuteScalar("select cast('9999-12-30' as datetime) + 100"));
        AreEqual("Arithmetic overflow error converting expression to data type datetime.", ex.Message);
    }

    [TestMethod]
    public void Arithmetic_DateTimePlus_NullIntReturnsNull()
    {
        // DbDataReader surfaces SQL NULL as DBNull at the public boundary.
        AreEqual(DBNull.Value, ExecuteScalar("select cast('2024-01-15' as datetime) + cast(null as int)"));
    }

    [TestMethod]
    public void Arithmetic_NullDateTimePlusInt_ReturnsNull()
    {
        AreEqual(DBNull.Value, ExecuteScalar("select cast(null as datetime) + 1"));
    }

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
