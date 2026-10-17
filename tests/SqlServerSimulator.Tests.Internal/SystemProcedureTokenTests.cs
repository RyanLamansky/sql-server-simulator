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
    public void SetTriggerOrder_Refusals_EndTheCallOrTheBatch()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int); exec('create trigger tr on t after insert as return'); exec('create trigger d on database for create_table as return')");
        // The argument check ahead of the transaction ends only the call.
        CollectionAssert.AreEqual(
            new[] { "ERR 15600", "DONEINPROC F6 MORE|ERROR 0", "RET 1", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1" },
            Batch(fixture, "exec sp_settriggerorder 'tr', 'Middle', 'INSERT'; select 1"));
        // An unknown statement type is refused inside it, then committed.
        CollectionAssert.AreEqual(
            new[] { "ENV8", "ERR 15600", "DONEINPROC F6 MORE|ERROR 0", "ENV9", "RET 1", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec sp_settriggerorder 'tr', 'First', 'CREATE_TABLE'"));
        // A scoped trigger's event group fails the statement setting it.
        CollectionAssert.AreEqual(
            new[] { "ENV8", "ERR 15600", "ENV10", "DONE FD ERROR 0" },
            Batch(fixture, "exec sp_settriggerorder 'd', 'First', 'DDL_TABLE_EVENTS', 'DATABASE'; select 1"));
    }

    [TestMethod]
    public void RenamingTheSessionsDatabase_SendsNoCollation()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create database zz1");
        CollectionAssert.AreEqual(
            new[] { "ENV1", "INFO 5701", "ENV7", "DONE E2 MORE 0", "INFO 5021", "ENV1", "INFO 5701", "RET 0", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "use zz1; exec sp_rename 'zz1', 'zz2', 'DATABASE'"));
    }

    [TestMethod]
    public void Recompile_NullName_CommitsThenRefuses()
        => AssertBatch("exec sp_recompile NULL", "ENV8", "ENV9", "ERR 15165", "DONEINPROC F6 MORE|ERROR 0", "RET 15165", "DONEPROC E0 FINAL 0");

    [TestMethod]
    public void TableOption_FailsItsStatementInsideItsTransaction()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int)");
        CollectionAssert.AreEqual(
            new[] { "DONEINPROC C1 MORE|COUNT 1", "ENV8", "ERR 2599", "ENV10", "DONE FD ERROR 0" },
            Batch(fixture, "exec sp_tableoption 't', 'text in row', 'on'; select 1"));
        CollectionAssert.AreEqual(
            new[] { "DONEINPROC C1 MORE|COUNT 1", "ENV8", "ERR 4992", "DONEINPROC AA MORE|ERROR 0", "ENV9", "RET 4992", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec sp_tableoption 't', 'large value types out of row', '1'"));
        CollectionAssert.AreEqual(
            new[] { "ENV8", "DONE D4 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "ERR 15002", "DONEINPROC F6 MORE|ERROR 0", "RET 15002", "DONEPROC E0 MORE 0", "ENV10", "DONE D2 FINAL 0" },
            Batch(fixture, "begin tran; exec sp_tableoption 't', 'table lock on bulk load', '1'; rollback"));
    }

    [TestMethod]
    public void AddRoleMember_InACallersTransaction_RollsItBackAndClosesWithMsg266()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create role r");
        CollectionAssert.AreEqual(
            new[] { "ENV8", "DONE D4 MORE 0", "ENV10", "ERR 15410", "DONEINPROC F6 MORE|ERROR 0", "RET 1", "ERR 266", "DONEPROC E0 MORE|ERROR 0", "ROWS", "DONE C1 COUNT 1" },
            Batch(fixture, "begin tran; exec sp_addrolemember 'r', 'nosuch'; select @@trancount"));
        Assert.AreEqual(0, fixture.TranCount);
    }

    [TestMethod]
    public void XmlPrepareDocument_SendsMsg6601AheadOfMsg6602()
        => AssertBatch("declare @h int; exec sp_xml_preparedocument @h output, '<a></b>'", "INFO 6601", "ERR 6602", "RET 1", "DONEPROC E0 ERROR 0");

    /// <summary>
    /// The ODBC catalog procedures close each statement they run ahead of
    /// their result, an IF as a count-less 0x00C0 and an assignment as a
    /// 0x00C1 counting a row, the sequence following what they were given.
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_columns t", "S I S I I I I I I S S I I S I")]
    [DataRow("exec sp_columns t, @ODBCVer = 3", "S I I I I I I I S S I I S I")]
    [DataRow("exec sp_columns '%'", "S I S I I S I I I I S S I I I")]
    [DataRow("exec sp_columns_100 'nosuch'", "I S I I I I I I S S I I I")]
    [DataRow("exec sp_tables t", "I I I I S I S I I I S S S I I S I")]
    [DataRow("exec sp_tables", "I I I I S I S I S S I I I")]
    [DataRow("exec sp_tables t, dbo, simulated", "I I I I I S I S I I S S I I S I")]
    [DataRow("exec sp_pkeys t, dbo", "I I I S S")]
    [DataRow("exec sp_special_columns t", "I I I S I I I S S I I I S")]
    [DataRow("exec sp_special_columns t, @scope = 'C'", "I I S I I I S S I I I S")]
    [DataRow("exec sp_stored_procedures p", "I I I I I I S S S I I S S I I")]
    [DataRow("exec sp_stored_procedures 'nosuch'", "I I I I I I S S I I S I I I I S")]
    [DataRow("exec sp_stored_procedures", "I I I I S S I I S I S I I S I S")]
    [DataRow("exec sp_datatype_info", "S I S I S S")]
    [DataRow("exec sp_table_privileges t", "I S S I I I I I S")]
    [DataRow("exec sp_column_privileges t, @column_name = 'a'", "I I I S")]
    [DataRow("exec sp_sproc_columns p", "I I S I S S I S S I I S I")]
    [DataRow("exec sp_sproc_columns 'nosuch'", "I I S I S S I S S I I I I S")]
    public void CatalogProcedure_ClosesTheStatementsItRunsAheadOfItsResult(string sql, string steps)
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int primary key, b int); exec('create procedure p as return')");
        var tokens = Batch(fixture, sql);
        var ahead = tokens.TakeWhile(static token => token != "ROWS").Select(static token => token switch
        {
            "DONEINPROC C0 MORE 0" => "I",
            "DONEINPROC C1 MORE|COUNT 1" => "S",
            _ => token,
        });
        Assert.AreEqual(steps, string.Join(' ', ahead));
    }

    [TestMethod]
    public void CatalogProcedureSteps_NoCountDropsThem()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int primary key, b int)");
        CollectionAssert.AreEqual(
            new[] { "DONE B9 MORE 0", "ROWS", "ORDER 1,2,3,5", "DONEINPROC C1 MORE 1", "RET 0", "DONEPROC E0 MORE 0", "DONE BA FINAL 0" },
            Batch(fixture, "set nocount on; exec sp_pkeys t; set nocount off"));
    }

    /// <summary>
    /// The extended-property procedures make their change in a transaction
    /// of their own inside a TRY, whose CATCH rolls back and raises the
    /// failure again, ending the batch.
    /// </summary>
    [TestMethod]
    public void ExtendedProperty_WorksInItsOwnTransactionInsideATry()
    {
        using var fixture = new TdsSessionFixture();
        CollectionAssert.AreEqual(
            new[]
            {
                "DONEINPROC C0 MORE 0", "DONEINPROC C0 MORE 0", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC E0 MORE 0", "DONEINPROC C0 MORE 0",
                "ENV8", "DONEINPROC D4 MORE 0", "DONEINPROC 15D MORE 0", "DONEINPROC 15F MORE 0", "ENV9", "DONEINPROC D5 MORE 0", "DONEINPROC C1 MORE|COUNT 1",
                "RET 0", "DONEPROC E0 FINAL 0",
            },
            Batch(fixture, "exec sp_addextendedproperty 'p', 'v'"));
        CollectionAssert.AreEqual(
            new[]
            {
                "DONEINPROC C0 MORE 0", "DONEINPROC C0 MORE 0", "DONEINPROC C0 MORE 0", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC E0 MORE 0", "DONEINPROC C0 MORE 0",
                "ENV8", "DONEINPROC D4 MORE 0", "DONEINPROC 15D MORE 0", "DONEINPROC 15E MORE 0", "DONEINPROC C0 MORE 0", "ENV10", "DONEINPROC D2 MORE 0",
                "ERR 15233", "DONE FD ERROR 0",
            },
            Batch(fixture, "exec sp_addextendedproperty 'p', 'v'; select 1"));
    }

    [TestMethod]
    public void DropLogin_InsideAProcedure_SendsNoReturnStatuses()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create login l with password = 'Zz!passw0rd1', check_policy = off");
        CollectionAssert.AreEqual(new[] { "RET 0", "DONEPROC E0 FINAL 0" }, Batch(fixture, "exec('drop login l')"));
    }
}
