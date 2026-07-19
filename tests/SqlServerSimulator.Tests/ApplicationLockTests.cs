using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Pins the application-lock surface (<c>sp_getapplock</c> /
/// <c>sp_releaseapplock</c> / <c>APPLOCK_MODE</c> / <c>APPLOCK_TEST</c>).
/// Every behavior asserted here was probe-confirmed against SQL Server 2025:
/// lock-arbitration outcomes surface as EXEC return codes (never exceptions),
/// validation failures return -999, and only a fixed set of conditions
/// (NULL resource, bad timeout, not-held release, missing mode, unknown
/// principal) raise. Return codes are read through the
/// <c>DECLARE @r int; EXEC @r = … ; SELECT @r</c> idiom via
/// <see cref="DbCommand.ExecuteScalar"/>.
/// </summary>
[TestClass]
public class ApplicationLockTests
{
    public TestContext TestContext { get; set; } = null!;

    // Formats and runs an EXEC @r = sp_getapplock call, returning the code.
    private static int GetAppLock(DbConnection connection, string resource, string mode, string owner = "Session", int timeout = 0)
        => ReturnCode(connection,
            $"declare @r int; exec @r = sp_getapplock @Resource = N'{resource}', @LockMode = '{mode}', @LockOwner = '{owner}', @LockTimeout = {timeout}; select @r");

    // Formats and runs an EXEC @r = sp_releaseapplock call, returning the code.
    private static int ReleaseAppLock(DbConnection connection, string resource, string owner = "Session")
        => ReturnCode(connection,
            $"declare @r int; exec @r = sp_releaseapplock @Resource = N'{resource}', @LockOwner = '{owner}'; select @r");

    private static int ReturnCode(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return IsInstanceOfType<int>(command.ExecuteScalar());
    }

    private static object? Scalar(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(sql);
        return command.ExecuteScalar();
    }

    private static string AppLockMode(DbConnection connection, string resource, string owner = "Session", string principal = "public")
        => (string)Scalar(connection, $"select applock_mode('{principal}', N'{resource}', '{owner}')")!;

    private static short AppLockTest(DbConnection connection, string resource, string mode, string owner = "Session", string principal = "public")
        => (short)Scalar(connection, $"select applock_test('{principal}', N'{resource}', '{mode}', '{owner}')")!;

    private static SimulatedSqlException AssertError(DbConnection connection, string sql, int number)
    {
        var ex = Throws<SimulatedSqlException>(() => Scalar(connection, sql));
        AreEqual(number, ex.Number);
        return ex;
    }

    [TestMethod]
    public void GetAppLock_Granted_ReturnsZero()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "res", "Exclusive"));
    }

    [TestMethod]
    public void GetAppLock_BadLockModeString_ReturnsMinus999()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(-999, GetAppLock(connection, "res", "Bogus"));
    }

    [TestMethod]
    public void GetAppLock_BadLockOwnerString_ReturnsMinus999()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(-999, GetAppLock(connection, "res", "Exclusive", owner: "Nonsense"));
    }

    [TestMethod]
    public void GetAppLock_TransactionOwnerWithoutTransaction_ReturnsMinus999()
    {
        // Unlike APPLOCK_MODE / APPLOCK_TEST (which raise Msg 3918), the proc
        // returns -999 silently when the Transaction owner has no active tx.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(-999, GetAppLock(connection, "res", "Exclusive", owner: "Transaction"));
    }

    [TestMethod]
    public void GetAppLock_MissingResource_ReturnsMinus999()
    {
        // A missing @Resource is -999 (silent); an explicit NULL @Resource is
        // Msg 1224. Presence, not NULL-ness, distinguishes them.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(-999, ReturnCode(connection,
            "declare @r int; exec @r = sp_getapplock @LockMode = 'Exclusive', @LockOwner = 'Session'; select @r"));
    }

    [TestMethod]
    public void GetAppLock_NullResource_RaisesMsg1224()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = NULL, @LockMode = 'Exclusive', @LockOwner = 'Session'; select @r",
            1224);
        AreEqual("An invalid application lock resource was passed to xp_userlock.", ex.Message);
    }

    [TestMethod]
    public void GetAppLock_TimeoutBelowMinusOne_RaisesMsg1227()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = AssertError(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = N'res', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = -2; select @r",
            1227);
    }

    [TestMethod]
    public void ReleaseAppLock_NotHeld_RaisesMsg1223()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection,
            "declare @r int; exec @r = sp_releaseapplock @Resource = N'ghost', @LockOwner = 'Session'; select @r",
            1223);
        AreEqual("Cannot release the application lock (Database Principal: 'public', Resource: 'ghost') because it is not currently held.", ex.Message);
    }

    [TestMethod]
    public void GetAppLock_MissingLockMode_RaisesMsg201()
    {
        // Missing @LockMode is a parameter-binding failure (Msg 201) — it
        // precedes even the silent -999 a missing @Resource would produce.
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = N'res', @LockOwner = 'Session'; select @r",
            201);
        AreEqual("Procedure or function 'sp_getapplock' expects parameter '@LockMode', which was not supplied.", ex.Message);
    }

    [TestMethod]
    public void GetAppLock_UnknownDbPrincipal_RaisesMsg1202()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = AssertError(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = N'res', @LockMode = 'Exclusive', @LockOwner = 'Session', @DbPrincipal = 'nobody'; select @r",
            1202);
    }

    [TestMethod]
    public void ReferenceCounting_TwoAcquiresNeedTwoReleases()
    {
        // N acquires of the same (session, owner, mode) need N releases; the
        // mode stays visible until the last release drains it.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "res", "Exclusive"));
        AreEqual(0, GetAppLock(connection, "res", "Exclusive"));
        AreEqual(0, ReleaseAppLock(connection, "res"));
        AreEqual("Exclusive", AppLockMode(connection, "res"));
        AreEqual(0, ReleaseAppLock(connection, "res"));
        AreEqual("NoLock", AppLockMode(connection, "res"));
    }

    [TestMethod]
    public void GetAppLock_OwnerDefaultsToTransaction_InsideTransaction()
    {
        // An omitted @LockOwner defaults to Transaction: the hold shows up
        // under the Transaction owner and NOT under Session.
        using var connection = new Simulation().CreateOpenConnection();
        _ = Scalar(connection, "begin tran");
        AreEqual(0, ReturnCode(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = N'res', @LockMode = 'Exclusive'; select @r"));
        AreEqual("Exclusive", AppLockMode(connection, "res", owner: "Transaction"));
        AreEqual("NoLock", AppLockMode(connection, "res", owner: "Session"));
        _ = Scalar(connection, "commit");
    }

    [TestMethod]
    public void TransactionOwned_ReleasedOnCommit()
    {
        var simulation = new Simulation();
        using var holder = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();

        _ = Scalar(holder, "begin tran");
        AreEqual(0, GetAppLock(holder, "res", "Exclusive", owner: "Transaction"));
        AreEqual((short)0, AppLockTest(observer, "res", "Exclusive"));

        _ = Scalar(holder, "commit");
        AreEqual((short)1, AppLockTest(observer, "res", "Exclusive"));
    }

    [TestMethod]
    public void TransactionOwned_ReleasedOnRollback()
    {
        var simulation = new Simulation();
        using var holder = simulation.CreateOpenConnection();
        using var observer = simulation.CreateOpenConnection();

        _ = Scalar(holder, "begin tran");
        AreEqual(0, GetAppLock(holder, "res", "Exclusive", owner: "Transaction"));
        AreEqual((short)0, AppLockTest(observer, "res", "Exclusive"));

        _ = Scalar(holder, "rollback");
        AreEqual((short)1, AppLockTest(observer, "res", "Exclusive"));
    }

    [TestMethod]
    public void SessionOwned_SurvivesCommit_ReleasedOnDispose()
    {
        var simulation = new Simulation();
        using var observer = simulation.CreateOpenConnection();

        var holder = simulation.CreateOpenConnection();
        try
        {
            _ = Scalar(holder, "begin tran");
            AreEqual(0, GetAppLock(holder, "res", "Exclusive"));
            _ = Scalar(holder, "commit");
            // A Session-owned lock outlives the transaction that spanned it.
            AreEqual((short)0, AppLockTest(observer, "res", "Exclusive"));
        }
        finally
        {
            holder.Dispose();
        }

        // Closing the connection releases every Session-owned lock it held.
        AreEqual((short)1, AppLockTest(observer, "res", "Exclusive"));
    }

    [TestMethod]
    public void CrossConnection_ExclusiveBlocksShared_FailFast()
    {
        var simulation = new Simulation();
        using var a = simulation.CreateOpenConnection();
        using var b = simulation.CreateOpenConnection();

        AreEqual(0, GetAppLock(a, "res", "Exclusive"));
        AreEqual(-1, GetAppLock(b, "res", "Shared", timeout: 0));
        // B can't take Shared; A re-tests its own hold as compatible.
        AreEqual((short)0, AppLockTest(b, "res", "Shared"));
        AreEqual((short)1, AppLockTest(a, "res", "Shared"));
    }

    [TestMethod]
    [DataRow("Shared", "Shared", 0)]
    [DataRow("Shared", "Update", 0)]
    [DataRow("Update", "Update", -1)]
    [DataRow("Shared", "Exclusive", -1)]
    [DataRow("IntentExclusive", "IntentExclusive", 0)]
    public void Compatibility_SpotChecks(string firstMode, string secondMode, int expectedSecondCode)
    {
        var simulation = new Simulation();
        using var a = simulation.CreateOpenConnection();
        using var b = simulation.CreateOpenConnection();

        AreEqual(0, GetAppLock(a, "res", firstMode));
        AreEqual(expectedSecondCode, GetAppLock(b, "res", secondMode, timeout: 0));
    }

    [TestMethod]
    public void GetAppLock_GrantedAfterWait_ReturnsOne()
    {
        var simulation = new Simulation();
        using var a = simulation.CreateOpenConnection();

        AreEqual(0, GetAppLock(a, "res", "Exclusive"));

        // B waits up to 3s; A releases after ~200ms, so B is granted after
        // waiting and reports return code 1.
        var waiter = Task.Run(() =>
        {
            using var b = simulation.CreateOpenConnection();
            return GetAppLock(b, "res", "Shared", timeout: 3000);
        }, this.TestContext.CancellationToken);

        var releaser = Task.Run(() =>
        {
            Thread.Sleep(200);
            _ = ReleaseAppLock(a, "res");
        }, this.TestContext.CancellationToken);

        IsTrue(Task.WaitAll([waiter, releaser], TimeSpan.FromSeconds(15)), "Wait/release tasks did not complete in time.");
        AreEqual(1, waiter.Result);
    }

    [TestMethod]
    public void Deadlock_OneVictim_ReturnsMinus3()
    {
        var simulation = new Simulation();
        using var a = simulation.CreateOpenConnection();
        using var b = simulation.CreateOpenConnection();

        AreEqual(0, GetAppLock(a, "dlA", "Exclusive"));
        AreEqual(0, GetAppLock(b, "dlB", "Exclusive"));

        // A wants dlB (held by B); B wants dlA (held by A). One is chosen the
        // deadlock victim and gets -3 — with no exception raised.
        var aTask = Task.Run(() => GetAppLock(a, "dlB", "Exclusive", timeout: 5000), this.TestContext.CancellationToken);
        var bTask = Task.Run(() => GetAppLock(b, "dlA", "Exclusive", timeout: 5000), this.TestContext.CancellationToken);

        IsTrue(Task.WaitAll([aTask, bTask], TimeSpan.FromSeconds(15)), "Deadlock tasks did not complete in time.");

        var codes = new[] { aTask.Result, bTask.Result };
        // Exactly one connection is the deadlock victim (-3), with no
        // exception raised. The survivor's Session-owned hold isn't released
        // by the victim's -3 (a proc return, not a rolled-back transaction),
        // so the survivor stays blocked and times out (-1) at its 5s deadline.
        _ = ContainsSingle(codes.Where(c => c == -3));
        _ = ContainsSingle(codes.Where(c => c == -1));
    }

    [TestMethod]
    public void ResourceNames_CaseSensitive()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "Foo", "Exclusive"));
        // 'foo' is a distinct resource from 'Foo'.
        AreEqual("NoLock", AppLockMode(connection, "foo"));
        AreEqual("Exclusive", AppLockMode(connection, "Foo"));
    }

    [TestMethod]
    public void ResourceNames_TrailingSpaceSignificant()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "bar ", "Exclusive"));
        AreEqual("NoLock", AppLockMode(connection, "bar"));
        AreEqual("Exclusive", AppLockMode(connection, "bar "));
    }

    [TestMethod]
    public void ResourceNames_TruncateTo255()
    {
        // Names longer than 255 truncate; a 256-char name collides with its
        // own 255-char prefix, so a second connection's acquire conflicts.
        var prefix255 = new string('x', 255);
        var name256 = prefix255 + "y";
        var simulation = new Simulation();
        using var a = simulation.CreateOpenConnection();
        using var b = simulation.CreateOpenConnection();

        AreEqual(0, GetAppLock(a, prefix255, "Exclusive"));
        AreEqual(-1, GetAppLock(b, name256, "Exclusive", timeout: 0));
    }

    [TestMethod]
    public void LockMode_CaseInsensitive()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "res", "exclusive"));
        AreEqual("Exclusive", AppLockMode(connection, "res"));
    }

    [TestMethod]
    [DataRow("Shared", "Shared")]
    [DataRow("Update", "Update")]
    [DataRow("IntentShared", "IntentShared")]
    [DataRow("IntentExclusive", "IntentExclusive")]
    [DataRow("Exclusive", "Exclusive")]
    public void ApplockMode_ExactDisplayStrings(string requestMode, string expectedDisplay)
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "res", requestMode));
        AreEqual(expectedDisplay, AppLockMode(connection, "res"));
    }

    [TestMethod]
    public void ApplockMode_NoLockWhenNothingHeld()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual("NoLock", AppLockMode(connection, "res"));
    }

    [TestMethod]
    public void ApplockMode_NullPrincipal_RaisesMsg8116Arg1()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_mode(NULL, N'res', 'Session')", 8116);
        AreEqual("Argument data type NULL is invalid for argument 1 of applock_mode function.", ex.Message);
    }

    [TestMethod]
    public void ApplockMode_NullResource_RaisesMsg8116Arg2()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_mode('public', NULL, 'Session')", 8116);
        AreEqual("Argument data type NULL is invalid for argument 2 of applock_mode function.", ex.Message);
    }

    [TestMethod]
    public void ApplockMode_TransactionOwnerWithoutTransaction_RaisesMsg3918()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_mode('public', N'res', 'Transaction')", 3918);
        AreEqual("The statement or function must be executed in the context of a user transaction.", ex.Message);
    }

    [TestMethod]
    public void ApplockMode_InvalidOwnerString_RaisesMsg1226()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_mode('public', N'res', 'Nonsense')", 1226);
        AreEqual("An invalid application lock owner was passed to applock_mode.", ex.Message);
    }

    [TestMethod]
    public void ApplockTest_InvalidModeString_RaisesMsg1225()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_test('public', N'res', 'Bogus', 'Session')", 1225);
        AreEqual("An invalid application lock mode was passed to applock_test.", ex.Message);
    }

    [TestMethod]
    public void ApplockTest_NullMode_RaisesMsg8116Arg3()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = AssertError(connection, "select applock_test('public', N'res', NULL, 'Session')", 8116);
        AreEqual("Argument data type NULL is invalid for argument 3 of applock_test function.", ex.Message);
    }

    [TestMethod]
    public void DbPrincipal_DboIsSeparateLockIdentity()
    {
        // A lock held under 'dbo' is invisible to the 'public' identity and
        // vice versa — the (principal, resource) pair keys the resource.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, ReturnCode(connection,
            "declare @r int; exec @r = sp_getapplock @Resource = N'res', @LockMode = 'Exclusive', @LockOwner = 'Session', @DbPrincipal = 'dbo'; select @r"));
        AreEqual("Exclusive", AppLockMode(connection, "res", principal: "dbo"));
        AreEqual("NoLock", AppLockMode(connection, "res", principal: "public"));
    }

    [TestMethod]
    public void ApplockMode_UnknownPrincipal_RaisesMsg1202()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = AssertError(connection, "select applock_mode('nobody', N'res', 'Session')", 1202);
    }

    [TestMethod]
    public void DmTranLocks_ProjectsApplicationRow()
    {
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, GetAppLock(connection, "q", "Update"));

        using var command = connection.CreateCommand(
            "select resource_description, request_mode from sys.dm_tran_locks where resource_type = 'APPLICATION'");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read(), "Expected an APPLICATION lock row in sys.dm_tran_locks.");
        var description = reader.GetString(0);
        var requestMode = reader.GetString(1);
        // resource_description shape: <principal-id>:[<name>]:(<hash>); the
        // hash is simulator-specific, so only the id + bracketed name are pinned.
        StartsWith("0:[q]:(", description);
        AreEqual("U", requestMode);
        IsFalse(reader.Read(), "Expected exactly one APPLICATION lock row.");
    }

    [TestMethod]
    public void EfMigrationsLockShape_ReturnsZero()
    {
        // The verbatim batch EF Core 10 issues around a Migrate() — the
        // '__EFMigrationsLock' Session/Exclusive acquire must return 0.
        using var connection = new Simulation().CreateOpenConnection();
        AreEqual(0, ReturnCode(connection,
            "DECLARE @result int;\nEXEC @result = sp_getapplock @Resource = '__EFMigrationsLock', @LockOwner = 'Session', @LockMode = 'Exclusive';\nSELECT @result"));
    }
}
