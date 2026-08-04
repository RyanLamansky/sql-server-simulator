using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The aggregate executor accumulates straight off the row enumeration when
/// there is one grouping set, and serves a small <c>TOP (n)</c> / <c>FETCH</c>
/// over the grouped stream from a bounded heap instead of sorting every group.
/// Neither changes an answer, so these pin the rows against real's own values
/// and — for the heap — against the full sort it replaces, which is the same
/// query with no row limit at all, sliced here.
/// </summary>
[TestClass]
public sealed class GroupedAggregateStreamingTests
{
    /// <summary>
    /// 120 rows: <c>bucket</c> cycles 0-11 (10 rows each, so a count-ordered
    /// group list is one big tie), <c>id</c> sums differ per bucket (so a
    /// sum-ordered one is total), <c>amount</c> is a scaled decimal, <c>d</c>
    /// spans four months of unequal length, and <c>tag</c> carries NULLs. Every
    /// expected value below comes from running the same script against
    /// SQL Server 2025.
    /// </summary>
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table g (id int not null primary key, bucket int not null,
                            amount decimal(9,2) not null, d date not null, tag varchar(10) null);
            declare @i int = 1;
            while @i <= 120
            begin
                insert g values (@i, @i % 12, cast(@i as decimal(9,2)) / 4, dateadd(day, @i, '2020-01-01'),
                                 case when @i % 7 = 0 then null else 't' + cast(@i % 5 as varchar(2)) end);
                set @i = @i + 1;
            end
            """);
        return simulation;
    }

    /// <summary>Each row's cells joined by <c>|</c>, so a whole result set compares as a list of strings.</summary>
    private static List<string> Rows(Simulation simulation, string sql)
    {
        var rows = new List<string>();
        using var reader = simulation.ExecuteReader(sql);
        while (reader.Read())
        {
            var cells = new string[reader.FieldCount];
            for (var i = 0; i < cells.Length; i++)
            {
                cells[i] = reader.IsDBNull(i)
                    ? "NULL"
                    : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            rows.Add(string.Join('|', cells));
        }

        return rows;
    }

    // ---- the bounded heap over the grouped stream ----

    /// <summary>
    /// The heap's rows are the full sort's first n, group for group. The
    /// control is the identical query with no row limit — the whole grouped
    /// stream sorted, which is what the heap replaces — sliced to n here rather
    /// than through <c>OFFSET</c> / <c>FETCH</c>, either of which would reach
    /// the heap itself. The sort key is total, so the two agree row for row
    /// whatever either does with ties.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(40)]
    public void GroupedTopN_MatchesTheFullSort_OnATotalOrder(int n)
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Rows(simulation, "select bucket, sum(id) as s from g group by bucket order by s desc").Take(n).ToList(),
            Rows(simulation, $"select top ({n}) bucket, sum(id) as s from g group by bucket order by s desc"));
    }

    /// <summary>
    /// The same with the leading ORDER BY key fully tied — every bucket holds
    /// 10 rows — and a second key settling it. A tie inside the window is what
    /// the heap's admit-on-strictly-better rule has to get right.
    /// </summary>
    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    [DataRow(7)]
    [DataRow(12)]
    public void GroupedTopN_MatchesTheFullSort_AcrossATiedKey(int n)
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            Rows(simulation, "select bucket, count(*) as c from g group by bucket order by c desc, bucket").Take(n).ToList(),
            Rows(simulation, $"select top ({n}) bucket, count(*) as c from g group by bucket order by c desc, bucket"));
    }

    /// <summary>
    /// A tie group straddling the cap with nothing to settle it: the <em>keys</em>
    /// are determined even though which groups carry them is not — neither the
    /// heap nor real's TopN Sort is stable — so this pins the key column and the
    /// row count, the way the row-level top-N tests do.
    /// </summary>
    [TestMethod]
    public void GroupedTopN_TieStraddlingTheCap_ReturnsTheRightKeys()
        => CollectionAssert.AreEqual(
            new List<string> { "10", "10", "10" },
            Rows(Seeded(), "select top (3) count(*) as n from g group by bucket order by n"));

    /// <summary>Real's own values for the heap-served shapes.</summary>
    [TestMethod]
    public void GroupedTopN_MatchesReal()
    {
        var simulation = Seeded();
        CollectionAssert.AreEqual(
            new List<string> { "0|660", "11|650", "10|640" },
            Rows(simulation, "select top (3) bucket, sum(id) as s from g group by bucket order by s desc, bucket"));
        CollectionAssert.AreEqual(
            new List<string> { "0|10", "1|10", "2|10", "3|10" },
            Rows(simulation, "select top (4) bucket, count(*) as n from g group by bucket order by n desc, bucket"));
        CollectionAssert.AreEqual(
            new List<string> { "6|600", "7|610", "8|620" },
            Rows(simulation, "select top (3) bucket, sum(id) as s from g group by bucket having sum(id) > 590 order by s"));
    }

    /// <summary>
    /// The shapes that need the whole ordered set behind the cap and so decline
    /// the heap: <c>WITH TIES</c> reads past the boundary, <c>PERCENT</c> reads
    /// the total group count, an <c>OFFSET</c> skips into the middle of the
    /// order, and <c>DISTINCT</c> dedupes before the cap means anything. Real's
    /// values.
    /// </summary>
    [TestMethod]
    public void GroupedRowLimits_DecliningShapesStillAnswer()
    {
        var simulation = Seeded();
        AreEqual(12, simulation.ExecuteScalar("select count(*) from (select top (3) with ties count(*) as n from g group by bucket order by n) x"));
        AreEqual(2, simulation.ExecuteScalar("select count(*) from (select top (10) percent bucket from g group by bucket order by bucket) x"));
        CollectionAssert.AreEqual(
            new List<string> { "10|640", "9|630" },
            Rows(simulation, "select bucket, sum(id) as s from g group by bucket order by s desc offset 2 rows fetch next 2 rows only"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from (select distinct top (5) count(*) as n from g group by bucket order by n) x"));
    }

    /// <summary>
    /// A window over the grouped result projects through a second pass and
    /// still reaches the heap, and the window itself still spans every group —
    /// the grand total is the whole result's, not the window's.
    /// </summary>
    [TestMethod]
    public void GroupedTopN_WithAWindowOverTheGroups_SeesEveryGroup()
        => CollectionAssert.AreEqual(
            new List<string> { "0|660|7260", "11|650|7260", "10|640|7260" },
            Rows(Seeded(), "select top (3) bucket, sum(id) as s, sum(sum(id)) over () as grand from g group by bucket order by s desc, bucket"));

    // ---- streaming versus the buffered several-grouping-set path ----

    /// <summary>
    /// <c>ROLLUP</c> partitions the same rows twice, so it keeps the buffered
    /// path — the subtotal row and the leaf rows both come out of one read.
    /// Real's values.
    /// </summary>
    [TestMethod]
    public void Rollup_StillPartitionsTheSameRowsSeveralWays()
        => CollectionAssert.AreEqual(
            new List<string> { "NULL|120|1", "0|10|0", "1|10|0", "2|10|0" },
            Rows(Seeded(), "select top (4) bucket, count(*) as n, grouping(bucket) as g from g group by rollup(bucket) order by n desc, g desc, bucket"));

    /// <summary>
    /// A grouping <em>expression</em> keeps a representative row per group, so
    /// a projection reaching the column underneath it resolves. The streaming
    /// loop is handed the join driver's own tuple, which the next row rewrites,
    /// so this is what proves the representative is snapshotted rather than
    /// aliased — an aliased one would report the last row of the whole scan for
    /// every group. Real's values; the four months are of unequal length, so the
    /// counts distinguish them.
    /// </summary>
    [TestMethod]
    public void GroupingExpression_ResolvesTheColumnUnderneathIt()
        => CollectionAssert.AreEqual(
            new List<string> { "3|2020-03-01|31", "1|2020-01-02|30", "4|2020-04-01|30", "2|2020-02-01|29" },
            Rows(Seeded(), "select month(d) as m, convert(varchar(10), min(d), 23) as firstDay, count(*) as n from g group by month(d) order by n desc, m"));

    /// <summary>The implicit whole-input group exists over no rows at all.</summary>
    [TestMethod]
    public void UngroupedAggregate_OverAnEmptyFilter_StillReturnsOneRow()
        => CollectionAssert.AreEqual(
            new List<string> { "0|NULL|NULL" },
            Rows(Seeded(), "select count(*) as n, sum(id) as s, max(d) as m from g where id < 0"));

    /// <summary>
    /// An aggregate operand calling a scalar UDF that reads the same table runs
    /// that read while the outer enumeration is still open on the streaming
    /// path. Real's values. (A subquery written directly in the operand is
    /// Msg 130 on real, so a UDF is the reachable spelling of the same shape.)
    /// </summary>
    [TestMethod]
    public void UdfReadingTheSameTableInsideAnAggregateOperand_RunsMidScan()
    {
        var simulation = Seeded();
        _ = simulation.ExecuteNonQuery("""
            create function dbo.peer(@id int) returns int as
            begin return (select count(*) from g where id <= @id); end
            """);
        CollectionAssert.AreEqual(
            new List<string> { "0|10|660", "1|10|550" },
            Rows(simulation, "select top (2) bucket, count(*) as n, sum(dbo.peer(id)) as walk from g group by bucket order by bucket"));
    }

    /// <summary>
    /// Real pipelines its Filter into its aggregate, so an aggregate operand
    /// raising on an early row preempts a WHERE that would have raised on a
    /// later one: over a table whose first row zeroes the divisor and whose last
    /// row's text isn't numeric, real reports Msg 8134 rather than the
    /// conversion error (probe-confirmed against SQL Server 2025). Streaming is
    /// what makes the simulator agree — buffering every WHERE-passing row first
    /// reported the conversion error.
    /// </summary>
    [TestMethod]
    public void AggregateOperandError_PreemptsALaterRowsWhereError()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table e (id int not null primary key, a int not null, s varchar(10) not null);
            insert e values (1, 0, '1'), (2, 5, '1'), (3, 5, '1'), (4, 5, '1'), (5, 5, 'xyz')
            """);
        var error = Throws<System.Data.Common.DbException>(
            () => simulation.ExecuteScalar("select sum(1 / a) from e where cast(s as int) > 0"));
        AreEqual("8134", error.Data["HelpLink.EvtID"]);
    }

    // ---- the decimal accumulation the streaming loop feeds ----

    /// <summary>
    /// <c>SUM</c> over a scaled decimal keeps the operand's scale and value.
    /// The aggregator takes its no-coercion path here (the result carries the
    /// operand's scale and a wider integer part), so this pins that path's
    /// equivalence against real's totals.
    /// </summary>
    [TestMethod]
    public void SumOfADecimalColumn_KeepsScaleAndValue()
        => CollectionAssert.AreEqual(
            new List<string>
            {
                "0|165.00", "1|137.50", "2|140.00", "3|142.50", "4|145.00", "5|147.50",
                "6|150.00", "7|152.50", "8|155.00", "9|157.50", "10|160.00", "11|162.50",
            },
            Rows(Seeded(), "select bucket, sum(amount) as total from g group by bucket order by bucket"));

    /// <summary>
    /// A decimal target with no room for the integer part still overflows —
    /// Msg 8115, as real raises for the same expression.
    /// </summary>
    [TestMethod]
    public void SumOfADecimalTooWideForItsTarget_StillOverflows()
    {
        var error = Throws<System.Data.Common.DbException>(
            () => Seeded().ExecuteScalar("select sum(cast(amount as decimal(38,37))) from g"));
        AreEqual("8115", error.Data["HelpLink.EvtID"]);
    }

    /// <summary>
    /// Narrowing the operand first rounds each value, not the total, and the
    /// aggregate's own scale follows the operand it was given. Real's values.
    /// </summary>
    [TestMethod]
    public void SumAcrossScales_TakesTheOperandsOwnScale()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table m (v decimal(9,4) not null);
            insert m values (1.0001), (2.0002), (3.0003)
            """);
        CollectionAssert.AreEqual(
            new List<string> { "6.0006|6.00|6" },
            Rows(simulation, "select sum(v) as a, sum(cast(v as decimal(9,2))) as b, sum(cast(v as decimal(9,0))) as c from m"));
    }

    /// <summary>
    /// <c>COUNT(DISTINCT)</c> per group dedupes within the group and skips
    /// NULLs. Real's values.
    /// </summary>
    [TestMethod]
    public void CountDistinctPerGroup_DedupesWithinTheGroup()
        => CollectionAssert.AreEqual(
            new List<string> { "0|5|10", "1|5|10", "2|5|10" },
            Rows(Seeded(), "select top (3) bucket, count(distinct tag) as d, count(*) as n from g group by bucket order by d desc, bucket"));
}
