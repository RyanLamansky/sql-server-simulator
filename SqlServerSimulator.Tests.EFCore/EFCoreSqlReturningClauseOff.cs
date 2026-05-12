using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Exercises EF Core's <c>UseSqlOutputClause(false)</c> path, which apps
/// typically opt into when the target table has an <c>INSTEAD OF</c> trigger
/// (the simulator doesn't model triggers, but the opt-out is a legitimate
/// configuration). With the SQL OUTPUT clause disabled for an entity, EF
/// Core emits a table-variable-based workaround pattern instead of the bare
/// <c>OUTPUT INSERTED.col</c> form — staging the returned identity through
/// a <c>DECLARE @inserted TABLE (...)</c> + <c>OUTPUT INSERTED.col INTO @inserted</c>
/// + <c>SELECT … FROM @inserted</c> sequence. This regression test locks in
/// the simulator's coverage of that path now that <c>DECLARE @t TABLE</c>
/// and <c>OUTPUT … INTO @t</c> both ship.
/// </summary>
[TestClass]
public class EFCoreSqlReturningClauseOff
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SaveChanges_SingleEntity_RehydratesIdentityViaTableVariable()
    {
        using var context = new OutputOffDbContext(TestDbContext.CreateWidgetsSimulation());

        var widget = new Widget { Name = "First" };
        _ = context.Widgets.Add(widget);
        _ = context.SaveChanges();

        Assert.AreEqual(1, widget.Id);
        Assert.AreEqual("First", context.Widgets.Where(w => w.Id == 1).Select(w => w.Name).Single());
    }

    [TestMethod]
    public void SaveChanges_MultipleEntities_RehydratesContiguousIdentities()
    {
        using var context = new OutputOffDbContext(TestDbContext.CreateWidgetsSimulation());

        var a = new Widget { Name = "A" };
        var b = new Widget { Name = "B" };
        var c = new Widget { Name = "C" };
        context.Widgets.AddRange(a, b, c);
        _ = context.SaveChanges();

        Assert.AreEqual(1, a.Id);
        Assert.AreEqual(2, b.Id);
        Assert.AreEqual(3, c.Id);
    }

    /// <summary>
    /// Variant of <see cref="TestDbContext"/> that disables the SQL OUTPUT
    /// clause for the <see cref="Widget"/> entity via
    /// <c>ToTable(t =&gt; t.UseSqlOutputClause(false))</c>. EF Core then emits
    /// the table-variable-based workaround pattern for SaveChanges (the path
    /// apps with INSTEAD OF triggers configure into; the simulator doesn't
    /// model triggers but the configuration is still legitimate). Stays on
    /// the bare <c>UseSqlServer</c> code path (no adapter needed for
    /// <see cref="Widget"/>'s int identity column).
    /// </summary>
    private sealed class OutputOffDbContext(Simulation simulation) : TestDbContext(simulation)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            _ = modelBuilder.Entity<Widget>()
                .ToTable("Widgets", t => t.UseSqlOutputClause(false));
        }
    }
}
