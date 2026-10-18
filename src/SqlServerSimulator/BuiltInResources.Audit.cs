using SqlServerSimulator.Audit;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// SQL Server Audit's views: the audits and specifications the server and each
// database hold, the running audits' status, and real's own action and class
// catalog; see docs/claude/audit.md.
internal static partial class BuiltInResources
{
    private static void RegisterAuditViews(Dictionary<string, CatalogView> views)
    {
        void Sys(string name, HeapColumn[] columns, Func<Parser.BatchContext, Database, IEnumerable<SqlValue[]>> rows) =>
            views["sys." + name] = new CatalogView(name, columns, rows);

        var serverCollation = Collation.Get("SQL_Latin1_General_CP1_CI_AS");
        var predicate = NVarcharSqlType.Get(3000, serverCollation, Coercibility.Implicit);
        var path260 = NVarcharSqlType.Get(260, serverCollation, Coercibility.Implicit);
        var text60 = NVarcharSqlType.Get(60, serverCollation, Coercibility.Implicit);
        var text256 = NVarcharSqlType.Get(256, serverCollation, Coercibility.Implicit);
        var contextKeys = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, serverCollation, Coercibility.Implicit);
        var actionId = CharSqlType.Get(4, Collation.Catalog, Coercibility.Implicit);

        HeapColumn[] auditColumns =
        [
            new("audit_id", SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("audit_guid", SqlType.UniqueIdentifier, null, true),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("principal_id", SqlType.Int32, null, true),
            new("type", charTwo, 2, false),
            new("type_desc", nvarchar60Catalog, 60, true),
            new("on_failure", SqlType.TinyInt, null, true),
            new("on_failure_desc", nvarchar60Catalog, 60, true),
            new("is_state_enabled", SqlType.Bit, null, true),
            new("queue_delay", SqlType.Int32, null, true),
            new("predicate", predicate, 3000, true),
        ];
        SqlValue[] AuditRow(ServerAudit audit) =>
        [
            SqlValue.FromInt32(audit.Id),
            SqlValue.FromSystemName(audit.Name),
            SqlValue.FromGuid(audit.Guid),
            SqlValue.FromDateTime(audit.CreateDate),
            SqlValue.FromDateTime(audit.ModifyDate),
            SqlValue.FromInt32(audit.PrincipalId),
            SqlValue.FromChar(charTwo, audit.TypeCode),
            SqlValue.FromNVarchar(nvarchar60Catalog, audit.TypeDesc),
            SqlValue.FromByte(audit.OnFailure),
            SqlValue.FromNVarchar(nvarchar60Catalog, audit.OnFailureDesc),
            SqlValue.FromBoolean(audit.Running is not null),
            SqlValue.FromInt32(audit.QueueDelay),
            audit.PredicateText is { } text ? SqlValue.FromNVarchar(predicate, text) : SqlValue.Null(predicate),
        ];
        Sys("server_audits", [.. auditColumns, new("is_operator_audit", SqlType.Bit, null, true)],
            (batch, _) => VisibleAudits(batch).Select(audit => (SqlValue[])[.. AuditRow(audit), SqlValue.FromBoolean(false)]));
        Sys("server_file_audits",
        [
            .. auditColumns,
            new("max_file_size", SqlType.BigInt, null, true),
            new("max_rollover_files", SqlType.Int32, null, true),
            new("max_files", SqlType.Int32, null, true),
            new("reserve_disk_space", SqlType.Bit, null, true),
            new("log_file_path", path260, 260, true),
            new("log_file_name", path260, 260, true),
            new("retention_days", SqlType.Int32, null, true),
        ], (batch, _) => VisibleAudits(batch).Where(static audit => audit.Destination == AuditDestination.File).Select(audit => (SqlValue[])
        [
            .. AuditRow(audit),
            SqlValue.FromInt64(audit.MaxSize),
            SqlValue.FromInt32(audit.MaxRolloverFiles),
            SqlValue.FromInt32(audit.MaxFiles),
            SqlValue.FromBoolean(audit.ReserveDiskSpace),
            SqlValue.FromNVarchar(path260, audit.FilePath),
            SqlValue.FromNVarchar(path260, audit.LogFileName),
            SqlValue.FromInt32(0),
        ]));

        HeapColumn[] specificationColumns(string idColumn) =>
        [
            new(idColumn, SqlType.Int32, null, false),
            new("name", SqlType.SystemName, 128, false),
            new("create_date", SqlType.DateTime, null, false),
            new("modify_date", SqlType.DateTime, null, false),
            new("audit_guid", SqlType.UniqueIdentifier, null, true),
            new("is_state_enabled", SqlType.Bit, null, true),
            new("is_session_context_enabled", SqlType.Bit, null, true),
            new("session_context_keys", contextKeys, null, true),
        ];
        SqlValue[] SpecificationRow(AuditSpecification specification) =>
        [
            SqlValue.FromInt32(specification.Id),
            SqlValue.FromSystemName(specification.Name),
            SqlValue.FromDateTime(specification.CreateDate),
            SqlValue.FromDateTime(specification.ModifyDate),
            SqlValue.FromGuid(specification.AuditGuid),
            SqlValue.FromBoolean(specification.Enabled),
            SqlValue.FromBoolean(false),
            SqlValue.Null(contextKeys),
        ];
        HeapColumn[] detailColumns(string idColumn) =>
        [
            new(idColumn, SqlType.Int32, null, false),
            new("audit_action_id", actionId, 4, false),
            new("audit_action_name", text60, 60, true),
            new("class", SqlType.TinyInt, null, false),
            new("class_desc", nvarchar60Catalog, 60, true),
            new("major_id", SqlType.Int32, null, false),
            new("minor_id", SqlType.Int32, null, false),
            new("audited_principal_id", SqlType.Int32, null, false),
            new("audited_result", text60, 60, true),
            new("is_group", SqlType.Bit, null, true),
        ];
        SqlValue[] DetailRow(AuditSpecification specification, AuditSpecificationDetail detail) =>
        [
            SqlValue.FromInt32(specification.Id),
            SqlValue.FromChar(actionId, detail.Action.Id),
            SqlValue.FromNVarchar(text60, detail.Action.Name ?? ""),
            SqlValue.FromByte(detail.SecurableClass),
            SqlValue.FromNVarchar(nvarchar60Catalog, detail.ClassDesc),
            SqlValue.FromInt32(detail.MajorId),
            SqlValue.FromInt32(0),
            SqlValue.FromInt32(detail.PrincipalId),
            SqlValue.FromNVarchar(text60, "SUCCESS AND FAILURE"),
            SqlValue.FromBoolean(detail.Action.IsGroup),
        ];

        Sys("server_audit_specifications", specificationColumns("server_specification_id"),
            (batch, _) => VisibleServerSpecifications(batch).Select(SpecificationRow));
        // A server specification's rows list by specification, then action
        // id (probed 2026-10-17 against SQL Server 2025).
        Sys("server_audit_specification_details", detailColumns("server_specification_id"),
            (batch, _) => VisibleServerSpecifications(batch).SelectMany(specification =>
                specification.Details.OrderBy(static detail => detail.Action.Id, StringComparer.Ordinal).Select(detail => DetailRow(specification, detail))));
        Sys("database_audit_specifications", specificationColumns("database_specification_id"),
            (batch, database) => VisibleDatabaseSpecifications(batch, database).Select(SpecificationRow));
        // A database's rows list by securable — class, then id — then
        // principal and action id, the specifications interleaved (probed
        // 2026-10-17 against SQL Server 2025).
        Sys("database_audit_specification_details", detailColumns("database_specification_id"),
            (batch, database) => VisibleDatabaseSpecifications(batch, database)
                .SelectMany(static specification => specification.Details.Select(detail => (specification, detail)))
                .OrderBy(static pair => pair.detail.SecurableClass).ThenBy(static pair => pair.detail.MajorId).ThenBy(static pair => pair.detail.PrincipalId)
                .ThenBy(static pair => pair.detail.Action.Id, StringComparer.Ordinal).ThenBy(static pair => pair.specification.Id)
                .Select(pair => DetailRow(pair.specification, pair.detail)));

        Sys("dm_server_audit_status",
        [
            new("audit_id", SqlType.Int32, null, false),
            new("name", text256, 256, false),
            new("status", SqlType.SmallInt, null, false),
            new("status_desc", text256, 256, false),
            new("status_time", SqlType.GetDateTime2(7), null, false),
            new("event_session_address", SqlType.Varbinary, 8, true),
            new("audit_file_path", text256, 256, true),
            new("audit_file_size", SqlType.BigInt, null, true),
        ], (batch, _) => (batch.Connection.Simulation.RunningAudits ?? []).Select(run => new[]
        {
            SqlValue.FromInt32(run.Audit.Id),
            SqlValue.FromNVarchar(text256, run.Audit.Name),
            SqlValue.FromInt16(1),
            SqlValue.FromNVarchar(text256, "STARTED"),
            SqlValue.FromDateTime2(SqlType.GetDateTime2(7), run.StartTime),
            SqlValue.FromVarbinary(run.Address),
            run.File is { } file ? SqlValue.FromNVarchar(text256, file.Path) : SqlValue.Null(text256),
            run.File is { } sized ? SqlValue.FromInt64(sized.Size) : SqlValue.Null(SqlType.BigInt),
        }));

        var actionText = NVarcharSqlType.Get(128, Collation.Catalog, Coercibility.Implicit);
        var classText = NVarcharSqlType.Get(35, serverCollation, Coercibility.Implicit);
        var actionCode = VarcharSqlType.Get(4, serverCollation, Coercibility.Implicit);
        var classCode = VarcharSqlType.Get(2, serverCollation, Coercibility.Implicit);
        static SqlValue Text(NVarcharSqlType type, string? text) => text is null ? SqlValue.Null(type) : SqlValue.FromNVarchar(type, text);
        Sys("dm_audit_actions",
        [
            new("action_id", actionCode, 4, true),
            new("name", actionText, 128, true),
            new("class_desc", classText, 35, true),
            new("covering_action_name", actionText, 128, true),
            new("parent_class_desc", classText, 35, true),
            new("covering_parent_action_name", actionText, 128, true),
            new("configuration_level", actionText, 128, true),
            new("containing_group_name", actionText, 128, true),
            new("action_in_log", SqlType.Bit, null, false),
        ], (_, _) => AuditCatalog.Instance.Actions.Select(action => new[]
        {
            SqlValue.FromVarchar(actionCode, action.Id),
            Text(actionText, action.Name),
            Text(classText, action.ClassDesc),
            Text(actionText, action.CoveringActionName),
            Text(classText, action.ParentClassDesc),
            Text(actionText, action.CoveringParentActionName),
            Text(actionText, action.ConfigurationLevel),
            Text(actionText, action.ContainingGroupName),
            SqlValue.FromBoolean(action.ActionInLog),
        }));
        Sys("dm_audit_class_type_map",
        [
            new("class_type", classCode, 2, true),
            new("class_type_desc", classText, 35, true),
            new("securable_class_desc", classText, 35, true),
        ], (_, _) => AuditCatalog.Instance.ClassTypes.Select(row => new[]
        {
            row.ClassType is null ? SqlValue.Null(classCode) : SqlValue.FromVarchar(classCode, row.ClassType),
            Text(classText, row.ClassTypeDesc),
            Text(classText, row.SecurableClassDesc),
        }));
    }

    /// <summary>
    /// The gate of <c>sys.fn_get_audit_file</c>: a session without <c>VIEW
    /// SERVER SECURITY AUDIT</c> (which <c>CONTROL SERVER</c> covers) is
    /// refused with Msg 300 naming it, then Msg 297 (probed 2026-10-17
    /// against SQL Server 2025).
    /// </summary>
    internal static void DemandServerSecurityAudit(Parser.BatchContext batch)
    {
        var connection = batch.Connection;
        if (connection.Simulation.SessionHoldsServerPermission(connection, Permission.ViewServerSecurityAudit))
            return;
        throw SimulatedSqlException.Aggregate([
            SimulatedSqlException.ServerStatePermissionDenied("VIEW SERVER SECURITY AUDIT", Simulation.MasterDatabaseName),
            SimulatedSqlException.UserLacksPermissionToPerformAction(),
        ]);
    }

    /// <summary>
    /// Whether the session reads the server's audits and specifications:
    /// real lists them only to a login holding <c>ALTER ANY SERVER AUDIT</c>
    /// or <c>VIEW SERVER SECURITY AUDIT</c> (probed 2026-10-17 against SQL
    /// Server 2025), which <c>CONTROL SERVER</c> covers.
    /// </summary>
    private static bool SeesServerAudits(Parser.BatchContext batch)
    {
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        return simulation.SessionHoldsServerPermission(connection, Permission.AlterAnyServerAudit)
            || simulation.SessionHoldsServerPermission(connection, Permission.ViewServerSecurityAudit);
    }

    private static ServerAudit[] VisibleAudits(Parser.BatchContext batch) =>
        SeesServerAudits(batch) && batch.Connection.Simulation.AuditsIfMade is { } server ? server.Audits() : [];

    private static AuditSpecification[] VisibleServerSpecifications(Parser.BatchContext batch) =>
        SeesServerAudits(batch) && batch.Connection.Simulation.AuditsIfMade is { } server ? server.ServerSpecifications() : [];

    /// <summary>A database's specifications, listed to a session holding <c>ALTER ANY DATABASE AUDIT</c> there or reading the server's audits.</summary>
    private static AuditSpecification[] VisibleDatabaseSpecifications(Parser.BatchContext batch, Database database)
    {
        if (database.AuditSpecifications.IsEmptyLockFree())
            return [];
        if (!SeesServerAudits(batch)
            && !PermissionEnforcement.HoldsDatabasePermission(batch, database, "ALTER ANY DATABASE AUDIT")
            && !PermissionEnforcement.HoldsDatabasePermission(batch, database, "VIEW DATABASE SECURITY AUDIT"))
        {
            return [];
        }
        return [.. database.AuditSpecifications.ToArray().Select(static pair => pair.Value).OrderBy(static specification => specification.Id)];
    }
}
