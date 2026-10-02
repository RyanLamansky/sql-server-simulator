using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>INSERT … EXEC</c> — appending the result sets a
/// stored procedure or dynamic-SQL batch yields into an INSERT target. Covers
/// the dynamic-SQL and procedure forms, INTO + column-list reordering,
/// multi-result-set appending with the total <c>@@ROWCOUNT</c>, the zero-row
/// pure-DML case, table-variable targets, identity allocation parity with
/// INSERT…SELECT, and the rejection edges (column-count mismatch Msg 213,
/// nested INSERT…EXEC Msg 8164, OUTPUT clause Msg 483). Probed against SQL
/// Server 2025 (2026-07-14).
/// </summary>
[TestClass]
public sealed class InsertExecTests
{
    [TestMethod]
    public void DynamicSql_SingleResultSet()
        => AreEqual(42, new Simulation().ExecuteScalar(
            "create table #t (a int); insert #t exec('select 42'); select a from #t"));

    [TestMethod]
    public void Procedure_Target()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 7 union all select 8 end");
        AreEqual(15, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select sum(a) from #t"));
    }

    [TestMethod]
    public void Into_With_ColumnList_ReordersValues()
        => AreEqual(2010, new Simulation().ExecuteScalar(
            "create table #t (a int, b int); insert into #t (b, a) exec('select 10, 20'); select a * 100 + b from #t"));

    [TestMethod]
    public void MultipleResultSets_AppendAllRows()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 5; select 6 end");
        AreEqual(11, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select sum(a) from #t"));
    }

    [TestMethod]
    public void RowCount_Is_TotalAcrossResultSets()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin select 5; select 6 end");
        AreEqual(2, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select @@rowcount"));
    }

    [TestMethod]
    public void NoResultSet_InsertsZeroRows_Succeeds()
    {
        // A pure-DML procedure yields no tabular output; INSERT…EXEC leaves
        // the target empty and completes without error (probe-confirmed).
        var sim = new Simulation();
        sim.ExecuteBatches("create procedure dbo.p as begin declare @x int = 1 end");
        AreEqual(0, sim.ExecuteScalar(
            "create table #t (a int); insert #t exec dbo.p; select count(*) from #t"));
    }

    [TestMethod]
    public void TableVariable_Target()
        => AreEqual(3, new Simulation().ExecuteScalar(
            "declare @tv table (a int); insert @tv exec('select 1 union all select 2'); select sum(a) from @tv"));

    [TestMethod]
    public void Identity_AllocatesLikeInsertSelect()
        => AreEqual(2, new Simulation().ExecuteScalar(
            "create table #t (id int identity, a int); insert #t (a) exec('select 7 union all select 8'); select max(id) from #t"));

    [TestMethod]
    public void ColumnCountMismatch_MoreValues_Raises213()
        => new Simulation().AssertSqlError(
            "create table #t (a int); insert #t exec('select 1, 2')",
            213, "Column name or number of supplied values does not match table definition.");

    [TestMethod]
    public void ColumnCountMismatch_FewerValues_Raises213()
        => new Simulation().AssertSqlError(
            "create table #t (a int, b int); insert #t exec('select 1')",
            213, "Column name or number of supplied values does not match table definition.");

    [TestMethod]
    public void UncoercibleValue_SurfacesConversionError()
    {
        // The result-set rows flow through the same per-row coercion path as
        // INSERT…SELECT, so a value that won't convert to the target type
        // raises the simulator's usual conversion error (Msg 245).
        var ex = new Simulation().AssertSqlError(
            "create table #t (a int); insert #t exec('select ''notanint''')", 245);
        Contains("Conversion failed", ex.Message);
    }

    [TestMethod]
    public void Nested_InsertExec_Raises8164()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p_inner as begin select 99 end",
            "create procedure dbo.p_outer as begin create table #inner (a int); insert #inner exec dbo.p_inner; select a from #inner end");
        sim.AssertSqlError(
            "create table #t (a int); insert #t exec dbo.p_outer",
            8164, "An INSERT EXEC statement cannot be nested.");
    }

    [TestMethod]
    public void OutputClause_Raises483()
        => new Simulation().AssertSqlError(
            "create table #t (a int); insert #t output inserted.a exec('select 42')",
            483, "The OUTPUT clause cannot be used in an INSERT...EXEC statement.");

    /// <summary>
    /// Each SELECT the executed body runs meets the target columns' one-way
    /// assignment rule as it compiles, over no rows too, at its own line in
    /// the body; a constant NULL is exempt, and the error ends the INSERT but
    /// not the caller's batch (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AssignmentRule_JudgesEachResultSet()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (d decimal(10, 2))",
            "create procedure p as begin select 1.5; select getdate() where 1 = 0; end");
        var ex = sim.AssertSqlError("insert t exec p; select 1", 257);
        AreEqual("3 1 p", $"{ex.Errors[0].State} {ex.Errors[0].LineNumber} {ex.Errors[0].Procedure}");
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void AssignmentRule_ContinuesTheCallersBatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d date)");
        using var connection = sim.CreateOpenConnection();
        using var reader = connection.CreateCommand("""
            begin try insert t exec ('select 1'); end try begin catch select error_number(); end catch
            """).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(206, reader.GetInt32(0));
    }

    [TestMethod]
    [DataRow("select null")]
    [DataRow("select cast(null as datetime)")]
    public void AssignmentRule_ExemptsAConstantNull(string body)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (d decimal(10, 2))");
        _ = sim.ExecuteNonQuery($"insert t exec ('{body.Replace("'", "''", StringComparison.Ordinal)}')");
        AreEqual(1, sim.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>
    /// The executed body runs on past an error that ends only its statement,
    /// as any called batch does: its errors and later messages reach the
    /// client, every later result set is inserted, and the INSERT
    /// itself succeeds — <c>@@ROWCOUNT</c> counting every row, <c>@@ERROR</c>
    /// reading 0 (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void BodyError_RunsTheBodyOn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select 1/0; print 'body after'; select 2; raiserror('x', 16, 1); select 3 end");
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p; print concat(@@rowcount, ' ', @@error)").ExecuteNonQuery());
        CollectionAssert.AreEqual(
            new[] { "8134 Divide by zero error encountered.", "50000 x", "0 body after", "0 3 0" },
            ex.Errors.Select(e => $"{e.Number} {e.Message}").ToArray());
        AreEqual(6, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    /// <summary>
    /// A nested procedure's error continues the same way, its later rows
    /// inserted with the caller's.
    /// </summary>
    [TestMethod]
    public void NestedBodyError_RunsBothBodiesOn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure q as begin select 5; select 1/0; select 6 end",
            "create procedure p as begin select 1; exec q; select 2 end");
        using var connection = sim.CreateOpenConnection();
        _ = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p").ExecuteNonQuery());
        AreEqual("4 14", connection.CreateCommand("select concat(count(*), ' ', sum(a)) from t").ExecuteScalar());
    }

    /// <summary>
    /// An error that ends the executed batch — a missing object, from a
    /// procedure or dynamic SQL — still lets the INSERT write the rows
    /// returned before it, inside a transaction too; the INSERT then reports
    /// no count and leaves <c>@@ERROR</c> reading the error.
    /// </summary>
    [TestMethod]
    [DataRow("exec ('select 1; select * from nosuch; select 2')")]
    [DataRow("exec p")]
    public void BodyEndedByAMissingObject_InsertsWhatItReturned(string exec)
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select * from nosuch; select 2 end");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand($"begin tran; insert t {exec}; print concat(@@rowcount, ' ', @@error); commit");
        var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        CollectionAssert.AreEqual(new[] { "208", "0" }, ex.Errors.Select(e => e.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray());
        AreEqual("1 208", ex.Errors[1].Message);
        AreEqual(1, connection.CreateCommand("select sum(a) from t").ExecuteScalar());
    }

    /// <summary>
    /// A result set the target can't take ends the body and the INSERT with
    /// nothing written, rows of earlier result sets included, yet the INSERT
    /// doesn't fail: <c>@@ERROR</c> reads 0 after it, and the batch goes on.
    /// </summary>
    [TestMethod]
    public void AssignmentRule_WritesNothingAndLeavesAtAtErrorClear()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select newid(); select 2 end");
        using var connection = sim.CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() => connection.CreateCommand("insert t exec p; print concat(@@rowcount, ' ', @@error)").ExecuteNonQuery());
        AreEqual(206, ex.Number);
        AreEqual("0 0", ex.Errors[1].Message);
        AreEqual(0, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>
    /// Inside a <c>TRY</c> the body's first error is caught instead, and the
    /// INSERT writes nothing.
    /// </summary>
    [TestMethod]
    public void BodyErrorInsideTry_IsCaughtAndInsertsNothing()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create table t (a int)",
            "create procedure p as begin select 1; select 1/0; select 2 end");
        AreEqual("8134 0", sim.ExecuteScalar(
            "begin try insert t exec p end try begin catch select concat(error_number(), ' ', (select count(*) from t)) end catch"));
    }

    /// <summary>
    /// A nested INSERT … EXEC fails only its own statement in the executed
    /// body, which goes on (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void NestedInsertExec_EndsOnlyItsStatement()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (a int); create table u (a int)", "create procedure p as begin insert u exec('select 5'); select 1; end");
        AreEqual(8164, Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("insert t exec p")).Number);
        AreEqual("1|0", (string?)simulation.ExecuteScalar("select concat((select count(*) from t where a = 1), '|', (select count(*) from u))"));
    }

    [TestMethod]
    public void InsertExecWithOutput_EndsTheBatch()
    {
        var simulation = new Simulation();
        _ = Throws<SimulatedSqlException>(() => simulation.ExecuteNonQuery("create table t (a int); insert t output inserted.a exec ('select 1'); create table after_error (a int)"));
        AreEqual(0, simulation.ExecuteScalar<int>("select count(*) from sys.tables where name = 'after_error'"));
    }
}
