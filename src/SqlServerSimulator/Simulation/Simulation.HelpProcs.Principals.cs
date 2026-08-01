using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// sp_helptrigger (a table's or view's DML triggers) and sp_helpuser (the
// database's users and roles). Both project live catalog state — the schema's
// Triggers dict and the database's Principals / RoleMembers surfaces — with
// column names, types, ordering and error wording probe-confirmed against SQL
// Server 2025 (2026-07-31).
partial class Simulation
{
    private static readonly SqlType[] SpHelpTriggerSchema =
    [
        SqlType.SystemName, SqlType.SystemName, SqlType.Int32, SqlType.Int32,
        SqlType.Int32, SqlType.Int32, SqlType.Int32, SqlType.SystemName,
    ];

    private static readonly string[] SpHelpTriggerColumnNames =
    [
        "trigger_name", "trigger_owner", "isupdate", "isdelete", "isinsert",
        "isafter", "isinsteadof", "trigger_schema",
    ];

    private static readonly string[] SpHelpUserColumnNames =
        ["UserName", "RoleName", "LoginName", "DefDBName", "DefSchemaName", "UserID", "SID"];

    // The role form's own shape: substring(name, 1, 25) for both name columns,
    // the principal ids as bare ints.
    private static readonly NVarcharSqlType HelpUserRoleNameType =
        NVarcharSqlType.Get(25, Collation.Baseline, Coercibility.Implicit);

    private static readonly SqlType[] SpHelpUserRoleSchema =
        [HelpUserRoleNameType, SqlType.Int32, HelpUserRoleNameType, SqlType.Int32];

    private static readonly string[] SpHelpUserRoleColumnNames =
        ["Role_name", "Role_id", "Users_in_role", "Userid"];

    private static readonly CharSqlType HelpUserIdType =
        CharSqlType.Get(10, Collation.Baseline, Coercibility.Implicit);

    private static readonly VarbinarySqlType HelpUserSidType = VarbinarySqlType.Get(85);

    /// <summary>
    /// Handles <c>EXEC sp_helptrigger @tabname [, @triggertype]</c> — one row
    /// per DML trigger attached to the named table or view:
    /// <c>trigger_name</c> / <c>trigger_owner</c> (sysname), the five
    /// <c>is*</c> int flags real reads out of <c>OBJECTPROPERTY</c>, and
    /// <c>trigger_schema</c> (sysname). <c>@triggertype</c> restricts to
    /// <c>insert</c> / <c>update</c> / <c>delete</c>; anything else is Msg
    /// 15305. A name that resolves to something other than a table or a view
    /// is Msg 15009 — real filters <c>sys.objects</c> to <c>type in ('U','V')</c>
    /// before its own existence check, so a procedure reports "does not exist"
    /// rather than a wrong-kind error.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpTrigger(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (objectName, triggerType) = ParseHelpArgs(arguments, "sp_helptrigger", "triggertype");
        var target = ResolveHelpTarget(batch, "sp_helptrigger", objectName);
        if (target.Object is not (HeapTable or View))
            throw SimulatedSqlException.HelpObjectDoesNotExist(objectName!, batch.CurrentDatabase.Name);

        var wanted = triggerType is null ? TriggerActions.None : ParseHelpTriggerType(triggerType);
        var owner = SqlValue.FromSystemName("dbo");
        var schemaName = SqlValue.FromSystemName(target.Schema.Name);
        var rows = new List<SqlValue[]>();
        foreach (var trigger in target.Schema.Triggers.Values)
        {
            if (!ReferenceEquals(trigger.Parent, target.Object))
                continue;
            if (wanted != TriggerActions.None && (trigger.Actions & wanted) == 0)
                continue;
            var isAfter = trigger.Timing == TriggerTiming.After;
            rows.Add([
                SqlValue.FromSystemName(trigger.Name),
                owner,
                HelpTriggerFlag(trigger.Actions, TriggerActions.Update),
                HelpTriggerFlag(trigger.Actions, TriggerActions.Delete),
                HelpTriggerFlag(trigger.Actions, TriggerActions.Insert),
                SqlValue.FromInt32(isAfter ? 1 : 0),
                SqlValue.FromInt32(isAfter ? 0 : 1),
                schemaName,
            ]);
        }

        rows.Sort(ByFirstCell);
        yield return new SimulatedSqlResultSet(SpHelpTriggerSchema, SpHelpTriggerColumnNames, rows);
    }

    private static SqlValue HelpTriggerFlag(TriggerActions actions, TriggerActions wanted) =>
        SqlValue.FromInt32((actions & wanted) != 0 ? 1 : 0);

    private static TriggerActions ParseHelpTriggerType(string triggerType) =>
        BuiltInToken.Equals(triggerType, "insert") ? TriggerActions.Insert
        : BuiltInToken.Equals(triggerType, "update") ? TriggerActions.Update
        : BuiltInToken.Equals(triggerType, "delete") ? TriggerActions.Delete
        : throw SimulatedSqlException.HelpTriggerTypeIsNotValid();

    /// <summary>
    /// Handles <c>EXEC sp_helpuser [@name_in_db]</c>. For no argument or a
    /// user name: one row per (user, role-membership) pair —
    /// <c>UserName</c> / <c>RoleName</c> / <c>LoginName</c> / <c>DefDBName</c>
    /// / <c>DefSchemaName</c> (each an nvarchar whose width real measures from
    /// the reported rows), <c>UserID char(10)</c> and <c>SID varbinary(85)</c>
    /// — sorted by user name, with <c>public</c> standing in for a user that
    /// belongs to no role. For a role name: real's four-column
    /// <c>Role_name</c> / <c>Role_id</c> / <c>Users_in_role</c> / <c>Userid</c>
    /// membership set. A name that is neither is Msg 15198.
    /// </summary>
    /// <remarks>
    /// <c>DefSchemaName</c> and <c>SID</c> report NULL, which is what
    /// <c>sys.database_principals</c> reports for the same principals — the
    /// simulator's principal model carries neither a per-user default schema
    /// (every name resolves through <c>dbo</c>) nor a security identifier.
    /// <c>LoginName</c> is the user's <c>CREATE USER … FOR LOGIN</c> link and
    /// <c>DefDBName</c> is that login's default database, the <c>master</c>
    /// every login reports through <c>sys.server_principals</c>.
    /// </remarks>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpUser(BatchContext batch)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var (name, _) = ParseHelpArgs(arguments, "sp_helpuser", "name_in_db");
        var database = batch.CurrentDatabase;
        var users = HelpUserRows(database, name);
        if (users.Count > 0 || name is null)
        {
            yield return HelpUserResultSet(users);
            yield break;
        }

        // Real tries the user form first and falls through to the role form
        // only when it matched nothing.
        if (!database.Principals.TryGetValue(name, out var role) || role.TypeCode != "R")
            throw SimulatedSqlException.HelpNameIsNotAUserOrRole(name);

        var members = new List<SqlValue[]>();
        var roleName = SqlValue.FromString(HelpUserRoleNameType, Truncate(role.Name, 25));
        var roleId = SqlValue.FromInt32(role.PrincipalId);
        foreach (var (roleId2, memberId) in HelpUserRoleMembers(database))
        {
            if (roleId2 != role.PrincipalId)
                continue;
            foreach (var principal in database.Principals.Values)
            {
                if (principal.PrincipalId != memberId)
                    continue;
                members.Add([
                    roleName, roleId,
                    SqlValue.FromString(HelpUserRoleNameType, Truncate(principal.Name, 25)),
                    SqlValue.FromInt32(principal.PrincipalId),
                ]);
            }
        }

        members.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a[0].AsString, b[0].AsString, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : a[1].AsInt32.CompareTo(b[1].AsInt32);
        });
        yield return new SimulatedSqlResultSet(SpHelpUserRoleSchema, SpHelpUserRoleColumnNames, members);
    }

    // One entry per (user, role) pair, with a lone 'public' entry for a user in
    // no role — real's LEFT JOIN through sys.database_role_members. Database
    // roles themselves are excluded (u.type <> 'R').
    private static List<(string User, string Role, string? Login, int UserId)> HelpUserRows(
        Database database, string? only)
    {
        var members = HelpUserRoleMembers(database);
        var rows = new List<(string User, string Role, string? Login, int UserId)>();
        foreach (var principal in database.Principals.Values)
        {
            if (principal.TypeCode == "R")
                continue;
            if (only is not null && !database.Collation.Equals(principal.Name, only))
                continue;

            var before = rows.Count;
            foreach (var (roleId, memberId) in members)
            {
                if (memberId != principal.PrincipalId)
                    continue;
                foreach (var role in database.Principals.Values)
                {
                    if (role.PrincipalId == roleId)
                        rows.Add((principal.Name, role.Name, principal.LoginName, principal.PrincipalId));
                }
            }

            if (rows.Count == before)
                rows.Add((principal.Name, "public", principal.LoginName, principal.PrincipalId));
        }

        rows.Sort(static (a, b) => string.Compare(a.User, b.User, StringComparison.OrdinalIgnoreCase));
        return rows;
    }

    private static (int RoleId, int MemberId)[] HelpUserRoleMembers(Database database)
    {
        lock (database.RoleMembers)
            return [.. database.RoleMembers];
    }

    private static SimulatedSqlResultSet HelpUserResultSet(List<(string User, string Role, string? Login, int UserId)> rows)
    {
        // Real widens each name column to the widest value it is about to
        // report — measuring nvarchar UserName / RoleName / LoginName /
        // DefDBName / DefSchemaName in bytes (its datalength) — and falls back
        // to a per-column floor when every value is NULL.
        var userWidth = HelpUserWidth(rows, 8, static r => r.User.Length * 2);
        var roleWidth = HelpUserWidth(rows, 9, static r => r.Role.Length * 2);
        var loginWidth = HelpUserWidth(rows, 9, static r => (r.Login?.Length ?? 0) * 2);
        var databaseWidth = HelpUserWidth(rows, 9, static r => r.Login is null ? 0 : HelpUserDefaultDatabase.Length * 2);

        var userType = NVarcharSqlType.Get(userWidth, Collation.Baseline, Coercibility.Implicit);
        var roleType = NVarcharSqlType.Get(roleWidth, Collation.Baseline, Coercibility.Implicit);
        var loginType = NVarcharSqlType.Get(loginWidth, Collation.Baseline, Coercibility.Implicit);
        var databaseType = NVarcharSqlType.Get(databaseWidth, Collation.Baseline, Coercibility.Implicit);
        // Every principal's default schema is dbo and no principal carries a
        // security identifier, so both columns are the all-NULL case whose
        // width falls back to real's floor.
        var schemaType = NVarcharSqlType.Get(9, Collation.Baseline, Coercibility.Implicit);

        SqlType[] schema =
            [userType, roleType, loginType, databaseType, schemaType, HelpUserIdType, HelpUserSidType];

        var nullSchemaName = SqlValue.Null(schemaType);
        var nullSid = SqlValue.Null(HelpUserSidType);
        var nullLogin = SqlValue.Null(loginType);
        var nullDatabase = SqlValue.Null(databaseType);
        var defaultDatabase = SqlValue.FromString(databaseType, Truncate(HelpUserDefaultDatabase, databaseWidth));
        var cells = new List<SqlValue[]>(rows.Count);
        foreach (var (user, role, login, userId) in rows)
        {
            cells.Add([
                SqlValue.FromString(userType, Truncate(user, userWidth)),
                SqlValue.FromString(roleType, Truncate(role, roleWidth)),
                login is null ? nullLogin : SqlValue.FromString(loginType, Truncate(login, loginWidth)),
                login is null ? nullDatabase : defaultDatabase,
                nullSchemaName,
                SqlValue.FromString(HelpUserIdType, userId.ToString(CultureInfo.InvariantCulture)),
                nullSid,
            ]);
        }

        return new SimulatedSqlResultSet(schema, SpHelpUserColumnNames, cells);
    }

    // The default database sys.server_principals reports for every login.
    private const string HelpUserDefaultDatabase = "master";

    private static int HelpUserWidth(
        List<(string User, string Role, string? Login, int UserId)> rows,
        int floor,
        Func<(string User, string Role, string? Login, int UserId), int> length)
    {
        var max = 0;
        foreach (var row in rows)
            max = Math.Max(max, length(row));
        return max == 0 ? floor : max;
    }
}
