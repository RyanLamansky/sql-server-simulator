using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>XACT_STATE()</c> outside a user transaction reads 1 in a statement real
/// opens a transaction for — one naming a table-like source, a function or
/// sequence object, or one of the built-ins that open one — while
/// <c>@@TRANCOUNT</c> stays 0 (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class XactStateStatementTransactionTests
{
    private static Simulation Setup()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int); insert t values (1); create sequence s",
            "create view v as select a from t",
            "create function f() returns table as return select a from t",
            "create function g() returns int as begin return 1 end",
            "create function h0() returns int as begin return xact_state() end");
        return sim;
    }

    [TestMethod]
    [DataRow("select xact_state()", (short)0)]
    [DataRow("select xact_state() from t", (short)1)]
    [DataRow("select xact_state() from v", (short)1)]
    [DataRow("select xact_state() from f()", (short)1)]
    [DataRow("select xact_state() from sys.objects where name = 't'", (short)1)]
    [DataRow("select xact_state() from openjson('[1]')", (short)1)]
    [DataRow("with c as (select 1 a) select xact_state() from c", (short)1)]
    [DataRow("select xact_state() from (values (1)) x(a)", (short)0)]
    [DataRow("select xact_state() from (select 1 a) d", (short)0)]
    [DataRow("select xact_state(), (select count(*) from t)", (short)1)]
    [DataRow("select case when exists (select * from t) then xact_state() end", (short)1)]
    [DataRow("select xact_state(), dbo.g()", (short)1)]
    [DataRow("select cast(dbo.h0() as smallint)", (short)1)]
    [DataRow("select xact_state(), next value for s", (short)1)]
    [DataRow("select xact_state(), object_id('t')", (short)1)]
    [DataRow("select xact_state(), @@spid", (short)1)]
    [DataRow("select xact_state(), @@rowcount", (short)0)]
    [DataRow("select xact_state(), concat('a', 'b')", (short)1)]
    [DataRow("select xact_state(), iif(1 = 1, 1, 0)", (short)0)]
    [DataRow("select xact_state(), getdate()", (short)0)]
    [DataRow("select xact_state(), current_user", (short)1)]
    [DataRow("select xact_state(), hierarchyid::GetRoot()", (short)1)]
    [DataRow("select xact_state(), cast('<a>1</a>' as xml).value('(/a)[1]', 'int')", (short)1)]
    [DataRow("select xact_state(), cast('<a/>' as xml)", (short)0)]
    public void Statement(string query, short expected)
        => AreEqual(expected, Setup().ExecuteScalar(query));

    [TestMethod]
    public void TranCountStaysZero()
        => AreEqual(0, Setup().ExecuteScalar("select @@trancount from t"));

    [TestMethod]
    public void SetAndIfReadTheirOwnStatement()
        => AreEqual("1100", Setup().ExecuteScalar("""
            declare @r varchar(10) = '', @x int;
            set @x = (select xact_state() from t); set @r += str(@x, 1);
            if (select xact_state() from t) = 1 set @r += '1' else set @r += '0';
            if exists (select * from t) set @r += str(xact_state(), 1);
            if xact_state() = 1 set @r += '1' else set @r += '0';
            select @r
            """));

    [TestMethod]
    public void ClrTypedVariableOpensIt()
        => AreEqual((short)1, new Simulation().ExecuteScalar("declare @h hierarchyid = '/1/'; select xact_state() where @h is not null"));

    [TestMethod]
    public void CursorReadsTheStatementItsQueryCompiledIn()
        => AreEqual((short)1, Setup().ExecuteScalar("""
            declare c cursor for select xact_state() from t;
            declare @v smallint;
            open c; fetch c into @v; close c; deallocate c;
            select @v
            """));

    [TestMethod]
    public void CachedPlanReplayKeepsIt()
    {
        var sim = Setup();
        AreEqual((short)1, sim.ExecuteScalar("select xact_state() from t"));
        AreEqual((short)1, sim.ExecuteScalar("select xact_state() from t"));
    }

    [TestMethod]
    public void ProcedureBodyStatementsReadTheirOwn()
    {
        var sim = Setup();
        sim.ExecuteBatches("create procedure p as select xact_state()");
        AreEqual((short)0, sim.ExecuteScalar("exec p"));
    }
}
