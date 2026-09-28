using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A row-writing statement whose error a TRY frame catches reports a count
/// of 0, which SqlClient folds into <c>RecordsAffected</c>, where an uncaught
/// error leaves the statement uncounted (probed 2026-09-28 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class CaughtWriteCountTests
{
    private static int Measure(string setup, string measured)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(setup);
        return simulation.ExecuteNonQuery(measured);
    }

    [TestMethod]
    [DataRow("begin try insert t values (1); end try begin catch end catch")]
    [DataRow("begin try update t set id = 1 / 0; end try begin catch end catch")]
    [DataRow("begin try delete t where 1 / 0 = 1; end try begin catch end catch")]
    [DataRow("begin try insert t values ('x'); end try begin catch end catch")]
    [DataRow("begin try merge t using (select 1 id) s on 1 = 0 when not matched then insert values (s.id); end try begin catch end catch")]
    [DataRow("begin try select 1 / 0 x into #n; end try begin catch end catch")]
    [DataRow("begin try begin try insert t values (1); end try begin catch throw; end catch end try begin catch end catch")]
    [DataRow("begin try exec('insert t values (1)'); end try begin catch end catch")]
    public void CaughtWrite_CountsZero(string measured)
        => AreEqual(0, Measure("create table t (id int primary key); insert t values (1)", measured));

    [TestMethod]
    [DataRow("set nocount on; begin try insert t values (1); end try begin catch end catch")]
    [DataRow("begin try set nocount on; insert t values (1); end try begin catch set nocount off; end catch")]
    [DataRow("begin try declare @i int = 1 / 0; end try begin catch end catch")]
    [DataRow("begin try throw 50000, 'x', 1; end try begin catch end catch")]
    [DataRow("begin try create table t (id int); end try begin catch end catch")]
    public void OtherCaughtErrors_CountNothing(string measured)
        => AreEqual(-1, Measure("create table t (id int primary key); insert t values (1)", measured));

    [TestMethod]
    [Description("A trigger's failing UPDATE and the INSERT that fired it each report a zero count; one caught inside a procedure counts there.")]
    public void EachFailingWriteOnTheWayOut_ReportsItsOwn()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int);
            create table u (id int not null);
            insert u values (1);
            """);
        _ = simulation.ExecuteNonQuery("create trigger tr on t after insert as update u set id = null");
        using var reader = simulation.ExecuteReader("begin try insert t values (1); end try begin catch select 'c' x; end catch");
        IsTrue(reader.Read());
        AreEqual(0, reader.RecordsAffected);
    }
}
