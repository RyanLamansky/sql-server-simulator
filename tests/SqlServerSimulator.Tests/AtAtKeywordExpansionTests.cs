using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for the expanded <c>@@</c>-keyword surface added alongside the
/// scalar-function coverage push: constant-returning ones land in
/// <c>Value</c>'s switch; session/database-state ones get dedicated
/// expression classes. Probe-confirmed defaults against SQL Server 2025
/// (2026-05-22) for the configurable knobs (@@DATEFIRST=7, @@TEXTSIZE=-1,
/// @@OPTIONS=5432, etc.).
/// </summary>
[TestClass]
public sealed class AtAtKeywordExpansionTests
{
    [TestMethod]
    public void AtAt_MaxPrecision_ReturnsByte38()
        => AreEqual((byte)38, new Simulation().ExecuteScalar("select @@max_precision"));

    [TestMethod]
    public void AtAt_MaxConnections_Returns32767()
        => AreEqual(32767, new Simulation().ExecuteScalar("select @@max_connections"));

    [TestMethod]
    public void AtAt_MicrosoftVersion_ReturnsProductVersionEncoding()
        => AreEqual(285216737, new Simulation().ExecuteScalar("select @@microsoftversion"));

    [TestMethod]
    public void AtAt_MicrosoftVersion_ComposesInExpression()
        => AreEqual(17, new Simulation().ExecuteScalar("select @@microsoftversion / 16777216"));

    [TestMethod]
    public void AtAt_Langid_ReturnsZero()
        => AreEqual((short)0, new Simulation().ExecuteScalar("select @@langid"));

    [TestMethod]
    public void AtAt_Language_ReturnsUsEnglish()
        => AreEqual("us_english", new Simulation().ExecuteScalar("select @@language"));

    [TestMethod]
    public void AtAt_ServiceName_ReturnsMssqlServer()
        => AreEqual("MSSQLSERVER", new Simulation().ExecuteScalar("select @@servicename"));

    [TestMethod]
    public void AtAt_ServerName_ReturnsSimulated()
        => AreEqual("SIMULATED", new Simulation().ExecuteScalar("select @@servername"));

    [TestMethod]
    public void AtAt_RemServer_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select @@remserver"));

    [TestMethod]
    public void AtAt_DateFirst_ReturnsSeven()
        => AreEqual((byte)7, new Simulation().ExecuteScalar("select @@datefirst"));

    [TestMethod]
    public void AtAt_TextSize_ReturnsNegativeOne()
        => AreEqual(-1, new Simulation().ExecuteScalar("select @@textsize"));

    [TestMethod]
    public void AtAt_Options_ReturnsDefaultMask()
        => AreEqual(5432, new Simulation().ExecuteScalar("select @@options"));

    [TestMethod]
    public void AtAt_NestLevel_ReturnsZeroOutsideProc()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@nestlevel"));

    [TestMethod]
    public void AtAt_NestLevel_IncrementsInProc()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p as select @@nestlevel");
        AreEqual(1, sim.ExecuteScalar("exec dbo.p"));
    }

    [TestMethod]
    public void AtAt_ProcId_ReturnsZeroOutsideProc()
        => AreEqual(0, new Simulation().ExecuteScalar("select @@procid"));

    [TestMethod]
    public void AtAt_ProcId_ReturnsProcObjectIdInProc()
    {
        var sim = new Simulation();
        sim.ExecuteBatches(
            "create procedure dbo.p as select @@procid");
        var procId = (int?)sim.ExecuteScalar("select object_id('dbo.p')");
        AreEqual(procId, sim.ExecuteScalar("exec dbo.p"));
    }

    [TestMethod]
    public void AtAt_Dbts_ReturnsEightBytes()
    {
        var result = new Simulation().ExecuteScalar("select @@dbts");
        IsTrue(result is byte[] { Length: 8 });
    }

    // A plan cached by one session is reused by the next; the session scalars
    // read the running session, not the one that compiled the plan. The FROM
    // keeps the statement off the FROM-less path, which never caches, and the
    // options set leave the plan-cache key alone (SET LANGUAGE would not).
    [TestMethod]
    public void SessionScalars_ReadTheRunningSessionThroughACachedPlan()
    {
        const string Text = "select concat(@@spid, '|', @@trancount, '|', @@datefirst, '|', @@language, '|', @@langid, '|', @@textsize, '|', @@lock_timeout) from sys.dm_exec_sessions where session_id = @@spid";
        var simulation = new Simulation();
        using var first = simulation.CreateOpenConnection();
        using var second = simulation.CreateOpenConnection();
        using (var read = first.CreateCommand(Text))
            AreEqual("51|0|7|us_english|0|-1|-1", read.ExecuteScalar());
        using (var set = second.CreateCommand("set datefirst 1; set textsize 100; set lock_timeout 5; begin tran"))
            _ = set.ExecuteNonQuery();
        using var again = second.CreateCommand(Text);
        AreEqual("52|1|1|us_english|0|100|5", again.ExecuteScalar());
    }
}
