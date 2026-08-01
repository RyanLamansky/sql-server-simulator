using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>FOR XML … , TYPE</c> result-column seam over real SqlClient: with
/// TYPE the single column arrives <em>unnamed</em> and typed <c>xml</c>, and an
/// empty input rowset still yields one row (NULL); without it the column keeps
/// SQL Server's GUID-shaped sentinel name, carries the string form, and an
/// empty rowset yields no rows at all. Probe-confirmed against SQL Server 2025
/// via <c>GetSchemaTable</c> (real reports <c>ntext</c> for the untyped column
/// where the simulator reports <c>nvarchar</c> — see <c>docs/claude/xml.md</c>).
/// </summary>
[TestClass]
public sealed class ForXmlTypeWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (id int not null, a int); insert t values (1, 10), (2, 30)");
        return simulation;
    }

    [TestMethod]
    public async Task Typed_ColumnIsUnnamedXml()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select id, a from t order by id for xml path('p'), type", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("", reader.GetName(0));
        AreEqual("xml", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("<p><id>1</id><a>10</a></p><p><id>2</id><a>30</a></p>", reader.GetString(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task Untyped_ColumnIsTheNamedStringSentinel()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select id, a from t order by id for xml path('p')", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual("XML_F52E2B61-18A1-11d1-B105-00805F49916B", reader.GetName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("<p><id>1</id><a>10</a></p><p><id>2</id><a>30</a></p>", reader.GetString(0));
    }

    [TestMethod]
    public async Task EmptyRowset_TypedYieldsNullRow_UntypedYieldsNoRows()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using (var typed = new SqlCommand("select id from t where 1 = 0 for xml path('p'), type", connection))
        await using (var reader = await typed.ExecuteReaderAsync(TestContext.CancellationToken))
        {
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
            IsTrue(await reader.IsDBNullAsync(0, TestContext.CancellationToken));
            IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
        }

        await using var untyped = new SqlCommand("select id from t where 1 = 0 for xml path('p')", connection);
        await using var untypedReader = await untyped.ExecuteReaderAsync(TestContext.CancellationToken);
        IsFalse(await untypedReader.ReadAsync(TestContext.CancellationToken));
    }
}
