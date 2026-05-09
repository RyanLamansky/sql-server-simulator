using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// End-to-end tests for EF Core 10's <c>Database.BeginTransaction</c> path.
/// Probe-confirmed against SQL Server 2025: EF wraps SaveChanges through
/// SqlClient's transaction API (not raw <c>BEGIN TRANSACTION</c> SQL), so
/// the simulator's connection-scoped <see cref="Storage.UndoLog"/> handles
/// the entire transaction lifecycle. Bundle 1 already covered statement-
/// level atomicity; Bundle 2 adds the cross-statement transaction scope.
/// </summary>
[TestClass]
public class EFCoreTransaction
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Database_BeginTransaction_Commit_PersistsAcrossSaveChanges()
    {
        using var context = new TestDbContext(TestDbContext.CreateAuthorsSimulation());
        using var tx = context.Database.BeginTransaction();

        _ = context.Authors.Add(new Author { Name = "alice" });
        _ = context.SaveChanges();
        _ = context.Books.Add(new Book { AuthorId = 1, Title = "B1", Score = 10 });
        _ = context.SaveChanges();

        tx.Commit();

        Assert.AreEqual(1, context.Authors.AsNoTracking().Count());
        Assert.AreEqual(1, context.Books.AsNoTracking().Count());
    }

    [TestMethod]
    public void Database_BeginTransaction_Rollback_UndoesAcrossSaveChanges()
    {
        using var context = new TestDbContext(TestDbContext.CreateAuthorsSimulation());
        using (var tx = context.Database.BeginTransaction())
        {
            _ = context.Authors.Add(new Author { Name = "alice" });
            _ = context.SaveChanges();
            _ = context.Books.Add(new Book { AuthorId = 1, Title = "B1", Score = 10 });
            _ = context.SaveChanges();
            tx.Rollback();
        }

        // Both saves rolled back together.
        Assert.AreEqual(0, context.Authors.AsNoTracking().Count());
        Assert.AreEqual(0, context.Books.AsNoTracking().Count());
    }

    [TestMethod]
    public void SaveChangesFailure_InsideTx_LeavesTxAliveAndCommitable()
    {
        // Probe-confirmed shape: a SaveChanges that hits a constraint
        // mid-batch (rolled back via the EF-emitted ROLLBACK TRANSACTION
        // savepoint) leaves the surrounding transaction alive. The user
        // can continue with more SaveChanges or commit what survived.
        var simulation = TestDbContext.CreateAuthorsSimulation();
        // Pre-seed via raw SQL so we have a key to collide with.
        _ = simulation.CreateOpenConnection()
            .CreateCommand("set identity_insert Authors on; insert Authors (Id, Name) values (1, 'alice'); set identity_insert Authors off;")
            .ExecuteNonQuery();

        using var context = new TestDbContext(simulation);
        using var tx = context.Database.BeginTransaction();

        // Bob saves successfully (auto-Id=2).
        _ = context.Authors.Add(new Author { Name = "bob" });
        _ = context.SaveChanges();

        // Force a duplicate-PK collision against the pre-seeded alice (Id=1).
        var dup = new Author { Id = 1, Name = "duplicate" };
        _ = context.Authors.Add(dup);
        _ = Assert.Throws<DbUpdateException>(() => context.SaveChanges());
        context.Entry(dup).State = EntityState.Detached;

        // Tx alive — commit succeeds; bob's pre-failure save survives.
        tx.Commit();

        var names = context.Authors.AsNoTracking().Select(a => a.Name).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(new[] { "alice", "bob" }, names);
    }

    [TestMethod]
    public void Using_TxDisposedWithoutCommit_AutoRollsBack()
    {
        // The classic "exception before tx.Commit()" pattern: the using-block
        // disposes the tx, which auto-rolls-back since neither Commit nor
        // Rollback ran.
        using var context = new TestDbContext(TestDbContext.CreateAuthorsSimulation());
        try
        {
            using var tx = context.Database.BeginTransaction();
            _ = context.Authors.Add(new Author { Name = "alice" });
            _ = context.SaveChanges();
            throw new InvalidOperationException("simulated failure pre-commit");
        }
        catch (InvalidOperationException)
        {
        }

        Assert.AreEqual(0, context.Authors.AsNoTracking().Count());
    }
}
