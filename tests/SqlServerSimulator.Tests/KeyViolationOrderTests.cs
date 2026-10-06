using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which key a row breaking several reports: real checks the clustered index
/// first and the rest in <c>index_id</c> order, and an <c>IGNORE_DUP_KEY</c>
/// key the row duplicates drops it whatever else it breaks (probed 2026-10-06
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class KeyViolationOrderTests
{
    [TestMethod]
    public void TableLevelPrimaryKey_OutranksAnEarlierUnique()
        // CREATE TABLE numbers the primary key ahead of the unique constraint
        // declared before it.
        => new Simulation().AssertSqlError("""
            create table t (a int, b int not null, constraint u unique (a), constraint p primary key nonclustered (b));
            insert t values (1, 1);
            insert t values (1, 1)
            """, 2627, "Violation of PRIMARY KEY constraint 'p'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).");

    [TestMethod]
    public void LaterClusteredPrimaryKey_IsCheckedFirst()
        => new Simulation().AssertSqlError("""
            create table t (a int, b int, c int not null, constraint u unique (a));
            create unique index ix on t(b);
            alter table t add constraint p primary key clustered (c);
            insert t values (1, 1, 1);
            insert t values (1, 1, 1)
            """, 2627, "Violation of PRIMARY KEY constraint 'p'. Cannot insert duplicate key in object 'dbo.t'. The duplicate key value is (1).");

    [TestMethod]
    public void UniqueIndex_CreatedBeforeAUniqueConstraint_IsCheckedFirst()
        => new Simulation().AssertSqlError("""
            create table t (a int, b int, c int);
            create unique index ix on t(c);
            alter table t add constraint u unique (a);
            insert t values (1, 1, 1);
            insert t values (1, 2, 1)
            """, 2601, "Cannot insert duplicate key row in object 'dbo.t' with unique index 'ix'. The duplicate key value is (1).");

    [TestMethod]
    public void Update_ReportsTheLowestIndexId()
        => new Simulation().AssertSqlError("""
            create table t (a int, b int, c int not null primary key clustered);
            create unique index ix on t(b);
            alter table t add constraint u unique (a);
            insert t values (1, 1, 1), (2, 2, 2);
            update t set a = 1, b = 1 where c = 2
            """, 2601, "Cannot insert duplicate key row in object 'dbo.t' with unique index 'ix'. The duplicate key value is (1).");

    [TestMethod]
    public void IgnoreDupKey_OnALaterIndex_DropsTheRow()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int not null primary key clustered, b int);
            create unique index ix on t(b) with (ignore_dup_key = on);
            insert t values (1, 1)
            """);
        AreEqual(0, simulation.ExecuteNonQuery("insert t values (1, 1)"));
        _ = simulation.AssertSqlError("insert t values (1, 2)", 2627);
    }
}
