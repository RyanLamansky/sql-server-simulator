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
}
