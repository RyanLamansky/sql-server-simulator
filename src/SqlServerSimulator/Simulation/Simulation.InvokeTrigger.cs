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
    /// back, rolling the write itself. Which of the matching triggers actually
    /// fire depends on what's already running — see <see cref="CanFireTrigger"/>
    /// for the <c>RECURSIVE_TRIGGERS</c> / <c>nested triggers</c> rules.
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
        // Find matching AFTER triggers across all schemas of the target's own
        // database, which is the session's only until a three-part name names
        // another. Relative order is whatever the schema dict enumerates — not
        // necessarily creation order, and nothing depends on it: SQL Server
        // leaves multi-trigger order unspecified too, without
        // sp_settriggerorder.
        var targetDatabase = outerBatch.DatabaseFor(targetTable);
        var matching = new List<Trigger>();
        foreach (var schema in targetDatabase.Schemas.Values)
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
                if (!CanFireTrigger(outerBatch, trigger))
                    continue;
                matching.Add(trigger);
            }
        }
        if (matching.Count == 0)
            return;

        // sp_settriggerorder pins at most one trigger to each end for this
        // action; everything else keeps the dictionary's own order, which is
        // as unspecified here as it is on real.
        if (matching.Count > 1)
        {
            matching.Sort((x, y) => TriggerOrderRank(x, action).CompareTo(TriggerOrderRank(y, action)));
        }

        var insertedPseudo = MaterializePseudoTable(targetTable.Columns, "inserted", insertedRows ?? [], outerBatch);
        var deletedPseudo = MaterializePseudoTable(targetTable.Columns, "deleted", deletedRows ?? [], outerBatch);
        var mask = BuildColumnsUpdatedMask(targetTable, targetTable.Columns.Length, action, updatedColumnOrdinals);
        RunTriggerBodies(outerBatch, targetDatabase, matching, insertedPseudo, deletedPseudo, affectedRowCount, mask);
    }

    /// <summary>
    /// Sort rank for a firing action: <c>-1</c> for the trigger pinned first,
    /// <c>1</c> for the one pinned last, <c>0</c> otherwise. <c>List.Sort</c>
    /// is unstable, but only the two pinned ends carry a meaningful position —
    /// the middle is unordered on real too.
    /// </summary>
    private static int TriggerOrderRank(Trigger trigger, TriggerActions action) =>
        (trigger.FirstForActions & action) != 0 ? -1
        : (trigger.LastForActions & action) != 0 ? 1
        : 0;

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
        List<int>? updatedColumnOrdinals = null)
    {
        Trigger? matched = null;
        var targetDatabase = outerBatch.DatabaseFor(parent);
        foreach (var schema in targetDatabase.Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.Parent, parent)) continue;
                if (trigger.Timing != TriggerTiming.InsteadOf) continue;
                if ((trigger.Actions & action) == 0) continue;
                if (trigger.IsDisabled) continue;
                if (!CanFireTrigger(outerBatch, trigger)) continue;
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
        RunTriggerBodies(outerBatch, targetDatabase, [matched], insertedPseudo, deletedPseudo, affectedRowCount, mask);
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
        Database targetDatabase,
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
                // Whether this trigger fires at all was settled by the caller's
                // CanFireTrigger filter; nothing between there and here changes
                // the stack, since each body pops its own frame on exit.
                RunOneTriggerBody(
                    outerBatch,
                    targetDatabase,
                    new TriggerFrame(trigger, insertedPseudo, deletedPseudo, columnsUpdatedMask),
                    trigger.BodyText,
                    trigger.BodyLineOffset,
                    trigger.Name,
                    trigger.ExecuteAsClause,
                    trigger.ObjectId,
                    trigger.Timing == TriggerTiming.After,
                    affectedRowCount,
                    trigger.UsesQuotedIdentifier);
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
    /// Runs one trigger body in a child <see cref="BatchContext"/>: nesting-cap
    /// check, frame push, <c>WITH EXECUTE AS</c> impersonation, body dispatch,
    /// the Msg 3616 swallowed-error check, and the unwinding. Shared by the DML
    /// fire loop (<see cref="RunTriggerBodies"/>) and the DDL one
    /// (<c>FireDdlTriggers</c>), which differ only in the frame they hand it and
    /// the atomic scope they publish around the whole set.
    /// </summary>
    /// <param name="outerBatch">The firing statement's batch, which buffers any result sets the body yields.</param>
    /// <param name="bodyDatabase">
    /// The database the body resolves names in — the trigger's own, which is
    /// the firing statement's only until a three-part name mutates another
    /// database. Probe-confirmed against SQL Server 2025: a trigger fired by
    /// <c>INSERT other.dbo.t</c> reads <c>DB_NAME()</c> as <c>other</c> (and
    /// <c>ORIGINAL_DB_NAME()</c> as the session's), so its unqualified writes
    /// land in the table's database, not the caller's.
    /// </param>
    /// <param name="frame">The pseudo-table / event-data frame the body resolves against.</param>
    /// <param name="bodyText">Raw body source, re-tokenized in the child batch.</param>
    /// <param name="bodyLineOffset">Newlines from the CREATE verb to the body, for error-line attribution.</param>
    /// <param name="triggerName">The trigger's unqualified name, reported as <c>ERROR_PROCEDURE</c>.</param>
    /// <param name="executeAsClause">Module <c>WITH EXECUTE AS</c> principal, or null.</param>
    /// <param name="objectId">The trigger's object id, pushed on the in-flight stack.</param>
    /// <param name="countsAsAfterFrame">
    /// Whether the frame this body pushes counts as an AFTER-trigger frame for
    /// the <c>nested triggers</c> rule — true only for AFTER DML triggers.
    /// </param>
    /// <param name="affectedRowCount">The firing statement's row count, which <c>@@ROWCOUNT</c> reads on body entry.</param>
    /// <param name="usesQuotedIdentifier">
    /// The trigger's creation-time <c>QUOTED_IDENTIFIER</c> capture, which the
    /// body parses under instead of the firing session's setting.
    /// </param>
    private void RunOneTriggerBody(
        BatchContext outerBatch,
        Database bodyDatabase,
        TriggerFrame frame,
        string bodyText,
        int bodyLineOffset,
        string triggerName,
        string? executeAsClause,
        int objectId,
        bool countsAsAfterFrame,
        int affectedRowCount,
        bool usesQuotedIdentifier)
    {
        var connection = outerBatch.Connection;
        if (connection.NestingLevel >= SimulatedDbConnection.MaxNestingLevel)
            throw SimulatedSqlException.MaximumNestingLevelExceeded();

        connection.LastStatementRowCount = affectedRowCount;

        var savedImpersonationDepth = connection.Security.ImpersonationDepth;
        var savedBodyErrorRaised = connection.TriggerBodyErrorRaised;
        // The body resolves names in the trigger's own database — the session's
        // unless the firing statement wrote through a three-part name. Not a
        // USE: the switch is invisible to the firing batch, which resumes in
        // its own database when the body returns.
        var savedDatabase = connection.CurrentDatabase;
        // A trigger body parses under the QUOTED_IDENTIFIER captured at its
        // CREATE, not the firing session's (probe-confirmed). Swapping the
        // session flag rather than seeding the child parser directly is what
        // carries the setting to everything downstream of the body — dynamic
        // SQL it EXECs, the plan-cache key, and the Msg 1934 gates — all of
        // which read the connection.
        var savedQuotedIdentifiers = connection.QuotedIdentifiers;
        // SET NOCOUNT inside a trigger body reverts at trigger exit
        // (probe-confirmed): the near-universal `set nocount on` opening a
        // trigger leaves the firing statement's own count intact and doesn't
        // follow the session out.
        var savedNoCount = connection.NoCount;
        // XACT_ABORT / ROWCOUNT / DATEFIRST are body-scoped the same way
        // (probe-confirmed against SQL Server 2025 for a procedure body, whose
        // scoping a trigger body shares).
        var savedOptions = new SimulatedDbConnection.SessionOptionScope(connection);
        BatchContext? innerBatch = null;
        try
        {
            connection.CurrentDatabase = bodyDatabase;
            connection.QuotedIdentifiers = usesQuotedIdentifier;
            connection.NestingLevel++;
            connection.TriggerNestLevel++;
            connection.FiringTriggers.Add((objectId, countsAsAfterFrame));
            connection.TriggerBodyErrorRaised = false;
            // Module WITH EXECUTE AS: run the body as the impersonated
            // principal (OWNER / SELF → dbo, CALLER → no-op, named user →
            // that principal); unwound in the finally below.
            PushModuleExecuteAsFrame(connection, executeAsClause, connection.CurrentDatabase);
            if (!string.IsNullOrEmpty(bodyText))
            {
                using var bodyCommand = new SimulatedDbCommand(this, connection);
#pragma warning disable CA2100 // bodyText is the simulator's own captured body span
                bodyCommand.CommandText = bodyText;
#pragma warning restore CA2100
                innerBatch = new BatchContext(bodyCommand, frame)
                {
                    // Trigger-body errors report a CREATE-relative line and
                    // carry the trigger's UNQUALIFIED name (probe-confirmed:
                    // ERROR_PROCEDURE / SqlError.Procedure = "tr", not
                    // "dbo.tr" — the one asymmetry from stored procedures).
                    LineOffset = bodyLineOffset,
                    ErrorProcedureName = triggerName,
                };
                var parser = innerBatch.Parser;
                parser.MoveNextOptional();
                foreach (var bodyOutcome in DispatchStatementsUntil(innerBatch, endKeyword: null))
                {
                    // A body SELECT is the firing statement's result
                    // set on real, so buffer it for the dispatcher to
                    // yield once the statement completes. Rows-affected
                    // outcomes stay discarded — the body's counts are
                    // not the statement's.
                    if (bodyOutcome is SimulatedQueryResult)
                        (outerBatch.PendingTriggerResultSets ??= []).Add(bodyOutcome);
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
            connection.CurrentDatabase = savedDatabase;
            connection.QuotedIdentifiers = savedQuotedIdentifiers;
            connection.NoCount = savedNoCount;
            savedOptions.Restore(connection);
            connection.NestingLevel--;
            connection.TriggerNestLevel--;
            connection.FiringTriggers.RemoveAt(connection.FiringTriggers.Count - 1);
            // Local temp tables the trigger body created are dropped at
            // trigger exit (probe-confirmed Msg 208 afterward — module-
            // scoped lifetime, same as procs / dynamic SQL).
            innerBatch?.DropScopedTempTables();
            connection.Security.RevertTo(savedImpersonationDepth);
            connection.TriggerBodyErrorRaised = savedBodyErrorRaised;
        }
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

    /// <summary>
    /// Raises Msg 334 when a DML statement's <c>OUTPUT</c> clause returns rows
    /// to the client and <paramref name="parent"/> carries an enabled trigger
    /// for <paramref name="action"/>. A no-op when the statement has no OUTPUT
    /// or sends it <c>INTO</c> a destination.
    /// </summary>
    /// <param name="batch">The executing batch, for the trigger lookup.</param>
    /// <param name="parent">The DML target, or <c>null</c> when unresolved.</param>
    /// <param name="action">The statement's own DML action.</param>
    /// <param name="targetAsWritten">
    /// The target exactly as the statement names it — real echoes the written
    /// reference, and MERGE echoes its alias.
    /// </param>
    /// <param name="outputReturnsToClient">
    /// True when an OUTPUT clause was given without <c>INTO</c>.
    /// </param>
    internal static void RejectClientOutputOnTriggeredTarget(
        BatchContext batch, SchemaObject? parent, TriggerActions action, string targetAsWritten, bool outputReturnsToClient)
    {
        if (!outputReturnsToClient || parent is null)
            return;
        if (HasTrigger(batch, parent, action, TriggerTiming.After) || HasTrigger(batch, parent, action, TriggerTiming.InsteadOf))
            throw SimulatedSqlException.OutputWithoutIntoOnTriggeredTarget(targetAsWritten);
    }

    private static bool HasTrigger(BatchContext batch, SchemaObject parent, TriggerActions action, TriggerTiming timing)
    {
        // Triggers the nesting rules suppress are excluded — for AFTER, so the
        // DML site skips the per-row snapshot capture it would only feed to a
        // trigger that isn't going to run. For INSTEAD OF the exclusion is
        // load-bearing: a body that issues DML against its own target must
        // reach the heap, because the trigger can't run a second time.
        // Probe-confirmed: real SQL Server's INSTEAD OF body's nested INSERT
        // writes the heap directly.
        foreach (var schema in batch.DatabaseFor(parent).Schemas.Values)
        {
            foreach (var trigger in schema.Triggers.Values)
            {
                if (!ReferenceEquals(trigger.Parent, parent)) continue;
                if (trigger.Timing != timing) continue;
                if ((trigger.Actions & action) == 0) continue;
                if (trigger.IsDisabled) continue;
                if (!CanFireTrigger(batch, trigger)) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="trigger"/> fires given what's already running on
    /// the connection — the two rules below, both probed against SQL Server 2025.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Direct recursion.</strong> With the database's
    /// <c>RECURSIVE_TRIGGERS</c> off (the default), a trigger whose body's DML
    /// would re-fire that same trigger is skipped and the DML reaches the heap.
    /// The test is the <em>innermost</em> firing trigger, not the whole stack:
    /// real re-fires a trigger sitting further up (T1's trigger writes T2, whose
    /// trigger writes T1, whose trigger runs again), and a stored procedure
    /// between the body and the DML doesn't launder the recursion either.
    /// Turning <c>RECURSIVE_TRIGGERS</c> on lifts the rule for AFTER triggers
    /// only — an INSTEAD OF trigger never re-fires itself whatever the setting,
    /// since real processes its body's DML against its own target as if the
    /// table had no INSTEAD OF trigger.
    /// </para>
    /// <para>
    /// <strong>Nested triggers.</strong> With the <c>nested triggers</c> server
    /// option off, an AFTER trigger doesn't fire while any AFTER trigger is
    /// running up the stack — only the first AFTER level runs. INSTEAD OF
    /// triggers are exempt and nest normally, and an AFTER trigger underneath
    /// nothing but INSTEAD OF frames still fires.
    /// </para>
    /// </remarks>
    private static bool CanFireTrigger(BatchContext batch, Trigger trigger)
    {
        var stack = batch.Connection.FiringTriggers;
        if (stack.Count == 0)
            return true;

        if (trigger.Timing == TriggerTiming.After)
        {
            if (!batch.Connection.Simulation.NestedTriggersEnabled)
            {
                foreach (var (_, isAfter) in stack)
                {
                    if (isAfter)
                        return false;
                }
                return true;
            }

            if (batch.CurrentDatabase.RecursiveTriggers)
                return true;
        }

        return stack[^1].ObjectId != trigger.ObjectId;
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
