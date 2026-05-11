using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for local temp tables (<c>#foo</c>). Covers session-scoped storage,
/// cross-batch persistence within a connection, cross-connection isolation,
/// auto-drop at connection close, DROP TABLE semantics (regular + temp,
/// IF EXISTS, Msg 3701), three-part-name acceptance in DROP, identity /
/// SCOPE_IDENTITY across DML, transactional CREATE / DROP undo, and the
/// not-modeled <c>##</c> globals raising <see cref="NotSupportedException"/>.
/// Behavior probed against SQL Server 2025 (2026-05-11).
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
    public void GlobalTempTables_Unsupported()
    {
        using var conn = new Simulation().CreateOpenConnection();
        _ = Throws<NotSupportedException>(() => Exec(conn, "create table ##g (id int)"));
    }
}
