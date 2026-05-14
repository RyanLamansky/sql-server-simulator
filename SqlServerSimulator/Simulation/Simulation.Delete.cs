using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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
        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            context.MoveNextRequired();

        var leadingIdent = BatchContext.ParseObjectName(context, acceptTableVariable: true);

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
        _ = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
        // Phase 1a: acquire X on the resolved DELETE target. Tx-scoped when
        // BEGIN TRAN is active. Skipped when leadingTable is null (multi-
        // source alias form — target determined post-FROM, deferred to 1b).
        if (leadingTable is not null)
            context.Batch.AcquireDataLockIfApplicable(leadingTable, default, isWrite: true);

        // OUTPUT requires a known target. INSERTED isn't a valid qualifier
        // in DELETE OUTPUT (probe-confirmed Msg 4104). Alias-form multi-
        // source DELETE with OUTPUT isn't modeled — see the matching
        // limitation in ParseUpdate. DELETE OUTPUT through a view is also
        // rejected (the DELETED.* would need view-output-column rebinding).
        MutationOutputProjection? output = null;
        if (leadingView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"DELETE … OUTPUT through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");
        if (leadingTable is not null)
            output = TryParseOutputClauseForMutation(context, leadingTable, allowInserted: false, allowDeleted: true);
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException("OUTPUT with alias-form multi-source DELETE isn't modeled — re-emit with the table name as the target if OUTPUT is required.");

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            return leadingView is not null
                ? throw new NotSupportedException($"Multi-source DELETE through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled — target the underlying table directly.")
                : ExecuteJoinedDelete(context, leadingIdent, leadingTable, output);
        }

        var table = leadingTable ?? throw (BatchContext.IsTableVariableName(leadingIdent.Leaf)
            ? SimulatedSqlException.MustDeclareTableVariable(leadingIdent.Leaf)
            : SimulatedSqlException.InvalidObjectName(leadingIdent));
        return table.IsTableValuedParameter
            ? throw SimulatedSqlException.TableValuedParameterIsReadOnly(leadingIdent.Leaf)
            : ExecuteDeleteAgainstTable(context, table, output, leadingView);
    }

    /// <summary>
    /// Single-table no-FROM execution path: iterates the target heap directly,
    /// tombstones matching rows, and projects OUTPUT.DELETED if requested.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteDeleteAgainstTable(
        ParserContext context,
        HeapTable table,
        MutationOutputProjection? output,
        View? sourceView = null)
    {
        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
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
        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
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
                            if (Collation.Default.Equals(sourceView.OutputColumns[v].Name, name.Leaf))
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
                        if (Collation.Default.Equals(table.Columns[k].Name, name.Leaf))
                            return localValues[k];
                    }
                    throw SimulatedSqlException.InvalidColumnName(name);
                }

                if (where.Run(new RuntimeContext(Resolve, context.Batch)) != true)
                    continue;
            }

            deleted.Add((pageIndex, slotIndex, (output is null && !needsFullForTriggers && !needsFullForHistory && !needsFullForFk) ? null : fullValues));
        }

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
        MutationOutputProjection? output)
    {
        var sourcesList = new List<FromSource>();
        var joinsList = new List<JoinSpec>();
        Selection.ParseSourcesAndJoins(context, depth: 0, sourcesList, joinsList, outerTypeResolver: null);
        var sources = sourcesList.ToArray();
        var joins = joinsList.ToArray();

        var targetIndex = FindMutationTargetIndex(sources, leadingIdent.Leaf, leadingTable);
        if (targetIndex < 0)
            throw SimulatedSqlException.InvalidObjectName(leadingIdent);

        var table = sources[targetIndex].BackingTable
            ?? throw new NotSupportedException("UPDATE / DELETE target must be a table — derived-table targets aren't modeled.");

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
        MutationOutputProjection? output,
        View? sourceView = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Delete);

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
                historyTable.Heap.Insert(RowEncoder.EncodeRow(historyTable.StoredColumns, ProjectStoredValues(historyTable, historyRow), historyTable.Heap), undoLog);
            }
        }
        foreach (var (pageIndex, slotIndex, _) in deleted)
            table.Heap.DeleteAt(pageIndex, slotIndex, undoLog);

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
                EnforceIncomingForeignKeys(table, oldRows, affectedNewValues: null, context, "DELETE", depth: 0);
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
        MutationOutputProjection output)
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
