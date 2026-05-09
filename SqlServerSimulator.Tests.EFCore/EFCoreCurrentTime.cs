using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for the current-time scalar functions through EF Core 10:
/// <c>DateTime.UtcNow</c> in a server-side WHERE predicate emits
/// <c>GETUTCDATE()</c>, and a <c>HasDefaultValueSql("getutcdate()")</c>-bound
/// column omitted from the INSERT round-trips a freshly-stamped value back
/// via <c>OUTPUT INSERTED.[CreatedAt]</c>. Pre-bundle, both paths failed at
/// parse with "GETUTCDATE not a recognized built-in function".
/// </summary>
[TestClass]
public class EFCoreCurrentTime
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void DateTimeUtcNow_InWhereClause_FiltersAgainstServerStamp()
    {
        // EF Core 10 translates `DateTime.UtcNow` inside a server-side query
        // expression to `GETUTCDATE()` rather than capturing the client-side
        // value as a parameter — confirms the simulator handles the emitted
        // function call.
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2020, 1, 1) },
            new Event { Id = 2, CreatedAt = new DateTime(2099, 12, 31) });
        _ = context.SaveChanges();

        var futureCount = context.Events.Count(e => e.CreatedAt > DateTime.UtcNow);
        var pastCount = context.Events.Count(e => e.CreatedAt < DateTime.UtcNow);
        Assert.AreEqual(1, futureCount);
        Assert.AreEqual(1, pastCount);
    }

    [TestMethod]
    public void HasDefaultValueSql_GetUtcDate_StampsOnSaveChanges()
    {
        // Heartbeat.CreatedAt is bound to HasDefaultValueSql("getutcdate()");
        // EF Core 10 omits the column from the INSERT and reads the
        // simulator-generated value back via OUTPUT INSERTED.[CreatedAt].
        var before = DateTime.UtcNow;
        using var context = new TestDbContext(TestDbContext.CreateHeartbeatsSimulation());
        var beat = new Heartbeat { Note = "first" };
        _ = context.Heartbeats.Add(beat);
        _ = context.SaveChanges();
        var after = DateTime.UtcNow;

        Assert.IsGreaterThanOrEqualTo(before.AddSeconds(-1), beat.CreatedAt);
        Assert.IsLessThanOrEqualTo(after.AddSeconds(1), beat.CreatedAt);

        // Confirm round-trip — the value EF wrote into the entity matches what's persisted.
        var persisted = context.Heartbeats.AsNoTracking().Single().CreatedAt;
        Assert.AreEqual(beat.CreatedAt, persisted);
    }

    [TestMethod]
    public void HasDefaultValueSql_GetUtcDate_AcrossMultipleSaveChanges_ProducesDistinctStamps()
    {
        using var context = new TestDbContext(TestDbContext.CreateHeartbeatsSimulation());

        var stamps = new List<DateTime>();
        for (var i = 0; i < 4; i++)
        {
            if (i > 0) Thread.Sleep(2);
            var beat = new Heartbeat { Note = $"beat-{i}" };
            _ = context.Heartbeats.Add(beat);
            _ = context.SaveChanges();
            stamps.Add(beat.CreatedAt);
        }

        Assert.HasCount(4, stamps);
        Assert.IsGreaterThan(stamps[0], stamps[^1]);
        for (var i = 1; i < stamps.Count; i++)
            Assert.IsGreaterThanOrEqualTo(stamps[i - 1], stamps[i]);
    }
}
