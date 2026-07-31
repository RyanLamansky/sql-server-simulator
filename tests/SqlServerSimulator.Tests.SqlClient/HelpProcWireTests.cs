using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The sp_help family over the wire. <c>sp_help</c> is the simulator's widest
/// multi-result-set system procedure, so it exercises the TDS path that
/// streams several differently-shaped sets from a single SQLBatch, and its
/// severity-10 "no foreign keys reference…" messages arrive as SqlClient
/// <c>InfoMessage</c> events rather than result sets. Shapes are
/// probe-confirmed against SQL Server 2025 (2026-07-31).
/// </summary>
[TestClass]
public sealed class HelpProcWireTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task SpHelp_StreamsEveryResultSetToSqlClient()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table t (
                id int identity(1, 1) not null constraint pk_t primary key,
                name varchar(20) not null
            )
            """);

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        var messages = new List<int>();
        connection.InfoMessage += (_, e) =>
        {
            foreach (SqlError error in e.Errors)
                messages.Add(error.Number);
        };

        await using var command = new SqlCommand("exec sp_help 't'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        // Object info.
        AreEqual(4, reader.FieldCount);
        AreEqual("Created_datetime", reader.GetName(3));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("t", reader.GetString(0));
        AreEqual("user table", reader.GetString(2));

        // Column detail — the char(5)-padded Prec / Scale cells survive the wire.
        IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
        AreEqual(10, reader.FieldCount);
        AreEqual("Column_name", reader.GetName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("id", reader.GetString(0));
        AreEqual("10   ", reader.GetString(4));

        // Advances to the next result set and its first row, which is the
        // shape of every remaining single-row set below.
        async Task NextRow()
        {
            IsTrue(await reader.NextResultAsync(TestContext.CancellationToken));
            IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        }

        // Identity, rowguidcol, filegroup, indexes.
        await NextRow();
        AreEqual("id", reader.GetString(0));
        AreEqual(1m, reader.GetDecimal(1));

        await NextRow();
        AreEqual("No rowguidcol column defined.", reader.GetString(0));

        await NextRow();
        AreEqual("PRIMARY", reader.GetString(0));

        await NextRow();
        AreEqual("index_description", reader.GetName(1));
        AreEqual("pk_t", reader.GetString(0));

        // Constraints.
        await NextRow();
        AreEqual("constraint_type", reader.GetName(0));
        AreEqual("PRIMARY KEY (clustered)", reader.GetString(0));

        IsFalse(await reader.NextResultAsync(TestContext.CancellationToken));
        await reader.CloseAsync();
        Contains(15470, messages);
    }

    [TestMethod]
    public async Task SpHelpText_ReturnsTheModuleSourceOverTheWire()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create view v1 as select 1 as x");

        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("exec sp_helptext 'v1'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        AreEqual(1, reader.FieldCount);
        AreEqual("Text", reader.GetName(0));
        AreEqual("nvarchar", reader.GetDataTypeName(0));
        IsTrue(await reader.ReadAsync(TestContext.CancellationToken));
        AreEqual("create view v1 as select 1 as x", reader.GetString(0));
        IsFalse(await reader.ReadAsync(TestContext.CancellationToken));
    }
}
