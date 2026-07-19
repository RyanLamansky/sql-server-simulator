using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

[TestClass]
public class EFCoreBasics
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void InsertRowSync()
    {
        using var context = new TestDbContext(1);
    }

    /// <summary>
    /// Same as <see cref="InsertRowSync"/> except using async logic.
    /// The simulator is 100% sync so this really just ensures the bult-in default async-over-sync wrapper works.
    /// </summary>
    [TestMethod]
    public async Task InsertRowAsync()
    {
        await using var context = new TestDbContext();

        var row = new TestRow { Id = 1 };

        _ = context.Rows.Add(row);

        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);
    }

    [TestMethod]
    public void RoundTrip()
    {
        const int storedValue = 3;
        using var context = new TestDbContext(storedValue);
        var receivedValue = context.Rows.Select(x => x.Id).AsEnumerable();

        Assert.AreEqual(storedValue, receivedValue.FirstOrDefault());
    }

    [TestMethod]
    public void MultiRowInsert()
    {
        int[] storedValues = [2, 3];
        using var context = new TestDbContext(storedValues);
        CollectionAssert.AreEquivalent(storedValues, context.Rows.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public void FirstOrDefault()
    {
        int[] storedValues = [4, 5];
        using var context = new TestDbContext(storedValues);
        var receivedValue = context.Rows.Select(x => x.Id);
        // Without an OrderBy, we can't guarantee which of the two possibilities is returned.
        CollectionAssert.Contains(storedValues, receivedValue.FirstOrDefault());
    }

    [TestMethod]
    public void SingleOrDefault()
    {
        const int storedValue = 6;
        using var context = new TestDbContext(storedValue);
        var receivedValue = context.Rows.Select(x => x.Id);
        Assert.AreEqual(storedValue, receivedValue.SingleOrDefault());
    }

    [TestMethod]
    public void Single_WithWhereMatchingOneRow_Returns()
    {
        using var context = new TestDbContext(1, 2, 3);
        var id = context.Rows.Where(r => r.Id == 2).Select(r => r.Id).Single();
        Assert.AreEqual(2, id);
    }

    [TestMethod]
    public void Single_WithPredicateMatchingOneRow_Returns()
    {
        using var context = new TestDbContext(1, 2, 3);
        var id = context.Rows.Select(r => r.Id).Single(x => x == 3);
        Assert.AreEqual(3, id);
    }

    [TestMethod]
    public void Single_WithWhereMatchingMultipleRows_Throws()
    {
        // EF Core asks the simulator for `SELECT TOP 2 ...` so it can detect
        // the cardinality violation client-side and raise InvalidOperationException
        // — this verifies our TOP and WHERE composition land that response.
        // Bypass the EF entity tracker (which would reject the duplicate Id
        // primary key) by inserting via raw SQL.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table Rows ( Id int )");
        _ = simulation.ExecuteNonQuery("insert Rows values (1),(2),(2),(3)");
        using var context = new TestDbContext(simulation);
        _ = Assert.Throws<InvalidOperationException>(() =>
            context.Rows.Where(r => r.Id == 2).Select(r => r.Id).Single());
    }

    [TestMethod]
    public void Single_WithWhereMatchingZeroRows_Throws()
    {
        using var context = new TestDbContext(1, 2, 3);
        _ = Assert.Throws<InvalidOperationException>(() =>
            context.Rows.Where(r => r.Id == 99).Select(r => r.Id).Single());
    }

    [TestMethod]
    public void SingleOrDefault_WithWhereMatchingZeroRows_ReturnsDefault()
    {
        using var context = new TestDbContext(1, 2, 3);
        var id = context.Rows.Where(r => r.Id == 99).Select(r => r.Id).SingleOrDefault();
        Assert.AreEqual(0, id);
    }

    [TestMethod]
    public void SingleOrDefault_WithWhereMatchingMultipleRows_Throws()
    {
        // See Single_WithWhereMatchingMultipleRows_Throws for the raw-SQL bypass rationale.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table Rows ( Id int )");
        _ = simulation.ExecuteNonQuery("insert Rows values (1),(2),(2),(3)");
        using var context = new TestDbContext(simulation);
        _ = Assert.Throws<InvalidOperationException>(() =>
            context.Rows.Where(r => r.Id == 2).Select(r => r.Id).SingleOrDefault());
    }

    [TestMethod]
    public void SingleOrDefault_WithPredicateMatchingZeroRows_ReturnsDefault()
    {
        using var context = new TestDbContext(1, 2, 3);
        var id = context.Rows.Select(r => r.Id).SingleOrDefault(x => x == 99);
        Assert.AreEqual(0, id);
    }

    [TestMethod]
    public async Task SingleAsync_WithWhereMatchingOneRow_Returns()
    {
        await using var context = new TestDbContext(1, 2, 3);
        var id = await context.Rows.Where(r => r.Id == 2).Select(r => r.Id).SingleAsync(this.TestContext.CancellationToken);
        Assert.AreEqual(2, id);
    }

    [TestMethod]
    public void Take()
    {
        int[] storedValues = [4, 5];
        using var context = new TestDbContext(storedValues);
        var receivedValue = context.Rows.Select(x => x.Id);
        // Without an OrderBy, we can't guarantee which of the two possibilities is returned.
        CollectionAssert.Contains(storedValues, receivedValue.Take(1).AsEnumerable().First());
    }
}
