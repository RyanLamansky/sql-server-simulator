using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Fires every enabled AFTER trigger matching <paramref name="action"/>
    /// on <paramref name="targetTable"/>, populating the inserted /
    /// deleted pseudo-tables from <paramref name="insertedRows"/> /
    /// <paramref name="deletedRows"/>. Called by INSERT / UPDATE /
    /// DELETE / MERGE post-write. A trigger body throwing propagates
    /// up — the calling DML's undo log walks back, rolling the write
    /// itself. Direct same-trigger recursion is suppressed via
    /// <see cref="SimulatedDbConnection.FiringTriggerIds"/> (matches
    /// SQL Server's default <c>RECURSIVE_TRIGGERS OFF</c>).
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
        int affectedRowCount)
    {
        // Find matching triggers across all schemas. Trigger ordering:
        // SQL Server doesn't guarantee order unless sp_settriggerorder is
        // used; the simulator uses schema-dict insertion order, which is
        // stable for a single connection's sequence of CREATE TRIGGER
        // statements.
        var matching = new List<Trigger>();
        foreach (var schema in outerBatch.CurrentDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.ParentTable, targetTable))
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

        var connection = outerBatch.Connection;

        // Build pseudo-tables once per fire. Real SQL Server always
        // exposes BOTH inserted and deleted to trigger bodies — the
        // logically-absent one (deleted for INSERT, inserted for DELETE)
        // is empty rather than missing. A trigger body that joins
        // <c>inserted left join deleted</c> works the same in an INSERT
        // and an UPDATE; the FROM-clause name resolution doesn't
        // discriminate by event type.
        var insertedPseudo = MaterializePseudoTable(targetTable, "inserted", insertedRows ?? [], outerBatch);
        var deletedPseudo = MaterializePseudoTable(targetTable, "deleted", deletedRows ?? [], outerBatch);

        // Save the outer caller's SCOPE_IDENTITY / @@IDENTITY anchor.
        // Real SQL Server scopes SCOPE_IDENTITY per stored-context-scope —
        // a trigger's identity inserts don't leak to the caller's
        // SCOPE_IDENTITY (probe-confirmed). The simulator collapses
        // SCOPE_IDENTITY and @@IDENTITY into one slot, so save/restore
        // around the trigger fires preserves the outer caller's view.
        // EF Core's HasTrigger emit shape relies on this (the post-
        // INSERT SELECT does WHERE [Id] = scope_identity() against the
        // outer caller's identity).
        var outerScopeIdentity = connection.LastIdentity;

        foreach (var trigger in matching)
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

            // @@ROWCOUNT inside the trigger reflects the firing DML's
            // affected-row count (probe-confirmed). Body statements then
            // mutate @@ROWCOUNT normally. The caller already set
            // LastStatementRowCount to the affected count before
            // returning, so we don't need to save/restore here — the
            // body's own statements will overwrite as they execute.
            connection.LastStatementRowCount = affectedRowCount;

            var triggerFrame = new TriggerFrame(trigger, insertedPseudo, deletedPseudo);
            try
            {
                connection.NestingLevel++;
                connection.TriggerNestLevel++;
                if (!string.IsNullOrEmpty(trigger.BodyText))
                {
                    using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // trigger.BodyText is the simulator's own captured body span
                    bodyCommand.CommandText = trigger.BodyText;
#pragma warning restore CA2100
                    var innerBatch = new BatchContext(bodyCommand, triggerFrame);
                    var parser = innerBatch.Parser;
                    parser.MoveNextOptional();
                    // Drain outcomes — result sets from a trigger body
                    // propagate to the outer caller via the dispatch
                    // loop's enumerator, but the immediate caller here
                    // (the DML site) doesn't yield them. For v1 the
                    // body's result sets are simply enumerated and
                    // discarded; revisiting if EF apps rely on
                    // trigger-emitted result sets.
                    foreach (var _ in DispatchStatementsUntil(innerBatch, endKeyword: null))
                    {
                        // discard
                    }
                }
            }
            finally
            {
                connection.NestingLevel--;
                connection.TriggerNestLevel--;
                _ = connection.FiringTriggerIds.Remove(trigger.ObjectId);
            }
        }

        // Restore the caller's SCOPE_IDENTITY view after all triggers
        // have finished. Inside the triggers, IDENT_CURRENT / @@IDENTITY
        // saw the trigger's effects; the caller continues to see the
        // outer DML's last identity.
        connection.LastIdentity = outerScopeIdentity;
    }

    /// <summary>
    /// Fast-path predicate: returns true when at least one enabled
    /// AFTER trigger of the requested action exists for the given
    /// table. DML sites consult this before bothering to capture full
    /// per-row snapshots for the inserted / deleted pseudo-tables.
    /// </summary>
    internal static bool HasAfterTrigger(BatchContext batch, HeapTable table, TriggerActions action)
    {
        foreach (var schema in batch.CurrentDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.ParentTable, table))
                    continue;
                if (trigger.Timing != TriggerTiming.After)
                    continue;
                if ((trigger.Actions & action) == 0)
                    continue;
                if (trigger.IsDisabled)
                    continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Builds the inserted / deleted pseudo-table for a single trigger
    /// fire. The HeapTable shares the parent table's column array (the
    /// schema is identical — Msg 286 wouldn't fire here) and gets a
    /// fresh empty heap that this method populates by re-encoding each
    /// supplied row via the parent's StoredColumns / StorageOrdinals
    /// projection. <see cref="HeapTable.IsTableVariable"/> is set so
    /// inserts bypass identity/default re-running and aren't tracked in
    /// the regular transaction undo log.
    /// </summary>
    private static HeapTable MaterializePseudoTable(HeapTable parent, string name, List<SqlValue[]> rows, BatchContext outerBatch)
    {
        var pseudo = new HeapTable(
            name,
            parent.Columns,
            objectId: outerBatch.CurrentDatabase.AllocateObjectId(),
            schemaId: Database.DboSchemaId,
            createDate: outerBatch.CurrentStatement.UtcNow,
            isTableVariable: true);
        foreach (var row in rows)
        {
            var stored = ProjectStoredValues(parent, row);
            pseudo.Heap.Insert(RowEncoder.EncodeRow(parent.StoredColumns, stored, pseudo.Heap), undoLog: null);
        }
        return pseudo;
    }
}
