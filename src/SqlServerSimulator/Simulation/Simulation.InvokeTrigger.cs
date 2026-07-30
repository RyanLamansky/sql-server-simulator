using SqlServerSimulator.Parser;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Fires every enabled AFTER trigger matching <paramref name="action"/>
    /// on <paramref name="targetTable"/> (AFTER is heap-table-only),
    /// populating the inserted / deleted pseudo-tables from
    /// <paramref name="insertedRows"/> / <paramref name="deletedRows"/>.
    /// Called by INSERT / UPDATE / DELETE / MERGE post-write. A trigger
    /// body throwing propagates up — the calling DML's undo log walks
    /// back, rolling the write itself. Direct same-trigger recursion is
    /// suppressed via <see cref="SimulatedDbConnection.FiringTriggerIds"/>
    /// (matches SQL Server's default <c>RECURSIVE_TRIGGERS OFF</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The DML's affected-row count is set into
    /// <see cref="SimulatedDbConnection.LastStatementRowCount"/> before
    /// each trigger fires so <c>@@ROWCOUNT</c> inside the body reflects
    /// the firing statement (probe-confirmed). The body's own statements
    /// then update <c>@@ROWCOUNT</c> normally; on body exit the outer
    /// statement's recorded row count is restored by the calling DML
    /// site (the DML's row count was the value before trigger fire).
    /// </para>
    /// <para>
    /// Pseudo-tables are materialized as fresh <see cref="HeapTable"/>
    /// instances carrying the parent table's column array (with no
    /// constraints / no Identity counter advances) and the affected
    /// rows pre-encoded into the heap. Re-using the parent's HeapColumn
    /// instances is safe because the pseudo-tables are read-only inside
    /// the trigger body (DML against <c>inserted</c> / <c>deleted</c>
    /// would surface Msg 286 in real SQL Server — the simulator's HeapTable
    /// doesn't have a "read-only" toggle yet, so trigger bodies that
    /// attempt this would silently succeed; left as a known gap).
    /// </para>
    /// </remarks>
    internal void FireTriggers(
        BatchContext outerBatch,
        HeapTable targetTable,
        TriggerActions action,
        List<SqlValue[]>? insertedRows,
        List<SqlValue[]>? deletedRows,
        int affectedRowCount,
        IReadOnlyList<int>? updatedColumnOrdinals = null)
    {
        // Find matching AFTER triggers across all schemas. Ordering uses
        // schema-dict insertion order — stable for a single connection's
        // CREATE TRIGGER sequence (SQL Server doesn't guarantee order
        // unless sp_settriggerorder is used).
        var matching = new List<Trigger>();
        foreach (var schema in outerBatch.CurrentDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.Parent, targetTable))
                    continue;
                if (trigger.Timing != TriggerTiming.After)
                    continue;
                if ((trigger.Actions & action) == 0)
                    continue;
                if (trigger.IsDisabled)
                    continue;
                matching.Add(trigger);
            }
        }
        if (matching.Count == 0)
            return;

        var insertedPseudo = MaterializePseudoTable(targetTable.Columns, "inserted", insertedRows ?? [], outerBatch);
        var deletedPseudo = MaterializePseudoTable(targetTable.Columns, "deleted", deletedRows ?? [], outerBatch);
        var mask = BuildColumnsUpdatedMask(targetTable, targetTable.Columns.Length, action, updatedColumnOrdinals);
        RunTriggerBodies(outerBatch, matching, insertedPseudo, deletedPseudo, affectedRowCount, mask);
    }

    /// <summary>
    /// Builds the <c>COLUMNS_UPDATED()</c> bitmask for one trigger fire.
    /// DELETE yields an empty array (probe-confirmed: <c>DATALENGTH</c> 0,
    /// not a run of zero bytes); INSERT sets every bit through the table's
    /// column-id watermark regardless of which columns the statement named,
    /// including the bits of columns since dropped; UPDATE sets exactly the
    /// columns <paramref name="updatedColumnOrdinals"/> names.
    /// </summary>
    /// <param name="table">
    /// The trigger's parent table, or <c>null</c> when the parent is a view —
    /// a view's columns carry no stable ids and can't be dropped individually,
    /// so its ordinals stand in for column ids.
    /// </param>
    /// <param name="columnCount">Pseudo-table column count, the view-parent watermark.</param>
    /// <param name="action">The DML action firing the trigger.</param>
    /// <param name="updatedColumnOrdinals">
    /// Full-column ordinals (positions in <see cref="HeapTable.Columns"/>)
    /// assigned by the statement's SET clause. Only read for UPDATE.
    /// </param>
    private static byte[] BuildColumnsUpdatedMask(HeapTable? table, int columnCount, TriggerActions action, IReadOnlyList<int>? updatedColumnOrdinals)
    {
        if (action == TriggerActions.Delete)
            return [];

        var watermark = table?.MaxColumnIdUsed ?? columnCount;
        var mask = new byte[(watermark + 7) / 8];
        void Set(int columnId)
        {
            if (columnId >= 1 && (columnId - 1) / 8 < mask.Length)
                mask[(columnId - 1) / 8] |= (byte)(1 << ((columnId - 1) % 8));
        }

        if (action == TriggerActions.Insert)
        {
            for (var id = 1; id <= watermark; id++)
                Set(id);
            return mask;
        }

        if (updatedColumnOrdinals is not null)
        {
            foreach (var ordinal in updatedColumnOrdinals)
            {
                if ((uint)ordinal >= (uint)(table?.Columns.Length ?? columnCount))
                    continue;
                Set(table is null ? ordinal + 1 : table.Columns[ordinal].ColumnId);
            }
        }
        return mask;
    }

    /// <summary>
    /// Fires the single matching INSTEAD OF trigger for <paramref name="action"/>
    /// on <paramref name="parent"/> (a <see cref="HeapTable"/> or a
    /// <see cref="View"/>). Returns <c>true</c> when a trigger fired,
    /// <c>false</c> when none was attached (so the caller proceeds with
    /// the normal heap-write path). Max-one enforcement at CREATE TRIGGER
    /// time (Msg 2111) means we either find zero or exactly one match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="pseudoColumns"/> is the column shape of INSERTED /
    /// DELETED — the parent table's columns for a table target, or the
    /// view's <see cref="View.OutputColumns"/> for a view target. Rows
    /// supplied in <paramref name="insertedRows"/> /
    /// <paramref name="deletedRows"/> must match this shape.
    /// </para>
    /// <para>
    /// Direct-recursion suppression still applies — an INSTEAD OF body
    /// that issues DML against its own target won't re-fire itself (the
    /// nested DML reaches the heap directly).
    /// </para>
    /// </remarks>
    internal bool TryFireInsteadOfTrigger(
        BatchContext outerBatch,
        SchemaObject parent,
        TriggerActions action,
        HeapColumn[] pseudoColumns,
        List<SqlValue[]>? insertedRows,
        List<SqlValue[]>? deletedRows,
        int affectedRowCount,
        IReadOnlyList<int>? updatedColumnOrdinals = null)
    {
        Trigger? matched = null;
        foreach (var schema in outerBatch.CurrentDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.Parent, parent)) continue;
                if (trigger.Timing != TriggerTiming.InsteadOf) continue;
                if ((trigger.Actions & action) == 0) continue;
                if (trigger.IsDisabled) continue;
                matched = trigger;
                break;
            }
            if (matched is not null) break;
        }
        if (matched is null)
            return false;

        var insertedPseudo = MaterializePseudoTable(pseudoColumns, "inserted", insertedRows ?? [], outerBatch);
        var deletedPseudo = MaterializePseudoTable(pseudoColumns, "deleted", deletedRows ?? [], outerBatch);
        var mask = BuildColumnsUpdatedMask(parent as HeapTable, pseudoColumns.Length, action, updatedColumnOrdinals);
        RunTriggerBodies(outerBatch, [matched], insertedPseudo, deletedPseudo, affectedRowCount, mask);
        return true;
    }

    /// <summary>
    /// Shared per-trigger dispatch loop: runs each trigger's body inside
    /// a child <see cref="BatchContext"/>, enforces nesting / recursion
    /// limits, and restores the outer caller's SCOPE_IDENTITY anchor on
    /// exit. Used by both AFTER and INSTEAD OF dispatch paths.
    /// </summary>
    private void RunTriggerBodies(
        BatchContext outerBatch,
        List<Trigger> triggers,
        HeapTable insertedPseudo,
        HeapTable deletedPseudo,
        int affectedRowCount,
        byte[] columnsUpdatedMask)
    {
        var connection = outerBatch.Connection;

        // Save the outer caller's SCOPE_IDENTITY / @@IDENTITY anchor.
        // Real SQL Server scopes SCOPE_IDENTITY per stored-context-scope —
        // a trigger's identity inserts don't leak to the caller's
        // SCOPE_IDENTITY (probe-confirmed). The simulator collapses
        // SCOPE_IDENTITY and @@IDENTITY into one slot, so save/restore
        // around the trigger fires preserves the outer caller's view.
        var outerScopeIdentity = connection.LastIdentity;

        // Publish the firing statement's atomic scope for the duration of the
        // bodies, so every mutation underneath — the body's own statements and
        // any module it calls — joins it rather than committing independently.
        // A nested fire re-publishes the same log it already joined, so the
        // save/restore nests harmlessly.
        var outerTriggerLog = connection.TriggerStatementUndoLog;
        var outerTriggerVersionEntries = connection.TriggerStatementVersionEntries;
        connection.TriggerStatementUndoLog = outerBatch.CurrentUndoLog;
        connection.TriggerStatementVersionEntries = outerBatch.CurrentStatementVersionEntries;

        try
        {
            foreach (var trigger in triggers)
            {
                // Direct-recursion guard. Trigger T currently firing → skip
                // re-fires of T (the body's DML may still fire other triggers
                // — only same-trigger recursion is blocked).
                if (!connection.FiringTriggerIds.Add(trigger.ObjectId))
                    continue;

                if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
                {
                    _ = connection.FiringTriggerIds.Remove(trigger.ObjectId);
                    throw SimulatedSqlException.MaximumNestingLevelExceeded();
                }

                connection.LastStatementRowCount = affectedRowCount;

                var triggerFrame = new TriggerFrame(trigger, insertedPseudo, deletedPseudo, columnsUpdatedMask);
                var savedImpersonationDepth = connection.Security.ImpersonationDepth;
                var savedBodyErrorRaised = connection.TriggerBodyErrorRaised;
                BatchContext? innerBatch = null;
                try
                {
                    connection.NestingLevel++;
                    connection.TriggerNestLevel++;
                    connection.TriggerBodyErrorRaised = false;
                    // Module WITH EXECUTE AS: run the body as the impersonated
                    // principal (OWNER / SELF → dbo, CALLER → no-op, named user →
                    // that principal); unwound in the finally below.
                    PushModuleExecuteAsFrame(connection, trigger.ExecuteAsClause, connection.CurrentDatabase);
                    if (!string.IsNullOrEmpty(trigger.BodyText))
                    {
                        using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // trigger.BodyText is the simulator's own captured body span
                        bodyCommand.CommandText = trigger.BodyText;
#pragma warning restore CA2100
                        innerBatch = new BatchContext(bodyCommand, triggerFrame)
                        {
                            // Trigger-body errors report a CREATE-relative line and
                            // carry the trigger's UNQUALIFIED name (probe-confirmed:
                            // ERROR_PROCEDURE / SqlError.Procedure = "tr", not
                            // "dbo.tr" — the one asymmetry from stored procedures).
                            LineOffset = trigger.BodyLineOffset,
                            ErrorProcedureName = trigger.Name,
                        };
                        var parser = innerBatch.Parser;
                        parser.MoveNextOptional();
                        foreach (var _ in DispatchStatementsUntil(innerBatch, endKeyword: null))
                        {
                            // discard
                        }
                        // Real aborts the batch when any error of severity >= 11
                        // was raised while the body ran, even one the body's own
                        // TRY / CATCH swallowed — the swallow doesn't save it.
                        if (connection.TriggerBodyErrorRaised)
                            throw SimulatedSqlException.ErrorRaisedDuringTriggerExecution();
                    }
                }
                finally
                {
                    connection.NestingLevel--;
                    connection.TriggerNestLevel--;
                    // Local temp tables the trigger body created are dropped at
                    // trigger exit (probe-confirmed Msg 208 afterward — module-
                    // scoped lifetime, same as procs / dynamic SQL).
                    innerBatch?.DropScopedTempTables();
                    connection.Security.RevertTo(savedImpersonationDepth);
                    connection.TriggerBodyErrorRaised = savedBodyErrorRaised;
                    _ = connection.FiringTriggerIds.Remove(trigger.ObjectId);
                }
            }
        }
        finally
        {
            connection.TriggerStatementUndoLog = outerTriggerLog;
            connection.TriggerStatementVersionEntries = outerTriggerVersionEntries;
        }

        connection.LastIdentity = outerScopeIdentity;
    }

    /// <summary>
    /// Fast-path predicate: returns true when at least one enabled
    /// AFTER trigger of the requested action exists for the given
    /// table. DML sites consult this before bothering to capture full
    /// per-row snapshots for the inserted / deleted pseudo-tables.
    /// </summary>
    internal static bool HasAfterTrigger(BatchContext batch, HeapTable table, TriggerActions action) =>
        HasTrigger(batch, table, action, TriggerTiming.After);

    /// <summary>
    /// Fast-path predicate: returns true when an enabled INSTEAD OF
    /// trigger of the requested action exists for <paramref name="parent"/>
    /// (a <see cref="HeapTable"/> or a <see cref="View"/>). The caller
    /// uses this to short-circuit the heap-write path and route to
    /// <see cref="TryFireInsteadOfTrigger"/>.
    /// </summary>
    internal static bool HasInsteadOfTrigger(BatchContext batch, SchemaObject parent, TriggerActions action) =>
        HasTrigger(batch, parent, action, TriggerTiming.InsteadOf);

    private static bool HasTrigger(BatchContext batch, SchemaObject parent, TriggerActions action, TriggerTiming timing)
    {
        // In-flight triggers are excluded — for AFTER, this matters only
        // when a body re-enters itself (the recursion guard would skip
        // anyway, so the result-set effect is the same). For INSTEAD OF
        // the distinction is load-bearing: a body that issues DML against
        // its own target must reach the heap, because the trigger
        // (suppressed by the recursion guard) can't run a second time.
        // Probe-confirmed: real SQL Server's INSTEAD OF body's nested
        // INSERT writes the heap directly.
        var firingIds = batch.Connection.FiringTriggerIds;
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.Parent, parent)) continue;
                if (trigger.Timing != timing) continue;
                if ((trigger.Actions & action) == 0) continue;
                if (trigger.IsDisabled) continue;
                if (firingIds.Contains(trigger.ObjectId)) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the inserted / deleted pseudo-table for a single trigger
    /// fire. The pseudo HeapTable carries the supplied column array
    /// (parent table columns for a table target, view OutputColumns for
    /// a view target) and gets a fresh empty heap that this method
    /// populates by encoding each supplied row.
    /// <see cref="HeapTable.IsTableVariable"/> is set so inserts bypass
    /// identity/default re-running and aren't tracked in the regular
    /// transaction undo log.
    /// </summary>
    private static HeapTable MaterializePseudoTable(HeapColumn[] columns, string name, List<SqlValue[]> rows, BatchContext outerBatch)
    {
        var pseudo = new HeapTable(
            name,
            columns,
            objectId: outerBatch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: outerBatch.CurrentStatement.UtcNow,
            isTableVariable: true);
        foreach (var row in rows)
        {
            var stored = ProjectStoredValuesForColumns(columns, pseudo.StorageOrdinals, row);
            _ = pseudo.Heap.Insert(RowEncoder.EncodeRow(pseudo.StoredColumns, stored, pseudo.Heap), undoLog: null);
        }
        return pseudo;
    }

    /// <summary>
    /// Projects a per-column-array row to the stored-column subset.
    /// Mirrors <c>ProjectStoredValues</c> but takes the column array and
    /// storage ordinals directly so it works for view-shaped pseudo-tables
    /// (whose OutputColumns are all <see cref="HeapColumn.IsStored"/> = true,
    /// making this a straight copy in practice but defensive against future
    /// non-stored projection columns).
    /// </summary>
    private static SqlValue[] ProjectStoredValuesForColumns(HeapColumn[] columns, int[] storageOrdinals, SqlValue[] fullRow)
    {
        var stored = new SqlValue[columns.Length == storageOrdinals.Length
            ? CountStored(storageOrdinals)
            : storageOrdinals.Length];
        for (var i = 0; i < storageOrdinals.Length; i++)
        {
            var s = storageOrdinals[i];
            if (s >= 0)
                stored[s] = fullRow[i];
        }
        return stored;
    }

    private static int CountStored(int[] storageOrdinals)
    {
        var n = 0;
        for (var i = 0; i < storageOrdinals.Length; i++)
        {
            if (storageOrdinals[i] >= 0) n++;
        }
        return n;
    }
}
