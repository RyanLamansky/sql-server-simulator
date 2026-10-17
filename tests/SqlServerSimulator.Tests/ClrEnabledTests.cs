using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>clr enabled</c> set to 0 on a CLR-enabled simulation: every CLR kind is
/// still created, and running one is Msg 6263 (probed 2026-10-17 against SQL
/// Server 2025).
/// </summary>
[TestClass]
public sealed class ClrEnabledTests
{
    private const string Disabled = "Execution of user code in the .NET Framework is disabled. Enable \"clr enabled\" configuration option.";

    private static Simulation DisabledClr()
    {
        var sim = ClrFrameworkFixture.Simulation(
            "exec sp_configure 'clr enabled', 0; reconfigure",
            "create procedure dbo.hello as external name simclr.Procs.Hello",
            "create function dbo.lg(@n int) returns nvarchar(3) as external name simclr.Funcs.Long",
            "create function dbo.series(@c int) returns table (n int, label nvarchar(20)) as external name simclr.Tvfs.Series",
            "create aggregate dbo.concat (@v nvarchar(100), @sep nvarchar(5)) returns nvarchar(max) external name simclr.Concat",
            "create type dbo.Point external name simclr.Point",
            "create table t (id int, p dbo.Point)",
            "create table tt (a int)",
            "create trigger tr on tt after insert as external name simclr.Trig.Report",
            "create procedure dbo.callsfn as select dbo.lg(2) x");
        return sim;
    }

    /// <summary>
    /// A CLR procedure call fails as its own statement, ahead of its
    /// arguments, leaving a return code variable as it was.
    /// </summary>
    [TestMethod]
    public void Procedure_FailsAsItsOwnStatement()
    {
        var sim = DisabledClr();
        var error = sim.AssertSqlError("declare @rc int = 5;\nexec @rc = dbo.hello 1", 6263);
        AreEqual(Disabled, error.Errors[0].Message);
        AreEqual(2, error.LineNumber);
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table #r (v varchar(30))").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            declare @rc int = 5;
            exec @rc = dbo.hello;
            insert #r select concat(@@error, '|', @rc);
            begin try exec dbo.hello end try begin catch insert #r select concat(error_number(), '|', isnull(error_procedure(), 'null')) end catch
            """).ExecuteNonQuery());
        AreEqual("6263|5;6263|null", connection.CreateCommand("select string_agg(v, ';') from #r").ExecuteScalar());
    }

    /// <summary>
    /// A reference to a CLR function, table-valued function, aggregate or a
    /// user-defined type's code fails the batch as it compiles — a skipped
    /// branch's included — so nothing ahead of it runs.
    /// </summary>
    [TestMethod]
    [DataRow("select dbo.lg(1)")]
    [DataRow("if 1 = 0 select dbo.lg(1)")]
    [DataRow("select * from dbo.series(2)")]
    [DataRow("select dbo.concat(name, ',') from sys.objects where 1 = 0")]
    [DataRow("select p.ToString() from t")]
    [DataRow("select dbo.Point::Describe()")]
    [DataRow("select cast('3,4' as dbo.Point)")]
    [DataRow("select cast(p as nvarchar(20)) from t")]
    public void CompiledReference_FailsTheBatch(string statement)
    {
        var sim = DisabledClr();
        _ = sim.ExecuteNonQuery("create table tt_log (a int)");
        var error = sim.AssertSqlError($"insert tt_log values (1);\n{statement}", 6263);
        AreEqual(2, error.LineNumber);
        AreEqual(0, sim.ExecuteScalar("select count(*) from tt_log"));
    }

    /// <summary>
    /// A procedure whose body references a CLR function fails as the body
    /// compiles, attributed to the procedure, and its caller goes on; so does
    /// dynamic SQL's.
    /// </summary>
    [TestMethod]
    public void ModuleBody_FailsAsItCompiles_CallerGoesOn()
    {
        using var connection = DisabledClr().CreateOpenConnection();
        _ = connection.CreateCommand("create table #r (v varchar(30))").ExecuteNonQuery();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin try exec dbo.callsfn end try begin catch insert #r select concat(error_number(), '|', error_procedure()) end catch;
            exec ('select dbo.lg(1)');
            insert #r select @@error
            """).ExecuteNonQuery());
        AreEqual("6263|dbo.callsfn;6263", connection.CreateCommand("select string_agg(v, ';') from #r").ExecuteScalar());
    }

    /// <summary>
    /// A CLR trigger fails at the firing statement's line, naming no
    /// procedure, ending the batch and rolling the transaction back, or
    /// dooming it when a <c>TRY</c> catches it.
    /// </summary>
    [TestMethod]
    public void Trigger_DoomsTheTransaction()
    {
        using var connection = DisabledClr().CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("begin tran;\n\ninsert tt values (1);\nselect 1").ExecuteNonQuery());
        AreEqual(6263, error.Number);
        Contains("The statement has been terminated.", error.Message);
        AreEqual(3, error.LineNumber);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual("6263|-1|", connection.CreateCommand("""
            begin tran;
            begin try insert tt values (1) end try begin catch select concat(error_number(), '|', xact_state(), '|', error_procedure()) end catch;
            rollback
            """).ExecuteScalar());
        AreEqual(0, connection.CreateCommand("select count(*) from tt").ExecuteScalar());
    }

    /// <summary>
    /// What needs no CLR code still runs: reading a user-defined type's
    /// column, declaring a variable of it, converting it to binary, and
    /// creating every kind.
    /// </summary>
    [TestMethod]
    public void NoClrCode_StillRuns()
    {
        var sim = DisabledClr();
        AreEqual(0, sim.ExecuteScalar("declare @p dbo.Point; select count(*) from (select p, cast(p as varbinary(20)) b from t) x"));
        AreEqual(5, sim.ExecuteScalar("select count(*) from sys.objects where name in ('hello', 'lg', 'series', 'concat', 'tr')"));
    }

    /// <summary>
    /// An implicit conversion between a user-defined type and a string runs
    /// the type's code too, refused here as the statement runs where real
    /// refuses the batch as it compiles.
    /// </summary>
    [TestMethod]
    public void ImplicitConversion_FailsTheStatement()
    {
        var sim = DisabledClr();
        _ = sim.AssertSqlError("insert t values (1, '3,4')", 6263);
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>
    /// Turning the option back on runs the code again, a plan compiled while
    /// it was on refused once it is off.
    /// </summary>
    [TestMethod]
    public void ReconfigureBackOn_RunsAgain()
    {
        var sim = DisabledClr();
        _ = sim.ExecuteNonQuery("exec sp_configure 'clr enabled', 1; reconfigure");
        AreEqual("yyy", sim.ExecuteScalar("select dbo.lg(3)"));
        _ = sim.ExecuteNonQuery("exec sp_configure 'clr enabled', 0; reconfigure");
        _ = sim.AssertSqlError("select dbo.lg(3)", 6263);
    }
}
