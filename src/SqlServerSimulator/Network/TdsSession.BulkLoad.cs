namespace SqlServerSimulator.Network;

internal sealed partial class TdsSession
{
    /// <summary>
    /// The <c>INSERT BULK</c> preamble parsed from the SQL batch that opened
    /// bulk-load mode, held until the following <c>BulkLoadBCP</c> data packet
    /// (type 7) arrives. Null between bulk operations.
    /// </summary>
    private BulkInsertPlan? pendingBulk;

    /// <summary>
    /// True when a SQL batch is SqlClient's <c>INSERT BULK …</c> statement,
    /// which opens bulk-load mode rather than executing as ordinary SQL.
    /// </summary>
    private static bool IsBulkInsertBatch(string text)
    {
        var span = text.AsSpan().TrimStart();
        if (!span.StartsWith("insert", StringComparison.OrdinalIgnoreCase))
            return false;
        var rest = span["insert".Length..];
        if (rest.Length == 0 || !char.IsWhiteSpace(rest[0]))
            return false;
        rest = rest.TrimStart();
        return rest.StartsWith("bulk", StringComparison.OrdinalIgnoreCase)
            && (rest.Length == 4 || !char.IsLetterOrDigit(rest[4]));
    }

    /// <summary>
    /// Parses the <c>INSERT BULK</c> preamble and stores the plan for the
    /// forthcoming data packet, acknowledging the statement with a DONE — which
    /// SqlClient reads (via <c>SubmitUpdateBulkCommand</c>) before it streams
    /// the <c>BulkLoadBCP</c> data packet. On a parse / resolution failure an
    /// ERROR + DONE is written instead and no plan is stored, so a following
    /// data packet is rejected as orphaned.
    /// </summary>
    private void BeginBulkInsert(string batchText, TdsTokenWriter writer)
    {
        this.databaseAtMessageStart = this.connection!.Database;
        try
        {
            this.pendingBulk = Simulation.PrepareBulkInsert(this.connection, batchText);
            writer.WriteDone(Tds.DoneFinal, 0);
        }
        catch (SimulatedSqlException ex)
        {
            this.pendingBulk = null;
            WriteErrors(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
        }
        catch (NotSupportedException ex)
        {
            this.pendingBulk = null;
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning disable CA1031 // Deliberate: see TdsSession.IsRecoverableStatementFault.
        catch (Exception ex) when (IsRecoverableStatementFault(ex, writer))
        {
            this.pendingBulk = null;
            WriteUnexpectedStatementFault(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Handles a <c>BulkLoadBCP</c> data packet (type 7): decodes its
    /// COLMETADATA + ROW stream and writes the rows to the pending destination
    /// with bulk-load semantics, answering with a DONE carrying the row count
    /// (or an ERROR + DONE on failure). A packet with no preceding
    /// <c>INSERT BULK</c> is a protocol error that ends the session.
    /// </summary>
    private void ExecuteBulkLoad(TdsMessage message, TdsTokenWriter writer)
    {
        var plan = this.pendingBulk;
        this.pendingBulk = null;
        if (plan is null)
        {
            // Real answers a bulk-load packet nothing awaits with a bare DONE
            // carrying the error bit, then drops the connection (probed
            // 2026-10-07 against SQL Server 2025).
            writer.WriteDone(Tds.DoneError, 0);
            this.connection!.Close();
            return;
        }

        try
        {
            var rows = TdsBulkLoadReader.ReadRows(message.Payload);
            var affected = simulation.ExecuteBulkInsert(plan, rows, this.connection!);
            this.WriteSessionEnvChangesIfAny(writer);
            writer.WriteDone(Tds.DoneCount, affected);
        }
        catch (SimulatedSqlException ex)
        {
            _ = this.FlushInfoMessages(writer);
            WriteErrors(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
        }
        catch (NotSupportedException ex)
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, $"SqlServerSimulator: {ex.Message}", "SIMULATED", "", 1);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning disable CA1031 // Deliberate: see TdsSession.IsRecoverableStatementFault.
        catch (Exception ex) when (IsRecoverableStatementFault(ex, writer))
        {
            _ = this.FlushInfoMessages(writer);
            WriteUnexpectedStatementFault(writer, ex);
            writer.WriteDone(Tds.DoneError, 0);
        }
#pragma warning restore CA1031
    }

    /// <summary>
    /// Resumes the batch a <c>WRITETEXT BULK</c> or <c>UPDATETEXT BULK</c>
    /// suspended, with the message that followed it, as real does (probed
    /// 2026-10-07 against SQL Server 2025 with a raw TDS client): a bulk-load
    /// packet's 4-byte length and bytes are the statement's data — cut short,
    /// Msg 4002 — and any other request is not, which is Msg 4022, the
    /// request itself going unread and the batch's remaining output answering
    /// it. An attention ends the batch where it waits, with Msg 3621 and a
    /// DONE carrying both the attention and the error bit; a
    /// transaction-manager request ends the session, with Msg 4014, the
    /// transaction's rollback, Msg 3621 and Msg 596.
    /// </summary>
    /// <returns>Whether the session goes on.</returns>
    private async ValueTask<bool> ResumeBulkTextAsync(SimulatedBulkTextRequest parked, TdsMessage message, TdsTokenWriter writer, CancellationToken cancellationToken)
    {
        var connection = this.connection!;
        var terminated = SimulatedSqlException.AttentionStatementTerminatedMessage(connection);
        switch (message.PacketType)
        {
            case Tds.PacketAttention:
                connection.AbandonParkedBulkText();
                writer.WriteErrorOrInfo(Tds.TokenInfo, terminated.Number, terminated.State, terminated.Class, terminated.Message, ServerName, terminated.Procedure, terminated.LineNumber);
                writer.WriteDoneToken(Tds.TokenDone, Tds.DoneAttention | Tds.DoneError, 0, StatementDoneKind.Batch);
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                return true;
            case Tds.PacketBulkLoad:
                var payload = message.Payload;
                if (payload.Length >= sizeof(uint)
                    && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload) is var length
                    && length <= (uint)(payload.Length - sizeof(uint)))
                {
                    parked.Data = payload.AsSpan(sizeof(uint), (int)length).ToArray();
                }
                else
                {
                    parked.StreamEndedEarly = true;
                }
                break;
            case Tds.PacketTransactionManager:
                connection.AbandonParkedBulkText();
                var fatal = SimulatedSqlException.NetworkInputFatal();
                fatal.ResolveDiagnostics(1, 0, "");
                WriteErrors(writer, fatal);
                connection.CurrentTransaction?.EndRollback();
                this.WriteTransactionEnvChanges(writer);
                writer.WriteErrorOrInfo(Tds.TokenInfo, terminated.Number, terminated.State, terminated.Class, terminated.Message, ServerName, terminated.Procedure, terminated.LineNumber);
                WriteErrors(writer, SimulatedSqlException.SessionKilled());
                writer.WriteDoneToken(Tds.TokenDone, Tds.DoneError | Tds.DoneServerError, 0, StatementDoneKind.Batch);
                await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
                await connection.CloseAsync().ConfigureAwait(false);
                return false;
            default:
                break;
        }

        this.watchedRequest = connection.BeginRequest();
        await this.ExecuteBatchTextAsync(parked.CommandText, writer, cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(final: true, cancellationToken).ConfigureAwait(false);
        return connection.State != System.Data.ConnectionState.Closed;
    }
}
