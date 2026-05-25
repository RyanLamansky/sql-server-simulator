using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for explicit window frames (<c>ROWS BETWEEN</c> /
/// <c>RANGE BETWEEN</c>) and <c>LAST_VALUE</c>. Default frame logic
/// (running totals via the implicit <c>RANGE UNBOUNDED PRECEDING TO
/// CURRENT ROW</c> when ORDER BY is present in OVER) lives here too.
/// Frame rejection paths for ranking + LAG/LEAD (Msg 10752), frame
/// without ORDER BY (Msg 10756), <c>BETWEEN FOLLOWING AND PRECEDING</c>
/// (Msg 4193), and <c>RANGE</c> with offset bounds (Msg 4194) round out
/// the suite. All behaviors probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class WindowFrameTests
{
    private static DbConnection SeededTies()
    {
        var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table t (id int, grp int, v int);
            insert t values (1,1,10), (2,1,20), (3,1,20), (4,1,30), (5,2,5), (6,2,5), (7,2,50)
            """).ExecuteNonQuery();
        return connection;
    }

    // === Default frame (ORDER BY in OVER → running total with peer-tie grouping) ===

    [TestMethod]
    public void Sum_OrderByInOver_DefaultFrameIsRunningTotal()
    {
        // Default frame = RANGE UNBOUNDED PRECEDING TO CURRENT ROW.
        // Order by id (distinct values) → running total = cumulative sum.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, grp, v, sum(v) over(partition by grp order by id) from t order by grp, id").ExecuteReader();
        var rows = new List<(int Id, int Grp, int V, int Rt)>();
        while (reader.Read())
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3)));
        AreEqual((1, 1, 10, 10), rows[0]);
        AreEqual((2, 1, 20, 30), rows[1]);
        AreEqual((3, 1, 20, 50), rows[2]);
        AreEqual((4, 1, 30, 80), rows[3]);
        AreEqual((5, 2, 5, 5), rows[4]);
        AreEqual((6, 2, 5, 10), rows[5]);
        AreEqual((7, 2, 50, 60), rows[6]);
    }

    [TestMethod]
    public void Sum_OrderByInOver_DefaultRange_PeerTiesGroup()
    {
        // ORDER BY v with ties at v=20 → RANGE default groups peers, both
        // tied rows see the same running total.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, sum(v) over(partition by grp order by v) from t where grp = 1 order by v, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(2);
        AreEqual(10, byId[1]);    // v=10, only itself
        AreEqual(50, byId[2]);    // v=20 peer of 3 → 10+20+20
        AreEqual(50, byId[3]);    // peer with id=2
        AreEqual(80, byId[4]);    // v=30 + previous 50
    }

    // === Explicit ROWS frames ===

    [TestMethod]
    public void Sum_RowsUnboundedPrecedingToCurrentRow_RunningTotal()
    {
        // ROWS is row-precise (no peer grouping) — ties at v=20 advance one at a time.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, sum(v) over(partition by grp order by v, id rows between unbounded preceding and current row) from t where grp = 1 order by v, id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(2));
        CollectionAssert.AreEqual(new[] { 10, 30, 50, 80 }, values);
    }

    [TestMethod]
    public void Sum_RowsBetween1PrecedingAnd1Following_SlidingWindow()
    {
        // 3-row sliding sum (centered on each row, clamped at boundaries).
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows between 1 preceding and 1 following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // grp 1: ids 1..4, v=10,20,20,30.
        AreEqual(30, byId[1]);  // 10+20 (no left).
        AreEqual(50, byId[2]);  // 10+20+20.
        AreEqual(70, byId[3]);  // 20+20+30.
        AreEqual(50, byId[4]);  // 20+30 (no right).
        // grp 2: ids 5,6,7, v=5,5,50.
        AreEqual(10, byId[5]);
        AreEqual(60, byId[6]);
        AreEqual(55, byId[7]);
    }

    [TestMethod]
    public void Sum_RowsBetweenUnboundedPrecedingAndUnboundedFollowing_WholePartition()
    {
        // Equivalent to no-ORDER-BY whole-partition broadcast.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows between unbounded preceding and unbounded following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(80, byId[1]);   // grp 1 total
        AreEqual(80, byId[4]);
        AreEqual(60, byId[5]);   // grp 2 total
        AreEqual(60, byId[7]);
    }

    [TestMethod]
    public void Count_RowsBetween1PrecedingAnd1Following_CountsFrameRows()
    {
        // COUNT(*) over sliding 3-row frame — clamped to 2 at boundaries.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, count(*) over(partition by grp order by id rows between 1 preceding and 1 following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(2, byId[1]);  // grp 1 first row
        AreEqual(3, byId[2]);
        AreEqual(3, byId[3]);
        AreEqual(2, byId[4]);  // grp 1 last row
        AreEqual(2, byId[5]);  // grp 2 first
        AreEqual(3, byId[6]);
        AreEqual(2, byId[7]);
    }

    [TestMethod]
    public void Sum_RowsBetween5FollowingAnd10Following_EmptyFrameReturnsNull()
    {
        // Every row's frame is outside the partition → SUM returns NULL.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows between 5 following and 10 following) from t").ExecuteReader();
        var values = new List<int?>();
        while (reader.Read())
            values.Add(reader.IsDBNull(1) ? null : reader.GetInt32(1));
        HasCount(7, values);
        foreach (var v in values)
            IsNull(v);
    }

    [TestMethod]
    public void Count_EmptyFrameReturnsZero()
    {
        // COUNT(*) over an empty frame returns 0, not NULL — distinguishes
        // SUM's "no rows → NULL" from COUNT's "no rows → 0".
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, count(*) over(partition by grp order by id rows between 5 following and 10 following) from t").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(1));
        HasCount(7, values);
        foreach (var v in values)
            AreEqual(0, v);
    }

    // === Single-bound shorthand ===

    [TestMethod]
    public void Sum_RowsUnboundedPreceding_ShorthandForBetweenAndCurrentRow()
    {
        // ROWS UNBOUNDED PRECEDING  ≡  ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows unbounded preceding) from t order by grp, id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(1));
        // grp 1: 10, 30, 50, 80. grp 2: 5, 10, 60.
        CollectionAssert.AreEqual(new[] { 10, 30, 50, 80, 5, 10, 60 }, values);
    }

    [TestMethod]
    public void Sum_RowsNPreceding_Shorthand()
    {
        // ROWS 1 PRECEDING  ≡  ROWS BETWEEN 1 PRECEDING AND CURRENT ROW.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows 1 preceding) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(10, byId[1]);
        AreEqual(30, byId[2]);
        AreEqual(40, byId[3]);
        AreEqual(50, byId[4]);
    }

    [TestMethod]
    public void Sum_RowsCurrentRow_ShorthandReturnsCurrentRowValue()
    {
        // ROWS CURRENT ROW  ≡  ROWS BETWEEN CURRENT ROW AND CURRENT ROW.
        // For SUM that means each row sees only itself.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, sum(v) over(partition by grp order by id rows current row) from t order by grp, id").ExecuteReader();
        while (reader.Read())
            AreEqual(reader.GetInt32(1), reader.GetInt32(2));
    }

    // === RANGE explicit frame ===

    [TestMethod]
    public void Sum_RangeBetweenUnboundedPrecedingAndCurrentRow_PeerTiesGroup()
    {
        // Explicit RANGE matches the default behavior — peer ties share total.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by v range between unbounded preceding and current row) from t where grp = 1").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        AreEqual(10, byId[1]);
        AreEqual(50, byId[2]);
        AreEqual(50, byId[3]);
        AreEqual(80, byId[4]);
    }

    // === LAST_VALUE ===

    [TestMethod]
    public void LastValue_DefaultFrame_ReturnsCurrentRowValueUnderRange()
    {
        // LAST_VALUE's default frame is RANGE UNBOUNDED PRECEDING TO CURRENT ROW.
        // Under RANGE+CURRENT ROW, "last" = the last peer of the current row's
        // group. ORDER BY id has no ties → "last" = current row itself.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, last_value(v) over(partition by grp order by id) from t order by grp, id").ExecuteReader();
        while (reader.Read())
            AreEqual(reader.GetInt32(1), reader.GetInt32(2));
    }

    [TestMethod]
    public void LastValue_RowsUnboundedToUnbounded_ReturnsPartitionLast()
    {
        // The classic "partition last" idiom — explicit unbounded-to-unbounded
        // frame, no peer grouping under ROWS.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, last_value(v) over(partition by grp order by id rows between unbounded preceding and unbounded following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // grp 1 last v (id=4) → 30; broadcast across ids 1..4.
        // grp 2 last v (id=7) → 50; broadcast across ids 5..7.
        AreEqual(30, byId[1]);
        AreEqual(30, byId[4]);
        AreEqual(50, byId[5]);
        AreEqual(50, byId[7]);
    }

    [TestMethod]
    public void LastValue_RangeWithTies_GroupsPeerEnd()
    {
        // ORDER BY v with ties at 20 + RANGE default → "last peer" for each
        // group: the v=20 peers share LAST_VALUE = 20.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, last_value(v) over(partition by grp order by v) from t where grp = 1 order by v, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(2);
        AreEqual(10, byId[1]);
        AreEqual(20, byId[2]);
        AreEqual(20, byId[3]);
        AreEqual(30, byId[4]);
    }

    // === FIRST_VALUE with explicit frame ===

    [TestMethod]
    public void FirstValue_RowsBetween1PrecedingAndCurrentRow_FrameStart()
    {
        // FIRST_VALUE follows the frame start; with ROWS BETWEEN 1 PRECEDING AND
        // CURRENT ROW the frame start is the previous row (or current for the
        // partition's leading row).
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, v, first_value(v) over(partition by grp order by id rows between 1 preceding and current row) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(2);
        // grp 1: id=1→ 10 (alone), id=2→ 10 (prev), id=3→ 20, id=4→ 20.
        AreEqual(10, byId[1]);
        AreEqual(10, byId[2]);
        AreEqual(20, byId[3]);
        AreEqual(20, byId[4]);
        // grp 2: id=5→ 5, id=6→ 5, id=7→ 5.
        AreEqual(5, byId[5]);
        AreEqual(5, byId[6]);
        AreEqual(5, byId[7]);
    }

    // === Error paths ===

    [TestMethod]
    [DataRow("select row_number() over(order by id rows between unbounded preceding and current row) from t")]
    [DataRow("select rank() over(order by id rows between unbounded preceding and current row) from t")]
    [DataRow("select dense_rank() over(order by id rows between unbounded preceding and current row) from t")]
    [DataRow("select ntile(3) over(order by id rows between unbounded preceding and current row) from t")]
    [DataRow("select lag(v) over(order by id rows between unbounded preceding and current row) from t")]
    [DataRow("select lead(v) over(order by id rows between unbounded preceding and current row) from t")]
    public void Frame_RejectedOnRankingAndOffsetFunctions_Msg10752(string sql)
    {
        using var connection = SeededTies();
        var ex = Throws<DbException>(() => _ = connection.CreateCommand(sql).ExecuteScalar());
        AreEqual("10752", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Range_WithNumericOffsetBounds_Raises4194()
    {
        // RANGE restricted to UNBOUNDED + CURRENT ROW per probe.
        using var connection = SeededTies();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select sum(v) over(order by v range between 1 preceding and 1 following) from t").ExecuteScalar());
        AreEqual("4194", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Frame_BetweenFollowingAndPreceding_Raises4193()
    {
        // Semantically empty / inverted frame — SQL Server rejects up-front.
        using var connection = SeededTies();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select sum(v) over(order by id rows between 1 following and 1 preceding) from t").ExecuteScalar());
        AreEqual("4193", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Frame_BetweenCurrentRowAndUnboundedPreceding_Raises102()
    {
        // UNBOUNDED PRECEDING is invalid as an end bound — Msg 102 syntax.
        using var connection = SeededTies();
        var ex = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select sum(v) over(order by id rows between current row and unbounded preceding) from t").ExecuteScalar());
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void LastValue_WithoutOrderBy_Raises4112()
    {
        // Same as FIRST_VALUE — value functions require ORDER BY.
        using var connection = SeededTies();
        _ = Throws<DbException>(() =>
            _ = connection.CreateCommand(
                "select last_value(v) over(partition by grp) from t").ExecuteScalar());
    }

    // === MIN / MAX / AVG over frames ===

    [TestMethod]
    public void MinMax_OverSlidingFrame()
    {
        // MAX over 3-row sliding frame.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, max(v) over(partition by grp order by id rows between 1 preceding and 1 following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // grp 1 v=10,20,20,30: maxes 20, 20, 30, 30.
        AreEqual(20, byId[1]);
        AreEqual(20, byId[2]);
        AreEqual(30, byId[3]);
        AreEqual(30, byId[4]);
        // grp 2 v=5,5,50: maxes 5, 50, 50.
        AreEqual(5, byId[5]);
        AreEqual(50, byId[6]);
        AreEqual(50, byId[7]);
    }

    [TestMethod]
    public void Avg_OverRunningTotal_DecimalScaleWidens()
    {
        // AVG(int) over a running-total frame — preserves per-row truncation
        // semantics of the underlying AVG aggregator.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table d (v int);
            insert d values (1), (3), (5)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select v, avg(v) over(order by v rows between unbounded preceding and current row) from d order by v").ExecuteReader();
        var values = new List<(int V, int Avg)>();
        while (reader.Read())
            values.Add((reader.GetInt32(0), reader.GetInt32(1)));
        // Running averages: 1/1=1, 4/2=2, 9/3=3.
        CollectionAssert.AreEqual(new[] { (1, 1), (3, 2), (5, 3) }, values);
    }

    // === Sliding frames exercise the incremental Remove path per aggregator ===

    [TestMethod]
    public void Sum_RowsBetweenCurrentRowAndUnboundedFollowing_ReverseRunningTotal()
    {
        // Start advances (CURRENT ROW) with the end pinned at the partition's
        // last row — every step removes the leaving row from the slider.
        using var connection = SeededTies();
        using var reader = connection.CreateCommand(
            "select id, sum(v) over(partition by grp order by id rows between current row and unbounded following) from t order by grp, id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // grp 1 v=10,20,20,30 → suffix sums 80,70,50,30.
        AreEqual(80, byId[1]);
        AreEqual(70, byId[2]);
        AreEqual(50, byId[3]);
        AreEqual(30, byId[4]);
        // grp 2 v=5,5,50 → 60,55,50.
        AreEqual(60, byId[5]);
        AreEqual(55, byId[6]);
        AreEqual(50, byId[7]);
    }

    [TestMethod]
    public void Min_SlidingFrame_DropsLeavingExtreme()
    {
        // Ascending values with a trailing 2-PRECEDING frame: the current
        // minimum repeatedly leaves the window, so the removable multiset must
        // surface the next-smallest survivor rather than a stale extreme.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table m (id int, v int);
            insert m values (1,10), (2,20), (3,30), (4,40), (5,50)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select id, min(v) over(order by id rows between 2 preceding and current row) from m order by id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(1));
        // Frames {10},{10,20},{10,20,30},{20,30,40},{30,40,50} → mins 10,10,10,20,30.
        CollectionAssert.AreEqual(new[] { 10, 10, 10, 20, 30 }, values);
    }

    [TestMethod]
    public void Avg_SlidingFrame_RemovesLeavingRows()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table a (id int, v int);
            insert a values (1,10), (2,20), (3,30), (4,40)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select id, avg(v) over(order by id rows between 1 preceding and current row) from a order by id").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(1));
        // Pairs {10}=10, {10,20}=15, {20,30}=25, {30,40}=35.
        CollectionAssert.AreEqual(new[] { 10, 15, 25, 35 }, values);
    }

    [TestMethod]
    public void VarP_SlidingFrame_SubtractsMoments()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table s (id int, v int);
            insert s values (1,2), (2,4), (3,6), (4,8)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select id, varp(v) over(order by id rows between 1 preceding and current row) from s order by id").ExecuteReader();
        var values = new List<double>();
        while (reader.Read())
            values.Add(reader.GetDouble(1));
        // {2}→0, {2,4}→1, {4,6}→1, {6,8}→1 (sum / sum-of-squares moments subtract).
        CollectionAssert.AreEqual(new[] { 0.0, 1.0, 1.0, 1.0 }, values);
    }

    [TestMethod]
    public void ChecksumAgg_SlidingFrame_MatchesDirectAggregate()
    {
        // XOR is its own inverse: removing the leaving row must leave exactly
        // the state of aggregating the survivors directly.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("""
            create table c (id int, v int);
            insert c values (1,11), (2,22), (3,33), (4,44)
            """).ExecuteNonQuery();
        using var reader = connection.CreateCommand(
            "select id, checksum_agg(v) over(order by id rows between 1 preceding and current row) from c order by id").ExecuteReader();
        var byId = new Dictionary<int, int>();
        while (reader.Read())
            byId[reader.GetInt32(0)] = reader.GetInt32(1);
        // Row 3's frame is {22, 33}; the slider reached it by removing 11.
        var expected = (int)connection.CreateCommand("select checksum_agg(v) from c where id in (2,3)").ExecuteScalar()!;
        AreEqual(expected, byId[3]);
    }
}
