using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// <c>fn_trace_gettable(filename, number_files)</c>, bare, <c>sys.</c>- or
    /// <c>::</c>-prefixed: the events a trace file holds, in SQL Server 2025's
    /// 66-column shape (probed 2026-10-16). The one file there is to read is
    /// the default trace's, named by its own path or by its rollover set's
    /// base <c>log.trc</c> in the log directory, any case; it holds no events,
    /// since the simulator records none. A caller without <c>ALTER TRACE</c>
    /// is Msg 8189, an empty or NULL name Msg 19050 and any other file Msg
    /// 19049, all as the function runs.
    /// </summary>
    private static Selection ParseTraceGetTable(ParserContext context, string functionName) =>
        ParseSystemTvf(context, functionName, database: null, 2, TraceTableColumns, static (batch, args) =>
        {
            var connection = batch.Connection;
            if (!connection.Security.EffectiveIsDbo
                && !connection.Simulation.HoldsServerPermission(connection.Security.Effective.LoginName, Permission.AlterTrace))
            {
                throw SimulatedSqlException.TracePermissionDenied("FN_TRACE_GETTABLE", state: 15);
            }
            var path = args[0].IsNull ? "" : args[0].CoerceTo(SqlType.NVarchar).AsString;
            if (path.Length == 0)
                throw SimulatedSqlException.TraceFileNameInvalid(path);
            return string.Equals(path, BuiltInResources.DefaultTracePath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, ErrorLog.Directory + "log.trc", StringComparison.OrdinalIgnoreCase)
                ? []
                : throw SimulatedSqlException.TraceFileNotFound(path);
        });

    private static readonly (string Name, SqlType Type)[] TraceTableColumns = BuildTraceTableColumns();

    private static (string Name, SqlType Type)[] BuildTraceTableColumns()
    {
        var name = NVarcharSqlType.Get(256, Collation.Catalog, Coercibility.Implicit);
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
