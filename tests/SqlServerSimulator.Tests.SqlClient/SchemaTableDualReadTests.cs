using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The in-process reader's <c>GetSchemaTable</c> against SqlClient's reading
/// the same result over the wire — every cell of every row, for a result
/// spanning the type system and for browse-mode (<c>KeyInfo</c>) results.
/// The wire side is SqlClient deriving the table from COLMETADATA, TABNAME and
/// COLINFO, which the endpoint writes as SQL Server 2025 does, so agreement
/// here is agreement with what a SqlClient application sees against real.
/// </summary>
[TestClass]
public sealed class SchemaTableDualReadTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table stt (id int identity primary key, ti tinyint not null, si smallint, bi bigint, bt bit, d decimal(10,2), n numeric(5,0),
                f float, r real, m money, sm smallmoney, dt datetime, sdt smalldatetime, dd date, t time(3), dt2 datetime2(4), dto datetimeoffset,
                c char(5), vc varchar(10), vcm varchar(max), nc nchar(4), nvc nvarchar(20) not null, nvm nvarchar(max), b binary(3), vb varbinary(7),
                vbm varbinary(max), g uniqueidentifier, x xml, sv sql_variant, rv rowversion, tx text, ntx ntext, img image, comp as (ti + 1), h hierarchyid);
            create table w1 (id int identity primary key, name varchar(10), amt int, rv rowversion);
            insert w1 (name, amt) values ('a', 1)
            """);
        return simulation;
    }

    private static List<string> Rows(DataTable schema)
    {
        var rows = new List<string>();
        foreach (DataRow row in schema.Rows)
        {
            rows.Add(string.Join("|", schema.Columns.Cast<DataColumn>().Select(column => row[column] switch
            {
                Type type => type.Name,
                DBNull => "NULL",
                var value => $"{column.ColumnName}={value}",
            })));
        }
        return rows;
    }

    [TestMethod]
    [DataRow("select *, ti * 2 as expr, vc as alias from stt", CommandBehavior.Default)]
    [DataRow("select name as n, amt * 2 as x, id from w1", CommandBehavior.KeyInfo)]
    [DataRow("select name from w1 for browse", CommandBehavior.Default)]
    [DataRow("select count(*), max(name) from w1", CommandBehavior.KeyInfo)]
    public async Task InProcessSchemaTable_MatchesSqlClientsOverTheWire(string sql, CommandBehavior behavior)
    {
        var simulation = Seeded();

        List<string> inProcess;
        using (var connection = simulation.CreateDbConnection())
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader(behavior);
            inProcess = Rows(reader.GetSchemaTable()!);
        }

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var wire = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var wireCommand = new SqlCommand(sql, wire);
        await using var wireReader = await wireCommand.ExecuteReaderAsync(behavior, TestContext.CancellationToken);
        var overWire = Rows(wireReader.GetSchemaTable());

        AreEqual(string.Join("\n", overWire), string.Join("\n", inProcess));
    }

    [TestMethod]
    public void DataTableLoad_ReadsTheSchema()
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select id, name, amt from w1";
        using var reader = command.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);

        HasCount(1, table.Rows);
        IsTrue(table.Columns["id"]!.AutoIncrement);
        IsTrue(table.Columns["id"]!.ReadOnly);
        AreEqual(10, table.Columns["name"]!.MaxLength);
    }

    [TestMethod]
    public void KeyInfoReader_RestoresTheSessionOnClose()
    {
        using var connection = Seeded().CreateDbConnection();
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "select name from w1";
            using var reader = command.ExecuteReader(CommandBehavior.KeyInfo);
            AreEqual(3, reader.FieldCount);
            AreEqual(1, reader.VisibleFieldCount);
        }
        using var plain = connection.CreateCommand();
        plain.CommandText = "select name from w1";
        using var plainReader = plain.ExecuteReader();
        AreEqual(1, plainReader.FieldCount);
    }
}
