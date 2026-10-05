using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>UPDATE STATISTICS</c> and the statistics lifecycle around it: a
/// statistic describes the rows as it was last built — at its index's
/// creation, by an update, or by the automatic creation and update a query's
/// predicate triggers — which <c>sys.dm_db_stats_properties</c> reports.
/// Every error below was probed against SQL Server 2025 on 2026-09-25 and
/// 2026-10-05.
/// </summary>
[TestClass]
public sealed class UpdateStatisticsTests
{
    private const string Setup = """
        create table t (id int primary key, v int);
        create index ix on t (v);
        create statistics st on t (v);
        """;

    [TestMethod]
    [DataRow("update statistics t")]
    [DataRow("update statistics dbo.t ix")]
    [DataRow("update statistics t (ix, st)")]
    [DataRow("update statistics t with fullscan")]
    [DataRow("update statistics t with sample 50 percent")]
    [DataRow("update statistics t with sample 100 rows, norecompute")]
    [DataRow("update statistics t with resample, all")]
    [DataRow("update statistics t with fullscan, columns")]
    [DataRow("update statistics t with index")]
    [DataRow("update statistics t with persist_sample_percent = on, fullscan")]
    [DataRow("update statistics t with maxdop = 2, auto_drop = off, incremental = off")]
    [DataRow("update statistics t with sample 0 percent")]
    [DataRow("update statistics t with rowcount = 5, pagecount = 1")]
    public void AcceptedForms_Run(string statement)
        => AreEqual(1, new Simulation().ExecuteScalar($"{Setup} {statement}; select 1"));

    [TestMethod]
    [DataRow("update statistics nosuch", 2706, "Table 'nosuch' does not exist.")]
    [DataRow("update statistics dbo.nosuch", 2706, "Table 'nosuch' does not exist.")]
    [DataRow("update statistics t nosuchstat", 2727, "Cannot find index 'nosuchstat'.")]
    [DataRow("update statistics t (ix, nosuch)", 2727, "Cannot find index 'nosuch'.")]
    [DataRow("update statistics t with bogus", 155, "'bogus' is not a recognized UPDATE STATISTICS option.")]
    [DataRow("update statistics t with sample 150 percent", 1031, "Percent values must be between 0 and 100.")]
    [DataRow("update statistics t with fullscan, sample 10 percent", 1052, "Conflicting UPDATE STATISTICS options \"PERCENT\" and \"FULLSCAN\".")]
    [DataRow("update statistics t with resample, fullscan", 1052, "Conflicting UPDATE STATISTICS options \"RESAMPLE\" and \"FULLSCAN\".")]
    [DataRow("update statistics t with sample 10 rows, fullscan", 1052, "Conflicting UPDATE STATISTICS options \"ROWS\" and \"FULLSCAN\".")]
    [DataRow("update statistics t with sample 10 percent, resample", 1052, "Conflicting UPDATE STATISTICS options \"PERCENT\" and \"RESAMPLE\".")]
    [DataRow("update statistics t with all, columns", 1052, "Conflicting UPDATE STATISTICS options \"ALL\" and \"COLUMNS\".")]
    [DataRow("update statistics t with fullscan, fullscan", 1039, "Option 'FULLSCAN' is specified more than once.")]
    [DataRow("update statistics t with incremental = on", 9108, "This type of statistics is not supported to be incremental.")]
    [DataRow("update statistics t with resample on partitions (1)", 9111, "UPDATE STATISTICS ON PARTITIONS syntax is not supported for non-incremental statistics.")]
    [DataRow("update statistics t with sample 10", 102, "Incorrect syntax near '10'.")]
    [DataRow("update statistics", 102, "Incorrect syntax near 'statistics'.")]
    [DataRow("update statistics t with stats_stream = 0x00", 1092, "In this context 0 statistics name(s) cannot be specified for option 'STATS_STREAM'.")]
    [DataRow("update statistics t with persist_sample_percent = on", 153, "Invalid usage of the option PERSIST_SAMPLE_PERCENT in the UPDATE STATISTICS statement.")]
    public void Refusals_MatchReal(string statement, int number, string message)
        => new Simulation().AssertSqlError($"{Setup} {statement}", number, message);

    [TestMethod]
    public void ViewWithoutAnIndex_IsNotATable()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches("create table t (id int)", "create view vw as select id from t");
        AreEqual(7, simulation.AssertSqlError("update statistics vw", 2706).State);
    }

    /// <summary>
    /// Each statistic an update reaches takes <c>no_recompute</c> from whether
    /// this update wrote <c>NORECOMPUTE</c>, clearing an earlier one.
    /// </summary>
    [TestMethod]
    public void NoRecompute_FollowsTheLatestUpdate()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup + "create statistics st2 on t (id);");
        const string Read = "select string_agg(concat(name, ':', cast(no_recompute as int)), ',') within group (order by name) from sys.stats where name like 'st%'";
        _ = simulation.ExecuteNonQuery("update statistics t with norecompute");
        AreEqual("st:1,st2:1", simulation.ExecuteScalar(Read));
        _ = simulation.ExecuteNonQuery("update statistics t st");
        AreEqual("st:0,st2:1", simulation.ExecuteScalar(Read));
    }

    private const string Properties = """
        select concat(s.name, ':', p.rows, ':', p.modification_counter)
        from sys.stats s cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p
        where s.object_id = object_id('t') and s.name = 'st'
        """;

    /// <summary>
    /// A statistic built over an empty table describes nothing until it is
    /// built again, however many rows arrive (probed 2026-10-05 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void AStatisticDescribesTheRowsItWasBuiltOver()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); create statistics st on t (v) with norecompute; insert t values (1, 1), (2, 2)");
        AreEqual("st::", sim.ExecuteScalar(Properties));
        _ = sim.ExecuteNonQuery("update statistics t st");
        AreEqual("st:2:0", sim.ExecuteScalar(Properties));
        _ = sim.ExecuteNonQuery("insert t values (3, 3); update t set v = 0 where id = 1");
        AreEqual("st:2:2", sim.ExecuteScalar(Properties));
    }

    /// <summary>
    /// A query's predicate on a column no statistic leads creates an
    /// automatic one, <c>_WA_Sys_&lt;column id&gt;_&lt;object id&gt;</c> in
    /// hex, and refreshes it once the column's modifications pass real's
    /// threshold (500 under 500 rows).
    /// </summary>
    [TestMethod]
    public void APredicateCreatesAndRefreshesAnAutomaticStatistic()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int primary key, v int); insert t values (1, 1), (2, 2), (3, 3); select count(*) from t where v = 2");
        const string Auto = """
            select concat(left(s.name, 17), ':', s.auto_created, ':', s.auto_drop, ':', p.rows)
            from sys.stats s cross apply sys.dm_db_stats_properties(s.object_id, s.stats_id) p
            where s.object_id = object_id('t') and s.auto_created = 1
            """;
        AreEqual("_WA_Sys_00000002_:1:1:3", sim.ExecuteScalar(Auto));
        _ = sim.ExecuteNonQuery("insert t select value + 3, value from generate_series(1, 499)");
        _ = sim.ExecuteScalar("select count(*) from t where v = 5");
        AreEqual("_WA_Sys_00000002_:1:1:3", sim.ExecuteScalar(Auto));
        // A cached plan doesn't check its statistics again, so the query
        // that refreshes them is a new text.
        _ = sim.ExecuteNonQuery("insert t values (1000, 0)");
        _ = sim.ExecuteScalar("select count(*) from t where v = 6");
        AreEqual("_WA_Sys_00000002_:1:1:503", sim.ExecuteScalar(Auto));
    }

    [TestMethod]
    public void PersistSamplePercent_IsRecorded()
        => IsTrue(new Simulation().ExecuteScalar<bool>($"""
            {Setup}
            update statistics t st with sample 50 percent, persist_sample_percent = on;
            select has_persisted_sample from sys.stats where name = 'st'
            """));
}
