using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>ALTER DATABASE … SET</c> switches the simulator records for
/// <c>sys.databases</c> and <c>DATABASEPROPERTYEX</c>. Every expectation
/// probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class DatabaseSwitchesTests
{
    private const string AllSwitches = """
        alter database current set auto_close on, auto_shrink on, auto_create_statistics on (incremental = on),
            auto_update_statistics_async on, ansi_nulls on, ansi_padding on, ansi_warnings on, arithabort on,
            concat_null_yields_null on, numeric_roundabort on, quoted_identifier on, cursor_close_on_commit on,
            cursor_default local, ansi_null_default on, parameterization forced, page_verify torn_page_detection,
            date_correlation_optimization on, temporal_history_retention off, recovery simple;
        alter database current set restricted_user
        """;

    [TestMethod]
    public void SysDatabases_ReportsTheRecordedSwitches()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AllSwitches);
        AreEqual("2:1:1:1:1:1:1:1:1:1:1:1:1:1:1:1:1:1:0:TORN_PAGE_DETECTION:RESTRICTED_USER", sim.ExecuteScalar("""
            select concat_ws(':', user_access, 0 + is_auto_close_on, 0 + is_auto_shrink_on, page_verify_option, 0 + is_auto_create_stats_incremental_on,
                0 + is_auto_update_stats_async_on, 0 + is_ansi_null_default_on, 0 + is_ansi_nulls_on, 0 + is_ansi_padding_on, 0 + is_ansi_warnings_on,
                0 + is_arithabort_on, 0 + is_concat_null_yields_null_on, 0 + is_numeric_roundabort_on, 0 + is_quoted_identifier_on,
                0 + is_cursor_close_on_commit_on, 0 + is_local_cursor_default, 0 + is_parameterization_forced, 0 + is_date_correlation_on,
                0 + is_temporal_history_retention_enabled, page_verify_option_desc, user_access_desc)
            from sys.databases where name = db_name()
            """));
    }

    [TestMethod]
    [DataRow("IsAutoClose", "1")]
    [DataRow("IsAnsiNullsEnabled", "1")]
    [DataRow("IsNullConcat", "1")]
    [DataRow("IsLocalCursorsDefault", "1")]
    [DataRow("IsTornPageDetectionEnabled", "1")]
    [DataRow("IsAutoCreateStatisticsIncremental", "1")]
    [DataRow("Recovery", "SIMPLE")]
    [DataRow("UserAccess", "RESTRICTED_USER")]
    public void DatabasePropertyEx_ReportsTheRecordedSwitches(string property, string expected)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(AllSwitches);
        AreEqual(expected, sim.ExecuteScalar($"select cast(databasepropertyex(db_name(), '{property}') as varchar(30))"));
    }

    [TestMethod]
    [DataRow("IsAutoCreateStatistics", "1", "int")]
    [DataRow("IsAnsiNullsEnabled", "0", "int")]
    [DataRow("IsXTPSupported", "1", "tinyint")]
    [DataRow("IsClone", "0", "tinyint")]
    [DataRow("IsFulltextEnabled", "1", "int")]
    [DataRow("Version", "998", "int")]
    [DataRow("IsMemoryOptimizedElevateToSnapshotEnabled", "0", "int")]
    public void DatabasePropertyEx_AnswersTheDefaults(string property, string expected, string baseType)
        => AreEqual($"{expected}:{baseType}", new Simulation().ExecuteScalar(
            $"select concat(cast(databasepropertyex(db_name(), '{property}') as varchar(30)), ':', cast(sql_variant_property(databasepropertyex(db_name(), '{property}'), 'BaseType') as varchar(30)))"));

    [TestMethod]
    public void ComparisonStyle_FollowsTheCollation()
        => AreEqual(196608, new Simulation().ExecuteScalar(
            "create database cs collate Latin1_General_CS_AS; select cast(databasepropertyex('cs', 'ComparisonStyle') as int)"));

    [TestMethod]
    public void TurningAutoCreateStatisticsOff_ClearsItsIncrementalMode()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("alter database current set auto_create_statistics on (incremental = on); alter database current set auto_create_statistics off");
        AreEqual("0:0", sim.ExecuteScalar("select concat(0 + is_auto_create_stats_on, ':', 0 + is_auto_create_stats_incremental_on) from sys.databases where name = db_name()"));
    }
}
