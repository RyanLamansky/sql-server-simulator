using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// What a joined UPDATE / DELETE answers once its row sources go through the
/// read path's own passes — the once-per-enumeration materialization of a
/// deferred source and the WHERE narrowing of every source but the mutation
/// target (<c>Selection.PrepareMutationJoinSources</c>). Both are pure cost
/// reductions, so every value asserted here was probed against SQL Server 2025
/// first.
/// <para>
/// The Halloween cases are the load-bearing ones: a derived / CTE source reading
/// the <em>target table itself</em> reads the pre-statement rows, because the
/// statement collects its whole affected-row set before it writes anything —
/// which is what makes running that source once identical to running it per
/// target row. The <c>NEWID()</c> shape is the counterpart: real re-draws per
/// target row, so the materialization's volatility gate has to decline it.
/// </para>
/// The strategy each shape resolves to is asserted in
/// <c>SqlServerSimulator.Tests.Internal</c>'s <c>MutationJoinStrategyTests</c>.
/// </summary>
[TestClass]
public sealed class MutationJoinSourceTests
{
    /// <summary>
    /// Three target rows and a five-row detail table to aggregate: the groups
    /// total 105 / 200 / 301, and <c>k</c> carries an index so a WHERE on it can
    /// narrow the joined source.
    /// </summary>
    private const string Setup = """
        create table t (id int not null primary key, v int not null);
        create table s (sid int not null primary key, id int not null, k int not null, w int not null);
        create index ix_s_k on s (k);
        insert t values (1, 10), (2, 20), (3, 30);
        insert s values (1, 1, 7, 100), (2, 1, 7, 5), (3, 2, 8, 200), (4, 3, 7, 300), (5, 3, 8, 1)
        """;

    private const string GroupedDerived = "(select id, sum(w) as total from s group by id) d";

    /// <summary>Every row of a result set, values pipe-joined and rows semicolon-joined.</summary>
    private static string Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        foreach (var record in reader.EnumerateRecords())
        {
            var values = new object[record.FieldCount];
            _ = record.GetValues(values);
            rows.Add(string.Join("|", values));
        }

        return string.Join("; ", rows);
    }

    private static string TargetRows(Simulation simulation) => Rows(simulation, "select id, v from t order by id");

    /// <summary>The target table's rows after <paramref name="mutation"/> ran over <see cref="Setup"/>.</summary>
    private static string AfterMutation(string mutation)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"{Setup}; {mutation}");
        return TargetRows(sim);
    }

    // ---- the deferred source runs once and answers the same ------------------

    /// <summary>
    /// The motivating shape: a grouped derived table joined to the target, whose
    /// body used to re-execute once per target row.
    /// </summary>
    [TestMethod]
    public void UpdateFromGroupedDerivedTable_AppliesEachGroupsAggregate()
        => AreEqual("1|105; 2|200; 3|301",
            AfterMutation($"update t set v = d.total from t join {GroupedDerived} on d.id = t.id"));

    /// <summary>The CTE spelling of the same join takes the same path.</summary>
    [TestMethod]
    public void UpdateFromCteReference_AppliesEachGroupsAggregate()
        => AreEqual("1|105; 2|200; 3|301", AfterMutation("""
            with d as (select id, sum(w) as total from s group by id)
            update t set v = d.total from t join d on d.id = t.id
            """));

    /// <summary>A view body reaches the same materialization as a derived table.</summary>
    [TestMethod]
    public void UpdateFromViewSource_AppliesEachGroupsAggregate()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            Setup,
            "create view totals as select id, sum(w) as total from s group by id",
            "update t set v = d.total from t join totals d on d.id = t.id");
        AreEqual("1|105; 2|200; 3|301", TargetRows(sim));
    }

    /// <summary>The DELETE counterpart: the derived source decides which rows go.</summary>
    [TestMethod]
    public void DeleteFromGroupedDerivedTable_RemovesTheJoinedRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery($"delete t from t join {GroupedDerived} on d.id = t.id where d.total > 150"));
        AreEqual("1|10", TargetRows(sim));
    }

    /// <summary>
    /// <c>@@ROWCOUNT</c> counts the target rows the statement touched, not the
    /// join tuples that reached them: three source rows collapse onto two
    /// targets, and the first tuple to reach a target supplies its value
    /// (probe-confirmed — real reports 2 and takes the first row's 100).
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTable_DedupesJoinMultipliedTargets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v int not null);
            create table s (id int not null, w int not null);
            insert t values (1, 0), (2, 0);
            insert s values (1, 100), (1, 101), (2, 200)
            """);
        AreEqual(2, sim.ExecuteNonQuery("update t set v = d.w from t join (select id, w from s) d on d.id = t.id"));
        AreEqual("1|100; 2|200", TargetRows(sim));
    }

    /// <summary>
    /// <c>OUTPUT</c> hangs off the write pipeline downstream of enumeration, so
    /// both sides still project (probe-confirmed values).
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTable_OutputProjectsBothSides()
        => AreEqual("10|105; 20|200; 30|301", Rows(new Simulation(), $"""
            {Setup};
            update t set v = d.total
            output deleted.v as oldv, inserted.v as newv
            from t join {GroupedDerived} on d.id = t.id
            """));

    /// <summary>The DELETE form's OUTPUT projects the rows it removed.</summary>
    [TestMethod]
    public void DeleteFromDerivedTable_OutputProjectsTheDeletedRows()
        => AreEqual("1|10; 3|30", Rows(new Simulation(), $"""
            {Setup};
            delete t
            output deleted.id, deleted.v
            from t join {GroupedDerived} on d.id = t.id
            where d.total <> 200
            """));

    /// <summary>
    /// A LEFT JOIN whose right side matches nothing still surfaces the target
    /// row with the derived side reading NULL — the materialized source keys
    /// into the hash path, whose unmatched-left extension has to answer the same.
    /// </summary>
    [TestMethod]
    public void UpdateLeftJoinDerivedTable_NullExtendsTheUnmatchedTargets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v int not null);
            create table s (id int not null, w int not null);
            insert t values (1, 0), (2, 0), (3, 0);
            insert s values (1, 100)
            """);
        AreEqual(3, sim.ExecuteNonQuery(
            "update t set v = isnull(d.w, -1) from t left join (select id, max(w) as w from s group by id) d on d.id = t.id"));
        AreEqual("1|100; 2|-1; 3|-1", TargetRows(sim));
    }

    /// <summary>
    /// The same LEFT JOIN with a WHERE that reads the null-extended side: the
    /// conjunct is UNKNOWN for a NULL-filled slot unless it says so, so only the
    /// rows that name themselves survive (probe-confirmed — real updates ids 2
    /// and 3).
    /// </summary>
    [TestMethod]
    public void UpdateLeftJoinDerivedTable_WhereReadsTheNullExtendedSide()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int not null primary key, v int not null);
            create table s (id int not null, w int not null);
            insert t values (1, 0), (2, 0), (3, 0);
            insert s values (1, 100), (3, 300)
            """);
        AreEqual(2, sim.ExecuteNonQuery("""
            update t set v = isnull(d.w, -1)
            from t left join (select id, sum(w) as w from s group by id) d on d.id = t.id
            where d.w > 150 or d.w is null
            """));
        AreEqual("1|0; 2|-1; 3|300", TargetRows(sim));
    }

    /// <summary>
    /// The derived source named <em>first</em>, with the target joined onto it:
    /// the leftmost slot stays deferred (it already runs once), and the target
    /// keeps its own enumeration whatever slot it occupies.
    /// </summary>
    [TestMethod]
    public void UpdateWithTheDerivedSourceNamedFirst_UpdatesTheJoinedTarget()
        => AreEqual("1|105; 2|200; 3|301",
            AfterMutation($"update t set v = d.total from {GroupedDerived} join t on t.id = d.id"));

    // ---- Halloween: a source reading the mutation target --------------------

    /// <summary>
    /// The self-referencing case. Real computes the aggregate against the
    /// pre-statement rows, so every row lands on the pre-update maximum
    /// (probe-confirmed: <c>(10, 20, 30)</c> → <c>30, 30, 30</c>) — running the
    /// derived body once has to answer the same, and does because the statement
    /// writes nothing until its whole row set is collected.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedAggregateOverTheTarget_ReadsThePreUpdateRows()
        => AreEqual("1|30; 2|30; 3|30",
            AfterMutation("update t set v = d.m from t join (select max(v) as m from t) d on 1 = 1"));

    /// <summary>
    /// The same shape with the target's own column in the SET expression: each
    /// row adds the pre-update sum of all three (60), never a running one.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedSumOverTheTarget_AddsThePreUpdateTotalToEveryRow()
        => AreEqual("1|70; 2|80; 3|90",
            AfterMutation("update t set v = t.v + d.s from t join (select sum(v) as s from t) d on 1 = 1"));

    /// <summary>The CTE spelling of the self-referencing aggregate answers identically.</summary>
    [TestMethod]
    public void UpdateFromCteAggregateOverTheTarget_ReadsThePreUpdateRows()
        => AreEqual("1|30; 2|30; 3|30", AfterMutation("""
            with d as (select max(v) as m from t)
            update t set v = d.m from t join d on 1 = 1
            """));

    /// <summary>
    /// A grouped derived table over the target, correlated by key: every row
    /// reads its own pre-update value (probe-confirmed <c>100 / 200 / 300</c>).
    /// </summary>
    [TestMethod]
    public void UpdateFromGroupedDerivedOverTheTarget_ReadsEachRowsPreUpdateValue()
        => AreEqual("1|100; 2|200; 3|300",
            AfterMutation("update t set v = d.s from t join (select id, sum(v) * 10 as s from t group by id) d on d.id = t.id"));

    /// <summary>
    /// The DELETE counterpart: the derived maximum is computed once against the
    /// pre-statement rows, so every row below it goes (probe-confirmed — 2 rows
    /// deleted, the row holding the maximum survives).
    /// </summary>
    [TestMethod]
    public void DeleteFromDerivedAggregateOverTheTarget_RemovesEveryRowBelowThePreDeleteMaximum()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery("delete t from t join (select max(v) as m from t) d on t.v < d.m"));
        AreEqual("3|30", TargetRows(sim));
    }

    /// <summary>
    /// The target joined to <em>itself</em> as a plain base-table source, which
    /// the WHERE narrowing could otherwise seek: row 1 takes row 2's pre-update
    /// value and row 2 takes row 3's, never the value row 2 was just given
    /// (probe-confirmed <c>20, 30, 30</c> with <c>@@ROWCOUNT</c> 2).
    /// </summary>
    [TestMethod]
    public void UpdateJoiningTheTargetToItself_ReadsThePreUpdateRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery("update t set v = u.v from t join t u on u.id = t.id + 1"));
        AreEqual("1|20; 2|30; 3|30", TargetRows(sim));
    }

    // ---- the volatility gate ------------------------------------------------

    /// <summary>
    /// Real re-draws <c>NEWID()</c> per target row — probe-confirmed, five
    /// distinct values over five target rows — so the materialization declines
    /// the source and it keeps its per-row execution.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTableDrawingNewid_DrawsOnePerTargetRow()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, g uniqueidentifier null);
            insert t (id) values (1), (2), (3), (4), (5);
            update t set g = d.g from t join (select top 1 newid() as g from t) d on 1 = 1;
            select count(distinct g) from t
            """));

    /// <summary>
    /// The DELETE side of the same gate: the drawn value is never NULL, so the
    /// join matches nothing and every row survives (probe-confirmed).
    /// </summary>
    [TestMethod]
    public void DeleteFromDerivedTableDrawingNewid_MatchesNoRow()
        => AreEqual(5, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key);
            insert t values (1), (2), (3), (4), (5);
            delete t from t join (select top 1 newid() as g from t) d on d.g is null;
            select count(*) from t
            """));

    // ---- the non-target WHERE narrowing -------------------------------------

    /// <summary>
    /// An equality on the joined source's indexed column narrows that source and
    /// leaves the answer alone: the conjunct stays in the WHERE the statement
    /// re-runs per tuple. Rows 1 and 3 have a <c>k = 7</c> detail row, row 2
    /// doesn't (probe-confirmed <c>100 / 20 / 300</c>, <c>@@ROWCOUNT</c> 2).
    /// </summary>
    [TestMethod]
    public void UpdateWithEqualityOnTheJoinedSource_UpdatesOnlyItsMatchingTargets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery("update t set v = s.w from t join s on s.id = t.id where s.k = 7"));
        AreEqual("1|100; 2|20; 3|300", TargetRows(sim));
    }

    /// <summary>The DELETE form narrows the same way and removes the same rows.</summary>
    [TestMethod]
    public void DeleteWithEqualityOnTheJoinedSource_RemovesOnlyItsMatchingTargets()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery("delete t from t join s on s.id = t.id where s.k = 8"));
        AreEqual("1|10", TargetRows(sim));
    }

    /// <summary>
    /// A WHERE equality on the <em>target</em>'s own key still filters — the
    /// pass leaves that source enumerating exactly as it did, so the predicate
    /// is a residual filter rather than a seek.
    /// </summary>
    [TestMethod]
    public void UpdateWithEqualityOnTheTarget_UpdatesThatRowOnly()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(1, sim.ExecuteNonQuery("update t set v = s.w from t join s on s.id = t.id where t.id = 2"));
        AreEqual("1|10; 2|200; 3|30", TargetRows(sim));
    }

    /// <summary>
    /// A narrowed source under a LEFT JOIN: the tuple its lost match would have
    /// NULL-extended is excluded by the very conjunct that justified the
    /// narrowing, so no target row is updated off a NULL-filled slot
    /// (probe-confirmed — the same <c>100 / 20 / 300</c> the INNER spelling
    /// answers).
    /// </summary>
    [TestMethod]
    public void UpdateLeftJoinWithEqualityOnTheJoinedSource_KeepsTheResidualFilter()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery("update t set v = isnull(s.w, -1) from t left join s on s.id = t.id where s.k = 7"));
        AreEqual("1|100; 2|20; 3|300", TargetRows(sim));
    }

    // ---- everything downstream of enumeration -------------------------------

    /// <summary>
    /// An AFTER trigger on a joined UPDATE fires once for the statement, with
    /// INSERTED / DELETED carrying every affected row — trigger dispatch hangs
    /// off the commit phase, well downstream of the row sources.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTable_FiresTheAfterTriggerOnceWithEveryRow()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            $"{Setup}; create table audit (fired int not null identity(1, 1) primary key, rows int not null, oldsum int not null, newsum int not null)",
            """
            create trigger tr_t on t after update as
            insert audit (rows, oldsum, newsum)
            select count(*), (select sum(v) from deleted), (select sum(v) from inserted) from inserted
            """,
            $"update t set v = d.total from t join {GroupedDerived} on d.id = t.id");
        AreEqual("1|3|60|606", Rows(sim, "select fired, rows, oldsum, newsum from audit order by fired"));
    }

    /// <summary>
    /// The whole statement is one atomic unit: a ROLLBACK after it restores
    /// every target row, so the materialized read didn't escape the undo log.
    /// </summary>
    [TestMethod]
    public void UpdateFromDerivedTable_RollsBackWholesale()
    {
        var sim = new Simulation();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand(Setup).ExecuteNonQuery();
        _ = connection.CreateCommand($"""
            begin tran;
            update t set v = d.total from t join {GroupedDerived} on d.id = t.id;
            rollback
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand("select id, v from t order by id").ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader.GetInt32(0)}|{reader.GetInt32(1)}");
        AreEqual("1|10; 2|20; 3|30", string.Join("; ", rows));
    }

    /// <summary>
    /// <c>TOP</c> caps the affected rows after collection, so the cap still
    /// applies over a materialized source (probe-confirmed <c>@@ROWCOUNT</c> 2).
    /// </summary>
    [TestMethod]
    public void UpdateTopFromDerivedTable_CapsTheAffectedRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(Setup);
        AreEqual(2, sim.ExecuteNonQuery($"update top (2) t set v = d.total from t join {GroupedDerived} on d.id = t.id"));
    }

    /// <summary>
    /// A skipped statement runs neither pass: the materializing execution would
    /// otherwise run a deferred body on behalf of a statement that never runs,
    /// and raise where the per-outer-row execution never reached one — an empty
    /// target drives no rows at all.
    /// </summary>
    [TestMethod]
    public void SkippedUpdateFromRaisingDerivedTable_EvaluatesNothing()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            create table t (id int not null primary key, v int not null);
            create table z (v int not null);
            insert z values (0);
            if 1 = 0
            begin
                update t set v = d.x from t join (select 1 / z.v as x from z) d on 1 = 1;
            end
            select 1
            """));
}
