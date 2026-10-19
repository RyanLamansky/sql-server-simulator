using System.Globalization;
using SqlServerSimulator.ExtendedEvents;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;

namespace SqlServerSimulator.Audit;

/// <summary>
/// The audited actions the engine's own seams report, each the record SQL
/// Server 2025 writes for it (probed 2026-10-17): a login, a failed login and
/// a logout, an access to a schema object, a schema object's change, a
/// permission's change, a <c>DBCC</c>, <c>BACKUP</c> or <c>RESTORE</c>, and
/// <c>sp_audit_write</c>'s record. Every one asks
/// <see cref="Simulation.RunningAudits"/> first, so a server auditing nothing
/// pays one field read.
/// </summary>
internal static class AuditSites
{
    private const string ActionInfo = "<action_info xmlns=\"http://schemas.microsoft.com/sqlserver/2008/sqlaudit_data\">";

    /// <summary><c>LGIS</c>: class <c>LX</c>, the session's options as the statement and its connection in <c>additional_information</c>.</summary>
    public static void Login(SimulatedDbConnection connection, bool pooled)
    {
        if (connection.Simulation.RunningAudits is null)
            return;
        AuditEvents.Write(connection, new AuditOccurrence("LGIS", "LOGIN", "LX")
        {
            ServerScope = true,
            Statement = XeSessionEvents.OptionsText(connection),
            AdditionalInformation = ActionInfo + "<pooled_connection>" + (pooled ? "1" : "0") + "</pooled_connection><client_options>0x28000020</client_options><client_options1>0x0001f438</client_options1><connect_options>0x00000000</connect_options><packet_data_size>"
                + (connection.Transport.PacketSize == 0 ? 4096 : connection.Transport.PacketSize).ToString(CultureInfo.InvariantCulture) + "</packet_data_size><address>" + ClientAddress(connection) + "</address><is_dac>0</is_dac></action_info>",
        });
    }

    /// <summary><c>LGO</c> as a session that logged in ends; its CPU and I/O counters read zero.</summary>
    public static void Logout(SimulatedDbConnection connection)
    {
        if (connection.Simulation.RunningAudits is null)
            return;
        AuditEvents.Write(connection, new AuditOccurrence("LGO ", "LOGIN", "LX")
        {
            ServerScope = true,
            AdditionalInformation = ActionInfo + "<pooled_connection>0</pooled_connection><total_cpu>0</total_cpu><reads>0</reads><writes>0</writes><is_dac>0</is_dac></action_info>",
        });
    }

    /// <summary>
    /// <c>LGIF</c> for a refused login: no session or principal ids, the
    /// name it tried as <c>server_principal_name</c>, the error-log line as
    /// its statement, and the error and state it was refused with.
    /// </summary>
    public static void LoginFailed(Simulation simulation, string userName, int number, byte state, string message, string client)
    {
        if (simulation.RunningAudits is null)
            return;
        using var standIn = simulation.CreateDbConnection();
        AuditEvents.Write(standIn, new AuditOccurrence("LGIF", "LOGIN", "LX")
        {
            Succeeded = false,
            FailedLoginName = userName,
            Statement = message,
            AdditionalInformation = ActionInfo + "<pooled_connection>0</pooled_connection><error>0x" + number.ToString("x8", CultureInfo.InvariantCulture)
                + "</error><state>" + state.ToString(CultureInfo.InvariantCulture) + "</state><address>" + client.Trim('<', '>') + "</address></action_info>",
        });
    }

    private static string ClientAddress(SimulatedDbConnection connection) =>
        connection.Transport.Client is { } endpoint
            ? (endpoint.Address.IsIPv4MappedToIPv6 ? endpoint.Address.MapToIPv4() : endpoint.Address).ToString()
            : Simulation.LocalMachineClient.Trim('<', '>');

    /// <summary>The action id an object permission's access is audited under, null for one no action reports.</summary>
    private static string? AccessAction(string permission)
    {
        if (permission.Length > 10)
            return null;
        Span<char> upper = stackalloc char[permission.Length];
        _ = permission.AsSpan().ToUpperInvariant(upper);
        return upper switch
        {
            "DELETE" => "DL  ",
            "EXECUTE" => "EX  ",
            "INSERT" => "IN  ",
            "RECEIVE" => "RC  ",
            "REFERENCES" => "RF  ",
            "SELECT" => "SL  ",
            "UPDATE" => "UP  ",
            _ => null,
        };
    }

    /// <summary>
    /// An access to a schema object under <paramref name="permission"/> —
    /// <c>SL</c>, <c>IN</c>, <c>UP</c>, <c>DL</c>, <c>EX</c>, <c>RF</c> or
    /// <c>RC</c> — once per statement and object, whether or not an ownership
    /// chain spares the permission check, <paramref name="succeeded"/> false
    /// for one the check refused. A module body's access carries its module
    /// stack (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    public static void ObjectAccess(BatchContext batch, Database database, string permission, int objectId, int schemaId, string objectName, string schemaName, SchemaObject? securable, bool succeeded)
    {
        var connection = batch.Connection;
        if (connection.Simulation.RunningAudits is null || batch.IsSkipping || AccessAction(permission) is not { } action)
            return;
        var key = ((long)database.Id << 48) | ((long)(uint)objectId << 8) | action[0];
        if (!(batch.CurrentStatement.AuditedAccesses ??= []).Add(key ^ (succeeded ? 0 : 1L << 62)))
            return;
        var classType = (securable ?? Parser.Expressions.ObjectProperty.FindObject(database, objectId))?.ObjectTypeCode ?? "U ";
        AuditEvents.Write(connection, new AuditOccurrence(action, "OBJECT", classType)
        {
            Succeeded = succeeded,
            PermissionBitmask = AuditCatalog.Instance.PermissionBitmask("OBJECT", permission.ToUpperInvariant()),
            IsColumnPermission = action is "SL  " or "UP  " or "RF  ",
            Database = database,
            ObjectId = objectId,
            SchemaId = schemaId,
            SchemaName = schemaName,
            ObjectName = objectName,
            ObjectAccess = true,
            Statement = AuditEvents.StatementText(batch),
            AdditionalInformation = AuditEvents.ModuleStack(batch),
            TransactionId = batch.CurrentTransactionId(),
        });
    }

    /// <summary>
    /// A write to a statement's target: its <c>SELECT</c> when it reads the
    /// target's columns (a <c>WHERE</c>), then the write itself, as real
    /// records an <c>UPDATE … WHERE</c> (probed 2026-10-17 against SQL Server
    /// 2025).
    /// </summary>
    public static void TargetWrite(BatchContext batch, SchemaObject target, string verb, bool reads, bool succeeded)
    {
        var database = batch.DatabaseFor(target);
        var schemaName = PermissionEnforcement.SchemaNameFor(database, target.SchemaId);
        if (reads)
            ObjectAccess(batch, database, "SELECT", target.ObjectId, target.SchemaId, target.Name, schemaName, target, succeeded);
        ObjectAccess(batch, database, verb, target.ObjectId, target.SchemaId, target.Name, schemaName, target, succeeded);
    }

    /// <summary>
    /// <c>CR</c>, <c>AL</c> or <c>DR</c> of a schema object, class its type
    /// (<c>SCHEMA_OBJECT_CHANGE_GROUP</c>); a creation is preceded by its
    /// schema's <c>AL</c> (<c>DATABASE_OBJECT_CHANGE_GROUP</c>), as real
    /// records <c>CREATE TABLE</c>, <c>VIEW</c> and <c>PROCEDURE</c> (probed
    /// 2026-10-17 against SQL Server 2025).
    /// </summary>
    public static void ObjectChanged(BatchContext batch, XeEventKind kind, Database database, SchemaObject changed)
    {
        var connection = batch.Connection;
        if (connection.Simulation.RunningAudits is null || batch.IsSkipping)
            return;
        var schemaName = PermissionEnforcement.SchemaNameFor(database, changed.SchemaId);
        var statement = AuditEvents.StatementText(batch);
        var transactionId = batch.CurrentTransactionId();
        if (kind == XeEventKind.ObjectCreated)
        {
            AuditEvents.Write(connection, new AuditOccurrence("AL  ", "SCHEMA", "SC")
            {
                Database = database,
                ObjectId = changed.SchemaId,
                ObjectName = schemaName,
                Statement = statement,
                TransactionId = transactionId,
            });
        }
        AuditEvents.Write(connection, new AuditOccurrence(ChangeAction(kind), "OBJECT", changed.ObjectTypeCode)
        {
            Database = database,
            ObjectId = changed.ObjectId,
            SchemaId = changed.SchemaId,
            SchemaName = schemaName,
            ObjectName = changed.Name,
            Statement = statement,
            TransactionId = transactionId,
        });
    }

    /// <summary><c>CR</c>, <c>AL</c> or <c>DR</c> of a schema, class <c>SC</c> (<c>DATABASE_OBJECT_CHANGE_GROUP</c>).</summary>
    public static void SchemaChanged(BatchContext batch, XeEventKind kind, Database database, int schemaId, string name)
    {
        var connection = batch.Connection;
        if (connection.Simulation.RunningAudits is null || batch.IsSkipping)
            return;
        AuditEvents.Write(connection, new AuditOccurrence(ChangeAction(kind), "SCHEMA", "SC")
        {
            Database = database,
            ObjectId = schemaId,
            ObjectName = name,
            Statement = AuditEvents.StatementText(batch),
            TransactionId = batch.CurrentTransactionId(),
        });
    }

    private static string ChangeAction(XeEventKind kind) => kind switch
    {
        XeEventKind.ObjectCreated => "CR  ",
        XeEventKind.ObjectDeleted => "DR  ",
        _ => "AL  ",
    };

    /// <summary>
    /// <c>G</c>, <c>D</c> or <c>R</c> for one permission a statement grants,
    /// denies or revokes to one principal on an object (class its type), a
    /// schema (<c>SC</c>) or the database (<c>DB</c>, the database as its
    /// object), the permission's bit in <c>permission_bitmask</c> and the
    /// grantee as the target principal.
    /// </summary>
    public static void PermissionChanged(BatchContext batch, Database database, string verb, byte securableClass, int majorId, SchemaObject? securable, string permission, DatabasePrincipal grantee)
    {
        var connection = batch.Connection;
        if (connection.Simulation.RunningAudits is null || batch.IsSkipping)
            return;
        var actionId = verb switch
        {
            "DENY" => "D   ",
            "REVOKE" => "R   ",
            _ => "G   ",
        };
        string classDesc, classType, schemaName = "", objectName;
        var objectId = majorId;
        switch (securableClass)
        {
            case PermissionChecker.ClassDatabase:
                (classDesc, classType, objectName, objectId) = ("DATABASE", "DB", database.Name, database.Id);
                break;
            case PermissionChecker.ClassSchema:
                (classDesc, classType, objectName) = ("SCHEMA", "SC", PermissionEnforcement.SchemaNameFor(database, majorId));
                break;
            case PermissionChecker.ClassObject when securable is not null:
                (classDesc, classType, objectName) = ("OBJECT", securable.ObjectTypeCode, securable.Name);
                schemaName = PermissionEnforcement.SchemaNameFor(database, securable.SchemaId);
                break;
            default:
                return;
        }
        var login = grantee.LoginName;
        AuditEvents.Write(connection, new AuditOccurrence(actionId, classDesc, classType)
        {
            PermissionBitmask = AuditCatalog.Instance.PermissionBitmask(classDesc, permission.ToUpperInvariant()),
            Database = database,
            ObjectId = objectId,
            SchemaName = schemaName,
            ObjectName = objectName,
            TargetDatabasePrincipalId = grantee.PrincipalId,
            TargetDatabasePrincipalName = grantee.Name,
            TargetServerPrincipalId = login is not null && connection.Simulation.Logins.TryGetValue(login, out var registered) ? registered.PrincipalId : 0,
            TargetServerPrincipalName = login ?? "",
            TargetServerPrincipalSid = login is null ? null : Simulation.LoginSid(connection.Simulation, login),
            Statement = AuditEvents.StatementText(batch),
            TransactionId = batch.CurrentTransactionId(),
        });
    }

    /// <summary>
    /// <c>DBCC</c>, <c>BACKUP</c> or <c>RESTORE</c> as it starts: class
    /// <c>DB</c>, the database as its object — the current one for
    /// <c>DBCC</c>, the one named for a backup, and for a restore the named
    /// database with no object (probed 2026-10-17 against SQL Server 2025).
    /// A <c>RESTORE</c> reading a device's contents (<c>HEADERONLY</c> and the
    /// like) records nothing here.
    /// </summary>
    public static void StatementStarting(ParserContext context)
    {
        var batch = context.Batch;
        var connection = batch.Connection;
        var checkpoint = context.SaveCheckpoint();
        var verb = ((ReservedKeyword)context.Token!).Keyword;
        string? named = null;
        if (verb != Keyword.Dbcc)
        {
            var scope = context.GetNextOptional();
            if ((scope is ReservedKeyword { Keyword: Keyword.Database } || (scope is UnquotedString { Value: var log } && log.Equals("LOG", StringComparison.OrdinalIgnoreCase)))
                && context.GetNextOptional() is Name name)
            {
                named = name.Value;
            }
        }
        context.RestoreCheckpoint(checkpoint);
        if (verb != Keyword.Dbcc && named is null)
            return;
        var simulation = connection.Simulation;
        var database = named is null ? batch.CurrentDatabase : simulation.Databases.GetValueOrDefault(named);
        var occurrence = verb switch
        {
            Keyword.Backup => new AuditOccurrence("BA  ", "DATABASE", "DB"),
            Keyword.Restore => new AuditOccurrence("RS  ", "DATABASE", "DB"),
            _ => new AuditOccurrence("DBCC", "DATABASE", "DB"),
        };
        occurrence.Database = database ?? batch.CurrentDatabase;
        occurrence.DatabaseName = named ?? batch.CurrentDatabase.Name;
        occurrence.ObjectId = verb == Keyword.Restore ? 0 : database?.Id ?? 0;
        occurrence.ObjectName = verb == Keyword.Restore ? "" : occurrence.DatabaseName;
        occurrence.Statement = AuditEvents.StatementText(batch);
        AuditEvents.Write(connection, occurrence);
    }

    /// <summary><c>sp_audit_write</c>'s <c>UDAU</c> record (<c>USER_DEFINED_AUDIT_GROUP</c>): class <c>DB</c>, the event id and information it was given.</summary>
    public static void UserDefined(BatchContext batch, short eventId, bool succeeded, string information)
    {
        var connection = batch.Connection;
        if (connection.Simulation.RunningAudits is null)
            return;
        var database = batch.CurrentDatabase;
        AuditEvents.Write(connection, new AuditOccurrence("UDAU", "DATABASE", "DB")
        {
            Succeeded = succeeded,
            Database = database,
            ObjectId = database.Id,
            ObjectName = database.Name,
            Statement = AuditEvents.StatementText(batch),
            UserDefinedEventId = eventId,
            UserDefinedInformation = information,
        });
    }
}
