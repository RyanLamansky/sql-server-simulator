using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET ANSI_NULL_DFLT_ON</c> / <c>ANSI_NULL_DFLT_OFF</c> and the database's
/// <c>ANSI_NULL_DEFAULT</c>: the nullability a <c>CREATE TABLE</c> column
/// stating neither <c>NULL</c> nor <c>NOT NULL</c> gets. Every expectation
/// probed 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class AnsiNullDefaultTests
{
    private const string Nullability = "select string_agg(concat(name, '=', cast(is_nullable as int)), ',') within group (order by column_id) from sys.columns where object_id = object_id('t')";

    [TestMethod]
    public void Default_ColumnsAreNullable()
        => AreEqual("a=1,b=1", new Simulation().ExecuteScalar($"create table t (a int, b varchar(5)); {Nullability}"));

    [TestMethod]
    public void DfltOff_UnstatedColumnsAreNotNull()
        => AreEqual("a=0,b=0,c=1,d=0,e=1", new Simulation().ExecuteScalar($"""
            set ansi_null_dflt_off on
            create table t (a int, b bit, c int null, d int not null, e as a + 1)
            {Nullability}
            """));

    [TestMethod]
    public void DfltOnOff_WithDatabaseOptionOff_NotNull()
        => AreEqual("a=0", new Simulation().ExecuteScalar($"set ansi_null_dflt_on off; create table t (a int); {Nullability}"));

    [TestMethod]
    public void BothOff_DatabaseOptionDecides()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set ansi_null_dflt_on off; alter database current set ansi_null_default on").ExecuteNonQuery();
        AreEqual("a=1", connection.CreateCommand($"create table t (a int); {Nullability}").ExecuteScalar());
    }

    [TestMethod]
    public void TempTable_FollowsTheSession()
        => IsFalse((bool)new Simulation().ExecuteScalar("set ansi_null_dflt_off on; create table #t (a int); select is_nullable from tempdb.sys.columns where object_id = object_id('tempdb..#t')")!);

    [TestMethod]
    [DataRow("declare @t table (a int); insert @t values (null); select count(*) from @t")]
    [DataRow("create table t (x int); alter table t add a int; insert t (x, a) values (1, null); select count(*) from t")]
    [DataRow("select cast(null as int) a into t where 1 = 0; insert t values (null); select count(*) from t")]
    public void OtherColumnSources_StayNullable(string batch)
        => AreEqual(1, new Simulation().ExecuteScalar("set ansi_null_dflt_off on; " + batch));

    [TestMethod]
    public void InsertingNull_IntoADefaultedColumn_RaisesMsg515()
        => _ = new Simulation().AssertSqlError("set ansi_null_dflt_off on; create table t (a int); insert t values (null)", 515);

    [TestMethod]
    public void SettingOneOn_TurnsTheOtherOff()
        => AreEqual("1024,2048,0,1024", new Simulation().ExecuteScalar("""
            declare @r varchar(40) = concat(@@options & 3072, ',')
            set ansi_null_dflt_off on
            set @r += concat(@@options & 3072, ',')
            set ansi_null_dflt_off off
            set @r += concat(@@options & 3072, ',')
            set ansi_null_dflt_on on
            select @r + concat(@@options & 3072, '')
            """));

    [TestMethod]
    public void DmExecSessions_ReportsDfltOn()
        => IsFalse((bool)new Simulation().ExecuteScalar("set ansi_null_dflt_off on; select ansi_null_dflt_on from sys.dm_exec_sessions where session_id = @@spid")!);

    [TestMethod]
    public void ProcedureSet_AppliesInsideAndReverts()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as begin set ansi_null_dflt_off on; create table t (a int); end");
        AreEqual("a=0 1024", sim.ExecuteScalar($"exec p; select concat(({Nullability}), ' ', @@options & 3072)"));
    }

    [TestMethod]
    public void AnsiDefaultsOff_TurnsDfltOnOff()
        => AreEqual("a=0", new Simulation().ExecuteScalar($"set ansi_defaults off; create table t (a int); {Nullability}"));

    /// <summary>
    /// An <c>ALTER DATABASE … ANSI_NULL_DEFAULT</c> doesn't reach a <c>CREATE
    /// TABLE</c> later in its own batch, which compiled under the old value;
    /// the next batch sees it (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void DatabaseOption_ChangedInTheBatch_ReachesOnlyLaterBatches()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set ansi_null_dflt_on off; alter database current set ansi_null_default on; create table t1 (a int)").ExecuteNonQuery();
        _ = connection.CreateCommand("create table t2 (a int)").ExecuteNonQuery();
        AreEqual("t1=0,t2=1", connection.CreateCommand("select string_agg(concat(object_name(object_id), '=', cast(is_nullable as int)), ',') within group (order by object_name(object_id)) from sys.columns where object_id in (object_id('t1'), object_id('t2'))").ExecuteScalar());
    }
}
