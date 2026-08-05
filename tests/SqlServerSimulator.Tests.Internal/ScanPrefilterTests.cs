using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the two range-predicate access-path decisions that
/// no correctness test can see, since both are result-transparent:
/// <list type="bullet">
/// <item>the range seek's <b>span gate</b> — an interval selecting more than a
/// quarter of a table's rows abandons the seek and keeps the scan
/// (<c>RangeSpanTooWide(table)</c>), because the seek's per-address reads and
/// ordered-view walk lose to the sequential scan once the range is that wide;</item>
/// <item>the <b>scan prefilter</b> — a joined source no key can seek still has
/// its own sargable WHERE conjuncts applied to its row stream before the join
/// runs (<c>ScanPrefilter(table,n)</c>), which is the access path a range on an
/// unindexed column gets.</item>
/// </list>
/// Both read the opt-in <see cref="IndexSeekDiagnostics"/> trace, recorded at
/// the decision point, and assert the rows stay exactly what the scan produced.
/// </summary>
[TestClass]
public sealed class ScanPrefilterTests
{
    // A table wide enough to cross the span gate's row-count floor, keyed on id
    // so a range on it is seekable, with a `bucket` column no index covers.
    private const int WideRows = 4000;

    private static SimulatedDbConnection OpenWide()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, $"""
            create table wide (id int not null primary key, bucket int not null, v int null);
            declare @i int = 1;
            while @i <= {WideRows} begin
                insert wide values (@i, @i % 100, @i * 2);
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

    // Runs `query`, capturing the access-path trace and the first column of
    // every row.
    private static (List<string> Trace, List<object?> Rows) Run(SimulatedDbConnection connection, string query)
    {
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

    private static (List<string> Trace, List<object?> Rows) RunOnWide(string query)
    {
        using var connection = OpenWide();
        return Run(connection, query);
    }

    // ---- span gate: a wide interval abandons the seek, a narrow one keeps it ----

    [TestMethod]
    public void WholeTableRange_AbandonsSeek()
    {
        var (trace, rows) = RunOnWide("select count(*) from wide where id >= 1");
        Contains($"RangeSpanTooWide(wide)", trace);
        Contains("Scan(wide)", trace);
        DoesNotContain("RangeSeek(wide)", trace);
        AreEqual(WideRows, rows[0]);
    }

    [TestMethod]
    public void RangeJustOverAQuarter_AbandonsSeek()
    {
        // 1..1001 is 1001 rows of 4000 — one past the gate's quarter.
        var (trace, rows) = RunOnWide("select count(*) from wide where id between 1 and 1001");
        Contains("RangeSpanTooWide(wide)", trace);
        AreEqual(1001, rows[0]);
    }

    [TestMethod]
    public void RangeJustUnderAQuarter_StillSeeks()
    {
        var (trace, rows) = RunOnWide("select count(*) from wide where id between 1 and 1000");
        Contains("RangeSeek(wide)", trace);
        DoesNotContain("RangeSpanTooWide(wide)", trace);
        AreEqual(1000, rows[0]);
    }

    [TestMethod]
    public void NarrowRange_OnWideTable_Seeks()
    {
        // No ORDER BY: a sort on the same key column would be eliminated by the
        // ordered scan instead, which is a different access path.
        var (trace, rows) = RunOnWide("select v from wide where id between 10 and 12");
        Contains("RangeSeek(wide)", trace);
        HasCount(3, rows);
        Contains(20, rows);
        Contains(24, rows);
    }

    [TestMethod]
    public void SmallTable_WideRange_KeepsSeekingBelowTheFloor()
    {
        // Under RangeSpanGateMinRows the gate never engages, so a whole-table
        // range on a tiny table still takes the seek it always did.
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, "create table t (id int not null primary key); insert t values (1), (2), (3)");
        var (trace, rows) = Run(connection, "select id from t where id >= 1");
        Contains("RangeSeek(t)", trace);
        DoesNotContain("RangeSpanTooWide(t)", trace);
        HasCount(3, rows);
    }

    [TestMethod]
    public void WideRangeOnMutationTarget_AbandonsSeek()
    {
        // The gate lives in the address-only core the DELETE / UPDATE path
        // shares, so a wide-range mutation keeps its scan too.
        using var connection = OpenWide();
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(connection, "delete wide where id >= 1");
            Contains("RangeSpanTooWide(wide)", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }

        var (_, rows) = Run(connection, "select count(*) from wide");
        AreEqual(0, rows[0]);
    }

    // ---- scan prefilter: a join source no index can seek is still narrowed ----

    private static SimulatedDbConnection OpenJoined()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table hdr (id int not null primary key, made date not null, tag int not null);
            create table line (id int not null, qty int not null);
            create index ix_line_id on line (id);
            insert hdr values
                (1, '2020-01-01', 7), (2, '2020-02-01', 7), (3, '2020-03-01', 8), (4, '2020-04-01', 8);
            insert line values (1, 10), (1, 20), (2, 30), (3, 40), (4, 50)
            """);
        return connection;
    }

    [TestMethod]
    public void RangeOnUnindexedJoinColumn_PrefiltersTheScan()
    {
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, """
            select sum(l.qty) from hdr h join line l on l.id = h.id
            where h.made between '2020-01-15' and '2020-03-15'
            """);
        Contains("ScanPrefilter(hdr,1)", trace);
        AreEqual(70, rows[0]);
    }

    [TestMethod]
    public void TwoBoundsOnOneColumn_PushBothConjuncts()
    {
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, """
            select sum(l.qty) from hdr h join line l on l.id = h.id
            where h.made >= '2020-01-15' and h.made < '2020-03-15'
            """);
        Contains("ScanPrefilter(hdr,2)", trace);
        AreEqual(70, rows[0]);
    }

    [TestMethod]
    public void EqualityOnUnindexedJoinColumn_PrefiltersTheScan()
    {
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, "select sum(l.qty) from hdr h join line l on l.id = h.id where h.tag = 8");
        Contains("ScanPrefilter(hdr,1)", trace);
        AreEqual(90, rows[0]);
    }

    [TestMethod]
    public void PrefilterOnNonLeftmostSource_Engages()
    {
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, "select sum(l.qty) from line l join hdr h on l.id = h.id where h.tag = 7");
        Contains("ScanPrefilter(hdr,1)", trace);
        AreEqual(60, rows[0]);
    }

    [TestMethod]
    public void SeekableSource_TakesTheSeek_NotThePrefilter()
    {
        // The prefilter is the seek's fallback, so a source the seek narrowed
        // never takes it — the key positions on the predicate instead.
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, "select sum(l.qty) from hdr h join line l on l.id = h.id where h.id = 1");
        Contains("Seek(hdr)", trace);
        DoesNotContain("ScanPrefilter(hdr,1)", trace);
        AreEqual(30, rows[0]);
    }

    [TestMethod]
    public void SiblingColumnValueSide_Declines()
    {
        // `h.tag = l.qty` reads a sibling of the same FROM, which isn't readable
        // before the join runs — the shape test refuses it.
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, "select sum(l.qty) from hdr h join line l on l.id = h.id where h.tag = l.qty");
        DoesNotContain("ScanPrefilter(hdr,1)", trace);
        DoesNotContain("ScanPrefilter(line,1)", trace);
        IsNull(rows[0]);
    }

    [TestMethod]
    public void NonSargableConjuncts_Decline()
    {
        using var connection = OpenJoined();
        foreach (var where in new[] { "h.tag + 0 = 7", "h.tag is null", "h.made is not null", "not (h.tag = 7)" })
        {
            var (trace, _) = Run(connection, $"select sum(l.qty) from hdr h join line l on l.id = h.id where {where}");
            DoesNotContain("ScanPrefilter(hdr,1)", trace);
        }
    }

    [TestMethod]
    public void VariableValueSide_Pushes()
    {
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, """
            declare @from date = '2020-01-15';
            select sum(l.qty) from hdr h join line l on l.id = h.id where h.made > @from
            """);
        Contains("ScanPrefilter(hdr,1)", trace);
        AreEqual(120, rows[0]);
    }

    [TestMethod]
    public void EveryJoinKind_PrefiltersTheFilteredSide()
    {
        // The NULL-extendable side of an outer join is filtered too: the pushed
        // shapes are all NULL-rejecting on the source's own column, so a tuple
        // NULL-extended because this side lost a row is excluded by the residual
        // exactly as the matched-but-failing tuple was.
        using var connection = OpenJoined();
        foreach (var kind in new[] { "join", "left join", "right join", "full join" })
        {
            var (trace, _) = Run(connection, $"select h.id, l.qty from line l {kind} hdr h on h.id = l.id where h.tag = 8");
            Contains("ScanPrefilter(hdr,1)", trace);
        }
    }

    [TestMethod]
    public void SingleSourceQuery_NeverPrefilters()
    {
        // With one source the residual WHERE already is the scan's filter, so
        // there is nothing for a prefilter to save.
        using var connection = OpenJoined();
        var (trace, rows) = Run(connection, "select count(*) from hdr h where h.tag = 7");
        DoesNotContain("ScanPrefilter(hdr,1)", trace);
        AreEqual(2, rows[0]);
    }

    [TestMethod]
    public void PrefilteredOuter_LetsTheJoinSeekItsInner()
    {
        // The point of the pass: a large driving table cut down to a handful of
        // rows takes the join's per-outer-row seek instead of hashing the inner.
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table big (id int not null primary key, made date not null);
            create table child (id int not null, qty int not null);
            create index ix_child_id on child (id);
            declare @i int = 1;
            while @i <= 600 begin
                insert big values (@i, dateadd(day, @i, '2020-01-01'));
                insert child values (@i, @i);
                set @i += 1;
            end
            """);

        IndexSeekDiagnostics.Sink = [];
        JoinDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                select sum(c.qty) from big b join child c on c.id = b.id
                where b.made between '2020-01-02' and '2020-01-04'
                """;
            using var reader = command.ExecuteReader();
            _ = reader.Read();
            AreEqual(6, reader.GetValue(0));
            Contains("ScanPrefilter(big,1)", IndexSeekDiagnostics.Sink);
            Contains("Inner:NestedLoopIndexSeek(keys=1)", JoinDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
            JoinDiagnostics.Sink = null;
        }
    }
}
