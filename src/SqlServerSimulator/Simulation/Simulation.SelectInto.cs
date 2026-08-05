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

        var isLocalTemp = BatchContext.IsLocalTempName(leaf);
        var isGlobalTemp = !isLocalTemp && BatchContext.IsGlobalTempName(leaf);
        Schema? schema = null;
        var destination = isLocalTemp
            ? batch.Connection.TempTables
            : isGlobalTemp
                ? batch.Connection.Simulation.GlobalTempTables
                : batch.TryResolveSchema(targetName, out schema) ? schema.HeapTables
                    : throw SimulatedSqlException.InvalidObjectName(targetName);
        // A three-part target lands in the named database, so both the object
        // id and the owning-database stamp come from the resolved schema.
        var owningDatabase = schema?.Database;
        // SELECT INTO creates the destination, so a read-only database refuses
        // it whatever the source produces — including no rows at all
        // (probe-confirmed). A #temp destination resolves no schema and stays
        // legal.
        owningDatabase?.RejectWriteWhenReadOnly();
        var destTable = new HeapTable(leaf, destColumns, (owningDatabase ?? batch.CurrentDatabase).AllocateObjectId())
        {
            OwningDatabase = owningDatabase,
            UsesAnsiNulls = batch.Connection.AnsiNulls,
        };
        if (isGlobalTemp)
            destTable.OwnerSession = batch.Connection.Session;
        // SELECT INTO creates a table, so it collides with every name in the
        // shared object namespace — a synonym, view or procedure of that name
        // raises Msg 2714 just as another table would (probe-confirmed).
        if (schema is not null && schema.HasNameInSharedNamespace(leaf))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);
        if (!destination.TryAdd(leaf, destTable))
            throw SimulatedSqlException.ThereIsAlreadyAnObject(leaf);
        // A local temp created via SELECT INTO inside a module body is dropped
        // when that module exits (probe-confirmed, same as CREATE TABLE #t).
        if (isLocalTemp)
            batch.RegisterScopedTempTable(leaf);

        // Temp-table SELECT INTO participates in transactional CREATE undo —
        // probe-confirmed that ROLLBACK undoes both local and global temp-table
        // CREATEs on real SQL Server, matching the asymmetry already documented
        // for regular CREATE TABLE which isn't logged.
        if ((isLocalTemp || isGlobalTemp) && batch.Connection.CurrentTransaction is { } tx)
            tx.UndoLog.RecordTempTableCreation(destination, leaf);

        // Execute the SELECT and stream each row into the destination. Rows
        // arrive as SqlValue[] and are encoded through the destination's own
        // HeapColumn[] (same types by construction, but the encoder needs the
        // schema with nullability and LOB-store routing). Reading the values
        // rather than the bytes is what keeps a projecting SELECT from encoding
        // a page image only for this loop to decode it straight back — the
        // round trip landed on exactly the values the projection had computed.
        // Identity columns track source values via ObserveExplicit so the
        // high-water mark survives the copy.
        var resultSet = selection.Execute(batch);
        var rowCount = 0;
        var undoLog = batch.Connection.CurrentTransaction?.UndoLog;
        foreach (var sourceValues in resultSet.RowValues)
        {
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

        // SELECT … INTO creates a table, so real raises CREATE_TABLE for it
        // (probe-confirmed) — but not for a temp destination.
        if (!isLocalTemp && !isGlobalTemp)
            RecordDdlEvent(batch.Parser, "CREATE_TABLE", schema?.Name ?? Database.DefaultSchemaName, leaf, "TABLE");
        return new SimulatedNonQuery(rowCount);
    }
}
