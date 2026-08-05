using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the adaptive semi / anti-join switch
/// (<c>Parser/SemiJoinIndex.cs</c> + <c>Selection.SemiJoin.cs</c>): an
/// equi-correlated <c>EXISTS</c> / <c>[NOT] IN</c> whose outer side outgrows
/// <see cref="SemiJoinProbe.PerRowEvaluationsBeforeBuild"/> rows must stop
/// executing its inner plan per row and answer from the key set its
/// decorrelated plan built once, while every shape the transform can't preserve
/// must keep executing per row. Both halves are invisible to the correctness
/// suite — the answers are identical either way — so this reads the opt-in
/// <see cref="SemiJoinDiagnostics"/> trace and the
/// <see cref="SimulatedDbConnection.SubqueryPlanExecutions"/> counter, which
/// counts exactly the per-outer-row inner executions the switch removes.
/// </summary>
[TestClass]
public sealed class SemiJoinStrategyTests
{
    /// <summary>
    /// <paramref name="outerRows"/> outer rows against a 40-row inner, with the
    /// correlation key indexed on both sides — the shape whose per-row path is a
    /// seek, so the switch has to earn its build.
    /// </summary>
    private static SimulatedDbConnection OpenWithRows(int outerRows)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = $"""
            create table outer_rows (id int not null primary key, k int not null, v int null);
            create table inner_rows (k int not null, v int null, tag int not null);
            create index ix_inner_k on inner_rows (k);
            declare @i int = 1;
            while @i <= {outerRows} begin
                insert outer_rows values (@i, @i % 40, @i % 7);
                set @i += 1;
            end;
            set @i = 0;
            while @i < 40 begin
                insert inner_rows values (@i, @i % 7, @i);
                set @i += 1;
            end
            """;
        _ = setup.ExecuteNonQuery();
        return connection;
    }

    /// <summary>
    /// Runs <paramref name="sql"/> to completion, reporting the switch's own
    /// decisions plus how many times a subquery's inner plan ran per outer row.
    /// </summary>
    private static (List<string> Trace, long PerRowExecutions, int Rows) Run(SimulatedDbConnection connection, string sql)
    {
        var before = connection.SubqueryPlanExecutions;
        SemiJoinDiagnostics.Sink = [];
        try
        {
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

            return (SemiJoinDiagnostics.Sink, connection.SubqueryPlanExecutions - before, rows);
        }
        finally
        {
            SemiJoinDiagnostics.Sink = null;
        }
    }

    // ---- the switch engages ----

    [TestMethod]
    public void CorrelatedExists_OuterPastThreshold_BuildsOnceThenProbes()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, rows) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)");
        Contains("SemiJoin:Build(keys=1,groups=40)", trace);
        AreEqual(SemiJoinProbe.PerRowEvaluationsBeforeBuild, executions);
        AreEqual(400, rows);
    }

    [TestMethod]
    public void CorrelatedNotExists_OuterPastThreshold_Builds()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where not exists (select 1 from inner_rows i where i.k = o.k and i.tag > 900)");
        Contains("SemiJoin:Build(keys=1,groups=0)", trace);
        AreEqual(SemiJoinProbe.PerRowEvaluationsBeforeBuild, executions);
    }

    [TestMethod]
    public void CorrelatedIn_OuterPastThreshold_Builds()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where o.v in (select i.v from inner_rows i where i.k = o.k)");
        Contains("SemiJoin:Build(keys=1,groups=40)", trace);
        AreEqual(SemiJoinProbe.PerRowEvaluationsBeforeBuild, executions);
    }

    [TestMethod]
    public void CorrelatedExists_TwoCorrelationColumns_BuildsCompositeKey()
    {
        using var connection = OpenWithRows(400);
        var (trace, _, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k and i.v = o.v)");
        Contains("SemiJoin:Build(keys=2,groups=40)", trace);
    }

    [TestMethod]
    public void CorrelatedExists_InDeleteWhere_Builds()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "delete from outer_rows where exists (select 1 from inner_rows i where i.k = outer_rows.k and i.tag < 5)");
        Contains("SemiJoin:Build(keys=1,groups=5)", trace);
        AreEqual(SemiJoinProbe.PerRowEvaluationsBeforeBuild, executions);
    }

    [TestMethod]
    public void CorrelatedExists_InUpdateWhere_Builds()
    {
        using var connection = OpenWithRows(400);
        var (trace, _, _) = Run(connection,
            "update outer_rows set v = 0 where exists (select 1 from inner_rows i where i.k = outer_rows.k and i.tag < 5)");
        Contains("SemiJoin:Build(keys=1,groups=5)", trace);
    }

    // ---- the switch stays out of the way ----

    [TestMethod]
    public void CorrelatedExists_SmallOuter_StaysPerRow()
    {
        using var connection = OpenWithRows(100);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(100L, executions);
    }

    [TestMethod]
    public void CorrelatedExists_ThresholdRowExactly_StaysPerRow()
    {
        using var connection = OpenWithRows(SemiJoinProbe.PerRowEvaluationsBeforeBuild);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(SemiJoinProbe.PerRowEvaluationsBeforeBuild, executions);
    }

    [TestMethod]
    public void ResidualConjunctReadsOuter_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k and i.tag < o.id)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    [TestMethod]
    public void ProjectionReadsOuter_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where o.v in (select o.id from inner_rows i where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    [TestMethod]
    public void AggregateBody_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select count(*) from inner_rows i where i.k = o.k having count(*) > 1)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    [TestMethod]
    public void TopBody_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select top (1) 1 from inner_rows i where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    [TestMethod]
    public void DistinctBody_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where o.v in (select distinct i.v from inner_rows i where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    /// <summary>
    /// A body whose lock footprint is load-bearing keeps its per-row execution:
    /// one pass would <c>UPDLOCK</c> every row of the table for the rest of the
    /// transaction, where the correlated executions lock only what they matched.
    /// </summary>
    [TestMethod]
    public void LockHintedBody_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i with (updlock) where i.k = o.k)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    /// <summary>The same for a session whose isolation level makes every read tx-scoped.</summary>
    [TestMethod]
    public void RepeatableReadSession_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            """
            set transaction isolation level repeatable read;
            select id from outer_rows o where exists (select 1 from inner_rows i where i.k = o.k)
            """);
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    [TestMethod]
    public void NonEquiCorrelation_StaysPerRow()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            "select id from outer_rows o where exists (select 1 from inner_rows i where i.k < o.k)");
        IsEmpty(trace);
        AreEqual(400L, executions);
    }

    /// <summary>
    /// A correlation hidden inside a nested subquery is invisible to the
    /// parse-time classification — the key plan's own execution latches the
    /// consult and declines the site for the rest of the statement, with the
    /// row that triggered the build falling back to its own per-row execution.
    /// </summary>
    [TestMethod]
    public void NestedSubqueryReadsOuter_DeclinesAtBuild()
    {
        using var connection = OpenWithRows(400);
        var (trace, executions, _) = Run(connection,
            """
            select id from outer_rows o
            where exists (select 1 from inner_rows i
                          where i.k = o.k and i.tag < (select max(x.id) from outer_rows x where x.id = o.id))
            """);
        Contains("SemiJoin:Decline(correlated)", trace);
        IsGreaterThan(400L, executions);
    }

    // ---- the drive-side transform for a small uncorrelated IN subquery ----

    private static (List<string> Trace, int Rows) RunSeek(SimulatedDbConnection connection, string sql)
    {
        IndexSeekDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var rows = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows++;
            return (IndexSeekDiagnostics.Sink, rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void SmallUncorrelatedInSubquery_DrivesTheSeek()
    {
        using var connection = OpenWithRows(400);
        var (trace, rows) = RunSeek(connection,
            "select id from outer_rows o where o.id in (select i.tag from inner_rows i where i.tag < 3)");
        Contains("Seek(outer_rows)", trace);
        AreEqual(2, rows);
    }

    [TestMethod]
    public void UncorrelatedInSubqueryPastTheCap_KeepsTheScan()
    {
        using var connection = OpenWithRows(400);
        // 40 inner rows would fit the cap; the cross join widens the value set
        // to 80, and the whole family then declines.
        var (trace, _) = RunSeek(connection,
            "select id from outer_rows o where o.id in (select i.tag from inner_rows i cross join inner_rows j where j.tag < 2)");
        Contains("Scan(outer_rows)", trace);
    }

    [TestMethod]
    public void NotInSubquery_KeepsTheScan()
    {
        using var connection = OpenWithRows(400);
        var (trace, _) = RunSeek(connection,
            "select id from outer_rows o where o.id not in (select i.tag from inner_rows i where i.tag < 3)");
        Contains("Scan(outer_rows)", trace);
    }

    [TestMethod]
    public void CorrelatedInSubquery_KeepsTheScan()
    {
        using var connection = OpenWithRows(400);
        var (trace, _) = RunSeek(connection,
            "select id from outer_rows o where o.id in (select i.tag from inner_rows i where i.k = o.v)");
        Contains("Scan(outer_rows)", trace);
    }

    [TestMethod]
    public void UnindexedSubjectColumn_KeepsTheScan()
    {
        using var connection = OpenWithRows(400);
        var (trace, _) = RunSeek(connection,
            "select id from outer_rows o where o.v in (select i.tag from inner_rows i where i.tag < 3)");
        Contains("Scan(outer_rows)", trace);
    }
}
