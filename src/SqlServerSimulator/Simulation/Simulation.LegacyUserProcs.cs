using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

// The pre-DDL spellings of database and server security management: sp_adduser /
// sp_grantdbaccess / sp_dropuser / sp_revokedbaccess, sp_addrole / sp_droprole,
// sp_addsrvrolemember / sp_dropsrvrolemember, sp_helprolemember and
// sp_change_users_login. Each does what real's procedure does itself, then runs
// the statement it builds, so the statement's refusals arrive at line 1 in no
// procedure.
partial class Simulation
{
    private static readonly SystemProcedureParameter[] AddUserParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("name_in_db", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("grpname", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] GrantDbAccessParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("name_in_db", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropUserParameters =
    [
        new("name_in_db", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] AddRoleParameters =
    [
        new("rolename", SqlType.NVarchar, 128),
        new("ownername", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] DropRoleParameters =
    [
        new("rolename", SqlType.NVarchar, 128),
    ];

    private static readonly SystemProcedureParameter[] ServerRoleMemberParameters =
    [
        new("loginame", SqlType.NVarchar, 128),
        new("rolename", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] HelpRoleMemberParameters =
    [
        new("rolename", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SystemProcedureParameter[] ChangeUsersLoginParameters =
    [
        new("Action", SqlType.NVarchar, 10),
        new("UserNamePattern", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("LoginName", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
        new("Password", SqlType.NVarchar, 128, SqlValue.Null(SqlType.NVarchar)),
    ];

    private static readonly SqlType[] HelpRoleMemberSchema = [SqlType.SystemName, SqlType.SystemName, VarbinarySqlType.Get(85)];
    private static readonly string[] HelpRoleMemberColumnNames = ["DbRole", "MemberName", "MemberSID"];
    private static readonly SqlType[] ChangeUsersLoginReportSchema = [SqlType.SystemName, VarbinarySqlType.Get(85)];
    private static readonly string[] ChangeUsersLoginReportColumnNames = ["UserName", "UserSID"];

    /// <summary>The <c>principal_id</c> of the <c>db_owner</c> fixed role.</summary>
    private const int DbOwnerRoleId = 16384;

    private static DatabasePrincipal? FindDatabasePrincipal(BatchContext batch, string name) =>
        batch.CurrentDatabase.Principals.TryGetValue(name, out var principal) ? principal : null;

    /// <summary>Runs each statement as a batch of its own, the way real's procedures <c>EXEC</c> them one at a time.</summary>
    private IEnumerable<SimulatedStatementOutcome> RunStatements(BatchContext batch, IEnumerable<string> statements)
    {
        foreach (var statement in statements)
        {
            if (statement.Length == 0)
                continue;
            var failed = false;
            foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            {
                failed |= outcome is SimulatedErrorOutcome;
                yield return outcome;
            }
            if (failed)
                yield break;
        }
    }

    /// <summary>
    /// The statements that give a user or role the schema of its own name, which
    /// <c>sp_adduser</c>, <c>sp_grantdbaccess</c> and <c>sp_addrole</c> create with
    /// it — and its default schema, for a user (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    private static IEnumerable<string> OwnSchemaStatements(BatchContext batch, string name, bool isUser)
    {
        if (batch.CurrentDatabase.Schemas.ContainsKey(name))
            yield break;
        var quoted = QuoteIdentifier(name);
        yield return $"create schema {quoted} authorization {quoted}";
        if (isUser)
            yield return $"alter user {quoted} with default_schema = {quoted}";
    }

    /// <summary>Drops the schema a user or role of the same name owns, when it holds nothing — what the drop procedures do before the principal goes.</summary>
    private static string DropOwnSchemaStatement(BatchContext batch, DatabasePrincipal principal)
    {
        var database = batch.CurrentDatabase;
        if (!database.Schemas.TryGetValue(principal.Name, out var schema) || schema.PrincipalId != principal.PrincipalId)
            return string.Empty;
        return schema.SchemaObjects().Any() ? string.Empty : "drop schema " + QuoteIdentifier(principal.Name);
    }

    /// <summary>
    /// <c>sp_adduser @loginame [, @name_in_db [, @grpname]]</c> is <c>CREATE USER … FOR LOGIN</c>
    /// with a schema of its own name as the default, and the user added to <c>@grpname</c> when
    /// that role exists (Msg 15014 when it doesn't, ahead of any change). <c>guest</c> is the
    /// exception: it is granted <c>CONNECT</c> instead of created.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddUser(BatchContext batch, string calledAs) =>
        this.AddUser(batch, calledAs, "sp_adduser", AddUserParameters, viaAddUser: true);

    /// <summary><c>sp_grantdbaccess @loginame [, @name_in_db]</c>: <c>sp_adduser</c> without the group.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpGrantDbAccess(BatchContext batch, string calledAs) =>
        this.AddUser(batch, calledAs, "sp_grantdbaccess", GrantDbAccessParameters, viaAddUser: false);

    private IEnumerable<SimulatedStatementOutcome> AddUser(BatchContext batch, string calledAs, string procedure, SystemProcedureParameter[] parameters, bool viaAddUser)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, parameters);
        var login = RequireValidName(values[0]);
        var userName = values[1].IsNull ? login : values[1].AsString;
        var group = viaAddUser && !values[2].IsNull ? values[2].AsString : null;

        if (viaAddUser && BuiltInToken.Comparer.Equals(login, "guest"))
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, "grant connect to guest", preDeclaredVariables: null))
                yield return outcome;
            yield break;
        }
        if (!IsRegisteredLogin(batch, login))
        {
            throw viaAddUser
                ? AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(login), 15)
                : SimulatedSqlException.NotAValidLogin(login).PinLine(1);
        }
        if (group is not null && FindDatabasePrincipal(batch, group) is not { TypeCode: "R" })
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.RoleDoesNotExistInDatabase(group), 23);

        List<string> statements = ["create user " + QuoteIdentifier(userName) + " for login " + QuoteIdentifier(login), .. OwnSchemaStatements(batch, userName, isUser: true)];
        if (group is not null)
            statements.Add("alter role " + QuoteIdentifier(group) + " add member " + QuoteIdentifier(userName));
        foreach (var outcome in this.RunStatements(batch, statements))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_dropuser @name_in_db</c> drops the user's own empty schema and the user
    /// (<c>REVOKE CONNECT</c> for <c>guest</c>). A name that isn't a principal is
    /// Msg 15008 from line 12; <c>dbo</c> is refused by <c>sys.sp_revokedbaccess</c>,
    /// which it runs.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropUser(BatchContext batch, string calledAs) =>
        this.DropUser(batch, calledAs, "sp_dropuser", viaDropUser: true);

    /// <summary><c>sp_revokedbaccess @name_in_db</c>: <c>sp_dropuser</c>'s own body.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpRevokeDbAccess(BatchContext batch, string calledAs) =>
        this.DropUser(batch, calledAs, "sp_revokedbaccess", viaDropUser: false);

    private IEnumerable<SimulatedStatementOutcome> DropUser(BatchContext batch, string calledAs, string procedure, bool viaDropUser)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, DropUserParameters);
        string name;
        if (viaDropUser)
        {
            name = values[0].IsNull ? "(null)" : values[0].AsString;
            if (values[0].IsNull || FindDatabasePrincipal(batch, name) is null)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserDoesNotExistInDatabase(name), 12);
        }
        else
        {
            name = RequireValidName(values[0]);
            if (FindDatabasePrincipal(batch, name) is null)
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CannotDropUser(name), 51);
        }

        var principal = FindDatabasePrincipal(batch, name)!;
        if (principal.PrincipalId == Database.DboPrincipalId)
            throw AtProcedureLine(SimulatedSqlException.CannotDropDatabaseOwnerUser(name), viaDropUser ? "sys.sp_revokedbaccess" : calledAs, 51);
        if (principal.PrincipalId == Database.GuestPrincipalId)
        {
            foreach (var outcome in this.ExecuteDynamicBatch(batch, "revoke connect from guest", preDeclaredVariables: null))
                yield return outcome;
            yield break;
        }
        foreach (var outcome in this.RunStatements(batch, [DropOwnSchemaStatement(batch, principal), "drop user " + QuoteIdentifier(name)]))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_addrole @rolename [, @ownername]</c> is <c>CREATE ROLE … AUTHORIZATION</c> (dbo by
    /// default) and a schema of the role's name.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddRole(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_addrole", calledAs, arguments, AddRoleParameters);
        var name = RequireValidName(values[0]);
        var owner = values[1].IsNull ? "dbo" : values[1].AsString;
        List<string> statements = ["create role " + QuoteIdentifier(name) + " authorization " + QuoteIdentifier(owner)];
        statements.AddRange(OwnSchemaStatements(batch, name, isUser: false));
        foreach (var outcome in this.RunStatements(batch, statements))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_droprole @rolename</c> is <c>DROP ROLE</c>, and the role's own empty schema; a name
    /// nothing answers to is Msg 15151, and <c>public</c> or a fixed role Msg 15150, both from line 28.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropRole(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_droprole", calledAs, arguments, DropRoleParameters);
        var name = RequireValidName(values[0]);
        var principal = FindDatabasePrincipal(batch, name) ?? throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CannotDropRole(name), 28);
        if (principal.TypeCode == "R" && principal.IsFixedRole)
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.CannotDropFixedRole(name), 28);
        foreach (var outcome in this.RunStatements(batch, [principal.TypeCode == "R" ? DropOwnSchemaStatement(batch, principal) : string.Empty, "drop role " + QuoteIdentifier(name)]))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_addsrvrolemember @loginame, @rolename</c> is <c>ALTER SERVER ROLE … ADD MEMBER</c>;
    /// a principal that isn't there is Msg 15007 from line 33 — <c>sa</c> is there, and the
    /// statement refuses it.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpAddSrvRoleMember(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_addsrvrolemember", calledAs, arguments, ServerRoleMemberParameters);
        var login = RequireValidName(values[0]);
        var role = RequireValidName(values[1]);
        if (!batch.Connection.Simulation.TryResolveServerPrincipalId(login, out _))
            throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotAValidLogin(login), 33);
        var statement = "alter server role " + QuoteIdentifier(role) + " add member " + QuoteIdentifier(login);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary><c>sp_dropsrvrolemember @loginame, @rolename</c> is <c>ALTER SERVER ROLE … DROP MEMBER</c>.</summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpDropSrvRoleMember(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_dropsrvrolemember", calledAs, arguments, ServerRoleMemberParameters);
        var login = RequireValidName(values[0]);
        var role = RequireValidName(values[1]);
        var statement = "alter server role " + QuoteIdentifier(role) + " drop member " + QuoteIdentifier(login);
        foreach (var outcome in this.ExecuteDynamicBatch(batch, statement, preDeclaredVariables: null))
            yield return outcome;
    }

    /// <summary>
    /// <c>sp_helprolemember [@rolename]</c> lists each role of the database with its members
    /// — <c>dbo</c> belongs to <c>db_owner</c> — as <c>DbRole</c>, <c>MemberName</c> and
    /// <c>MemberSID</c>, by role and member name. A name that isn't a role is Msg 15409.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpHelpRoleMember(BatchContext batch, string calledAs)
    {
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments("sp_helprolemember", calledAs, arguments, HelpRoleMemberParameters);
        var database = batch.CurrentDatabase;
        DatabasePrincipal? only = null;
        if (!values[0].IsNull)
        {
            only = FindDatabasePrincipal(batch, values[0].AsString);
            if (only is not { TypeCode: "R" })
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.NotARole(values[0].AsString), 9);
        }

        var pairs = new List<(int RoleId, int MemberId)>();
        lock (database.RoleMembers)
            pairs.AddRange(database.RoleMembers);
        if (!pairs.Contains((DbOwnerRoleId, Database.DboPrincipalId)))
            pairs.Add((DbOwnerRoleId, Database.DboPrincipalId));
        var byId = new Dictionary<int, DatabasePrincipal>();
        foreach (var (_, principal) in database.Principals)
            byId[principal.PrincipalId] = principal;
        var collation = database.Collation;
        var rows = new List<(string Role, string Member, byte[]? Sid)>();
        foreach (var (roleId, memberId) in pairs)
        {
            if (!byId.TryGetValue(roleId, out var role) || !byId.TryGetValue(memberId, out var member) || (only is not null && role.PrincipalId != only.PrincipalId))
                continue;
            rows.Add((role.Name, member.Name, BuiltInResources.DatabasePrincipalSid(database, member)));
        }
        rows.Sort((a, b) => collation.Compare(a.Role, b.Role) is var byRole and not 0 ? byRole : collation.Compare(a.Member, b.Member));
        yield return new SimulatedSqlResultSet(HelpRoleMemberSchema, HelpRoleMemberColumnNames,
            rows.ConvertAll(row => new[] { SqlValue.FromSystemName(row.Role), SqlValue.FromSystemName(row.Member), row.Sid is { } sid ? SqlValue.FromVarbinary(sid) : SqlValue.Null(VarbinarySqlType.Get(85)) }));
    }

    /// <summary>
    /// <c>sp_change_users_login @Action [, @UserNamePattern [, @LoginName [, @Password]]]</c>
    /// over users whose login is gone. Real finds them by SID; users and logins are linked by
    /// name here, so an orphan is a user whose <c>FOR LOGIN</c> name no longer names a login.
    /// <c>REPORT</c> lists them, <c>UPDATE_ONE</c> links one to a login and <c>AUTO_FIX</c>
    /// links each matching one to the login of its own name, creating that login from
    /// <c>@Password</c> when there is none. <c>@UserNamePattern</c> is a name here, not a
    /// pattern.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSpChangeUsersLogin(BatchContext batch, string calledAs)
    {
        const string procedure = "sp_change_users_login";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;

        var values = BindSystemProcedureArguments(procedure, calledAs, arguments, ChangeUsersLoginParameters);
        var invalid = SimulatedSqlException.InvalidSystemProcedureOption(procedure);
        var action = values[0].IsNull ? "" : values[0].AsString.TrimEnd(' ').ToUpperInvariant();
        var database = batch.CurrentDatabase;
        var simulation = batch.Connection.Simulation;
        bool IsOrphan(DatabasePrincipal user) =>
            user.TypeCode == "S" && user.LoginName is { } login
            && (simulation.Logins.TryGetValue(login, out var linked) ? linked.PrincipalId != user.LoginPrincipalId : !BuiltInToken.Comparer.Equals(login, "sa"));
        var users = database.Principals.EnumerateValues().OrderBy(static principal => principal.PrincipalId).ToList();

        switch (action)
        {
            case "AUTO_FIX":
                {
                    if (values[1].IsNull || !values[2].IsNull)
                        throw AtSystemProcedureLine(calledAs, invalid, 206);
                    var byUpdating = 0;
                    var byCreating = 0;
                    var target = FindDatabasePrincipal(batch, values[1].AsString);
                    if (target is not null && IsOrphan(target))
                    {
                        if (IsRegisteredLogin(batch, target.Name))
                        {
                            target.LoginName = target.Name;
                            target.LoginPrincipalId = simulation.TryResolveServerPrincipalId(target.Name, out var fixedId) ? fixedId : 0;
                            byUpdating++;
                        }
                        else
                        {
                            if (values[3].IsNull)
                                throw AtSystemProcedureLine(calledAs, invalid, 239);
                            yield return ProcedureMessage(batch, calledAs, 253, 15293,
                                $"Barring a conflict, the row for user '{target.Name}' will be fixed by updating its link to a new login.");
                            foreach (var outcome in this.ExecuteDynamicBatch(batch, "create login " + QuoteIdentifier(target.Name) + " with password = " + QuoteText(values[3].AsString), preDeclaredVariables: null))
                                yield return outcome;
                            target.LoginName = target.Name;
                            target.LoginPrincipalId = simulation.TryResolveServerPrincipalId(target.Name, out var createdId) ? createdId : 0;
                            byCreating++;
                        }
                    }
                    yield return ProcedureMessage(batch, calledAs, 299, 15295, $"The number of orphaned users fixed by updating users was {byUpdating}.");
                    yield return ProcedureMessage(batch, calledAs, 300, 15294, $"The number of orphaned users fixed by adding new logins and then updating users was {byCreating}.");
                    yield break;
                }
            case "REPORT":
                if (!values[1].IsNull || !values[2].IsNull || !values[3].IsNull)
                    throw AtSystemProcedureLine(calledAs, invalid, 59);
                yield return new SimulatedSqlResultSet(ChangeUsersLoginReportSchema, ChangeUsersLoginReportColumnNames,
                    users.FindAll(IsOrphan).ConvertAll(user => new[] { SqlValue.FromSystemName(user.Name), SqlValue.FromVarbinary(BuiltInResources.DatabasePrincipalSid(database, user) ?? []) }));
                yield break;
            case "UPDATE_ONE":
                {
                    if (values[1].IsNull || values[2].IsNull)
                        throw AtSystemProcedureLine(calledAs, invalid, 99);
                    var userName = values[1].AsString;
                    var loginName = values[2].AsString;
                    var user = FindDatabasePrincipal(batch, userName);
                    var orphan = user is not null && IsOrphan(user);
                    if (!IsRegisteredLogin(batch, loginName))
                    {
                        throw orphan
                            ? AtSystemProcedureLine(calledAs, invalid, 128)
                            : AtSystemProcedureLine(calledAs, SimulatedSqlException.UserAbsentOrInvalid(userName), 123);
                    }
                    if (!orphan)
                        throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UserAbsentOrInvalid(userName), 140);
                    var existing = users.Find(other => other.TypeCode == "S" && !IsOrphan(other) && other.LoginName is { } linked && BuiltInToken.Comparer.Equals(linked, loginName));
                    if (existing is not null)
                        throw AtSystemProcedureLine(calledAs, SimulatedSqlException.LoginAlreadyHasUser(existing.Name), 175);
                    user!.LoginName = loginName;
                    user.LoginPrincipalId = simulation.TryResolveServerPrincipalId(loginName, out var relinkedId) ? relinkedId : 0;
                    yield break;
                }
            default:
                if (values[0].IsNull)
                    throw AtSystemProcedureLine(calledAs, invalid, 206);
                throw AtSystemProcedureLine(calledAs, SimulatedSqlException.UnrecognizedChangeUsersLoginAction(values[0].AsString.TrimEnd(' ')), 186);
        }
    }
}
