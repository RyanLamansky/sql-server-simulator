using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Each value of an <c>IN</c> list, or of an <c>OR</c> of equalities, compares
/// with the column under its own type promotion, so over an index the list
/// reads what a scan reads: against a <c>varbinary</c> column <c>0x01</c>
/// matches every key equal to it once padded with zero bytes, while <c>2</c>
/// converts the column to <c>int</c> (probed 2026-10-25 against SQL Server
/// 2025, with and without the index).
/// </summary>
[TestClass]
public sealed class MixedTypeInListSeekTests
{
    private static Simulation Keys(bool indexed)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"""
            create table vb (id int primary key, k varbinary(8));
            insert vb values (5, 0x01), (6, 0x0100), (7, 0x010000), (8, 0x02), (9, 0x00000002), (10, 0x0101);
            {(indexed ? "create index ix on vb (k);" : "")}
            """);
        return sim;
    }

    [TestMethod]
    [DataRow("k in (0x01, 2)", "5,6,7,8,9")]
    [DataRow("k in (2, 0x01)", "5,6,7,8,9")]
    [DataRow("k = 0x01 or k = 2", "5,6,7,8,9")]
    [DataRow("k in (0x01, 0x02)", "5,6,7,8")]
    [DataRow("k in (1, 2)", "5,8,9")]
    [DataRow("k = 0x01", "5,6,7")]
    public void Read_MatchesWhatEachValuesOwnComparisonMatches(string predicate, string expected)
    {
        foreach (var indexed in (ReadOnlySpan<bool>)[false, true])
            AreEqual(expected, Keys(indexed).ExecuteScalar($"select string_agg(id, ',') within group (order by id) from vb where {predicate}"), $"indexed: {indexed}");
    }

    [TestMethod]
    public void Delete_RemovesWhatTheScanMatches()
        => AreEqual("10", Keys(indexed: true).ExecuteScalar("delete vb where k in (0x01, 2); select string_agg(id, ',') from vb"));
}
