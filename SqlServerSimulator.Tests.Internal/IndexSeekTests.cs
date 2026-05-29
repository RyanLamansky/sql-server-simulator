using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards the equality-seek narrowing (<c>Selection.Execution.IndexSeek.cs</c>):
/// a single-base-table scan with <c>indexedColumn = &lt;stable value&gt;</c> WHERE
/// conjuncts must take the index seek, keyed on the longest leading key-column
/// prefix those conjuncts cover (the <c>SeekWidth(table,n)</c> trace records the
/// prefix length); reads where the seek would be unsound (snapshot / RCSI once
/// the table carries a version chain, tx-scoped row locks, non-indexed or range
/// predicates, NULL probe) must keep the full scan. The seek is
/// result-transparent, so the correctness suite passes either way — these read
/// the opt-in <see cref="IndexSeekDiagnostics"/> trace (recorded at the single
/// decision point) to assert the path directly, and check the row results stay
/// correct under it. All three single-table projectors — non-aggregate,
/// aggregate, and window — narrow through the seek. IN-list and OR-of-equality
/// conjuncts decompose through the same path: each candidate becomes one probe
/// against the per-Heap cache (cartesian-producted across columns).
/// </summary>
[TestClass]
public sealed class IndexSeekTests
{
    // Runs setup then query on one connection, capturing the seek/scan trace and
    // the first column of every result row.
    private static (List<string> Trace, List<object?> Rows) Run(string setup, string query)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using (var s = connection.CreateCommand())
        {
            s.CommandText = setup;
            _ = s.ExecuteNonQuery();
        }

        IndexSeekDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            var rows = new List<object?>();
            while (reader.Read())
                rows.Add(reader.IsDBNull(0) ? null : reader.GetValue(0));
            return (IndexSeekDiagnostics.Sink, rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    private const string TableT = """
        create table t (id int not null primary key, val int not null);
        insert t values (1, 5), (2, 50), (3, 500)
        """;

    // ---- the seek fires and stays correct ----

    [TestMethod]
    public void PrimaryKeyPointLookup_Seeks()
    {
        var (trace, rows) = Run(TableT, "select val from t where id = 2");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void PointLookup_NoMatch_SeeksToEmptyBucket()
    {
        var (trace, rows) = Run(TableT, "select val from t where id = 99");
        Contains("Seek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void NegativeLiteralEquality_Seeks()
    {
        // `-1` parses as `0 - 1` (a TwoSidedExpression), not a folded literal,
        // so the stable-value test must recurse into the arithmetic node to keep
        // it sargable — otherwise a negative-key equality silently full-scans.
        var (trace, rows) = Run(
            "create table t (id int not null primary key, val int not null); insert t values (-1, 5), (2, 50), (3, 500)",
            "select val from t where id = -1");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(5, rows[0]);
    }

    [TestMethod]
    public void ConstantArithmeticEquality_Seeks()
    {
        // A deterministic arithmetic node over row-invariant operands is itself
        // a row-invariant probe value — `id = 1 + 1` seeks the same as `id = 2`.
        var (trace, rows) = Run(TableT, "select val from t where id = 1 + 1");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void NonUniqueIndexLeadingColumn_Seeks()
    {
        var (trace, rows) = Run("""
            create table c (id int not null, pid int not null);
            create index ix_c_pid on c (pid);
            insert c values (1, 10), (2, 10), (3, 10), (4, 20)
            """, "select id from c where pid = 10");
        Contains("Seek(c)", trace);
        HasCount(3, rows);
    }

    [TestMethod]
    public void CorrelatedExists_InnerSeeks()
    {
        var (trace, rows) = Run("""
            create table p (id int not null primary key);
            create table c (id int not null, pid int not null);
            create index ix_c_pid on c (pid);
            insert p values (1), (2), (3);
            insert c values (10, 1), (11, 1), (12, 3)
            """, "select id from p where exists (select 1 from c where c.pid = p.id)");
        Contains("Seek(c)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void CorrelatedScalarSubquery_InnerSeeks()
    {
        var (trace, rows) = Run("""
            create table p (id int not null primary key);
            create table c (pid int not null primary key, amount int not null);
            insert p values (1), (2);
            insert c values (1, 100), (2, 200)
            """, "select (select amount from c where c.pid = p.id) from p where id = 2");
        Contains("Seek(c)", trace);
        HasCount(1, rows);
        AreEqual(200, rows[0]);
    }

    [TestMethod]
    public void StringKey_CaseInsensitiveCollation_Seeks()
    {
        var (trace, rows) = Run("""
            create table t (code varchar(10) not null primary key);
            insert t values ('Abc'), ('Def')
            """, "select code from t where code = 'ABC'");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
    }

    [TestMethod]
    public void CharKey_TrailingSpaceInsensitive_Seeks()
    {
        var (trace, rows) = Run("""
            create table t (c char(5) not null primary key);
            insert t values ('ab')
            """, "select c from t where c = 'ab'");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
    }

    [TestMethod]
    public void TypeMismatchProbe_PromotesAndSeeks()
    {
        var (trace, rows) = Run("""
            create table t (id int not null primary key);
            insert t values (5)
            """, "declare @v bigint = 5; select id from t where id = @v");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
    }

    [TestMethod]
    public void OutOfDomainProbe_SeeksToEmptyBucket()
    {
        var (trace, rows) = Run("""
            create table t (id int not null primary key);
            insert t values (1), (2)
            """, "declare @v bigint = 9999999999; select id from t where id = @v");
        Contains("Seek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void SeekWithResidualPredicate_AppliesBoth()
    {
        var (trace, rows) = Run(TableT, "select id from t where id = 2 and val > 10");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
    }

    // ---- pure conversions on the value side are peeled and still seek ----

    [TestMethod]
    public void CastLiteralValueSide_Seeks()
    {
        var (trace, rows) = Run(TableT, "select val from t where id = cast(2 as bigint)");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void ConvertVariableValueSide_Seeks()
    {
        var (trace, rows) = Run(TableT, "declare @v bigint = 2; select val from t where id = convert(int, @v)");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void ParenthesizedValueSide_Seeks()
    {
        var (trace, rows) = Run(TableT, "select val from t where id = ((2))");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void CorrelatedCastOfOuterRef_InnerSeeks()
    {
        var (trace, rows) = Run("""
            create table p (id int not null primary key);
            create table c (id int not null, pid int not null);
            create index ix_c_pid on c (pid);
            insert p values (1), (2), (3);
            insert c values (10, 1), (11, 3)
            """, "select id from p where exists (select 1 from c where c.pid = cast(p.id as bigint))");
        Contains("Seek(c)", trace);
        HasCount(2, rows);
    }

    // Peeling a conversion that bottoms out in a column of THIS source must not
    // seek — the value isn't row-invariant.
    [TestMethod]
    public void CastOfSameTableColumn_Declines()
    {
        var (trace, rows) = Run(TableT, "select id from t where id = cast(val as bigint)");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        IsEmpty(rows);
    }

    // ---- composite (multi-column prefix) seeks ----

    private const string CompositeT = """
        create table t (a int not null, b int not null, v int not null, primary key (a, b));
        insert t values (1, 10, 100), (1, 20, 200), (1, 30, 300), (2, 10, 400)
        """;

    [TestMethod]
    public void CompositeKey_FullEquality_SeeksOnWholePrefix()
    {
        var (trace, rows) = Run(CompositeT, "select v from t where a = 1 and b = 20");
        Contains("Seek(t)", trace);
        Contains("SeekWidth(t,2)", trace);
        HasCount(1, rows);
        AreEqual(200, rows[0]);
    }

    // Conjunct order doesn't matter — columns map to the index prefix by ordinal.
    [TestMethod]
    public void CompositeKey_ReversedConjunctOrder_SeeksOnWholePrefix()
    {
        var (trace, rows) = Run(CompositeT, "select v from t where b = 20 and a = 1");
        Contains("SeekWidth(t,2)", trace);
        HasCount(1, rows);
        AreEqual(200, rows[0]);
    }

    // Only the leading column is constrained: seek narrows on it alone, the
    // second key column stays unfiltered.
    [TestMethod]
    public void CompositeKey_LeadingColumnOnly_SeeksWidthOne()
    {
        var (trace, rows) = Run(CompositeT, "select v from t where a = 1");
        Contains("SeekWidth(t,1)", trace);
        HasCount(3, rows);
    }

    // A non-selective leading column is exactly the case the prefix seek exists
    // for: the bit flag alone barely narrows, the full (flag, id) prefix is precise.
    [TestMethod]
    public void CompositeIndex_NonSelectiveLeadingFlag_SeeksOnWholePrefix()
    {
        var (trace, rows) = Run("""
            create table t (finalized bit not null, item int not null, v int not null);
            create index ix on t (finalized, item);
            insert t values (1, 5, 50), (1, 6, 60), (0, 5, 70), (1, 5, 80)
            """, "select v from t where finalized = 1 and item = 5");
        Contains("SeekWidth(t,2)", trace);
        HasCount(2, rows);
    }

    // index (a, b, c) with the middle column unconstrained: the prefix stops at
    // the first gap, so only the leading column anchors the seek.
    [TestMethod]
    public void CompositeIndex_GapInPrefix_SeeksUpToGap()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, v int not null);
            create index ix on t (a, b, c);
            insert t values (1, 10, 100, 1), (1, 20, 100, 2), (2, 10, 100, 3)
            """, "select v from t where a = 1 and c = 100");
        Contains("SeekWidth(t,1)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void ThreeColumnKey_FullEquality_SeeksOnWholePrefix()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, v int not null, primary key (a, b, c));
            insert t values (1, 10, 100, 1), (1, 10, 200, 2), (1, 20, 100, 3)
            """, "select v from t where a = 1 and b = 10 and c = 200");
        Contains("SeekWidth(t,3)", trace);
        HasCount(1, rows);
        AreEqual(2, rows[0]);
    }

    // A NULL on a non-leading key component can't anchor a seek, so the prefix
    // stops before it; the residual `b = @n` then excludes every candidate.
    [TestMethod]
    public void CompositeKey_NullSecondComponent_SeeksLeadingThenExcludes()
    {
        var (trace, rows) = Run(CompositeT, "declare @n int = null; select v from t where a = 1 and b = @n");
        Contains("SeekWidth(t,1)", trace);
        IsEmpty(rows);
    }

    // Correlated composite seek: the inner re-keys both prefix columns per outer
    // row off the shared per-heap cache.
    [TestMethod]
    public void CompositeKey_CorrelatedExists_InnerSeeksWholePrefix()
    {
        var (trace, rows) = Run("""
            create table p (a int not null, b int not null);
            create table c (a int not null, b int not null, primary key (a, b));
            insert p values (1, 10), (1, 99), (2, 10);
            insert c values (1, 10), (2, 10)
            """, "select b from p where exists (select 1 from c where c.a = p.a and c.b = p.b)");
        Contains("SeekWidth(c,2)", trace);
        HasCount(2, rows);
    }

    // ---- the seek correctly declines (full scan) ----

    [TestMethod]
    public void NullProbe_Declines()
    {
        var (trace, rows) = Run(TableT, "declare @v int = null; select id from t where id = @v");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void NonIndexedColumn_Declines()
    {
        var (trace, rows) = Run(TableT, "select id from t where val = 50");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(1, rows);
    }

    [TestMethod]
    public void AggregateProjection_EqualityFilter_Seeks()
    {
        // The aggregate projector narrows its buffered input through the same
        // seek as the row projector — SELECT agg(...) WHERE indexedcol = x seeks.
        var (trace, rows) = Run(TableT, "select count(*) from t where id = 2");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(1, rows);
        AreEqual(1, rows[0]);
    }

    [TestMethod]
    public void WindowProjection_EqualityFilter_Seeks()
    {
        // The window projector narrows its buffered input through the same
        // seek as the row / aggregate projectors. Without this, an OVER clause
        // silently defeats a perfectly sargable WHERE (the regression that
        // was making running-total-per-parent EF queries scan the table).
        var (trace, rows) = Run(TableT,
            "select val, sum(val) over (order by id) from t where id = 2");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void InList_OnIndexedColumn_Seeks()
    {
        // `col IN (a, b, c)` is logically `col=a OR col=b OR col=c` — every
        // candidate is a stable value, so the seek fires one probe per
        // candidate and unions the buckets. EF Core's `Contains(...)` against
        // a small list emits exactly this shape.
        var (trace, rows) = Run(TableT, "select val from t where id in (1, 3)");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
        Contains(5, rows);
        Contains(500, rows);
    }

    [TestMethod]
    public void InList_NoMatches_SeeksEmpty()
    {
        // Non-matching IN-list probes still seek (every key misses its bucket);
        // the trace must show Seek (no Scan) and the result is empty.
        var (trace, rows) = Run(TableT, "select val from t where id in (99, 100)");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void InList_SingleValue_Seeks()
    {
        // A one-element IN list is the same shape as a single equality — both
        // routes (the equality fast path AND the family fast path) should
        // land on the same single probe.
        var (trace, rows) = Run(TableT, "select val from t where id in (2)");
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void InList_WithNull_SkipsNullKeepsRest()
    {
        // A NULL element in the IN list can never match (= against NULL is
        // UNKNOWN). It's silently dropped from the probe set; the non-NULL
        // candidates still anchor the seek.
        var (trace, rows) = Run(TableT, "select val from t where id in (1, null, 3)");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void InList_AllNull_Declines()
    {
        // When every IN-list element is NULL there's no usable probe; the
        // column drops out of the prefix and the seek can't anchor — falls
        // through to scan.
        var (trace, rows) = Run(TableT, "select val from t where id in (null, null)");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void NotIn_Declines()
    {
        // `col NOT IN (...)` is AND-of-inequalities, not a positive equality
        // family — the seek path can't narrow it without sorted-index
        // support, so it falls through to scan.
        var (trace, rows) = Run(TableT, "select val from t where id not in (1, 3)");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void OrEqualityChain_OnIndexedColumn_Seeks()
    {
        // `col = a OR col = b OR col = c` is equivalent to an IN list and
        // takes the same multi-probe seek; nested OR-trees flatten through
        // the recursive TryGetEqualityFamily walk.
        var (trace, rows) = Run(TableT, "select val from t where id = 1 or id = 3");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void OrEqualityChain_ReversedSides_Seeks()
    {
        // Either side of each equality may carry the column; the family
        // walker accepts both `id = lit` and `lit = id` in the same chain.
        var (trace, rows) = Run(TableT, "select val from t where 1 = id or id = 3");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void OrEqualityChain_MixedColumns_Declines()
    {
        // A single column must anchor the whole chain. `id = 1 OR val = 50`
        // can't seek (the rows behind `val = 50` aren't indexed by val), so
        // the family extractor rejects the chain and the path scans.
        var (trace, rows) = Run(TableT, "select id from t where id = 1 or val = 50");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void OrChain_WithNonEqualityLeaf_Declines()
    {
        // Non-equality leaves (range, IS NULL, etc.) abort the chain walk —
        // a single non-positive-equality breaks the family shape, so the
        // whole conjunct falls through to scan rather than narrowing.
        var (trace, rows) = Run(TableT, "select id from t where id = 1 or id > 2");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void InListCombinedWithEquality_CompositeKeySeeksWholePrefix()
    {
        // `a = x AND b IN (y, z)` expands to two probes against a composite
        // (a, b) key — the prefix width is still 2 (the IN-list is on the
        // trailing key column), each probe is one cartesian-product tuple.
        var setup = """
            create table c (a int not null, b int not null, payload int, constraint pk_c primary key (a, b));
            insert c values (1, 10, 100), (1, 20, 200), (1, 30, 300), (2, 10, 999)
            """;
        var (trace, rows) = Run(setup, "select payload from c where a = 1 and b in (10, 30) order by b");
        Contains("Seek(c)", trace);
        Contains("SeekWidth(c,2)", trace);
        DoesNotContain("Scan(c)", trace);
        HasCount(2, rows);
        AreEqual(100, rows[0]);
        AreEqual(300, rows[1]);
    }

    [TestMethod]
    public void InListOnBothCompositeColumns_SeeksCartesian()
    {
        // Two IN lists on a composite key drive a cartesian product of
        // probes: `a IN (1, 2) AND b IN (10, 20)` → four probes against
        // (a, b). Each probe hits at most one bucket so duplicates can't
        // appear in the candidate stream.
        var setup = """
            create table c (a int not null, b int not null, payload int, constraint pk_c primary key (a, b));
            insert c values (1, 10, 1), (1, 20, 2), (2, 10, 3), (2, 20, 4), (3, 30, 999)
            """;
        var (trace, rows) = Run(setup, "select payload from c where a in (1, 2) and b in (10, 20) order by payload");
        Contains("Seek(c)", trace);
        Contains("SeekWidth(c,2)", trace);
        DoesNotContain("Scan(c)", trace);
        HasCount(4, rows);
    }

    [TestMethod]
    public void RcsiRead_NoVersions_Seeks()
    {
        // With an empty version store every row is implicitly committed at
        // Xmin 0 (visible to every snapshot), so the live-heap index is sound
        // for an RCSI reader — the seek fires instead of forcing a full scan.
        var (trace, rows) = Run($"alter database simulated set read_committed_snapshot on; {TableT}",
            "select val from t where id = 2");
        Contains("Seek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void RcsiRead_WithLiveVersionChain_Seeks()
    {
        // An open writer leaves a version chain on the table. The RCSI reader
        // still seeks (it no longer declines table-wide on any version chain),
        // materializing each candidate through the version store: it sees the
        // pre-write committed value of the row the writer touched and the
        // unaffected value of another, both via the seek.
        var sim = new Simulation();
        using var reader = sim.CreateDbConnection();
        reader.Open();
        using (var setup = reader.CreateCommand())
        {
            setup.CommandText = $"alter database simulated set read_committed_snapshot on; {TableT}";
            _ = setup.ExecuteNonQuery();
        }

        using var writer = sim.CreateDbConnection();
        writer.Open();
        using var writeCmd = writer.CreateCommand();
        writeCmd.CommandText = "begin tran; update t set val = 999 where id = 1";
        _ = writeCmd.ExecuteNonQuery();

        IndexSeekDiagnostics.Sink = [];
        try
        {
            AreEqual(50, ReadVal(reader, "select val from t where id = 2"));
            Contains("Seek(t)", IndexSeekDiagnostics.Sink);
            DoesNotContain("Scan(t)", IndexSeekDiagnostics.Sink);

            // The touched row reads its last-committed value (5), not the
            // writer's uncommitted 999 — resolved through the version store on
            // the seek path.
            AreEqual(5, ReadVal(reader, "select val from t where id = 1"));
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void SnapshotRead_AfterCommittedKeyChange_SeekStaysCorrect()
    {
        // The false-negative case the table-wide decline used to guard against:
        // a committed UPDATE moves a row's PRIMARY KEY (2 -> 99) after a SNAPSHOT
        // reader pins its snapshot. The reader must still find the row by its
        // old key (the live heap no longer has id = 2, so the bucket misses it —
        // the version-chain sweep is what recovers it) and must NOT see it under
        // the new key (the snapshot predates that commit). Both stay seeks.
        var sim = new Simulation();
        using var reader = sim.CreateDbConnection();
        reader.Open();
        using (var setup = reader.CreateCommand())
        {
            setup.CommandText =
                $"alter database simulated set allow_snapshot_isolation on; {TableT}";
            _ = setup.ExecuteNonQuery();
        }

        // Pin the snapshot before the writer commits.
        Exec(reader, "set transaction isolation level snapshot");
        Exec(reader, "begin tran");
        _ = ReadVal(reader, "select count(*) from t");

        using (var writer = sim.CreateDbConnection())
        {
            writer.Open();
            Exec(writer, "update t set id = 99 where id = 2");
        }

        IndexSeekDiagnostics.Sink = [];
        try
        {
            // Old key still resolves to the snapshot-visible row (val 50).
            AreEqual(50, ReadVal(reader, "select val from t where id = 2"));
            // New key is invisible to this snapshot — found in the live bucket
            // but the resolved version carries the old key, so the residual
            // WHERE drops it.
            IsNull(ReadVal(reader, "select val from t where id = 99"));
            Contains("Seek(t)", IndexSeekDiagnostics.Sink);
            DoesNotContain("Scan(t)", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }

        Exec(reader, "commit");
    }

    private static void Exec(SimulatedDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        _ = cmd.ExecuteNonQuery();
    }

    private static object? ReadVal(SimulatedDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        return r.Read() ? (r.IsDBNull(0) ? null : r.GetValue(0)) : null;
    }

    [TestMethod]
    public void RepeatableReadHint_Declines()
    {
        var (trace, rows) = Run(TableT, "select val from t with (repeatableread) where id = 2");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(1, rows);
    }

    // ---- incremental maintenance (no warm-up): the per-Heap cache applies the
    // mutation journal delta instead of rebuilding on every write. CacheReplay /
    // CacheBuild trace which path a seek took; CacheBuild means a full scan
    // rebuild, CacheReplay means the incremental delta. ----

    private static List<object?> ReadRows(SimulatedDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var rows = new List<object?>();
        while (r.Read())
            rows.Add(r.IsDBNull(0) ? null : r.GetValue(0));
        return rows;
    }

    // Opens a connection, runs setup, warms the per-Heap seek cache (the warm
    // seek builds the entry and activates the journal — untraced, Sink is null),
    // runs `mutate`, then captures `probe`'s trace + first-column rows. The
    // captured read exercises the incrementally-maintained cache.
    private static (List<string> Trace, List<object?> Rows) WarmMutateProbe(string setup, string warm, string mutate, string probe)
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, setup);
        _ = ReadVal(c, warm);
        Exec(c, mutate);
        IndexSeekDiagnostics.Sink = [];
        try
        {
            return (IndexSeekDiagnostics.Sink, ReadRows(c, probe));
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void InsertAfterWarmup_ReplaysDelta_FindsNewRow()
    {
        var (trace, rows) = WarmMutateProbe(
            TableT, "select val from t where id = 1", "insert t values (4, 40)", "select val from t where id = 4");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(40, rows[0]);
    }

    [TestMethod]
    public void UpdateIndexedKeyAfterWarmup_RowLeavesOldBucket()
    {
        // In-place key change: the row must vanish from the old key's bucket and
        // appear under the new one, all via the Update journal event's
        // remove-old / add-new.
        var (trace, rows) = WarmMutateProbe(
            TableT, "select val from t where id = 1", "update t set id = 99 where id = 2", "select val from t where id = 2");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void UpdateIndexedKeyAfterWarmup_RowFoundUnderNewKey()
    {
        var (trace, rows) = WarmMutateProbe(
            TableT, "select val from t where id = 1", "update t set id = 99 where id = 2", "select val from t where id = 99");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        HasCount(1, rows);
        AreEqual(50, rows[0]);
    }

    [TestMethod]
    public void DeleteAfterWarmup_ReplaysDelta_RowGone()
    {
        var (trace, rows) = WarmMutateProbe(
            TableT, "select val from t where id = 1", "delete from t where id = 2", "select val from t where id = 2");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void RolledBackInsert_InvalidatesJournal_RebuildsAndExcludes()
    {
        // Rollback rewinds the heap by mutating pages directly (no reversing
        // journal events), so it invalidates the journal — the next seek must
        // rebuild from the rewound state and not surface the rolled-back row.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, TableT);
        _ = ReadVal(c, "select val from t where id = 1");
        Exec(c, "begin tran");
        Exec(c, "insert t values (4, 40)");
        Exec(c, "rollback");
        IndexSeekDiagnostics.Sink = [];
        try
        {
            var rows = ReadRows(c, "select val from t where id = 4");
            Contains("CacheBuild", IndexSeekDiagnostics.Sink);
            DoesNotContain("CacheReplay", IndexSeekDiagnostics.Sink);
            IsEmpty(rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void TruncateAfterWarmup_InvalidatesJournal_Rebuilds()
    {
        var (trace, rows) = WarmMutateProbe(
            TableT, "select val from t where id = 1", "truncate table t", "select val from t where id = 2");
        Contains("CacheBuild", trace);
        DoesNotContain("CacheReplay", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void BulkInsertBeyondJournalCap_FallsBackToRebuild()
    {
        // The journal is bounded; a single insert that overruns the cap drops the
        // oldest events and advances the dropped-through generation past the warm
        // cache's, forcing a rebuild — which still returns the correct row.
        var (trace, rows) = WarmMutateProbe(
            TableT,
            "select val from t where id = 1",
            "insert t (id, val) select value, value * 10 from generate_series(1000, 1800)",
            "select val from t where id = 1500");
        Contains("CacheBuild", trace);
        Contains("Seek(t)", trace);
        HasCount(1, rows);
        AreEqual(15000, rows[0]);
    }

    [TestMethod]
    public void InterleavedInsertSeekLoop_NeverStale()
    {
        // Each insert/seek cycle must see the row just inserted — the regression
        // an incrementally-maintained cache risks is a stale read after a write.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (id int not null primary key, val int not null)");
        for (var i = 1; i <= 50; i++)
        {
            Exec(c, $"insert t values ({i}, {i * 100})");
            AreEqual(i * 100, ReadVal(c, $"select val from t where id = {i}"));
        }
    }

    // ---- range seeks on a leading key column (>, >=, <, <=, BETWEEN). The bound
    // conjunct stays residual, so the seek only narrows; results match a scan. ----

    [TestMethod]
    public void GreaterThan_OnPrimaryKey_RangeSeeks()
    {
        var (trace, rows) = Run(TableT, "select id from t where id > 1");
        Contains("RangeSeek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
        Contains(2, rows);
        Contains(3, rows);
    }

    [TestMethod]
    public void GreaterOrEqual_OnPrimaryKey_RangeSeeks()
    {
        var (trace, rows) = Run(TableT, "select id from t where id >= 2");
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void LessThan_OnPrimaryKey_RangeSeeks()
    {
        var (trace, rows) = Run(TableT, "select id from t where id < 3");
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(1, rows);
        Contains(2, rows);
    }

    [TestMethod]
    public void LessOrEqual_OnPrimaryKey_RangeSeeks()
    {
        var (trace, rows) = Run(TableT, "select id from t where id <= 2");
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void Between_OnPrimaryKey_RangeSeeksInclusiveBothEnds()
    {
        var (trace, rows) = Run(TableT, "select id from t where id between 2 and 3");
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(2, rows);
        Contains(3, rows);
    }

    [TestMethod]
    public void TwoSidedRange_OnPrimaryKey_RangeSeeks()
    {
        var (trace, rows) = Run(TableT, "select id from t where id > 1 and id < 3");
        Contains("RangeSeek(t)", trace);
        HasCount(1, rows);
        AreEqual(2, rows[0]);
    }

    [TestMethod]
    public void ReversedOperandOrder_RangeSeeks()
    {
        // `2 < id` is `id > 2` — the planner flips the operator when the column
        // is on the right.
        var (trace, rows) = Run(TableT, "select id from t where 2 < id");
        Contains("RangeSeek(t)", trace);
        HasCount(1, rows);
        AreEqual(3, rows[0]);
    }

    [TestMethod]
    public void RangeOnSecondaryIndexLeadingColumn_RangeSeeks()
    {
        var (trace, rows) = Run("""
            create table c (id int not null, pid int not null);
            create index ix_c_pid on c (pid);
            insert c values (1, 10), (2, 20), (3, 30), (4, 5)
            """, "select id from c where pid >= 20");
        Contains("RangeSeek(c)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void RangeOnNonIndexedColumn_Declines()
    {
        var (trace, rows) = Run(TableT, "select id from t where val > 100");
        Contains("Scan(t)", trace);
        DoesNotContain("RangeSeek(t)", trace);
        HasCount(1, rows);
        AreEqual(3, rows[0]);
    }

    [TestMethod]
    public void EqualityPrefixThenRange_TakesEqualitySeek_NotRange()
    {
        // index (a, b): a = 1 AND b > 10 — the equality path seeks on the leading
        // column a (width 1) and the range on b stays residual. The single
        // leading-column range path doesn't fire here.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, v int not null, primary key (a, b));
            insert t values (1, 10, 100), (1, 20, 200), (1, 30, 300), (2, 10, 400)
            """, "select v from t where a = 1 and b > 10");
        Contains("SeekWidth(t,1)", trace);
        DoesNotContain("RangeSeek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void NullBound_RangeSeeksToEmpty()
    {
        // `id > @null` is UNKNOWN for every row, so the range matches nothing — a
        // valid (empty) seek rather than a scan.
        var (trace, rows) = Run(TableT, "declare @v int = null; select id from t where id > @v");
        Contains("RangeSeek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void OutOfDomainUpperBound_PromotesUp_ReturnsAll()
    {
        // bigint bound wider than the int column: promotion is upward, so the
        // comparison is exact and every row qualifies.
        var (trace, rows) = Run(TableT, "select id from t where id < 9999999999");
        Contains("RangeSeek(t)", trace);
        HasCount(3, rows);
    }

    [TestMethod]
    public void EmptyRange_LowerAboveUpper_RangeSeeksToEmpty()
    {
        var (trace, rows) = Run(TableT, "select id from t where id > 5 and id < 2");
        Contains("RangeSeek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void StringRange_CaseInsensitiveCollation_RangeSeeks()
    {
        var (trace, rows) = Run("""
            create table t (code varchar(10) not null primary key);
            insert t values ('apple'), ('mango'), ('pear')
            """, "select code from t where code >= 'm'");
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void DateRange_HalfOpenInterval_RangeSeeks()
    {
        // The canonical clustered-key range: created >= @from AND created < @to.
        var (trace, rows) = Run("""
            create table e (created date not null primary key, label varchar(10) not null);
            insert e values ('2026-01-01', 'a'), ('2026-02-01', 'b'), ('2026-03-01', 'c')
            """, "select label from e where created >= '2026-01-15' and created < '2026-03-01'");
        Contains("RangeSeek(e)", trace);
        HasCount(1, rows);
        AreEqual("b", rows[0]);
    }

    [TestMethod]
    public void RangeAfterWarmup_ReplaysDelta_SortedViewStaysCurrent()
    {
        // The lazily-built sorted view must be maintained incrementally: a row
        // inserted after the range warm-up must appear in a later range scan via
        // the journal replay, not a rebuild.
        var (trace, rows) = WarmMutateProbe(
            TableT, "select id from t where id > 0", "insert t values (4, 40)", "select id from t where id >= 3");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        Contains("RangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(3, rows);
        Contains(4, rows);
    }

    // ---- ORDER BY elimination: a single NOT-NULL leading-key-column sort
    // streams in key order instead of buffering + sorting. Observable if wrong,
    // so these assert the exact output sequence, not just the trace. The setup
    // inserts in scrambled order so heap order != key order — a wrong elimination
    // would surface as heap-order output. ----

    private const string ScrambledT = """
        create table t (id int not null primary key, val int not null);
        insert t values (3, 30), (1, 10), (2, 20), (5, 50), (4, 40)
        """;

    private static string Seq(List<object?> rows) => string.Join(",", rows);

    [TestMethod]
    public void OrderByPrimaryKey_Eliminates_AscendingOrder()
    {
        var (trace, rows) = Run(ScrambledT, "select id from t order by id");
        Contains("OrderedScan(t)", trace);
        AreEqual("1,2,3,4,5", Seq(rows));
    }

    [TestMethod]
    public void OrderByPrimaryKeyDescending_Eliminates_DescendingOrder()
    {
        var (trace, rows) = Run(ScrambledT, "select id from t order by id desc");
        Contains("OrderedScan(t)", trace);
        AreEqual("5,4,3,2,1", Seq(rows));
    }

    [TestMethod]
    public void OrderByWithOffsetFetch_Eliminates_StreamsCorrectPage()
    {
        var (trace, rows) = Run(ScrambledT, "select id from t order by id offset 2 rows fetch next 2 rows only");
        Contains("OrderedScan(t)", trace);
        AreEqual("3,4", Seq(rows));
    }

    [TestMethod]
    public void OrderByWithTop_Eliminates_StreamsTopN()
    {
        var (trace, rows) = Run(ScrambledT, "select top 2 id from t order by id");
        Contains("OrderedScan(t)", trace);
        AreEqual("1,2", Seq(rows));
    }

    [TestMethod]
    public void OrderByDescWithTop_Eliminates_StreamsTopN()
    {
        var (trace, rows) = Run(ScrambledT, "select top 2 id from t order by id desc");
        Contains("OrderedScan(t)", trace);
        AreEqual("5,4", Seq(rows));
    }

    [TestMethod]
    public void RangeAndOrderBySameColumn_EliminatesAndNarrows()
    {
        var (trace, rows) = Run(ScrambledT, "select id from t where id >= 2 and id < 5 order by id");
        Contains("OrderedScan(t)", trace);
        AreEqual("2,3,4", Seq(rows));
    }

    [TestMethod]
    public void ResidualFilterOnOtherColumn_StillEliminatesOrder()
    {
        // val is not indexed, so it doesn't compete with the order column — the
        // ordered scan streams by id and filters val as a residual.
        var (trace, rows) = Run(ScrambledT, "select id from t where val >= 20 order by id");
        Contains("OrderedScan(t)", trace);
        AreEqual("2,3,4,5", Seq(rows));
    }

    [TestMethod]
    public void OrderByOnSecondaryIndexColumn_Eliminates()
    {
        var (trace, rows) = Run("""
            create table t (id int not null, pid int not null);
            create index ix on t (pid);
            insert t values (1, 30), (2, 10), (3, 20)
            """, "select pid from t order by pid");
        Contains("OrderedScan(t)", trace);
        AreEqual("10,20,30", Seq(rows));
    }

    // ---- declines (keeps the buffered sort), still correct order ----

    [TestMethod]
    public void OrderByNullableColumn_Declines_NullsFirst()
    {
        // A nullable column's NULL-key rows aren't in the ordered view, so
        // elimination declines — and the sort puts NULL first (ASC).
        var (trace, rows) = Run("""
            create table t (id int not null, n int null);
            create index ix on t (n);
            insert t values (1, 30), (2, null), (3, 10)
            """, "select n from t order by n");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual(",10,30", Seq(rows));
    }

    [TestMethod]
    public void OrderByExpression_Declines()
    {
        var (trace, rows) = Run(ScrambledT, "select id from t order by id + 0");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual("1,2,3,4,5", Seq(rows));
    }

    [TestMethod]
    public void MultiColumnOrderBy_Declines()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (2, 1), (1, 2), (1, 1)
            """, "select a from t order by a, b");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual("1,1,2", Seq(rows));
    }

    [TestMethod]
    public void DistinctOrderBy_Declines()
    {
        var (trace, rows) = Run(ScrambledT, "select distinct id from t order by id");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual("1,2,3,4,5", Seq(rows));
    }

    [TestMethod]
    public void EqualityOnOtherIndexedColumn_DeclinesOrderElimination_PrefersSeek()
    {
        // status has its own index — the equality seek on it beats an ordered
        // scan of the whole table, so order elimination declines and the result
        // still comes back correctly ordered.
        var (trace, rows) = Run("""
            create table t (id int not null primary key, status int not null);
            create index ix on t (status);
            insert t values (3, 9), (1, 9), (2, 7)
            """, "select id from t where status = 9 order by id");
        DoesNotContain("OrderedScan(t)", trace);
        Contains("Seek(t)", trace);
        AreEqual("1,3", Seq(rows));
    }

    [TestMethod]
    public void OrderByEliminationCorrectAfterWarmupAndMutations()
    {
        // The ordered view feeding elimination must stay current under the
        // incremental journal: warm, then insert / delete / key-update, then a
        // later ORDER BY must reflect them in order.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, ScrambledT);
        _ = ReadRows(c, "select id from t order by id");
        Exec(c, "insert t values (10, 100)");
        Exec(c, "delete from t where id = 3");
        Exec(c, "update t set id = 0 where id = 5");
        var rows = ReadRows(c, "select id from t order by id");
        AreEqual("0,1,2,4,10", Seq(rows));
    }

    [TestMethod]
    public void OrderByEliminationDifferential_MatchesIndependentSort()
    {
        // Cross-check eliminated ORDER BY against a C# sort of a no-WHERE read
        // (which doesn't eliminate) over a non-trivial scrambled dataset.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (id int not null primary key, val int not null)");
        Exec(c, "insert t (id, val) select (value * 7919) % 1000 + 1, value from generate_series(1, 400)");

        var expected = new List<int>();
        foreach (var o in ReadRows(c, "select id from t"))
            expected.Add(Convert.ToInt32(o));
        expected.Sort();

        var actual = new List<int>();
        foreach (var o in ReadRows(c, "select id from t order by id"))
            actual.Add(Convert.ToInt32(o));

        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }

    [TestMethod]
    public void RangeCorrectUnderManyMutations_MatchesIndependentBaseline()
    {
        // Stress the incrementally-maintained sorted view: interleave inserts,
        // key-changing updates, and deletes, then confirm a range seek matches a
        // brute-force filter over a no-WHERE full read (which doesn't seek).
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (id int not null primary key, val int not null)");
        Exec(c, "insert t (id, val) select value, value from generate_series(1, 40)");
        _ = ReadVal(c, "select id from t where id > 0");
        Exec(c, "delete from t where id between 10 and 20");
        Exec(c, "update t set id = id + 100 where id between 30 and 35");
        Exec(c, "insert t values (5000, 0)");

        var expected = new List<int>();
        foreach (var o in ReadRows(c, "select id from t"))
        {
            var x = Convert.ToInt32(o);
            if (x is > 25 and < 5001)
                expected.Add(x);
        }

        var actual = new List<int>();
        foreach (var o in ReadRows(c, "select id from t where id > 25 and id < 5001"))
            actual.Add(Convert.ToInt32(o));

        expected.Sort();
        actual.Sort();
        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }
}
