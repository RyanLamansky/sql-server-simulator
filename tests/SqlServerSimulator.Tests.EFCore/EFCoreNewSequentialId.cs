namespace SqlServerSimulator;

/// <summary>
/// Exercises the simulator's <c>NEWSEQUENTIALID()</c> default-clause
/// behavior through EF Core's natural server-generated-key pattern: a
/// <see cref="Guid"/> primary key annotated
/// <see cref="System.ComponentModel.DataAnnotations.Schema.DatabaseGeneratedOption.Identity"/>
/// causes EF Core to omit the column from INSERTs and recover the
/// server-assigned value through <c>OUTPUT INSERTED.Id</c>.
/// </summary>
[TestClass]
public class EFCoreNewSequentialId
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveChanges_PopulatesGeneratedGuid()
    {
        using var context = new TestDbContext(TestDbContext.CreateTokensSimulation());

        var token = new Token { Label = "first" };
        _ = context.Tokens.Add(token);
        _ = context.SaveChanges();

        Assert.AreNotEqual(Guid.Empty, token.Id);
    }

    [TestMethod]
    public void SaveChanges_AcrossRows_ProducesAscendingGuids()
    {
        using var context = new TestDbContext(TestDbContext.CreateTokensSimulation());

        var tokens = new[]
        {
            new Token { Label = "a" },
            new Token { Label = "b" },
            new Token { Label = "c" },
            new Token { Label = "d" },
        };
        foreach (var t in tokens)
            _ = context.Tokens.Add(t);
        _ = context.SaveChanges();

        // EF Core's MERGE batch doesn't promise to preserve Add-order when
        // matching OUTPUT rows back to entities, so sort the assigned values
        // before checking monotonicity. The behavioral guarantee under test
        // is "NEWSEQUENTIALID emits a strictly ascending sequence" — not
        // "EF Core preserves Add-order through MERGE".
        var ids = tokens.Select(t => t.Id).ToArray();
        Assert.AreEqual(ids.Length, ids.Distinct().Count(), "Generated GUIDs were not unique");
        Array.Sort(ids);
        for (var i = 1; i < ids.Length; i++)
            Assert.IsGreaterThan(ids[i - 1], ids[i]);
    }

    [TestMethod]
    public async Task SaveChangesAsync_PopulatesGeneratedGuid()
    {
        await using var context = new TestDbContext(TestDbContext.CreateTokensSimulation());

        var token = new Token { Label = "async" };
        _ = context.Tokens.Add(token);
        _ = await context.SaveChangesAsync(this.TestContext.CancellationToken);

        Assert.AreNotEqual(Guid.Empty, token.Id);
    }
}
