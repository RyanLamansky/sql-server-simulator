namespace SqlServerSimulator;

/// <summary>
/// A remote call's token stream as the provider relays it — a procedure
/// scope whose rowsets close in DONEINPROCs, an <c>EXEC … AT</c>'s each
/// followed by a RETURNSTATUS 0, the server's counts in DONEINPROCs of the
/// call's kind, its other statements reaching the client as nothing but their
/// messages — each sequence as real SQL Server 2025 sends it through a
/// loopback linked server (captured 2026-10-17 with a raw TDS client).
/// </summary>
[TestClass]
public sealed class RemoteCallTokenTests
{
    private static TdsSessionFixture Loopback()
    {
        var simulation = new Simulation();
        simulation.AddRemoteSimulation("SELF", simulation);
        var fixture = new TdsSessionFixture(simulation);
        _ = fixture.RunBatch("exec sp_addlinkedserver 'SELF', 'SQL Server'");
        _ = fixture.RunBatch("create table t (a int)");
        _ = fixture.RunBatch("create procedure p as begin insert t values (1); print 'p'; select 1 as a; insert t values (2), (3) end");
        _ = fixture.RunBatch("create procedure q as begin select 1 as a; raiserror('e', 16, 1) end");
        return fixture;
    }

    [TestMethod]
    [DataRow("exec ('select 1 as a; select 2 as b') at SELF", "ROWS / DONEINPROC C1 MORE|COUNT 1 / RET 0 / ROWS / DONEINPROC C1 MORE|COUNT 1 / RET 0 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('insert simulated..t values (1); select 1 as a; insert simulated..t values (1), (2)') at SELF", "DONEINPROC E0 MORE|COUNT 1 / ROWS / DONEINPROC C1 MORE|COUNT 1 / RET 0 / DONEINPROC E0 MORE|COUNT 2 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('declare @x int; select @x = 1') at SELF", "DONEINPROC E0 MORE 0 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('select 1 as a; print ''x''') at SELF", "ROWS / DONEINPROC C1 MORE|COUNT 1 / RET 0 / INFO 0 / DONEINPROC E0 MORE 0 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('select 1/0 as a; select 2 as b') at SELF", "ERR 8134 / DONEINPROC E0 MORE|ERROR 0 / ROWS / DONEINPROC C1 MORE|COUNT 1 / RET 0 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('select 1/0 as a') at SELF", "ERR 8134 / DONEPROC E0 ERROR 0")]
    [DataRow("exec SELF.simulated.dbo.p", "DONEINPROC E0 MORE|COUNT 1 / INFO 0 / DONEINPROC E0 MORE 0 / ROWS / DONEINPROC C1 MORE|COUNT 1 / DONEINPROC E0 MORE|COUNT 2 / DONEPROC E0 FINAL 0")]
    [DataRow("exec SELF.simulated.dbo.q", "ROWS / DONEINPROC C1 MORE|COUNT 1 / ERR 50000 / DONEPROC E0 ERROR 0")]
    [DataRow("set nocount on; exec SELF.simulated.dbo.p; set nocount off", "DONE B9 MORE 0 / INFO 0 / ROWS / DONEINPROC C1 MORE 1 / DONEPROC E0 MORE 0 / DONE BA FINAL 0")]
    public void RemoteCall_ClosesAsTheProviderRelaysIt(string sql, string expected)
    {
        using var fixture = Loopback();
        Assert.AreEqual(expected, string.Join(" / ", TdsSessionFixture.Describe(fixture.RunBatch(sql))));
    }

    /// <summary>
    /// A call the provider's query timeout ended sends its message alone and
    /// closes in a count-less DONEPROC, whatever the server had sent ahead of
    /// it (captured 2026-10-17 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec ('print ''x''; waitfor delay ''00:00:01.500''; select 1 as a') at SELF", "INFO 7412 / DONEPROC E0 FINAL 0")]
    [DataRow("exec ('waitfor delay ''00:00:01.500''') at SELF; select 2 as b", "INFO 7412 / DONEPROC E0 MORE 0 / ROWS / DONE C1 COUNT 1")]
    [DataRow("exec SELF.master.sys.sp_executesql N'waitfor delay ''00:00:01.500'''", "INFO 7412 / DONEPROC E0 FINAL 0")]
    public void RemoteCall_PastTheQueryTimeout_ClosesInADoneProcAlone(string sql, string expected)
    {
        using var fixture = Loopback();
        _ = fixture.RunBatch("exec sp_serveroption 'SELF', 'query timeout', '1'");
        Assert.AreEqual(expected, string.Join(" / ", TdsSessionFixture.Describe(fixture.RunBatch(sql))));
    }

    [TestMethod]
    public void RemoteCall_InsideAProcedure_ClosesInADoneInProc()
    {
        using var fixture = Loopback();
        _ = fixture.RunBatch("create procedure outer_p as exec ('select 1 as a') at SELF");
        Assert.AreEqual(
            "ROWS / DONEINPROC C1 MORE|COUNT 1 / DONEINPROC E0 MORE 0 / RET 0 / DONEPROC E0 FINAL 0",
            string.Join(" / ", TdsSessionFixture.Describe(fixture.RunBatch("exec outer_p"))));
    }
}
