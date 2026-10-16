using System.Data;
using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The metadata SqlClient asks for on its own: <c>SqlConnection.GetSchema</c>,
/// whose collections are queries and the <c>_managed</c> procedures, and
/// <c>SqlCommandBuilder.DeriveParameters</c>, which reads
/// <c>sp_procedure_params_100_managed</c>.
/// </summary>
[TestClass]
public sealed class ClientMetadataWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seeded()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, """
            create table dbo.t (id int not null constraint pk_t primary key, name nvarchar(20) not null default N'x', amount decimal(9, 2),
                sparse_one int sparse null, all_sparse xml column_set for all_sparse_columns);
            create index ix_name on dbo.t (name) include (amount);
            create table dbo.child (id int, t_id int constraint fk_child_t references dbo.t (id));
            """);
        Wire.ExecInProc(simulation, "create view dbo.v as select id, name from dbo.t");
        Wire.ExecInProc(simulation, "create type dbo.keys as table (k int primary key)");
        Wire.ExecInProc(simulation, "create procedure dbo.p @a int, @b nvarchar(10) = N'q' output, @c decimal(5, 2) out, @d dbo.keys readonly as select @a");
        return simulation;
    }

    [TestMethod]
    public async Task GetSchema_AnswersEveryCollection()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        var collections = await connection.GetSchemaAsync("MetaDataCollections", TestContext.CancellationToken);
        foreach (DataRow collection in collections.Rows)
        {
            var name = (string)collection["CollectionName"];
            _ = await connection.GetSchemaAsync(name, TestContext.CancellationToken);
        }

        var columns = await connection.GetSchemaAsync("Columns", [null, "dbo", "t"], TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "id", "name", "amount", "all_sparse" }, columns.Rows.Cast<DataRow>().Select(row => (string)row["COLUMN_NAME"]).ToArray());
        AreEqual("(N'x')", columns.Rows[1]["COLUMN_DEFAULT"]);
        AreEqual(9, Convert.ToInt32(columns.Rows[2]["NUMERIC_PRECISION"], System.Globalization.CultureInfo.InvariantCulture));

        var allColumns = await connection.GetSchemaAsync("AllColumns", [null, "dbo", "t"], TestContext.CancellationToken);
        HasCount(5, allColumns.Rows.Cast<DataRow>());
        var columnSet = await connection.GetSchemaAsync("ColumnSetColumns", [null, "dbo", "t"], TestContext.CancellationToken);
        AreEqual("sparse_one", columnSet.Rows.Cast<DataRow>().Single()["COLUMN_NAME"]);

        var indexes = await connection.GetSchemaAsync("Indexes", [null, null, "t"], TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "ix_name", "pk_t" }, indexes.Rows.Cast<DataRow>().Select(row => (string)row["index_name"]).ToArray());
        AreEqual("CLUSTERED", indexes.Rows[1]["type_desc"]);

        var indexColumns = await connection.GetSchemaAsync("IndexColumns", [null, null, "t", "ix_name"], TestContext.CancellationToken);
        CollectionAssert.AreEqual(new[] { "amount:0", "name:1" }, indexColumns.Rows.Cast<DataRow>().Select(row => $"{row["column_name"]}:{row["ordinal_position"]}").ToArray());

        var foreignKeys = await connection.GetSchemaAsync("ForeignKeys", [null, null, "child"], TestContext.CancellationToken);
        AreEqual("fk_child_t", foreignKeys.Rows.Cast<DataRow>().Single()["CONSTRAINT_NAME"]);
    }

    [TestMethod]
    public async Task DeriveParameters_ReadsTheProceduresParameters()
    {
        await using var listener = await Seeded().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);

        await using var command = new SqlCommand("dbo.p", connection) { CommandType = CommandType.StoredProcedure };
        SqlCommandBuilder.DeriveParameters(command);
        var parameters = command.Parameters.Cast<SqlParameter>().ToArray();
        CollectionAssert.AreEqual(new[] { "@RETURN_VALUE", "@a", "@b", "@c", "@d" }, parameters.Select(parameter => parameter.ParameterName).ToArray());
        AreEqual(ParameterDirection.ReturnValue, parameters[0].Direction);
        AreEqual(SqlDbType.Int, parameters[1].SqlDbType);
        AreEqual(ParameterDirection.InputOutput, parameters[2].Direction);
        AreEqual(SqlDbType.NVarChar, parameters[2].SqlDbType);
        AreEqual(10, parameters[2].Size);
        AreEqual(SqlDbType.Decimal, parameters[3].SqlDbType);
        AreEqual((byte)5, parameters[3].Precision);
        AreEqual((byte)2, parameters[3].Scale);
        AreEqual(SqlDbType.Structured, parameters[4].SqlDbType);
        AreEqual("simulated.dbo.keys", parameters[4].TypeName);
    }
}
