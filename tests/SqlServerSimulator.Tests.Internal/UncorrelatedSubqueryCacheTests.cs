using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// How many times a subquery's inner plan actually runs. A plan that never
/// reads the enclosing row runs once for the whole statement and every later
/// outer row reads the memoized result; a correlated one — or one holding a
/// built-in that draws a fresh value per call — keeps running per row. The
/// internal <see cref="SimulatedDbConnection.SubqueryPlanExecutions"/> counter
/// is what makes the distinction assertable rather than inferred from timings;
/// the answers those plans produce are covered by
/// <c>SqlServerSimulator.Tests.UncorrelatedSubqueryTests</c>.
/// </summary>
[TestClass]
public sealed class UncorrelatedSubqueryCacheTests
{
    /// <summary>Eight outer rows, four inner values — so "once" and "per row" are far apart.</summary>
    private static SimulatedDbConnection OpenWithRows()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = """
            create table outer_rows (id int not null primary key, k int not null);
            create table inner_rows (v int not null);
            insert outer_rows values (1, 10), (2, 20), (3, 30), (4, 40), (5, 50), (6, 60), (7, 70), (8, 80);
            insert inner_rows values (20), (40), (60), (80);
            """;
        _ = setup.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> to completion — every result set, every row,
    /// so nothing is left unexecuted behind a paused iterator — and reports
    /// (rows returned, inner-plan executions).
    /// </summary>
    private static (int Rows, long Executions) Measure(SimulatedDbConnection connection, string sql)
    {
        var before = connection.SubqueryPlanExecutions;
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = 0;
        using (var reader = command.ExecuteReader())
        {
            do
            {
                while (reader.Read())
                    rows++;
            }
            while (reader.NextResult());
        }

        return (rows, connection.SubqueryPlanExecutions - before);
    }

    [TestMethod]
    public void ScalarSubqueryInWhere_Uncorrelated_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((7, 1L), Measure(connection, "select id from outer_rows o where o.k >= (select min(v) from inner_rows)"));
    }

    [TestMethod]
    public void ScalarSubqueryInWhere_Correlated_RunsPerRow()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 8L), Measure(connection, "select id from outer_rows o where 1 = (select count(*) from inner_rows i where i.v = o.k)"));
    }

    [TestMethod]
    public void ScalarSubqueryInSelectList_Uncorrelated_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((8, 1L), Measure(connection, "select (select max(v) from inner_rows) from outer_rows"));
    }

    [TestMethod]
    public void Exists_Uncorrelated_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((8, 1L), Measure(connection, "select id from outer_rows where exists (select 1 from inner_rows where v = 40)"));
    }

    [TestMethod]
    public void Exists_Correlated_RunsPerRow()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 8L), Measure(connection, "select id from outer_rows o where exists (select 1 from inner_rows i where i.v = o.k)"));
    }

    [TestMethod]
    public void InSubquery_Uncorrelated_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 1L), Measure(connection, "select id from outer_rows o where o.k in (select v from inner_rows)"));
        AreEqual((4, 1L), Measure(connection, "select id from outer_rows o where o.k not in (select v from inner_rows)"));
    }

    [TestMethod]
    public void InSubquery_Correlated_RunsPerRow()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 8L), Measure(connection, "select id from outer_rows o where o.k in (select v from inner_rows i where i.v = o.k)"));
    }

    [TestMethod]
    public void QuantifiedComparison_Uncorrelated_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((1, 1L), Measure(connection, "select id from outer_rows o where o.k >= all (select v from inner_rows)"));
    }

    [TestMethod]
    public void QuantifiedComparison_Correlated_RunsPerRow()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 8L), Measure(connection, "select id from outer_rows o where o.k = any (select v from inner_rows i where i.v = o.k)"));
    }

    /// <summary>
    /// Two sites in one statement are memoized independently, so the
    /// uncorrelated half still runs once while the correlated half runs per
    /// surviving row — four of the eight, since the first conjunct filters
    /// before the second is asked.
    /// </summary>
    [TestMethod]
    public void MixedSites_MemoizeIndependently()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 5L), Measure(
            connection,
            """
            select id from outer_rows o
            where o.k in (select v from inner_rows)
              and exists (select 1 from inner_rows i where i.v = o.k)
            """));
    }

    /// <summary>
    /// Real re-draws a <c>NEWID()</c> inside an uncorrelated subquery per outer
    /// row (probe-confirmed against SQL Server 2025), so the plan declines the
    /// memo and keeps executing.
    /// </summary>
    [TestMethod]
    public void NewIdInside_RunsPerRow()
    {
        using var connection = OpenWithRows();
        AreEqual((8, 8L), Measure(connection, "select (select top 1 newid() from inner_rows) from outer_rows"));
    }

    /// <summary>
    /// <c>RAND()</c> is already frozen for the statement, so it needs no gate
    /// and the plan is memoized like any other outer-independent one.
    /// </summary>
    [TestMethod]
    public void RandInside_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((8, 1L), Measure(connection, "select (select top 1 rand() from inner_rows) from outer_rows"));
    }

    /// <summary>
    /// The memo is per statement, so the second of two identical statements in
    /// one batch runs its inner plan again rather than reading the first's
    /// result — which is what lets a statement observe writes the previous one
    /// made.
    /// </summary>
    [TestMethod]
    public void SecondStatementInBatch_RunsAgain()
    {
        using var connection = OpenWithRows();
        var (rows, executions) = Measure(
            connection,
            """
            select id from outer_rows o where o.k >= (select max(v) from inner_rows);
            select id from outer_rows o where o.k >= (select max(v) from inner_rows);
            """);
        AreEqual(2, rows);
        AreEqual(2L, executions);
    }

    /// <summary>
    /// A subquery under an APPLY that re-runs per outer row is still memoized
    /// once for the statement when it reads neither scope — the memo's scope is
    /// the statement, not the lateral invocation.
    /// </summary>
    [TestMethod]
    public void UnderLateralApply_RunsOnce()
    {
        using var connection = OpenWithRows();
        AreEqual((4, 1L), Measure(
            connection,
            "select a.k2 from outer_rows o cross apply (select o.k as k2 where o.k in (select v from inner_rows)) a"));
    }
}
