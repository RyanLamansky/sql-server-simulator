using System.Collections.Concurrent;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One simulated SQL Server database. A <see cref="Simulation"/> hosts a
/// dictionary of these, keyed by name; each <see cref="SimulatedDbConnection"/>
/// tracks which one is active via <see cref="SimulatedDbConnection.CurrentDatabase"/>.
/// <see cref="Simulation.Databases"/> starts empty; the first connection lazily
/// seeds <see cref="Simulation.DefaultDatabaseName"/> if no import has landed
/// a database first. <c>USE &lt;db&gt;</c> / temp-table / cross-database
/// features can graft on cleanly later.
/// </summary>
internal sealed class Database
{
    /// <summary>The schema name an unqualified table reference resolves through.</summary>
    public const string DefaultSchemaName = "dbo";

    /// <summary>
    /// Principal id of the database-owning <c>dbo</c> user (1, matching real
    /// SQL Server). The identity the permission layer treats as the
    /// bypass-everything owner: a session whose effective principal is this id
    /// short-circuits every check. Seeded in the constructor's fixed-principal
    /// block and read by <see cref="SessionSecurityContext"/> and the
    /// principal scalars in place of a scattered literal <c>1</c>.
    /// </summary>
    public const int DboPrincipalId = 1;

    /// <summary>Principal id of the <c>guest</c> user (2) — the fallback identity for a mapped login connecting to <c>master</c>.</summary>
    public const int GuestPrincipalId = 2;

    /// <summary>Principal id of the <c>INFORMATION_SCHEMA</c> catalog principal (3).</summary>
    public const int InformationSchemaPrincipalId = 3;

    /// <summary>Principal id of the <c>sys</c> catalog principal (4).</summary>
    public const int SysPrincipalId = 4;

    /// <summary>Database name (the key in <see cref="Simulation.Databases"/>).</summary>
    public readonly string Name;

    /// <summary>
    /// Stored <c>database_id</c>. System databases carry their fixed reserved
    /// ids (master = 1, tempdb = 2, model = 3, msdb = 4); user databases take
    /// the smallest free id ≥ 5 at registration time
    /// (<see cref="Simulation.RegisterUserDatabase"/>) and keep it for their
    /// lifetime — a <c>DROP DATABASE</c> frees the id for the next create to
    /// reuse (matching real SQL Server's smallest-free allocation). Not set in
    /// the constructor: the value depends on the ids already in use in the
    /// hosting simulation, so <see cref="Simulation"/> assigns it after
    /// construction. Single source of truth for
    /// <see cref="Parser.Expressions.DbId.DatabasesWithIds"/> — read by
    /// <c>DB_ID</c> / <c>DB_NAME</c>, <c>sys.databases.database_id</c>,
    /// <c>OBJECT_NAME</c> routing, and <c>DBCC SHRINKDATABASE</c>.
    /// </summary>
    internal short Id;

    /// <summary>
    /// Namespaces inside this database, keyed by name. Pre-populated with the
    /// default <c>dbo</c> schema; <c>CREATE SCHEMA &lt;name&gt;</c> adds more.
    /// Schema-qualified table references (<c>SELECT * FROM audit.t</c>) route
    /// through here; unqualified references fall back to
    /// <see cref="DefaultSchemaName"/>. Comparer is <see cref="Collation"/>
    /// at the database's construction time; doesn't rebuild if a later
    /// <c>ALTER DATABASE COLLATE</c> shifts <see cref="Collation"/>.
    /// </summary>
    public readonly ConcurrentDictionary<string, Schema> Schemas;

    /// <summary>
    /// Schema-id of the default <c>dbo</c> schema. Matches real SQL Server's
    /// conventional value; surfaces in <c>sys.schemas</c>, <c>sys.tables.schema_id</c>,
    /// etc. Apps that hard-code <c>schema_id = 1</c> for dbo work as expected.
    /// </summary>
    public const int DboSchemaId = 1;

    /// <summary>Conventional schema-id for <c>INFORMATION_SCHEMA</c> (matches real SQL Server).</summary>
    public const int InformationSchemaId = 3;

    /// <summary>Conventional schema-id for <c>sys</c> (matches real SQL Server).</summary>
    public const int SysSchemaId = 4;

    public Database(string name, Collation collation)
    {
        this.Name = name;
        this.Collation = collation;
        this.CollationName = collation.Name;
        this.Schemas = new(collation);
        this.DdlTriggers = new(collation);
        this.Principals = new(collation);
        this.FullTextCatalogs = new(collation);
        this.Filegroups = new(collation) { ["PRIMARY"] = PrimaryFilegroupId };
        this.Schemas[DefaultSchemaName] = new Schema(this, DefaultSchemaName, DboSchemaId);
        this.Schemas["INFORMATION_SCHEMA"] = new Schema(this, "INFORMATION_SCHEMA", InformationSchemaId);
        this.Schemas["sys"] = new Schema(this, "sys", SysSchemaId);
        // Pre-seed the fixed database principals so AW's GRANT … TO public
        // resolves at parse time without a CREATE USER / CREATE ROLE
        // prologue. Principal ids match real SQL Server's convention
        // (probe-confirmed against sys.database_principals on 2026-05-14):
        // public=0, dbo=1, guest=2, INFORMATION_SCHEMA=3, sys=4.
        var seedDate = DateTime.UtcNow;
        this.Principals["public"] = new DatabasePrincipal(0, "public", "R", "DATABASE_ROLE", isFixedRole: true, seedDate);
        this.Principals["dbo"] = new DatabasePrincipal(DboPrincipalId, "dbo", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["guest"] = new DatabasePrincipal(GuestPrincipalId, "guest", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["INFORMATION_SCHEMA"] = new DatabasePrincipal(InformationSchemaPrincipalId, "INFORMATION_SCHEMA", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["sys"] = new DatabasePrincipal(SysPrincipalId, "sys", "S", "SQL_USER", isFixedRole: false, seedDate);
        // The nine fixed database roles, with real SQL Server's principal ids
        // (probe-confirmed 2026-07-21). 16388 is deliberately absent — real
        // skips it. All type R, is_fixed_role, owned by dbo. Membership is
        // tracked in RoleMembers like any role; the permission checker reads
        // the closure and gives db_owner / db_datareader / db_datawriter /
        // db_ddladmin / db_denydatareader / db_denydatawriter their virtual
        // capabilities.
        foreach (var (id, roleName) in FixedDatabaseRoles)
            this.Principals[roleName] = new DatabasePrincipal(id, roleName, "R", "DATABASE_ROLE", isFixedRole: true, seedDate);
    }

    /// <summary>
    /// The nine fixed database roles and their real-SQL-Server principal ids.
    /// Seeded into <see cref="Principals"/> at construction; consulted by the
    /// permission checker's fixed-role capability rules.
    /// </summary>
    public static readonly (int Id, string Name)[] FixedDatabaseRoles =
    [
        (16384, "db_owner"),
        (16385, "db_accessadmin"),
        (16386, "db_securityadmin"),
        (16387, "db_ddladmin"),
        (16389, "db_backupoperator"),
        (16390, "db_datareader"),
        (16391, "db_datawriter"),
        (16392, "db_denydatareader"),
        (16393, "db_denydatawriter"),
    ];

    private int nextSchemaId = 4;

    /// <summary>
    /// Allocates the next user schema id. Counter is seeded so the first
    /// allocation returns 5 (matching real SQL Server's "user schemas start
    /// at 5" convention; ids 1-4 are pre-assigned to dbo / guest /
    /// INFORMATION_SCHEMA / sys, with guest unmodeled in the simulator).
    /// </summary>
    public int AllocateSchemaId() => Interlocked.Increment(ref this.nextSchemaId);

    /// <summary>
    /// Database compatibility level. Freshly-constructed databases default
    /// to the most recent supported level; user code switches via
    /// <c>ALTER DATABASE … SET COMPATIBILITY_LEVEL = N</c>.
    /// </summary>
    public CompatibilityLevel CompatibilityLevel = CompatibilityLevel.Sql170;

    /// <summary>
    /// Database identifier-resolution collation. Drives every catalog dict
    /// comparer in this database (<see cref="Schemas"/>,
    /// <see cref="Schema.HeapTables"/>, <see cref="Schema.Functions"/>, …)
    /// and every <c>BatchContext</c> / <c>Schema</c> identifier-equality
    /// site. Set once at construction; <c>ALTER DATABASE COLLATE</c>
    /// updates this so subsequent identifier compares route through the
    /// new collation, but the construction-time dict comparers don't
    /// rebuild (matches real SQL Server's behavior: existing objects keep
    /// their identifier registration, new ones bind under the new
    /// collation). Seeded at construction from
    /// <see cref="Simulation.ServerCollation"/>.
    /// </summary>
    public Collation Collation;

    /// <summary>
    /// Database-scope <c>COLLATE</c> declaration as a string. Surfaces in
    /// <c>sys.databases.collation_name</c>,
    /// <c>DATABASEPROPERTYEX(name, 'Collation')</c>, and
    /// <c>INFORMATION_SCHEMA.COLUMNS.COLLATION_NAME</c>. Kept in sync with
    /// <see cref="Collation"/>.Name on every <c>ALTER DATABASE COLLATE</c>;
    /// also seeds the per-column default collation for new columns that
    /// don't carry an explicit <c>COLLATE</c> clause. Whitelist of accepted
    /// names lives in <see cref="Collation.IsRecognized"/>; an unrecognized
    /// name raises <see cref="NotSupportedException"/> from
    /// <c>ALTER DATABASE COLLATE</c>.
    /// </summary>
    public string CollationName;

    /// <summary>
    /// Explicit override of the per-database <c>VERBOSE_TRUNCATION_WARNINGS</c>
    /// scoped configuration; <c>null</c> means follow the compatibility-level
    /// default. Set via
    /// <c>ALTER DATABASE SCOPED CONFIGURATION SET VERBOSE_TRUNCATION_WARNINGS = ON|OFF</c>.
    /// </summary>
    public bool? VerboseTruncationWarnings;

    private long rowVersionCounter;

    /// <summary>
    /// Allocates the next <c>rowversion</c> counter value (also surfaced as
    /// <c>@@DBTS</c> in real SQL Server). Database-scoped, monotonic, shared
    /// across every <c>rowversion</c> column in every table — INSERT and
    /// UPDATE on a rowversion-bearing table both advance it. The counter is
    /// the in-memory representation; the 8-byte big-endian wire form
    /// materializes on demand via <see cref="SqlValue.AsBytes"/> /
    /// <see cref="RowVersionSqlType.Encode"/>, never per-row in the hot
    /// path.
    /// </summary>
    public long AllocateRowVersion() => Interlocked.Increment(ref this.rowVersionCounter);

    /// <summary>
    /// <c>ALLOW_SNAPSHOT_ISOLATION</c> per-database setting. Default <c>false</c>;
    /// flipped by <c>ALTER DATABASE … SET ALLOW_SNAPSHOT_ISOLATION { ON | OFF }</c>.
    /// When <c>false</c>, any user-table access by a session whose
    /// <see cref="SimulatedDbConnection.SessionIsolationLevel"/> is
    /// <see cref="System.Data.IsolationLevel.Snapshot"/> raises Msg 3952.
    /// Independent of <see cref="ReadCommittedSnapshot"/> — both can be on or
    /// off in any combination.
    /// </summary>
    public bool AllowSnapshotIsolation;

    /// <summary>
    /// <c>READ_COMMITTED_SNAPSHOT</c> per-database setting. Default
    /// <c>false</c>; flipped by <c>ALTER DATABASE … SET
    /// READ_COMMITTED_SNAPSHOT { ON | OFF }</c>. When <c>true</c>, sessions
    /// at the default <see cref="System.Data.IsolationLevel.ReadCommitted"/>
    /// take a per-statement snapshot instead of acquiring row-S locks for
    /// reads. Writers under RCSI behave identically to vanilla RC (row-X
    /// tx-scoped). The probed real-server requirement that all other
    /// connections close before the flip is relaxed in the simulator — the
    /// flip takes effect immediately.
    /// </summary>
    public bool ReadCommittedSnapshot;

    /// <summary>
    /// <c>RECURSIVE_TRIGGERS</c> per-database setting. Default <c>false</c>;
    /// flipped by <c>ALTER DATABASE … SET RECURSIVE_TRIGGERS { ON | OFF }</c>
    /// and surfaced as <c>sys.databases.is_recursive_triggers_on</c>. When
    /// <c>false</c>, an AFTER trigger whose body's DML would re-fire that same
    /// trigger is skipped (direct recursion); when <c>true</c> the re-fire
    /// happens, bounded only by the 32-level nesting cap. Indirect recursion —
    /// the trigger firing again underneath another table's trigger — happens
    /// either way, and INSTEAD OF triggers never self-recurse regardless.
    /// </summary>
    public bool RecursiveTriggers;

    /// <summary>
    /// <c>TRUSTWORTHY</c> per-database setting. Default <c>false</c> for every
    /// database except <c>msdb</c> (which real ships trustworthy); flipped by
    /// <c>ALTER DATABASE … SET TRUSTWORTHY { ON | OFF }</c> and surfaced as
    /// <c>sys.databases.is_trustworthy_on</c>. When <c>true</c>, a
    /// database-scoped identity established here — an <c>EXECUTE AS USER</c>
    /// frame, a module's <c>WITH EXECUTE AS &lt;user&gt;</c> frame, an activated
    /// application role — may reach another database, resolving the frame's own
    /// login there like any ordinary session; when <c>false</c> every such
    /// reference is Msg 916.
    /// </summary>
    /// <remarks>
    /// Real gates the crossing on an <em>authenticator</em> as well: this
    /// database's owner must hold <c>AUTHENTICATE</c> in the target, which a
    /// sysadmin-owned database satisfies through <c>dbo</c>. Every simulated
    /// database is dbo-owned, so the authenticator always qualifies and the flag
    /// alone decides.
    /// </remarks>
    public bool Trustworthy;

    /// <summary>
    /// <c>DB_CHAINING</c> per-database setting. Default <c>false</c> for a user
    /// database (real ships <c>master</c> / <c>tempdb</c> / <c>msdb</c> on);
    /// flipped by <c>ALTER DATABASE … SET DB_CHAINING { ON | OFF }</c> and
    /// surfaced as <c>sys.databases.is_db_chaining_on</c>. An ownership chain
    /// crosses the database boundary only when <em>both</em> databases have it
    /// on — the module's and the referenced object's — and the two objects share
    /// an owner; otherwise the chain breaks and the caller needs its own grant on
    /// what the module reached.
    /// </summary>
    /// <remarks>
    /// Every simulated object is dbo-owned (there is no schema / database
    /// <c>AUTHORIZATION</c> surface), so the owner-match half of real's rule is
    /// always satisfied and the pair of flags decides. The caller still needs a
    /// user in the target database — chaining lends rights, not access, so a
    /// login with no user there is Msg 916 either way.
    /// </remarks>
    public bool CrossDatabaseChaining;

    /// <summary>
    /// <c>READ_ONLY</c> per-database access mode, flipped by
    /// <c>ALTER DATABASE … SET { READ_ONLY | READ_WRITE }</c> and surfaced as
    /// <c>sys.databases.is_read_only</c> and
    /// <c>DATABASEPROPERTYEX(name, 'Updateability')</c>. While set, every write
    /// to this database raises <strong>Msg 3906</strong> through
    /// <see cref="RejectWriteWhenReadOnly"/>.
    /// </summary>
    /// <remarks>
    /// The flag is checked at the point a write actually happens, which is what
    /// reproduces real's laziness (probe-confirmed against SQL Server 2025,
    /// 2026-08-04): an <c>UPDATE</c> / <c>DELETE</c> matching no row, and an
    /// <c>INSERT … SELECT</c> producing none, complete quietly, while an
    /// <c>INSERT … VALUES</c> and every DDL statement raise. Writes to a table
    /// belonging to no database — a <c>#temp</c> table, a table variable, a
    /// table-valued parameter — are unaffected however the session's own
    /// database is set, matching real's separate <c>tempdb</c>.
    /// </remarks>
    public bool IsReadOnly;

    /// <summary>
    /// The database's recovery model, reported through
    /// <c>sys.databases.recovery_model</c> / <c>recovery_model_desc</c>. Set by
    /// <c>ALTER DATABASE … SET RECOVERY</c> and by a bacpac import carrying the
    /// source's value; drives nothing else, since the simulator has no
    /// transaction log. Seeded per database in the constructor — real ships
    /// <c>master</c> / <c>tempdb</c> / <c>msdb</c> SIMPLE and <c>model</c>
    /// FULL, which every new user database inherits.
    /// </summary>
    public RecoveryModel RecoveryModel = RecoveryModel.Full;

    /// <summary>
    /// This database's Query Store configuration, set by
    /// <c>ALTER DATABASE … SET QUERY_STORE</c> and reported through
    /// <c>sys.database_query_store_options</c> and
    /// <c>sys.databases.is_query_store_on</c>. Retained but inert — no query is
    /// ever captured, so every <c>sys.query_store_*</c> capture view stays
    /// empty whatever the state says. Replaced wholesale rather than mutated
    /// in place, so a partly-parsed options block leaves the old values
    /// standing.
    /// </summary>
    public QueryStoreOptions QueryStore = new();

    /// <summary>
    /// Raises <strong>Msg 3906</strong> (<c>Failed to update database "&lt;n&gt;"
    /// because the database is read-only.</c>) when this database is
    /// <see cref="IsReadOnly"/>. Called from the write seams — the per-row DML
    /// writes and the DDL statements' target resolution — so the error names
    /// the database being written rather than the session's, which is what a
    /// three-part write into a read-only database reports on real.
    /// </summary>
    /// <param name="state">
    /// The error state real reports at the calling site — 1 everywhere but
    /// <c>ALTER TABLE</c>, which reports 12.
    /// </param>
    public void RejectWriteWhenReadOnly(byte state = 1)
    {
        if (this.IsReadOnly)
            throw SimulatedSqlException.DatabaseIsReadOnly(this.Name, state);
    }

    /// <summary>
    /// The full-text subsystem's own read-only refusal — <strong>Msg 7690</strong>
    /// rather than the Msg 3906 every other statement reports, at the state
    /// belonging to the calling statement.
    /// </summary>
    public void RejectFullTextWriteWhenReadOnly(byte state)
    {
        if (this.IsReadOnly)
            throw SimulatedSqlException.FullTextOperationOnReadOnlyDatabase(state);
    }

    private int nextObjectId = 100;

    /// <summary>
    /// Allocates the next per-object identifier. Each user table gets one at
    /// CREATE; the value is stable through INSERT / UPDATE / DELETE / TRUNCATE
    /// (DROP-then-recreate yields a fresh ID, matching real SQL Server —
    /// probe-confirmed 2026-05-11). The counter never reuses a value and
    /// bypasses transaction rollback (matches the identity-counter rule for
    /// INSERT — rolling back doesn't return IDs to the pool). Backs
    /// <c>OBJECT_ID()</c> and the upcoming <c>sys.objects</c> catalog view.
    /// </summary>
    public int AllocateObjectId() => Interlocked.Increment(ref this.nextObjectId);

    private int nextUserTypeId = 256;

    /// <summary>
    /// Allocates the next per-database <c>user_type_id</c> for a user-defined
    /// type. Surfaces in <c>sys.types.user_type_id</c> and
    /// <c>sys.table_types.user_type_id</c>; system types occupy ids 0–255
    /// (matching real SQL Server's convention), so user-defined types start
    /// at 256. The counter is monotonic and never reuses a value — same
    /// invariant as <see cref="AllocateObjectId"/>.
    /// </summary>
    public int AllocateUserTypeId() => Interlocked.Increment(ref this.nextUserTypeId);

    /// <summary>
    /// Per-database extended-properties dictionary. Keyed by
    /// <see cref="ExtendedPropertyKey"/> (<c>class</c> + <c>major_id</c> +
    /// <c>minor_id</c> + <c>name</c>); the value is the user-supplied
    /// <see cref="SqlValue"/> attached to that target. Populated by
    /// <c>sp_addextendedproperty</c>, mutated by
    /// <c>sp_updateextendedproperty</c>, drained by
    /// <c>sp_dropextendedproperty</c>; surfaced by
    /// <c>sys.extended_properties</c> and <c>fn_listextendedproperty</c>.
    /// Probe-confirmed against SQL Server 2025: the catalog view is per-
    /// database (not per-schema), and names are case-insensitive.
    /// </summary>
    public readonly ConcurrentDictionary<ExtendedPropertyKey, SqlValue> ExtendedProperties = new();

    /// <summary>
    /// Per-database DDL triggers. Database-scope DDL triggers are stored
    /// here rather than in any per-schema dict because their parent is
    /// the database itself (<c>sys.triggers.parent_class = 0</c>).
    /// Populated by <c>CREATE TRIGGER … ON DATABASE</c>; drained by
    /// <c>DROP TRIGGER … ON DATABASE</c>; surfaced by <c>sys.triggers</c>
    /// and <c>sys.sql_modules</c>. <strong>Not fired</strong> — see
    /// <see cref="DdlTrigger"/> for the no-enforcement rationale.
    /// </summary>
    public readonly ConcurrentDictionary<string, DdlTrigger> DdlTriggers;

    /// <summary>
    /// Per-database principals (users + roles). Pre-seeded with the fixed
    /// principals (<c>public</c>, <c>dbo</c>, <c>guest</c>, <c>INFORMATION_SCHEMA</c>,
    /// <c>sys</c>); populated by <c>CREATE USER</c> / <c>CREATE ROLE</c>;
    /// drained by <c>DROP USER</c> / <c>DROP ROLE</c>; surfaced by
    /// <c>sys.database_principals</c>. The simulator has no permission
    /// model; this dict exists for catalog-view round-trip and for
    /// resolving <c>GRANT … TO &lt;name&gt;</c> at parse time.
    /// </summary>
    public readonly ConcurrentDictionary<string, DatabasePrincipal> Principals;

    /// <summary>
    /// Per-database permission grants/denies. Populated by
    /// <c>GRANT</c> / <c>DENY</c>; drained by <c>REVOKE</c>; surfaced by
    /// <c>sys.database_permissions</c>. The simulator has no permission
    /// model; this list exists for catalog-view round-trip only.
    /// </summary>
    public readonly List<DatabasePermission> Permissions = [];

    /// <summary>
    /// Role-membership records: each entry is a (role_principal_id,
    /// member_principal_id) pair. Populated by
    /// <c>ALTER ROLE name ADD MEMBER name</c>; drained by
    /// <c>ALTER ROLE name DROP MEMBER name</c>; surfaced by
    /// <c>sys.database_role_members</c>.
    /// </summary>
    public readonly List<(int RoleId, int MemberId)> RoleMembers = [];

    /// <summary>
    /// Application-lock resources (the <c>sp_getapplock</c> family), keyed by
    /// (database-principal id, resource name). Names compare ordinally —
    /// case- and trailing-space-sensitive, probe-confirmed — after the caller
    /// truncates to 255 characters (<c>AppLock.NormalizeResource</c>).
    /// Interned lazily under the dictionary's own lock via
    /// <see cref="GetOrCreateApplicationLock"/>; entries are never removed —
    /// an idle <see cref="LockResource"/> is a few words and the name set is
    /// small in practice.
    /// </summary>
    public readonly Dictionary<(int PrincipalId, string Resource), LockResource> ApplicationLocks = [];

    /// <summary>
    /// Interns the <see cref="LockResource"/> for one application-lock
    /// identity, creating it on first use. Distinct principals produce
    /// distinct resources — a lock held under one principal neither
    /// conflicts with nor is visible to another principal's same-named lock
    /// (probe-confirmed).
    /// </summary>
    public LockResource GetOrCreateApplicationLock(int principalId, string resource)
    {
        lock (this.ApplicationLocks)
        {
            if (!this.ApplicationLocks.TryGetValue((principalId, resource), out var existing))
                this.ApplicationLocks[(principalId, resource)] = existing = new LockResource();
            return existing;
        }
    }

    private int nextPrincipalId = 4;

    /// <summary>
    /// Allocates the next user principal id. Counter is seeded at 4 so the
    /// first allocation returns 5 — real SQL Server reserves ids 0..4 for
    /// the fixed principals seeded in this database's constructor.
    /// </summary>
    public int AllocatePrincipalId() => Interlocked.Increment(ref this.nextPrincipalId);

    /// <summary>
    /// Per-database full-text catalogs. Populated by
    /// <c>CREATE FULLTEXT CATALOG</c>; drained by
    /// <c>DROP FULLTEXT CATALOG</c>; surfaced by
    /// <c>sys.fulltext_catalogs</c>. The simulator has no full-text search
    /// engine; this dict exists for AW model.xml round-trip + catalog-view
    /// visibility — query-time CONTAINS / FREETEXT predicates raise
    /// <see cref="NotSupportedException"/> rather than evaluate.
    /// </summary>
    public readonly ConcurrentDictionary<string, FullTextCatalog> FullTextCatalogs;

    private int nextFullTextCatalogId;

    /// <summary>
    /// Registered CLR assemblies, keyed by name. Populated by
    /// <c>CREATE ASSEMBLY</c>, drained by <c>DROP ASSEMBLY</c>, surfaced by
    /// <c>sys.assemblies</c> / <c>sys.assembly_files</c>. Assemblies are
    /// database-scoped rather than schema-scoped, which is why they live here
    /// rather than on <see cref="Schema"/>.
    /// </summary>
    public readonly ConcurrentDictionary<string, SqlAssembly> Assemblies = new(StringComparer.OrdinalIgnoreCase);

    // Real SQL Server hands user assemblies ids well above the system range
    // (the shipped Microsoft.SqlServer.Types is 1; a first user assembly was
    // observed at 65538). Starting the counter at 65536 keeps user ids in the
    // same band without pretending to reproduce the exact seed.
    private int nextAssemblyId = 65536;

    /// <summary>Allocates the next <c>sys.assemblies.assembly_id</c>.</summary>
    public int AllocateAssemblyId() => Interlocked.Increment(ref this.nextAssemblyId);

    /// <summary>
    /// Allocates the next full-text catalog id. Real SQL Server's
    /// <c>sys.fulltext_catalogs.fulltext_catalog_id</c> uses a separate
    /// numbering space starting at 5 (the first user catalog probe-confirmed
    /// at id 5); the simulator matches by seeding the counter so the first
    /// allocation returns 5.
    /// </summary>
    public int AllocateFullTextCatalogId() => Interlocked.Increment(ref this.nextFullTextCatalogId) + 4;

    /// <summary>The built-in <c>PRIMARY</c> filegroup's <c>data_space_id</c> (1).</summary>
    public const int PrimaryFilegroupId = 1;

    /// <summary>
    /// Per-database filegroups keyed by name (case-insensitive), value =
    /// <c>data_space_id</c>. Seeded with <c>PRIMARY = 1</c> (the built-in
    /// default filegroup every database carries). Additional filegroups
    /// register through the bacpac loader's <c>SqlFilegroup</c> dispatch and
    /// receive sequential ids from 2 in registration order. Surfaced by
    /// <c>sys.filegroups</c> / <c>sys.data_spaces</c> and consumed by
    /// FILEGROUP-scoped extended properties (class 20 = DATASPACE, whose
    /// <c>major_id</c> is the <c>data_space_id</c>). There is no physical file
    /// model — the registry exists for catalog-view visibility + bacpac
    /// round-trip (DacFx re-emits a <c>SqlFilegroup</c> element per non-PRIMARY
    /// row) only; table / index placement isn't tracked (every heap lives on
    /// PRIMARY).
    /// </summary>
    public readonly ConcurrentDictionary<string, int> Filegroups;

    private int nextFilegroupId = PrimaryFilegroupId;

    /// <summary>
    /// Registers a filegroup by name (idempotent), returning its
    /// <c>data_space_id</c>. A name already present keeps its id; a new name
    /// gets the next sequential id from 2 (PRIMARY holds 1).
    /// </summary>
    public int RegisterFilegroup(string name) =>
        this.Filegroups.GetOrAdd(name, _ => Interlocked.Increment(ref this.nextFilegroupId));

    private int nextXmlCollectionId = 65535;

    /// <summary>
    /// Allocates the next XML schema collection id. Real SQL Server's
    /// <c>sys.xml_schema_collections.xml_collection_id</c> uses a high-range
    /// numbering with the first user collection at id 65536 (probe-confirmed
    /// against SQL Server 2025). The counter is seeded at 65535 so the first
    /// allocation returns 65536, matching that convention.
    /// </summary>
    public int AllocateXmlCollectionId() => Interlocked.Increment(ref this.nextXmlCollectionId);
}

/// <summary>
/// Identifies one entry in <see cref="Database.ExtendedProperties"/>.
/// <see cref="Class"/> follows real SQL Server's <c>sys.extended_properties.class</c>:
/// 0 = DATABASE, 1 = OBJECT_OR_COLUMN, 3 = SCHEMA (additional class numbers
/// for PARAMETER, TYPE, INDEX, etc. exist in real SQL Server but aren't
/// modeled in this bundle). <see cref="MajorId"/> identifies the target —
/// schema_id for class 3, object_id for class 1, 0 for class 0
/// (DATABASE-level uses no level args). <see cref="MinorId"/> is 0 for
/// table / view / proc / func targets and the column ordinal (1-based) for
/// column targets. <see cref="Name"/> is the user-supplied property name
/// (e.g. <c>MS_Description</c>) — compared case-insensitively per real
/// SQL Server semantics.
/// </summary>
internal readonly struct ExtendedPropertyKey(byte @class, int majorId, int minorId, string name) : IEquatable<ExtendedPropertyKey>
{
    public readonly byte Class = @class;
    public readonly int MajorId = majorId;
    public readonly int MinorId = minorId;
    public readonly string Name = name;

    public bool Equals(ExtendedPropertyKey other) =>
        this.Class == other.Class
        && this.MajorId == other.MajorId
        && this.MinorId == other.MinorId
        && BuiltInToken.Equals(this.Name, other.Name);

    public override bool Equals(object? obj) => obj is ExtendedPropertyKey other && this.Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(this.Class, this.MajorId, this.MinorId, BuiltInToken.GetHashCode(this.Name));
}
