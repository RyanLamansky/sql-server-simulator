using System.Text;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes an UPDATE statement. Supports the bare form
    /// (<c>UPDATE table SET col = expr [WHERE pred]</c>), the EF7+ single-
    /// source <c>ExecuteUpdate</c> form (<c>UPDATE alias SET ... FROM table AS alias [WHERE]</c>),
    /// and the joined-source form (<c>UPDATE alias SET ... FROM t AS alias JOIN u AS b ON ... [WHERE]</c>)
    /// that EF Core emits for ExecuteUpdate over collection navigations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two-phase execution: phase 1 walks the row stream (heap directly for
    /// the no-FROM form; the joined-row tuple enumerator for the FROM form),
    /// picks rows matching WHERE, and computes their new full-column values
    /// (every SET RHS evaluated against the same <em>pre-update</em> snapshot
    /// of the row, matching SQL Server's documented behavior — verified by
    /// probe). Per-row constraints (NOT NULL via Msg 515 with the
    /// <c>"UPDATE fails."</c> verb; CHECK via Msg 547 with
    /// <c>"UPDATE statement"</c>) fire here. Phase 2 validates PK / UNIQUE
    /// against the <em>post-update</em> virtual state — every affected
    /// row's new key is checked against the other affected rows' new
    /// keys plus the non-affected heap rows' existing keys. Phase 3
    /// mutates: each affected row's old slot is tombstoned, then the
    /// new bytes are appended.
    /// </para>
    /// <para>
    /// In the joined-source form, the same target row may surface in
    /// multiple join tuples (e.g. a customer with two qualifying orders).
    /// SQL Server applies the SET exactly once per unique target row,
    /// using the <em>first</em> matching tuple's RHS values (heap-scan order
    /// — probe-confirmed against SQL Server 2025). The simulator dedupes
    /// targets by (page, slot) — same semantic, modulo any mutation of
    /// heap-scan order under the hood.
    /// </para>
    /// </remarks>
    private static SimulatedStatementOutcome ParseUpdate(ParserContext context)
    {
        context.MoveNextRequired();
        var leadingIdent = BatchContext.ParseObjectName(context, acceptTableVariable: true);

        // View target: route to base table with view-aware column lookups,
        // visibility filtering, and (optional) WITH CHECK OPTION enforcement.
        // Joined-source UPDATEs through views (alias-form + FROM clause)
        // aren't supported — EF Core doesn't emit that shape and it would
        // require composing the view's visibility predicate with a multi-
        // source join, which the existing alias-form path can't represent.
        View? leadingView = null;
        HeapTable? leadingTable;
        if (context.Batch.TryResolveView(leadingIdent, out var resolvedView))
        {
            // INSTEAD OF UPDATE on a view replaces the heap-write path; the
            // trigger body is responsible for any base-table mutations. The
            // simulator supports this only when the view is updatable (the
            // single-base path) so INSERTED / DELETED can be projected from
            // a heap row through the view's column map. INSTEAD OF UPDATE
            // on a non-updatable (join / aggregate) view is documented as
            // a deferred shape in CLAUDE.md.
            if (HasInsteadOfTrigger(context.Batch, resolvedView, TriggerActions.Update)
                && resolvedView.BaseTable is null)
            {
                throw new NotSupportedException($"INSTEAD OF UPDATE through a non-updatable view ('{resolvedView.Schema.Name}.{resolvedView.Name}') isn't modeled. Updatable single-base views work; join / aggregate / DISTINCT views are deferred.");
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
            // Leading identifier: target table name (single-table form) or an
            // alias for the FROM clause that follows (multi-table form). Try
            // table-resolution now; if it fails, the FROM clause must provide
            // the binding via alias-matching. Aliases are always single-segment,
            // so a multi-part name that fails to resolve is always Msg 208.
            _ = context.Batch.TryResolveTable(leadingIdent, out leadingTable);
        }

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        // Phase-1 SET parsing: raw (columnName, expr) pairs without ordinal
        // resolution — target may not be known yet. Each entry recognizes
        // single or qualified column names on the LHS, plain '=' or compound
        // arithmetic-assignment (+= -= *= /= %= &= |= ^=) on the operator
        // slot. Compound forms desugar to FromCompoundOp(op, Reference(col),
        // rhs) so the per-row ResolveOriginal resolver evaluates the column's
        // pre-update value as the LHS — matches probe-confirmed
        // "UPDATE t SET v += rhs" semantics on a real SQL Server instance.
        var rawAssignments = new List<(string ColumnName, Expression Expr)>();
        while (true)
        {
            if (context.GetNextRequired() is not StringToken first)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            string columnName;
            Expression lhsForCompound;
            var afterName = context.GetNextRequired();
            if (afterName is Operator { Character: '.' })
            {
                if (context.GetNextRequired() is not StringToken col)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                columnName = col.Value;
                lhsForCompound = new Reference(first.Value, col.Value);
                context.MoveNextRequired();
            }
            else
            {
                columnName = first.Value;
                lhsForCompound = new Reference(columnName);
            }

            if (TryConsumeAssignmentOperator(context) is not char assignOp)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            context.MoveNextRequired();
            var rhs = Expression.Parse(context);
            var finalExpr = assignOp == '=' ? rhs : TwoSidedExpression.FromCompoundOp(assignOp, lhsForCompound, rhs);
            rawAssignments.Add((columnName, finalExpr));

            if (context.Token is Operator { Character: ',' })
                continue;
            break;
        }

        // OUTPUT requires a known target. If leading-ident resolved to a
        // table, parse OUTPUT now (existing single-table OUTPUT path). For
        // the alias-form multi-source case, OUTPUT support would require
        // deferring its parse until after FROM has identified the target —
        // not modeled today (EF Core 10 doesn't combine OUTPUT with multi-
        // source ExecuteUpdate, and the simulator raises NotSupportedException
        // when this combination is attempted). OUTPUT through a view is
        // also rejected — the projected INSERTED.* / DELETED.* would need
        // view-output-column rebinding, which isn't modeled.
        MutationOutputProjection? output = null;
        if (leadingView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"UPDATE … OUTPUT through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");
        if (leadingTable is not null)
            output = TryParseOutputClauseForMutation(context, leadingTable, allowInserted: true, allowDeleted: true);
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException("OUTPUT with alias-form multi-source UPDATE isn't modeled — re-emit with the table name as the target if OUTPUT is required.");

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            return leadingView is not null
                ? throw new NotSupportedException($"Multi-source UPDATE through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled — the alias-form FROM clause can't compose with the view's visibility predicate. Target the underlying table directly.")
                : ExecuteJoinedUpdate(context, leadingIdent, leadingTable, rawAssignments, output);
        }

        var table = leadingTable ?? throw (BatchContext.IsTableVariableName(leadingIdent.Leaf)
            ? SimulatedSqlException.MustDeclareTableVariable(leadingIdent.Leaf)
            : SimulatedSqlException.InvalidObjectName(leadingIdent));
        return table.IsTableValuedParameter
            ? throw SimulatedSqlException.TableValuedParameterIsReadOnly(leadingIdent.Leaf)
            : ExecuteUpdateAgainstTable(context, table, rawAssignments, output, leadingView);
    }

    /// <summary>
    /// Single-table no-FROM execution path: iterates the target heap directly
    /// with addresses, evaluates WHERE / SET against per-row resolvers, and
    /// runs the standard two-phase validation + mutation pipeline.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteUpdateAgainstTable(
        ParserContext context,
        HeapTable table,
        List<(string ColumnName, Expression Expr)> rawAssignments,
        MutationOutputProjection? output,
        View? sourceView = null)
    {
        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase, sourceView);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        foreach (var (pageIndex, slotIndex, rowBytes) in table.Heap.EnumerateRowsWithAddress())
        {
            var fullValues = DecodeFullRow(table, rowBytes);
            EvaluateComputedColumns(table, fullValues, context.Batch);

            // View visibility filter: rows not visible in the view aren't
            // candidates for UPDATE through it. AND-of-WHEREs up the chain
            // (no-op when sourceView is null or the chain has no WHERE).
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues, context.Batch))
                continue;

            SqlValue ResolveOriginal(MultiPartName name)
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
                                : fullValues[baseOrd];
                        }
                    }
                    throw SimulatedSqlException.InvalidColumnName(name);
                }
                for (var k = 0; k < table.Columns.Length; k++)
                {
                    if (Collation.Default.Equals(table.Columns[k].Name, name.Leaf))
                        return fullValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }

            if (where is not null && where.Run(new RuntimeContext(ResolveOriginal, context.Batch)) != true)
                continue;

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list expressions.
            context.Batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, ResolveOriginal);

            // WITH CHECK OPTION: the post-update row must satisfy every
            // CHECK OPTION-bearing WHERE in the chain. Fires before
            // CommitUpdate so a violating UPDATE leaves the heap unchanged.
            if (sourceView?.CheckOptionCheck is { } co && !co(newValues, context.Batch))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            // Snapshot the old row when OUTPUT, AFTER UPDATE triggers, or
            // an INSTEAD OF UPDATE on the parent (table or view) needs it
            // for DELETED.<col> resolution.
            var insteadOfParent = (SchemaObject?)sourceView ?? table;
            var oldSnapshotNeeded = output is not null
                || HasAfterTrigger(context.Batch, table, TriggerActions.Update)
                || HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Update)
                || table.SystemVersioning is not null
                || table.IncomingForeignKeys.Count > 0;
            var oldSnapshot = oldSnapshotNeeded ? fullValues : null;
            affected.Add((pageIndex, slotIndex, newValues, oldSnapshot));
        }

        return CommitUpdate(context, table, affected, output, sourceView);
    }

    /// <summary>
    /// Joined-source UPDATE execution. Reuses the SELECT-side
    /// <see cref="Selection.ParseSourcesAndJoins"/> and
    /// <see cref="Selection.EnumerateJoinedRows"/> machinery: parses the
    /// <c>FROM</c> clause as a multi-source list, identifies the target by
    /// matching the leading identifier against each source's qualifier,
    /// builds a byte[]-to-(page,slot) address map for the target heap, and
    /// then iterates join tuples — applying WHERE per tuple, deduping
    /// targets by (page, slot), and applying SET against the first matching
    /// tuple's resolver. The address-map approach avoids extending
    /// <see cref="Selection.EnumerateJoinedRows"/> with address-tracking
    /// (which would only matter to mutations).
    /// </summary>
    private static SimulatedStatementOutcome ExecuteJoinedUpdate(
        ParserContext context,
        MultiPartName leadingIdent,
        HeapTable? leadingTable,
        List<(string ColumnName, Expression Expr)> rawAssignments,
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

        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = BooleanExpression.Parse(context);
        }

        var targetAddresses = new Dictionary<byte[], (int Page, int Slot)>(ReferenceEqualityComparer.Instance);
        sources[targetIndex] = WrapSourceWithAddressTracking(sources[targetIndex], table, targetAddresses);

        var seen = new HashSet<(int Page, int Slot)>();
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();

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

            var fullValues = DecodeFullRow(table, targetBytes);
            EvaluateComputedColumns(table, fullValues, context.Batch);

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list.
            context.Batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, ResolveTuple);
            var oldSnapshotNeeded = output is not null
                || HasAfterTrigger(context.Batch, table, TriggerActions.Update)
                || HasInsteadOfTrigger(context.Batch, table, TriggerActions.Update)
                || table.SystemVersioning is not null;
            var oldSnapshot = oldSnapshotNeeded ? fullValues : null;
            affected.Add((addr.Page, addr.Slot, newValues, oldSnapshot));
        }

        return CommitUpdate(context, table, affected, output, sourceView: null);
    }

    /// <summary>
    /// Phase 2 (PK / UNIQUE validation) + phase 3 (tombstone old, insert
    /// new) + OUTPUT projection. Shared by the no-FROM and joined-source
    /// execution paths so the post-collection logic stays in one place.
    /// When an INSTEAD OF UPDATE trigger is attached to the target
    /// (either the heap table directly, or the view passed in
    /// <paramref name="sourceView"/>), the heap-write / AFTER-trigger
    /// path is skipped and the INSTEAD OF body fires with INSERTED /
    /// DELETED carrying the would-be new and old row values.
    /// </summary>
    private static SimulatedStatementOutcome CommitUpdate(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        MutationOutputProjection? output,
        View? sourceView = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Update);

        if (affected.Count == 0)
            return output is null ? new SimulatedNonQuery(0) : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, []);

        if (insteadOfActive)
        {
            FireInsteadOfUpdateTrigger(context, table, sourceView, affected);
            return output is null || output.HasTarget
                ? new SimulatedNonQuery(affected.Count)
                : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, ProjectMutationOutput(affected, output));
        }

        EnforceKeyConstraintsForUpdate(table, affected);

        // Outgoing FK check on the post-update rows (UPDATE may have rewritten
        // the child's FK columns to point at a parent that doesn't exist).
        if (table.OutgoingForeignKeys.Count > 0)
        {
            var newRows = new List<SqlValue[]>(affected.Count);
            foreach (var (_, _, fullNew, _) in affected)
                newRows.Add(fullNew);
            EnforceOutgoingForeignKeys(table, newRows, context, "UPDATE");
        }

        var undoLog = table.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;
        // System-versioned UPDATE: copy each affected row's pre-update state
        // to the history sibling before tombstoning the current row. History
        // rows carry the row's original ROW START and a fresh ROW END = the
        // statement's frozen UtcNow.
        if (table.SystemVersioning is { } historyTable && table.PeriodColumns is { } pc)
            WriteHistoryRowsForUpdate(table, historyTable, pc, affected, context, undoLog);
        foreach (var (pageIndex, slotIndex, _, _) in affected)
            table.Heap.DeleteAt(pageIndex, slotIndex, undoLog);
        foreach (var (_, _, fullNew, _) in affected)
            table.Heap.Insert(RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, fullNew), table.Heap), undoLog);

        // Incoming-FK cascade: if any of the updated rows participate in a
        // referenced key, fire the matching FK's UPDATE action against the
        // child tables. Filters internally on actually-changed referenced
        // columns so an UPDATE that doesn't touch a key is a no-op.
        if (table.IncomingForeignKeys.Count > 0)
        {
            var pairs = new List<(SqlValue[] OldFull, SqlValue[] NewFull)>(affected.Count);
            foreach (var (_, _, fullNew, fullOld) in affected)
            {
                if (fullOld is null) continue;
                pairs.Add((fullOld, fullNew));
            }
            if (pairs.Count > 0)
                EnforceIncomingFkOnUpdate(table, pairs, context, depth: 0);
        }

        if (output is not null)
        {
            var rows = ProjectMutationOutput(affected, output);
            // OUTPUT INTO @t suppresses the result set (probe-confirmed).
            if (!output.HasTarget)
            {
                FireAfterUpdateTriggers(context, table, affected);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
            }
        }
        FireAfterUpdateTriggers(context, table, affected);
        return new SimulatedNonQuery(affected.Count);
    }

    /// <summary>
    /// Writes a history row for each row affected by a system-versioned
    /// UPDATE. Each history row preserves the pre-update full column set,
    /// with ROW END overwritten to the statement's frozen UtcNow. The
    /// resulting period is <c>[original ROW START, UtcNow)</c> — the
    /// half-open interval during which that row was current.
    /// </summary>
    private static void WriteHistoryRowsForUpdate(
        HeapTable parent,
        HeapTable historyTable,
        (int StartOrdinal, int EndOrdinal) period,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        ParserContext context,
        UndoLog? undoLog)
    {
        var stampedNow = SqlValue.FromDateTime2(parent.Columns[period.EndOrdinal].Type, context.Batch.CurrentStatement.UtcNow);
        foreach (var (_, _, _, oldFull) in affected)
        {
            if (oldFull is null)
                continue;
            var historyRow = new SqlValue[oldFull.Length];
            Array.Copy(oldFull, historyRow, oldFull.Length);
            historyRow[period.EndOrdinal] = stampedNow;
            historyTable.Heap.Insert(RowEncoder.EncodeRow(historyTable.StoredColumns, ProjectStoredValues(historyTable, historyRow), historyTable.Heap), undoLog);
        }
    }

    private static List<byte[]> ProjectMutationOutput(
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        MutationOutputProjection output)
    {
        var rows = new List<byte[]>(affected.Count);
        foreach (var (_, _, fullNew, fullOld) in affected)
        {
            var projectedBytes = output.ProjectRow(insertedValues: fullNew, deletedValues: fullOld);
            if (projectedBytes is not null)
                rows.Add(projectedBytes);
        }
        return rows;
    }

    /// <summary>
    /// Fires the single INSTEAD OF UPDATE trigger attached to
    /// <paramref name="sourceView"/> (when non-null) or
    /// <paramref name="table"/> (table target). INSERTED / DELETED are
    /// projected through the view's <see cref="View.BaseColumnOrdinals"/>
    /// for a view target so the trigger sees view-shaped rows; for a
    /// table target the heap row is used directly.
    /// </summary>
    private static void FireInsteadOfUpdateTrigger(
        ParserContext context,
        HeapTable table,
        View? sourceView,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected)
    {
        var insertedRows = new List<SqlValue[]>(affected.Count);
        var deletedRows = new List<SqlValue[]>(affected.Count);
        foreach (var (_, _, fullNew, fullOld) in affected)
        {
            insertedRows.Add(sourceView is null ? fullNew : ProjectThroughView(sourceView, fullNew));
            deletedRows.Add(sourceView is null
                ? (fullOld ?? new SqlValue[table.Columns.Length])
                : (fullOld is null
                    ? new SqlValue[sourceView.OutputColumns.Length]
                    : ProjectThroughView(sourceView, fullOld)));
        }
        context.Connection.LastStatementRowCount = affected.Count;
        var pseudoColumns = sourceView?.OutputColumns ?? table.Columns;
        var parent = (SchemaObject?)sourceView ?? table;
        _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
            context.Batch, parent, TriggerActions.Update,
            pseudoColumns, insertedRows, deletedRows,
            affectedRowCount: affected.Count);
    }

    /// <summary>
    /// Projects a base-table row through a view's
    /// <see cref="View.BaseColumnOrdinals"/> map to the view's
    /// <see cref="View.OutputColumns"/> shape. Derived projection slots
    /// (BaseColumnOrdinals[i] = -1) get a typed NULL — there's no underlying
    /// base column whose value to surface. Used by INSTEAD OF UPDATE / DELETE
    /// on updatable views.
    /// </summary>
    private static SqlValue[] ProjectThroughView(View view, SqlValue[] baseRow)
    {
        var projected = new SqlValue[view.OutputColumns.Length];
        for (var i = 0; i < view.OutputColumns.Length; i++)
        {
            var baseOrd = view.BaseColumnOrdinals[i];
            projected[i] = baseOrd < 0 ? SqlValue.Null(view.OutputColumns[i].Type) : baseRow[baseOrd];
        }
        return projected;
    }

    /// <summary>
    /// Fires AFTER UPDATE triggers attached to <paramref name="table"/>.
    /// The affected list always carries <c>FullNew</c>; <c>FullOld</c>
    /// is only populated when the caller pre-captures (OUTPUT clause or
    /// the trigger-presence check). When triggers are present but
    /// FullOld is null on any row (e.g. update path that didn't need
    /// the old values for OUTPUT), the trigger sees a NULL-projected
    /// DELETED row — gap documented in CLAUDE.md.
    /// </summary>
    private static void FireAfterUpdateTriggers(
        ParserContext context,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected)
    {
        if (!HasAfterTrigger(context.Batch, table, TriggerActions.Update))
            return;
        var insertedRows = new List<SqlValue[]>(affected.Count);
        var deletedRows = new List<SqlValue[]>(affected.Count);
        foreach (var (_, _, fullNew, fullOld) in affected)
        {
            insertedRows.Add(fullNew);
            deletedRows.Add(fullOld ?? new SqlValue[table.Columns.Length]);
        }
        context.Connection.LastStatementRowCount = affected.Count;
        context.Batch.Connection.Simulation.FireTriggers(
            context.Batch, table, TriggerActions.Update,
            insertedRows, deletedRows, affectedRowCount: affected.Count);
    }

    /// <summary>
    /// Builds the <c>database.schema.leaf</c> qualified name for an error
    /// message that requires it. Schema-id → schema-name lookup is an O(N)
    /// scan of the database's schemas dict; the table count for any realistic
    /// workload makes this acceptable.
    /// </summary>
    internal static string QualifyTableName(HeapTable table, Database database)
    {
        foreach (var entry in database.Schemas)
        {
            if (entry.Value.SchemaId == table.SchemaId)
                return $"{database.Name}.{entry.Key}.{table.Name}";
        }
        return $"{database.Name}.{table.Name}";
    }

    /// <summary>
    /// Resolves the raw <c>SET</c> column-name pairs to ordinals against the
    /// target table, rejecting writes to identity / computed / rowversion /
    /// GENERATED ALWAYS columns up-front so the per-row loop never has to
    /// re-check.
    /// </summary>
    private static List<(int Ordinal, Expression Expr)> ResolveSetAssignments(
        List<(string ColumnName, Expression Expr)> rawAssignments,
        HeapTable table,
        Database database,
        View? sourceView = null)
    {
        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        foreach (var (colName, expr) in rawAssignments)
        {
            int columnOrdinal;
            if (sourceView is not null)
            {
                var viewOrd = -1;
                for (var i = 0; i < sourceView.OutputColumns.Length; i++)
                {
                    if (Collation.Default.Equals(sourceView.OutputColumns[i].Name, colName))
                    {
                        viewOrd = i;
                        break;
                    }
                }
                if (viewOrd < 0)
                    throw SimulatedSqlException.InvalidColumnName(colName);
                columnOrdinal = sourceView.BaseColumnOrdinals[viewOrd];
                if (columnOrdinal < 0)
                    throw SimulatedSqlException.ViewDmlTouchesDerivedField($"{sourceView.Schema.Name}.{sourceView.Name}");
            }
            else
            {
                columnOrdinal = -1;
                for (var i = 0; i < table.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(table.Columns[i].Name, colName))
                    {
                        columnOrdinal = i;
                        break;
                    }
                }
                if (columnOrdinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(colName);
            }

            var column = table.Columns[columnOrdinal];
            if (column.Identity is not null)
                throw SimulatedSqlException.CannotUpdateIdentityColumn(column.Name);
            if (column.Computed is not null)
                throw SimulatedSqlException.ColumnCannotBeModified(column.Name);
            if (column.Type == SqlType.RowVersion)
                throw SimulatedSqlException.CannotUpdateTimestampColumn();
            if (column.GeneratedAs != GeneratedAlwaysAsRow.None)
                throw SimulatedSqlException.CannotUpdateGeneratedAlways(QualifyTableName(table, database));

            assignments.Add((columnOrdinal, expr));
        }
        return assignments;
    }

    /// <summary>
    /// Decodes one heap row into the target table's full logical column
    /// array (size <c>Columns.Length</c>), with NULL stand-ins for unstored
    /// (non-persisted computed) slots. The caller then runs
    /// <see cref="EvaluateComputedColumns"/> to fill in computed values.
    /// </summary>
    private static SqlValue[] DecodeFullRow(HeapTable table, byte[] rowBytes)
    {
        var fullValues = new SqlValue[table.Columns.Length];
        var storedColumns = table.StoredColumns;
        for (var i = 0; i < table.Columns.Length; i++)
        {
            var ord = table.StorageOrdinals[i];
            fullValues[i] = ord < 0
                ? SqlValue.Null(table.Columns[i].Type)
                : RowDecoder.DecodeColumn(storedColumns, rowBytes, ord, table.Heap);
        }
        return fullValues;
    }

    /// <summary>
    /// Computes the post-SET row from the pre-update <paramref name="fullValues"/>
    /// snapshot: every SET RHS evaluates against the same snapshot (matching
    /// SQL Server: <c>UPDATE t SET a = 100, b = a + 1</c> over a row with
    /// <c>(a=10, b=20)</c> yields <c>(a=100, b=11)</c>); rowversion auto-bumps;
    /// computed columns recompute against the new values; NOT NULL and CHECK
    /// fire here (per-row constraint validation); PK / UNIQUE wait for phase 2.
    /// </summary>
    private static SqlValue[] ComputeUpdatedRow(
        ParserContext context,
        HeapTable table,
        SqlValue[] fullValues,
        List<(int Ordinal, Expression Expr)> assignments,
        Func<MultiPartName, SqlValue> resolver)
    {
        var newValues = new SqlValue[table.Columns.Length];
        Array.Copy(fullValues, newValues, fullValues.Length);

        foreach (var (ordinal, expr) in assignments)
        {
            var raw = expr.Run(new RuntimeContext(resolver, context.Batch));
            EnforceMaxLength(raw, table.Columns[ordinal], table.Name, context.Connection);
            newValues[ordinal] = CoerceForInsert(raw, table.Columns[ordinal].Type);
        }

        for (var ci = 0; ci < table.Columns.Length; ci++)
        {
            if (table.Columns[ci].Type == SqlType.RowVersion)
                newValues[ci] = SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion());
        }

        // Advance the current row's ROW START on a system-versioned UPDATE.
        // ROW END stays at max (the row is still current). The pre-update
        // ROW START surfaces in `fullValues` for the history-row copy that
        // CommitUpdate writes.
        if (table.PeriodColumns is { } pc && table.SystemVersioning is not null)
            newValues[pc.StartOrdinal] = SqlValue.FromDateTime2(table.Columns[pc.StartOrdinal].Type, context.Batch.CurrentStatement.UtcNow);

        EvaluateComputedColumns(table, newValues, context.Batch);
        EnforceNotNull(table, newValues, "UPDATE");
        EnforceCheckConstraints(table, newValues, context.Batch, "UPDATE");

        return newValues;
    }

    /// <summary>
    /// Locates the mutation target inside a multi-source FROM clause. Match
    /// rule (probe-confirmed against SQL Server 2025): the target is the
    /// source whose <see cref="FromSource.Qualifier"/> equals the leading
    /// identifier (alias OR table name). When the leading identifier is a
    /// table name and that exact name appears as a source's qualifier, we
    /// match; otherwise we look for any source whose
    /// <see cref="FromSource.BackingTable"/> equals the up-front-resolved
    /// table — covering the no-alias multi-source form
    /// (<c>UPDATE TableName ... FROM TableName JOIN ...</c>) where the
    /// source's qualifier is the table name itself.
    /// </summary>
    /// <returns>The matching source's index in <paramref name="sources"/>, or -1 when no source matches.</returns>
    private static int FindMutationTargetIndex(FromSource[] sources, string leadingIdent, HeapTable? leadingTable)
    {
        for (var s = 0; s < sources.Length; s++)
        {
            if (sources[s].Qualifier is { } q && Collation.Default.Equals(q, leadingIdent))
                return s;
        }
        if (leadingTable is not null)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (ReferenceEquals(sources[s].BackingTable, leadingTable))
                    return s;
            }
        }
        return -1;
    }

    /// <summary>
    /// Wraps the target <see cref="FromSource"/>'s row enumerator so each
    /// yielded <c>byte[]</c> is recorded into <paramref name="addressMap"/>
    /// alongside its <c>(page, slot)</c> address — a side-channel the join
    /// driver doesn't see but the mutation loop relies on. The simulator's
    /// heap row enumerators allocate a fresh <c>byte[]</c> per yield (rows
    /// are sliced out of the page's backing buffer via <c>ToArray()</c>),
    /// so a one-shot map built before iteration would have stale references
    /// against the join driver's per-iteration allocations. Recording during
    /// iteration keeps the map keyed by the exact instances the join driver
    /// places in tuples, so the lookup is reference-equality fast and
    /// correct even when the target source is on the inner side of a join
    /// (which restarts its enumeration once per outer tuple).
    /// </summary>
    private static FromSource WrapSourceWithAddressTracking(
        FromSource original,
        HeapTable table,
        Dictionary<byte[], (int Page, int Slot)> addressMap)
    {
        IEnumerable<byte[]> RowsRecording()
        {
            foreach (var (page, slot, bytes) in table.Heap.EnumerateRowsWithAddress())
            {
                addressMap[bytes] = (page, slot);
                yield return bytes;
            }
        }

        return new FromSource(
            qualifier: original.Qualifier,
            columnNames: original.ColumnNames,
            columns: original.Columns,
            storedSchema: original.StoredSchema,
            storageOrdinals: original.StorageOrdinals,
            lobStore: original.LobStore,
            rows: RowsRecording(),
            backingTable: original.BackingTable);
    }

    /// <summary>
    /// Multi-source column resolver for joined UPDATE / DELETE: resolves a
    /// reference against any source's columns by qualifier-aware lookup,
    /// decodes the column from the source's tuple slot. NULL-filled tuple
    /// slots (LEFT JOIN no-match) surface as typed NULL.
    /// </summary>
    private static SqlValue ResolveAcrossMutationTuple(FromSource[] sources, byte[]?[] tuple, MultiPartName name)
    {
        var (s, c) = Selection.FindSourceColumn(sources, name);
        if (s == -1)
            throw SimulatedSqlException.InvalidColumnName(name);

        var bytes = tuple[s];
        if (bytes is null)
            return SqlValue.Null(sources[s].Columns[c].Type);

        var ord = sources[s].StorageOrdinals?[c] ?? c;
        return RowDecoder.DecodeColumn(sources[s].StoredSchema, bytes, ord, sources[s].LobStore);
    }

    /// <summary>
    /// PK / UNIQUE validation for UPDATE: each affected row's new stored-key
    /// tuple is checked against (a) every other affected row's new key and
    /// (b) every non-affected heap row's existing key. Self-collision (a row
    /// matching its own pre-update self) is impossible because affected
    /// addresses are excluded from the heap-side scan. Edge case not
    /// modeled: SQL Server allows mass "shift" updates like
    /// <c>UPDATE t SET k = k + 1</c> over a unique-key column — the simulator's
    /// per-row check fails when the shifted value matches another affected
    /// row's pre-shift value pattern (CLAUDE.md flags this as a quirk).
    /// </summary>
    private static void EnforceKeyConstraintsForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected)
    {
        if (table.KeyConstraints.Count == 0)
            return;

        var affectedAddrs = new HashSet<(int, int)>();
        var storedSnapshots = new SqlValue[affected.Count][];
        for (var i = 0; i < affected.Count; i++)
        {
            _ = affectedAddrs.Add((affected[i].PageIndex, affected[i].SlotIndex));
            storedSnapshots[i] = ProjectStoredValues(table, affected[i].FullNew);
        }

        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        for (var i = 0; i < affected.Count; i++)
        {
            var myStored = storedSnapshots[i];

            foreach (var constraint in table.KeyConstraints)
            {
                for (var j = 0; j < affected.Count; j++)
                {
                    if (i == j)
                        continue;
                    if (KeyTuplesEqualStored(myStored, storedSnapshots[j], constraint))
                        throw KeyViolationForUpdate(table, constraint, myStored);
                }

                foreach (var (p, s, bytes) in table.Heap.EnumerateRowsWithAddress())
                {
                    if (affectedAddrs.Contains((p, s)))
                        continue;
                    var allEqual = true;
                    for (var k = 0; k < constraint.StorageOrdinals.Length; k++)
                    {
                        var ord = constraint.StorageOrdinals[k];
                        var existing = RowDecoder.DecodeColumn(storedColumns, bytes, ord, lobStore);
                        if (!existing.Equals(myStored[ord]))
                        {
                            allEqual = false;
                            break;
                        }
                    }
                    if (allEqual)
                        throw KeyViolationForUpdate(table, constraint, myStored);
                }
            }
        }
    }

    private static bool KeyTuplesEqualStored(SqlValue[] a, SqlValue[] b, KeyConstraint constraint)
    {
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            var ord = constraint.StorageOrdinals[i];
            if (!a[ord].Equals(b[ord]))
                return false;
        }
        return true;
    }

    private static SimulatedSqlException KeyViolationForUpdate(HeapTable table, KeyConstraint constraint, SqlValue[] storedValues)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < constraint.StorageOrdinals.Length; i++)
        {
            if (i > 0)
                _ = sb.Append(", ");
            _ = sb.Append(FormatKeyValue(storedValues[constraint.StorageOrdinals[i]]));
        }
        return SimulatedSqlException.ViolationOfKeyConstraint(constraint.ViolationKindWord, constraint.Name, table.Name, sb.ToString());
    }
}
