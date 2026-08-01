using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for ranking windows (<c>ROW_NUMBER</c> / <c>RANK</c> /
/// <c>DENSE_RANK</c> / <c>NTILE</c>) and value windows (<c>LAG</c> /
/// <c>LEAD</c> / <c>FIRST_VALUE</c>). All require ORDER BY inside OVER and
/// share the post-WHERE buffer + per-partition sort path; the ranking
/// family additionally tracks tie behavior (RANK skips, DENSE_RANK doesn't,
/// ROW_NUMBER assigns arbitrary order within ties), and value functions
/// re-resolve the operand against another row's tuple. <c>LAST_VALUE</c>
/// isn't covered — its implicit-frame semantic (current row, not partition
/// last) requires explicit-frame support which the simulator doesn't model.
/// </summary>
[TestClass]
public sealed class WindowFunctionTests
{
    private static DbConnection SeededPosts()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table posts (id int, blog_id int, score int);
            insert posts values (1, 1, 10), (2, 1, 30), (3, 1, 20), (4, 2, 5), (5, 2, 50), (6, 2, 5)
            """).ExecuteNonQuery();
        return connection;
    }

    private static List<(int Id, long Row)> ReadIdRow(DbCommand command)
    {
        using var reader = command.ExecuteReader();
        var rows = new List<(int, long)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt64(1)));
        return rows;
    }

    [TestMethod]
    public void RowNumber_PartitionBy_AssignsPerPartitionSequence()
    {
        // Partition by blog_id, order by score asc. Each partition gets 1, 2, 3...
        using var connection = SeededPosts();
        var rows = ReadIdRow(connection.CreateCommand(
            "select id, row_number() over(partition by blog_id order by score) as rn from posts"));
        // blog_id=1: id=1 (score=10) → 1, id=3 (score=20) → 2, id=2 (score=30) → 3
        // blog_id=2: id=4 or id=6 (score=5, tie) → 1 / 2 in some order, id=5 (score=50) → 3
        var byId = rows.ToDictionary(r => r.Id, r => r.Row);
        AreEqual(1L, byId[1]);
        AreEqual(3L, byId[2]);
        AreEqual(2L, byId[3]);
        AreEqual(3L, byId[5]);
        // The tied rows get 1 and 2 (in some stable order); both must appear.
        IsTrue((byId[4] == 1 && byId[6] == 2) || (byId[4] == 2 && byId[6] == 1));
    }

    [TestMethod]
    public void RowNumber_OrderByDesc_AssignsLargestFirst()
    {
        using var connection = SeededPosts();
        var rows = ReadIdRow(connection.CreateCommand(
            "select id, row_number() over(partition by blog_id order by score desc) as rn from posts"));
        var byId = rows.ToDictionary(r => r.Id, r => r.Row);
        // blog_id=1: id=2 (30) → 1, id=3 (20) → 2, id=1 (10) → 3
        AreEqual(1L, byId[2]);
        AreEqual(2L, byId[3]);
        AreEqual(3L, byId[1]);
        // blog_id=2: id=5 (50) → 1, ties for 2/3 between id=4 and id=6
        AreEqual(1L, byId[5]);
    }

    [TestMethod]
    public void RowNumber_NoPartitionBy_AssignsGlobalSequence()
    {
        using var connection = SeededPosts();
        var rows = ReadIdRow(connection.CreateCommand(
            "select id, row_number() over(order by score) as rn from posts"));
        // 6 rows total; row numbers 1..6 (ties get adjacent numbers).
        var rowNumbers = rows.Select(r => r.Row).OrderBy(n => n).ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6 }, rowNumbers);
    }

    [TestMethod]
    public void RowNumber_WrappedInDerivedTable_FilterByRow()
    {
        // The exact shape EF Core 10 emits for SelectMany.OrderBy.Take(2):
        // wrap ROW_NUMBER in a derived table, outer WHERE filters by the row column.
        using var connection = SeededPosts();
        var ids = new List<int>();
        using var reader = connection.CreateCommand(
            "select id from (select id, row_number() over(partition by blog_id order by score desc) as rn from posts) as p where rn <= 2 order by id").ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        // blog_id=1 top-2 by score desc: id=2 (30), id=3 (20). blog_id=2: id=5 (50), then one of {id=4, id=6}.
        HasCount(4, ids);
        Contains(2, ids);
        Contains(3, ids);
        Contains(5, ids);
        IsTrue(ids.Contains(4) || ids.Contains(6));
    }

    [TestMethod]
    public void RowNumber_WrappedInDerivedTable_SkipTakeRange()
    {
        // Skip+Take per group: WHERE 1 < rn AND rn <= 3 → ranks 2 and 3.
        using var connection = SeededPosts();
        var ids = new List<int>();
        using var reader = connection.CreateCommand(
            "select id from (select id, row_number() over(partition by blog_id order by score) as rn from posts) as p where 1 < rn and rn <= 3 order by id").ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt32(0));
        // blog_id=1 ranks 2-3 (score asc): id=3 (20), id=2 (30).
        // blog_id=2 ranks 2-3 (score asc, ties on score=5): one of {4,6} at rank 2, id=5 (50) at rank 3.
        HasCount(4, ids);
        Contains(2, ids);
        Contains(3, ids);
        Contains(5, ids);
    }

    [TestMethod]
    public void RowNumber_PartitionByMultipleColumns()
    {
        using var connection = SeededPosts();
        var rows = ReadIdRow(connection.CreateCommand(
            "select id, row_number() over(partition by blog_id, score order by id) as rn from posts"));
        // Tied (blog_id=2, score=5) rows partition by (2, 5): id=4 and id=6 each get their own sequence.
        // Order by id: id=4 → 1, id=6 → 2.
        var byId = rows.ToDictionary(r => r.Id, r => r.Row);
        AreEqual(1L, byId[4]);
        AreEqual(2L, byId[6]);
        // Other partitions are singleton: each gets row 1.
        AreEqual(1L, byId[1]);
        AreEqual(1L, byId[2]);
        AreEqual(1L, byId[3]);
        AreEqual(1L, byId[5]);
    }

    // === Parser rejections ===

    [TestMethod]
    public void RowNumber_RequiresOver()
    {
        using var connection = SeededPosts();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("select row_number() from posts").ExecuteScalar());
    }

    [TestMethod]
    public void RowNumber_RequiresOrderByInsideOver()
    {
        // ROW_NUMBER without ORDER BY in OVER is invalid SQL.
        using var connection = SeededPosts();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("select row_number() over() from posts").ExecuteScalar());
    }

    /// <summary>
    /// A window sharing a SELECT with GROUP BY numbers the <em>groups</em>, so
    /// the row count matches the group count and the ranks run 1..N over them
    /// (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void RowNumber_CombinedWithGroupBy_NumbersTheGroups()
    {
        using var connection = SeededPosts();
        using var reader = connection.CreateCommand(
            "select blog_id, row_number() over(order by blog_id), count(*) from posts group by blog_id order by blog_id")
            .ExecuteReader();

        var rowNumbers = new List<long>();
        var perGroupCounts = new List<int>();
        while (reader.Read())
        {
            rowNumbers.Add(Convert.ToInt64(reader.GetValue(1)));
            perGroupCounts.Add(Convert.ToInt32(reader.GetValue(2)));
        }

        IsNotEmpty(rowNumbers);
        // ROW_NUMBER runs 1..groupCount — it counts groups, not base rows.
        CollectionAssert.AreEqual(
            Enumerable.Range(1, rowNumbers.Count).Select(i => (long)i).ToList(),
            rowNumbers);
        // The per-group COUNT(*) still counts that group's base rows, so the
        // two are measuring different things in the same projection.
        IsGreaterThanOrEqualTo(rowNumbers.Count, perGroupCounts.Sum());
    }

    /// <summary>
    /// A ranking window ordered by an aggregate — the reporting shape
    /// "rank each group by its total".
    /// </summary>
    [TestMethod]
    public void Rank_OrderedByAggregate_RanksGroupsByTheirTotal()
    {
        using var connection = SeededPosts();
        using var reader = connection.CreateCommand(
            "select blog_id, count(*) as n, rank() over(order by count(*) desc) as rnk from posts group by blog_id")
            .ExecuteReader();

        var byRank = new List<(long Rank, int Count)>();
        while (reader.Read())
            byRank.Add((Convert.ToInt64(reader.GetValue(2)), Convert.ToInt32(reader.GetValue(1))));

        IsNotEmpty(byRank);
        byRank.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        AreEqual(1L, byRank[0].Rank);
        // Rank 1 must hold the largest group.
        AreEqual(byRank.Max(entry => entry.Count), byRank[0].Count);
    }

    // === RANK / DENSE_RANK ===

    /// <summary>
    /// Seed for tie-sensitive tests: scores=10,20,20,30 in blog 1; 5,5,50 in blog 2.
    /// </summary>
    private static DbConnection SeededTies()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int, grp int, v int);
            insert t values (1, 1, 10), (2, 1, 20), (3, 1, 20), (4, 1, 30), (5, 2, 5), (6, 2, 5), (7, 2, 50)
            """).ExecuteNonQuery();
        return connection;
    }

    [TestMethod]
    public void Rank_TiesShareRank_NextRankSkipsAhead()
    {
        // RANK with ties: 10 → 1, 20 (tie) → 2, 20 (tie) → 2, 30 → 4 (skips 3).
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, rank() over(order by v) from t where grp = 1").ExecuteReader();
        var byId = new Dictionary<int, long>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt64(1);
        AreEqual(1L, byId[1]);
        AreEqual(2L, byId[2]);
        AreEqual(2L, byId[3]);
        AreEqual(4L, byId[4]);
    }

    [TestMethod]
    public void DenseRank_TiesShareRank_NextRankDoesNotSkip()
    {
        // DENSE_RANK with ties: 10 → 1, 20 (tie) → 2, 20 (tie) → 2, 30 → 3 (no gap).
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, dense_rank() over(order by v) from t where grp = 1").ExecuteReader();
        var byId = new Dictionary<int, long>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt64(1);
        AreEqual(1L, byId[1]);
        AreEqual(2L, byId[2]);
        AreEqual(2L, byId[3]);
        AreEqual(3L, byId[4]);
    }

    [TestMethod]
    public void Rank_PartitionedResetsPerPartition()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, rank() over(partition by grp order by v) from t").ExecuteReader();
        var byId = new Dictionary<int, long>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt64(1);
        // Group 1: 1→1, 2→2, 3→2, 4→4. Group 2: 5→1, 6→1, 7→3.
        AreEqual(1L, byId[5]);
        AreEqual(1L, byId[6]);
        AreEqual(3L, byId[7]);
    }

    [TestMethod]
    public void Rank_RequiresOrderByInsideOver()
    {
        using var connection = SeededTies();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand("select rank() over() from t").ExecuteScalar());
    }

    // === NTILE ===

    [TestMethod]
    public void NTile_DistributesRowsEvenly()
    {
        // 6 rows / 3 buckets = 2 rows per bucket.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, ntile(3) over(order by id) from t where id <= 6").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(1, byId[1]);
        AreEqual(1, byId[2]);
        AreEqual(2, byId[3]);
        AreEqual(2, byId[4]);
        AreEqual(3, byId[5]);
        AreEqual(3, byId[6]);
    }

    [TestMethod]
    public void NTile_UnevenDistribution_FirstBucketsLarger()
    {
        // 7 rows / 3 buckets → smaller=2, remainder=1 → first bucket has 3, rest have 2.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, ntile(3) over(order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // Bucket sizes: [3, 2, 2] → IDs 1,2,3 → bucket 1; 4,5 → 2; 6,7 → 3.
        AreEqual(1, byId[1]);
        AreEqual(1, byId[2]);
        AreEqual(1, byId[3]);
        AreEqual(2, byId[4]);
        AreEqual(2, byId[5]);
        AreEqual(3, byId[6]);
        AreEqual(3, byId[7]);
    }

    [TestMethod]
    public void NTile_BucketsExceedRows_OneRowPerBucketThenEmpty()
    {
        // 3 rows / 5 buckets → first 3 buckets get 1 row each, last 2 buckets are empty.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, ntile(5) over(order by id) from t where id <= 3").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(1, byId[1]);
        AreEqual(2, byId[2]);
        AreEqual(3, byId[3]);
    }

    [TestMethod]
    public void NTile_NonPositiveBucketCount_Raises9819()
    {
        using var connection = SeededTies();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand("select ntile(0) over(order by id) from t").ExecuteScalar());
        AreEqual("9819", ex.Data["HelpLink.EvtID"]);
    }

    // === LAG / LEAD ===

    [TestMethod]
    public void Lag_DefaultOffsetOne_PartitionBoundaryNull()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, lag(v) over(order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int?>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        IsNull(byId[1]); // First row: no predecessor → NULL.
        AreEqual(10, byId[2]);
        AreEqual(20, byId[3]);
        AreEqual(20, byId[4]);
        AreEqual(30, byId[5]);
    }

    [TestMethod]
    public void Lag_ExplicitOffset_LooksBackFurther()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, lag(v, 2) over(order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int?>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        IsNull(byId[1]);
        IsNull(byId[2]);
        AreEqual(10, byId[3]);
        AreEqual(20, byId[4]);
    }

    [TestMethod]
    public void Lag_WithDefaultExpression_SubstitutesAtBoundary()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, lag(v, 1, -99) over(order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(-99, byId[1]);
        AreEqual(10, byId[2]);
    }

    [TestMethod]
    public void Lead_MirrorsLagInOppositeDirection()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, lead(v) over(order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int?>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        AreEqual(20, byId[1]);
        AreEqual(20, byId[2]);
        AreEqual(30, byId[3]);
        IsNull(byId[7]); // Last row: no successor → NULL.
    }

    [TestMethod]
    public void Lag_PartitionedDoesNotCrossPartitionBoundary()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, lag(v) over(partition by grp order by id) from t").ExecuteReader();
        var byId = new Dictionary<int, int?>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        IsNull(byId[1]); // grp 1's first row.
        IsNull(byId[5]); // grp 2's first row — wouldn't be NULL if partition was ignored.
    }

    // === FIRST_VALUE ===

    [TestMethod]
    public void FirstValue_ReturnsPartitionFirstAfterOrderBy()
    {
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, first_value(v) over(partition by grp order by v) from t").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // grp 1: smallest v=10 → broadcast across rows 1,2,3,4.
        // grp 2: smallest v=5 → broadcast across rows 5,6,7.
        AreEqual(10, byId[1]);
        AreEqual(10, byId[4]);
        AreEqual(5, byId[5]);
        AreEqual(5, byId[7]);
    }

    [TestMethod]
    public void FirstValue_RespectsOrderByDirection()
    {
        // DESC ORDER BY → "first" is the max in each partition.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, first_value(v) over(partition by grp order by v desc) from t").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(30, byId[1]); // grp 1's largest v.
        AreEqual(50, byId[5]); // grp 2's largest v.
    }

    /// <summary>
    /// Msg 5309: an <c>OVER (ORDER BY …)</c> term real folds to a constant is
    /// rejected — the position carries no ordinal semantics, so there is
    /// nothing for the folded value to name. The rule reaches every window
    /// kind, the named-window definition and <c>NEXT VALUE FOR … OVER</c>, and
    /// it fires on the folded shapes the statement-level Msg 408 gate rejects.
    /// </summary>
    [TestMethod]
    [DataRow("select row_number() over (order by 'x') from t")]
    [DataRow("select row_number() over (order by 'x' desc) from t")]
    [DataRow("select row_number() over (order by null) from t")]
    [DataRow("select row_number() over (order by 1.5) from t")]
    [DataRow("select row_number() over (order by 0x01) from t")]
    [DataRow("select row_number() over (order by 0) from t")]
    [DataRow("select row_number() over (order by -1) from t")]
    [DataRow("select row_number() over (order by 1 - 2) from t")]
    [DataRow("select row_number() over (order by cast(1 as bigint)) from t")]
    [DataRow("select row_number() over (order by cast(1 as tinyint)) from t")]
    [DataRow("select row_number() over (order by 'a' + 'b') from t")]
    [DataRow("select row_number() over (order by concat('a', 'b')) from t")]
    [DataRow("select row_number() over (order by case when 1 = 1 then 'a' else 'b' end) from t")]
    [DataRow("select row_number() over (order by v, 'x') from t")]
    [DataRow("select sum(v) over (order by 'x') from t")]
    [DataRow("select lag(v) over (order by 'x') from t")]
    [DataRow("select count(*) over (partition by 'x' order by 'y') from t")]
    [DataRow("select sum(v) over w from t window w as (order by 'x')")]
    [DataRow("select * from (select row_number() over (order by 'x') r from t) q")]
    [DataRow("select percentile_cont(0.5) within group (order by 1.0) over () from t")]
    [DataRow("select next value for sq over (order by 'x') from t")]
    public void WindowOrderBy_ConstantTerm_RaisesMsg5309(string commandText)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2); create sequence sq as int start with 1");
        sim.AssertSqlError(
            commandText,
            5309,
            "Windowed functions, aggregates and NEXT VALUE FOR functions do not support constants as ORDER BY clause expressions.");
    }

    /// <summary>
    /// Msg 5308 is the sibling rejection for a folded constant that could pass
    /// for a column index — an <c>int</c> of at least 1, however it was
    /// written. Real applies no range check against the select list, so an
    /// out-of-range number lands here rather than on Msg 108.
    /// </summary>
    [TestMethod]
    [DataRow("select row_number() over (order by 1) from t")]
    [DataRow("select row_number() over (order by +1) from t")]
    [DataRow("select row_number() over (order by (1)) from t")]
    [DataRow("select row_number() over (order by 100) from t")]
    [DataRow("select row_number() over (order by 1 + 1) from t")]
    [DataRow("select row_number() over (order by cast(1 as int)) from t")]
    [DataRow("select row_number() over (order by abs(-1)) from t")]
    [DataRow("select row_number() over (order by len('abc')) from t")]
    [DataRow("select row_number() over (order by abs(len('abc'))) from t")]
    [DataRow("select row_number() over (order by coalesce(null, 1)) from t")]
    [DataRow("select row_number() over (order by case when 1 = 1 then 1 else 2 end) from t")]
    [DataRow("select row_number() over (order by iif(1 = 1, 1, 2)) from t")]
    [DataRow("select row_number() over (order by 1, 'x') from t")]
    [DataRow("select next value for sq over (order by 1) from t")]
    public void WindowOrderBy_IntegerIndexTerm_RaisesMsg5308(string commandText)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2); create sequence sq as int start with 1");
        sim.AssertSqlError(
            commandText,
            5308,
            "Windowed functions, aggregates and NEXT VALUE FOR functions do not support integer indices as ORDER BY clause expressions.");
    }

    /// <summary>
    /// The window gate reads the same predicate as the statement-level Msg 408
    /// one, so everything real evaluates rather than folds still orders the
    /// partition — including a bare variable, which has no Msg 1008 counterpart
    /// in this position (probe-confirmed).
    /// </summary>
    [TestMethod]
    [DataRow("select row_number() over (order by (select 1)) from t")]
    [DataRow("select row_number() over (order by getdate()) from t")]
    [DataRow("select row_number() over (order by @@spid) from t")]
    [DataRow("select row_number() over (order by isnull(null, 1)) from t")]
    [DataRow("select row_number() over (order by upper('a')) from t")]
    [DataRow("select row_number() over (order by cast(getdate() as date)) from t")]
    [DataRow("select row_number() over (partition by 'x' order by v) from t")]
    [DataRow("select row_number() over (partition by 1 + 1 order by v) from t")]
    [DataRow("select sum(v) over w from t window w as (partition by 'x' order by v)")]
    [DataRow("declare @p int = 1; select row_number() over (order by @p) from t")]
    [DataRow("select next value for sq over (order by v) from t")]
    public void WindowOrderBy_NonConstantTerm_Ranks(string commandText)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int); insert t values (3),(1),(2); create sequence sq as int start with 1");
        using var reader = sim.ExecuteReader(commandText);
        var rows = 0;
        while (reader.Read())
            rows++;
        AreEqual(3, rows);
    }

    // ----- Named-window references: `OVER w` and the refining `OVER (w …)` -----

    private const string NamedWindowFixture =
        "create table nw (id int, g int, v int); insert nw values (1,1,10),(2,1,20),(3,2,30),(4,2,40); ";

    /// <summary>
    /// Every window kind reaches a named window through the bare
    /// <c>OVER w</c> form, not just the aggregate one — the reference is
    /// registered spec-less at parse and patched once the trailing
    /// <c>WINDOW</c> clause is read.
    /// </summary>
    [TestMethod]
    [DataRow("row_number()")]
    [DataRow("rank()")]
    [DataRow("dense_rank()")]
    [DataRow("ntile(2)")]
    [DataRow("cume_dist()")]
    [DataRow("percent_rank()")]
    [DataRow("lag(v)")]
    [DataRow("lead(v)")]
    [DataRow("first_value(v)")]
    [DataRow("last_value(v)")]
    [DataRow("sum(v)")]
    public void NamedWindow_BareReference_EveryKindResolves(string call)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(4, sim.ExecuteScalar(
            $"select count(*) from (select {call} over w x from nw window w as (partition by g order by id)) z"));
    }

    [TestMethod]
    public void NamedWindow_RowNumber_RanksWithinPartition()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        // Two rows per g, so the per-partition sequence tops out at 2.
        AreEqual(2L, sim.ExecuteScalar(
            "select max(rn) from (select row_number() over w rn from nw window w as (partition by g order by id)) z"));
    }

    /// <summary>
    /// PERCENTILE_CONT takes its ordering from WITHIN GROUP, so a named window
    /// contributes only the partitioning — g = 1 holds 10 and 20, median 15.
    /// </summary>
    [TestMethod]
    public void NamedWindow_Percentile_TakesPartitionOnly()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(15d, sim.ExecuteScalar(
            "select min(p) from (select percentile_cont(0.5) within group (order by v) over w p from nw window w as (partition by g)) z"));
    }

    /// <summary>A named window resolves from the statement's ORDER BY too.</summary>
    [TestMethod]
    public void NamedWindow_ReferencedFromOrderBy_Sorts()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        using var reader = sim.ExecuteReader(
            "select id from nw window w as (partition by g order by id desc) order by row_number() over w, id");
        IsTrue(reader.Read());
        AreEqual(2, reader.GetInt32(0));
    }

    /// <summary>Window names are identifiers: a case-insensitive collation resolves <c>OVER W</c>.</summary>
    [TestMethod]
    public void NamedWindow_NameIsCaseInsensitive()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(4, sim.ExecuteScalar(
            "select count(*) from (select row_number() over W rn from nw window w as (order by id)) z"));
    }

    /// <summary>A window may be named after a column without shadowing it.</summary>
    [TestMethod]
    public void NamedWindow_NameMatchingColumn_Resolves()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(4, sim.ExecuteScalar(
            "select count(*) from (select row_number() over id rn from nw window id as (partition by g order by id)) z"));
    }

    /// <summary>
    /// <c>OVER (w …)</c> refines the named window with the elements it doesn't
    /// already carry — here the ORDER BY, which flips the ranking direction.
    /// </summary>
    [TestMethod]
    public void NamedWindow_Refinement_AddsOrderBy()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        // partition by g, order by id desc → each partition's leading row is
        // its highest id: 2 and 4.
        AreEqual(2, sim.ExecuteScalar(
            "select min(id) from (select id, row_number() over (w order by id desc) rn from nw window w as (partition by g)) z where rn = 1"));
    }

    [TestMethod]
    public void NamedWindow_Refinement_AddsPartitionBy()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(2L, sim.ExecuteScalar(
            "select max(rn) from (select row_number() over (w partition by g) rn from nw window w as (order by id)) z"));
    }

    /// <summary>A refinement may add the frame an aggregate window runs under.</summary>
    [TestMethod]
    public void NamedWindow_Refinement_AddsFrame()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(30, sim.ExecuteScalar(
            "select max(s) from (select sum(v) over (w rows unbounded preceding) s from nw window w as (partition by g order by id)) z where s < 40"));
    }

    /// <summary>
    /// A definition may itself refine another, in either written order — the
    /// reference walk resolves forward as readily as backward.
    /// </summary>
    [TestMethod]
    [DataRow("window w as (partition by g), w2 as (w order by id)")]
    [DataRow("window w2 as (w order by id), w as (partition by g)")]
    [DataRow("window w as (partition by g), w2 as (w)")]
    public void NamedWindow_DefinitionRefinesAnother_Resolves(string windowClause)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        AreEqual(4, sim.ExecuteScalar(
            $"select count(*) from (select sum(v) over w2 s from nw {windowClause}) z"));
    }

    /// <summary>Real requires at least one refining element: <c>OVER (w)</c> is a syntax error even though <c>WINDOW w2 AS (w)</c> is not.</summary>
    [TestMethod]
    [DataRow("select row_number() over (w) from nw window w as (order by id)")]
    [DataRow("select sum(v) over (w) from nw window w as (order by id)")]
    public void NamedWindow_EmptyRefinement_RaisesMsg102(string commandText)
        => _ = new Simulation().AssertSqlError(NamedWindowFixture + commandText, 102);

    /// <summary>
    /// Msg 4123: an element written in the refinement that the referenced
    /// window already carries. The state tracks the referenced window rather
    /// than the conflicting element — 2 when it carries a frame, 3 otherwise.
    /// </summary>
    [TestMethod]
    [DataRow("select row_number() over (w order by id desc) from nw window w as (partition by g order by id)")]
    [DataRow("select row_number() over (w partition by g) from nw window w as (partition by g order by id)")]
    [DataRow("select sum(v) over (w rows unbounded preceding) from nw window w as (order by id rows unbounded preceding)")]
    [DataRow("select sum(v) over (w order by id) from nw window w as (partition by g order by id rows unbounded preceding)")]
    public void NamedWindow_ConflictingRefinement_RaisesMsg4123(string commandText)
        => new Simulation().AssertSqlError(
            NamedWindowFixture + commandText,
            4123,
            "Window element in OVER clause can not also be specified in WINDOW clause.");

    /// <summary>A refinement supplies only what the referenced window lacks, so a non-overlapping element merges.</summary>
    [TestMethod]
    [DataRow("select sum(v) over (w partition by g) from nw window w as (order by id rows unbounded preceding)")]
    [DataRow("select sum(v) over (w order by id) from nw window w as (partition by g rows unbounded preceding)")]
    [DataRow("select sum(v) over (w rows unbounded preceding) from nw window w as (partition by g order by id)")]
    public void NamedWindow_NonOverlappingRefinement_Merges(string commandText)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(NamedWindowFixture);
        using var reader = sim.ExecuteReader(commandText);
        var rows = 0;
        while (reader.Read())
            rows++;
        AreEqual(4, rows);
    }

    /// <summary>
    /// Msg 4106 — a frame inherited from the named window. Distinct from the
    /// Msg 10752 real answers when the frame is written in the OVER clause
    /// itself, despite the identical wording.
    /// </summary>
    [TestMethod]
    [DataRow("row_number()", "row_number")]
    [DataRow("rank()", "rank")]
    [DataRow("ntile(2)", "ntile")]
    [DataRow("lag(v)", "lag")]
    public void NamedWindow_InheritedFrame_RaisesMsg4106(string call, string functionName)
        => new Simulation().AssertSqlError(
            NamedWindowFixture + $"select {call} over w from nw window w as (partition by g order by id rows unbounded preceding)",
            4106,
            $"The function '{functionName}' may not have a window frame.");

    /// <summary>
    /// The percentile pair reaches Msg 4106 only past the Msg 5363 ORDER BY
    /// gate, so the definition that trips it carries a frame and no ordering.
    /// </summary>
    [TestMethod]
    public void NamedWindow_PercentileInheritedFrame_RaisesMsg4106()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select percentile_cont(0.5) within group (order by v) over w from nw window w as (partition by g rows unbounded preceding)",
            4106,
            "The function 'percentile_cont' may not have a window frame.");

    /// <summary>A frame written in the refinement stays on the inline Msg 10752 path.</summary>
    [TestMethod]
    public void NamedWindow_RefinementFrame_RaisesMsg10752()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select row_number() over (w rows unbounded preceding) from nw window w as (partition by g order by id)",
            10752,
            "The function 'row_number' may not have a window frame.");

    /// <summary>
    /// Msg 5366 — the ORDER-BY-requiring kinds reached through a named window
    /// with none. The inline counterpart is Msg 4112, with different wording.
    /// </summary>
    [TestMethod]
    [DataRow("rank()", "rank")]
    [DataRow("ntile(2)", "ntile")]
    [DataRow("cume_dist()", "cume_dist")]
    [DataRow("lag(v)", "lag")]
    [DataRow("first_value(v)", "first_value")]
    [DataRow("last_value(v)", "last_value")]
    public void NamedWindow_WithoutOrderBy_RaisesMsg5366(string call, string functionName)
        => new Simulation().AssertSqlError(
            NamedWindowFixture + $"select {call} over w from nw window w as (partition by g)",
            5366,
            $"The function '{functionName}' must have an OVER clause or a WINDOW with ORDER BY.");

    /// <summary>Msg 5363 — the percentile pair takes its ordering from WITHIN GROUP, so a named window may not carry one.</summary>
    [TestMethod]
    public void NamedWindow_PercentileWithOrderBy_RaisesMsg5363()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select percentile_disc(0.5) within group (order by v) over w from nw window w as (partition by g order by id)",
            5363,
            "The function 'percentile_disc' may not have ORDER BY in OVER or WINDOW clause.");

    /// <summary>Msg 5364 — a resolved frame with no ordering to frame against; the inline counterpart is Msg 10756.</summary>
    [TestMethod]
    public void NamedWindow_FrameWithoutOrderBy_RaisesMsg5364()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select sum(v) over w from nw window w as (partition by g rows unbounded preceding)",
            5364,
            "Window frame with ROWS or RANGE must have an ORDER BY clause.");

    /// <summary>Msg 5365 — definitions referencing each other in a loop.</summary>
    [TestMethod]
    public void NamedWindow_CyclicReferences_RaisesMsg5365()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select row_number() over w from nw window w as (w2 order by id), w2 as (w partition by g)",
            5365,
            "Cyclic window references are not permitted.");

    /// <summary>A definition naming itself isn't a cycle to real — the name simply isn't in its own scope.</summary>
    [TestMethod]
    public void NamedWindow_SelfReference_RaisesMsg5362()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select row_number() over w from nw window w as (w order by id)",
            5362,
            "Window 'w' is undefined.");

    [TestMethod]
    [DataRow("select row_number() over nope from nw window w as (order by id)")]
    [DataRow("select row_number() over (nope order by id) from nw")]
    public void NamedWindow_Undefined_RaisesMsg5362(string commandText)
        => new Simulation().AssertSqlError(NamedWindowFixture + commandText, 5362, "Window 'nope' is undefined.");

    [TestMethod]
    public void NamedWindow_RepeatedName_RaisesMsg16211()
        => new Simulation().AssertSqlError(
            NamedWindowFixture + "select row_number() over w from nw window w as (order by id), w as (order by g)",
            16211,
            "Cannot repeat window name in the WINDOW clause.");

    /// <summary>
    /// The named-window path doesn't bypass the ORDER BY constant gate: a
    /// definition's constant sort term lands on the same Msg 5308 / 5309 split
    /// an inline <c>OVER (ORDER BY …)</c> does, for the ranking kinds too.
    /// </summary>
    [TestMethod]
    [DataRow("select row_number() over w from nw window w as (order by 1)", 5308)]
    [DataRow("select rank() over w from nw window w as (partition by g order by 2)", 5308)]
    [DataRow("select row_number() over w from nw window w as (order by 'x')", 5309)]
    [DataRow("select lag(v) over w from nw window w as (order by null)", 5309)]
    [DataRow("select row_number() over (w order by 1) from nw window w as (partition by g)", 5308)]
    [DataRow("select row_number() over (w order by 'x') from nw window w as (partition by g)", 5309)]
    public void NamedWindow_ConstantOrderByTerm_RaisesConstantGate(string commandText, int errorNumber)
        => _ = new Simulation().AssertSqlError(NamedWindowFixture + commandText, errorNumber);
}
