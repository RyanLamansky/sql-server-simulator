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
    /// <c>INSERT BULK</c> is a protocol error answered with a DONE.
    /// </summary>
    private void ExecuteBulkLoad(TdsMessage message, TdsTokenWriter writer)
    {
        var plan = this.pendingBulk;
        this.pendingBulk = null;
        if (plan is null)
        {
            writer.WriteErrorOrInfo(Tds.TokenError, 50000, 1, 16, "SqlServerSimulator: a bulk-load data packet arrived without a preceding INSERT BULK statement.", "SIMULATED", "", 1);
            writer.WriteDone(Tds.DoneError, 0);
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
}
