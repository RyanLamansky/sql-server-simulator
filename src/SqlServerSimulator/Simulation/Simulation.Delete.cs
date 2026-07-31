using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes <c>DELETE [FROM] &lt;table&gt; [WHERE pred]</c>
    /// (single-table form), <c>DELETE [FROM] &lt;alias&gt; FROM &lt;table&gt; AS &lt;alias&gt; [WHERE]</c>
    /// (single-source EF7+ <c>ExecuteDelete</c> form), and the joined-source
    /// form (<c>DELETE [FROM] &lt;alias&gt; FROM t AS &lt;alias&gt; JOIN u AS b ON ... [WHERE]</c>)
    /// that EF Core emits for <c>ExecuteDelete</c> over collection navigations.
    /// Rows matching the predicate are tombstoned at the page level; their
    /// payload bytes and any LOB chains are not reclaimed (CLAUDE.md flags
    /// this as a leak quirk pending the LOB-lifecycle bundle).
    /// </summary>
    /// <remarks>
    /// In the joined-source form, the same target row may surface in
    /// multiple join tuples; SQL Server deletes each unique target exactly
    /// once (probe-confirmed). The simulator dedupes by (page, slot)
    /// during enumeration to match.
    /// </remarks>
    private static SimulatedStatementOutcome ParseDelete(ParserContext context)
    {
        context.MoveNextRequired();
        var top = Selection.ParseDmlTopClause(context);
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();

        var leadingIdent = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        context.Batch.RejectCrossDatabaseMutation(leadingIdent);

        View? leadingView = null;
        HeapTable? leadingTable;
        if (context.Batch.TryResolveView(leadingIdent, out var resolvedView))
        {
            if (HasInsteadOfTrigger(context.Batch, resolvedView, TriggerActions.Delete)
                && resolvedView.BaseTable is null)
            {
                throw new NotSupportedException($"INSTEAD OF DELETE through a non-updatable view ('{resolvedView.Schema.Name}.{resolvedView.Name}') isn't modeled. Updatable single-base views work; join / aggregate / DISTINCT views are deferred.");
            }
            if (resolvedView.BaseTable is not { } baseTable)
            {
                throw resolvedView.RejectionReason == ViewUpdatabilityRejection.MultipleSources
                    ? SimulatedSqlException.ViewUpdateAffectsMultipleTables($"{resolvedView.Schema.Name}.{resolvedView.Name}")
                    : SimulatedSqlException.CannotUpdateNonUpdatableView($"{resolvedView.Schema.Name}.{resolvedView.Name}");
            }
            leadingView = resolvedView;
            leadingTable = baseTable;
        }
        else
        {
            _ = context.Batch.TryResolveTable(leadingIdent, out leadingTable);
        }
        context.MoveNextOptional();
        Selection.ValidateDmlTargetHints(Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false));
        // Phase 1a: acquire X on the resolved DELETE target. Tx-scoped when
        // BEGIN TRAN is active. Skipped when leadingTable is null (multi-
        // source alias form — target determined post-FROM, deferred to 1b).
        if (leadingTable is not null)
        {
            RejectDisabledClusteredIndex(leadingTable);
            _ = context.Batch.AcquireDataLockIfApplicable(leadingTable, default, isWrite: true);
        }

        // OUTPUT requires a known target. INSERTED isn't a valid qualifier
        // in DELETE OUTPUT (probe-confirmed Msg 4104). Alias-form multi-
        // source DELETE with OUTPUT isn't modeled — see the matching
        // limitation in ParseUpdate. DELETE OUTPUT through a view is also
        // rejected (the DELETED.* would need view-output-column rebinding).
        OutputProjection? output = null;
        if (leadingView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"DELETE … OUTPUT through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");
        if (leadingTable is not null)
        {
            output = TryParseOutputClauseForMutation(context, leadingTable, allowInserted: false, allowDeleted: true);
            RejectClientOutputOnTriggeredTarget(context.Batch, leadingTable, TriggerActions.Delete, leadingIdent.ToString(), output is { HasTarget: false });
        }
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
        {
            throw new NotSupportedException("OUTPUT with alias-form multi-source DELETE isn't modeled — re-emit with the table name as the target if OUTPUT is required.");
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            return leadingView is not null
                ? throw new NotSupportedException($"Multi-source DELETE through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled — target the underlying table directly.")
                : ExecuteJoinedDelete(context, leadingIdent, leadingTable, output, top);
        }

        var table = leadingTable ?? throw (BatchContext.IsTableVariableName(leadingIdent.Leaf)
            ? SimulatedSqlException.MustDeclareTableVariable(leadingIdent.Leaf)
            : context.Batch.UnresolvableObjectName(leadingIdent));
        return table.IsTableValuedParameter
            ? throw SimulatedSqlException.TableValuedParameterIsReadOnly(leadingIdent.Leaf)
            : ExecuteDeleteAgainstTable(context, table, output, top, leadingView);
    }

    /// <summary>
    /// Single-table no-FROM execution path: iterates the target heap directly,
    /// tombstones matching rows, and projects OUTPUT.DELETED if requested.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteDeleteAgainstTable(
        ParserContext context,
        HeapTable table,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        View? sourceView = null)
    {
        BooleanExpression? where = null;
        Cursor? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
                positionedCursor = ParseWhereCurrentOf(context, table);
            else
                where = BooleanExpression.Parse(context);
        }

        if (!context.Batch.IsSkipping)
        {
            // DELETE reads the target when it has a WHERE clause — real then
            // also requires SELECT, checked first so the SELECT denial surfaces
            // when both SELECT and DELETE are missing (probe M1d). A bare DELETE
            // with no WHERE reads nothing and needs only DELETE (M1e). DELETE
            // itself is not column-grantable, so it stays object-grain; only the
            // read-implies-SELECT is column-grain on a base table.
            if (sourceView is not null)
            {
                if (where is not null)
                    PermissionEnforcement.CheckView(context.Batch, "SELECT", sourceView);
                PermissionEnforcement.CheckView(context.Batch, "DELETE", sourceView);
            }
            else
            {
                if (where is not null && PermissionEnforcement.Applies(context.Batch))
                {
                    var readColumns = new HashSet<int>();
                    where.VisitOperandExpressions(op => op.VisitColumnReferences(n => PermissionEnforcement.AddColumnOrdinal(table, n, readColumns)));
                    PermissionEnforcement.CheckTableColumns(context.Batch, Permission.Select, table, readColumns);
                }
                PermissionEnforcement.CheckTable(context.Batch, "DELETE", table);
            }
        }

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();
        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var hasDeleteTriggers = HasAfterTrigger(context.Batch, table, TriggerActions.Delete);
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);
        var needsFullForTriggers = hasDeleteTriggers || insteadOfActive;
        var needsFullForHistory = table.SystemVersioning is not null;
        var needsFullForFk = table.IncomingForeignKeys.Count > 0;

        // Seek the target when WHERE carries an indexable equality / range
        // (positioned DELETE leaves where null, so it keeps the full scan — the
        // cursor already fixed one row). The loop re-runs WHERE below, so the
        // seek only narrows the rows considered.
        var rowSource = where is not null
            ? Selection.SeekMutationTarget(table, where, context.Batch) ?? table.Heap.EnumerateRowsWithAddress()
            : table.Heap.EnumerateRowsWithAddress();
        foreach (var (pageIndex, slotIndex, rowBytes) in rowSource)
        {
            // Positioned DELETE (WHERE CURRENT OF): only the cursor's row.
            if (positionedCursor is not null && !CursorRowMatches(positionedCursor, (pageIndex, slotIndex)))
                continue;

            SqlValue[]? fullValues = null;
            if (where is not null || output is not null || sourceView is not null || needsFullForTriggers || needsFullForHistory || needsFullForFk)
            {
                fullValues = DecodeFullRow(table, rowBytes);
                EvaluateComputedColumns(table, fullValues, context.Batch);
            }

            // View visibility filter: rows not visible in the view aren't
            // candidates for DELETE through it. AND-of-WHEREs up the chain.
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues!, context.Batch))
                continue;

            if (where is not null)
            {
                var localValues = fullValues!;
                SqlValue Resolve(MultiPartName name)
                {
                    if (sourceView is not null)
                    {
                        for (var v = 0; v < sourceView.OutputColumns.Length; v++)
                        {
                            if (context.Batch.CurrentDatabase.Collation.Equals(sourceView.OutputColumns[v].Name, name.Leaf))
                            {
                                var baseOrd = sourceView.BaseColumnOrdinals[v];
                                return baseOrd < 0
                                    ? throw SimulatedSqlException.InvalidColumnName(name)
                                    : localValues[baseOrd];
                            }
                        }
                        throw SimulatedSqlException.InvalidColumnName(name);
                    }
                    for (var k = 0; k < table.Columns.Length; k++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, name.Leaf))
                            return localValues[k];
                    }
                    throw SimulatedSqlException.InvalidColumnName(name);
                }

                if (where.Run(new RuntimeContext(Resolve, context.Batch)) != true)
                    continue;
            }

            deleted.Add((pageIndex, slotIndex, (output is null && !needsFullForTriggers && !needsFullForHistory && !needsFullForFk) ? null : fullValues));
        }

        ApplyDmlTopCap(top, deleted, context.Batch);

        // SI writer pre-flight: scan the version chain for snapshot-visible
        // tombstoned rows. A pre-delete payload matching WHERE means
        // another tx already removed a row our snapshot still sees —
        // Msg 3960 with auto-rollback (probe-confirmed against SQL Server
        // 2025; mirrors the SI UPDATE-on-RC-deleted case). Helper lives
        // in Simulation.Update.cs and the partial-class scope shares it.
        // Skipped for positioned deletes — the cursor fixed a single live row.
        if (positionedCursor is null)
            CheckSnapshotConflictOnTombstonedRows(context, table, where, sourceView);

        return CommitDelete(context, table, deleted, output, sourceView);
    }

    /// <summary>
    /// Joined-source DELETE execution. Mirrors
    /// <see cref="ExecuteJoinedUpdate"/>'s shape: parses multi-source FROM,
    /// identifies target via <see cref="FindMutationTargetIndex"/>, builds
    /// the byte[]-to-(page,slot) address map, then iterates join tuples
    /// applying WHERE per tuple and deduping target deletes by address.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedDelete(
        ParserContext context,
        MultiPartName leadingIdent,
        HeapTable? leadingTable,
        OutputProjection? output,
        Selection.DmlTopLimit? top)
    {
        var sourcesList = new List<FromSource>();
        var joinsList = new List<JoinSpec>();
        Selection.ParseSourcesAndJoins(context, depth: 0, sourcesList, joinsList, outerTypeResolver: null);
        var sources = sourcesList.ToArray();
        var joins = joinsList.ToArray();

        var targetIndex = FindMutationTargetIndex(context.Batch.CurrentDatabase.Collation, sources, leadingIdent.Leaf, leadingTable);
        if (targetIndex < 0)
            throw SimulatedSqlException.InvalidObjectName(leadingIdent);

        var table = sources[targetIndex].BackingTable
            ?? throw new NotSupportedException("UPDATE / DELETE target must be a table — derived-table targets aren't modeled.");
        if (!context.Batch.IsSkipping)
        {
            // A joined DELETE reads every FROM source (target + join sources);
            // real requires SELECT on each, checked before the DELETE write
            // permission (probe M2).
            CheckJoinedReadSources(context.Batch, sources, targetIndex);
            PermissionEnforcement.CheckTable(context.Batch, "DELETE", table);
        }

        // Alias-form DELETE: table-IX wasn't pre-acquired (target identified
        // post-FROM). Acquire it now; row-X per affected row fires at the
        // mutation site below.
        _ = context.Batch.AcquireDataLockIfApplicable(table, default, isWrite: true);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        var targetAddresses = new Dictionary<byte[], (int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        sources[targetIndex] = WrapSourceWithAddressTracking(sources[targetIndex], table, targetAddresses);

        var seen = new HashSet<(int Page, int Slot)>();
        var deleted = new List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)>();

        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, context.Batch, outerResolver: null))
        {
            var localTuple = tuple;
            SqlValue ResolveTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, localTuple, name);

            if (where is not null && where.Run(new RuntimeContext(ResolveTuple, context.Batch)) != true)
                continue;

            var targetBytes = tuple[targetIndex];
            if (targetBytes is null)
                continue;
            if (!targetAddresses.TryGetValue(targetBytes, out var addr))
                continue;
            if (!seen.Add(addr))
                continue;

            SqlValue[]? fullValues = null;
            var needsFull = output is not null
                || HasAfterTrigger(context.Batch, table, TriggerActions.Delete)
                || HasInsteadOfTrigger(context.Batch, table, TriggerActions.Delete)
                || table.SystemVersioning is not null;
            if (needsFull)
            {
                fullValues = DecodeFullRow(table, targetBytes);
                EvaluateComputedColumns(table, fullValues, context.Batch);
            }
            deleted.Add((addr.Page, addr.Slot, fullValues));
        }

        ApplyDmlTopCap(top, deleted, context.Batch);

        return CommitDelete(context, table, deleted, output, sourceView: null);
    }

    /// <summary>
    /// Tombstones the deleted rows and emits OUTPUT.DELETED projection rows
    /// when requested. Shared between the no-FROM and joined-source paths.
    /// When an INSTEAD OF DELETE trigger is attached to the target (heap
    /// table directly, or the view passed in <paramref name="sourceView"/>),
    /// the heap-delete / AFTER-trigger path is skipped and the INSTEAD OF
    /// body fires with DELETED carrying the would-be deleted rows.
    /// </summary>
    private static SimulatedStatementOutcome CommitDelete(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted,
        OutputProjection? output,
        View? sourceView = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);

        // SNAPSHOT isolation write-conflict: a DELETE on a row modified
        // since this SI tx's snapshot raises Msg 3960 and auto-rolls-back.
        if (context.Batch.Connection.SessionIsolationLevel == System.Data.IsolationLevel.Snapshot)
        {
            foreach (var (pageIndex, slotIndex, _) in deleted)
                Storage.VersionStore.CheckSnapshotUpdateConflict(context.Batch, table, (pageIndex, slotIndex));
        }

        if (insteadOfActive)
        {
            FireInsteadOfDeleteTrigger(context, table, sourceView, deleted);
            if (output is null || output.HasTarget)
                return new SimulatedNonQuery(deleted.Count);
            var rows = ProjectDeleteOutput(deleted, output);
            return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
        }

        var undoLog = table.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        // System-versioned DELETE: copy each row's pre-delete state to
        // history with ROW END = UtcNow before tombstoning the current row.
        if (table.SystemVersioning is { } historyTable && table.PeriodColumns is { } pc)
        {
            var stampedNow = SqlValue.FromDateTime2(table.Columns[pc.EndOrdinal].Type, context.Batch.CurrentStatement.UtcNow);
            foreach (var (_, _, oldFull) in deleted)
            {
                if (oldFull is null)
                    continue;
                var historyRow = new SqlValue[oldFull.Length];
                Array.Copy(oldFull, historyRow, oldFull.Length);
                historyRow[pc.EndOrdinal] = stampedNow;
                var (newPage, newSlot) = historyTable.Heap.Insert(RowEncoder.EncodeRow(historyTable.StoredColumns, ProjectStoredValues(historyTable, historyRow), historyTable.Heap), undoLog);
                if (IsLockableTable(historyTable))
                    context.Batch.AcquireRowLockTxScoped(historyTable, newPage, newSlot, LockMode.Exclusive);
            }
        }
        var lockableTable = IsLockableTable(table);
        var captureVersions = Storage.VersionStore.IsVersioningEnabled(context.CurrentDatabase) && lockableTable;
        foreach (var (pageIndex, slotIndex, _) in deleted)
        {
            if (lockableTable)
                context.Batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive);
            var oldBytes = captureVersions ? table.Heap.ReadSlotBytes(pageIndex, slotIndex) : null;
            table.Heap.DeleteAt(pageIndex, slotIndex, undoLog, ReclaimSuperseded(table, context));
            if (oldBytes is not null)
                Storage.VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytes, Storage.VersionWriteKind.Delete);
            // Row-lock dict cleanup: the slot is tombstoned and slot ids
            // never get reused (`Heap.DeleteAt` doesn't recycle directory
            // entries), so the per-row LockResource has no future relevance.
            // Drop the dict entry here even though our row-X is still held
            // — the holder reference in `tx.HeldLocks` / `StatementSchemaLocks`
            // keeps the resource alive until release; concurrent accessors
            // can't reach a tombstoned slot (heap iteration skips them, and
            // SI / RCSI tombstoned-slot resolution bypasses locks entirely).
            if (lockableTable)
                _ = table.RowLocks.TryRemove((pageIndex, slotIndex), out _);
        }

        // Incoming-FK cascade: parent-side DELETE fires the matching FK's
        // DELETE action on every child table whose FK columns reference one
        // of the deleted rows. NO ACTION raises Msg 547; CASCADE recurses;
        // SET NULL / SET DEFAULT rewrite the child's FK columns.
        if (table.IncomingForeignKeys.Count > 0)
        {
            var oldRows = new List<SqlValue[]>(deleted.Count);
            foreach (var (_, _, oldFull) in deleted)
            {
                if (oldFull is not null)
                    oldRows.Add(oldFull);
            }
            if (oldRows.Count > 0)
                EnforceIncomingForeignKeysOnDelete(table, oldRows, context, "DELETE", depth: 0);
        }

        if (output is not null)
        {
            var rows = ProjectDeleteOutput(deleted, output);
            // OUTPUT INTO @t suppresses the result set (probe-confirmed).
            if (!output.HasTarget)
            {
                FireAfterDeleteTriggers(context, table, deleted);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
            }
        }
        FireAfterDeleteTriggers(context, table, deleted);
        return new SimulatedNonQuery(deleted.Count);
    }

    private static List<byte[]> ProjectDeleteOutput(
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted,
        OutputProjection output)
    {
        var rows = new List<byte[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
        {
            var projectedBytes = output.ProjectRow(insertedValues: null, deletedValues: fullOld);
            if (projectedBytes is not null)
                rows.Add(projectedBytes);
        }
        return rows;
    }

    private static void FireAfterDeleteTriggers(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted)
    {
        if (!HasAfterTrigger(context.Batch, table, TriggerActions.Delete))
            return;
        var deletedRows = new List<SqlValue[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
            deletedRows.Add(fullOld ?? new SqlValue[table.Columns.Length]);
        context.Connection.LastStatementRowCount = deleted.Count;
        context.Batch.Connection.Simulation.FireTriggers(
            context.Batch, table, TriggerActions.Delete,
            insertedRows: null, deletedRows: deletedRows,
            affectedRowCount: deleted.Count);
    }

    /// <summary>
    /// Fires the INSTEAD OF DELETE trigger attached to
    /// <paramref name="sourceView"/> (when non-null) or
    /// <paramref name="table"/>. DELETED is projected through the view's
    /// <see cref="View.BaseColumnOrdinals"/> for a view target; INSERTED
    /// is empty for DELETE.
    /// </summary>
    private static void FireInsteadOfDeleteTrigger(
        ParserContext context,
        HeapTable table,
        View? sourceView,
        List<(int PageIndex, int SlotIndex, SqlValue[]? FullOld)> deleted)
    {
        var deletedRows = new List<SqlValue[]>(deleted.Count);
        foreach (var (_, _, fullOld) in deleted)
        {
            deletedRows.Add(sourceView is null
                ? (fullOld ?? new SqlValue[table.Columns.Length])
                : (fullOld is null
                    ? new SqlValue[sourceView.OutputColumns.Length]
                    : ProjectThroughView(sourceView, fullOld)));
        }
        context.Connection.LastStatementRowCount = deleted.Count;
        var pseudoColumns = sourceView?.OutputColumns ?? table.Columns;
        var parent = (SchemaObject?)sourceView ?? table;
        _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
            context.Batch, parent, TriggerActions.Delete,
            pseudoColumns, insertedRows: null, deletedRows: deletedRows,
            affectedRowCount: deleted.Count);
    }
}
