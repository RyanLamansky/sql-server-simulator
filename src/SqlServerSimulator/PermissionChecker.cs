using SqlServerSimulator.Parser;

namespace SqlServerSimulator;

/// <summary>
/// One object a statement reads (table / view / TVF), recorded at parse time
/// on <see cref="Parser.Selection.ReferencedSecurables"/> so the SELECT
/// permission check can run at execution against the current principal — the
/// list is principal-independent, so it caches with the plan.
/// </summary>
internal readonly struct ReferencedSecurable(Database database, Schemas.SchemaObject securable, string schemaName, string permission = "SELECT", Schemas.View? module = null, Parser.Selection? moduleBody = null)
{
    /// <summary>
    /// For a reference to a view, the view and what the body its reference
    /// parsed reads: once the view itself passes, those reads are checked in
    /// the same step, so a broken ownership chain is refused with the
    /// statement rather than when the first row is read (see
    /// <see cref="PermissionEnforcement.CheckModuleBodyReads(BatchContext, Schemas.SchemaObject, Database, List{ReferencedSecurable}?, Dictionary{int, ColumnReadTarget}?)"/>).
    /// Only the lists are kept — a cached plan is shared, and a body plan
    /// reaches the batch that parsed it.
    /// </summary>
    public readonly Schemas.View? Module = moduleBody is null ? null : module;

    /// <summary>What <see cref="Module"/>'s body reads, itself carrying its own views' reads.</summary>
    public readonly List<ReferencedSecurable>? ModuleReads = moduleBody?.ReferencedSecurables;

    /// <summary>The columns of <see cref="ModuleReads"/> the body names.</summary>
    public readonly Dictionary<int, ColumnReadTarget>? ModuleReadColumns = moduleBody?.ReadColumnsByObject;

    public readonly Database Database = database;

    /// <summary>
    /// The object read, which the checks ask for its owner rather than finding
    /// it again by <see cref="ObjectId"/> in a walk of every schema.
    /// </summary>
    public readonly Schemas.SchemaObject Securable = securable;

    public readonly int ObjectId = securable.ObjectId;
    public readonly int SchemaId = securable.SchemaId;
    public readonly string ObjectName = securable.Name;
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
/// A permission check a statement makes while it parses, kept on the batch's
/// replay log (<see cref="BatchContext.ReplayLockLog"/>) beside the locks the
/// parse takes, so a plan-cache replay, which parses nothing, makes it again
/// at the same point as the replaying principal (see
/// <see cref="PermissionEnforcement.CheckWhileParsing"/>). Holds only the
/// securable and the name it was written as, if that decides the securable,
/// so the plan it rides with stays the same for every principal.
/// </summary>
internal sealed class CompiledPermissionCheck(string permission, MultiPartName? writtenName, Schemas.SchemaObject resolved)
{
    private readonly string permission = permission;
    private readonly MultiPartName? writtenName = writtenName;
    private readonly Schemas.SchemaObject resolved = resolved;

    /// <summary>Makes the check as <paramref name="batch"/>'s effective principal.</summary>
    public void Run(BatchContext batch)
    {
        if (this.writtenName is { } name)
            PermissionEnforcement.CheckReference(batch, this.permission, name, this.resolved);
        else
            PermissionEnforcement.CheckSchemaObject(batch, this.permission, this.resolved);
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
    /// <summary>
    /// Whether permission checks apply to a reference into
    /// <paramref name="target"/>: a genuinely restricted principal, and — for
    /// the session's own database — not inside an ownership-chained module
    /// body. For another database the module-body suppression does
    /// <em>not</em> hold here: an ownership chain breaks at the database
    /// boundary unless <c>DB_CHAINING</c> is on in both databases, so real
    /// checks the caller's rights on the object the module reached across
    /// (probe-confirmed: a dbo-owned view selecting from another database
    /// raises Msg 229 naming the base table there). The chaining exemption is
    /// applied one step later, in <see cref="TryResolveScope(BatchContext, Database, int, out int, Schemas.SchemaObject)"/>, so that the
    /// Msg 916 a missing user in the target earns still fires. A create-time
    /// bind suppresses everything — it reads no row.
    /// </summary>
    internal static bool Applies(BatchContext batch, Database target) =>
        !Bypasses(batch.Connection, target)
        && !batch.CreateTimeBinding
        && (batch.EnforcesPermissions || !ReferenceEquals(target, batch.CurrentDatabase));

    /// <summary>
    /// The <c>dbo</c> bypass, database-boundary aware. An effective <c>dbo</c>
    /// waves through every check in its own database, and in another one too
    /// when the identity carries a server principal — exact in the simulator's
    /// principal model, where such a <c>dbo</c> came from a sysadmin login or
    /// the empty-registry dev mode, both <c>dbo</c> in every database. The one
    /// exception is a <c>dbo</c> identity that exists only inside one database:
    /// an <c>EXECUTE AS USER = 'dbo'</c> or a module's <c>WITH EXECUTE AS
    /// OWNER</c> / <c>SELF</c> frame carries no server principal, so another
    /// database answers it as it answers any database-scoped frame, through
    /// <see cref="TryResolveCrossDatabasePrincipal"/> — Msg 916 out of an
    /// ordinary source even when the session's own login is <c>sa</c>
    /// (probe-confirmed). The same-database question stays one frame read plus
    /// two of its fields, so the hot path pays nothing for the boundary case.
    /// </summary>
    internal static bool Bypasses(SimulatedDbConnection connection, Database target)
    {
        var effective = connection.Security.Effective;
        return effective.DatabasePrincipalId == Database.DboPrincipalId
            && (!effective.IsDatabaseScoped || ReferenceEquals(target, connection.CurrentDatabase));
    }

    /// <summary>
    /// <see cref="Bypasses"/> with no target in hand — true when the effective
    /// identity waves through whatever database a securable turns out to live
    /// in, which is what lets a list be skipped whole. A database-scoped
    /// <c>dbo</c> frame answers false, and each securable is then examined in
    /// turn.
    /// </summary>
    private static bool BypassesEverywhere(SimulatedDbConnection connection)
    {
        var effective = connection.Security.Effective;
        return effective.DatabasePrincipalId == Database.DboPrincipalId && !effective.IsDatabaseScoped;
    }

    /// <summary>The server permissions the session's effective identity lends a database check — none for a database-scoped identity.</summary>
    private static ServerLoginRights Rights(BatchContext batch) => ServerLoginRights.For(batch.Connection);

    /// <summary>
    /// Resolves the principal that answers for a reference into
    /// <paramref name="target"/> and reports whether it needs checking at all.
    /// The session's own database answers with the effective principal
    /// directly; another database resolves the session's <em>login</em> to its
    /// user there — real's rule, since a login's rights are per database —
    /// raising Msg 916 when it has none. Returns <see langword="false"/> (no
    /// check) for a session that bypasses, and for a cross-database reference
    /// whose target principal is <c>dbo</c>.
    /// </summary>
    /// <remarks>
    /// This is the form the DDL and statement gates ask, and it holds inside a
    /// module body too: ownership chaining covers data access alone, so a
    /// procedure's <c>TRUNCATE</c>, <c>CREATE TABLE</c> or <c>ALTER TABLE</c>
    /// is checked against its caller (probed 2026-10-04 against SQL Server
    /// 2025), as is every statement gate a body reaches.
    /// </remarks>
    private static bool TryResolveScope(BatchContext batch, Database target, out int principalId)
    {
        if (Bypasses(batch.Connection, target) || batch.CreateTimeBinding)
        {
            principalId = 0;
            return false;
        }
        if (ReferenceEquals(target, batch.CurrentDatabase))
        {
            principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
            return true;
        }
        principalId = ResolveCrossDatabasePrincipal(batch.Connection, target).PrincipalId;
        return principalId != Database.DboPrincipalId;
    }

    /// <summary>
    /// <see cref="TryResolveScope(BatchContext, Database, out int)"/> for a
    /// reference to object <paramref name="objectId"/>, which additionally
    /// answers a broken ownership chain: inside a module body that records its
    /// owner, a same-database object with another owner is checked against the
    /// caller as though no module intervened.
    /// </summary>
    private static bool TryResolveScope(BatchContext batch, Database target, int objectId, out int principalId, Schemas.SchemaObject? securable = null)
    {
        if (objectId != 0 && BreaksOwnershipChain(batch, target, objectId, securable))
        {
            principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
            return true;
        }
        if (!Applies(batch, target))
        {
            principalId = 0;
            return false;
        }
        if (ReferenceEquals(target, batch.CurrentDatabase))
        {
            principalId = batch.Connection.Security.Effective.DatabasePrincipalId;
            return true;
        }
        // The principal resolves either way — chaining lends the module owner's
        // rights, not access to the database, so a login with no user there is
        // still Msg 916 (probe-confirmed).
        principalId = ResolveCrossDatabasePrincipal(batch.Connection, target).PrincipalId;
        var chained = !batch.EnforcesPermissions
            && ChainsAcross(batch.CurrentDatabase, batch.OwnershipChainOwnerId ?? Database.DboPrincipalId, target, objectId);
        return principalId != Database.DboPrincipalId && !chained;
    }

    /// <summary>
    /// Whether a same-database reference from inside a module body falls off
    /// its ownership chain — the object's effective owner differs from
    /// <see cref="BatchContext.OwnershipChainOwnerId"/>. Never for a bypassing
    /// session or a create-time bind, and never outside a module body.
    /// </summary>
    private static bool BreaksOwnershipChain(BatchContext batch, Database target, int objectId, Schemas.SchemaObject? securable) =>
        !batch.EnforcesPermissions
        && batch.OwnershipChainOwnerId is int chainOwner
        && !batch.CreateTimeBinding
        && ReferenceEquals(target, batch.CurrentDatabase)
        && !Bypasses(batch.Connection, target)
        && (securable is null ? Ownership.EffectiveOwnerId(target, objectId) : Ownership.RegisteredOwnerId(target, securable)) is int owner
        && owner != chainOwner;

    /// <summary>
    /// Whether an ownership chain re-links across the boundary between two
    /// databases: <c>DB_CHAINING</c> on in <em>both</em>, and the module's
    /// owner <paramref name="fromOwnerId"/> and object
    /// <paramref name="objectId"/>'s owner mapping to the same login — a
    /// <c>dbo</c> standing for its database's owner. Probe-confirmed against
    /// SQL Server 2025: on/on carries the chain through a view and through a
    /// procedure body, while on/off, off/on and off/off all break it and the
    /// caller needs its own grant on the object the module reached; with both
    /// on, databases owned by different logins break it (probed 2026-09-27),
    /// as does a target object owned by a user of another login, while two
    /// users of one login keep it. An <paramref name="objectId"/> of 0 — no
    /// object in hand — asks the flags alone.
    /// </summary>
    private static bool ChainsAcross(Database from, int fromOwnerId, Database to, int objectId) =>
        from.CrossDatabaseChaining && to.CrossDatabaseChaining
        && (objectId == 0
            || (Ownership.OwnerLogin(from, fromOwnerId) is { } fromLogin
                && Ownership.EffectiveOwnerId(to, objectId) is int toOwnerId
                && Ownership.OwnerLogin(to, toOwnerId) is { } toLogin
                && BuiltInToken.Comparer.Equals(fromLogin, toLogin)));

    /// <summary>
    /// The database user <paramref name="connection"/>'s login runs as in
    /// <paramref name="target"/>, or Msg 916 when it can't reach that database
    /// at all. Shared by the cross-database permission checks and by
    /// <c>USE</c> / <c>ChangeDatabase</c>, which ask the same question.
    /// A <see cref="SecurityPrincipalFrame.IsDatabaseScoped"/> identity — an
    /// <c>EXECUTE AS USER</c> frame, a module's <c>WITH EXECUTE AS &lt;user&gt;</c>
    /// frame, or an activated application role — carries no server principal, so
    /// it resolves only out of a <see cref="Database.Trustworthy"/> database;
    /// elsewhere real refuses the crossing outright. Under <c>TRUSTWORTHY</c> the
    /// frame's own login answers in the target like any ordinary session's, so a
    /// <c>WITHOUT LOGIN</c> user — whose reported identity is a SID, not a login —
    /// still can't reach a database it has no user in (probe-confirmed).
    /// </summary>
    internal static DatabasePrincipal ResolveCrossDatabasePrincipal(SimulatedDbConnection connection, Database target) =>
        TryResolveCrossDatabasePrincipal(connection, target, out var principal)
            ? principal
            : throw SimulatedSqlException.CannotAccessDatabaseUnderSecurityContext(connection.Security.Effective.LoginName, target.Name);

    /// <summary>
    /// <see cref="ResolveCrossDatabasePrincipal"/> without the throw — false
    /// where that one raises Msg 916. The id-form <c>OBJECT_NAME</c> /
    /// <c>OBJECT_SCHEMA_NAME</c> take this path: a database their login can't
    /// reach simply reveals nothing to them (probe-confirmed — the id form never
    /// raises, unlike the name form's three-part lookup).
    /// </summary>
    internal static bool TryResolveCrossDatabasePrincipal(SimulatedDbConnection connection, Database target, out DatabasePrincipal principal)
    {
        var effective = connection.Security.Effective;
        if (effective.IsDatabaseScoped && !AcceptsDatabaseScopedToken(connection.Simulation, connection.CurrentDatabase, target))
        {
            // An untrusted token still reaches a database that lets guest in,
            // as guest (probed 2026-10-04 against SQL Server 2025: USE master
            // under EXECUTE AS USER answers USER_NAME() = guest).
            if (target.Principals.TryGetValue("guest", out var guest)
                && PermissionChecker.IsGranted(target, Database.GuestPrincipalId, Permission.Connect, PermissionChecker.ClassDatabase, 0, 0))
            {
                principal = guest;
                return true;
            }
            principal = null!;
            return false;
        }
        return Simulation.TryMapLoginToDatabaseUser(connection.Simulation, target, effective.LoginName, out principal);
    }

    /// <summary>
    /// Whether <paramref name="target"/> accepts a database-scoped token minted
    /// in <paramref name="source"/>: the source is <c>TRUSTWORTHY</c> and its
    /// owner's login is an authenticator in the target — <c>dbo</c> there, or a
    /// user holding <c>AUTHENTICATE</c> (a <c>db_owner</c> member does). Probed
    /// 2026-09-27 against SQL Server 2025 as the exact line: an owner with no
    /// user in the target, or one whose user lacks <c>AUTHENTICATE</c>, is
    /// Msg 916 however trustworthy the source.
    /// </summary>
    private static bool AcceptsDatabaseScopedToken(Simulation simulation, Database source, Database target) =>
        source.Trustworthy
        && Simulation.TryMapLoginToDatabaseUser(simulation, target, source.OwnerLoginName, out var authenticator)
        && (authenticator.PrincipalId == Database.DboPrincipalId
            || PermissionChecker.IsGranted(target, authenticator.PrincipalId, Permission.Authenticate, PermissionChecker.ClassDatabase, 0, 0,
                ServerLoginRights.ForLogin(simulation, source.OwnerLoginName)));

    /// <summary>
    /// The principal a catalog-view read of <paramref name="target"/> filters
    /// by, or <see langword="null"/> when it sees everything. A read of another
    /// database asks exactly what a data reference asks — the login's user
    /// <em>there</em>, Msg 916 when it has none — and then applies that
    /// principal's own visibility, so a login restricted at home and
    /// <c>db_owner</c> away sees the away catalog whole (probe-confirmed
    /// against SQL Server 2025, where the same Msg 916 answers every cross-database
    /// catalog view, filtered and unfiltered alike).
    /// </summary>
    internal static int? MetadataVisibilityPrincipal(BatchContext batch, Database target) =>
        RestrictedPrincipal(batch, target) is int principalId ? FilteringPrincipal(target, principalId, Rights(batch)) : null;

    /// <summary>
    /// The principal answering for <paramref name="target"/>, or
    /// <see langword="null"/> for a session that bypasses every check there —
    /// Msg 916 when the login can't reach it. The principal-keyed catalog
    /// views filter by this rather than <see cref="MetadataVisibilityPrincipal"/>,
    /// since an object-catalog bypass such as <c>db_ddladmin</c>'s leaves the
    /// principals filtered (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static int? RestrictedPrincipal(BatchContext batch, Database target)
    {
        if (Bypasses(batch.Connection, target))
            return null;
        var principalId = ReferenceEquals(target, batch.CurrentDatabase)
            ? batch.Connection.Security.Effective.DatabasePrincipalId
            : ResolveCrossDatabasePrincipal(batch.Connection, target).PrincipalId;
        return principalId == Database.DboPrincipalId ? null : principalId;
    }

    /// <summary>
    /// <see cref="MetadataVisibilityPrincipal"/> for the callers that hide
    /// rather than raise: false when the login can't reach
    /// <paramref name="target"/> at all, where the throwing form raises Msg 916.
    /// The id-form <c>OBJECT_NAME</c> / <c>OBJECT_SCHEMA_NAME</c> ask the
    /// visibility question alone — a database their login has no user in reveals
    /// nothing and they answer NULL, and so does one a database-scoped frame
    /// can't reach (probed 2026-09-29 against SQL Server 2025: a <c>WITH
    /// EXECUTE AS OWNER</c> body answers NULL for a foreign id, whether the
    /// database id is written or read through <c>DB_ID</c>, which is NULL
    /// there itself).
    /// </summary>
    internal static bool TryMetadataVisibilityPrincipal(BatchContext batch, Database target, out int? principalId)
    {
        principalId = null;
        var security = batch.Connection.Security;
        if (Bypasses(batch.Connection, target))
            return true;
        if (ReferenceEquals(target, batch.CurrentDatabase))
        {
            principalId = FilteringPrincipal(target, security.Effective.DatabasePrincipalId, Rights(batch));
            return true;
        }
        if (!TryResolveCrossDatabasePrincipal(batch.Connection, target, out var principal))
            return false;
        principalId = FilteringPrincipal(target, principal.PrincipalId, Rights(batch));
        return true;
    }

    /// <summary>
    /// The visibility test the catalog procedures apply to each object they
    /// list, or <see langword="null"/> when the session sees the whole catalog
    /// — the same rule the catalog views filter by.
    /// </summary>
    internal static Func<Schemas.SchemaObject, bool>? ObjectVisibility(BatchContext batch, Database database)
    {
        if (!TryMetadataVisibilityPrincipal(batch, database, out var principalId))
            return static _ => false;
        if (principalId is not int filter)
            return null;
        var closure = PermissionChecker.BuildPrincipalClosure(database, filter);
        var server = Rights(batch);
        return obj => PermissionChecker.CanViewMetadata(database, closure, obj.ObjectId, obj.SchemaId, server);
    }

    /// <summary>
    /// Whether the session may read <paramref name="obj"/>'s definition text —
    /// <c>OBJECT_DEFINITION</c>, <c>sys.sql_modules.definition</c>,
    /// <c>sp_helptext</c>: it must see the object and hold one of the
    /// permissions <see cref="PermissionChecker.CanViewDefinition"/> names, a
    /// trigger's answered on its parent. A <c>dbo</c> session pays one frame read.
    /// </summary>
    internal static bool CanSeeDefinition(BatchContext batch, Database database, Schemas.SchemaObject obj)
    {
        if (!TryMetadataVisibilityPrincipal(batch, database, out var principalId))
            return false;
        if (principalId is not int filter)
            return true;
        var governing = obj is Schemas.Trigger trigger ? trigger.Parent : obj;
        var server = Rights(batch);
        return PermissionChecker.CanViewMetadata(database, filter, governing.ObjectId, governing.SchemaId, server)
            && PermissionChecker.CanViewDefinition(database, filter, governing.ObjectId, governing.SchemaId, server);
    }

    /// <summary>The principal a catalog read of <paramref name="database"/> filters by, or <see langword="null"/> when <paramref name="principalId"/> sees everything there.</summary>
    private static int? FilteringPrincipal(Database database, int principalId, ServerLoginRights server) =>
        principalId == Database.DboPrincipalId || PermissionChecker.HasFullMetadataVisibility(database, principalId, server)
            ? null
            : principalId;

    /// <summary>
    /// Refuses a restricted session's read of a catalog view it may not read
    /// (<see cref="PermissionChecker.CanReadCatalogView"/>) with Msg 229, which names the view in
    /// <c>mssqlsystemresource</c>, where real keeps it. A <c>dbo</c> session pays
    /// one flag read.
    /// </summary>
    internal static void CheckCatalogViewRead(BatchContext batch, Schemas.CatalogView view, Database target)
    {
        var security = batch.Connection.Security;
        if (security.EffectiveIsDbo)
            return;
        int principalId;
        if (ReferenceEquals(target, batch.CurrentDatabase))
            principalId = security.Effective.DatabasePrincipalId;
        else if (TryResolveCrossDatabasePrincipal(batch.Connection, target, out var principal))
            principalId = principal.PrincipalId;
        else
            return;
        if (principalId == Database.DboPrincipalId
            || !BuiltInResources.CatalogViewsById.Value.TryGetValue(view.ObjectId, out var entry))
        {
            return;
        }
        var schemaId = entry.SchemaName == "INFORMATION_SCHEMA" ? Database.InformationSchemaId : Database.SysSchemaId;
        if (!PermissionChecker.CanReadCatalogView(target, principalId, view.ObjectId, schemaId))
            throw SimulatedSqlException.PermissionDenied("SELECT", view.Name, "mssqlsystemresource", entry.SchemaName);
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
        if (securables is null || securables.Count == 0)
            return;
        // A SQLCLR function marked SystemDataAccessKind.Read alone reads the
        // catalog through its context connection but no user object — a
        // table, a view or a function (probed 2026-09-28 against SQL Server
        // 2025).
        if (batch.RestrictsUserData && securables.Exists(securable => securable.ObjectId > 0))
            throw SimulatedSqlException.RestrictedDataAccess();
        if (BypassesEverywhere(batch.Connection))
            return;
        // Real names the last-bound denied object, not the first: with no
        // grant on either of two joined tables the error names the second
        // (probed 2026-09-29 against SQL Server 2025, joins, subqueries,
        // unions and INSERT … SELECT alike), so the list is walked backwards.
        for (var i = securables.Count - 1; i >= 0; i--)
        {
            var s = securables[i];
            CheckReadSource(batch, s, readColumns);
            if (s.Module is { } module)
                CheckModuleBodyReads(batch, module, module.Schema.Database, s.ModuleReads, s.ModuleReadColumns);
        }
    }

    private static void CheckReadSource(BatchContext batch, ReferencedSecurable s, Dictionary<int, ColumnReadTarget>? readColumns)
    {
        var database = s.Database;
        // Inside a module body a synonym's chain is judged on the object it
        // stands for, which a broken chain names (probed 2026-10-04 against
        // SQL Server 2025).
        if (!batch.EnforcesPermissions && ReferenceEquals(database, batch.CurrentDatabase)
            && s.Securable is Schemas.Synonym synonym
            && Ownership.RegisteredOwnerId(database, synonym) is not null
            && synonym.BaseObject.Count <= 2
            && batch.TryResolveSchema(synonym.BaseObject, out var baseSchema)
            && baseSchema.TryFindInSharedNamespace(synonym.BaseObject.Leaf, out var baseObject))
        {
            CheckObject(batch, database, s.Permission, baseObject.ObjectId, baseObject.SchemaId, baseObject.Name, baseSchema.Name);
            return;
        }
        if (!TryResolveScope(batch, database, s.ObjectId, out var principalId, s.Securable))
            return;
        var permission = Permission.Resolve(s.Permission);
        // Column-grain path: a SELECT read with tracked columns.
        if (permission == Permission.Select && readColumns is not null && readColumns.TryGetValue(s.ObjectId, out var target))
        {
            CheckColumnGrants(database, principalId, Permission.Select, target, Rights(batch));
            return;
        }
        if (!PermissionChecker.IsGranted(database, principalId, permission, PermissionChecker.ClassObject, s.ObjectId, s.SchemaId, Rights(batch), s.Securable))
            throw SimulatedSqlException.PermissionDenied(s.Permission.ToUpperInvariant(), s.ObjectName, database.Name, s.SchemaName);
        // A passed EXECUTE check on a scalar UDF invoked in this query memos
        // the object so the per-row invocation seam skips the re-check.
        if (s.Permission.Equals("EXECUTE", StringComparison.OrdinalIgnoreCase))
            _ = (batch.ExecuteCheckedFunctionIds ??= []).Add(CheckedKey(batch, s.ObjectId));
    }

    /// <summary>
    /// The memo key of a passed once-per-batch check: the object and the
    /// principal it passed for, so a check that passed before an <c>EXECUTE
    /// AS</c> or a <c>REVERT</c> doesn't answer for the identity after it.
    /// </summary>
    private static long CheckedKey(BatchContext batch, int objectId) =>
        ((long)batch.Connection.Security.Effective.DatabasePrincipalId << 32) | (uint)objectId;

    /// <summary>
    /// Checks the reads of a subquery that carries a securable list of its own —
    /// one written in an expression slot no query expression encloses: a scalar
    /// UDF's value-form <c>RETURN (SELECT …)</c>, a <c>SET</c> / <c>DECLARE</c>
    /// initializer, an <c>IF</c> / <c>WHILE</c> condition, a <c>PRINT</c>
    /// operand, or a DML statement's operand. Those plans reach none of the
    /// per-statement check sites, so without this the reference is unchecked.
    /// A subquery nested inside a query expression records into <em>that</em>
    /// statement's list and carries none of its own, so the per-row evaluation
    /// path reads one null field and returns.
    /// </summary>
    /// <remarks>
    /// Inside a module body the same-database references stay chained and only
    /// the cross-database ones answer to the caller's rights — which is the
    /// behavior real shows for both scalar-UDF body forms alike: an intact
    /// ownership chain skips the check for <c>RETURN (SELECT …)</c> and for
    /// <c>SELECT @v = … FROM t</c> equally, and a chain broken by an
    /// other-owner schema or by the database boundary raises Msg 229 naming the
    /// base object for both (probe-confirmed against SQL Server 2025).
    /// </remarks>
    internal static void CheckSubqueryReads(BatchContext batch, Selection inner)
    {
        if (inner.ReferencedSecurables is { } securables)
            CheckReadSources(batch, securables, inner.ReadColumnsByObject);
    }

    /// <summary>
    /// Checks the reads a module body makes into a database other than
    /// <paramref name="moduleDatabase"/> — the ownership chain the body's own
    /// frame suppresses breaks at the database boundary, so those references
    /// answer to the caller's rights in the database they reached. Same-database
    /// reads are skipped here (the chain holds), which is what keeps an ordinary
    /// view free. The statement-dispatching bodies (procedures, triggers, scalar
    /// UDFs) reach the ordinary check sites per statement and need no call here;
    /// a view body is inlined into the referencing statement and reaches none,
    /// so its plan is checked at invocation.
    /// </summary>
    internal static void CheckCrossDatabaseReads(BatchContext batch, Database moduleDatabase, int moduleOwnerId, List<ReferencedSecurable>? securables)
    {
        if (securables is null || BypassesEverywhere(batch.Connection) || batch.CreateTimeBinding)
            return;
        foreach (var s in securables)
        {
            if (ReferenceEquals(s.Database, moduleDatabase) || Bypasses(batch.Connection, s.Database))
                continue;
            // Resolving first is what keeps Msg 916 in play even when the chain
            // re-links — the caller needs a user in the target either way.
            var principalId = ResolveCrossDatabasePrincipal(batch.Connection, s.Database).PrincipalId;
            if (principalId != Database.DboPrincipalId
                && !ChainsAcross(moduleDatabase, moduleOwnerId, s.Database, s.ObjectId)
                && !PermissionChecker.IsGranted(s.Database, principalId, Permission.Resolve(s.Permission), PermissionChecker.ClassObject, s.ObjectId, s.SchemaId, Rights(batch)))
            {
                throw SimulatedSqlException.PermissionDenied(s.Permission.ToUpperInvariant(), s.ObjectName, s.Database.Name, s.SchemaName);
            }
        }
    }

    /// <summary>
    /// Checks an inlined module body's reads — a view's or an inline TVF's —
    /// at invocation: the cross-database ones as
    /// <see cref="CheckCrossDatabaseReads"/> does, and the same-database ones
    /// whose object has an owner other than <paramref name="module"/>'s, where
    /// the ownership chain breaks and the caller needs its own rights (probed
    /// 2026-09-27 against SQL Server 2025: a <c>u1</c>-owned view over a
    /// dbo-owned table is Msg 229 naming the table). A chain that holds costs a
    /// dbo session nothing and a restricted one a linear owner lookup.
    /// </summary>
    internal static void CheckModuleBodyReads(BatchContext batch, Schemas.SchemaObject module, Database moduleDatabase, Selection body) =>
        CheckModuleBodyReads(batch, module, moduleDatabase, body.ReferencedSecurables, body.ReadColumnsByObject);

    internal static void CheckModuleBodyReads(
        BatchContext batch, Schemas.SchemaObject module, Database moduleDatabase, List<ReferencedSecurable>? securables, Dictionary<int, ColumnReadTarget>? bodyReadColumns)
    {
        CheckCrossDatabaseReads(batch, moduleDatabase, Ownership.EffectiveOwnerId(moduleDatabase, module), securables);
        if (securables is null || batch.CreateTimeBinding || Bypasses(batch.Connection, moduleDatabase))
            return;
        int? moduleOwner = null;
        for (var i = securables.Count - 1; i >= 0; i--)
        {
            var s = securables[i];
            CheckModuleLink(s);
            // A view the body reads carries its own reads, whose chain is
            // judged from that view's owner on.
            if (s.Module is { } nested)
                CheckModuleBodyReads(batch, nested, nested.Schema.Database, s.ModuleReads, s.ModuleReadColumns);
        }

        void CheckModuleLink(ReferencedSecurable s)
        {
            if (!ReferenceEquals(s.Database, moduleDatabase))
                return;
            moduleOwner ??= Ownership.EffectiveOwnerId(moduleDatabase, module);
            if (Ownership.RegisteredOwnerId(moduleDatabase, s.Securable) is not int owner || owner == moduleOwner)
                return;
            var principalId = ReferenceEquals(moduleDatabase, batch.CurrentDatabase)
                ? batch.Connection.Security.Effective.DatabasePrincipalId
                : ResolveCrossDatabasePrincipal(batch.Connection, moduleDatabase).PrincipalId;
            if (principalId == Database.DboPrincipalId)
                return;
            var permission = Permission.Resolve(s.Permission);
            if (permission == Permission.Select && bodyReadColumns is { } readColumns && readColumns.TryGetValue(s.ObjectId, out var target))
            {
                CheckColumnGrants(moduleDatabase, principalId, Permission.Select, target, Rights(batch));
                return;
            }
            if (!PermissionChecker.IsGranted(moduleDatabase, principalId, permission, PermissionChecker.ClassObject, s.ObjectId, s.SchemaId, Rights(batch), s.Securable))
                throw SimulatedSqlException.PermissionDenied(s.Permission.ToUpperInvariant(), s.ObjectName, moduleDatabase.Name, s.SchemaName);
        }
    }

    /// <summary>
    /// Checks a write that passes through <paramref name="module"/> into
    /// <paramref name="target"/> — a DML statement through an updatable view
    /// — when the two have different owners, which breaks the ownership chain
    /// and makes the caller need the permission on the base object itself
    /// (probed 2026-09-27 against SQL Server 2025: Msg 229 naming the table).
    /// </summary>
    internal static void CheckBrokenChainWrite(BatchContext batch, string permission, Schemas.SchemaObject module, Schemas.SchemaObject target)
    {
        // A view over a view is a chain of links, each compared with the next
        // (the chain breaks and resumes at every owner change), so the walk
        // checks the permission on each object whose owner differs from the
        // one before it.
        var from = module;
        while (from is Schemas.View { UpstreamView: { } next })
        {
            CheckBrokenChainLink(batch, permission, from, next);
            from = next;
        }
        CheckBrokenChainLink(batch, permission, from, target);
    }

    internal static void CheckBrokenChainLink(BatchContext batch, string permission, Schemas.SchemaObject module, Schemas.SchemaObject target)
    {
        if (TryResolveBrokenChain(batch, module, target, out var database, out var principalId)
            && !PermissionChecker.IsGranted(database, principalId, Permission.Resolve(permission), PermissionChecker.ClassObject, target.ObjectId, target.SchemaId, Rights(batch)))
        {
            throw SimulatedSqlException.PermissionDenied(permission, target.Name, database.Name, SchemaNameFor(database, target.SchemaId));
        }
    }

    /// <summary>
    /// The column-grain form of <see cref="CheckBrokenChainWrite"/> for an
    /// <c>UPDATE</c> / <c>DELETE</c> through a single-table view:
    /// <paramref name="viewColumns"/>' ordinals translate to the base table's
    /// through <see cref="Schemas.View.BaseColumnOrdinals"/>, so a column the
    /// view names is checked on the base column it reads (probed 2026-09-27
    /// against SQL Server 2025: Msg 230 naming the base column and table). A
    /// null <paramref name="viewColumns"/> checks every base column — the
    /// read an <c>INSTEAD OF</c> trigger's pseudo-tables make — and an empty
    /// set checks nothing.
    /// </summary>
    internal static void CheckBrokenChainColumns(BatchContext batch, Permission permission, Schemas.View view, ColumnReadTarget? viewColumns)
    {
        var current = view;
        var columns = viewColumns;
        while (current.UpstreamView is { } next)
        {
            if (columns is { Ordinals.Count: 0 })
                return;
            var nextColumns = new ColumnReadTarget(next);
            if (columns is not null)
            {
                foreach (var ordinal in columns.Ordinals)
                {
                    if (current.UpstreamColumnOrdinals[ordinal - 1] is var upstreamOrdinal and >= 0)
                        _ = nextColumns.Ordinals.Add(upstreamOrdinal + 1);
                }
                if (nextColumns.Ordinals.Count == 0)
                    return;
            }
            if (TryResolveBrokenChain(batch, current, next, out var linkDatabase, out var linkPrincipal))
                CheckColumnGrants(linkDatabase, linkPrincipal, permission, nextColumns, Rights(batch));
            current = next;
            columns = columns is null ? null : nextColumns;
        }
        if (current.BaseTable is not { } baseTable
            || columns is { Ordinals.Count: 0 }
            || !TryResolveBrokenChain(batch, current, baseTable, out var database, out var principalId))
        {
            return;
        }
        var baseColumns = new ColumnReadTarget(baseTable);
        if (columns is not null)
        {
            foreach (var ordinal in columns.Ordinals)
            {
                if (current.BaseColumnOrdinals[ordinal - 1] is var baseOrdinal and >= 0)
                    _ = baseColumns.Ordinals.Add(baseOrdinal + 1);
            }
            if (baseColumns.Ordinals.Count == 0)
                return;
        }
        CheckColumnGrants(database, principalId, permission, baseColumns, Rights(batch));
    }

    /// <summary>
    /// One link of the chain an UPDATE through a join view crosses above the
    /// join itself — <paramref name="from"/> reading <paramref name="to"/>, the
    /// next view down. When their owners differ the caller needs SELECT on the
    /// columns of <paramref name="to"/> the statement reads, then UPDATE on it
    /// at object grain for each of <paramref name="writes"/> (probed 2026-09-29 against
    /// SQL Server 2025: a write through <c>dbo.v1</c> over a <c>u1</c>-owned
    /// join view is Msg 229 naming the join view, SELECT before UPDATE, while a
    /// view the join only reads takes the SELECT alone).
    /// </summary>
    internal static void CheckBrokenChainViewLink(BatchContext batch, Schemas.View from, Schemas.View to, ColumnReadTarget? reads, string[] writes)
    {
        if (!TryResolveBrokenChain(batch, from, to, out var database, out var principalId))
            return;
        // Real reports both denials, SELECT first, where each is missing.
        SimulatedSqlException? selectDenied = null;
        if (reads is { Ordinals.Count: > 0 })
        {
            try
            {
                CheckColumnGrants(database, principalId, Permission.Select, reads, Rights(batch));
            }
            catch (SimulatedSqlException denied)
            {
                selectDenied = denied;
            }
        }
        SimulatedSqlException? writeDenied = null;
        foreach (var write in writes)
        {
            if (!PermissionChecker.IsGranted(database, principalId, Permission.Resolve(write), PermissionChecker.ClassObject, to.ObjectId, to.SchemaId, Rights(batch)))
            {
                writeDenied = SimulatedSqlException.PermissionDenied(write, to.Name, database.Name, SchemaNameFor(database, to.SchemaId));
                break;
            }
        }
        if (selectDenied is not null && writeDenied is not null)
            throw SimulatedSqlException.Aggregate([selectDenied, writeDenied]);
        if ((selectDenied ?? writeDenied) is { } denial)
            throw denial;
    }

    /// <summary>The write permission list of an <c>UPDATE</c> crossing a link.</summary>
    internal static readonly string[] UpdateWrites = ["UPDATE"];

    /// <summary>The write permission list of a link a statement only reads through.</summary>
    internal static readonly string[] NoWrites = [];

    /// <summary>
    /// The column-grain broken-chain check for a write through a join view:
    /// <paramref name="columns"/> are already the base table's, gathered from
    /// what the statement and the view's joins and filters read of it, or the
    /// columns it assigns. An empty set checks nothing.
    /// </summary>
    internal static void CheckBrokenChainTableColumns(BatchContext batch, Permission permission, Schemas.View view, ColumnReadTarget columns)
    {
        if (columns.Ordinals.Count > 0 && TryResolveBrokenChain(batch, view, columns.Securable, out var database, out var principalId))
            CheckColumnGrants(database, principalId, permission, columns, Rights(batch));
    }

    /// <summary>
    /// True when a write through <paramref name="module"/> into
    /// <paramref name="target"/> must be checked on the target: the owners
    /// differ and the caller, resolved in the target's database, isn't
    /// <c>dbo</c>. A create-time bind checks nothing.
    /// </summary>
    private static bool TryResolveBrokenChain(BatchContext batch, Schemas.SchemaObject module, Schemas.SchemaObject target, out Database database, out int principalId)
    {
        database = batch.DatabaseFor(target);
        principalId = Database.DboPrincipalId;
        if (batch.CreateTimeBinding || Bypasses(batch.Connection, database)
            || Ownership.EffectiveOwnerId(batch.DatabaseFor(module), module) == Ownership.EffectiveOwnerId(database, target))
        {
            return false;
        }
        principalId = ReferenceEquals(database, batch.CurrentDatabase)
            ? batch.Connection.Security.Effective.DatabasePrincipalId
            : ResolveCrossDatabasePrincipal(batch.Connection, database).PrincipalId;
        return principalId != Database.DboPrincipalId;
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
        if (ColumnsDenial(batch, permission, target) is { } denied)
            throw denied;
    }

    /// <summary><see cref="CheckColumns"/> returning the refusal rather than raising it, so a statement can report every permission it lacks together.</summary>
    internal static SimulatedSqlException? ColumnsDenial(BatchContext batch, Permission permission, ColumnReadTarget target)
    {
        if (target.Ordinals.Count == 0)
            return null;
        var database = batch.DatabaseFor(target.Securable);
        return TryResolveScope(batch, database, target.Securable.ObjectId, out var principalId, target.Securable)
            ? ColumnGrantsDenial(database, principalId, permission, target, Rights(batch))
            : null;
    }

    /// <summary>
    /// Joins the refusals of one statement's checks into the single report real
    /// sends — a <c>DELETE … WHERE</c> lacking both is Msg 229 for
    /// <c>SELECT</c> and again for <c>DELETE</c>, each column an UPDATE may not
    /// read or write its own Msg 230 (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException? Combine(SimulatedSqlException? first, SimulatedSqlException? second) =>
        first is null ? second : second is null ? first : SimulatedSqlException.Aggregate([first, second]);

    private static void CheckColumnGrants(Database database, int principalId, Permission permission, ColumnReadTarget target, ServerLoginRights server)
    {
        if (ColumnGrantsDenial(database, principalId, permission, target, server) is { } denied)
            throw denied;
    }

    /// <summary>
    /// Column-level enforcement of <paramref name="permission"/> (SELECT for
    /// reads, UPDATE for writes). While no column of the object is reachable
    /// the refusal is the object-level Msg 229; otherwise every column the
    /// statement touches and may not is its own Msg 230, in ascending ordinal
    /// order (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException? ColumnGrantsDenial(Database database, int principalId, Permission permission, ColumnReadTarget target, ServerLoginRights server)
    {
        var securable = target.Securable;
        var closure = PermissionChecker.BuildPrincipalClosure(database, principalId);
        var objectAccessible = PermissionChecker.IsGrantedInClosure(database, closure, permission, PermissionChecker.ClassObject, securable.ObjectId, securable.SchemaId, server, securable);
        if (!objectAccessible
            && !PermissionChecker.HasAccessibleColumn(database, closure, permission, securable.ObjectId, securable.SchemaId, server, securable))
        {
            return SimulatedSqlException.PermissionDenied(permission.CanonicalName, securable.Name, database.Name, SchemaNameFor(database, securable.SchemaId));
        }
        // With no column-level row stored on the object, every column answers
        // exactly as the object did.
        if (objectAccessible && !PermissionChecker.HasColumnRows(database, securable.ObjectId))
            return null;
        List<SimulatedSqlException>? denied = null;
        foreach (var ordinal in target.OrdinalsToCheck())
        {
            if (!PermissionChecker.IsColumnGranted(database, closure, permission, securable.ObjectId, securable.SchemaId, ordinal, server, securable))
                (denied ??= []).Add(SimulatedSqlException.ColumnPermissionDenied(permission.CanonicalName, target.Columns[ordinal - 1].Name, securable.Name, database.Name, SchemaNameFor(database, securable.SchemaId)));
        }
        return denied is null ? null : SimulatedSqlException.Aggregate(denied);
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
        var database = batch.DatabaseFor(function);
        if (!TryResolveScope(batch, database, function.ObjectId, out var principalId, function))
            return;
        var checkedIds = batch.ExecuteCheckedFunctionIds ??= [];
        var key = CheckedKey(batch, function.ObjectId);
        if (checkedIds.Contains(key))
            return;
        if (!PermissionChecker.IsGranted(database, principalId, Permission.Execute, PermissionChecker.ClassObject, function.ObjectId, function.SchemaId, Rights(batch), function))
            throw SimulatedSqlException.PermissionDenied("EXECUTE", function.Name, database.Name, function.Schema.Name);
        _ = checkedIds.Add(key);
    }

    /// <summary>Checks <c>UPDATE</c> on a sequence a <c>NEXT VALUE FOR</c> draws from, once per statement (memoized beside the scalar functions' <c>EXECUTE</c>).</summary>
    internal static void CheckSequenceUpdate(BatchContext batch, Schemas.Sequence sequence)
    {
        var checkedIds = batch.ExecuteCheckedFunctionIds ??= [];
        var key = CheckedKey(batch, sequence.ObjectId);
        if (checkedIds.Contains(key))
            return;
        CheckSchemaObject(batch, "UPDATE", sequence);
        _ = checkedIds.Add(key);
    }

    /// <summary>
    /// Checks a permission on an XML schema collection in the current
    /// database — <c>REFERENCES</c> for a column typed by it, <c>EXECUTE</c>
    /// for a variable — raising Msg 229 naming it (probed 2026-10-04 against
    /// SQL Server 2025).
    /// </summary>
    internal static void CheckXmlSchemaCollection(BatchContext batch, Schemas.XmlSchemaCollection collection, string permission)
    {
        var database = batch.CurrentDatabase;
        if (TryResolveScope(batch, database, out var principalId)
            && !PermissionChecker.IsGranted(database, principalId, Permission.Resolve(permission), PermissionChecker.ClassXmlSchemaCollection, collection.Id, collection.SchemaId, Rights(batch)))
        {
            throw SimulatedSqlException.PermissionDenied(permission, collection.Name, database.Name, SchemaNameFor(database, collection.SchemaId));
        }
    }

    /// <summary>Checks one permission on one object in <paramref name="database"/>; throws Msg 229 (with optional Procedure attribution) on denial. No-op when checks don't apply.</summary>
    internal static void CheckObject(BatchContext batch, Database database, string permission, int objectId, int schemaId, string objectName, string schemaName, string procedure = "", Schemas.SchemaObject? securable = null)
    {
        if (!TryResolveScope(batch, database, objectId, out var principalId, securable))
            return;
        if (!PermissionChecker.IsGranted(database, principalId, Permission.Resolve(permission), PermissionChecker.ClassObject, objectId, schemaId, Rights(batch), securable))
            throw SimulatedSqlException.PermissionDenied(permission.ToUpperInvariant(), objectName, database.Name, schemaName, procedure);
    }

    /// <summary>Checks a permission on a resolved securable — a table, view, synonym or module; no-op when checks don't apply.</summary>
    internal static void CheckSchemaObject(BatchContext batch, string permission, Schemas.SchemaObject securable, string procedure = "")
    {
        // A table variable or temp table is no securable: every session that
        // can name one may write it (probed 2026-09-27 against SQL Server
        // 2025 — a restricted user inserts into its own @t and #t).
        if (securable is Storage.HeapTable { IsTableVariable: true } or Storage.HeapTable { Name: ['#', ..] })
            return;
        var database = batch.DatabaseFor(securable);
        CheckObject(batch, database, permission, securable.ObjectId, securable.SchemaId, securable.Name, SchemaNameFor(database, securable.SchemaId), procedure, securable);
    }

    /// <summary><see cref="CheckSchemaObject"/> returning a Msg 229 refusal rather than raising it, so a statement can report every permission it lacks together.</summary>
    internal static SimulatedSqlException? SchemaObjectDenial(BatchContext batch, string permission, Schemas.SchemaObject securable)
    {
        try
        {
            CheckSchemaObject(batch, permission, securable);
            return null;
        }
        catch (SimulatedSqlException denied) when (denied.Number == 229)
        {
            return denied;
        }
    }

    /// <summary>
    /// Checks a permission on the securable a reference written as
    /// <paramref name="writtenName"/> reached (see <see cref="SecurableFor"/>);
    /// no-op when checks don't apply.
    /// </summary>
    internal static void CheckReference(BatchContext batch, string permission, MultiPartName writtenName, Schemas.SchemaObject resolved, string procedure = "") =>
        CheckSchemaObject(batch, permission, SecurableFor(batch, writtenName, resolved), procedure);

    /// <summary>
    /// <see cref="CheckReference"/> — or, with no <paramref name="writtenName"/>,
    /// <see cref="CheckSchemaObject"/> — for a check a statement makes as it
    /// parses rather than as it starts executing: the check is made now, unless the
    /// batch is only compiling, and when the parse is one the plan cache may
    /// keep it is also recorded among the parse's locks, for a replay to make
    /// again in the same place as its own principal. Whoever parses, the
    /// recording is the same — the check is recorded even for a principal it
    /// waves through — which is what keeps the cached plan principal-independent.
    /// </summary>
    internal static void CheckWhileParsing(BatchContext batch, string permission, MultiPartName? writtenName, Schemas.SchemaObject resolved)
    {
        if (batch.IsSkipping)
            return;
        var check = new CompiledPermissionCheck(permission, writtenName, resolved);
        batch.ReplayLockLog?.Add(new ReplayedLock(check));
#if DEBUG
        // The check reads the principal, which the replay reads again.
        using var recorded = PlanCacheCaptureAudit.SuspendPrincipalWatch();
#endif
        check.Run(batch);
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

    internal static string SchemaNameFor(Database database, int schemaId)
    {
        foreach (var (_, schema) in database.Schemas)
        {
            if (schema.SchemaId == schemaId)
                return schema.Name;
        }
        return Database.DefaultSchemaName;
    }

    /// <summary>Whether the effective principal may run any DDL in <paramref name="database"/> (a <c>db_owner</c> / <c>db_ddladmin</c> member) — the gate for the statements that raise Msg 15247 (CREATE SEQUENCE / ROLE / USER / SCHEMA). True for dbo / module bodies.</summary>
    internal static bool HasDdlAdminCapability(BatchContext batch, Database database) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsDdlAdminOrOwner(database, principalId)
        || Rights(batch).Implies(Permission.Control, PermissionChecker.ClassDatabase);

    /// <summary>Whether the effective principal is a <c>db_owner</c> member of <paramref name="database"/> (the DROP USER gate). True for dbo / module bodies.</summary>
    internal static bool IsOwner(BatchContext batch, Database database) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsOwner(database, principalId)
        || Rights(batch).Implies(Permission.Control, PermissionChecker.ClassDatabase);

    /// <summary>Whether the effective principal is a <c>db_owner</c> or <c>db_backupoperator</c> member of <paramref name="database"/> (the <c>CHECKPOINT</c> gate). True for dbo / module bodies.</summary>
    internal static bool IsOwnerOrBackupOperator(BatchContext batch, Database database) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsOwnerOrBackupOperator(database, principalId)
        || Rights(batch).Implies(Permission.Control, PermissionChecker.ClassDatabase);

    /// <summary>Whether the effective principal holds a database-scope permission in <paramref name="database"/> (CREATE TABLE gate, CONNECT, etc.). Always true for dbo / module bodies.</summary>
    internal static bool HasDatabasePermission(BatchContext batch, Database database, string permission) =>
        HasDatabasePermission(batch, database, Permission.Resolve(permission));

    /// <summary>Typed form of <see cref="HasDatabasePermission(BatchContext, Database, string)"/> — the DDL gates that name a catalog permission directly.</summary>
    internal static bool HasDatabasePermission(BatchContext batch, Database database, Permission permission) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsGranted(database, principalId, permission, PermissionChecker.ClassDatabase, 0, 0, Rights(batch));

    /// <summary>Whether the effective principal holds a database permission named by its canonical text — one the <see cref="Permission"/> enum doesn't carry. True for dbo / module bodies.</summary>
    internal static bool HoldsDatabasePermission(BatchContext batch, Database database, string permissionName) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsGrantedByName(database, principalId, permissionName, PermissionChecker.ClassDatabase, 0, 0, Rights(batch));

    /// <summary>
    /// Whether the effective principal holds CONTROL on <paramref name="schema"/>
    /// — schema-scope CONTROL, or database-scope CONTROL. The <c>DROP SCHEMA</c>
    /// gate's other half (probe-confirmed: schema ALTER is <em>not</em> enough
    /// there, unlike every object drop).
    /// </summary>
    internal static bool HasSchemaControl(BatchContext batch, Schema schema) =>
        !TryResolveScope(batch, schema.Database, out var principalId)
        || PermissionChecker.IsGranted(schema.Database, principalId,
            Permission.Control, PermissionChecker.ClassSchema, schema.SchemaId, 0, Rights(batch));

    /// <summary>
    /// Whether the effective principal may <c>DROP</c> an object in
    /// <paramref name="schema"/>: ALTER on the schema <strong>or</strong> CONTROL
    /// on the object itself — probe-confirmed as the pair real accepts for every
    /// object kind (a plain object-scope ALTER is not enough, which is what
    /// separates a DROP from an ALTER TABLE). True for dbo / module bodies.
    /// </summary>
    internal static bool HasDropAuthority(BatchContext batch, Schema schema, int objectId) =>
        HasSchemaAlter(batch, schema)
        || HasObjectControl(batch, schema.Database, objectId, schema.SchemaId);

    /// <summary>Whether the effective principal holds CONTROL on the given object (the DROP alternative and the <c>ALTER SCHEMA … TRANSFER</c> source gate). True for dbo / module bodies.</summary>
    internal static bool HasObjectControl(BatchContext batch, Database database, int objectId, int schemaId) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsGranted(database, principalId, Permission.Control, PermissionChecker.ClassObject, objectId, schemaId, Rights(batch));

    /// <summary>
    /// Whether the effective principal holds <paramref name="permission"/> on the
    /// described securable in <paramref name="database"/> — the generic form of
    /// the typed gates above. True for dbo / module bodies.
    /// </summary>
    internal static bool HoldsPermission(BatchContext batch, Database database, Permission permission, byte securableClass, int majorId, int schemaId) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsGranted(database, principalId, permission, securableClass, majorId, schemaId, Rights(batch));

    /// <summary>
    /// Whether the effective principal may name <paramref name="targetPrincipalId"/>
    /// as a new owner: itself or a role it belongs to, else <c>IMPERSONATE</c> on
    /// the target (which <c>CONTROL</c> covers). True for dbo / module bodies.
    /// </summary>
    internal static bool MayActAs(BatchContext batch, Database database, int targetPrincipalId) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.BuildPrincipalClosure(database, principalId).Contains(targetPrincipalId)
        || PermissionChecker.IsGranted(database, principalId, Permission.Impersonate, PermissionChecker.ClassDatabasePrincipal, targetPrincipalId, 0, Rights(batch));

    /// <summary>
    /// Whether the effective principal may create or drop a database — the one
    /// gate that answers at <em>server</em> scope. Satisfied by the server
    /// permission (<c>CREATE ANY DATABASE</c> / <c>ALTER ANY DATABASE</c>, which
    /// covers it) or by <c>dbcreator</c> membership; probe-confirmed that a plain
    /// login holds neither and that <c>dbcreator</c> carries both statements.
    /// True for dbo / module bodies.
    /// </summary>
    internal static bool HasDatabaseDdlAuthority(BatchContext batch, Permission permission)
    {
        var security = batch.Connection.Security;
        if (security.EffectiveIsDbo || batch.CreateTimeBinding || !batch.EnforcesPermissions)
            return true;
        var simulation = batch.Connection.Simulation;
        var login = security.Effective.LoginName;
        return simulation.HoldsServerPermission(login, permission)
            || simulation.IsLoginInServerRole(login, Simulation.DbCreatorRoleId);
    }

    /// <summary>
    /// Whether the effective principal holds ALTER on <paramref name="schema"/>
    /// (the DDL gate for CREATE TABLE / VIEW / PROCEDURE / FUNCTION and DROP
    /// TABLE). True for dbo / module bodies; satisfied by schema-scope ALTER /
    /// CONTROL, database-scope ALTER / CONTROL, and the <c>db_ddladmin</c> /
    /// <c>db_owner</c> fixed roles — but NOT by an object-scope ALTER (probe M5b).
    /// </summary>
    internal static bool HasSchemaAlter(BatchContext batch, Schema schema) =>
        !TryResolveScope(batch, schema.Database, out var principalId)
        || PermissionChecker.IsGranted(schema.Database, principalId,
            Permission.Alter, PermissionChecker.ClassSchema, schema.SchemaId, 0, Rights(batch));

    /// <summary>
    /// Whether the effective principal holds ALTER on the given object (the DDL
    /// gate for ALTER TABLE — object-scope ALTER suffices, probe M5b). True for
    /// dbo / module bodies.
    /// </summary>
    internal static bool HasObjectAlter(BatchContext batch, Database database, int objectId, int schemaId) =>
        !TryResolveScope(batch, database, out var principalId)
        || PermissionChecker.IsGranted(database, principalId,
            Permission.Alter, PermissionChecker.ClassObject, objectId, schemaId, Rights(batch));

    /// <summary>
    /// The dual DDL gate for <c>CREATE VIEW</c> / <c>PROCEDURE</c> /
    /// <c>FUNCTION</c>: the database-scope CREATE-of-that-kind permission (Msg
    /// 262 state 18, the module carried as Procedure attribution) plus ALTER on
    /// the target schema (Msg 2760). No-op for dbo / module bodies.
    /// </summary>
    internal static void CheckCreateModule(BatchContext batch, string permission, string moduleName, Schema schema)
    {
        if (Bypasses(batch.Connection, schema.Database) || batch.CreateTimeBinding)
            return;
        if (!HasDatabasePermission(batch, schema.Database, permission))
            throw SimulatedSqlException.CreateModulePermissionDenied(permission, schema.Database.Name, moduleName);
        if (!HasSchemaAlter(batch, schema))
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
    private const int DbAccessAdmin = 16385;
    private const int DbSecurityAdmin = 16386;
    private const int DbDdlAdmin = 16387;
    private const int DbBackupOperator = 16389;
    private const int DbDataReader = 16390;
    private const int DbDataWriter = 16391;
    private const int DbDenyDataReader = 16392;
    private const int DbDenyDataWriter = 16393;

    // Securable classes (sys.database_permissions.class).
    internal const byte ClassDatabase = 0;
    internal const byte ClassObject = 1;
    internal const byte ClassSchema = 3;
    internal const byte ClassDatabasePrincipal = 4;
    internal const byte ClassType = 6;
    internal const byte ClassXmlSchemaCollection = 10;
    internal const byte ClassFulltextCatalog = 23;

    // Server-scope permissions (sys.server_permissions.class = 100) live on
    // Simulation.ServerPermissions, not a Database; Simulation.HoldsServerPermission
    // walks them, but the covering graph shares this class tag so the VIEW
    // …STATE server-scope edges resolve through Permission.Covering.
    internal const byte ClassServer = 100;

    // The ON LOGIN:: securable (sys.server_permissions.class = 101,
    // class_desc SERVER_PRINCIPAL), major_id = the target login's
    // principal_id. Shares Simulation.ServerPermissions with class 100;
    // Simulation.HoldsServerPrincipalPermission walks both, since a class-100
    // blanket grant (IMPERSONATE ANY LOGIN, VIEW ANY DEFINITION) satisfies a
    // per-login request while a class-101 DENY overrides it.
    internal const byte ClassServerPrincipal = 101;

    /// <summary>
    /// The database permissions each fixed database role carries — what
    /// <c>fn_my_permissions(NULL, 'DATABASE')</c> lists for a member, less
    /// <c>CONNECT</c> and the two column-encryption permissions every user holds
    /// (probed 2026-10-04 against SQL Server 2025). The permission graph takes
    /// them from there: <c>db_ddladmin</c>'s <c>ALTER ANY SCHEMA</c> reaches
    /// every object's <c>ALTER</c>, and <c>db_owner</c>'s <c>CONTROL</c> all of it.
    /// </summary>
    private static readonly (int RoleId, string[] Permissions)[] FixedRoleGrants =
    [
        (DbOwner, ["CONTROL"]),
        (DbAccessAdmin, ["ALTER ANY USER", "CREATE SCHEMA"]),
        (DbSecurityAdmin, ["ALTER ANY APPLICATION ROLE", "ALTER ANY ROLE", "CREATE SCHEMA", "VIEW DEFINITION"]),
        (DbDdlAdmin,
        [
            "ALTER ANY ASSEMBLY", "ALTER ANY ASYMMETRIC KEY", "ALTER ANY CERTIFICATE", "ALTER ANY CONTRACT",
            "ALTER ANY DATABASE DDL TRIGGER", "ALTER ANY DATABASE EVENT NOTIFICATION", "ALTER ANY DATASPACE",
            "ALTER ANY EXTERNAL DATA SOURCE", "ALTER ANY EXTERNAL FILE FORMAT", "ALTER ANY EXTERNAL JOB",
            "ALTER ANY EXTERNAL LANGUAGE", "ALTER ANY EXTERNAL LIBRARY", "ALTER ANY EXTERNAL MODEL", "ALTER ANY EXTERNAL STREAM",
            "ALTER ANY FULLTEXT CATALOG", "ALTER ANY MESSAGE TYPE", "ALTER ANY REMOTE SERVICE BINDING", "ALTER ANY ROUTE",
            "ALTER ANY SCHEMA", "ALTER ANY SERVICE", "ALTER ANY SYMMETRIC KEY", "ALTER LEDGER", "CHECKPOINT",
            "CREATE AGGREGATE", "CREATE ASSEMBLY", "CREATE ASYMMETRIC KEY", "CREATE CERTIFICATE", "CREATE CONTRACT",
            "CREATE DATABASE DDL EVENT NOTIFICATION", "CREATE DEFAULT", "CREATE EXTERNAL LANGUAGE", "CREATE EXTERNAL LIBRARY",
            "CREATE EXTERNAL MODEL", "CREATE FULLTEXT CATALOG", "CREATE FUNCTION", "CREATE MESSAGE TYPE", "CREATE PROCEDURE",
            "CREATE QUEUE", "CREATE REMOTE SERVICE BINDING", "CREATE ROUTE", "CREATE RULE", "CREATE SCHEMA", "CREATE SERVICE",
            "CREATE SYMMETRIC KEY", "CREATE SYNONYM", "CREATE TABLE", "CREATE TYPE", "CREATE VIEW", "CREATE XML SCHEMA COLLECTION",
            "ENABLE LEDGER", "REFERENCES",
        ]),
        (DbBackupOperator, ["BACKUP DATABASE", "BACKUP LOG", "CHECKPOINT"]),
        (DbDataReader, ["SELECT"]),
        (DbDataWriter, ["DELETE", "INSERT", "UPDATE"]),
    ];

    /// <summary>The database permissions the two deny roles deny their members, as a database-scope <c>DENY</c> would.</summary>
    private static readonly (int RoleId, string[] Permissions)[] FixedRoleDenies =
    [
        (DbDenyDataReader, ["SELECT"]),
        (DbDenyDataWriter, ["DELETE", "INSERT", "UPDATE"]),
    ];

    /// <summary>
    /// Whether <paramref name="principalId"/> may read the catalog view with id
    /// <paramref name="viewId"/>: a <c>DENY SELECT</c> reaching it refuses —
    /// <c>db_denydatareader</c>'s included (probed 2026-10-04 against SQL
    /// Server 2025) — and one of the system objects the database grants
    /// <c>public</c> <c>SELECT</c> on (<see cref="Database.SeedsPublicSelect"/>)
    /// needs that grant, or another, still standing — a <c>REVOKE</c> of the
    /// seeded one refuses it (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static bool CanReadCatalogView(Database database, int principalId, int viewId, int schemaId)
    {
        var closure = BuildClosure(database, principalId);
        var satisfiers = BuildSatisfiers(database, Permission.Select, ClassObject, viewId, schemaId, columnOrdinal: 0);
        // A database-scope DENY SELECT doesn't reach the INFORMATION_SCHEMA
        // views (probed 2026-10-04 against SQL Server 2025).
        if (schemaId == Database.InformationSchemaId)
            satisfiers = satisfiers.WithoutDatabase();
        if (HasMatchingRow(database, closure, satisfiers, deny: true) || FixedRolesMatch(closure, satisfiers, deny: true))
            return false;
        return !(database.SeedsPublicSelect(viewId) || Array.IndexOf(UngrantedSystemObjectIds, viewId) >= 0)
            || closure.Contains(DbOwner)
            || HasMatchingRow(database, closure, satisfiers, deny: false)
            || FixedRolesMatch(closure, satisfiers, deny: false);
    }

    /// <summary>
    /// The system views and functions a user database grants nobody — every
    /// one a restricted principal there may not read, which
    /// <c>HAS_PERMS_BY_NAME</c> lists (probed 2026-10-04 against SQL Server
    /// 2025): <c>sys.sql_expression_dependencies</c>, the
    /// <c>system_internals_*</c> trio, <c>sysaltfiles</c>,
    /// <c>database_mirroring_witnesses</c> and five internal functions.
    /// </summary>
    private static readonly int[] UngrantedSystemObjectIds =
        [-1057103479, -1052007962, -857107446, -495, -488, -487, -486, -432442948, -292564105, -214, -192];

    /// <summary>Whether the effective principal holds <paramref name="permission"/> on the described securable. An off-catalog (<see cref="Permission.Other"/>) request is never satisfied.</summary>
    internal static bool IsGranted(Database database, int principalId, Permission permission, byte securableClass, int majorId, int schemaId, ServerLoginRights server = default, Schemas.SchemaObject? securable = null) =>
        permission != Permission.Other
        && IsGrantedInClosure(database, BuildClosure(database, principalId), permission.CanonicalName, securableClass, majorId, schemaId, server, permission, securable);

    /// <summary><see cref="IsGranted(Database, int, Permission, byte, int, int, ServerLoginRights, Schemas.SchemaObject)"/> over a principal closure the caller already built.</summary>
    internal static bool IsGrantedInClosure(Database database, HashSet<int> closure, Permission permission, byte securableClass, int majorId, int schemaId, ServerLoginRights server = default, Schemas.SchemaObject? securable = null) =>
        permission != Permission.Other
        && IsGrantedInClosure(database, closure, permission.CanonicalName, securableClass, majorId, schemaId, server, permission, securable);

    /// <summary>
    /// <see cref="IsGranted(Database, int, Permission, byte, int, int, ServerLoginRights, Schemas.SchemaObject)"/>
    /// for a permission named by its canonical text — one the
    /// <see cref="Permission"/> enum doesn't carry (<c>CREATE ROLE</c>,
    /// <c>ALTER ANY USER</c>, …), answered from the same graph.
    /// </summary>
    internal static bool IsGrantedByName(Database database, int principalId, string permissionName, byte securableClass, int majorId, int schemaId, ServerLoginRights server = default) =>
        IsGrantedInClosure(database, BuildClosure(database, principalId), permissionName, securableClass, majorId, schemaId, server, Permission.Resolve(permissionName));

    private static bool IsGrantedInClosure(Database database, HashSet<int> closure, string permissionName, byte securableClass, int majorId, int schemaId, ServerLoginRights server, Permission permission, Schemas.SchemaObject? securable = null)
    {
        if (OwnsSecurable(database, closure, securableClass, majorId, schemaId, securable))
            return true;
        var satisfiers = BuildSatisfiers(database, permissionName, securableClass, majorId, schemaId, columnOrdinal: 0);

        // DENY binds first — explicit D rows, then the deny roles.
        if (HasMatchingRow(database, closure, satisfiers, deny: true) || FixedRolesMatch(closure, satisfiers, deny: true))
            return false;

        // GRANT test — explicit G/W rows, then the fixed roles' own grants,
        // then the login's server permissions: for a name the enum doesn't
        // carry, through the database permissions the graph says imply it.
        if (HasMatchingRow(database, closure, satisfiers, deny: false) || FixedRolesMatch(closure, satisfiers, deny: false))
            return true;
        if (permission != Permission.Other)
            return server.Implies(permission, securableClass);
        foreach (var s in satisfiers)
        {
            if (s.Class == ClassDatabase && Permission.Resolve(s.Name) is var implied && implied != Permission.Other && server.Implies(implied, ClassDatabase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether the principal closure <paramref name="closure"/> owns the
    /// securable — an object through its effective owner or its schema's
    /// owner, a schema through its owner, a role through its owner. An owner
    /// holds <c>CONTROL</c> that no <c>DENY</c> can take away, and a role's
    /// members share its ownership (probed 2026-09-27 against SQL Server 2025:
    /// a member of the role that owns a table reads it with no grant).
    /// Restricted principals only reach this, so the linear owner lookups stay
    /// off the <c>dbo</c> path, and a caller holding the object passes it as
    /// <paramref name="securable"/>, which spares the walk of every schema's
    /// objects that finding it by id costs — a walk each column check of a
    /// statement would otherwise repeat.
    /// </summary>
    private static bool OwnsSecurable(Database database, HashSet<int> closure, byte securableClass, int majorId, int schemaId, Schemas.SchemaObject? securable = null)
    {
        switch (securableClass)
        {
            case ClassObject:
                return closure.Contains(Ownership.SchemaOwnerId(database, schemaId))
                    || ((securable is null ? Ownership.EffectiveOwnerId(database, majorId) : Ownership.RegisteredOwnerId(database, securable)) is int owner
                        && closure.Contains(owner));
            case ClassSchema:
                return closure.Contains(Ownership.SchemaOwnerId(database, majorId));
            case ClassType or ClassXmlSchemaCollection:
                return closure.Contains(Ownership.SchemaOwnerId(database, schemaId));
            case ClassDatabasePrincipal:
                foreach (var (_, principal) in database.Principals)
                {
                    // A user holds CONTROL on itself (probed 2026-10-04
                    // against SQL Server 2025: fn_my_permissions lists it).
                    if (principal.PrincipalId == majorId)
                    {
                        return principal.TypeCode == "R"
                            ? closure.Contains(principal.OwningPrincipalId)
                            : principal.TypeCode != "A" && majorId > Database.SysPrincipalId && closure.Contains(majorId);
                    }
                }
                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// Whether the effective principal may read / write column
    /// <paramref name="columnOrdinal"/> (1-based, matching
    /// <c>sys.columns.column_id</c>) of the object. A column-level row decides
    /// for its own grantee ahead of that grantee's object-level row — a column
    /// <c>GRANT</c> admits the column under the same principal's table
    /// <c>DENY</c>, and a column <c>REVOKE</c> (the <c>R</c> row a column
    /// revoke of a table-level grant leaves) takes the column out of that
    /// principal's table, schema and database grants — while a schema or
    /// database <c>DENY</c>, another principal's <c>DENY</c> and the deny roles
    /// still bind (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static bool IsColumnGranted(Database database, int principalId, Permission permission, int objectId, int schemaId, int columnOrdinal, ServerLoginRights server = default, Schemas.SchemaObject? securable = null) =>
        IsColumnGranted(database, BuildClosure(database, principalId), permission, objectId, schemaId, columnOrdinal, server, securable);

    /// <summary><see cref="IsColumnGranted(Database, int, Permission, int, int, int, ServerLoginRights, Schemas.SchemaObject)"/> over a closure the caller built once for several columns.</summary>
    internal static bool IsColumnGranted(Database database, HashSet<int> closure, Permission permission, int objectId, int schemaId, int columnOrdinal, ServerLoginRights server = default, Schemas.SchemaObject? securable = null)
    {
        if (permission == Permission.Other)
            return false;

        if (OwnsSecurable(database, closure, ClassObject, objectId, schemaId, securable))
            return true;
        var satisfiers = BuildSatisfiers(database, permission.CanonicalName, ClassObject, objectId, schemaId, columnOrdinal);

        foreach (var s in satisfiers)
        {
            foreach (var row in database.Permissions.On(s.Class, s.MajorId))
            {
                if (row.State != PermissionState.Deny || !Matches(row, s) || !closure.Contains(row.GranteePrincipalId))
                    continue;
                if (row.Class == ClassObject && row.MinorId == 0
                    && HasColumnRow(database, row.GranteePrincipalId, objectId, columnOrdinal, satisfiers, grant: true))
                {
                    continue;
                }
                return false;
            }
        }
        if (FixedRolesMatch(closure, satisfiers, deny: true))
            return false;

        foreach (var s in satisfiers)
        {
            foreach (var row in database.Permissions.On(s.Class, s.MajorId))
            {
                if (row.State is not (PermissionState.Grant or PermissionState.GrantWithGrantOption)
                    || !Matches(row, s) || !closure.Contains(row.GranteePrincipalId))
                {
                    continue;
                }
                if (row.MinorId == 0 && HasColumnRow(database, row.GranteePrincipalId, objectId, columnOrdinal, satisfiers, grant: false))
                    continue;
                return true;
            }
        }
        return FixedRolesMatch(closure, satisfiers, deny: false)
            || server.Implies(permission, ClassObject);
    }

    /// <summary>
    /// Whether <paramref name="granteeId"/> holds a column-level row on
    /// <paramref name="columnOrdinal"/> of the object for one of the request's
    /// object-class permissions — a <c>G</c> / <c>W</c> row when
    /// <paramref name="grant"/>, else an <c>R</c> row.
    /// </summary>
    private static bool HasColumnRow(Database database, int granteeId, int objectId, int columnOrdinal, Satisfiers satisfiers, bool grant)
    {
        foreach (var row in database.Permissions.On(ClassObject, objectId))
        {
            if (row.GranteePrincipalId != granteeId || row.MinorId != columnOrdinal)
                continue;
            var stateMatches = grant ? row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption : row.State == PermissionState.Revoke;
            if (stateMatches && Matches(row, satisfiers))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether any permission row is stored on one of the object's columns —
    /// without one, <see cref="IsColumnGranted(Database, HashSet{int}, Permission, int, int, int, ServerLoginRights, Schemas.SchemaObject)"/>
    /// answers every column as <see cref="IsGrantedInClosure(Database, HashSet{int}, Permission, byte, int, int, ServerLoginRights, Schemas.SchemaObject)"/>
    /// answers the object, since only a column-scoped row tells them apart.
    /// </summary>
    internal static bool HasColumnRows(Database database, int objectId)
    {
        foreach (var row in database.Permissions.On(ClassObject, objectId))
        {
            if (row.MinorId != 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether any of the object's columns is accessible to the principal — what separates the column-level Msg 230
    /// from the object-level Msg 229 once the object-grain check has failed:
    /// real reports the object while no column of it is reachable, a column
    /// grant defeated by a schema <c>DENY</c> included (probed 2026-10-04
    /// against SQL Server 2025).
    /// </summary>
    internal static bool HasAccessibleColumn(Database database, int principalId, Permission permission, int objectId, int schemaId, ServerLoginRights server = default) =>
        HasAccessibleColumn(database, BuildClosure(database, principalId), permission, objectId, schemaId, server);

    /// <summary>
    /// <see cref="HasAccessibleColumn(Database, int, Permission, int, int, ServerLoginRights)"/>
    /// over a built closure. Only a column that carries a column-level grant to
    /// the closure can be reachable here — the object-grain check already
    /// failed — so just those columns are asked.
    /// </summary>
    internal static bool HasAccessibleColumn(Database database, HashSet<int> closure, Permission permission, int objectId, int schemaId, ServerLoginRights server, Schemas.SchemaObject? securable = null)
    {
        foreach (var row in database.Permissions.On(ClassObject, objectId))
        {
            if (row.MinorId != 0
                && row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                && closure.Contains(row.GranteePrincipalId)
                && IsColumnGranted(database, closure, permission, objectId, schemaId, row.MinorId, server, securable))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether the effective principal is a member of <paramref name="role"/> (transitively), or the role is <c>public</c> (everyone).</summary>
    internal static bool IsRoleMember(Database database, int principalId, DatabasePrincipal role) =>
        role.PrincipalId == 0 || BuildClosure(database, principalId).Contains(role.PrincipalId);

    /// <summary>Whether the principal is a (transitive) member of <c>db_owner</c> or <c>db_ddladmin</c>.</summary>
    internal static bool IsDdlAdminOrOwner(Database database, int principalId)
    {
        var closure = BuildClosure(database, principalId);
        return closure.Contains(DbOwner) || closure.Contains(DbDdlAdmin);
    }

    /// <summary>Whether the principal is a (transitive) member of <c>db_owner</c> or <c>db_backupoperator</c>.</summary>
    internal static bool IsOwnerOrBackupOperator(Database database, int principalId)
    {
        var closure = BuildClosure(database, principalId);
        return closure.Contains(DbOwner) || closure.Contains(DbBackupOperator);
    }

    /// <summary>Whether the principal is a (transitive) member of <c>db_owner</c>.</summary>
    internal static bool IsOwner(Database database, int principalId) =>
        BuildClosure(database, principalId).Contains(DbOwner);

    /// <summary>The effective principal + every role it belongs to transitively + <c>public</c> — exposed for the per-enumeration metadata-visibility scan so it builds the closure once.</summary>
    internal static HashSet<int> BuildPrincipalClosure(Database database, int principalId) =>
        BuildClosure(database, principalId);

    /// <summary>
    /// Whether the principal sees every object's metadata regardless of grants:
    /// a <c>db_ddladmin</c> member (probe-confirmed against SQL Server 2025), or
    /// a holder of <c>VIEW DEFINITION</c> on the database — <c>db_owner</c>'s
    /// <c>CONTROL</c> and <c>db_securityadmin</c>'s own grant included — that no
    /// <c>DENY</c> takes away.
    /// </summary>
    internal static bool HasFullMetadataVisibility(Database database, int principalId, ServerLoginRights server = default) =>
        HasFullMetadataVisibility(database, BuildClosure(database, principalId), server);

    private static bool HasFullMetadataVisibility(Database database, HashSet<int> closure, ServerLoginRights server)
    {
        // A DENY of VIEW DEFINITION or CONTROL on a schema or an object hides
        // what it reaches even from a principal that sees the rest (probed
        // 2026-10-04 against SQL Server 2025), so such a principal is
        // filtered object by object.
        foreach (var row in database.Permissions)
        {
            if (row.State == PermissionState.Deny && row.Class is ClassObject or ClassSchema
                && row.Permission is Permission.ViewDefinition or Permission.Control
                && closure.Contains(row.GranteePrincipalId))
            {
                return false;
            }
        }
        return closure.Contains(DbDdlAdmin)
            || IsGrantedInClosure(database, closure, "VIEW DEFINITION", ClassDatabase, 0, 0, server, Permission.ViewDefinition);
    }

    /// <summary>
    /// Whether the principal may see the metadata (catalog-view rows,
    /// <c>OBJECT_ID</c> / <c>OBJECT_NAME</c> / <c>OBJECT_SCHEMA_NAME</c> results)
    /// of the object with the given id / schema. A <c>DENY</c> of
    /// <c>VIEW DEFINITION</c> reaching the object hides it whatever else is
    /// granted; otherwise it shows under the full-visibility bypass
    /// (<see cref="HasFullMetadataVisibility(Database,int,ServerLoginRights)"/>),
    /// to its owner, and to a principal holding a grant of any permission that
    /// applies to its kind — at object scope any row on it, a column's
    /// included, and at schema and database scope a permission whose graph
    /// reaches one the object's kind takes, so a schema <c>SELECT</c> reveals
    /// the schema's tables and views but not its procedures, and
    /// <c>db_datareader</c> likewise (probed 2026-10-04 against SQL Server 2025).
    /// </summary>
    internal static bool CanViewMetadata(Database database, int principalId, int objectId, int schemaId, ServerLoginRights server = default) =>
        CanViewMetadata(database, BuildClosure(database, principalId), objectId, schemaId, server);

    internal static bool CanViewMetadata(Database database, HashSet<int> closure, int objectId, int schemaId, ServerLoginRights server = default)
    {
        var kind = Parser.Expressions.ObjectProperty.FindObject(database, objectId) is { } found ? KindOf(found) : ObjectKind.Tabular;
        if (IsDenied(database, closure, "VIEW DEFINITION", ClassObject, objectId, schemaId))
            return false;
        // A server permission that grants something on every user object
        // reveals it as a database grant would: SELECT ALL USER SECURABLES,
        // and ALTER ANY DATABASE through each database's ALTER (probed
        // 2026-09-29 against SQL Server 2025).
        if (HasFullMetadataVisibility(database, closure, server)
            || OwnsSecurable(database, closure, ClassObject, objectId, schemaId)
            || server.Holds(Permission.SelectAllUserSecurables)
            || server.Holds(Permission.AlterAnyDatabase))
        {
            return true;
        }
        // A cheap scan for any grant that could reveal the object, then the
        // confirmation that one of the kind's permissions is actually held —
        // a grant a DENY cancels reveals nothing (probed 2026-10-04 against
        // SQL Server 2025: db_datareader beside db_denydatareader sees no
        // table).
        if (!HasRevealingGrant(database, closure, objectId, schemaId, kind))
            return false;
        foreach (var permission in ApplicablePermissions(kind))
        {
            if (IsGrantedInClosure(database, closure, permission, ClassObject, objectId, schemaId, server, Permission.Resolve(permission)))
                return true;
        }
        foreach (var row in database.Permissions.On(ClassObject, objectId))
        {
            if (row.MinorId != 0
                && row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption && closure.Contains(row.GranteePrincipalId))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasRevealingGrant(Database database, HashSet<int> closure, int objectId, int schemaId, ObjectKind kind)
    {
        var revealing = RevealingPermissions(kind);
        foreach (var row in database.Permissions)
        {
            if (row.State is not (PermissionState.Grant or PermissionState.GrantWithGrantOption)
                || !closure.Contains(row.GranteePrincipalId))
            {
                continue;
            }
            var reveals = row.Class switch
            {
                ClassDatabase => Contains(revealing.Database, row.DisplayName),
                ClassObject => row.MajorId == objectId,
                ClassSchema => row.MajorId == schemaId && Contains(revealing.Schema, row.DisplayName),
                _ => false,
            };
            if (reveals)
                return true;
        }
        foreach (var (roleId, permissions) in FixedRoleGrants)
        {
            if (!closure.Contains(roleId))
                continue;
            foreach (var permission in permissions)
            {
                if (Contains(revealing.Database, permission))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The database principals a restricted principal sees in
    /// <c>sys.database_principals</c>: the five catalog principals and the
    /// fixed roles, itself and every role it belongs to, the principals its
    /// closure owns, and those it holds a permission on (probed 2026-10-04
    /// against SQL Server 2025 — a fellow <c>db_datareader</c> member stays
    /// hidden).
    /// </summary>
    internal static HashSet<int>? VisiblePrincipals(Database database, int principalId, ServerLoginRights server)
    {
        var closure = BuildClosure(database, principalId);
        if (IsGrantedInClosure(database, closure, "VIEW DEFINITION", ClassDatabase, 0, 0, server, Permission.ViewDefinition))
            return null;
        // The ALTER ANY permission of each principal kind reveals that kind:
        // db_accessadmin sees every user, db_securityadmin every role.
        var users = IsGrantedInClosure(database, closure, "ALTER ANY USER", ClassDatabase, 0, 0, server, Permission.Other);
        var roles = IsGrantedInClosure(database, closure, "ALTER ANY ROLE", ClassDatabase, 0, 0, server, Permission.Other);
        var applicationRoles = IsGrantedInClosure(database, closure, "ALTER ANY APPLICATION ROLE", ClassDatabase, 0, 0, server, Permission.Other);
        var visible = new HashSet<int>(closure);
        foreach (var (_, principal) in database.Principals)
        {
            if (principal.PrincipalId <= Database.SysPrincipalId || principal.IsFixedRole || closure.Contains(principal.OwningPrincipalId)
                || principal.TypeCode switch { "A" => applicationRoles, "R" => roles, _ => users })
            {
                _ = visible.Add(principal.PrincipalId);
            }
        }
        foreach (var row in database.Permissions)
        {
            if (row.Class == ClassDatabasePrincipal && row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption
                && closure.Contains(row.GranteePrincipalId))
            {
                _ = visible.Add(row.MajorId);
            }
        }
        return visible;
    }

    /// <summary>
    /// The grantees whose <c>sys.database_permissions</c> rows a restricted
    /// principal sees, or null for all of them: itself and its roles, and
    /// every principal of a kind whose <c>ALTER ANY</c> permission it holds
    /// (probed 2026-10-04 against SQL Server 2025: a db_accessadmin member sees
    /// every user's CONNECT, a db_datareader member only its own).
    /// </summary>
    internal static HashSet<int>? VisibleGrantees(Database database, int principalId, ServerLoginRights server)
    {
        var closure = BuildClosure(database, principalId);
        if (IsGrantedInClosure(database, closure, "VIEW DEFINITION", ClassDatabase, 0, 0, server, Permission.ViewDefinition))
            return null;
        var users = IsGrantedInClosure(database, closure, "ALTER ANY USER", ClassDatabase, 0, 0, server, Permission.Other);
        var roles = IsGrantedInClosure(database, closure, "ALTER ANY ROLE", ClassDatabase, 0, 0, server, Permission.Other);
        var applicationRoles = IsGrantedInClosure(database, closure, "ALTER ANY APPLICATION ROLE", ClassDatabase, 0, 0, server, Permission.Other);
        if (users || roles || applicationRoles)
        {
            foreach (var (_, principal) in database.Principals)
            {
                if (principal.TypeCode switch { "A" => applicationRoles, "R" => roles, _ => users })
                    _ = closure.Add(principal.PrincipalId);
            }
        }
        return closure;
    }

    /// <summary>Whether a restricted principal sees a user-defined type: its owner, or a holder of any permission on it.</summary>
    internal static bool CanViewTypeMetadata(Database database, int principalId, int userTypeId, int schemaId, int ownerId, ServerLoginRights server)
    {
        var closure = BuildClosure(database, principalId);
        if (closure.Contains(ownerId))
            return true;
        foreach (var name in (string[])["CONTROL", "EXECUTE", "REFERENCES", "TAKE OWNERSHIP", "VIEW DEFINITION"])
        {
            if (IsGrantedInClosure(database, closure, name, ClassType, userTypeId, schemaId, server, Permission.Resolve(name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a restricted principal sees a full-text catalog's
    /// <c>sys.fulltext_catalogs</c> row: as its owner, with full metadata
    /// visibility, or holding any permission on the catalog.
    /// </summary>
    internal static bool CanViewFullTextCatalogMetadata(Database database, int principalId, int catalogId, int ownerId, ServerLoginRights server)
    {
        var closure = BuildClosure(database, principalId);
        if (closure.Contains(ownerId) || HasFullMetadataVisibility(database, closure, server))
            return true;
        foreach (var name in (string[])["ALTER", "CONTROL", "REFERENCES", "TAKE OWNERSHIP", "VIEW DEFINITION"])
        {
            if (IsGrantedInClosure(database, closure, name, ClassFulltextCatalog, catalogId, 0, server, Permission.Resolve(name)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <c>sys.sql_modules.definition</c>, <c>OBJECT_DEFINITION</c> and
    /// <c>sp_helptext</c> show the definition of an object the principal can
    /// see: its owner, or a holder of <c>VIEW DEFINITION</c>, <c>ALTER</c>,
    /// <c>CONTROL</c> or <c>TAKE OWNERSHIP</c> on it — on its parent table for a
    /// trigger (probed 2026-10-04 against SQL Server 2025: <c>SELECT</c> or
    /// <c>EXECUTE</c> alone reveals the row and leaves the definition NULL).
    /// </summary>
    internal static bool CanViewDefinition(Database database, int principalId, int objectId, int schemaId, ServerLoginRights server = default)
    {
        var closure = BuildClosure(database, principalId);
        if (HasFullMetadataVisibility(database, closure, server))
            return true;
        foreach (var permission in DefinitionPermissions)
        {
            if (IsGrantedInClosure(database, closure, permission.CanonicalName, ClassObject, objectId, schemaId, server, permission))
                return true;
        }
        return false;
    }

    private static readonly Permission[] DefinitionPermissions = [Permission.ViewDefinition, Permission.Alter, Permission.TakeOwnership];

    /// <summary>Whether a <c>DENY</c> reaches <paramref name="permissionName"/> on the securable — an explicit row or a deny role.</summary>
    private static bool IsDenied(Database database, HashSet<int> closure, string permissionName, byte securableClass, int majorId, int schemaId)
    {
        var satisfiers = BuildSatisfiers(database, permissionName, securableClass, majorId, schemaId, columnOrdinal: 0);
        return HasMatchingRow(database, closure, satisfiers, deny: true) || FixedRolesMatch(closure, satisfiers, deny: true);
    }

    /// <summary>The kinds of object whose applicable permissions differ.</summary>
    private enum ObjectKind : byte
    {
        Tabular,
        TableFunction,
        Procedure,
        ScalarFunction,
        Sequence,
        Other,
    }

    private static ObjectKind KindOf(Schemas.SchemaObject obj) => obj.ObjectTypeCode switch
    {
        "FN" or "FS" => ObjectKind.ScalarFunction,
        "IF" or "TF" or "FT" => ObjectKind.TableFunction,
        "P " or "PC" or "X " => ObjectKind.Procedure,
        "SN" => ObjectKind.Other,
        "SO" => ObjectKind.Sequence,
        _ => ObjectKind.Tabular,
    };

    /// <summary>The object permissions each kind takes — the ones whose grant at any scope reveals it.</summary>
    private static string[] ApplicablePermissions(ObjectKind kind) => kind switch
    {
        ObjectKind.Procedure => ["ALTER", "CONTROL", "EXECUTE", "REFERENCES", "TAKE OWNERSHIP", "VIEW DEFINITION"],
        ObjectKind.ScalarFunction => ["ALTER", "CONTROL", "EXECUTE", "REFERENCES", "TAKE OWNERSHIP", "VIEW DEFINITION"],
        ObjectKind.Sequence => ["ALTER", "CONTROL", "REFERENCES", "TAKE OWNERSHIP", "UPDATE", "VIEW DEFINITION"],
        ObjectKind.TableFunction => ["ALTER", "CONTROL", "DELETE", "INSERT", "REFERENCES", "SELECT", "TAKE OWNERSHIP", "UPDATE", "VIEW DEFINITION"],
        ObjectKind.Other => ["ALTER", "CONTROL", "DELETE", "EXECUTE", "INSERT", "REFERENCES", "SELECT", "TAKE OWNERSHIP", "UPDATE", "VIEW CHANGE TRACKING", "VIEW DEFINITION"],
        _ => ["ALTER", "CONTROL", "DELETE", "INSERT", "REFERENCES", "SELECT", "TAKE OWNERSHIP", "UNMASK", "UPDATE", "VIEW CHANGE TRACKING", "VIEW DEFINITION"],
    };

    /// <summary>Whether <paramref name="permissionName"/> is one <paramref name="obj"/>'s kind takes.</summary>
    internal static bool AppliesTo(Schemas.SchemaObject obj, string permissionName) =>
        Contains(ApplicablePermissions(KindOf(obj)), permissionName);

    /// <summary>Per kind, the schema- and database-scope permissions whose grant implies one the kind takes.</summary>
    private static readonly (string[] Schema, string[] Database)[] RevealingByKind = BuildRevealing();

    private static (string[] Schema, string[] Database) RevealingPermissions(ObjectKind kind) => RevealingByKind[(int)kind];

    private static (string[] Schema, string[] Database)[] BuildRevealing()
    {
        var result = new (string[], string[])[(int)ObjectKind.Other + 1];
        for (var kind = ObjectKind.Tabular; kind <= ObjectKind.Other; kind++)
        {
            var schema = new HashSet<string>(StringComparer.Ordinal);
            var database = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in ApplicablePermissions(kind))
            {
                foreach (var link in PermissionGraph.Implying("OBJECT", permission))
                {
                    _ = link.Class switch
                    {
                        ClassDatabase => database.Add(link.Name),
                        ClassSchema => schema.Add(link.Name),
                        _ => false,
                    };
                }
            }
            result[(int)kind] = ([.. schema], [.. database]);
        }
        return result;
    }

    private static bool Contains(string[] names, string name) => Array.IndexOf(names, name) >= 0;

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

    /// <summary>One securable-and-permission a stored row can carry that satisfies (or denies) a request.</summary>
    private readonly struct Satisfier(byte securableClass, int majorId, int minorId, string name)
    {
        public readonly byte Class = securableClass;
        public readonly int MajorId = majorId;
        public readonly int MinorId = minorId;
        public readonly string Name = name;
    }

    private static bool HasMatchingRow(Database database, HashSet<int> closure, Satisfiers satisfiers, bool deny)
    {
        foreach (var s in satisfiers)
        {
            foreach (var row in database.Permissions.On(s.Class, s.MajorId))
            {
                var stateMatches = deny ? row.State == PermissionState.Deny : row.State is PermissionState.Grant or PermissionState.GrantWithGrantOption;
                if (stateMatches && Matches(row, s) && closure.Contains(row.GranteePrincipalId))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Whether <paramref name="row"/>, one stored on the satisfier's securable, carries its column and permission.</summary>
    private static bool Matches(DatabasePermission row, Satisfier satisfier) =>
        row.MinorId == satisfier.MinorId && string.Equals(row.DisplayName, satisfier.Name, StringComparison.OrdinalIgnoreCase);

    private static bool Matches(DatabasePermission row, Satisfiers satisfiers)
    {
        foreach (var s in satisfiers)
        {
            if (row.Class == s.Class && row.MajorId == s.MajorId && row.MinorId == s.MinorId
                && string.Equals(row.DisplayName, s.Name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Whether a fixed role in <paramref name="closure"/> grants (or, with <paramref name="deny"/>, denies) a database-scope satisfier.</summary>
    private static bool FixedRolesMatch(HashSet<int> closure, Satisfiers satisfiers, bool deny)
    {
        foreach (var (roleId, permissions) in deny ? FixedRoleDenies : FixedRoleGrants)
        {
            if (!closure.Contains(roleId))
                continue;
            foreach (var s in satisfiers)
            {
                if (s.Class == ClassDatabase && Contains(permissions, s.Name))
                    return true;
            }
        }
        return false;
    }

    private static Satisfiers BuildSatisfiers(Database database, Permission permission, byte securableClass, int majorId, int schemaId, int columnOrdinal) =>
        BuildSatisfiers(database, permission.CanonicalName, securableClass, majorId, schemaId, columnOrdinal);

    /// <summary>
    /// The (class, major_id, minor_id, permission) tuples a row could carry that
    /// would satisfy (or deny) the request — every pair
    /// <see cref="PermissionGraph"/> says implies it, placed on the securable
    /// itself, its schema or the database. An object-scope link carries
    /// <c>minor_id 0</c>, and when <paramref name="columnOrdinal"/> is non-zero
    /// also that column's <c>minor_id</c>, so an object-grain request is never
    /// satisfied by a column-scoped row.
    /// </summary>
    private static Satisfiers BuildSatisfiers(Database database, string permissionName, byte securableClass, int majorId, int schemaId, int columnOrdinal) =>
        new(PermissionGraph.Implying(ClassDescription(database, securableClass, majorId), permissionName), securableClass, majorId, schemaId, columnOrdinal, withDatabase: true);

    /// <summary>
    /// The satisfiers of one request, produced from its graph links as they're
    /// enumerated — every check builds them, so they're never materialized.
    /// </summary>
    private readonly struct Satisfiers(PermissionGraph.Link[] links, byte securableClass, int majorId, int schemaId, int columnOrdinal, bool withDatabase)
    {
        private readonly PermissionGraph.Link[] links = links;
        private readonly byte securableClass = securableClass;
        private readonly int majorId = majorId;
        private readonly int schemaId = schemaId;
        private readonly int columnOrdinal = columnOrdinal;
        private readonly bool withDatabase = withDatabase;

        /// <summary>These satisfiers less the database-scope ones.</summary>
        public Satisfiers WithoutDatabase() => new(this.links, this.securableClass, this.majorId, this.schemaId, this.columnOrdinal, withDatabase: false);

        public Enumerator GetEnumerator() => new(this);

        public struct Enumerator(Satisfiers satisfiers)
        {
            private readonly Satisfiers satisfiers = satisfiers;
            private int next;
            private bool columnPending;

#pragma warning disable SSS001 // foreach binds an enumerator's Current only as a property.
            public Satisfier Current { readonly get; private set; }
#pragma warning restore SSS001

            public bool MoveNext()
            {
                var s = this.satisfiers;
                if (this.columnPending)
                {
                    // An object-scope link's column-scoped twin follows it.
                    this.columnPending = false;
                    this.Current = new(this.Current.Class, this.Current.MajorId, s.columnOrdinal, this.Current.Name);
                    return true;
                }
                while (this.next < s.links.Length)
                {
                    var link = s.links[this.next++];
                    if (!s.withDatabase && link.Class == ClassDatabase)
                        continue;
                    var linkMajor = link.Class == s.securableClass ? s.majorId
                        : link.Class == ClassSchema ? s.schemaId
                        : link.Class == ClassDatabase ? 0
                        : s.majorId;
                    this.Current = new(link.Class, linkMajor, 0, link.Name);
                    this.columnPending = s.columnOrdinal != 0 && link.Class == ClassObject;
                    return true;
                }
                return false;
            }
        }
    }

    /// <summary>The <c>sys.fn_builtin_permissions</c> class a securable's permissions are listed under.</summary>
    private static string ClassDescription(Database database, byte securableClass, int majorId)
    {
        switch (securableClass)
        {
            case ClassDatabase:
                return "DATABASE";
            case ClassObject:
                return "OBJECT";
            case ClassSchema:
                return "SCHEMA";
            case ClassDatabasePrincipal:
                foreach (var (_, principal) in database.Principals)
                {
                    if (principal.PrincipalId == majorId)
                    {
                        return principal.TypeCode switch
                        {
                            "A" => "APPLICATION ROLE",
                            "R" => "ROLE",
                            _ => "USER",
                        };
                    }
                }
                return "USER";
            case ClassType:
                return "TYPE";
            case ClassXmlSchemaCollection:
                return "XML SCHEMA COLLECTION";
            case ClassFulltextCatalog:
                return "FULLTEXT CATALOG";
            default:
                return "DATABASE";
        }
    }
}
