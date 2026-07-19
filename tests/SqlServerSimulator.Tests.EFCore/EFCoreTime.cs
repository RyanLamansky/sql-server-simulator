namespace SqlServerSimulator;

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's <c>time(N)</c> column
/// mappings — both <see cref="TimeOnly"/> and <see cref="TimeSpan"/>. Both
/// CLR types route through <see cref="System.Data.DbType.Time"/>; the
/// substitute mappings inherit from
/// <see cref="Microsoft.EntityFrameworkCore.Storage.TimeOnlyTypeMapping"/> /
/// <see cref="Microsoft.EntityFrameworkCore.Storage.TimeSpanTypeMapping"/>
/// rather than the SqlServer provider's downcasting variants.
/// </summary>
[TestClass]
public class EFCoreTime
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new AdapterTestDbContext(new Simulation()));

    private static Schedule NewSchedule(int id, TimeOnly? alarm = null, TimeOnly? snooze = null, TimeSpan? shift = null, TimeSpan? @break = null) => new()
    {
        Id = id,
        Birthday = new DateOnly(1990, 7, 4),
        PlanStart = new DateTime(2026, 5, 4),
        CheckIn = new DateTime(2026, 5, 4, 12, 0, 0),
        DailyAlarm = alarm ?? new TimeOnly(6, 30),
        Snooze = snooze,
        ShiftLength = shift ?? TimeSpan.FromHours(8),
        Break = @break,
    };

    [TestMethod]
    public void Insert_TimeOnly_FullPrecisionRoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());

        var alarm = new TimeOnly(6, 30, 15).Add(TimeSpan.FromTicks(1234567));
        _ = context.Schedules.Add(NewSchedule(1, alarm: alarm));
        _ = context.SaveChanges();

        Assert.AreEqual(alarm, context.Schedules.Select(s => s.DailyAlarm).First());
    }

    [TestMethod]
    public async Task InsertAsync_TimeOnly_RoundTrips()
    {
        await using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());

        var alarm = new TimeOnly(7, 0);
        _ = context.Schedules.Add(NewSchedule(1, alarm: alarm));
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(alarm, context.Schedules.Select(s => s.DailyAlarm).First());
    }

    [TestMethod]
    public void Insert_NullableTimeOnly_AcceptsNullAndValue()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var snooze = new TimeOnly(0, 9, 0, 250);
        context.Schedules.AddRange(
            NewSchedule(1),
            NewSchedule(2, snooze: snooze));
        _ = context.SaveChanges();

        var rows = context.Schedules.OrderBy(s => s.Id).Select(s => s.Snooze).ToArray();
        Assert.IsNull(rows[0]);
        Assert.AreEqual(snooze, rows[1]);
    }

    [TestMethod]
    public void Insert_TimeOnly_LowerPrecisionColumn_RoundsHalfUp()
    {
        // Snooze is time(3); 0.5ms above a millisecond boundary rounds up.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var snooze = new TimeOnly(0, 9, 0, 100).Add(TimeSpan.FromTicks(5_000));
        _ = context.Schedules.Add(NewSchedule(1, snooze: snooze));
        _ = context.SaveChanges();

        Assert.AreEqual(new TimeOnly(0, 9, 0, 101), context.Schedules.Select(s => s.Snooze).First());
    }

    [TestMethod]
    public void Insert_TimeSpan_FullPrecisionRoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());

        var shift = new TimeSpan(0, 8, 30, 0, 0).Add(TimeSpan.FromTicks(1234567));
        _ = context.Schedules.Add(NewSchedule(1, shift: shift));
        _ = context.SaveChanges();

        Assert.AreEqual(shift, context.Schedules.Select(s => s.ShiftLength).First());
    }

    [TestMethod]
    public void Insert_NullableTimeSpan_AcceptsNullAndValue()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var lunch = TimeSpan.FromMinutes(30);
        context.Schedules.AddRange(
            NewSchedule(1),
            NewSchedule(2, @break: lunch));
        _ = context.SaveChanges();

        var rows = context.Schedules.OrderBy(s => s.Id).Select(s => s.Break).ToArray();
        Assert.IsNull(rows[0]);
        Assert.AreEqual(lunch, rows[1]);
    }

    [TestMethod]
    public void Insert_TimeSpan_Time0Column_TruncatesSubSecond()
    {
        // Break is time(0); sub-second precision drops at encode time.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var raw = new TimeSpan(0, 0, 30, 45, 750);
        _ = context.Schedules.Add(NewSchedule(1, @break: raw));
        _ = context.SaveChanges();

        // Note: time(0) rounds half-up, so 30.75s → 31s.
        Assert.AreEqual(new TimeSpan(0, 0, 30, 46), context.Schedules.Select(s => s.Break).First());
    }

    [TestMethod]
    public void Where_FiltersByTimeOnlyEquality()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var target = new TimeOnly(7, 15);
        context.Schedules.AddRange(
            NewSchedule(1, alarm: new TimeOnly(6, 0)),
            NewSchedule(2, alarm: target),
            NewSchedule(3, alarm: new TimeOnly(8, 30)));
        _ = context.SaveChanges();

        var match = context.Schedules.Where(s => s.DailyAlarm == target).Select(s => s.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void Insert_TimeOnly_Midnight_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(NewSchedule(1, alarm: TimeOnly.MinValue));
        _ = context.SaveChanges();

        Assert.AreEqual(TimeOnly.MinValue, context.Schedules.Select(s => s.DailyAlarm).First());
    }

    [TestMethod]
    public void Insert_TimeSpan_NearMaxTimeOfDay_RoundTrips()
    {
        // 23:59:59.9999999 is the max representable in time(7).
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var max = new TimeSpan(23, 59, 59).Add(TimeSpan.FromTicks(9_999_999));
        _ = context.Schedules.Add(NewSchedule(1, shift: max));
        _ = context.SaveChanges();

        Assert.AreEqual(max, context.Schedules.Select(s => s.ShiftLength).First());
    }
}
