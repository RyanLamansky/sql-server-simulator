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
    public void EqualityPrefixThenRange_ExtendsSeekPastPrefix()
    {
        // key (a, b): a = 1 AND b > 10 — the equality seek on a (width 1)
        // extends its seek predicate with the range on b, touching only the
        // in-range slice of a's group instead of leaving the range residual.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, v int not null, primary key (a, b));
            insert t values (1, 10, 100), (1, 20, 200), (1, 30, 300), (2, 10, 400)
            """, "select v from t where a = 1 and b > 10");
        Contains("SeekWidth(t,1)", trace);
        Contains("PrefixRangeSeek(t)", trace);
        DoesNotContain("Scan(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
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

    // ---- equality-prefix + range continuation (PrefixRangeSeek): a stable
    // range bound on the key column immediately after the matched equality
    // prefix extends the seek predicate one column further — a real index
    // seek's shape (equality prefix, then at most one range column, everything
    // deeper residual). The bound conjuncts stay in the residual WHERE, so the
    // extension only narrows; results match a scan. ----

    private const string PrefixRangeT = """
        create table t (a int not null, b int not null, v int not null, primary key (a, b));
        insert t values (1, 10, 100), (1, 20, 200), (1, 30, 300), (1, 40, 400), (2, 10, 500), (2, 20, 600)
        """;

    [TestMethod]
    public void PrefixRange_Between_InclusiveBothEnds()
    {
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 1 and b between 20 and 30");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
    }

    [TestMethod]
    public void PrefixRange_ExclusiveUpperBound()
    {
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 1 and b < 30");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(100, rows);
        Contains(200, rows);
    }

    [TestMethod]
    public void PrefixRange_TwoSidedExclusive()
    {
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 1 and b > 10 and b < 40");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
    }

    [TestMethod]
    public void PrefixRange_NeverCrossesIntoSiblingGroup()
    {
        // The (a = 2) group also has b = 10..20 — the composite bound keys the
        // range under the pinned a, so no sibling-group row can leak in.
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 2 and b >= 10");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(500, rows);
        Contains(600, rows);
    }

    [TestMethod]
    public void PrefixRange_TwoEqualitiesThenRange_ExtendsAfterWidthTwo()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, v int not null, primary key (a, b, c));
            insert t values (1, 1, 5, 100), (1, 1, 15, 200), (1, 1, 25, 300), (1, 2, 15, 400)
            """, "select v from t where a = 1 and b = 1 and c >= 15");
        Contains("SeekWidth(t,2)", trace);
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
    }

    [TestMethod]
    public void RangeBeyondContinuationColumn_StaysResidual()
    {
        // key (a, b, c): the range sits on c but b has no equality, so the seek
        // predicate ends at a (width 1) and the c bound stays a residual filter
        // — matching a real seek predicate, which can't skip a key column.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, v int not null, primary key (a, b, c));
            insert t values (1, 1, 5, 100), (1, 2, 15, 200), (1, 3, 25, 300), (2, 1, 15, 400)
            """, "select v from t where a = 1 and c >= 15");
        Contains("SeekWidth(t,1)", trace);
        DoesNotContain("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
    }

    [TestMethod]
    public void PrefixRange_InListPrefix_SeeksPerProbe()
    {
        // The range continuation composes with an IN-list prefix: each probe
        // value fires its own composite-bounded slice.
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a in (1, 2) and b >= 20");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(4, rows);
        Contains(200, rows);
        Contains(300, rows);
        Contains(400, rows);
        Contains(600, rows);
    }

    [TestMethod]
    public void PrefixRange_NullBound_SeeksToEmpty()
    {
        // b > @null is UNKNOWN for every row — a valid empty seek, narrower
        // than probing a's whole group.
        var (trace, rows) = Run(PrefixRangeT, "declare @v int = null; select v from t where a = 1 and b > @v");
        Contains("PrefixRangeSeek(t)", trace);
        IsEmpty(rows);
    }

    [TestMethod]
    public void PrefixRange_ResidualPredicateStillApplies()
    {
        // The non-seekable conjunct (v > 250) filters the seeked slice.
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 1 and b >= 20 and v > 250");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(300, rows);
        Contains(400, rows);
    }

    [TestMethod]
    public void PrefixRange_SecondaryIndex_Seeks()
    {
        var (trace, rows) = Run("""
            create table c (id int not null, pid int not null, seq int not null);
            create index ix_c on c (pid, seq);
            insert c values (1, 10, 1), (2, 10, 2), (3, 10, 3), (4, 20, 1)
            """, "select id from c where pid = 10 and seq >= 2");
        Contains("PrefixRangeSeek(c)", trace);
        HasCount(2, rows);
        Contains(2, rows);
        Contains(3, rows);
    }

    [TestMethod]
    public void PrefixRange_RangeContinuationBreaksTie_PicksExtendingIndex()
    {
        // Two indexes match the same-width equality prefix on a; only (a, b)
        // can extend with the range on b, so it wins the tie.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, v int not null);
            create index ix_ac on t (a, c);
            create index ix_ab on t (a, b);
            insert t values (1, 10, 7, 100), (1, 20, 8, 200), (1, 30, 9, 300), (2, 10, 7, 400)
            """, "select v from t where a = 1 and b > 10");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(2, rows);
        Contains(200, rows);
        Contains(300, rows);
    }

    [TestMethod]
    public void PrefixRange_CorrelatedBound_SeeksPerOuterRow()
    {
        // The range bound is a correlated outer reference — evaluated per outer
        // row against the shared per-Heap cache, like correlated equality probes.
        var (trace, rows) = Run("""
            create table p (id int not null primary key, threshold int not null);
            create table c (id int not null, pid int not null, num int not null);
            create index ix_c on c (pid, num);
            insert p values (1, 15), (2, 5);
            insert c values (10, 1, 10), (11, 1, 20), (12, 2, 10), (13, 3, 99)
            """, "select id from p where exists (select 1 from c where c.pid = p.id and c.num > p.threshold)");
        Contains("PrefixRangeSeek(c)", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void PrefixRange_DateSlicePerCustomer_Seeks()
    {
        // The canonical OLTP shape: customer pin + date window on (cust, dt).
        var (trace, rows) = Run("""
            create table o (cust int not null, dt date not null, amt int not null, primary key (cust, dt));
            insert o values (1, '2026-01-05', 10), (1, '2026-02-05', 20), (1, '2026-03-05', 30), (2, '2026-02-05', 40)
            """, "select amt from o where cust = 1 and dt >= '2026-02-01' and dt < '2026-03-01'");
        Contains("PrefixRangeSeek(o)", trace);
        HasCount(1, rows);
        AreEqual(20, rows[0]);
    }

    [TestMethod]
    public void PrefixRange_AggregateProjection_Seeks()
    {
        var (trace, rows) = Run(PrefixRangeT, "select sum(v) from t where a = 1 and b >= 20");
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(1, rows);
        AreEqual(900, rows[0]);
    }

    [TestMethod]
    public void PrefixRange_WidenedEntryServesNarrowerSeek_NoRebuildThrash()
    {
        // Alternating `a = 1 AND b > 10` (composite (a, b) entry) and `a = 1`
        // (arity-1 probe) must share one widened cache entry: the narrow probe
        // reads its arity's lazily-built hash view instead of forcing a rebuild
        // back to the (a) prefix per query.
        var (trace, rows) = WarmMutateProbe(
            PrefixRangeT, "select v from t where a = 1 and b > 10", "insert t values (3, 1, 700)", "select v from t where a = 1");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        Contains("Seek(t)", trace);
        HasCount(4, rows);
    }

    [TestMethod]
    public void PrefixRange_SmallGroup_ReturnsGroupForResidual()
    {
        // Below the group-size threshold the continuation returns the whole
        // equality group and the residual WHERE applies the range — walking
        // the ordered view per key costs more than the residual there.
        var (trace, rows) = Run(PrefixRangeT, "select v from t where a = 1 and b between 20 and 30");
        Contains("PrefixRangeGroup", trace);
        DoesNotContain("PrefixRangeSlice", trace);
        HasCount(2, rows);
    }

    [TestMethod]
    public void PrefixRange_LargeGroup_TakesOrderedSlice()
    {
        // A 400-row group exceeds the threshold, so the seek slices the
        // composite ordered view — only the in-range keys are touched, and the
        // composite bounds keep the slice inside the pinned group.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, v int not null, primary key (a, b));
            insert t select 1, value, value * 10 from generate_series(1, 400);
            insert t values (2, 105, 9999)
            """, "select v from t where a = 1 and b between 100 and 110");
        Contains("PrefixRangeSlice", trace);
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(11, rows);
        Contains(1000, rows);
        Contains(1100, rows);
        DoesNotContain(9999, rows);
    }

    [TestMethod]
    public void PrefixRange_NarrowHashView_MaintainedByReplay_FindsNewRow()
    {
        // Builds the widened (a, b) entry, then the arity-1 narrow hash view,
        // then inserts into the probed group: the journal replay must maintain
        // the narrow view alongside the buckets — the add side is the one
        // maintenance direction the residual WHERE can't repair (a missing
        // candidate is a lost row, not a filtered false-positive).
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, PrefixRangeT);
        _ = ReadVal(c, "select v from t where a = 1 and b > 10"); // widens the entry to (a, b)
        _ = ReadVal(c, "select v from t where a = 1");            // builds the arity-1 narrow view
        Exec(c, "insert t values (1, 50, 700)");
        IndexSeekDiagnostics.Sink = [];
        try
        {
            var rows = ReadRows(c, "select v from t where a = 1");
            Contains("CacheReplay", IndexSeekDiagnostics.Sink);
            DoesNotContain("CacheBuild", IndexSeekDiagnostics.Sink);
            HasCount(5, rows);
            Contains(700, rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void PrefixRange_NarrowHashView_KeyChangeMovesRowAcrossBuckets()
    {
        // The remove direction: a key-moving UPDATE against the widened entry
        // must relocate the row across narrow-view buckets via the replay.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, PrefixRangeT);
        _ = ReadVal(c, "select v from t where a = 1 and b > 10");
        _ = ReadVal(c, "select v from t where a = 1");
        Exec(c, "update t set a = 3 where a = 1 and b = 40");
        HasCount(3, ReadRows(c, "select v from t where a = 1"));
        var moved = ReadRows(c, "select v from t where a = 3");
        HasCount(1, moved);
        AreEqual(400, moved[0]);
    }

    [TestMethod]
    public void PrefixRange_AfterWarmup_ReplaysDelta()
    {
        // The composite ordered view is maintained by the same journal replay as
        // the hash buckets — a row inserted after warm-up lands in the slice.
        var (trace, rows) = WarmMutateProbe(
            PrefixRangeT, "select v from t where a = 1 and b > 10", "insert t values (1, 25, 250)", "select v from t where a = 1 and b > 10");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        Contains("PrefixRangeSeek(t)", trace);
        HasCount(4, rows);
        Contains(250, rows);
    }

    [TestMethod]
    public void PrefixRange_RcsiRead_MaterializesThroughVersionStore()
    {
        // The extended seek rides the same snapshot materializer as the pure
        // equality seek: an open writer's uncommitted update is invisible, the
        // pre-write committed values come back through the version store.
        var sim = new Simulation();
        using var reader = sim.CreateDbConnection();
        reader.Open();
        using (var setup = reader.CreateCommand())
        {
            setup.CommandText = $"alter database simulated set read_committed_snapshot on; {PrefixRangeT}";
            _ = setup.ExecuteNonQuery();
        }

        using var writer = sim.CreateDbConnection();
        writer.Open();
        using var writeCmd = writer.CreateCommand();
        writeCmd.CommandText = "begin tran; update t set v = 999 where a = 1 and b = 20";
        _ = writeCmd.ExecuteNonQuery();

        IndexSeekDiagnostics.Sink = [];
        try
        {
            var rows = ReadRows(reader, "select v from t where a = 1 and b >= 20");
            Contains("PrefixRangeSeek(t)", IndexSeekDiagnostics.Sink);
            DoesNotContain("Scan(t)", IndexSeekDiagnostics.Sink);
            HasCount(3, rows);
            Contains(200, rows);
            DoesNotContain(999, rows);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
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

    [TestMethod]
    public void EqualityPrefixThenOrderBySuffix_Eliminates()
    {
        // WHERE a = 1 ORDER BY b on PK (a, b): the seek positions on a = 1 and
        // the trailing key column emerges already ordered — no buffered sort, and
        // only the a = 1 group is touched (a = 2 rows are interleaved in heap
        // order to prove the scan doesn't drag them in).
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (2, 5), (1, 30), (1, 10), (2, 1), (1, 20)
            """, "select b from t where a = 1 order by b");
        Contains("OrderedScan(t)", trace);
        AreEqual("10,20,30", Seq(rows));
    }

    [TestMethod]
    public void EqualityPrefixThenOrderBySuffixDescending_Eliminates()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (2, 5), (1, 30), (1, 10), (1, 20)
            """, "select b from t where a = 1 order by b desc");
        Contains("OrderedScan(t)", trace);
        AreEqual("30,20,10", Seq(rows));
    }

    [TestMethod]
    public void EqualityPrefixWithRedundantOrderColumn_Eliminates()
    {
        // ORDER BY a, b with a pinned to a constant ≡ ORDER BY b: a is stripped as
        // a constant, leaving b as the effective order column.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 30), (1, 10), (2, 99), (1, 20)
            """, "select b from t where a = 1 order by a, b");
        Contains("OrderedScan(t)", trace);
        AreEqual("10,20,30", Seq(rows));
    }

    [TestMethod]
    public void EqualityPrefixAndRangeOnOrderColumn_EliminatesAndNarrows()
    {
        // WHERE a = 1 AND b >= 20 AND b < 40 ORDER BY b: the pinned a = 1 prefix
        // plus a folded range on b stream the matching slice already ordered.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 10), (1, 20), (1, 30), (1, 40), (2, 25)
            """, "select b from t where a = 1 and b >= 20 and b < 40 order by b");
        Contains("OrderedScan(t)", trace);
        AreEqual("20,30", Seq(rows));
    }

    [TestMethod]
    public void MultiColumnOrderByDescending_Eliminates_Reversed()
    {
        // ORDER BY a DESC, b DESC reverses the ascending composite view.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (2, 1), (1, 2), (1, 1), (2, 2)
            """, "select 10 * a + b from t order by a desc, b desc");
        Contains("OrderedScan(t)", trace);
        AreEqual("22,21,12,11", Seq(rows));
    }

    [TestMethod]
    public void EqualityPrefixNullProbe_ReturnsEmpty()
    {
        // a = NULL matches nothing; the ordered seek resolves to an empty stream.
        // (a is a nullable leading index column, not a PK column.)
        var (_, rows) = Run("""
            create table t (a int null, b int not null);
            create index ix on t (a, b);
            insert t values (1, 10), (1, 20)
            """, "select b from t where a = null order by b");
        IsEmpty(rows);
    }

    [TestMethod]
    public void EqualityPrefixOrderedScanCorrectAfterMutations()
    {
        // The composite ordered view feeding an equality-prefix scan must stay
        // current under the incremental journal: warm, mutate, then a later
        // WHERE a = 1 ORDER BY b reflects the changes in order.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (a int not null, b int not null, primary key (a, b))");
        Exec(c, "insert t values (1, 30), (1, 10), (2, 5), (1, 20)");
        _ = ReadRows(c, "select b from t where a = 1 order by b");
        Exec(c, "insert t values (1, 5), (2, 1)");
        Exec(c, "delete from t where a = 1 and b = 20");
        Exec(c, "update t set b = 15 where a = 1 and b = 30");
        var rows = ReadRows(c, "select b from t where a = 1 order by b");
        AreEqual("5,10,15", Seq(rows));
    }

    [TestMethod]
    public void MultiColumnOrderByDifferential_MatchesIndependentSort()
    {
        // Cross-check an eliminated composite ORDER BY against a C# sort over a
        // no-WHERE read (which doesn't eliminate). 1000 * a + b fully encodes the
        // (a, b) order (b < 1000), so sorting the integer equals ORDER BY a, b.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (id int not null primary key, a int not null, b int not null)");
        Exec(c, "create index ix on t (a, b)");
        Exec(c, "insert t (id, a, b) select value, (value * 7919) % 17, (value * 104729) % 97 from generate_series(1, 300)");

        var expected = new List<int>();
        foreach (var o in ReadRows(c, "select 1000 * a + b from t"))
            expected.Add(Convert.ToInt32(o));
        expected.Sort();

        var actual = new List<int>();
        foreach (var o in ReadRows(c, "select 1000 * a + b from t order by a, b"))
            actual.Add(Convert.ToInt32(o));

        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }

    // ---- keyset pagination: WHERE a > @x OR (a = @x AND b > @y) ORDER BY a, b
    // seeks past the cursor instead of scanning + sorting + skipping ----

    [TestMethod]
    public void KeysetForwardTwoColumn_SeeksPastCursor()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (3, 1)
            """, "select 10 * a + b from t where a > 1 or (a = 1 and b > 2) order by a, b");
        Contains("OrderedScan(t)", trace);
        Contains("KeysetSeek(t)", trace);
        AreEqual("13,21,22,31", Seq(rows));
    }

    [TestMethod]
    public void KeysetDescendingTwoColumn_SeeksPastCursor()
    {
        // a DESC, b DESC keyset uses the < staircase; the cursor is the exclusive
        // upper bound, and the ascending in-range list is reversed into the page.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 1), (1, 2), (1, 3), (2, 1), (2, 2), (3, 1)
            """, "select 10 * a + b from t where a < 3 or (a = 3 and b < 1) order by a desc, b desc");
        Contains("OrderedScan(t)", trace);
        Contains("KeysetSeek(t)", trace);
        AreEqual("22,21,13,12,11", Seq(rows));
    }

    [TestMethod]
    public void KeysetThreeColumn_SeeksPastCursor()
    {
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, c int not null, primary key (a, b, c));
            insert t values (1, 2, 1), (1, 2, 2), (1, 2, 3), (1, 3, 1), (2, 1, 1)
            """, "select 100 * a + 10 * b + c from t where a > 1 or (a = 1 and b > 2) or (a = 1 and b = 2 and c > 2) order by a, b, c");
        Contains("KeysetSeek(t)", trace);
        // Rows (1,2,3),(1,3,1),(2,1,1) in key order → 100*a + 10*b + c.
        AreEqual("123,131,211", Seq(rows));
    }

    [TestMethod]
    public void KeysetOnNonClusteredCompositeIndex_SeeksPastCursor()
    {
        // The cursor seek rides a secondary CREATE INDEX, not just the PK —
        // acceleration is index-source-agnostic.
        var (trace, rows) = Run("""
            create table t (id int not null primary key, a int not null, b int not null);
            create index ix on t (a, b);
            insert t values (10, 1, 1), (20, 1, 2), (30, 2, 1), (40, 2, 2), (50, 3, 9)
            """, "select id from t where a > 2 or (a = 2 and b > 1) order by a, b");
        Contains("OrderedScan(t)", trace);
        Contains("KeysetSeek(t)", trace);
        AreEqual("40,50", Seq(rows));
    }

    [TestMethod]
    public void KeysetWithResidualFilter_SeeksAndFilters()
    {
        // The keyset OR is one conjunct; an unrelated AND filter stays residual.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, active int not null, primary key (a, b));
            insert t values (1, 1, 1), (1, 2, 0), (2, 1, 1), (2, 2, 1), (3, 1, 0)
            """, "select 10 * a + b from t where (a > 1 or (a = 1 and b > 1)) and active = 1 order by a, b");
        Contains("KeysetSeek(t)", trace);
        AreEqual("21,22", Seq(rows));
    }

    [TestMethod]
    public void KeysetParameterizedCursor_SeeksPastCursor()
    {
        // Variables are stable cursor values, the normal pagination shape. The
        // DECLAREs share the query's batch (Run runs setup separately).
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 1), (1, 2), (2, 1), (2, 2), (3, 1)
            """, "declare @a int = 2; declare @b int = 1; select 10 * a + b from t where a > @a or (a = @a and b > @b) order by a, b");
        Contains("KeysetSeek(t)", trace);
        AreEqual("22,31", Seq(rows));
    }

    [TestMethod]
    public void KeysetDifferential_MatchesIndependentFilterAndSort()
    {
        // Cross-check the keyset seek against a C# (a, b) > (cursor) filter + sort
        // over a scrambled non-clustered composite index.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, "create table t (id int not null primary key, a int not null, b int not null)");
        Exec(c, "create index ix on t (a, b)");
        Exec(c, "insert t (id, a, b) select value, (value * 7919) % 23, (value * 104729) % 89 from generate_series(1, 400)");

        var expected = new List<int>();
        foreach (var (a, b) in ReadPairs(c, "select a, b from t"))
        {
            if ((a > 11) || ((a == 11) && (b > 40)))
                expected.Add((1000 * a) + b);
        }
        expected.Sort();

        var actual = new List<int>();
        foreach (var o in ReadRows(c, "select 1000 * a + b from t where a > 11 or (a = 11 and b > 40) order by a, b"))
            actual.Add(Convert.ToInt32(o));

        AreEqual(string.Join(",", expected), string.Join(",", actual));
    }

    private static List<(int A, int B)> ReadPairs(SimulatedDbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var pairs = new List<(int, int)>();
        while (r.Read())
            pairs.Add((Convert.ToInt32(r.GetValue(0)), Convert.ToInt32(r.GetValue(1))));
        return pairs;
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
    public void MultiColumnOrderBy_OnCompositeKey_Eliminates()
    {
        // ORDER BY a, b matches the (a, b) PK leading prefix, both NOT NULL, so
        // the composite ordered view streams in key order — no buffered sort.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (2, 1), (1, 2), (1, 1)
            """, "select a from t order by a, b");
        Contains("OrderedScan(t)", trace);
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
    public void MixedDirectionMultiColumnOrderBy_Declines()
    {
        // a ASC, b DESC can't be served by the single ascending-by-value
        // composite view (only all-ASC or all-DESC), so elimination declines.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 2), (1, 1), (2, 1)
            """, "select 10 * a + b from t order by a asc, b desc");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual("12,11,21", Seq(rows));
    }

    [TestMethod]
    public void MultiColumnOrderByNullableSuffix_Declines()
    {
        // b is nullable, so its NULL-key rows aren't in the composite view —
        // elimination declines and the sort puts the NULL first.
        var (trace, rows) = Run("""
            create table t (a int not null, b int null);
            create index ix on t (a, b);
            insert t values (1, 30), (1, null), (1, 10)
            """, "select b from t where a = 1 order by b");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual(",10,30", Seq(rows));
    }

    [TestMethod]
    public void KeysetInconsistentCursorValues_DeclinesKeyset_StillCorrect()
    {
        // a > 1 OR (a = 0 AND b > 5) isn't a clean (a, b) > cursor staircase (the
        // a-value disagrees: 1 vs 0), so the keyset cursor declines — the ordered
        // scan still runs and the residual OR filters it to the right rows.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (0, 9), (1, 1), (1, 2), (2, 1)
            """, "select 10 * a + b from t where a > 1 or (a = 0 and b > 5) order by a, b");
        DoesNotContain("KeysetSeek(t)", trace);
        Contains("OrderedScan(t)", trace);
        AreEqual("9,21", Seq(rows));
    }

    [TestMethod]
    public void OrderByColumnsNotMatchingKeyOrder_Declines()
    {
        // ORDER BY b, a doesn't match the (a, b) key order, so no ordered view
        // serves it.
        var (trace, rows) = Run("""
            create table t (a int not null, b int not null, primary key (a, b));
            insert t values (1, 2), (2, 1), (1, 1)
            """, "select 10 * a + b from t order by b, a");
        DoesNotContain("OrderedScan(t)", trace);
        AreEqual("11,21,12", Seq(rows));
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

    // ---- foreign-key enforcement rides the same per-Heap seek cache (shared via
    // HeapSeekCache.For). An INSERT … VALUES does no query-path seek, so any
    // CacheBuild / CacheReplay during a child insert is the parent-existence
    // seek; a no-WHERE parent delete does no query-path seek either, isolating
    // the cascade's child-lookup seek. ----

    [TestMethod]
    public void ForeignKeyChildInsert_SeeksParentExistence()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table p (id int not null primary key);
            create table ch (id int not null primary key, pid int not null references p(id));
            insert p values (1), (2), (3)
            """);
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(c, "insert ch values (10, 2)");
            // INSERT VALUES has no SELECT/WHERE to seek, so this is the FK
            // parent-existence seek building the parent's id index.
            Contains("CacheBuild", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void ForeignKeyChildInsert_ReplaysParentDelta_NoWarmup()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table p (id int not null primary key);
            create table ch (id int not null primary key, pid int not null references p(id));
            insert p values (1), (2)
            """);
        Exec(c, "insert ch values (10, 1)"); // builds the parent seek cache
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(c, "insert p values (3)");      // moves the parent's mutation generation
            Exec(c, "insert ch values (11, 3)"); // parent-existence seek replays the delta
            Contains("CacheReplay", IndexSeekDiagnostics.Sink);
            DoesNotContain("CacheBuild", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void ForeignKeyCascadeDelete_SeeksChildren()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table p (id int not null primary key);
            create table ch (id int not null primary key, pid int not null references p(id) on delete cascade);
            insert p values (1), (2);
            insert ch values (10, 1), (11, 1), (12, 2)
            """);
        IndexSeekDiagnostics.Sink = [];
        try
        {
            // No WHERE → the parent delete itself doesn't seek, so the cache
            // activity is the cascade seeking children by their pid.
            Exec(c, "delete from p");
            Contains("CacheBuild", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
        AreEqual(0, Convert.ToInt32(ReadVal(c, "select count(*) from ch")));
    }

    [TestMethod]
    public void ForeignKeyCascadeDelete_SelectiveParent_CorrectChildrenRemain()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table p (id int not null primary key);
            create table ch (id int not null primary key, pid int not null references p(id) on delete cascade);
            insert p values (1), (2), (3);
            insert ch values (10, 1), (11, 1), (12, 2), (13, 3)
            """);
        Exec(c, "delete from p where id = 1");
        AreEqual("12,13", Seq(ReadRows(c, "select id from ch order by id")));
    }

    // ---- UPDATE / DELETE target scans ride the same per-Heap seek cache
    // (Selection.SeekMutationTarget). A single-table UPDATE / DELETE does no
    // query-path seek of its own, so any CacheBuild / CacheReplay during one is
    // the mutation target seek; a full scan touches the cache not at all. The
    // mutation loop re-runs the full WHERE per row, so the seek only narrows. ----

    private static List<string> ExecTraced(SimulatedDbConnection c, string sql)
    {
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(c, sql);
            return IndexSeekDiagnostics.Sink;
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    private static SimulatedDbConnection FreshT()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, TableT);
        return c;
    }

    [TestMethod]
    public void UpdateByPrimaryKey_SeeksTarget()
    {
        var c = FreshT();
        Contains("CacheBuild", ExecTraced(c, "update t set val = 999 where id = 2"));
        AreEqual(999, Convert.ToInt32(ReadVal(c, "select val from t where id = 2")));
        AreEqual("5,500", Seq(ReadRows(c, "select val from t where id <> 2 order by id")));
    }

    [TestMethod]
    public void DeleteByPrimaryKey_SeeksTarget()
    {
        var c = FreshT();
        Contains("CacheBuild", ExecTraced(c, "delete from t where id = 2"));
        AreEqual("1,3", Seq(ReadRows(c, "select id from t order by id")));
    }

    [TestMethod]
    public void DeleteByRange_RangeSeeksTarget()
    {
        var c = FreshT();
        Contains("CacheBuild", ExecTraced(c, "delete from t where id >= 2"));
        AreEqual("1", Seq(ReadRows(c, "select id from t order by id")));
    }

    [TestMethod]
    public void DeleteByInList_SeeksTarget()
    {
        var c = FreshT();
        Contains("CacheBuild", ExecTraced(c, "delete from t where id in (1, 3)"));
        AreEqual("2", Seq(ReadRows(c, "select id from t order by id")));
    }

    [TestMethod]
    public void UpdateByPrefixRange_SeeksTarget()
    {
        // key (a, b): the mutation target seek extends the equality on a with
        // the range on b, rewriting only the in-range slice of a's group.
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, PrefixRangeT);
        Contains("CacheBuild", ExecTraced(c, "update t set v = 0 where a = 1 and b >= 20"));
        AreEqual("100,0,0,0,500,600", Seq(ReadRows(c, "select v from t order by a, b")));
    }

    [TestMethod]
    public void DeleteByPrefixRange_SeeksTarget()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, PrefixRangeT);
        Contains("CacheBuild", ExecTraced(c, "delete from t where a = 1 and b between 20 and 30"));
        AreEqual("100,400,500,600", Seq(ReadRows(c, "select v from t order by a, b")));
    }

    [TestMethod]
    public void UpdateOnUnindexedColumn_FullScans()
    {
        var c = FreshT();
        // val carries no key / index, so nothing seekable — the mutation keeps
        // its full scan and never touches the seek cache.
        DoesNotContain("CacheBuild", ExecTraced(c, "update t set val = 0 where val = 50"));
        AreEqual(0, Convert.ToInt32(ReadVal(c, "select val from t where id = 2")));
    }

    [TestMethod]
    public void UpdateSeek_ResidualWhereStillExcludes()
    {
        var c = FreshT();
        // id = 2 seeks the single row, but the residual val > 1000 conjunct
        // (re-checked in the mutation loop) excludes it: zero rows updated.
        Exec(c, "update t set val = 7 where id = 2 and val > 1000");
        AreEqual(50, Convert.ToInt32(ReadVal(c, "select val from t where id = 2")));
    }

    [TestMethod]
    public void UpdateSeek_ReplaysDelta_NoWarmup()
    {
        var c = FreshT();
        Exec(c, "update t set val = val where id = 1"); // warms the seek cache
        Exec(c, "insert t values (4, 40)");             // moves the mutation generation
        var trace = ExecTraced(c, "update t set val = 111 where id = 4");
        Contains("CacheReplay", trace);
        DoesNotContain("CacheBuild", trace);
        AreEqual(111, Convert.ToInt32(ReadVal(c, "select val from t where id = 4")));
    }

    [TestMethod]
    public void DeleteSeek_NoMatch_LeavesTableIntact()
    {
        var c = FreshT();
        Exec(c, "delete from t where id = 99");
        AreEqual("1,2,3", Seq(ReadRows(c, "select id from t order by id")));
    }

    // ---- MERGE inverts its target × source scan into a per-source-row target
    // seek (Selection.TryPrepareMergeTargetSeek) when the ON carries a seekable
    // target equality, no NOT MATCHED BY SOURCE clause forces every target to be
    // visited, and the target isn't a view. A MERGE does no query-path seek of
    // its own, so a CacheBuild during one is the inverted target seek; the
    // declined cases (NMBS clause, non-seekable ON) keep the full scan and never
    // touch the cache. ----

    private static SimulatedDbConnection FreshTarget()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table tgt (id int not null primary key, v int not null);
            insert tgt values (1, 10), (2, 20), (3, 30)
            """);
        return c;
    }

    [TestMethod]
    public void MergeOnPrimaryKey_SeeksTarget()
    {
        var c = FreshTarget();
        var trace = ExecTraced(c, """
            merge tgt as t
            using (values (2, 200), (4, 400)) as s(id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched then insert (id, v) values (s.id, s.v);
            """);
        Contains("CacheBuild", trace);
        AreEqual(4, Convert.ToInt32(ReadVal(c, "select count(*) from tgt")));
        AreEqual(200, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 2"))); // matched → updated
        AreEqual(400, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 4"))); // not matched → inserted
        AreEqual(10, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 1")));  // untouched
    }

    [TestMethod]
    public void MergeNotMatchedBySource_SeeksThenComplementScans()
    {
        var c = FreshTarget();
        // A NOT MATCHED BY SOURCE clause must visit every target to find the
        // unmatched ones, but the match phase still seeks (CacheBuild): the seek
        // builds matchedByTarget, then one heap pass applies MATCHED to the hits
        // and BY-SOURCE DELETE to the rest — no per-target source loop.
        var trace = ExecTraced(c, """
            merge tgt as t
            using (values (2, 200)) as s(id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched by source then delete;
            """);
        Contains("CacheBuild", trace);
        AreEqual("2", Seq(ReadRows(c, "select id from tgt order by id"))); // 1 and 3 deleted
        AreEqual(200, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 2")));
    }

    [TestMethod]
    public void MergeNotMatchedBySource_ThreeWay_AllBranchesCorrect()
    {
        var c = FreshTarget(); // tgt = (1,10), (2,20), (3,30)
        // id=2 matched → update; id=4 not matched by target → insert; id=1,3 not
        // matched by source → delete. Exercises all three branches through the
        // seek + complement-scan path.
        Exec(c, """
            merge tgt as t
            using (values (2, 222), (4, 444)) as s(id, v) on t.id = s.id
            when matched then update set v = s.v
            when not matched then insert (id, v) values (s.id, s.v)
            when not matched by source then delete;
            """);
        AreEqual("2,4", Seq(ReadRows(c, "select id from tgt order by id")));
        AreEqual(222, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 2")));
        AreEqual(444, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 4")));
    }

    [TestMethod]
    public void MergeSeek_ResidualOnTerm_Filters()
    {
        var c = new Simulation().CreateDbConnection();
        c.Open();
        Exec(c, """
            create table tgt (id int not null primary key, active int not null, v int not null);
            insert tgt values (1, 1, 10), (2, 0, 20)
            """);
        // ON seeks id; the residual t.active = 1 (re-checked per candidate)
        // excludes id = 2, so its source row matches nothing and v stays 20.
        var trace = ExecTraced(c, """
            merge tgt as t
            using (values (1, 100), (2, 200)) as s(id, v) on t.id = s.id and t.active = 1
            when matched then update set v = s.v;
            """);
        Contains("CacheBuild", trace);
        AreEqual(100, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 1")));
        AreEqual(20, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 2")));
    }

    [TestMethod]
    public void MergeNonSeekableOn_FullScans()
    {
        var c = FreshTarget();
        // ON on an unindexed expression (v, no key) — nothing seekable, so the
        // inversion declines and the scan stands.
        var trace = ExecTraced(c, """
            merge tgt as t
            using (values (20, 999)) as s(v, nv) on t.v = s.v
            when matched then update set v = s.nv;
            """);
        DoesNotContain("CacheBuild", trace);
        AreEqual(999, Convert.ToInt32(ReadVal(c, "select v from tgt where id = 2")));
    }
}
