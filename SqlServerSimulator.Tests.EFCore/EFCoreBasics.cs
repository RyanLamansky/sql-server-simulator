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
        const int storedValue = 6; // Until `Where` is supported, this won't pass if multiple rows exist.
        using var context = new TestDbContext(storedValue);
        var receivedValue = context.Rows.Select(x => x.Id);
        Assert.AreEqual(storedValue, receivedValue.SingleOrDefault());
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
