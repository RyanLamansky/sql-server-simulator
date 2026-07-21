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
            if (!PermissionChecker.IsGranted(database, principalId, s.Permission, PermissionChecker.ClassObject, s.ObjectId, s.SchemaId))
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
        if (!PermissionChecker.IsGranted(database, principalId, permission, PermissionChecker.ClassObject, objectId, schemaId))
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
            permission, PermissionChecker.ClassDatabase, 0, 0);
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

    private const string Control = "CONTROL";

    /// <summary>Whether the effective principal holds <paramref name="permission"/> on the described securable.</summary>
    internal static bool IsGranted(Database database, int principalId, string permission, byte securableClass, int majorId, int schemaId)
    {
        var closure = BuildClosure(database, principalId);
        var satisfiers = BuildSatisfiers(permission, securableClass, majorId, schemaId);

        // DENY binds first — explicit D rows, then the deny-roles.
        if (HasMatchingRow(database, closure, satisfiers, deny: true))
            return false;
        if (IsReadPermission(permission) && closure.Contains(DbDenyDataReader))
            return false;
        if (IsWritePermission(permission) && closure.Contains(DbDenyDataWriter))
            return false;

        // GRANT test — explicit G/W rows, then the grant-roles.
        return HasMatchingRow(database, closure, satisfiers, deny: false)
            || closure.Contains(DbOwner)
            || (IsReadPermission(permission) && closure.Contains(DbDataReader))
            || (IsWritePermission(permission) && closure.Contains(DbDataWriter))
            || (IsDdlPermission(permission) && closure.Contains(DbDdlAdmin));
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

    private static bool HasMatchingRow(Database database, HashSet<int> closure, List<(byte Class, int MajorId, string Permission)> satisfiers, bool deny)
    {
        foreach (var row in database.Permissions)
        {
            var stateMatches = deny ? row.State == "D" : row.State is "G" or "W";
            if (!stateMatches || !closure.Contains(row.GranteePrincipalId))
                continue;
            foreach (var (cls, majorId, permission) in satisfiers)
            {
                if (row.Class == cls && row.MajorId == majorId
                    && string.Equals(row.PermissionName, permission, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
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
    private static List<(byte Class, int MajorId, string Permission)> BuildSatisfiers(string permission, byte securableClass, int majorId, int schemaId)
    {
        var result = new List<(byte, int, string)>();
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

    private static void AddScope(List<(byte, int, string)> result, byte cls, int majorId, string permission)
    {
        var current = permission;
        while (current is not null)
        {
            result.Add((cls, majorId, current));
            current = Covering(cls, current);
        }
    }

    /// <summary>
    /// The immediate covering permission for (<paramref name="cls"/>,
    /// <paramref name="permission"/>) — the permission that, when granted,
    /// implies this one. Chains to <c>CONTROL</c> for most; the exceptions
    /// (OBJECT SELECT ← RECEIVE ← CONTROL, DATABASE CREATE TABLE ← ALTER ←
    /// CONTROL) come straight from <c>sys.fn_builtin_permissions</c>. Returns
    /// <see langword="null"/> at <c>CONTROL</c> (the top).
    /// </summary>
    private static string? Covering(byte cls, string permission)
    {
        Span<char> buf = stackalloc char[permission.Length];
        _ = permission.AsSpan().ToUpperInvariant(buf);
        return (cls, upper: buf.ToString()) switch
        {
            (_, "CONTROL") => null,
            (ClassObject, "SELECT") => "RECEIVE",
            (ClassObject, "RECEIVE") => Control,
            (ClassDatabase, "CREATE TABLE") => "ALTER",
            _ => Control,
        };
    }

    private static bool IsReadPermission(string permission) =>
        string.Equals(permission, "SELECT", StringComparison.OrdinalIgnoreCase);

    private static bool IsWritePermission(string permission) =>
        permission.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
        || permission.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
        || permission.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

    private static bool IsDdlPermission(string permission) =>
        permission.Equals("ALTER", StringComparison.OrdinalIgnoreCase)
        || permission.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase);
}
