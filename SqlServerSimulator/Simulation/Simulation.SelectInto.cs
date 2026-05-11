using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Executes a <c>SELECT … INTO target [FROM …]</c> statement: creates
    /// the destination heap table from the parse-time-derived schema (see
    /// <see cref="Selection.DestColumnSchema"/>), then runs the projection
    /// and inserts each row into the new table. Returns a
    /// <see cref="SimulatedNonQuery"/> whose record count is the row count
    /// written (real SQL Server's SELECT INTO doesn't yield a result set
    /// envelope; ExecuteNonQuery returns the row count).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Probe-confirmed against SQL Server 2025 (2026-05-11):
    /// </para>
    /// <list type="bullet">
    /// <item>Target name routes by <c>#</c>-prefix to the connection's
    /// <see cref="SimulatedDbConnection.TempTables"/> or the current
    /// database's <see cref="Database.HeapTables"/>. Same routing rule as
    /// CREATE TABLE.</item>
    /// <item>Target-already-exists raises Msg 2714 (same error as
    /// <c>CREATE TABLE</c> with a duplicate name).</item>
    /// <item>Identity column on the destination tracks the source's values
    /// through <see cref="IdentityState.ObserveExplicit"/> as each row is
    /// copied — so a follow-up plain insert generates the next sequential
    /// value past the highest one copied.</item>
    /// <item>Temp-table targets participate in transactional CREATE undo:
    /// a SELECT INTO #foo inside <c>BEGIN TRAN</c> + <c>ROLLBACK</c>
    /// removes the dest table entirely (same machinery as <c>CREATE TABLE
    /// #foo</c>).</item>
    /// </list>
    /// </remarks>
    private static SimulatedNonQuery ExecuteSelectInto(Selection selection, BatchContext batch)
    {
        var targetName = selection.IntoTarget!;
        var destColumns = selection.DestColumnSchema!;

        // Global temp tables aren't modeled — surface explicitly rather than
        // letting it land as a regular table.
        if (targetName.Length >= 2 && targetName[0] == '#' && targetName[1] == '#')
            throw new NotSupportedException($"Global temp tables (##{targetName[2..]}) aren't modeled. Use a local temp table (#{targetName[2..]}) or a permanent table.");

        var destTable = new HeapTable(targetName, destColumns);
        var isTempTable = BatchContext.IsLocalTempName(targetName);
        var destination = isTempTable
            ? batch.Connection.TempTables
            : batch.CurrentDatabase.HeapTables;
        if (!destination.TryAdd(targetName, destTable))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(targetName);

        // Temp-table SELECT INTO participates in transactional CREATE undo
        // (same rule as CREATE TABLE #foo). Regular SELECT INTO doesn't —
        // matches the asymmetry already documented for CREATE TABLE.
        if (isTempTable && batch.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableCreation(batch.Connection.TempTables, targetName);

        // Execute the SELECT and stream each row into the destination. The
        // row bytes are encoded per Selection.Schema; decode to SqlValue[]
        // then re-encode through the destination's HeapColumn[] (same types
        // by construction, but the encoder needs the schema with nullability
        // and LOB-store routing). Identity columns track source values via
        // ObserveExplicit so the high-water mark survives the copy.
        var resultSet = selection.Execute(batch);
        var rowCount = 0;
        var undoLog = batch.Connection.CurrentTransaction?.UndoLog;
        foreach (var rowBytes in resultSet.RowBytes)
        {
            var sourceValues = RowDecoder.DecodeRow(selection.Schema, rowBytes);
            for (var i = 0; i < destColumns.Length; i++)
            {
                if (destColumns[i].Identity is { } identity && !sourceValues[i].IsNull)
                    identity.ObserveExplicit(sourceValues[i].CoerceTo(SqlType.BigInt).AsInt64);
            }
            var encoded = RowEncoder.EncodeRow(destTable.StoredColumns, sourceValues, destTable.Heap);
            // Use the active undo log so a containing tx's ROLLBACK unwinds
            // the row writes alongside the table creation entry.
            destTable.Heap.Insert(encoded, undoLog);
            rowCount++;
        }

        return new SimulatedNonQuery(rowCount);
    }
}
