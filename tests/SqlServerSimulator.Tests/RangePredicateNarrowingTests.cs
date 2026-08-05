using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The answers a range predicate keeps under the two narrowings that decide how
/// it reads: the range seek's <b>span gate</b> (a wide interval abandons the
/// seek for the scan) and the <b>scan prefilter</b> (a joined source no key can
/// seek has its own sargable conjuncts applied before the join runs). Both are
/// result-transparent by design, so every fixture below runs the predicate twice
/// — once written so a key can position on it, once written so nothing can
/// (<c>col + 0</c>, which is the same three-valued predicate for an
/// <c>int</c> and the same date for a <c>dateadd(day, 0, …)</c>) — and asserts
/// the two agree as well as matching the expected rows. A divergence is the
/// narrowing changing results, which is the one way either can be wrong.
/// <para>
/// Which path each query actually took is asserted in
/// <c>SqlServerSimulator.Tests.Internal.ScanPrefilterTests</c>, where the
/// access-path trace is reachable.
/// </para>
/// </summary>
[TestClass]
public sealed class RangePredicateNarrowingTests
{
    // ---- fixtures ----

    /// <summary>
    /// 4 000 rows — past the span gate's row-count floor, so a range covering
    /// more than a quarter of them abandons the seek. <c>n</c> is indexed and
    /// nullable (every 7th row), <c>code</c> is indexed and nullable (every
    /// 11th).
    /// </summary>
    private static Simulation Wide()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table wide (id int not null primary key, n int null, code varchar(20) null, v int not null);
            create index ix_wide_n on wide (n);
            create index ix_wide_code on wide (code);
            insert wide (id, n, code, v)
            select value,
                   case when value % 7 = 0 then null else value end,
                   case when value % 11 = 0 then null else concat('k', right(concat('0000', value), 4)) end,
                   value * 2
            from generate_series(1, 4000)
            """);
        return sim;
    }

    /// <summary>
    /// A header / line pair whose header filter column carries no index, so the
    /// seek declines and the prefilter is the only narrowing available. Header 3
    /// has a NULL tag; line 5 has no header, header 4 has no line.
    /// </summary>
    private static Simulation Joined()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null primary key, tag int null);
            create table l (id int not null, qty int not null);
            create index ix_l_id on l (id);
            insert h values (1, 10), (2, 20), (3, null), (4, 40);
            insert l values (1, 100), (2, 200), (3, 300), (5, 500)
            """);
        return sim;
    }

    private static string Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                cells[i] = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString() ?? string.Empty;
            rows.Add(string.Join(',', cells));
        }

        return string.Join('|', rows);
    }

    /// <summary>
    /// Asserts the narrowed form produces <paramref name="expected"/> and that
    /// the form nothing can narrow produces the same.
    /// </summary>
    private static void SameEitherWay(Simulation sim, string expected, string narrowable, string opaque)
    {
        var narrowed = Rows(sim, narrowable);
        AreEqual(Rows(sim, opaque), narrowed);
        AreEqual(expected, narrowed);
    }

    // ---- span gate: the range's answer never depends on how wide it is ----

    [TestMethod]
    public void WholeTableRange_AnswersLikeTheScan()
        => SameEitherWay(Wide(), "4000",
            "select count(*) from wide where id >= 1",
            "select count(*) from wide where id + 0 >= 1");

    [TestMethod]
    public void RangeAcrossTheGateBoundary_AgreesOnBothSides()
    {
        // 1000 rows is just under the gate's quarter (seeks), 1001 just over
        // (abandons) — the two have to differ by exactly the one row.
        var sim = Wide();
        SameEitherWay(sim, "1000",
            "select count(*) from wide where id between 1 and 1000",
            "select count(*) from wide where id + 0 between 1 and 1000");
        SameEitherWay(sim, "1001",
            "select count(*) from wide where id between 1 and 1001",
            "select count(*) from wide where id + 0 between 1 and 1001");
    }

    [TestMethod]
    public void BoundaryValues_InclusiveAndExclusive_MatchTheScan()
    {
        var sim = Wide();
        SameEitherWay(sim, "10|11|12",
            "select id from wide where id >= 10 and id <= 12 order by v",
            "select id from wide where id + 0 >= 10 and id + 0 <= 12 order by v");
        SameEitherWay(sim, "11",
            "select id from wide where id > 10 and id < 12 order by v",
            "select id from wide where id + 0 > 10 and id + 0 < 12 order by v");
        SameEitherWay(sim, "1|4000",
            "select id from wide where id <= 1 or id >= 4000 order by v",
            "select id from wide where id + 0 <= 1 or id + 0 >= 4000 order by v");
    }

    [TestMethod]
    public void NullsNeverFallInARange_AtAnyWidth()
    {
        // n is NULL for every 7th row; a range — wide or narrow — is UNKNOWN for
        // those, so they're in neither the range's rows nor its complement.
        var sim = Wide();
        SameEitherWay(sim, "3429",
            "select count(*) from wide where n >= 1",
            "select count(*) from wide where n + 0 >= 1");
        SameEitherWay(sim, "9",
            "select count(*) from wide where n between 1 and 10",
            "select count(*) from wide where n + 0 between 1 and 10");
        AreEqual(571, sim.ExecuteScalar("select count(*) from wide where n is null"));
    }

    [TestMethod]
    public void ReversedBounds_AreEmptyAtAnyWidth()
        => SameEitherWay(Wide(), "0",
            "select count(*) from wide where id > 3000 and id < 100",
            "select count(*) from wide where id + 0 > 3000 and id + 0 < 100");

    [TestMethod]
    public void UnboundedBelow_WideEnoughToAbandonTheSeek()
        => SameEitherWay(Wide(), "3000",
            "select count(*) from wide where id <= 3000",
            "select count(*) from wide where id + 0 <= 3000");

    [TestMethod]
    public void StringRange_CaseInsensitiveCollation_MatchesTheScan()
    {
        var sim = Wide();
        SameEitherWay(sim, "3",
            "select count(*) from wide where code >= 'K0010' and code < 'k0014'",
            "select count(*) from wide where code + '' >= 'K0010' and code + '' < 'k0014'");
    }

    [TestMethod]
    public void StringRange_CaseSensitiveCollation_MatchesTheScan()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table s (code varchar(20) collate Latin1_General_CS_AS not null primary key, v int not null);
            insert s (code, v)
            select concat(case when value % 2 = 0 then 'A' else 'a' end, right(concat('0000', value), 4)), value
            from generate_series(1, 3000)
            """);
        // CS_AS compares case only as a tiebreaker, not as a primary weight, so
        // the interval spans both spellings — five rows, not the three the
        // upper-case run alone holds. That ordering is the collation's, and
        // getting it from the collation is the point: an ordinal comparer would
        // answer three.
        SameEitherWay(sim, "5",
            "select count(*) from s where code >= 'A0010' and code <= 'A0014'",
            "select count(*) from s where code + '' >= 'A0010' and code + '' <= 'A0014'");
    }

    [TestMethod]
    public void StringRange_AccentInsensitiveCollation_MatchesTheScan()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table s (code nvarchar(20) collate Latin1_General_CI_AI not null primary key, v int not null);
            insert s values (N'cafe', 1), (N'cafér', 2), (N'cafz', 3), (N'cbfe', 4)
            """);
        // The keys have to differ by more than an accent: an AI collation folds
        // one away, so N'café' beside N'cafe' is Msg 2627 on a primary key
        // (probe-confirmed against SQL Server 2025).
        // AI folds the accent, so 'café' sorts with 'cafe' and both fall inside
        // a range bounded by the unaccented spellings.
        SameEitherWay(sim, "1|2",
            "select v from s where code >= N'cafe' and code < N'cafz' order by v",
            "select v from s where code + N'' >= N'cafe' and code + N'' < N'cafz' order by v");
    }

    [TestMethod]
    public void CompositePrefixThenRange_MatchesTheScan()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (a int not null, b int not null, v int not null, primary key (a, b));
            insert t (a, b, v) select value % 4, value, value from generate_series(1, 4000)
            """);
        SameEitherWay(sim, "2",
            "select count(*) from t where a = 1 and b between 100 and 108",
            "select count(*) from t where a + 0 = 1 and b + 0 between 100 and 108");
    }

    [TestMethod]
    public void RangeAfterInsert_SeesTheNewRow()
    {
        // The sorted view is maintained through the heap's mutation generation:
        // a row inserted into an already-warmed range has to show up.
        var sim = Wide();
        AreEqual(9, sim.ExecuteScalar("select count(*) from wide where id between 1 and 9"));
        _ = sim.ExecuteNonQuery("insert wide (id, n, code, v) values (100000, 5, 'k0005b', 1)");
        AreEqual(9, sim.ExecuteScalar("select count(*) from wide where id between 1 and 9"));
        AreEqual(4, sim.ExecuteScalar("select count(*) from wide where n between 4 and 6"));
        AreEqual(1, sim.ExecuteScalar("select count(*) from wide where id >= 100000"));
    }

    [TestMethod]
    public void WideRangeDelete_RemovesExactlyTheRange()
    {
        // The gate lives in the core the mutation path shares, so the DELETE
        // falls back to its scan — and still deletes exactly the range.
        var sim = Wide();
        _ = sim.ExecuteNonQuery("delete wide where id >= 1000");
        AreEqual(999, sim.ExecuteScalar("select count(*) from wide"));
        AreEqual(999, sim.ExecuteScalar("select max(id) from wide"));
    }

    [TestMethod]
    public void WideRangeUnderSnapshot_ReadsTheSnapshotRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current set allow_snapshot_isolation on");
        _ = sim.ExecuteNonQuery("""
            create table wide (id int not null primary key, v int not null);
            insert wide (id, v) select value, value from generate_series(1, 4000)
            """);

        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        _ = reader.CreateCommand("select count(*) from wide where id >= 1").ExecuteScalar();
        _ = writer.CreateCommand("delete wide where id > 2000").ExecuteNonQuery();
        AreEqual(4000, reader.CreateCommand("select count(*) from wide where id >= 1").ExecuteScalar());
        AreEqual(500, reader.CreateCommand("select count(*) from wide where id between 1 and 500").ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(2000, reader.CreateCommand("select count(*) from wide where id >= 1").ExecuteScalar());
    }

    [TestMethod]
    public void WideRangeUnderRepeatableRead_ReadsEveryRow()
    {
        // A tx-scoped row lock keeps the whole-table scan whatever the range —
        // the answer is the same, which is what this pins.
        var sim = Wide();
        using var connection = sim.CreateOpenConnection();
        _ = connection.CreateCommand("set transaction isolation level repeatable read; begin tran").ExecuteNonQuery();
        AreEqual(4000, connection.CreateCommand("select count(*) from wide where id >= 1").ExecuteScalar());
        AreEqual(9, connection.CreateCommand("select count(*) from wide where id between 1 and 9").ExecuteScalar());
        _ = connection.CreateCommand("commit").ExecuteNonQuery();
    }

    // ---- scan prefilter: a join source narrowed before the join ----

    [TestMethod]
    public void InnerJoin_RangeOnUnindexedColumn_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200",
            "select h.id, sum(l.qty) from h join l on l.id = h.id where h.tag > 15 group by h.id order by h.id",
            "select h.id, sum(l.qty) from h join l on l.id = h.id where h.tag + 0 > 15 group by h.id order by h.id");

    [TestMethod]
    public void InnerJoin_FilterDropsEveryRow()
        => SameEitherWay(Joined(), string.Empty,
            "select h.id from h join l on l.id = h.id where h.tag > 900",
            "select h.id from h join l on l.id = h.id where h.tag + 0 > 900");

    [TestMethod]
    public void LeftJoin_FilterOnTheNullExtendableSide_MatchesTheScan()
    {
        // Filtering h drops the rows a left row would otherwise have matched, so
        // that row NULL-extends instead — and the residual conjunct reads NULL
        // for it, excluding it exactly as the failing match was excluded.
        SameEitherWay(Joined(), "2",
            "select l.id from l left join h on h.id = l.id where h.tag > 15 order by l.id",
            "select l.id from l left join h on h.id = l.id where h.tag + 0 > 15 order by l.id");
    }

    [TestMethod]
    public void LeftJoin_FilterOnThePreservedSide_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200|4,<null>",
            "select h.id, l.qty from h left join l on l.id = h.id where h.tag > 15 order by h.id",
            "select h.id, l.qty from h left join l on l.id = h.id where h.tag + 0 > 15 order by h.id");

    [TestMethod]
    public void RightJoin_FilterOnTheNullExtendableSide_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200",
            "select h.id, l.qty from h right join l on l.id = h.id where h.tag > 15 order by l.id",
            "select h.id, l.qty from h right join l on l.id = h.id where h.tag + 0 > 15 order by l.id");

    [TestMethod]
    public void FullJoin_FilterOnEitherSide_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200|4,<null>",
            "select h.id, l.qty from h full join l on l.id = h.id where h.tag > 15 order by h.id",
            "select h.id, l.qty from h full join l on l.id = h.id where h.tag + 0 > 15 order by h.id");

    [TestMethod]
    public void CrossApply_FilterOnTheOuterSide_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200",
            "select h.id, x.qty from h cross apply (select top 1 qty from l where l.id = h.id) x where h.tag > 15 order by h.id",
            "select h.id, x.qty from h cross apply (select top 1 qty from l where l.id = h.id) x where h.tag + 0 > 15 order by h.id");

    [TestMethod]
    public void OuterApply_FilterOnTheOuterSide_MatchesTheScan()
        => SameEitherWay(Joined(), "2,200|4,<null>",
            "select h.id, x.qty from h outer apply (select top 1 qty from l where l.id = h.id) x where h.tag > 15 order by h.id",
            "select h.id, x.qty from h outer apply (select top 1 qty from l where l.id = h.id) x where h.tag + 0 > 15 order by h.id");

    [TestMethod]
    public void NullColumnValue_IsDroppedByFilterAndResidualAlike()
        => SameEitherWay(Joined(), "0",
            "select count(*) from h join l on l.id = h.id where h.tag > 900 or h.tag is null and h.tag > 1",
            "select count(*) from h join l on l.id = h.id where h.tag + 0 > 900 or h.tag is null and h.tag + 0 > 1");

    [TestMethod]
    public void CorrelatedValueSide_MatchesTheScan()
    {
        // The bound reads an enclosing-scope column, which is fixed for one
        // execution of the inner plan — the prefilter's other accepted value shape.
        var sim = Joined();
        // Header 3's NULL tag makes the bound UNKNOWN, so its EXISTS is false.
        SameEitherWay(sim, "1|2|4",
            "select h.id from h where exists (select 1 from h i join l on l.id = i.id where i.tag > h.tag - 100) order by h.id",
            "select h.id from h where exists (select 1 from h i join l on l.id = i.id where i.tag + 0 > h.tag - 100) order by h.id");
    }

    [TestMethod]
    public void TopWithPrefilteredSource_TakesTheSameRows()
        => SameEitherWay(Joined(), "2,200",
            "select top 1 h.id, l.qty from h join l on l.id = h.id where h.tag > 15 order by h.id",
            "select top 1 h.id, l.qty from h join l on l.id = h.id where h.tag + 0 > 15 order by h.id");

    [TestMethod]
    public void WindowProjector_WithPrefilteredSource_MatchesTheScan()
        => SameEitherWay(Joined(), "2,1",
            "select h.id, row_number() over (order by h.id) from h join l on l.id = h.id where h.tag > 15",
            "select h.id, row_number() over (order by h.id) from h join l on l.id = h.id where h.tag + 0 > 15");

    [TestMethod]
    public void ThreeSourceJoin_EachSourceFilteredIndependently()
    {
        var sim = Joined();
        _ = sim.ExecuteNonQuery("create table m (id int not null, note int not null); insert m values (1, 1), (2, 2), (3, 3)");
        SameEitherWay(sim, "2,200,2",
            "select h.id, l.qty, m.note from h join l on l.id = h.id join m on m.id = h.id where h.tag > 15 and m.note < 3",
            "select h.id, l.qty, m.note from h join l on l.id = h.id join m on m.id = h.id where h.tag + 0 > 15 and m.note + 0 < 3");
    }

    [TestMethod]
    public void SiblingColumnBound_MatchesTheScan()
    {
        // `h.tag > l.qty` reads a sibling of the same FROM, which the prefilter
        // refuses — the answer is the residual's either way.
        var sim = Joined();
        SameEitherWay(sim, "0",
            "select count(*) from h join l on l.id = h.id where h.tag > l.qty",
            "select count(*) from h join l on l.id = h.id where h.tag + 0 > l.qty");
    }

    [TestMethod]
    public void NonSelectivePredicateOverManyRows_StillAnswersExactly()
    {
        // Past the prefilter's probe window a predicate keeping most rows stops
        // being evaluated on the scan — the residual WHERE is what settles them,
        // so the answer must not move.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table big (id int not null primary key, keepme int not null);
            create table kid (id int not null, qty int not null);
            create index ix_kid_id on kid (id);
            insert big (id, keepme) select value, case when value % 10 = 0 then 0 else 1 end from generate_series(1, 6000);
            insert kid (id, qty) select value, value from generate_series(1, 6000)
            """);
        SameEitherWay(sim, "5400",
            "select count(*) from big b join kid k on k.id = b.id where b.keepme > 0",
            "select count(*) from big b join kid k on k.id = b.id where b.keepme + 0 > 0");
    }

    [TestMethod]
    public void ErrorInThePushedBound_RaisesOnlyWhenTheJoinProducesRows()
    {
        // Evaluating the bound on the scan must not raise for a row the join
        // would never have produced a tuple from: the prefilter keeps a row
        // whose bound threw and lets the residual decide.
        var sim = Joined();
        _ = sim.ExecuteNonQuery("delete l");
        AreEqual(0, sim.ExecuteScalar("""
            declare @a int = 1, @b int = 0;
            select count(*) from h join l on l.id = h.id where h.tag > @a / @b
            """));

        var populated = Joined();
        _ = populated.AssertSqlError("""
            declare @a int = 1, @b int = 0;
            select count(*) from h join l on l.id = h.id where h.tag > @a / @b
            """, 8134);
    }

    [TestMethod]
    public void UpdateThroughAJoin_NarrowsWithoutChangingWhatItWrites()
    {
        var sim = Joined();
        _ = sim.ExecuteNonQuery("update l set qty = qty + 1 from l join h on h.id = l.id where h.tag > 15");
        AreEqual("1,100|2,201|3,300|5,500", Rows(sim, "select id, qty from l order by id"));
    }

    [TestMethod]
    public void DeleteThroughAJoin_NarrowsWithoutChangingWhatItRemoves()
    {
        var sim = Joined();
        _ = sim.ExecuteNonQuery("delete l from l join h on h.id = l.id where h.tag > 15");
        AreEqual("1,100|3,300|5,500", Rows(sim, "select id, qty from l order by id"));
    }

    [TestMethod]
    public void PrefilteredJoin_UnderSnapshot_ReadsTheSnapshotRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current set allow_snapshot_isolation on");
        _ = sim.ExecuteNonQuery("""
            create table h (id int not null primary key, tag int not null);
            create table l (id int not null, qty int not null);
            create index ix_l_id on l (id);
            insert h values (1, 10), (2, 20), (3, 30);
            insert l values (1, 100), (2, 200), (3, 300)
            """);

        using var reader = sim.CreateOpenConnection();
        using var writer = sim.CreateOpenConnection();
        _ = reader.CreateCommand("set transaction isolation level snapshot; begin tran").ExecuteNonQuery();
        AreEqual(500, reader.CreateCommand("select sum(l.qty) from h join l on l.id = h.id where h.tag > 15").ExecuteScalar());
        _ = writer.CreateCommand("update h set tag = 1 where id = 2").ExecuteNonQuery();
        AreEqual(500, reader.CreateCommand("select sum(l.qty) from h join l on l.id = h.id where h.tag > 15").ExecuteScalar());
        _ = reader.CreateCommand("commit").ExecuteNonQuery();
        AreEqual(300, reader.CreateCommand("select sum(l.qty) from h join l on l.id = h.id where h.tag > 15").ExecuteScalar());
    }
}
