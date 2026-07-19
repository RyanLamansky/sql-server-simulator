using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The in-process ADO surface shares the TDS wire's continue-on-error engine:
/// a statement-terminating error (severity 11-16) ends its statement but the
/// batch runs to completion, and the front door renders the shared outcome
/// stream as SqlClient-shaped exceptions. The eight cases below mirror the
/// pinned oracle probed against real SQL Server 2025 + Microsoft.Data.SqlClient
/// (2026-07-17): ExecuteNonQuery / ExecuteScalar drain the batch and surface
/// every statement error through one aggregated
/// <see cref="SimulatedSqlException.Errors"/> collection; the reader surfaces
/// errors positionally and survives; and a batch-aborting error (a bind miss or
/// an uncaught THROW) stops the following statements. Message text may differ
/// from real SQL Server, but Msg numbers, Errors.Count, side-effect counts, and
/// positional behavior match. The wire counterpart lives in
/// <c>BatchErrorContinuationTests</c> in <c>SqlServerSimulator.Tests.SqlClient</c>.
/// </summary>
[TestClass]
public sealed class BatchErrorContinuationInProcTests
{
    /// <summary>Probe 1: ExecuteNonQuery over a batch with two continue-class errors and three inserts.</summary>
    [TestMethod]
    public void ExecuteNonQuery_TwoContinueClassErrors_AggregatesBoth_AllInsertsPersist()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();

        using var batch = connection.CreateCommand(
            "insert t values (1); select 1/0; insert t values (2); drop table does_not_exist; insert t values (3)");
        var ex = Throws<SimulatedSqlException>(() => batch.ExecuteNonQuery());

        // One exception carrying both errors in batch order: Msg 8134 (divide,
        // class 16) then Msg 3701 (drop missing, class 11).
        AreEqual(2, ex.Errors.Count);
        AreEqual(8134, ex.Errors[0].Number);
        AreEqual(3701, ex.Errors[1].Number);
        AreEqual(8134, ex.Number);

        // The whole batch ran — all three inserts landed.
        AreEqual(3, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>Probe 2: ExecuteReader with an error between result sets — positional at Read, reader survives.</summary>
    [TestMethod]
    public void ExecuteReader_ErrorBetweenResultSets_ReadThrows_ReaderSurvives()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 'rs1' as a; select 1/0 as boom; select 'rs3' as c");
        using var reader = command.ExecuteReader();

        IsTrue(reader.Read());
        AreEqual("rs1", reader.GetValue(0));
        IsFalse(reader.Read());

        // Advancing to the failed SELECT succeeds (it is row-returning, so real
        // SQL Server framed it with COLMETADATA); the first Read throws.
        IsTrue(reader.NextResult());
        var ex = Throws<SimulatedSqlException>(() => reader.Read());
        AreEqual(8134, ex.Number);

        // The reader survives to the trailing result set.
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("rs3", reader.GetValue(0));
    }

    /// <summary>Probe 3: ExecuteReader with an error inside a result set — Read throws, tail reads clean.</summary>
    [TestMethod]
    public void ExecuteReader_ErrorMidResultSet_ReadThrows_TailReadsClean()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values (2),(1),(0),(5)").ExecuteNonQuery();

        using var command = connection.CreateCommand("select 10/id from t; select 'after' as tail");
        using var reader = command.ExecuteReader();

        // Divergence (documented, out of scope): the simulator materializes a
        // SELECT's rows up front, so the divide-by-zero fires before any row is
        // yielded — real SQL Server streams two rows first. The positional
        // behavior matches: Read throws, and the reader survives.
        var ex = Throws<SimulatedSqlException>(() =>
        {
            while (reader.Read())
            {
            }
        });
        AreEqual(8134, ex.Number);

        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("after", reader.GetValue(0));
    }

    /// <summary>Probe 4: ExecuteScalar with the error before the first result set.</summary>
    [TestMethod]
    public void ExecuteScalar_ErrorBeforeFirstResult_Throws()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("select 1/0; select 42"));
        AreEqual(8134, ex.Number);
        AreEqual(1, ex.Errors.Count);
    }

    /// <summary>Probe 5: ExecuteScalar with the error after the first result set still throws (drains the batch).</summary>
    [TestMethod]
    public void ExecuteScalar_ErrorAfterFirstResult_ThrowsRatherThanReturningValue()
    {
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("select 42; select 1/0"));
        AreEqual(8134, ex.Number);
    }

    /// <summary>Probe 6: disposing a reader without draining executes the remaining statements and swallows their errors.</summary>
    [TestMethod]
    public void ReaderDispose_WithoutDraining_ExecutesRemainingStatements_SwallowsErrors()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();

        using (var command = connection.CreateCommand(
            "select 'rs1' as a; insert t values (1); select 1/0; insert t values (2)"))
        {
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());
            // Dispose here (end of using) without draining.
        }

        // Both inserts ran during the drain-on-dispose; the divide-by-zero was
        // swallowed (no exception escaped the dispose).
        AreEqual(2, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>Probe 7: a batch-aborting bind error (Msg 208) stops the following statements.</summary>
    [TestMethod]
    public void BatchAbortingError_Msg208_StopsFollowingStatements()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();

        using var batch = connection.CreateCommand(
            "insert t values (1); select * from table_that_does_not_exist; insert t values (2)");
        var ex = Throws<SimulatedSqlException>(() => batch.ExecuteNonQuery());
        AreEqual(208, ex.Number);
        AreEqual(1, ex.Errors.Count);

        // The insert before the miss ran; the one after did not.
        AreEqual(1, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>Probe 8: severity-16 RAISERROR continues; an uncaught THROW aborts. Both errors aggregate.</summary>
    [TestMethod]
    public void RaiserrorSeverity16Continues_ThrowAborts_AggregatesBothErrors()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();

        using var batch = connection.CreateCommand(
            "insert t values (1); raiserror('custom failure', 16, 1); insert t values (2); throw 50001, 'thrown failure', 1; insert t values (3)");
        var ex = Throws<SimulatedSqlException>(() => batch.ExecuteNonQuery());

        // RAISERROR's Msg 50000 and THROW's Msg 50001, in batch order.
        AreEqual(2, ex.Errors.Count);
        AreEqual(50000, ex.Errors[0].Number);
        AreEqual(50001, ex.Errors[1].Number);

        // The two inserts before the THROW ran; the THROW aborted the batch so
        // the third insert did not.
        AreEqual(2, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }

    /// <summary>
    /// A single non-row-returning statement that fails surfaces eagerly at
    /// ExecuteReader — real SQL Server sent no result-set envelope, so SqlClient
    /// throws on the advance rather than a later Read. This is what lets EF
    /// Core's no-OUTPUT modification batches, which never call Read, observe a
    /// failed INSERT / UPDATE / DELETE.
    /// </summary>
    [TestMethod]
    public void NonRowReturningError_SurfacesEagerlyAtExecuteReader()
    {
        using var connection = new Simulation().CreateOpenConnection();
        var ex = Throws<SimulatedSqlException>(() =>
        {
            using var reader = connection.CreateCommand("drop table #nope").ExecuteReader();
        });
        AreEqual(3701, ex.Number);
    }

    /// <summary>
    /// A non-row-returning error between two result sets surfaces on the
    /// NextResult that advances onto it (not a later Read) — matching how real
    /// SqlClient surfaces an error token that no COLMETADATA precedes.
    /// </summary>
    [TestMethod]
    public void NonRowReturningError_BetweenResultSets_SurfacesAtNextResult()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1 as a; drop table #nope; select 2 as b");
        using var reader = command.ExecuteReader();

        IsTrue(reader.Read());
        AreEqual(1, reader.GetValue(0));
        IsFalse(reader.Read());

        var ex = Throws<SimulatedSqlException>(() => reader.NextResult());
        AreEqual(3701, ex.Number);
    }

    /// <summary>
    /// A severity ≤ 10 RAISERROR is informational, not a statement error: it
    /// raises the <see cref="SimulatedDbConnection.InfoMessage"/> event and the
    /// batch continues without an exception.
    /// </summary>
    [TestMethod]
    public void InformationalRaiserror_DoesNotThrow_FiresInfoMessage_BatchContinues()
    {
        using var connection = (SimulatedDbConnection)new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t (id int)").ExecuteNonQuery();

        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);

        using var batch = connection.CreateCommand(
            "insert t values (1); raiserror('just a note', 5, 1); insert t values (2)");
        var affected = batch.ExecuteNonQuery();

        AreEqual(2, affected);
        Contains("just a note", string.Join("\n", messages));
        AreEqual(2, connection.CreateCommand("select count(*) from t").ExecuteScalar());
    }
}
