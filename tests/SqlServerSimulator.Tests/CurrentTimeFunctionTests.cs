using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>GETDATE</c> / <c>GETUTCDATE</c> / <c>SYSDATETIME</c> /
/// <c>SYSUTCDATETIME</c> / <c>SYSDATETIMEOFFSET</c> / <c>CURRENT_TIMESTAMP</c>.
/// The simulator captures <see cref="DateTime.UtcNow"/> once per top-level
/// statement and serves every call within that statement from the snapshot,
/// matching SQL Server's per-statement freeze. Per the Azure SQL Database
/// default, the simulator does no local-time conversion: every variant
/// returns the same UTC instant; <c>SYSDATETIMEOFFSET</c> reports
/// <c>+00:00</c>.
/// </summary>
[TestClass]
public sealed class CurrentTimeFunctionTests
{
    [TestMethod]
    public void GetDate_ReturnsDateTime_RecentValue()
    {
        var before = DateTime.UtcNow;
        var actual = (DateTime)ExecuteScalar("select getdate()")!;
        var after = DateTime.UtcNow;
        IsTrue(actual >= before.AddSeconds(-1) && actual <= after.AddSeconds(1),
            $"getdate()={actual:o} not within +/-1s of [{before:o}, {after:o}]");
    }

    [TestMethod]
    public void GetDate_DataTypeName_IsDatetime()
    {
        using var reader = new Simulation().ExecuteReader("select getdate()");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void GetUtcDate_DataTypeName_IsDatetime()
    {
        using var reader = new Simulation().ExecuteReader("select getutcdate()");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void SysDateTime_DataTypeName_IsDateTime2_7()
    {
        using var reader = new Simulation().ExecuteReader("select sysdatetime()");
        IsTrue(reader.Read());
        AreEqual("datetime2", reader.GetDataTypeName(0));
        var value = reader.GetDateTime(0);
        IsGreaterThan(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), value);
    }

    [TestMethod]
    public void SysUtcDateTime_DataTypeName_IsDateTime2_7()
    {
        using var reader = new Simulation().ExecuteReader("select sysutcdatetime()");
        IsTrue(reader.Read());
        AreEqual("datetime2", reader.GetDataTypeName(0));
    }

    [TestMethod]
    public void SysDateTimeOffset_DataTypeName_IsDatetimeoffset_OffsetIsZero()
    {
        using var reader = new Simulation().ExecuteReader("select sysdatetimeoffset()");
        IsTrue(reader.Read());
        AreEqual("datetimeoffset", reader.GetDataTypeName(0));
        var value = reader.GetFieldValue<DateTimeOffset>(0);
        AreEqual(TimeSpan.Zero, value.Offset);
    }

    [TestMethod]
    public void CurrentTimestamp_NoParens_ReturnsDatetime()
    {
        using var reader = new Simulation().ExecuteReader("select current_timestamp");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
    }

    // Real SQL Server raises Msg 102 with "near ')'"; the simulator's
    // outer-parser fallback raises Msg 102 too (different "near" token).
    [TestMethod]
    public void CurrentTimestamp_WithParens_RaisesSyntaxError()
        => AssertSqlError("select current_timestamp()", 102);

    [TestMethod]
    public void PerStatementFreeze_TwoSysDateTimeCalls_IdenticalToTheTick()
    {
        using var reader = new Simulation().ExecuteReader("select sysdatetime() as a, sysdatetime() as b");
        IsTrue(reader.Read());
        AreEqual(reader.GetDateTime(0).Ticks, reader.GetDateTime(1).Ticks);
    }

    [TestMethod]
    public void PerStatementFreeze_AllSixInOneSelect_AllReturnSameInstant()
    {
        using var reader = new Simulation().ExecuteReader("""
            select getdate(), getutcdate(), sysdatetime(), sysutcdatetime(), sysdatetimeoffset(), current_timestamp
            """);
        IsTrue(reader.Read());
        // GETDATE/GETUTCDATE/CURRENT_TIMESTAMP round to 1/300s tick; SYSDATETIME/SYSUTCDATETIME
        // are 100ns. After rounding the legacy datetime variants to 1/300s, the SYSDATETIME
        // tick should round to the same legacy bucket. Compare via legacy-tick rounding.
        var sysDt = reader.GetDateTime(2);
        var legacyExpected = RoundToLegacyTick(sysDt);
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(0).Ticks);
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(1).Ticks);
        AreEqual(reader.GetDateTime(2).Ticks, reader.GetDateTime(3).Ticks);
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(5).Ticks);
        var dto = reader.GetFieldValue<DateTimeOffset>(4);
        AreEqual(reader.GetDateTime(2).Ticks, dto.UtcDateTime.Ticks);
    }

    [TestMethod]
    public void Update_StampsAllRows_WithSameValue()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int, stamp datetime2(7));
            insert t values (1, null), (2, null), (3, null), (4, null), (5, null)
            """).ExecuteNonQuery();

        _ = connection.CreateCommand("update t set stamp = sysdatetime()").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select stamp from t order by id").ExecuteReader();
        var stamps = new List<DateTime>();
        while (reader.Read()) stamps.Add(reader.GetDateTime(0));
        HasCount(5, stamps);
        var first = stamps[0];
        foreach (var s in stamps)
            AreEqual(first.Ticks, s.Ticks);
    }

    [TestMethod]
    public void Default_GetUtcDate_EvaluatesPerInsert_NotPerCreate()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int, created_at datetime2(7) default sysutcdatetime())
            """).ExecuteNonQuery();

        var stamps = new List<DateTime>();
        for (var i = 1; i <= 4; i++)
        {
            if (i > 1) Thread.Sleep(2);
            _ = connection.CreateCommand($"insert t (id) values ({i})").ExecuteNonQuery();
        }

        using var reader = connection.CreateCommand("select created_at from t order by id").ExecuteReader();
        while (reader.Read()) stamps.Add(reader.GetDateTime(0));
        HasCount(4, stamps);
        // Stamps should be monotonically non-decreasing across the 4 inserts; the first vs
        // last should differ by at least a tick (Thread.Sleep(2) between each forces drift).
        IsGreaterThan(stamps[0], stamps[^1]);
        for (var i = 1; i < stamps.Count; i++)
            IsGreaterThanOrEqualTo(stamps[i - 1], stamps[i]);
    }

    [TestMethod]
    public void GetDate_InWhereClause_FiltersByCurrentInstant()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table events (id int, occurred_at datetime);
            insert events values (1, '2020-01-01'), (2, '2099-12-31')
            """).ExecuteNonQuery();

        var futureCount = (int)connection.CreateCommand("select count(*) from events where occurred_at > getdate()").ExecuteScalar()!;
        AreEqual(1, futureCount);

        var pastCount = (int)connection.CreateCommand("select count(*) from events where occurred_at < getdate()").ExecuteScalar()!;
        AreEqual(1, pastCount);
    }

    // CURRENT_TIMESTAMP routes through the reserved-keyword dispatch in
    // Expression.Parse's expression-start switch, not as a bare identifier
    // through Reference. This test pins that current_timestamp in a FROM-less
    // SELECT produces a datetime value rather than being treated as a column.
    [TestMethod]
    public void CurrentTimestamp_AsColumnAlias_DoesNotConfuseParser()
        => AreEqual("datetime", DescribeFirstColumnTypeName("select current_timestamp"));

    [TestMethod]
    public void Functions_AreCaseInsensitive()
    {
        AreEqual("datetime", DescribeFirstColumnTypeName("select GeTdAtE()"));
        AreEqual("datetime", DescribeFirstColumnTypeName("select GETUTCDATE()"));
        AreEqual("datetime2", DescribeFirstColumnTypeName("select SYSDATETIME()"));
        AreEqual("datetime", DescribeFirstColumnTypeName("select Current_Timestamp"));
    }

    // A view / inline-TVF body runs on its own child batch that never reaches
    // the dispatch loop, so it inherits the referencing statement's freeze
    // instead of being stamped. Before it did, current-time calls in such a
    // body read an unstamped 0001-01-01 — outside legacy datetime's range, so
    // bare GETDATE() raised Msg 242 (at first read for a view; at CREATE for an
    // inline TVF, whose output-column inference parses the body).
    [TestMethod]
    public void View_ProjectingGetDate_ReturnsCurrentInstant()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create view v as select getdate() as d");
        var before = DateTime.UtcNow;
        using var reader = simulation.ExecuteReader("select d from v");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
        AssertRecent(before, reader.GetDateTime(0));
    }

    [TestMethod]
    public void InlineTvf_ProjectingGetDate_ReturnsCurrentInstant()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create function dbo.m() returns table as return (select getdate() as d)");
        var before = DateTime.UtcNow;
        using var reader = simulation.ExecuteReader("select d from dbo.m()");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
        AssertRecent(before, reader.GetDateTime(0));
    }

    [TestMethod]
    public void View_ProjectingCastGetDateToDateTime2_ReturnsCurrentInstant()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create view v as select cast(getdate() as datetime2) as d");
        var before = DateTime.UtcNow;
        using var reader = simulation.ExecuteReader("select d from v");
        IsTrue(reader.Read());
        AreEqual("datetime2", reader.GetDataTypeName(0));
        AssertRecent(before, reader.GetDateTime(0));
    }

    // Projected types match real SQL Server's sys.columns for the same view
    // (probe-confirmed: system_type_id 61 / 42 / 43 in that order).
    [TestMethod]
    public void View_ProjectingWholeCurrentTimeFamily_TypesAndInstantMatch()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("""
            create view v as select getdate() as d, getutcdate() as u, current_timestamp as c,
                sysdatetime() as s, sysutcdatetime() as su, sysdatetimeoffset() as o,
                dateadd(day, 1, getdate()) as tomorrow
            """);
        var before = DateTime.UtcNow;
        using var reader = simulation.ExecuteReader("select d, u, c, s, su, o, tomorrow from v");
        IsTrue(reader.Read());
        AreEqual("datetime", reader.GetDataTypeName(0));
        AreEqual("datetime", reader.GetDataTypeName(1));
        AreEqual("datetime", reader.GetDataTypeName(2));
        AreEqual("datetime2", reader.GetDataTypeName(3));
        AreEqual("datetime2", reader.GetDataTypeName(4));
        AreEqual("datetimeoffset", reader.GetDataTypeName(5));
        AreEqual("datetime", reader.GetDataTypeName(6));

        var legacyExpected = RoundToLegacyTick(reader.GetDateTime(3));
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(0).Ticks);
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(1).Ticks);
        AreEqual(legacyExpected.Ticks, reader.GetDateTime(2).Ticks);
        AreEqual(reader.GetDateTime(3).Ticks, reader.GetDateTime(4).Ticks);
        AreEqual(TimeSpan.Zero, reader.GetFieldValue<DateTimeOffset>(5).Offset);
        AreEqual(legacyExpected.AddDays(1).Ticks, reader.GetDateTime(6).Ticks);
        AssertRecent(before, reader.GetDateTime(3));
    }

    // Real inlines both into the referencing statement's plan, so their
    // current-time calls read that statement's freeze (probe-confirmed: a view
    // read once per row across a 300,000-row scan yields one constant value,
    // equal to the referencing statement's own SYSDATETIME()).
    [TestMethod]
    public void View_CurrentTime_SharesReferencingStatementFreeze()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create view v as select sysdatetime() as d");
        AreEqual(1, simulation.ExecuteScalar("select case when sysdatetime() = (select d from v) then 1 else 0 end"));
    }

    [TestMethod]
    public void InlineTvf_CurrentTime_IsConstantAcrossPerRowApply()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (id int)",
            "create function dbo.m() returns table as return (select sysdatetime() as d)",
            "insert t values (1), (2), (3)");
        AreEqual(1, simulation.ExecuteScalar("select count(distinct f.d) from t cross apply dbo.m() f"));
        AreEqual(1, simulation.ExecuteScalar("select case when sysdatetime() = (select d from dbo.m()) then 1 else 0 end"));
    }

    // The freeze is the referencing statement's, not a value baked into the
    // view at CREATE: a later statement reads a later instant.
    [TestMethod]
    public void View_CurrentTime_AdvancesAcrossStatements()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create view v as select sysdatetime() as d");
        var first = simulation.ExecuteScalar<DateTime>("select d from v");
        Thread.Sleep(2);
        IsGreaterThan(first, simulation.ExecuteScalar<DateTime>("select d from v"));
    }

    private static void AssertRecent(DateTime before, DateTime actual)
        => IsTrue(actual >= before.AddSeconds(-1) && actual <= DateTime.UtcNow.AddSeconds(1),
            $"{actual:o} not within +/-1s of [{before:o}, {DateTime.UtcNow:o}]");

    private static string DescribeFirstColumnTypeName(string commandText)
    {
        using var reader = new Simulation().ExecuteReader(commandText);
        IsTrue(reader.Read());
        return reader.GetDataTypeName(0);
    }

    private static DateTime RoundToLegacyTick(DateTime utcDt)
    {
        // datetime granularity is 1/300 second; round half-up.
        var dayCount = (int)(utcDt.Date - new DateTime(1900, 1, 1)).TotalDays;
        var timeUnits = ((utcDt.TimeOfDay.Ticks * 300) + (TimeSpan.TicksPerSecond / 2)) / TimeSpan.TicksPerSecond;
        if (timeUnits == 25920000)
        {
            dayCount++;
            timeUnits = 0;
        }
        var roundedTicks = timeUnits * TimeSpan.TicksPerSecond / 300;
        // SqlClient rounds the stored 1/300 tick to whole milliseconds on retrieval.
        var clientTicks = (roundedTicks + (TimeSpan.TicksPerMillisecond / 2)) / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond;
        return new DateTime(1900, 1, 1).AddDays(dayCount).AddTicks(clientTicks);
    }
}
