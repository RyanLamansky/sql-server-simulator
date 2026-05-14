using System.Collections.Concurrent;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

/// <summary>
/// One simulated SQL Server database. A <see cref="Simulation"/> hosts a
/// dictionary of these, keyed by name; each <see cref="SimulatedDbConnection"/>
/// tracks which one is active via <see cref="SimulatedDbConnection.CurrentDatabase"/>.
/// The shape is in place so <c>USE &lt;db&gt;</c> / temp-table / cross-database
/// features can graft on cleanly later — at the moment every
/// <see cref="Simulation"/> ships with exactly one entry named
/// <see cref="Simulation.DefaultDatabaseName"/>.
/// </summary>
internal sealed class Database
{
    /// <summary>The schema name an unqualified table reference resolves through.</summary>
    public const string DefaultSchemaName = "dbo";

    /// <summary>Database name (the key in <see cref="Simulation.Databases"/>).</summary>
    public readonly string Name;

    /// <summary>
    /// Namespaces inside this database, keyed by name. Pre-populated with the
    /// default <c>dbo</c> schema; <c>CREATE SCHEMA &lt;name&gt;</c> adds more.
    /// Schema-qualified table references (<c>SELECT * FROM audit.t</c>) route
    /// through here; unqualified references fall back to
    /// <see cref="DefaultSchemaName"/>.
    /// </summary>
    public readonly ConcurrentDictionary<string, Schema> Schemas = new(Collation.Default);

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

    public Database(string name)
    {
        this.Name = name;
        this.Schemas[DefaultSchemaName] = new Schema(DefaultSchemaName, DboSchemaId);
        this.Schemas["INFORMATION_SCHEMA"] = new Schema("INFORMATION_SCHEMA", InformationSchemaId);
        this.Schemas["sys"] = new Schema("sys", SysSchemaId);
        // Pre-seed the fixed database principals so AW's GRANT … TO public
        // resolves at parse time without a CREATE USER / CREATE ROLE
        // prologue. Principal ids match real SQL Server's convention
        // (probe-confirmed against sys.database_principals on 2026-05-14):
        // public=0, dbo=1, guest=2, INFORMATION_SCHEMA=3, sys=4.
        var seedDate = DateTime.UtcNow;
        this.Principals["public"] = new DatabasePrincipal(0, "public", "R", "DATABASE_ROLE", isFixedRole: true, seedDate);
        this.Principals["dbo"] = new DatabasePrincipal(1, "dbo", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["guest"] = new DatabasePrincipal(2, "guest", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["INFORMATION_SCHEMA"] = new DatabasePrincipal(3, "INFORMATION_SCHEMA", "S", "SQL_USER", isFixedRole: false, seedDate);
        this.Principals["sys"] = new DatabasePrincipal(4, "sys", "S", "SQL_USER", isFixedRole: false, seedDate);
    }

    /// <summary>
    /// Convenience accessor for the <c>dbo</c> schema's tables — the
    /// unqualified-reference fallback path. Equivalent to
    /// <c>Schemas[DefaultSchemaName].HeapTables</c>.
    /// </summary>
    public ConcurrentDictionary<string, HeapTable> DefaultSchemaTables => this.Schemas[DefaultSchemaName].HeapTables;

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

    private long transactionCommitCounter;

    /// <summary>
    /// Allocates the next transaction commit id used by SNAPSHOT and
    /// READ_COMMITTED_SNAPSHOT visibility. Monotonic, database-scoped,
    /// never reused. Each committing transaction reads one stamp; readers
    /// under SI or RCSI compare against this stamp to decide which version
    /// of a row to return. The counter starts at zero so the implicit
    /// "Xmin = 0" for rows that pre-date the first SI/RCSI read is always
    /// visible to any snapshot.
    /// </summary>
    public long AllocateTransactionCommitId() => Interlocked.Increment(ref this.transactionCommitCounter);

    /// <summary>
    /// Reads the current value of the commit-id counter without advancing
    /// it. Used to stamp a snapshot at first read under SNAPSHOT isolation
    /// and at each statement start under READ_COMMITTED_SNAPSHOT. Returning
    /// the latest committed stamp guarantees readers see every transaction
    /// that committed before the snapshot was taken.
    /// </summary>
    public long CurrentTransactionCommitId => Interlocked.Read(ref this.transactionCommitCounter);

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
    /// Active SNAPSHOT-isolation transactions whose snapshot Xid is still
    /// load-bearing — every entry's <see cref="SimulatedDbTransaction.SnapshotXid"/>
    /// is non-null and the tx hasn't reached Commit / Rollback / Dispose yet.
    /// Populated by <see cref="Parser.BatchContext.ResolveSnapshotXidForRead"/>
    /// on first user-table read of an SI tx; drained by the corresponding
    /// finalization path. Read by the version-store GC to compute the
    /// oldest active snapshot Xid (HVs whose <c>Xmax &lt;= oldest_active</c>
    /// are safe to drop), and by <c>sys.dm_tran_active_snapshot_database_transactions</c>
    /// to enumerate per-session SI state.
    /// </summary>
    public readonly ConcurrentDictionary<SimulatedDbTransaction, byte> ActiveSnapshotTxs = new();

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
    public readonly ConcurrentDictionary<string, DdlTrigger> DdlTriggers = new(Collation.Default);

    /// <summary>
    /// Per-database principals (users + roles). Pre-seeded with the fixed
    /// principals (<c>public</c>, <c>dbo</c>, <c>guest</c>, <c>INFORMATION_SCHEMA</c>,
    /// <c>sys</c>); populated by <c>CREATE USER</c> / <c>CREATE ROLE</c>;
    /// drained by <c>DROP USER</c> / <c>DROP ROLE</c>; surfaced by
    /// <c>sys.database_principals</c>. The simulator has no permission
    /// model; this dict exists for catalog-view round-trip and for
    /// resolving <c>GRANT … TO &lt;name&gt;</c> at parse time.
    /// </summary>
    public readonly ConcurrentDictionary<string, DatabasePrincipal> Principals = new(Collation.Default);

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

    private int nextPrincipalId = 4;

    /// <summary>
    /// Allocates the next user principal id. Counter is seeded at 4 so the
    /// first allocation returns 5 — real SQL Server reserves ids 0..4 for
    /// the fixed principals seeded in this database's constructor.
    /// </summary>
    public int AllocatePrincipalId() => Interlocked.Increment(ref this.nextPrincipalId);
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
        && Collation.Default.Equals(this.Name, other.Name);

    public override bool Equals(object? obj) => obj is ExtendedPropertyKey other && this.Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(this.Class, this.MajorId, this.MinorId, Collation.Default.GetHashCode(this.Name));

    public static bool operator ==(ExtendedPropertyKey left, ExtendedPropertyKey right) => left.Equals(right);
    public static bool operator !=(ExtendedPropertyKey left, ExtendedPropertyKey right) => !left.Equals(right);
}
