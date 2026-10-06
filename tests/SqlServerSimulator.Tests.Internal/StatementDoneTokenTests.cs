namespace SqlServerSimulator;

/// <summary>
/// The DONE token every statement closes with, naming its kind in
/// <c>CurCmd</c>, and the RETURNSTATUS + DONEPROC a procedure scope closes
/// with — each sequence as real SQL Server 2025 sends it (captured 2026-09-28
/// with a raw TDS client).
/// </summary>
[TestClass]
public sealed class StatementDoneTokenTests
{
    private static List<string> Batch(TdsSessionFixture fixture, string sql) => TdsSessionFixture.Describe(fixture.RunBatch(sql));

    private static void AssertBatch(string sql, params string[] expected)
    {
        using var fixture = new TdsSessionFixture();
        var actual = Batch(fixture, sql);
        CollectionAssert.AreEqual(expected, actual, string.Join(" / ", actual));
    }

    [TestMethod]
    public void TransactionStatements_EachCloseWithTheirKind()
        => AssertBatch(
            "begin tran; save tran s; rollback tran s; commit",
            "ENV8", "DONE D4 MORE 0", "DONE D6 MORE 0", "DONE D2 MORE 0", "ENV9", "DONE D5 FINAL 0");

    [TestMethod]
    public void SetOptions_OnOffValue_AndQuotedIdentifierSendsNone()
        => AssertBatch(
            "set nocount on; set quoted_identifier on; set lock_timeout 5; set nocount off",
            "DONE B9 MORE 0", "DONE F9 MORE 0", "DONE BA FINAL 0");

    [TestMethod]
    public void VariableAssignments_CountOneRow_ADeclareWithoutInitializerSendsNone()
        => AssertBatch(
            "declare @x int; declare @y int = 1; set @x = 2; select @x = 3",
            "DONE C1 MORE|COUNT 1", "DONE C1 MORE|COUNT 1", "DONE C1 COUNT 1");

    [TestMethod]
    public void ConditionsCloseBeforeTheirBody_PrintAfterItsMessage()
        => AssertBatch(
            "declare @i int = 0; while @i < 2 set @i += 1; if @i = 2 print 'x'",
            "DONE C1 MORE|COUNT 1",
            "DONE C0 MORE 0", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0", "DONE C1 MORE|COUNT 1", "DONE C0 MORE 0",
            "DONE C0 MORE 0", "INFO 0", "DONE F7 FINAL 0");

    [TestMethod]
    public void TryCatch_FramesItsBlocks()
        => AssertBatch(
            "begin try select 1/0 end try begin catch print 'c' end catch",
            "DONE 15D MORE 0", "ROWS", "DONE C1 MORE|COUNT 0", "DONE 15E MORE 0", "INFO 0", "DONE F7 MORE 0", "DONE 15F FINAL 0");

    [TestMethod]
    public void Procedure_ClosesWithItsReturnStatus_ItsValuedReturnCountingARow()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create proc p as begin select 1; return 5 end");
        CollectionAssert.AreEqual(
            new[] { "ROWS", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC C1 MORE|COUNT 1", "RET 5", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1" },
            Batch(fixture, "exec p; select 2"));
    }

    [TestMethod]
    public void NestedCall_ClosesWithADoneInProc_WhichNoCountDrops()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create proc q as select 2");
        _ = fixture.RunBatch("create proc p as begin exec q end");
        _ = fixture.RunBatch("create proc r as begin set nocount on; declare @x int = 1; exec q end");
        CollectionAssert.AreEqual(
            new[] { "ROWS", "DONEINPROC C1 MORE|COUNT 1", "DONEINPROC E0 MORE 0", "RET 0", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec p"));
        CollectionAssert.AreEqual(
            new[] { "ROWS", "DONEINPROC C1 MORE 1", "RET 0", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec r"));
    }

    /// <summary>
    /// A compile's non-aborting error — a scalar function call it couldn't
    /// inline — goes out first with no DONE of its own; the next DONE, whatever
    /// statement sends it, carries DONE_ERROR and drops DONE_COUNT, keeping
    /// the count (captured 2026-09-30 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void CompileFailure_SendsNoDone_TheNextDoneCarriesItsErrorBit()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create function dbo.f() returns int as begin declare @x int; select @x = count(*) from nosuch; return @x end");
        CollectionAssert.AreEqual(
            new[] { "ERR 208", "ROWS", "DONE C1 MORE|ERROR 1", "ROWS", "ERR 208", "DONE C1 ERROR 0" },
            Batch(fixture, "select 1; select dbo.f()"));
        CollectionAssert.AreEqual(
            new[] { "ERR 208", "DONE 15D MORE|ERROR 0", "ROWS", "DONE C1 MORE|COUNT 0", "DONE 15E MORE 0", "ROWS", "DONE C1 MORE|COUNT 1", "DONE 15F FINAL 0" },
            Batch(fixture, "begin try select dbo.f() end try begin catch select error_number() end catch"));
    }

    [TestMethod]
    public void StatementError_ClosesWithItsKind_TerminationNoticeAheadOfTheDone()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int primary key); insert t values (1)");
        CollectionAssert.AreEqual(
            new[] { "ERR 3902", "DONE D5 MORE|ERROR 0", "ERR 2627", "INFO 3621", "DONE C3 MORE|ERROR 0", "ROWS", "DONE C1 COUNT 1" },
            Batch(fixture, "commit; insert t values (1); select 1"));
    }

    [TestMethod]
    public void BatchEndingError_ClosesWithTheBatchsDone()
        => AssertBatch(
            "select 1; throw 50001, 'x', 1; select 2",
            "ROWS", "DONE C1 MORE|COUNT 1", "ERR 50001", "DONE FD ERROR 0");

    [TestMethod]
    public void BatchEndingInAStatementWithoutADone_ClosesWithTheBatchsDone()
        => AssertBatch("declare @x int", "DONE FD FINAL 0");

    [TestMethod]
    public void Use_AnnouncesTheDatabaseAheadOfItsMessage_AndItsCollationAfter()
        => AssertBatch("use master", "ENV1", "INFO 5701", "ENV7", "DONE E2 FINAL 0");

    [TestMethod]
    public void MissingProcedure_ClosesTheCallsScopeWithTheError()
        => AssertBatch("exec nosuch; select 1", "ERR 2812", "DONEPROC E0 MORE|ERROR 0", "ROWS", "DONE C1 COUNT 1");

    [TestMethod]
    public void ProcedureBodyTransactions_AnnouncedBesideTheirStatements()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create proc p as begin print 'a'; begin tran; commit end");
        CollectionAssert.AreEqual(
            new[] { "INFO 0", "DONEINPROC F7 MORE 0", "ENV8", "DONEINPROC D4 MORE 0", "ENV9", "DONEINPROC D5 MORE 0", "RET 0", "DONEPROC E0 FINAL 0" },
            Batch(fixture, "exec p"));
    }

    /// <summary>
    /// Under NOEXEC each statement still closes with its kind — a count of 0
    /// where it would count rows, both IF branches walked, a TRY's block
    /// closed and its CATCH opened, a call's scope closed with no status —
    /// save one real compiles through simple parameterization (probed
    /// 2026-10-06 against SQL Server 2025 with a raw TDS client).
    /// </summary>
    [TestMethod]
    public void NoExec_ClosesEachStatementWithItsKind()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int)");
        CollectionAssert.AreEqual(
            new[] { "DONE B9 MORE 0", "DONE C4 MORE|COUNT 0", "DONE C1 MORE 0", "DONE C0 MORE 0", "DONE C1 MORE 0", "DONE C1 MORE 0", "DONE C1 MORE|COUNT 0", "DONE C1 MORE|COUNT 0", "DONEPROC E0 MORE 0", "DONE BA FINAL 0" },
            Batch(fixture, "set noexec on; delete t; select a from t; if 1 = 0 select 1 else select 2; declare @x int = 1; select @x = 2; exec sp_who; set noexec off"));
        CollectionAssert.AreEqual(
            new[] { "DONE B9 MORE 0", "DONE 15D MORE 0", "DONE C1 MORE 0", "DONE 15F MORE 0", "DONE 15E MORE 0", "DONE C1 MORE 0", "DONE 15F MORE 0", "DONE DB MORE 0", "DONE BA FINAL 0" },
            Batch(fixture, "set noexec on; begin try select 1 end try begin catch select 2 end catch; return; set noexec off"));
        CollectionAssert.AreEqual(
            new[] { "DONE B9 MORE 0", "DONE BA FINAL 0" },
            Batch(fixture, "set noexec on; insert t values (1); update t set a = 2; select a from t where a = 1; set noexec off"));
    }

    /// <summary>
    /// A security or schema statement sending no DONE when it succeeds closes
    /// its error with one of its own (probed 2026-10-06 against SQL Server
    /// 2025); a schema's or type's creation doesn't.
    /// </summary>
    [TestMethod]
    [DataRow("alter schema dbo transfer dbo.nosuch; select 1", "ERR 15151", "DONE AA MORE|ERROR 0")]
    [DataRow("grant select on nosuch to public; select 1", "ERR 15151", "DONE AA MORE|ERROR 0")]
    [DataRow("drop user nosuchuser; select 1", "ERR 15151", "DONE AA MORE|ERROR 0")]
    [DataRow("alter role nosuchrole add member nosuchuser; select 1", "ERR 15151", "DONE AA MORE|ERROR 0")]
    [DataRow("create synonym nosch.s for dbo.x; select 1", "ERR 2760", "DONE AA MORE|ERROR 0")]
    [DataRow("drop type nosuchtype; select 1", "ERR 218", "DONE FD MORE|ERROR 0")]
    public void SecurityDdlError_ClosesWithItsOwnDone(string sql, string error, string done)
        => AssertBatch(sql, error, done, "ROWS", "DONE C1 COUNT 1");

    [TestMethod]
    public void EnableTriggerAtBatchStart_IsNoProcedureCall()
        => AssertBatch("disable trigger nosuchtrg on database", "ERR 1088", "DONE FD ERROR 0");

    /// <summary>
    /// A system procedure's return status: the error's number for
    /// <c>sp_recompile</c>, 1 after a statement it runs fails, and
    /// <c>sp_executesql</c>'s last statement's <c>@@ERROR</c> (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_recompile 'nosuch'", "ERR 15165 / DONEINPROC F6 MORE|ERROR 0 / RET 15165 / DONEPROC E0 FINAL 0")]
    [DataRow("exec sp_addrole 'db_owner'", "ERR 15023 / DONEINPROC AA MORE|ERROR 0 / RET 1 / DONEPROC E0 FINAL 0")]
    [DataRow("exec sp_executesql N'raiserror(''x'', 16, 1)'", "ERR 50000 / DONEINPROC F6 MORE|ERROR 0 / RET 50000 / DONEPROC E0 FINAL 0")]
    [DataRow("exec sp_executesql N'select 1/0; declare @x int'", "ROWS / ERR 8134 / DONEINPROC C1 MORE|ERROR 0 / RET 8134 / DONEPROC E0 FINAL 0")]
    public void SystemProcedure_ReturnStatus(string sql, string expected)
        => AssertBatch(sql, expected.Split(" / "));

    /// <summary>
    /// A positioned write ending the session sends nothing ahead of its Msg
    /// 596, so the DONE of the statement before it, held until the next token,
    /// goes down unsent (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SessionEndingWrite_TakesThePriorDoneDown()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table a1 (k int primary key check (k < 10), v int); create table a2 (k int primary key check (k >= 10), v int)");
        _ = fixture.RunBatch("create view pv as select k, v from a1 union all select k, v from a2");
        _ = fixture.RunBatch("insert pv values (1, 1), (11, 2)");
        var tokens = Batch(fixture, "declare c cursor for select k, v from pv; open c; fetch next from c; select 1 m; update pv set v = 5 where current of c");
        CollectionAssert.AreEqual(new[] { "ROWS", "ERR 596", "DONE FD ERROR 0" }, tokens.Skip(tokens.Count - 3).ToArray(), string.Join(" / ", tokens));
    }

    /// <summary>
    /// A trigger body's caught SELECT closes under the body's own NOCOUNT,
    /// though the body's SET reverts before the firing statement sends what it
    /// buffered (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void TriggerBodysCaughtSelect_KeepsTheBodysNoCount()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("create table t (a int); create table l (a int)");
        _ = fixture.RunBatch("create trigger tr on t after insert as set nocount on; begin try select 1 / 0; end try begin catch insert l values (1); end catch;");
        CollectionAssert.AreEqual(
            new[] { "ROWS", "DONEINPROC C1 MORE 0", "ERR 3930", "INFO 3621", "DONE FD ERROR 0" },
            Batch(fixture, "insert t values (1)"));
    }
}
