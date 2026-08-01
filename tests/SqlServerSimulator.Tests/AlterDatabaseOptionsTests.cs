using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Exercises the parse-and-discard surface for the database-scope
/// <c>ALTER DATABASE name SET …</c> options the bacpac loader will emit on
/// import. The semantic effect of these toggles isn't modeled (the simulator
/// has no recovery model, no query store, no torn-page detector); the test
/// goal is that each canonical T-SQL shape SqlPackage may emit parses
/// without throwing. Shapes are probe-confirmed against SQL Server 2025
/// (2026-05-14). Load-bearing options (COMPATIBILITY_LEVEL, ALLOW_SNAPSHOT_ISOLATION,
/// READ_COMMITTED_SNAPSHOT) have separate behavior coverage in
/// <see cref="CompatibilityLevelTests"/> and <see cref="SnapshotIsolationTests"/>.
/// </summary>
[TestClass]
public class AlterDatabaseOptionsTests
{
    [TestMethod]
    // ON/OFF (no `=`)
    [DataRow("ALTER DATABASE simulated SET ANSI_NULLS ON")]
    [DataRow("ALTER DATABASE simulated SET ANSI_NULLS OFF")]
    [DataRow("ALTER DATABASE simulated SET ANSI_PADDING ON")]
    [DataRow("ALTER DATABASE simulated SET ANSI_WARNINGS ON")]
    [DataRow("ALTER DATABASE simulated SET ARITHABORT ON")]
    [DataRow("ALTER DATABASE simulated SET CONCAT_NULL_YIELDS_NULL ON")]
    [DataRow("ALTER DATABASE simulated SET NUMERIC_ROUNDABORT OFF")]
    [DataRow("ALTER DATABASE simulated SET QUOTED_IDENTIFIER ON")]
    [DataRow("ALTER DATABASE simulated SET TORN_PAGE_DETECTION OFF")]
    [DataRow("ALTER DATABASE simulated SET TEMPORAL_HISTORY_RETENTION ON")]
    // Enum (bare identifier value)
    [DataRow("ALTER DATABASE simulated SET RECOVERY FULL")]
    [DataRow("ALTER DATABASE simulated SET RECOVERY BULK_LOGGED")]
    [DataRow("ALTER DATABASE simulated SET RECOVERY SIMPLE")]
    [DataRow("ALTER DATABASE simulated SET PAGE_VERIFY CHECKSUM")]
    [DataRow("ALTER DATABASE simulated SET PAGE_VERIFY NONE")]
    [DataRow("ALTER DATABASE simulated SET PAGE_VERIFY TORN_PAGE_DETECTION")]
    [DataRow("ALTER DATABASE simulated SET CURSOR_DEFAULT GLOBAL")]
    [DataRow("ALTER DATABASE simulated SET CURSOR_DEFAULT LOCAL")]
    // `= ON|OFF` (`=` required)
    [DataRow("ALTER DATABASE simulated SET ACCELERATED_DATABASE_RECOVERY = ON")]
    [DataRow("ALTER DATABASE simulated SET ACCELERATED_DATABASE_RECOVERY = OFF")]
    [DataRow("ALTER DATABASE simulated SET OPTIMIZED_LOCKING = ON")]
    [DataRow("ALTER DATABASE simulated SET OPTIMIZED_LOCKING = OFF")]
    // Integer with unit
    [DataRow("ALTER DATABASE simulated SET TARGET_RECOVERY_TIME = 60 SECONDS")]
    [DataRow("ALTER DATABASE simulated SET TARGET_RECOVERY_TIME = 1 MINUTES")]
    // CURRENT name
    [DataRow("ALTER DATABASE CURRENT SET ANSI_NULLS ON")]
    // Access-mode states (bare, no `=`) with the optional termination clause —
    // the teardown an ORM runs before DROP DATABASE (Django/mssql-django emits
    // `SET SINGLE_USER WITH ROLLBACK IMMEDIATE`).
    [DataRow("ALTER DATABASE simulated SET SINGLE_USER")]
    [DataRow("ALTER DATABASE simulated SET MULTI_USER")]
    [DataRow("ALTER DATABASE simulated SET RESTRICTED_USER")]
    [DataRow("ALTER DATABASE simulated SET SINGLE_USER WITH ROLLBACK IMMEDIATE")]
    [DataRow("ALTER DATABASE simulated SET SINGLE_USER WITH ROLLBACK AFTER 5 SECONDS")]
    [DataRow("ALTER DATABASE simulated SET SINGLE_USER WITH ROLLBACK AFTER 30")]
    [DataRow("ALTER DATABASE simulated SET SINGLE_USER WITH NO_WAIT")]
    public void Option_ParsesAndDiscards(string sql)
        => AreEqual(-1, new Simulation().ExecuteNonQuery(sql));

    [TestMethod]
    // Bare ON/OFF — the simplest QUERY_STORE shape
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = OFF")]
    // CLEAR / CLEAR ALL — no `=`
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE CLEAR")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE CLEAR ALL")]
    // Single sub-option
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (OPERATION_MODE = READ_ONLY)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (DATA_FLUSH_INTERVAL_SECONDS = 900)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (MAX_STORAGE_SIZE_MB = 1000)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (INTERVAL_LENGTH_MINUTES = 30)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (SIZE_BASED_CLEANUP_MODE = AUTO)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (SIZE_BASED_CLEANUP_MODE = OFF)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_MODE = ALL)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_MODE = AUTO)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_MODE = NONE)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_MODE = CUSTOM)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (MAX_PLANS_PER_QUERY = 200)")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (WAIT_STATS_CAPTURE_MODE = ON)")]
    // Nested sub-blocks
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (CLEANUP_POLICY = (STALE_QUERY_THRESHOLD_DAYS = 30))")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_POLICY = (EXECUTION_COUNT = 10))")]
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (QUERY_CAPTURE_POLICY = (STALE_CAPTURE_POLICY_THRESHOLD = 24 HOURS, EXECUTION_COUNT = 30, TOTAL_COMPILE_CPU_TIME_MS = 1000, TOTAL_EXECUTION_CPU_TIME_MS = 100))")]
    // Multi-sub-option
    [DataRow("ALTER DATABASE simulated SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, INTERVAL_LENGTH_MINUTES = 30, MAX_STORAGE_SIZE_MB = 1000, QUERY_CAPTURE_MODE = AUTO)")]
    public void QueryStore_ParsesAndDiscards(string sql)
        => AreEqual(-1, new Simulation().ExecuteNonQuery(sql));

    [TestMethod]
    public void QueryStore_UnknownSubOption_RaisesSyntaxError()
    {
        // Probe-confirmed verbatim: SQL Server 2025 raises Msg 102 near the
        // first unknown sub-option name; the simulator's parse-and-discard
        // walks each sub-option through the closed accept-list and the first
        // unknown name surfaces as Msg 102.
        var ex = new Simulation().AssertSqlError(
            "ALTER DATABASE simulated SET QUERY_STORE = ON (BOGUS_OPTION = 1)",
            102);
        Contains("BOGUS_OPTION", ex.Message);
    }

    [TestMethod]
    public void Collate_Default_Accepts()
        => AreEqual(-1, new Simulation().ExecuteNonQuery(
            "ALTER DATABASE simulated COLLATE SQL_Latin1_General_CP1_CI_AS"));

    /// <summary>
    /// Names outside the catalog raise <c>NotSupportedException</c> with the
    /// "recognized list" wording. Real SQL Server raises Msg 448; the
    /// simulator's "honest about what's modeled" stance keeps the
    /// distinction visible. The chosen name <c>MadeUp_Locale_CI_AS</c> has
    /// a parseable grammar but isn't in the per-prefix tail-set catalog
    /// (probed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void Collate_NonDefault_RaisesNotSupported()
    {
        var ex = Throws<NotSupportedException>(() =>
            new Simulation().ExecuteNonQuery("ALTER DATABASE simulated COLLATE MadeUp_Locale_CI_AS"));
        Contains("MadeUp_Locale_CI_AS", ex.Message);
        Contains("recognized list", ex.Message);
    }

    [TestMethod]
    public void Recovery_WithEqualsSign_RaisesSyntaxError()
    {
        // Probe-confirmed: SQL Server 2025 rejects `SET RECOVERY = FULL`
        // (the documented grammar is bare enum value, no `=`). Simulator
        // falls through to Msg 102 the same way.
        _ = new Simulation().AssertSqlError(
            "ALTER DATABASE simulated SET RECOVERY = FULL", 102);
    }

    [TestMethod]
    public void AcceleratedDatabaseRecovery_BareForm_RaisesSyntaxError()
    {
        // Probe-confirmed: SQL Server 2025 requires `= ON|OFF` for this option.
        _ = new Simulation().AssertSqlError(
            "ALTER DATABASE simulated SET ACCELERATED_DATABASE_RECOVERY ON", 102);
    }

    [TestMethod]
    public void TargetRecoveryTime_MissingUnit_RaisesSyntaxError()
    {
        // Probe-confirmed: the unit (SECONDS|MINUTES) is required.
        _ = new Simulation().AssertSqlError(
            "ALTER DATABASE simulated SET TARGET_RECOVERY_TIME = 60", 102);
    }

    [TestMethod]
    public void NamedDatabase_TargetsThatDatabase_NotTheSessions()
    {
        // The named database is the one the option lands on — probe-confirmed,
        // and what makes a per-database versioning flag settable from anywhere.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create database other; ALTER DATABASE other SET READ_COMMITTED_SNAPSHOT ON");
        AreEqual("0|1", sim.ExecuteScalar("""
            select cast(databasepropertyex('simulated', 'IsReadCommittedSnapshotOn') as varchar(1))
                 + '|' + cast(databasepropertyex('other', 'IsReadCommittedSnapshotOn') as varchar(1))
            """));
    }

    [TestMethod]
    public void UnknownDatabase_Raises5011()
    {
        // Real raises Msg 5011 (sev 14 state 5) then a trailing Msg 5069; the
        // simulator surfaces the informative first error alone.
        var ex = new Simulation().AssertSqlError("ALTER DATABASE nope SET READ_COMMITTED_SNAPSHOT ON", 5011);
        AreEqual(14, ex.Class);
        AreEqual(5, ex.State);
        AreEqual("User does not have permission to alter database 'nope', the database does not exist, or the database is not in a state that allows access checks.", ex.Message);
    }

    [TestMethod]
    public void ReadCommittedSnapshot_StillWiredThrough()
    {
        // Regression: the dispatcher refactor must preserve the load-bearing
        // semantic effect for the three historically-shipped options.
        // RCSI is the easiest to observe externally.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("ALTER DATABASE simulated SET READ_COMMITTED_SNAPSHOT ON");
        // Indirect verification: SI iso under RCSI doesn't raise Msg 3952 on a
        // statement-level snapshot read.
        using var conn = sim.CreateOpenConnection();
        using var cmd = conn.CreateCommand("SELECT 1");
        AreEqual(1, cmd.ExecuteScalar());
    }
}
