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
        Wire.ExecInProc(simulation, "create view v1 as select name, amt from w1");
        Wire.ExecInProc(simulation, "create view v2 as select id, name as nm, amt + 1 as a1 from w1");
        Wire.ExecInProc(simulation, "create view v3 as select a.name, b.v from w1 a join w2 b on a.id = b.k1");
        Wire.ExecInProc(simulation, "create view v4 as select a, b from h1");
        Wire.ExecInProc(simulation, "create view v5 as select name, count(*) c from w1 group by name");
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

    /// <summary>
    /// A derived table, view or CTE flattens into the base tables under it:
    /// its columns name their base columns, and those tables' keys and
    /// rowversions ride as hidden columns, unless the body groups or is a set
    /// operation (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select name from (select name, id from w1) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select name from w1) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select * from (select name, id from w1) d", "name:.w1.name: id:.w1.id:k rv:.w1.rv:h visible=2")]
    [DataRow("select x from (select name as x from w1) d", "x:.w1.name:a id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select n from (select name n, amt from w1) d", "n:.w1.name:a id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name, amt from v1", "name:.w1.name: amt:.w1.amt: id:.w1.id:kh rv:.w1.rv:h visible=2")]
    [DataRow("select * from v2", "id:.w1.id:k nm:.w1.name:a a1:..a1:e rv:.w1.rv:h visible=3")]
    [DataRow("select nm from v2", "nm:.w1.name:a id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select a1 from v2", "a1:..a1:e id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select * from v3", "name:.w1.name: v:.w2.v: id:.w1.id:kh rv:.w1.rv:h k1:.w2.k1:kh k2:.w2.k2:kh visible=2")]
    [DataRow("select name from v3", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h k1:.w2.k1:kh k2:.w2.k2:kh visible=1")]
    [DataRow("select a from v4", "a:.h1.a: visible=1")]
    [DataRow("select * from v5", "name:.w1.name: c:..c: visible=2")]
    [DataRow("select d.name, w2.v from (select name, id from w1) d join w2 on d.id = w2.k1", "name:.w1.name: v:.w2.v: id:.w1.id:kh rv:.w1.rv:h k1:.w2.k1:kh k2:.w2.k2:kh visible=2")]
    [DataRow("select name from (select name, id from w1) d where id > 0", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select top 5 name, id from w1) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select distinct name from w1) d", "name:.w1.name: visible=1")]
    [DataRow("select name from (select name from w1 union all select name from w1) d", "name:..name:e visible=1")]
    [DataRow("select q.name from (select name from (select name, id from w1) i) q", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from v1 x", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select d.n from (select name n from v1) d", "n:.w1.name:a id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select * from (select id, name from w1 group by id, name) d", "id:.w1.id:k name:.w1.name: visible=2")]
    [DataRow("with c as (select name, id from w1) select name from c", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select a.name, b.name from (select id, name from w1) a join w1 b on a.id = b.id", "name:.w1.name: name:.w1.name: id:.w1.id:kh rv:.w1.rv:h id:.w1.id:kh rv:.w1.rv:h visible=2")]
    [DataRow("select x.name from (select id + 0 as id2, name from w1) x", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select name, id from w1) d left join h1 on d.id = h1.a", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select d.v from (select v from w2) d", "v:.w2.v: k1:.w2.k1:kh k2:.w2.k2:kh visible=1")]
    [DataRow("select n from (select name n, rv from w1) d", "n:.w1.name:a id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select w1.name, w2.v from w1 cross join w2) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h k1:.w2.k1:kh k2:.w2.k2:kh visible=1")]
    [DataRow("select * from (select name from w1 where id in (select k1 from w2)) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    [DataRow("select name from (select * from w1) d", "name:.w1.name: id:.w1.id:kh rv:.w1.rv:h visible=1")]
    public async Task KeyInfo_FlattensDerivedTablesViewsAndCtes(string sql, string expected) =>
        AreEqual(expected, await this.KeyInfoSchema(sql));

    [TestMethod]
    public async Task HiddenColumns_ThroughAViewCarryTheBaseKeys()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("select v from v3", connection);
        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.KeyInfo, TestContext.CancellationToken);

        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual(5, reader.FieldCount);
        AreEqual(1, reader.VisibleFieldCount);
        AreEqual(10, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(1, reader.GetInt32(3));
        AreEqual(1, reader.GetInt32(4));
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
