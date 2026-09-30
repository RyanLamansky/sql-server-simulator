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
    public void Success_SendsSev10CautionInfoMessage()
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
        // Severity 10 arrives as class 0 (probed 2026-09-23).
        AreEqual<byte>(0, error.Class);
        AreEqual<byte>(1, error.State);
        // Raised from line 801 of sp_rename itself (probed 2026-09-26).
        AreEqual(801, error.LineNumber);
        AreEqual("sp_rename", error.Procedure);
    }

    /// <summary>
    /// The NULL and <c>OBJECT</c> types rename any object in the schema
    /// namespace and any constraint, leaving a module's stored definition as
    /// it was (probed 2026-09-25 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AnyObjectOrConstraint_Renames()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (id int constraint pk_t primary key, v int constraint df_t default 0 constraint ck_t check (v >= 0))",
            "create sequence dbo.s",
            "create view dbo.v as select 1 as a",
            "create procedure dbo.p as select 7",
            "create function dbo.f() returns int as begin return 1 end",
            "create synonym dbo.syn for dbo.t",
            "create trigger dbo.tr on t after insert as print 'x'");
        foreach (var (from, to) in new[] { ("s", "s2"), ("v", "v2"), ("p", "p2"), ("f", "f2"), ("syn", "syn2"), ("tr", "tr2"), ("pk_t", "pk_t2"), ("df_t", "df_t2"), ("ck_t", "ck_t2") })
            _ = sim.ExecuteNonQuery($"exec sp_rename 'dbo.{from}', '{to}', 'OBJECT'");
        AreEqual(9, sim.ExecuteScalar("select count(*) from sys.objects where name in ('s2','v2','p2','f2','syn2','tr2','pk_t2','df_t2','ck_t2')"));
        AreEqual(7, sim.ExecuteScalar("exec p2"));
        AreEqual("create procedure dbo.p as select 7", sim.ExecuteScalar("select definition from sys.sql_modules where object_id = object_id('p2')"));
    }

    [TestMethod]
    public void ObjectType_NotFound_Raises15248()
        => new Simulation().AssertSqlError("exec sp_rename 'dbo.nosuch', 'x', 'OBJECT'", 15248, "Either the parameter @objname is ambiguous or the claimed @objtype (OBJECT) is wrong.");

    [TestMethod]
    public void Recompile_AnswersForAnObject()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        sim.AssertSqlError("exec sp_recompile 'dbo.nosuch'", 15165, "Could not find object 'dbo.nosuch' or you do not have permission.");
        using var connection = sim.CreateDbConnection();
        connection.Open();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);
        using var command = connection.CreateCommand();
        command.CommandText = "exec sp_recompile '[dbo].[t]'";
        _ = command.ExecuteNonQuery();
        CollectionAssert.AreEqual(new[] { "Object '[dbo].[t]' was successfully marked for recompilation." }, messages);
    }

    /// <summary>
    /// A new name differing from the old in case alone renames the column,
    /// index or table itself — Django's <c>RenameField</c> to a new casing
    /// (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CaseOnlyRename_RenamesItself()
        => AreEqual("FiElD IX_CASE R", new Simulation().ExecuteScalar("""
            create table r (id int primary key, field int, v int);
            create index ix_Case on r (v);
            exec sp_rename 'r.field', 'FiElD', 'COLUMN';
            exec sp_rename 'r.ix_Case', 'IX_CASE', 'INDEX';
            exec sp_rename 'r', 'R';
            select concat(
                (select name from sys.columns where object_id = object_id('r') and column_id = 2), ' ',
                (select name from sys.indexes where object_id = object_id('r') and index_id = 2), ' ',
                (select name from sys.tables where object_id = object_id('r')))
            """));

    /// <summary>
    /// Without <c>@objtype</c>, a table.leaf name renames the table's column,
    /// else its index, and the collision message names the inferred kind in
    /// lower case where a passed <c>@objtype</c> is echoed as written (probed
    /// 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void NullObjtype_TableLeaf_RenamesColumnOrIndex()
        => AreEqual("a2 i9", new Simulation().ExecuteScalar("""
            create table r (id int primary key, a int);
            create index i1 on r (a);
            exec sp_rename 'r.a', 'a2';
            exec sp_rename 'dbo.r.i1', 'i9';
            select concat(col_name(object_id('r'), 2), ' ', (select name from sys.indexes where object_id = object_id('r') and index_id = 2))
            """));

    [TestMethod]
    [DataRow("exec sp_rename 'r.a', 'b'", "column")]
    [DataRow("exec sp_rename 'r', 's', 'Object'", "Object")]
    public void Collision_NamesTheKindAsPassedOrInferred(string rename, string kind)
    {
        var ex = new Simulation().AssertSqlError("create table r (a int, b int); create table s (id int); " + rename, 15335);
        AreEqual($"Error: The new name '{(kind == "column" ? "b" : "s")}' is already in use as a {kind} name and would cause a duplicate that is not permitted.", ex.Errors[0].Message);
        AreEqual(738, ex.Errors[0].LineNumber);
    }

    /// <summary>
    /// A column a computed column or a CHECK constraint reads can't be renamed
    /// (Msg 15336 from line 774), while a DEFAULT, an index key and either end
    /// of a foreign key rename freely; a computed column itself is Msg 4928
    /// from line 905, after the caution (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    [TestMethod]
    [DataRow("r.pink")]
    [DataRow("r.w")]
    [DataRow("r.c")]
    [DataRow("dbo.r.t")]
    public void Column_ReadByComputedOrCheck_Raises15336(string column)
    {
        var ex = new Simulation().AssertSqlError(RenameDependencySetup + $"exec sp_rename '{column}', 'renamed', 'COLUMN'", 15336);
        AreEqual($"Object '{column}' cannot be renamed because the object participates in enforced dependencies.", ex.Errors[0].Message);
        AreEqual<byte>(16, ex.Errors[0].Class);
        AreEqual(774, ex.Errors[0].LineNumber);
        AreEqual("sp_rename", ex.Errors[0].Procedure);
    }

    [TestMethod]
    public void Column_DefaultIndexOrForeignKey_Renames()
        => AreEqual("id2 d2 k2 fkc2", new Simulation().ExecuteScalar(RenameDependencySetup + """
            exec sp_rename 'r.d', 'd2', 'COLUMN';
            exec sp_rename 'r.k', 'k2', 'COLUMN';
            exec sp_rename 'r.fkc', 'fkc2', 'COLUMN';
            exec sp_rename 'r.id', 'id2', 'COLUMN';
            select concat(col_name(object_id('r'), 1), ' ', col_name(object_id('r'), 6), ' ', col_name(object_id('r'), 7), ' ', col_name(object_id('r'), 8))
            """));

    [TestMethod]
    public void ComputedColumn_Raises4928AfterCaution()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(RenameDependencySetup);
        var ex = sim.AssertSqlError("exec sp_rename 'r.g', 'g2', 'COLUMN'", 4928);
        AreEqual("Cannot alter column 'g' because it is 'COMPUTED'.", ex.Errors[0].Message);
        AreEqual(905, ex.Errors[0].LineNumber);
        AreEqual("g", sim.ExecuteScalar("select col_name(object_id('r'), 4)"));
    }

    private const string RenameDependencySetup = """
        create table r (id int primary key, field int, pink int, g as pink + 1, c int check (c > 0), d int default 1, k int,
            fkc int references r (id), t int, u int, constraint ck2 check (t > u), w int, gw as w * 2 persisted);
        create index ix_k on r (k);

        """;
}
