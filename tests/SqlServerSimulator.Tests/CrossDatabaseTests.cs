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

    // Scalar metadata lookups across DBs. Real SQL Server probe (2026-05-23):
    // OBJECT_ID's name arg honors 3-part / db..table / bracketed-3-part forms
    // and routes to the named database; OBJECT_NAME's optional 2nd arg is
    // load-bearing — without it, only the current DB's id space is consulted.
    // SCHEMA_ID / SCHEMA_NAME have no 3-part-name reach (leaf-name only).

    [TestMethod]
    public void ObjectId_ThreePartName_ReachesNamedDatabase()
    {
        var sim = TwoDatabaseFixture();
        // Default connection routes to "ops" (alphabetical). 3-part name
        // reaches into "sales".
        AreNotEqual(DBNull.Value, sim.ExecuteScalar("select object_id('sales.dbo.Customer')"));
        AreNotEqual(DBNull.Value, sim.ExecuteScalar("select object_id('[sales].[dbo].[Customer]')"));
        AreNotEqual(DBNull.Value, sim.ExecuteScalar("select object_id('sales..Customer')"));
    }

    [TestMethod]
    public void ObjectId_ThreePartName_MissingTable_ReturnsNull()
    {
        var sim = TwoDatabaseFixture();
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('sales.dbo.NoSuchTable')"));
    }

    [TestMethod]
    public void ObjectId_ThreePartName_MissingDatabase_ReturnsNull()
    {
        var sim = TwoDatabaseFixture();
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_id('no_such_db.dbo.Customer')"));
    }

    [TestMethod]
    public void ObjectId_ThreePartName_AgreesAcrossDatabasesForSameTable()
    {
        // The 1-part name resolves in the current DB; the 3-part name targets
        // the same row in the same DB; both must return the same id.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("USE ops").ExecuteNonQuery();
        var local = conn.CreateCommand("select object_id('Order_')").ExecuteScalar();
        var qualified = conn.CreateCommand("select object_id('ops.dbo.Order_')").ExecuteScalar();
        AreNotEqual(DBNull.Value, local);
        AreEqual(local, qualified);
    }

    [TestMethod]
    public void ObjectName_WithDbId_ReachesNamedDatabase()
    {
        // OBJECT_NAME(id, db_id) walks the named DB's object-id namespace
        // — each DB allocates ids independently, so the same id can name
        // different objects in different DBs. The db_id arg picks which
        // DB's mapping to use.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        var salesCustId = (int)conn.CreateCommand("select object_id('sales.dbo.Customer')").ExecuteScalar()!;
        AreEqual("Customer",
            conn.CreateCommand($"select object_name({salesCustId}, db_id('sales'))").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectName_WithDbId_DistinguishesCollidingIds()
    {
        // Per-database allocation: sales.dbo.Customer and ops.dbo.Order_ end
        // up with the same object_id. With db_id, OBJECT_NAME returns the
        // name from the named DB regardless of where the id was first
        // observed — proving the second arg routes the lookup.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        var customerId = (int)conn.CreateCommand("select object_id('sales.dbo.Customer')").ExecuteScalar()!;
        var orderId = (int)conn.CreateCommand("select object_id('ops.dbo.Order_')").ExecuteScalar()!;
        AreEqual(customerId, orderId);
        AreEqual("Customer", conn.CreateCommand($"select object_name({customerId}, db_id('sales'))").ExecuteScalar());
        AreEqual("Order_", conn.CreateCommand($"select object_name({customerId}, db_id('ops'))").ExecuteScalar());
    }

    [TestMethod]
    public void ObjectName_WithDbId_IdNotPresentInNamedDb_ReturnsNull()
    {
        // db_id routes the lookup to ops; an id that ops doesn't allocate
        // resolves to NULL (the lookup never crosses into other DBs).
        var sim = TwoDatabaseFixture();
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_name(99999, db_id('ops'))"));
    }

    [TestMethod]
    public void ObjectName_WithDbId_Null_ReturnsNull()
    {
        var sim = TwoDatabaseFixture();
        var anyId = (int)sim.ExecuteScalar("select object_id('sales.dbo.Customer')")!;
        AreEqual(DBNull.Value, sim.ExecuteScalar($"select object_name({anyId}, NULL)"));
    }

    [TestMethod]
    public void ObjectName_WithDbId_InvalidDbId_ReturnsNull()
    {
        var sim = TwoDatabaseFixture();
        var anyId = (int)sim.ExecuteScalar("select object_id('sales.dbo.Customer')")!;
        AreEqual(DBNull.Value, sim.ExecuteScalar($"select object_name({anyId}, 99999)"));
    }

    [TestMethod]
    public void SchemaId_MultiPartName_ReturnsNull()
    {
        // Real SQL Server treats SCHEMA_ID as leaf-name-only. Any dot in the
        // argument fails to resolve and returns NULL.
        var sim = TwoDatabaseFixture();
        AreEqual(DBNull.Value, sim.ExecuteScalar("select schema_id('sales.dbo')"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select schema_id('ops.dbo')"));
    }

    [TestMethod]
    public void SchemaId_ScopedToCurrentDatabase()
    {
        // SCHEMA_ID reads only the current DB's sys.schemas. Same name
        // resolves the same id regardless of which DB owns it — both DBs in
        // the fixture have a dbo at id 1.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("USE ops").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select schema_id('dbo')").ExecuteScalar());
        _ = conn.CreateCommand("USE sales").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select schema_id('dbo')").ExecuteScalar());
    }
}
