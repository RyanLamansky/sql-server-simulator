using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>SET XACT_ABORT</c>'s error-promotion semantics, probed against SQL
/// Server 2025 (2026-08-06). The option turns the statement-terminating
/// run-time error family into a batch-aborting, transaction-rolling one; a
/// <c>TRY</c> frame still catches the error but the transaction is left doomed.
/// <c>RAISERROR</c> is the exemption. See
/// <c>docs/claude/transactions.md#set-xact_abort</c>.
/// </summary>
[TestClass]
public sealed class XactAbortTests
{
    private const string Seed = """
        CREATE TABLE parent (id int PRIMARY KEY);
        CREATE TABLE child (id int PRIMARY KEY, pid int REFERENCES parent (id));
        CREATE TABLE t (id int PRIMARY KEY, v int NOT NULL, s varchar(3) NULL);
        INSERT INTO parent (id) VALUES (1);
        """;

    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        using var seed = connection.CreateCommand(Seed);
        _ = seed.ExecuteNonQuery();
        return connection;
    }

    private static object? Scalar(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand(commandText);
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Runs a batch expected to fail and reports the error number it raised
    /// (<c>0</c> for a clean run), so the assertions that follow can read the
    /// session state the failure left behind.
    /// </summary>
    private static int RunReportingError(DbConnection connection, string commandText)
    {
        try
        {
            using var command = connection.CreateCommand(commandText);
            _ = command.ExecuteNonQuery();
            return 0;
        }
        catch (SimulatedSqlException ex)
        {
            return ex.Number;
        }
    }

    /// <summary>
    /// Each row is a run-time error real promotes: with the option OFF the
    /// transaction stands at 1 and the inserted row survives, with it ON the
    /// whole transaction rolls back. Msg numbers as probed — 547 FK, 2627
    /// duplicate key, 515 NOT NULL, 8134 divide by zero, 2628 truncation, 208
    /// deferred name resolution.
    /// </summary>
    [TestMethod]
    [DataRow("INSERT INTO child (id, pid) VALUES (1, 99);", 547)]
    [DataRow("INSERT INTO t (id, v) VALUES (100, 2);", 2627)]
    [DataRow("INSERT INTO t (id, v) VALUES (101, NULL);", 515)]
    [DataRow("SELECT 1 / 0;", 8134)]
    [DataRow("INSERT INTO t (id, v, s) VALUES (102, 1, 'abcd');", 2628)]
    [DataRow("SELECT * FROM no_such_table_zz;", 208)]
    public void PromotedErrorRollsBackWhereTheOptionOffLeavesItStanding(string failing, int expectedNumber)
    {
        using var off = Seeded();
        AreEqual(expectedNumber, RunReportingError(
            off, $"SET XACT_ABORT OFF; BEGIN TRAN; INSERT INTO t (id, v) VALUES (100, 1); {failing}"));
        AreEqual(1, Scalar(off, "SELECT @@TRANCOUNT"));
        AreEqual(1, Scalar(off, "SELECT COUNT(*) FROM t"));

        using var on = Seeded();
        AreEqual(expectedNumber, RunReportingError(
            on, $"SET XACT_ABORT ON; BEGIN TRAN; INSERT INTO t (id, v) VALUES (100, 1); {failing}"));
        AreEqual(0, Scalar(on, "SELECT @@TRANCOUNT"));
        AreEqual(0, Scalar(on, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>
    /// The promotion also ends the batch: an error real would let the batch run
    /// past (Msg 8134 with the option off) leaves the following statement unrun
    /// with it on.
    /// </summary>
    [TestMethod]
    public void PromotedErrorEndsTheBatch()
    {
        using var off = Seeded();
        AreEqual(8134, RunReportingError(off, "SET XACT_ABORT OFF; SELECT 1 / 0; INSERT INTO t (id, v) VALUES (7, 7);"));
        AreEqual(1, Scalar(off, "SELECT COUNT(*) FROM t"));

        using var on = Seeded();
        AreEqual(8134, RunReportingError(on, "SET XACT_ABORT ON; SELECT 1 / 0; INSERT INTO t (id, v) VALUES (7, 7);"));
        AreEqual(0, Scalar(on, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>The whole stack, not one level: <c>@@TRANCOUNT</c> 2 reads 0.</summary>
    [TestMethod]
    public void PromotedErrorRollsBackTheWholeStack()
    {
        using var connection = Seeded();
        AreEqual(8134, RunReportingError(connection, "SET XACT_ABORT ON; BEGIN TRAN; BEGIN TRAN; SELECT 1 / 0;"));
        AreEqual(0, Scalar(connection, "SELECT @@TRANCOUNT"));
    }

    /// <summary>
    /// RAISERROR is exempt at every severity: the batch runs on and the
    /// transaction stays committable, in contrast with THROW.
    /// </summary>
    [TestMethod]
    public void RaiserrorIsExemptWhileThrowIsNot()
    {
        using var connection = Seeded();
        AreEqual(50000, RunReportingError(
            connection, "SET XACT_ABORT ON; BEGIN TRAN; RAISERROR('boom', 16, 1); INSERT INTO t (id, v) VALUES (5, 5);"));
        AreEqual(1, Scalar(connection, "SELECT @@TRANCOUNT"));
        AreEqual((short)1, Scalar(connection, "SELECT XACT_STATE()"));
        AreEqual(1, Scalar(connection, "SELECT COUNT(*) FROM t"));
        _ = RunReportingError(connection, "ROLLBACK");

        AreEqual(50000, RunReportingError(
            connection, "SET XACT_ABORT ON; BEGIN TRAN; THROW 50000, 'boom', 1; INSERT INTO t (id, v) VALUES (6, 6);"));
        AreEqual(0, Scalar(connection, "SELECT @@TRANCOUNT"));
        AreEqual(0, Scalar(connection, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>
    /// Inside a TRY the CATCH runs, <c>@@TRANCOUNT</c> is untouched and
    /// <c>XACT_STATE()</c> reads -1. RAISERROR dooms the transaction here even
    /// though it doesn't when uncaught.
    /// </summary>
    [TestMethod]
    [DataRow("INSERT INTO child (id, pid) VALUES (1, 99);", 547)]
    [DataRow("RAISERROR('boom', 16, 1);", 50000)]
    [DataRow("THROW 50000, 'boom', 1;", 50000)]
    public void CaughtErrorDoomsTheTransaction(string failing, int expectedNumber)
    {
        using var connection = Seeded();
        using var command = connection.CreateCommand($"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                {failing}
            END TRY
            BEGIN CATCH
                SELECT ERROR_NUMBER() AS n, @@TRANCOUNT AS tc, XACT_STATE() AS xs;
            END CATCH
            ROLLBACK;
            """);
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(expectedNumber, reader.GetInt32(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual((short)-1, reader.GetInt16(2));
    }

    /// <summary>
    /// A statement that writes to the log inside a doomed transaction is
    /// Msg 3930; a read is not. COMMIT and SAVE TRANSACTION take the same
    /// refusal, and a nested TRY can catch it.
    /// </summary>
    [TestMethod]
    [DataRow("INSERT INTO t (id, v) VALUES (9, 1);")]
    [DataRow("CREATE TABLE zz (a int);")]
    [DataRow("COMMIT;")]
    [DataRow("SAVE TRAN s1;")]
    public void DoomedTransactionRefusesWrites(string write)
    {
        using var connection = Seeded();
        AreEqual(3930, Scalar(connection, $"""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                SELECT 1 / 0;
            END TRY
            BEGIN CATCH
                BEGIN TRY
                    {write}
                END TRY
                BEGIN CATCH
                    SELECT ERROR_NUMBER();
                END CATCH
            END CATCH
            ROLLBACK;
            """));
    }

    /// <summary>A read inside a doomed transaction answers normally.</summary>
    [TestMethod]
    public void DoomedTransactionStillReads()
    {
        using var connection = Seeded();
        AreEqual(1, Scalar(connection, """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                SELECT 1 / 0;
            END TRY
            BEGIN CATCH
                SELECT COUNT(*) FROM parent;
            END CATCH
            ROLLBACK;
            """));
    }

    /// <summary>
    /// A batch that ends with the transaction still doomed reports Msg 3998
    /// and rolls back.
    /// </summary>
    [TestMethod]
    public void DoomedTransactionAtEndOfBatchRaises3998()
    {
        using var connection = Seeded();
        using (var command = connection.CreateCommand("""
            SET XACT_ABORT ON;
            BEGIN TRAN;
            INSERT INTO t (id, v) VALUES (3, 1);
            BEGIN TRY
                SELECT 1 / 0;
            END TRY
            BEGIN CATCH
            END CATCH
            """))
        {
            var ex = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
            AreEqual(3998, ex.Number);
            AreEqual("Uncommittable transaction is detected at the end of the batch. The transaction is rolled back.", ex.Message);
        }

        AreEqual(0, Scalar(connection, "SELECT @@TRANCOUNT"));
        AreEqual(0, Scalar(connection, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>ROLLBACK inside the CATCH clears the doom and the batch runs on.</summary>
    [TestMethod]
    public void RollbackInCatchClearsTheDoom()
    {
        using var connection = Seeded();
        AreEqual(1, Scalar(connection, """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                SELECT 1 / 0;
            END TRY
            BEGIN CATCH
                ROLLBACK;
                INSERT INTO t (id, v) VALUES (10, 1);
            END CATCH
            SELECT COUNT(*) FROM t;
            """));
    }

    /// <summary>
    /// An error raised inside a procedure with no TRY of its own, called from
    /// inside the caller's TRY, is caught by the caller and dooms the
    /// transaction rather than rolling it back — which is why the TRY question
    /// is asked of the whole session stack, not one batch frame.
    /// </summary>
    [TestMethod]
    public void ProcedureErrorInsideCallerTryDooms()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE PROC p AS INSERT INTO child (id, pid) VALUES (1, 99);"))
        {
            _ = create.ExecuteNonQuery();
        }

        AreEqual((short)-1, Scalar(connection, """
            SET XACT_ABORT ON;
            BEGIN TRAN;
            BEGIN TRY
                EXEC p;
            END TRY
            BEGIN CATCH
                SELECT XACT_STATE();
            END CATCH
            ROLLBACK;
            """));
    }

    /// <summary>
    /// An uncaught error inside a procedure body ends the whole calling batch,
    /// not just the body.
    /// </summary>
    [TestMethod]
    public void ProcedureErrorEndsTheCallingBatch()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE PROC p AS INSERT INTO child (id, pid) VALUES (1, 99);"))
        {
            _ = create.ExecuteNonQuery();
        }

        AreEqual(547, RunReportingError(
            connection, "SET XACT_ABORT ON; BEGIN TRAN; EXEC p; INSERT INTO t (id, v) VALUES (11, 1);"));
        AreEqual(0, Scalar(connection, "SELECT @@TRANCOUNT"));
        AreEqual(0, Scalar(connection, "SELECT COUNT(*) FROM t"));
    }

    /// <summary>
    /// A body's own <c>SET XACT_ABORT</c> binds while it runs and reverts when
    /// it returns — for a procedure and for dynamic SQL alike.
    /// </summary>
    [TestMethod]
    public void ModuleScopedSetReverts()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE PROC p AS SET XACT_ABORT ON;"))
        {
            _ = create.ExecuteNonQuery();
        }

        AreEqual(0, Scalar(connection, "SET XACT_ABORT OFF; EXEC p; SELECT @@OPTIONS & 16384"));
        AreEqual(16384, Scalar(connection, "EXEC ('SET XACT_ABORT ON; SELECT @@OPTIONS & 16384');"));
        AreEqual(0, Scalar(connection, "SELECT @@OPTIONS & 16384"));
    }

    /// <summary>
    /// A body inherits the caller's setting: with the caller ON, the procedure
    /// reads the bit set.
    /// </summary>
    [TestMethod]
    public void BodyInheritsCallerSetting()
    {
        using var connection = Seeded();
        using (var create = connection.CreateCommand("CREATE PROC p AS SELECT @@OPTIONS & 16384;"))
        {
            _ = create.ExecuteNonQuery();
        }

        AreEqual(16384, Scalar(connection, "SET XACT_ABORT ON; EXEC p;"));
    }

    /// <summary><c>@@OPTIONS</c> bit 16384 tracks the option; a fresh session has it clear.</summary>
    [TestMethod]
    public void OptionsBitTracksTheSetting()
    {
        AreEqual(5432, new Simulation().ExecuteScalar("SELECT @@OPTIONS"));
        AreEqual(0, new Simulation().ExecuteScalar("SELECT @@OPTIONS & 16384"));
        AreEqual(16384, new Simulation().ExecuteScalar("SET XACT_ABORT ON; SELECT @@OPTIONS & 16384"));
        AreEqual(0, new Simulation().ExecuteScalar("SET XACT_ABORT ON; SET XACT_ABORT OFF; SELECT @@OPTIONS & 16384"));
    }

    /// <summary>
    /// <c>XACT_STATE()</c> keeps its 0 / 1 readings while nothing is doomed.
    /// </summary>
    [TestMethod]
    public void XactStateReadsZeroAndOneWithoutDoom()
    {
        AreEqual((short)0, new Simulation().ExecuteScalar("SELECT XACT_STATE()"));
        AreEqual((short)1, new Simulation().ExecuteScalar("BEGIN TRAN; SELECT XACT_STATE()"));
    }
}
