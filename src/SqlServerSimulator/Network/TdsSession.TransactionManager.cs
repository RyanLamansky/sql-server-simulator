using System.Buffers.Binary;
using System.Data;
using System.Text;

namespace SqlServerSimulator.Network;

internal sealed partial class TdsSession
{
    /// <summary>
    /// Isolation byte from the most recent TM begin request, reused when a
    /// commit / rollback carries <c>fBeginXact</c> and the follow-on transaction
    /// is opened (ODBC's manual-commit mode — see the commit / rollback arms).
    /// </summary>
    private byte lastTmIsolation;

    /// <summary>
    /// Handles a Transaction Manager request (begin / commit / rollback /
    /// save), mapping it onto the session connection's transaction API.
    /// SqlClient sends these for the <c>SqlTransaction</c> object model;
    /// SQL-text transactions never arrive this way.
    /// </summary>
    /// <summary>
    /// Test-only entry to the TM request handler over a caller-supplied
    /// connection and writer, with no socket / login. The SqlClient oracle
    /// begins each transaction explicitly and never sets <c>fBeginXact</c>, so
    /// the follow-on begin and the descriptor-carrying commit / rollback
    /// ENVCHANGE (the ODBC manual-commit path) would otherwise have no
    /// automated coverage. Oracle: <c>TransactionManagerFBeginXactTests</c>.
    /// </summary>
    internal void RunTransactionManagerRequestForTesting(SimulatedDbConnection testConnection, TdsMessage message, TdsTokenWriter writer)
    {
        testConnection.FramesEveryStatement = true;
        this.connection = testConnection;
        testConnection.TransactionEvents ??= [];
        this.ExecuteTransactionManagerRequest(message, writer);
    }

    /// <summary>
    /// Test-only entry to the SQL-batch handler over a caller-supplied
    /// connection and writer, with no socket / login, for asserting the token
    /// stream a batch produces — its transaction ENVCHANGEs above all, which
    /// SqlClient consumes without surfacing. Oracle:
    /// <c>TransactionEnvChangeTests</c>.
    /// </summary>
    internal void RunBatchForTesting(SimulatedDbConnection testConnection, TdsMessage message, TdsTokenWriter writer)
    {
        testConnection.FramesEveryStatement = true;
        this.connection = testConnection;
        testConnection.TransactionEvents ??= [];
        this.ExecuteBatchAsync(message, writer, CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    private void ExecuteTransactionManagerRequest(TdsMessage message, TdsTokenWriter writer)
    {
        var payload = message.Payload;
        var offset = SkipAllHeaders(payload);
        if (offset + 2 > payload.Length)
            throw new InvalidDataException("Transaction Manager request is missing its request type.");

        var requestType = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset));
        offset += 2;

        try
        {
            switch (requestType)
            {
                case Tds.TmBeginTransaction:
                    {
                        this.lastTmIsolation = offset < payload.Length ? payload[offset] : (byte)0;
                        // A begin arriving while a transaction is already open
                        // nests on real — @@TRANCOUNT rises, and no ENVCHANGE is
                        // sent (probed 2026-09-28 against SQL Server 2025). The
                        // parallel-transaction refusal is SqlClient's own
                        // client-side rule, not the server's.
                        var isolationLevel = MapIsolationLevel(this.lastTmIsolation);
                        if (this.connection!.CurrentTransaction is { } open)
                        {
                            if (isolationLevel != IsolationLevel.Unspecified)
                                this.connection.SessionIsolationLevel = isolationLevel;
                            open.TranCount++;
                        }
                        else
                        {
                            _ = this.connection.StartTransaction(isolationLevel);
                        }
                        this.WriteTransactionEnvChanges(writer);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmCommitTransaction:
                    {
                        // COMMIT_XACT body: name (B_VARBYTE) + flags (fBeginXact
                        // in bit 0). ODBC's manual-commit mode sets fBeginXact so
                        // the server opens the next transaction immediately —
                        // that's how it holds @@TRANCOUNT at 1 continuously
                        // (probe-confirmed against SQL Server 2025, 2026-07-23);
                        // dropping the follow-on begin desyncs the driver.
                        _ = ReadTransactionName(payload, ref offset);
                        var beginNext = ReadBeginXactFlag(payload, ref offset);
                        // With nothing open — never begun, or ended by the
                        // engine or SQL text since — the request is Msg 3902 at
                        // state 3 and opens nothing (probed 2026-09-28).
                        var open = this.connection!.CurrentTransaction
                            ?? throw SimulatedSqlException.NoCorrespondingBeginCommit(state: 3);
                        // The request ends one nesting level, and the
                        // transaction only with the last.
                        if (open.TranCount > 1)
                            open.TranCount--;
                        else
                            open.EndCommit();
                        this.BeginFollowOnTransactionIfRequested(beginNext);
                        this.WriteTransactionEnvChanges(writer);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmRollbackTransaction:
                    {
                        // ROLLBACK_XACT body: name (B_VARBYTE) + flags. A named
                        // rollback targets a savepoint (transaction stays open, no
                        // ENVCHANGE, fBeginXact not meaningful) or the transaction
                        // a BEGIN TRANSACTION named; a nameless rollback ends the
                        // transaction and, when fBeginXact is set (ODBC
                        // manual-commit), opens the next one. With nothing open
                        // either form is Msg 3903 at state 2 (probed 2026-09-28).
                        var name = ReadTransactionName(payload, ref offset);
                        var beginNext = ReadBeginXactFlag(payload, ref offset);
                        var open = this.connection!.CurrentTransaction
                            ?? throw SimulatedSqlException.NoCorrespondingBeginRollback(state: 2);
                        if (name.Length == 0)
                        {
                            open.EndRollback(TransactionEvent.StatementRollback);
                            this.BeginFollowOnTransactionIfRequested(beginNext);
                        }
                        else
                        {
                            open.RollbackByName(name);
                        }

                        this.WriteTransactionEnvChanges(writer);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmSaveTransaction:
                    {
                        var name = ReadTransactionName(payload, ref offset);
                        var open = this.connection!.CurrentTransaction
                            ?? throw SimulatedSqlException.SaveTransactionWithoutTransaction();
                        open.SetSavepointByName(name);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                default:
                    writer.WriteErrorOrInfo(
                        Tds.TokenError, 50000, 1, 16,
                        $"The SqlServerSimulator network listener does not support Transaction Manager request type {requestType}.",
                        "SIMULATED", "", 1);
                    writer.WriteDone(Tds.DoneError, 0);
                    break;
            }
        }
        catch (SimulatedSqlException ex)
        {
            WriteErrors(writer, ex);
            this.WriteTransactionEnvChanges(writer);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning disable CA1031 // Deliberate: see TdsSession.IsRecoverableStatementFault.
        catch (Exception ex) when (IsRecoverableStatementFault(ex, writer))
        {
            WriteUnexpectedStatementFault(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// The <c>fBeginXact</c> flag (bit 0 of the trailing flags byte on a TM
    /// commit / rollback request): the client asks the server to open a fresh
    /// transaction immediately after ending the current one. Absent (past the
    /// payload) reads as clear.
    /// </summary>
    private static bool ReadBeginXactFlag(byte[] payload, ref int offset)
    {
        if (offset >= payload.Length)
            return false;
        var flags = payload[offset];
        offset++;
        return (flags & 1) != 0;
    }

    /// <summary>
    /// Opens the follow-on transaction that an ODBC manual-commit
    /// <c>fBeginXact</c> commit / rollback requests, reusing the last begin
    /// request's isolation; its begin ENVCHANGE (a new descriptor) follows the
    /// ending one before the response DONE.
    /// </summary>
    private void BeginFollowOnTransactionIfRequested(bool beginNext)
    {
        if (beginNext)
            _ = this.connection!.StartTransaction(MapIsolationLevel(this.lastTmIsolation));
    }

    private static IsolationLevel MapIsolationLevel(byte wire) => wire switch
    {
        1 => IsolationLevel.ReadUncommitted,
        2 => IsolationLevel.ReadCommitted,
        3 => IsolationLevel.RepeatableRead,
        4 => IsolationLevel.Serializable,
        5 => IsolationLevel.Snapshot,
        _ => IsolationLevel.Unspecified,
    };

    /// <summary>
    /// Transaction names in TM requests are B_VARBYTE: the length prefix
    /// counts BYTES of UTF-16 data, unlike the char-counted B_VARCHAR used by
    /// most of the protocol.
    /// </summary>
    private static string ReadTransactionName(byte[] payload, ref int offset)
    {
        if (offset >= payload.Length)
            return "";

        var byteCount = payload[offset];
        offset++;
        if (offset + byteCount > payload.Length)
            throw new InvalidDataException("Transaction Manager request name extends past the end of the payload.");

        var name = Encoding.Unicode.GetString(payload.AsSpan(offset, byteCount));
        offset += byteCount;
        return name;
    }
}
