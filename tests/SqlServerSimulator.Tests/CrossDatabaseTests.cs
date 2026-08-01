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
    public void Use_InsideExecDynamicSql_BindsForThatBatchOnly()
    {
        // Probe-confirmed: a USE inside EXEC('…') changes the context for the
        // dynamic batch and the caller resumes where it was.
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "USE sales; EXEC('USE ops; SELECT COUNT(*) FROM Order_')";
        AreEqual(2, cmd.ExecuteScalar());
        cmd.CommandText = "SELECT DB_NAME()";
        AreEqual("sales", cmd.ExecuteScalar());
    }

    [TestMethod]
    public void Use_InsideSpExecuteSql_BindsForThatBatchOnly()
    {
        var sim = TwoDatabaseFixture();
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "USE sales; EXEC sp_executesql N'USE ops; SELECT DB_NAME()'";
        AreEqual("ops", cmd.ExecuteScalar());
        cmd.CommandText = "SELECT DB_NAME()";
        AreEqual("sales", cmd.ExecuteScalar());
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

    // === Cross-database writes through a three-part name ===
    //
    // Probed against SQL Server 2025 (2026-07-31): every shape below succeeds
    // on real, one transaction spans both databases, SCOPE_IDENTITY flows to
    // the caller, the rowversion counter charged is the target's, and a
    // trigger on the target runs in the target database's context.

    /// <summary>
    /// Session database <c>simulated</c> plus a target database <c>zdb</c>
    /// holding <c>dbo.t</c>. The target sorts after <c>simulated</c> so a
    /// fresh connection still lands in the session database.
    /// </summary>
    private static Simulation WriteFixture()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create database zdb;
            use zdb;
            create table dbo.t (id int identity(1, 1) primary key, v nvarchar(20) null);
            insert dbo.t (v) values ('a'), ('b')
            """);
        return sim;
    }

    [TestMethod]
    public void CrossDatabaseInsert_WritesToTheNamedDatabase()
    {
        var sim = WriteFixture();
        AreEqual(1, sim.ExecuteNonQuery("insert zdb.dbo.t (v) values ('x')"));
        AreEqual(3, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
        AreEqual("simulated", sim.ExecuteScalar("select db_name()"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_FlowsScopeIdentityToTheCallingSession()
    {
        var sim = WriteFixture();
        AreEqual(3, sim.ExecuteScalar("insert zdb.dbo.t (v) values ('x'); select cast(scope_identity() as int)"));
    }

    [TestMethod]
    public void CrossDatabaseUpdate_WritesToTheNamedDatabase()
    {
        var sim = WriteFixture();
        AreEqual(1, sim.ExecuteNonQuery("update zdb.dbo.t set v = 'updated' where v = 'a'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'updated'"));
    }

    [TestMethod]
    public void CrossDatabaseDelete_WritesToTheNamedDatabase()
    {
        var sim = WriteFixture();
        AreEqual(1, sim.ExecuteNonQuery("delete zdb.dbo.t where v = 'a'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
    }

    [TestMethod]
    public void CrossDatabaseMerge_WritesToTheNamedDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            merge zdb.dbo.t as tgt
            using (values (1, 'merged'), (99, 'inserted')) as s (id, v) on tgt.id = s.id
            when matched then update set v = s.v
            when not matched then insert (v) values (s.v);
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'merged'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'inserted'"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_OutputClause_ReturnsTheWrittenRows()
    {
        var sim = WriteFixture();
        AreEqual("x", sim.ExecuteScalar("insert zdb.dbo.t (v) output inserted.v values ('x')"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_OutputInto_TargetsTheSessionDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.audit (id int, v nvarchar(20));
            insert zdb.dbo.t (v) output inserted.id, inserted.v into dbo.audit values ('x')
            """);
        AreEqual("x", sim.ExecuteScalar("select v from dbo.audit"));
    }

    [TestMethod]
    public void OutputInto_TargetsTheOtherDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.local (id int primary key, v nvarchar(20));
            use zdb;
            create table dbo.audit (id int, v nvarchar(20));
            use simulated;
            insert dbo.local (id, v) output inserted.id, inserted.v into zdb.dbo.audit values (1, 'x')
            """);
        AreEqual("x", sim.ExecuteScalar("select v from zdb.dbo.audit"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_SelectsFromTheSessionDatabase()
    {
        // The written-name split EF and reporting tools actually emit: target
        // qualified across the boundary, source unqualified in the session's db.
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.src (id int primary key, v nvarchar(20));
            insert dbo.src values (1, 'from-src'), (2, 'from-src2');
            insert zdb.dbo.t (v) select v from dbo.src
            """);
        AreEqual(2, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v like 'from-src%'"));
    }

    [TestMethod]
    public void CrossDatabaseMerge_SourceInTheSessionDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.src (id int primary key, v nvarchar(20));
            insert dbo.src values (1, 'merged'), (7, 'new');
            merge zdb.dbo.t as tgt using dbo.src as s on tgt.id = s.id
            when matched then update set v = s.v
            when not matched then insert (v) values (s.v);
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'merged'"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'new'"));
    }

    [TestMethod]
    public void CrossDatabaseWrite_RollsBackWithTheTransaction()
    {
        // One transaction spans both databases in-instance — probe-confirmed
        // that ROLLBACK undoes the other database's write and @@TRANCOUNT
        // never reflects the crossing.
        var sim = WriteFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("begin tran; insert zdb.dbo.t (v) values ('rolled-back')").ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual(3, conn.CreateCommand("select count(*) from zdb.dbo.t").ExecuteScalar());
        _ = conn.CreateCommand("rollback").ExecuteNonQuery();
        AreEqual(2, conn.CreateCommand("select count(*) from zdb.dbo.t").ExecuteScalar());
    }

    [TestMethod]
    public void CrossDatabaseWrite_RollsBackToASavepoint()
    {
        var sim = WriteFixture();
        using var conn = sim.CreateOpenConnection();
        _ = conn.CreateCommand("""
            begin tran;
            insert zdb.dbo.t (v) values ('kept');
            save tran s1;
            insert zdb.dbo.t (v) values ('dropped');
            rollback tran s1;
            commit
            """).ExecuteNonQuery();
        AreEqual(1, conn.CreateCommand("select count(*) from zdb.dbo.t where v = 'kept'").ExecuteScalar());
        AreEqual(0, conn.CreateCommand("select count(*) from zdb.dbo.t where v = 'dropped'").ExecuteScalar());
    }

    [TestMethod]
    public void CrossDatabaseInsert_ChargesTheTargetDatabaseRowVersionCounter()
    {
        // The rowversion counter is per-database (@@DBTS), so the cross-database
        // INSERT advances the target's and leaves the session's alone: the
        // session table's second row is stamp 2, not 3.
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.local (id int primary key, r rowversion);
            use zdb;
            create table dbo.remote (id int primary key, r rowversion);
            use simulated;
            insert dbo.local (id) values (1);
            insert zdb.dbo.remote (id) values (1);
            insert dbo.local (id) values (2)
            """);
        AreEqual(2L, sim.ExecuteScalar("select cast(r as bigint) from dbo.local where id = 2"));
        AreEqual(1L, sim.ExecuteScalar("select cast(r as bigint) from zdb.dbo.remote where id = 1"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_FiresTheTargetsTriggerInTheTargetDatabase()
    {
        // Probe-confirmed: DB_NAME() inside the body is the target database, so
        // the body's unqualified INSERT lands there rather than in the caller's.
        var sim = WriteFixture();
        sim.ExecuteBatches(
            "use zdb",
            "create table dbo.trigger_log (msg nvarchar(100))",
            "create trigger dbo.tr_t on dbo.t after insert as insert dbo.trigger_log (msg) select db_name()",
            "use simulated",
            "insert zdb.dbo.t (v) values ('fires')");
        AreEqual("zdb", sim.ExecuteScalar("select msg from zdb.dbo.trigger_log"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_TriggerBodyFailure_RollsBackTheWrite()
    {
        var sim = WriteFixture();
        sim.ExecuteBatches(
            "use zdb",
            "create trigger dbo.tr_t on dbo.t after insert as throw 51000, 'no', 1",
            "use simulated");
        _ = sim.AssertSqlError("insert zdb.dbo.t (v) values ('x')", 51000);
        AreEqual(2, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_IdentityInsertThroughAThreePartName()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            set identity_insert zdb.dbo.t on;
            insert zdb.dbo.t (id, v) values (500, 'explicit');
            set identity_insert zdb.dbo.t off
            """);
        AreEqual("explicit", sim.ExecuteScalar("select v from zdb.dbo.t where id = 500"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_EnforcesTheTargetsForeignKey()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            use zdb;
            create table dbo.parent (id int primary key);
            create table dbo.child (id int primary key, p int not null references dbo.parent (id));
            use simulated
            """);
        var ex = sim.AssertSqlError("insert zdb.dbo.child values (1, 99)", 547);
        Contains("FOREIGN KEY constraint", ex.Message);
    }

    [TestMethod]
    public void CrossDatabaseInsert_ConsumesAThreePartSequence()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            use zdb;
            create sequence dbo.s as int start with 100 increment by 1;
            create table dbo.seeded (id int primary key);
            use simulated;
            insert zdb.dbo.seeded (id) values (next value for zdb.dbo.s)
            """);
        AreEqual(100, sim.ExecuteScalar("select id from zdb.dbo.seeded"));
    }

    [TestMethod]
    public void CrossDatabaseUpdate_AliasFormJoiningTheSessionDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.src (id int primary key, v nvarchar(20));
            insert dbo.src values (1, 'joined');
            update b set v = s.v from zdb.dbo.t b join dbo.src s on b.id = s.id
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'joined'"));
    }

    [TestMethod]
    public void CrossDatabaseDelete_AliasFormJoiningTheSessionDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("""
            create table dbo.src (id int primary key);
            insert dbo.src values (1);
            delete b from zdb.dbo.t b join dbo.src s on b.id = s.id
            """);
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
    }

    [TestMethod]
    public void CrossDatabaseWrite_ThroughTheShortForm()
    {
        var sim = WriteFixture();
        AreEqual(1, sim.ExecuteNonQuery("insert zdb..t (v) values ('short')"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'short'"));
    }

    [TestMethod]
    public void CrossDatabaseWrite_ThroughAViewInTheOtherDatabase()
    {
        var sim = WriteFixture();
        sim.ExecuteBatches(
            "use zdb",
            "create view dbo.v as select id, v from dbo.t",
            "use simulated",
            "insert zdb.dbo.v (v) values ('viewed')");
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.dbo.t where v = 'viewed'"));
    }

    [TestMethod]
    public void CrossDatabaseUpdate_FiresTheTargetsUpdateTrigger()
    {
        var sim = WriteFixture();
        sim.ExecuteBatches(
            "use zdb",
            "create table dbo.trigger_log (msg nvarchar(100))",
            "create trigger dbo.tr_t on dbo.t after update as insert dbo.trigger_log (msg) select db_name()",
            "use simulated",
            "update zdb.dbo.t set v = 'z' where id = 1");
        AreEqual("zdb", sim.ExecuteScalar("select msg from zdb.dbo.trigger_log"));
    }

    [TestMethod]
    public void CrossDatabaseInsert_TriggerBodyWritesBackThroughAThreePartName()
    {
        // The body runs in the target's database, so reaching the firing
        // session's database is itself a three-part write — the round trip.
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("create table dbo.written_back (msg nvarchar(50))");
        sim.ExecuteBatches(
            "use zdb",
            "create trigger dbo.tr_t on dbo.t after insert as insert simulated.dbo.written_back (msg) select 'from-trigger'",
            "use simulated",
            "insert zdb.dbo.t (v) values ('x')");
        AreEqual("from-trigger", sim.ExecuteScalar("select msg from dbo.written_back"));
    }

    [TestMethod]
    public void CrossDatabaseWrite_UnderTheTargetsReadCommittedSnapshot()
    {
        // Version capture follows the target's setting, not the session's —
        // the version store is per-database.
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("alter database zdb set read_committed_snapshot on");
        AreEqual(1, sim.ExecuteNonQuery("insert zdb.dbo.t (v) values ('rcsi')"));
        _ = sim.ExecuteNonQuery("update zdb.dbo.t set v = 'rcsi2' where v = 'rcsi'");
        AreEqual(3, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
    }

    [TestMethod]
    public void ThreePartName_Ddl_TargetsTheNamedDatabase()
    {
        // DDL through a three-part name routes the same way DML does (real
        // accepts CREATE / ALTER / DROP / TRUNCATE TABLE this way; only
        // CREATE VIEW / PROCEDURE / FUNCTION / TRIGGER refuse the db prefix,
        // with Msg 166).
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("create table zdb.dbo.fresh (id int)");
        _ = sim.ExecuteNonQuery("alter table zdb.dbo.fresh add extra int null");
        _ = sim.ExecuteNonQuery("truncate table zdb.dbo.t");
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.sys.columns where name = 'extra'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from zdb.dbo.t"));
        _ = sim.ExecuteNonQuery("drop table zdb.dbo.fresh");
        AreEqual(0, sim.ExecuteScalar("select count(*) from zdb.sys.tables where name = 'fresh'"));
    }

    [TestMethod]
    public void CrossDatabaseWrite_IntoAnImportedDatabase()
    {
        // BACPAC-imported tables are registered through the same CREATE TABLE
        // path, so they carry the owning-database stamp the routing reads —
        // the trigger fires and reports the imported database.
        var sim = TwoDatabaseFixture();
        sim.ExecuteBatches(
            "use sales",
            "create table dbo.trigger_log (msg nvarchar(50))",
            "create trigger dbo.tr_customer on dbo.Customer after insert as insert dbo.trigger_log (msg) select db_name()",
            "use ops",
            "insert sales.dbo.Customer values (99)");
        AreEqual("sales", sim.ExecuteScalar("select msg from sales.dbo.trigger_log"));
        AreEqual(4, sim.ExecuteScalar("select count(*) from sales.dbo.Customer"));
    }

    [TestMethod]
    public void CrossDatabaseSelectInto_CreatesTheTableInTheNamedDatabase()
    {
        var sim = WriteFixture();
        _ = sim.ExecuteNonQuery("select id, v into zdb.dbo.copied from zdb.dbo.t");
        AreEqual(2, sim.ExecuteScalar("select count(*) from zdb.dbo.copied"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from zdb.sys.tables where name = 'copied'"));
        AreEqual(0, sim.ExecuteScalar("select count(*) from sys.tables where name = 'copied'"));
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
