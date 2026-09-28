using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET NOEXEC</c> compiles each statement without running it;
/// <c>SET PARSEONLY</c> checks a batch's syntax and neither binds nor runs it.
/// Every expectation probed 2026-09-28 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NoExecParseOnlyTests
{
    private static DbConnection Seeded()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (a int primary key); insert t values (1)");
        return sim.CreateOpenConnection();
    }

    private static List<object> Results(DbConnection connection, string batch)
    {
        using var reader = connection.CreateCommand(batch).ExecuteReader();
        var values = new List<object>();
        do
        {
            while (reader.Read())
                values.Add(reader.GetValue(0));
        }
        while (reader.NextResult());
        return values;
    }

    [TestMethod]
    public void NoExec_StatementsAfterItDontRun_UntilItsTurnedOff()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new object[] { 2, 1 }, Results(connection, "set noexec on; select 1; insert t values (2); print 'p'; set noexec off; select 2; select count(*) from t"));
    }

    [TestMethod]
    public void NoExec_AcrossBatches()
    {
        using var connection = Seeded();
        IsEmpty(Results(connection, "set noexec on"));
        IsEmpty(Results(connection, "declare @a int = 1 / 0; select @a; raiserror('r', 16, 1); create table t2 (a int); use master"));
        IsEmpty(Results(connection, "select * from missing"));
        CollectionAssert.AreEqual(new object[] { "simulated", null! }, Results(connection, "set noexec off; select db_name(); select object_id('t2')").Select(v => v is DBNull ? null! : v).ToArray());
    }

    [TestMethod]
    public void NoExec_StillReportsCompileErrors()
    {
        using var connection = Seeded();
        _ = Results(connection, "set noexec on");
        AreEqual(207, Throws<SimulatedSqlException>(() => Results(connection, "select nosuch from t")).Number);
    }

    [TestMethod]
    public void NoExec_InAnUntakenBranch_DoesNothing()
    {
        using var connection = Seeded();
        CollectionAssert.AreEqual(new object[] { 1 }, Results(connection, "if 1 = 0 set noexec on; select 1"));
    }

    [TestMethod]
    public void NoExec_SetInAProcedure_RevertsOnReturn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure p as begin set noexec on; select 1; end");
        using var connection = sim.CreateOpenConnection();
        CollectionAssert.AreEqual(new object[] { 2 }, Results(connection, "exec p; select 2"));
    }

    [TestMethod]
    public void ParseOnly_GovernsItsWholeBatch_AndTheNextOnes()
    {
        using var connection = Seeded();
        IsEmpty(Results(connection, "select 1; set parseonly on; select 2"));
        IsEmpty(Results(connection, "select nosuch from missing; insert t values (5)"));
        AreEqual(156, Throws<SimulatedSqlException>(() => Results(connection, "select from")).Number);
        CollectionAssert.AreEqual(new object[] { 1, 5432 }, Results(connection, "set parseonly off; select count(*) from t; select @@options"));
    }

    [TestMethod]
    [DataRow("create procedure p as set parseonly on", "p")]
    [DataRow("-- lead\ncreate procedure p as\nselect 1\nset parseonly off", "p")]
    [DataRow("create function f() returns int as\nbegin\n  set parseonly on\n  return 1\nend", "f")]
    [Description("Real reports it at line 0 wherever the SET sits (probed 2026-09-28 against SQL Server 2025).")]
    public void ParseOnly_InAModule_RaisesMsg1059_AtLineZero(string sql, string procedure)
    {
        var error = new Simulation().AssertSqlError(sql, 1059);
        AreEqual(0, error.LineNumber);
        AreEqual(procedure, error.Procedure);
    }
}
