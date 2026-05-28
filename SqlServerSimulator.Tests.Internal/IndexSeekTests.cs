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
/// correct under it. Both the non-aggregate and aggregate single-table
/// projectors narrow through the seek; the window projector doesn't.
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
    public void RangePredicate_Declines()
    {
        var (trace, rows) = Run(TableT, "select id from t where id > 1");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(2, rows);
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
    public void RcsiRead_WithLiveVersionChain_Declines()
    {
        // An open writer leaves a version chain on the table, so an RCSI
        // reader's visible version can diverge from the live heap row — the
        // seek declines back to the full scan, and the reader still sees the
        // pre-write committed value.
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
            using var command = reader.CreateCommand();
            command.CommandText = "select val from t where id = 2";
            using var r = command.ExecuteReader();
            var rows = new List<object?>();
            while (r.Read())
                rows.Add(r.GetValue(0));
            Contains("Scan(t)", IndexSeekDiagnostics.Sink);
            DoesNotContain("Seek(t)", IndexSeekDiagnostics.Sink);
            HasCount(1, rows);
            AreEqual(50, rows[0]);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void RepeatableReadHint_Declines()
    {
        var (trace, rows) = Run(TableT, "select val from t with (repeatableread) where id = 2");
        Contains("Scan(t)", trace);
        DoesNotContain("Seek(t)", trace);
        HasCount(1, rows);
    }
}
