using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// Row-level security under EF Core: the database applies a security policy
/// whose predicates read the tenant each connection set in its
/// <c>SESSION_CONTEXT</c>, so the same LINQ query — one SQL text, one cached
/// plan — reads, updates and inserts only its own tenant's rows from each
/// context, and a write the block predicate refuses surfaces as a
/// <see cref="DbUpdateException"/> over Msg 33504.
/// </summary>
[TestClass]
public class EFCoreRowLevelSecurity
{
    public TestContext TestContext { get; set; } = null!;

    [ClassInitialize]
    public static void WarmModel(TestContext _) => AssemblyHooks.WarmModel(() => new TenantContext(new Simulation(), 0));

    private sealed class Invoice
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        [Column(TypeName = "nvarchar(20)")]
        public string Customer { get; set; } = "";

        public int Amount { get; set; }
    }

    /// <summary>A context whose connection carries <c>tenant</c> in its session context.</summary>
    private sealed class TenantContext(Simulation simulation, int tenant) : DbContext
    {
        public DbSet<Invoice> Invoices => Set<Invoice>();

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            var connection = simulation.CreateDbConnection();
            connection.Open();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "exec sp_set_session_context N'tenant', @tenant";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@tenant";
                parameter.Value = tenant;
                _ = command.Parameters.Add(parameter);
                _ = command.ExecuteNonQuery();
            }
            _ = optionsBuilder.UseSqlServer(connection);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            _ = modelBuilder.Entity<Invoice>().Property(i => i.Id).ValueGeneratedNever();
    }

    private static Simulation CreateSimulation()
    {
        var simulation = new Simulation();
        using var connection = simulation.CreateOpenConnection();
        foreach (var batch in (string[])[
            """
            create table Invoices (Id int not null primary key, TenantId int not null, Customer nvarchar(20) not null, Amount int not null);
            insert Invoices values (1, 1, N'ann', 10), (2, 2, N'bob', 20), (3, 1, N'cat', 30), (4, 2, N'dan', 40);
            """,
            "create function dbo.tenant_rows(@tenant int) returns table with schemabinding as return select 1 as ok where @tenant = cast(session_context(N'tenant') as int);",
            "create security policy dbo.tenancy add filter predicate dbo.tenant_rows(TenantId) on dbo.Invoices, add block predicate dbo.tenant_rows(TenantId) on dbo.Invoices after insert, add block predicate dbo.tenant_rows(TenantId) on dbo.Invoices after update;",
        ])
        {
            using var command = connection.CreateCommand(batch);
            _ = command.ExecuteNonQuery();
        }
        return simulation;
    }

    [TestMethod]
    public void Query_EachTenantReadsItsOwnRowsThroughOnePlan()
    {
        var simulation = CreateSimulation();
        using var first = new TenantContext(simulation, 1);
        using var second = new TenantContext(simulation, 2);

        CollectionAssert.AreEqual(new[] { "ann", "cat" }, first.Invoices.Where(i => i.Amount > 5).OrderBy(i => i.Id).Select(i => i.Customer).ToArray());
        CollectionAssert.AreEqual(new[] { "bob", "dan" }, second.Invoices.Where(i => i.Amount > 5).OrderBy(i => i.Id).Select(i => i.Customer).ToArray());
        Assert.AreEqual(40, first.Invoices.Sum(i => i.Amount));
        Assert.IsNull(first.Invoices.FirstOrDefault(i => i.Id == 2));
    }

    [TestMethod]
    public void ExecuteUpdate_ReachesOnlyTheTenantsRows()
    {
        var simulation = CreateSimulation();
        using (var first = new TenantContext(simulation, 1))
            Assert.AreEqual(2, first.Invoices.ExecuteUpdate(setters => setters.SetProperty(i => i.Amount, i => i.Amount + 1)));

        using var second = new TenantContext(simulation, 2);
        CollectionAssert.AreEqual(new[] { 20, 40 }, second.Invoices.OrderBy(i => i.Id).Select(i => i.Amount).ToArray());
    }

    [TestMethod]
    public void SaveChanges_ForeignTenantRowRefusedByTheBlockPredicate()
    {
        var simulation = CreateSimulation();
        using var first = new TenantContext(simulation, 1);
        _ = first.Invoices.Add(new Invoice { Id = 5, TenantId = 1, Customer = "eve", Amount = 50 });
        _ = first.SaveChanges();
        _ = first.Invoices.Add(new Invoice { Id = 6, TenantId = 2, Customer = "fay", Amount = 60 });

        var error = Assert.Throws<DbUpdateException>(() => first.SaveChanges());
        Assert.AreEqual(33504, Assert.IsInstanceOfType<SimulatedSqlException>(error.InnerException).Number);
        Assert.AreEqual(3, first.Invoices.AsNoTracking().Count());
    }

    [TestMethod]
    public async Task SaveChangesAsync_MovingARowToAnotherTenantRefused()
    {
        var simulation = CreateSimulation();
        await using var first = new TenantContext(simulation, 1);
        var invoice = await first.Invoices.SingleAsync(i => i.Id == 1, this.TestContext.CancellationToken);
        invoice.TenantId = 2;

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => first.SaveChangesAsync(this.TestContext.CancellationToken));
        Assert.AreEqual(33504, Assert.IsInstanceOfType<SimulatedSqlException>(error.InnerException).Number);
    }
}
