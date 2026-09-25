using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>UPDATE STATISTICS</c>: nothing to rebuild, so the statement is its
/// validation plus the <c>NORECOMPUTE</c> flag a user-created statistic
/// carries. Every error below was probed against SQL Server 2025 on 2026-09-25.
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
}
