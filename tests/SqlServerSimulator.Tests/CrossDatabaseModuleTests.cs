using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A module reached through a three-part name or a synonym binds and runs in
/// the database that owns it — unqualified names, <c>DB_NAME()</c>, the catalog
/// and dynamic SQL read that one — while <c>@@ROWCOUNT</c>, the transaction and
/// <c>#temp</c> tables stay the session's. Probed 2026-09-28 against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class CrossDatabaseModuleTests
{
    /// <summary>
    /// <c>other.dbo.t</c> holds two rows and the session's own <c>t</c> none, so
    /// a count says which one a body read; <paramref name="modules"/> are
    /// created in <c>other</c>, one batch each.
    /// </summary>
    private static Simulation Fixture(params ReadOnlySpan<string> modules)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int); create database other");
        _ = simulation.ExecuteNonQuery("use other; create table t (a int); insert t values (1), (2)");
        simulation.ExecuteBatches(["use other", .. modules]);
        return simulation;
    }

    [TestMethod]
    public void Procedure_RunsInItsOwnDatabase()
        => AreEqual("other|2|p|1|simulated", Fixture("create procedure p as select concat(db_name(), '|', count(*), '|', object_name(@@procid), '|', case when @@procid = object_id('dbo.p') then 1 else 0 end) from t")
            .ExecuteScalar("""
                declare @r table (v varchar(50));
                insert @r exec other.dbo.p;
                select concat((select v from @r), '|', db_name())
                """));

    [TestMethod]
    public void View_ReadsItsOwnDatabase()
        => AreEqual("other|2|1", Fixture("create view v as select db_name() d, count(*) c, (select count(*) from sys.tables) n from t")
            .ExecuteScalar("select concat(d, '|', c, '|', n) from other.dbo.v"));

    [TestMethod]
    public void View_TheReferencingStatementStaysInTheSession()
        => AreEqual("simulated|other", Fixture("create view v as select db_name() d from t where a = 1")
            .ExecuteScalar("select concat(db_name(), '|', d) from other.dbo.v"));

    [TestMethod]
    public void ScalarFunction_RunsInItsOwnDatabase()
        => AreEqual("other|2|f", Fixture("create function f() returns varchar(50) as begin return concat(db_name(), '|', (select count(*) from t), '|', object_name(@@procid)) end")
            .ExecuteScalar("select other.dbo.f()"));

    [TestMethod]
    public void InlineFunction_RunsInItsOwnDatabase()
    {
        var simulation = Fixture("create function itf(@a int) returns table as return select db_name() d, count(*) c from t where a <= @a");
        _ = simulation.ExecuteNonQuery("insert t values (1), (2), (2)");
        AreEqual("other:1,other:2,other:2", simulation.ExecuteScalar("select string_agg(concat(x.d, ':', x.c), ',') within group (order by t.a) from t cross apply other.dbo.itf(t.a) x"));
    }

    [TestMethod]
    public void MultiStatementFunction_RunsInItsOwnDatabase()
        => AreEqual("other|2|mtf", Fixture("create function mtf() returns @r table (d sysname, c int, n sysname) as begin insert @r select db_name(), count(*), object_name(@@procid) from t; return end")
            .ExecuteScalar("select concat(d, '|', c, '|', n) from other.dbo.mtf()"));

    [TestMethod]
    public void Synonym_ReachesTheModulesDatabase()
    {
        var simulation = Fixture("create procedure p as select count(*) from t", "create view v as select count(*) c from t");
        _ = simulation.ExecuteNonQuery("create synonym sp for other.dbo.p; create synonym sv for other.dbo.v");
        AreEqual(2, simulation.ExecuteScalar("exec sp"));
        AreEqual(2, simulation.ExecuteScalar("select c from sv"));
    }

    [TestMethod]
    public void Procedure_SeesTheSessionsTempTablesAndTransaction()
    {
        var simulation = Fixture("create procedure p as insert t select a from #tmp");
        AreEqual("3|3|1|2", simulation.ExecuteScalar("""
            create table #tmp (a int); insert #tmp values (1), (2), (3);
            begin tran;
            exec other.dbo.p;
            declare @rc int = @@rowcount;
            declare @inside int = (select count(*) from other.dbo.t);
            declare @tc int = @@trancount;
            rollback;
            select concat(@rc, '|', @inside - 2, '|', @tc, '|', (select count(*) from other.dbo.t))
            """));
    }

    [TestMethod]
    public void Procedure_DynamicSqlAndUnqualifiedNamesUseItsDatabase()
    {
        var simulation = Fixture(
            "create function dbo.g() returns int as begin return (select count(*) from t) end",
            "create procedure inner_p as select db_name()",
            "create procedure p as begin exec inner_p; exec ('select count(*) from t'); select dbo.g(); create table made (x int); end");
        using var reader = simulation.ExecuteReader("exec other.dbo.p");
        IsTrue(reader.Read());
        AreEqual("other", reader.GetString(0));
        IsTrue(reader.NextResult() && reader.Read());
        AreEqual(2, reader.GetInt32(0));
        IsTrue(reader.NextResult() && reader.Read());
        AreEqual(2, reader.GetInt32(0));
        reader.Close();
        AreEqual("0|1", simulation.ExecuteScalar("select concat(count(object_id('dbo.made')), '|', count(object_id('other.dbo.made')))"));
    }

    [TestMethod]
    public void Trigger_ProcIdNamesIt()
        => AreEqual("other|tr", Fixture(
                "create table lg (d sysname, n sysname null)",
                "create trigger tr on t after insert as insert lg select db_name(), object_name(@@procid)")
            .ExecuteScalar("insert other.dbo.t values (3); select concat(d, '|', n) from other.dbo.lg"));

    /// <summary>
    /// A cross-database write runs the target's CHECK function and sequence in
    /// their database, while a DEFAULT reading <c>DB_NAME()</c> reads the
    /// session's.
    /// </summary>
    [TestMethod]
    public void CrossDatabaseWrite_ConstraintsBindInTheTargetsDatabase()
    {
        var simulation = Fixture(
            "create sequence s start with 100; create table lk (a int); insert lk values (7)",
            "create function dbo.chk(@v int) returns int as begin return (select count(*) from lk where a = @v) end",
            "create table w (id int default next value for dbo.s, a int check (dbo.chk(a) = 1), d sysname default db_name())");
        _ = simulation.ExecuteNonQuery("create sequence s start with 500; create table lk (a int)");
        AreEqual("100|7|simulated", simulation.ExecuteScalar("insert other.dbo.w (a) values (7); select concat(id, '|', a, '|', d) from other.dbo.w"));
        _ = simulation.AssertSqlError("update other.dbo.w set a = 8", 547);
    }

    [TestMethod]
    public void WriteThroughView_SetExpressionReadsTheSession()
        => AreEqual("3:simulated", Fixture("create table t2 (a int, d sysname null)", "create view v as select a, d from t2")
            .ExecuteScalar("insert other.dbo.v (a) values (3); update other.dbo.v set d = db_name() where a = 3; select concat(a, ':', d) from other.dbo.v"));

    [TestMethod]
    public void LocalViewOverOtherDatabasesView_ReadsThere()
    {
        var simulation = Fixture("create view v as select db_name() d, count(*) c from t");
        simulation.ExecuteBatches("create view lv as select * from other.dbo.v", "create procedure lp as select concat(d, '|', c) from lv");
        AreEqual("other|2", simulation.ExecuteScalar("exec lp"));
    }

    [TestMethod]
    public void ProcedureError_IsAttributedAsTheCallSpellsIt()
        => AreEqual("other.dbo.p|3|other", Fixture("""
                create procedure p as
                begin try
                  declare @x int = 1 / 0;
                end try
                begin catch
                  select concat(error_procedure(), '|', error_line(), '|', db_name());
                end catch
                """)
            .ExecuteScalar("exec other.dbo.p"));
}
