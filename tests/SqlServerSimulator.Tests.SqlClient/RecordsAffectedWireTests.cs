using Microsoft.Data.SqlClient;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SqlDataReader.RecordsAffected</c> over the wire: genuine
/// <c>Microsoft.Data.SqlClient</c> computes it from the DONE tokens the
/// endpoint writes, so these assert the endpoint's own arithmetic through the
/// client that consumes it — the strongest oracle available for the value.
/// <para>
/// Two token fields decide it, both probed off SQL Server 2025's wire
/// (2026-08-03). DONE's <c>CurCmd</c> says which kind of statement produced the
/// token, and SqlClient leaves the SELECT kind (0x00C1) out of the sum because
/// a SELECT's count is rows returned, not rows affected — real tags a plain
/// SELECT and a cursor FETCH that way while tagging SELECT INTO, INSERT, UPDATE
/// and DELETE their own non-SELECT kinds. DONE_COUNT says whether there is a
/// count at all, and <c>SET NOCOUNT ON</c> clears it (keeping the row count in
/// the token) for every statement kind. Real reports the same number here as
/// <c>ExecuteNonQuery</c> does, which every case asserts.
/// </para>
/// </summary>
[TestClass]
public sealed class RecordsAffectedWireTests
{
    public TestContext TestContext { get; set; } = null!;

    private static Simulation Seed()
    {
        var simulation = new Simulation();
        Wire.ExecInProc(simulation, "create table t (a int, b varchar(10))");
        Wire.ExecInProc(simulation, "insert into t values (1, 'a'), (2, 'b'), (3, 'c')");
        return simulation;
    }

    /// <summary>
    /// Drains and closes a reader over <paramref name="sql"/>, then runs the
    /// same batch through <c>ExecuteNonQuery</c> against a fresh simulation,
    /// asserting both report <paramref name="expected"/>.
    /// </summary>
    private async Task AssertRecordsAffectedAsync(int expected, string sql)
    {
        await using (var listener = await Seed().ListenLocalAsync(0, TestContext.CancellationToken))
        await using (var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken))
        {
            await using var command = new SqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
            do
            {
                while (await reader.ReadAsync(TestContext.CancellationToken))
                {
                }
            }
            while (await reader.NextResultAsync(TestContext.CancellationToken));
            await reader.CloseAsync();
            AreEqual(expected, reader.RecordsAffected, $"reader: {sql}");
        }

        await using var nonQueryListener = await Seed().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var nonQueryConnection = await Wire.OpenAsync(nonQueryListener, TestContext.CancellationToken);
        await using var nonQuery = new SqlCommand(sql, nonQueryConnection);
        AreEqual(expected, await nonQuery.ExecuteNonQueryAsync(TestContext.CancellationToken), $"ExecuteNonQuery: {sql}");
    }

    /// <summary>
    /// The shape the bug was: without a SELECT <c>CurCmd</c> on the DONE,
    /// SqlClient folded a three-row SELECT's row count into the value.
    /// </summary>
    [TestMethod]
    public Task SelectReportsNoCount() => AssertRecordsAffectedAsync(-1, "select a from t");

    [TestMethod]
    public Task EmptySelectReportsNoCount() => AssertRecordsAffectedAsync(-1, "select a from t where a = 999");

    [TestMethod]
    public Task AssignmentOnlySelectReportsNoCount() =>
        AssertRecordsAffectedAsync(-1, "declare @x int; select @x = a from t");

    [TestMethod]
    public Task DdlReportsNoCount() => AssertRecordsAffectedAsync(-1, "create table t9 (a int)");

    [TestMethod]
    public Task InsertReportsRowsWritten() => AssertRecordsAffectedAsync(1, "insert into t values (9, 'z')");

    [TestMethod]
    public Task UpdateReportsRowsChanged() => AssertRecordsAffectedAsync(3, "update t set b = b");

    [TestMethod]
    public Task UpdateMatchingNothingReportsZero() =>
        AssertRecordsAffectedAsync(0, "update t set b = 'x' where a = 999");

    /// <summary>SELECT INTO writes rows, so real tags it a non-SELECT kind and it counts.</summary>
    [TestMethod]
    public Task SelectIntoReportsRowsWritten() =>
        AssertRecordsAffectedAsync(3, "select a into #s from t; drop table #s");

    /// <summary>
    /// A DML statement whose OUTPUT clause returns rows is tabular but still
    /// reports what it changed, so its DONE is the one result-set DONE that
    /// goes out unclassified rather than as a SELECT.
    /// </summary>
    [TestMethod]
    public Task OutputToClientReportsRowsChanged() =>
        AssertRecordsAffectedAsync(1, "insert into t output inserted.a values (9, 'z')");

    [TestMethod]
    public Task MixedBatchSumsOnlyTheChangingStatements() =>
        AssertRecordsAffectedAsync(4, """
            insert into t values (9, 'z');
            update t set b = 'q' where a < 3;
            select a from t;
            delete from t where a = 9;
            """);

    [TestMethod]
    public Task NoCountSuppressesTheCount() =>
        AssertRecordsAffectedAsync(-1, "set nocount on; insert into t values (9, 'z')");

    [TestMethod]
    public Task NoCountTurnedOffMidBatchResumesCounting() =>
        AssertRecordsAffectedAsync(1, """
            set nocount on;
            insert into t values (9, 'z');
            set nocount off;
            insert into t values (8, 'y');
            """);

    /// <summary>
    /// Catches deciding a statement's DONE_COUNT from the session flag after
    /// the following statement has already run: the first INSERT completed
    /// while NOCOUNT was off, so its count reaches the client.
    /// </summary>
    [TestMethod]
    public Task NoCountTurnedOnMidBatchKeepsTheEarlierCount() =>
        AssertRecordsAffectedAsync(1, """
            insert into t values (9, 'z');
            set nocount on;
            insert into t values (8, 'y');
            """);

    /// <summary>
    /// A procedure's body statements reach the client as DONEINPROC tokens and
    /// follow the same rules: the two writes count, the SELECT does not.
    /// </summary>
    [TestMethod]
    public async Task ProcedureBodySumsItsDmlOnly()
    {
        var simulation = Seed();
        Wire.ExecInProc(simulation, """
            create procedure p as
            begin
                insert into t values (9, 'z');
                select a from t where a = 9;
                delete from t where a = 9;
            end
            """);
        await using var listener = await simulation.ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand("exec p", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);
        await reader.CloseAsync();
        AreEqual(2, reader.RecordsAffected);
    }

    /// <summary>
    /// The value grows as the reader is advanced and is final once closed —
    /// SqlClient reads it out of the tokens it has consumed so far, so a
    /// statement ahead of the current result set has contributed and one behind
    /// it has not.
    /// </summary>
    [TestMethod]
    public async Task AccumulatesAsTheReaderAdvances()
    {
        await using var listener = await Seed().ListenLocalAsync(0, TestContext.CancellationToken);
        await using var connection = await Wire.OpenAsync(listener, TestContext.CancellationToken);
        await using var command = new SqlCommand(
            """
            insert into t values (9, 'z');
            select a from t;
            delete from t where a = 9;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.CancellationToken);

        // The INSERT's DONE arrived ahead of the SELECT's COLMETADATA.
        AreEqual(1, reader.RecordsAffected);
        await reader.CloseAsync();
        AreEqual(2, reader.RecordsAffected);
    }
}
