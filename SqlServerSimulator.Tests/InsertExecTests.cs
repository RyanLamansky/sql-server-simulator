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
}
