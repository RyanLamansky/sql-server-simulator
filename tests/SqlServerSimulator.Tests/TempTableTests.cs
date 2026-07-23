using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for local temp tables (<c>#foo</c>) and global temp tables (<c>##foo</c>).
/// Covers session-scoped storage for local temps, instance-wide visibility for
/// global temps, cross-batch persistence within a connection, cross-connection
/// isolation (local) / sharing (global), auto-drop at connection close, DROP
/// TABLE semantics (regular + temp, IF EXISTS, Msg 3701), three-part-name
/// acceptance in DROP, identity / SCOPE_IDENTITY across DML, transactional
/// CREATE / DROP undo. Behavior probed against SQL Server 2025 (2026-05-11).
/// </summary>
[TestClass]
public sealed class TempTableTests
{
    private static int CountRows(DbConnection conn, string table) =>
        (int)conn.CreateCommand($"select count(*) from {table}").ExecuteScalar()!;

    private static void Exec(DbConnection conn, string sql) =>
        _ = conn.CreateCommand(sql).ExecuteNonQuery();

    private static int ExecRows(DbConnection conn, string sql) =>
        conn.CreateCommand(sql).ExecuteNonQuery();

    private static void ExecInTx(DbConnection conn, DbTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    [TestMethod]
    public void CreateInsertSelect_HappyPath()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(-1, ExecRows(conn, "create table #t (id int, name varchar(20))"));
        AreEqual(2, ExecRows(conn, "insert #t values (1, 'a'), (2, 'b')"));
        AreEqual(2, CountRows(conn, "#t"));
    }

    [TestMethod]
    public void PersistsAcrossBatchesInSameConnection()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        Exec(conn, "insert #t values (1)");
        Exec(conn, "insert #t values (2)");
        AreEqual(2, CountRows(conn, "#t"));
    }

    [TestMethod]
    public void AutoDroppedOnConnectionClose()
    {
        var sim = new Simulation();
        using (var conn = sim.CreateOpenConnection())
        {
            Exec(conn, "create table #t (id int)");
            Exec(conn, "insert #t values (1)");
        }
        using var conn2 = sim.CreateOpenConnection();
        var ex = Throws<DbException>(() => Exec(conn2, "select * from #t"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void CrossConnectionIsolation()
    {
        var sim = new Simulation();
        using var a = sim.CreateOpenConnection();
        using var b = sim.CreateOpenConnection();
        Exec(a, "create table #shared (id int)");
        Exec(a, "insert #shared values (10)");
        var ex = Throws<DbException>(() => Exec(b, "select * from #shared"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
        Exec(b, "create table #shared (id int)");
        Exec(b, "insert #shared values (20)");
        AreEqual(10, (int)a.CreateCommand("select id from #shared").ExecuteScalar()!);
        AreEqual(20, (int)b.CreateCommand("select id from #shared").ExecuteScalar()!);
    }

    [TestMethod]
    public void DropTable_TempTable_RemovesIt()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        AreEqual(-1, ExecRows(conn, "drop table #t"));
        var ex = Throws<DbException>(() => Exec(conn, "select * from #t"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DropTable_RegularTable_RemovesIt()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table t (id int)");
        AreEqual(-1, ExecRows(conn, "drop table t"));
        var ex = Throws<DbException>(() => Exec(conn, "select * from t"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DropTable_Missing_RaisesMsg3701()
    {
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<DbException>(() => Exec(conn, "drop table #missing"));
        AreEqual("3701", ex.Data["HelpLink.EvtID"]);
        AreEqual("Cannot drop the table '#missing', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropTable_MissingRegular_RaisesMsg3701()
    {
        // The regular (non-temp) missing-table path raises the same Msg 3701
        // St 5 as the temp path above — probe-confirmed verbatim wording
        // against SQL Server 2025.
        using var conn = new Simulation().CreateOpenConnection();
        var ex = Throws<DbException>(() => Exec(conn, "drop table missing_regular"));
        AreEqual("3701", ex.Data["HelpLink.EvtID"]);
        AreEqual("Cannot drop the table 'missing_regular', because it does not exist or you do not have permission.", ex.Message);
    }

    [TestMethod]
    public void DropTable_IfExists_Missing_IsSilent()
    {
        using var conn = new Simulation().CreateOpenConnection();
        AreEqual(-1, ExecRows(conn, "drop table if exists #missing"));
        AreEqual(-1, ExecRows(conn, "drop table if exists missing_regular"));
    }

    [TestMethod]
    public void DropTable_Qualified_TempName_AcceptsAndIgnoresQualifier()
    {
        // Probe-confirmed: real SQL Server resolves `tempdb..#foo`,
        // `tempdb.dbo.#foo`, and even `claude..#foo` all to the session's
        // #foo. The simulator strips the qualifier on # names.
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        AreEqual(-1, ExecRows(conn, "drop table tempdb..#t"));
        Exec(conn, "create table #t2 (id int)");
        AreEqual(-1, ExecRows(conn, "drop table tempdb.dbo.#t2"));
    }

    [TestMethod]
    public void DropTable_CommaList()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #a (id int)");
        Exec(conn, "create table #b (id int)");
        AreEqual(-1, ExecRows(conn, "drop table #a, #b"));
        AreEqual("208", Throws<DbException>(() => Exec(conn, "select * from #a")).Data["HelpLink.EvtID"]);
        AreEqual("208", Throws<DbException>(() => Exec(conn, "select * from #b")).Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DuplicateCreate_RaisesMsg2714()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #dup (id int)");
        var ex = Throws<DbException>(() => Exec(conn, "create table #dup (id int)"));
        AreEqual("2714", ex.Data["HelpLink.EvtID"]);
        AreEqual("There is already an object named '#dup' in the database.", ex.Message);
    }

    [TestMethod]
    public void IdentityAndScopeIdentity_WorkOnTempTable()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #id (id int identity primary key, name varchar(10))");
        Exec(conn, "insert #id (name) values ('a')");
        Exec(conn, "insert #id (name) values ('b')");
        AreEqual(2m, (decimal)conn.CreateCommand("select scope_identity()").ExecuteScalar()!);
        AreEqual(2, CountRows(conn, "#id"));
    }

    [TestMethod]
    public void DropAndRecreate_SameName_DifferentSchema()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        Exec(conn, "insert #t values (1)");
        Exec(conn, "drop table #t");
        Exec(conn, "create table #t (id int, name varchar(10))");
        Exec(conn, "insert #t values (2, 'b')");
        AreEqual(1, CountRows(conn, "#t"));
    }

    [TestMethod]
    public void JoinAcrossTwoTempTables()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #x (id int, v int)");
        Exec(conn, "create table #y (id int, w int)");
        Exec(conn, "insert #x values (1, 10), (2, 20)");
        Exec(conn, "insert #y values (1, 100), (2, 200)");
        AreEqual(220, (int)conn.CreateCommand("select x.v + y.w from #x x join #y y on x.id = y.id where x.id = 2").ExecuteScalar()!);
    }

    [TestMethod]
    public void BareHashIsValidTempTableName()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table # (id int)");
        Exec(conn, "insert # values (1), (2)");
        AreEqual(2, CountRows(conn, "#"));
    }

    [TestMethod]
    public void Transaction_RollbackUndoesInsertIntoTempTable()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        using (var tx = conn.BeginTransaction())
        {
            ExecInTx(conn, tx, "insert #t values (1), (2)");
            tx.Rollback();
        }
        AreEqual(0, CountRows(conn, "#t"));
    }

    [TestMethod]
    public void Transaction_RollbackUndoesCreateTableTemp()
    {
        // Probe-confirmed: CREATE TABLE #foo inside BEGIN TRAN + ROLLBACK
        // leaves #foo gone on real SQL Server.
        using var conn = new Simulation().CreateOpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            ExecInTx(conn, tx, "create table #created_in_tx (id int)");
            tx.Rollback();
        }
        var ex = Throws<DbException>(() => Exec(conn, "select * from #created_in_tx"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Transaction_RollbackRestoresDroppedTempTable()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #t (id int)");
        Exec(conn, "insert #t values (1), (2)");
        using (var tx = conn.BeginTransaction())
        {
            ExecInTx(conn, tx, "drop table #t");
            tx.Rollback();
        }
        AreEqual(2, CountRows(conn, "#t"));
    }

    [TestMethod]
    public void Transaction_CommitKeepsTempTableCreation()
    {
        using var conn = new Simulation().CreateOpenConnection();
        using (var tx = conn.BeginTransaction())
        {
            ExecInTx(conn, tx, "create table #kept (id int)");
            tx.Commit();
        }
        Exec(conn, "insert #kept values (1)");
        AreEqual(1, CountRows(conn, "#kept"));
    }

    [TestMethod]
    public void GlobalTemp_CrossSession_Visible()
    {
        var sim = new Simulation();
        using var a = sim.CreateOpenConnection();
        using var b = sim.CreateOpenConnection();
        Exec(a, "create table ##g (id int)");
        Exec(a, "insert ##g values (10)");
        AreEqual(10, (int)b.CreateCommand("select id from ##g").ExecuteScalar()!);
        Exec(b, "insert ##g values (20)");
        AreEqual(2, CountRows(a, "##g"));
    }

    [TestMethod]
    public void GlobalTemp_DropFromNonOwnerSession_Works()
    {
        // Probe-confirmed: any session can drop another's ##foo.
        var sim = new Simulation();
        using var owner = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        Exec(owner, "create table ##g (id int)");
        Exec(other, "drop table ##g");
        var ex = Throws<DbException>(() => Exec(owner, "select * from ##g"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void GlobalTemp_AutoDroppedOnOwnerDisconnect()
    {
        // Probe-confirmed against SQL Server 2025 (pooling disabled): ##foo
        // dropped unconditionally when the creating session disconnects,
        // regardless of other sessions' references.
        var sim = new Simulation();
        using var witness = sim.CreateOpenConnection();
        using (var owner = sim.CreateOpenConnection())
        {
            Exec(owner, "create table ##g (id int)");
            Exec(owner, "insert ##g values (1)");
            AreEqual(1, CountRows(witness, "##g"));
        }
        var ex = Throws<DbException>(() => Exec(witness, "select * from ##g"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void GlobalTemp_DuplicateCreate_RaisesMsg2714()
    {
        var sim = new Simulation();
        using var a = sim.CreateOpenConnection();
        using var b = sim.CreateOpenConnection();
        Exec(a, "create table ##dup (id int)");
        var ex = Throws<DbException>(() => Exec(b, "create table ##dup (id int)"));
        AreEqual("2714", ex.Data["HelpLink.EvtID"]);
        AreEqual("There is already an object named '##dup' in the database.", ex.Message);
    }

    [TestMethod]
    public void GlobalTemp_BareDoubleHash_IsValidName()
    {
        // Probe-confirmed: real SQL Server accepts ## (length 2) as a table name.
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table ## (id int)");
        Exec(conn, "insert ## values (1), (2)");
        AreEqual(2, CountRows(conn, "##"));
    }

    [TestMethod]
    public void GlobalTemp_QualifierIgnored()
    {
        // Probe-confirmed: `tempdb..##q` and `tempdb.dbo.##q` both resolve to
        // the same global temp regardless of qualifier shape.
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table ##q (id int)");
        Exec(conn, "insert ##q values (5)");
        AreEqual(5, (int)conn.CreateCommand("select id from tempdb..##q").ExecuteScalar()!);
        AreEqual(5, (int)conn.CreateCommand("select id from tempdb.dbo.##q").ExecuteScalar()!);
    }

    [TestMethod]
    public void GlobalTemp_RollbackUndoesCreate()
    {
        var sim = new Simulation();
        using var owner = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        using (var tx = owner.BeginTransaction())
        {
            ExecInTx(owner, tx, "create table ##rb (id int)");
            tx.Rollback();
        }
        // Visible to neither session after rollback.
        var ex = Throws<DbException>(() => Exec(owner, "select * from ##rb"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
        AreEqual("208", Throws<DbException>(() => Exec(other, "select * from ##rb")).Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void GlobalTemp_RollbackRestoresDropped()
    {
        var sim = new Simulation();
        using var owner = sim.CreateOpenConnection();
        Exec(owner, "create table ##rd (id int)");
        Exec(owner, "insert ##rd values (1), (2)");
        using (var tx = owner.BeginTransaction())
        {
            ExecInTx(owner, tx, "drop table ##rd");
            tx.Rollback();
        }
        AreEqual(2, CountRows(owner, "##rd"));
    }

    [TestMethod]
    public void GlobalTemp_Truncate_FromAnySession()
    {
        var sim = new Simulation();
        using var owner = sim.CreateOpenConnection();
        using var other = sim.CreateOpenConnection();
        Exec(owner, "create table ##t (id int)");
        Exec(owner, "insert ##t values (1), (2), (3)");
        Exec(other, "truncate table ##t");
        AreEqual(0, CountRows(owner, "##t"));
    }

    // Module-scoped temp-table lifetime: a local #temp created inside a
    // procedure / trigger / dynamic-SQL body is dropped when that module
    // exits, not left on the session. Probe-confirmed against SQL Server 2025
    // (2026-07-23) — a following statement sees Msg 208, and a re-entrant call
    // re-creates the name without a Msg 2714 collision. Visibility down the
    // call stack (a nested module sees an enclosing module's temp) is
    // preserved.

    [TestMethod]
    public void ProcCreatedTemp_NotVisibleAfterProcReturns()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create procedure dbo.p as begin create table #pt (a int); insert #pt values (1); end");
        Exec(conn, "exec dbo.p");
        var ex = Throws<DbException>(() => Exec(conn, "select * from #pt"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void ProcCreatingTemp_CalledTwice_NoCollision()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create procedure dbo.p as begin create table #pt (a int); insert #pt values (1); select count(*) from #pt; end");
        Exec(conn, "exec dbo.p");
        // Without module scoping the second call would raise Msg 2714 (the
        // first call's #pt lingering on the session).
        AreEqual(1, conn.CreateCommand("exec dbo.p").ExecuteScalar());
    }

    [TestMethod]
    public void ProcSelectIntoTemp_NotVisibleAfterProcReturns()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create procedure dbo.p as begin select 1 a into #si; end");
        Exec(conn, "exec dbo.p");
        var ex = Throws<DbException>(() => Exec(conn, "select * from #si"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void NestedProc_SeesEnclosingProcTemp_ThenBothDropAtExit()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create procedure dbo.inner_p as begin select count(*) from #outer; end");
        Exec(conn, "create procedure dbo.outer_p as begin create table #outer (a int); insert #outer values (1), (2); exec dbo.inner_p; end");
        // The nested proc sees the enclosing proc's temp during execution.
        AreEqual(2, conn.CreateCommand("exec dbo.outer_p").ExecuteScalar());
        // After the outer proc returns the session no longer sees it.
        var ex = Throws<DbException>(() => Exec(conn, "select * from #outer"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void DynamicSqlCreatedTemp_NotVisibleAfterExec()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "exec ('create table #e (a int); insert #e values (5)')");
        var ex = Throws<DbException>(() => Exec(conn, "select * from #e"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void TriggerCreatedTemp_NotVisibleAfterTriggerFires()
    {
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table dbo.t (id int)");
        Exec(conn, "create trigger dbo.tr on dbo.t after insert as begin create table #trg (a int); insert #trg values (1); end");
        Exec(conn, "insert dbo.t values (10)");
        var ex = Throws<DbException>(() => Exec(conn, "select * from #trg"));
        AreEqual("208", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void SessionCreatedTemp_PersistsAcrossStatements()
    {
        // The module-scoping change must not affect session-level temps.
        using var conn = new Simulation().CreateOpenConnection();
        Exec(conn, "create table #s (a int)");
        Exec(conn, "insert #s values (1), (2)");
        AreEqual(2, CountRows(conn, "#s"));
    }
}
