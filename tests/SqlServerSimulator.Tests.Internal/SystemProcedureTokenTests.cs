namespace SqlServerSimulator;

/// <summary>
/// The token streams real SQL Server 2025 sends for system procedures whose
/// statements, return statuses and transactions reach the wire — captured
/// 2026-10-17 with a raw TDS client: the application-lock procedures'
/// statement-by-statement DONEINPROCs and -999, an extended procedure's
/// refusal closing its own DONEPROC, and the transaction a legacy security
/// procedure wraps its work in.
/// </summary>
[TestClass]
public sealed class SystemProcedureTokenTests
{
    private static List<string> Batch(TdsSessionFixture fixture, string sql) => TdsSessionFixture.Describe(fixture.RunBatch(sql));

    private static void AssertBatch(string sql, params string[] expected)
    {
        using var fixture = new TdsSessionFixture();
        var actual = Batch(fixture, sql);
        CollectionAssert.AreEqual(expected, actual, string.Join(" / ", actual));
    }

    [TestMethod]
    public void GetAppLock_ClosesEachOfItsStatements_TheTimeoutDefaultAssignedOnItsOwn()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(
            new[]
            {
                "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0",
                "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0",
                "DONEINPROC E0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "RET 0", "DONEPROC E0 FINAL 0",
            },
            Batch(fixture, "exec sp_getapplock @Resource = 'r', @LockMode = 'Exclusive', @LockOwner = 'Session'"));
        CollectionAssert.AreEqual(
            new[]
            {
                "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0",
                "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0",
                "DONEINPROC E0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "RET 0", "DONEPROC E0 FINAL 0",
            },
            Batch(fixture, "exec sp_getapplock @Resource = 'r', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0"));
    }

    [TestMethod]
    public void GetAppLock_RefusedMode_PrintsThenAnswersMinus999()
        => AssertBatch(
            "exec sp_getapplock @Resource = 'r', @LockMode = 'Bogus'",
            "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "INFO 15625", "DONEINPROC F6 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "RET -999", "DONEPROC E0 FINAL 0");

    [TestMethod]
    public void ReleaseAppLock_NotHeld_FailsItsCallOfXpUserlock_AndAnswersMinus999()
        => AssertBatch(
            "exec sp_releaseapplock @Resource = 'r', @LockOwner = 'Session'",
            "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "ERR 1223", "DONEINPROC E0 MORE|ERROR 0", "DONEINPROC C1 MORE|COUNT 1", "RET -999", "DONEPROC E0 FINAL 0");

    [TestMethod]
    public void AppLockStatements_NoCountDropsThem()
        => AssertBatch(
            "set nocount on; exec sp_releaseapplock @Resource = 'r', @LockOwner = 'Session'; set nocount off",
            "DONE B9 MORE 0", "ERR 1223", "DONEINPROC E0 MORE|ERROR 0", "RET -999", "DONEPROC E0 MORE 0", "DONE BA FINAL 0");

    [TestMethod]
    public void ReleaseAppLock_CaughtByTry_SendsItsCallsDoneWithoutTheError()
        => AssertBatch(
            "begin try exec sp_releaseapplock @Resource = 'r', @LockOwner = 'Session' end try begin catch end catch",
            "DONE 15D MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC E0 MORE 0", "DONEPROC E0 MORE 0", "DONE 15E MORE 0", "DONE 15F FINAL 0");

    [TestMethod]
    public void SetSessionContext_Refusal_ClosesTheProcedureItself()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("exec sp_set_session_context @key = N'k', @value = 1, @read_only = 1");
        CollectionAssert.AreEqual(
            new[] { "ERR 15664", "RET 1", "DONEPROC E0 ERROR 0" },
            Batch(fixture, "exec sp_set_session_context @key = N'k', @value = 2"));
    }

    [TestMethod]
    public void SecurityProcedure_WrapsItsWorkInATransaction()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(new[] { "ENV8", "ENV9", "RET 0", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_addrole 'r'"));
        // The DROP ROLE it runs refuses, and its rollback follows.
        CollectionAssert.AreEqual(new[] { "ENV8", "ERR 15151", "DONEINPROC AA MORE|ERROR 0", "ENV10", "RET 1", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_droprole 'nosuch'"));
        // sp_addrolemember rolls back ahead of its own Msg 15410.
        CollectionAssert.AreEqual(new[] { "ENV8", "ENV10", "ERR 15410", "DONEINPROC F6 MORE|ERROR 0", "RET 1", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_addrolemember 'r', 'nosuch'"));
        // sp_changedbowner commits after its statement's refusal.
        CollectionAssert.AreEqual(new[] { "ENV8", "ERR 15151", "DONEINPROC AA MORE|ERROR 0", "ENV9", "RET 1", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_changedbowner 'nosuch'"));
    }

    [TestMethod]
    [DataRow(@"exec sp_denylogin 'NT AUTHORITY\SYSTEM'", "ENV8 / ENV9 / RET 0 / DONEPROC E0 FINAL 0")]
    [DataRow(@"exec sp_grantlogin 'NT AUTHORITY\SYSTEM'", "ENV8 / ENV9 / RET 0 / DONEPROC E0 FINAL 0")]
    [DataRow(@"exec sp_grantlogin 'NT AUTHORITY\LOCAL SERVICE'", "ENV8 / ERR 15151 / DONEINPROC AA MORE|ERROR 0 / ENV10 / ERR 15401 / DONEINPROC F6 MORE|ERROR 0 / RET 1 / DONEPROC E0 FINAL 0")]
    [DataRow(@"exec sp_denylogin 'zz\nosuch'", "ENV8 / ENV10 / ERR 15401 / DONEINPROC F6 MORE|ERROR 0 / RET 1 / DONEPROC E0 FINAL 0")]
    [DataRow(@"exec sp_revokelogin 'zz\nosuch'", "ERR 15401 / DONEINPROC AA MORE|ERROR 0 / RET 1 / DONEPROC E0 FINAL 0")]
    [DataRow(@"set implicit_transactions on; exec sp_grantlogin 'NT AUTHORITY\SYSTEM'; exec sp_revokelogin 'zz\nosuch'; set implicit_transactions off", "DONE B9 MORE 0 / ENV8 / ENV9 / RET 0 / DONEPROC E0 MORE 0 / ERR 15401 / DONEINPROC AA MORE|ERROR 0 / RET 1 / DONEPROC E0 MORE 0 / DONE BA FINAL 0")]
    public void WindowsLoginProcedures_FollowTheLegacyTransactionRule(string sql, string expected)
    {
        using var fixture = new TdsSessionFixture();
        Assert.AreEqual(expected, string.Join(" / ", Batch(fixture, sql)));
    }

    [TestMethod]
    public void SecurityProcedure_InACallersTransaction_RefusesWithMsg15002()
        => AssertBatch(
            "begin tran; exec sp_droprole 'nosuch'; rollback",
            "ENV8", "DONE D4 MORE 0", "ERR 15002", "DONEINPROC F6 MORE|ERROR 0", "RET 1", "DONEPROC E0 MORE 0", "ENV10", "DONE D2 FINAL 0");

    [TestMethod]
    public void SecurityProcedure_ErrorCaughtByTry_LeavesItsTransactionOpen()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(
            new[] { "DONE 15D MORE 0", "ENV8", "DONEPROC E0 MORE 0", "DONE 15E MORE 0", "DONE 15F FINAL 0" },
            Batch(fixture, "begin try exec sp_droprole 'nosuch' end try begin catch end catch"));
        Assert.AreEqual(1, fixture.TranCount);
    }

    [TestMethod]
    public void ObjectProcedures_PlaceTheirMessagesAgainstTheirTransaction()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (id int primary key, a int)");
        // sp_rename cautions before its commit, sp_recompile reports after it.
        CollectionAssert.AreEqual(new[] { "ENV8", "INFO 15477", "ENV9", "RET 0", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_rename 't.a', 'b', 'COLUMN'"));
        CollectionAssert.AreEqual(new[] { "ENV8", "ENV9", "INFO 15070", "RET 0", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_recompile 't'"));
        // A refusal after the commit.
        CollectionAssert.AreEqual(new[] { "ENV8", "ENV9", "ERR 15248", "DONEINPROC F6 MORE|ERROR 0", "RET 1", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec sp_rename 't.nosuch', 'c', 'COLUMN'"));
    }

    [TestMethod]
    public void DropLogin_InsideAProcedure_SendsNoReturnStatuses()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create login l with password = 'Zz!passw0rd1', check_policy = off");
        CollectionAssert.AreEqual(new[] { "RET 0", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec('drop login l')"));
    }
}
