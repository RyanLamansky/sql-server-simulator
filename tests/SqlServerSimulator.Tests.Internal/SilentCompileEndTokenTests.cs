namespace SqlServerSimulator;

/// <summary>
/// What a batch whose compile ends without a message — a table variable
/// defaulting to a sequence missing as it compiles — sends, as real SQL
/// Server 2025 sends it (captured 2026-10-08 with a raw TDS client): its
/// DONE alone, the error bit clear; called under a <c>TRY</c>, the scopes
/// opened inside the <c>TRY</c>'s own close, the outermost DONEPROC carrying
/// the error bit and ending the response; under <c>XACT_ABORT</c>, the
/// batch's DONE with the bit, nothing closing.
/// </summary>
[TestClass]
public sealed class SilentCompileEndTokenTests
{
    private const string Missing = "declare @t table (a int default next value for nosuch, b int);";

    private static void AssertBatch(string sql, params string[] expected)
    {
        using var fixture = new TdsSessionFixture();
        var actual = TdsSessionFixture.Describe(fixture.RunBatch(sql));
        CollectionAssert.AreEqual(expected, actual, string.Join(" / ", actual));
    }

    [TestMethod]
    public void Batch_SendsItsDoneAlone()
        => AssertBatch("print 'a'; create sequence s as int start with 1; declare @t table (a int default next value for s, b int); print 'b'", "DONE FD FINAL 0");

    [TestMethod]
    public void DynamicBatch_ClosesItsScopeWithNoStatus()
        => AssertBatch($"exec('{Missing}'); select 2", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");

    [TestMethod]
    public void SpExecuteSql_StillReturnsItsStatus()
        => AssertBatch($"exec sp_executesql N'{Missing}'; select 2", "RET 0", "DONEPROC E0 MORE 0", "ROWS", "DONE C1 COUNT 1");

    [TestMethod]
    public void TryAroundTheCall_ItsDoneProcCarriesTheError()
        => AssertBatch($"begin try exec('{Missing}'); select 5 end try begin catch select 6 end catch", "DONE 15D MORE 0", "DONEPROC E0 ERROR 0");

    [TestMethod]
    public void TryFurtherOut_ClosesTheInnerScopesClear()
        => AssertBatch(
            $"begin try exec('exec(''exec(''''{Missing}''''); select 11''); select 12'); select 13 end try begin catch select 14 end catch",
            "DONE 15D MORE 0", "DONEINPROC E0 MORE 0", "DONEINPROC E0 MORE 0", "DONEPROC E0 ERROR 0");

    [TestMethod]
    public void TryInTheCaller_LeavesTheScopesAroundItUnclosed()
        => AssertBatch(
            $"exec('exec(''begin try exec(''''{Missing}''''); select 11 end try begin catch select 14 end catch''); select 12'); select 13",
            "DONEINPROC 15D MORE 0", "DONEPROC E0 ERROR 0");

    [TestMethod]
    public void XactAbort_RollsBackAndSendsTheBatchsDone()
    {
        using var fixture = new TdsSessionFixture();
        _ = fixture.RunBatch("set xact_abort on");
        _ = fixture.RunBatch("begin tran");
        var actual = TdsSessionFixture.Describe(fixture.RunBatch($"exec('{Missing}'); select 4"));
        CollectionAssert.AreEqual(new[] { "ENV10", "DONE FD ERROR 0" }, actual, string.Join(" / ", actual));
    }
}
