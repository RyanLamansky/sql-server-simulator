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

    /// <summary>
    /// Outside a module <c>@@PROCID</c> is the batch's ad hoc object id: a
    /// nonzero hash of the batch text below 2^30, the same for equal texts, and
    /// a dynamic batch's own (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void AtAt_ProcId_IsTheBatchTextsAdHocIdOutsideProc()
    {
        var sim = new Simulation();
        var adHoc = (int)sim.ExecuteScalar("select @@procid")!;
        IsTrue(adHoc is > 0 and < 1 << 30, $"{adHoc}");
        AreEqual(adHoc, sim.ExecuteScalar("select @@procid"));
        AreEqual(adHoc, sim.ExecuteScalar("exec ('select @@procid')"));
        AreNotEqual(adHoc, sim.ExecuteScalar("select  @@procid"));
        AreEqual(DBNull.Value, sim.ExecuteScalar("select object_name(@@procid)"));
    }

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

    [TestMethod]
    public void TheStringKeywords_CarryRealsLengths()
        => AreEqual("256|256|600|256", new Simulation().ExecuteScalar("""
            select @@servername a, @@servicename b, @@version c, @@remserver d into #x;
            select string_agg(cast(max_length as varchar(5)), '|') within group (order by column_id) from tempdb.sys.columns where object_id = object_id('tempdb..#x')
            """));

    /// <summary>
    /// An <c>@@</c> name no built-in claims — spelled as real spells none,
    /// underscores included — is an undeclared variable, Msg 137 at state 2,
    /// and state 1 as an assignment's target (probed 2026-10-16 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("select @@nope", "@@nope", (byte)2)]
    [DataRow("select @@nope x", "@@nope", (byte)2)]
    [DataRow("select 1 + @@nope", "@@nope", (byte)2)]
    [DataRow("print @@nope", "@@nope", (byte)2)]
    [DataRow("declare @a int = @@nope", "@@nope", (byte)2)]
    [DataRow("if @@nope = 1 select 1", "@@nope", (byte)2)]
    [DataRow("select @@nope('x')", "@@nope", (byte)2)]
    [DataRow("select @@", "@@", (byte)2)]
    [DataRow("select @@locktimeout", "@@locktimeout", (byte)2)]
    [DataRow("select @@cpu__busy", "@@cpu__busy", (byte)2)]
    [DataRow("exec sp_executesql N'select @@nope'", "@@nope", (byte)2)]
    [DataRow("set @@nope = 1", "@@nope", (byte)1)]
    [DataRow("select @@nope = 1", "@@nope", (byte)1)]
    [DataRow("select @@nope += 1", "@@nope", (byte)1)]
    [DataRow("select @@nope = @@nope2", "@@nope2", (byte)2)]
    public void AtAt_UnknownName_IsAnUndeclaredVariable(string sql, string name, byte state)
    {
        var error = new Simulation().AssertSqlError(sql, 137);
        AreEqual(($"Must declare the scalar variable \"{name}\".", (byte)15, state, 1), (error.Errors[0].Message, error.Class, error.State, error.Errors.Count));
    }

    /// <summary>
    /// Such a name is an ordinary variable once declared — a local, a table
    /// variable, a cursor variable, a procedure's, function's or dynamic
    /// batch's parameter — while a built-in's name is Msg 102 where a variable
    /// is declared (probed 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("declare @@x int = 5; set @@x += 1; select @@X", 6)]
    [DataRow("declare @@x int; select @@x = 9; select @@x", 9)]
    [DataRow("declare @@ int = 3; select @@", 3)]
    [DataRow("declare @@@x int = 4; select @@@x", 4)]
    [DataRow("declare @@t table (a int); insert @@t values (7); select a from @@t", 7)]
    [DataRow("declare c cursor for select 8; declare @@c cursor; set @@c = c; open @@c; declare @v int; fetch @@c into @v; select @v", 8)]
    [DataRow("exec sp_executesql N'select @@p', N'@@p int', @@p = 4", 4)]
    public void AtAt_DeclaredUnknownName_IsAVariable(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    public void AtAt_UnknownName_NamesAParameter()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            "create procedure p @@p int, @q int = 2 as select @@p * @q",
            "create function f (@@a int) returns int as begin return @@a * 2 end");
        AreEqual(6, simulation.ExecuteScalar("exec p 3"));
        AreEqual(16, simulation.ExecuteScalar("exec p @@p = 8"));
        AreEqual(8, simulation.ExecuteScalar("select dbo.f(4)"));
    }

    [TestMethod]
    [DataRow("declare @@rowcount int")]
    [DataRow("declare @@SPID int = 7")]
    [DataRow("declare @@lock_timeout int")]
    public void AtAt_BuiltInName_IsNoVariable(string sql)
        => _ = new Simulation().AssertSqlError(sql, 102);

    [TestMethod]
    public void AtAt_UnderscoredBuiltIn_ReadsAsRealSpellsIt()
        => AreEqual(-1, new Simulation().ExecuteScalar("select @@LOCK_TIMEOUT"));
}
