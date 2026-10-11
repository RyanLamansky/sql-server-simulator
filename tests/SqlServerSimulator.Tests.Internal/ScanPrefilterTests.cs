using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Perf-regression guard for the access-path decisions that no correctness
/// test can see, since all are result-transparent:
/// <list type="bullet">
/// <item>the range seek's <b>span gate</b> — an interval selecting more than a
/// quarter of a table's rows abandons the seek and keeps the scan
/// (<c>RangeSpanTooWide(table)</c>), because the seek's per-address reads and
/// ordered-view walk lose to the sequential scan once the range is that wide;</item>
/// <item>the equality seek's span gate — a lone source's bucket holding more
/// than three quarters of the table gives way to the scan
/// (<c>EqualitySpanTooWide(table)</c>) where nothing but the access path
/// changes;</item>
/// <item>the <b>scan prefilter</b> — a joined source no key can seek still has
/// its own sargable WHERE conjuncts applied to its row stream before the join
/// runs (<c>ScanPrefilter(table,n)</c>), which is the access path a range on an
/// unindexed column gets, and a seeked one its conjuncts the seek didn't
/// answer (<c>SeekPrefilter(table,n)</c>);</item>
/// <item>the <b>sampled count</b> — a filtered source the reorder may drive
/// from is counted over its first rows (<c>SampledFilter(table,n)</c>).</item>
/// </list>
/// All read the opt-in <see cref="IndexSeekDiagnostics"/> trace, recorded at
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

    // ---- LIKE pushes like a comparison does ----

    private static SimulatedDbConnection OpenNamed()
    {
        var connection = OpenJoined();
        Exec(connection, """
            create table who (id int not null primary key, name nvarchar(20) null);
            insert who values (1, N'Alfred'), (2, N'Ana'), (3, N'Bob'), (4, null);
            create table pat (p nvarchar(20) not null);
            insert pat values (N'A%')
            """);
        return connection;
    }

    [TestMethod]
    public void LikeOnJoinColumn_PrefiltersTheScan()
    {
        using var connection = OpenNamed();
        foreach (var (where, expected) in new[]
        {
            ("w.name like N'A%'", 60),
            ("w.name not like N'A%'", 40),
            ("w.name like N'A!%' escape N'!'", (object?)null),
        })
        {
            var (trace, rows) = Run(connection, $"select sum(l.qty) from who w join line l on l.id = w.id where {where}");
            Contains("ScanPrefilter(who,1)", trace, where);
            AreEqual(expected, rows[0], where);
        }
    }

    [TestMethod]
    public void LikeWithSiblingPattern_Declines()
    {
        using var connection = OpenNamed();
        var (trace, rows) = Run(connection, "select count(*) from who w join pat on w.name like pat.p");
        DoesNotContain("ScanPrefilter(who,1)", trace);
        AreEqual(2, rows[0]);
    }

    // ---- a joined UPDATE / DELETE's target is prefiltered too ----

    [TestMethod]
    public void JoinedDeleteTarget_Prefiltered()
    {
        using var connection = OpenJoined();
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(connection, "delete l from line l cross apply (select top (1) h.tag from hdr h where h.id <= l.id order by h.id) a where l.qty >= 30");
            Contains("ScanPrefilter(line,1)", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }

        var (_, rows) = Run(connection, "select sum(qty) from line");
        AreEqual(30, rows[0]);
    }

    [TestMethod]
    public void JoinedUpdateTarget_Prefiltered()
    {
        using var connection = OpenJoined();
        IndexSeekDiagnostics.Sink = [];
        try
        {
            Exec(connection, "update h set tag = l.qty from hdr h join line l on l.id = h.id where h.made > '2020-02-15'");
            Contains("ScanPrefilter(hdr,1)", IndexSeekDiagnostics.Sink);
        }
        finally
        {
            IndexSeekDiagnostics.Sink = null;
        }

        var (_, rows) = Run(connection, "select sum(tag) from hdr");
        AreEqual(7 + 7 + 40 + 50, rows[0]);
    }

    // ---- equality span gate: a bucket holding most of the table scans ----

    // 4,000 rows: f and the nullable g at 1 for nine rows in ten, e for 76 in a
    // hundred and d for 74, (a, b) a composite index led by a nine-in-ten a.
    private static SimulatedDbConnection OpenFlags()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table flags (id int not null primary key, f int not null, g int null, e int not null, d int not null, a int not null, b int not null);
            create index ix_f on flags (f);
            create index ix_g on flags (g);
            create index ix_e on flags (e);
            create index ix_d on flags (d);
            create index ix_ab on flags (a, b);
            insert flags select value, iif(value % 10 = 0, 2, 1), iif(value % 10 = 0, 2, 1), iif(value % 100 < 76, 1, 2), iif(value % 100 < 74, 1, 2),
                iif(value % 10 = 0, 2, 1), value % 7
                from generate_series(1, 4000);
            create table other (id int not null primary key);
            insert other values (1), (2)
            """);
        return connection;
    }

    [TestMethod]
    public void WideEquality_GivesWayToTheScan()
    {
        using var connection = OpenFlags();
        foreach (var column in new[] { "f", "e" })
        {
            var (trace, rows) = Run(connection, $"select count(*) from flags where {column} = 1");
            Contains("EqualitySpanTooWide(flags)", trace);
            Contains("Scan(flags)", trace);
            DoesNotContain("Seek(flags)", trace);
            AreEqual(column == "f" ? 3600 : 3040, rows[0]);
        }
    }

    [TestMethod]
    public void EqualityUnderThreeQuarters_Seeks()
    {
        using var connection = OpenFlags();
        var (trace, rows) = Run(connection, "select count(*) from flags where d = 1");
        Contains("Seek(flags)", trace);
        DoesNotContain("EqualitySpanTooWide(flags)", trace);
        AreEqual(2960, rows[0]);
    }

    [TestMethod]
    [DataRow("select count(*) from flags where 10 / id > -1 and f = 1")]
    [DataRow("select count(*) from flags where b >= 0 and f = 1")]
    [DataRow("select count(*) from flags where b = 3 and f = 1")]
    [DataRow("select count(*) from flags where g = 1")]
    [DataRow("select count(*) from flags where f in (1, 3)")]
    [DataRow("select count(*) from flags where a = 1")]
    [DataRow("select count(*) from flags with (repeatableread) where f = 1")]
    [DataRow("select count(*) from flags f join other o on o.id = f.f where f.f = 1")]
    public void WideEquality_KeepsTheSeekWhereTheScanWouldShowIt(string query)
    {
        // An equality the WHERE doesn't open with, on a nullable column, among
        // several probes, on a prefix of a wider index (which reads by the
        // rest of its key), under a lock held to the transaction's end, or on
        // a joined source each keep the seek.
        using var connection = OpenFlags();
        var (trace, _) = Run(connection, query);
        Contains("Seek(flags)", trace);
        DoesNotContain("EqualitySpanTooWide(flags)", trace);
    }

    // ---- a seeked source is filtered by its other conjuncts ----

    // 2,000 orders over ten customers, 200 each, dated over a hundred days, and
    // three of them noted `rare`; 6,000 lines, three per order.
    private static SimulatedDbConnection OpenOrders()
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table o (id int not null primary key, cust int not null, d date not null, v int not null, note nvarchar(10) not null);
            create index ix_cust on o (cust);
            insert o select value, value % 10, dateadd(day, value % 100, '2024-01-01'), 1,
                iif(value in (1503, 977, 31), N'rare', N'n') from generate_series(1, 2000);
            create table l (id int not null primary key, oid int not null, q int not null);
            create index ix_oid on l (oid);
            insert l select value, (value * 7) % 2000 + 1, value % 13 from generate_series(1, 6000)
            """);
        return connection;
    }

    [TestMethod]
    public void SeekedSource_FilteredByItsOtherConjuncts()
    {
        using var connection = OpenOrders();
        var (trace, rows) = Run(connection, "select count(*) from o join l on l.oid = o.id where o.cust = 1 and o.d between '2024-01-01' and '2024-01-21'");
        Contains("Seek(o)", trace);
        Contains("SeekPrefilter(o,1)", trace);
        AreEqual(120, rows[0]);
    }

    [TestMethod]
    [DataRow("where o.cust = 1")]
    [DataRow("where o.cust = 1 and 10 / o.v > 0 and o.d between '2024-01-01' and '2024-01-21'")]
    [DataRow("where o.id < 100 and o.d between '2024-01-01' and '2024-01-21'")]
    public void SeekedSource_NotFiltered(string where)
    {
        // Nothing past the keyed equality, a conjunct after one that isn't
        // sargable, and a seek leaving the join few rows.
        using var connection = OpenOrders();
        var (trace, _) = Run(connection, $"select count(*) from o join l on l.oid = o.id {where}");
        Contains("Seek(o)", trace);
        DoesNotContain("SeekPrefilter(o,1)", trace);
    }

    // ---- a filtered source is counted by a sample ----

    private static (List<string> Trace, List<string> Joins) RunJoined(SimulatedDbConnection connection, string query)
    {
        JoinDiagnostics.Sink = [];
        try
        {
            var (trace, _) = Run(connection, query);
            return (trace, JoinDiagnostics.Sink);
        }
        finally
        {
            JoinDiagnostics.Sink = null;
        }
    }

    [TestMethod]
    public void FilteredSourceCutToAFew_DrivesTheChain()
    {
        using var connection = OpenOrders();
        var (trace, joins) = RunJoined(connection, "select sum(l.q) from l join o on l.oid = o.id where o.note = N'rare'");
        Contains("ScanPrefilter(o,1)", trace);
        Contains("SampledFilter(o,3)", trace);
        Contains("Reorder(1,0)", joins);
        Contains("Inner:NestedLoopIndexSeek(keys=1)", joins);
    }

    [TestMethod]
    public void SeekFilteredSourceCutToAFew_DrivesTheChain()
    {
        using var connection = OpenOrders();
        var (trace, joins) = RunJoined(connection, "select sum(l.q) from l join o on l.oid = o.id where o.cust = 1 and o.d = '2024-01-02'");
        Contains("SeekPrefilter(o,1)", trace);
        Contains("SampledFilter(o,20)", trace);
        Contains("Reorder(1,0)", joins);
    }

    [TestMethod]
    public void FilteredSourceWithNoPartnerToSeek_SamplesNothing()
    {
        // Driving from o would hash over it rather than seek l per row.
        using var connection = OpenOrders();
        Exec(connection, "drop index ix_oid on l");
        var (trace, joins) = RunJoined(connection, "select sum(l.q) from l join o on l.oid = o.id where o.note = N'rare'");
        Contains("ScanPrefilter(o,1)", trace);
        Assert.IsFalse(trace.Exists(entry => entry.StartsWith("SampledFilter(", StringComparison.Ordinal)));
        Assert.IsFalse(joins.Exists(entry => entry.StartsWith("Reorder(", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void FilteredSourcePastTheSample_KeepsTheWrittenOrder()
    {
        using var connection = OpenOrders();
        var (trace, joins) = RunJoined(connection, "select sum(l.q) from l join o on l.oid = o.id where o.note = N'n'");
        Contains("SampledFilter(o,-1)", trace);
        DoesNotContain("Reorder(1,0)", joins);
    }

    [TestMethod]
    public void SmallWrittenDriver_SamplesNothing()
    {
        // A written driver of 128 rows or fewer seeks its partners per row
        // whatever the filter keeps, so counting the filter buys nothing.
        using var connection = OpenOrders();
        var (trace, _) = RunJoined(connection, "select sum(l.q) from l join o on l.oid = o.id where l.id < 50 and o.note = N'rare'");
        Contains("ScanPrefilter(o,1)", trace);
        Assert.IsFalse(trace.Exists(entry => entry.StartsWith("SampledFilter(", StringComparison.Ordinal)));
    }

    [TestMethod]
    [DataRow("x", "SampledFilter(b,5)", 584)]
    [DataRow("n", "SampledFilter(b,-1)", 39316)]
    public void SampledSourceTheChainDoesNotDriveFrom_IsReadPerRow(string note, string sampled, int count)
    {
        // b is sampled for the seek into c its key offers, and the subquery
        // in the ON keeps the written order, so b stays the inner of a nested
        // loop that reads it once per row of a.
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Exec(connection, """
            create table a (id int not null primary key, k int not null);
            insert a select value, value % 300 from generate_series(1, 1000);
            create table b (id int not null primary key, note nvarchar(10) not null);
            insert b select value, iif(value % 60 = 7, N'x', N'n') from generate_series(1, 300);
            create table c (id int not null primary key);
            insert c select value from generate_series(1, 300)
            """);
        var (trace, joins) = RunJoined(connection, $"select count(*) from a join b on a.k < b.id join c on c.id = b.id and (select 1) = 1 where a.id <= 200 and b.note = N'{note}'");
        Contains(sampled, trace);
        Assert.IsFalse(joins.Exists(entry => entry.StartsWith("Reorder(", StringComparison.Ordinal)));
        AreEqual(count, Run(connection, $"select count(*) from a join b on a.k < b.id join c on c.id = b.id and (select 1) = 1 where a.id <= 200 and b.note = N'{note}'").Rows[0]);
    }
}
