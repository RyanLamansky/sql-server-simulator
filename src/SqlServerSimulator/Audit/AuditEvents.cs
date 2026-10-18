using System.Text;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Audit;

/// <summary>
/// What one audited action reports beyond the session's own fields: the
/// action and the class its group is looked up under, and the securable,
/// statement and extra information its record carries.
/// </summary>
internal sealed class AuditOccurrence(string actionId, string classDesc, string classType)
{
    /// <summary>The four-character action id, blanks kept (<c>SL  </c>).</summary>
    public readonly string ActionId = actionId;

    /// <summary>The class <c>sys.dm_audit_actions</c> lists the action under for its group (<c>OBJECT</c>, <c>DATABASE</c>, <c>LOGIN</c>…).</summary>
    public readonly string ClassDesc = classDesc;

    /// <summary>The record's two-character <c>class_type</c> (<c>U </c>, <c>DB</c>, <c>LX</c>), empty for NULL.</summary>
    public readonly string ClassType = classType;

    public bool Succeeded = true;

    public byte[]? PermissionBitmask;

    public bool IsColumnPermission;

    /// <summary>The database the action happens in: its specifications judge it, and the record names it.</summary>
    public Database? Database;

    /// <summary>The database name the record carries, when it isn't <see cref="Database"/>'s.</summary>
    public string? DatabaseName;

    public int ObjectId;

    /// <summary>The object's schema, for a database specification's action on that schema.</summary>
    public int SchemaId;

    public string SchemaName = "";

    public string ObjectName = "";

    public string Statement = "";

    public string AdditionalInformation = "";

    public int TargetServerPrincipalId;

    public string TargetServerPrincipalName = "";

    public byte[]? TargetServerPrincipalSid;

    public int TargetDatabasePrincipalId;

    public string TargetDatabasePrincipalName = "";

    public long TransactionId;

    public long DatabaseTransactionId;

    public short UserDefinedEventId;

    public string UserDefinedInformation = "";

    /// <summary>Whether a database specification's actions on securables may cover it — an object access.</summary>
    public bool ObjectAccess;

    /// <summary>A failed login's own principal fields: the name it tried, no session or principal ids.</summary>
    public string? FailedLoginName;

    /// <summary>Whether the record names no database principal — a login's or logout's.</summary>
    public bool ServerScope;
}

/// <summary>
/// Writes audit records: <c>SecAudit.audit_event</c>, the private Extended
/// Event SQL Server's audits run on, fired through the engine's own
/// <see cref="XeFiring"/> and judged by each audit's <c>WHERE</c> predicate
/// as an event session's predicate is, into every running audit whose
/// enabled specifications cover the action. A site asks
/// <see cref="Simulation.RunningAudits"/> first — one field read, null while
/// no audit runs — so a server running none pays nothing more.
/// </summary>
internal static class AuditEvents
{
    /// <summary>The <c>audit_event</c> event, resolved against the Extended Events catalog on first use.</summary>
    public static XeObject Event => Holder.Value;

    private static class Holder
    {
        public static readonly XeObject Value = XeMetadata.Instance.Find("SecAudit", "audit_event", "event")!;
    }

    /// <summary>The <c>action_id</c> field's value: the four characters, the first in the low byte.</summary>
    public static long PackActionId(string actionId)
    {
        long value = 0;
        for (var i = 3; i >= 0; i--)
            value = (value << 8) | (i < actionId.Length ? (byte)actionId[i] : (byte)' ');
        return (int)value;
    }

    /// <summary>The <c>class_type</c> field's value: the two characters, the first in the low byte; 0 for none.</summary>
    public static long PackClassType(string classType) =>
        classType.Length == 0 ? 0 : (short)(classType[0] | ((classType.Length > 1 ? classType[1] : ' ') << 8));

    /// <summary>The four characters <see cref="PackActionId"/> packed.</summary>
    public static string UnpackActionId(long value)
    {
        Span<char> text = stackalloc char[4];
        for (var i = 0; i < 4; i++)
            text[i] = (char)((value >> (8 * i)) & 0xFF);
        return new string(text);
    }

    /// <summary>The two characters <see cref="PackClassType"/> packed, null for none.</summary>
    public static string? UnpackClassType(long value) =>
        value == 0 ? null : new string([(char)(value & 0xFF), (char)((value >> 8) & 0xFF)]);

    /// <summary>
    /// An <c>AUSC</c> record, which an audit writes to itself as it starts and
    /// stops whatever its specifications name, its predicate still judging it
    /// (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    public static void SessionChanged(SimulatedDbConnection connection, RunningAudit running, string action)
    {
        var occurrence = new AuditOccurrence("AUSC", "SERVER AUDIT", "A ")
        {
            ServerScope = true,
            AdditionalInformation = "<action_info xmlns=\"http://schemas.microsoft.com/sqlserver/2008/sqlaudit_data\"><session><![CDATA[" + running.Audit.Name + "$A]]></session>" + action + "</action_info>",
        };
        var firing = new XeFiring(Event, Data(connection, occurrence, DateTime.UtcNow), connection, DateTime.UtcNow);
        Deliver(running, firing);
    }

    /// <summary>
    /// Writes <paramref name="occurrence"/> to every running audit that one
    /// of its enabled specifications points at and that covers the action: a
    /// server specification by the action's group, a database specification
    /// of the action's database by its group or — for an object access — by
    /// the action on the object, its schema or the database for a principal
    /// the session's user is or is a member of. An audit writes it once
    /// however many of its specifications cover it.
    /// </summary>
    public static void Write(SimulatedDbConnection connection, AuditOccurrence occurrence)
    {
        var simulation = connection.Simulation;
        if (simulation.RunningAudits is not { } running)
            return;
        var group = AuditCatalog.Instance.GroupOf(occurrence.ActionId, occurrence.ClassDesc);
        XeFiring? firing = null;
        foreach (var run in running)
        {
            if (!Covers(simulation, run.Audit.Guid, occurrence, group, connection))
                continue;
            firing ??= new XeFiring(Event, Data(connection, occurrence, DateTime.UtcNow), connection, DateTime.UtcNow);
            Deliver(run, firing);
        }
    }

    private static bool Covers(Simulation simulation, Guid audit, AuditOccurrence occurrence, string? group, SimulatedDbConnection connection)
    {
        if (group is not null && simulation.AuditsIfMade?.ServerSpecificationFor(audit) is { Enabled: true } serverSpecification)
        {
            foreach (var detail in serverSpecification.Details)
            {
                if (detail.Action.IsGroup && detail.Action.Name == group)
                    return true;
            }
        }
        if (occurrence.Database is not { } database)
            return false;
        int? principal = null;
        foreach (var (_, specification) in database.AuditSpecifications)
        {
            if (!specification.Enabled || specification.AuditGuid != audit)
                continue;
            foreach (var detail in specification.Details)
            {
                if (detail.Action.IsGroup)
                {
                    if (detail.Action.Name == group)
                        return true;
                    continue;
                }
                if (!occurrence.ObjectAccess || detail.Action.Id != occurrence.ActionId)
                    continue;
                var onSecurable = detail.SecurableClass switch
                {
                    1 => detail.MajorId == occurrence.ObjectId,
                    3 => detail.MajorId == occurrence.SchemaId,
                    _ => true,
                };
                if (!onSecurable)
                    continue;
                principal ??= DatabasePrincipal(connection, database).Id;
                if (detail.PrincipalId == global::SqlServerSimulator.Database.PublicPrincipalId || detail.PrincipalId == principal
                    || (PrincipalById(database, detail.PrincipalId) is { } role && PermissionChecker.IsRoleMember(database, principal.Value, role)))
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static DatabasePrincipal? PrincipalById(Database database, int id)
    {
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.PrincipalId == id)
                return principal;
        }
        return null;
    }

    /// <summary>Judges <paramref name="firing"/> by the audit's predicate and writes it to the audit's file.</summary>
    private static void Deliver(RunningAudit run, XeFiring firing)
    {
        if (run.Audit.Predicate is { } predicate && !predicate.Evaluate(firing))
            return;
        if (run.File is not { } file)
            return;
        var record = new AuditRecord(firing.Data, firing.Timestamp);
        lock (file.Gate)
        {
            file.Records.Add(record);
            file.RecordBytes += EstimatedSize(record);
        }
    }

    /// <summary>A record's bytes at the simulator's estimate of real's encoding: some 300 bytes of fixed fields, then two a character of its text.</summary>
    private static long EstimatedSize(AuditRecord record)
    {
        long size = 300;
        foreach (var value in record.Data)
        {
            if (value.Family == XeTypeFamily.UnicodeString && value.Text is { } text)
                size += 2 * text.Length;
        }
        return size;
    }

    /// <summary>The database principal the session acts as in <paramref name="database"/>: its effective one there, else the user its login maps to, else none.</summary>
    private static (int Id, string Name) DatabasePrincipal(SimulatedDbConnection connection, Database database)
    {
        var effective = connection.Security.Effective;
        if (ReferenceEquals(database, connection.CurrentDatabase) || (effective.DatabasePrincipalId == global::SqlServerSimulator.Database.DboPrincipalId && !effective.IsDatabaseScoped))
            return (effective.DatabasePrincipalId, effective.DatabasePrincipalName);
        return PermissionEnforcement.TryResolveCrossDatabasePrincipal(connection, database, out var principal)
            ? (principal.PrincipalId, principal.Name)
            : (0, "");
    }

    /// <summary>The server principal id of <paramref name="loginName"/>: 1 for <c>sa</c>, a registered login's own, else 0.</summary>
    private static int ServerPrincipalId(Simulation simulation, string loginName) =>
        BuiltInToken.Comparer.Equals(loginName, "sa") ? 1
        : simulation.Logins.TryGetValue(loginName, out var login) ? login.PrincipalId
        : 0;

    /// <summary>The record's <c>audit_event</c> field values: the occurrence's own, then the session's.</summary>
    private static XeValue[] Data(SimulatedDbConnection connection, AuditOccurrence occurrence, DateTime utcNow)
    {
        var simulation = connection.Simulation;
        var security = connection.Security;
        var failed = occurrence.FailedLoginName;
        var loginName = failed ?? security.Effective.LoginName;
        var sessionLogin = failed is null ? security.OriginalLoginName : "";
        var (databasePrincipalId, databasePrincipalName) = occurrence.ServerScope || failed is not null || occurrence.Database is not { } database
            ? (0, "")
            : DatabasePrincipal(connection, database);
        var transport = connection.Transport;
        var client = transport.Client is { } endpoint
            ? (endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address).ToString()
            : Simulation.LocalMachineClient.Trim('<', '>');
        return XeEvents.Data(Event,
            ("audit_schema_version", XeValue.FromSigned(1)),
            ("sequence_number", XeValue.FromSigned(1)),
            ("action_id", XeValue.FromSigned(PackActionId(occurrence.ActionId))),
            ("succeeded", XeValue.FromBoolean(occurrence.Succeeded)),
            ("is_column_permission", XeValue.FromBoolean(occurrence.IsColumnPermission)),
            ("session_id", XeValue.FromSigned(failed is null ? connection.Spid : 0)),
            ("server_principal_id", XeValue.FromSigned(failed is null ? ServerPrincipalId(simulation, loginName) : 0)),
            ("database_principal_id", XeValue.FromSigned(databasePrincipalId)),
            ("target_server_principal_id", XeValue.FromSigned(occurrence.TargetServerPrincipalId)),
            ("target_database_principal_id", XeValue.FromSigned(occurrence.TargetDatabasePrincipalId)),
            ("object_id", XeValue.FromSigned(occurrence.ObjectId)),
            ("transaction_id", XeValue.FromSigned(occurrence.TransactionId)),
            ("class_type", XeValue.FromSigned(PackClassType(occurrence.ClassType))),
            ("database_transaction_id", XeValue.FromUnsigned((ulong)occurrence.DatabaseTransactionId)),
            ("client_ip", XeValue.FromUnicode(client)),
            ("permission_bitmask", XeValue.FromBinary(occurrence.PermissionBitmask ?? new byte[16])),
            ("sequence_group_id", XeValue.FromGuid(Guid.NewGuid())),
            ("session_server_principal_name", XeValue.FromUnicode(sessionLogin)),
            ("server_principal_name", XeValue.FromUnicode(loginName)),
            ("server_principal_sid", XeValue.FromBinary(failed is null ? Simulation.LoginSid(simulation, loginName) : [])),
            ("database_principal_name", XeValue.FromUnicode(databasePrincipalName)),
            ("target_server_principal_name", XeValue.FromUnicode(occurrence.TargetServerPrincipalName)),
            ("target_server_principal_sid", XeValue.FromBinary(occurrence.TargetServerPrincipalSid ?? [])),
            ("target_database_principal_name", XeValue.FromUnicode(occurrence.TargetDatabasePrincipalName)),
            ("server_instance_name", XeValue.FromUnicode(SimulatedDbConnection.DataSourceName)),
            ("database_name", XeValue.FromUnicode(occurrence.DatabaseName ?? occurrence.Database?.Name ?? "")),
            ("schema_name", XeValue.FromUnicode(occurrence.SchemaName)),
            ("object_name", XeValue.FromUnicode(occurrence.ObjectName)),
            ("statement", XeValue.FromUnicode(occurrence.Statement)),
            ("additional_information", XeValue.FromUnicode(occurrence.AdditionalInformation)),
            ("user_defined_event_id", XeValue.FromSigned(occurrence.UserDefinedEventId)),
            ("user_defined_information", XeValue.FromUnicode(occurrence.UserDefinedInformation)),
            ("application_name", XeValue.FromUnicode(connection.ClientApplicationName)),
            ("connection_id", XeValue.FromGuid(transport.ConnectionId)),
            ("host_name", XeValue.FromUnicode(connection.ClientHostName)),
            ("event_time", XeValue.FromUnsigned((ulong)utcNow.ToFileTimeUtc())));
    }

    /// <summary>
    /// The text of the statement <paramref name="batch"/> runs, as the
    /// statement events place it — a module body's own statement inside one —
    /// or empty for one they place none for.
    /// </summary>
    public static string StatementText(BatchContext batch) =>
        XeStatementEvents.Place(batch, batch.CurrentStatement.StartIndex)?.Text ?? "";

    /// <summary>
    /// <c>&lt;tsql_stack&gt;</c> naming each module the statement runs inside,
    /// which an object access from a module body carries as its
    /// <c>additional_information</c> (probed 2026-10-17 against SQL Server
    /// 2025: <c>&lt;frame nest_level = '1' database_name = 'db' schema_name =
    /// 'dbo' object_name = 'p'/&gt;</c>); empty outside one.
    /// </summary>
    public static string ModuleStack(BatchContext batch)
    {
        StringBuilder? builder = null;
        var level = batch.Connection.NestingLevel;
        for (var frame = batch; frame is not null && frame.ModuleObjectId != 0; frame = frame.CallerBatch)
        {
            var database = frame.ModuleSchema?.Database ?? frame.CurrentDatabase;
            if (Parser.Expressions.ObjectProperty.FindObject(database, frame.ModuleObjectId) is not { } module)
                continue;
            builder ??= new StringBuilder("<tsql_stack>");
            _ = builder.Append("<frame nest_level = '").Append(Math.Max(1, level--)).Append("' database_name = '").Append(database.Name)
                .Append("' schema_name = '").Append(PermissionEnforcement.SchemaNameFor(database, module.SchemaId)).Append("' object_name = '").Append(module.Name).Append("'/>");
        }
        return builder?.Append("</tsql_stack>").ToString() ?? "";
    }
}
