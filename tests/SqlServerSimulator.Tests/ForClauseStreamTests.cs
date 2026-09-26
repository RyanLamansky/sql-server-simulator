using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A SELECT statement's own FOR JSON / untyped FOR XML result as the client
/// receives it: the document in rows of 2033 UTF-16 units, FOR XML typed
/// <c>ntext</c>, and <c>@@ROWCOUNT</c> the rows the clause serialized — none
/// of which applies where the same query is a subquery or a view body.
/// Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class ForClauseStreamTests
{
    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int identity primary key, a varchar(10));
            insert t (a) select 'xxxxxxx' from (values (0), (1), (2), (3), (4)) f (n)
                cross join (values (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) g (n) cross join (values (0), (1), (2), (3), (4), (5), (6), (7), (8), (9)) h (n)
            """);
        return simulation;
    }

    private static string ChunkLengths(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var lengths = new List<int>();
        while (reader.Read())
            lengths.Add(reader.GetString(0).Length);
        return string.Join(",", lengths);
    }

    [TestMethod]
    [DataRow("select a from t for json path", "2033,2033,2033,1902")]
    [DataRow("select a from t for xml raw", "2033,2033,2033,2033,868")]
    [DataRow("select a from t where id = 1 for json path", "17")]
    [DataRow("select a from t where id = 0 for json path", "")]
    public void TheDocumentStreamsIn2033UnitRows(string sql, string expected) =>
        AreEqual(expected, ChunkLengths(Seeded(), sql));

    [TestMethod]
    public void AChunkBoundarySplitsASurrogatePair()
    {
        using var reader = new Simulation().ExecuteReader(
            "select replicate(cast(N'y' as nvarchar(max)), 2026) + nchar(55357) + nchar(56832) + N'zz' a for json path, without_array_wrapper");
        IsTrue(reader.Read());
        AreEqual('\uD83D', reader.GetString(0)[^1]);
        IsTrue(reader.Read());
        AreEqual("\uDE00zz\"}", reader.GetString(0));
    }

    [TestMethod]
    public void TheStreamedDocumentJoinsBackToTheWholeValue()
    {
        var simulation = Seeded();
        using var reader = simulation.ExecuteReader("select a from t for json path");
        var streamed = string.Concat(reader.EnumerateRecords().Select(record => record.GetString(0)));
        AreEqual(simulation.ExecuteScalar("select (select a from t for json path)"), streamed);
    }

    [TestMethod]
    [DataRow("select a from t for json path; select @@rowcount", 500)]
    [DataRow("select a from t for xml auto; select @@rowcount", 500)]
    [DataRow("select a from t where id < 4 for xml path; select @@rowcount", 3)]
    [DataRow("select count(*) c from t for json path; select @@rowcount", 1)]
    [DataRow("select a from t for xml raw, type; select @@rowcount", 1)]
    [DataRow("select a from t where id = 0 for json path; select @@rowcount", 0)]
    [DataRow("declare @x nvarchar(max) = (select a from t for json path); select @@rowcount", 1)]
    public void RowCount_IsTheRowsTheClauseSerialized(string sql, int expected)
    {
        using var reader = Seeded().ExecuteReader(sql);
        object? last = null;
        do
        {
            while (reader.Read())
                last = reader.GetValue(0);
        }
        while (reader.NextResult());
        AreEqual(expected, last);
    }

    [TestMethod]
    [DataRow("select a from t for xml raw", "ntext")]
    [DataRow("select a from t for json path", "nvarchar(max)")]
    [DataRow("select (select a from t for xml raw) x", "nvarchar(max)")]
    [DataRow("select a from t for xml raw, type", "xml")]
    public void Describe_TypesTheStatementsOwnForXmlAsNText(string sql, string expected) =>
        AreEqual(expected, Seeded().ExecuteScalar($"select system_type_name from sys.dm_exec_describe_first_result_set(N'{sql}', null, 0)"));

    [TestMethod]
    public void AViewBody_ReturnsTheWholeDocument()
    {
        var simulation = Seeded();
        simulation.ExecuteBatches("create view v (j) as select a from t for json path");
        AreEqual("8001", ChunkLengths(simulation, "select j from v"));
    }
}
