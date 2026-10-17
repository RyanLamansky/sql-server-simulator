using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A CLR routine's pipe under <c>disallow results from triggers</c>: a result
/// set sent while a trigger fires is a <c>SqlException</c> carrying Msg 524
/// inside the routine, which ends the context transaction (probed 2026-10-17
/// against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class ClrTriggerResultsTests
{
    private const string Refusal = "A trigger returned a resultset and the server option 'disallow results from triggers' is true.";

    private static Simulation Disallowing()
        => ClrFrameworkFixture.Simulation(
            "exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'disallow results from triggers', 1; reconfigure",
            "create procedure dbo.rows @n int as external name simclr.Procs.Rows",
            "create table t (a int)",
            "create trigger tr on t after insert as external name simclr.Trig.Rows",
            "create table c (a int)",
            "create trigger trc on c after insert as external name simclr.Trig.RowsCaught",
            "create table p (a int)",
            "create trigger trp on p after insert as begin exec dbo.rows 1; print 'unreached' end");

    /// <summary>
    /// The routine throwing the refusal is Msg 6549 over its report at the
    /// trigger's line 1, which ends the batch and rolls the transaction back,
    /// or dooms it when a <c>TRY</c> catches it.
    /// </summary>
    [TestMethod]
    public void Uncaught_IsMsg6549_RollingBack()
    {
        var sim = Disallowing();
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran; insert t values (1); select 1").ExecuteNonQuery());
        AreEqual((6549, 1, "tr"), (error.Number, error.LineNumber, error.Procedure));
        StartsWith($"A .NET Framework error occurred during execution of user defined routine or aggregate 'tr': \r\nSystem.Data.SqlClient.SqlException: {Refusal}", error.Errors[0].Message);
        Contains("SqlPipe.Send(SqlDataRecord record)", error.Errors[0].Message);
        EndsWith(". User transaction, if any, will be rolled back.", error.Errors[0].Message);
        Contains("The statement has been terminated.", error.Message);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual("6549|-1|1", connection.CreateCommand("""
            begin tran;
            begin try insert t values (1) end try begin catch select concat(error_number(), '|', xact_state(), '|', @@trancount) end catch;
            rollback
            """).ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>
    /// A routine catching the refusal reads Msg 524 at class 16, state 1 and
    /// line 0, naming no procedure, with no result set begun; returning past
    /// it is Msg 3991, the context transaction having ended.
    /// </summary>
    [TestMethod]
    public void Caught_IsMsg3991OnReturn()
    {
        using var connection = Disallowing().CreateOpenConnection();
        var messages = new List<string>();
        ((SimulatedDbConnection)connection).InfoMessage += (_, e) => messages.AddRange(e.Errors.Cast<SimulatedError>().Where(entry => entry.Class == 0).Select(entry => entry.Message));
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert c values (1)").ExecuteNonQuery());
        AreEqual((3991, "trc"), (error.Number, error.Procedure));
        Contains("caught 524|16|1|0||False", string.Join('\n', messages) + error.Message);
        AreEqual(0, connection.CreateCommand("select count(*) from c").ExecuteScalar());
    }

    /// <summary>
    /// A CLR procedure a T-SQL trigger calls meets the same refusal, and its
    /// Msg 6549 ends the batch; called outside a trigger, it sends its rows.
    /// </summary>
    [TestMethod]
    public void ProcedureCalledByATrigger_IsRefusedToo()
    {
        var sim = Disallowing();
        var error = sim.AssertSqlError("insert p values (1)", 6549);
        Contains(Refusal, error.Errors[0].Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from p"));
        AreEqual(1, sim.ExecuteScalar("exec dbo.rows 1"));
    }

    /// <summary>
    /// <c>ExecuteAndSend</c>'s command meets the T-SQL rule: its Msg 524 at
    /// line 1, naming no procedure, reaches the client ahead of the routine's
    /// Msg 6549, which ends the batch.
    /// </summary>
    [TestMethod]
    public void ExecuteAndSend_IsMsg524ThenMsg6549()
    {
        var sim = Disallowing();
        _ = sim.ExecuteNonQuery("create table s (a int)");
        _ = sim.ExecuteNonQuery("create trigger ts on s after insert as external name simclr.Ctx.TrigSend");
        var error = sim.AssertSqlError("insert s values (1)", 524);
        AreEqual((1, ""), (error.Errors[0].LineNumber, error.Errors[0].Procedure));
        AreEqual("524,6549", string.Join(',', error.Errors.Cast<SimulatedError>().Where(entry => entry.Class > 10).Select(entry => entry.Number)));
        Contains("SqlPipe.ExecuteAndSend(SqlCommand command)", error.Errors[1].Message);
        AreEqual(0, sim.ExecuteScalar("select count(*) from s"));
    }

    /// <summary>With the option off, the trigger's row reaches the client.</summary>
    [TestMethod]
    public void OptionOff_SendsTheRow()
    {
        var sim = Disallowing();
        _ = sim.ExecuteNonQuery("exec sp_configure 'disallow results from triggers', 0; reconfigure");
        AreEqual(7, sim.ExecuteScalar("insert t values (1)"));
    }
}
