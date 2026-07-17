using System.Collections.Concurrent;
using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;

namespace SqlServerSimulator;

/// <summary>
/// Simulates a SQL Server instance.
/// </summary>
/// <remarks>
/// Implementation is split across <c>Simulation.*.cs</c> partial-class files
/// by statement family (<c>Create</c>, <c>Insert</c>, <c>Output</c>,
/// <c>Merge</c>, <c>Set</c>, <c>Alter</c>, <c>Dbcc</c>, plus <c>Coerce</c>
/// for the value-coercion helpers shared between INSERT and MERGE). This file
/// holds the public surface (<see cref="CreateDbConnection"/>), the
/// simulation-wide state, and the top-level statement dispatcher.
/// </remarks>
public sealed partial class Simulation
{
    /// <summary>
    /// Creates a new simulated SQL Server instance with no tables or data.
    /// </summary>
    public Simulation()
    {
        RandomNumberGenerator.Fill(this.newSequentialIdAnchor);
        // Every instance ships with the four SQL Server system databases
        // (master = 1, tempdb = 2, model = 3, msdb = 4), present from
        // construction so `USE master`, `master.sys.*` three-part reads,
        // `master.dbo.<proc>` calls, and SSMS's connect-time `has_dbaccess`
        // / msdb catalog probes all resolve without an explicit import.
        // Seeded here under the ctor-time collation (baseline); the
        // ServerCollationName object-initializer setter runs afterward and
        // re-points each system database's collation to the chosen server
        // collation.
        foreach (var (name, _) in SystemDatabaseIds)
            this.Databases.Add(name, new Database(name, this.ServerCollation));
        var msdb = this.Databases[MsdbDatabaseName];
        SeedMsdbPolicyHealthView(msdb);
        SeedMsdbPolicyConfigurationView(msdb);
        SeedMsdbPolicyAutomationFunction(msdb);
    }

    /// <summary>
    /// Creates a simulated database connection.
    /// </summary>
    /// <returns>A new simulated database connection instance.</returns>
    public SimulatedDbConnection CreateDbConnection() => new(this);

    /// <summary>
    /// Seeds <c>msdb.dbo.syspolicy_system_health_state</c> as an empty view so
    /// SSMS's server-level Policy Health feature — which calls
    /// <c>has_dbaccess('msdb')</c> at connect and then
    /// <c>select … from msdb.dbo.syspolicy_system_health_state</c> — renders
    /// cleanly instead of raising a permission error. On the real server this
    /// is a view over the policy-store internals; the simulator ships the same
    /// six-column shape (probe-confirmed 2026-07-14) with a body that yields no
    /// rows. Constructing the <see cref="View"/> directly (rather than running
    /// <c>CREATE VIEW</c> DDL) avoids materializing a connection during
    /// construction — the body re-parses through the querying connection at
    /// read time, and a FROM-less <c>WHERE 1 = 0</c> guarantees zero rows.
    /// </summary>
    private static void SeedMsdbPolicyHealthView(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        var withIdType = NVarcharSqlType.Get(400, msdb.Collation, Coercibility.Implicit);
        var expressionType = NVarcharSqlType.Get(SqlType.MaxLengthSentinel, msdb.Collation, Coercibility.Implicit);
        HeapColumn[] outputColumns =
        [
            new("health_state_id", SqlType.BigInt, maxLength: null, nullable: false),
            new("policy_id", SqlType.Int32, maxLength: null, nullable: false),
            new("last_run_date", SqlType.DateTime, maxLength: null, nullable: false),
            new("target_query_expression_with_id", withIdType, maxLength: 400, nullable: false),
            new("target_query_expression", expressionType, maxLength: SqlType.MaxLengthSentinel, nullable: false),
            new("result", SqlType.Bit, maxLength: null, nullable: false),
        ];
        // Wrap the typed projection in a derived table so the outer
        // WHERE 1 = 0 is a standard filtered SELECT (a FROM-less SELECT whose
        // final projection ends in an alias doesn't route a trailing WHERE
        // through the parser's alias-continue path — only ORDER BY does). The
        // schema surfaced to callers comes from the view's OutputColumns; the
        // body only has to parse and yield zero rows.
        const string bodyText =
            "select health_state_id, policy_id, last_run_date, target_query_expression_with_id, " +
            "target_query_expression, result from (select cast(null as bigint) as health_state_id, " +
            "cast(null as int) as policy_id, cast(null as datetime) as last_run_date, " +
            "cast(null as nvarchar(400)) as target_query_expression_with_id, " +
            "cast(null as nvarchar(max)) as target_query_expression, cast(null as bit) as result) v " +
            "where 1 = 0";
        var view = new View(
            schema,
            "syspolicy_system_health_state",
            msdb.AllocateObjectId(),
            outputColumns,
            bodyText,
            withCheckOption: false,
            isSchemaBound: false,
            createDate: DateTime.UtcNow,
            baseTable: null,
            baseColumnOrdinals: [],
            rejectionReason: ViewUpdatabilityRejection.UnsupportedShape,
            visibilityCheck: null,
            checkOptionCheck: null)
        {
            DefinitionText = $"CREATE VIEW dbo.syspolicy_system_health_state AS {bodyText}",
        };
        schema.Views[view.Name] = view;
    }

    /// <summary>
    /// Seeds <c>msdb.dbo.syspolicy_configuration</c> as a four-row view.
    /// SSMS's Object-Explorer PolicyStore setup reads
    /// <c>(SELECT current_value FROM msdb.dbo.syspolicy_configuration WHERE
    /// name = '…')</c> for <c>Enabled</c> / <c>HistoryRetentionInDays</c> /
    /// <c>LogOnSuccess</c> and casts each to <c>bit</c> / <c>int</c>. On the
    /// real server this is a view whose <c>current_value</c> column is
    /// <c>sql_variant</c> (probe-confirmed 2026-07-14: the three named rows
    /// carry <c>int</c> bases, <c>PurgeHistoryJobGuid</c> a <c>binary</c>
    /// GUID). The simulator doesn't model sql_variant, and a single column
    /// can't hold both an int and a binary GUID, so <c>current_value</c> is
    /// surfaced as <c>nvarchar</c> — the integer rows stay CAST-compatible
    /// with the <c>bit</c> / <c>int</c> targets SSMS applies (the GUID row is
    /// never cast). Values copied verbatim from the reference. Constructed as
    /// a <see cref="View"/> directly (like the health-state seed) so no
    /// connection is materialized at construction; the body re-parses through
    /// the querying connection at read time.
    /// </summary>
    private static void SeedMsdbPolicyConfigurationView(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        var textType = NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit);
        HeapColumn[] outputColumns =
        [
            new("name", textType, maxLength: 128, nullable: false),
            new("current_value", textType, maxLength: 128, nullable: true),
        ];
        const string bodyText =
            "select name, current_value from (values " +
            "(cast(N'Enabled' as nvarchar(128)), cast(N'1' as nvarchar(128))), " +
            "(cast(N'HistoryRetentionInDays' as nvarchar(128)), cast(N'0' as nvarchar(128))), " +
            "(cast(N'LogOnSuccess' as nvarchar(128)), cast(N'0' as nvarchar(128))), " +
            "(cast(N'PurgeHistoryJobGuid' as nvarchar(128)), cast(N'0x46762DA67B564E42A23C1376789E8D8E' as nvarchar(128)))" +
            ") v(name, current_value)";
        var view = new View(
            schema,
            "syspolicy_configuration",
            msdb.AllocateObjectId(),
            outputColumns,
            bodyText,
            withCheckOption: false,
            isSchemaBound: false,
            createDate: DateTime.UtcNow,
            baseTable: null,
            baseColumnOrdinals: [],
            rejectionReason: ViewUpdatabilityRejection.UnsupportedShape,
            visibilityCheck: null,
            checkOptionCheck: null)
        {
            DefinitionText = $"CREATE VIEW dbo.syspolicy_configuration AS {bodyText}",
        };
        schema.Views[view.Name] = view;
    }

    /// <summary>
    /// Seeds <c>msdb.dbo.fn_syspolicy_is_automation_enabled()</c> as a scalar
    /// function returning <c>bit</c> 1. SSMS's Object-Explorer PolicyHealth
    /// query is
    /// <c>case when 1 = msdb.dbo.fn_syspolicy_is_automation_enabled() and
    /// exists (select * from msdb.dbo.syspolicy_system_health_state where …)
    /// then 1 else 0 end</c>; the function must resolve without error (the
    /// three-part call routes to msdb.dbo from any current database). The
    /// return value mirrors the reference (probe-confirmed 2026-07-14: returns
    /// <c>1</c>, consistent with <c>syspolicy_configuration</c>'s
    /// <c>Enabled = 1</c> row). Constructed directly (like the health-state
    /// and configuration seeds) rather than run through <c>CREATE FUNCTION</c>
    /// so no connection is materialized at construction; the body re-parses
    /// per call.
    /// </summary>
    private static void SeedMsdbPolicyAutomationFunction(Database msdb)
    {
        var schema = msdb.Schemas[Database.DefaultSchemaName];
        const string bodyText = "return cast(1 as bit)";
        var function = new ScalarFunction(
            schema,
            "fn_syspolicy_is_automation_enabled",
            msdb.AllocateObjectId(),
            parameters: [],
            returnType: SqlType.Bit,
            returnsNullOnNullInput: false,
            bodyText,
            createDate: DateTime.UtcNow)
        {
            DefinitionText = $"CREATE FUNCTION dbo.fn_syspolicy_is_automation_enabled() RETURNS bit AS BEGIN {bodyText} END",
        };
        schema.Functions[function.Name] = function;
    }

    /// <summary>
    /// Binds <paramref name="target"/> under <paramref name="name"/> so the
    /// <c>sp_addlinkedserver @server = '<paramref name="name"/>'</c>
    /// procedure can activate it as a linked server on this simulation.
    /// Two-step model: this call only establishes the in-process object-
    /// graph link; <c>sp_addlinkedserver</c> is what makes four-part-name
    /// references (<c>linkedserver.db.schema.t</c>) resolve. Re-registering
    /// the same name overwrites the prior binding; an active linked server
    /// continues to point at its original target until
    /// <c>sp_addlinkedserver</c> is called again.
    /// </summary>
    /// <param name="name">Linked-server name. Matched case-insensitively to
    /// <c>@server</c> on the <c>sp_addlinkedserver</c> call and to the
    /// leading segment of a four-part name at FROM resolution.</param>
    /// <param name="target">The remote <see cref="Simulation"/>. May be any
    /// other instance; a simulation may register itself for round-trip
    /// testing.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public void AddRemoteSimulation(string name, Simulation target)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(target);
        this.AvailableRemotes[name] = target;
        // Changing or replacing a remote binding can alter what an active
        // linked-server name resolves to at the next sp_addlinkedserver
        // call; invalidate any cached plan that may have captured the prior
        // resolution.
        BumpSchemaVersion();
    }

    /// <summary>
    /// Bindings established via
    /// <see cref="AddRemoteSimulation(string, Simulation)"/>: name → remote
    /// <see cref="Simulation"/>. A bare binding has no SQL-visible effect;
    /// <c>sp_addlinkedserver</c> reads from this dict to pick which
    /// <see cref="Simulation"/> backs the linked-server activation.
    /// Case-insensitive keys (<see cref="BuiltInToken"/>).
    /// </summary>
    internal readonly ConcurrentDictionary<string, Simulation> AvailableRemotes = new(BuiltInToken.Comparer);

    /// <summary>
    /// Active linked servers: name → <see cref="LinkedServer"/>. Populated
    /// by <c>sp_addlinkedserver</c>; cleared by <c>sp_dropserver</c>.
    /// Four-part-name FROM resolution consults this dict; <c>sys.servers</c>
    /// projects one row per entry plus the local-server row. Case-
    /// insensitive keys (<see cref="BuiltInToken"/>).
    /// </summary>
    internal readonly ConcurrentDictionary<string, LinkedServer> ActiveLinkedServers = new(BuiltInToken.Comparer);

    /// <summary>
    /// The database name woven into error messages that include a fully
    /// qualified table reference (e.g. Msg 515's <c>"&lt;db&gt;.dbo.&lt;t&gt;"</c>,
    /// Msg 547's <c>database "&lt;db&gt;"</c> wording). Also the key of the
    /// single <see cref="Database"/> entry in <see cref="Databases"/> that
    /// every freshly-constructed <see cref="Simulation"/> ships with.
    /// </summary>
    internal const string DefaultDatabaseName = "simulated";

    /// <summary>
    /// The <c>master</c> system database's name. Every <see cref="Simulation"/>
    /// seeds one at construction (<c>database_id</c> 1), so <c>USE master</c>,
    /// three-part <c>master.sys.*</c> reads, and <c>master.dbo.&lt;proc&gt;</c>
    /// calls resolve without an explicit import. Excluded from the
    /// initial-database fallback so a fresh connection still lands on
    /// <see cref="DefaultDatabaseName"/> rather than master.
    /// </summary>
    internal const string MasterDatabaseName = "master";

    /// <summary>The <c>tempdb</c> system database's name (<c>database_id</c> 2).</summary>
    internal const string TempdbDatabaseName = "tempdb";

    /// <summary>The <c>model</c> system database's name (<c>database_id</c> 3).</summary>
    internal const string ModelDatabaseName = "model";

    /// <summary>The <c>msdb</c> system database's name (<c>database_id</c> 4).</summary>
    internal const string MsdbDatabaseName = "msdb";

    /// <summary>
    /// The four SQL Server system databases and their fixed <c>database_id</c>s,
    /// in id order: <c>master</c> = 1, <c>tempdb</c> = 2, <c>model</c> = 3,
    /// <c>msdb</c> = 4. Every <see cref="Simulation"/> seeds all four at
    /// construction. This is the single source of truth for the reserved-id
    /// block; user databases take ids from 5 in name order
    /// (see <c>DatabasesWithIds</c>).
    /// </summary>
    internal static readonly (string Name, short Id)[] SystemDatabaseIds =
    [
        (MasterDatabaseName, 1),
        (TempdbDatabaseName, 2),
        (ModelDatabaseName, 3),
        (MsdbDatabaseName, 4),
    ];

    /// <summary>
    /// Case-insensitive set of the four system-database names. Consulted by
    /// the initial-database fallback (a fresh connection never lands on a
    /// system database) and the user-database id allocation
    /// (<c>DatabasesWithIds</c> filters these out before numbering user
    /// databases from 5). Keyed by <see cref="BuiltInToken.Comparer"/>.
    /// </summary>
    internal static readonly FrozenSet<string> SystemDatabaseNames =
        new[] { MasterDatabaseName, TempdbDatabaseName, ModelDatabaseName, MsdbDatabaseName }
            .ToFrozenSet(BuiltInToken.Comparer);

    /// <summary>
    /// Server-wide default collation name. Used as the seed for every
    /// <see cref="Database"/> created on this simulation — both the lazy
    /// <c>"simulated"</c> seed picked up on first
    /// <see cref="CreateDbConnection"/> and bacpac imports that don't carry
    /// their own collation declaration. Defaults to
    /// <c>SQL_Latin1_General_CP1_CI_AS</c>.
    /// </summary>
    /// <remarks>
    /// Mirrors SQL Server's <c>model.collation</c>: install-time choice,
    /// immutable thereafter (the only way to change it on a real instance
    /// is the <c>sqlservr -m -q</c> rebuild-master dance, and it's blocked
    /// outright on Azure SQL). Hence <see langword="init"/>-only on this
    /// API — set it in an object initializer
    /// (<c>new Simulation { ServerCollationName = "…" }</c>) before the
    /// first <see cref="CreateDbConnection"/> /
    /// <see cref="ImportBacpac(Stream, out Storage.Bacpac.BacpacImportResult, Storage.Bacpac.BacpacImportOptions?)"/>.
    /// Per-database divergence after construction goes through
    /// <c>ALTER DATABASE COLLATE</c>, which only affects the targeted
    /// database. An unrecognized collation name raises
    /// <see cref="ArgumentException"/>.
    /// </remarks>
    public string ServerCollationName
    {
        get => this.ServerCollation.Name;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            this.ServerCollation = Collation.TryGet(value)
                ?? throw new ArgumentException($"Collation '{value}' is not recognized by the simulator.", nameof(value));
            // The ctor seeded the system databases under the baseline collation
            // before this object-initializer setter ran; re-point each so its
            // collation mirrors the chosen server collation (as real SQL
            // Server's system-database collations track the install-time server
            // collation). The construction-time schema dict comparers don't
            // rebuild — matching the documented ALTER DATABASE COLLATE quirk.
            foreach (var (name, _) in SystemDatabaseIds)
            {
                if (this.Databases.TryGetValue(name, out var systemDatabase))
                {
                    systemDatabase.Collation = this.ServerCollation;
                    systemDatabase.CollationName = this.ServerCollation.Name;
                }
            }
        }
    }

    /// <summary>
    /// Resolved <see cref="Collation"/> backing <see cref="ServerCollationName"/>.
    /// Internal accessor used by <see cref="Database"/> seeding paths
    /// (<c>SimulatedDbConnection.ResolveInitialDatabase</c>,
    /// <see cref="ImportBacpac(Stream, out Storage.Bacpac.BacpacImportResult, Storage.Bacpac.BacpacImportOptions?)"/>);
    /// public callers go through the string-typed property to keep
    /// <see cref="Collation"/> off the public API surface.
    /// </summary>
    internal Collation ServerCollation { get; private set; } = Collation.Baseline;

    /// <summary>
    /// Per-database state hosted by this server instance, keyed by name.
    /// Seeded at construction with the four system databases
    /// (<see cref="SystemDatabaseIds"/>: master / tempdb / model / msdb);
    /// <see cref="SimulatedDbConnection"/>'s constructor lazily seeds
    /// <see cref="DefaultDatabaseName"/> on first connection to a Simulation
    /// that has no user database (so the all-T-SQL use case keeps working
    /// without an explicit import / CREATE DATABASE).
    /// <see cref="ImportBacpac(Stream, out Storage.Bacpac.BacpacImportResult, Storage.Bacpac.BacpacImportOptions?)"/>
    /// adds further entries; <c>USE &lt;db&gt;</c> switches a session's
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/> across entries
    /// (Msg 911 on miss). Fresh connections pick the lazy seed when present,
    /// else the alphabetically-first entry (see
    /// <see cref="SimulatedDbConnection"/>'s ResolveInitialDatabase).
    /// </summary>
    internal readonly Dictionary<string, Database> Databases = new(BuiltInToken.Comparer);

    /// <summary>
    /// SQL-authentication server logins created via <c>CREATE LOGIN</c>, keyed
    /// by name. Empty by default, in which case the TDS endpoint
    /// (<see cref="ListenAsync"/>) accepts any credentials; once at least one
    /// login exists, the endpoint enforces LOGIN7 credentials against this
    /// registry (mismatch → Msg 18456 and disconnect). In-process
    /// <see cref="CreateDbConnection"/> sessions never authenticate — that's
    /// how logins are seeded. <c>ALTER LOGIN … WITH PASSWORD</c> replaces the
    /// (immutable) entry wholesale so concurrent endpoint reads see a
    /// consistent hash.
    /// </summary>
    internal readonly ConcurrentDictionary<string, ServerLogin> Logins = new(BuiltInToken.Comparer);

    /// <summary>
    /// Per-Simulation monotonic counter for server-principal ids. Each
    /// <c>CREATE LOGIN</c> claims a fresh id via
    /// <see cref="AllocatePrincipalId"/>; the counter is seeded at 2 so the
    /// first allocation returns 3 — ids 1 and 2 are reserved for the synthetic
    /// <c>sa</c> / <c>public</c> rows <c>sys.server_principals</c> projects.
    /// </summary>
    private int nextPrincipalId = 2;

    /// <summary>
    /// Allocates the next server-principal id for a freshly-created
    /// <see cref="ServerLogin"/>. <c>ALTER LOGIN</c> preserves the existing id
    /// rather than allocating a new one.
    /// </summary>
    internal int AllocatePrincipalId() => Interlocked.Increment(ref this.nextPrincipalId);

    /// <summary>
    /// Stable install-time timestamp used as the <c>create_date</c> /
    /// <c>modify_date</c> of the synthetic fixed <c>sys.server_principals</c>
    /// rows (<c>sa</c> / <c>public</c>). Captured once at construction, exactly
    /// as each <see cref="Database"/> seeds its fixed database principals' dates
    /// from a single <c>DateTime.UtcNow</c>.
    /// </summary>
    internal readonly DateTime SeedDate = DateTime.UtcNow;

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static Dictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Global temp tables (<c>##foo</c>) — instance-wide, visible to every
    /// connection on this <see cref="Simulation"/>. Created by any session via
    /// <c>CREATE TABLE ##foo</c>; the creating connection is stamped on
    /// <see cref="HeapTable.OwnerConnection"/> and used by
    /// <see cref="SimulatedDbConnection.Dispose"/> to auto-drop the entry at
    /// session close. Any session can DROP / TRUNCATE / SELECT / DML a
    /// <c>##foo</c> regardless of ownership — probe-confirmed against SQL
    /// Server 2025 (any session can drop another's global temp).
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025 (pooling disabled) that the
    /// auto-drop fires unconditionally on owner-disconnect — Microsoft Learn's
    /// "dropped when all tasks have stopped referencing" wording is misleading.
    /// A non-owner session mid-statement at the moment of owner-disconnect
    /// observes Msg 208 on its next reference to the table. No reference
    /// counting needed.
    /// </remarks>
    internal readonly ConcurrentDictionary<string, HeapTable> GlobalTempTables = new(BuiltInToken.Comparer);

    /// <summary>
    /// Virtual <c>sys.&lt;view&gt;</c> catalog views (<c>sys.schemas</c>,
    /// <c>sys.tables</c>, <c>sys.objects</c>), keyed by leaf name. Each
    /// projects live <see cref="Database"/> / <see cref="Schema"/> /
    /// <see cref="HeapTable"/> metadata on every read; rows aren't cached.
    /// Materialized once per process via <see cref="BuiltInResources"/>.
    /// </summary>
    internal static Dictionary<string, CatalogView> CatalogViews => BuiltInResources.CatalogViews.Value;

    /// <summary>
    /// Random 12-byte tail (raw bytes [4..15] of the produced GUID) for
    /// <see cref="GenerateNewSequentialId"/>. Filled once at construction —
    /// stands in for SQL Server's "MAC address + boot timestamp" anchor that
    /// distinguishes one server's sequence from another's.
    /// </summary>
    private readonly byte[] newSequentialIdAnchor = new byte[12];

    /// <summary>
    /// Per-Simulation monotonic counter for session ids. Each
    /// <see cref="SimulatedDbConnection"/> claims a fresh SPID on construction
    /// via <see cref="AllocateSpid"/>. Real SQL Server reserves SPIDs 1-50
    /// for system / internal use and starts user sessions at 51; the counter
    /// here is seeded so the first allocation also returns 51, matching
    /// the deadlock-victim message convention.
    /// </summary>
    private int nextSpid = 50;

    /// <summary>
    /// Live connection registry. Each <see cref="SimulatedDbConnection"/>
    /// registers itself at construction and deregisters in
    /// <see cref="SimulatedDbConnection.Dispose"/>. The
    /// <c>sys.dm_tran_locks</c> / <c>sys.dm_os_waiting_tasks</c> DMVs
    /// enumerate this list to surface waiter rows (waiters' state is on
    /// the connection's <see cref="SimulatedDbConnection.WaitingOnResource"/>,
    /// not on the resource itself).
    /// </summary>
    internal readonly HashSet<SimulatedDbConnection> Connections = new(ReferenceEqualityComparer.Instance);

    /// <summary>Registers a connection at construction time.</summary>
    internal void RegisterConnection(SimulatedDbConnection connection)
    {
        lock (this.Connections)
            _ = this.Connections.Add(connection);
    }

    /// <summary>Unregisters a connection at dispose time.</summary>
    internal void UnregisterConnection(SimulatedDbConnection connection)
    {
        lock (this.Connections)
            _ = this.Connections.Remove(connection);
    }

    /// <summary>
    /// Snapshot of currently-registered connections. The caller iterates
    /// the snapshot rather than the live set so concurrent open / dispose
    /// during enumeration is safe.
    /// </summary>
    internal SimulatedDbConnection[] SnapshotConnections()
    {
        lock (this.Connections)
            return [.. this.Connections];
    }

    /// <summary>
    /// Per-Simulation lock coordinator — single gate every
    /// <see cref="LockResource"/> acquisition / release serializes through,
    /// plus the cycle-detection walker. One instance per simulation;
    /// SystemHeapTables (shared across simulations) bypass via the
    /// resolver's no-Sch-S branch, so cross-simulation locking isn't a
    /// concern.
    /// </summary>
    internal readonly LockManager LockManager = new();

    /// <summary>
    /// Monotonic counter bumped by every successful CREATE / DROP / ALTER
    /// statement, plus <c>ImportBacpac</c>. Reads via <c>Volatile.Read</c>
    /// for cache-version comparisons; writes via
    /// <see cref="Interlocked.Increment(ref long)"/>. The
    /// <see cref="planCache"/> stamps each stored <see cref="Selection"/>
    /// with the version it was parsed under, and a lookup whose live version
    /// differs treats the entry as stale and re-parses.
    /// </summary>
    internal long SchemaVersion;

    /// <summary>
    /// Per-instance parse-result cache for single-SELECT command batches
    /// (the EF-query shape). Keyed by (<see cref="DbCommand.CommandText"/>,
    /// current database name, parameter type signature); the entry records
    /// the parsed <see cref="Selection"/> and the <see cref="SchemaVersion"/>
    /// it was parsed under, so a DDL bump invalidates without per-entry walk.
    /// Capped at <see cref="PlanCacheCapacity"/>: once full, new entries are
    /// silently dropped (the working set for a stable EF app is dozens of
    /// queries, so the cap is mostly defensive). Bypassed for batches that
    /// reference session-scoped tables (<c>#temp</c>, <c>##gtemp</c>,
    /// <c>@t</c>), contain DDL, or aren't a single top-level SELECT — the
    /// candidate is captured by the dispatch loop and dropped on any
    /// disqualifying condition.
    /// </summary>
    private readonly ConcurrentDictionary<PlanCacheKey, PlanCacheEntry> planCache = new();

    private const int PlanCacheCapacity = 1024;

    /// <summary>Test-observable: total hits on the plan cache since
    /// construction. Incremented after a key match against a non-stale
    /// entry, just before <c>ReplayCachedSelection</c> is called.</summary>
    internal long PlanCacheHits;

    /// <summary>Test-observable: total misses on the plan cache where an
    /// eligible command text fell through to the full parse path (either no
    /// key, no entry, or a stale-version entry). Incremented exactly once
    /// per eligible call that doesn't hit.</summary>
    internal long PlanCacheMisses;

    /// <summary>Test-observable: live count of entries in the plan cache.</summary>
    internal int PlanCacheCount => this.planCache.Count;

    /// <summary>Cache key for <see cref="planCache"/>. The schema-version
    /// is intentionally NOT part of the key — it sits on the entry so a stale
    /// lookup overwrites in place rather than orphaning entries on every DDL.
    /// The session's QUOTED_IDENTIFIER setting IS part of the key: it changes
    /// how <c>"…"</c> tokenizes, so the same text parses to different plans
    /// under each setting (mirroring real SQL Server, whose plan-cache keys
    /// fold in the parse-time SET options).</summary>
    private readonly record struct PlanCacheKey(string CommandText, string DatabaseName, string ParameterSignature, bool QuotedIdentifiers);

    /// <summary>Cache entry: the parsed <see cref="Selection"/> plus the
    /// <see cref="SchemaVersion"/> active when it was parsed.</summary>
    private sealed class PlanCacheEntry(Selection plan, long schemaVersionAtParse)
    {
        public readonly Selection Plan = plan;
        public readonly long SchemaVersionAtParse = schemaVersionAtParse;
    }

    /// <summary>
    /// Increments <see cref="SchemaVersion"/>, signaling that any cached
    /// <see cref="Selection"/> parsed under the prior version is potentially
    /// stale. Called by the Create / Drop / Alter dispatch arm and by
    /// <c>ImportBacpac</c>.
    /// </summary>
    internal void BumpSchemaVersion() => Interlocked.Increment(ref this.SchemaVersion);

    /// <summary>
    /// Allocates the next session id (SPID) for a freshly-constructed
    /// <see cref="SimulatedDbConnection"/>. Used to fill the <c>Process ID
    /// &lt;N&gt;</c> slot in Msg 1205 (deadlock victim) and to identify
    /// lock holders / waiters in any future <c>sys.dm_tran_locks</c> /
    /// <c>sys.dm_exec_sessions</c> projection.
    /// </summary>
    internal int AllocateSpid() => Interlocked.Increment(ref this.nextSpid);

    /// <summary>
    /// Monotonic counter for <see cref="GenerateNewSequentialId"/>; each call
    /// reserves the next value via <see cref="Interlocked.Increment(ref long)"/>
    /// and packs it into raw bytes [0..3] of the produced GUID.
    /// </summary>
    private long newSequentialIdCounter;

    /// <summary>
    /// Produces the next <c>NEWSEQUENTIALID()</c> value: a
    /// <see cref="Guid"/> whose comparison under SQL Server's
    /// <c>uniqueidentifier</c> ordering rules is strictly greater than
    /// every value previously returned for this <see cref="Simulation"/>.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>uniqueidentifier</c> compares group-by-group from
    /// most significant to least: bytes <c>[10..15]</c>, then <c>[8..9]</c>,
    /// then <c>[6..7]</c>, then <c>[4..5]</c>, then <c>[0..3]</c>; within
    /// each group the lower-indexed byte is more significant. To get
    /// strict monotonicity the simulator fixes bytes <c>[4..15]</c> for the
    /// lifetime of the simulation and packs an incrementing 64-bit counter
    /// into bytes <c>[0..3]</c> big-endian (raw byte 0 = MSB, raw byte 3 =
    /// LSB). Each increment lands in the comparison-LSB position
    /// (raw byte 3) and carries propagate left toward higher comparison
    /// significance — matching real SQL Server's per-call delta.
    /// Monotonicity holds for the first 2^32 calls; beyond that the counter
    /// wraps and the cycle restarts. The GUID is constructed via
    /// <see cref="Guid(ReadOnlySpan{byte}, bool)"/> with <c>bigEndian</c>
    /// true, so its display order matches the raw byte order assembled here.
    /// </remarks>
    internal Guid GenerateNewSequentialId()
    {
        var counter = (uint)Interlocked.Increment(ref this.newSequentialIdCounter);
        Span<byte> bytes = stackalloc byte[16];
        bytes[0] = (byte)(counter >> 24);
        bytes[1] = (byte)(counter >> 16);
        bytes[2] = (byte)(counter >> 8);
        bytes[3] = (byte)counter;
        this.newSequentialIdAnchor.CopyTo(bytes[4..]);
        return new Guid(bytes, bigEndian: true);
    }

    /// <summary>
    /// Top-level statement dispatch. Iterates through the command's tokens,
    /// dispatching each statement to its dedicated parser by leading keyword.
    /// Yields outcomes for data-producing statements (SELECT, INSERT) and runs
    /// schema/control statements for side-effect only (CREATE, SET, ALTER,
    /// DBCC). The shape mirrors <c>Expression.ResolveBuiltIn</c>: a single
    /// switch with one case per keyword, each delegating to a focused method.
    /// </summary>
    /// <remarks>
    /// Statement separators (<c>;</c>) are <i>optional</i> between most
    /// statements, mirroring real SQL Server's relaxed batch grammar. Two
    /// exceptions: a CTE (<c>WITH</c>) directly following another statement
    /// raises Msg 319, and a <c>MERGE</c> not terminated by <c>;</c> raises
    /// Msg 10713. The loop drains explicit separators at the top of each
    /// iteration; statement parsers are expected to leave <see cref="ParserContext.Token"/>
    /// at their first un-consumed token (the lookahead-position contract on
    /// <see cref="ParserContext"/>). For parsers that historically left
    /// <c>Token</c> on the last token they consumed (DBCC's closing <c>)</c>,
    /// SET-session-state's <c>ON</c>/<c>OFF</c>, etc.) the bottom of the loop
    /// normalizes by advancing one token when <c>Token</c> isn't already at a
    /// recognizable statement boundary.
    /// </remarks>
    /// <param name="command">The command whose <see cref="DbCommand.CommandText"/> is dispatched.</param>
    /// <param name="continueOnError">
    /// Marks a top-level batch. When <see langword="true"/> (the default —
    /// both the in-process ADO surface and the TDS wire pass it), a
    /// statement-terminating error outside any TRY frame is emitted as a
    /// <see cref="SimulatedErrorOutcome"/> and the batch continues to the next
    /// statement (real SQL Server's default severity model), so both front
    /// doors render one shared outcome stream. Child batches (proc / trigger /
    /// UDF / dynamic-SQL bodies) construct their own <see cref="BatchContext"/>
    /// and leave this <see langword="false"/>, so their errors throw and
    /// surface at the invoking statement. Batch-aborting errors (deadlock
    /// class 13, class ≥ 17, an uncaught THROW, a bind-class name-resolution
    /// miss) end the batch regardless.
    /// </param>
    internal IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommand(SimulatedDbCommand command, bool continueOnError = true)
    {
        // CommandType.StoredProcedure: CommandText is the procedure name and
        // Parameters maps by name to the proc's declared parameters. Bypass
        // the SQL-text parser and route directly to InvokeProcedure with the
        // parameter collection translated into ProcArguments. The procedure-
        // call entrypoint that hand-rolled SqlClient code uses.
        if (command.CommandType == CommandType.StoredProcedure)
        {
            foreach (var outcome in InvokeFromCommandTypeStoredProcedure(command))
                yield return outcome;
            yield break;
        }

        // Plan-cache fast path: a single-SELECT batch parsed once under the
        // current schema version replays without tokenizing or re-parsing.
        // Eligibility is gated by TryBuildPlanCacheKey (non-empty text, live
        // connection) and the entry's recorded schema version must match the
        // current one — a stale entry falls through to the standard dispatch
        // and overwrites itself on the way out (the SELECT arm of
        // DispatchOneStatementCore does the inline promotion).
        var cacheKey = TryBuildPlanCacheKey(command);
        var schemaVersionAtStart = Volatile.Read(ref this.SchemaVersion);
        if (cacheKey is { } key
            && this.planCache.TryGetValue(key, out var entry)
            && entry.SchemaVersionAtParse == schemaVersionAtStart)
        {
            _ = Interlocked.Increment(ref this.PlanCacheHits);
            foreach (var outcome in ReplayCachedSelection(command, entry.Plan))
                yield return outcome;
            yield break;
        }
        if (cacheKey is not null)
            _ = Interlocked.Increment(ref this.PlanCacheMisses);

        var batch = new BatchContext(command) { ContinueOnError = continueOnError };
        // Stash the prepared cache-key components on the batch so the SELECT
        // arm can promote inline (the iterator's post-foreach code is
        // unreachable when the consumer disposes the reader without draining
        // — the common path for a one-result-set ExecuteReader call).
        if (cacheKey is { } prepared)
        {
            batch.PlanCacheCommandText = prepared.CommandText;
            batch.PlanCacheDatabaseName = prepared.DatabaseName;
            batch.PlanCacheParameterSignature = prepared.ParameterSignature;
            batch.PlanCacheSchemaVersion = schemaVersionAtStart;
        }
        try
        {
            var context = batch.Parser;
            context.MoveNextOptional();
            foreach (var outcome in DispatchStatementsUntil(batch, endKeyword: null))
                yield return outcome;
            WriteBackOutputParameters(batch);
        }
        finally
        {
            // The flush has to run even when the consumer disposes the reader
            // before fully draining the iterator (ExecuteScalar reads one row
            // and disposes) — otherwise PRINT / sev-≤10 RAISERROR output that
            // fired before the first SELECT silently vanishes.
            batch.FlushPrintMessages();
        }
    }

    /// <summary>
    /// Promotes a freshly-parsed top-level <see cref="Selection"/> into the
    /// per-instance plan cache when the batch context's stashed key
    /// components are set and the live <see cref="SchemaVersion"/> still
    /// matches the version captured at batch start. Called from the SELECT
    /// arm of <see cref="DispatchOneStatementCore"/> AFTER row materialization
    /// but BEFORE the iterator yields the outcome — so the entry is in the
    /// cache by the time the consumer sees the first row, even if the
    /// consumer disposes the reader without draining the rest of the
    /// iterator. The caller is responsible for the upstream gates (block
    /// depth, first statement, no session-scoped references, parser at EOB).
    /// </summary>
    internal void TryPromoteSelectionToPlanCache(BatchContext batch, Selection selection)
    {
        if (batch.PlanCacheCommandText is not { } text) return;
        if (batch.PlanCacheDatabaseName is not { } dbName) return;
        if (batch.PlanCacheParameterSignature is not { } paramSig) return;
        if (Volatile.Read(ref this.SchemaVersion) != batch.PlanCacheSchemaVersion) return;
        // A cacheable batch is a single SELECT (no SET can precede it), so the
        // connection's live setting still equals the value at parse.
        var key = new PlanCacheKey(text, dbName, paramSig, batch.Connection.QuotedIdentifiers);
        // Refresh-in-place semantics: when a DDL has invalidated the prior
        // entry under this key, the indexer overwrites without growing the
        // dictionary. The capacity cap therefore only gates fresh keys, not
        // re-cached versions of an already-tracked one.
        if (this.planCache.ContainsKey(key) || this.planCache.Count < PlanCacheCapacity)
            this.planCache[key] = new PlanCacheEntry(selection, batch.PlanCacheSchemaVersion);
    }

    /// <summary>
    /// Builds the plan-cache key for a command, or returns <see langword="null"/>
    /// when caching can't apply (no text, no connection, no current database).
    /// The parameter signature folds in each parameter's name, declared
    /// <see cref="DbParameter.DbType"/>, declared
    /// <see cref="DbParameter.Size"/> / <see cref="DbParameter.Precision"/> /
    /// <see cref="DbParameter.Scale"/> in <see cref="DbCommand.Parameters"/>
    /// declaration order — variations in any of those alter parse-time type
    /// inference (e.g. result column types when the SELECT projects a
    /// parameter) and so demand a separate cached plan.
    /// </summary>
    private static PlanCacheKey? TryBuildPlanCacheKey(SimulatedDbCommand command)
        => string.IsNullOrEmpty(command.CommandText)
            ? null
            : command.Connection is { CurrentDatabase: { } currentDb } connection
                && BuildPlanCacheParameterSignature(command) is { } sig
                    ? new PlanCacheKey(command.CommandText, currentDb.Name, sig, connection.QuotedIdentifiers)
                    : null;

    private static string? BuildPlanCacheParameterSignature(SimulatedDbCommand command)
    {
        var parameters = command.Parameters;
        if (parameters.Count == 0)
            return "";
        var sb = new System.Text.StringBuilder();
        foreach (SimulatedDbParameter p in parameters)
        {
            if (sb.Length > 0)
                _ = sb.Append('|');
            // DbType is a stable shorthand that already covers the type-
            // inference dimension parser code reads (string vs numeric vs
            // temporal); precision / scale / size catch the
            // decimal(p,s) / varchar(n) variants that bind through the same
            // DbType. ParameterName is included because the parser resolves
            // VariableReference by name. The getter raises ArgumentException
            // for an unmapped CLR Value (the TVP IDataReader binding path is
            // the live case) — returning null bypasses the cache for this
            // command, which is the right behavior anyway since structured
            // parameters carry session-scoped data the cache doesn't model.
            DbType dbType;
            try
            {
                dbType = p.DbType;
            }
            catch (ArgumentException)
            {
                return null;
            }
            _ = sb.Append(p.ParameterName).Append(':').Append((int)dbType).Append(':')
                .Append(p.Size).Append(':').Append((int)p.Precision).Append(':').Append((int)p.Scale);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Replays a cached <see cref="Selection"/> against a fresh
    /// <see cref="BatchContext"/> for the incoming command, mirroring the
    /// SELECT arm of <see cref="DispatchOneStatementCore"/> for outcome
    /// shape (result-set vs assignment-only NonQuery) and for
    /// <see cref="SimulatedDbConnection.LastStatementRowCount"/>
    /// maintenance. Bypasses tokenization and parsing entirely.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ReplayCachedSelection(SimulatedDbCommand command, Selection selection)
    {
        var batch = new BatchContext(command);
        try
        {
            // Replay bypasses the dispatch loop, so stamp the per-statement
            // frame the way the loop's top-of-iteration would: without this a
            // replayed GETDATE() reads default(DateTime) rather than now.
            // (StatementScopedValues starts null on a fresh frame — no clear
            // needed.) StartLine mirrors the single-statement dispatch value
            // for ERROR_LINE parity.
            batch.CurrentStatement.UtcNow = DateTime.UtcNow;
            batch.CurrentStatement.StartLine = 1;
            var connection = batch.Connection;
            var rows = selection.Execute(batch).RowBytes.ToList();
            connection.LastStatementRowCount = rows.Count;
            yield return selection.IsAssignmentOnly
                ? new SimulatedNonQuery(rows.Count)
                : new SimulatedSqlResultSet(selection.Schema, selection.ColumnNames, rows) { ColumnNullability = selection.ColumnNullability };
            WriteBackOutputParameters(batch);
        }
        finally
        {
            batch.FlushPrintMessages();
        }
    }

    /// <summary>
    /// Procedure-call entrypoint for <see cref="CommandType.StoredProcedure"/>:
    /// resolves <see cref="DbCommand.CommandText"/> as a procedure name,
    /// binds each <see cref="DbCommand.Parameters"/> entry to a declared
    /// parameter by name (or by direction for
    /// <see cref="ParameterDirection.ReturnValue"/>), and invokes
    /// the procedure. Output / InputOutput parameter values write back to
    /// <c>DbParameter.Value</c> at exit (mirroring SqlClient); the ReturnValue
    /// parameter captures the procedure's <c>RETURN</c> code (default 0).
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeFromCommandTypeStoredProcedure(SimulatedDbCommand command)
    {
        // Build a transient outer batch to host the call. This batch's
        // variable dict isn't used for parameter binding (procs read from
        // their child batch's own variable dict, seeded from boundValues);
        // but it's the home for the temporary writeback slots.
        var batch = new BatchContext(command);
        var context = batch.Parser;
        context.MoveNextOptional();
        if (context.Token is not Name)
            throw SimulatedSqlException.CouldNotFindStoredProcedure(command.CommandText);
        var procName = BatchContext.ParseObjectName(context);

        // System procedures (xp_msver, sp_getapplock, …) aren't in the user
        // procedure namespace TryResolveProcedure searches — their dispatch
        // lives in ParseExec's system-proc switch, which consumes arguments by
        // parsing EXEC text. DacFx invokes xp_msver by name over TDS RPC, so
        // route a name-form system-proc call through a synthesized top-level
        // EXEC whose arguments are the RPC parameters literalized positionally.
        // Positional (not named) synthesis is required because RPC callers may
        // repeat a parameter name — DacFx passes five @optname parameters to
        // xp_msver — which named-argument synthesis would reject as duplicates.
        if (ResolveSystemProcedureName(batch.CurrentDatabase.Collation, procName.Leaf) is { } systemProcName)
        {
            foreach (var outcome in InvokeSystemProcedureFromRpc(batch.Connection, systemProcName, command.Parameters))
                yield return outcome;
            yield break;
        }

        if (!batch.TryResolveProcedure(procName, out var procedure))
            throw SimulatedSqlException.CouldNotFindStoredProcedure(procName.ToString());

        // Translate each non-ReturnValue parameter into a ProcArgument. The
        // ReturnValue-direction parameter (at most one) gets pulled out and
        // its writeback happens after the call completes.
        var arguments = new List<ProcArgument>();
        var returnValueWriteback = (DbParameter?)null;
        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Direction is ParameterDirection.ReturnValue)
            {
                returnValueWriteback = parameter;
                continue;
            }

            var pname = parameter.ParameterName.StartsWith('@') ? parameter.ParameterName[1..] : parameter.ParameterName;
            var dbType = SqlType.GetByDbType(parameter.DbType);
            var value = parameter.Value is null or DBNull
                ? SqlValue.Null(dbType)
                : dbType.ConvertParameter(parameter.Value);
            // For OUTPUT-direction parameters we need a live VariableSlot in
            // the outer batch so InvokeProcedure can write back through it.
            // The slot's DeclaredType drives the coercion on writeback.
            VariableSlot? outputSlot = null;
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                outputSlot = new VariableSlot(dbType, declaredMaxLength: null, value, parameter);
                batch.Variables[pname] = outputSlot;
            }
            arguments.Add(new ProcArgument(pname, isDefault: false, value, outputSlot));
        }

        // ReturnValue slot: lives in the outer batch's Variables under an
        // internal name so the InvokeProcedure writeback path can target it.
        // The dot prefix means user @-variables can't collide.
        string? returnCodeVarName = null;
        if (returnValueWriteback is not null)
        {
            returnCodeVarName = ".rc";
            batch.Variables[returnCodeVarName] = new VariableSlot(SqlType.Int32, declaredMaxLength: null, SqlValue.FromInt32(0), returnValueWriteback);
        }

        foreach (var outcome in InvokeProcedure(batch, procedure, arguments, returnCodeVarName))
            yield return outcome;

        // Output param writeback: the per-argument OutputSlot.Value was
        // updated by InvokeProcedure. Copy back to each DbParameter.Value.
        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                var pname = parameter.ParameterName.StartsWith('@') ? parameter.ParameterName[1..] : parameter.ParameterName;
                if (batch.Variables.TryGetValue(pname, out var slot))
                    parameter.Value = slot.Value.IsNull ? DBNull.Value : slot.Value.ToObject();
            }
        }

        // ReturnValue writeback: the .rc slot was written by InvokeProcedure.
        if (returnValueWriteback is not null && batch.Variables.TryGetValue(".rc", out var rcSlot))
            returnValueWriteback.Value = rcSlot.Value.IsNull ? DBNull.Value : rcSlot.Value.ToObject();
    }

    /// <summary>
    /// Dispatches a name-form RPC call to a modeled system procedure by
    /// synthesizing an equivalent top-level <c>EXEC &lt;name&gt; &lt;args&gt;</c>
    /// batch, then running it through the standard statement dispatch so the
    /// call reuses <see cref="ParseExec"/>'s system-proc switch. Each
    /// non-<see cref="ParameterDirection.ReturnValue"/> parameter is literalized
    /// positionally (see <see cref="LiteralizeRpcArgument"/>); output/return
    /// writeback isn't wired for system procs (the modeled ones — xp_msver and
    /// friends — return result sets, not OUTPUT values). Result sets stream back
    /// through the yielded outcomes.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> InvokeSystemProcedureFromRpc(
        SimulatedDbConnection connection,
        string canonicalName,
        DbParameterCollection parameters)
    {
        var sql = new System.Text.StringBuilder("EXEC ").Append(canonicalName);
        var first = true;
        foreach (DbParameter parameter in parameters)
        {
            if (parameter.Direction is ParameterDirection.ReturnValue)
                continue;
            _ = sql.Append(first ? " " : ", ").Append(LiteralizeRpcArgument(parameter));
            first = false;
        }

        using var childCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // synthesized from a modeled system-proc name + literalized parameters, not free-form input
        childCommand.CommandText = sql.ToString();
#pragma warning restore CA2100
        var childBatch = new BatchContext(childCommand);
        var parser = childBatch.Parser;
        parser.MoveNextOptional();
        foreach (var outcome in DispatchStatementsUntil(childBatch, endKeyword: null))
            yield return outcome;
    }

    /// <summary>
    /// Renders one RPC parameter value as a T-SQL literal for the synthesized
    /// system-proc EXEC: NULL as <c>NULL</c>, string-family values as an
    /// <c>N'…'</c> literal (doubling embedded quotes), exact / approximate
    /// numerics as a bare number, and any other type as a quoted string form.
    /// The modeled system procs read only string / integer arguments, so the
    /// numeric and string paths cover every realistic call.
    /// </summary>
    private static string LiteralizeRpcArgument(DbParameter parameter)
    {
        var declaredType = SqlType.GetByDbType(parameter.DbType);
        var value = parameter.Value is null or DBNull
            ? SqlValue.Null(declaredType)
            : declaredType.ConvertParameter(parameter.Value);
        return value.IsNull
            ? "NULL"
            : value.Type.Category switch
            {
                SqlTypeCategory.Integer or SqlTypeCategory.Decimal or SqlTypeCategory.Money or SqlTypeCategory.Approximate =>
                    value.CoerceTo(SqlType.NVarchar).AsString,
                _ => $"N'{value.CoerceTo(SqlType.NVarchar).AsString.Replace("'", "''", StringComparison.Ordinal)}'",
            };
    }

    /// <summary>
    /// Drives the per-statement dispatch loop until either end-of-batch
    /// (when <paramref name="endKeyword"/> is null — top-level call from
    /// <see cref="CreateResultSetsForCommand"/>) or the matching keyword
    /// (when <paramref name="endKeyword"/> is <c>END</c> — block-scoped
    /// call from <see cref="ParseBeginBlock"/>). Handles statement-separator
    /// (<c>;</c>) draining and the CTE-must-be-separated rule
    /// (<c>requireSemicolonBeforeCte</c>); the body of each statement is
    /// dispatched by <see cref="DispatchOneStatement"/>.
    /// </summary>
    internal IEnumerable<SimulatedStatementOutcome> DispatchStatementsUntil(BatchContext batch, Keyword? endKeyword)
    {
        var context = batch.Parser;
        var requireSemicolonBeforeCte = false;
        // BEGIN...END block dispatch (endKeyword=End) bumps BlockDepth so the
        // must-be-first-statement check on CREATE/ALTER
        // PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA rejects them inside
        // the block. Top-level (endKeyword=null) doesn't bump — that's the
        // case where the first statement may legitimately be CREATE PROC.
        var nestedBlock = endKeyword is not null;
        if (nestedBlock)
            batch.BlockDepth++;
        try
        {
            while (context.Token is not null)
            {
                // Early-exit on RETURN: stop dispatching once the batch has been
                // signaled to exit. Any remaining statements (including the END
                // terminator of an enclosing block) are abandoned — the caller
                // handles cursor state. Checked here at the top of every iteration
                // so RETURN inside a block exits the block dispatcher promptly
                // (the block's "expect END" check has a matching short-circuit).
                if (batch.ReturnSignaled)
                    yield break;

                // A batch-aborting name-resolution error (Msg 208 and kin)
                // emitted under continueOnError stops the whole batch — real
                // SQL Server does not run the statements after it. Breaking
                // here rather than resuming at the next token is what kills the
                // abandoned-mid-parse Msg 319 / 102 cascade.
                if (batch.BatchAborted)
                    yield break;

                if (endKeyword is Keyword end && context.Token is ReservedKeyword rk && rk.Keyword == end)
                    yield break;

                if (context.Token is Operator { Character: ';' })
                {
                    requireSemicolonBeforeCte = false;
                    context.MoveNextOptional();
                    continue;
                }

                var statementStartIndex = context.Token.StartIndex;
                foreach (var outcome in DispatchOneStatement(batch, requireSemicolonBeforeCte))
                    yield return outcome;
                requireSemicolonBeforeCte = true;
                batch.HasDispatchedStatement = true;

                // Non-progress guard: a statement dispatch that consumed zero
                // tokens would re-dispatch the same position forever. Normal
                // parses always consume at least the leading token, but the
                // error-recovery scans stop at the next statement boundary —
                // and when the failing token itself IS a boundary keyword
                // (e.g. an orphaned ELSE after deferred-name recovery
                // abandoned its IF mid-parse), the scan advances nothing.
                // Discovered via SSMS's Query Store probe batch, where the
                // wire path's continue-on-error turned this into an infinite
                // error stream that exhausted host memory.
                if (context.Token is { } afterDispatch && afterDispatch.StartIndex == statementStartIndex)
                    context.MoveNextOptional();
            }
        }
        finally
        {
            if (nestedBlock)
                batch.BlockDepth--;
        }
    }

    /// <summary>
    /// Dispatches a single statement at <see cref="ParserContext.Token"/>'s
    /// current position. Handles the optional CTE prefix (<c>WITH</c>),
    /// runs the per-statement frame setup (<see cref="StatementContext.UtcNow"/>),
    /// then routes by leading keyword to the matching parser. Yields zero
    /// or more outcomes (a SELECT produces a result set; an INSERT with
    /// <c>OUTPUT</c> produces one; DML without OUTPUT and DDL produce a
    /// <see cref="SimulatedNonQuery"/>; IF / BEGIN…END recursively yield
    /// their body's outcomes; SET / DECLARE / transaction statements yield
    /// nothing). When <see cref="BatchContext.IsSkipping"/> is true, every
    /// branch suppresses its outcome yield (the body's parser still ran
    /// for cursor-advance + name resolution, but no result reaches the
    /// client) and the <c>LastStatementRowCount</c> update is skipped.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> DispatchOneStatement(BatchContext batch, bool requireSemicolonBeforeCte)
    {
        // Snapshot the statement-start line before parser advance — used as
        // ERROR_LINE() default when an error fires inside this statement.
        batch.CurrentStatement.StartLine = batch.Parser.Token?.LineNumber ?? 1;
        batch.CurrentStatement.StartIndex = batch.Parser.Token?.StartIndex ?? 0;
        batch.CurrentStatement.SuppressErrorReset = false;
        // Classify the statement as row-returning from its leading token so a
        // failure under continue-on-error surfaces the way real SQL Server
        // frames it: a SELECT (bare / CTE-prefixed / parenthesized) or VALUES
        // has already sent COLMETADATA before erroring, so the in-process
        // reader surfaces it positionally (Read throws); anything else (DML /
        // DDL) has no result-set envelope, so the reader throws eagerly on the
        // advance onto it. See StatementContext.LeadingKeywordReturnsRows.
        batch.CurrentStatement.LeadingKeywordReturnsRows = batch.Parser.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Select or Keyword.With or Keyword.Values } => true,
            Operator { Character: '(' } => true,
            _ => false,
        };
        // READ_COMMITTED_SNAPSHOT readers take a fresh snapshot per statement;
        // clearing here ensures the next statement allocates a new Xid on its
        // first user-table read.
        batch.RcsiStatementSnapshotXid = null;
        // Per-statement stamp bump — establishes a fresh "row" context for
        // NEXT VALUE FOR caching at the statement boundary. Multi-row DML
        // and SELECT iterators bump again per-row, but one-shot statements
        // (SET, DECLARE init, RETURN, scalar SELECT) inherit this baseline
        // bump and don't need to advance the stamp themselves.
        batch.BumpRowStamp();

        // Two-phase dispatch: the core iterator runs the statement body
        // (parser + execution); the wrapper materializes its outcomes and
        // intercepts SimulatedSqlException only when an enclosing TRY frame
        // is active (TryFrameDepth > 0). Iterator methods can't have catch
        // clauses around yield, so the materialize-then-yield split is the
        // structural workaround. Materialization is cheap — every statement
        // produces ≤ 1 outcome and SELECT already materializes its rows to
        // a List before yielding the result set.
        //
        // Statement-scoped Sch-S / Sch-M locks released in `finally` here so
        // they unwind on success, error, or TRY-caught exception alike.
        // The connection's `CurrentExecutingThreadId` is set to the current
        // managed thread for the statement's duration so concurrent acquirers
        // on the same thread can short-circuit to Msg 1205 (no progress is
        // possible while this thread is the executor). Save+restore handles
        // the nested-body case (proc / trigger / UDF dispatch enters this
        // method recursively under the same connection).
        var connection = batch.Connection;
        var savedThreadId = connection.CurrentExecutingThreadId;
        connection.CurrentExecutingThreadId = Environment.CurrentManagedThreadId;
        List<SimulatedStatementOutcome>? outcomes = null;
        SimulatedSqlException? caught = null;
        SimulatedSqlException? continuedError = null;
        var deferredNameError = false;
        try
        {
            try
            {
                outcomes = [.. DispatchOneStatementCore(batch, requireSemicolonBeforeCte)];
            }
            catch (SimulatedSqlException ex)
            {
                // Class 13 = deadlock victim. Real SQL Server auto-rolls
                // back the active transaction before propagating (probe-
                // confirmed: @@TRANCOUNT reads 0 in the catch handler).
                // Done BEFORE the TRY-frame check so both the propagating
                // path and the TRY-caught path observe the same rollback.
                if (ex.Class == 13)
                    connection.CurrentTransaction?.Rollback();
                // Deferred name resolution: real SQL Server binds object /
                // column names lazily, so an un-taken IF / WHILE branch (or a
                // block skipped after BREAK / CONTINUE / RETURN) that names a
                // nonexistent table or column compiles fine and is discarded.
                // The simulator resolves names inline with parsing, so in skip
                // mode such a failure just means the discarded statement
                // referenced something absent — drop the statement instead of
                // surfacing the error. Checked ahead of the TRY-frame path: a
                // skipped BEGIN TRY body must not activate its CATCH. Only
                // name resolution defers — syntax / structural errors carry
                // other numbers and still propagate.
                if (batch.IsSkipping && IsDeferrableNameResolutionError(ex))
                {
                    deferredNameError = true;
                }
                else if (batch.TryFrameDepth > 0)
                {
                    caught = ex;
                }
                else if (batch.ContinueOnError && (IsBatchAbortingNameResolution(ex) || ex.TerminatesBatch))
                {
                    // Batch-aborting error: either a bind-class name-resolution
                    // failure (missing object / column / ambiguous / could-not-
                    // be-bound) or an uncaught THROW (ex.TerminatesBatch). Real
                    // SQL Server ends the batch rather than continuing to the
                    // next statement — probe-confirmed that a mid-batch THROW
                    // leaves the following statement unrun (contrast a
                    // severity-16 RAISERROR, which continues). Emit the one
                    // error, then set the flag the dispatch loop breaks on — no
                    // cursor recovery scan, so the OPTION (USE HINT(...)) tail's
                    // `USE` token is never mis-dispatched as a `USE <database>`
                    // statement.
                    continuedError = ex;
                    batch.BatchAborted = true;
                }
                else if (batch.ContinueOnError && IsStatementTerminating(ex))
                {
                    continuedError = ex;
                }
                else
                {
                    throw;
                }
            }
        }
        finally
        {
            batch.ReleaseStatementSchemaLocks();
            connection.CurrentExecutingThreadId = savedThreadId;
        }

        if (deferredNameError)
        {
            // The parser threw mid-statement; advance to the next statement
            // boundary so the outer dispatch loop resumes cleanly (same
            // cursor-recovery scan the TRY-caught path uses below). No
            // @@ERROR / InFlightError mutation — the skipped statement is
            // conceptually never compiled, not run-and-failed.
            var parser = batch.Parser;
            while (parser.Token is not null && !IsStatementBoundary(parser.Token))
                parser.MoveNextOptional();
            yield break;
        }

        if (continuedError is not null)
        {
            // Top-level statement-terminating continuation: the statement
            // failed but the batch proceeds to the next one (real SQL Server's
            // default, non-XACT_ABORT severity model). Set @@ERROR so a
            // following statement observes it, but do NOT touch InFlightError /
            // ErrorSignaled — those are TRY/CATCH-only state, and this error is
            // bound for the client, not a CATCH block. Same cursor-recovery
            // scan the deferred-name and TRY-caught paths use, then emit the
            // error into the shared outcome stream so both front doors render
            // it (the wire writes error token(s); the in-process reader
            // converts it to a throw); the outer dispatch loop resumes at the
            // next statement.
            connection.LastErrorNumber = continuedError.Number;
            // A batch-aborting error skips the cursor-recovery scan: the outer
            // dispatch loop breaks on BatchAborted, so the cursor position no
            // longer matters and scanning could only mis-stop on a keyword-like
            // token inside the failed statement's own tail.
            if (!batch.BatchAborted)
            {
                var parser = batch.Parser;
                while (parser.Token is not null && !IsStatementBoundary(parser.Token))
                    parser.MoveNextOptional();
            }
            yield return new SimulatedErrorOutcome(continuedError, batch.CurrentStatement.LeadingKeywordReturnsRows);
            yield break;
        }

        if (caught is not null)
        {
            // First error in this TRY body wins; subsequent throws while
            // already-signaled (from skip-mode parsers that still hit
            // runtime errors) silently swallow — the captured first error
            // is what CATCH sees.
            if (!batch.ErrorSignaled)
            {
                batch.InFlightError = new CaughtError(
                    caught.Number,
                    caught.Message,
                    caught.Class,
                    caught.State,
                    batch.CurrentStatement.StartLine,
                    Procedure: null);
                batch.ErrorSignaled = true;
            }
            connection.LastErrorNumber = caught.Number;

            // The parser threw mid-statement, so the cursor is at an
            // unpredictable position. Advance to the next statement boundary
            // (a `;`, statement-starting keyword like `END`, or EOB) so the
            // outer DispatchStatementsUntil loop can resume cleanly — without
            // this scan it'd re-dispatch the same partially-parsed statement
            // and infinite-loop. IsStatementBoundary treats `END` as a stop,
            // so we land at `END TRY` for the typical case.
            var parser = batch.Parser;
            while (parser.Token is not null && !IsStatementBoundary(parser.Token))
                parser.MoveNextOptional();
            yield break;
        }

        // Skip-mode statements don't count toward @@ERROR reset (skip-mode
        // dispatch is conceptually "didn't run" — the surrounding scope owns
        // @@ERROR). Successful real statements clear @@ERROR to 0 unless the
        // statement explicitly opted out (RAISERROR sev ≤ 10 WITH SETERROR
        // wrote its own number and asked us not to clobber it).
        if (!batch.IsSkipping && !batch.CurrentStatement.SuppressErrorReset)
            connection.LastErrorNumber = 0;

        foreach (var o in outcomes!)
            yield return o;
    }

    /// <summary>
    /// True for the parse-time error real SQL Server defers to bind time —
    /// Msg 208 (invalid object name) — when it surfaces from a statement
    /// dispatched in skip mode (un-taken IF / WHILE branch, or a block skipped
    /// after BREAK / CONTINUE / RETURN). Real SQL Server binds object names
    /// lazily, so a skipped statement referencing a missing table / sequence /
    /// XML collection compiles cleanly and is discarded; this swallow drops it
    /// rather than surfacing an error.
    /// </summary>
    /// <remarks>
    /// The common orphan-prone shapes — a missing table in a FROM clause (incl.
    /// an <c>EXISTS</c> / scalar subquery inside an <c>IF</c> condition) and a
    /// missing schema-qualified function call — no longer reach here: the FROM
    /// parser and the function-call parser substitute placeholder metadata in
    /// skip mode (<see cref="FromSource.IsPlaceholder"/>, <c>Expression</c>'s
    /// deferred-call fallback), so those statements parse to completion and are
    /// discarded whole. This swallow remains for the residual object-name sites
    /// that still resolve inline (DML target tables, <c>NEXT VALUE FOR</c>
    /// sequences, XML schema collections). Msg 207 (invalid column on a
    /// resolvable table) is deliberately excluded — probe-confirmed that real
    /// SQL Server errors on it at compile time even in an un-taken branch, so it
    /// falls through to the batch-aborting path (<see cref="IsBatchAbortingNameResolution"/>).
    /// </remarks>
    private static bool IsDeferrableNameResolutionError(SimulatedSqlException ex)
        => ex.Number is 208;

    /// <summary>
    /// True when <paramref name="ex"/> is a statement-terminating error that
    /// ends the current statement but lets the batch continue to the next one
    /// (SQL Server's default severity model). Severity (<see cref="SimulatedSqlException.Class"/>)
    /// 11..16 are statement-terminating; severity ≤ 10 are informational (not
    /// raised as errors) and ≥ 17 are batch/connection-terminating. Deadlock
    /// (Msg 1205, class 13) is the one in-range exception — it aborts the batch
    /// — so it is excluded. Consulted on every top-level batch
    /// (<see cref="BatchContext.ContinueOnError"/>).
    /// </summary>
    /// <remarks>
    /// Known divergence: a genuine syntax error (e.g. Msg 102, class 15)
    /// occurring mid-batch continues over the wire rather than failing the
    /// whole batch as real SQL Server does at compile time. The simulator
    /// interleaves parse and execution (it never modeled a compile-then-run
    /// split), and real tooling such as SMO never sends syntactically invalid
    /// batches — the batches that rely on continuation (DROP #tmp cleanup,
    /// etc.) are all runtime errors. Distinguishing parse-origin from
    /// runtime-origin errors is out of scope.
    /// </remarks>
    private static bool IsStatementTerminating(SimulatedSqlException ex)
        => ex.Class is >= 11 and <= 16 && ex.Number != 1205;

    /// <summary>
    /// True for the bind-class name-resolution failures that abort the whole
    /// batch on real SQL Server rather than merely terminating their statement.
    /// Probe-confirmed against SQL Server 2025 (2026-07-16): with a
    /// <c>SELECT 1; &lt;failing&gt;; SELECT 2</c> batch, the statements before
    /// the failure stream their results and the single error surfaces, but the
    /// statements after it never execute — for Msg 208 (invalid object),
    /// Msg 207 (invalid column), Msg 209 (ambiguous column), Msg 4104 (multi-
    /// part identifier could not be bound), and Msg 4121 (cannot find the
    /// column / function). Contrast the statement-terminating errors that DO
    /// let the batch continue: Msg 3701 (drop missing), Msg 8134 (divide by
    /// zero), Msg 2812 (EXEC missing proc), a severity-16 RAISERROR. Consulted
    /// on every top-level batch (<see cref="BatchContext.ContinueOnError"/>);
    /// both front doors surface the abort (the wire stops writing tokens, the
    /// in-process reader throws the emitted error).
    /// Divergence: real fails Msg 207 / 209 / 4104 at compile time so even the
    /// statements *before* the failure don't run, whereas the simulator
    /// interleaves parse and execution and has already streamed them — the same
    /// compile-vs-runtime divergence <see cref="IsStatementTerminating"/>
    /// documents. The abort-the-rest behavior matches either way.
    /// </summary>
    private static bool IsBatchAbortingNameResolution(SimulatedSqlException ex)
        => ex.Number is 195 or 207 or 208 or 209 or 4104 or 4121;

    private IEnumerable<SimulatedStatementOutcome> DispatchOneStatementCore(BatchContext batch, bool requireSemicolonBeforeCte)
    {
        var context = batch.Parser;
        var connection = context.Connection;

        // CTE bindings live for exactly one statement. Clear at the top of
        // every iteration; a WITH prefix below repopulates.
        context.CteBindings = null;
        batch.CurrentStatement.UtcNow = DateTime.UtcNow;
        batch.CurrentStatement.StatementScopedValues = null;

        // WITH prefix applies to the immediately-following SELECT / INSERT /
        // UPDATE / DELETE / MERGE. ParseCteBindings sets context.CteBindings
        // and advances the cursor to the dispatched statement's leading
        // keyword; the switch below runs unchanged.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (requireSemicolonBeforeCte)
                throw SimulatedSqlException.CteRequiresPrecedingSemicolon();
            ParseCteBindings(context);
        }

        SimulatedStatementOutcome? outcome;
        switch (context.Token)
        {
            case ReservedKeyword { Keyword: Keyword.Select }:
                {
                    var selection = Selection.Parse(context, 0);
                    if (selection.IntoTarget is not null)
                    {
                        // SELECT INTO: creates the destination table and
                        // inserts each projected row. RunMutation gives
                        // the executor access to the active undo log so
                        // transactional CREATE+INSERT can roll back. In
                        // skip mode, ExecuteSelectInto returns SimulatedNonQuery(0)
                        // without touching the heap.
                        outcome = RunMutation(context, _ => ExecuteSelectInto(selection, batch));
                        if (!batch.IsSkipping)
                        {
                            connection.LastStatementRowCount = outcome.RecordsAffected;
                            yield return outcome;
                        }
                        break;
                    }
                    if (batch.IsSkipping)
                        break;
                    // Materialize rows up-front so @@ROWCOUNT reflects the
                    // statement's full row count for the next statement in
                    // the same batch (real SQL Server runs server-side and
                    // sets @@ROWCOUNT on completion; the simulator
                    // materializes to mirror that).
                    var rows = selection.Execute(batch).RowBytes.ToList();
                    connection.LastStatementRowCount = rows.Count;
                    outcome = selection.IsAssignmentOnly
                        ? new SimulatedNonQuery(rows.Count)
                        : new SimulatedSqlResultSet(selection.Schema, selection.ColumnNames, rows) { ColumnNullability = selection.ColumnNullability };

                    // Plan-cache promotion inline before the yield. Gates:
                    // top-level (BlockDepth == 0 → not inside IF / WHILE /
                    // BEGIN…END / TRY/CATCH); first top-level statement
                    // (!HasDispatchedStatement, which the outer dispatch
                    // loop sets AFTER this method returns); shape (not
                    // assignment-only — those yield NonQuery and aren't worth
                    // caching); no session-scoped table reference; parser at
                    // EOB (no trailing statements). The promote helper then
                    // re-checks schema version and cap before adding.
                    if (batch.BlockDepth == 0
                        && !batch.HasDispatchedStatement
                        && !selection.IsAssignmentOnly
                        && !batch.HasSessionScopedReference
                        && context.Token is null)
                    {
                        TryPromoteSelectionToPlanCache(batch, selection);
                    }
                    yield return outcome;
                    break;
                }

            case ReservedKeyword { Keyword: Keyword.Insert }:
                outcome = RunMutation(context, ParseInsert);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Merge }:
                outcome = RunMutation(context, ParseMerge);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                // Real SQL Server requires `;` after MERGE (Msg 10713) —
                // the only statement family with a mandatory terminator.
                // Check before normalization so the cursor is still on the
                // parser's lookahead position. The check runs even in skip
                // mode — the grammar requirement is independent of execution.
                if (context.Token is not Operator { Character: ';' })
                    throw SimulatedSqlException.MergeMustBeTerminated();
                break;

            case ReservedKeyword { Keyword: Keyword.Update }:
                outcome = RunMutation(context, ParseUpdate);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Delete }:
                outcome = RunMutation(context, ParseDelete);
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.If }:
                foreach (var o in ParseIfStatement(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.While }:
                foreach (var o in ParseWhileStatement(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Break }:
                ParseBreakStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Continue }:
                ParseContinueStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Return }:
                ParseReturnStatement(batch);
                break;

            case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute }:
                foreach (var o in ParseExec(batch))
                    yield return o;
                break;

            case UnquotedString { ContextualKeyword: ContextualKeyword.Throw }:
                ParseThrowStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Print }:
                ParsePrintStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.RaisError }:
                ParseRaiserrorStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.WaitFor }:
                ParseWaitForStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Truncate }:
                ParseTruncateStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Use }:
                ParseUseStatement(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Begin }:
                // Peek the token after BEGIN to disambiguate transaction-start
                // (BEGIN TRAN / BEGIN TRANSACTION / BEGIN DISTRIBUTED TRAN) from
                // not-modeled forms (BEGIN TRY / BEGIN ATOMIC) from a compound
                // statement block (BEGIN … END). The transaction case restores
                // and re-parses via TryParseBeginTransaction so its existing
                // BEGIN-consuming flow stays untouched.
                {
                    var checkpoint = context.SaveCheckpoint();
                    context.MoveNextRequired();
                    var afterBegin = context.Token;
                    context.RestoreCheckpoint(checkpoint);
                    switch (afterBegin)
                    {
                        case ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction }:
                            if (TryParseBeginTransaction(context) && !batch.IsSkipping)
                                connection.LastStatementRowCount = 0;
                            break;
                        case ReservedKeyword { Keyword: Keyword.Distributed }:
                            throw new NotSupportedException("BEGIN DISTRIBUTED TRANSACTION isn't modeled (no distributed transaction coordinator).");
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Try }:
                            foreach (var o in ParseTryCatch(batch))
                                yield return o;
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Atomic }:
                            foreach (var o in ParseBeginAtomicBlock(batch))
                                yield return o;
                            if (!batch.IsSkipping)
                                connection.LastStatementRowCount = 0;
                            break;
                        default:
                            foreach (var o in ParseBeginBlock(batch))
                                yield return o;
                            if (!batch.IsSkipping)
                                connection.LastStatementRowCount = 0;
                            break;
                    }
                    break;
                }

            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseShrink(context, batch, out outcome):
            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseShowStatistics(context, batch, out outcome):
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = 0;
                    if (outcome is not null)
                        yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
            case ReservedKeyword { Keyword: Keyword.Drop } when TryParseDrop(context):
            case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
                // DDL invalidates every cached plan parsed under the prior
                // schema version. Skip-mode statements don't actually execute
                // the DDL (their parse-only walk has no schema effect), so the
                // bump is gated on !IsSkipping.
                if (!batch.IsSkipping)
                {
                    BumpSchemaVersion();
                    connection.LastStatementRowCount = 0;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Commit } when TryParseCommit(context):
            case ReservedKeyword { Keyword: Keyword.Save } when TryParseSavepoint(context):
            case ReservedKeyword { Keyword: Keyword.Rollback } when TryParseRollbackTransaction(context):
            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseDbcc(context):
            case ReservedKeyword { Keyword: Keyword.Grant } when TryParseGrantRevokeDeny(context, PermissionStatementKind.Grant):
            case ReservedKeyword { Keyword: Keyword.Revoke } when TryParseGrantRevokeDeny(context, PermissionStatementKind.Revoke):
            case ReservedKeyword { Keyword: Keyword.Deny } when TryParseGrantRevokeDeny(context, PermissionStatementKind.Deny):
            case UnquotedString { ContextualKeyword: ContextualKeyword.Disable } when TryParseEnableOrDisableTrigger(context, disable: true):
            case UnquotedString { ContextualKeyword: ContextualKeyword.Enable } when TryParseEnableOrDisableTrigger(context, disable: false):
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;
            case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context):
                // SET @v = expr (probe-confirmed to set @@ROWCOUNT to 1).
                // Other SET shapes (SET NOCOUNT etc.) reach here too; the
                // simulator can't distinguish without re-parsing, but the
                // session-state SET shapes are rare and the rowcount they
                // leave isn't asserted-on in practice.
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 1;
                break;
            case ReservedKeyword { Keyword: Keyword.Declare }:
                {
                    // Cursor declaration (`DECLARE <name> CURSOR …`) is the
                    // only DECLARE form whose first token after the keyword
                    // isn't an `@`-prefixed variable name. Peek to route.
                    var declCheckpoint = context.SaveCheckpoint();
                    context.MoveNextRequired();
                    var isCursorDeclaration = context.Token is not AtPrefixedString;
                    context.RestoreCheckpoint(declCheckpoint);
                    if (isCursorDeclaration)
                    {
                        ParseDeclareCursor(batch);
                        if (!batch.IsSkipping)
                            connection.LastStatementRowCount = 0;
                    }
                    else
                    {
                        var initRowCount = TryParseDeclare(context);
                        if (!batch.IsSkipping && initRowCount is int n)
                            connection.LastStatementRowCount = n;
                        // No initializer → @@ROWCOUNT preserved (probe-confirmed).
                    }
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Open }:
                ParseOpenCursor(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Fetch }:
                foreach (var o in ParseFetchCursor(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Close }:
                ParseCloseCursor(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Deallocate }:
                ParseDeallocateCursor(batch);
                if (!batch.IsSkipping)
                    connection.LastStatementRowCount = 0;
                break;
            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // Normalize cursor to a lookahead position. Well-behaved parsers
        // already left Token at their first un-consumed token (`;`, the
        // next statement's leading keyword, or null at EOF); for parsers
        // that ended on the last consumed token, advance once.
        if (!IsStatementBoundary(context.Token))
            context.MoveNextOptional();
    }

    /// <summary>
    /// Returns true when <paramref name="token"/> is at a place the
    /// dispatch loop can resume from without advancing: a <c>;</c>, end of
    /// batch, a recognized statement-starting keyword, or the <c>END</c>
    /// terminator of a BEGIN…END block. Used to decide whether to re-normalize
    /// a parser's leftover cursor position.
    ///
    /// This is the single source of truth for "does this token begin a new
    /// top-level statement (or a hard boundary)?" — the dispatch loop, the
    /// EXEC-argument scanner, the principal-DDL parse-and-discard tail, and the
    /// SELECT projection-list terminator all route through it, so a new
    /// statement keyword is added in exactly one place. SQL Server accepts
    /// back-to-back statements without a separating <c>;</c>; every consumer
    /// mirrors that by treating the full keyword set uniformly as a boundary.
    /// </summary>
    internal static bool IsStatementBoundary(Token? token) =>
        token is null
        or Operator { Character: ';' }
        or ReservedKeyword
        {
            Keyword: Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete
                or Keyword.Merge or Keyword.Begin or Keyword.Commit or Keyword.Rollback
                or Keyword.Save or Keyword.Create or Keyword.Drop or Keyword.Alter or Keyword.Dbcc
                or Keyword.Set or Keyword.Declare or Keyword.With or Keyword.If or Keyword.Else
                or Keyword.End or Keyword.While or Keyword.Break or Keyword.Continue
                or Keyword.Return or Keyword.Print or Keyword.RaisError or Keyword.WaitFor
                or Keyword.Truncate or Keyword.Use or Keyword.Grant or Keyword.Revoke or Keyword.Deny
                or Keyword.Open or Keyword.Fetch or Keyword.Close or Keyword.Deallocate
                or Keyword.Exec or Keyword.Execute
        }
        // THROW is a contextual keyword in SQL Server's grammar — added with
        // the TRY/CATCH companion feature in 2012, not in the reserved list.
        // It surfaces as UnquotedString from the tokenizer; statement-boundary
        // detection routes through the cached ContextualKeyword classifier.
        or UnquotedString { ContextualKeyword: ContextualKeyword.Throw };

    /// <summary>
    /// At end-of-batch, copies the final values of every InputOutput /
    /// Output direction <see cref="DbParameter"/> from its variable slot
    /// back into <see cref="DbParameter.Value"/>. Mirrors SqlClient's
    /// behavior of round-tripping mutations made by SQL-text in the batch
    /// (probe-confirmed against SQL Server 2025: a parameter sent in as 5,
    /// mutated by `SET @x = 999`, reads 999 from the caller's
    /// <c>param.Value</c> after <c>ExecuteNonQuery</c>).
    /// </summary>
    private static void WriteBackOutputParameters(BatchContext batch)
    {
        foreach (var slot in batch.Variables.Values)
        {
            if (slot.Parameter is { } parameter
                && parameter.Direction is ParameterDirection.InputOutput or ParameterDirection.Output)
            {
                parameter.Value = slot.Value.IsNull ? DBNull.Value : slot.Value.ToObject();
            }
        }
    }

    /// <summary>
    /// Wraps a mutation statement (INSERT / UPDATE / DELETE / MERGE) with
    /// statement-level atomicity. Routes mutations to the connection's
    /// active transaction's <see cref="UndoLog"/> when one exists (Bundle 2
    /// — explicit <c>BeginTransaction</c>); otherwise creates a fresh
    /// per-statement log (Bundle 1 — auto-commit). In both cases the
    /// statement captures a marker at entry; on exception only the entries
    /// appended this statement are unwound, which matches SQL Server's
    /// "failed statement leaves the surrounding transaction alive" behavior
    /// (probe-confirmed 2026-05-08). Identity / rowversion counters bypass
    /// the log entirely.
    /// </summary>
    /// <summary>
    /// Parses <c>SAVE TRAN[SACTION] &lt;name&gt;</c> and records the active
    /// transaction's current undo-log position against the name. EF Core 10
    /// emits this per SaveChanges call inside an active
    /// <c>Database.BeginTransaction</c> so a failed SaveChanges can roll
    /// back just that save's writes via <c>ROLLBACK TRANSACTION &lt;name&gt;</c>.
    /// Returns false if the next token isn't <c>TRAN</c> / <c>TRANSACTION</c>
    /// (the <c>case … when</c> dispatch falls through to a syntax error).
    /// </summary>
    private static bool TryParseSavepoint(ParserContext context)
    {
        if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            return false;
        var name = context.GetNextRequired<Name>().Value;
        context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        var tx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);
        tx.Savepoints[name] = tx.UndoLog.Position;
        return true;
    }

    /// <summary>
    /// Parses <c>BEGIN TRAN[SACTION] [name] [WITH MARK 'description']</c>.
    /// Opens a fresh <see cref="SimulatedDbTransaction"/> on the connection
    /// when none is active (TRANCOUNT 0 → 1) or increments
    /// <see cref="SimulatedDbTransaction.TranCount"/> when one already is
    /// (nested-BEGIN TRANCOUNT bump, no real nesting). The optional name and
    /// WITH MARK clause are accepted but cosmetic — SQL Server treats the
    /// name as documentation only, and only the outermost COMMIT actually
    /// commits regardless of which name the COMMIT references.
    /// </summary>
    private static bool TryParseBeginTransaction(ParserContext context)
    {
        if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            return false;
        // Optional name (BEGIN TRANSACTION my_tx). Cosmetic; consume and ignore.
        if (context.MoveNext() && context.Token is Name)
            context.MoveNextOptional();

        if (context.Batch.IsSkipping)
            return true;

        if (context.Connection.CurrentTransaction is { } existing)
        {
            existing.TranCount++;
        }
        else
        {
            context.Connection.CurrentTransaction = new SimulatedDbTransaction(
                context.Simulation, context.Connection, System.Data.IsolationLevel.Unspecified);
        }
        return true;
    }

    /// <summary>
    /// Parses <c>COMMIT [TRAN[SACTION]] [name] [WORK]</c>. Decrements
    /// <see cref="SimulatedDbTransaction.TranCount"/>; when it reaches 0
    /// the transaction actually commits (drops the undo log and clears
    /// <see cref="SimulatedDbConnection.CurrentTransaction"/>). Raises
    /// <see cref="SimulatedSqlException.NoCorrespondingBeginCommit"/>
    /// (Msg 3902) when no transaction is active — probe-confirmed wording.
    /// </summary>
    private static bool TryParseCommit(ParserContext context)
    {
        // COMMIT alone is the bare form; followed by TRAN/TRANSACTION/WORK
        // gives the qualified form, optionally followed by a name.
        if (context.MoveNext()
            && context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
        {
            // Optional savepoint-style name. Consume and ignore.
            if (context.MoveNext() && context.Token is Name)
                context.MoveNextOptional();
        }
        // COMMIT WORK is an ANSI-equivalent. WORK isn't reserved in the
        // simulator's keyword list; accept it as an unquoted identifier
        // following COMMIT.
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Work })
        {
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        var tx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.NoCorrespondingBeginCommit();

        tx.TranCount--;
        if (tx.TranCount == 0)
            tx.Commit();
        return true;
    }

    /// <summary>
    /// Parses <c>ROLLBACK [TRAN[SACTION]] [name] [WORK]</c>. Two shapes:
    /// with a savepoint name → partial rollback to the saved position
    /// (EF Core 10's SaveChanges-failure recovery path); without a name →
    /// full transaction rollback regardless of TRANCOUNT depth (probe-
    /// confirmed). Bare <c>ROLLBACK</c> with no active transaction raises
    /// <see cref="SimulatedSqlException.NoCorrespondingBeginRollback"/>
    /// (Msg 3903).
    /// </summary>
    private static bool TryParseRollbackTransaction(ParserContext context)
    {
        // After ROLLBACK, accept TRAN/TRANSACTION/WORK or fall through to
        // bare-ROLLBACK with the cursor on the next un-consumed token.
        if (context.MoveNext())
        {
            if (context.Token is ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            {
                if (context.MoveNext() && context.Token is Name nameToken)
                {
                    // Savepoint-name path: partial rollback to the saved position.
                    var name = nameToken.Value;
                    context.MoveNextOptional();

                    if (context.Batch.IsSkipping)
                        return true;

                    var tx = context.Connection.CurrentTransaction
                        ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
                    if (!tx.Savepoints.TryGetValue(name, out var marker))
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    tx.UndoLog.RollbackTo(marker);
                    return true;
                }
            }
            else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Work })
            {
                context.MoveNextOptional();
            }
        }

        if (context.Batch.IsSkipping)
            return true;

        // Bare ROLLBACK (or ROLLBACK TRAN / ROLLBACK WORK with no name) →
        // full rollback regardless of TRANCOUNT.
        var activeTx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
        activeTx.Rollback();
        return true;
    }

    private static SimulatedStatementOutcome RunMutation(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> body)
    {
        var tx = context.Connection.CurrentTransaction;
        var log = tx?.UndoLog ?? new UndoLog();
        var marker = log.Position;
        // Table variables get a parallel per-statement undo log so multi-row
        // mutations roll back atomically on mid-statement failure (probe-
        // confirmed: real SQL Server rolls back partial @t writes on row-
        // level errors). The log is dropped on statement success, so
        // ROLLBACK TRAN never sees these entries — matches the non-
        // transactional invariant.
        var tableVarLog = new UndoLog();
        // Auto-commit statements get a statement-scoped pending-version
        // list; explicit transactions route entries onto the tx's
        // accumulating list (finalized at COMMIT, discarded at ROLLBACK).
        // The marker captures the tx-list size on entry so a statement-
        // atomic mid-execution failure can discard only the entries this
        // statement added.
        var versionEntriesMarker = tx?.PendingVersionEntries.Count ?? 0;
        var statementVersionEntries = tx is null ? new List<PendingVersionEntry>() : null;

        var savedLog = context.Batch.CurrentUndoLog;
        var savedTableVarLog = context.Batch.CurrentTableVarUndoLog;
        var savedStatementVersionEntries = context.Batch.CurrentStatementVersionEntries;
        context.Batch.CurrentUndoLog = log;
        context.Batch.CurrentTableVarUndoLog = tableVarLog;
        context.Batch.CurrentStatementVersionEntries = statementVersionEntries;
        try
        {
            var outcome = body(context);
            if (statementVersionEntries is { } autoCommitEntries)
            {
                // FinalizePendingEntries clears the list, so capture whether
                // this statement versioned anything before the call.
                var versionedThisStatement = autoCommitEntries.Count > 0;
                Storage.VersionStore.FinalizePendingEntries(autoCommitEntries, context.CurrentDatabase);
                // Auto-commit statement: its writes are now permanent, so
                // commit the throwaway log — reclaiming chains superseded by
                // this statement's UPDATE/DELETEs (unversioned path). Under an
                // explicit tx (statementVersionEntries is null) the entries
                // stay on the tx's log until COMMIT instead.
                log.Commit();
                // When this statement versioned its superseded rows, those
                // images are pinned only by the HistoricalVersions just
                // created. With no snapshot open nothing needs them, so collect
                // now rather than leaving them until the next explicit-tx
                // commit (the version-store analog of the unversioned
                // log.Commit() above). An active snapshot legitimately needs the
                // versions, so defer — and skip the scan — until it closes.
                var autoCommitDatabase = context.CurrentDatabase;
                if (versionedThisStatement && autoCommitDatabase.ActiveSnapshotTxs.IsEmpty)
                    Storage.VersionStore.RunGarbageCollection(autoCommitDatabase);
            }
            // Table-variable writes are non-transactional and final on
            // statement success regardless of any enclosing tx, so their
            // throwaway log always commits here.
            tableVarLog.Commit();
            return outcome;
        }
        catch
        {
            if (statementVersionEntries is { } autoCommitEntries)
            {
                Storage.VersionStore.DiscardPendingEntries(autoCommitEntries);
            }
            else if (tx is not null && tx.PendingVersionEntries.Count > versionEntriesMarker)
            {
                var added = tx.PendingVersionEntries.GetRange(versionEntriesMarker, tx.PendingVersionEntries.Count - versionEntriesMarker);
                tx.PendingVersionEntries.RemoveRange(versionEntriesMarker, tx.PendingVersionEntries.Count - versionEntriesMarker);
                Storage.VersionStore.DiscardPendingEntries(added);
            }
            log.RollbackTo(marker);
            tableVarLog.Rollback();
            throw;
        }
        finally
        {
            context.Batch.CurrentUndoLog = savedLog;
            context.Batch.CurrentTableVarUndoLog = savedTableVarLog;
            context.Batch.CurrentStatementVersionEntries = savedStatementVersionEntries;
        }
    }
}
