using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Wire coverage for varchar data under a non-CP1252 column collation: what
/// survives depends on which parameter type the client sends, and the endpoint
/// reproduces real SQL Server's split exactly.
/// </summary>
[TestClass]
public sealed class CollationCodePageWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private const string CreateTable =
        "create table t (tag varchar(10), tr varchar(40) collate Turkish_CI_AS, ja varchar(40) collate Japanese_XJIS_140_CI_AS)";

    private const string Turkish = "Ğğİış";

    private const string Japanese = "こんにちは";

    /// <summary>
    /// A Unicode parameter reaches the server intact, so the column stores its
    /// own code page's bytes — five CP1254 bytes for the Turkish column, ten
    /// CP932 bytes for the Japanese one.
    /// </summary>
    [TestMethod]
    public async Task UnicodeParameter_NonCp1252Column_RoundTripsThroughItsCodePage()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var create = new SqlCommand(CreateTable, connection))
            _ = await create.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var insert = new SqlCommand("insert t values ('u', @a, @b)", connection))
        {
            _ = insert.Parameters.AddWithValue("@a", Turkish);
            _ = insert.Parameters.AddWithValue("@b", Japanese);
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await using var read = new SqlCommand("select tr, ja, datalength(tr), datalength(ja) from t", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(Turkish, reader.GetString(0));
        AreEqual(Japanese, reader.GetString(1));
        AreEqual(5, reader.GetInt32(2));
        AreEqual(10, reader.GetInt32(3));
    }

    /// <summary>
    /// An <c>AnsiString</c> parameter is encoded by the client in the
    /// <em>server</em> collation's code page before it reaches the wire, so
    /// text outside that code page is already lost on arrival regardless of
    /// the destination column's collation. Probe-confirmed byte-identical on
    /// SQL Server 2025 with a CP1252 server collation: the Turkish letters
    /// best-fit-fold to <c>GgIis</c> and the kana become <c>?????</c>.
    /// </summary>
    [TestMethod]
    public async Task AnsiStringParameter_EncodesInServerCollation_BeforeReachingTheColumn()
    {
        var simulation = new Simulation();
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var create = new SqlCommand(CreateTable, connection))
            _ = await create.ExecuteNonQueryAsync(TestContext.CancellationToken);

        await using (var insert = new SqlCommand("insert t values ('a', @a, @b)", connection))
        {
            insert.Parameters.Add("@a", System.Data.SqlDbType.VarChar, 40).Value = Turkish;
            insert.Parameters.Add("@b", System.Data.SqlDbType.VarChar, 40).Value = Japanese;
            _ = await insert.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        await using var read = new SqlCommand("select tr, ja, datalength(tr), datalength(ja) from t", connection);
        await using var reader = await read.ExecuteReaderAsync(TestContext.CancellationToken);
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("GgIis", reader.GetString(0));
        AreEqual("?????", reader.GetString(1));
        AreEqual(5, reader.GetInt32(2));
        AreEqual(5, reader.GetInt32(3));
    }
}
