using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The rows a query keeps when a WHERE conjunct written above a view / derived
/// table / CTE is pushed into that body so it reaches the base scan: a chain of
/// views answers what the equivalent inline query answers, a renamed column maps
/// through, an outer join's NULL extension survives, and every shape the push
/// declines answers exactly as it did. Each asserted value was probed against
/// SQL Server 2025 first for the shapes where the push could plausibly change
/// one (an outer join filtered on its NULL-extendable side, its <c>IS NULL</c>
/// anti-join mirror, a body carrying its own WHERE, a doubly-renamed column).
/// The strategy each shape resolves to is asserted in
/// <c>SqlServerSimulator.Tests.Internal</c>'s
/// <c>ViewPredicatePushdownStrategyTests</c>.
/// </summary>
[TestClass]
public sealed class ViewPredicatePushdownTests
{
    /// <summary>
    /// Three customers (one with no orders), four orders, and a five-deep view
    /// chain over them. Every filtered column is indexed on the base table, so a
    /// pushed conjunct has somewhere to land.
    /// </summary>
    private static Simulation Sales()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table cust (cust_id int not null primary key, cust_name varchar(20) not null);
            create table ord (ord_id int not null primary key, cust_id int not null, region_id int not null, total int not null);
            create index ix_ord_cust on ord (cust_id);
            insert cust values (10, 'a'), (20, 'b'), (30, 'c');
            insert ord values (1, 10, 1, 100), (2, 10, 2, 200), (3, 20, 1, 300), (4, 20, 2, 400)
            """);
        foreach (var body in new[]
        {
            "create view v1 as select ord_id, cust_id, region_id, total from ord",
            "create view v2 as select ord_id, cust_id, region_id, total from v1 where total > 0",
            "create view v3 as select ord_id, cust_id, region_id, total from v2 where region_id > 0",
            "create view v4 as select ord_id, cust_id, region_id, total from v3 where ord_id > 0",
            "create view v5 as select ord_id, cust_id, region_id, total from v4 where total > 50",
        })
        {
            _ = sim.ExecuteNonQuery(body);
        }

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

    // ---- the chain -----------------------------------------------------------

    /// <summary>The motivating shape: the five-deep chain answers what the base table answers.</summary>
    [TestMethod]
    public void FiveDeepViewChain_FilteredOnAKey_AnswersTheBaseTablesRows()
    {
        var sim = Sales();
        AreEqual(
            string.Join(",", Rows(sim, "select ord_id, total from ord where cust_id = 10 order by ord_id")),
            string.Join(",", Rows(sim, "select ord_id, total from v5 where cust_id = 10 order by ord_id")));
    }

    /// <summary>The aggregate spelling the workload measures — one row, over the filtered chain.</summary>
    [TestMethod]
    public void FiveDeepViewChain_Aggregated_CountsOnlyTheFilteredRows() =>
        CollectionAssert.AreEqual(
            (string[])["2|200"], Rows(Sales(), "select count(*), max(total) from v5 where cust_id = 10"));

    /// <summary>The inline derived-table chain answers identically.</summary>
    [TestMethod]
    public void FiveDeepDerivedTableChain_FilteredOnAKey_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["1|100", "2|200"], Rows(Sales(), """
            select ord_id, total from (
              select * from (
                select * from (
                  select * from (
                    select ord_id, cust_id, region_id, total from ord
                  ) d1 where total > 0
                ) d2 where region_id > 0
              ) d3 where ord_id > 0
            ) d4 where total > 50 and cust_id = 10 order by ord_id
            """));

    /// <summary>A chain whose every level renames the column keeps mapping it by ordinal.</summary>
    [TestMethod]
    public void DoublyRenamedColumn_FiltersTheBaseColumnBehindIt()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view r1 as select ord_id as o, cust_id as c from ord");
        _ = sim.ExecuteNonQuery("create view r2 as select o, c as k from r1");
        CollectionAssert.AreEqual((string[])["10|1", "10|2"], Rows(sim, "select k, o from r2 where k = 10 order by o"));
    }

    /// <summary>A body's own WHERE still decides first: only the row passing both survives.</summary>
    [TestMethod]
    public void BodyWithItsOwnWhere_AppliesBothConjuncts()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vf as select ord_id, cust_id from ord where total > 150");
        CollectionAssert.AreEqual((string[])["2"], Rows(sim, "select ord_id from vf where cust_id = 10 order by ord_id"));
    }

    /// <summary>A CTE reference takes the push and answers the same rows.</summary>
    [TestMethod]
    public void CteBody_FilteredAbove_AnswersTheFilteredRows() =>
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(Sales(),
            "with c as (select ord_id, cust_id from ord) select ord_id from c where cust_id = 10 order by ord_id"));

    /// <summary>A range bound and an IN list push as the equality does.</summary>
    [TestMethod]
    public void RangeAndInListConjuncts_AnswerTheirOwnRows()
    {
        var sim = Sales();
        CollectionAssert.AreEqual((string[])["3", "4"], Rows(sim, "select ord_id from v5 where cust_id > 15 order by ord_id"));
        CollectionAssert.AreEqual((string[])["1", "2", "3", "4"], Rows(sim, "select ord_id from v5 where cust_id in (10, 20) order by ord_id"));
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(sim, "select ord_id from v5 where cust_id between 5 and 15 order by ord_id"));
    }

    /// <summary>A parameterized filter pushes its value, not its variable — a view body's batch holds none.</summary>
    [TestMethod]
    public void VariableComparand_FiltersByItsValue() =>
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(Sales(),
            "declare @c int = 10; select ord_id from v5 where cust_id = @c order by ord_id"));

    /// <summary>A NULL comparand excludes every row, above a body as it does over a table.</summary>
    [TestMethod]
    public void NullComparand_AnswersNoRows() =>
        IsEmpty(Rows(Sales(), "declare @c int; select ord_id from v5 where cust_id = @c"));

    // ---- outer joins: the residual conjunct is what keeps them right ----------

    /// <summary>
    /// A LEFT JOIN filtered on its NULL-extendable side keeps only the matched
    /// rows — the pushed conjunct narrows the view, and the residual excludes the
    /// tuples the narrowing NULL-extended (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void LeftJoinFilteredOnThePushedViewSide_KeepsOnlyTheMatchedRows() =>
        CollectionAssert.AreEqual((string[])["10|1", "10|2"], Rows(Sales(), """
            select c.cust_id, v.ord_id from cust c left join v5 v on v.cust_id = c.cust_id
            where v.cust_id = 10 order by c.cust_id, v.ord_id
            """));

    /// <summary>
    /// Its anti-join mirror: <c>IS NULL</c> isn't NULL-rejecting, so it is never
    /// pushed — pushing it would turn every filtered-out row into a NULL-extended
    /// match. The customer with no orders is the only answer, on both engines.
    /// </summary>
    [TestMethod]
    public void LeftJoinAntiJoinAgainstAView_KeepsOnlyTheUnmatchedRow() =>
        CollectionAssert.AreEqual((string[])["30|"], Rows(Sales(), """
            select c.cust_id, v.ord_id from cust c left join v5 v on v.cust_id = c.cust_id
            where v.cust_id is null order by c.cust_id
            """));

    /// <summary>A view on the preserved side of a RIGHT JOIN filters the same way.</summary>
    [TestMethod]
    public void RightJoinWithThePushedViewOnTheLeft_KeepsOnlyTheMatchedRows() =>
        CollectionAssert.AreEqual((string[])["1|10", "2|10"], Rows(Sales(), """
            select v.ord_id, c.cust_id from v5 v right join cust c on v.cust_id = c.cust_id
            where v.cust_id = 10 order by v.ord_id
            """));

    /// <summary>An OUTER APPLY body takes the push with its own correlation intact.</summary>
    [TestMethod]
    public void OuterApplyBody_FilteredAbove_KeepsOnlyTheMatchedRows() =>
        CollectionAssert.AreEqual((string[])["10|1", "10|2"], Rows(Sales(), """
            select c.cust_id, o.ord_id from cust c
            outer apply (select ord_id, cust_id, total from ord where ord.cust_id = c.cust_id) o
            where o.cust_id = 10 order by c.cust_id, o.ord_id
            """));

    /// <summary>
    /// A joined view materializes once per enumeration <em>after</em> the push,
    /// so what it materializes is already narrowed — and the rows are the ones
    /// the join produced before either pass existed.
    /// </summary>
    [TestMethod]
    public void ViewJoinedToATable_AnswersTheJoinedRows() =>
        CollectionAssert.AreEqual((string[])["10|a|1", "10|a|2"], Rows(Sales(), """
            select c.cust_id, c.cust_name, v.ord_id from cust c join v5 v on v.cust_id = c.cust_id
            where v.cust_id = 10 order by v.ord_id
            """));

    // ---- the declines answer identically -------------------------------------

    /// <summary>A TOP body's rows are the ones it chose, not the ones a pushed filter would have left it.</summary>
    [TestMethod]
    public void TopBody_AnswersTheRowsItChose()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vt as select top 2 ord_id, cust_id from ord order by ord_id desc");
        IsEmpty(Rows(sim, "select ord_id from vt where cust_id = 10"));
    }

    /// <summary>An OFFSET / FETCH body declines for the same reason.</summary>
    [TestMethod]
    public void OffsetFetchBody_AnswersTheRowsItChose() =>
        CollectionAssert.AreEqual((string[])["2"], Rows(Sales(), """
            select ord_id from (
              select ord_id, cust_id from ord order by ord_id offset 1 rows fetch next 2 rows only
            ) d where cust_id = 10 order by ord_id
            """));

    /// <summary>A DISTINCT body answers its distinct rows, filtered after.</summary>
    [TestMethod]
    public void DistinctBody_AnswersItsDistinctRows()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vd as select distinct cust_id from ord");
        CollectionAssert.AreEqual((string[])["10"], Rows(sim, "select cust_id from vd where cust_id = 10"));
    }

    /// <summary>A grouped body's aggregate covers the whole group, not the filtered part of it.</summary>
    [TestMethod]
    public void GroupedBody_AggregatesTheWholeGroup()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vg as select cust_id, count(*) as n, sum(total) as s from ord group by cust_id");
        CollectionAssert.AreEqual((string[])["10|2|300"], Rows(sim, "select cust_id, n, s from vg where cust_id = 10"));
    }

    /// <summary>A windowed body numbers every row it produced, filtered after.</summary>
    [TestMethod]
    public void WindowedBody_NumbersEveryRowItProduced()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vw as select ord_id, cust_id, row_number() over (order by ord_id) as rn from ord");
        CollectionAssert.AreEqual((string[])["1|1", "2|2"], Rows(sim, "select ord_id, rn from vw where cust_id = 10 order by ord_id"));
    }

    /// <summary>A set-operation body answers both branches, filtered after.</summary>
    [TestMethod]
    public void SetOperationBody_AnswersBothBranches()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("""
            create view vu as select ord_id, cust_id from ord where total > 150
            union all select ord_id, cust_id from ord where total <= 150
            """);
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(sim, "select ord_id from vu where cust_id = 10 order by ord_id"));
    }

    /// <summary>A computed output column filters as its computed value, above the body.</summary>
    [TestMethod]
    public void ExpressionProjection_FiltersOnTheComputedValue()
    {
        var sim = Sales();
        _ = sim.ExecuteNonQuery("create view vx as select ord_id, cust_id * 10 as scaled from ord");
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(sim, "select ord_id from vx where scaled = 100 order by ord_id"));
    }

    /// <summary>A conjunct over an expression of the column answers the same rows it always did.</summary>
    [TestMethod]
    public void ExpressionOverTheColumn_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(Sales(),
            "select ord_id from v5 where cust_id + 5 = 15 order by ord_id"));

    /// <summary>
    /// A conjunct pairing the view's column with a sibling source's isn't the
    /// body's to apply — the sibling name means nothing in there, and an ordinal
    /// that happened to land would filter on the wrong column.
    /// </summary>
    [TestMethod]
    public void ConjunctPairingTheViewWithASibling_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["10|1", "10|2", "20|3", "20|4"], Rows(Sales(), """
            select c.cust_id, v.ord_id from cust c join v5 v on 1 = 1
            where v.cust_id = c.cust_id order by c.cust_id, v.ord_id
            """));

    /// <summary>An OR across two sources isn't one source's conjunct, so it filters where it was written.</summary>
    [TestMethod]
    public void OrAcrossTwoSources_AnswersTheSameRows() =>
        CollectionAssert.AreEqual((string[])["10|1", "10|2", "20|3", "20|4"], Rows(Sales(), """
            select c.cust_id, v.ord_id from cust c join v5 v on v.cust_id = c.cust_id
            where v.cust_id = 10 or c.cust_name = 'b' order by c.cust_id, v.ord_id
            """));

    /// <summary>
    /// A skipped branch's view read raises nothing — the pass runs from the row
    /// source, which a skipped statement never reaches — and leaves the view
    /// answering normally after it.
    /// </summary>
    [TestMethod]
    public void SkippedBranchReadingAView_RaisesNothing()
    {
        var sim = Sales();
        AreEqual(-1, sim.ExecuteNonQuery("if 1 = 0 begin declare @c int = 10; select ord_id from v5 where cust_id = @c end"));
        CollectionAssert.AreEqual((string[])["1", "2"], Rows(sim, "select ord_id from v5 where cust_id = 10 order by ord_id"));
    }
}
