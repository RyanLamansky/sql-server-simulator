using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavior a multi-source query keeps when a WHERE predicate is pushed down
/// onto a source the FROM clause doesn't name first, and when a pure INNER
/// equi-join chain is then reordered to drive from it: the rows every join kind
/// produces are unchanged, an outer join's NULL extension survives, an ON
/// conjunct pairing two non-adjacent sources still binds, and the shapes the
/// reorder declines answer identically either way. The strategy each shape
/// resolves to is asserted in <c>SqlServerSimulator.Tests.Internal</c>'s
/// <c>JoinStrategyTests</c>.
/// </summary>
[TestClass]
public sealed class JoinPredicatePushdownTests
{
    /// <summary>
    /// A five-table star-into-chain: 3 regions, 7 customers (one of them with no
    /// orders), 12 orders, 24 order lines over 4 items. Every join column is a
    /// primary key on one side and indexed on the other, so a filter anywhere in
    /// the chain can seek.
    /// </summary>
    private static Simulation Sales()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table region (region_id int not null primary key, region_name varchar(20) not null);
            create table cust (cust_id int not null primary key, region_id int not null, cust_name varchar(20) not null);
            create table ord (ord_id int not null primary key, cust_id int not null, ord_total int not null);
            create table item (item_id int not null primary key, item_name varchar(20) not null);
            create table line (line_id int not null primary key, ord_id int not null, item_id int not null, qty int not null);
            create index ix_region_name on region (region_name);
            create index ix_cust_region on cust (region_id);
            create index ix_ord_cust on ord (cust_id);
            create index ix_ord_total on ord (ord_total);
            create index ix_line_ord on line (ord_id);
            create index ix_line_item on line (item_id);
            insert region values (1, 'north'), (2, 'south'), (3, 'east');
            insert cust values (1, 1, 'a'), (2, 1, 'b'), (3, 2, 'c'), (4, 2, 'd'),
                               (5, 3, 'e'), (6, 3, 'f'), (7, 1, 'g');
            insert item values (100, 'w'), (200, 'x'), (300, 'y'), (400, 'z');
            insert ord values (11, 1, 10), (12, 1, 20), (21, 2, 30), (22, 2, 40),
                              (31, 3, 50), (32, 3, 60), (41, 4, 70), (42, 4, 80),
                              (51, 5, 90), (52, 5, 100), (61, 6, 110), (62, 6, 120);
            insert line values
                (1, 11, 100, 1), (2, 11, 200, 2), (3, 12, 100, 3), (4, 12, 300, 4),
                (5, 21, 100, 5), (6, 21, 200, 6), (7, 22, 300, 7), (8, 22, 400, 8),
                (9, 31, 100, 9), (10, 31, 200, 10), (11, 32, 300, 11), (12, 32, 400, 12),
                (13, 41, 100, 13), (14, 41, 200, 14), (15, 42, 300, 15), (16, 42, 400, 16),
                (17, 51, 100, 17), (18, 51, 200, 18), (19, 52, 300, 19), (20, 52, 400, 20),
                (21, 61, 100, 21), (22, 61, 200, 22), (23, 62, 300, 23), (24, 62, 400, 24)
            """);
        return sim;
    }

    /// <summary>Every row of a result set, each rendered as its pipe-joined values.</summary>
    private static List<string> Rows(Simulation simulation, string commandText)
    {
        using var reader = simulation.ExecuteReader(commandText);
        var rows = new List<string>();
        foreach (var record in reader.EnumerateRecords())
        {
            var values = new object[record.FieldCount];
            _ = record.GetValues(values);
            rows.Add(string.Join("|", values));
        }

        return rows;
    }

    // ---- the reordered shape -------------------------------------------------

    /// <summary>
    /// The motivating shape: a five-table INNER chain written fact-table-first
    /// with the selective equality on the <em>third</em> source. Customer 2 owns
    /// orders 21 and 22, whose four lines carry qty 5 + 6 + 7 + 8.
    /// </summary>
    [TestMethod]
    public void MidChainEqualityFilter_SumsOnlyTheFilteredSourcesRows()
        => AreEqual(26, Sales().ExecuteScalar("""
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id = c.region_id
            join item i on i.item_id = l.item_id
            where c.cust_id = 2
            """));

    /// <summary>
    /// The same query hand-written to drive from the filtered source — the order
    /// the reorder produces — answers the same, which is the equivalence the
    /// reorder rests on.
    /// </summary>
    [TestMethod]
    public void MidChainEqualityFilter_MatchesTheHandReorderedSpelling()
        => AreEqual(26, Sales().ExecuteScalar("""
            select sum(l.qty) from cust c
            join ord o on o.cust_id = c.cust_id
            join region r on r.region_id = c.region_id
            join line l on l.ord_id = o.ord_id
            join item i on i.item_id = l.item_id
            where c.cust_id = 2
            """));

    /// <summary>A filter on the last-named source reorders the same way.</summary>
    [TestMethod]
    public void LastSourceEqualityFilter_SumsOnlyThatSourcesRows()
        => AreEqual(69, Sales().ExecuteScalar("""
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join item i on i.item_id = l.item_id
            where i.item_id = 100
            """));

    /// <summary>
    /// The full row set, not just an aggregate: every projected column still
    /// reads from the source it names after the slots move, and the ORDER BY
    /// still orders by the column it named.
    /// </summary>
    [TestMethod]
    public void MidChainEqualityFilter_ProjectsEverySourcesColumns()
    {
        var rows = Rows(Sales(), """
            select c.cust_name, r.region_name, o.ord_id, i.item_name, l.qty
            from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id = c.region_id
            join item i on i.item_id = l.item_id
            where c.cust_id = 2
            order by l.qty
            """);
        HasCount(4, rows);
        AreEqual("b|north|21|w|5", rows[0]);
        AreEqual("b|north|22|z|8", rows[3]);
    }

    /// <summary>
    /// An ON conjunct naming a source two levels to its left still binds after
    /// the reorder: <c>region</c>'s ON reads <c>cust</c>, which the reorder
    /// places at a different step than the written order did.
    /// </summary>
    [TestMethod]
    public void OnConjunctNamingADistantSource_StillBindsAfterTheReorder()
        => AreEqual(26, Sales().ExecuteScalar("""
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id = c.region_id
            where c.cust_id = 2
            """));

    /// <summary>
    /// An ON conjunct whose two sources are <em>both</em> already placed when its
    /// own level is reached has to re-attach at the step that completed the pair,
    /// not be dropped: <c>o.cust_id = c.cust_id</c> is written on region's level
    /// while the reorder places region before <c>ord</c>. Dropping it would let
    /// every order join customer 2.
    /// </summary>
    [TestMethod]
    public void OnConjunctBetweenTwoEarlierSources_ReattachesAtTheCompletingStep()
        => AreEqual(26, Sales().ExecuteScalar("""
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id = c.region_id and o.cust_id = c.cust_id
            where c.cust_id = 2
            """));

    /// <summary>
    /// Two equalities pinning two different sources: order 21 holds lines with
    /// qty 5 and 6, and customer 2 does sit in the 'north' region, so both
    /// conjuncts have to survive the reorder as residual filters.
    /// </summary>
    [TestMethod]
    public void TwoNarrowedSources_StillApplyBothFilters()
        => AreEqual(11, Sales().ExecuteScalar("""
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id = c.region_id
            where o.ord_id = 21 and r.region_name = 'north'
            """));

    /// <summary>
    /// A materialized derived table can be a reorder <em>member</em> — its rows
    /// are fixed for the enumeration — though never the driver, since only a
    /// seek-narrowed base table drives.
    /// </summary>
    [TestMethod]
    public void MaterializedDerivedTable_ParticipatesAsAReorderMember()
        => AreEqual(26, Sales().ExecuteScalar("""
            select sum(d.qty) from ord o
            join cust c on c.cust_id = o.cust_id
            join (select ord_id, qty from line) d on d.ord_id = o.ord_id
            where c.cust_id = 2
            """));

    // ---- join kinds the reorder declines, pushdown still applies -------------

    /// <summary>
    /// LEFT JOIN with the filter on a third, INNER-joined source: the customer
    /// with no orders keeps its NULL-extended row, so region 'north' reports
    /// customers 1 and 2 (two orders each) plus customer 7's single NULL row.
    /// </summary>
    [TestMethod]
    public void LeftJoin_FilterOnAnotherSource_KeepsNullExtendedRows()
        => AreEqual(5, Sales().ExecuteScalar("""
            select count(*) from cust c
            left join ord o on o.cust_id = c.cust_id
            join region r on r.region_id = c.region_id
            where r.region_name = 'north'
            """));

    /// <summary>…and the NULL-extended row is genuinely the order-less customer.</summary>
    [TestMethod]
    public void LeftJoin_FilterOnAnotherSource_NullExtendsTheOrderlessCustomer()
        => AreEqual("g", Sales().ExecuteScalar("""
            select c.cust_name from cust c
            left join ord o on o.cust_id = c.cust_id
            join region r on r.region_id = c.region_id
            where r.region_name = 'north' and o.ord_id is null
            """));

    /// <summary>
    /// A WHERE equality on the NULL-supplied side of a LEFT JOIN narrows it, and
    /// the same conjunct still excludes every row the narrowing would have
    /// NULL-extended — the residual invariant that makes the pushdown safe for
    /// outer joins.
    /// </summary>
    [TestMethod]
    public void LeftJoin_FilterOnTheNullSuppliedSide_AnswersAsAnInnerJoin()
        => AreEqual(1, Sales().ExecuteScalar("""
            select count(*) from cust c
            left join ord o on o.cust_id = c.cust_id
            where o.ord_total = 30
            """));

    /// <summary>
    /// RIGHT JOIN whose ON can never match: every order emits with the customer
    /// side NULL-filled, and the WHERE equality on that preserved right source
    /// narrows it to customer 2's two orders.
    /// </summary>
    [TestMethod]
    public void RightJoin_FilterOnThePreservedSide_KeepsTheUnmatchedRightRows()
        => AreEqual(2, Sales().ExecuteScalar("""
            select count(*) from cust c
            right join ord o on o.cust_id = c.cust_id and c.cust_id > 100
            where o.cust_id = 2
            """));

    /// <summary>
    /// FULL JOIN with a filter on the right source: the unmatched-left rows the
    /// operator emits carry a NULL for that column, so the residual conjunct
    /// drops them exactly as it did before the narrowing.
    /// </summary>
    [TestMethod]
    public void FullJoin_FilterOnTheRightSource_AnswersTheNarrowedRowsOnly()
        => AreEqual(1, Sales().ExecuteScalar("""
            select count(*) from cust c
            full join ord o on o.cust_id = c.cust_id
            where o.ord_id = 21
            """));

    /// <summary>
    /// CROSS APPLY's right side is lateral, so the chain never reorders and the
    /// per-outer-row execution stands: customer 2's largest order total.
    /// </summary>
    [TestMethod]
    public void CrossApply_FilterOnTheOuterSource_KeepsTheLateralSemantics()
        => AreEqual(40, Sales().ExecuteScalar("""
            select t.ord_total from cust c
            cross apply (select top (1) o.ord_total from ord o
                         where o.cust_id = c.cust_id order by o.ord_total desc) t
            where c.cust_id = 2
            """));

    // ---- shapes the reorder declines ----------------------------------------

    /// <summary>
    /// A non-equi ON conjunct can't be read as a join-graph edge, so the chain
    /// keeps its written order — and answers what it always did: customer 5's
    /// two orders against the two regions below its own.
    /// </summary>
    [TestMethod]
    public void NonEquiOnPredicate_KeepsTheWrittenOrderAndTheSameRows()
        => AreEqual(4, Sales().ExecuteScalar("""
            select count(*) from ord o
            join cust c on c.cust_id = o.cust_id
            join region r on r.region_id < c.region_id
            where c.cust_id = 5
            """));

    /// <summary>
    /// An ON clause that names none of its own level's columns leaves that
    /// source disconnected from the join graph; the reorder declines rather than
    /// guessing, and the cross product it implies is still produced (customer
    /// 5's two orders × four items).
    /// </summary>
    [TestMethod]
    public void DisconnectedJoinGraph_KeepsTheWrittenOrderAndTheSameRows()
        => AreEqual(8, Sales().ExecuteScalar("""
            select count(*) from ord o
            join cust c on c.cust_id = o.cust_id
            join item i on c.cust_id = o.cust_id
            where c.cust_id = 5
            """));

    // ---- equivalence over a seeded set --------------------------------------

    private static Simulation SeededChain()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table dim (dim_id int not null primary key, bucket int not null);
            create table mid (mid_id int not null primary key, dim_id int not null);
            create table fact (fact_id int not null primary key, mid_id int not null, amount int not null);
            create index ix_mid_dim on mid (dim_id);
            create index ix_fact_mid on fact (mid_id);
            declare @i int = 1;
            while @i <= 50 begin insert dim values (@i, @i % 7); set @i += 1; end
            set @i = 1;
            while @i <= 200 begin insert mid values (@i, (@i % 50) + 1); set @i += 1; end
            set @i = 1;
            while @i <= 1000 begin insert fact values (@i, (@i % 200) + 1, @i * 3); set @i += 1; end
            """);
        return sim;
    }

    /// <summary>
    /// A thousand-row chain where the reorder engages, checked against the same
    /// query with the filtered source written first (which doesn't reorder — its
    /// narrowed source already drives). Both spellings agree on the count, the
    /// sum and the extremes, and the filter really does select rows.
    /// </summary>
    [TestMethod]
    public void SeededChain_ReorderedAndWrittenOrderFormsAgree()
    {
        var sim = SeededChain();
        var reordered = Rows(sim, """
            select count(*), sum(f.amount), min(f.amount), max(f.amount)
            from fact f
            join mid m on m.mid_id = f.mid_id
            join dim d on d.dim_id = m.dim_id
            where d.dim_id = 17
            """);
        var writtenFirst = Rows(sim, """
            select count(*), sum(f.amount), min(f.amount), max(f.amount)
            from dim d
            join mid m on m.dim_id = d.dim_id
            join fact f on f.mid_id = m.mid_id
            where d.dim_id = 17
            """);
        HasCount(1, reordered);
        AreEqual(writtenFirst[0], reordered[0]);
        AreEqual("20|29400|45|2895", reordered[0]);
    }

    /// <summary>
    /// The same seeded set with a range bound rather than an equality: the range
    /// seek narrows too, and the reorder's driver choice still answers what the
    /// written order does.
    /// </summary>
    [TestMethod]
    public void SeededChain_RangeNarrowedDimension_AgreesWithTheWrittenOrder()
    {
        var sim = SeededChain();
        var reordered = Rows(sim, """
            select count(*), sum(f.amount) from fact f
            join mid m on m.mid_id = f.mid_id
            join dim d on d.dim_id = m.dim_id
            where d.dim_id between 3 and 5
            """);
        var writtenFirst = Rows(sim, """
            select count(*), sum(f.amount) from dim d
            join mid m on m.dim_id = d.dim_id
            join fact f on f.mid_id = m.mid_id
            where d.dim_id between 3 and 5
            """);
        AreEqual(writtenFirst[0], reordered[0]);
    }

    // ---- a correlated inner whose own FROM is a join ------------------------

    /// <summary>
    /// The pushdown's probe value may name a column that escapes the plan's own
    /// FROM — the enclosing query's row, fixed for one execution of the inner
    /// plan — so a correlated subquery reading through a join narrows the same
    /// way a single-table one does. What matters here is that it still answers
    /// identically: each customer's own lines, and NULL for the one with no
    /// orders.
    /// </summary>
    [TestMethod]
    public void CorrelatedSubqueryOverAJoin_TotalsPerOuterRow()
    {
        var rows = Rows(Sales(), """
            select c.cust_id,
                   (select sum(l.qty) from ord o join line l on l.ord_id = o.ord_id
                    where o.cust_id = c.cust_id) as total
            from cust c
            order by c.cust_id
            """);
        HasCount(7, rows);
        AreEqual("1|10", rows[0]);
        AreEqual("2|26", rows[1]);
        AreEqual("3|42", rows[2]);
        AreEqual("4|58", rows[3]);
        AreEqual("5|74", rows[4]);
        AreEqual("6|90", rows[5]);
        AreEqual("7|", rows[6]);
    }

    /// <summary>
    /// …and agrees column for column with the grouped derived-table spelling of
    /// the same question, which reads every source once.
    /// </summary>
    [TestMethod]
    public void CorrelatedSubqueryOverAJoin_MatchesTheGroupedSpelling()
    {
        var sim = Sales();
        var correlated = Rows(sim, """
            select c.cust_id,
                   (select sum(l.qty) from ord o join line l on l.ord_id = o.ord_id
                    where o.cust_id = c.cust_id) as total
            from cust c
            order by c.cust_id
            """);
        var grouped = Rows(sim, """
            select c.cust_id, g.total from cust c
            left join (select o.cust_id, sum(l.qty) as total from ord o
                       join line l on l.ord_id = o.ord_id group by o.cust_id) g
              on g.cust_id = c.cust_id
            order by c.cust_id
            """);
        AreEqual(string.Join(";", grouped), string.Join(";", correlated));
    }

    /// <summary>
    /// Correlation through two levels: the innermost plan's probe names the
    /// outermost query's column, reached through chained enclosing resolvers.
    /// </summary>
    [TestMethod]
    public void DepthTwoCorrelatedSubqueryOverAJoin_TotalsPerOutermostRow()
    {
        var rows = Rows(Sales(), """
            select c.cust_id,
                   (select (select sum(l.qty) from ord o join line l on l.ord_id = o.ord_id
                            where o.cust_id = c.cust_id)
                    from region r where r.region_id = c.region_id) as total
            from cust c
            where c.cust_id in (2, 7)
            order by c.cust_id
            """);
        HasCount(2, rows);
        AreEqual("2|26", rows[0]);
        AreEqual("7|", rows[1]);
    }

    /// <summary>
    /// <c>EXISTS</c> over a joined inner: the customers who bought item 400.
    /// </summary>
    [TestMethod]
    public void CorrelatedExistsOverAJoin_FiltersByMatch()
    {
        var rows = Rows(Sales(), """
            select c.cust_id from cust c
            where exists (select 1 from ord o join line l on l.ord_id = o.ord_id
                          where o.cust_id = c.cust_id and l.item_id = 400)
            order by c.cust_id
            """);
        AreEqual("2;3;4;5;6", string.Join(";", rows));
    }

    /// <summary>
    /// <c>IN (SELECT …)</c> over a joined inner: only the customer whose lines
    /// carry a quantity above 20.
    /// </summary>
    [TestMethod]
    public void CorrelatedInOverAJoin_FiltersByMatch()
    {
        var rows = Rows(Sales(), """
            select c.cust_id from cust c
            where c.cust_id in (select o.cust_id from ord o join line l on l.ord_id = o.ord_id
                                where o.cust_id = c.cust_id and l.qty > 20)
            order by c.cust_id
            """);
        AreEqual("6", string.Join(";", rows));
    }

    /// <summary>
    /// A WHERE conjunct equating two sources of the <em>same</em> query is not
    /// an escaping value — it can't anchor a pushdown seek, since neither side
    /// is readable before the join runs. Here it merely restates the ON, so
    /// every order still counts.
    /// </summary>
    [TestMethod]
    public void SiblingSourceEquality_KeepsEveryJoinedRow()
        => AreEqual(12, Sales().ExecuteScalar("""
            select count(*) from cust c join ord o on o.cust_id = c.cust_id
            where c.cust_id = o.cust_id
            """));

    /// <summary>
    /// The reorder is decided per execution off the seek's own candidate counts,
    /// so a replayed plan sees rows written between runs rather than the first
    /// execution's shape.
    /// </summary>
    [TestMethod]
    public void RepeatedExecution_SeesRowsWrittenBetweenRuns()
    {
        var sim = Sales();
        const string query = """
            select sum(l.qty) from line l
            join ord o on o.ord_id = l.ord_id
            join cust c on c.cust_id = o.cust_id
            where c.cust_id = 2
            """;
        AreEqual(26, sim.ExecuteScalar(query));
        _ = sim.ExecuteNonQuery("insert line values (99, 21, 100, 100)");
        AreEqual(126, sim.ExecuteScalar(query));
    }
}
