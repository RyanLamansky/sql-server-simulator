using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A joined source a seek narrowed, further filtered by the WHERE's other
/// conjuncts on it, and a filtered source the FROM clause doesn't name first,
/// which drives the join once the filter leaves it a few rows: the rows are
/// the WHERE's, a conjunct raises where it did, and a join driven from the
/// filtered source returns its rows in that source's order. Every expectation
/// probed 2026-10-11 against SQL Server 2025. The strategies themselves are
/// asserted in <c>SqlServerSimulator.Tests.Internal</c>'s
/// <c>ScanPrefilterTests</c>.
/// </summary>
[TestClass]
public sealed class FilteredJoinSourceTests
{
    /// <summary>
    /// 2,000 orders over ten customers, 200 each, dated over a hundred days,
    /// the one with <c>v = 0</c> (order 41, customer 1) dated outside the
    /// first ten; three orders noted <c>rare</c>; 6,000 lines, three per order.
    /// </summary>
    private static Simulation Orders()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table o (id int not null primary key, cust int not null, d date not null, v int not null, note nvarchar(10) not null);
            create index ix_cust on o (cust);
            insert o select value, value % 10, dateadd(day, value % 100, '2024-01-01'), case when value = 41 then 0 else 1 end,
                case when value in (1503, 977, 31) then N'rare' else N'n' end from generate_series(1, 2000);
            create table l (id int not null primary key, oid int not null, q int not null);
            create index ix_oid on l (oid);
            insert l select value, (value * 7) % 2000 + 1, value % 13 from generate_series(1, 6000)
            """);
        return simulation;
    }

    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join(":", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetValue(i)}")));
        return string.Join(",", rows);
    }

    [TestMethod]
    public void SeekedSource_ConjunctAfterTheFilter_RaisesOnNoRowTheFilterRejects() =>
        AreEqual("60:363", Rows(Orders(), "select count(*), sum(l.q) from o join l on l.oid = o.id where o.cust = 1 and o.d between '2024-01-01' and '2024-01-10' and 10 / o.v > 0"));

    [TestMethod]
    public void SeekedSource_FilteredRowsAreTheWheres()
    {
        var simulation = Orders();
        const string Where = "where o.cust = 1 and o.d between '2024-01-01' and '2024-01-21'";
        var joined = Rows(simulation, $"select o.id, l.id from o join l on l.oid = o.id {Where} order by o.id, l.id");
        HasCount(120, joined.Split(','));
        StartsWith("1:2000,1:4000,1:6000,11:1430,11:3430,11:5430,101:300,", joined);
        EndsWith("1911:1130,1911:3130,1911:5130", joined);
        AreEqual(120, simulation.ExecuteScalar($"select count(*) from l left join o on l.oid = o.id {Where}"));
    }

    [TestMethod]
    public void SeekedSource_ConjunctThatRaises_StillRaises() =>
        Orders().AssertSqlError("declare @z int = 0; select count(*) from o join l on l.oid = o.id where o.cust = 1 and o.v > 10 / @z", 8134);

    [TestMethod]
    public void FilteredSourceNamedSecond_DrivesTheJoinInItsOrder() =>
        AreEqual("290:31,2290:31,4290:31,1568:977,3568:977,5568:977,786:1503,2786:1503,4786:1503",
            Rows(Orders(), "select l.id, o.id from l join o on l.oid = o.id where o.note = N'rare'"));

    [TestMethod]
    [DataRow("", "10:2991,1007:1994,2004:997")]
    [DataRow("create index ix_ord on line (ord_id)", "2004:997,1007:1994,10:2991")]
    public void FilteredSourceNamedSecond_DrivesOnlyWhereItSeeksItsPartner(string index, string expected)
    {
        // Real drives from the filtered orders only where it can seek their
        // lines by an index; without one it hashes over the orders and reads
        // the lines in their own order.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"""
            create table ord (id int primary key, cust int not null, note nvarchar(20) not null, d date not null, v int not null);
            create table line (id int primary key, ord_id int not null, qty int not null);
            create index ix_cust on ord (cust);
            {index};
            insert ord select value, value % 7, iif(value % 997 = 0, N'rare', N'common'), dateadd(day, value % 400, '2024-01-01'), value % 13
                from generate_series(1, 3000);
            insert line select value, 3001 - value, value % 5 from generate_series(1, 3000)
            """);
        AreEqual(expected, Rows(simulation, "select l.id, o.id from line l join ord o on o.id = l.ord_id where o.note = N'rare'"));
    }

    [TestMethod]
    public void FilteredSourceKeepingMostRows_AnswersTheSame() =>
        AreEqual("5991:35950", Rows(Orders(), "select count(*), sum(l.q) from l join o on l.oid = o.id where o.note = N'n'"));

    [TestMethod]
    [DataRow("n")]
    [DataRow("x")]
    public void FilteredSourceReadPerRow_AnswersAsUnfiltered(string note)
    {
        // A sampled source the chain doesn't drive from is read again for
        // each row of a nested loop: b is sampled for the seek into c its key
        // offers, a filter keeping most rows runs past its sample, and the
        // subquery in the ON keeps the chain in written order whatever the
        // filter keeps. The non-sargable spelling reads b whole.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int not null primary key, k int not null);
            insert a select value, value % 300 from generate_series(1, 1000);
            create table b (id int not null primary key, note nvarchar(10) not null);
            insert b select value, iif(value % 60 = 7, N'x', N'n') from generate_series(1, 300);
            create table c (id int not null primary key);
            insert c select value from generate_series(1, 300)
            """);
        const string Query = "select count(*), sum(a.k + b.id) from a join b on a.k < b.id join c on c.id = b.id and (select 1) = 1 where a.id <= 200 and {0}";
        AreEqual(
            Rows(simulation, string.Format(System.Globalization.CultureInfo.InvariantCulture, Query, $"b.note + N'' = N'{note}'")),
            Rows(simulation, string.Format(System.Globalization.CultureInfo.InvariantCulture, Query, $"b.note = N'{note}'")));
    }
}
