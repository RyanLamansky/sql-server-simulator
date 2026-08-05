using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Correctness contract for the bounded per-partition <c>ROW_NUMBER()</c>
/// selection — the greatest-n-per-group idiom
/// (<c>… FROM (SELECT …, ROW_NUMBER() OVER (PARTITION BY …) rn FROM t) x WHERE
/// rn = 1</c>), where the enclosing filter's constant bound lets the body keep
/// each partition's top rows instead of sorting every partition in full.
/// <para>
/// Every test runs its query <b>twice</b>: once written so the bound can bind
/// (<c>rn</c> named directly) and once written so it can't (<c>rn + 0</c>, the
/// same predicate over an expression no bound reads), then asserts the two agree
/// row for row <em>and</em> match the expected rows. A divergence is then caught
/// as a divergence rather than as a wrong literal — and pinning the two paths
/// against each other is what the tie behaviour needs, since ranking ties is
/// what real leaves plan-dependent.
/// </para>
/// </summary>
[TestClass]
public sealed class WindowTopNBoundTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// 240 rows over 8 partitions whose sort key <b>ties in threes</b>
    /// (<c>k = id / 3</c>), so every bound from 1 upward lands inside a tie
    /// group: the boundary case the two paths have to agree on.
    /// </summary>
    private static SimulatedDbConnection OpenTied()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table t (id int not null primary key, g int not null, k int not null, tag nvarchar(10) null);
            declare @i int = 1;
            while @i <= 240 begin
                insert t values (@i, @i % 8, @i / 3, concat('r', @i));
                set @i += 1;
            end
            create table peer (g int not null, label nvarchar(10) not null);
            insert peer values (0, 'a'), (1, 'b'), (2, 'c'), (3, 'd'), (9, 'z')
            """);
        return connection;
    }

    /// <summary>6000 rows, one partition — wide enough that a deep-paging bound overruns the selection heap's ceiling.</summary>
    private static SimulatedDbConnection OpenDeep()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table t (id int not null primary key, k int not null);
            declare @i int = 1;
            while @i <= 6000 begin
                insert t values (@i, (@i * 7) % 900);
                set @i += 1;
            end
            """);
        return connection;
    }

    private static void Exec(SimulatedDbConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    /// <summary>Every row of <paramref name="query"/>, rendered as pipe-joined values in column order.</summary>
    private static List<string> Read(SimulatedDbConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < cells.Length; i++)
                cells[i] = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString() ?? string.Empty;
            rows.Add(string.Join('|', cells));
        }

        return rows;
    }

    /// <summary>
    /// Runs the bound-taking and bound-declining spellings of one query — the
    /// token <c>{rn}</c> becomes <c>rn</c> in the first and <c>(rn + 0)</c> in
    /// the second — and asserts they agree row for row. Returns the rows so the
    /// caller can assert what they are.
    /// </summary>
    private static List<string> BoundedMatchesUnbounded(SimulatedDbConnection connection, string query)
    {
        var bounded = Read(connection, query.Replace("{rn}", "rn", StringComparison.Ordinal));
        var unbounded = Read(connection, query.Replace("{rn}", "(rn + 0)", StringComparison.Ordinal));
        AreEqual(
            string.Join("\n", unbounded),
            string.Join("\n", bounded),
            "the bounded per-partition selection returned different rows than the full-sort path");
        return bounded;
    }

    private const string TiedBody =
        "select g, id, tag, row_number() over (partition by g order by k, id) as rn from t";

    private static string Shape(string filter, string body = TiedBody) =>
        $"select g, id, tag from ({body}) x where {filter}";

    // ---- the bound's own shapes ----

    [TestMethod]
    public void RowNumberEqualsOne_AgreesAndKeepsOneRowPerPartition()
    {
        using var connection = OpenTied();
        var rows = BoundedMatchesUnbounded(connection, Shape("{rn} = 1"));
        HasCount(8, rows);
    }

    [TestMethod]
    public void RowNumberAtMost_AgreesAcrossATieGroup()
    {
        using var connection = OpenTied();
        // Every k value covers three consecutive ids, so a bound of 2 cuts a tie
        // group in half in every partition — the case a heap and a full sort can
        // only agree on under a total order.
        HasCount(16, BoundedMatchesUnbounded(connection, Shape("{rn} <= 2")));
    }

    [TestMethod]
    public void RowNumberLessThan_Agrees()
    {
        using var connection = OpenTied();
        HasCount(24, BoundedMatchesUnbounded(connection, Shape("{rn} < 4")));
    }

    [TestMethod]
    public void RowNumberBetween_AgreesAndSkipsTheLeadingRows()
    {
        using var connection = OpenTied();
        HasCount(32, BoundedMatchesUnbounded(connection, Shape("{rn} between 3 and 6")));
    }

    [TestMethod]
    public void TwoOneSidedConjuncts_Agree()
    {
        using var connection = OpenTied();
        HasCount(24, BoundedMatchesUnbounded(connection, Shape("{rn} > 2 and {rn} <= 5")));
    }

    [TestMethod]
    public void ReversedOperandOrder_Agrees()
    {
        using var connection = OpenTied();
        HasCount(24, BoundedMatchesUnbounded(connection, Shape("3 >= {rn}")));
    }

    [TestMethod]
    public void EqualityFamilyBound_Agrees()
    {
        using var connection = OpenTied();
        HasCount(24, BoundedMatchesUnbounded(connection, Shape("{rn} in (1, 4, 2)")));
    }

    [TestMethod]
    public void FractionalComparand_Agrees()
    {
        using var connection = OpenTied();
        // `rn <= 2.5` keeps two rows per partition; a bound rounded the wrong
        // way would drop one the residual keeps.
        HasCount(16, BoundedMatchesUnbounded(connection, Shape("{rn} <= 2.5")));
    }

    [TestMethod]
    public void EmptyWindow_ReturnsNoRows()
    {
        using var connection = OpenTied();
        IsEmpty(BoundedMatchesUnbounded(connection, Shape("{rn} < 1")));
    }

    [TestMethod]
    public void BoundPastEveryPartition_ReturnsEveryRow()
    {
        using var connection = OpenTied();
        HasCount(240, BoundedMatchesUnbounded(connection, Shape("{rn} <= 1000")));
    }

    [TestMethod]
    public void VariableBound_AgreesAndReExecutesPerValue()
    {
        using var connection = OpenTied();
        // The same cached plan, two values: the bound is read from the executing
        // batch, so the second run may not answer with the first one's window.
        HasCount(8, BoundedMatchesUnbounded(connection, "declare @k int = 1; " + Shape("{rn} <= @k")));
        HasCount(24, BoundedMatchesUnbounded(connection, "declare @k int = 3; " + Shape("{rn} <= @k")));
    }

    [TestMethod]
    public void NullBound_ReturnsNoRows()
    {
        using var connection = OpenTied();
        IsEmpty(BoundedMatchesUnbounded(connection, "declare @k int = null; " + Shape("{rn} <= @k")));
    }

    // ---- tie behaviour, pinned ----

    [TestMethod]
    public void TiesAtTheBound_KeepTheEarliestScannedRows()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        // Twenty rows share the leading sort key, so only the arrival-order
        // tiebreak separates them — and they arrive in descending id order, so a
        // path picking by id rather than by arrival answers differently. The
        // partition is deliberately past the point where the full-sort path's
        // introsort stops being an insertion sort (and so stops being stable of
        // its own accord).
        Exec(connection, """
            create table tie (id int not null primary key, k int not null);
            declare @i int = 60;
            while @i >= 1 begin
                insert tie values (@i, @i % 3);
                set @i -= 1;
            end
            """);
        using (connection)
        {
            var rows = BoundedMatchesUnbounded(
                connection,
                "select id from (select id, row_number() over (order by k) as rn from tie) x where {rn} <= 5");
            AreEqual("60\n57\n54\n51\n48", string.Join('\n', rows));
        }
    }

    [TestMethod]
    public void AllKeysEqual_KeepsTheFirstRowsScanned()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table t (id int not null primary key, k int not null);
            declare @i int = 1;
            while @i <= 50 begin
                insert t values (@i, 7);
                set @i += 1;
            end
            """);
        using (connection)
        {
            var rows = BoundedMatchesUnbounded(
                connection,
                "select id from (select id, row_number() over (order by k) as rn from t) x where {rn} <= 3");
            AreEqual("1\n2\n3", string.Join('\n', rows));
        }
    }

    // ---- the shapes that decline still answer identically ----

    [TestMethod]
    public void Rank_Agrees()
    {
        using var connection = OpenTied();
        // RANK numbers peers alike, so a bound of 2 keeps every member of the
        // first two key groups — the row count no per-partition heap could have
        // guessed, which is why the kind declines.
        HasCount(16, BoundedMatchesUnbounded(
            connection,
            Shape("{rn} <= 2", "select g, id, tag, rank() over (partition by g order by k) as rn from t")));
    }

    [TestMethod]
    public void DenseRank_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            Shape("{rn} <= 2", "select g, id, tag, dense_rank() over (partition by g order by k) as rn from t"));
    }

    [TestMethod]
    public void ASecondWindowFunction_Agrees()
    {
        using var connection = OpenTied();
        var rows = BoundedMatchesUnbounded(
            connection,
            "select g, id, s from (select g, id, sum(k) over (partition by g) as s, "
                + "row_number() over (partition by g order by k, id) as rn from t) x where {rn} = 1");
        HasCount(8, rows);
    }

    [TestMethod]
    public void NonConstantBound_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(connection, Shape("{rn} <= g"));
    }

    [TestMethod]
    public void ConjunctUnderAnOr_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(connection, Shape("{rn} = 1 or g = 3"));
    }

    [TestMethod]
    public void RowNumberInsideAnExpression_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            Shape("{rn} = 2", "select g, id, tag, row_number() over (partition by g order by k, id) + 1 as rn from t"));
    }

    // ---- composition with the rest of the query ----

    [TestMethod]
    public void BesideAnotherFilter_Agrees()
    {
        using var connection = OpenTied();
        HasCount(4, BoundedMatchesUnbounded(connection, Shape("{rn} = 1 and g >= 4")));
    }

    [TestMethod]
    public void BodyWithItsOwnWhere_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            Shape("{rn} <= 2", "select g, id, tag, row_number() over (partition by g order by k, id) as rn from t where k > 20"));
    }

    [TestMethod]
    public void JoinedBody_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            "select x.g, x.id, x.label from (select t.g, t.id, p.label, "
                + "row_number() over (partition by t.g order by t.k, t.id) as rn "
                + "from t join peer p on p.g = t.g) x where {rn} <= 2");
    }

    [TestMethod]
    public void CteBody_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            "with c as (select g, id, tag, row_number() over (partition by g order by k, id) as rn from t) "
                + "select g, id, tag from c x where {rn} = 1");
    }

    [TestMethod]
    public void NestedThroughAPlainDerivedTable_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(
            connection,
            "select g, id from (select g, id, rn from "
                + "(select g, id, row_number() over (partition by g order by k, id) as rn from t) inner1) x "
                + "where {rn} <= 2");
    }

    [TestMethod]
    public void OuterJoinToTheBoundedSide_Agrees()
    {
        using var connection = OpenTied();
        // The bounded body is the NULL-supplied side: a partition whose rows the
        // bound dropped must leave the peer row NULL-extended and then excluded
        // by the residual, exactly as an unmatched one was.
        _ = BoundedMatchesUnbounded(
            connection,
            "select p.label, x.id from peer p left join "
                + "(select g, id, row_number() over (partition by g order by k, id) as rn from t) x "
                + "on x.g = p.g where {rn} = 1");
    }

    [TestMethod]
    public void UnderAnAggregate_Agrees()
    {
        using var connection = OpenTied();
        var rows = BoundedMatchesUnbounded(
            connection,
            "select count(*) as n, sum(id) as s from "
                + "(select g, id, row_number() over (partition by g order by k, id) as rn from t) x where {rn} = 1");
        HasCount(1, rows);
    }

    [TestMethod]
    public void WithAnOuterOrderBy_Agrees()
    {
        using var connection = OpenTied();
        _ = BoundedMatchesUnbounded(connection, Shape("{rn} <= 2") + " order by id desc");
    }

    [TestMethod]
    public void WithAnOuterTop_Agrees()
    {
        using var connection = OpenTied();
        HasCount(3, BoundedMatchesUnbounded(
            connection,
            $"select top (3) g, id, tag from ({TiedBody}) x where {{rn}} <= 2 order by g, id"));
    }

    [TestMethod]
    public void FeedingADelete_Agrees()
    {
        using var connection = OpenTied();
        const string Statement =
            """
            delete from t where id in (
                select id from (
                    select id, g, row_number() over (partition by g order by k, id) as rn from t) x
                where {rn} = 1)
            """;
        Exec(connection, Statement.Replace("{rn}", "rn", StringComparison.Ordinal));
        var afterBounded = Read(connection, "select count(*) from t");

        using var control = OpenTied();
        Exec(control, Statement.Replace("{rn}", "(rn + 0)", StringComparison.Ordinal));
        AreEqual(Read(control, "select count(*) from t")[0], afterBounded[0]);
        AreEqual("232", afterBounded[0]);
    }

    [TestMethod]
    public void RowsAreYieldedInArrivalOrder()
    {
        using var connection = OpenTied();
        // The bounded path collects per partition and then restores the arrival
        // order the full-sort path yields in — asserted by the row-for-row
        // agreement, and pinned here as an absolute too.
        var rows = BoundedMatchesUnbounded(connection, "select id from (" + TiedBody + ") x where {rn} = 1");
        AreEqual("1\n2\n3\n4\n5\n6\n7\n8", string.Join('\n', rows));
    }

    // ---- past the selection heap's ceiling ----

    [TestMethod]
    public void DeepPagingBound_Agrees()
    {
        using var connection = OpenDeep();
        var rows = BoundedMatchesUnbounded(
            connection,
            "select id, k from (select id, k, row_number() over (order by k desc, id desc) as rn from t) x "
                + "where {rn} between 5001 and 5050");
        HasCount(50, rows);
    }

    [TestMethod]
    public void BoundAtTheHeapCeiling_Agrees()
    {
        using var connection = OpenDeep();
        HasCount(4096, BoundedMatchesUnbounded(
            connection,
            "select id from (select id, row_number() over (order by k, id) as rn from t) x where {rn} <= 4096"));
    }

    [TestMethod]
    public void BoundOnePastTheHeapCeiling_Agrees()
    {
        using var connection = OpenDeep();
        HasCount(4097, BoundedMatchesUnbounded(
            connection,
            "select id from (select id, row_number() over (order by k, id) as rn from t) x where {rn} <= 4097"));
    }

    // ---- transaction / concurrency shapes the narrowing must not disturb ----

    [TestMethod]
    public void UnderSnapshotIsolation_Agrees()
    {
        using var connection = OpenTied();
        Exec(connection, "alter database current set allow_snapshot_isolation on");
        Exec(connection, "set transaction isolation level snapshot");
        _ = BoundedMatchesUnbounded(connection, Shape("{rn} = 1"));
    }

    [TestMethod]
    public void OverAnEmptyTable_ReturnsNoRows()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, "create table t (id int not null primary key, g int not null, k int not null)");
        using (connection)
        {
            IsEmpty(BoundedMatchesUnbounded(
                connection,
                "select id from (select id, row_number() over (partition by g order by k) as rn from t) x where {rn} = 1"));
        }
    }
}
