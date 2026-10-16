using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The host and maintenance procedures DBA scripts call: xp_fixeddrives,
// xp_loginconfig and xp_servicecontrol over the host the simulator models,
// sp_srvrolepermission's static list, and sp_sqlexec, sp_flush_log,
// sp_resetstatus, sp_clean_db_free_space and sp_clean_db_file_free_space.
// See docs/claude/catalog-views.md.
partial class Simulation
{
    private static readonly SqlType[] XpFixedDrivesSchema = [NVarcharSqlType.Get(1, Collation.Baseline, Coercibility.Implicit), SqlType.Int32];

    private static readonly string[] XpFixedDrivesNames = ["drive", "MB free"];

    private static readonly NVarcharSqlType XpLoginConfigText = NVarcharSqlType.Get(28, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] XpLoginConfigSchema = [XpLoginConfigText, XpLoginConfigText];

    private static readonly string[] XpLoginConfigNames = ["name", "config_value"];

    private static readonly SqlType[] XpServiceControlSchema = [NVarcharSqlType.Get(132, Collation.Baseline, Coercibility.Implicit)];

    private static readonly string[] XpServiceControlNames = ["Current Service State"];

    private static readonly SqlType[] SpSrvRolePermissionSchema = [SqlType.SystemName, NVarcharSqlType.Get(64, Collation.Baseline, Coercibility.Implicit)];

    private static readonly string[] SpSrvRolePermissionNames = ["ServerRole", "Permission"];

    private static readonly bool[] SpSrvRolePermissionNullability = [false, true];

    /// <summary>
    /// The megabytes free <c>xp_fixeddrives</c> reports for the one drive,
    /// <c>C</c>, a fixed placeholder near the reference's own as
    /// <c>xp_msver</c>'s host cells are.
    /// </summary>
    private const int FixedDriveMegabytesFree = 900000;

    /// <summary>
    /// <c>xp_loginconfig</c>'s rows, as SQL Server 2025 on Linux reports them
    /// in mixed authentication mode (probed 2026-10-16).
    /// </summary>
    private static readonly (string Name, string? Value)[] LoginConfiguration =
    [
        ("login mode", "Mixed"),
        ("default login", "guest"),
        ("default domain", null),
        ("audit level", "failure"),
        ("set hostname", "false"),
        ("map _", "domain separator"),
        ("map $", null),
        ("map #", "-"),
    ];

    /// <summary>
    /// <c>sp_srvrolepermission</c>'s rows, in the order real returns them: the
    /// fixed server roles by name, each role's permissions by text (captured
    /// 2026-10-16 from SQL Server 2025).
    /// </summary>
    private static readonly (string Role, string[] Permissions)[] FixedServerRolePermissions =
    [
        ("##MS_DatabaseConnector##", ["Add member to DatabaseConnector", "Can connect to any database"]),
        ("##MS_DatabaseManager##", ["Add member to Database Manager", "Can Manage any database"]),
        ("##MS_DefinitionReader##", ["Add member to definitionreader", "Read definitions of objects"]),
        ("##MS_LoginManager##", ["Add member to Login Manager", "Can manage any login"]),
        ("##MS_PerformanceDefinitionReader##", ["Add member to permissiondefinitionreader", "Read definitions of non security objects"]),
        ("##MS_SecurityDefinitionReader##", ["Add member to securitydefinitionreader", "Read definitions of security objects"]),
        ("##MS_ServerPerformanceStateReader##", ["Add member to serverperformacnestatereader", "Read rows from non security DMVs"]),
        ("##MS_ServerSecurityStateReader##", ["Add member to serversecuritystatereader", "Read rows from security DMVs"]),
        ("##MS_ServerStateManager##", ["Add member to serverstatemanager", "Read rows from DMVs"]),
        ("##MS_ServerStateReader##", ["Add member to serverstatereader", "Read rows from DMVs"]),
        ("bulkadmin", ["Add member to bulkadmin", "BULK INSERT"]),
        ("dbcreator",
        [
            "Add member to dbcreator", "ALTER DATABASE", "CREATE DATABASE", "DROP DATABASE", "Extend database", "RESTORE DATABASE", "RESTORE LOG",
            "sp_renamedb",
        ]),
        ("diskadmin", ["Add member to diskadmin", "DISK INIT", "sp_addumpdevice", "sp_diskdefault", "sp_dropdevice"]),
        ("processadmin", ["Add member to processadmin", "KILL"]),
        ("securityadmin",
        [
            "Add member to securityadmin", "Grant/deny/revoke CREATE DATABASE", "Read the error log", "sp_addlinkedsrvlogin", "sp_addlogin",
            "sp_defaultdb", "sp_defaultlanguage", "sp_denylogin", "sp_droplinkedsrvlogin", "sp_droplogin", "sp_dropremotelogin", "sp_grantlogin",
            "sp_helplogins", "sp_password", "sp_remoteoption (update)", "sp_revokelogin",
        ]),
        ("serveradmin",
        [
            "Add member to serveradmin", "dbcc freeproccache", "RECONFIGURE", "SHUTDOWN", "sp_configure", "sp_fulltext_service", "sp_tableoption",
        ]),
        ("setupadmin", ["Add member to setupadmin", "Add/drop/configure linked servers", "Mark a stored procedure as startup"]),
        ("sysadmin",
        [
            "Add extended procedures", "Add member to bulkadmin", "Add member to Database Manager", "Add member to DatabaseConnector",
            "Add member to dbcreator", "Add member to definitionreader", "Add member to diskadmin", "Add member to Login Manager",
            "Add member to permissiondefinitionreader", "Add member to processadmin", "Add member to securityadmin",
            "Add member to securitydefinitionreader", "Add member to serveradmin", "Add member to serverperformacnestatereader",
            "Add member to serversecuritystatereader", "Add member to serverstatemanager", "Add member to serverstatereader",
            "Add member to setupadmin", "Add member to sysadmin", "Add/drop to/from db_accessadmin", "Add/drop to/from db_backupoperator",
            "Add/drop to/from db_datareader", "Add/drop to/from db_datawriter", "Add/drop to/from db_ddladmin", "Add/drop to/from db_denydatareader",
            "Add/drop to/from db_denydatawriter", "Add/drop to/from db_owner", "Add/drop to/from db_securityadmin",
            "Add/drop/configure linked servers", "All DDL but GRANT, REVOKE, DENY", "ALTER DATABASE", "BACKUP DATABASE", "BACKUP LOG", "BULK INSERT",
            "Can connect to any database", "Can Manage any database", "Can manage any login", "CHECKPOINT", "Complete SETUSER SQL user",
            "Constraints on System tables", "CREATE DATABASE", "Create indices on system tables", "Create/delete/modify system tables",
            "dbcc change 'on' rules", "dbcc checkalloc", "dbcc checkdb", "dbcc checkfilegroup", "dbcc checkident", "dbcc checktable",
            "dbcc cleantable", "dbcc dbreindex", "dbcc dropcleanbuffers", "dbcc freeproccache", "dbcc inputbuffer", "dbcc outputbuffer",
            "dbcc pintable", "dbcc proccache", "dbcc setcpuweight", "dbcc setioweight", "dbcc show_statistics", "dbcc showcontig",
            "dbcc showoptweights", "DBCC ShrinkDatabase", "dbcc shrinkfile", "dbcc traceon/off", "dbcc updateusage",
            "DELETE permission on any object", "DENY", "DISK INIT", "DROP DATABASE", "EXECUTE any procedure", "Extend database", "GRANT",
            "Grant/deny/revoke CREATE DATABASE", "INSERT permission on any object", "KILL", "Mark a stored procedure as startup",
            "Raiserror With Log", "Read definitions of non security objects", "Read definitions of objects", "Read definitions of security objects",
            "Read rows from DMVs", "Read rows from non security DMVs", "Read rows from security DMVs", "Read the error log", "RECONFIGURE",
            "REFERENCES permission on any table", "RESTORE DATABASE", "RESTORE LOG", "REVOKE", "SELECT permission on any object", "SHUTDOWN",
            "sp_addapprole", "sp_addlinkedsrvlogin", "sp_addlogin", "sp_addrole", "sp_addrolemember", "sp_addumpdevice", "sp_adduser",
            "sp_altermessage", "sp_approlepassword", "sp_change_users_login", "sp_changedbowner", "sp_changeobjectowner", "sp_configure",
            "sp_dbcmptlevel", "sp_defaultdb", "sp_defaultlanguage", "sp_denylogin", "sp_diskdefault", "sp_dropapprole", "sp_dropdevice",
            "sp_droplinkedsrvlogin", "sp_droplogin", "sp_dropremotelogin", "sp_droprole", "sp_droprolemember", "sp_dropuser", "sp_fulltext_catalog",
            "sp_fulltext_column", "sp_fulltext_database", "sp_fulltext_service", "sp_fulltext_table", "sp_grantdbaccess", "sp_grantlogin",
            "sp_helplogins", "sp_password", "sp_recompile", "sp_refreshview", "sp_remoteoption", "sp_remoteoption (update)", "sp_rename",
            "sp_renamedb", "sp_revokedbaccess", "sp_revokelogin", "sp_tableoption", "sp_updatestats", "TRUNCATE TABLE",
            "UPDATE permission on any object", "USE to a suspect database",
        ]),
    ];

    /// <summary>
    /// <c>EXEC xp_fixeddrives [@type]</c>: the host's one drive, <c>C</c>,
    /// with its megabytes free, listed for a type of 0 or none and for a
    /// <c>CONTROL SERVER</c> holder alone — anyone else reads the set empty;
    /// a type that is no integer is Msg 22005 at severity 12 and a second
    /// argument at severity 15, each leaving the batch running (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpFixedDrives(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count > 1)
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.FixedDrivesInvalidParameter(typeError: false));
            yield break;
        }
        var type = 0;
        if (arguments.Count == 1)
        {
            var value = arguments[0].Value;
            if (arguments[0].IsDefault || value.IsNull || !SqlType.IsIntegerCategory(value.Type))
            {
                yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.FixedDrivesInvalidParameter(typeError: true));
                yield break;
            }
            type = value.CoerceTo(SqlType.Int32).AsInt32;
        }
        var connection = batch.Connection;
        List<SqlValue[]> rows = type == 0 && connection.Simulation.SessionHoldsServerPermission(connection, Permission.ControlServer)
            ? [[SqlValue.FromNVarchar((NVarcharSqlType)XpFixedDrivesSchema[0], "C"), SqlValue.FromInt32(FixedDriveMegabytesFree)]]
            : [];
        yield return new SimulatedSqlResultSet(XpFixedDrivesSchema, XpFixedDrivesNames, rows);
    }

    /// <summary>
    /// <c>EXEC xp_loginconfig [@name]</c>: the server's authentication
    /// settings, or the one a name picks case aside; a name that is none, a
    /// value that is no string, or a second argument is reported as a plain
    /// message numbered 0 and returns 1 (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpLoginConfig(BatchContext batch, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, "xp_loginconfig", calledAs);
        string? refusal = null;
        string? name = null;
        if (arguments.Count > 1)
            refusal = "Error executing extended stored procedure: Invalid Parameter.";
        else if (arguments.Count == 1 && (arguments[0].IsDefault || arguments[0].Value.IsNull || !SqlType.IsStringCategory(arguments[0].Value.Type)))
            refusal = "Error executing extended stored procedure: Invalid Parameter Type";
        else if (arguments.Count == 1)
            name = arguments[0].Value.AsString;
        var rows = new List<SqlValue[]>();
        foreach (var (setting, value) in LoginConfiguration)
        {
            if (name is null || string.Equals(setting, name, StringComparison.OrdinalIgnoreCase))
                rows.Add([SqlValue.FromNVarchar(XpLoginConfigText, setting), value is null ? SqlValue.Null(XpLoginConfigText) : SqlValue.FromNVarchar(XpLoginConfigText, value)]);
        }
        if (name is not null && rows.Count == 0)
            refusal = "Error executing extended stored procedure: Invalid parameter value.";
        SetReturnCode(batch, returnCodeVar, refusal is null ? 0 : 1);
        if (refusal is not null)
        {
            yield return Printed(new SimulatedError(@class: 0, lineNumber: 0, message: refusal, number: 0, procedure: "", server: batch.Connection.DataSource, source: "SqlServerSimulator", state: 0));
            yield break;
        }
        yield return new SimulatedSqlResultSet(XpLoginConfigSchema, XpLoginConfigNames, rows);
    }

    /// <summary>
    /// <c>EXEC xp_servicecontrol @action, @service</c>: the one service the
    /// host has, <c>SQLServerAgent</c>, reads as stopped, and every action on
    /// it fails as real's own does on Linux; another service doesn't exist.
    /// Each refusal is Msg 22003 that leaves the batch running and returns 1
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeXpServiceControl(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, "xp_servicecontrol", calledAs);
        static bool IsText(ProcArgument argument) => !argument.IsDefault && !argument.Value.IsNull && SqlType.IsStringCategory(argument.Value.Type);
        if (arguments.Count < 2 || !IsText(arguments[0]) || !IsText(arguments[1]))
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.ServiceControlInvalidParameter());
            yield break;
        }
        var action = arguments[0].Value.AsString.ToUpperInvariant();
        if (action is not ("CONTINUE" or "PAUSE" or "QUERYSTATE" or "START" or "STOP"))
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.ServiceControlInvalidCommand());
            yield break;
        }
        if (!string.Equals(arguments[1].Value.AsString, "SQLServerAgent", StringComparison.OrdinalIgnoreCase))
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.ServiceControlFailed("OpenService", 1060));
            yield break;
        }
        if (action != "QUERYSTATE")
        {
            yield return ExtendedProcedureRefusal(batch, SimulatedSqlException.ServiceControlFailed("StartService", action == "START" ? 1053 : 1062));
            yield break;
        }
        yield return new SimulatedSqlResultSet(XpServiceControlSchema, XpServiceControlNames, [[SqlValue.FromNVarchar((NVarcharSqlType)XpServiceControlSchema[0], "Stopped.")]]);
    }

    /// <summary>
    /// <c>EXEC sp_srvrolepermission [@srvrolename]</c>: what each fixed server
    /// role may do, as real's static list words it; a name that is no fixed
    /// server role (<c>public</c> included) is Msg 15412 (probed 2026-10-16
    /// against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpSrvRolePermission(BatchContext batch, string procedure, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, [new("srvrolename", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar))]);
        var name = values[0].IsNull ? null : values[0].AsString;
        var rows = new List<SqlValue[]>();
        foreach (var (role, permissions) in FixedServerRolePermissions)
        {
            if (name is not null && !BuiltInToken.Equals(role, name))
                continue;
            foreach (var permission in permissions)
                rows.Add([SqlValue.FromSystemName(role), SqlValue.FromNVarchar((NVarcharSqlType)SpSrvRolePermissionSchema[1], permission)]);
        }
        if (rows.Count == 0)
            throw SimulatedSqlException.NotAKnownFixedRole(name!);
        yield return new SimulatedSqlResultSet(SpSrvRolePermissionSchema, SpSrvRolePermissionNames, rows) { ColumnNullability = SpSrvRolePermissionNullability };
    }

    /// <summary>
    /// <c>EXEC sp_sqlexec @p1</c>: runs its text as dynamic SQL, returning 0
    /// whatever the batch raised (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpSqlExec(BatchContext batch, string procedure, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, [new("p1", SqlType.NVarchar, int.MaxValue)]);
        SetReturnCode(batch, returnCodeVar, 0);
        if (values[0].IsNull)
            yield break;
        foreach (var outcome in this.ExecuteDynamicBatch(batch, values[0].AsString, preDeclaredVariables: null, viaSystemProcedure: true))
            yield return outcome;
        SetReturnCode(batch, returnCodeVar, 0);
    }

    /// <summary>
    /// <c>EXEC sp_flush_log</c>: the log is never delayed here, so there is
    /// nothing to flush; it takes any argument and returns 1, as real's does
    /// with no delayed-durable transaction pending (probed 2026-10-16 against
    /// SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpFlushLog(BatchContext batch, string? returnCodeVar)
    {
        _ = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        SetReturnCode(batch, returnCodeVar, 1);
    }

    /// <summary>
    /// <c>EXEC sp_resetstatus @DBName</c>: no database is ever suspect, so it
    /// says the flag is already reset (Msg 15056 at line 59) and returns 1; a
    /// database that doesn't exist is Msg 15010 at line 35, and the procedure
    /// is a sysadmin's (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpResetStatus(BatchContext batch, string procedure, string calledAs, string? returnCodeVar)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireExtendedProcedureExecute(batch, procedure, calledAs);
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, [new("DBName", SqlType.NVarchar, 128)]);
        var name = values[0].IsNull ? "" : values[0].AsString;
        if (!batch.Connection.Simulation.Databases.TryGetValue(name, out var database))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpDatabaseDoesNotExist(name), 35);
        SetReturnCode(batch, returnCodeVar, 1);
        yield return ProcedureMessage(batch, calledAs, 59, 15056, $"The suspect flag on the database \"{database.Name}\" is already reset.");
    }

    /// <summary>
    /// <c>EXEC sp_clean_db_free_space @dbname [, @cleaning_delay]</c> and
    /// <c>sp_clean_db_file_free_space @dbname, @fileid [, @cleaning_delay]</c>:
    /// a page's freed bytes are never kept here, so there is nothing to clean,
    /// and each sends the Msg 2528 real's <c>DBCC</c> closes with — once per
    /// data file of the database, or once for the file named, whatever its
    /// id — from <c>sp_clean_db_file_free_space</c> at line 69; a database that
    /// doesn't exist is Msg 15010 naming it bracketed, and a login without
    /// <c>CONTROL SERVER</c> is then refused with Msg 14260, which returns 1
    /// and leaves the batch running (probed 2026-10-16 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpCleanDbFreeSpace(BatchContext batch, string procedure, string calledAs, bool oneFile)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        SystemProcedureParameter[] parameters = oneFile
            ? [new("dbname", SqlType.NVarchar, 128), new("fileid", SqlType.Int32), new("cleaning_delay", SqlType.Int32, 0, SqlValue.FromInt32(0))]
            : [new("dbname", SqlType.NVarchar, 128), new("cleaning_delay", SqlType.Int32, 0, SqlValue.FromInt32(0))];
        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var name = values[0].IsNull ? "" : values[0].AsString;
        if (!batch.Connection.Simulation.Databases.TryGetValue(name, out var database))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.HelpDatabaseDoesNotExist("[" + name + "]"), oneFile ? 18 : 14);
        // The cleaning itself runs as a dynamic batch of real's, whose refusal
        // names no procedure and leaves the caller's batch running.
        var connection = batch.Connection;
        if (!connection.Simulation.SessionHoldsServerPermission(connection, Permission.ControlServer))
        {
            var refusal = SimulatedSqlException.InsufficientPermissionToRunCommand();
            refusal.PreserveDiagnostics(3, "");
            yield return new SimulatedErrorOutcome(refusal) { DoneKind = batch.CurrentStatement.DoneKind };
            yield break;
        }
        var files = oneFile ? 1 : database.FilesInOrder().Count(static file => !file.IsLog && !file.IsContainer);
        for (var i = 0; i < files; i++)
        {
            var completed = SimulatedSqlException.DbccExecutionCompletedMessage(batch);
            completed.LineNumber = 69;
            completed.Procedure = "sp_clean_db_file_free_space";
            yield return Printed(completed);
        }
    }
}
