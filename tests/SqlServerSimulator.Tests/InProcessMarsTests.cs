using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The MARS rules real SQL Server applies to the requests of one session,
/// which the in-process connection — a MARS one — applies with an open
/// reader as a pending request until it has read past its batch's end
/// (probed 2026-10-06 against SQL Server 2025 over a MARS SqlClient
/// connection). See <c>docs/claude/data-reader.md#in-process-mars-overlapping-readers</c>.
/// </summary>
[TestClass]
public sealed class InProcessMarsTests
{
    private static DbConnection Seeded()
    {
        var connection = new Simulation().CreateOpenConnection();
        using var seed = connection.CreateCommand("create table big (id int primary key, v int not null, pad char(2000) not null default 'x'); insert big (id, v) select value, 0 from generate_series(1, 200)");
        _ = seed.ExecuteNonQuery();
        return connection;
    }

    private static object? Scalar(DbConnection connection, string commandText, DbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand(commandText);
        command.Transaction = transaction;
        return command.ExecuteScalar();
    }

    private static SimulatedSqlException Refused(Action action) => ThrowsExactly<SimulatedSqlException>(action);

    /// <summary>Reads the rest of a reader's batch, returning the error its end raised, if any.</summary>
    private static SimulatedSqlException? Drain(DbDataReader reader)
    {
        try
        {
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
            return null;
        }
        catch (SimulatedSqlException error)
        {
            return error;
        }
    }

    [TestMethod]
    public void ConnectionString_AnnouncesMars()
    {
        using var connection = new Simulation().CreateDbConnection();
        AreEqual("MultipleActiveResultSets=True", connection.ConnectionString);
    }

    /// <summary>
    /// A begin while a reader is pending is Msg 3988 at line 1; once the
    /// reader has read past the end of a single-statement batch it no longer
    /// counts.
    /// </summary>
    [TestMethod]
    public void BeginTransaction_WhileAReaderIsPending_IsMsg3988()
    {
        using var connection = Seeded();
        using var command = connection.CreateCommand("select id, pad from big order by id");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());

        var refused = Refused(() => connection.BeginTransaction());
        AreEqual(3988, refused.Number);
        AreEqual(1, refused.LineNumber);

        while (reader.Read())
        {
        }
        connection.BeginTransaction().Rollback();
    }

    /// <summary>
    /// A reader counts as outstanding until it has read past its end, however
    /// small its result: SqlClient tells real so in each request's header.
    /// </summary>
    [TestMethod]
    public void BeginTransaction_WhileASmallReaderIsUnread_IsMsg3988()
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());
        AreEqual(3988, Refused(() => connection.BeginTransaction()).Number);
        IsFalse(reader.Read());
        connection.BeginTransaction().Rollback();
    }

    /// <summary>
    /// A commit under a small reader in the transaction is refused with
    /// Msg 3981 though its response has all gone out — which also means its
    /// batch had already run on, and the rollback the refusal makes leaves
    /// it alone: no Msg 3989, no Msg 3998.
    /// </summary>
    [TestMethod]
    public void Commit_UnderASmallReaderInTheTransaction_IsMsg3981_AndLeavesTheReaderAlone()
    {
        using var connection = Seeded();
        var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand("select 1; select concat(@@trancount, ',', xact_state())");
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());

        AreEqual(3981, Refused(transaction.Commit).Number);
        AreEqual(1, Scalar(connection, "select 1"));
        IsFalse(reader.Read());
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("1,1", reader.GetString(0));
        IsNull(Drain(reader));
    }

    /// <summary>
    /// A reader that began outside any transaction doesn't stop another
    /// command beginning, saving and committing its own.
    /// </summary>
    [TestMethod]
    public void AutocommitReader_LeavesAnotherCommandFreeToCommit()
    {
        using var connection = Seeded();
        using var command = connection.CreateCommand("select id, pad from big order by id");
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());

        AreEqual(0, Scalar(connection, "begin tran; save tran s; update big set v = 5 where id = 1; commit; select @@trancount"));
        AreEqual("0,5", Scalar(connection, "select concat(@@trancount, ',', (select v from big where id = 1))"));
        IsNull(Drain(reader));
    }

    /// <summary>
    /// A commit or save point under a pending reader working in the
    /// transaction is Msg 3981 at line 1 — through the API (state 1 / 2) or
    /// by SQL text — and rolls the transaction back; until the reader has
    /// finished every command is Msg 3989, and its batch carries on doomed
    /// and ends with Msg 3998.
    /// </summary>
    [TestMethod]
    [DataRow("api commit", 1)]
    [DataRow("api save", 2)]
    [DataRow("commit", 1)]
    [DataRow("save tran s", 2)]
    public void TransactionOperation_UnderAPendingReaderInTheTransaction_IsMsg3981(string operation, int state)
    {
        using var connection = Seeded();
        var transaction = connection.BeginTransaction();
        AreEqual(1, Scalar(connection, "update big set v = 5 where id = 1; select @@rowcount", transaction));
        using var command = connection.CreateCommand("select id, pad from big order by id; select concat(@@trancount, ',', xact_state())");
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        IsTrue(reader.Read());

        var refused = Refused(() =>
        {
            switch (operation)
            {
                case "api commit":
                    transaction.Commit();
                    break;
                case "api save":
                    transaction.Save("s");
                    break;
                default:
                    _ = Scalar(connection, operation, transaction);
                    break;
            }
        });
        AreEqual(3981, refused.Number);
        AreEqual(state, refused.State);
        AreEqual(1, refused.LineNumber);
        AreEqual(3989, Refused(() => Scalar(connection, "select 1")).Number);

        while (reader.Read())
        {
        }
        IsTrue(reader.NextResult());
        IsTrue(reader.Read());
        AreEqual("1,-1", reader.GetString(0));
        var end = Drain(reader);
        IsNotNull(end);
        AreEqual(3998, end.Number);
        AreEqual("0,0", Scalar(connection, "select concat(@@trancount, ',', (select v from big where id = 1))"));
    }

    /// <summary>
    /// A rollback under a pending reader working in the transaction goes
    /// ahead, but the reader's batch then refuses a write with Msg 3930.
    /// </summary>
    [TestMethod]
    public void Rollback_UnderAPendingReader_LeavesItsBatchDoomed()
    {
        using var connection = Seeded();
        var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand("select id, pad from big order by id; update big set v = 9 where id = 2"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            IsTrue(reader.Read());

            transaction.Rollback();
            AreEqual(3989, Refused(() => Scalar(connection, "select 1")).Number);
            var refused = Drain(reader);
            IsNotNull(refused);
            AreEqual(3930, refused.Number);
        }
        AreEqual("0,0", Scalar(connection, "select concat(@@trancount, ',', (select v from big where id = 2))"));
    }

    /// <summary>
    /// A transaction a batch begins and leaves open while a reader is pending
    /// is rolled back with Msg 3997, as a MARS batch's; with nothing else
    /// pending the batch leaves it open, as a batch on a connection without
    /// MARS does.
    /// </summary>
    [TestMethod]
    public void BatchTransactionLeftOpen_WhileAReaderIsPending_IsMsg3997()
    {
        using var connection = Seeded();
        using (var command = connection.CreateCommand("select id, pad from big order by id"))
        using (var reader = command.ExecuteReader())
        {
            IsTrue(reader.Read());
            var stillActive = Refused(() => Scalar(connection, "begin tran; update big set v = 5 where id = 1"));
            AreEqual(3997, stillActive.Number);
            AreEqual(1, stillActive.LineNumber);
            AreEqual("0,0", Scalar(connection, "select concat(@@trancount, ',', (select v from big where id = 1))"));
        }

        AreEqual(1, Scalar(connection, "begin tran; select @@trancount"));
        AreEqual(1, Scalar(connection, "select @@trancount"));
    }

    /// <summary>
    /// While a reader is parked on a DML statement's <c>OUTPUT</c> rows larger
    /// than what real gets ahead of its client, every other command is
    /// Msg 3980; smaller rows hold nothing.
    /// </summary>
    [TestMethod]
    public void DmlOutputRowsBeingRead_RefuseOtherCommandsWithMsg3980()
    {
        using var connection = Seeded();
        using (var command = connection.CreateCommand("update big set v = v + 1 output inserted.id, inserted.pad"))
        using (var reader = command.ExecuteReader())
        {
            IsTrue(reader.Read());
            var busy = Refused(() => Scalar(connection, "select 1"));
            AreEqual(3980, busy.Number);
            AreEqual(1, busy.LineNumber);
            AreEqual(3980, Refused(() => connection.BeginTransaction()).Number);
            IsNull(Drain(reader));
        }
        AreEqual(1, Scalar(connection, "select 1"));

        using (var command = connection.CreateCommand("update big set v = v + 1 output inserted.id where id <= 10"))
        using (var reader = command.ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(1, Scalar(connection, "select 1"));
        }
    }

    /// <summary>
    /// A batch runs on as far as its output fits what real gets ahead of its
    /// client: past a small result set at once, past a large one once the
    /// reader has read its last row. What a statement run ahead reported
    /// waits for the advance onto it.
    /// </summary>
    [TestMethod]
    public void Batch_RunsAheadOfTheReader_AsFarAsItsOutputFits()
    {
        using var connection = Seeded();
        using (var command = connection.CreateCommand("select 1; update big set v = 6 where id = 1; select 2"))
        using (var reader = command.ExecuteReader())
        {
            AreEqual(6, Scalar(connection, "select v from big where id = 1"));
            AreEqual(-1, reader.RecordsAffected);
            IsTrue(reader.NextResult());
            AreEqual(1, reader.RecordsAffected);
        }

        using (var command = connection.CreateCommand("select id, pad from big order by id; update big set v = 7 where id = 1; select 2"))
        using (var reader = command.ExecuteReader())
        {
            IsTrue(reader.Read());
            AreEqual(6, Scalar(connection, "select v from big where id = 1"));
            while (reader.Read())
            {
            }
            AreEqual(7, Scalar(connection, "select v from big where id = 1"));
            IsTrue(reader.NextResult());
            IsTrue(reader.Read());
            AreEqual(2, reader.GetInt32(0));
        }
    }
}
