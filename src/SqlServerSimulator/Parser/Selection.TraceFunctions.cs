using SqlServerSimulator.Storage;
using SqlServerSimulator.Trace;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// <c>fn_trace_gettable(filename, number_files)</c>, bare, <c>sys.</c>- or
    /// <c>::</c>-prefixed: the events a trace file holds, in SQL Server 2025's
    /// 66-column shape (probed 2026-10-16). A user trace's file reads with the
    /// rollover files after it — <paramref name="functionName"/>'s second
    /// argument caps how many, all of them when it is NULL or below one — each
    /// opening with real's header row and the trace's start rows among its
    /// events. The default trace's file, named by its own path or by its
    /// rollover set's base <c>log.trc</c> in the log directory, any case,
    /// holds no events. Without <c>ALTER TRACE</c> the function is Msg 8189,
    /// an empty or NULL name Msg 19050 and any other file Msg 19049, all as
    /// the function runs.
    /// </summary>
    private static Selection ParseTraceGetTable(ParserContext context, string functionName) =>
        ParseSystemTvf(context, functionName, database: null, 2, TraceTableColumns, static (batch, args) =>
        {
            RequireAlterTrace(batch, "FN_TRACE_GETTABLE", 15);
            var path = args[0].IsNull ? "" : args[0].CoerceTo(SqlType.NVarchar).AsString;
            if (path.Length == 0)
                throw SimulatedSqlException.TraceFileNameInvalid(path);
            if (string.Equals(path, BuiltInResources.DefaultTracePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, ErrorLog.Directory + "log.trc", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }
            var files = batch.Connection.Simulation.TracesIfMade?.Files;
            if (files is null || !files.TryGetValue(path, out var file))
                throw SimulatedSqlException.TraceFileNotFound(path);
            var limit = args[1].IsNull ? 0 : args[1].CoerceTo(SqlType.Int32).AsInt32;
            var rows = new List<SqlValue[]>();
            for (var read = 1; ; read++)
            {
                foreach (var record in file.Records())
                    rows.Add(TraceTableRow(record));
                if (read == limit || file.Next is not { } next || !files.TryGetValue(next, out file))
                    break;
            }
            return rows;
        });

    /// <summary>One trace row in <see cref="TraceTableColumns"/>' shape, each column converted to the function's type for it.</summary>
    private static SqlValue[] TraceTableRow(TraceRecord record)
    {
        var row = new SqlValue[TraceTableColumns.Length];
        for (var i = 0; i < row.Length; i++)
            row[i] = SqlValue.Null(TraceTableColumns[i].Type);
        foreach (var (columnId, value) in record.Columns)
        {
            var at = TraceTableIndex[columnId];
            row[at] = value.CoerceTo(TraceTableColumns[at].Type);
        }
        row[TraceTableIndex[27]] = SqlValue.FromInt32(record.EventClass);
        return row;
    }

    /// <summary>Msg 8189 at <paramref name="state"/>, naming <paramref name="surface"/>, unless the session holds <c>ALTER TRACE</c>.</summary>
    private static void RequireAlterTrace(BatchContext batch, string surface, byte state)
    {
        var connection = batch.Connection;
        if (!connection.Simulation.SessionHoldsServerPermission(connection, Permission.AlterTrace))
            throw SimulatedSqlException.TracePermissionDenied(surface, state);
    }

    /// <summary>
    /// <c>fn_trace_getinfo(traceid)</c>: five rows a trace — its options,
    /// file, size limit, stop time and status — for the trace named, or for
    /// every trace, the default one first, under 0, NULL or <c>DEFAULT</c>
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static Selection ParseTraceGetInfo(ParserContext context, string functionName) =>
        ParseSystemTvf(context, functionName, database: null, 1, TraceInfoColumns, static (batch, args) =>
        {
            RequireAlterTrace(batch, "FN_TRACE_GETINFO", 12);
            var id = args[0].IsNull ? 0 : args[0].CoerceTo(SqlType.Int32).AsInt32;
            var simulation = batch.Connection.Simulation;
            var rows = new List<SqlValue[]>();
            if ((id is 0 or 1) && simulation.DefaultTraceEnabled)
            {
                Add(1, SqlValue.FromInt32(2), BuiltInResources.DefaultTracePath, SqlValue.FromInt64(20), SqlValue.Null(SqlType.DateTime), 1);
            }
            foreach (var trace in simulation.TracesIfMade?.Traces() ?? [])
            {
                if (id == 0 || id == trace.Id)
                {
                    Add(trace.Id, SqlValue.FromInt32(trace.Options), trace.CurrentPath, SqlValue.FromInt64(trace.MaxSizeMegabytes),
                        trace.StopTime is { } stop ? SqlValue.FromDateTime(stop) : SqlValue.Null(SqlType.DateTime), trace.IsRunning ? 1 : 0);
                }
            }
            return rows;

            // The path is an nchar its own length, as real's variant carries it.
            void Add(int traceId, SqlValue options, string path, SqlValue maxSize, SqlValue stopTime, int status)
            {
                rows.Add([SqlValue.FromInt32(traceId), SqlValue.FromInt32(1), SqlValue.FromVariant(options)]);
                rows.Add([SqlValue.FromInt32(traceId), SqlValue.FromInt32(2), SqlValue.FromVariant(SqlValue.FromNChar(NCharSqlType.Get(path.Length, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit), path))]);
                rows.Add([SqlValue.FromInt32(traceId), SqlValue.FromInt32(3), SqlValue.FromVariant(maxSize)]);
                rows.Add([SqlValue.FromInt32(traceId), SqlValue.FromInt32(4), stopTime.IsNull ? SqlValue.Null(SqlType.SqlVariant) : SqlValue.FromVariant(stopTime)]);
                rows.Add([SqlValue.FromInt32(traceId), SqlValue.FromInt32(5), SqlValue.FromVariant(SqlValue.FromInt32(status))]);
            }
        }, notNullColumns: 2, arityState: 3);

    /// <summary>
    /// <c>fn_trace_geteventinfo(traceid)</c>: each event the trace takes with
    /// each of its columns, by event and then column; the default trace's are
    /// real's own (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static Selection ParseTraceGetEventInfo(ParserContext context, string functionName) =>
        ParseSystemTvf(context, functionName, database: null, 1, TraceEventInfoColumns, static (batch, args) =>
        {
            RequireAlterTrace(batch, "FN_TRACE_GETEVENTINFO", 13);
            var id = args[0].IsNull ? 0 : args[0].CoerceTo(SqlType.Int32).AsInt32;
            var simulation = batch.Connection.Simulation;
            IEnumerable<(int EventId, int ColumnId)> pairs = id == 1
                ? simulation.DefaultTraceEnabled ? TraceCatalog.Instance.DefaultTraceEvents : []
                : simulation.TracesIfMade?.Find(id)?.EventColumns() ?? [];
            return [.. pairs.Select(pair => new[] { SqlValue.FromInt32(pair.EventId), SqlValue.FromInt32(pair.ColumnId) })];
        }, notNullColumns: 2, arityState: 3);

    /// <summary>
    /// <c>fn_trace_getfilterinfo(traceid)</c>: the trace's filters in the
    /// order they were set, each value as the variant real gives it — a
    /// string its own length — save a GUID, which real reads back as NULL
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static Selection ParseTraceGetFilterInfo(ParserContext context, string functionName) =>
        ParseSystemTvf(context, functionName, database: null, 1, TraceFilterInfoColumns, static (batch, args) =>
        {
            RequireAlterTrace(batch, "FN_TRACE_GETFILTERINFO", 14);
            var id = args[0].IsNull ? 0 : args[0].CoerceTo(SqlType.Int32).AsInt32;
            var trace = id == 1 ? null : batch.Connection.Simulation.TracesIfMade?.Find(id);
            return [.. (trace?.Filters() ?? []).Select(filter => new[]
            {
                SqlValue.FromInt32(filter.ColumnId), SqlValue.FromInt32(filter.LogicalOperator), SqlValue.FromInt32(filter.ComparisonOperator),
                filter.Value.IsNull || filter.Value.Type is UniqueIdentifierSqlType
                    ? SqlValue.Null(SqlType.SqlVariant)
                    : SqlValue.FromVariant(filter.Value.Type is NVarcharSqlType
                        ? SqlValue.FromNVarchar(NVarcharSqlType.Get(Math.Max(1, filter.Value.AsString.Length), Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit), filter.Value.AsString)
                        : filter.Value),
            })];
        }, notNullColumns: 3, arityState: 3);

    private static readonly (string Name, SqlType Type)[] TraceInfoColumns =
        [("traceid", SqlType.Int32), ("property", SqlType.Int32), ("value", SqlType.SqlVariant)];

    private static readonly (string Name, SqlType Type)[] TraceEventInfoColumns =
        [("eventid", SqlType.Int32), ("columnid", SqlType.Int32)];

    private static readonly (string Name, SqlType Type)[] TraceFilterInfoColumns =
        [("columnid", SqlType.Int32), ("logical_operator", SqlType.Int32), ("comparison_operator", SqlType.Int32), ("value", SqlType.SqlVariant)];

    private static readonly (string Name, SqlType Type)[] TraceTableColumns = BuildTraceTableColumns();

    /// <summary>Per trace column id, its position in <see cref="TraceTableColumns"/>.</summary>
    private static readonly int[] TraceTableIndex = BuildTraceTableIndex();

    private static int[] BuildTraceTableIndex()
    {
        var index = new int[TraceCatalog.MaxColumnId + 1];
        for (var i = 0; i < TraceTableColumns.Length; i++)
        {
            foreach (var column in TraceCatalog.Instance.Columns)
            {
                if (column.Name == TraceTableColumns[i].Name)
                    index[column.Id] = i;
            }
        }
        return index;
    }

    private static (string Name, SqlType Type)[] BuildTraceTableColumns()
    {
        var name = NVarcharSqlType.Get(256, Collation.Get("SQL_Latin1_General_CP1_CI_AS"), Coercibility.Implicit);
        return
        [
            ("TextData", SqlType.NText), ("BinaryData", SqlType.Image), ("DatabaseID", SqlType.Int32), ("TransactionID", SqlType.BigInt),
            ("LineNumber", SqlType.Int32), ("NTUserName", name), ("NTDomainName", name), ("HostName", name), ("ClientProcessID", SqlType.Int32),
            ("ApplicationName", name), ("LoginName", name), ("SPID", SqlType.Int32), ("Duration", SqlType.BigInt), ("StartTime", SqlType.DateTime),
            ("EndTime", SqlType.DateTime), ("Reads", SqlType.BigInt), ("Writes", SqlType.BigInt), ("CPU", SqlType.Int32), ("Permissions", SqlType.BigInt),
            ("Severity", SqlType.Int32), ("EventSubClass", SqlType.Int32), ("ObjectID", SqlType.Int32), ("Success", SqlType.Int32),
            ("IndexID", SqlType.Int32), ("IntegerData", SqlType.Int32), ("ServerName", name), ("EventClass", SqlType.Int32),
            ("ObjectType", SqlType.Int32), ("NestLevel", SqlType.Int32), ("State", SqlType.Int32), ("Error", SqlType.Int32), ("Mode", SqlType.Int32),
            ("Handle", SqlType.Int32), ("ObjectName", name), ("DatabaseName", name), ("FileName", name), ("OwnerName", name), ("RoleName", name),
            ("TargetUserName", name), ("DBUserName", name), ("LoginSid", SqlType.Image), ("TargetLoginName", name), ("TargetLoginSid", SqlType.Image),
            ("ColumnPermissions", SqlType.Int32), ("LinkedServerName", name), ("ProviderName", name), ("MethodName", name), ("RowCounts", SqlType.BigInt),
            ("RequestID", SqlType.Int32), ("XactSequence", SqlType.BigInt), ("EventSequence", SqlType.BigInt), ("BigintData1", SqlType.BigInt),
            ("BigintData2", SqlType.BigInt), ("GUID", SqlType.UniqueIdentifier), ("IntegerData2", SqlType.Int32), ("ObjectID2", SqlType.BigInt),
            ("Type", SqlType.Int32), ("OwnerID", SqlType.Int32), ("ParentName", name), ("IsSystem", SqlType.Int32), ("Offset", SqlType.Int32),
            ("SourceDatabaseID", SqlType.Int32), ("SqlHandle", SqlType.Image), ("SessionLoginName", name), ("PlanHandle", SqlType.Image),
            ("GroupID", SqlType.Int32),
        ];
    }
}
