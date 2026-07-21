using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

/// <summary>
/// One object a statement reads (table / view / TVF), recorded at parse time
/// on <see cref="Parser.Selection.ReferencedSecurables"/> so the SELECT
/// permission check can run at execution against the current principal — the
/// list is principal-independent, so it caches with the plan.
/// </summary>
internal readonly struct ReferencedSecurable(int objectId, int schemaId, string objectName, string schemaName, string permission = "SELECT")
{
    public readonly int ObjectId = objectId;
    public readonly int SchemaId = schemaId;
    public readonly string ObjectName = objectName;
    public readonly string SchemaName = schemaName;

    /// <summary>The permission this read requires — <c>SELECT</c> for tables / views / TVFs, <c>EXECUTE</c> for a scalar UDF invoked in the query.</summary>
    public readonly string Permission = permission;
}

/// <summary>
/// Execution-time permission enforcement — the thin layer between the
/// statement dispatch / row sources and <see cref="PermissionChecker"/>. Every
/// entry point short-circuits before any allocation when the effective
/// principal is <c>dbo</c> or when the batch is inside a static module body
/// (ownership chaining), so a <c>dbo</c> session sees zero added cost.
/// </summary>
internal static class PermissionEnforcement
{
    /// <summary>Whether permission checks apply for this batch: a genuinely restricted principal, not inside an ownership-chained module body.</summary>
    internal static bool Applies(BatchContext batch) =>
        batch.EnforcesPermissions && !batch.Connection.Security.EffectiveIsDbo;

    /// <summary>Checks SELECT on every read securable a <see cref="Parser.Selection"/> recorded; throws Msg 229 on the first denial.</summary>
    internal static void CheckReadSources(BatchContext batch, List<ReferencedSecurable>? securables)
    {
        if (securables is null || securables.Count == 0 || !Applies(batch))
            return;
        var database = batch.CurrentDatabase;
        var principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
        foreach (var s in securables)
        {
            if (!PermissionChecker.IsGranted(database, principalId, Permission.Resolve(s.Permission), PermissionChecker.ClassObject, s.ObjectId, s.SchemaId))
                throw SimulatedSqlException.PermissionDenied(s.Permission.ToUpperInvariant(), s.ObjectName, database.Name, s.SchemaName);
        }
    }

    /// <summary>Checks one permission on one object; throws Msg 229 (with optional Procedure attribution) on denial. No-op when checks don't apply.</summary>
    internal static void CheckObject(BatchContext batch, string permission, int objectId, int schemaId, string objectName, string schemaName, string procedure = "")
    {
        if (!Applies(batch))
            return;
        var database = batch.CurrentDatabase;
        var principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
        if (!PermissionChecker.IsGranted(database, principalId, Permission.Resolve(permission), PermissionChecker.ClassObject, objectId, schemaId))
            throw SimulatedSqlException.PermissionDenied(permission.ToUpperInvariant(), objectName, database.Name, schemaName, procedure);
    }

    /// <summary>Checks a permission on a heap table (deriving its schema name); no-op when checks don't apply.</summary>
    internal static void CheckTable(BatchContext batch, string permission, Storage.HeapTable table)
    {
        if (!Applies(batch))
            return;
        CheckObject(batch, permission, table.ObjectId, table.SchemaId, table.Name, SchemaNameFor(batch.CurrentDatabase, table.SchemaId));
    }

    /// <summary>Checks a permission on a view; no-op when checks don't apply.</summary>
    internal static void CheckView(BatchContext batch, string permission, Schemas.View view)
    {
        if (!Applies(batch))
            return;
        CheckObject(batch, permission, view.ObjectId, view.SchemaId, view.Name, view.Schema.Name);
    }

    private static string SchemaNameFor(Database database, int schemaId)
    {
        foreach (var schema in database.Schemas.Values)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>Whether the effective principal holds a database-scope permission (CREATE TABLE gate, CONNECT, etc.). Always true for dbo / module bodies.</summary>
    internal static bool HasDatabasePermission(BatchContext batch, string permission) =>
        !Applies(batch)
        || PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
            Permission.Resolve(permission), PermissionChecker.ClassDatabase, 0, 0);
}

/// <summary>
/// The effective-permission engine. Answers "may the effective principal
/// perform &lt;permission&gt; on &lt;securable&gt;?" against a database's
/// <see cref="Database.Permissions"/> / <see cref="Database.RoleMembers"/>
/// plus the fixed-role virtual permissions. Execution-time only — nothing it
/// computes is baked into a cached plan (the plan cache is shared across
/// principals), so every call re-reads the current effective principal.
/// </summary>
/// <remarks>
/// Algorithm (probe-confirmed against SQL Server 2025, 2026-07-21):
/// <list type="number">
/// <item>Compute the principal closure: the effective principal + every role
/// it belongs to transitively (nested roles) + <c>public</c> (id 0).</item>
/// <item>DENY binds first: an explicit <c>D</c> row (or a deny-role) matching
/// the permission or any covering permission at any scope denies regardless of
/// grants. Explicit DENY binds even a <c>db_owner</c> member.</item>
/// <item>GRANT test: a <c>G</c>/<c>W</c> row (or a grant-role) matching the
/// permission or a covering permission at object → schema → database scope.</item>
/// </list>
/// The <see cref="Database.DboPrincipalId"/> bypass is handled by the callers
/// (<see cref="SessionSecurityContext.EffectiveIsDbo"/>) before this class is
/// ever reached, so the engine only runs for genuinely restricted principals.
/// </remarks>
internal static class PermissionChecker
{
    // Fixed database-role principal ids (real SQL Server convention).
    private const int DbOwner = 16384;
    private const int DbDdlAdmin = 16387;
    private const int DbDataReader = 16390;
    private const int DbDataWriter = 16391;
    private const int DbDenyDataReader = 16392;
    private const int DbDenyDataWriter = 16393;

    // Securable classes (sys.database_permissions.class).
    internal const byte ClassDatabase = 0;
    internal const byte ClassObject = 1;
    internal const byte ClassSchema = 3;
    internal const byte ClassDatabasePrincipal = 4;

    /// <summary>Whether the effective principal holds <paramref name="permission"/> on the described securable. An off-catalog (<see cref="Permission.Other"/>) request is never satisfied.</summary>
    internal static bool IsGranted(Database database, int principalId, Permission permission, byte securableClass, int majorId, int schemaId)
    {
        if (permission == Permission.Other)
            return false;

        var closure = BuildClosure(database, principalId);
        var satisfiers = BuildSatisfiers(permission, securableClass, majorId, schemaId);

        // DENY binds first — explicit D rows, then the deny-roles.
        if (HasMatchingRow(database, closure, satisfiers, deny: true))
            return false;
        if (permission.Category == PermissionCategory.Read && closure.Contains(DbDenyDataReader))
            return false;
        if (permission.Category == PermissionCategory.Write && closure.Contains(DbDenyDataWriter))
            return false;

        // GRANT test — explicit G/W rows, then the grant-roles.
        return HasMatchingRow(database, closure, satisfiers, deny: false)
            || closure.Contains(DbOwner)
            || (permission.Category == PermissionCategory.Read && closure.Contains(DbDataReader))
            || (permission.Category == PermissionCategory.Write && closure.Contains(DbDataWriter))
            || (permission.Category == PermissionCategory.Ddl && closure.Contains(DbDdlAdmin));
    }

    /// <summary>Whether the effective principal is a member of <paramref name="role"/> (transitively), or the role is <c>public</c> (everyone).</summary>
    internal static bool IsRoleMember(Database database, int principalId, DatabasePrincipal role) =>
        role.PrincipalId == 0 || BuildClosure(database, principalId).Contains(role.PrincipalId);

    /// <summary>
    /// The effective principal + every role it belongs to transitively +
    /// <c>public</c> (id 0). Fixed-role memberships live in
    /// <see cref="Database.RoleMembers"/> alongside user roles, so this
    /// naturally folds in <c>db_owner</c> / <c>db_datareader</c> / etc.
    /// </summary>
    private static HashSet<int> BuildClosure(Database database, int principalId)
    {
        var closure = new HashSet<int> { principalId, 0 };
        // Fixed point over role membership: keep adding roles whose member is
        // already in the closure until nothing new appears (handles nested
        // roles at arbitrary depth).
        bool grew;
        do
        {
            grew = false;
            foreach (var (roleId, memberId) in database.RoleMembers)
            {
                if (closure.Contains(memberId) && closure.Add(roleId))
                    grew = true;
            }
        }
        while (grew);
        return closure;
    }

    private static bool HasMatchingRow(Database database, HashSet<int> closure, List<(byte Class, int MajorId, Permission Permission)> satisfiers, bool deny)
    {
        foreach (var row in database.Permissions)
        {
            var stateMatches = deny ? row.State == PermissionState.Deny : row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption;
            if (!stateMatches || !closure.Contains(row.GranteePrincipalId))
                continue;
            foreach (var (cls, majorId, permission) in satisfiers)
            {
                if (row.Class == cls && row.MajorId == majorId && row.Permission == permission)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The set of (class, major_id, permission) tuples a G/W (or D) row could
    /// carry that would satisfy (or deny) the request: the permission itself
    /// plus every covering permission, walked up the object → schema →
    /// database scope chain.
    /// </summary>
    private static List<(byte Class, int MajorId, Permission Permission)> BuildSatisfiers(Permission permission, byte securableClass, int majorId, int schemaId)
    {
        var result = new List<(byte, int, Permission)>();
        switch (securableClass)
        {
            case ClassObject:
                AddScope(result, ClassObject, majorId, permission);
                AddScope(result, ClassSchema, schemaId, permission);
                AddScope(result, ClassDatabase, 0, permission);
                break;
            case ClassSchema:
                AddScope(result, ClassSchema, majorId, permission);
                AddScope(result, ClassDatabase, 0, permission);
                break;
            case ClassDatabasePrincipal:
                AddScope(result, ClassDatabasePrincipal, majorId, permission);
                break;
            default:
                AddScope(result, ClassDatabase, 0, permission);
                break;
        }
        return result;
    }

    private static void AddScope(List<(byte, int, Permission)> result, byte cls, int majorId, Permission permission)
    {
        Permission? current = permission;
        while (current is Permission p)
        {
            result.Add((cls, majorId, p));
            current = p.Covering(cls);
        }
    }
}
