using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The role, filegroup, device and session-binding reports — sp_helprole,
// sp_helpdbfixedrole, sp_dbfixedrolepermission, sp_helpfilegroup,
// sp_helpdevice, sp_helpntgroup — and the small procedures sp_validname,
// sp_getbindtoken and sp_bindsession.
partial class Simulation
{
    private static readonly SqlType[] SpHelpRoleSchema = [SqlType.SystemName, SqlType.Int32, SqlType.Int32];

    private static readonly string[] SpHelpRoleColumnNames = ["RoleName", "RoleId", "IsAppRole"];

    private static readonly SqlType[] SpHelpDbFixedRoleSchema =
        [SqlType.SystemName, NVarcharSqlType.Get(64, Collation.Baseline, Coercibility.Implicit)];

    private static readonly string[] SpHelpDbFixedRoleColumnNames = ["DbFixedRole", "Description"];

    private static readonly string[] SpDbFixedRolePermissionColumnNames = ["DbFixedRole", "Permission"];

    /// <summary>The fixed database roles, in principal-id order, with the <c>Description</c> <c>sp_helpdbfixedrole</c> reports.</summary>
    private static readonly (string Name, string Description)[] FixedDatabaseRoleDescriptions =
    [
        ("db_owner", "DB Owners"),
        ("db_accessadmin", "DB Access Administrators"),
        ("db_securityadmin", "DB Security Administrators"),
        ("db_ddladmin", "DB DDL Administrators"),
        ("db_backupoperator", "DB Backup Operator"),
        ("db_datareader", "DB Data Reader"),
        ("db_datawriter", "DB Data Writer"),
        ("db_denydatareader", "DB Deny Data Reader"),
        ("db_denydatawriter", "DB Deny Data Writer"),
    ];

    /// <summary>
    /// <c>sp_dbfixedrolepermission</c>'s rows, in the order real returns them:
    /// the roles by name, each role's permissions by text (captured 2026-10-04
    /// from SQL Server 2025).
    /// </summary>
    private static readonly (string Role, string[] Permissions)[] FixedDatabaseRolePermissions =
    [
        ("db_accessadmin", ["sp_dropuser", "sp_grantdbaccess", "sp_revokedbaccess"]),
        ("db_backupoperator", ["BACKUP DATABASE", "BACKUP LOG", "CHECKPOINT"]),
        ("db_datareader", ["SELECT permission on any object"]),
        ("db_datawriter", ["DELETE permission on any object", "INSERT permission on any object", "UPDATE permission on any object"]),
        ("db_ddladmin", ["All DDL but GRANT, REVOKE, DENY", "dbcc cleantable", "dbcc show_statistics", "dbcc showcontig", "REFERENCES permission on any table", "sp_changeobjectowner", "sp_fulltext_column", "sp_fulltext_table", "sp_recompile", "sp_rename", "sp_tableoption", "TRUNCATE TABLE"]),
        ("db_denydatareader", ["No SELECT permission on any object"]),
        ("db_denydatawriter", ["No DELETE permission on any object", "No INSERT permission on any object", "No UPDATE permission on any object"]),
        ("db_owner", ["Add/drop to/from db_accessadmin", "Add/drop to/from db_backupoperator", "Add/drop to/from db_datareader", "Add/drop to/from db_datawriter", "Add/drop to/from db_ddladmin", "Add/drop to/from db_denydatareader", "Add/drop to/from db_denydatawriter", "Add/drop to/from db_owner", "Add/drop to/from db_securityadmin", "All DDL but GRANT, REVOKE, DENY", "BACKUP DATABASE", "BACKUP LOG", "CHECKPOINT", "dbcc checkalloc", "dbcc checkdb", "dbcc checkfilegroup", "dbcc checkident", "dbcc checktable", "dbcc cleantable", "dbcc dbreindex", "dbcc proccache", "dbcc show_statistics", "dbcc showcontig", "dbcc shrinkdatabase", "dbcc shrinkfile", "dbcc updateusage", "DELETE permission on any object", "DENY", "EXECUTE any procedure", "GRANT", "INSERT permission on any object", "REFERENCES permission on any table", "REVOKE", "SELECT permission on any object", "sp_addapprole", "sp_addrole", "sp_addrolemember", "sp_approlepassword", "sp_change_users_login", "sp_changeobjectowner", "sp_dbcmptlevel", "sp_dropapprole", "sp_droprole", "sp_droprolemember", "sp_dropuser", "sp_fulltext_catalog", "sp_fulltext_column", "sp_fulltext_database", "sp_fulltext_table", "sp_grantdbaccess", "sp_recompile", "sp_refreshview", "sp_rename", "sp_revokedbaccess", "sp_tableoption", "TRUNCATE TABLE", "UPDATE permission on any object"]),
        ("db_securityadmin", ["DENY", "GRANT", "REVOKE", "sp_addapprole", "sp_addrole", "sp_addrolemember", "sp_approlepassword", "sp_changeobjectowner", "sp_dropapprole", "sp_droprole", "sp_droprolemember"]),
    ];

    private static readonly SqlType[] SpHelpFilegroupSchema = [SqlType.SystemName, SqlType.SmallInt, SqlType.Int32];

    private static readonly string[] SpHelpFilegroupListColumnNames = ["groupname", "groupid", "filecount"];

    private static readonly SqlType[] SpHelpFilegroupFileSchema =
        [SqlType.SystemName, SqlType.SmallInt, HelpFilePathType, HelpFileSizeType, HelpFileSizeType, HelpFileSizeType];

    private static readonly string[] SpHelpFilegroupFileColumnNames = ["file_in_group", "fileid", "filename", "size", "maxsize", "growth"];

    private static readonly SqlType[] SpHelpDeviceSchema =
    [
        SqlType.SystemName,
        NVarcharSqlType.Get(260, Collation.Baseline, Coercibility.Implicit),
        NVarcharSqlType.Get(255, Collation.Baseline, Coercibility.Implicit),
        SqlType.Int32,
        SqlType.SmallInt,
        SqlType.Int32,
    ];

    private static readonly string[] SpHelpDeviceColumnNames = ["device_name", "physical_name", "description", "status", "cntrltype", "size"];

    private static readonly SqlType[] SpHelpNtGroupSchema = [SqlType.SystemName, SqlType.Int32, VarbinarySqlType.Get(85), SqlType.Int32];

    private static readonly string[] SpHelpNtGroupColumnNames = ["NTGroupName", "NtGroupId", "SID", "HasDbAccess"];

    /// <summary>
    /// <c>EXEC sp_helprole [@rolename]</c>: the database roles and application
    /// roles in principal-id order, user-defined ones among the fixed, with
    /// <c>IsAppRole</c>; one by name, or Msg 15409 for a name that is no role
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpRole(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helprole", firstName: "rolename");
        var database = batch.CurrentDatabase;
        var rows = new List<(int Id, SqlValue[] Cells)>();
        foreach (var principal in database.Principals.EnumerateValues())
        {
            if (principal.TypeCode is not ("R" or "A"))
                continue;
            if (name is not null && !database.Collation.Equals(principal.Name, name))
                continue;
            rows.Add((principal.PrincipalId, [SqlValue.FromSystemName(principal.Name), SqlValue.FromInt32(principal.PrincipalId), SqlValue.FromInt32(principal.TypeCode == "A" ? 1 : 0)]));
        }
        if (name is not null && rows.Count == 0)
            throw SimulatedSqlException.NotARole(name);
        rows.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        yield return new SimulatedSqlResultSet(SpHelpRoleSchema, SpHelpRoleColumnNames, rows.ConvertAll(static row => row.Cells));
    }

    /// <summary>
    /// <c>EXEC sp_helpdbfixedrole [@rolename]</c>: the nine fixed database roles
    /// and their descriptions, or one of them; any other name is Msg 15412
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpDbFixedRole(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpdbfixedrole", firstName: "rolename");
        var rows = new List<SqlValue[]>();
        foreach (var (role, description) in FixedDatabaseRoleDescriptions)
        {
            if (name is null || BuiltInToken.Equals(role, name))
                rows.Add([SqlValue.FromSystemName(role), SqlValue.FromNVarchar((NVarcharSqlType)SpHelpDbFixedRoleSchema[1], description)]);
        }
        if (rows.Count == 0)
            throw SimulatedSqlException.NotAKnownFixedRole(name!);
        yield return new SimulatedSqlResultSet(SpHelpDbFixedRoleSchema, SpHelpDbFixedRoleColumnNames, rows);
    }

    /// <summary>
    /// <c>EXEC sp_dbfixedrolepermission [@rolename]</c>: what each fixed
    /// database role may do, as real's static list words it; a name that is
    /// no fixed role is Msg 15412 (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpDbFixedRolePermission(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_dbfixedrolepermission", firstName: "rolename");
        var rows = new List<SqlValue[]>();
        foreach (var (role, permissions) in FixedDatabaseRolePermissions)
        {
            if (name is not null && !BuiltInToken.Equals(role, name))
                continue;
            foreach (var permission in permissions)
                rows.Add([SqlValue.FromSystemName(role), SqlValue.FromNVarchar((NVarcharSqlType)SpHelpDbFixedRoleSchema[1], permission)]);
        }
        if (rows.Count == 0)
            throw SimulatedSqlException.NotAKnownFixedRole(name!);
        yield return new SimulatedSqlResultSet(SpHelpDbFixedRoleSchema, SpDbFixedRolePermissionColumnNames, rows);
    }

    /// <summary>
    /// <c>EXEC sp_helpfilegroup [@filegroupname]</c>: each filegroup of the
    /// current database with its id and file count, or one filegroup followed
    /// by its files as <c>sp_helpfile</c> reports them; a name no filegroup
    /// carries is Msg 15325 (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpFilegroup(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpfilegroup", firstName: "filegroupname");
        var database = batch.CurrentDatabase;
        var groups = new List<(string Name, int Id)>();
        foreach (var (groupName, dataSpaceId) in database.Filegroups)
        {
            if (name is null || database.Collation.Equals(groupName, name.TrimEnd(' ')))
                groups.Add((groupName, dataSpaceId));
        }
        if (name is not null && groups.Count == 0)
            throw SimulatedSqlException.HelpFilegroupDoesNotExist(name);
        groups.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        var files = database.FilesInOrder();
        var rows = groups.ConvertAll(group => new[]
        {
            SqlValue.FromSystemName(group.Name),
            SqlValue.FromInt16((short)group.Id),
            SqlValue.FromInt32(files.Count(file => !file.IsLog && !file.IsContainer && file.DataSpaceId == group.Id)),
        });
        yield return new SimulatedSqlResultSet(SpHelpFilegroupSchema, SpHelpFilegroupListColumnNames, rows);
        if (name is null)
            yield break;

        var groupId = groups[0].Id;
        var fileRows = new List<SqlValue[]>();
        foreach (var file in files)
        {
            if (file.IsLog || file.IsContainer || file.DataSpaceId != groupId)
                continue;
            var maxSize = BuiltInResources.ReportedMaxSizePages(file);
            fileRows.Add([
                SqlValue.FromSystemName(file.Name),
                SqlValue.FromInt16((short)file.FileId),
                SqlValue.FromString(HelpFilePathType, file.PhysicalName),
                SqlValue.FromString(HelpFileSizeType, HelpFileKilobytes(BuiltInResources.FileSizePages(database, file))),
                SqlValue.FromString(HelpFileSizeType, maxSize == -1 ? "Unlimited" : HelpFileKilobytes(maxSize)),
                SqlValue.FromString(HelpFileSizeType, file.IsPercentGrowth
                    ? file.Growth.ToString(System.Globalization.CultureInfo.InvariantCulture) + "%"
                    : HelpFileKilobytes(file.Growth)),
            ]);
        }
        yield return new SimulatedSqlResultSet(SpHelpFilegroupFileSchema, SpHelpFilegroupFileColumnNames, fileRows);
    }

    /// <summary>
    /// <c>EXEC sp_helpdevice [@devname]</c>: the backup devices, of which a
    /// simulation has none — an empty set, and Msg 15012 for a named one
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpDevice(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpdevice", firstName: "devname");
        if (name is not null)
            throw SimulatedSqlException.BackupDeviceDoesNotExist(name);
        yield return new SimulatedSqlResultSet(SpHelpDeviceSchema, SpHelpDeviceColumnNames, []);
    }

    /// <summary>
    /// <c>EXEC sp_helpntgroup [@ntname]</c>: the database's Windows-group
    /// users, of which a simulation has none — an empty set, and Msg 15420 for
    /// a named one (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpNtGroup(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var (name, _) = ParseHelpArgs(arguments, "sp_helpntgroup", firstName: "ntname");
        if (name is not null)
            throw SimulatedSqlException.WindowsGroupDoesNotExist(name);
        yield return new SimulatedSqlResultSet(SpHelpNtGroupSchema, SpHelpNtGroupColumnNames, []);
    }

    private static readonly string[] ValidNameParameterNames = ["name", "raise_error"];

    /// <summary>
    /// <c>EXEC sp_validname @name [, @raise_error]</c>: an empty or NULL name is
    /// Msg 15004 from line 17 — or, with <c>@raise_error = 0</c>, return code 1
    /// alone — and any other name returns 0, leaving <c>@@ROWCOUNT</c> at 1
    /// (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpValidName(BatchContext batch, string? returnCode)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireSystemProcedureShape("sp_validname", arguments, ValidNameParameterNames, required: 1);
        string? name = null;
        var raiseError = true;
        var positional = 0;
        foreach (var argument in arguments)
        {
            var slot = argument.Name is { } argumentName ? Array.FindIndex(ValidNameParameterNames, parameter => BuiltInToken.Equals(argumentName, parameter)) : positional++;
            if (slot == 0)
                name = CatalogStringArg(argument);
            else
                raiseError = argument.IsDefault || argument.Value.IsNull || CatalogFlagArg(argument);
        }
        var valid = !string.IsNullOrEmpty(name);
        if (returnCode is not null)
        {
            var slot = batch.GetVariableSlot(returnCode);
            slot.Value = SqlValue.FromInt32(valid ? 0 : 1).CoerceTo(slot.DeclaredType);
        }
        if (!valid && raiseError)
            throw SimulatedSqlException.ValidNameCannotBeNull();
        if (valid)
            batch.Connection.LastStatementRowCount = 1;
    }

    /// <summary>
    /// <c>EXEC sp_getbindtoken @out_token OUTPUT</c>: a 32-character token for
    /// the session's transaction, written to a variable passed <c>OUTPUT</c>
    /// (Msg 591 otherwise), with return code 1 as real's extended procedure
    /// answers; outside a transaction Msg 3921 (probed 2026-10-04 against SQL
    /// Server 2025). The token is the transaction's, the same for every call
    /// and every session bound to it, and a new transaction's is new (probed
    /// 2026-10-07).
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpGetBindToken(BatchContext batch, string? returnCode)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        if (arguments.Count == 0)
            throw SimulatedSqlException.ProcedureExpectsParameter("sp_getbindtoken", "out_token", state: 9);
        RequireSystemProcedureShape("sp_getbindtoken", arguments, ["out_token"], required: 1);
        if (arguments[0].OutputSlot is not { } output)
            throw SimulatedSqlException.BindTokenParameterNotOutput();
        if (batch.Connection.CurrentTransaction is not { } transaction)
            throw SimulatedSqlException.NoTransactionForBindToken();
        var token = batch.Connection.Simulation.IssueBindToken(transaction);
        output.Value = Parser.Expressions.Cast.ApplyCoercion(SqlValue.FromVarchar(token), output.DeclaredType, output.DeclaredMaxLength);
        if (returnCode is not null)
        {
            var slot = batch.GetVariableSlot(returnCode);
            slot.Value = SqlValue.FromInt32(1).CoerceTo(slot.DeclaredType);
        }
    }

    /// <summary>
    /// <c>EXEC sp_bindsession @bind_token</c>: binds the session to the
    /// transaction a <c>sp_getbindtoken</c> token names, which it then shares
    /// with every session bound to it — its locks, its uncommitted writes and
    /// its fate, each session nesting on a <c>@@TRANCOUNT</c> of its own — and
    /// a NULL or empty token unbinds the session, the transaction going on for
    /// the others (probed 2026-10-07 against SQL Server 2025). A session
    /// already in a transaction leaves it first with Msg 3924, rolling its own
    /// back. A token of a transaction that has ended binds nothing, silently,
    /// while the session that began it remains and isn't the caller, and is
    /// Msg 3922 otherwise; anything else is Msg 3909, and a token not passed
    /// as <c>varchar</c> Msg 257.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpBindSession(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        RequireSystemProcedureShape("sp_bindsession", arguments, ["bind_token"], required: 1);
        var argument = arguments[0];
        if (!argument.IsDefault && !argument.IsUntypedNull && argument.Value.Type is not VarcharSqlType)
            throw SimulatedSqlException.ImplicitConversionNotAllowed(argument.Value.Type.SqlServerName, "varchar", state: 5);
        var connection = batch.Connection;
        var token = CatalogStringArg(argument);
        if (string.IsNullOrEmpty(token))
        {
            LeaveTransaction(connection);
            yield break;
        }
        if (token.Length < 32)
            throw SimulatedSqlException.BindTokenIsInvalid(state: 1);
        if (!connection.Simulation.BindTokens.TryGetValue(token, out var issue))
            throw SimulatedSqlException.BindTokenIsInvalid(token.AsSpan(0, 32).IndexOfAnyExcept('-') < 0 ? (byte)3 : (byte)2);
        if (connection.CurrentTransaction is not null)
        {
            yield return new SimulatedInfoOutcome(SimulatedSqlException.SessionDefectedFromTransactionMessage(connection));
            LeaveTransaction(connection);
        }
        if (!issue.Transaction.TryGetTarget(out var transaction) || transaction.Ended)
        {
            if (ReferenceEquals(issue.Issuer, connection.Session) || !connection.Simulation.HasSession(issue.Issuer))
                throw SimulatedSqlException.BindTransactionDoesNotExist();
            yield break;
        }
        transaction.Attach(connection, TransactionMembership.Bound, running: true);
        // Ended by another member while this one waited its turn.
        if (transaction.Ended)
            connection.EndedUnderBinding = null;
    }

    /// <summary>
    /// Takes <paramref name="connection"/> out of the transaction it is in: one
    /// it shares goes on for the others, and one no one else is left in rolls
    /// back.
    /// </summary>
    private static void LeaveTransaction(SimulatedDbConnection connection)
    {
        if (connection.CurrentTransaction is not { } transaction)
            return;
        if (transaction.Members is null || !transaction.Detach(connection))
            transaction.EndRollback();
    }
}
