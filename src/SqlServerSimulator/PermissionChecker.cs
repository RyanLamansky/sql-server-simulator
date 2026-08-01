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
/// One column-grantable object a statement touches — a base table or a view —
/// together with the 1-based column ordinals it reads or assigns. Query reads
/// are recorded at parse time on <see cref="Parser.Selection.ReadColumnsByObject"/>
/// (keyed by <see cref="Schemas.SchemaObject.ObjectId"/>); the UPDATE / DELETE
/// paths, which don't ride a <see cref="Parser.Selection"/> plan, build one
/// inline per check. An empty <see cref="Ordinals"/> set on a query read means
/// the object was touched without naming a column (<c>COUNT(*)</c> /
/// <c>SELECT 1</c>), which real checks as requiring the permission on
/// <em>every</em> column.
/// </summary>
/// <remarks>
/// A reference that arrived through a synonym never gets one of these: a
/// synonym is an entity-level securable that takes no column list at all
/// (Msg 1020), so such a reference is checked object-grain against the synonym.
/// </remarks>
internal sealed class ColumnReadTarget(Schemas.SchemaObject securable, Storage.HeapColumn[] columns)
{
    public readonly Schemas.SchemaObject Securable = securable;

    /// <summary>The securable's columns in ordinal order — a table's columns or a view's projection columns.</summary>
    public readonly Storage.HeapColumn[] Columns = columns;

    /// <summary>The 1-based ordinals touched so far (<c>sys.columns.column_id</c>).</summary>
    public readonly HashSet<int> Ordinals = [];

    public ColumnReadTarget(Storage.HeapTable table)
        : this(table, table.Columns)
    {
    }

    public ColumnReadTarget(Schemas.View view)
        : this(view, view.OutputColumns)
    {
    }

    /// <summary>
    /// Resolves a column reference's leaf name to its ordinal and records it. An
    /// unresolved name — a correlated or aliased reference this target doesn't
    /// own — is ignored, so recording never alters query semantics.
    /// </summary>
    public void Add(MultiPartName name) => this.Add(name.Leaf);

    public void Add(string columnName)
    {
        for (var i = 0; i < this.Columns.Length; i++)
        {
            if (BuiltInToken.Equals(this.Columns[i].Name, columnName))
            {
                _ = this.Ordinals.Add(i + 1);
                return;
            }
        }
    }

    /// <summary>
    /// The ordinals a check must visit, ascending — the recorded set, or every
    /// column when nothing was named (the <c>COUNT(*)</c> shape).
    /// </summary>
    public int[] OrdinalsToCheck()
    {
        if (this.Ordinals.Count == 0)
        {
            var all = new int[this.Columns.Length];
            for (var i = 0; i < all.Length; i++)
                all[i] = i + 1;
            return all;
        }
        var ordinals = new int[this.Ordinals.Count];
        this.Ordinals.CopyTo(ordinals);
        Array.Sort(ordinals);
        return ordinals;
    }
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

    /// <summary>
    /// Whether metadata-visibility filtering applies for this batch: a genuinely
    /// restricted session principal that lacks the full-visibility bypass. Unlike
    /// <see cref="Applies"/> this is NOT suppressed inside a module body — metadata
    /// visibility is a property of the session principal, not the execution frame,
    /// so it gates on the session principal alone. Short-circuits on
    /// <see cref="SessionSecurityContext.EffectiveIsDbo"/> before any allocation,
    /// so a <c>dbo</c> session pays a single bool read.
    /// </summary>
    internal static bool MetadataVisibilityApplies(BatchContext batch)
    {
        var security = batch.Connection.Security;
        return !security.EffectiveIsDbo
            && !PermissionChecker.HasFullMetadataVisibility(batch.CurrentDatabase, security.Effective.DatabasePrincipalId);
    }

    /// <summary>
    /// Checks the read permission on every securable a <see cref="Parser.Selection"/>
    /// recorded; throws on the first denial. A SELECT read whose column ordinals
    /// were tracked (<paramref name="readColumns"/> — base tables and views alike)
    /// is checked column-by-column (Msg 230 naming the first inaccessible column,
    /// or Msg 229 when the principal has no access to the object at all); TVFs,
    /// scalar-UDF EXECUTE, and any reference that arrived through a synonym stay
    /// object-grain (Msg 229), the last because the synonym's own id never keys
    /// the column map.
    /// </summary>
    internal static void CheckReadSources(BatchContext batch, List<ReferencedSecurable>? securables, Dictionary<int, ColumnReadTarget>? readColumns = null)
    {
        if (securables is null || securables.Count == 0 || !Applies(batch))
            return;
        var database = batch.CurrentDatabase;
        var principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
        foreach (var s in securables)
        {
            var permission = Permission.Resolve(s.Permission);
            // Column-grain path: a SELECT read with tracked columns.
            if (permission == Permission.Select && readColumns is not null && readColumns.TryGetValue(s.ObjectId, out var target))
            {
                CheckColumnGrants(database, principalId, Permission.Select, target);
                continue;
            }
            if (!PermissionChecker.IsGranted(database, principalId, permission, PermissionChecker.ClassObject, s.ObjectId, s.SchemaId))
                throw SimulatedSqlException.PermissionDenied(s.Permission.ToUpperInvariant(), s.ObjectName, database.Name, s.SchemaName);
            // A passed EXECUTE check on a scalar UDF invoked in this query memos
            // the object so the per-row invocation seam skips the re-check.
            if (s.Permission.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
                _ = (batch.ExecuteCheckedFunctionIds ??= []).Add(s.ObjectId);
        }
    }

    /// <summary>
    /// Column-level enforcement over an ordinal set gathered inline (the UPDATE /
    /// DELETE write and read-implies-SELECT paths, which don't ride a
    /// <see cref="Parser.Selection"/> plan). No-op for dbo / module bodies, and
    /// no-op when nothing was named — unlike a query read, a DML statement that
    /// resolved no column of the target genuinely touches none of them.
    /// </summary>
    internal static void CheckColumns(BatchContext batch, Permission permission, ColumnReadTarget target)
    {
        if (target.Ordinals.Count == 0 || !Applies(batch))
            return;
        CheckColumnGrants(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId, permission, target);
    }

    /// <summary>
    /// Column-level enforcement of <paramref name="permission"/> (SELECT for
    /// reads, UPDATE for writes). When the principal has no positive grant
    /// reaching the object at all, the object-level check has already failed →
    /// Msg 229; otherwise each column is checked in ascending ordinal order and
    /// the first inaccessible one raises Msg 230.
    /// </summary>
    private static void CheckColumnGrants(Database database, int principalId, Permission permission, ColumnReadTarget target)
    {
        // Object-level Msg 229 when the object is inaccessible at object grain
        // (no grant, or an object / schema / db DENY overriding the grant) AND
        // the principal holds no column-level grant on it — otherwise the object
        // is partially accessible and an inaccessible column raises Msg 230.
        var securable = target.Securable;
        var schemaName = SchemaNameFor(database, securable.SchemaId);
        var objectAccessible = PermissionChecker.IsGranted(database, principalId, permission, PermissionChecker.ClassObject, securable.ObjectId, securable.SchemaId);
        if (!objectAccessible && !PermissionChecker.HasColumnLevelGrant(database, principalId, permission, securable.ObjectId))
            throw SimulatedSqlException.PermissionDenied(permission.CanonicalName, securable.Name, database.Name, schemaName);
        foreach (var ordinal in target.OrdinalsToCheck())
        {
            if (!PermissionChecker.IsColumnGranted(database, principalId, permission, securable.ObjectId, securable.SchemaId, ordinal))
                throw SimulatedSqlException.ColumnPermissionDenied(permission.CanonicalName, target.Columns[ordinal - 1].Name, securable.Name, database.Name, schemaName);
        }
    }

    /// <summary>
    /// Checks EXECUTE on a scalar UDF at the invocation seam, once per statement
    /// (memoized on <see cref="BatchContext.ExecuteCheckedFunctionIds"/>). The
    /// query-context path records its EXECUTE securables through
    /// <see cref="CheckReadSources"/>, which pre-seeds the memo, so a UDF invoked
    /// in a SELECT isn't re-checked per row; a UDF invoked in a SET / IF operand
    /// (no read-source sink) is checked here. Throws Msg 229 on denial.
    /// </summary>
    internal static void CheckScalarFunctionExecute(BatchContext batch, Schemas.ScalarFunction function)
    {
        if (!Applies(batch))
            return;
        var checkedIds = batch.ExecuteCheckedFunctionIds ??= [];
        if (!checkedIds.Add(function.ObjectId))
            return;
        var database = batch.CurrentDatabase;
        var principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
        if (!PermissionChecker.IsGranted(database, principalId, Permission.Execute, PermissionChecker.ClassObject, function.ObjectId, function.SchemaId))
            throw SimulatedSqlException.PermissionDenied("EXECUTE", function.Name, database.Name, function.Schema.Name);
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

    /// <summary>Checks a permission on a resolved securable — a table, view, synonym or module; no-op when checks don't apply.</summary>
    internal static void CheckSchemaObject(BatchContext batch, string permission, Schemas.SchemaObject securable, string procedure = "")
    {
        if (!Applies(batch))
            return;
        CheckObject(batch, permission, securable.ObjectId, securable.SchemaId, securable.Name, SchemaNameFor(batch.CurrentDatabase, securable.SchemaId), procedure);
    }

    /// <summary>
    /// Checks a permission on the securable a reference written as
    /// <paramref name="writtenName"/> reached (see <see cref="SecurableFor"/>);
    /// no-op when checks don't apply.
    /// </summary>
    internal static void CheckReference(BatchContext batch, string permission, MultiPartName writtenName, Schemas.SchemaObject resolved, string procedure = "")
    {
        if (!Applies(batch))
            return;
        CheckSchemaObject(batch, permission, SecurableFor(batch, writtenName, resolved), procedure);
    }

    /// <summary>
    /// The securable a reference written as <paramref name="writtenName"/> is
    /// checked against: the <see cref="Schemas.Synonym"/> itself when the name
    /// is one, otherwise <paramref name="resolved"/>.
    /// </summary>
    /// <remarks>
    /// A synonym is its own securable and real never walks the check through to
    /// the base object — a grant on the base alone does not admit a reference
    /// through the synonym (the denial even names the synonym), and a DENY on the
    /// base does not block one. Probe-confirmed against SQL Server 2025.
    /// </remarks>
    internal static Schemas.SchemaObject SecurableFor(BatchContext batch, MultiPartName writtenName, Schemas.SchemaObject resolved) =>
        batch.TryResolveSynonym(writtenName, out var synonym) ? synonym : resolved;

    private static string SchemaNameFor(Database database, int schemaId)
    {
        foreach (var schema in database.Schemas.Values)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>Whether the effective principal may run any DDL (a <c>db_owner</c> / <c>db_ddladmin</c> member) — the gate for the statements that raise Msg 15247 (CREATE SEQUENCE / ROLE / USER / SCHEMA). True for dbo / module bodies.</summary>
    internal static bool HasDdlAdminCapability(BatchContext batch) =>
        !Applies(batch)
        || PermissionChecker.IsDdlAdminOrOwner(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId);

    /// <summary>Whether the effective principal is a <c>db_owner</c> member (the DROP USER gate). True for dbo / module bodies.</summary>
    internal static bool IsOwner(BatchContext batch) =>
        !Applies(batch)
        || PermissionChecker.IsOwner(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId);

    /// <summary>Whether the effective principal holds a database-scope permission (CREATE TABLE gate, CONNECT, etc.). Always true for dbo / module bodies.</summary>
    internal static bool HasDatabasePermission(BatchContext batch, string permission) =>
        !Applies(batch)
        || PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
            Permission.Resolve(permission), PermissionChecker.ClassDatabase, 0, 0);

    /// <summary>
    /// Whether the effective principal holds ALTER on the given schema (the DDL
    /// gate for CREATE TABLE / VIEW / PROCEDURE / FUNCTION and DROP TABLE). True
    /// for dbo / module bodies; satisfied by schema-scope ALTER / CONTROL,
    /// database-scope ALTER / CONTROL, and the <c>db_ddladmin</c> / <c>db_owner</c>
    /// fixed roles — but NOT by an object-scope ALTER (probe M5b).
    /// </summary>
    internal static bool HasSchemaAlter(BatchContext batch, int schemaId) =>
        !Applies(batch)
        || PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
            Permission.Alter, PermissionChecker.ClassSchema, schemaId, 0);

    /// <summary>
    /// Whether the effective principal holds ALTER on the given object (the DDL
    /// gate for ALTER TABLE — object-scope ALTER suffices, probe M5b). True for
    /// dbo / module bodies.
    /// </summary>
    internal static bool HasObjectAlter(BatchContext batch, int objectId, int schemaId) =>
        !Applies(batch)
        || PermissionChecker.IsGranted(batch.CurrentDatabase, batch.Connection.Security.Effective.DatabasePrincipalId,
            Permission.Alter, PermissionChecker.ClassObject, objectId, schemaId);

    /// <summary>
    /// The dual DDL gate for <c>CREATE VIEW</c> / <c>PROCEDURE</c> /
    /// <c>FUNCTION</c>: the database-scope CREATE-of-that-kind permission (Msg
    /// 262 state 18, the module carried as Procedure attribution) plus ALTER on
    /// the target schema (Msg 2760). No-op for dbo / module bodies.
    /// </summary>
    internal static void CheckCreateModule(BatchContext batch, string permission, string moduleName, Schema schema)
    {
        if (!Applies(batch))
            return;
        if (!HasDatabasePermission(batch, permission))
            throw SimulatedSqlException.CreateModulePermissionDenied(permission, batch.CurrentDatabase.Name, moduleName);
        if (!HasSchemaAlter(batch, schema.SchemaId))
            throw SimulatedSqlException.SpecifiedSchemaNameDoesNotExist(schema.Name);
    }
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
    private const int DbSecurityAdmin = 16386;
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

    // Server-scope permissions (sys.server_permissions.class = 100) live on
    // Simulation.ServerPermissions, not a Database; Simulation.HoldsServerPermission
    // walks them, but the covering graph shares this class tag so the VIEW
    // …STATE server-scope edges resolve through Permission.Covering.
    internal const byte ClassServer = 100;

    /// <summary>Whether the effective principal holds <paramref name="permission"/> on the described securable. An off-catalog (<see cref="Permission.Other"/>) request is never satisfied.</summary>
    internal static bool IsGranted(Database database, int principalId, Permission permission, byte securableClass, int majorId, int schemaId)
    {
        if (permission == Permission.Other)
            return false;

        var closure = BuildClosure(database, principalId);
        var satisfiers = BuildSatisfiers(permission, securableClass, majorId, schemaId, columnOrdinal: 0);

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

    /// <summary>
    /// Whether the effective principal may read / write column
    /// <paramref name="columnOrdinal"/> (1-based, matching
    /// <c>sys.columns.column_id</c>) of the object, under a column-level grant
    /// model. Same DENY-first / GRANT precedence as <see cref="IsGranted"/>, but
    /// a matching row at the column's <c>minor_id</c> also satisfies (grant) or
    /// binds (deny), alongside the object-level (minor 0), schema, and database
    /// scopes: a column DENY overrides a table GRANT, and a column GRANT stands
    /// in for an absent table GRANT (probe-confirmed against SQL Server 2025).
    /// </summary>
    internal static bool IsColumnGranted(Database database, int principalId, Permission permission, int objectId, int schemaId, int columnOrdinal)
    {
        if (permission == Permission.Other)
            return false;

        var closure = BuildClosure(database, principalId);
        var satisfiers = BuildSatisfiers(permission, ClassObject, objectId, schemaId, columnOrdinal);

        // DENY binds first — a column / object / schema / db DENY, then the deny-roles.
        if (HasMatchingRow(database, closure, satisfiers, deny: true))
            return false;
        if (permission.Category == PermissionCategory.Read && closure.Contains(DbDenyDataReader))
            return false;
        if (permission.Category == PermissionCategory.Write && closure.Contains(DbDenyDataWriter))
            return false;

        // GRANT test — a column / object / schema / db G/W row, then the grant-roles.
        return HasMatchingRow(database, closure, satisfiers, deny: false)
            || closure.Contains(DbOwner)
            || (permission.Category == PermissionCategory.Read && closure.Contains(DbDataReader))
            || (permission.Category == PermissionCategory.Write && closure.Contains(DbDataWriter));
    }

    /// <summary>
    /// Whether the effective principal holds a <em>column-level</em> GRANT
    /// (<c>minor_id &gt; 0</c>) of <paramref name="permission"/> (or a covering
    /// permission) on the object — grant-side only. Pairs with the object-grain
    /// <see cref="IsGranted"/> to draw the Msg 229 vs 230 boundary: a column that
    /// fails <see cref="IsColumnGranted"/> raises the column-level Msg 230 when
    /// the object is object-grain accessible or carries any column grant
    /// (partial access), and the object-level Msg 229 when neither holds — no
    /// grant reaches the object, or an object / schema / database DENY (or a
    /// deny-role) has nullified the object-grain grant entirely.
    /// </summary>
    internal static bool HasColumnLevelGrant(Database database, int principalId, Permission permission, int objectId)
    {
        if (permission == Permission.Other)
            return false;

        var closure = BuildClosure(database, principalId);
        foreach (var row in database.Permissions)
        {
            if (row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                && row.Class == ClassObject
                && row.MajorId == objectId
                && row.MinorId != 0
                && closure.Contains(row.GranteePrincipalId)
                && row.Permission.Covers(permission, ClassObject))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the effective principal is a member of <paramref name="role"/> (transitively), or the role is <c>public</c> (everyone).</summary>
    internal static bool IsRoleMember(Database database, int principalId, DatabasePrincipal role) =>
        role.PrincipalId == 0 || BuildClosure(database, principalId).Contains(role.PrincipalId);

    /// <summary>Whether the principal is a (transitive) member of <c>db_owner</c> or <c>db_ddladmin</c> — the "may run any DDL" gate for the 15247 statements.</summary>
    internal static bool IsDdlAdminOrOwner(Database database, int principalId)
    {
        var closure = BuildClosure(database, principalId);
        return closure.Contains(DbOwner) || closure.Contains(DbDdlAdmin);
    }

    /// <summary>Whether the principal is a (transitive) member of <c>db_owner</c>.</summary>
    internal static bool IsOwner(Database database, int principalId) =>
        BuildClosure(database, principalId).Contains(DbOwner);

    /// <summary>The effective principal + every role it belongs to transitively + <c>public</c> — exposed for the per-enumeration metadata-visibility scan so it builds the closure once.</summary>
    internal static HashSet<int> BuildPrincipalClosure(Database database, int principalId) =>
        BuildClosure(database, principalId);

    /// <summary>
    /// Whether the principal sees every object's metadata regardless of grants:
    /// a <c>db_owner</c> / <c>db_ddladmin</c> / <c>db_securityadmin</c> member
    /// (probe-confirmed against SQL Server 2025), or a holder of <c>CONTROL</c> /
    /// <c>VIEW DEFINITION</c> granted at database scope.
    /// </summary>
    internal static bool HasFullMetadataVisibility(Database database, int principalId) =>
        HasFullMetadataVisibility(database, BuildClosure(database, principalId));

    private static bool HasFullMetadataVisibility(Database database, HashSet<int> closure)
    {
        if (closure.Contains(DbOwner) || closure.Contains(DbDdlAdmin) || closure.Contains(DbSecurityAdmin))
            return true;
        foreach (var row in database.Permissions)
        {
            if (row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                && row.Class == ClassDatabase
                && row.Permission is Permission.Control or Permission.ViewDefinition
                && closure.Contains(row.GranteePrincipalId))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether the principal may see the metadata (catalog-view rows,
    /// <c>OBJECT_ID</c> / <c>OBJECT_NAME</c> / <c>OBJECT_SCHEMA_NAME</c> results)
    /// of the object with the given id / schema. True under the full-visibility
    /// bypass (<see cref="HasFullMetadataVisibility(Database,int)"/>), or when the
    /// principal (or any role in its closure) holds any object-applicable
    /// permission reaching the object — a direct object-scope grant (any
    /// permission, any column via <c>minor_id</c>), a schema-scope grant, an
    /// object-applicable database-scope grant, or the <c>db_datareader</c> /
    /// <c>db_datawriter</c> fixed roles. DENY does not hide metadata (grant-only
    /// scan — probe-scoped assumption: metadata-hiding by DENY was not observed).
    /// </summary>
    internal static bool CanViewMetadata(Database database, int principalId, int objectId, int schemaId) =>
        CanViewMetadata(database, BuildClosure(database, principalId), objectId, schemaId);

    internal static bool CanViewMetadata(Database database, HashSet<int> closure, int objectId, int schemaId)
    {
        if (HasFullMetadataVisibility(database, closure))
            return true;
        foreach (var row in database.Permissions)
        {
            if (row.State is not (PermissionState.Grant or PermissionState.GrantWithGrantOption)
                || !closure.Contains(row.GranteePrincipalId))
            {
                continue;
            }
            var reveals = row.Class switch
            {
                ClassDatabase => RevealsObjectMetadata(row.Permission),
                ClassObject => row.MajorId == objectId,
                ClassSchema => row.MajorId == schemaId,
                _ => false,
            };
            if (reveals)
                return true;
        }
        // db_datareader / db_datawriter confer SELECT / IUD on every object,
        // which reveals its metadata. (A slight over-reveal for procedures,
        // which those roles can't actually access — accepted for simplicity.)
        return closure.Contains(DbDataReader) || closure.Contains(DbDataWriter);
    }

    /// <summary>
    /// Whether a database-scope grant of this permission reveals every object's
    /// metadata — the object-applicable permissions do; the connect / create /
    /// impersonate permissions (notably the <c>CONNECT</c> every user is seeded)
    /// do not, so they can't blanket-reveal the catalog.
    /// </summary>
    private static bool RevealsObjectMetadata(Permission permission) => permission switch
    {
        Permission.Connect or Permission.CreateFunction or Permission.CreateProcedure
            or Permission.CreateSequence or Permission.CreateTable or Permission.CreateView
            or Permission.Impersonate or Permission.Other => false,
        _ => true,
    };

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

    private static bool HasMatchingRow(Database database, HashSet<int> closure, List<(byte Class, int MajorId, int MinorId, Permission Permission)> satisfiers, bool deny)
    {
        foreach (var row in database.Permissions)
        {
            var stateMatches = deny ? row.State == PermissionState.Deny : row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption;
            if (!stateMatches || !closure.Contains(row.GranteePrincipalId))
                continue;
            foreach (var (cls, majorId, minorId, permission) in satisfiers)
            {
                if (row.Class == cls && row.MajorId == majorId && row.MinorId == minorId && row.Permission == permission)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The set of (class, major_id, minor_id, permission) tuples a G/W (or D)
    /// row could carry that would satisfy (or deny) the request: the permission
    /// itself plus every covering permission, walked up the object → schema →
    /// database scope chain. For an object-scope request the object-level rows
    /// carry <c>minor_id 0</c>; when <paramref name="columnOrdinal"/> is non-zero
    /// the object scope additionally admits a row at that column's
    /// <c>minor_id</c> (the column-level grant / deny) — so an object-grain
    /// request (ordinal 0) is never satisfied by a column-scoped row, and a
    /// column request is satisfied by either its own column row or the
    /// all-columns object row.
    /// </summary>
    private static List<(byte Class, int MajorId, int MinorId, Permission Permission)> BuildSatisfiers(Permission permission, byte securableClass, int majorId, int schemaId, int columnOrdinal)
    {
        var result = new List<(byte, int, int, Permission)>();
        switch (securableClass)
        {
            case ClassObject:
                AddScope(result, ClassObject, majorId, minorId: 0, permission);
                if (columnOrdinal != 0)
                    AddScope(result, ClassObject, majorId, columnOrdinal, permission);
                AddScope(result, ClassSchema, schemaId, minorId: 0, permission);
                AddScope(result, ClassDatabase, 0, minorId: 0, permission);
                break;
            case ClassSchema:
                AddScope(result, ClassSchema, majorId, minorId: 0, permission);
                AddScope(result, ClassDatabase, 0, minorId: 0, permission);
                break;
            case ClassDatabasePrincipal:
                AddScope(result, ClassDatabasePrincipal, majorId, minorId: 0, permission);
                break;
            default:
                AddScope(result, ClassDatabase, 0, minorId: 0, permission);
                break;
        }
        return result;
    }

    private static void AddScope(List<(byte, int, int, Permission)> result, byte cls, int majorId, int minorId, Permission permission)
    {
        foreach (var p in permission.CoveringChain(cls))
            result.Add((cls, majorId, minorId, p));
    }
}
