using System.Globalization;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A <c>MIN</c> or <c>MAX</c> of a column an index leads, under a WHERE that
/// index's seek consumes whole, is read off the index's end as real's seek
/// reads it, passing over NULLs without Msg 8153, while a WHERE leaving
/// anything of the seek, or <c>MIN</c> and <c>MAX</c> together over a nullable
/// column, scans and warns (probed 2026-10-26 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class IndexEndAggregateTests
{
    [TestMethod]
    [DataRow("select min(k) from t", "0", false)]
    [DataRow("select max(k) from t where g = 3", "209", false)]
    [DataRow("select min(k) from t where g = 1 and k between 20 and 100", "21", false)]
    [DataRow("select max(k) from t where k < 50", "49", false)]
    [DataRow("select min(id), max(id) from t", "1|600", false)]
    [DataRow("select min(k) from t where v = 3", "7", true)]
    [DataRow("select min(k), max(k) from t", "0|210", true)]
    public void IndexEnd_AnswersAndWarnsAsRealDoes(string sql, string expected, bool warns)
    {
        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand("""
            create table t (id int primary key, g int not null, k int null, v int);
            create index ix_gk on t (g, k);
            create index ix_k on t (k);
            insert t select value, value % 4, case when value % 7 = 0 then null else value * 13 % 211 end, value % 5 from generate_series(1, 600);
            """).ExecuteNonQuery();
        var warned = false;
        connection.InfoMessage += (_, e) => warned |= e.Errors[0].Number == 8153;
        using var reader = connection.CreateCommand(sql).ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expected, string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        reader.Close();
        AreEqual(warns, warned);
    }

    /// <summary>
    /// Stepping through a table by <c>MIN</c> reads one row per step, where
    /// each step aggregated every row past the last.
    /// </summary>
    [TestMethod]
    [Timeout(20000)]
    public void MinLoop_RunsInLinearTime() =>
        AreEqual(16000, new Simulation().ExecuteScalar("""
            create table t (id int primary key, v int); insert t select value, value % 97 from generate_series(1, 16000);
            declare @x int = 0, @n int = 0;
            while 1 = 1
            begin
                select @x = min(id) from t where id > @x;
                if @x is null break;
                set @n += 1;
            end;
            select @n
            """));
}
