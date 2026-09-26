using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Browse mode — <c>CommandBehavior.KeyInfo</c>, which SqlClient sends as
/// <c>SET NO_BROWSETABLE ON</c> around the query — as SqlClient's
/// <c>GetSchemaTable</c> reads it: each base table's key and rowversion
/// columns appended as hidden columns, and every column's base table and
/// column, key, alias and expression flags from the TABNAME / COLINFO tokens.
/// Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BrowseModeWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table w1 (id int identity primary key, name varchar(10), amt int, rv rowversion);
            insert w1 (name, amt) values ('a', 1), ('b', 2);
            create table w2 (k1 int, k2 int, v int, primary key (k1, k2));
            insert w2 values (1, 1, 10);
            create table h1 (a int, b int);
            insert h1 values (1, 2);
            create table u1 (a int not null, b int);
            create unique index ux on u1 (a);
            insert u1 values (1, 2)
            """);
        return simulation;
    }

    // "name base key hidden expr alias" per schema row, plus the visible count.
    private async Task<string> KeyInfoSchema(string sql, CommandBehavior behavior = CommandBehavior.KeyInfo)
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(behavior, TestContext.CancellationToken);
        var rows = new List<string>();
        foreach (DataRow row in reader.GetSchemaTable().Rows)
        {
            rows.Add($"{row["ColumnName"]}:{row["BaseSchemaName"]}.{row["BaseTableName"]}.{row["BaseColumnName"]}"
                + $":{Flag(row["IsKey"], "k")}{Flag(row["IsHidden"], "h")}{Flag(row["IsExpression"], "e")}{Flag(row["IsAliased"], "a")}");
        }
        while (await reader.ReadAsync(TestContext.CancellationToken))
        {
        }
        return $"{string.Join(" ", rows)} visible={reader.VisibleFieldCount}";
    }

    private static string Flag(object value, string letter) => value is true ? letter : "";

    [TestMethod]
    [DataRow("select name, amt from w1", "name:.w1.name: amt:.w1.amt: id:.w1.id:kh rv:.w1.rv:h visible=2")]
    [DataRow("select id, name from w1", "id:.w1.id:k name:.w1.name: rv:.w1.rv:h visible=2")]
    [DataRow("select name as n, amt * 2 as x from w1", "n:.w1.name:a x:..x:e id:.w1.id:kh rv:.w1.rv:h visible=2")]
    [DataRow("select id as k, name from w1", "k:.w1.id:ka name:.w1.name: rv:.w1.rv:h visible=2")]
    [DataRow("select * from w2", "k1:.w2.k1:k k2:.w2.k2:k v:.w2.v: visible=3")]
    [DataRow("select a.name, b.v from w1 a join w2 b on a.id = b.k1",
        "name:.w1.name: v:.w2.v: id:.w1.id:kh rv:.w1.rv:h k1:.w2.k1:kh k2:.w2.k2:kh visible=2")]
    [DataRow("select a from h1", "a:.h1.a: visible=1")]
    [DataRow("select b from u1", "b:.u1.b: a:.u1.a:kh visible=1")]
    [DataRow("select name, amt from dbo.w1", "name:dbo.w1.name: amt:dbo.w1.amt: id:dbo.w1.id:kh rv:dbo.w1.rv:h visible=2")]
    [DataRow("select count(*) from w1", ":..:e visible=1")]
    [DataRow("select distinct name from w1", "name:.w1.name: visible=1")]
    [DataRow("select name from w1 union select name from w1", "name:..name:e visible=1")]
    public async Task KeyInfo_DescribesBaseColumnsAndCarriesHiddenKeys(string sql, string expected) =>
        AreEqual(expected, await this.KeyInfoSchema(sql));

    /// <summary>
    /// <c>FOR BROWSE</c> puts its one statement in browse mode without the
    /// session option, an <c>OPTION</c> clause may follow it, and a set
    /// operation refuses it with Msg 198.
    /// </summary>
    [TestMethod]
    [DataRow("select name from w1 for browse", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from w1 order by amt for browse option (maxdop 1)", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select b from u1 for browse", "b:.u1.b: a:.u1.a:kh visible=1")]
    public async Task ForBrowse_DescribesItsOneStatement(string sql, string expected) =>
        AreEqual(expected, await this.KeyInfoSchema(sql, CommandBehavior.Default));

    [TestMethod]
    public async Task ForBrowse_OverASetOperation_IsMsg198()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select name from w1 union select name from w1 for browse", connection);
        AreEqual(198, (await ThrowsAsync<SqlException>(() => command.ExecuteReaderAsync(TestContext.CancellationToken))).Number);
    }

    [TestMethod]
    public async Task HiddenColumns_RideEveryRowButStayOutOfGetValues()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select name from w1 order by id", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(3, reader.FieldCount);
        var values = new object[3];
        AreEqual(1, reader.GetValues(values));
        AreEqual("a", values[0]);
        AreEqual(1, reader.GetInt32(1));
    }

    [TestMethod]
    public async Task WithoutKeyInfo_NoHiddenColumns()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select name from w1", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        AreEqual(1, reader.FieldCount);
    }
}
