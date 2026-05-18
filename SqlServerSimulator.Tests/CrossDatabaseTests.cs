using SqlServerSimulator.Bacpac;
using SqlServerSimulator.Storage.Bacpac;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Cross-database 3-part-name routing + <c>USE &lt;db&gt;</c> support.
/// Wording / message numbers / abort semantics probed against SQL Server 2025.
/// </summary>
[TestClass]
public class CrossDatabaseTests
{
    /// <summary>
    /// Builds a Simulation with two databases ("sales" + "ops"), each
    /// holding one table populated with rows, by going through ImportBacpac.
    /// All cross-DB scenarios below operate on this fixture.
    /// </summary>
    private static Simulation TwoDatabaseFixture()
    {
        var sim = new Simulation();
        using (var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Customer", t => t.Column("Id", "int").Row(1).Row(2).Row(3))
            .Build())
        {
            sim.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "sales" });
        }
        using (var bacpac = BacpacBuilder.Create()
            .Table("dbo", "Order_", t => t.Column("Id", "int").Row(10).Row(20))
            .Build())
        {
            sim.ImportBacpac(bacpac, out _, new BacpacImportOptions { DatabaseName = "ops" });
        }
        return sim;
    }

    [TestMethod]
    public void Use_SwitchesCurrentDatabase()
    {
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "USE sales; SELECT COUNT(*) FROM Customer";
        AreEqual(3, cmd.ExecuteScalar());
        cmd.CommandText = "USE ops; SELECT COUNT(*) FROM Order_";
        AreEqual(2, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void Use_BracketedName_Works()
    {
        var sim = TwoDatabaseFixture();
        AreEqual(3, sim.ExecuteScalar("USE [sales]; SELECT COUNT(*) FROM Customer"));
    }

    [TestMethod]
    public void Use_MissingDatabase_Msg911()
    {
        var sim = TwoDatabaseFixture();
        var ex = sim.AssertSqlError("USE NoSuchDb", 911);
        AreEqual("Database 'NoSuchDb' does not exist. Make sure that the name is entered correctly.", ex.Message);
    }

    [TestMethod]
    public void Use_MissingDatabase_AbortsBatch()
    {
        // Real SQL Server: a failing USE prevents subsequent statements
        // from running. The simulator's dispatch loop surfaces the
        // SimulatedSqlException at the USE site; subsequent statements in
        // the same batch never reach the dispatch.
        var sim = TwoDatabaseFixture();
        _ = sim.AssertSqlError("USE NoSuchDb; SELECT 99", 911);
    }

    [TestMethod]
    public void Use_VariableForm_Msg102()
    {
        var sim = TwoDatabaseFixture();
        _ = sim.AssertSqlError("DECLARE @d sysname = 'sales'; USE @d", 102);
    }

    [TestMethod]
    public void Use_InsideIf_Works()
    {
        var sim = TwoDatabaseFixture();
        AreEqual(3, sim.ExecuteScalar("IF 1=1 USE sales; SELECT COUNT(*) FROM Customer"));
    }

    [TestMethod]
    public void Use_SurvivesRollback()
    {
        // USE is not transactional — probe-confirmed.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("BEGIN TRAN; USE sales; ROLLBACK").ExecuteNonQuery();
        using var cmd = conn.CreateCommand("SELECT COUNT(*) FROM Customer");
        AreEqual(3, cmd.ExecuteScalar());
    }

    [TestMethod]
    public void ThreePartName_OtherDatabase_Reads()
    {
        var sim = TwoDatabaseFixture();
        // Default connection routes to alphabetically-first ("ops"). The
        // 3-part name reaches into "sales" without an explicit USE.
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM sales.dbo.Customer"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM ops.dbo.Order_"));
    }

    [TestMethod]
    public void ShortForm_DbDotDotTable_ResolvesToDefaultSchema()
    {
        // Real SQL Server: `db..table` resolves to `db.<user-default-schema>.table`.
        // The simulator has no per-login default schema so it always
        // substitutes `dbo` — equivalent for users whose default schema is
        // dbo (probe-confirmed: `claude..probe_short` returns the dbo table).
        var sim = TwoDatabaseFixture();
        AreEqual(3, sim.ExecuteScalar("SELECT COUNT(*) FROM sales..Customer"));
        AreEqual(2, sim.ExecuteScalar("SELECT COUNT(*) FROM ops..Order_"));
    }

    [TestMethod]
    public void ThreePartName_CrossDatabaseJoin_Works()
    {
        var sim = TwoDatabaseFixture();
        // 2 ops orders × 3 sales customers = 6 rows.
        AreEqual(6, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sales.dbo.Customer c CROSS JOIN ops.dbo.Order_ o"));
    }

    [TestMethod]
    public void ThreePartName_CatalogView_ScopesToNamedDatabase()
    {
        var sim = TwoDatabaseFixture();
        // sales.sys.tables iterates sales schemas — should find Customer.
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sales.sys.tables WHERE name = 'Customer'"));
        AreEqual(0, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM sales.sys.tables WHERE name = 'Order_'"));
        AreEqual(1, sim.ExecuteScalar(
            "SELECT COUNT(*) FROM ops.sys.tables WHERE name = 'Order_'"));
    }

    [TestMethod]
    public void ThreePartName_MissingDatabase_Msg208()
    {
        var sim = TwoDatabaseFixture();
        // Real SQL Server returns Msg 208 with the fully-qualified name.
        var ex = sim.AssertSqlError("SELECT * FROM NoSuchDb.dbo.t", 208);
        Contains("'NoSuchDb.dbo.t'", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseInsert_Rejected_NotSupported()
    {
        var sim = TwoDatabaseFixture();
        // Connection routes to "ops" (alphabetically first), so writing to
        // sales.dbo.Customer via 3-part name is cross-DB.
        var ex = Throws<NotSupportedException>(() =>
            sim.ExecuteNonQuery("INSERT INTO sales.dbo.Customer VALUES (99)"));
        Contains("Cross-database write", ex.Message);
        Contains("sales.dbo.Customer", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseUpdate_Rejected_NotSupported()
    {
        var sim = TwoDatabaseFixture();
        var ex = Throws<NotSupportedException>(() =>
            sim.ExecuteNonQuery("UPDATE sales.dbo.Customer SET Id = Id + 100"));
        Contains("Cross-database write", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseDelete_Rejected_NotSupported()
    {
        var sim = TwoDatabaseFixture();
        var ex = Throws<NotSupportedException>(() =>
            sim.ExecuteNonQuery("DELETE FROM sales.dbo.Customer"));
        Contains("Cross-database write", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseWrite_AfterUseToTargetDb_Works()
    {
        // After USE sales, the same INSERT references the current DB and
        // routes through the normal single-DB path.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("USE sales").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("INSERT INTO Customer VALUES (99)").ExecuteNonQuery());
        AreEqual(4, conn.CreateCommand("SELECT COUNT(*) FROM Customer").ExecuteScalar());
    }

    [TestMethod]
    public void Use_SkippedBranch_DoesNotSwitchDatabase()
    {
        // Un-taken branch short-circuits before the database lookup — the
        // target name doesn't need to exist for skip-mode to be exercised.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table only_in_simulated (id int); insert only_in_simulated values (42)");
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("if 1=0 use no_such_database").ExecuteNonQuery();
        AreEqual(42, conn.CreateCommand("select id from only_in_simulated").ExecuteScalar());
    }
}
