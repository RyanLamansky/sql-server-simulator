namespace SqlServerSimulator;

/// <summary>
/// Exercises <c>datetime2(N)</c> and <c>datetimeoffset(N)</c> column support
/// through EF Core. Confirms full-precision (precision 7) round trips,
/// lower-precision rounding-on-store, datetimeoffset offset preservation,
/// equality-by-UTC-instant, and legacy datetime tick-quantization round-trip.
/// </summary>
[TestClass]
public class EFCoreDateTime
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Insert_DateTime_FullPrecisionRoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var dt = new DateTime(2026, 5, 4, 13, 45, 30).AddTicks(1234567);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = dt });
        _ = context.SaveChanges();

        Assert.AreEqual(dt, context.Events.Select(e => e.CreatedAt).First());
    }

    [TestMethod]
    public async Task InsertAsync_DateTime_RoundTrips()
    {
        await using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var dt = new DateTime(2026, 5, 4, 13, 45, 30);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = dt });
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreEqual(dt, context.Events.Select(e => e.CreatedAt).First());
    }

    [TestMethod]
    public void Insert_NullableDateTime_AcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation()).WithSaved(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4) });

        Assert.IsNull(context.Events.Select(e => e.Updated).First());
    }

    [TestMethod]
    public void Insert_NullableDateTime_AcceptsValue()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var updated = new DateTime(2026, 5, 4, 14, 0, 0, 250);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Updated = updated });
        _ = context.SaveChanges();

        Assert.AreEqual(updated, context.Events.Select(e => e.Updated).First());
    }

    [TestMethod]
    public void Insert_LowerPrecisionColumn_RoundsHalfUp()
    {
        // Updated is datetime2(3); 0.5ms above ms boundary rounds to next ms.
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var updated = new DateTime(2026, 5, 4, 13, 45, 30, 100).AddTicks(5_000);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Updated = updated });
        _ = context.SaveChanges();

        Assert.AreEqual(new DateTime(2026, 5, 4, 13, 45, 30, 101), context.Events.Select(e => e.Updated).First());
    }

    [TestMethod]
    public void Where_FiltersByDateTimeEquality()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var target = new DateTime(2026, 5, 4, 13, 45, 30);
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4, 12, 0, 0) },
            new Event { Id = 2, CreatedAt = target },
            new Event { Id = 3, CreatedAt = new DateTime(2026, 5, 4, 15, 0, 0) });
        _ = context.SaveChanges();

        Assert.AreEqual(2, context.Events.Where(e => e.CreatedAt == target).Select(e => e.Id).Single());
    }

    [TestMethod]
    public void MultipleRows_RoundTripBothColumns()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var a = new DateTime(2026, 5, 4, 13, 45, 30);
        var b = new DateTime(2026, 5, 4, 14, 0, 0);
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = a, Updated = b },
            new Event { Id = 2, CreatedAt = b, Updated = null });
        _ = context.SaveChanges();

        var rows = context.Events.OrderBy(e => e.Id).Select(e => new { e.CreatedAt, e.Updated }).ToArray();
        Assert.HasCount(2, rows);
        Assert.AreEqual(a, rows[0].CreatedAt);
        Assert.AreEqual(b, rows[0].Updated);
        Assert.AreEqual(b, rows[1].CreatedAt);
        Assert.IsNull(rows[1].Updated);
    }

    [TestMethod]
    public void Insert_DateTimeOffset_FullPrecisionAndOffsetRoundTrip()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var dto = new DateTimeOffset(2026, 5, 4, 13, 45, 30, TimeSpan.FromHours(-7)).AddTicks(1234567);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = dto });
        _ = context.SaveChanges();

        var read = context.Events.Select(e => e.OccurredAt).First();
        Assert.AreEqual(dto, read);
        Assert.AreEqual(TimeSpan.FromHours(-7), read.Offset);
    }

    [TestMethod]
    public void Insert_NullableDateTimeOffset_AcceptsNullAndValue()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var cancelled = new DateTimeOffset(2026, 5, 4, 14, 0, 0, TimeSpan.FromHours(2));
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = DateTimeOffset.UnixEpoch },
            new Event { Id = 2, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = DateTimeOffset.UnixEpoch, Cancelled = cancelled });
        _ = context.SaveChanges();

        var rows = context.Events.OrderBy(e => e.Id).Select(e => e.Cancelled).ToArray();
        Assert.IsNull(rows[0]);
        Assert.AreEqual(cancelled, rows[1]);
    }

    [TestMethod]
    public void Insert_DateTimeOffset_LowerPrecisionColumn_RoundsHalfUp()
    {
        // Cancelled is datetimeoffset(3); 0.5ms above ms boundary rounds to next ms.
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var cancelled = new DateTimeOffset(2026, 5, 4, 13, 45, 30, 100, TimeSpan.FromHours(-7)).AddTicks(5_000);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = DateTimeOffset.UnixEpoch, Cancelled = cancelled });
        _ = context.SaveChanges();

        var expected = new DateTimeOffset(2026, 5, 4, 13, 45, 30, 101, TimeSpan.FromHours(-7));
        Assert.AreEqual(expected, context.Events.Select(e => e.Cancelled).First());
    }

    [TestMethod]
    public void Where_FiltersByDateTimeOffsetEquality_CrossOffset()
    {
        // Stored value and parameter share UTC instant but carry different offsets; equality should match.
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var east = new DateTimeOffset(2026, 5, 4, 20, 45, 30, TimeSpan.FromHours(7));
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = east },
            new Event { Id = 2, CreatedAt = new DateTime(2026, 5, 4), OccurredAt = new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero) });
        _ = context.SaveChanges();

        var west = new DateTimeOffset(2026, 5, 4, 6, 45, 30, TimeSpan.FromHours(-7));
        Assert.AreEqual(1, context.Events.Where(e => e.OccurredAt == west).Select(e => e.Id).Single());
    }

    [TestMethod]
    public void Insert_LegacyDateTime_RoundTripsAtTickGranularity()
    {
        // Legacy datetime stores 1/300-second ticks (.997 input → tick 299, the
        // raw .9966666); SqlClient rounds that to whole ms on read, so EF surfaces
        // .997 — a lossless round-trip for a millisecond-precision input.
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());

        var started = new DateTime(2026, 5, 4, 13, 45, 30, 997);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Started = started });
        _ = context.SaveChanges();

        var read = context.Events.Select(e => e.Started).First();
        Assert.IsNotNull(read);
        Assert.AreEqual(started.Date, read.Value.Date);
        Assert.AreEqual(started.Hour, read.Value.Hour);
        Assert.AreEqual(started.Minute, read.Value.Minute);
        Assert.AreEqual(started.Second, read.Value.Second);
        Assert.AreEqual(9_970_000, read.Value.Ticks % TimeSpan.TicksPerSecond);
    }

    [TestMethod]
    public void Insert_LegacyDateTime_999msRollsToNextSecond()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation()).WithSaved(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Started = new DateTime(2026, 5, 4, 13, 45, 30, 999) });

        Assert.AreEqual(new DateTime(2026, 5, 4, 13, 45, 31), context.Events.Select(e => e.Started).First());
    }

    [TestMethod]
    public void Insert_NullableLegacyDateTime_AcceptsNull()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation()).WithSaved(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4) });

        Assert.IsNull(context.Events.Select(e => e.Started).First());
        Assert.IsNull(context.Events.Select(e => e.Ended).First());
    }

    [TestMethod]
    public void Where_FiltersByLegacyDateTimeEquality()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var target = new DateTime(2026, 5, 4, 13, 45, 30);
        context.Events.AddRange(
            new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Started = new DateTime(2026, 5, 4, 12, 0, 0) },
            new Event { Id = 2, CreatedAt = new DateTime(2026, 5, 4), Started = target },
            new Event { Id = 3, CreatedAt = new DateTime(2026, 5, 4), Started = new DateTime(2026, 5, 4, 15, 0, 0) });
        _ = context.SaveChanges();

        Assert.AreEqual(2, context.Events.Where(e => e.Started == target).Select(e => e.Id).Single());
    }

    [TestMethod]
    public void Insert_LegacyDateTime_AtMin_RoundTrips()
    {
        using var context = new TestDbContext(TestDbContext.CreateEventsSimulation());
        var min = new DateTime(1753, 1, 1);
        _ = context.Events.Add(new Event { Id = 1, CreatedAt = new DateTime(2026, 5, 4), Started = min });
        _ = context.SaveChanges();

        Assert.AreEqual(min, context.Events.Select(e => e.Started).First());
    }

    [TestMethod]
    public void Where_DateTimeYearExtraction_TranslatesToDatepart()
    {
        // EF Core translates .Year to DATEPART(year, col).
        var simulation = TestDbContext.CreateEventsSimulation();
        using (var seed = new TestDbContext(simulation))
        {
            seed.Events.AddRange(
                new Event { Id = 1, CreatedAt = new DateTime(2023, 6, 1), OccurredAt = DateTimeOffset.MinValue },
                new Event { Id = 2, CreatedAt = new DateTime(2024, 6, 1), OccurredAt = DateTimeOffset.MinValue },
                new Event { Id = 3, CreatedAt = new DateTime(2024, 12, 1), OccurredAt = DateTimeOffset.MinValue });
            _ = seed.SaveChanges();
        }

        using var context = new TestDbContext(simulation);
        var ids = context.Events.Where(e => e.CreatedAt.Year == 2024).OrderBy(e => e.Id).Select(e => e.Id).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 3 }, ids);
    }

    [TestMethod]
    public void Projection_DateTimeAddDays_TranslatesToDateadd()
    {
        var simulation = TestDbContext.CreateEventsSimulation();
        using (var seed = new TestDbContext(simulation))
        {
            _ = seed.Events.Add(new Event
            {
                Id = 1,
                CreatedAt = new DateTime(2024, 6, 1),
                OccurredAt = DateTimeOffset.MinValue,
            });
            _ = seed.SaveChanges();
        }

        using var context = new TestDbContext(simulation);
        Assert.AreEqual(new DateTime(2024, 6, 8), context.Events.Select(e => e.CreatedAt.AddDays(7)).Single());
    }
}
