using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Trace;

namespace SqlServerSimulator;

// SQL Trace's catalog views, read from SQL Server 2025's own rows, and the
// user traces' rows of sys.traces; see docs/claude/trace.md.
internal static partial class BuiltInResources
{
    private static void RegisterTraceViews(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        var name = NVarcharSqlType.Get(128, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        var package = NVarcharSqlType.Get(60, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        SqlValue Text(NVarcharSqlType type, string value) => SqlValue.FromNVarchar(type, value);

        Sys("trace_categories",
        [
            new("category_id", SqlType.SmallInt, null, false),
            new("name", name, 128, true),
            new("type", SqlType.TinyInt, null, false),
        ], (_, _) => TraceCatalog.Instance.Categories.Select(category => new[]
        {
            SqlValue.FromInt16(category.Id), Text(name, category.Name), SqlValue.FromByte(category.Type),
        }));

        Sys("trace_columns",
        [
            new("trace_column_id", SqlType.SmallInt, null, false),
            new("name", name, 128, true),
            new("type_name", name, 128, true),
            new("max_size", SqlType.Int32, null, true),
            new("is_filterable", SqlType.Bit, null, false),
            new("is_repeatable", SqlType.Bit, null, false),
            new("is_repeated_base", SqlType.Bit, null, false),
        ], (_, _) => TraceCatalog.Instance.Columns.Select(column => new[]
        {
            SqlValue.FromInt16(column.Id), Text(name, column.Name), Text(name, column.TypeName), SqlValue.FromInt32(column.MaxSize),
            SqlValue.FromBoolean(column.IsFilterable), SqlValue.FromBoolean(column.IsRepeatable), SqlValue.FromBoolean(column.IsRepeatedBase),
        }));

        Sys("trace_events",
        [
            new("trace_event_id", SqlType.SmallInt, null, false),
            new("category_id", SqlType.SmallInt, null, false),
            new("name", name, 128, true),
        ], (_, _) => TraceCatalog.Instance.Events.Select(@event => new[]
        {
            SqlValue.FromInt16(@event.Id), SqlValue.FromInt16(@event.CategoryId), Text(name, @event.Name),
        }));

        Sys("trace_subclass_values",
        [
            new("trace_event_id", SqlType.SmallInt, null, false),
            new("trace_column_id", SqlType.SmallInt, null, false),
            new("subclass_name", name, 128, true),
            new("subclass_value", SqlType.SmallInt, null, true),
        ], (_, _) => TraceCatalog.Instance.SubclassValues.Select(value => new[]
        {
            SqlValue.FromInt16(value.EventId), SqlValue.FromInt16(value.ColumnId), Text(name, value.Name), SqlValue.FromInt16(value.Value),
        }));

        Sys("trace_event_bindings",
        [
            new("trace_event_id", SqlType.SmallInt, null, false),
            new("trace_column_id", SqlType.SmallInt, null, false),
        ], (_, _) => TraceCatalog.Instance.Bindings.Select(binding => new[]
        {
            SqlValue.FromInt16(binding.EventId), SqlValue.FromInt16(binding.ColumnId),
        }));

        Sys("trace_xe_event_map",
        [
            new("trace_event_id", SqlType.SmallInt, null, false),
            new("package_name", package, 60, false),
            new("xe_event_name", package, 60, false),
        ], (_, _) => TraceCatalog.Instance.XeEventMap.Select(row => new[]
        {
            SqlValue.FromInt16(row.EventId), Text(package, row.Package), Text(package, row.Name),
        }));

        Sys("trace_xe_action_map",
        [
            new("trace_column_id", SqlType.SmallInt, null, false),
            new("package_name", package, 60, false),
            new("xe_action_name", package, 60, false),
        ], (_, _) => TraceCatalog.Instance.XeActionMap.Select(row => new[]
        {
            SqlValue.FromInt16(row.ColumnId), Text(package, row.Package), Text(package, row.Name),
        }));
    }

    /// <summary>
    /// <c>sys.traces</c>' rows for the user traces, as real lists one: two
    /// buffers of a kilobyte, the file position in whole megabytes, and the
    /// start and last event times once it has them (probed 2026-10-16 against
    /// SQL Server 2025).
    /// </summary>
    private static IEnumerable<SqlValue[]> UserTraceRows(Simulation simulation)
    {
        if (simulation.TracesIfMade is not { } server)
            yield break;
        foreach (var trace in server.Traces())
        {
            yield return
            [
                SqlValue.FromInt32(trace.Id),
                SqlValue.FromInt32(trace.IsRunning ? 1 : 0),
                SqlValue.FromNVarchar(trace.CurrentPath),
                SqlValue.FromInt64(trace.MaxSizeMegabytes),
                trace.StopTime is { } stop ? SqlValue.FromDateTime(stop) : SqlValue.Null(SqlType.DateTime),
                SqlValue.FromInt32(trace.MaxFiles),
                SqlValue.FromBoolean(false),
                SqlValue.FromBoolean(trace.IsRollover),
                SqlValue.FromBoolean(trace.IsShutdown),
                SqlValue.FromBoolean(false),
                SqlValue.FromInt32(2),
                SqlValue.FromInt32(1024),
                SqlValue.FromInt64(trace.FilePosition),
                SqlValue.Null(SqlType.Int32),
                trace.StartTime is { } start ? SqlValue.FromDateTime(start) : SqlValue.Null(SqlType.DateTime),
                trace.LastEventTime is { } last ? SqlValue.FromDateTime(last) : SqlValue.Null(SqlType.DateTime),
                SqlValue.FromInt64(trace.EventCount),
                SqlValue.Null(SqlType.Int32),
            ];
        }
    }
}
