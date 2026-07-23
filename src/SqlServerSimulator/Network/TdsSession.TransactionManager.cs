using System.Buffers.Binary;
using System.Data;
using System.Text;

namespace SqlServerSimulator.Network;

internal sealed partial class TdsSession
{
    /// <summary>
    /// The transaction opened by a Transaction Manager begin request; SQL-text
    /// transactions manage the connection state directly and bypass this.
    /// </summary>
    private SimulatedDbTransaction? transaction;

    /// <summary>
    /// Source of the opaque 8-byte transaction descriptors handed to clients
    /// in the begin ENVCHANGE and echoed back in ALL_HEADERS.
    /// </summary>
    private ulong lastTransactionDescriptor;

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
        this.connection = testConnection;
        this.ExecuteTransactionManagerRequest(message, writer);
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
                        this.transaction = this.connection!.BeginTransaction(MapIsolationLevel(this.lastTmIsolation));
                        writer.WriteEnvChangeTransaction(Tds.EnvBeginTransaction, ++this.lastTransactionDescriptor);
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
                        if (this.transaction is null)
                            throw new InvalidOperationException("The Transaction Manager commit request has no corresponding BEGIN TRANSACTION.");

                        this.transaction.Commit();
                        this.transaction = null;
                        writer.WriteEnvChangeTransaction(Tds.EnvCommitTransaction, this.lastTransactionDescriptor);
                        this.BeginFollowOnTransactionIfRequested(beginNext, writer);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmRollbackTransaction:
                    {
                        // ROLLBACK_XACT body: name (B_VARBYTE) + flags. A named
                        // rollback targets a savepoint (transaction stays open, no
                        // ENVCHANGE, fBeginXact not meaningful); a nameless
                        // rollback ends the transaction and, when fBeginXact is
                        // set (ODBC manual-commit), opens the next one.
                        var name = ReadTransactionName(payload, ref offset);
                        var beginNext = ReadBeginXactFlag(payload, ref offset);
                        if (name.Length == 0)
                        {
                            if (this.transaction is null)
                                throw new InvalidOperationException("The Transaction Manager rollback request has no corresponding BEGIN TRANSACTION.");

                            this.transaction.Rollback();
                            this.transaction = null;
                            writer.WriteEnvChangeTransaction(Tds.EnvRollbackTransaction, this.lastTransactionDescriptor);
                            this.BeginFollowOnTransactionIfRequested(beginNext, writer);
                        }
                        else
                        {
                            // Rollback to a savepoint keeps the transaction alive,
                            // so no transaction ENVCHANGE is emitted.
                            this.ExecuteTransactionStatement($"rollback transaction [{name.Replace("]", "]]", StringComparison.Ordinal)}]");
                        }

                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmSaveTransaction:
                    {
                        var name = ReadTransactionName(payload, ref offset);
                        this.ExecuteTransactionStatement($"save transaction [{name.Replace("]", "]]", StringComparison.Ordinal)}]");
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
            writer.WriteDone(Tds.DoneError, 0);
        }
        catch (InvalidOperationException ex)
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            writer.WriteDone(Tds.DoneError, 0);
        }
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
    /// request's isolation and emitting the begin ENVCHANGE (a new descriptor)
    /// the driver expects before the response DONE.
    /// </summary>
    private void BeginFollowOnTransactionIfRequested(bool beginNext, TdsTokenWriter writer)
    {
        if (!beginNext)
            return;
        this.transaction = this.connection!.BeginTransaction(MapIsolationLevel(this.lastTmIsolation));
        writer.WriteEnvChangeTransaction(Tds.EnvBeginTransaction, ++this.lastTransactionDescriptor);
    }

    private void ExecuteTransactionStatement(string statement)
    {
        using var command = this.connection!.CreateCommand();
#pragma warning disable CA2100 // Bracket-escaped savepoint statement synthesized from the client's TM request.
        command.CommandText = statement;
#pragma warning restore CA2100
        _ = command.ExecuteNonQuery();
    }

    private static IsolationLevel MapIsolationLevel(byte wire) => wire switch
    {
        1 => IsolationLevel.ReadUncommitted,
        2 => IsolationLevel.ReadCommitted,
        3 => IsolationLevel.RepeatableRead,
        4 => IsolationLevel.Serializable,
        5 => IsolationLevel.Snapshot,
        _ => IsolationLevel.ReadCommitted,
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
