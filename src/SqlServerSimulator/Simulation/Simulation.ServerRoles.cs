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
    /// member_principal_id) pair. Populated by <c>ALTER SERVER ROLE … ADD
    /// MEMBER</c>; drained by <c>… DROP MEMBER</c>; surfaced by
    /// <c>sys.server_role_members</c>.
    /// </summary>
    internal readonly List<(int RoleId, int MemberId)> ServerRoleMembers = [];

    /// <summary>
    /// Server-scope permission grants / denies (class 100). Populated by
    /// server-scope <c>GRANT</c> / <c>DENY</c> and <c>CREATE LOGIN</c>'s
    /// auto-seeded <c>CONNECT SQL</c>; drained by <c>REVOKE</c>; surfaced by
    /// <c>sys.server_permissions</c>. Server scope outlives any database, hence
    /// the <see cref="Simulation"/>-level home.
    /// </summary>
    internal readonly List<ServerPermission> ServerPermissions = [];

    /// <summary>
    /// Canonical server-permission name → 4-char <c>type</c> code, imported from
    /// the SERVER-class rows of <c>sys.fn_builtin_permissions</c>. The set the
    /// server-scope GRANT path recognizes; an off-table name falls back to the
    /// first-letter-of-each-word heuristic. Keyed by <see cref="BuiltInToken.Comparer"/>.
    /// </summary>
    private static readonly FrozenDictionary<string, string> ServerPermissionCodes =
        new Dictionary<string, string>(BuiltInToken.Comparer)
        {
            ["ADMINISTER BULK OPERATIONS"] = "ADBO",
            ["ALTER ANY CONNECTION"] = "ALCO",
            ["ALTER ANY CREDENTIAL"] = "ALCD",
            ["ALTER ANY DATABASE"] = "ALDB",
            ["ALTER ANY ENDPOINT"] = "ALHE",
            ["ALTER ANY EVENT NOTIFICATION"] = "ALES",
            ["ALTER ANY LINKED SERVER"] = "ALLS",
            ["ALTER ANY LOGIN"] = "ALLG",
            ["ALTER ANY SERVER AUDIT"] = "ALAA",
            ["ALTER ANY SERVER ROLE"] = "ALSR",
            ["ALTER RESOURCES"] = "ALRS",
            ["ALTER SERVER STATE"] = "ALSS",
            ["ALTER SETTINGS"] = "ALST",
            ["ALTER TRACE"] = "ALTR",
            ["AUTHENTICATE SERVER"] = "AUTH",
            ["CONNECT ANY DATABASE"] = "CADB",
            ["CONNECT SQL"] = "COSQ",
            ["CONTROL SERVER"] = "CL",
            ["CREATE ANY DATABASE"] = "CRDB",
            ["CREATE DDL EVENT NOTIFICATION"] = "CRDE",
            ["CREATE ENDPOINT"] = "CRHE",
            ["CREATE LOGIN"] = "CRLG",
            ["CREATE SERVER ROLE"] = "CRSR",
            ["CREATE TRACE EVENT NOTIFICATION"] = "CRTE",
            ["EXTERNAL ACCESS ASSEMBLY"] = "XA",
            ["IMPERSONATE ANY LOGIN"] = "IAL",
            ["SELECT ALL USER SECURABLES"] = "SUS",
            ["SHUTDOWN"] = "SHDN",
            ["UNSAFE ASSEMBLY"] = "XU",
            ["VIEW ANY CRYPTOGRAPHICALLY SECURED DEFINITION"] = "VACD",
            ["VIEW ANY DATABASE"] = "VWDB",
            ["VIEW ANY DEFINITION"] = "VWAD",
            ["VIEW ANY ERROR LOG"] = "VEL",
            ["VIEW ANY PERFORMANCE DEFINITION"] = "VAP",
            ["VIEW ANY SECURITY DEFINITION"] = "VAS",
            ["VIEW SERVER PERFORMANCE STATE"] = "VSP",
            ["VIEW SERVER SECURITY STATE"] = "VSS",
            ["VIEW SERVER STATE"] = "VWSS",
        }.ToFrozenDictionary(BuiltInToken.Comparer);

    /// <summary>Whether <paramref name="name"/> is a recognized server-scope permission (routes an ON-less GRANT to the server-scope path).</summary>
    internal static bool IsServerScopePermission(string name) => ServerPermissionCodes.ContainsKey(name.Trim());

    /// <summary>The 4-char <c>type</c> code for a server permission — the catalog value, else a first-letter-of-each-word heuristic (space-padded to 4).</summary>
    private static string ServerPermissionTypeCode(string name)
    {
        var trimmed = name.Trim();
        if (ServerPermissionCodes.TryGetValue(trimmed, out var code))
            return code;
        var initials = new string([.. trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]))]);
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

    /// <summary>Whether the login runs as a <c>sysadmin</c> member — <c>sa</c> always, else transitive <c>sysadmin</c> membership. Maps the login to <c>dbo</c> everywhere.</summary>
    internal bool IsLoginSysadmin(string loginName) =>
        BuiltInToken.Comparer.Equals(loginName, "sa")
        || (this.TryResolveServerPrincipalId(loginName, out var id) && this.IsServerPrincipalInRole(id, SysadminRoleId));

    /// <summary>
    /// Whether <paramref name="loginName"/> holds <paramref name="permission"/> at
    /// server scope. The server-scope counterpart to
    /// <see cref="PermissionChecker.IsGranted"/>: a <c>sysadmin</c> bypass (incl.
    /// <c>sa</c>; sysadmin logins already map to <c>dbo</c> and never reach a gate,
    /// so this is belt-and-suspenders), then a DENY-first / GRANT scan over the
    /// login's <see cref="BuildServerPrincipalClosure">server-principal closure</see>
    /// with the server-scope covering graph — a stored grant satisfies the request
    /// when it <c>Covers</c> it (so <c>VIEW SERVER STATE</c> answers a <c>VIEW
    /// SERVER PERFORMANCE STATE</c> requirement).
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
    /// <c>VIEW DEFINITION</c> ← <c>VIEW ANY DEFINITION</c>, <c>ALTER</c> ←
    /// <c>ALTER ANY LOGIN</c>). DENY over either class binds first, so a
    /// <c>DENY IMPERSONATE ON LOGIN::x</c> beats a server-wide
    /// <c>GRANT IMPERSONATE ANY LOGIN</c> (probe-confirmed).
    /// </summary>
    internal bool HoldsServerPrincipalPermission(string loginName, int targetPrincipalId, Permission permission, Permission blanketEquivalent)
    {
        if (this.IsLoginSysadmin(loginName))
            return true;

        var closure = this.BuildServerPrincipalClosure(loginName);
        lock (this.ServerPermissions)
        {
            // DENY binds first, over both classes.
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
        return false;
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
            || this.HoldsServerPrincipalPermission(loginName, targetPrincipalId, Permission.ViewDefinition, Permission.ViewAnyDefinition)
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
        var simulation = context.Batch.Connection.Simulation;
        if (!simulation.TryResolveServerRole(roleName, out var roleId, out _))
            throw SimulatedSqlException.CannotAlterServerRole(roleName);
        if (!simulation.TryResolveServerPrincipalId(memberName, out var memberId))
            throw SimulatedSqlException.CannotAddServerPrincipal(memberName);
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
        var simulation = context.Batch.Connection.Simulation;
        if (FixedServerRoleIds.ContainsKey(name))
            throw SimulatedSqlException.CannotDropFixedServerRole(name);
        if (!simulation.ServerRoles.TryRemove(name, out var removed))
            return ifExists ? true : throw SimulatedSqlException.CannotDropServerRole(name);
        lock (simulation.ServerRoleMembers)
            _ = simulation.ServerRoleMembers.RemoveAll(m => m.RoleId == removed.PrincipalId || m.MemberId == removed.PrincipalId);
        return true;
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
