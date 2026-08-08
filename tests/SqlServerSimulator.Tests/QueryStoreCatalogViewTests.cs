using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the Query Store surface — the configuration
/// <c>ALTER DATABASE … SET QUERY_STORE</c> retains and
/// <c>sys.database_query_store_options</c> projects, the always-empty capture
/// views, and the SSMS probe batch that reads them. Every expectation is
/// probe-confirmed against SQL Server 2025 (2026-08-08) except where a comment
/// says otherwise.
/// </summary>
[TestClass]
public sealed class QueryStoreCatalogViewTests
{
    /// <summary>
    /// The exact SSMS Query Store probe batch: OBJECT_ID gates the block, the
    /// first SELECT reads actual_state (2, the READ_WRITE a fresh database
    /// inherits from <c>model</c>), the nested IF EXISTS over the always-empty
    /// runtime-stats view falls to its ELSE.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void SsmsQueryStoreProbeBatch_ReturnsStateThenZero()
    {
        using var reader = new Simulation().ExecuteReader(
            "IF OBJECT_ID (N'[sys].[database_query_store_options]') IS NOT NULL " +
            "BEGIN " +
            "SELECT ISNULL(actual_state, -2) FROM sys.database_query_store_options; " +
            "IF EXISTS (SELECT TOP(1) 1 FROM sys.query_store_runtime_stats) SELECT 1 ELSE SELECT 0; " +
            "END");

        IsTrue(reader.Read());
        AreEqual(2, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(reader.Read());

        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual(0, Convert.ToInt32(reader.GetValue(0)));
        IsFalse(reader.Read());

        IsFalse(reader.NextResult());
    }

    [TestMethod]
    public void DatabaseQueryStoreOptions_ObjectId_ResolvesToInt()
        => IsInstanceOfType<int>(new Simulation().ExecuteScalar(
            "SELECT OBJECT_ID(N'[sys].[database_query_store_options]')"));

    /// <summary>
    /// The whole defaults row for a fresh database, which SQL Server 2025
    /// inherits from <c>model</c>: on in READ_WRITE, capture mode AUTO,
    /// size-based cleanup AUTO, wait stats ON, and the four capture-policy
    /// columns NULL because the mode isn't CUSTOM.
    /// </summary>
    [TestMethod]
    public void DatabaseQueryStoreOptions_FreshDatabase_ReportsModelDefaults()
    {
        using var reader = new Simulation().ExecuteReader("""
            select desired_state, desired_state_desc, actual_state, actual_state_desc, readonly_reason,
                   current_storage_size_mb, flush_interval_seconds, interval_length_minutes,
                   max_storage_size_mb, stale_query_threshold_days, max_plans_per_query,
                   query_capture_mode, query_capture_mode_desc,
                   capture_policy_execution_count, capture_policy_total_compile_cpu_time_ms,
                   capture_policy_total_execution_cpu_time_ms, capture_policy_stale_threshold_hours,
                   size_based_cleanup_mode, size_based_cleanup_mode_desc,
                   wait_stats_capture_mode, wait_stats_capture_mode_desc, actual_state_additional_info
            from sys.database_query_store_options
            """);

        IsTrue(reader.Read());
        AreEqual((short)2, reader.GetInt16(0));
        AreEqual("READ_WRITE", reader.GetString(1));
        AreEqual((short)2, reader.GetInt16(2));
        AreEqual("READ_WRITE", reader.GetString(3));
        AreEqual(0, reader.GetInt32(4));
        AreEqual(0L, reader.GetInt64(5));
        AreEqual(900L, reader.GetInt64(6));
        AreEqual(60L, reader.GetInt64(7));
        AreEqual(1000L, reader.GetInt64(8));
        AreEqual(30L, reader.GetInt64(9));
        AreEqual(200L, reader.GetInt64(10));
        AreEqual((short)2, reader.GetInt16(11));
        AreEqual("AUTO", reader.GetString(12));
        for (var i = 13; i <= 16; i++)
            IsTrue(reader.IsDBNull(i), $"capture-policy column {i}");
        AreEqual((short)1, reader.GetInt16(17));
        AreEqual("AUTO", reader.GetString(18));
        AreEqual((short)1, reader.GetInt16(19));
        AreEqual("ON", reader.GetString(20));
        AreEqual(string.Empty, reader.GetString(21));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// <c>master</c> and <c>tempdb</c> project no row at all — they're the two
    /// databases real refuses to host a store on. <c>model</c> and <c>msdb</c>
    /// each get one like a user database, <c>model</c>'s on (which is why a
    /// new database's is) and <c>msdb</c>'s off.
    /// </summary>
    [TestMethod]
    [DataRow("master", null)]
    [DataRow("tempdb", null)]
    [DataRow("model", "READ_WRITE")]
    [DataRow("msdb", "OFF")]
    public void DatabaseQueryStoreOptions_SystemDatabases_SplitByHostability(string database, string? expected)
    {
        using var reader = new Simulation().ExecuteReader(
            $"select actual_state_desc from {database}.sys.database_query_store_options");

        if (expected is null)
        {
            IsFalse(reader.Read());
            return;
        }
        IsTrue(reader.Read());
        AreEqual(expected, reader.GetString(0));
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("master", false)]
    [DataRow("tempdb", false)]
    [DataRow("model", true)]
    [DataRow("msdb", false)]
    [DataRow("simulated", true)]
    public void SysDatabases_IsQueryStoreOn_MatchesSeededState(string database, bool expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"select is_query_store_on from sys.databases where name = '{database}'"));

    /// <summary>
    /// The whole sub-option set lands on the catalog row, including the two
    /// nested policy blocks. <c>OPERATION_MODE</c> overrides the READ_WRITE a
    /// bare <c>= ON</c> would select, and <c>STALE_CAPTURE_POLICY_THRESHOLD</c>
    /// normalizes its unit to the hours the column reports.
    /// </summary>
    [TestMethod]
    public void QueryStoreOptions_SubOptions_AreRetained()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database simulated set query_store = on
            (
                operation_mode = read_only,
                cleanup_policy = (stale_query_threshold_days = 7),
                data_flush_interval_seconds = 300,
                max_storage_size_mb = 512,
                interval_length_minutes = 15,
                size_based_cleanup_mode = off,
                query_capture_mode = custom,
                max_plans_per_query = 42,
                wait_stats_capture_mode = off,
                query_capture_policy = (stale_capture_policy_threshold = 2 days, execution_count = 5,
                                        total_compile_cpu_time_ms = 100, total_execution_cpu_time_ms = 200)
            )
            """);

        AreEqual("READ_ONLY", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(7L, sim.ExecuteScalar("select stale_query_threshold_days from sys.database_query_store_options"));
        AreEqual(300L, sim.ExecuteScalar("select flush_interval_seconds from sys.database_query_store_options"));
        AreEqual(512L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
        AreEqual(15L, sim.ExecuteScalar("select interval_length_minutes from sys.database_query_store_options"));
        AreEqual("OFF", sim.ExecuteScalar("select size_based_cleanup_mode_desc from sys.database_query_store_options"));
        AreEqual("CUSTOM", sim.ExecuteScalar("select query_capture_mode_desc from sys.database_query_store_options"));
        AreEqual(42L, sim.ExecuteScalar("select max_plans_per_query from sys.database_query_store_options"));
        AreEqual("OFF", sim.ExecuteScalar("select wait_stats_capture_mode_desc from sys.database_query_store_options"));
        AreEqual(5, sim.ExecuteScalar("select capture_policy_execution_count from sys.database_query_store_options"));
        AreEqual(100L, sim.ExecuteScalar("select capture_policy_total_compile_cpu_time_ms from sys.database_query_store_options"));
        AreEqual(200L, sim.ExecuteScalar("select capture_policy_total_execution_cpu_time_ms from sys.database_query_store_options"));
        AreEqual(48, sim.ExecuteScalar("select capture_policy_stale_threshold_hours from sys.database_query_store_options"));
        // is_query_store_on tracks desired_state, so READ_ONLY still reads on.
        IsTrue((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'simulated'")!);
    }

    /// <summary>
    /// Turning the store off keeps every configured value — real reports the
    /// last-set sub-options on a disabled store, and re-enabling restores them.
    /// </summary>
    [TestMethod]
    public void QueryStoreOptions_SurviveOff()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database simulated set query_store = on (interval_length_minutes = 15, max_plans_per_query = 42);
            alter database simulated set query_store = off
            """);
        AreEqual("OFF", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(15L, sim.ExecuteScalar("select interval_length_minutes from sys.database_query_store_options"));
        AreEqual(42L, sim.ExecuteScalar("select max_plans_per_query from sys.database_query_store_options"));

        _ = sim.ExecuteNonQuery("alter database simulated set query_store = on");
        AreEqual("READ_WRITE", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(15L, sim.ExecuteScalar("select interval_length_minutes from sys.database_query_store_options"));
    }

    /// <summary>
    /// An <c>= ON</c> carrying only unrelated sub-options still turns the store
    /// on, and <c>CLEAR</c> — which purges captured data, of which there is
    /// none — leaves both the state and the configuration alone.
    /// </summary>
    [TestMethod]
    public void QueryStore_OnWithSubOptionEnables_AndClearLeavesStateAlone()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database simulated set query_store = off;
            alter database simulated set query_store = on (max_storage_size_mb = 777)
            """);
        AreEqual("READ_WRITE", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(777L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));

        _ = sim.ExecuteNonQuery("alter database simulated set query_store clear all");
        AreEqual("READ_WRITE", sim.ExecuteScalar("select desired_state_desc from sys.database_query_store_options"));
        AreEqual(777L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
    }

    /// <summary>
    /// The capture-policy columns are masked by the capture mode, not cleared
    /// by it: leaving CUSTOM projects them NULL, and returning to CUSTOM brings
    /// the same values back. A first switch to CUSTOM with no policy block
    /// reports real's own 30 / 1000 / 100 / 24 defaults.
    /// </summary>
    [TestMethod]
    public void CapturePolicyColumns_AreMaskedByCaptureMode_NotCleared()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = on (query_capture_mode = custom)");
        AreEqual(30, sim.ExecuteScalar("select capture_policy_execution_count from sys.database_query_store_options"));
        AreEqual(1000L, sim.ExecuteScalar("select capture_policy_total_compile_cpu_time_ms from sys.database_query_store_options"));
        AreEqual(100L, sim.ExecuteScalar("select capture_policy_total_execution_cpu_time_ms from sys.database_query_store_options"));
        AreEqual(24, sim.ExecuteScalar("select capture_policy_stale_threshold_hours from sys.database_query_store_options"));

        _ = sim.ExecuteNonQuery("""
            alter database simulated set query_store = on (query_capture_policy = (execution_count = 5));
            alter database simulated set query_store = on (query_capture_mode = auto)
            """);
        AreEqual(1, sim.ExecuteScalar(
            "select count(*) from sys.database_query_store_options where capture_policy_execution_count is null"));

        _ = sim.ExecuteNonQuery("alter database simulated set query_store = on (query_capture_mode = custom)");
        AreEqual(5, sim.ExecuteScalar("select capture_policy_execution_count from sys.database_query_store_options"));
    }

    [TestMethod]
    [DataRow("all", "ALL")]
    [DataRow("auto", "AUTO")]
    [DataRow("none", "NONE")]
    [DataRow("custom", "CUSTOM")]
    public void QueryCaptureMode_EveryValue_RoundTrips(string written, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"alter database simulated set query_store = on (query_capture_mode = {written})");
        AreEqual(expected, sim.ExecuteScalar("select query_capture_mode_desc from sys.database_query_store_options"));
    }

    [TestMethod]
    [DataRow("1 hour", 1)]
    [DataRow("36 hours", 36)]
    [DataRow("1 day", 24)]
    [DataRow("2 days", 48)]
    public void StaleCapturePolicyThreshold_NormalizesUnitToHours(string written, int expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "alter database simulated set query_store = on (query_capture_mode = custom, " +
            $"query_capture_policy = (stale_capture_policy_threshold = {written}))");
        AreEqual(expected, sim.ExecuteScalar("select capture_policy_stale_threshold_hours from sys.database_query_store_options"));
    }

    /// <summary>
    /// Every QUERY_STORE form on <c>master</c> / <c>tempdb</c> raises Msg 12438
    /// — <c>= OFF</c> and <c>CLEAR</c> included, all worded as being about
    /// enabling. Real trails a Msg 5069 the simulator flattens away.
    /// </summary>
    [TestMethod]
    [DataRow("master", "= ON")]
    [DataRow("master", "= OFF")]
    [DataRow("master", "CLEAR")]
    [DataRow("master", "= ON (MAX_STORAGE_SIZE_MB = 500)")]
    [DataRow("tempdb", "= ON")]
    [DataRow("tempdb", "= OFF")]
    public void QueryStore_OnMasterOrTempdb_Raises12438(string database, string tail)
    {
        var ex = new Simulation().AssertSqlError($"alter database {database} set query_store {tail}", 12438);
        AreEqual($"Cannot perform action because Query Store cannot be enabled on system database {database}.", ex.Message);
    }

    /// <summary>A refusal leaves the configuration untouched.</summary>
    [TestMethod]
    public void QueryStore_RefusedOnMaster_LeavesStateOff()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("alter database master set query_store = on", 12438);
        IsFalse((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'master'")!);
    }

    [TestMethod]
    public void QueryStore_OnModelAndMsdb_IsAccepted()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            alter database model set query_store = off;
            alter database msdb set query_store = on
            """);
        IsFalse((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'model'")!);
        IsTrue((bool)sim.ExecuteScalar("select is_query_store_on from sys.databases where name = 'msdb'")!);
    }

    /// <summary>
    /// Grammar rejections real raises too: a policy block's unknown entry, an
    /// <c>OPERATION_MODE</c> value outside the two modes, a
    /// <c>STALE_QUERY_THRESHOLD_DAYS</c> lifted out of its CLEANUP_POLICY
    /// wrapper, a threshold with no unit, and the two flags' wrong spellings.
    /// </summary>
    [TestMethod]
    [DataRow("(CLEANUP_POLICY = (BOGUS = 5))")]
    [DataRow("(QUERY_CAPTURE_POLICY = (BOGUS_THING = 5))")]
    [DataRow("(STALE_QUERY_THRESHOLD_DAYS = 3)")]
    [DataRow("(QUERY_CAPTURE_POLICY = (STALE_CAPTURE_POLICY_THRESHOLD = 5))")]
    [DataRow("(QUERY_CAPTURE_POLICY = (STALE_CAPTURE_POLICY_THRESHOLD = 5 MINUTES))")]
    [DataRow("(OPERATION_MODE = BOGUS)")]
    [DataRow("(QUERY_CAPTURE_MODE = BOGUS)")]
    [DataRow("(WAIT_STATS_CAPTURE_MODE = AUTO)")]
    public void QueryStore_MalformedSubOption_RaisesSyntaxError(string block)
        => _ = new Simulation().AssertSqlError($"alter database simulated set query_store = on {block}", 102);

    /// <summary>
    /// <c>OPERATION_MODE = OFF</c> and <c>SIZE_BASED_CLEANUP_MODE = ON</c> both
    /// name a keyword where the grammar wants an identifier, which is real's
    /// Msg 156 rather than Msg 102.
    /// </summary>
    [TestMethod]
    [DataRow("(OPERATION_MODE = OFF)")]
    [DataRow("(SIZE_BASED_CLEANUP_MODE = ON)")]
    public void QueryStore_KeywordWhereIdentifierExpected_Raises156(string block)
        => _ = new Simulation().AssertSqlError($"alter database simulated set query_store = on {block}", 156);

    /// <summary>A block that raises partway through leaves the old values standing.</summary>
    [TestMethod]
    public void QueryStore_PartiallyParsedBlock_LeavesConfigurationUntouched()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = on (max_storage_size_mb = 512)");
        _ = sim.AssertSqlError(
            "alter database simulated set query_store = on (max_storage_size_mb = 999, bogus_option = 1)", 102);
        AreEqual(512L, sim.ExecuteScalar("select max_storage_size_mb from sys.database_query_store_options"));
    }

    /// <summary>
    /// Every capture view resolves and reads empty — the simulator captures
    /// nothing, whatever the configured state says. A <c>select *</c> is what
    /// makes the column shape load-bearing rather than the row count.
    /// </summary>
    [TestMethod]
    [DataRow("query_context_settings")]
    [DataRow("query_store_plan")]
    [DataRow("query_store_plan_feedback")]
    [DataRow("query_store_plan_forcing_locations")]
    [DataRow("query_store_query")]
    [DataRow("query_store_query_hints")]
    [DataRow("query_store_query_text")]
    [DataRow("query_store_query_variant")]
    [DataRow("query_store_runtime_stats")]
    [DataRow("query_store_runtime_stats_interval")]
    [DataRow("query_store_wait_stats")]
    public void QueryStoreCaptureViews_AreAlwaysEmpty(string view)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database simulated set query_store = on");
        using var reader = sim.ExecuteReader($"select * from sys.{view}");
        IsGreaterThan(0, reader.FieldCount);
        IsFalse(reader.Read());
    }

    /// <summary>
    /// The two views whose rows are fixed metadata rather than captured data:
    /// four replica roles for a user database (none for a system one, which is
    /// the split real reports even for the QS-on <c>model</c>), and one
    /// internal-state row for every database, <c>master</c> included.
    /// </summary>
    [TestMethod]
    public void QueryStoreReplicas_ProjectsTheFourRoles()
    {
        using var reader = new Simulation().ExecuteReader(
            "select replica_group_id, role_type, replica_name from sys.query_store_replicas order by replica_group_id");

        foreach (var (id, role, name) in new[]
        {
            (1L, (short)1, "Primary"),
            (2L, (short)2, "Secondary"),
            (3L, (short)3, "Geo Secondary"),
            (4L, (short)4, "Geo HA Secondary"),
        })
        {
            IsTrue(reader.Read());
            AreEqual(id, reader.GetInt64(0));
            AreEqual(role, reader.GetInt16(1));
            AreEqual(name, reader.GetString(2));
        }
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("master")]
    [DataRow("model")]
    [DataRow("msdb")]
    [DataRow("tempdb")]
    public void QueryStoreReplicas_SystemDatabase_IsEmpty(string database)
        => AreEqual(0, new Simulation().ExecuteScalar($"select count(*) from {database}.sys.query_store_replicas"));

    [TestMethod]
    [DataRow("master")]
    [DataRow("simulated")]
    public void DatabaseQueryStoreInternalState_ProjectsOneZeroedRow(string database)
    {
        using var reader = new Simulation().ExecuteReader(
            $"select pending_message_count, messaging_memory_used_mb from {database}.sys.database_query_store_internal_state");

        IsTrue(reader.Read());
        AreEqual(0L, reader.GetInt64(0));
        AreEqual(0L, reader.GetInt64(1));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// Non-progress-guard regression. A TRY-caught Msg 208 puts the batch in
    /// skip mode; the skipped IF's deferred-name recovery abandons the IF
    /// mid-parse, orphaning <c>SELECT 1 ELSE SELECT 2</c>; the bare ELSE then
    /// raises with the cursor already on a statement boundary. Pre-guard the
    /// recovery scan advanced zero tokens and the dispatch loop never
    /// terminated (the SSMS Query Store probe crash of 2026-07-15). The guard
    /// bounds it: the batch completes promptly and the CATCH returns the first
    /// error, Msg 208.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public void SkipModeOrphanedElse_DoesNotHang_CatchReturnsFirstError()
        => AreEqual(208, new Simulation().ExecuteScalar(
            "BEGIN TRY " +
            "SELECT * FROM nosuchtable1 " +
            "IF EXISTS (SELECT 1 FROM nosuchtable2) SELECT 1 ELSE SELECT 2 " +
            "END TRY BEGIN CATCH SELECT ERROR_NUMBER() END CATCH"));
}
