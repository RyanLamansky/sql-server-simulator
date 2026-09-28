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
}
