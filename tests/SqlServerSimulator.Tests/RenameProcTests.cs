using System.Data;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Public-EXEC tests for <c>sp_rename</c> (table / column / index rename).
/// Behavior + message wording / numbers / severity probe-confirmed against
/// SQL Server 2025 (2026-07-23): success buffers the sev-10 Msg 15477
/// "Caution" info message; a missing table → Msg 15225 (with <c>@itemtype</c>
/// rendered <c>(null)</c>); a missing column / index → Msg 15248; a colliding
/// new name → Msg 15335 (kind = COLUMN / INDEX / the ungrammatical
/// <c>object</c>). All raised errors are attributed to <c>sp_rename</c>.
/// </summary>
[TestClass]
public sealed class RenameProcTests
{
    [TestMethod]
    public void Column_Rename_NewNameQueryable_OldNameGone()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, oldcol int);
            insert t values (1, 42);
            exec sp_rename 'dbo.t.oldcol', 'newcol', 'COLUMN'
            """);
        AreEqual(42, sim.ExecuteScalar("select newcol from t"));
        // The old name no longer binds — Msg 207 invalid column name.
        _ = sim.AssertSqlError("select oldcol from t", 207);
        // sys.columns reflects the new name.
        AreEqual("newcol", sim.ExecuteScalar(
            "select name from sys.columns where object_id = object_id('dbo.t') and name = 'newcol'"));
    }

    [TestMethod]
    public void Column_Rename_TwoPartName_ResolvesSchemaToDbo()
        => AreEqual(7, new Simulation().ExecuteScalar("""
            create table t (a int);
            insert t values (7);
            exec sp_rename 't.a', 'b', 'COLUMN';
            select b from t
            """));

    [TestMethod]
    public void Rename_BareIdentifierNewName_TreatedAsString()
    {
        // Alembic / SSMS emit the new name as a bare (unquoted) identifier —
        // `exec sp_rename 'dbo.t.oldcol', newcol, 'COLUMN'` — which SQL Server
        // treats as a string constant of the identifier's verbatim text
        // (case preserved). The EXEC argument parser must accept it, not raise
        // Msg 102. Both the column-rename and table-rename forms use it.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, oldcol int);
            insert t values (1, 42);
            exec sp_rename 'dbo.t.oldcol', HeadLine, 'COLUMN';
            exec sp_rename 'dbo.t', tbl2
            """);
        // Verbatim case is preserved on the renamed column.
        AreEqual(42, sim.ExecuteScalar("select HeadLine from tbl2"));
        AreEqual("HeadLine", sim.ExecuteScalar(
            "select name from sys.columns where object_id = object_id('dbo.tbl2') and name = 'HeadLine'"));
    }

    [TestMethod]
    public void Column_Rename_ObjtypeIsCaseInsensitive()
        => AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (a int);
            insert t values (3);
            exec sp_rename 'dbo.t.a', 'b', 'column';
            select b from t
            """));

    [TestMethod]
    public void Table_Rename_NewNameSelectable_OldNameGone()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table oldtab (a int);
            insert oldtab values (9);
            exec sp_rename 'dbo.oldtab', 'newtab'
            """);
        AreEqual(9, sim.ExecuteScalar("select a from newtab"));
        // Old name no longer resolves — Msg 208 invalid object name.
        _ = sim.AssertSqlError("select a from oldtab", 208);
        // OBJECT_ID / sys.tables reflect the new name.
        AreEqual("newtab", sim.ExecuteScalar("select name from sys.tables where name = 'newtab'"));
        AreEqual(1, sim.ExecuteScalar("select case when object_id('dbo.newtab') is not null then 1 else 0 end"));
    }

    [TestMethod]
    public void Table_Rename_NamedArguments()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create table t1 (a int);
            insert t1 values (5);
            exec sp_rename @objname = 'dbo.t1', @newname = 't2';
            select a from t2
            """));

    [TestMethod]
    public void Index_Rename_ReflectedInSysIndexes()
        => AreEqual("ix_new", new Simulation().ExecuteScalar("""
            create table t (a int, b int);
            create index ix_old on t (a);
            exec sp_rename 'dbo.t.ix_old', 'ix_new', 'INDEX';
            select name from sys.indexes where object_id = object_id('dbo.t') and name = 'ix_new'
            """));

    [TestMethod]
    public void Table_NotFound_Raises15225_NamingObjectDbAndItemtype()
    {
        var ex = new Simulation().AssertSqlError("exec sp_rename 'dbo.nosuch', 'x'", 15225);
        Assert.Contains("dbo.nosuch", ex.Message);
        Assert.Contains("(null)", ex.Message);
        Assert.Contains("could be found in the current database", ex.Message);
        AreEqual("sp_rename", ex.Procedure);
    }

    [TestMethod]
    public void Column_NotFound_Raises15248()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (a int);
            exec sp_rename 'dbo.t.nocol', 'x', 'COLUMN'
            """, 15248);
        Assert.Contains("@objtype (COLUMN) is wrong", ex.Message);
        AreEqual("sp_rename", ex.Procedure);
    }

    [TestMethod]
    public void Index_NotFound_Raises15248()
        => Assert.Contains("@objtype (INDEX) is wrong", new Simulation().AssertSqlError("""
            create table t (a int);
            exec sp_rename 'dbo.t.noix', 'x', 'INDEX'
            """, 15248).Message);

    [TestMethod]
    public void Column_NameCollision_Raises15335()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t (a int, b int);
            exec sp_rename 'dbo.t.a', 'b', 'COLUMN'
            """, 15335);
        AreEqual("Error: The new name 'b' is already in use as a COLUMN name and would cause a duplicate that is not permitted.", ex.Message);
        AreEqual("sp_rename", ex.Procedure);
    }

    [TestMethod]
    public void Table_NameCollision_Raises15335_AsObject()
    {
        var ex = new Simulation().AssertSqlError("""
            create table t1 (a int);
            create table t2 (a int);
            exec sp_rename 'dbo.t1', 't2'
            """, 15335);
        // "a object" — the ungrammatical wording is matched verbatim against real.
        AreEqual("Error: The new name 't2' is already in use as a object name and would cause a duplicate that is not permitted.", ex.Message);
    }

    [TestMethod]
    public void Index_NameCollision_Raises15335()
        => Assert.Contains("already in use as a INDEX name", new Simulation().AssertSqlError("""
            create table t (a int, b int);
            create index ix1 on t (a);
            create index ix2 on t (b);
            exec sp_rename 'dbo.t.ix1', 'ix2', 'INDEX'
            """, 15335).Message);

    [TestMethod]
    public void UnmodeledObjtype_RaisesNotSupported()
        => Assert.Contains("USERDATATYPE", Throws<NotSupportedException>(
            () => new Simulation().ExecuteNonQuery("exec sp_rename 'dbo.foo', 'bar', 'USERDATATYPE'")).Message);

    [TestMethod]
    public void Rename_ViaStoredProcedureCommandType_MutatesCatalog()
    {
        // CommandType.StoredProcedure is the exact path the TDS RPC-by-name
        // dispatch (ExecuteProcedureRpcAsync) drives, so this covers the
        // non-EXEC entry point.
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "create table t (a int); insert t values (11)";
            _ = setup.ExecuteNonQuery();
        }
        using (var rename = conn.CreateCommand())
        {
            rename.CommandType = CommandType.StoredProcedure;
            rename.CommandText = "sp_rename";
            AddStringParam(rename, "@objname", "dbo.t.a");
            AddStringParam(rename, "@newname", "b");
            AddStringParam(rename, "@objtype", "COLUMN");
            _ = rename.ExecuteNonQuery();
        }
        using var query = conn.CreateCommand();
        query.CommandText = "select b from t";
        AreEqual(11, query.ExecuteScalar());
    }

    private static void AddStringParam(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = DbType.String;
        parameter.Value = value;
        _ = command.Parameters.Add(parameter);
    }

    [TestMethod]
    public void Success_BuffersSev10CautionInfoMessage()
    {
        var conn = new Simulation().CreateDbConnection();
        conn.Open();
        var captured = new List<SimulatedInfoMessageEventArgs>();
        conn.InfoMessage += (_, e) => captured.Add(e);

        using (var setup = conn.CreateCommand())
        {
            setup.CommandText = "create table t (a int)";
            _ = setup.ExecuteNonQuery();
        }
        using (var rename = conn.CreateCommand())
        {
            rename.CommandText = "exec sp_rename 'dbo.t.a', 'b', 'COLUMN'";
            _ = rename.ExecuteNonQuery();
        }

        HasCount(1, captured);
        AreEqual("Caution: Changing any part of an object name could break scripts and stored procedures.", captured[0].Message);
        var error = captured[0].Errors[0];
        AreEqual(15477, error.Number);
        AreEqual<byte>(10, error.Class);
        AreEqual<byte>(1, error.State);
    }
}
