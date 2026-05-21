namespace SqlServerSimulator;

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's
/// <c>DateTime → smalldatetime</c> mapping. Without the adapter,
/// <c>SqlServerDateTimeTypeMapping</c> downcasts the parameter to
/// <c>SqlParameter</c> to set <c>SqlDbType.SmallDateTime</c>; the
/// substitute mapping inherits from
/// <see cref="Microsoft.EntityFrameworkCore.Storage.DateTimeTypeMapping"/>
/// and routes through <see cref="System.Data.DbType.DateTime"/> instead.
/// </summary>
[TestClass]
public class EFCoreSmallDateTime
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new AdapterTestDbContext(new Simulation()));

    private static Schedule NewSchedule(int id, DateTime? checkIn = null) => new()
    {
        Id = id,
        Birthday = new DateOnly(1990, 7, 4),
        PlanStart = new DateTime(2026, 5, 4),
        CheckIn = checkIn ?? new DateTime(2026, 5, 4, 12, 0, 0),
        DailyAlarm = new TimeOnly(6, 30),
        ShiftLength = TimeSpan.FromHours(8),
    };

    [TestMethod]
    public void Insert_SmallDateTime_RoundsToNearestMinute()
    {
        // smalldatetime stores at minute granularity; 30 seconds rounds up.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(NewSchedule(1, checkIn: new DateTime(2026, 5, 4, 12, 0, 30)));
        _ = context.SaveChanges();

        Assert.AreEqual(new DateTime(2026, 5, 4, 12, 1, 0), context.Schedules.Select(s => s.CheckIn).First());
    }

    [TestMethod]
    public void Insert_SmallDateTime_TruncatesSubMinute()
    {
        // SQL Server smalldatetime quantizes to legacy 1/300s ticks before
        // applying half-up minute rounding, so values clearly under 29.5s
        // round down. (29.999s would round up because the legacy tick
        // quantization itself rolls up to 30s first.)
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(NewSchedule(1, checkIn: new DateTime(2026, 5, 4, 12, 0, 29, 0)));
        _ = context.SaveChanges();

        Assert.AreEqual(new DateTime(2026, 5, 4, 12, 0, 0), context.Schedules.Select(s => s.CheckIn).First());
    }

    [TestMethod]
    public async Task InsertAsync_SmallDateTime_RoundTrips()
    {
        await using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var checkIn = new DateTime(2026, 5, 4, 9, 15, 0);
        _ = context.Schedules.Add(NewSchedule(1, checkIn: checkIn));
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(checkIn, context.Schedules.Select(s => s.CheckIn).First());
    }

    [TestMethod]
    public void Where_FiltersBySmallDateTimeEquality()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var target = new DateTime(2026, 5, 4, 9, 30, 0);
        context.Schedules.AddRange(
            NewSchedule(1, checkIn: new DateTime(2026, 5, 4, 8, 0, 0)),
            NewSchedule(2, checkIn: target),
            NewSchedule(3, checkIn: new DateTime(2026, 5, 4, 10, 0, 0)));
        _ = context.SaveChanges();

        var match = context.Schedules.Where(s => s.CheckIn == target).Select(s => s.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void Insert_SmallDateTime_AtBaseDate_RoundTrips()
    {
        // smalldatetime epoch is 1900-01-01.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var epoch = new DateTime(1900, 1, 1);
        _ = context.Schedules.Add(NewSchedule(1, checkIn: epoch));
        _ = context.SaveChanges();

        Assert.AreEqual(epoch, context.Schedules.Select(s => s.CheckIn).First());
    }
}
