using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>sp_estimate_data_compression_savings</c>'s shape and refusals, probed
/// 2026-10-15 against SQL Server 2025. Its sizes are the simulator's own page
/// counts, current and requested alike.
/// </summary>
[TestClass]
public sealed class CompressionEstimateTests
{
    private static Simulation WithTables()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table dbo.t (id int primary key, c nvarchar(100), d int);
            create index ix on dbo.t (d);
            create table dbo.h (a int);
            insert dbo.t select top 200 row_number() over (order by (select null)), replicate(N'x', 50), 1 from sys.all_objects;
            exec('create view dbo.v as select id from dbo.t');
            """);
        return simulation;
    }

    [TestMethod]
    public void Estimate_ListsEachIndexPartition_InRealsShape()
    {
        using var reader = WithTables().ExecuteReader("exec sp_estimate_data_compression_savings 'dbo', 't', null, null, 'ROW'");
        CollectionAssert.AreEqual(new[]
        {
            "object_name", "schema_name", "index_id", "partition_number",
            "size_with_current_compression_setting(KB)", "size_with_requested_compression_setting(KB)",
            "sample_size_with_current_compression_setting(KB)", "sample_size_with_requested_compression_setting(KB)",
        }, Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray());
        var rows = reader.EnumerateRecords().Select(record => (record.GetString(0), record.GetString(1), record.GetInt32(2), record.GetInt32(3), record.GetInt64(4), record.GetInt64(5))).ToList();
        HasCount(2, rows);
        AreEqual(("t", "dbo", 1, 1), (rows[0].Item1, rows[0].Item2, rows[0].Item3, rows[0].Item4));
        AreEqual(2, rows[1].Item3);
        IsGreaterThan(0L, rows[0].Item5);
        AreEqual(rows[0].Item5, rows[0].Item6);
    }

    [TestMethod]
    public void Estimate_OfOneIndex_AndOfAnEmptyHeap()
    {
        var simulation = WithTables();
        AreEqual(2, simulation.ExecuteScalar("create table #r (o sysname, s sysname, i int, p int, a bigint, b bigint, c bigint, d bigint); insert #r exec sp_estimate_data_compression_savings 'dbo', 't', 2, 1, 'none'; select max(i) from #r"));
        AreEqual("0|1|0", simulation.ExecuteScalar("create table #r (o sysname, s sysname, i int, p int, a bigint, b bigint, c bigint, d bigint); insert #r exec sp_estimate_data_compression_savings 'dbo', 'h', null, null, 'PAGE'; select concat(i, '|', p, '|', a) from #r"));
    }

    [TestMethod]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 'nosuch', null, null, 'ROW'", 1088, 61, "Cannot find the object \"nosuch\" because it does not exist or you do not have permissions.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'nosch', 't', null, null, 'ROW'", 15659, 34, "The schema 'nosch' specified for parameter schema_name does not exist.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 't', null, null, 'BOGUS'", 3217, 105, "Invalid value specified for datacompression parameter.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 't', 9, null, 'ROW'", 15323, 116, "The selected index does not exist on table 't'.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 't', null, null, null", 23118, 97, "Either datacompression, or xmlcompression, or both must have a value. These parameters cannot be both set to NULL.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', null, null, null, 'ROW'", 15223, 44, "Error: The input parameter 'object_name' is not allowed to be null.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', '#t', null, null, 'ROW'", 15661, 51, "Compressing temporary tables is not supported by the stored procedure sp_estimate_data_compression_savings.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 't', null, 5, 'ROW'", 15001, 267, "Object 't' does not exist or is not a valid object for this operation.")]
    [DataRow("exec @rc = sp_estimate_data_compression_savings 'dbo', 'v', null, null, 'ROW'", 15001, 267, "Object 'v' does not exist or is not a valid object for this operation.")]
    public void Estimate_RefusesAsRealDoes(string sql, int number, int line, string message)
    {
        var error = WithTables().AssertSqlError("declare @rc int; " + sql, number).Errors[0];
        AreEqual((message, "sp_estimate_data_compression_savings", line), (error.Message, error.Procedure, error.LineNumber));
    }
}
