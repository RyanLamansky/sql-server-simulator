using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A table variable whose column defaults to a sequence that doesn't exist as
/// the batch compiles ends that batch's compile without a message: nothing in
/// the batch runs and <c>@@ERROR</c> stays as it was. Called by <c>EXEC</c>,
/// the call just ends, unless a <c>TRY</c> around it or <c>XACT_ABORT</c> makes
/// it end the caller's whole batch with the client's own severe error.
/// All probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TableVariableSequenceDefaultTests
{
    private const string MissingDefault = "declare @t table (a int default next value for nosuch, b int);";

    private static Simulation WithLog()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table log (m varchar(10))");
        return simulation;
    }

    private static string Log(Simulation simulation) => (string)simulation.ExecuteScalar("select isnull(string_agg(m, ','), '') from log")!;

    [TestMethod]
    [DataRow("insert log values ('a'); create sequence s1 as int start with 1; declare @t table (a int default next value for s1, b int); insert log values ('b')", DisplayName = "A sequence the batch creates")]
    [DataRow("insert log values ('a'); " + MissingDefault + " insert log values ('b')", DisplayName = "A sequence never created")]
    [DataRow("insert log values ('a'); declare @t table (a int, b int default next value for dbo.nosuch)", DisplayName = "Schema-qualified")]
    [DataRow("insert log values ('a'); declare @t table (a int default next value for otherdb.dbo.nosuch)", DisplayName = "Database-qualified")]
    [DataRow("insert log values ('a'); if 1 = 0 begin " + MissingDefault + " end", DisplayName = "In an untaken branch")]
    [DataRow(MissingDefault + " select nosuchcol from sys.objects", DisplayName = "Ahead of a binder error")]
    [DataRow("select * from nosuch_table; " + MissingDefault, DisplayName = "Behind a deferred statement")]
    [DataRow("begin try " + MissingDefault + " end try begin catch insert log values ('c') end catch", DisplayName = "In the batch's own TRY")]
    public void Batch_EndsWithoutAMessage(string batch)
    {
        var simulation = WithLog();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("select 1/0").ExecuteNonQuery());

        _ = connection.CreateCommand(batch).ExecuteNonQuery();

        AreEqual(8134, connection.CreateCommand("select @@error").ExecuteScalar());
        AreEqual("", Log(simulation));
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('s1')"));
    }

    [TestMethod]
    public void BinderErrorAhead_IsTheBatchsReport()
        => _ = WithLog().AssertSqlError("select nosuchcol from sys.objects; " + MissingDefault, 207);

    [TestMethod]
    public void SyntaxErrorAnywhere_IsTheBatchsReport()
        => _ = WithLog().AssertSqlError(MissingDefault + " select from where", 156);

    [TestMethod]
    public void SequenceFromAnEarlierBatch_Draws()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create sequence s as int start with 7");
        AreEqual(7, simulation.ExecuteScalar("declare @t table (a int default next value for s, b int); insert @t (b) values (1); select a from @t"));
    }

    /// <summary>
    /// A sequence the batch drops after it compiled is looked for again only
    /// as the default draws: an insert naming the column passes, and the one
    /// drawing ends the batch with Msg 208 at state 211.
    /// </summary>
    [TestMethod]
    public void SequenceDroppedByTheBatch_IsMsg208AsTheDefaultDraws()
    {
        var simulation = WithLog();
        _ = simulation.ExecuteNonQuery("create sequence s");
        var error = simulation.AssertSqlError(
            "drop sequence s; declare @t table (a int default next value for s, b int); insert log values ('x'); insert @t (a, b) values (1, 1); insert log values ('y'); insert @t (b) values (1); insert log values ('z')",
            208);
        AreEqual((byte)211, error.State);
        AreEqual("x,y", Log(simulation));
    }

    [TestMethod]
    [DataRow("select next value for nosuch", DisplayName = "A query")]
    [DataRow("create table t (a int default next value for nosuch)", DisplayName = "A table's default")]
    [DataRow("create table #t (a int default next value for nosuch)", DisplayName = "A #temp table's default")]
    [DataRow("create table t (a int); alter table t add constraint df default next value for nosuch for a", DisplayName = "ALTER TABLE … ADD DEFAULT")]
    public void MissingSequenceElsewhere_IsMsg208AtState211(string ddl)
        => AreEqual((byte)211, new Simulation().AssertSqlError(ddl, 208).State);

    [TestMethod]
    public void DynamicBatch_EndsAloneAndTheCallerGoesOn()
    {
        var simulation = WithLog();
        using var connection = simulation.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("select 1/0").ExecuteNonQuery());

        AreEqual(8134, connection.CreateCommand($"exec('{MissingDefault}'); select @@error").ExecuteScalar());
        AreEqual(0, connection.CreateCommand($"declare @r int = 5; exec @r = sp_executesql N'{MissingDefault}'; select @r").ExecuteScalar());
        _ = connection.CreateCommand($"exec('insert log values (''a''); {MissingDefault}'); insert log values ('b')").ExecuteNonQuery();
        AreEqual("b", Log(simulation));
    }

    [TestMethod]
    [DataRow("begin try exec('" + MissingDefault + "'); insert log values ('t') end try begin catch insert log values ('c') end catch", DisplayName = "A TRY around the EXEC")]
    [DataRow("exec('begin try exec(''" + MissingDefault + "''); insert log values (''t'') end try begin catch insert log values (''c'') end catch'); insert log values ('o')", DisplayName = "A TRY in the dynamic batch")]
    [DataRow("begin try exec sp_executesql N'" + MissingDefault + "'; insert log values ('t') end try begin catch insert log values ('c') end catch", DisplayName = "A TRY around sp_executesql")]
    public void TryAroundTheCall_EndsTheCallersBatch_TheTransactionStanding(string batch)
    {
        var simulation = WithLog();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("begin tran").ExecuteNonQuery();

        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand(batch).ExecuteNonQuery());
        AreEqual(0, error.Number);
        AreEqual((byte)11, error.Class);
        AreEqual("A severe error occurred on the current command.  The results, if any, should be discarded.", error.Message);
        AreEqual("", Log(simulation));
        AreEqual((short)1, connection.CreateCommand("select xact_state()").ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    [DataRow("exec('" + MissingDefault + "'); insert log values ('o')", DisplayName = "Calling it")]
    [DataRow(MissingDefault, DisplayName = "The batch itself")]
    public void XactAbort_EndsTheBatch_RollingTheTransactionBack(string batch)
    {
        var simulation = WithLog();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("set xact_abort on; begin tran; insert log values ('w')").ExecuteNonQuery();

        AreEqual(0, Throws<SimulatedSqlException>(() => connection.CreateCommand(batch).ExecuteNonQuery()).Number);
        AreEqual(0, connection.CreateCommand("select @@trancount").ExecuteScalar());
        AreEqual("", Log(simulation));
    }

    [TestMethod]
    public void Procedure_IsNotCreated()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create procedure p as " + MissingDefault + " select 1");
        AreEqual(DBNull.Value, simulation.ExecuteScalar("select object_id('p')"));
    }

    /// <summary>
    /// A procedure whose sequence was dropped since its CREATE compiles again
    /// as it is called and ends that call: no result, no return status.
    /// </summary>
    [TestMethod]
    public void ProcedureWhoseSequenceIsGone_EndsTheCall()
    {
        var simulation = WithLog();
        simulation.ExecuteBatches(
            "create sequence s as int start with 1",
            "create procedure p as declare @t table (a int default next value for s, b int); insert log values ('p')",
            "drop sequence s");
        AreEqual(5, simulation.ExecuteScalar("declare @r int = 5; exec @r = p; select @r"));
        AreEqual("", Log(simulation));
    }
}
