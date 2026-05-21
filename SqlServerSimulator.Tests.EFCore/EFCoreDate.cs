namespace SqlServerSimulator;

/// <summary>
/// Exercises the SqlServerSimulator EF Core adapter's <c>date</c> column
/// mappings — both <see cref="DateOnly"/> and <see cref="DateTime"/>. Each
/// pair would throw <c>InvalidCastException</c> at SaveChanges under
/// vanilla <c>UseSqlServer</c> because <c>SqlServerDateOnlyTypeMapping</c>
/// and <c>SqlServerDateTimeTypeMapping</c> downcast the parameter to
/// <c>SqlParameter</c> to force <c>SqlDbType.Date</c>.
/// </summary>
[TestClass]
public class EFCoreDate
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new AdapterTestDbContext(new Simulation()));

    [TestMethod]
    public void Insert_DateOnly_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());

        var birthday = new DateOnly(1990, 7, 4);
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = birthday, PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = context.SaveChanges();

        Assert.AreEqual(birthday, context.Schedules.Select(s => s.Birthday).First());
    }

    [TestMethod]
    public async Task InsertAsync_DateOnly_RoundTrips()
    {
        await using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());

        var birthday = new DateOnly(2000, 1, 1);
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = birthday, PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(birthday, context.Schedules.Select(s => s.Birthday).First());
    }

    [TestMethod]
    public void Insert_NullableDateOnly_AcceptsNull()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = new DateOnly(1990, 7, 4), PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = context.SaveChanges();

        Assert.IsNull(context.Schedules.Select(s => s.Anniversary).First());
    }

    [TestMethod]
    public void Insert_NullableDateOnly_AcceptsValue()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var anniversary = new DateOnly(2015, 6, 12);
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = new DateOnly(1990, 7, 4), Anniversary = anniversary, PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = context.SaveChanges();

        Assert.AreEqual(anniversary, context.Schedules.Select(s => s.Anniversary).First());
    }

    [TestMethod]
    public void Insert_DateTimeIntoDate_TruncatesTimePortion()
    {
        // PlanStart is mapped as `date`, so any time-of-day in the source
        // DateTime drops at column-encode time.
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = new DateOnly(1990, 7, 4), PlanStart = new DateTime(2026, 5, 4, 13, 45, 30), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = context.SaveChanges();

        Assert.AreEqual(new DateTime(2026, 5, 4), context.Schedules.Select(s => s.PlanStart).First());
    }

    [TestMethod]
    public void Where_FiltersByDateOnlyEquality()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        var target = new DateOnly(1985, 3, 14);
        var filler = new Schedule { Id = 0, Birthday = new DateOnly(2000, 1, 1), PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) };
        context.Schedules.AddRange(
            new Schedule { Id = 1, Birthday = new DateOnly(1990, 7, 4), PlanStart = filler.PlanStart, CheckIn = filler.CheckIn, DailyAlarm = filler.DailyAlarm, ShiftLength = filler.ShiftLength },
            new Schedule { Id = 2, Birthday = target, PlanStart = filler.PlanStart, CheckIn = filler.CheckIn, DailyAlarm = filler.DailyAlarm, ShiftLength = filler.ShiftLength },
            new Schedule { Id = 3, Birthday = new DateOnly(1995, 12, 31), PlanStart = filler.PlanStart, CheckIn = filler.CheckIn, DailyAlarm = filler.DailyAlarm, ShiftLength = filler.ShiftLength });
        _ = context.SaveChanges();

        var match = context.Schedules.Where(s => s.Birthday == target).Select(s => s.Id).Single();
        Assert.AreEqual(2, match);
    }

    [TestMethod]
    public void Insert_DateOnly_AtMin_RoundTrips()
    {
        using var context = new AdapterTestDbContext(AdapterTestDbContext.CreateSchedulesSimulation());
        _ = context.Schedules.Add(new Schedule { Id = 1, Birthday = DateOnly.MinValue, PlanStart = new DateTime(2026, 5, 4), CheckIn = new DateTime(2026, 5, 4, 12, 0, 0), DailyAlarm = new TimeOnly(6, 30), ShiftLength = TimeSpan.FromHours(8) });
        _ = context.SaveChanges();

        Assert.AreEqual(DateOnly.MinValue, context.Schedules.Select(s => s.Birthday).First());
    }
}
