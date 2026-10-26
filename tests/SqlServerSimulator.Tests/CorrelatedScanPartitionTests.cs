using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A correlated subquery or APPLY body equating an unindexed column of the
/// table it scans with a value fixed for its execution reads that table once
/// per statement, partitioned by the column, rather than once per outer row,
/// and answers as the scans did: a query without ORDER BY meets a key's rows
/// in the scan's order, the comparison folds by the column's collation and
/// pads trailing spaces, and a statement writing the table reads its rows as
/// the writes leave them (probed 2026-10-26 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class CorrelatedScanPartitionTests
{
    [TestMethod]
    [DataRow(
        "select a.id, stuff((select ',' + cast(b.id as varchar(10)) from b where b.s = a.s for xml path('')), 1, 1, '') c from a",
        "1:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 2:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 3:1,2,7,8,13,14,19,20,25,26,31,32,37,38,43,44,49,50,55,56 4: 5:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 6:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 7:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 8:1,2,7,8,13,14,19,20,25,26,31,32,37,38,43,44,49,50,55,56 9: 10:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 11:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60 12:5,6,11,12,17,18,23,24,29,30,35,36,41,42,47,48,53,54,59,60")]
    [DataRow("select a.id, (select top 1 b.id from b where b.g = a.g) c from a", "1:1 2:2 3: 4: 5:5 6:6 7:9 8:1 9:2 10: 11: 12:5")]
    [DataRow("select a.id, (select count(*) from b where b.s = a.n) c from a", "1:20 2:0 3:0 4:20 5:20 6:0 7:0 8:20 9:20 10:0 11:0 12:20")]
    public void Partition_AnswersAsTheScansDid(string query, string expected) =>
        AreEqual(expected, Seeded().ExecuteScalar($"select string_agg(concat(id, ':', c), ' ') within group (order by id) from ({query}) q"));

    [TestMethod]
    public void WriteToTheTableRead_ReadsItsRowsAsTheWritesLeaveThem() =>
        AreEqual(
            "1:13 2:13 3:12 4: 5:13 6:13 7:11 8:12 9:12 10:13 11:13 12:12 13: 14:13 15:13 16:11 17:12 18:12 19:13 20:13 21:12 22: 23:13 24:13 25:11 26:12 27:12 28:13 29:13 30:12 31: 32:13 33:13 34:11 35:12 36:12 37:13 38:13 39:12 40: 41:13 42:13 43:11 44:12 45:12 46:13 47:13 48:12 49: 50:13 51:13 52:11 53:12 54:12 55:13 56:13 57:12 58: 59:13 60:13",
            Seeded().ExecuteScalar("""
                update b set v = (select max(b2.v) + 1 from b b2 where b2.g = b.g);
                select string_agg(concat(id, ':', v), ' ') within group (order by id) from b
                """));

    /// <summary>
    /// The grouped concatenation idiom reads its table once, where each group
    /// scanned every row.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void GroupedConcatenation_RunsInLinearTime() =>
        AreEqual(266894L, new Simulation().ExecuteScalar("""
            create table t (id int primary key, g int, name varchar(20));
            insert t select value, value % 2000, concat('n', value) from generate_series(1, 40000);
            select sum(len(x)) from (select g, stuff((select ',' + t2.name from t t2 where t2.g = t1.g for xml path('')), 1, 1, '') x from t t1 group by g) q
            """));

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table a (id int primary key, g int, s varchar(10) collate Latin1_General_CI_AS, n nvarchar(10) collate Latin1_General_CI_AS);
            insert a select value, nullif(value % 7, 3), choose(value % 5 + 1, 'x', 'X', 'x  ', 'y', null), choose(value % 4 + 1, N'x', N'Y', N'q', null) from generate_series(1, 12);
            create table b (id int primary key, g int, s varchar(10) collate Latin1_General_CI_AS, v int);
            insert b select value, nullif(value % 9, 4), choose(value % 6 + 1, 'x', 'Y', 'y ', 'z', null, 'X'), value * 7 % 13 from generate_series(1, 60);
            """);
        return simulation;
    }
}
