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

    [TestMethod]
    public void RowNumber_CombinedWithGroupBy_NotSupported()
    {
        // SQL Server allows this in real life, but EF Core 10 doesn't emit
        // the combination, so the simulator hasn't built it.
        using var connection = SeededPosts();
        _ = Throws<NotSupportedException>(() =>
            _ = connection.CreateCommand(
                "select blog_id, row_number() over(order by blog_id), count(*) from posts group by blog_id").ExecuteScalar());
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
}
