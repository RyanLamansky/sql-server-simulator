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
    /// <see cref="SimulatedDbConnection.TempTables"/> or the named schema's
    /// (or default <c>dbo</c>'s) heap-table dict via
    /// <see cref="Database.Schemas"/>. Same routing rule as CREATE TABLE.</item>
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
        var targetName = selection.IntoTarget!.Value;
        var destColumns = selection.DestColumnSchema!;
        var leaf = targetName.Leaf;

        // In a skipped IF branch the destination shouldn't be created at all
        // — the existence check (Msg 2714) and the SELECT execution both
        // need to be skipped so a `IF NOT EXISTS (…) SELECT … INTO foo` over
        // an already-existing `foo` doesn't false-positive.
        if (batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var destTable = new HeapTable(leaf, destColumns, batch.CurrentDatabase.AllocateObjectId());
        var isLocalTemp = BatchContext.IsLocalTempName(leaf);
        var isGlobalTemp = !isLocalTemp && BatchContext.IsGlobalTempName(leaf);
        var destination = isLocalTemp
            ? batch.Connection.TempTables
            : isGlobalTemp
                ? batch.Connection.Simulation.GlobalTempTables
                : batch.TryResolveSchema(targetName, out var schema) ? schema.HeapTables
                    : throw SimulatedSqlException.InvalidObjectName(targetName);
        if (isGlobalTemp)
            destTable.OwnerConnection = batch.Connection;
        if (!destination.TryAdd(leaf, destTable))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);

        // Temp-table SELECT INTO participates in transactional CREATE undo —
        // probe-confirmed that ROLLBACK undoes both local and global temp-table
        // CREATEs on real SQL Server, matching the asymmetry already documented
        // for regular CREATE TABLE which isn't logged.
        if ((isLocalTemp || isGlobalTemp) && batch.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableCreation(destination, leaf);

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
            var (newPage, newSlot) = destTable.Heap.Insert(encoded, undoLog);
            if (IsLockableTable(destTable))
                batch.AcquireRowLockTxScoped(destTable, newPage, newSlot, LockMode.Exclusive);
            rowCount++;
        }

        return new SimulatedNonQuery(rowCount);
    }
}
