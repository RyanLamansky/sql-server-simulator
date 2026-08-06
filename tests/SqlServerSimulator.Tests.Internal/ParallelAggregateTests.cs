using System.Data.Common;
using System.Globalization;
using System.Text;
using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Contract guard for the parallel grouped accumulation
/// (<c>Selection.Execution.AggregateParallel.cs</c>). Three things are asserted,
/// none of which the correctness suite reaches on its own:
/// <list type="number">
/// <item>the merged answer equals the serial answer, value for value, over the
/// same rows — every admitted aggregate kind and the boundary shapes;</item>
/// <item>an error raised on a worker's row surfaces as the error the serial scan
/// order would have raised, because the attempt is discarded and re-run;</item>
/// <item>each engagement gate actually declines, read off the opt-in
/// <see cref="AggregateDiagnostics"/> trace at the decision point rather than
/// inferred from a timing or an answer that would have been right either
/// way.</item>
/// </list>
/// The fixture is one table wider than <c>ParallelRowThreshold</c>, built once:
/// below the threshold nothing forks, so a smaller table would assert nothing.
/// </summary>
/// <remarks>
/// Serial by class: the fork draws on a process-wide worker budget, so two of
/// these tests running at once would starve each other of it and the engagement
/// assertions would report a decline that is a scheduling accident rather than
/// a gate.
/// </remarks>
[TestClass]
[DoNotParallelize]
public sealed class ParallelAggregateTests
{
    /// <summary>
    /// Comfortably past the 16,384-row engagement threshold, so the fork
    /// happens and most of the rows go through it.
    /// </summary>
    private const int Rows = 60_000;

    private static Simulation simulation = null!;

    /// <summary>
    /// One shared read-only fixture: 60k rows whose columns cover the merge's
    /// interesting cases — a low-cardinality group key (so the group-density
    /// gate admits it), a NULL-bearing column, a repeated string under the
    /// default case-insensitive collation, and both an integer and a decimal
    /// measure. Every test here reads and none writes, so sharing is safe under
    /// the suite's method-level parallelism.
    /// </summary>
    [ClassInitialize]
    public static void CreateFixture(TestContext context)
    {
        _ = context;
        simulation = new Simulation();
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        Execute(connection, """
            create table t (
                id int not null,
                grp int not null,
                measure int null,
                price decimal(12, 2) null,
                amount money null,
                label varchar(20) null,
                whenOn date not null)
            """);

        // grp: 12 buckets, so the group-density gate admits the grouped shapes.
        // measure: NULL on every 7th row. label: the same eight words, cased
        // differently on alternate rows, so a case-insensitive MIN / MAX meets
        // a tie between values that render differently.
        Execute(connection, $"""
            insert t (id, grp, measure, price, amount, label, whenOn)
            select value,
                   value % 12,
                   case when value % 7 = 0 then null else value % 997 end,
                   cast(value % 5000 as decimal(12, 2)) + cast(value % 100 as decimal(12, 2)) / 100,
                   cast(value % 313 as money),
                   case when value % 3 = 0
                        then upper(substring('alpha  bravo  charliedelta  echo   foxtrot golf   hotel  ', ((value % 8) * 7) + 1, 7))
                        else substring('alpha  bravo  charliedelta  echo   foxtrot golf   hotel  ', ((value % 8) * 7) + 1, 7)
                   end,
                   dateadd(day, value % 28, dateadd(month, value % 12, '2015-01-01'))
            from generate_series(1, {Rows.ToString(CultureInfo.InvariantCulture)})
            """);
        Execute(connection, "create table dim (k int); insert dim select value from generate_series(0, 11)");
    }

    private static void Execute(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>
    /// Runs <paramref name="sql"/> and renders the whole result set as text —
    /// every row, every column, NULLs and types included — so a comparison is a
    /// value-for-value one rather than a row count.
    /// </summary>
    private static (string Rendered, List<string> Trace) Run(string sql, bool forceSerial)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        AggregateDiagnostics.Sink = [];
        AggregateDiagnostics.EnableParallelAccumulation = !forceSerial;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rendered = new StringBuilder();
            do
            {
                while (reader.Read())
                {
                    for (var i = 0; i < reader.FieldCount; i++)
                    {
                        _ = rendered
                            .Append(reader.IsDBNull(i) ? "<null>" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))
                            .Append('');
                    }
                    _ = rendered.Append('\n');
                }
            }
            while (reader.NextResult());
            return (rendered.ToString(), AggregateDiagnostics.Sink);
        }
        finally
        {
            AggregateDiagnostics.Sink = null;
            AggregateDiagnostics.EnableParallelAccumulation = false;
        }
    }

    /// <summary>
    /// The whole contract in one assertion: the same statement over the same
    /// rows answers identically with and without the fork, and the fork really
    /// happened (otherwise the equality would be vacuous).
    /// </summary>
    private static void AssertParallelAgreesWithSerial(string sql)
    {
        var (parallel, trace) = Run(sql, forceSerial: false);
        var (serial, _) = Run(sql, forceSerial: true);
        AreEqual(serial, parallel, $"parallel and serial answers differ for: {sql}");
        IsTrue(
            trace.Exists(entry => entry.StartsWith("Aggregate:Parallel", StringComparison.Ordinal)),
            $"expected the parallel path to engage for: {sql} (trace: {string.Join(", ", trace)})");
    }

    private static void AssertDeclinesToSerial(string sql)
    {
        var (_, trace) = Run(sql, forceSerial: false);
        IsFalse(
            trace.Exists(entry => entry.StartsWith("Aggregate:Parallel", StringComparison.Ordinal)),
            $"expected the engagement gate to decline: {sql} (trace: {string.Join(", ", trace)})");
    }

    [TestMethod]
    [DataRow("select count(*) from t")]
    [DataRow("select count(measure) from t")]
    [DataRow("select count(distinct measure) from t")]
    [DataRow("select count_big(*) from t")]
    [DataRow("select approx_count_distinct(measure) from t")]
    [DataRow("select sum(measure) from t")]
    [DataRow("select sum(distinct measure) from t")]
    [DataRow("select sum(price) from t")]
    [DataRow("select sum(amount) from t")]
    [DataRow("select avg(measure) from t")]
    [DataRow("select avg(price) from t")]
    [DataRow("select avg(distinct price) from t")]
    [DataRow("select min(measure), max(measure) from t")]
    [DataRow("select min(price), max(price) from t")]
    [DataRow("select min(whenOn), max(whenOn) from t")]
    [DataRow("select min(label), max(label) from t")]
    [DataRow("select checksum_agg(measure) from t")]
    public void UngroupedAggregate_ParallelMatchesSerial(string sql) => AssertParallelAgreesWithSerial(sql);

    [TestMethod]
    [DataRow("select grp, count(*), sum(measure), avg(price), min(label), max(whenOn) from t group by grp order by grp")]
    [DataRow("select grp, count(distinct measure) from t group by grp order by grp")]
    [DataRow("select grp, sum(price) from t where measure > 100 group by grp order by grp")]
    [DataRow("select grp, sum(price) from t group by grp having sum(price) > 1000 order by grp")]
    [DataRow("select top (3) grp, count(*) as n from t group by grp order by n desc, grp")]
    [DataRow("select distinct count(*) from t group by grp")]
    [DataRow("select grp, count(*) from t group by grp order by grp offset 2 rows fetch next 3 rows only")]
    [DataRow("select year(whenOn), sum(case when month(whenOn) <= 6 then price else 0 end) from t group by year(whenOn) order by 1")]
    [DataRow("select grp, sum(measure) from t group by grp order by grp")]
    // An OR chain a written constant absorbs keeps the folded predicate in the
    // filter, so the purity walk meets the fold rather than the chain.
    [DataRow("select grp, count(*) from t where id > 0 or 1 = 1 group by grp order by grp")]
    public void GroupedAggregate_ParallelMatchesSerial(string sql) => AssertParallelAgreesWithSerial(sql);

    /// <summary>
    /// Boundary shapes: a WHERE that keeps nothing (the implicit empty group
    /// still has to answer), a group key that is NULL for part of the input, an
    /// all-NULL measure, and a single group over the whole table.
    /// </summary>
    [TestMethod]
    [DataRow("select count(*), sum(measure), min(label) from t where id < 0")]
    [DataRow("select measure, count(*) from t group by measure having count(*) > 60 order by measure")]
    [DataRow("select sum(case when 1 = 0 then measure end) from t")]
    [DataRow("select count(*) from t group by grp * 0")]
    [DataRow("select grp, count(measure), count(*) from t where measure is null group by grp order by grp")]
    public void BoundaryShapes_ParallelMatchesSerial(string sql) => AssertParallelAgreesWithSerial(sql);

    /// <summary>
    /// The label column repeats each word in two casings, so under the default
    /// case-insensitive collation <c>MIN</c> / <c>MAX</c> meets a tie between
    /// values that render differently — the case
    /// <c>MinMaxAggregator.TryMergeFrom</c> refuses, which sends the statement
    /// back through the serial path. The answer has to be the serial one.
    /// </summary>
    [TestMethod]
    public void MinMaxTieAcrossCasings_FallsBackAndKeepsSerialAnswer()
    {
        var (parallel, trace) = Run("select max(label) from t", forceSerial: false);
        var (serial, _) = Run("select max(label) from t", forceSerial: true);
        AreEqual(serial, parallel);
        Contains("Aggregate:SerialRerun(merge)", trace, $"trace: {string.Join(", ", trace)}");
    }

    /// <summary>
    /// A divide-by-zero on one known row. Serial order reports Msg 8134 from
    /// that row; the parallel attempt raises on whichever worker got there
    /// first, discards everything and re-runs — so the client sees the same
    /// error either way, and the trace shows the re-run happened.
    /// </summary>
    [TestMethod]
    public void ErrorOnAWorkerRow_ReRunsSeriallyAndReportsTheSameError()
    {
        const string Sql = "select sum(1 / (id - 40000)) from t";
        var parallelError = CaptureError(Sql, forceSerial: false, out var trace);
        var serialError = CaptureError(Sql, forceSerial: true, out _);
        AreEqual(8134, serialError);
        AreEqual(serialError, parallelError);
        Contains("Aggregate:SerialRerun(error)", trace, $"trace: {string.Join(", ", trace)}");
    }

    /// <summary>
    /// The streaming path's probe-pinned semantic — an aggregate operand
    /// raising on an early row preempts a WHERE that would have raised on a
    /// later one — survives the fork, because the fork's error path is a serial
    /// re-run rather than a report of whichever worker raised.
    /// </summary>
    [TestMethod]
    public void AggregateOperandErrorStillPreemptsALaterWhereError()
    {
        var error = CaptureError(
            "select sum(1 / (id - 40000)) from t where cast(case when id = 55000 then 'x' else '1' end as int) > 0",
            forceSerial: false,
            out _);
        AreEqual(8134, error);
    }

    private static int CaptureError(string sql, bool forceSerial, out List<string> trace)
    {
        using var connection = simulation.CreateDbConnection();
        connection.Open();
        AggregateDiagnostics.Sink = [];
        AggregateDiagnostics.EnableParallelAccumulation = !forceSerial;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var raised = ThrowsExactly<SimulatedSqlException>(() =>
            {
                using var reader = command.ExecuteReader();
                while (reader.Read()) { }
            });
            return raised.Number;
        }
        finally
        {
            trace = AggregateDiagnostics.Sink ?? [];
            AggregateDiagnostics.Sink = null;
            AggregateDiagnostics.EnableParallelAccumulation = false;
        }
    }

    /// <summary>
    /// Each engagement gate, asserted by the trace rather than by the answer:
    /// an inexact merge kind, an order-dependent one, a grouping shape that
    /// buffers anyway, a window, a group key nearly as numerous as the rows,
    /// and an expression whose purity nothing has proved.
    /// </summary>
    [TestMethod]
    // Inexact merges: float re-associates, the statistical family accumulates
    // in double, and the concatenating aggregates depend on arrival order.
    [DataRow("select sum(cast(measure as float)) from t")]
    [DataRow("select avg(cast(measure as real)) from t")]
    [DataRow("select stdev(measure), var(measure) from t")]
    [DataRow("select string_agg(label, ',') from (select top (20) label from t order by id) x")]
    // Several grouping sets buffer the rows anyway, so there is no stream to fork.
    [DataRow("select grp, count(*) from t group by rollup(grp)")]
    [DataRow("select grp, count(*) from t group by cube(grp, measure)")]
    // A window spans the whole grouped result, which needs every group first.
    [DataRow("select grp, count(*), sum(count(*)) over () from t group by grp")]
    // One group per row: every worker builds its own map and the merge pays for
    // all of them.
    [DataRow("select id, count(*) from t group by id")]
    // Expressions nothing has proved pure: two volatile draws and a subquery
    // in the WHERE (an aggregate cannot take one as its operand — real's
    // Msg 130 — so the WHERE is where a subquery reaches this path).
    [DataRow("select sum(case when rand() > 2 then 1 else 0 end) from t")]
    [DataRow("select count(*) from t where newid() is not null")]
    [DataRow("select count(*) from t where grp < (select max(k) from dim)")]
    // A predicate kind that answers the purity question from the base — the
    // rowset-valued family, which re-enters a plan per row.
    [DataRow("select count(*) from t where exists (select 1 from dim where dim.k = 99)")]
    [DataRow("select count(*) from t where grp in (select k from dim)")]
    [DataRow("select count(*) from t where grp < any (select k from dim)")]
    public void EngagementGates_Decline(string sql) => AssertDeclinesToSerial(sql);

    /// <summary>
    /// The purity question is asked of every expression the workers evaluate —
    /// each WHERE conjunct, each grouping expression and each aggregate
    /// operand — so an expression kind that never answers it is a kind the
    /// gate has never actually classified. This drives the whole scalar and
    /// predicate zoo through that walk in one statement and asserts the merged
    /// answer is the serial one, which is the only thing the classification is
    /// there to protect.
    /// </summary>
    [TestMethod]
    public void ParallelSafeExpressionZoo_ParallelMatchesSerial() =>
        AssertParallelAgreesWithSerial("""
            select grp,
                   count(*),
                   sum(abs(measure)),
                   sum(cast(ceiling(price) as int) + convert(int, floor(price)) + sign(measure) + round(measure, -1)),
                   max(datediff(day, '2015-01-01', dateadd(day, 1, whenOn))),
                   max(len(upper(lower(trim(rtrim(ltrim(label))))))),
                   max(datalength(replace(left(label, 4), 'a', 'b'))),
                   max(len(right(label, 3) + '.')),
                   max(coalesce(nullif(measure, 3), 0)),
                   max(isnull(measure, 0)),
                   max(greatest(measure, 1)),
                   max(least(measure, 900)),
                   max(iif(measure > 5, 1, 0)),
                   max(charindex('a', substring(label, 1, 4))),
                   max(len(concat(label, cast(measure as varchar(10))))),
                   max(len(json_value('{"a":"xy"}', '$.a'))),
                   max(len(label collate Latin1_General_CI_AS)),
                   max(-measure)
            from t
            where (measure is distinct from 12345 and abs(id) between 1 and 1000000)
               or not (grp in (98, 99) or sign(id) < 0)
            group by grp
            order by grp
            """);

    /// <summary>
    /// A variable reference is pure — it is read once per row but never
    /// written by one — so a WHERE naming one still engages.
    /// </summary>
    [TestMethod]
    public void VariableInWhere_ParallelMatchesSerial() =>
        AssertParallelAgreesWithSerial("""
            declare @floor int = 0;
            select grp, count(*), sum(measure) from t where id > @floor group by grp order by grp
            """);

    /// <summary>
    /// A derived-table source declines: its rows come from a plan that would
    /// have to be re-entered per row, and re-entering <c>Execute</c> writes
    /// <see cref="Parser.BatchContext"/> state.
    /// </summary>
    [TestMethod]
    public void DerivedTableSource_Declines() =>
        AssertDeclinesToSerial("select count(*), sum(measure) from (select id, measure from t where id > 0) d");

    /// <summary>
    /// Below the row threshold nothing forks, so a small table's aggregate pays
    /// none of the fan-out — the property that keeps the common short query
    /// unchanged.
    /// </summary>
    [TestMethod]
    public void BelowRowThreshold_StaysSerial()
    {
        var sim = new Simulation();
        using var connection = sim.CreateDbConnection();
        connection.Open();
        Execute(connection, "create table small (id int); insert small select value from generate_series(1, 1000)");
        AggregateDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "select count(*), sum(id) from small";
            using var reader = command.ExecuteReader();
            while (reader.Read()) { }
            IsEmpty(AggregateDiagnostics.Sink);
        }
        finally
        {
            AggregateDiagnostics.Sink = null;
        }
    }
}
