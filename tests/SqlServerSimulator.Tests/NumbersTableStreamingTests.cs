using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The numbers-table idiom — <c>ROW_NUMBER() OVER (ORDER BY (SELECT NULL))</c>
/// over a cross join of two large sources, cut by a <c>TOP</c>, a
/// <c>FETCH</c> or a bound on the number — streams: the window numbers rows as
/// they arrive, so reading stops once the limit is met rather than numbering
/// the whole product first. Each query here crosses a product of at least
/// twenty million rows and has a few seconds to finish, which buffering the
/// product could not do (it ran out of memory).
/// </summary>
[TestClass]
public sealed class NumbersTableStreamingTests
{
    private const string Product = "sys.all_columns a cross join sys.all_columns b";

    private static SimulatedDbConnection Open(string setup = "")
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        if (setup.Length != 0)
            _ = Read(connection, setup);
        return connection;
    }

    /// <summary>
    /// The values <paramref name="sql"/> reads, within <paramref name="timeoutSeconds"/>:
    /// a few seconds for a query whose only work is the rows it streams, longer
    /// for one that also writes its rows, so a slow or loaded host still fits
    /// that work in.
    /// </summary>
    private static List<long> Read(SimulatedDbConnection connection, string sql, int timeoutSeconds = 5)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = timeoutSeconds;
        using var reader = command.ExecuteReader();
        var values = new List<long>();
        do
        {
            while (reader.Read())
                values.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }
        while (reader.NextResult());
        return values;
    }

    private static List<long> Range(long first, int count) => [.. Enumerable.Range(0, count).Select(i => first + i)];

    private static void AreSequenceEqual(List<long> expected, List<long> actual) => CollectionAssert.AreEqual(expected, actual);

    [TestMethod]
    [DataRow($"select top (10) row_number() over (order by (select null)) from {Product}")]
    [DataRow($"select top (10) row_number() over (order by (select 1)) n from {Product} order by n")]
    [DataRow($"select top (10) row_number() over (order by (select null)) n from {Product} order by (select null)")]
    [DataRow($"select top (10) row_number() over (order by (select null)) from sys.all_columns a, sys.all_columns b")]
    [DataRow($"select top (10) row_number() over (order by (select null)) from {Product} cross join sys.all_columns c")]
    [DataRow("select top (10) row_number() over (order by (select null)) from master..spt_values a cross join master..spt_values b")]
    [DataRow($"with n as (select row_number() over (order by (select null)) rn from {Product}) select top (10) rn from n")]
    [DataRow($"with n as (select row_number() over (order by (select null)) rn from {Product}) select top (10) rn from n order by rn")]
    [DataRow($"with n as (select row_number() over (order by (select null)) rn, a.name from {Product}) select top (10) * from n order by 1")]
    [DataRow($"with n as (select row_number() over (order by (select null)) rn from {Product}) select rn from n where rn <= 10")]
    [DataRow($"select rn from (select row_number() over (order by (select null)) rn from {Product}) n where rn between 1 and 10")]
    [DataRow($"select row_number() over (order by (select null)) n from {Product} order by n offset 0 rows fetch next 10 rows only")]
    public void FirstTenOfTheProduct(string sql)
    {
        using var connection = Open();
        AreSequenceEqual(Range(1, 10), Read(connection, sql));
    }

    [TestMethod]
    [DataRow($"select row_number() over (order by (select null)) n from {Product} order by (select null) offset 100 rows fetch next 5 rows only")]
    [DataRow($"select row_number() over (order by (select null)) n from {Product} order by n offset 100 rows fetch next 5 rows only")]
    [DataRow($"with n as (select row_number() over (order by (select null)) rn from {Product}) select rn from n order by rn offset 100 rows fetch next 5 rows only")]
    [DataRow($"select rn from (select row_number() over (order by (select null)) rn from {Product}) n where rn between 101 and 105")]
    public void APageOfTheProduct(string sql)
    {
        using var connection = Open();
        AreSequenceEqual(Range(101, 5), Read(connection, sql));
    }

    [TestMethod]
    public void SelectIntoAndInsertSelectStream()
    {
        using var connection = Open();
        AreSequenceEqual([100_000, 5_000_050_000], Read(connection, $"""
            select top (100000) row_number() over (order by (select null)) n into #nums from {Product};
            select count(*) from #nums union all select sum(n) from #nums
            """, timeoutSeconds: 60));
        AreSequenceEqual([100_000], Read(connection, $"""
            create table #t (n bigint);
            insert #t select top (100000) row_number() over (order by (select null)) from {Product};
            select count(*) from #t
            """, timeoutSeconds: 60));
    }

    /// <summary>A user table of 10^5 rows crossed with itself: 10^10 tuples, ten read.</summary>
    [TestMethod]
    public void UserTableCrossedWithItself()
    {
        using var connection = Open($"select top (100000) row_number() over (order by (select null)) id into big from {Product}");
        AreSequenceEqual(Range(1, 10), Read(connection, "select top (10) row_number() over (order by (select null)) from big a cross join big b"));
        AreSequenceEqual(Range(1, 10), Read(connection, "select top (10) row_number() over (order by (select null)) from big a cross join sys.all_columns b"));
        AreSequenceEqual(Range(1, 10), Read(connection, "select top (10) row_number() over (order by (select null)) from sys.all_columns a cross join big b"));
    }

    /// <summary>The CTE-stacked numbers generator, ten rows cross-joined to 10^8.</summary>
    [TestMethod]
    public void StackedCteGenerator()
    {
        using var connection = Open();
        AreSequenceEqual(Range(1, 10), Read(connection, """
            with e1(n) as (select 1 from (values (1), (1), (1), (1), (1), (1), (1), (1), (1), (1)) v (n)),
                e2(n) as (select 1 from e1 a cross join e1 b),
                e4(n) as (select 1 from e2 a cross join e2 b),
                e8(n) as (select 1 from e4 a cross join e4 b)
            select top (10) row_number() over (order by (select null)) from e8
            """));
    }

    /// <summary>
    /// A row past the limit never evaluates its projection, so the division by
    /// zero the sixth row would raise doesn't (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void RowsPastTheLimitAreNotProjected()
    {
        using var connection = Open("create table t (id int primary key); insert t select top (50) row_number() over (order by (select null)) from sys.all_columns");
        AreSequenceEqual([2, 2, 3, 5, 10], Read(connection, "select top (5) 10 / (6 - id), row_number() over (order by (select null)) from t"));
    }

    /// <summary>A constant statement ORDER BY ties every row, so it keeps them as they arrive and reads only the ones it returns.</summary>
    [TestMethod]
    public void ConstantStatementOrderStreams()
    {
        using var connection = Open("create table t (id int primary key); insert t values (3), (1), (2)");
        AreSequenceEqual([1, 2], Read(connection, "select top (2) id from t order by (select null)"));
        HasCount(10, Read(connection, $"select top (10) a.column_id from {Product} order by (select null)"));
    }

    /// <summary>An order the arrival doesn't supply still sorts: a descending number, or a window ordered by a column.</summary>
    [TestMethod]
    public void OtherOrdersStillSort()
    {
        using var connection = Open("create table t (id int primary key, v int); insert t values (1, 30), (2, 10), (3, 20)");
        AreSequenceEqual([3, 2], Read(connection, "select top (2) row_number() over (order by (select null)) n from t order by n desc"));
        AreSequenceEqual([3, 2], Read(connection, "with n as (select row_number() over (order by (select null)) rn from t) select top (2) rn from n order by rn desc"));
        AreSequenceEqual([2, 3, 1], Read(connection, "select id, row_number() over (order by v) from t"));
        AreSequenceEqual([1, 2], Read(connection, "select top (2) row_number() over (order by v) n from t order by n"));
    }
}
