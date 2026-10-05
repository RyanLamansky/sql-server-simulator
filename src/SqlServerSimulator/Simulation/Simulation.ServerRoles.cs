using System.Collections.Concurrent;
using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// The fixed server roles and their real-SQL-Server <c>principal_id</c>s
    /// (probe-confirmed 2026-07-21, probe6 N1). Seeded into
    /// <c>sys.server_principals</c> as <c>type R</c>, <c>is_fixed_role 1</c>,
    /// <c>owning_principal_id 1</c>. Ids 1 / 2 are the synthetic <c>sa</c> /
    /// <c>public</c> rows; these occupy 3–20, and user server principals
    /// (created logins + custom roles) take ids from 258 via
    /// <see cref="AllocatePrincipalId"/>.
    /// </summary>
    internal static readonly (int Id, string Name)[] FixedServerRoles =
    [
        (3, "sysadmin"),
        (4, "securityadmin"),
        (5, "serveradmin"),
        (6, "setupadmin"),
        (7, "processadmin"),
        (8, "diskadmin"),
        (9, "dbcreator"),
        (10, "bulkadmin"),
        (11, "##MS_ServerStateReader##"),
        (12, "##MS_ServerStateManager##"),
        (13, "##MS_DefinitionReader##"),
        (14, "##MS_DatabaseConnector##"),
        (15, "##MS_DatabaseManager##"),
        (16, "##MS_LoginManager##"),
        (17, "##MS_SecurityDefinitionReader##"),
        (18, "##MS_PerformanceDefinitionReader##"),
        (19, "##MS_ServerSecurityStateReader##"),
        (20, "##MS_ServerPerformanceStateReader##"),
    ];

    /// <summary><c>principal_id</c> of the <c>sysadmin</c> fixed server role.</summary>
    internal const int SysadminRoleId = 3;

    /// <summary><c>principal_id</c> of the <c>dbcreator</c> fixed server role — the membership that carries <c>CREATE DATABASE</c> / <c>DROP DATABASE</c> (probe-confirmed).</summary>
    internal const int DbCreatorRoleId = 9;

    /// <summary><c>principal_id</c> of the <c>serveradmin</c> fixed server role — with <c>sysadmin</c>, the membership the message procedures require.</summary>
    internal const int ServerAdminRoleId = 5;

    /// <summary>Fixed-server-role name → id, for role-name resolution. Keyed by <see cref="BuiltInToken.Comparer"/>.</summary>
    private static readonly FrozenDictionary<string, int> FixedServerRoleIds =
        FixedServerRoles.ToFrozenDictionary(r => r.Name, r => r.Id, BuiltInToken.Comparer);

    /// <summary>
    /// Custom server roles created via <c>CREATE SERVER ROLE</c>, keyed by name.
    /// Projected into <c>sys.server_principals</c> (<c>type R</c>,
    /// <c>is_fixed_role 0</c>). Case-insensitive keys.
    /// </summary>
    internal readonly ConcurrentDictionary<string, ServerRole> ServerRoles = new(BuiltInToken.Comparer);

    /// <summary>
    /// Server-role membership records: each entry is a (role_principal_id,
    /// member_principal_id) pair, seeded with <c>sa</c>'s <c>sysadmin</c>
    /// membership real lists. Populated by <c>ALTER SERVER ROLE … ADD
    /// MEMBER</c>; drained by <c>… DROP MEMBER</c>; surfaced by
    /// <c>sys.server_role_members</c>.
    /// </summary>
    internal readonly List<(int RoleId, int MemberId)> ServerRoleMembers = [(SysadminRoleId, 1)];

    /// <summary>
    /// Server-scope permission grants / denies (class 100) and <c>ON LOGIN::</c>
    /// ones (class 101). Seeded with the two class-100 rows every instance
    /// starts with — <c>sa</c>'s <c>CONNECT SQL</c> and <c>public</c>'s
    /// <c>VIEW ANY DATABASE</c> (probed 2026-09-29 against SQL Server 2025;
    /// its per-endpoint <c>CONNECT</c> rows name endpoints the simulator
    /// doesn't carry); extended by server-scope <c>GRANT</c> / <c>DENY</c> and
    /// <c>CREATE LOGIN</c>'s auto-seeded <c>CONNECT SQL</c>; drained by
    /// <c>REVOKE</c>; surfaced by <c>sys.server_permissions</c>. Server scope
    /// outlives any database, hence the <see cref="Simulation"/>-level home.
    /// </summary>
    internal readonly List<ServerPermission> ServerPermissions =
    [
        new(1, 1, "CONNECT SQL", Permission.ConnectSql.CanonicalTypeCode, PermissionState.Grant),
        new(2, 1, "VIEW ANY DATABASE", Permission.ViewAnyDatabase.CanonicalTypeCode, PermissionState.Grant),
    ];

    /// <summary>
    /// The server permissions each fixed server role carries, indexed by
    /// <c>principal_id</c> − 3 (<see cref="FixedServerRoles"/>' order). Real
    /// keeps them out of <c>sys.server_permissions</c>; these are the direct
    /// grants whose covering closure reproduces what <c>fn_my_permissions(NULL,
    /// 'SERVER')</c> lists for a member of each role (probed 2026-09-29 against
    /// SQL Server 2025) — <c>serveradmin</c>'s <c>ALTER SERVER STATE</c>, for
    /// one, brings the three <c>VIEW SERVER … STATE</c> permissions with it.
    /// </summary>
    private static readonly Permission[][] FixedServerRoleGrants =
    [
        [Permission.ControlServer],                                                  // sysadmin
        [Permission.AlterAnyLogin],                                                  // securityadmin
        [Permission.AlterAnyEndpoint, Permission.AlterResources, Permission.AlterServerState, Permission.AlterSettings, Permission.Shutdown], // serveradmin
        [Permission.AlterAnyLinkedServer],                                           // setupadmin
        [Permission.AlterAnyConnection, Permission.AlterServerState],                // processadmin
        [Permission.AlterResources],                                                 // diskadmin
        [Permission.CreateAnyDatabase],                                              // dbcreator
        [Permission.AdministerBulkOperations],                                       // bulkadmin
        [Permission.ViewServerState],                                                // ##MS_ServerStateReader##
        [Permission.AlterServerState],                                               // ##MS_ServerStateManager##
        [Permission.ViewAnyDatabase, Permission.ViewAnyDefinition],                  // ##MS_DefinitionReader##
        [Permission.ConnectAnyDatabase],                                             // ##MS_DatabaseConnector##
        [Permission.AlterAnyDatabase],                                               // ##MS_DatabaseManager##
        [Permission.AlterAnyLogin],                                                  // ##MS_LoginManager##
        [Permission.ViewAnySecurityDefinition, Permission.ViewAnyCryptographicallySecuredDefinition], // ##MS_SecurityDefinitionReader##
        [Permission.ViewAnyPerformanceDefinition],                                   // ##MS_PerformanceDefinitionReader##
        [Permission.ViewServerSecurityState],                                        // ##MS_ServerSecurityStateReader##
        [Permission.ViewServerPerformanceState],                                     // ##MS_ServerPerformanceStateReader##
    ];

    /// <summary>
    /// Whether a server-scope <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c> with no
    /// <c>ON</c> clause names <paramref name="name"/> as a SERVER-class
    /// permission, which routes the statement to <see cref="ServerPermissions"/>.
    /// </summary>
    internal static bool IsServerScopePermission(string name) => Permission.Resolve(name).IsServerClass;

    /// <summary>The 4-char <c>type</c> code for a server permission — the catalog value, else a first-letter-of-each-word heuristic.</summary>
    private static string ServerPermissionTypeCode(string name)
    {
        var resolved = Permission.Resolve(name);
        if (resolved != Permission.Other)
            return resolved.CanonicalTypeCode;
        var initials = new string([.. name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]))]);
        return initials.Length >= 4 ? initials[..4] : initials;
    }

    /// <summary>Resolves a server-principal name (<c>sa</c> / <c>public</c> / a fixed or custom role / a login) to its <c>principal_id</c>.</summary>
    internal bool TryResolveServerPrincipalId(string name, out int principalId)
    {
        if (BuiltInToken.Comparer.Equals(name, "sa"))
        {
            principalId = 1;
            return true;
        }
        if (BuiltInToken.Comparer.Equals(name, "public"))
        {
            principalId = 2;
            return true;
        }
        if (FixedServerRoleIds.TryGetValue(name, out principalId))
            return true;
        if (this.ServerRoles.TryGetValue(name, out var role))
        {
            principalId = role.PrincipalId;
            return true;
        }
        if (this.Logins.TryGetValue(name, out var login))
        {
            principalId = login.PrincipalId;
            return true;
        }
        principalId = 0;
        return false;
    }

    /// <summary>Whether <paramref name="principalId"/> is a transitive member of the server role <paramref name="roleId"/> (walking <see cref="ServerRoleMembers"/>).</summary>
    internal bool IsServerPrincipalInRole(int principalId, int roleId)
    {
        var closure = new HashSet<int> { principalId };
        bool grew;
        lock (this.ServerRoleMembers)
        {
            do
            {
                grew = false;
                foreach (var (role, member) in this.ServerRoleMembers)
                {
                    if (closure.Contains(member) && closure.Add(role))
                        grew = true;
                }
            }
            while (grew);
        }
        return closure.Contains(roleId);
    }

    /// <summary>
    /// The server principals a login's grants flow through: the login's own
    /// server-principal id (when it resolves), every server role it belongs to
    /// transitively, and <c>public</c> (id 2, which every login carries). The
    /// closure <see cref="HoldsServerPermission"/> scans <see cref="ServerPermissions"/>
    /// against. An unresolvable login still carries <c>public</c>.
    /// </summary>
    internal HashSet<int> BuildServerPrincipalClosure(string loginName)
    {
        var closure = new HashSet<int> { 2 };
        if (this.TryResolveServerPrincipalId(loginName, out var id))
            _ = closure.Add(id);
        bool grew;
        lock (this.ServerRoleMembers)
        {
            do
            {
                grew = false;
                foreach (var (role, member) in this.ServerRoleMembers)
                {
                    if (closure.Contains(member) && closure.Add(role))
                        grew = true;
                }
            }
            while (grew);
        }
        return closure;
    }

    /// <summary>Whether the login is a (transitive) member of the fixed server role with <paramref name="roleId"/> — the <c>dbcreator</c> gate for database DDL.</summary>
    internal bool IsLoginInServerRole(string loginName, int roleId) =>
        this.TryResolveServerPrincipalId(loginName, out var id) && this.IsServerPrincipalInRole(id, roleId);

    /// <summary>Whether the login runs as a <c>sysadmin</c> member — <c>sa</c> always, else transitive <c>sysadmin</c> membership. Maps the login to <c>dbo</c> everywhere.</summary>
    internal bool IsLoginSysadmin(string loginName) =>
        BuiltInToken.Comparer.Equals(loginName, "sa")
        || (this.TryResolveServerPrincipalId(loginName, out var id) && this.IsServerPrincipalInRole(id, SysadminRoleId));

    /// <summary>
    /// Whether <paramref name="loginName"/> holds <paramref name="permission"/> at
    /// server scope. The server-scope counterpart to
    /// <see cref="PermissionChecker.IsGranted"/>: a <c>sysadmin</c> bypass (incl.
    /// <c>sa</c>), then a DENY-first / GRANT scan over the login's
    /// <see cref="BuildServerPrincipalClosure">server-principal closure</see>
    /// with the server-scope covering graph — a stored grant, or a fixed role's
    /// (<see cref="FixedServerRoleGrants"/>), satisfies the request when it
    /// <c>Covers</c> it (so <c>VIEW SERVER STATE</c> answers a <c>VIEW SERVER
    /// PERFORMANCE STATE</c> requirement, and <c>CONTROL SERVER</c> every one).
    /// </summary>
    internal bool HoldsServerPermission(string loginName, Permission permission) =>
        this.HoldsServerPrincipalPermission(loginName, targetPrincipalId: 0, permission, blanketEquivalent: permission);

    /// <summary>
    /// The <c>ON LOGIN::</c> counterpart: whether <paramref name="loginName"/>
    /// holds <paramref name="permission"/> against the server principal
    /// <paramref name="targetPrincipalId"/>. A class-101 row on that exact
    /// target satisfies it, and so does a class-100 row for
    /// <paramref name="blanketEquivalent"/> — the server-wide permission that
    /// covers every login (<c>IMPERSONATE</c> ← <c>IMPERSONATE ANY LOGIN</c>,
    /// <c>VIEW DEFINITION</c> ← <c>VIEW ANY SECURITY DEFINITION</c>, <c>ALTER</c> ←
    /// <c>ALTER ANY LOGIN</c>). DENY over either class binds first, so a
    /// <c>DENY IMPERSONATE ON LOGIN::x</c> beats a server-wide
    /// <c>GRANT IMPERSONATE ANY LOGIN</c> (probe-confirmed).
    /// </summary>
    internal bool HoldsServerPrincipalPermission(string loginName, int targetPrincipalId, Permission permission, Permission blanketEquivalent) =>
        this.IsLoginSysadmin(loginName)
        || this.HoldsServerPrincipalPermission(this.BuildServerPrincipalClosure(loginName), targetPrincipalId, permission, blanketEquivalent);

    /// <summary>
    /// <see cref="HoldsServerPermission(string, Permission)"/> for a login that
    /// isn't <c>sysadmin</c>, over its <see cref="BuildServerPrincipalClosure">server-principal
    /// closure</see> — the form a caller asking about several permissions in
    /// turn uses, building the closure once.
    /// </summary>
    internal bool HoldsServerPermissionInClosure(HashSet<int> closure, Permission permission) =>
        this.HoldsServerPrincipalPermission(closure, targetPrincipalId: 0, permission, blanketEquivalent: permission);

    private bool HoldsServerPrincipalPermission(HashSet<int> closure, int targetPrincipalId, Permission permission, Permission blanketEquivalent)
    {
        lock (this.ServerPermissions)
        {
            // DENY binds first, over both classes — CONTROL SERVER included
            // (probe-confirmed: a DENY VIEW SERVER STATE refuses a CONTROL
            // SERVER grantee the DMVs); a fixed role's grants can't be denied.
            foreach (var row in this.ServerPermissions)
            {
                if (row.State == PermissionState.Deny && closure.Contains(row.GranteeId)
                    && Satisfies(row, targetPrincipalId, permission, blanketEquivalent))
                {
                    return false;
                }
            }
            foreach (var row in this.ServerPermissions)
            {
                if (row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                    && closure.Contains(row.GranteeId)
                    && Satisfies(row, targetPrincipalId, permission, blanketEquivalent))
                {
                    return true;
                }
            }
        }
        foreach (var roleId in closure)
        {
            if (roleId is < SysadminRoleId or > FixedServerPrincipalIdMax)
                continue;
            foreach (var granted in FixedServerRoleGrants[roleId - SysadminRoleId])
            {
                if (granted.Covers(blanketEquivalent, PermissionChecker.ClassServer))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the session's effective identity holds the server permission
    /// <paramref name="permission"/> — the gate every server-scope statement
    /// asks. An identity minted inside one database (<c>EXECUTE AS USER</c>, a
    /// module's own frame, an application role) carries no server permission
    /// at all (probe-confirmed: a <c>CONTROL SERVER</c> login's user, reached
    /// through <c>EXECUTE AS USER</c>, reads nothing it doesn't hold in the
    /// database); the empty-registry dev mode, where every connection is
    /// <c>dbo</c> everywhere, holds everything. A <c>sa</c> session answers
    /// on one name compare.
    /// </summary>
    internal bool SessionHoldsServerPermission(SimulatedDbConnection connection, Permission permission)
    {
        var effective = connection.Security.Effective;
        return !effective.IsDatabaseScoped
            && (BuiltInToken.Comparer.Equals(effective.LoginName, "sa")
                || this.Logins.IsEmptyLockFree()
                || this.HoldsServerPermission(effective.LoginName, permission));
    }

    /// <summary>
    /// Whether one stored row answers a (target, permission) request: a
    /// class-101 row must name the same target and cover
    /// <paramref name="permission"/>; a class-100 row must cover
    /// <paramref name="blanketEquivalent"/>. A <c>targetPrincipalId</c> of 0
    /// is the pure server-scope request, which only class-100 rows answer.
    /// </summary>
    private static bool Satisfies(ServerPermission row, int targetPrincipalId, Permission permission, Permission blanketEquivalent) =>
        row.Class == PermissionChecker.ClassServerPrincipal
            ? targetPrincipalId != 0 && row.MajorId == targetPrincipalId && row.Permission.Covers(permission, PermissionChecker.ClassObject)
            : row.Permission.Covers(blanketEquivalent, PermissionChecker.ClassServer);

    /// <summary>
    /// Whether the session sees <paramref name="database"/>'s row in
    /// <c>sys.databases</c> and gets an answer from <c>DB_ID</c> / <c>DB_NAME</c>
    /// for it: always for <c>master</c>, <c>tempdb</c>, the session's current
    /// database and a database its login owns, and otherwise only with <c>VIEW
    /// ANY DATABASE</c> — which <c>public</c> holds from the start, so what
    /// hides a database is a <c>DENY</c> of it, or an identity minted inside
    /// one database, which holds no server permission (probed 2026-09-29
    /// against SQL Server 2025: an <c>EXECUTE AS USER</c> frame sees three
    /// rows, <c>DB_ID('msdb')</c> NULL).
    /// </summary>
    internal bool CanSeeDatabase(SimulatedDbConnection connection, Database database)
    {
        if (ReferenceEquals(database, connection.CurrentDatabase)
            || BuiltInToken.Comparer.Equals(database.Name, MasterDatabaseName)
            || BuiltInToken.Comparer.Equals(database.Name, TempdbDatabaseName)
            || this.SessionHoldsServerPermission(connection, Permission.ViewAnyDatabase))
        {
            return true;
        }
        var effective = connection.Security.Effective;
        return !effective.IsDatabaseScoped && BuiltInToken.Comparer.Equals(database.OwnerLoginName, effective.LoginName);
    }

    /// <summary>
    /// Whether a <em>restricted</em> session's login may see the
    /// <c>sys.server_principals</c> / <c>sys.sql_logins</c> row for
    /// <paramref name="targetPrincipalId"/>. Real reveals a login row to a
    /// principal that holds any permission on it (probe-confirmed: a bare login
    /// sees only itself; <c>ALTER ON LOGIN::x</c> reveals x; <c>VIEW ANY
    /// DEFINITION</c> reveals every row; a <c>DENY … ON LOGIN::x</c> re-hides x
    /// even under a server-wide grant). <c>sa</c>, <c>public</c> and the fixed
    /// server roles are always visible.
    /// </summary>
    internal bool CanViewServerPrincipal(string loginName, int targetPrincipalId)
    {
        if (targetPrincipalId <= FixedServerPrincipalIdMax)
            return true;
        if (this.TryResolveServerPrincipalId(loginName, out var selfId) && selfId == targetPrincipalId)
            return true;
        // A server role the login belongs to is visible through that membership.
        return this.IsServerPrincipalInRole(selfId, targetPrincipalId)
            || this.HoldsServerPrincipalPermission(loginName, targetPrincipalId, Permission.ViewDefinition, Permission.ViewAnySecurityDefinition)
            || this.HoldsServerPrincipalPermission(loginName, targetPrincipalId, Permission.Alter, Permission.AlterAnyLogin)
            || this.HoldsServerPrincipalPermission(loginName, targetPrincipalId, Permission.Impersonate, Permission.ImpersonateAnyLogin);
    }

    /// <summary>Largest <c>principal_id</c> in the always-visible fixed block — <c>sa</c> (1), <c>public</c> (2) and the 18 fixed server roles (3–20).</summary>
    internal const int FixedServerPrincipalIdMax = 20;

    /// <summary>
    /// Parses <c>CREATE SERVER ROLE name [AUTHORIZATION owner]</c>. Cursor on
    /// entry: the <c>SERVER</c> word (the token after <c>CREATE</c>). The role
    /// takes a fresh id from <see cref="AllocatePrincipalId"/>.
    /// </summary>
    internal static bool TryParseCreateServerRole(ParserContext context)
    {
        context.MoveNextRequired(); // consume SERVER
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Role })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        // AUTHORIZATION owner — parse-and-discard.
        if (context.Token is ReservedKeyword { Keyword: Keyword.Authorization })
            ConsumeToStatementBoundary(context);
        if (context.Batch.IsSkipping)
            return true;
        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.SessionHoldsServerPermission(context.Connection, Permission.CreateServerRole))
            throw SimulatedSqlException.UserDoesNotHavePermission();
        RecordServerSecurityUndo(context.Batch);
        if (simulation.TryResolveServerPrincipalId(name, out _))
            throw SimulatedSqlException.ServerPrincipalAlreadyExists(name);
        _ = simulation.ServerRoles.TryAdd(name,
            new ServerRole(simulation.AllocatePrincipalId(), name, context.Batch.CurrentStatement.UtcNow));
        return true;
    }

    /// <summary>
    /// Parses <c>ALTER SERVER ROLE role { ADD | DROP } MEMBER member</c> against
    /// <see cref="ServerRoleMembers"/>. Cursor on entry: the <c>SERVER</c> word
    /// (the token after <c>ALTER</c>). An unknown role raises Msg 15151
    /// (alter-role variant); an unknown member raises Msg 15151 (add-principal
    /// variant).
    /// </summary>
    internal static bool TryParseAlterServerRole(ParserContext context)
    {
        context.MoveNextRequired(); // consume SERVER
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Role })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name roleNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var roleName = roleNameToken.Value;
        context.MoveNextRequired();
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Add or Keyword.Drop } addOrDrop)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var isAdd = addOrDrop.Keyword == Keyword.Add;
        context.MoveNextRequired();
        if (context.Token is not UnquotedString { Value: var memberWord } || !memberWord.Equals("MEMBER", StringComparison.OrdinalIgnoreCase))
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        if (context.Token is not Name memberNameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var memberName = memberNameToken.Value;
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;
        RecordServerSecurityUndo(context.Batch);
        var simulation = context.Batch.Connection.Simulation;
        // public's membership is fixed, and sa is a member none may change (probed 2026-09-30).
        if (BuiltInToken.Comparer.Equals(roleName, "public"))
            throw SimulatedSqlException.PublicRoleMembershipFixed();
        if (!simulation.TryResolveServerRole(roleName, out var roleId, out var isFixed)
            || !simulation.MayChangeServerRoleMembers(context.Connection, roleId, isFixed))
        {
            throw SimulatedSqlException.CannotAlterServerRole(roleName);
        }
        if (BuiltInToken.Comparer.Equals(memberName, "sa"))
            throw SimulatedSqlException.CannotUseSpecialPrincipal(memberName);
        if (!simulation.TryResolveServerPrincipalId(memberName, out var memberId))
            throw isAdd ? SimulatedSqlException.CannotAddServerPrincipal(memberName) : SimulatedSqlException.CannotDropServerPrincipal(memberName);
        lock (simulation.ServerRoleMembers)
        {
            if (isAdd)
            {
                if (!simulation.ServerRoleMembers.Contains((roleId, memberId)))
                    simulation.ServerRoleMembers.Add((roleId, memberId));
            }
            else
            {
                _ = simulation.ServerRoleMembers.Remove((roleId, memberId));
            }
        }
        return true;
    }

    /// <summary>
    /// Parses <c>DROP SERVER ROLE [IF EXISTS] name</c>. Cursor on entry: the
    /// <c>SERVER</c> word (the token after <c>DROP</c>). Dropping a fixed role
    /// raises Msg 15150; an unknown role (without IF EXISTS) raises Msg 15151.
    /// </summary>
    internal static bool TryParseDropServerRole(ParserContext context)
    {
        context.MoveNextRequired(); // consume SERVER
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Role })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var ifExists = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.If })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Exists })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            ifExists = true;
            context.MoveNextRequired();
        }
        if (context.Token is not Name nameToken)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        var name = nameToken.Value;
        context.MoveNextOptional();
        if (context.Batch.IsSkipping)
            return true;
        RecordServerSecurityUndo(context.Batch);
        var simulation = context.Batch.Connection.Simulation;
        if (FixedServerRoleIds.ContainsKey(name))
            throw SimulatedSqlException.CannotDropFixedServerRole(name);
        if (simulation.ServerRoles.ContainsKey(name) && !simulation.SessionHoldsServerPermission(context.Connection, Permission.AlterAnyServerRole))
            throw SimulatedSqlException.CannotDropServerRole(name);
        if (!simulation.ServerRoles.TryRemove(name, out var removed))
            return ifExists ? true : throw SimulatedSqlException.CannotDropServerRole(name);
        lock (simulation.ServerRoleMembers)
            _ = simulation.ServerRoleMembers.RemoveAll(m => m.RoleId == removed.PrincipalId || m.MemberId == removed.PrincipalId);
        return true;
    }

    /// <summary>
    /// Whether the session may add members to, or drop them from, the server
    /// role <paramref name="roleId"/>: a custom role takes <c>ALTER ANY SERVER
    /// ROLE</c>, while a fixed role is closed to everything short of
    /// <c>sysadmin</c> except its own members — neither <c>CONTROL SERVER</c>
    /// nor <c>ALTER ANY SERVER ROLE</c> nor <c>securityadmin</c> adds a
    /// member to one, and a <c>dbcreator</c> member adds to <c>dbcreator</c>
    /// (probed 2026-09-29 against SQL Server 2025). A refusal is the same
    /// Msg 15151 a missing role earns.
    /// </summary>
    private bool MayChangeServerRoleMembers(SimulatedDbConnection connection, int roleId, bool isFixed)
    {
        var effective = connection.Security.Effective;
        if (!isFixed)
            return this.SessionHoldsServerPermission(connection, Permission.AlterAnyServerRole);
        return !effective.IsDatabaseScoped
            && (this.Logins.IsEmptyLockFree()
                || this.IsLoginSysadmin(effective.LoginName)
                || this.IsLoginInServerRole(effective.LoginName, roleId));
    }

    /// <summary>Resolves a server-role name (fixed or custom) to its id, reporting whether it's a fixed role; false for a non-role name.</summary>
    internal bool TryResolveServerRole(string name, out int roleId, out bool isFixed)
    {
        if (FixedServerRoleIds.TryGetValue(name, out roleId))
        {
            isFixed = true;
            return true;
        }
        if (this.ServerRoles.TryGetValue(name, out var role))
        {
            roleId = role.PrincipalId;
            isFixed = false;
            return true;
        }
        roleId = 0;
        isFixed = false;
        return false;
    }

    /// <summary>
    /// Applies a server-scope <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c> against
    /// <see cref="ServerPermissions"/>. Legal only when the current database is
    /// <c>master</c> (Msg 4621 elsewhere). Server-scope DENY replaces the prior
    /// GRANT row (unlike database scope, where G and D coexist — probe6 N4).
    /// </summary>
    internal static void ApplyServerScopeGrant(ParserContext context, PermissionStatementKind kind, List<string> permissions, List<string> granteeNames, string? loginSecurableName = null)
    {
        if (!BuiltInToken.Comparer.Equals(context.CurrentDatabase.Name, MasterDatabaseName))
            throw SimulatedSqlException.ServerPermissionsMasterOnly();
        var simulation = context.Batch.Connection.Simulation;
        var grantorId = context.Connection.Security.Effective.DatabasePrincipalId;

        // ON LOGIN::x → class 101 against the named login's principal_id; the
        // ON-less / ON SERVER:: form → class 100, major 0.
        var permClass = PermissionChecker.ClassServer;
        var majorId = 0;
        if (loginSecurableName is not null)
        {
            if (!simulation.TryResolveServerPrincipalId(loginSecurableName, out majorId))
                throw SimulatedSqlException.CannotFindLogin(loginSecurableName);
            permClass = PermissionChecker.ClassServerPrincipal;
        }

        var grantee = new List<int>(granteeNames.Count);
        foreach (var granteeName in granteeNames)
        {
            if (!simulation.TryResolveServerPrincipalId(granteeName, out var id))
                throw SimulatedSqlException.CannotFindLogin(granteeName);
            grantee.Add(id);
        }
        RecordServerSecurityUndo(context.Batch);
        lock (simulation.ServerPermissions)
        {
            foreach (var granteeId in grantee)
            {
                foreach (var permName in permissions)
                {
                    // Class-101 permissions (IMPERSONATE / ALTER / VIEW
                    // DEFINITION / CONTROL) are the ordinary catalog names, so
                    // their codes come from PermissionCatalog; the class-100
                    // server-permission names have their own table. Either way
                    // a catalog name projects its canonical uppercase spelling
                    // regardless of the GRANT's casing, matching real; an
                    // off-catalog name keeps its raw text.
                    var resolved = Permission.Resolve(permName);
                    var canonical = resolved == Permission.Other ? permName.Trim() : resolved.CanonicalName;
                    var code = permClass == PermissionChecker.ClassServerPrincipal && resolved != Permission.Other
                        ? resolved.CanonicalTypeCode
                        : ServerPermissionTypeCode(canonical);
                    bool Same(ServerPermission p) =>
                        p.GranteeId == granteeId && p.Class == permClass && p.MajorId == majorId
                        && BuiltInToken.Comparer.Equals(p.TypeCode.Trim(), code.Trim());
                    switch (kind)
                    {
                        case PermissionStatementKind.Grant:
                            _ = simulation.ServerPermissions.RemoveAll(p => Same(p) && p.State is PermissionState.Grant or PermissionState.GrantWithGrantOption or PermissionState.Deny);
                            simulation.ServerPermissions.Add(new ServerPermission(granteeId, grantorId, canonical, code, PermissionState.Grant, permClass, majorId));
                            break;
                        case PermissionStatementKind.Deny:
                            // Server-scope DENY replaces the prior G row (N4).
                            _ = simulation.ServerPermissions.RemoveAll(Same);
                            simulation.ServerPermissions.Add(new ServerPermission(granteeId, grantorId, canonical, code, PermissionState.Deny, permClass, majorId));
                            break;
                        default:
                            _ = simulation.ServerPermissions.RemoveAll(Same);
                            break;
                    }
                }
            }
        }
    }
}

/// <summary>One custom server role created via <c>CREATE SERVER ROLE</c>.</summary>
internal sealed class ServerRole(int principalId, string name, DateTime createDate)
{
    public readonly int PrincipalId = principalId;
    public readonly string Name = name;
    public readonly DateTime CreateDate = createDate;
}

/// <summary>
/// One server-scope permission grant / deny row: class 100 (<c>SERVER</c> — the
/// ON-less / <c>ON SERVER::</c> form, <see cref="MajorId"/> 0) or class 101
/// (<c>SERVER_PRINCIPAL</c> — the <c>ON LOGIN::</c> form, <see cref="MajorId"/>
/// the target login's <c>principal_id</c>).
/// </summary>
internal sealed class ServerPermission(int granteeId, int grantorId, string permissionName, string typeCode, PermissionState state, byte @class = PermissionChecker.ClassServer, int majorId = 0)
{
    public readonly int GranteeId = granteeId;
    public readonly int GrantorId = grantorId;
    public readonly string PermissionName = permissionName;
    public readonly string TypeCode = typeCode;
    public readonly PermissionState State = state;

    /// <summary><c>sys.server_permissions.class</c>: 100 = SERVER, 101 = SERVER_PRINCIPAL.</summary>
    public readonly byte Class = @class;

    /// <summary><c>sys.server_permissions.major_id</c>: 0 at class 100, the target login's <c>principal_id</c> at class 101.</summary>
    public readonly int MajorId = majorId;

    /// <summary>The resolved permission enum (<see cref="Permission.Other"/> for the long tail the state checker never matches), so <see cref="Simulation.HoldsServerPermission"/> compares by enum + covering graph rather than by name.</summary>
    public readonly Permission Permission = Permission.Resolve(permissionName);
}
