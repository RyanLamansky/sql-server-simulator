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
    /// Handles a Transaction Manager request (begin / commit / rollback /
    /// save), mapping it onto the session connection's transaction API.
    /// SqlClient sends these for the <c>SqlTransaction</c> object model;
    /// SQL-text transactions never arrive this way.
    /// </summary>
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
                        var isolation = offset < payload.Length ? payload[offset] : (byte)0;
                        this.transaction = this.connection!.BeginTransaction(MapIsolationLevel(isolation));
                        writer.WriteEnvChangeTransaction(Tds.EnvBeginTransaction, ++this.lastTransactionDescriptor);
                        writer.WriteDone(Tds.DoneFinal, 0);
                        break;
                    }

                case Tds.TmCommitTransaction:
                    if (this.transaction is null)
                        throw new InvalidOperationException("The Transaction Manager commit request has no corresponding BEGIN TRANSACTION.");

                    this.transaction.Commit();
                    this.transaction = null;
                    writer.WriteEnvChangeTransaction(Tds.EnvCommitTransaction, 0);
                    writer.WriteDone(Tds.DoneFinal, 0);
                    break;
                case Tds.TmRollbackTransaction:
                    {
                        var name = ReadTransactionName(payload, ref offset);
                        if (name.Length == 0)
                        {
                            if (this.transaction is null)
                                throw new InvalidOperationException("The Transaction Manager rollback request has no corresponding BEGIN TRANSACTION.");

                            this.transaction.Rollback();
                            this.transaction = null;
                            writer.WriteEnvChangeTransaction(Tds.EnvRollbackTransaction, 0);
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
