using System.Collections.Concurrent;
using System.Collections.Frozen;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;
using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
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
        this.LobReclamation = new(this);
        // The lock manager sweeps abandoned sessions before every acquisition,
        // which is what unblocks a live session waiting on a leaked one's lock.
        this.LockManager.OwningSimulation = this;
        // Every instance ships with the four SQL Server system databases
        // (master = 1, tempdb = 2, model = 3, msdb = 4), present from
        // construction so `USE master`, `master.sys.*` three-part reads,
        // `master.dbo.<proc>` calls, and SSMS's connect-time `has_dbaccess`
        // / msdb catalog probes all resolve without an explicit import.
        // Seeded here under the ctor-time collation (baseline); the
        // ServerCollationName object-initializer setter runs afterward and
        // re-points each system database's collation to the chosen server
        // collation.
        foreach (var (name, id) in SystemDatabaseIds)
            this.Databases[name] = new Database(name, this.ServerCollation) { Id = id };
        // Real ships master / tempdb / msdb with cross-database chaining on and
        // msdb trustworthy; model and every user database start with both off
        // (probe-confirmed against SQL Server 2025). A new database never
        // inherits either flag from model — real resets both at CREATE.
        this.Databases[MasterDatabaseName].CrossDatabaseChaining = true;
        this.Databases[TempdbDatabaseName].CrossDatabaseChaining = true;
        var msdb = this.Databases[MsdbDatabaseName];
        msdb.CrossDatabaseChaining = true;
        msdb.Trustworthy = true;
        // Recovery models real ships: master / tempdb / msdb SIMPLE, model FULL
        // (probe-confirmed). A new user database inherits model's, which is the
        // Database field's own default.
        this.Databases[MasterDatabaseName].RecoveryModel = RecoveryModel.Simple;
        this.Databases[TempdbDatabaseName].RecoveryModel = RecoveryModel.Simple;
        msdb.RecoveryModel = RecoveryModel.Simple;
        // master and msdb ship with snapshot isolation allowed, master with a
        // target recovery time of 0, and master and model with Service Broker
        // off (probed 2026-09-30 against SQL Server 2025).
        var master = this.Databases[MasterDatabaseName];
        master.AllowSnapshotIsolation = true;
        msdb.AllowSnapshotIsolation = true;
        master.TargetRecoveryTimeSeconds = 0;
        master.BrokerEnabled = false;
        this.Databases[ModelDatabaseName].BrokerEnabled = false;
        // Query Store: model ships on, which is why a fresh user database does
        // too (the QueryStoreOptions field default). master / tempdb refuse the
        // option outright and msdb ships off (probe-confirmed against SQL
        // Server 2025, 2026-08-08).
        this.Databases[MasterDatabaseName].QueryStore.DesiredState = QueryStoreState.Off;
        this.Databases[TempdbDatabaseName].QueryStore.DesiredState = QueryStoreState.Off;
        msdb.QueryStore.DesiredState = QueryStoreState.Off;
        SeedMsdbPolicyHealthView(msdb);
        SeedMsdbPolicyConfigurationView(msdb);
        SeedMsdbPolicyAutomationFunction(msdb);
        SeedMsdbBackupSet(msdb);
    }

    /// <summary>
    /// Seeds <c>msdb.dbo.backupset</c>, the backup history table, empty and in
    /// SQL Server 2025's column shape (probed 2026-09-30): the simulator takes
    /// no backups, so its history is empty, which SMO reads as a database's
    /// last backup dates being unset. Constructed directly so no connection is
    /// materialized at construction.
    /// </summary>
    private static void SeedMsdbBackupSet(Database msdb)
    {
        HeapColumn[] columns =
        [
            new("backup_set_id", SqlType.Int32, maxLength: null, nullable: false),
            new("backup_set_uuid", SqlType.UniqueIdentifier, maxLength: null, nullable: false),
            new("media_set_id", SqlType.Int32, maxLength: null, nullable: false),
            new("first_family_number", SqlType.TinyInt, maxLength: null, nullable: true),
            new("first_media_number", SqlType.SmallInt, maxLength: null, nullable: true),
            new("last_family_number", SqlType.TinyInt, maxLength: null, nullable: true),
            new("last_media_number", SqlType.SmallInt, maxLength: null, nullable: true),
            new("catalog_family_number", SqlType.TinyInt, maxLength: null, nullable: true),
            new("catalog_media_number", SqlType.SmallInt, maxLength: null, nullable: true),
            new("position", SqlType.Int32, maxLength: null, nullable: true),
            new("expiration_date", SqlType.DateTime, maxLength: null, nullable: true),
            new("software_vendor_id", SqlType.Int32, maxLength: null, nullable: true),
            new("name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("description", NVarcharSqlType.Get(255, msdb.Collation, Coercibility.Implicit), maxLength: 255, nullable: true),
            new("user_name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("software_major_version", SqlType.TinyInt, maxLength: null, nullable: true),
            new("software_minor_version", SqlType.TinyInt, maxLength: null, nullable: true),
            new("software_build_version", SqlType.SmallInt, maxLength: null, nullable: true),
            new("time_zone", SqlType.SmallInt, maxLength: null, nullable: true),
            new("mtf_minor_version", SqlType.TinyInt, maxLength: null, nullable: true),
            new("first_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("last_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("checkpoint_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("database_backup_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("database_creation_date", SqlType.DateTime, maxLength: null, nullable: true),
            new("backup_start_date", SqlType.DateTime, maxLength: null, nullable: true),
            new("backup_finish_date", SqlType.DateTime, maxLength: null, nullable: true),
            new("type", CharSqlType.Get(1, msdb.Collation, Coercibility.Implicit), maxLength: 1, nullable: true),
            new("sort_order", SqlType.SmallInt, maxLength: null, nullable: true),
            new("code_page", SqlType.SmallInt, maxLength: null, nullable: true),
            new("compatibility_level", SqlType.TinyInt, maxLength: null, nullable: true),
            new("database_version", SqlType.Int32, maxLength: null, nullable: true),
            new("backup_size", SqlType.GetDecimal(20, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("database_name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("server_name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("machine_name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("flags", SqlType.Int32, maxLength: null, nullable: true),
            new("unicode_locale", SqlType.Int32, maxLength: null, nullable: true),
            new("unicode_compare_style", SqlType.Int32, maxLength: null, nullable: true),
            new("collation_name", NVarcharSqlType.Get(128, msdb.Collation, Coercibility.Implicit), maxLength: 128, nullable: true),
            new("is_password_protected", SqlType.Bit, maxLength: null, nullable: true),
            new("recovery_model", NVarcharSqlType.Get(60, msdb.Collation, Coercibility.Implicit), maxLength: 60, nullable: true),
            new("has_bulk_logged_data", SqlType.Bit, maxLength: null, nullable: true),
            new("is_snapshot", SqlType.Bit, maxLength: null, nullable: true),
            new("is_readonly", SqlType.Bit, maxLength: null, nullable: true),
            new("is_single_user", SqlType.Bit, maxLength: null, nullable: true),
            new("has_backup_checksums", SqlType.Bit, maxLength: null, nullable: true),
            new("is_damaged", SqlType.Bit, maxLength: null, nullable: true),
            new("begins_log_chain", SqlType.Bit, maxLength: null, nullable: true),
            new("has_incomplete_metadata", SqlType.Bit, maxLength: null, nullable: true),
            new("is_force_offline", SqlType.Bit, maxLength: null, nullable: true),
            new("is_copy_only", SqlType.Bit, maxLength: null, nullable: true),
            new("first_recovery_fork_guid", SqlType.UniqueIdentifier, maxLength: null, nullable: true),
            new("last_recovery_fork_guid", SqlType.UniqueIdentifier, maxLength: null, nullable: true),
            new("fork_point_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("database_guid", SqlType.UniqueIdentifier, maxLength: null, nullable: true),
            new("family_guid", SqlType.UniqueIdentifier, maxLength: null, nullable: true),
            new("differential_base_lsn", SqlType.GetDecimal(25, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("differential_base_guid", SqlType.UniqueIdentifier, maxLength: null, nullable: true),
            new("compressed_backup_size", SqlType.GetDecimal(20, 0), maxLength: null, nullable: true, spelledNumeric: true),
            new("key_algorithm", NVarcharSqlType.Get(32, msdb.Collation, Coercibility.Implicit), maxLength: 32, nullable: true),
            new("encryptor_thumbprint", VarbinarySqlType.Get(20), maxLength: 20, nullable: true),
            new("encryptor_type", NVarcharSqlType.Get(32, msdb.Collation, Coercibility.Implicit), maxLength: 32, nullable: true),
            new("last_valid_restore_time", SqlType.DateTime, maxLength: null, nullable: true),
            new("compression_algorithm", NVarcharSqlType.Get(32, msdb.Collation, Coercibility.Implicit), maxLength: 32, nullable: true),
        ];
        var table = new HeapTable("backupset", columns, msdb.AllocateObjectId()) { OwningDatabase = msdb };
        msdb.Schemas[Database.DefaultSchemaName].HeapTables[table.Name] = table;
    }

    /// <summary>
    /// Test-only seam: when set, the TDS network session invokes it just before
    /// executing a SQLBatch, letting a wire test force an exception the session's
    /// typed catch list does not anticipate through the terminal crash boundary
    /// (verifying the client receives a severity-20 <c>SqlException</c> rather
    /// than a bare transport reset). Never set outside tests; per-instance, so
    /// parallel test simulations stay isolated.
    /// </summary>
    internal Action? NetworkBatchCrashHookForTesting;

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
            checkOptionCheck: null,
            isJoinUpdatable: false)
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
            checkOptionCheck: null,
            isJoinUpdatable: false)
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
    /// block; user databases take the smallest free id from 5 at registration
    /// time (see <see cref="RegisterUserDatabase"/> / <c>DatabasesWithIds</c>).
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
    /// (<see cref="RegisterUserDatabase"/> keeps ids 1–4 reserved so user
    /// databases number from 5). Keyed by <see cref="BuiltInToken.Comparer"/>.
    /// </summary>
    internal static readonly FrozenSet<string> SystemDatabaseNames =
        new[] { MasterDatabaseName, TempdbDatabaseName, ModelDatabaseName, MsdbDatabaseName }
            .ToFrozenSet(BuiltInToken.Comparer);

    /// <summary>
    /// Whether this instance may register and run CLR assemblies
    /// (<c>CREATE ASSEMBLY</c> plus the <c>EXTERNAL NAME</c> routines bound to
    /// them). Defaults to <see langword="false"/>; set it in an object
    /// initializer (<c>new Simulation { EnableClr = true }</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Enabling this permits arbitrary code execution inside the host
    /// process.</strong> <c>CREATE ASSEMBLY</c> takes a .NET assembly as raw
    /// bytes and runs its code in-process. Real SQL Server confines a
    /// <c>SAFE</c> assembly with Code Access Security; .NET removed CAS
    /// entirely and offers no in-process replacement, so a registered assembly
    /// here runs with the host process's full trust no matter which
    /// <c>PERMISSION_SET</c> the DDL names. The simulator screens candidates
    /// statically at registration — rejecting non-managed images, P/Invoke
    /// declarations, mutable statics, and references outside the framework and
    /// into a denied API surface — but a metadata screen is defense in depth,
    /// not a sandbox.
    /// </para>
    /// <para>
    /// Leave it off unless the assembly bytes come from a source you trust as
    /// much as your own application code. It matters most when a
    /// <c>Simulation</c> is exposed over the network endpoint, where any client
    /// that can issue DDL could otherwise supply an assembly.
    /// </para>
    /// </remarks>
    public bool EnableClr { get; init; }

    /// <summary>
    /// Opens the data and format files that <c>BULK INSERT</c> and
    /// <c>OPENROWSET(BULK …)</c> name, standing in for the server's file
    /// system. Defaults to <see langword="null"/>, under which every path
    /// behaves as a file that doesn't exist; set it in an object initializer
    /// (<c>new Simulation { OpenBulkFile = path =&gt; … }</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The delegate receives the path exactly as the statement spells it and
    /// returns a readable <see cref="Stream"/>, which the simulator reads to
    /// its end and disposes, or <see langword="null"/> for a file that doesn't
    /// exist. Tests can serve files from memory
    /// (<c>path =&gt; new MemoryStream(bytes)</c>); a host wanting real disk
    /// access supplies something like
    /// <c>path =&gt; File.Exists(path) ? File.OpenRead(path) : null</c>.
    /// </para>
    /// <para>
    /// <strong>Anything the delegate returns becomes readable to every
    /// session</strong>, as real SQL Server's file access is. Keep the
    /// mapping as narrow as the content allows, above all when a
    /// <c>Simulation</c> is exposed over the network endpoint. Files are only
    /// read: an <c>ERRORFILE</c> is never written.
    /// </para>
    /// </remarks>
    public Func<string, Stream?>? OpenBulkFile { get; init; }

    /// <summary>
    /// Server-wide default collation name. Used as the seed for every
    /// database created on this simulation — both the lazy
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
    /// <see cref="ImportBacpac(Stream, out BacpacImportResult, BacpacImportOptions?)"/>.
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
    /// <see cref="ImportBacpac(Stream, out BacpacImportResult, BacpacImportOptions?)"/>);
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
    /// <see cref="ImportBacpac(Stream, out BacpacImportResult, BacpacImportOptions?)"/>
    /// adds further entries; <c>USE &lt;db&gt;</c> switches a session's
    /// <see cref="SimulatedDbConnection.CurrentDatabase"/> across entries
    /// (Msg 911 on miss). Fresh connections pick the lazy seed when present,
    /// else the alphabetically-first entry (see
    /// <see cref="SimulatedDbConnection"/>'s ResolveInitialDatabase).
    /// Every session reads it while another may create, drop or rename a
    /// database, so it is concurrent; a writer still takes its lock, which
    /// serializes the read-modify-writes (id allocation, the rename's re-key,
    /// the name checks) the dictionary alone can't make atomic.
    /// </summary>
    internal readonly ConcurrentDictionary<string, Database> Databases = new(BuiltInToken.Comparer);

    /// <summary>
    /// Assigns <paramref name="db"/> the smallest free <c>database_id</c> ≥ 5
    /// (the reserved block 1–4 is the four system databases) and adds it to
    /// <see cref="Databases"/> under its name. Locks <see cref="Databases"/>
    /// for the read-modify-write so concurrent <c>CREATE DATABASE</c> /
    /// <c>ImportBacpac</c> calls can't collide on an id or the dictionary.
    /// Callers already holding the <see cref="Databases"/> lock use
    /// <see cref="RegisterUserDatabaseLocked"/> instead.
    /// </summary>
    internal void RegisterUserDatabase(Database db)
    {
        lock (this.Databases)
            RegisterUserDatabaseLocked(db);
    }

    /// <summary>
    /// The id-allocation + insert body of <see cref="RegisterUserDatabase"/>,
    /// assuming the caller already holds the <see cref="Databases"/> lock (the
    /// lazy default-database seed in <c>SimulatedDbConnection</c> runs inside
    /// that lock). Picks the smallest <see cref="short"/> ≥ 5 not currently
    /// held by any database; a freed id (from a dropped database) is naturally
    /// the smallest gap, so it's reused first — matching real SQL Server. The
    /// database starts from <c>model</c>'s scoped configuration, as real's
    /// does (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    internal void RegisterUserDatabaseLocked(Database db)
    {
        if (this.Databases.TryGetValue(ModelDatabaseName, out var model))
            db.ScopedConfiguration.CopyFrom(model.ScopedConfiguration);
        var used = new HashSet<short>();
        foreach (var (_, existing) in this.Databases)
            _ = used.Add(existing.Id);
        short id = 5;
        while (used.Contains(id))
            id++;
        db.Id = id;
        if (!this.Databases.TryAdd(db.Name, db))
            throw new ArgumentException($"A database named '{db.Name}' is already registered.", nameof(db));
    }

    /// <summary>
    /// SQL-authentication server logins created via <c>CREATE LOGIN</c>, keyed
    /// by name. Empty by default, in which case the TDS endpoint
    /// (<see cref="ListenLocalAsync(int, CancellationToken)"/>) accepts any credentials; once at least one
    /// login exists, the endpoint enforces LOGIN7 credentials against this
    /// registry (mismatch → Msg 18456 and disconnect). In-process
    /// <see cref="CreateDbConnection"/> sessions never authenticate — that's
    /// how logins are seeded. <c>ALTER LOGIN … WITH PASSWORD</c> replaces the
    /// (immutable) entry wholesale so concurrent endpoint reads see a
    /// consistent hash.
    /// </summary>
    internal readonly ConcurrentDictionary<string, ServerLogin> Logins = new(BuiltInToken.Comparer);

    /// <summary>
    /// Per-Simulation monotonic counter for user server-principal ids (created
    /// logins + custom server roles). Seeded at 257 so the first allocation
    /// returns 258 — ids 1 / 2 are the synthetic <c>sa</c> / <c>public</c> rows,
    /// 3–20 are the fixed server roles (<see cref="FixedServerRoles"/>), and real
    /// SQL Server allocates user server principals past that reserved block
    /// (observed 258+).
    /// </summary>
    private int nextPrincipalId = 257;

    /// <summary>
    /// Allocates the next server-principal id for a freshly-created
    /// <see cref="ServerLogin"/> or custom server role. <c>ALTER LOGIN</c>
    /// preserves the existing id rather than allocating a new one.
    /// </summary>
    internal int AllocatePrincipalId() => Interlocked.Increment(ref this.nextPrincipalId);

    /// <summary>
    /// Stable install-time timestamp used as the <c>create_date</c> /
    /// <c>modify_date</c> of the synthetic fixed <c>sys.server_principals</c>
    /// rows (<c>sa</c> / <c>public</c>) and as <c>sys.dm_os_sys_info</c>'s
    /// <c>sqlserver_start_time</c>. Captured once at construction, exactly
    /// as each <see cref="Database"/> seeds its fixed database principals' dates
    /// from a single <c>DateTime.UtcNow</c>.
    /// </summary>
    internal readonly DateTime SeedDate = DateTime.UtcNow;

    /// <summary><c>Environment.TickCount64</c> at construction, <c>sys.dm_os_sys_info</c>'s <c>sqlserver_start_time_ms_ticks</c>.</summary>
    internal readonly long StartTicks = Environment.TickCount64;

    /// <summary>
    /// System tables (e.g. <c>systypes</c>). Materialized once per process and
    /// shared across all <see cref="Simulation"/> instances; the bytes are
    /// immutable.
    /// </summary>
    internal static FrozenDictionary<string, HeapTable> SystemHeapTables => BuiltInResources.SystemHeapTables.Value;

    /// <summary>
    /// Global temp tables (<c>##foo</c>) — instance-wide, visible to every
    /// connection on this <see cref="Simulation"/>. Created by any session via
    /// <c>CREATE TABLE ##foo</c>; the creating connection is stamped on
    /// <see cref="HeapTable.OwnerSession"/> and used by
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
    internal static FrozenDictionary<string, CatalogView> CatalogViews => BuiltInResources.CatalogViews.Value;

    private long transactionCommitCounter;

    /// <summary>
    /// Serializes committing transactions' version-store stamping: the
    /// committer draws <see cref="NextTransactionCommitId"/>, stamps every row
    /// it wrote, and only then publishes the id with
    /// <see cref="PublishTransactionCommitId"/>, all under this lock. A
    /// snapshot taken while the stamps land still reads the old counter, so it
    /// sees none of the transaction's rows; one taken after sees them all.
    /// Publishing first would let a snapshot read a stamp whose rows are still
    /// marked in flight, see them hidden, and see them appear on its next read.
    /// Ordered before the tables' version gates and nothing else.
    /// </summary>
    internal readonly Lock CommitGate = new();

    /// <summary>
    /// The commit id the next committing transaction stamps its rows with,
    /// read under <see cref="CommitGate"/>. Monotonic, <b>instance-scoped</b>,
    /// never reused — one sequence for every database, mirroring real SQL
    /// Server's server-wide transaction sequence number (its version store
    /// lives in <c>tempdb</c>, not per database). Instance scope is what makes
    /// a snapshot stamp comparable across databases: a SNAPSHOT transaction
    /// fixes one stamp at its first data-access statement and reads <em>every</em>
    /// database as of that instant (probe-confirmed against SQL Server 2025 —
    /// a transaction whose first read was in one database still sees the
    /// pre-update state of another it reads later). Each committing transaction
    /// takes one stamp however many databases it wrote to. The counter starts
    /// at zero so the implicit "Xmin = 0" for rows that pre-date the first
    /// SI / RCSI read is visible to any snapshot.
    /// </summary>
    internal long NextTransactionCommitId => this.transactionCommitCounter + 1;

    /// <summary>Makes <paramref name="commitId"/>, whose rows are stamped, the current stamp; under <see cref="CommitGate"/>.</summary>
    internal void PublishTransactionCommitId(long commitId) => Interlocked.Exchange(ref this.transactionCommitCounter, commitId);

    private long transactionIdCounter;

    /// <summary>
    /// Draws the next id from the server-wide counter <c>CURRENT_TRANSACTION_ID()</c>
    /// and the transaction DMVs report: one per user transaction at its BEGIN,
    /// and one per autocommit statement that asks.
    /// </summary>
    internal long AllocateTransactionId() => Interlocked.Increment(ref this.transactionIdCounter);

    /// <summary>
    /// The last transaction id allocated server-wide; a transaction whose id
    /// exceeds a value read here began after the read.
    /// </summary>
    internal long LastAllocatedTransactionId => Volatile.Read(ref this.transactionIdCounter);

    private long tempTableCounter;

    /// <summary>
    /// The name a local <c>#temp</c> table carries inside <c>tempdb</c>, which
    /// the messages naming the table (Msg 515, Msg 2628) spell out: the written
    /// name padded with underscores to 116 characters, then twelve hex digits
    /// of a server-wide counter that every local temp table's creation
    /// advances, 128 characters in all (probed 2026-09-28 against SQL Server
    /// 2025). Real's counter carries the instance's history, so the digits
    /// match its shape, not its value.
    /// </summary>
    internal string AllocateTempTableInternalName(string name) =>
        $"{name.PadRight(116, '_')}{Interlocked.Increment(ref this.tempTableCounter):X12}";

    private long tableVariableCounter;

    /// <summary>
    /// The name a table variable carries inside <c>tempdb</c>, which Msg 2628
    /// and its system-named constraints spell out: <c>#</c> and the eight hex
    /// digits of its negative object id (<c>#B9CBEB0A</c>), a fresh one for
    /// each declaration (probed 2026-09-28 against SQL Server 2025). Real
    /// draws the id from <c>tempdb</c>'s allocator, whose values carry the
    /// instance's history, so the digits match its shape, not its value.
    /// </summary>
    internal string AllocateTableVariableInternalName()
    {
        var sequence = (uint)Interlocked.Increment(ref this.tableVariableCounter);
        var objectId = 0x8000_0000u | ((0x2100_0000u + (sequence * 999_983u)) & 0x7FFF_FFFFu);
        return $"#{objectId:X8}";
    }

    /// <summary>
    /// Reads the current value of the commit-id counter without advancing it.
    /// Used to stamp a snapshot at first read under SNAPSHOT isolation and at
    /// each statement's first read under READ_COMMITTED_SNAPSHOT. Returning the
    /// latest committed stamp guarantees readers see every transaction that
    /// committed before the snapshot was taken.
    /// </summary>
    internal long CurrentTransactionCommitId => Interlocked.Read(ref this.transactionCommitCounter);

    /// <summary>
    /// Active SNAPSHOT-isolation transactions whose snapshot Xid is still
    /// load-bearing — every entry's <see cref="SimulatedDbTransaction.SnapshotXid"/>
    /// is non-null and the tx hasn't reached Commit / Rollback / Dispose yet.
    /// Populated by <see cref="Parser.BatchContext.ResolveSnapshotXidForRead"/>
    /// on first user-table read of an SI tx; drained by the corresponding
    /// finalization path. Read by the version-store GC to compute the oldest
    /// active snapshot Xid (HVs whose <c>Xmax &lt;= oldest_active</c> are safe
    /// to drop), and by <c>sys.dm_tran_active_snapshot_database_transactions</c>
    /// to enumerate per-session SI state. Instance-scoped alongside the commit
    /// counter: one stamp reaches every database, so a snapshot open anywhere
    /// pins history everywhere.
    /// </summary>
    /// <remarks>
    /// Keyed by <see cref="SessionToken"/> and valued by a copy of the three
    /// facts its readers need, rather than by the transaction: a transaction
    /// holds its connection (ADO.NET requires it to), so a simulation-wide
    /// dictionary of transactions would pin every session that ever opened a
    /// snapshot. One entry per session is exact — a session has at most one
    /// current transaction.
    /// </remarks>
    internal readonly ConcurrentDictionary<SessionToken, ActiveSnapshotRegistration> ActiveSnapshotTxs = new();

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
    /// Live session registry — one <see cref="SessionToken"/> per
    /// <see cref="SimulatedDbConnection"/>, added at construction and removed
    /// when the connection is disposed or its session reclaimed. The
    /// <c>sys.dm_tran_locks</c> / <c>sys.dm_os_waiting_tasks</c> DMVs and the
    /// <c>sp_who</c> family enumerate it to surface waiter rows (a waiter's
    /// state lives on its token's <see cref="SessionToken.WaitingOnResource"/>,
    /// not on the resource).
    /// </summary>
    /// <remarks>
    /// Tokens, not connections: holding the connection here would keep every
    /// abandoned one alive forever, which is the whole reason the state it
    /// leaked behind could never be reclaimed. A token weighs a handful of
    /// fields and reaches its connection only weakly.
    /// </remarks>
    internal readonly HashSet<SessionToken> Sessions = new(ReferenceEqualityComparer.Instance);

    /// <summary>When a LOB chain a write gave up may go to another row (see <see cref="Storage.LobReclamation"/>).</summary>
    internal readonly LobReclamation LobReclamation;

    /// <summary>Registers a connection's session at construction time.</summary>
    internal void RegisterConnection(SimulatedDbConnection connection)
    {
        lock (this.Sessions)
            _ = this.Sessions.Add(connection.Session);
    }

    /// <summary>Unregisters a session at dispose / reclaim time.</summary>
    internal void UnregisterConnection(SimulatedDbConnection connection)
    {
        lock (this.Sessions)
            _ = this.Sessions.Remove(connection.Session);
    }

    /// <summary>
    /// Snapshot of the sessions whose connection is still resolvable. The
    /// caller iterates the snapshot rather than the live set so concurrent
    /// open / dispose during enumeration is safe. A session whose connection
    /// has been collected but whose teardown hasn't been drained yet still
    /// resolves (its token's weak reference tracks resurrection), so a
    /// leaked session keeps reporting through <c>sp_who</c> for exactly as
    /// long as it keeps holding locks.
    /// </summary>
    internal SimulatedDbConnection[] SnapshotConnections()
    {
        SessionToken[] tokens;
        lock (this.Sessions)
            tokens = [.. this.Sessions];
        var live = new List<SimulatedDbConnection>(tokens.Length);
        foreach (var token in tokens)
        {
            if (token.TryResolveOwner() is { } connection)
                live.Add(connection);
        }
        return [.. live];
    }

    /// <summary>
    /// Sessions whose connection was finalized without a <c>Dispose</c>,
    /// waiting for a normal worker thread to tear them down. The connection's
    /// finalizer resurrects it into this queue rather than doing the work
    /// there: a teardown rolls a transaction back, releases locks and pulses
    /// the lock manager's gate, none of which belongs on the finalizer thread.
    /// </summary>
    private readonly ConcurrentQueue<SimulatedDbConnection> abandonedSessions = new();

    /// <summary>
    /// Called from <see cref="SimulatedDbConnection.Dispose(bool)"/>'s
    /// finalizer arm. Enqueuing <c>this</c> from a finalizer resurrects the
    /// object, which is the point — the teardown needs the session state the
    /// connection still holds (its open transaction, its session application
    /// locks, its <c>##temp</c> ownership).
    /// </summary>
    internal void EnqueueAbandonedSession(SimulatedDbConnection connection) =>
        this.abandonedSessions.Enqueue(connection);

    /// <summary>Whether a finalized session waits in the abandoned-session queue, read lock-free.</summary>
    internal bool HasAbandonedSessions => !this.abandonedSessions.IsEmpty;

    /// <summary>
    /// Test-observable: how many sessions this simulation has reclaimed.
    /// </summary>
    internal int SessionsReclaimed;

    /// <summary>
    /// Tears down every session whose connection was abandoned without a
    /// <c>Dispose</c>, and returns how many this call reclaimed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Reclamation runs the connection's own <c>Dispose</c> path, so an open
    /// transaction rolls back through the transaction's own machinery
    /// (releasing its locks, discarding its version-store entries, unpinning
    /// the snapshot), session application locks release through the same bulk
    /// release <c>Close</c> uses, <c>##global</c> temp tables the session owned
    /// drop under the same ownership rule, cursors deallocate, and the SPID
    /// retires from <see cref="Sessions"/>. Nothing here is a parallel
    /// implementation of any of that.
    /// </para>
    /// <para>
    /// A session that is mid-statement is skipped and re-queued. In practice
    /// the case can't arise — the executing thread's stack holds the
    /// connection, so it isn't collectable — but the check is what makes that
    /// an assertion rather than an assumption, and it costs one field read.
    /// It reads the session thread's own marker, which a parallel-aggregate
    /// worker never writes.
    /// </para>
    /// <para>
    /// Timing is nondeterministic, as real's is: real SqlClient's finalizer
    /// closes the socket at some GC's discretion and the server resets the
    /// session then. Here the trigger is the same GC plus the next call to
    /// this method — from a new connection, from a lock acquisition, or from
    /// the version-store collector.
    /// </para>
    /// </remarks>
    internal int ReclaimAbandonedSessions()
    {
        if (this.abandonedSessions.IsEmpty)
            return 0;
        // One sweep at a time. A teardown rolls its transaction back, which
        // runs the version-store collector, which sweeps — so without this the
        // drain would re-enter itself once per queued session and recurse as
        // deep as the queue is long. A concurrent caller declining is right
        // too: the sweep is opportunistic, and whoever is already in it will
        // reach the same entries.
        if (Interlocked.CompareExchange(ref this.reclaimInProgress, 1, 0) != 0)
            return 0;
        try
        {
            return this.DrainAbandonedSessions();
        }
        finally
        {
            Volatile.Write(ref this.reclaimInProgress, 0);
        }
    }

    private int reclaimInProgress;

    /// <summary>Body of <see cref="ReclaimAbandonedSessions"/>, run by the
    /// single sweeper that won the gate above.</summary>
    private int DrainAbandonedSessions()
    {
        var reclaimed = 0;
        // Bounded by the queue length observed on entry so a teardown that
        // re-queues a mid-statement session can't spin here.
        for (var remaining = this.abandonedSessions.Count; remaining > 0; remaining--)
        {
            // CA2000 wants every dequeued connection disposed on every path,
            // but two paths deliberately hand it back instead: one whose
            // session is already torn down, and one that is mid-statement and
            // gets re-queued. Disposing on either would be the bug.
#pragma warning disable CA2000
            if (!this.abandonedSessions.TryDequeue(out var connection))
                break;
#pragma warning restore CA2000
            if (connection.Session.Reclaimed)
                continue;
            if (connection.Session.CurrentExecutingThreadId is not null)
            {
                this.abandonedSessions.Enqueue(connection);
                continue;
            }
            // The session's own Close + Dispose, not a cleanup routine of its
            // own: an abandoned session has to end exactly the way a closed one
            // does, and the moment the two paths diverge one of them starts
            // leaking what the other releases.
            using (connection)
                connection.Close();
            reclaimed++;
        }
        _ = Interlocked.Add(ref this.SessionsReclaimed, reclaimed);
        return reclaimed;
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
    /// The cacheable catalog views' rows, kept across statements and indexed
    /// on demand; every <see cref="BumpSchemaVersion"/> invalidates it, as does
    /// each non-DDL statement that changes what one of those views projects.
    /// </summary>
    internal readonly CatalogRowCache CatalogRows = new();

    /// <summary>
    /// Set once any column in the simulation is declared or altered
    /// <c>MASKED</c>, and never cleared: until then a query's compile skips the
    /// Dynamic Data Masking walk over its projection outright
    /// (<see cref="Parser.DataMask.OfProjection"/>).
    /// </summary>
    internal volatile bool DeclaresDataMasks;

    /// <summary>
    /// The trace flags <c>DBCC TRACEON( …, -1)</c> turned on server-wide, which
    /// every session sees beside its own <see cref="SimulatedDbConnection.TraceFlags"/>.
    /// Guarded by locking the set itself.
    /// </summary>
    internal readonly HashSet<int> GlobalTraceFlags = [];

    /// <summary>Whether <paramref name="flag"/> is on server-wide.</summary>
    internal bool IsGlobalTraceFlagOn(int flag)
    {
        lock (this.GlobalTraceFlags)
            return this.GlobalTraceFlags.Contains(flag);
    }

    /// <summary>
    /// Increments <see cref="SchemaVersion"/>, signaling that any cached
    /// <see cref="Selection"/> parsed under the prior version is potentially
    /// stale, and invalidates <see cref="CatalogRows"/>. Called by the
    /// Create / Drop / Alter dispatch arm and by <c>ImportBacpac</c>.
    /// </summary>
    internal void BumpSchemaVersion()
    {
        _ = Interlocked.Increment(ref this.SchemaVersion);
        this.CatalogRows.Invalidate();
    }

    /// <summary>
    /// Allocates the next session id (SPID) for a freshly-constructed
    /// <see cref="SimulatedDbConnection"/>. Used to fill the <c>Process ID
    /// &lt;N&gt;</c> slot in Msg 1205 (deadlock victim) and to identify
    /// lock holders / waiters in any future <c>sys.dm_tran_locks</c> /
    /// <c>sys.dm_exec_sessions</c> projection.
    /// </summary>
    internal int AllocateSpid() => Interlocked.Increment(ref this.nextSpid);

    /// <summary>
    /// Backs <c>@@CONNECTIONS</c>: the count of sessions allocated since the
    /// <see cref="Simulation"/> was constructed. Derived from the SPID
    /// allocator's distance past its seed (50), so it advances on every
    /// <see cref="SimulatedDbConnection"/> without a separate counter. Real
    /// SQL Server reports cumulative login attempts since server start; the
    /// session-allocation count is the closest cheap in-process proxy.
    /// </summary>
    internal int ConnectionsAllocated => Volatile.Read(ref this.nextSpid) - 50;

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
        // Counted for the whole enumeration, which is what makes it a measure
        // of load rather than of arrival: a reader held open is a statement
        // still in flight. Child batches (proc / UDF / dynamic-SQL bodies)
        // don't re-enter here, so one execution counts once.
        _ = Interlocked.Increment(ref this.statementsInFlight);
        if (command.Connection is { } requester)
        {
            var session = requester.Session;
            session.RequestStartUtc = DateTime.UtcNow;
            session.BatchText = command.CommandText;
            session.StatementStartIndex = 0;
            requester.BeginCommand();
        }
        try
        {
            foreach (var outcome in this.CreateResultSetsForCommandCore(command, continueOnError))
                yield return outcome;
            if (command.Connection is { ScopesTransactionsToBatch: true } mars && EndBatchScopedTransaction(command, mars) is { } stillActive)
                yield return stillActive;
        }
        finally
        {
            _ = Interlocked.Decrement(ref this.statementsInFlight);
            if (command.Connection is { } finished)
                finished.EndCommand();
            // A cancelled execution has unwound by here, whichever safe point
            // saw the cancellation; what it leaves behind is the same for
            // every front door.
            if (command.Connection is { ExecutionCancellationRequested: true } cancelled)
                cancelled.SettleCancelledExecution();
            // An error that ends the session closes the connection once the
            // command has delivered it.
            if (command.Connection is { SessionEnding: true } ended)
            {
                ended.SessionEnding = false;
                ended.Close();
            }
        }
    }

    private int statementsInFlight;

    /// <summary>
    /// Rolls back a transaction a MARS batch began by SQL text and left open,
    /// answering Msg 3997 after a SQL batch; an RPC's — <c>sp_executesql</c>
    /// or a procedure call — goes silently, its Msg 266 having already said
    /// so (probed 2026-09-30 against SQL Server 2025). A transaction begun
    /// before the batch, through the API or by an earlier batch, is not the
    /// batch's to end, and nor is one a cancel left.
    /// </summary>
    private static SimulatedErrorOutcome? EndBatchScopedTransaction(SimulatedDbCommand command, SimulatedDbConnection connection)
    {
        if (connection.CurrentTransaction is not { } open
            || open.TransactionId <= connection.TransactionIdAtExecutionStart
            || connection.ExecutionCancellationRequested)
        {
            return null;
        }
        open.EndRollback();
        if (command.CommandType == CommandType.StoredProcedure || command.ScopeTempTablesToBatch)
            return null;
        var stillActive = SimulatedSqlException.MarsBatchTransactionStillActive();
        stillActive.ResolveDiagnostics(1, 0, "");
        connection.LastErrorNumber = stillActive.Number;
        return new SimulatedErrorOutcome(stillActive);
    }

    /// <summary>
    /// How many command executions are currently in flight across every
    /// session of this simulation. Read by the parallel grouped accumulation
    /// (<c>Selection.Execution.AggregateParallel.cs</c>) to size — or refuse —
    /// its fan-out: intra-query parallelism is the right trade for an engine
    /// with idle cores and the wrong one for a saturated engine, where the
    /// workers only take cores the other sessions were using. Measured both
    /// ways on the concurrent workload drivers.
    /// </summary>
    internal int StatementsInFlight => Volatile.Read(ref this.statementsInFlight);

    private IEnumerable<SimulatedStatementOutcome> CreateResultSetsForCommandCore(SimulatedDbCommand command, bool continueOnError)
    {
        // Fresh cancellation scope per top-level execution, so a Cancel() /
        // attention that fired against a previous command on this connection
        // doesn't bleed into this one. Child batches (proc / UDF / dynamic-SQL
        // bodies) don't re-enter here — they share this scope through the
        // connection, so an attention aborts them too.
        // CommandTimeout is seconds, 0 meaning infinite (the SqlClient
        // convention). The deadline is enforced at the engine's safe points,
        // so a batch aborts between statements / loop iterations / during a
        // WAITFOR, and inside a statement at each row a join level reads from
        // its left (Selection.ThrowIfExecutionCancelled).
        command.Connection?.BeginExecutionScope(
            command.CommandTimeout > 0 ? TimeSpan.FromSeconds(command.CommandTimeout) : null);

        // CommandType.StoredProcedure: CommandText is the procedure name and
        // Parameters maps by name to the proc's declared parameters. Bypass
        // the SQL-text parser and route directly to InvokeProcedure with the
        // parameter collection translated into ProcArguments. The procedure-
        // call entrypoint that hand-rolled SqlClient code uses.
        if (command.CommandType == CommandType.StoredProcedure)
        {
            foreach (var outcome in InvokeFromCommandTypeStoredProcedure(command, continueOnError))
                yield return outcome;
            yield break;
        }

        // Plan-cache fast path: a single-SELECT batch parsed once under the
        // current schema version replays without tokenizing or re-parsing.
        // Eligibility is gated by TryBuildPlanCacheKey (non-empty text, live
        // connection) and the entry's recorded schema version must match the
        // current one — a stale entry falls through to the standard dispatch
        // and overwrites itself on the way out (RunSelectStatement does the
        // inline promotion).
        var cacheKey = TryBuildPlanCacheKey(command);
        var schemaVersionAtStart = Volatile.Read(ref this.SchemaVersion);
        if (cacheKey is { } key
            && this.planCache.TryGetValue(key, out var entry)
            && entry.SchemaVersionAtParse == schemaVersionAtStart)
        {
            _ = Interlocked.Increment(ref this.PlanCacheHits);
            var stale = new System.Runtime.CompilerServices.StrongBox<bool>();
            foreach (var outcome in ReplayCachedSelections(command, entry, stale))
                yield return outcome;
            if (!stale.Value)
                yield break;
        }
        if (cacheKey is not null)
            _ = Interlocked.Increment(ref this.PlanCacheMisses);

        var batch = new BatchContext(command)
        {
            ContinueOnError = continueOnError,
            ForceTempTableScope = command.ScopeTempTablesToBatch,
            ApiServerCursor = command.ApiServerCursor,
        };
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
            batch.PlanCacheKey = prepared;
            batch.DmlPlans = this.dmlPlanSets.TryGetValue(prepared, out var dmlPlans) ? dmlPlans : null;
        }

        // A command carrying parameters is an ad-hoc scope, not a plain batch:
        // SqlClient sends one as an sp_executesql RPC, and SQL Server reverts
        // the SET options it changed when that scope returns (probe-confirmed —
        // `SET NOCOUNT ON` in a parameterized command doesn't reach the next
        // command, while the same text with no parameters does). EF Core's
        // modification batches open with `SET NOCOUNT ON` and rely on it.
        var adHocScope = command.ScopeTempTablesToBatch || command.Parameters.Count > 0;
        var enteredNoCount = batch.Connection.NoCount;
        var enteredOptions = new SimulatedDbConnection.SessionOptionScope(batch.Connection);
        var enteredTranCount = batch.Connection.CurrentTransaction?.TranCount ?? 0;
        try
        {
            // Under SET PARSEONLY ON the batch is checked for syntax alone and
            // nothing runs.
            if (ScanParseTimeOptions(command, batch))
            {
                if (this.CompileBatch(CompileContextFor(batch, command), key: null, out _) is { Class: 15 } syntaxError)
                {
                    batch.Connection.LastErrorNumber = syntaxError.Number;
                    yield return new SimulatedErrorOutcome(syntaxError);
                }
                yield break;
            }

            // Nothing runs when the batch doesn't compile; the error is the
            // batch's whole response, raised at ExecuteReader like real's.
            var compileContext = CompileContextFor(batch, command);
            StatementClock? compileClock = batch.Connection.StatisticsTime ? StatementClock.Start(batch.Connection) : null;
            if (this.CompileBatch(compileContext, cacheKey, out var inliningFailures) is { } compileError)
            {
                batch.Connection.LastErrorNumber = compileError.Number;
                yield return new SimulatedErrorOutcome(compileError);
                yield break;
            }
            batch.StatementsCompiledOnRun = compileContext.StatementsCompiledOnRun;
            foreach (var failure in CompileFailuresSent(batch, inliningFailures))
                yield return failure;
            if (compileClock is not null)
                yield return new SimulatedInfoOutcome(CompileTime(batch, compileClock, compileContext.LastTopLevelStatementLine, BatchCreatedModuleName(command) ?? batch.ErrorProcedureName));

            var context = batch.Parser;
            context.MoveNextOptional();
            var batchAborted = false;
            foreach (var outcome in DispatchStatementsUntil(batch, endKeyword: null))
            {
                batchAborted |= outcome is SimulatedErrorOutcome { Exception: var ended } && EndsBatch(ended);
                yield return outcome;
            }
            // A transaction an XACT_ABORT-caught error doomed can survive to
            // here: the CATCH ran, the batch carried on, and nothing rolled it
            // back. Real ends such a batch by rolling back and reporting
            // Msg 3998 after the batch's own results (probe-confirmed).
            foreach (var message in DrainPendingMessages(batch.Connection))
                yield return message;
            if (batch.Connection.CurrentTransaction is { Doomed: true } doomed)
            {
                doomed.EndRollback();
                var endOfBatch = SimulatedSqlException.UncommittableTransactionAtEndOfBatch();
                endOfBatch.ResolveDiagnostics(1, batch.LineOffset, batch.ErrorProcedureName);
                yield return new SimulatedErrorOutcome(endOfBatch);
            }
            // An sp_executesql scope, like a procedure's, that ends with
            // @@TRANCOUNT other than it began with answers Msg 266 after its
            // results (probed 2026-09-30 against SQL Server 2025); a plain
            // batch is judged by nothing, a batch an error aborted (Msg 3609
            // from a trigger's rollback) says no more, and
            // IMPLICIT_TRANSACTIONS exempts.
            if (adHocScope
                && !batchAborted
                && !batch.Connection.ImplicitTransactions
                && !batch.Connection.ExecutionCancellationRequested
                && (batch.Connection.CurrentTransaction?.TranCount ?? 0) is var exitTranCount
                && exitTranCount != enteredTranCount)
            {
                var mismatch = SimulatedSqlException.TransactionCountMismatch(enteredTranCount, exitTranCount, "");
                batch.Connection.LastErrorNumber = mismatch.Number;
                yield return new SimulatedErrorOutcome(mismatch);
            }
            WriteBackOutputParameters(batch);
        }
        finally
        {
            if (adHocScope)
            {
                batch.Connection.NoCount = enteredNoCount;
                enteredOptions.Restore(batch.Connection);
            }
            // Whatever a consumer that stopped early left unread goes with it.
            batch.Connection.PendingMessages.Clear();
            // An RPC ad-hoc statement (sp_executesql / sp_execute / sp_prepexec)
            // runs in a nested scope, so temp tables it created are dropped when
            // it finishes — the tedious `execSql` re-run-without-Msg-2714 case.
            batch.DropScopedTempTables();
            // LOCAL cursors + cursor variables are frame-scoped: implicitly
            // deallocated when the batch ends (GLOBAL cursors persist on the
            // connection). Releases any SCROLL_LOCKS locks the deallocated
            // cursors held.
            TeardownFrameCursors(batch);
        }
    }

    /// <summary>
    /// Settles the two options a top-level batch applies while it parses, and
    /// so for the whole batch whatever the order its statements run in: the
    /// last <c>SET PARSEONLY</c> the batch holds decides whether any of it
    /// runs — statements ahead of it included — and becomes the session's,
    /// and the last <c>SET QUOTED_IDENTIFIER</c> (or <c>ANSI_DEFAULTS</c>) is
    /// what the batch's <c>@@OPTIONS</c> reads, statements ahead of it included
    /// (probed 2026-09-28 against SQL Server 2025). A batch creating a module
    /// holds no batch-level SET — its body's are the module's. The scan runs
    /// only for a batch whose text names one of the options.
    /// </summary>
    /// <returns>Whether <c>SET PARSEONLY</c> leaves the batch unrun.</returns>
    private static bool ScanParseTimeOptions(SimulatedDbCommand command, BatchContext batch)
    {
        var connection = command.Connection!;
        var text = command.CommandText;
        if (!text.Contains("PARSEONLY", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("QUOTED_IDENTIFIER", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("ANSI_DEFAULTS", StringComparison.OrdinalIgnoreCase))
        {
            return connection.ParseOnly;
        }

        // A bare context: the batch's own seeds its parameters, table-valued
        // ones included, which a scan mustn't consume.
        var scan = new BatchContext(command, new Dictionary<string, VariableSlot>(BatchContext.VariableNameComparer)).Parser;
        List<string> names = [];
        var quotedIdentifiers = connection.QuotedIdentifiers;
        try
        {
            if (!scan.MoveNext() || scan.Token is ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter })
                return connection.ParseOnly;
            do
            {
                if (scan.Token is not ReservedKeyword { Keyword: Keyword.Set })
                    continue;
                names.Clear();
                while (scan.MoveNext() && scan.Token is UnquotedString { Value: var name })
                {
                    names.Add(name);
                    if (!scan.MoveNext() || scan.Token is not Operator { Character: ',' })
                        break;
                }
                if (scan.Token is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off } value)
                    continue;
                foreach (var name in names)
                {
                    if (name.Equals("PARSEONLY", StringComparison.OrdinalIgnoreCase))
                        connection.ParseOnly = value.Keyword == Keyword.On;
                    else if (IsQuotedIdentifierOption(name))
                        quotedIdentifiers = value.Keyword == Keyword.On;
                }
            }
            while (scan.MoveNext());
        }
        catch (SimulatedSqlException)
        {
            // A text that doesn't tokenize reports that from the compile.
        }
        batch.QuotedIdentifiersAfterParse = quotedIdentifiers;
        return connection.ParseOnly;
    }

    // Parses a SELECT statement's query, as a browse statement when asked.
    private static Selection ParseSelectStatement(ParserContext context, bool browse)
    {
        using var browseStatement = ParserScope.Enter(ref context.BrowseStatement, browse);
        using var blocked = ParserScope.Enter(ref context.SimpleParameterizationBlocked, false);
        using var separatorRefusal = ParserScope.Enter(ref context.ParameterizedSeparatorRefusal, null);
        var subqueriesBefore = context.SubqueriesParsed;
        var selection = Selection.Parse(context, QueryScope.Statement);
        // Simple parameterization turns a literal a STRING_AGG separator casts
        // into a parameter, which is no longer the literal the separator has
        // to be: real's Msg 8733 for a statement it parameterizes, which is an
        // ad hoc one, never a module's.
        if (context.ParameterizedSeparatorRefusal is { } refusal
            && selection.SimplyParameterizable && !context.SimpleParameterizationBlocked && context.SubqueriesParsed == subqueriesBefore
            && RunsAdHoc(context.Batch))
        {
            throw refusal;
        }
        return selection.AsStatementResult();
    }

    /// <summary>
    /// Whether <paramref name="batch"/> is an ad hoc batch — a client's or
    /// dynamic SQL's — rather than a module body, whose statements real never
    /// parameterizes.
    /// </summary>
    private static bool RunsAdHoc(BatchContext batch) =>
        string.IsNullOrEmpty(batch.ErrorProcedureName) && batch.UdfFrame is null && batch.TriggerFrame is null && !batch.CalledFunctionBody
        && batch.ProcFrame is not { IsDynamicSql: false };

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
    private IEnumerable<SimulatedStatementOutcome> InvokeFromCommandTypeStoredProcedure(SimulatedDbCommand command, bool continueOnError)
    {
        // Build a transient outer batch to host the call. This batch's
        // variable dict isn't used for parameter binding (procs read from
        // their child batch's own variable dict, seeded from boundValues);
        // but it's the home for the temporary writeback slots. The body runs
        // on past a statement-terminating error as one a batch's EXEC calls
        // does (probed 2026-09-28 against SQL Server 2025 over RPC).
        var batch = new BatchContext(command) { ContinueOnError = continueOnError };
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

        // Synonym targets expand to their base name here for the same reason
        // the EXEC-statement path does — see ParseExec, including carrying the
        // synonym through as the EXECUTE check's securable.
        var rpcSynonym = batch.TryResolveSynonym(procName, out var resolvedSynonym) ? resolvedSynonym : null;
        var writtenName = procName.ToString();
        procName = batch.ExpandSynonym(procName);
        if (!batch.TryResolveProcedure(procName, out var procedure))
        {
            // Called by RPC, the miss is attributed to the name at line 1
            // (probed 2026-09-28 against SQL Server 2025).
            var missing = SimulatedSqlException.CouldNotFindStoredProcedure(procName.ToString());
            missing.PreserveDiagnostics(1, procName.ToString());
            throw missing;
        }

        // Translate each non-ReturnValue parameter into a ProcArgument. The
        // ReturnValue-direction parameter (at most one) gets pulled out and
        // its writeback happens after the call completes.
        var arguments = new List<ProcArgument>();
        var returnValueWriteback = (DbParameter?)null;
        for (var argumentIndex = 0; argumentIndex < command.Parameters.Count; argumentIndex++)
        {
            var parameter = command.Parameters[argumentIndex];
            if (parameter.Direction is ParameterDirection.ReturnValue)
            {
                returnValueWriteback = parameter;
                continue;
            }

            var pname = parameter.ParameterName.StartsWith('@') ? parameter.ParameterName[1..] : parameter.ParameterName;

            // Native DB-Lib RPC (pymssql / FreeTDS and other DB-Library
            // clients) sends procedure arguments positionally with empty
            // names; an empty name binds by position (null ProcArgument name),
            // a supplied name binds by name — the same positional/named split
            // EXEC's arguments take. The output-slot dictionary key falls back
            // to the ordinal when the name is empty so writeback stays unique.
            var argumentName = pname.Length == 0 ? null : pname;

            // Table-valued parameters are already materialized into the batch's
            // TableVariables by the BatchContext ctor (the Structured-parameter
            // seeding). Bind the live clone as a TVP argument by name — the same
            // ProcArgument shape EXEC's `@local_t` table-variable arg produces.
            if (parameter is SimulatedDbParameter structured
                && BatchContext.IsTableValuedParameterValue(structured)
                && batch.TableVariables.TryGetValue(pname, out var tvpClone))
            {
                arguments.Add(new ProcArgument(argumentName, isDefault: false, value: SqlValue.Null(SqlType.Int32), outputSlot: null, tableValue: tvpClone));
                continue;
            }

            // A parameter carrying a pre-built SqlValue (the TDS listener's
            // CLR-UDT / sql_variant RPC parameters) binds it verbatim, its own
            // type driving the argument and any output slot; otherwise the
            // declared DbType converts the CLR value.
            SqlType dbType;
            SqlValue value;
            if (parameter.Value is SqlValue preBuilt)
            {
                dbType = preBuilt.Type;
                value = preBuilt;
            }
            else
            {
                dbType = SqlType.GetByDbType(parameter.DbType);
                // Size = -1 is SqlClient's MAX-typed declaration; without the
                // promotion the slot's declared type is the bounded variant,
                // which (among other things) exempts it from the TEXTSIZE
                // output-parameter clip keyed off MAX-declared types.
                if (parameter.Size == SqlType.MaxLengthSentinel && BatchContext.AsMaxVariant(dbType) is { } maxVariant)
                    dbType = maxVariant;
                value = parameter.Value is null or DBNull ? SqlValue.Null(dbType) : dbType.ConvertParameter(parameter.Value);
            }

            // For OUTPUT-direction parameters we need a live VariableSlot in
            // the outer batch so InvokeProcedure can write back through it.
            // The slot's DeclaredType drives the coercion on writeback.
            VariableSlot? outputSlot = null;
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                outputSlot = new VariableSlot(dbType, declaredMaxLength: null, value, parameter);
                batch.Variables[OutputSlotKey(argumentIndex, pname)] = outputSlot;
            }
            arguments.Add(new ProcArgument(argumentName, isDefault: false, value, outputSlot));
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

        var attributionName = rpcSynonym is null ? writtenName : $"{procedure.Schema.Name}.{procedure.Name}";
        foreach (var outcome in InvokeProcedure(batch, procedure, arguments, returnCodeVarName, attributionName, rpcSynonym))
            yield return outcome;

        // Output param writeback: the per-argument OutputSlot.Value was
        // updated by InvokeProcedure. Copy back to each DbParameter.Value. The
        // slot key must match the binding side — position-derived for the
        // empty-named positional (DB-Lib) params, name-derived otherwise.
        for (var argumentIndex = 0; argumentIndex < command.Parameters.Count; argumentIndex++)
        {
            var parameter = command.Parameters[argumentIndex];
            if (parameter.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
            {
                var pname = parameter.ParameterName.StartsWith('@') ? parameter.ParameterName[1..] : parameter.ParameterName;
                if (batch.Variables.TryGetValue(OutputSlotKey(argumentIndex, pname), out var slot))
                {
                    var value = TextSizeCursor.Apply(slot.Value, slot.DeclaredType, batch.Connection.TextSize);
                    if (parameter is SimulatedDbParameter simulated)
                        simulated.OutputSqlValue = value;
                    parameter.Value = value.IsNull ? DBNull.Value : value.ToObject();
                }
            }
        }

        // ReturnValue writeback: the .rc slot was written by InvokeProcedure.
        if (returnValueWriteback is not null && batch.Variables.TryGetValue(".rc", out var rcSlot))
            returnValueWriteback.Value = rcSlot.Value.IsNull ? DBNull.Value : rcSlot.Value.ToObject();

        // Output-slot key: the declared name for a named argument, else an
        // ordinal-derived sentinel (positional DB-Lib params share the empty
        // name, so keying by name alone would collide). Leading-dot sentinels
        // can't collide with a real @-variable name.
        static string OutputSlotKey(int argumentIndex, string parameterName) =>
            parameterName.Length == 0 ? ".pos" + argumentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture) : parameterName;
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
        // The RPC is the procedure's scope, so the scope markers the
        // synthesized EXEC puts around it drop out.
        var depth = 0;
        foreach (var outcome in DispatchStatementsUntil(childBatch, endKeyword: null))
        {
            if (outcome is SimulatedProcScopeBoundary boundary)
            {
                depth += boundary.IsEnter ? 1 : -1;
                if (depth == (boundary.IsEnter ? 1 : 0))
                    continue;
            }
            yield return outcome;
        }
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
    /// Whether the <c>UPDATE</c> under the cursor opens <c>UPDATE STATISTICS</c>;
    /// on true the cursor has moved onto <c>STATISTICS</c>, otherwise it stays.
    /// </summary>
    private static bool IsUpdateStatistics(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        if (context.GetNextOptional() is ReservedKeyword { Keyword: Keyword.Statistics })
            return true;
        context.RestoreCheckpoint(checkpoint);
        return false;
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
        // A bare object name is an implicit EXECUTE only as the literal first
        // statement of a batch (probe-confirmed against SQL Server 2025:
        // `sp_who` works, `SELECT 1; sp_who` and even a leading `;sp_who` raise
        // Msg 102). Only top-level batches (endKeyword is null) qualify — never
        // a BEGIN…END block's first statement; a leading `;` or any dispatched
        // statement clears it. A module body isn't a batch of its own: its
        // CREATE is the batch's first statement, so a bare name opening the
        // body is Msg 102 too (probed 2026-09-25 for a procedure and a
        // trigger). A dynamic-SQL string is a batch of its own, so
        // `EXEC (N'sp_who')` and `sp_executesql N'sp_who'` qualify (probed
        // 2026-09-25).
        var atBatchStart = endKeyword is null && batch.ProcFrame is not { IsDynamicSql: false } && batch.TriggerFrame is null && batch.UdfFrame is null;
        // BEGIN...END block dispatch (endKeyword=End) bumps BlockDepth so the
        // must-be-first-statement check on CREATE/ALTER
        // PROCEDURE / FUNCTION / VIEW / TRIGGER / SCHEMA rejects them inside
        // the block. Top-level (endKeyword=null) doesn't bump — that's the
        // case where the first statement may legitimately be CREATE PROC.
        var nestedBlock = endKeyword is not null;
        if (nestedBlock)
        {
            batch.BlockDepth++;
            batch.DispatchLoopDepth++;
        }
        else
        {
            ScanBatchLabels(batch);
        }

        try
        {
            // A GOTO that is the batch's last statement still jumps.
            while (context.Token is not null || batch.PendingGotoLabel is not null)
            {
                // A GOTO is serviced by the innermost dispatch loop the label
                // is inside — the one whose BEGIN…END nesting it shares or
                // sits below. Every loop deeper than that unwinds first, the
                // way they unwind for RETURN, so a jump out of a block or a
                // loop body works and a jump to a label in the same body
                // doesn't leave the body.
                if (batch.PendingGotoLabel is { } gotoTarget)
                {
                    var label = batch.Labels[gotoTarget];
                    if (nestedBlock && label.BlockDepth < batch.DispatchLoopDepth)
                        yield break;
                    context.RestoreCheckpoint(label.Checkpoint);
                    // Jumping *into* blocks means their opening BEGIN never
                    // ran; their closing ENDs still have to be stepped over.
                    batch.PendingBlockEnds = label.BlockDepth - batch.DispatchLoopDepth;
                    batch.PendingGotoLabel = null;
                    continue;
                }

                if (batch.PendingBlockEnds > 0 && context.Token is ReservedKeyword { Keyword: Keyword.End })
                {
                    batch.PendingBlockEnds--;
                    context.MoveNextOptional();
                    continue;
                }

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

                // Client attention (TDS cancel / CommandTimeout) or an
                // in-process Cancel() aborts the batch at the statement
                // boundary: remaining statements are abandoned. Real SQL
                // Server aborts a cancelled batch at the next safe point;
                // between statements is one. The transaction is left intact
                // here (XACT_ABORT ON rollback is applied by the caller once
                // the batch has unwound).
                if (batch.CancelledAtStatementBoundary())
                    yield break;

                if (endKeyword is Keyword end && context.Token is ReservedKeyword rk && rk.Keyword == end)
                    yield break;

                if (context.Token is Operator { Character: ';' })
                {
                    requireSemicolonBeforeCte = false;
                    atBatchStart = false;
                    context.MoveNextOptional();
                    continue;
                }

                if (context.Token is not { } statementToken)
                    yield break;
                var statementStartIndex = statementToken.StartIndex;
                if (!nestedBlock && batch.Connection.StatisticsTime)
                    batch.LastTopLevelStatementLine = IsBeginTry(context) ? -1 : statementToken.LineNumber;
                foreach (var outcome in DispatchOneStatement(batch, requireSemicolonBeforeCte, atBatchStart))
                    yield return outcome;
                requireSemicolonBeforeCte = true;
                atBatchStart = false;
                batch.HasDispatchedStatement = true;
                if (!nestedBlock)
                    batch.TopLevelStatementsDispatched++;

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
            {
                batch.BlockDepth--;
                batch.DispatchLoopDepth--;
            }
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
    private IEnumerable<SimulatedStatementOutcome> DispatchOneStatement(BatchContext batch, bool requireSemicolonBeforeCte, bool atBatchStart) =>
        batch.BindErrors is { } report
            ? DetachedFrom(batch, report, this.DispatchFramedStatement(batch, requireSemicolonBeforeCte, atBatchStart))
            : this.DispatchFramedStatement(batch, requireSemicolonBeforeCte, atBatchStart);

    /// <summary>
    /// Runs a statement nested in one being read for its whole bind error
    /// report — an <c>IF</c> or <c>WHILE</c> body — with the report set aside:
    /// the nested statement reports for itself.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> DetachedFrom(BatchContext batch, BindErrorReport report, IEnumerable<SimulatedStatementOutcome> nested)
    {
        batch.BindErrors = null;
        try
        {
            foreach (var outcome in nested)
                yield return outcome;
        }
        finally
        {
            batch.BindErrors = report;
        }
    }

    /// <summary>Whether the parser sits on a plain <c>BEGIN … END</c> block,
    /// which real doesn't count as a statement it ran.</summary>
    private static bool AtBareBeginBlock(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Begin })
            return false;
        var checkpoint = context.SaveCheckpoint();
        var next = context.GetNextOptional();
        context.RestoreCheckpoint(checkpoint);
        return next is not (ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed }
            or UnquotedString { ContextualKeyword: ContextualKeyword.Try or ContextualKeyword.Catch or ContextualKeyword.Atomic });
    }

    /// <summary>
    /// The body of <see cref="DispatchOneStatement"/>: drives the statement
    /// through its <see cref="StatementLifecycle"/>, then sends what its
    /// <see cref="StatementEnding"/> owes.
    /// </summary>
    private IEnumerable<SimulatedStatementOutcome> DispatchFramedStatement(BatchContext batch, bool requireSemicolonBeforeCte, bool atBatchStart)
    {
        var lifecycle = new StatementLifecycle(batch, atBatchStart);
        lifecycle.Enter(batch);
        List<SimulatedStatementOutcome> outcomes = [];
        lifecycle.Run(this, batch, outcomes, requireSemicolonBeforeCte, atBatchStart);
        // A statement compiling as it runs sends its inlining failures ahead of
        // everything it sends, unless the compile itself failed — a binder
        // error stops real before any call inlines.
        if (lifecycle.CompiledOnRun is { } compiledOnRun
            && !(lifecycle.Error is { RaisedRunningFunctionBody: false } compileError && IsDeferredCompileError(compileError))
            && this.InliningFailuresOf(batch, compiledOnRun) is { } failures)
        {
            outcomes.InsertRange(0, failures.Select(failure => new SimulatedErrorOutcome(failure.Failure, raisedWhileCompiling: true)));
        }

        if (lifecycle.QueryStore is { } capture && lifecycle.Ending is not (StatementEnding.Deferred or StatementEnding.GatheredBindError))
            EndFramedQueryStoreCapture(batch, capture, lifecycle.QueryStoreIo, lifecycle.Error, lifecycle.StatementStart);

        switch (lifecycle.Ending)
        {
            case StatementEnding.Propagated:
                foreach (var outcome in lifecycle.EndPropagated(batch, outcomes))
                    yield return outcome;
                ExceptionDispatchInfo.Throw(lifecycle.Error!);
                yield break;
            case StatementEnding.Deferred:
            case StatementEnding.GatheredBindError:
                foreach (var outcome in lifecycle.EndSkipped(batch, outcomes))
                    yield return outcome;
                yield break;
            case StatementEnding.Continued:
                foreach (var outcome in lifecycle.EndContinued(batch, outcomes))
                    yield return outcome;
                yield break;
            case StatementEnding.Caught:
                foreach (var outcome in lifecycle.EndCaught(batch, outcomes))
                    yield return outcome;
                yield break;
        }

        // A completed statement's ending is sent here rather than through an
        // iterator of its own, which every statement would allocate.
        var connection = batch.Connection;
        lifecycle.Complete(batch, outcomes);

        // A statement calling a user function compiles it as it runs, and a
        // simply parameterized one compiles its parameterized form, which
        // STATISTICS TIME reports ahead of the statement's output (probed
        // 2026-09-28 against SQL Server 2025).
        if (lifecycle.TimedKind is not null && connection.StatisticsTime && (batch.CurrentStatement.CallsUserFunction || CompilesParameterized(batch, lifecycle.StatementStart)))
            yield return new SimulatedInfoOutcome(CompileTime(batch, clock: null, batch.CurrentStatement.StartLine + batch.LineOffset, batch.ErrorProcedureName));
        foreach (var outcome in ProducedOutcomes(batch, outcomes))
            yield return outcome;

        // Msg 8153 follows the rows of the statement whose aggregate dropped a
        // NULL. Cleared once sent, since an enclosing IF / BEGIN…END shares
        // this statement frame and would otherwise send it again.
        if (batch.CurrentStatement.NullEliminated)
        {
            batch.CurrentStatement.NullEliminated = false;
            if (connection.AnsiWarnings)
                yield return NullEliminatedWarning(batch);
        }
        foreach (var notice in ArithmeticNotices(batch))
            yield return notice;
        // A statement that turns STATISTICS TIME on or off reports no time,
        // having started or ended without it.
        var timed = lifecycle.TimedCall || (lifecycle.TimedKind is not null && batch.CurrentStatement.DoneKind != StatementDoneKind.NoDone);
        foreach (var notice in StatisticsReport(batch, lifecycle.StatementIo, outcomes, timed && connection.StatisticsTime, lifecycle.Clock, lifecycle.TimedCall, lifecycle.CreatedModule))
            yield return notice;
    }

    /// <summary>
    /// The Msg 3606 / 3607 an absorbed arithmetic fault owes the statement
    /// (<see cref="BatchContext.AbsorbsArithmeticFault"/>), in the order they
    /// occurred, after its rows. Cleared once sent, as Msg 8153 is.
    /// </summary>
    private static IEnumerable<SimulatedInfoOutcome> ArithmeticNotices(BatchContext batch)
    {
        var statement = batch.CurrentStatement;
        var overflow = statement.OwesOverflowNotice;
        var divideByZero = statement.OwesDivideByZeroNotice;
        statement.OwesOverflowNotice = statement.OwesDivideByZeroNotice = false;
        if (overflow)
            yield return new SimulatedInfoOutcome(SimulatedSqlException.ArithmeticOverflowOccurredMessage(batch), followsRows: true);
        if (divideByZero)
            yield return new SimulatedInfoOutcome(SimulatedSqlException.DivisionByZeroOccurredMessage(batch), followsRows: true);
    }

    /// <summary>
    /// What a statement produced, in the order real sends it: the messages it
    /// queued while it ran, what any trigger it fired sent, in the order the
    /// bodies ran (buffered on the batch because the DML executor has only one
    /// outcome to return), then its own outcomes. A statement that fails sends
    /// the same ahead of its error.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> ProducedOutcomes(BatchContext batch, List<SimulatedStatementOutcome> outcomes)
    {
        foreach (var message in DrainPendingMessages(batch.Connection))
            yield return message;
        if (batch.PendingTriggerOutcomes is { Count: > 0 } triggerOutcomes)
        {
            batch.PendingTriggerOutcomes = null;
            foreach (var outcome in triggerOutcomes)
                yield return outcome;
        }
        foreach (var outcome in outcomes)
            yield return outcome;
    }

    /// <summary>
    /// The count a row-writing statement reports when its error is caught by a
    /// <c>TRY</c> frame: 0, sent ahead of the <c>CATCH</c> block's own output
    /// and suppressed by <c>NOCOUNT</c> like any other, where an uncaught error
    /// ends the statement with no count at all (probed 2026-09-28 against SQL
    /// Server 2025). Each failing write on the way out reports its own, so an
    /// <c>INSERT</c> whose trigger's <c>UPDATE</c> failed reports two.
    /// A statement whose <c>OUTPUT</c> clause returns rows reports through the
    /// empty result set instead. Null for any other statement.
    /// </summary>
    private static SimulatedStatementOutcome? CaughtWriteCount(BatchContext batch)
    {
        var statement = batch.CurrentStatement;
        if (!statement.WritesRows)
            return null;
        SimulatedStatementOutcome count = statement.ClientOutputShape is var (schema, names)
            ? new SimulatedSqlResultSet(schema, names, Array.Empty<byte[]>(), 0) { EndedByError = true, ErrorCaught = true }
            : new SimulatedNonQuery(0);
        count.CountSuppressed = batch.Connection.NoCount;
        return count;
    }

    /// <summary>Real's Msg 8153, closing the statement whose aggregate skipped a NULL.</summary>
    private static SimulatedInfoOutcome NullEliminatedWarning(BatchContext batch) =>
        new(SimulatedSqlException.NullEliminatedMessage(batch), followsRows: true);

    /// <summary>
    /// The messages the engine has queued so far, in order, as outcomes to
    /// yield. See <see cref="SimulatedDbConnection.PendingMessages"/>.
    /// </summary>
    private static SimulatedInfoOutcome[] DrainPendingMessages(SimulatedDbConnection connection)
    {
        var queue = connection.PendingMessages;
        if (queue.Count == 0)
            return [];
        var drained = new SimulatedInfoOutcome[queue.Count];
        for (var i = 0; i < drained.Length; i++)
            drained[i] = new SimulatedInfoOutcome(queue.Dequeue());
        return drained;
    }

    /// <summary>
    /// Whether real follows <paramref name="error"/> with Msg 3621 ("The
    /// statement has been terminated."): it does when an execution error ends a
    /// statement that writes rows — a constraint, key or <c>CHECK OPTION</c>
    /// violation, a NULL into a NOT NULL column, a truncation, an arithmetic
    /// overflow or a divide by zero, a positioned update that found no row —
    /// from <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>MERGE</c>,
    /// <c>SELECT … INTO</c> or <c>ALTER TABLE … ALTER COLUMN</c> — and not for
    /// an error the statement's compilation raises (Msg 206, 213, 544), a
    /// <c>SELECT</c>'s own error, or one that ends the batch (probed
    /// 2026-09-23 against SQL Server 2025). Two exceptions to the last rule,
    /// probed 2026-09-24: an error that escaped a trigger body ends the firing
    /// statement first, so it says so even when the batch ends with it, and a
    /// unique index that finds duplicate keys (Msg 1505) earns it whether
    /// <c>CREATE INDEX</c> or a constraint built it, though it ends the batch,
    /// and a negative <c>TOP</c>'s Msg 127, which ends the batch as every
    /// run-time severity-15 error does, still ends a writing statement first
    /// (probed 2026-09-26). A CLR routine's throw (Msg 6522) earns it too,
    /// a CLR trigger's or a CLR type's <c>Parse</c> converting a written value,
    /// as does a CLR trigger's context connection ending the firing
    /// statement's transaction (Msg 6549, 3991, 3992; probed 2026-09-28), and so
    /// does a scalar subquery answering more than one row (Msg 512) inside a
    /// writing statement (probed 2026-09-28), and so does a positioned update
    /// or delete through a read-only cursor (Msg 16929; probed 2026-09-29) or
    /// naming a table the cursor doesn't update (Msg 16933; probed 2026-10-01),
    /// and so do the out-of-range conversions of a written value (Msg 242,
    /// 244, 248) and a date arithmetic overflow (Msg 517; probed 2026-10-01),
    /// and a lock timeout on a row or key (Msg 1222 at any state but the object
    /// lock's 56; probed 2026-10-03), and a trigger body's write refused in the
    /// unit a caught error doomed (Msg 3930; probed 2026-10-04).
    /// </summary>
    private static bool IsStatementTerminationNoticed(BatchContext batch, SimulatedSqlException error) =>
        error.Number is 1505 or 4457
        || error.EndedColumnRewrite
        || ((!batch.BatchAborted || error.EndedTriggerBody || error.Number == 127)
            && (batch.CurrentStatement.WritesRows || error.EndedFunctionWrite)
            && ((error.Number == 1222 && error.State != 56) || error.Number is 127 or 220 or 232 or 242 or 244 or 248 or 512 or 513 or 515 or 517 or 547 or 550 or 2601 or 2627 or 2628 or 3991 or 3992 or 6522 or 6549 or 8115 or 8134 or 4457 or 8152 or 8705 or 13921 or 16929 or 16931 or 16932 or 16933 or 16947
                || (error.Number == 208 && error.RaisedRunningFunctionBody)
                || (error.Number == 3930 && error.EndedTriggerBody)));

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
    /// sequences, XML schema collections), and for Msg 4902, an
    /// <c>ALTER TABLE … ADD</c> whose table doesn't exist yet: real defers that
    /// statement whole, so its column definitions' own compile errors (a
    /// precision past 38) wait behind the missing table (probed 2026-09-25).
    /// Msg 207 (invalid column on a resolvable table) is deliberately excluded
    /// — probe-confirmed that real SQL Server errors on it at compile time even
    /// in an un-taken branch, so it falls through to the batch-aborting path
    /// (<see cref="IsBatchAbortingNameResolution"/>).
    /// </remarks>
    private static bool IsDeferrableNameResolutionError(SimulatedSqlException ex)
        => ex.Number is 208 or 4902;

    /// <summary>
    /// Whether <paramref name="ex"/>, raised as a statement parses without
    /// running, waits for the statement to run: a missing object, or any
    /// error real's binder raises in a statement that reads an object that
    /// doesn't exist yet, which real binds only once it runs. Real's binder
    /// raises a few severity-15 errors too — Msg 107, 130, 145, 147, 164 and
    /// 4108 wait with their statement, where a parse-phase one such as Msg 174
    /// or 321 doesn't (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    private static bool DefersWithItsStatement(BatchContext batch, SimulatedSqlException ex)
        => IsDeferrableNameResolutionError(ex)
            || (batch.CurrentStatement.BindsDeferredSource && (IsBinderError(ex) || ex.Number is 107 or 130 or 145 or 147 or 157 or 164 or 4108));

    /// <summary>
    /// True when <paramref name="ex"/> is a statement-terminating error that
    /// ends the current statement but lets the batch continue to the next one
    /// (SQL Server's default severity model). Severity (<see cref="SimulatedSqlException.Class"/>)
    /// 11..16 are statement-terminating; severity ≤ 10 are informational (not
    /// raised as errors) and ≥ 17 are batch/connection-terminating — save a
    /// <c>RAISERROR</c> through 19, after which the batch carries on (probed
    /// 2026-09-28 against SQL Server 2025). Deadlock
    /// (Msg 1205, class 13) is the one in-range exception — it aborts the batch
    /// — so it is excluded. Consulted on every top-level batch
    /// (<see cref="BatchContext.ContinueOnError"/>).
    /// A syntax or bind error rarely reaches this: the batch compiles before it
    /// runs (<see cref="CompileBatch"/>), so only a statement that compile
    /// deferred raises one mid-batch.
    /// </summary>
    private static bool IsStatementTerminating(SimulatedSqlException ex)
        => (ex.Class is >= 11 and <= 16 || (ex.RaisedByRaiserror && ex.Class <= 19)) && ex.Number != 1205;

    /// <summary>
    /// Applies <c>SET XACT_ABORT ON</c>'s error promotion to
    /// <paramref name="ex"/> once, at the innermost dispatch frame that sees
    /// it. Probed against SQL Server 2025 across Msg 245 / 515 / 547 / 1222 /
    /// 2627 / 2628 / 8115 / 8134 and an uncaught <c>THROW</c>:
    /// <list type="bullet">
    /// <item>With no <c>TRY</c> frame open anywhere on the session, the whole
    /// transaction stack rolls back (<c>@@TRANCOUNT</c> 2 reads 0 afterwards,
    /// not 1) and the batch ends where the error was raised — including when
    /// the error came from inside a procedure body.</item>
    /// <item>With a <c>TRY</c> frame open, the <c>CATCH</c> runs and the batch
    /// carries on past it, but the transaction is left doomed: the same
    /// <c>@@TRANCOUNT</c>, <c>XACT_STATE()</c> reading <c>-1</c>, and Msg 3930
    /// on the next statement that would write.</item>
    /// <item><c>RAISERROR</c> is the exemption. Uncaught under the option it
    /// neither ends the batch nor touches the transaction; caught, it dooms the
    /// transaction like anything else, which is what
    /// <see cref="SimulatedSqlException.RaisedByRaiserror"/> encodes.</item>
    /// </list>
    /// Severity 15 is real's parse phase, which never reaches a transaction,
    /// and the two unconditional classes (deadlock victim, and the
    /// transaction-aborting errors) already rolled back above. An error
    /// marked <see cref="SimulatedSqlException.AbortsAsUnderXactAbort"/> takes
    /// this path with the option off too. An error raised inside a natively
    /// compiled module's atomic block ends the batch alone.
    /// </summary>
    private static void ApplyXactAbortPromotion(SimulatedDbConnection connection, SimulatedSqlException ex, bool changesTableStructure = false, int framesThatCannotCatch = 0)
    {
        // A structure-changing statement's own failure takes the same path,
        // save the two it raises while compiling (Msg 4902 / 2705), which end
        // the batch alone (probed 2026-09-26 against SQL Server 2025), and a
        // DROP TABLE a foreign key refuses (Msg 3726) outside a transaction,
        // after which the batch goes on (probed 2026-10-01). ALTER INDEX's
        // missing index (Msg 2727) does too, though its class is 11.
        var structuralFailure = changesTableStructure && (ex.Class == 16 || ex.Number == 2727) && ex.Number is not (4902 or 2705)
            && !(ex.Number == 3726 && connection.CurrentTransaction is null);
        // A divide by zero or an arithmetic overflow under ARITHABORT ON with
        // ANSI_WARNINGS OFF ends the batch and rolls the transaction back as
        // under XACT_ABORT, dooming it when caught, from a procedure body too
        // (probed 2026-09-28 against SQL Server 2025).
        var arithmeticAbort = connection.Arithabort && !connection.AnsiWarnings && ex.Number is 220 or 232 or 8115 or 8134 && !ex.IsIdentityOverflow;
        // An error inside a natively compiled module's atomic block rolls the
        // block back — its own transaction, or its savepoint in the caller's
        // (ParseBeginAtomicBlock) — and, uncaught, ends the batch, leaving the
        // caller's transaction committable (probed 2026-10-02 against SQL
        // Server 2025).
        if (connection.AtomicBlockDepth > 0 && !(connection.XactAbort || ex.AbortsAsUnderXactAbort || structuralFailure || arithmeticAbort)
            && !ex.XactAbortPromoted && !ex.AbortsTransaction && ex.Class is (>= 11 and <= 14) or 16 && ex.Number != 1205)
        {
            if (connection.OpenTryFrames - framesThatCannotCatch == 0 && !ex.RaisedByRaiserror)
                ex.XactAbortPromoted = true;
            return;
        }
        if (!(connection.XactAbort || ex.AbortsAsUnderXactAbort || structuralFailure || arithmeticAbort)
            || ex.XactAbortPromoted
            || ex.AbortsTransaction
            || ex.Class is not ((>= 11 and <= 14) or 16)
            || ex.Number == 1205)
        {
            return;
        }

        // Inside a SQLCLR routine the transaction held on entry is left ended
        // rather than rolled back — @@TRANCOUNT holds until the routine
        // returns, which settles it (probed 2026-09-28 against SQL Server
        // 2025).
        if (connection.ClrContext is { HasEntryTransaction: true } clrLevel && !ex.RaisedByRaiserror)
        {
            clrLevel.EntryTransactionEnded = true;
            if (connection.CurrentTransaction is { } ended)
                ended.Doomed = true;
            ex.XactAbortPromoted = true;
            return;
        }

        // A TRY in the scope a deferred compile error is raised in doesn't
        // catch it, so it rolls back unless a caller's TRY will (probed
        // 2026-10-02 against SQL Server 2025).
        if (connection.OpenTryFrames - framesThatCannotCatch > 0)
        {
            if (connection.CurrentTransaction is { } doomed)
                doomed.Doomed = true;
            else if (connection.TriggerStatementUndoLog is not null)
                connection.TriggerUnitDoomed = true;
            return;
        }

        // Uncaught, a severity-11 error — a DROP of a missing object's Msg
        // 3701 — leaves the batch and the transaction standing as RAISERROR
        // does, unless it is a structural one (probed 2026-10-02 against SQL
        // Server 2025); caught, it dooms the transaction like any other.
        if (ex.RaisedByRaiserror || (ex.Class == 11 && !structuralFailure && !ex.AbortsAsUnderXactAbort))
            return;
        ex.XactAbortPromoted = true;
        if (ex.DoomsWhenUncaught && connection.CurrentTransaction is { } doomedAtEnd)
        {
            doomedAtEnd.Doomed = true;
            return;
        }
        connection.CurrentTransaction?.EndRollback();
    }

    /// <summary>
    /// Raises Msg 3930 when the session's transaction has been doomed by an
    /// error caught under <c>SET XACT_ABORT ON</c>. Consulted where a statement
    /// would write to the log — every DML statement (through
    /// <see cref="RunMutation"/>, which covers the DDL that routes through it),
    /// the object DDL keyed on its leading keyword, <c>COMMIT</c> and
    /// <c>SAVE TRANSACTION</c>, plus the catalog writers whose leading token is
    /// neither: the <c>GRANT</c> / <c>REVOKE</c> / <c>DENY</c> family,
    /// <c>sp_rename</c> and the extended-property procedures. Reads pass
    /// through: a <c>SELECT</c> inside a doomed transaction answers normally
    /// (probe-confirmed), and so does a <c>DECLARE</c> or a <c>SET</c>.
    /// </summary>
    /// <remarks>
    /// The refusal comes <em>before</em> the statement's own name resolution —
    /// a <c>GRANT</c> on a missing object, an <c>sp_rename</c> of a missing one
    /// and an <c>sp_addextendedproperty</c> naming a missing table all report
    /// Msg 3930 rather than their own not-found error, which is the opposite of
    /// where the read-only gate sits for the latter two — and before that gate
    /// too: a doomed transaction in a read-only database reports 3930, not
    /// Msg 3906 (all probe-confirmed against SQL Server 2025, 2026-08-08).
    /// </remarks>
    private static void RejectWriteInDoomedTransaction(SimulatedDbConnection connection)
    {
        if (connection.CurrentTransaction is { Doomed: true } || (connection.CurrentTransaction is null && connection.TriggerStatementUndoLog is not null && connection.TriggerUnitDoomed))
            throw SimulatedSqlException.UncommittableTransactionCannotWrite();
    }

    /// <summary>
    /// True for an error real's binder raises and reports alongside the rest of
    /// a batch's binder errors: severity 16, and the severity-15 Msg 1087, which
    /// real gathers with them rather than letting it preempt the report the way
    /// a syntax error or an undeclared scalar variable does (probed 2026-09-24
    /// against SQL Server 2025). The severity-16 Msg 1710 preempts the report
    /// as a syntax error does (probed 2026-09-26).
    /// </summary>
    private static bool IsBinderError(SimulatedSqlException ex)
        => (ex.Class == 16 && ex.Number != 1710) || ex.Number == 1087;

    /// <summary>
    /// A bulk load's refusal of its permission or files — Msg 4834, 4860,
    /// 4861 — which, like a name-resolution miss, ends only the procedure's
    /// or dynamic batch it is raised in (probed 2026-09-29 against SQL Server
    /// 2025).
    /// </summary>
    private static bool IsBulkRefusal(SimulatedSqlException ex) => ex.Number is 4834 or 4860 or 4861;

    /// <summary>
    /// True for the bind-class name-resolution failures that abort the whole
    /// batch on real SQL Server rather than merely terminating their statement.
    /// Probe-confirmed against SQL Server 2025 (2026-07-16): with a
    /// <c>SELECT 1; &lt;failing&gt;; SELECT 2</c> batch, the statements before
    /// the failure stream their results and the single error surfaces, but the
    /// statements after it never execute — for Msg 208 (invalid object),
    /// Msg 207 (invalid column), Msg 209 (ambiguous column), Msg 4104 (multi-
    /// part identifier could not be bound), Msg 4121 (cannot find the
    /// column / function), and the view-write refusals Msg 4403 / 4405 / 4406,
    /// which a write through a CTE over a table the batch creates meets only
    /// when it runs (probed 2026-09-25). Contrast the statement-terminating errors that DO
    /// let the batch continue: Msg 3701 (drop missing), Msg 8134 (divide by
    /// zero), Msg 2812 (EXEC missing proc), a severity-16 RAISERROR. Consulted
    /// on every top-level batch (<see cref="BatchContext.ContinueOnError"/>);
    /// both front doors surface the abort (the wire stops writing tokens, the
    /// in-process reader throws the emitted error).
    /// Against objects that exist when the batch compiles, these fail the
    /// compile (<see cref="CompileBatch"/>) and nothing runs; what reaches
    /// here is a statement the compile deferred.
    /// </summary>
    private static bool IsBatchAbortingNameResolution(SimulatedSqlException ex)
        => ex.Number is 195 or 207 or 208 or 209 or 4104 or 4121 or 4403 or 4405 or 4406;

    /// <summary>
    /// Whether <paramref name="ex"/> is one real raises compiling a statement —
    /// a name-resolution miss, or a binder or type-check refusal — which,
    /// raised where the batch's compile deferred the statement, ends the batch
    /// uncatchable by the same scope's TRY as a name-resolution miss does
    /// (probed 2026-09-26 against SQL Server 2025, each after a CREATE TABLE
    /// deferred its statement; Msg 8124 2026-09-28, Msg 10709 2026-10-01). The bulk loads' refusals
    /// of their permission and files — Msg 4834, 4860, 4861 — end the batch
    /// the same way wherever they are raised (probed 2026-09-29). An error not
    /// listed keeps a run-time error's handling.
    /// </summary>
    internal static bool IsDeferredCompileError(SimulatedSqlException ex)
        => IsBatchAbortingNameResolution(ex) || IsBulkRefusal(ex) || ex.Number is 4902 or 2705
            || ex.Number is 107 or 108 or 130 or 145 or 147 or 157 or 164 or 174 or 205 or 206 or 213 or 243 or 264 or 321 or 330 or 331 or 332 or 333 or 425 or 426 or 447 or 448 or 529
                or 1011 or 1012 or 1013 or 4108 or 4115 or 4187 or 5318 or 5324 or 8117 or 8120 or 8121 or 8124 or 8155 or 8622 or 10709;

    /// <summary>
    /// Whether a TRY frame catches <paramref name="ex"/> where it is raised:
    /// any error but the transaction-aborting class, once the batch runs — and
    /// but a name-resolution miss of the batch's own, which real meets
    /// recompiling a statement it deferred and which no TRY in the same scope
    /// catches; raised by a procedure or dynamic batch it called, the same
    /// error is catchable (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static bool CaughtByTryFrame(BatchContext batch, SimulatedSqlException ex) =>
        batch.TryFrameDepth > 0 && !ex.AbortsTransaction && !ex.IsAttention && !batch.CreateTimeBinding
        && !(IsDeferredCompileError(ex) && !ex.EndedCalledBatch);

    /// <summary>
    /// Refuses <c>CREATE</c> / <c>ALTER</c> / <c>DROP DATABASE</c> and
    /// <c>ALTER DATABASE SCOPED CONFIGURATION</c> inside a user transaction —
    /// Msg 226 (states 5, 6, 7) or Msg 574 for the drop — ahead of the rest of
    /// the statement; the error ends only its statement and leaves the
    /// transaction committable, save the scoped-configuration one, which acts
    /// as under <c>XACT_ABORT</c> (probed 2026-09-27 against SQL Server 2025).
    /// Peeks without moving the cursor; null for any other statement.
    /// </summary>
    private static SimulatedSqlException? DatabaseDdlInTransaction(ParserContext context)
    {
        if (context.Token is not ReservedKeyword { Keyword: var verb } || verb is not (Keyword.Alter or Keyword.Create or Keyword.Drop))
            return null;
        var checkpoint = context.SaveCheckpoint();
        try
        {
            if (!context.MoveNext() || context.Token is not ReservedKeyword { Keyword: Keyword.Database })
                return null;
            var scoped = context.MoveNext() && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Scoped };
            return verb switch
            {
                Keyword.Alter when scoped => SimulatedSqlException.ScopedConfigurationInTransaction(),
                Keyword.Alter => SimulatedSqlException.DatabaseStatementInTransaction("ALTER DATABASE", 6),
                Keyword.Create => SimulatedSqlException.DatabaseStatementInTransaction("CREATE DATABASE", 5),
                _ => SimulatedSqlException.StatementInsideUserTransaction("DROP DATABASE"),
            };
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Whether the statement at <paramref name="parser"/>'s cursor changes a
    /// table's or index's structure (see
    /// <see cref="StatementContext.ChangesTableStructure"/>); peeks without
    /// moving the cursor.
    /// </summary>
    private static bool ChangesTableStructure(ParserContext parser)
    {
        if (parser.Token is not ReservedKeyword { Keyword: Keyword.Alter or Keyword.Create or Keyword.Drop or Keyword.Truncate or Keyword.Update } lead)
            return false;
        var checkpoint = parser.SaveCheckpoint();
        try
        {
            while (parser.MoveNext())
            {
                switch (parser.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.Table } when lead.Keyword is Keyword.Alter or Keyword.Drop or Keyword.Truncate:
                    case ReservedKeyword { Keyword: Keyword.Index } when lead.Keyword is Keyword.Alter or Keyword.Create or Keyword.Drop:
                    case ReservedKeyword { Keyword: Keyword.Statistics } when lead.Keyword is Keyword.Create or Keyword.Update:
                        return true;
                    // CREATE's index modifiers precede the INDEX keyword.
                    case ReservedKeyword { Keyword: Keyword.Unique or Keyword.Clustered or Keyword.NonClustered } when lead.Keyword == Keyword.Create:
                    case UnquotedString { Span: var modifier } when lead.Keyword == Keyword.Create && modifier.Equals("COLUMNSTORE", StringComparison.OrdinalIgnoreCase):
                        continue;
                }
                return false;
            }
            return false;
        }
        finally
        {
            parser.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Whether the statement at <paramref name="parser"/>'s cursor is object
    /// DDL or a permission statement — <c>CREATE</c>, <c>ALTER</c>,
    /// <c>DROP</c>, <c>TRUNCATE</c>, <c>UPDATE STATISTICS</c>, <c>GRANT</c>,
    /// <c>DENY</c>, <c>REVOKE</c> — which opens an implicit transaction before
    /// it runs, a missing object's <c>DROP</c> included; the database-level
    /// forms open none (probed 2026-09-28 against SQL Server 2025). Peeks
    /// without moving the cursor.
    /// </summary>
    private static bool OpensImplicitTransactionAsDdl(ParserContext parser)
    {
        if (parser.Token is not ReservedKeyword { Keyword: var lead })
            return false;
        if (lead is Keyword.Grant or Keyword.Deny or Keyword.Revoke or Keyword.Truncate)
            return true;
        if (lead is not (Keyword.Create or Keyword.Alter or Keyword.Drop or Keyword.Update))
            return false;
        var checkpoint = parser.SaveCheckpoint();
        try
        {
            return parser.MoveNext() && (lead == Keyword.Update
                ? parser.Token is ReservedKeyword { Keyword: Keyword.Statistics }
                : parser.Token is not ReservedKeyword { Keyword: Keyword.Database });
        }
        finally
        {
            parser.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// True for an error that ends the whole batch rather than its statement:
    /// a compile error of a statement the batch's compile deferred
    /// (<see cref="IsDeferredCompileError"/>) or any severity-15 error — a
    /// syntax error the compile walk stopped short of, or a run-time one such
    /// as a negative TOP's Msg 127 — the procedure or dynamic SQL it ended
    /// hasn't already contained, an uncaught <c>THROW</c>, or an error
    /// <c>SET XACT_ABORT ON</c> promoted (probed 2026-09-26 against SQL Server
    /// 2025). After a syntax error none of the batch runs on, where resuming
    /// at the next boundary keyword would read the broken statement's tail as
    /// statements of its own.
    /// </summary>
    private static bool EndsBatch(SimulatedSqlException ex)
        => ((IsDeferredCompileError(ex) || (ex.Class == 15 && !ex.RaisedByRaiserror)) && !ex.EndedCalledBatch) || ex.TerminatesBatch || ex.XactAbortPromoted || ex.IsAttention;

    private IEnumerable<SimulatedStatementOutcome> DispatchOneStatementCore(BatchContext batch, bool requireSemicolonBeforeCte, bool atBatchStart)
    {
        var context = batch.Parser;
        var connection = context.Connection;

        // CTE bindings and XMLNAMESPACES declarations live for exactly one
        // statement. Clear at the top of every iteration; a WITH prefix below
        // repopulates.
        context.CteBindings = null;
        context.CtePrefixLeadsSelectStatement = false;
        context.XmlNamespaces = null;
        batch.CurrentStatement.BeginExecution();

        // WITH prefix applies to the immediately-following SELECT / INSERT /
        // UPDATE / DELETE / MERGE. ParseCteBindings sets context.CteBindings
        // and advances the cursor to the dispatched statement's leading
        // keyword; the switch below runs unchanged.
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (requireSemicolonBeforeCte)
                throw SimulatedSqlException.CteRequiresPrecedingSemicolon();
            var withToken = context.Token;
            ParseCteBindings(context);
            batch.BindErrors?.AddCtePrefix(withToken, context.Token);
            context.CtePrefixLeadsSelectStatement = context.Token is ReservedKeyword { Keyword: Keyword.Select } or Operator { Character: '(' };
        }

        // A doomed transaction refuses object DDL with Msg 3930 the way it
        // refuses DML (probe-confirmed for CREATE TABLE). DML takes the check
        // inside RunMutation, which is where it can see whether the statement
        // reached a write; DDL writes unconditionally, so the leading keyword
        // is enough.
        if (!batch.IsSkipping
            && context.Token is ReservedKeyword { Keyword: Keyword.Create or Keyword.Alter or Keyword.Drop or Keyword.Truncate })
        {
            RejectWriteInDoomedTransaction(connection);
        }

        // SET FMTONLY ON: SELECT returns its metadata with zero rows and every
        // data-modifying statement is suppressed (no side effects) — the
        // deprecated metadata-discovery mode SqlClient's SqlBulkCopy still
        // wraps its `select * from dest` in. Runs only when not already
        // skipping (a never-taken branch's statement stays fully suppressed).
        // Deliberately shallow: control-flow and DDL statements under FMTONLY
        // still execute — SqlClient's usage never mixes those into the mode.
        if (connection.FmtOnly && !batch.IsSkipping
            && context.Token is ReservedKeyword { Keyword: var fmtKeyword }
            && fmtKeyword is Keyword.Select or Keyword.Insert or Keyword.Update or Keyword.Delete or Keyword.Merge)
        {
            yield return RunUnderFmtOnly(batch, fmtKeyword);
            yield break;
        }

        SimulatedStatementOutcome? outcome;
        int? rowCount = null;
        switch (context.Token)
        {
            // A query expression written in parentheses is a SELECT statement
            // too, `(SELECT 1) UNION (SELECT 2)` (probed 2026-09-26).
            case ReservedKeyword { Keyword: Keyword.Select }:
            case Operator { Character: '(' }:
                outcome = this.RunSelectStatement(batch, out var cutShort);
                if (outcome is not null)
                    yield return outcome;
                if (cutShort is not null)
                    ExceptionDispatchInfo.Throw(cutShort);
                break;

            case ReservedKeyword { Keyword: Keyword.Insert }:
                outcome = this.RunDmlStatement(context, ParseInsert);
                context.RejectTrailingToken();
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Merge }:
                outcome = this.RunDmlStatement(context, ParseMerge);
                context.RejectTrailingToken();
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                // Real SQL Server requires `;` after MERGE (Msg 10713) —
                // the only statement family with a mandatory terminator, and it
                // fires even when MERGE is the last statement in the batch
                // (probe-confirmed against SQL Server 2025: a bare MERGE with no
                // trailing `;` at end-of-batch still raises 10713). Check before
                // normalization so the cursor is still on the parser's lookahead
                // position. The check runs even in skip mode — the grammar
                // requirement is independent of execution.
                if (context.Token is not Operator { Character: ';' })
                    throw SimulatedSqlException.MergeMustBeTerminated();
                break;

            case ReservedKeyword { Keyword: Keyword.Update } when IsUpdateStatistics(context):
                _ = TryParseUpdateStatistics(context);
                context.RejectTrailingToken();
                rowCount = 0;
                // NORECOMPUTE surfaces in sys.stats.
                if (!batch.IsSkipping)
                    this.CatalogRows.Invalidate();
                break;

            case ReservedKeyword { Keyword: Keyword.Update }:
                outcome = this.RunDmlStatement(context, ParseUpdate);
                context.RejectTrailingToken();
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = outcome.RecordsAffected;
                    yield return outcome;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Delete }:
                outcome = this.RunDmlStatement(context, ParseDelete);
                context.RejectTrailingToken();
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
                {
                    // RETURN is a statement of its own for @@ROWCOUNT, counting
                    // one row when it carries a procedure's return status and
                    // none when bare, which is what the caller reads after
                    // the procedure (probed 2026-09-28 against SQL Server
                    // 2025); a function's body returns into its caller's
                    // statement instead. Read the skip state first: the RETURN
                    // itself starts skipping.
                    var returnRuns = !batch.IsSkipping;
                    var carriesStatus = ParseReturnStatement(batch);
                    // A token after the returned value is a syntax error at it
                    // (`RETURN 1 x` is Msg 102 near 'x'; probed 2026-09-30
                    // against SQL Server 2025).
                    context.RejectTrailingToken();
                    // A procedure's RETURN of a value is a SELECT-kind
                    // statement counting one row (probed 2026-09-28 against
                    // SQL Server 2025).
                    if (carriesStatus)
                    {
                        batch.CurrentStatement.DoneKind = StatementDoneKind.Select;
                        batch.CurrentStatement.DoneCount = 1;
                    }
                    if (returnRuns && batch.UdfFrame is null && !batch.CalledFunctionBody)
                        connection.LastStatementRowCount = carriesStatus ? 1 : 0;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute }:
                foreach (var o in ParseExec(batch))
                    yield return o;
                context.RejectTrailingToken();
                break;

            case ReservedKeyword { Keyword: Keyword.Reconfigure }:
                ParseReconfigureStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Revert }:
                RevertStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.SetUser }:
                SetUserStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case UnquotedString { ContextualKeyword: ContextualKeyword.Throw }:
                ParseThrowStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Goto }:
                ParseGotoStatement(batch);
                break;

            case UnquotedString when IsLabelDeclaration(context):
                ParseLabelDeclaration(context);
                break;

            case ReservedKeyword { Keyword: Keyword.Print }:
                ParsePrintStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.RaisError }:
                ParseRaiserrorStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.WaitFor }:
                ParseWaitForStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.ReadText }:
                {
                    var readText = ParseReadTextStatement(batch);
                    if (readText is not null)
                    {
                        connection.LastStatementRowCount = 1;
                        yield return readText;
                    }
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Bulk }:
                foreach (var bulkOutcome in this.RunBulkInsertStatement(batch))
                    yield return bulkOutcome;
                break;

            case ReservedKeyword { Keyword: Keyword.WriteText }:
                outcome = RunMutation(context, ParseWriteTextStatement);
                rowCount = outcome.RecordsAffected;
                break;

            case ReservedKeyword { Keyword: Keyword.UpdateText }:
                outcome = RunMutation(context, ParseUpdateTextStatement);
                rowCount = outcome.RecordsAffected;
                break;

            case ReservedKeyword { Keyword: Keyword.Truncate }:
                ParseTruncateStatement(batch);
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Use }:
                ParseUseStatement(batch);
                context.RejectTrailingToken();
                rowCount = 0;
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
                        case ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction or Keyword.Distributed }:
                            if (TryParseBeginTransaction(context))
                                rowCount = 0;
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Try }:
                            foreach (var o in ParseTryCatch(batch))
                                yield return o;
                            break;
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Catch }:
                            // A CATCH block opening where a statement belongs —
                            // at batch start, or inside a TRY body that never
                            // reached END TRY — is Msg 102 naming its BEGIN
                            // (probed 2026-09-25 against SQL Server 2025).
                            throw SimulatedSqlException.SyntaxErrorNear(context.Token);
                        case UnquotedString { ContextualKeyword: ContextualKeyword.Atomic }:
                            foreach (var o in ParseBeginAtomicBlock(batch))
                                yield return o;
                            rowCount = 0;
                            break;
                        default:
                            // A block leaves @@ROWCOUNT as its last statement
                            // set it (probed 2026-09-26 against SQL Server 2025),
                            // and @@ERROR likewise (probed 2026-10-04).
                            foreach (var o in ParseBeginBlock(batch))
                                yield return o;
                            batch.CurrentStatement.SuppressErrorReset = true;
                            break;
                    }
                    break;
                }

            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseShrink(context, batch, out var shrinkOutcomes):
                // SHRINK* and SHOW_STATISTICS leave @@ROWCOUNT alone (probed
                // 2026-09-28 against SQL Server 2025).
                foreach (var o in shrinkOutcomes)
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseShowStatistics(context, batch, out var statisticsOutcomes):
                foreach (var o in statisticsOutcomes)
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseCheckIdent(context, batch, out _):
                break;

            case ReservedKeyword { Keyword: Keyword.Dbcc } when TryParseInputBuffer(context, batch, out outcome, out var completion):
                // The row, then Msg 2528 after it, as real sends them.
                if (!batch.IsSkipping)
                {
                    connection.LastStatementRowCount = 1;
                    List<SimulatedStatementOutcome> produced = [outcome!];
                    if (completion is not null)
                        AfterDbccRows(batch, produced, completion);
                    foreach (var o in produced)
                        yield return o;
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Dbcc }:
                foreach (var dbccOutcome in ParseDbccCommand(context, batch))
                    yield return dbccOutcome;
                break;

            case ReservedKeyword { Keyword: Keyword.Checkpoint } when ParseCheckpoint(context, batch):
                break;

            case ReservedKeyword { Keyword: Keyword.Kill } when ParseKill(context, batch):
                break;

            case ReservedKeyword { Keyword: Keyword.Create } when TryParseCreate(context):
            case ReservedKeyword { Keyword: Keyword.Drop } when RejectingTrailingToken(context, TryParseDrop(context)):
            case ReservedKeyword { Keyword: Keyword.Alter } when TryParseAlter(context):
                batch.WalkMetDdl |= batch.CompilingForRun;
                // DDL invalidates every cached plan parsed under the prior
                // schema version. Skip-mode statements don't actually execute
                // the DDL (their parse-only walk has no schema effect), so the
                // bump is gated on !IsSkipping.
                rowCount = 0;
                if (!batch.IsSkipping)
                {
                    BumpSchemaVersion();
                    if (connection.CurrentTransaction is { } ddlTransaction)
                    {
                        lock (ddlTransaction.CatalogChanges)
                            _ = ddlTransaction.CatalogChanges.Add(connection.CurrentDatabase);
                    }
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Commit } when RejectingTrailingToken(context, TryParseCommit(context)):
            case ReservedKeyword { Keyword: Keyword.Save } when TryParseSavepoint(context):
            case ReservedKeyword { Keyword: Keyword.Rollback } when TryParseRollbackTransaction(context):
            case ReservedKeyword { Keyword: Keyword.Grant } when RejectingTrailingToken(context, TryParseGrantRevokeDeny(context, PermissionStatementKind.Grant)):
            case ReservedKeyword { Keyword: Keyword.Revoke } when RejectingTrailingToken(context, TryParseGrantRevokeDeny(context, PermissionStatementKind.Revoke)):
            case ReservedKeyword { Keyword: Keyword.Deny } when RejectingTrailingToken(context, TryParseGrantRevokeDeny(context, PermissionStatementKind.Deny)):
                rowCount = 0;
                break;
            case UnquotedString { ContextualKeyword: ContextualKeyword.Disable } when TryParseEnableOrDisableTrigger(context, disable: true):
            case UnquotedString { ContextualKeyword: ContextualKeyword.Enable } when TryParseEnableOrDisableTrigger(context, disable: false):
                rowCount = 0;
                // A trigger's is_disabled surfaces in sys.triggers.
                if (!batch.IsSkipping)
                    this.CatalogRows.Invalidate();
                break;
            case ReservedKeyword { Keyword: Keyword.Set } when TryParseSet(context, out var assignsVariable):
                // SET @v = expr sets @@ROWCOUNT to 1; setting a session option
                // (NOCOUNT, ANSI_NULLS, LANGUAGE, ROWCOUNT, …) resets it to 0
                // (probed 2026-09-24 against SQL Server 2025).
                rowCount = assignsVariable ? 1 : 0;
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
                        rowCount = 0;
                    }
                    else
                    {
                        // No initializer → @@ROWCOUNT and @@ERROR both
                        // preserved (probe-confirmed; @@ERROR probed 2026-09-24).
                        if (TryParseDeclare(context) is int n)
                        {
                            rowCount = n;
                        }
                        else
                        {
                            batch.CurrentStatement.SuppressErrorReset = true;
                        }
                        context.RejectTrailingToken();
                    }
                }
                break;

            case ReservedKeyword { Keyword: Keyword.Open }:
                ParseOpenCursor(batch);
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Fetch }:
                foreach (var o in ParseFetchCursor(batch))
                    yield return o;
                break;

            case ReservedKeyword { Keyword: Keyword.Close }:
                ParseCloseCursor(batch);
                rowCount = 0;
                break;

            case ReservedKeyword { Keyword: Keyword.Deallocate }:
                ParseDeallocateCursor(batch);
                rowCount = 0;
                break;

            // Bare object name at batch start → implicit EXECUTE (the form
            // mssql-jdbc's `getTypeInfo` sends: `sp_datatype_info_100 0, 3`
            // with no EXEC keyword). Routed through the same EXEC path so RPC
            // and text execution stay identical. Anywhere but the first
            // statement, a leading identifier stays Msg 102 (real's rule).
            // A variable naming the procedure takes the implicit form too, so
            // a batch opening `@p 1` runs `EXEC @p 1` and an undeclared one is
            // Msg 137 (probed 2026-09-25 against SQL Server 2025).
            case Name or AtPrefixedString when atBatchStart:
                foreach (var o in ParseExec(batch, implicitExec: true))
                    yield return o;
                break;

            // A stray END is named as a plain token, where any other keyword
            // opening a statement is Msg 156 (probed 2026-09-24 against SQL
            // Server 2025).
            case ReservedKeyword { Keyword: Keyword.End } strayEnd:
                throw SimulatedSqlException.SyntaxErrorNear(strayEnd);

            default:
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        // @@ROWCOUNT as the statement leaves it, for the arms that name it in
        // rowCount; an arm yielding its own outcome sets it ahead of that
        // yield, and a skipped statement leaves it alone.
        if (rowCount is { } count && !batch.IsSkipping)
            connection.LastStatementRowCount = count;

        // Cursor normalization, and the batch's trailing-token rule.
        //
        // A statement parser leaves the cursor in one of two places: at its
        // first un-consumed token (a `;`, the next statement's leading
        // keyword, or null at end of input), or on the last token it did
        // consume. The first is a statement boundary and needs nothing. The
        // second needs one advance — and used to get it unconditionally,
        // which is what silently swallowed a stray token after any parser of
        // the first kind: `DECLARE @x int = 1 zzz` ran clean where real
        // raises Msg 102 at the `zzz`.
        //
        // The two cases can't be told apart from the cursor alone, so the
        // dispatch arm says which: a statement kind whose parser stops past
        // its own input calls ParserContext.RejectTrailingToken, and a
        // non-boundary token after one of those is unconsumed input — Msg 102
        // naming it, real's own rule, probed per statement kind on
        // 2026-08-06. Everything else keeps the advance.
        // Read the flag unconditionally: it has to be cleared even when this
        // statement ended on a boundary, or it would still be set when the
        // next one finishes and would reject that statement's own tail.
        var rejectTrailingToken = context.ConsumeRejectTrailingToken();
        // A label begins the next statement as a keyword does (probed
        // 2026-09-28 against SQL Server 2025: `DECLARE @i int = 0` and
        // `GOTO l` each run on into a label on the next line).
        if (!EndsStatement(context.Token) && !IsLabelDeclaration(context))
        {
            if (rejectTrailingToken)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
        }
    }

    /// <summary>
    /// Runs a <c>SELECT</c> or a data-modifying statement under
    /// <c>SET FMTONLY ON</c>: a query answers its metadata with no rows, and a
    /// write parses in skip mode, so its syntax errors still surface, and
    /// closes as real closes the suppressed statement.
    /// </summary>
    private static SimulatedStatementOutcome RunUnderFmtOnly(BatchContext batch, Keyword fmtKeyword)
    {
        var context = batch.Parser;
        var connection = context.Connection;
        if (fmtKeyword == Keyword.Select)
        {
            var metadataSelection = Selection.Parse(context, QueryScope.Statement).AsStatementResult();
            connection.LastStatementRowCount = 0;
            if (metadataSelection.IntoTarget is null)
            {
                return new SimulatedSqlResultSet(metadataSelection.Schema, metadataSelection.ColumnNames, new List<byte[]>())
                {
                    ColumnNullability = metadataSelection.ColumnNullability,
                    ColumnReportsNumeric = metadataSelection.ColumnReportsNumeric,
                    ColumnAliasTypes = metadataSelection.ColumnAliasTypes,
                    ColumnIdentitySources = metadataSelection.ColumnIdentitySources,
                    ColumnWireFlags = metadataSelection.ColumnWireFlags,
                    ColumnOrigins = Selection.BaseColumnOrigins(metadataSelection),
                    ColumnIsComputed = Selection.ComputedColumnsOf(metadataSelection),
                    IsGrouped = metadataSelection.IsGrouped,
                };
            }
            else
            {
                // SELECT … INTO closes with a DONE counting 0, as the
                // suppressed DML below does.
                return new SimulatedNonQuery(0) { CountSuppressed = false, DoneKind = StatementDoneKind.SelectInto };
            }
        }

        // Parse the DML under skip mode so the cursor advances and syntax
        // errors still surface, but no heap write happens.
        batch.SkipModeFlag = true;
        SimulatedStatementOutcome suppressed;
        try
        {
            switch (fmtKeyword)
            {
                case Keyword.Insert:
                    suppressed = RunMutation(context, ParseInsert);
                    break;
                case Keyword.Update:
                    suppressed = RunMutation(context, ParseUpdate);
                    break;
                case Keyword.Delete:
                    suppressed = RunMutation(context, ParseDelete);
                    break;
                default:
                    suppressed = RunMutation(context, ParseMerge);
                    if (context.Token is not Operator { Character: ';' })
                        throw SimulatedSqlException.MergeMustBeTerminated();
                    break;
            }
        }
        finally
        {
            batch.SkipModeFlag = false;
        }

        // Real closes the suppressed statement with a DONE counting 0 —
        // under NOCOUNT too — or, for an OUTPUT clause, with its empty
        // result set (probed 2026-09-28 against SQL Server 2025).
        connection.LastStatementRowCount = 0;
        return suppressed is SimulatedSqlResultSet output
            ? new SimulatedSqlResultSet(output.Schema, output.ColumnNames, new List<byte[]>()) { ColumnNullability = output.ColumnNullability }
            : new SimulatedNonQuery(0) { CountSuppressed = false };
    }

    /// <summary>
    /// Runs a <c>SELECT</c> statement: parses it, runs it and materializes its
    /// rows, and joins it to the batch's plan-cache candidates. Returns what
    /// the statement sends — null for none — and in
    /// <paramref name="cutShort"/> the error a row raised after the rows ahead
    /// of it, which the caller throws once it has sent them.
    /// </summary>
    private SimulatedStatementOutcome? RunSelectStatement(BatchContext batch, out SimulatedSqlException? cutShort)
    {
        var context = batch.Parser;
        var connection = batch.Connection;
        SimulatedStatementOutcome outcome;
        cutShort = null;
        var statementStart = context.SaveCheckpoint();
        context.ForBrowseSeen = false;
        List<ReplayedLock>? replayLocks;
        // A statement the plan cache may store records the locks
        // its parse takes, which a replay takes again as its own
        // session.
        batch.ReplayLockLog = batch.PlanCacheCommandText is not null && batch.BlockDepth == 0 && !batch.IsSkipping ? [] : null;
        Selection selection;
        try
        {
            selection = ParseSelectStatement(context, browse: connection.NoBrowseTable);
            // A trailing FOR BROWSE puts the one statement in browse
            // mode, which decides its projection, so the statement is
            // read again as a browse statement (probed 2026-09-26).
            if (context.ForBrowseSeen && !connection.NoBrowseTable)
            {
                context.RestoreCheckpoint(statementStart);
                selection = ParseSelectStatement(context, browse: true);
            }
            else if (connection.NoBrowseTable && selection.IsSetOperationResult)
            {
                selection.Browse = Selection.SetOperationBrowseInfo(selection.Schema.Length);
            }
        }
        finally
        {
            replayLocks = batch.ReplayLockLog;
            batch.ReplayLockLog = null;
        }
        // A value literal or a name left dangling after a complete
        // SELECT is always unconsumed trailing input — real SQL
        // Server raises Msg 102 rather than silently ignoring it
        // (the non-T-SQL `SELECT id FROM t LIMIT 2` parses `LIMIT`
        // as the source's alias and leaves `2` dangling;
        // `FROM t a hash` leaves `hash`, probed 2026-09-24). A
        // well-formed SELECT never ends on one, nor on a comma
        // (`SELECT 1 WHERE 1 IN (NULL), 2` is near ',', probed
        // 2026-09-26), nor on an AS (`(SELECT 1) AS q` is near the
        // keyword 'as', probed 2026-09-30), nor on a closing paren
        // (`(SELECT 1))` and a CTE-led `… FROM c)` are near ')', probed
        // 2026-10-01); any other token is left to the generic
        // end-of-dispatch normalizer.
        if (context.Token is (Numeric or Literal or Name or Operator { Character: ',' or ')' } or ReservedKeyword { Keyword: Keyword.As }) and not UnquotedString { IsLabelDeclaration: true })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (!batch.IsSkipping)
            PermissionEnforcement.CheckReadSources(batch, selection.ReferencedSecurables, selection.ReadColumnsByObject);
        // Inside a function body real splits the SELECT three ways:
        // an assignment-only SELECT is legal, SELECT … INTO is a
        // side-effecting operator of its own, and anything else
        // would send rows to the client.
        if (batch.FunctionBodyShape is not null)
        {
            // A temporary destination is Msg 2772 in place of the operator
            // (probed 2026-10-04 against SQL Server 2025).
            if (selection.IntoTarget is { } into && (BatchContext.IsLocalTempName(into.Leaf) || BatchContext.IsGlobalTempName(into.Leaf)))
                FunctionBodyShape.NoteRefusal(batch, SimulatedSqlException.TemporaryTableInFunction(), batch.CurrentStatement.StartLine);
            else if (selection.IntoTarget is not null)
                FunctionBodyShape.NoteSideEffect(batch, "SELECT INTO", FunctionBodyShape.StatementOperatorState);
            else if (!selection.IsAssignmentOnly)
                FunctionBodyShape.NoteClientSelect(batch);
        }
        if (selection.IntoTarget is not null)
        {
            // SELECT INTO: creates the destination table and
            // inserts each projected row. RunMutation gives
            // the executor access to the active undo log so
            // transactional CREATE+INSERT can roll back. In
            // skip mode, ExecuteSelectInto returns SimulatedNonQuery(0)
            // without touching the heap.
            outcome = RunMutation(context, _ => ExecuteSelectInto(selection, batch));
            outcome.DoneKind = StatementDoneKind.SelectInto;
            if (!batch.IsSkipping)
            {
                connection.LastStatementRowCount = outcome.RecordsAffected;
                return outcome;
            }
            return null;
        }
        if (batch.IsSkipping)
            return null;
        if (connection.InsertExecTargetTypes is { } insertExecTargets && !selection.IsAssignmentOnly)
            RequireInsertExecAssignable(selection, insertExecTargets, batch);
        // Materialize rows up-front so @@ROWCOUNT reflects the
        // statement's full row count for the next statement in
        // the same batch (real SQL Server runs server-side and
        // sets @@ROWCOUNT on completion; the simulator
        // materializes to mirror that). The rows are held in
        // whichever form the plan produced — a projecting SELECT's
        // SqlValue rows travel to the reader as they are.
        // SET ROWCOUNT caps what the statement returns, including
        // the rows a `SELECT @v = …` assignment walks
        // (probe-confirmed: the assignment keeps the value from the
        // last row inside the cap, and @@ROWCOUNT reads the cap).
        // A row that raises ends the statement, but real has sent
        // the column metadata and the rows before it by then, so
        // they go out ahead of the error (see EndedByError) — an
        // empty result set when the plan failed before its first.
        SimulatedSqlResultSet? executed = null;
        int rowCount;
        try
        {
            executed = DataMasking.ForClient(selection.Execute(batch), selection.ColumnMasks, batch).WithRowCountLimit(connection.RowCountLimit);
            rowCount = executed.MaterializeRows();
            if (selection.CountsForClauseSourceRows)
                rowCount = executed.ReportedRowCount = batch.CurrentStatement.ForClauseSourceRows;
        }
        catch (SimulatedSqlException error) when (!selection.IsAssignmentOnly)
        {
            cutShort = error;
            rowCount = 0;
            executed ??= new SimulatedSqlResultSet(selection.Schema, selection.ColumnNames, new List<byte[]>())
            {
                ColumnNullability = selection.ColumnNullability,
                ColumnReportsNumeric = selection.ColumnReportsNumeric,
                ColumnAliasTypes = selection.ColumnAliasTypes,
                ColumnIdentitySources = selection.ColumnIdentitySources,
                ColumnWireFlags = selection.ColumnWireFlags,
                HiddenColumnCount = selection.HiddenColumnCount,
                Browse = selection.Browse,
            };
            executed.EndedByError = true;
            executed.ErrorCaught = CaughtByTryFrame(batch, error);
        }
        connection.LastStatementRowCount = rowCount;
        outcome = selection.IsAssignmentOnly
            ? new SimulatedNonQuery(rowCount, countsRowsReturned: true)
            : executed;

        // Plan-cache accumulation and promotion, inline before the
        // dispatch yields the outcome. Gates: top-level (BlockDepth == 0 → not inside
        // IF / WHILE / BEGIN…END / TRY/CATCH); shape (not
        // assignment-only — those yield NonQuery and aren't worth
        // caching); no session-scoped table reference.
        //
        // A qualifying SELECT joins the batch's candidate
        // sequence; the batch is promoted at the LAST statement,
        // which is the one that finds nothing but separators left.
        // Comparing the sequence's length against the count of
        // top-level statements dispatched is what proves no other
        // statement kind ran: the loop counts this statement only
        // after its dispatch returns, hence the +1.
        if (batch.BlockDepth == 0
            && !selection.IsAssignmentOnly
            && !batch.HasSessionScopedReference)
        {
            (batch.PlanCacheSequence ??= []).Add(selection);
            (batch.PlanCacheSequenceLocks ??= []).Add(replayLocks is null ? [] : [.. replayLocks]);
            (batch.PlanCacheSequenceSpans ??= []).Add((batch.CurrentStatement.StartIndex, context.PreviousTokenEnd));
            if (batch.PlanCacheSequence.Count == batch.TopLevelStatementsDispatched + 1
                && IsAtEndOfBatch(context))
            {
                TryPromoteSelectionsToPlanCache(batch);
            }
        }
        return outcome;
    }

    /// <summary>
    /// Marks the statement as one whose parser stops at its first un-consumed
    /// token, then passes <paramref name="parsed"/> straight through — for the
    /// dispatch arms whose bodies are shared with statement kinds that don't
    /// share that discipline, where the mark can't go on the arm itself.
    /// </summary>
    private static bool RejectingTrailingToken(ParserContext context, bool parsed)
    {
        if (parsed)
            context.RejectTrailingToken();
        return parsed;
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
                or Keyword.Exec or Keyword.Execute or Keyword.Reconfigure or Keyword.Revert
                or Keyword.Checkpoint or Keyword.Goto or Keyword.Bulk or Keyword.Kill
                or Keyword.ReadText or Keyword.WriteText or Keyword.UpdateText
                or Keyword.Backup or Keyword.Restore or Keyword.Shutdown
        }
        // THROW is a contextual keyword in SQL Server's grammar — added with
        // the TRY/CATCH companion feature in 2012, not in the reserved list.
        // It surfaces as UnquotedString from the tokenizer; statement-boundary
        // detection routes through the cached ContextualKeyword classifier.
        or UnquotedString { ContextualKeyword: ContextualKeyword.Throw }
        // A GOTO label declaration starts the next statement too.
        or UnquotedString { IsLabelDeclaration: true };

    /// <summary>
    /// Whether <paramref name="token"/>, standing where a complete statement
    /// ends, lets it end: a <see cref="IsStatementBoundary">boundary</see>, or
    /// a <c>(</c>, which opens a parenthesized query statement — <c>SELECT 1
    /// (SELECT 2)</c> and <c>UPDATE t SET a = 1 (SELECT 2)</c> are two
    /// statements each, and <c>SET @x = 1 (-1)</c> is the syntax error at the
    /// <c>-</c> (probed 2026-10-01 against SQL Server 2025).
    /// </summary>
    internal static bool EndsStatement(Token? token) =>
        IsStatementBoundary(token) || token is Operator { Character: '(' };

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
            if (slot.Parameter is SimulatedDbParameter parameter
                && parameter.Direction is ParameterDirection.InputOutput or ParameterDirection.Output)
            {
                // Output parameters ride the same TEXTSIZE wire-egress clip
                // as result columns (probe-confirmed 2026-07-19: a
                // varchar(max) OUTPUT under SET TEXTSIZE 10 arrives at the
                // client 10 chars long); server-side state is untouched.
                var value = TextSizeCursor.Apply(slot.Value, slot.DeclaredType, batch.Connection.TextSize);
                parameter.OutputSqlValue = value;
                parameter.Value = value.IsNull ? DBNull.Value : value.ToObject();
            }
        }
    }

    /// <summary>
    /// The transaction or savepoint name under the cursor, refused past 32
    /// characters while compiling (Msg 103 state 2, probe-confirmed against
    /// SQL Server 2025 for every statement that takes one).
    /// </summary>
    private static string ParseTransactionName(ParserContext context)
    {
        var name = ((Name)context.Token!).Value;
        return name.Length > SimulatedDbTransaction.MaxNameLength
            ? throw SimulatedSqlException.TransactionNameTooLong(name)
            : name;
    }

    /// <summary>
    /// The transaction or savepoint name a variable holds, cut to the 32
    /// characters a written one is refused past; a variable of a type other
    /// than a string is Msg 3914 (probed 2026-10-02 against SQL Server 2025).
    /// </summary>
    private static string? TransactionNameFromVariable(BatchContext batch, string variable)
    {
        var slot = batch.GetVariableSlot(variable);
        if (slot.DeclaredType is not (VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType))
            throw SimulatedSqlException.TransactionNameTypeInvalid(SimulatedSqlException.FamilyRootName(slot.DeclaredType));
        if (slot.Value.IsNull)
            return null;
        var held = slot.Value.AsString;
        return held.Length > SimulatedDbTransaction.MaxNameLength ? held[..SimulatedDbTransaction.MaxNameLength] : held;
    }

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
        string name;
        if (context.GetNextRequired() is AtPrefixedString variable)
        {
            context.MoveNextOptional();
            if (context.Batch.IsSkipping)
                return true;
            name = TransactionNameFromVariable(context.Batch, variable.Value) ?? string.Empty;
        }
        else
        {
            if (context.Token is not Name)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            name = ParseTransactionName(context);
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        if (context.Connection.CurrentTransaction is null && context.Connection.TriggerStatementUndoLog is { } unit)
        {
            (context.Connection.TriggerUnitSavepoints ??= []).Add((name, unit.Position, context.Connection.TriggerStatementVersionEntries?.Count ?? 0));
            return true;
        }
        var tx = context.Connection.CurrentTransaction
            ?? throw SimulatedSqlException.SaveTransactionWithoutTransaction();
        // SAVE TRANSACTION writes a log record, so a doomed transaction
        // refuses it with Msg 3930 (probe-confirmed).
        RejectWriteInDoomedTransaction(context.Connection);
        context.Connection.RefuseTransactionOperationWithRequestsPending(tx, state: 2);
        tx.SetSavepoint(name);
        return true;
    }

    /// <summary>
    /// Parses <c>BEGIN [DISTRIBUTED] TRAN[SACTION] [name] [WITH MARK ['description']]</c>.
    /// Opens a fresh <see cref="SimulatedDbTransaction"/> on the connection
    /// when none is active (TRANCOUNT 0 → 1) or increments
    /// <see cref="SimulatedDbTransaction.TranCount"/> when one already is
    /// (nested-BEGIN TRANCOUNT bump, no real nesting). The optional name is
    /// cosmetic — SQL Server treats it as documentation only, and only the
    /// outermost COMMIT actually commits regardless of which name the COMMIT
    /// references.
    /// </summary>
    /// <remarks>
    /// The mark a <c>WITH MARK</c> places is a transaction-log artifact used by
    /// point-in-time restore, which nothing here has: the description is parsed
    /// and discarded. Its two observable consequences are modeled —
    /// <b>Msg 3901</b> when the transaction went unnamed (raised at run time, so
    /// <c>@@TRANCOUNT</c> stays where it was), and the severity-10
    /// <b>Msg 3920</b> when a mark is already on the transaction, which real
    /// emits only for the second <i>marked</i> BEGIN rather than for every
    /// nested one. Both probe-confirmed against SQL Server 2025, along with the
    /// description being optional and admitting a variable.
    /// </remarks>
    private static bool TryParseBeginTransaction(ParserContext context)
    {
        if (!context.MoveNext())
            return false;
        // DISTRIBUTED asks for a transaction the coordinator would enlist
        // remote resources in. The statement opens the ordinary local
        // transaction, which is what real does until something actually
        // enlists: probe-confirmed that BEGIN DISTRIBUTED TRANSACTION matches
        // BEGIN TRANSACTION on @@TRANCOUNT, nesting either way, XACT_STATE,
        // COMMIT / ROLLBACK and even the WITH MARK diagnostics. What differs is
        // a linked server's read inside it, which then enlists too
        // (SimulatedDbTransaction.IsDistributed).
        var distributed = context.Token is ReservedKeyword { Keyword: Keyword.Distributed };
        if (distributed && !context.MoveNext())
            return false;
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Tran or Keyword.Transaction })
            return false;
        // Optional name (BEGIN TRANSACTION my_tx, or @v holding one). Cosmetic;
        // consume and ignore — but whether one was written decides Msg 3901.
        var named = false;
        string? literalName = null;
        string? nameVariable = null;
        if (context.MoveNext() && context.Token is Name or AtPrefixedString)
        {
            named = true;
            if (context.Token is AtPrefixedString variable)
                nameVariable = variable.Value;
            else
                literalName = ParseTransactionName(context);
            context.MoveNextOptional();
        }

        var marked = false;
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not UnquotedString { ContextualKeyword: ContextualKeyword.Mark })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            marked = true;
            // The description is optional and may be a literal or a variable.
            if (context.MoveNext() && context.Token is Literal or AtPrefixedString)
                context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        if (marked && !named)
            throw SimulatedSqlException.TransactionNameRequiredForMark();

        // A doomed transaction refuses to nest (probed 2026-10-02 against SQL
        // Server 2025).
        RejectWriteInDoomedTransaction(context.Connection);
        // Under SET IMPLICIT_TRANSACTIONS ON the statement opens the implicit
        // transaction first and then nests in it, @@TRANCOUNT reading 2
        // (probed 2026-09-28 against SQL Server 2025).
        context.Batch.BeginImplicitTransaction();
        if (context.Connection.CurrentTransaction is { } existing)
        {
            if (marked && existing.IsMarked)
            {
                context.Batch.AppendInfoError(
                    @class: 0,
                    state: 1,
                    number: 3920,
                    message: "The WITH MARK option only applies to the first BEGIN TRAN WITH MARK statement. The option is ignored.");
            }

            existing.TranCount++;
            existing.IsMarked |= marked;
            existing.IsDistributed |= distributed;
        }
        else
        {
            // A name held in a variable is cut to the 32 characters a written
            // one is refused past (probe-confirmed).
            var name = nameVariable is null ? literalName : TransactionNameFromVariable(context.Batch, nameVariable);
            context.Connection.CurrentTransaction = new SimulatedDbTransaction(
                context.Simulation, context.Connection, System.Data.IsolationLevel.Unspecified)
            {
                IsMarked = marked,
                Name = name,
                IsDistributed = distributed,
            };
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
            // Optional name, which real ignores (any name commits) once it
            // passes the length rule.
            if (context.MoveNext() && context.Token is Name)
            {
                _ = ParseTransactionName(context);
                context.MoveNextOptional();
            }
            else if (context.Token is AtPrefixedString)
            {
                context.MoveNextOptional();
            }
        }
        // COMMIT WORK is an ANSI-equivalent. WORK isn't reserved in the
        // simulator's keyword list; accept it as an unquoted identifier
        // following COMMIT.
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Work })
        {
            context.MoveNextOptional();
        }

        // WITH (DELAYED_DURABILITY = ON | OFF) asks to defer the log flush,
        // which nothing here has; accepted and discarded (probed 2026-10-02
        // against SQL Server 2025).
        if (context.Token is ReservedKeyword { Keyword: Keyword.With })
        {
            if (context.GetNextRequired() is not Operator { Character: '(' }
                || context.GetNextRequired() is not UnquotedString durability
                || !BuiltInToken.Equals(durability.Value, "DELAYED_DURABILITY")
                || context.GetNextRequired() is not Operator { Character: '=' }
                || context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.On or Keyword.Off }
                || context.GetNextRequired() is not Operator { Character: ')' })
            {
                throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
        }

        if (context.Batch.IsSkipping)
            return true;

        var connection = context.Connection;
        var tx = connection.CurrentTransaction;
        if (tx is null)
        {
            // In a trigger body fired by an auto-commit statement it commits
            // that statement's own unit — the firing statement's writes and the
            // body's so far — and the body runs on to Msg 3609 as after a
            // ROLLBACK; a logon trigger's login then stands whatever follows
            // (probed 2026-09-28 against SQL Server 2025).
            var triggerUnit = connection.TriggerStatementUndoLog
                ?? throw SimulatedSqlException.NoCorrespondingBeginCommit();
            if (connection.TriggerStatementVersionEntries is { } unitVersions)
                Storage.VersionStore.FinalizePendingEntries(unitVersions, connection.Simulation);
            triggerUnit.Commit();
            connection.TriggerStatementUndoLog = null;
            connection.TriggerTransactionEnded = true;
            connection.LogonUnitCommitted |= connection.RunningLogonTriggers;
            return true;
        }

        // A SQLCLR routine's context connection may commit only what it began
        // (probed 2026-09-28 against SQL Server 2025).
        if (context.Connection.ClrContext is { HasEntryTransaction: true } clrLevel && tx.TranCount <= clrLevel.EntryTranCount)
        {
            clrLevel.EntryTransactionEnded = true;
            throw SimulatedSqlException.ClrCommitRefused();
        }

        // A COMMIT is a log write, so a doomed transaction refuses it with
        // Msg 3930 exactly as a DML statement does (probe-confirmed) — real
        // names the message's own advice: roll back instead.
        RejectWriteInDoomedTransaction(connection);
        if (tx.TranCount == 1)
            connection.RefuseTransactionOperationWithRequestsPending(tx, state: 1);
        tx.TranCount--;
        if (tx.TranCount == 0)
        {
            // A trigger body ending the transaction its firing statement runs
            // in aborts the batch with Msg 3609 when it returns, as a ROLLBACK
            // does; one the body began over an auto-commit unit is its own.
            var endsFiringTransaction = connection.TriggerNestLevel > 0 && ReferenceEquals(connection.TriggerStatementUndoLog, tx.UndoLog);
            tx.EndCommit();
            if (endsFiringTransaction)
            {
                connection.TriggerStatementUndoLog = null;
                connection.TriggerTransactionEnded = true;
            }
        }
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
                if (context.MoveNext() && context.Token is Name or AtPrefixedString)
                {
                    // Savepoint-name path: partial rollback to the saved
                    // position; the outermost BEGIN's own name rolls the whole
                    // transaction back. A variable holds the name.
                    var variable = context.Token as AtPrefixedString;
                    var name = variable is null ? ParseTransactionName(context) : string.Empty;
                    context.MoveNextOptional();

                    if (context.Batch.IsSkipping)
                        return true;
                    if (variable is not null)
                        name = TransactionNameFromVariable(context.Batch, variable.Value) ?? string.Empty;

                    if (context.Connection.CurrentTransaction is null && TryRollbackTriggerUnitToSavepoint(context.Connection, name))
                        return true;
                    var tx = context.Connection.CurrentTransaction
                        ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
                    if (tx.TryRollbackToSavepoint(name))
                        return true;
                    if (!string.Equals(tx.Name, name, StringComparison.Ordinal))
                        throw SimulatedSqlException.CannotRollBackUnknownSavepoint(name);
                    if (context.Connection.ClrContext is { HasEntryTransaction: true } namedLevel)
                    {
                        namedLevel.EntryTransactionEnded = true;
                        throw SimulatedSqlException.ClrRollbackRefused();
                    }

                    tx.EndRollback(TransactionEvent.StatementRollback);
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
        // full rollback regardless of TRANCOUNT. In a trigger body fired by an
        // auto-commit statement it ends that statement's own transaction: the
        // firing statement's writes and the body's so far are undone, and what
        // the body writes afterwards commits on its own.
        var connection = context.Connection;
        // A SQLCLR routine's context connection may not roll back a
        // transaction held when the routine was entered (probed 2026-09-28
        // against SQL Server 2025).
        if (connection.ClrContext is { HasEntryTransaction: true } clrLevel)
        {
            clrLevel.EntryTransactionEnded = true;
            throw SimulatedSqlException.ClrRollbackRefused();
        }

        if (connection.CurrentTransaction is { } activeTx)
        {
            activeTx.EndRollback(TransactionEvent.StatementRollback);
            if (connection.TriggerNestLevel > 0)
            {
                connection.TriggerStatementUndoLog = null;
                connection.TriggerTransactionEnded = true;
            }
            return true;
        }
        var triggerUnit = connection.TriggerStatementUndoLog
            ?? throw SimulatedSqlException.NoCorrespondingBeginRollback();
        triggerUnit.Rollback();
        connection.TriggerStatementUndoLog = null;
        connection.TriggerTransactionEnded = true;
        return true;
    }

    /// <summary>
    /// Rolls a trigger body's auto-commit unit back to the newest savepoint
    /// <paramref name="name"/> names, consuming it and every later one, and
    /// keeps the unit open; false when the body set none of that name.
    /// </summary>
    private static bool TryRollbackTriggerUnitToSavepoint(SimulatedDbConnection connection, string name)
    {
        if (connection.TriggerStatementUndoLog is not { } unit || connection.TriggerUnitSavepoints is not { } savepoints)
            return false;
        var index = savepoints.FindLastIndex(savepoint => string.Equals(savepoint.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        var (_, undoPosition, versionEntryCount) = savepoints[index];
        savepoints.RemoveRange(index, savepoints.Count - index);
        unit.RollbackTo(undoPosition);
        if (connection.TriggerStatementVersionEntries is { } versions && versions.Count > versionEntryCount)
        {
            var undone = versions.GetRange(versionEntryCount, versions.Count - versionEntryCount);
            versions.RemoveRange(versionEntryCount, undone.Count);
            Storage.VersionStore.DiscardPendingEntries(undone, kept: versions);
        }
        return true;
    }

    /// <summary>
    /// Runs a write's parse-and-execute body, then — when its target is a
    /// linked server's table — replays what it did there. A value too long for
    /// a remote column is the provider's Msg 8152 at state 14 (probed
    /// 2026-09-28 against SQL Server 2025), where a local one is Msg 2628.
    /// </summary>
    private static SimulatedStatementOutcome RunMutationBody(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> body)
    {
        SimulatedStatementOutcome outcome;
        try
        {
            outcome = body(context);
        }
        catch (SimulatedSqlException error) when (error.Number == 2628 && context.Batch.CurrentStatement.RemoteWrite is not null)
        {
            throw SimulatedSqlException.StringOrBinaryWouldBeTruncatedLegacy(14);
        }
        if (context.Batch.CurrentStatement.RemoteWrite is { } remoteWrite && !context.Batch.IsSkipping)
            remoteWrite.Replay(context.Batch);
        return outcome;
    }

    /// <summary>
    /// Whether the <c>INSERT</c> / <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c>
    /// at the cursor names a table variable as its target — read past
    /// <c>TOP (…)</c>, <c>INTO</c> and <c>FROM</c>. Leaves the cursor where it
    /// found it.
    /// </summary>
    private static bool WritesTableVariable(ParserContext context)
    {
        var checkpoint = context.SaveCheckpoint();
        try
        {
            var depth = 0;
            for (var read = 0; read < 16 && context.GetNextOptional() is { } token; read++)
            {
                switch (token)
                {
                    case Operator { Character: '(' }:
                        depth++;
                        continue;
                    case Operator { Character: ')' }:
                        depth--;
                        continue;
                    case ReservedKeyword { Keyword: Keyword.Top or Keyword.Into or Keyword.From or Keyword.Percent }:
                        continue;
                    case AtPrefixedString when depth == 0:
                        return true;
                    default:
                        if (depth > 0)
                            continue;
                        return false;
                }
            }
            return false;
        }
        finally
        {
            context.RestoreCheckpoint(checkpoint);
        }
    }

    /// <summary>
    /// Wraps a mutation statement (INSERT / UPDATE / DELETE / MERGE) with
    /// statement-level atomicity. Routes mutations to the connection's
    /// active transaction's <see cref="UndoLog"/> when one exists (an
    /// explicit <c>BeginTransaction</c>); otherwise creates a fresh
    /// per-statement log (auto-commit). In both cases the
    /// statement captures a marker at entry; on exception only the entries
    /// appended this statement are unwound, which matches SQL Server's
    /// "failed statement leaves the surrounding transaction alive" behavior
    /// (probe-confirmed 2026-05-08). Identity / rowversion counters bypass
    /// the log entirely.
    /// </summary>
    private static SimulatedStatementOutcome RunMutation(ParserContext context, Func<ParserContext, SimulatedStatementOutcome> body)
    {
        context.Batch.CurrentStatement.WritesRows = true;
        // A table-variable target takes no transaction; its parser clears this.
        context.Batch.CurrentStatement.TransactedWrite = true;
        // A table variable stands outside the transaction, so a doomed one
        // lets it be written (probed 2026-10-02 against SQL Server 2025).
        if (!context.Batch.IsSkipping
            && (context.Connection.CurrentTransaction is { Doomed: true } || context.Connection is { CurrentTransaction: null, TriggerStatementUndoLog: not null, TriggerUnitDoomed: true })
            && !WritesTableVariable(context))
        {
            RejectWriteInDoomedTransaction(context.Connection);
        }
        // A write opens an implicit transaction, a table variable's included.
        context.Batch.BeginImplicitTransaction();
        var tx = context.Connection.CurrentTransaction;
        // Inside a trigger, join the firing statement's scope instead of
        // opening one: the parent statement and everything its triggers wrote
        // roll back as a single unit. Under an explicit transaction the shared
        // tx.UndoLog already gives that, so this is the auto-commit path.
        var enclosingTriggerLog = tx is null ? context.Connection.TriggerStatementUndoLog : null;
        var log = tx?.UndoLog ?? enclosingTriggerLog ?? new UndoLog(context.Connection.Simulation.LobReclamation);
        var marker = log.Position;
        // Table variables get a parallel per-statement undo log so multi-row
        // mutations roll back atomically on mid-statement failure (probe-
        // confirmed: real SQL Server rolls back partial @t writes on row-
        // level errors). The log is dropped on statement success, so
        // ROLLBACK TRAN never sees these entries — matches the non-
        // transactional invariant.
        var tableVarLog = new UndoLog(reclamation: null);
        // Auto-commit statements get a statement-scoped pending-version
        // list; explicit transactions route entries onto the tx's
        // accumulating list (finalized at COMMIT, discarded at ROLLBACK).
        // The marker captures the tx-list size on entry so a statement-
        // atomic mid-execution failure can discard only the entries this
        // statement added.
        var versionEntriesMarker = tx?.PendingVersionEntries.Count ?? 0;
        // Null when this statement joined an enclosing scope — the success and
        // failure paths below both key on it, so a joined statement neither
        // commits the shared log nor finalizes its versions. The statement
        // that fired the trigger does both, once, for the whole unit.
        var statementVersionEntries = tx is null && enclosingTriggerLog is null ? new List<PendingVersionEntry>() : null;

        var savedLog = context.Batch.CurrentUndoLog;
        var savedTableVarLog = context.Batch.CurrentTableVarUndoLog;
        var savedStatementVersionEntries = context.Batch.CurrentStatementVersionEntries;
        context.Batch.CurrentUndoLog = log;
        context.Batch.CurrentTableVarUndoLog = tableVarLog;
        context.Batch.CurrentStatementVersionEntries = enclosingTriggerLog is null
            ? statementVersionEntries
            : context.Connection.TriggerStatementVersionEntries;
        try
        {
            var outcome = RunMutationBodyUntilSettled();
            if (statementVersionEntries is { } autoCommitEntries)
            {
                // FinalizePendingEntries clears the list, so capture whether
                // this statement versioned anything before the call.
                var versionedThisStatement = autoCommitEntries.Count > 0;
                Storage.VersionStore.FinalizePendingEntries(autoCommitEntries, context.Connection.Simulation);
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
                var autoCommitSimulation = context.Connection.Simulation;
                if (versionedThisStatement && autoCommitSimulation.ActiveSnapshotTxs.IsEmptyLockFree())
                    Storage.VersionStore.RunGarbageCollection(autoCommitSimulation, context.CurrentDatabase);
            }
            // Table-variable writes are non-transactional and final on
            // statement success regardless of any enclosing tx, so their
            // throwaway log always commits here.
            tableVarLog.Commit();
            return outcome;
        }
        catch
        {
            RewindStatement();
            throw;
        }
        finally
        {
            context.Batch.CurrentUndoLog = savedLog;
            context.Batch.CurrentTableVarUndoLog = savedTableVarLog;
            context.Batch.CurrentStatementVersionEntries = savedStatementVersionEntries;
        }

        void RewindStatement()
        {
            // The heap rewinds before the pending versions go, as a
            // transaction's rollback does.
            log.RollbackTo(marker);
            if (statementVersionEntries is { } autoCommitEntries)
            {
                Storage.VersionStore.DiscardPendingEntries(autoCommitEntries);
            }
            else if (tx is not null && tx.PendingVersionEntries.Count > versionEntriesMarker)
            {
                var added = tx.PendingVersionEntries.GetRange(versionEntriesMarker, tx.PendingVersionEntries.Count - versionEntriesMarker);
                tx.PendingVersionEntries.RemoveRange(versionEntriesMarker, tx.PendingVersionEntries.Count - versionEntriesMarker);
                Storage.VersionStore.DiscardPendingEntries(added, kept: tx.PendingVersionEntries);
            }
            tableVarLog.Rollback();
        }

        // A target row the statement waited on came back deleted while its
        // key stands again (BatchContext.TargetKeyReinserted): real's read
        // meets the key and reads the row it holds, where the walk here met
        // the old address, judged from a stale image or missed it. The
        // statement's writes are rewound and it runs again, its read now
        // finding the key settled. Not inside a trigger's statement, whose
        // versions the firing statement keeps.
        SimulatedStatementOutcome RunMutationBodyUntilSettled()
        {
            var start = context.SaveCheckpoint();
            for (var attempt = 1; ; attempt++)
            {
                context.Batch.TargetKeyReinserted = false;
                var mayRunAgain = context.Batch.TargetWalkMayRunAgain;
                context.Batch.TargetWalkMayRunAgain = attempt < MaxTargetWalks && enclosingTriggerLog is null && !context.Batch.IsSkipping;
                SimulatedStatementOutcome outcome;
                try
                {
                    outcome = RunMutationBody(context, body);
                }
                finally
                {
                    context.Batch.TargetWalkMayRunAgain = mayRunAgain;
                }
                if (!context.Batch.TargetKeyReinserted || attempt == MaxTargetWalks || enclosingTriggerLog is not null || context.Batch.IsSkipping)
                    return outcome;
                RewindStatement();
                context.RestoreCheckpoint(start);
            }
        }
    }
}
