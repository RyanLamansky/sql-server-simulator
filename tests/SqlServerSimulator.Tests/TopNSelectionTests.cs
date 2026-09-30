using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An ORDER BY no index serves ranks its rows by their keys alone and projects
/// only the rows its <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> window returns,
/// from a bounded heap or from a buffer it selects the window out of. These pin
/// that every strategy answers with the uncapped full sort's rows in its order,
/// ties included, and that the select list runs only for the rows returned.
/// </summary>
[TestClass]
public sealed class TopNSelectionTests
{
    /// <summary>
    /// 6000 rows with a three-valued sort key and a clustered key giving the
    /// arrival order, so every page cuts through a 2000-row tie group.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null primary key, k int not null, s varchar(10) not null);
            insert t select value, value % 3, choose(value % 4 + 1, 'a', 'A', 'a ', 'b') from generate_series(1, 6000)
            """);
        return simulation;
    }

    private static List<int> Ids(Simulation simulation, string sql)
    {
        var ids = new List<int>();
        using var reader = simulation.ExecuteReader(sql);
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    [TestMethod]
    [DataRow("k", 0, 20)]
    [DataRow("k", 100, 20)]
    [DataRow("k desc", 4070, 20)]
    [DataRow("k desc", 4500, 20)]
    [DataRow("s, k desc", 1990, 25)]
    [DataRow("s desc", 5990, 20)]
    [DataRow("k", 7000, 20)]
    public void OffsetFetch_MatchesTheFullSortsPage(string order, int offset, int fetch)
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, $"select id from t order by {order}").Skip(offset).Take(fetch).ToList(),
            Ids(simulation, $"select id from t order by {order} offset {offset} rows fetch next {fetch} rows only"));
    }

    [TestMethod]
    public void OffsetWithoutFetch_ReturnsTheFullSortsTail()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from t order by s desc").Skip(5950).ToList(),
            Ids(simulation, "select id from t order by s desc offset 5950 rows"));
    }

    [TestMethod]
    public void Ties_KeepArrivalOrder_InEveryStrategy()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(new[] { 3, 6, 9, 12 }, Ids(simulation, "select top (4) id from t order by k"));
        CollectionAssert.AreEqual(new[] { 5991, 5994, 5997, 6000 }, Ids(simulation, "select id from t order by k offset 1996 rows fetch next 4 rows only"));
        CollectionAssert.AreEqual(new[] { 1, 4, 7 }, Ids(simulation, "select id from t order by k desc offset 2000 rows fetch next 3 rows only"));
    }

    [TestMethod]
    public void EfSkipTake_ParametersPageTheFullSort()
    {
        var simulation = Seeded();
        var full = Ids(simulation, "select id from t order by s, id");
        using var connection = simulation.CreateOpenConnection();
        foreach (var (skip, take) in new[] { (10, 5), (5000, 30), (10, 5) })
        {
            var page = new List<int>();
            using var command = connection.CreateCommand(
                "select id from t order by s, id offset @p rows fetch next @q rows only", ("@p", skip), ("@q", take));
            using var reader = command.ExecuteReader();
            while (reader.Read())
                page.Add(reader.GetInt32(0));
            CollectionAssert.AreEqual(full.Skip(skip).Take(take).ToList(), page);
        }
    }

    /// <summary>
    /// A row tying the boundary can arrive before the rows that push the
    /// boundary down to it: ids 1-4 (k = 5) are held, then evicted as ties of
    /// the root, until 5 and 6 (k = 3) settle it, and 8 ties it again.
    /// </summary>
    [TestMethod]
    public void WithTies_FollowsTheBoundaryAsItMoves()
        => CollectionAssert.AreEqual(
            new[] { 7, 5, 6, 8 },
            Ids(new Simulation(), """
                create table w (id int not null primary key, k int not null);
                insert w values (1, 5), (2, 5), (3, 5), (4, 5), (5, 3), (6, 3), (7, 1), (8, 3);
                select top (2) with ties id from w order by k
                """));

    [TestMethod]
    [DataRow(1)]
    [DataRow(1999)]
    [DataRow(2000)]
    [DataRow(4097)]
    public void WithTies_MatchesTheFullSortsPrefix(int n)
    {
        var simulation = Seeded();
        var full = Ids(simulation, "select id from t order by k desc");
        var groupsReached = n <= 2000 ? 1 : n <= 4000 ? 2 : 3;
        CollectionAssert.AreEqual(full.Take(groupsReached * 2000).ToList(), Ids(simulation, $"select top ({n}) with ties id from t order by k desc"));
    }

    [TestMethod]
    public void Percent_PagesTheFullSort()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from t order by s desc, k").Take(301).ToList(),
            Ids(simulation, "select top (5.01) percent id from t order by s desc, k"));
    }

    /// <summary>
    /// Real computes the select list above its Sort and Top, so a row outside
    /// the window never evaluates it (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SelectList_RunsOnlyForReturnedRows()
    {
        var simulation = Seeded();
        HasCount(3, Ids(simulation, "select top (3) id, 1 / (id - 5) from t order by k, id"));
        HasCount(5, Ids(simulation, "select id, 1 / (id - 5) from t order by k, id offset 10 rows fetch next 5 rows only"));
        HasCount(5, Ids(simulation, "select id, 1 / (id - 5) from t order by k, id offset 5000 rows fetch next 5 rows only"));
    }

    [TestMethod]
    public void SelectList_StillRaisesForAReturnedRow()
        => _ = Seeded().AssertSqlError("select top (3) id, 1 / (id - 5) from t order by k desc, id", 8134);

    /// <summary>A sort key naming the column by alias is computed for every row, as real's is.</summary>
    [TestMethod]
    public void SortKeyByAlias_EvaluatesForEveryRow()
        => _ = Seeded().AssertSqlError("select top (3) id, 1 / (id - 5) as x from t order by x", 8134);

    /// <summary>The value a row sorts by is the value it returns, even when drawn per call.</summary>
    [TestMethod]
    [DataRow("select top (40) checksum(newid()) as r from t order by r")]
    [DataRow("select checksum(newid()) as r from t order by 1 offset 4500 rows fetch next 40 rows only")]
    public void AVolatileSortKey_IsTheValueReturned(string sql)
    {
        var values = Ids(Seeded(), sql);
        HasCount(40, values);
        CollectionAssert.AreEqual(values.Order().ToList(), values);
    }

    /// <summary>A bounded row number past the heap's ceiling selects rather than sorts, and numbers identically.</summary>
    [TestMethod]
    public void BoundedRowNumber_PastTheHeapCeiling_MatchesTheUnboundedNumbering()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Ids(simulation, "select id from (select id, row_number() over (order by s, k) rn from t) x where rn + 0 between 5001 and 5010 order by rn"),
            Ids(simulation, "select id from (select id, row_number() over (order by s, k) rn from t) x where rn between 5001 and 5010 order by rn"));
    }
}
