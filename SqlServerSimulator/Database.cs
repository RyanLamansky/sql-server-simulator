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

    public Database(string name)
    {
        this.Name = name;
        this.Schemas[DefaultSchemaName] = new Schema(DefaultSchemaName);
    }

    /// <summary>
    /// Convenience accessor for the <c>dbo</c> schema's tables — the
    /// unqualified-reference fallback path. Equivalent to
    /// <c>Schemas[DefaultSchemaName].HeapTables</c>.
    /// </summary>
    public ConcurrentDictionary<string, HeapTable> DefaultSchemaTables => this.Schemas[DefaultSchemaName].HeapTables;

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
}
