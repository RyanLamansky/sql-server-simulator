using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>COMMIT</c> in a DML trigger body, and <c>XACT_STATE()</c> in the unit
/// a body runs in (probed 2026-09-28 against SQL Server 2025). See
/// <c>docs/claude/triggers.md</c>.
/// </summary>
[TestClass]
public sealed class TriggerCommitTests
{
    private static Simulation With(string triggerBody)
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create table t (a int); create table log (k varchar(10), tc int, xs int)",
            $"create trigger tr on t after insert as begin {triggerBody} end");
        return simulation;
    }

    [TestMethod]
    public void AutoCommitUnit_CommitsAndEndsTheBatchWith3609()
    {
        var simulation = With("declare @t int = @@trancount, @x int = xact_state(); commit; insert log values ('before', @t, @x); insert log values ('after', @@trancount, xact_state());");
        var ex = simulation.AssertSqlError("insert t values (1); select 'not reached'", 3609);
        AreEqual(("", 1), (ex.Procedure, ex.Errors.Count));
        AreEqual("1|before:1:1,after:2:1", simulation.ExecuteScalar(
            "select concat((select count(*) from t), '|', (select string_agg(concat(k, ':', tc, ':', xs), ',') from log))"));
    }

    [TestMethod]
    public void UserTransaction_EndedByTheBody_Is3609()
    {
        var simulation = With("commit;");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("begin tran; insert t values (1); select 'not reached'");
        AreEqual(3609, Throws<SimulatedSqlException>(() => command.ExecuteNonQuery()).Number);
        AreEqual("1|0", simulation.ExecuteScalar("select concat((select count(*) from t), '|', @@trancount)"));
    }

    [TestMethod]
    public void NestedUserTransaction_StaysOpen()
    {
        var simulation = With("commit;");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("begin tran; begin tran; insert t values (1); select @@trancount");
        AreEqual(1, command.ExecuteScalar());
    }

    [TestMethod]
    public void CaughtInTry()
        => AreEqual("3609|0|0|1", With("commit;").ExecuteScalar(
            "begin try insert t values (1); end try begin catch select concat(error_number(), '|', xact_state(), '|', @@trancount, '|', (select count(*) from t)); end catch"));

    [TestMethod]
    public void ErrorAfterCommit_KeepsWhatWasCommitted()
    {
        var simulation = With("commit; insert log values ('x', 0, 0); declare @z int = 1 / 0;");
        _ = simulation.AssertSqlError("insert t values (1)", 8134);
        AreEqual("1|1", simulation.ExecuteScalar("select concat((select count(*) from t), '|', (select count(*) from log))"));
    }

    [TestMethod]
    [DataRow("create table log (x int, tc int); insert log values (xact_state(), @@trancount); select concat(x, '|', tc) from log", "1|2")]
    [DataRow("declare @l table (x int, tc int); insert @l values (xact_state(), @@trancount); select concat(x, '|', tc) from @l", "1|0")]
    [DataRow("select concat(xact_state(), '|', @@trancount)", "0|0")]
    public void XactState_InAnAutoCommitWrite(string batch, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(batch));
}
