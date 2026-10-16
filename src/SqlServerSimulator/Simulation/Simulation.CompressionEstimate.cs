using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    private static readonly SystemProcedureParameter[] CompressionEstimateParameters =
    [
        new("schema_name", SqlType.NVarchar, 128),
        new("object_name", SqlType.NVarchar, 128),
        new("index_id", SqlType.Int32),
        new("partition_number", SqlType.Int32),
        new("data_compression", SqlType.NVarchar, 60),
        new("xml_compression", SqlType.Int32, 0, SqlValue.Null(SqlType.Int32)),
    ];

    private static readonly SqlType[] CompressionEstimateSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.Int32, SqlType.Int32, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt, SqlType.BigInt,
    ];

    private static readonly string[] CompressionEstimateColumnNames =
    [
        "object_name", "schema_name", "index_id", "partition_number",
        "size_with_current_compression_setting(KB)", "size_with_requested_compression_setting(KB)",
        "sample_size_with_current_compression_setting(KB)", "sample_size_with_requested_compression_setting(KB)",
    ];

    /// <summary>
    /// <c>sp_estimate_data_compression_savings @schema_name, @object_name,
    /// @index_id, @partition_number, @data_compression [, @xml_compression]</c>:
    /// one row per (index, partition) of a table, narrowed by the index and
    /// partition when given, in real's result shape and with real's refusals
    /// from real's lines, each returning its own number (probed 2026-10-15
    /// against SQL Server 2025). Real sizes the requested compression by
    /// rebuilding a sample of the rows in <c>tempdb</c>; the simulator, which
    /// stores rows uncompressed whatever the setting, reports every size as the
    /// unit's <c>sys.dm_db_partition_stats</c> pages, so the current and
    /// requested estimates are equal.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpEstimateDataCompressionSavings(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments("sp_estimate_data_compression_savings", calledAs, arguments, CompressionEstimateParameters);
        var database = batch.CurrentDatabase;
        var schemaName = values[0].IsNull ? batch.DefaultSchemaName : values[0].AsString;
        if (!database.Schemas.TryGetValue(schemaName, out var schema))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.SchemaParameterDoesNotExist(schemaName), 34);
        if (values[1].IsNull)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.InputParameterNotAllowedNull("object_name"), 44);
        var objectName = values[1].AsString;
        if (objectName.StartsWith('#'))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.CompressionEstimateOfTemporaryTable(), 51);
        var table = schema.HeapTables.TryGetValue(objectName, out var found) ? found : null;
        if (table is null && !schema.Views.ContainsKey(objectName))
        {
            throw OptionProcedureRefusal(calledAs, schema.HoldsObjectNamed(objectName)
                ? SimulatedSqlException.ObjectNotValidForOperation(objectName)
                : SimulatedSqlException.CompressionEstimateObjectNotFound(objectName), schema.HoldsObjectNamed(objectName) ? 69 : 61);
        }
        if (values[4].IsNull && values[5].IsNull)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.CompressionParametersBothNull(), 97);
        if (!values[4].IsNull && !IsAnyOf(values[4].AsString, "NONE", "ROW", "PAGE", "COLUMNSTORE", "COLUMNSTORE_ARCHIVE"))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.InvalidDataCompressionParameter(), 105);
        int? indexId = values[2].IsNull ? null : values[2].AsInt32;
        if (indexId is { } wanted && (table is null || !table.IndexIdentities().Exists(identity => identity.IndexId == wanted)))
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.SelectedIndexDoesNotExist(objectName), 116);

        int? partitionNumber = values[3].IsNull ? null : values[3].AsInt32;
        var rows = new List<SqlValue[]>();
        if (table is not null)
        {
            var objectValue = SqlValue.FromSystemName(objectName);
            var schemaValue = SqlValue.FromSystemName(schema.Name);
            foreach (var (unitIndex, unitPartition, usedPages) in BuiltInResources.PartitionUsedPages(batch, table))
            {
                if ((partitionNumber is { } p && unitPartition != p) || (indexId is { } i && unitIndex != i))
                    continue;
                var size = SqlValue.FromInt64(usedPages * 8);
                rows.Add([objectValue, schemaValue, SqlValue.FromInt32(unitIndex), SqlValue.FromInt32(unitPartition), size, size, size, size]);
            }
        }
        // A view without an index, or a partition the table doesn't have,
        // leaves nothing to estimate.
        if (rows.Count == 0)
            throw OptionProcedureRefusal(calledAs, SimulatedSqlException.ObjectNotValidForOperation(objectName), 267);
        yield return new SimulatedSqlResultSet(CompressionEstimateSchema, CompressionEstimateColumnNames, rows);
    }
}
