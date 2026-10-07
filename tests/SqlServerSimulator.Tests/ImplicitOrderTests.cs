using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The row order a query without ORDER BY returns where real's plan sorts on its
/// own. Every expectation is SQL Server 2025's own output for the same script
/// (probed 2026-09-29); ties real's unstable sort leaves in an order of its own
/// are kept out of the fixtures.
/// </summary>
[TestClass]
public sealed class ImplicitOrderTests
{
    private const string Setup = """
        create table h (id int not null, g int not null, x int not null, s varchar(10) not null);
        insert h values (5,2,50,'e'),(3,1,30,'c'),(8,3,80,'h'),(1,2,10,'a'),(9,1,90,'i'),(2,3,20,'b'),(7,2,70,'g'),(4,1,40,'d'),(6,3,60,'f');
        create table c (id int not null primary key, g int not null, x int not null, s varchar(10) not null);
        insert c select * from h;
        create table ci (g int not null, x int not null, s varchar(10) not null, primary key (g, x));
        insert ci select g, x, s from h;
        create table h2 (id int not null, g int not null, y int not null);
        insert h2 values (4,1,400),(2,3,200),(9,1,900),(1,2,100),(7,2,700);
        create table c2 (id int not null primary key, g int not null, y int not null);
        insert c2 select * from h2;
        create table gs (a int, b int, c int, x int);
        insert gs values (2,1,1,5),(1,2,2,6),(2,2,1,7),(1,1,2,8),(3,1,1,9),(1,1,1,1),(3,2,2,2);
        """;

    private static string Rows(string query)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        using var reader = simulation.ExecuteReader(query);
        var rows = new List<string>();
        foreach (var record in reader.EnumerateRecords())
        {
            var values = new object[record.FieldCount];
            _ = record.GetValues(values);
            rows.Add(string.Join('|', values.Select(static v => v is DBNull ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join(" / ", rows);
    }

    [TestMethod]
    [DataRow("select id, sum(x) over (order by id) from h", "1|10 / 2|30 / 3|60 / 4|100 / 5|150 / 6|210 / 7|280 / 8|360 / 9|450")]
    [DataRow("select id, g, row_number() over (partition by g order by x desc) from h", "9|1|1 / 4|1|2 / 3|1|3 / 7|2|1 / 5|2|2 / 1|2|3 / 8|3|1 / 6|3|2 / 2|3|3")]
    [DataRow("select id, sum(x) over (order by id desc) from c", "9|90 / 8|170 / 7|240 / 6|300 / 5|350 / 4|390 / 3|420 / 2|440 / 1|450")]
    [DataRow("select id, count(*) over () from h", "5|9 / 3|9 / 8|9 / 1|9 / 9|9 / 2|9 / 7|9 / 4|9 / 6|9")]
    [DataRow("select id, row_number() over (order by (select null)) from h", "5|1 / 3|2 / 8|3 / 1|4 / 9|5 / 2|6 / 7|7 / 4|8 / 6|9")]
    [DataRow("select id, row_number() over (order by x), rank() over (order by s desc) from h", "9|9|1 / 8|8|2 / 7|7|3 / 6|6|4 / 5|5|5 / 4|4|6 / 3|3|7 / 2|2|8 / 1|1|9")]
    [DataRow("select id, rank() over (order by s desc), row_number() over (order by x) from h", "1|9|1 / 2|8|2 / 3|7|3 / 4|6|4 / 5|5|5 / 6|4|6 / 7|3|7 / 8|2|8 / 9|1|9")]
    [DataRow("select id, row_number() over (order by x), rank() over (order by s desc), sum(x) over (order by x) from h", "9|9|1|450 / 8|8|2|360 / 7|7|3|280 / 6|6|4|210 / 5|5|5|150 / 4|4|6|100 / 3|3|7|60 / 2|2|8|30 / 1|1|9|10")]
    [DataRow("select id, g, count(*) over (partition by g), row_number() over (partition by g order by id) from h", "3|1|3|1 / 4|1|3|2 / 9|1|3|3 / 1|2|3|1 / 5|2|3|2 / 7|2|3|3 / 2|3|3|1 / 6|3|3|2 / 8|3|3|3")]
    [DataRow("select id, g, x, row_number() over (partition by x, g order by id) from h", "1|2|10|1 / 2|3|20|1 / 3|1|30|1 / 4|1|40|1 / 5|2|50|1 / 6|3|60|1 / 7|2|70|1 / 8|3|80|1 / 9|1|90|1")]
    [DataRow("select g, sum(x), row_number() over (order by sum(x) desc) from h group by g", "1|160|1 / 3|160|2 / 2|130|3")]
    [DataRow("select id, g, x from (select id, g, x, row_number() over (partition by g order by x desc) rn from h) d where rn = 1", "9|1|90 / 7|2|70 / 8|3|80")]
    public void Window_RowsFollowTheLastSort(string query, string expected) => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select g, sum(x) from h group by g", "1|160 / 2|130 / 3|160")]
    [DataRow("select g, x % 20, count(*) from h group by g, x % 20", "1|0|1 / 3|0|3 / 1|10|2 / 2|10|3")]
    [DataRow("select x % 20, g, count(*) from h group by x % 20, g", "0|1|1 / 10|1|2 / 10|2|3 / 0|3|3")]
    [DataRow("select g, s, count(*) from h group by g, s", "2|a|1 / 3|b|1 / 1|c|1 / 1|d|1 / 2|e|1 / 3|f|1 / 2|g|1 / 3|h|1 / 1|i|1")]
    [DataRow("select g, x % 20, s, count(*) from h group by g, x % 20, s", "1|0|d|1 / 1|10|c|1 / 1|10|i|1 / 2|10|a|1 / 2|10|e|1 / 2|10|g|1 / 3|0|b|1 / 3|0|f|1 / 3|0|h|1")]
    [DataRow("select g, s from h group by g, s", "1|c / 1|d / 1|i / 2|a / 2|e / 2|g / 3|b / 3|f / 3|h")]
    [DataRow("select x, g, count(*) from ci group by x, g", "30|1|1 / 40|1|1 / 90|1|1 / 10|2|1 / 50|2|1 / 70|2|1 / 20|3|1 / 60|3|1 / 80|3|1")]
    [DataRow("select id, g, count(*) from c group by id, g", "1|2|1 / 2|3|1 / 3|1|1 / 4|1|1 / 5|2|1 / 6|3|1 / 7|2|1 / 8|3|1 / 9|1|1")]
    [DataRow("select top 2 g, count(*) from h group by g", "1|3 / 2|3")]
    [DataRow("select g, x % 20, count(*) from h group by rollup(g, x % 20)", "1|0|1 / 1|10|2 / 1|NULL|3 / 2|10|3 / 2|NULL|3 / 3|0|3 / 3|NULL|3 / NULL|NULL|9")]
    [DataRow("select g, x % 20, count(*) from h group by grouping sets ((g, x % 20), (g), ())", "1|0|1 / 1|10|2 / 1|NULL|3 / 2|10|3 / 2|NULL|3 / 3|0|3 / 3|NULL|3 / NULL|NULL|9")]
    public void GroupBy_GroupsFollowTheStreamAggregateSort(string query, string expected) => AreEqual(expected, Rows(query));

    /// <summary>
    /// Several grouping sets run as a concatenation of rollup chains, built from
    /// the sets' column masks highest first and emitted in the order they
    /// started (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select a, b, count(*) from gs group by cube(a, b)", "1|1|2 / 2|1|1 / 3|1|1 / NULL|1|4 / 1|2|1 / 2|2|1 / 3|2|1 / NULL|2|3 / NULL|NULL|7 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2")]
    [DataRow("select a, b, c, count(*) from gs group by cube(a, b, c)", "1|1|1|1 / 2|1|1|1 / 3|1|1|1 / NULL|1|1|3 / 2|2|1|1 / NULL|2|1|1 / NULL|NULL|1|4 / 1|1|2|1 / NULL|1|2|1 / 1|2|2|1 / 3|2|2|1 / NULL|2|2|2 / NULL|NULL|2|3 / NULL|NULL|NULL|7 / 1|NULL|1|1 / 1|NULL|2|2 / 1|NULL|NULL|3 / 2|NULL|1|2 / 2|NULL|NULL|2 / 3|NULL|1|1 / 3|NULL|2|1 / 3|NULL|NULL|2 / 1|1|NULL|2 / 2|1|NULL|1 / 3|1|NULL|1 / NULL|1|NULL|4 / 1|2|NULL|1 / 2|2|NULL|1 / 3|2|NULL|1 / NULL|2|NULL|3")]
    [DataRow("select a, b, count(*) from gs group by grouping sets ((a), (b))", "NULL|1|4 / NULL|2|3 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2")]
    [DataRow("select a, b, count(*) from gs group by grouping sets ((b), (a))", "1|NULL|3 / 2|NULL|2 / 3|NULL|2 / NULL|1|4 / NULL|2|3")]
    [DataRow("select a, b, count(*) from gs group by grouping sets ((a), (b), ())", "NULL|1|4 / NULL|2|3 / NULL|NULL|7 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2")]
    [DataRow("select a, b, c, count(*) from gs group by grouping sets ((a, b), (c, a))", "1|NULL|1|1 / 2|NULL|1|2 / 3|NULL|1|1 / 1|NULL|2|2 / 3|NULL|2|1 / 1|1|NULL|2 / 2|1|NULL|1 / 3|1|NULL|1 / 1|2|NULL|1 / 2|2|NULL|1 / 3|2|NULL|1")]
    [DataRow("select a, b, c, count(*) from gs group by grouping sets ((b, a, c), (a, b))", "1|1|1|1 / 1|1|2|1 / 1|1|NULL|2 / 2|1|1|1 / 2|1|NULL|1 / 3|1|1|1 / 3|1|NULL|1 / 1|2|2|1 / 1|2|NULL|1 / 2|2|1|1 / 2|2|NULL|1 / 3|2|2|1 / 3|2|NULL|1")]
    [DataRow("select a, b, count(*) from gs group by grouping sets ((a), (b), (a))", "NULL|1|4 / NULL|2|3 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2")]
    [DataRow("select a, b, c, count(*) from gs group by a, cube(b, c)", "1|1|1|1 / 1|NULL|1|1 / 1|1|2|1 / 1|2|2|1 / 1|NULL|2|2 / 1|NULL|NULL|3 / 2|1|1|1 / 2|2|1|1 / 2|NULL|1|2 / 2|NULL|NULL|2 / 3|1|1|1 / 3|NULL|1|1 / 3|2|2|1 / 3|NULL|2|1 / 3|NULL|NULL|2 / 1|1|NULL|2 / 2|1|NULL|1 / 3|1|NULL|1 / 1|2|NULL|1 / 2|2|NULL|1 / 3|2|NULL|1")]
    [DataRow("select a, b, count(*) from gs group by rollup(a), rollup(b)", "1|1|2 / 2|1|1 / 3|1|1 / NULL|1|4 / 1|2|1 / 2|2|1 / 3|2|1 / NULL|2|3 / NULL|NULL|7 / 1|NULL|3 / 2|NULL|2 / 3|NULL|2")]
    [DataRow("select a, b from gs group by cube(a, b)", "1|1 / 2|1 / 3|1 / NULL|1 / 1|2 / 2|2 / 3|2 / NULL|2 / NULL|NULL / 1|NULL / 2|NULL / 3|NULL")]
    [DataRow("select a, b, count(*) from gs group by grouping sets ((a, b), ())", "1|1|2 / 1|2|1 / 2|1|1 / 2|2|1 / 3|1|1 / 3|2|1 / NULL|NULL|7")]
    [DataRow("select a, b, count(*), row_number() over (order by (select null)) from gs group by cube(a, b)", "1|1|2|1 / 2|1|1|2 / 3|1|1|3 / NULL|1|4|4 / 1|2|1|5 / 2|2|1|6 / 3|2|1|7 / NULL|2|3|8 / NULL|NULL|7|9 / 1|NULL|3|10 / 2|NULL|2|11 / 3|NULL|2|12")]
    public void GroupingSets_RollupChainsInMaskOrder(string query, string expected) => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select distinct g from h", "1 / 2 / 3")]
    [DataRow("select distinct x, g from h", "10|2 / 20|3 / 30|1 / 40|1 / 50|2 / 60|3 / 70|2 / 80|3 / 90|1")]
    [DataRow("select distinct x, g from ci", "30|1 / 40|1 / 90|1 / 10|2 / 50|2 / 70|2 / 20|3 / 60|3 / 80|3")]
    [DataRow("select distinct top 2 g from h", "1 / 2")]
    public void Distinct_RowsFollowTheDistinctSort(string query, string expected) => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select id, g from h union select id, g from h2", "1|2 / 2|3 / 3|1 / 4|1 / 5|2 / 6|3 / 7|2 / 8|3 / 9|1")]
    [DataRow("select g, id from h union select g, id from h2", "1|3 / 1|4 / 1|9 / 2|1 / 2|5 / 2|7 / 3|2 / 3|6 / 3|8")]
    [DataRow("select id from h union all select id from h2", "5 / 3 / 8 / 1 / 9 / 2 / 7 / 4 / 6 / 4 / 2 / 9 / 1 / 7")]
    [DataRow("select id from h union select id from h2 union all select 100", "1 / 2 / 3 / 4 / 5 / 6 / 7 / 8 / 9 / 100")]
    [DataRow("select id from h except select id from h2", "3 / 5 / 6 / 8")]
    [DataRow("select g, id from h intersect select g, id from h2", "1|4 / 1|9 / 2|1 / 2|7 / 3|2")]
    [DataRow("select top 2 id from h union select id from h2", "1 / 2 / 3 / 4 / 5 / 7 / 9")]
    [DataRow("select 2 union select 1", "2 / 1")]
    public void SetOperations_DedupSorts(string query, string expected) => AreEqual(expected, Rows(query));

    /// <summary>
    /// Over constants alone a union over a union, or one another set operator
    /// reads, merges; a lone union of two constant scans concatenates them,
    /// sorting only an input that repeats a row, or the whole when they share
    /// one (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select 3 union select 1 union select 2", "1 / 2 / 3")]
    [DataRow("select 2 union select 1 union select 2", "1 / 2")]
    [DataRow("select 5 union select 4 union all select 3", "4 / 5 / 3")]
    [DataRow("select 3 union all select 1 union all select 3 union select 0", "1 / 3 / 0")]
    [DataRow("select 3 union all select 1 union select 3", "1 / 3")]
    [DataRow("select 5 union (select 3 union all select 1 union all select 3)", "5 / 1 / 3")]
    [DataRow("select 2 union (select 9 union all select 1)", "2 / 9 / 1")]
    [DataRow("select 3 union all (select 2 union select 1)", "3 / 1 / 2")]
    [DataRow("select 3 union select 1 union all select 2 union select 0", "0 / 1 / 2 / 3")]
    [DataRow("(select 3 union all select 1 union all select 3) except (select 2)", "1 / 3")]
    [DataRow("(select 3 union all select 1) intersect (select 1 union all select 3)", "3 / 1")]
    [DataRow("(select 3 union all select 1 union all select 3) intersect (select 1 union all select 3)", "1 / 3")]
    [DataRow("select 2, 'b' union select 1, 'a' union select 1, 'c'", "1|a / 1|c / 2|b")]
    public void ConstantSetOperations_FollowRealsPlan(string query, string expected) => AreEqual(expected, Rows(query));

    /// <summary>
    /// An enclosing filter that rejects a constant branch outright folds it
    /// away before the union is planned, so only the branches left count: a
    /// filter reading a column bare in comparisons, <c>IN</c>, <c>BETWEEN</c>,
    /// <c>IS NULL</c> and their junctions folds, a column in arithmetic or a
    /// function, a <c>LIKE</c> or a variable doesn't, and a CTE read twice keeps
    /// a branch only both readings prune (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x > 1", "3 / 2")]
    [DataRow("select x from (select 1 x union select 3 union select 2) d where x > 1", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x > 0", "1 / 2 / 3")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x > 2", "3")]
    [DataRow("select x from (select 3 x union select 1 union select 2 union select 5) d where x > 1", "2 / 3 / 5")]
    [DataRow("select x from (select 3 x union select 1 union all select 2) d where x > 1", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union all select 2) d where x <> 3", "1 / 2")]
    [DataRow("select x from (select 3 x union select 1 union all select 2) d where x <> 2", "3 / 1")]
    [DataRow("select x from (select 3 x union select 1 union all select 2 union all select 0) d where x <> 2", "1 / 3 / 0")]
    [DataRow("select x from (select 3 x union select 1 union select 2 union all select 0) d where x > 1", "3 / 2")]
    [DataRow("select x from (select 3 x union all select 1 union all select 1 union select 2) d where x <> 2", "1 / 3")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x in (2, 3)", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where not (x = 1)", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x = 2 or x = 3", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x between 2 and 3", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x > 1 and (1 = 1 or x = 9)", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where exists (select 1) and x > 1", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where (select 1) = 1 and x > 1", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x > 1 and abs(x) > 0", "3 / 2")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x + 0 > 1", "2 / 3")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d where x like '%3%' or x = 2", "2 / 3")]
    [DataRow("declare @v int = 1; select x from (select 3 x union select @v union select 2) d where x > 1", "2 / 3")]
    [DataRow("declare @v int = 1; select x from (select 3 x union select 1 union select 2) d where x > @v", "2 / 3")]
    [DataRow("select x, k from (select 3 x, 9 k union select 1, 8 union select 2, 7) d where k > 7", "3|9 / 1|8")]
    [DataRow("select x from (select 3 x union select 1 union select 2) d join (select 1 y) e on d.x > 1 where e.y = 1", "3 / 2")]
    [DataRow("with c as (select 3 x union select 1 union select 2) select x from c where x > 1", "3 / 2")]
    [DataRow("with c as (select 3 x union select 1 union select 2) select x from c where x > 1 union all select x from c where x <> 2", "2 / 3 / 1 / 3")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x <> 1", "2 / 3 / 4")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x <> 4", "1 / 2 / 3")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x <> 2", "1 / 3 / 4")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x <> 3", "1 / 4 / 2")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x > 0", "1 / 2 / 3 / 4")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x > 3", "4")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x in (4, 3)", "4 / 3")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x in (2, 3)", "2 / 3")]
    [DataRow("select x from (select 4 x union all select 1 union select 2 union all select 3) d where x <> 1", "2 / 4 / 3")]
    [DataRow("select x from (select 4 x union all select 1 union select 2 union all select 3) d where x <> 2", "4 / 1 / 3")]
    [DataRow("select x from (select 4 x union all select 1 union select 2 union all select 3) d where x > 0", "1 / 2 / 4 / 3")]
    [DataRow("select x from (select 4 x union select 1 union all select 2) d where x <> 1", "4 / 2")]
    [DataRow("select x from (select 4 x union select 1 union all select 2) d where x > 0", "1 / 4 / 2")]
    [DataRow("select x from (select 4 x union all (select 2 union select 1)) d where x <> 1", "4 / 2")]
    [DataRow("select x from (select 4 x union all (select 2 union select 1)) d where x > 0", "4 / 1 / 2")]
    [DataRow("select x from (select 4 x union (select 2 union all select 1)) d where x <> 1", "4 / 2")]
    [DataRow("select x from (select 4 x union (select 5 union select 1)) d where x <> 1", "4 / 5")]
    [DataRow("select x from (select 4 x union (select 5 union select 1) union all select 0) d where x <> 1", "4 / 5 / 0")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union all select 0 union select 3) d where x <> 1", "0 / 2 / 3 / 4")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x <> 1 and x <> 2", "4 / 3")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3) d where x in (4, 2)", "4 / 2")]
    [DataRow("select x from (select 4 x union select 1 union all select 2 union select 3 union select 9) d where x <> 1", "2 / 3 / 4 / 9")]
    public void ConstantSetOperations_FoldTheEnclosingFilter(string query, string expected) => AreEqual(expected, Rows(query));

    /// <summary>
    /// A write through a windowed CTE still reaches the rows the body yields,
    /// though the body's rows leave in window order.
    /// </summary>
    [TestMethod]
    public void WindowedCteWrite_PairsRowsWithTheirBaseRows()
        => AreEqual("1:1,2:3", new Simulation().ExecuteScalar("""
            create table t (id int, v int); insert t values (2, 3), (1, 2), (1, 1);
            with d as (select *, row_number() over (partition by id order by v) rn from t) delete from d where rn > 1;
            select string_agg(concat(id, ':', v), ',') within group (order by id) from t
            """));
}
