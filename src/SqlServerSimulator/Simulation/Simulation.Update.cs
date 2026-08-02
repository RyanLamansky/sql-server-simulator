using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
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
        var top = Selection.ParseDmlTopClause(context);
        var leadingIdent = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        context.Batch.RejectCrossServerMutation(leadingIdent);

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
            // A multi-source body has no single base table to route to up
            // front — which base the statement writes is the SET list's to
            // say — so it leaves `leadingTable` null and the join-view path
            // below picks up once the SET list has parsed.
            if (resolvedView.BaseTable is null && !resolvedView.IsJoinUpdatable)
            {
                throw resolvedView.RejectionReason == ViewUpdatabilityRejection.MultipleSources
                    ? SimulatedSqlException.ViewUpdateAffectsMultipleTables($"{resolvedView.Schema.Name}.{resolvedView.Name}")
                    : SimulatedSqlException.CannotUpdateNonUpdatableView($"{resolvedView.Schema.Name}.{resolvedView.Name}");
            }
            leadingView = resolvedView;
            leadingTable = resolvedView.BaseTable;
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

        context.MoveNextRequired();
        Selection.ValidateDmlTargetHints(Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false));
        // Phase 1a: when the leading identifier resolved to a concrete table
        // (the simple `UPDATE t SET …` case), acquire X on it. Tx-scoped via
        // AcquireDataLockIfApplicable when an explicit BEGIN TRAN is active.
        // The multi-table-alias form's target is determined later via the
        // FROM clause; that path's X acquisition is deferred to phase 1b.
        if (leadingTable is not null)
        {
            RejectDisabledClusteredIndex(leadingTable);
            RejectIncorrectSetOptionsForWrite(leadingTable, context.Batch, "UPDATE");
            _ = context.Batch.AcquireDataLockIfApplicable(leadingTable, default, isWrite: true);
        }
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Set })
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

        // A subquery in a SET expression can reference the update target's
        // columns — `SET alias = (SELECT MAX(v) FROM (VALUES (t.name),(t.goes_by)) x(v))`
        // is what ORMs emit for GREATEST / LEAST — so the target's columns have
        // to be in scope while the SET list parses. Runtime already threads the
        // per-row resolver through RuntimeContext; only the parse-time type
        // resolution was missing. The multi-table alias form has no target yet
        // at this point and keeps the enclosing scope.
        // Restored right after the loop; a throw in between aborts the whole
        // statement, so there is no later parse to see a stale scope.
        var savedOuterTypeResolver = context.OuterTypeResolver;
        if (leadingTable is { } scopeTable)
        {
            var enclosing = savedOuterTypeResolver;
            context.OuterTypeResolver = name => ResolveUpdateTargetColumnType(scopeTable, name, enclosing);
        }

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

                // `col.modify('…')` is the mutator form of a SET clause — the
                // whole clause, with no assignment operator. Only a one-part
                // column name carries it: real answers Msg 102 for
                // `t.col.modify(…)`, which falls out of the assignment-operator
                // check below since the three-part shape lands here instead.
                if (XmlMethodCall.IsKnownMethodName(columnName) && context.Token is Operator { Character: '(' })
                {
                    rawAssignments.Add(ParseXmlMutatorSetClause(context, first.Value, columnName));
                    if (context.Token is Operator { Character: ',' })
                        continue;
                    break;
                }
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

        context.OuterTypeResolver = savedOuterTypeResolver;

        // OUTPUT requires a known target. If leading-ident resolved to a
        // table, parse OUTPUT now (existing single-table OUTPUT path). For
        // the alias-form multi-source case, OUTPUT support would require
        // deferring its parse until after FROM has identified the target —
        // not modeled today (EF Core 10 doesn't combine OUTPUT with multi-
        // source ExecuteUpdate, and the simulator raises NotSupportedException
        // when this combination is attempted). OUTPUT through a view is
        // also rejected — the projected INSERTED.* / DELETED.* would need
        // view-output-column rebinding, which isn't modeled.
        OutputProjection? output = null;
        if (leadingView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"UPDATE … OUTPUT through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");
        if (leadingTable is not null)
        {
            output = TryParseOutputClauseForMutation(context, leadingTable, allowInserted: true, allowDeleted: true);
            RejectClientOutputOnTriggeredTarget(context.Batch, leadingTable, TriggerActions.Update, leadingIdent.ToString(), output is { HasTarget: false });
        }
        else if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
        {
            throw new NotSupportedException("OUTPUT with alias-form multi-source UPDATE isn't modeled — re-emit with the table name as the target if OUTPUT is required.");
        }

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
        {
            return leadingView is not null
                ? throw new NotSupportedException($"Multi-source UPDATE through a view ('{leadingView.Schema.Name}.{leadingView.Name}') isn't modeled — the alias-form FROM clause can't compose with the view's visibility predicate. Target the underlying table directly.")
                : ExecuteJoinedUpdate(context, leadingIdent, leadingTable, rawAssignments, output, top);
        }

        // Through a multi-source view the SET list is what names the base
        // table, so the write routes only once it has parsed.
        if (leadingView is { BaseTable: null } joinView)
            return ExecuteJoinViewUpdate(context, leadingIdent, joinView, rawAssignments, top);

        var table = leadingTable ?? throw (BatchContext.IsTableVariableName(leadingIdent.Leaf)
            ? SimulatedSqlException.MustDeclareTableVariable(leadingIdent.Leaf)
            : context.Batch.UnresolvableObjectName(leadingIdent));
        if (table.IsTableValuedParameter)
            throw SimulatedSqlException.TableValuedParameterIsReadOnly(leadingIdent.Leaf);
        FunctionBodyShape.NoteTableWrite(context.Batch, "UPDATE", table);
        return ExecuteUpdateAgainstTable(context, leadingIdent, table, rawAssignments, output, top, leadingView);
    }

    /// <summary>
    /// Parses an UPDATE SET clause of the mutator shape
    /// <c>col.modify('&lt;xml-dml&gt;')</c> into the ordinary
    /// <c>(column, expression)</c> pair the rest of the pipeline consumes —
    /// the expression re-reads the column's pre-update value and answers the
    /// edited instance, so OUTPUT, triggers and constraint enforcement all see
    /// a plain new value. <c>sql:column()</c> references inside the XQuery
    /// bind through the target-table scope the SET list already parses under.
    /// </summary>
    private static (string ColumnName, Expression Expr) ParseXmlMutatorSetClause(ParserContext context, string columnName, string methodName)
    {
        var resolver = context.OuterTypeResolver;
        var expression = XmlModify.Parse(
            new Reference(columnName),
            columnName,
            methodName,
            context,
            resolver is null ? null : name => resolver(new MultiPartName(name)));
        return (columnName, expression);
    }

    /// <summary>
    /// Single-table no-FROM execution path: iterates the target heap directly
    /// with addresses, evaluates WHERE / SET against per-row resolvers, and
    /// runs the standard two-phase validation + mutation pipeline.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteUpdateAgainstTable(
        ParserContext context,
        MultiPartName targetName,
        HeapTable table,
        List<(string ColumnName, Expression Expr)> rawAssignments,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        View? sourceView = null)
    {
        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase, sourceView);

        // Compile-time bind of the predicate and the SET values, matching
        // real's compiling binder — a cross-collation comparison, a legacy-LOB
        // string-scalar argument and an unknown column all report here rather
        // than waiting for a row to reach the per-row resolver (so an empty
        // table and a module body at CREATE report them too).
        var targetTypeResolver = Selection.TargetColumnTypeResolver(context.Batch, table, sourceView);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(context.Batch, targetTypeResolver));

        BooleanExpression? where = null;
        PositionedCursorTarget? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
                positionedCursor = ParseWhereCurrentOf(context, table, [.. rawAssignments.Select(a => a.ColumnName)], sourceView);
            else
                where = Selection.ParseAndBindPredicate(context, targetTypeResolver);
        }

        CheckUpdatePermissions(context, targetName, table, sourceView, rawAssignments, where);

        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        // Seek the target when WHERE carries an indexable equality / range
        // (positioned UPDATE leaves where null, so it keeps the full scan). The
        // loop re-runs WHERE below, so the seek only narrows the rows considered.
        var rowSource = where is not null
            ? Selection.SeekMutationTarget(table, where, context.Batch) ?? table.Heap.EnumerateRowsWithAddress()
            : table.Heap.EnumerateRowsWithAddress();
        // Skip mode commits nothing (CommitUpdate returns early), so the walk
        // is pure cost — and running WHERE / SET against live rows can raise a
        // runtime error (a division by zero, a conversion failure) on behalf of
        // a statement that never ran. That matters at CREATE-time module
        // binding, where it would refuse a body real accepts. Everything the
        // bind needs — the target, the SET column ordinals, the predicate — was
        // resolved above.
        if (context.Batch.IsSkipping)
            rowSource = [];
        foreach (var (pageIndex, slotIndex, rowBytes) in rowSource)
        {
            // Positioned UPDATE (WHERE CURRENT OF): target only the row the
            // cursor is sitting on, identified by its stable heap address.
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, (pageIndex, slotIndex)))
                continue;

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
                        if (context.Batch.CurrentDatabase.Collation.Equals(sourceView.OutputColumns[v].Name, name.Leaf))
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
                    if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, name.Leaf))
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

        ApplyDmlTopCap(top, affected, context.Batch);

        // SI writer pre-flight: any row visible at our snapshot but
        // deleted by a concurrent committed tx (or in-flight foreign
        // delete) whose pre-delete payload matches WHERE is a conflict.
        // Msg 3960 fires before any heap mutation; auto-rolls back the SI
        // tx. Probe-confirmed against SQL Server 2025: UPDATE / DELETE on
        // an RC-deleted row that matches our snapshot raises 3960 even
        // though the live row is tombstoned. Skipped for positioned updates —
        // the cursor already fixed a single live row.
        if (positionedCursor is null)
            CheckSnapshotConflictOnTombstonedRows(context, table, where, sourceView);

        return CommitUpdate(context, table, affected, output, [.. assignments.Select(a => a.Ordinal)], sourceView);
    }

    /// <summary>
    /// The permission gate every single-target UPDATE passes through — the
    /// no-FROM form against a table or a view, and the join-view form once
    /// its SET list has named the base table. A read (a WHERE clause, or a
    /// SET expression that reads a column) additionally requires SELECT,
    /// checked first so that when both SELECT and UPDATE are missing the
    /// SELECT denial surfaces (probe M1); a constant-SET UPDATE with no
    /// WHERE reads nothing and needs only UPDATE (M1b).
    /// </summary>
    private static void CheckUpdatePermissions(
        ParserContext context,
        MultiPartName targetName,
        HeapTable table,
        View? sourceView,
        List<(string ColumnName, Expression Expr)> rawAssignments,
        BooleanExpression? where)
    {
        var updateSecurable = context.Batch.IsSkipping
            ? null
            : PermissionEnforcement.SecurableFor(context.Batch, targetName, (SchemaObject?)sourceView ?? table);
        if (updateSecurable is null || !PermissionEnforcement.Applies(context.Batch, context.Batch.DatabaseFor(updateSecurable)))
            return;

        if (updateSecurable is Synonym synonym)
        {
            // A synonym takes no column grants at all, so a reference
            // through one is checked object-grain against the synonym.
            if (where is not null || AnySetExpressionReadsColumn(rawAssignments, table, context.Batch))
                PermissionEnforcement.CheckSchemaObject(context.Batch, "SELECT", synonym);
            PermissionEnforcement.CheckSchemaObject(context.Batch, "UPDATE", synonym);
            return;
        }

        // Column-grain on a base table and a view alike: the WHERE +
        // SET-RHS columns require SELECT (checked first, per probe M1
        // ordering), each SET-target column requires UPDATE — first
        // inaccessible column → Msg 230 (or Msg 229 when the object is
        // wholly inaccessible for that permission). Through a view the
        // ordinals are the view's own, matching what
        // `GRANT UPDATE (col) ON <view>` stored.
        var read = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
        where?.VisitOperandExpressions(op => op.VisitColumnReferences(read.Add));
        foreach (var (_, expr) in rawAssignments)
            expr.VisitColumnReferences(read.Add);
        PermissionEnforcement.CheckColumns(context.Batch, Permission.Select, read);

        var assigned = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
        foreach (var (columnName, _) in rawAssignments)
            assigned.Add(columnName);
        PermissionEnforcement.CheckColumns(context.Batch, Permission.Update, assigned);
    }

    /// <summary>
    /// SI writer's tombstoned-slot pre-flight: walks the version chain
    /// dict for entries whose live heap slot is tombstoned, decodes each
    /// snapshot-visible historical payload, evaluates the UPDATE / DELETE
    /// WHERE predicate against it, and raises Msg 3960 (with auto-rollback)
    /// if any match. Closes the SI-vs-RC-deleted-row conflict path the
    /// regular live-heap iteration misses (heap iteration skips tombstoned
    /// slots). No-op for non-SI sessions and for sessions whose snapshot
    /// hasn't been allocated yet.
    /// </summary>
    private static void CheckSnapshotConflictOnTombstonedRows(ParserContext context, HeapTable table, BooleanExpression? where, View? sourceView)
    {
        var batch = context.Batch;
        if (batch.Connection.SessionIsolationLevel != System.Data.IsolationLevel.Snapshot)
            return;
        var snapshotXid = batch.Connection.CurrentTransaction?.SnapshotXid;
        if (snapshotXid is not { } sx)
            return;
        foreach (var kv in table.RowVersions)
        {
            if (!table.Heap.IsSlotTombstoned(kv.Key.PageIndex, kv.Key.SlotIndex))
                continue;
            var hist = Storage.VersionStore.ResolveTombstonedSlotForSnapshot(kv.Value, sx, batch.Connection.CurrentTransaction);
            if (hist is null)
                continue;
            var fullValues = DecodeFullRow(table, hist);
            EvaluateComputedColumns(table, fullValues, batch);
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues, batch))
                continue;
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
                                : fullValues[baseOrd];
                        }
                    }
                    throw SimulatedSqlException.InvalidColumnName(name);
                }
                for (var k = 0; k < table.Columns.Length; k++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(table.Columns[k].Name, name.Leaf))
                        return fullValues[k];
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }
            if (where is not null && where.Run(new RuntimeContext(Resolve, batch)) != true)
                continue;
            batch.Connection.CurrentTransaction?.Rollback();
            throw SimulatedSqlException.SnapshotIsolationUpdateConflict($"{Database.DefaultSchemaName}.{table.Name}", batch.DatabaseFor(table).Name);
        }
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
        // The alias form names its target through the FROM clause, so the write
        // is classified for a function body's Msg 443 here rather than at the
        // leading identifier.
        FunctionBodyShape.NoteTableWrite(context.Batch, "UPDATE", table);
        if (!context.Batch.IsSkipping)
        {
            // A joined UPDATE reads every FROM source (target + join sources);
            // real requires SELECT on each, checked before the UPDATE write
            // permission (probe M2). Additional-source reads inside a WHERE
            // subquery route through the standard Selection read-source sink.
            CheckJoinedReadSources(context.Batch, sources, targetIndex);
            PermissionEnforcement.CheckSchemaObject(context.Batch, "UPDATE", (SchemaObject?)sources[targetIndex].ViaSynonym ?? table);
        }

        // Alias-form UPDATE: table-IX wasn't pre-acquired because the target
        // wasn't yet known. Now that the FROM clause identified it, acquire
        // table-IX + the standard row-X-per-mutation will happen at the
        // mutation site (matching the simple-form UPDATE path).
        RejectIncorrectSetOptionsForWrite(table, context.Batch, "UPDATE");
        _ = context.Batch.AcquireDataLockIfApplicable(table, default, isWrite: true);

        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase);

        // Compile-time bind of the predicate and the SET values — see
        // ExecuteUpdateAgainstTable for why.
        var tupleTypeResolver = Selection.ColumnTypeResolverFor(sources);
        foreach (var (_, expr) in rawAssignments)
            UnresolvedCollation.RequireAssignable(expr.GetSqlType(context.Batch, tupleTypeResolver));

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.MoveNextRequired();
            where = Selection.ParseAndBindPredicate(context, tupleTypeResolver);
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

        ApplyDmlTopCap(top, affected, context.Batch);

        return CommitUpdate(context, table, affected, output, [.. assignments.Select(a => a.Ordinal)], sourceView: null);
    }

    /// <summary>
    /// Trims an affected-row list to the DML <c>TOP</c> cap in place. Always
    /// resolves the limit (even when the list is empty) so a bad value
    /// (negative / non-integer / out-of-range percent) raises before commit,
    /// matching SQL Server's rejection with no rows changed. No-op when
    /// <paramref name="top"/> is null.
    /// </summary>
    private static void ApplyDmlTopCap<T>(Selection.DmlTopLimit? top, List<T> rows, BatchContext batch)
    {
        if (top is not { } limit)
            return;
        var cap = Selection.ResolveDmlTopCap(limit, rows.Count, batch);
        if (cap < rows.Count)
            rows.RemoveRange(cap, rows.Count - cap);
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
        OutputProjection? output,
        IReadOnlyList<int> updatedColumnOrdinals,
        View? sourceView = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Update);

        if (affected.Count == 0)
        {
            // AFTER triggers still fire when the statement matched nothing —
            // real runs the body with empty INSERTED / DELETED and @@ROWCOUNT
            // 0, and UPDATE(col) still reports the SET-clause columns
            // (probe-confirmed for UPDATE / DELETE / INSERT…SELECT / MERGE).
            FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
            return output is null ? new SimulatedNonQuery(0) : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, Array.Empty<byte[]>());
        }

        // SNAPSHOT isolation write-conflict: each affected row must have
        // a live version no newer than my snapshot, otherwise Msg 3960
        // fires and the SI tx auto-rolls-back. Probe-confirmed against
        // SQL Server 2025.
        if (context.Batch.Connection.SessionIsolationLevel == System.Data.IsolationLevel.Snapshot)
        {
            foreach (var (pageIndex, slotIndex, _, _) in affected)
                Storage.VersionStore.CheckSnapshotUpdateConflict(context.Batch, table, (pageIndex, slotIndex));
        }

        if (insteadOfActive)
        {
            FireInsteadOfUpdateTrigger(context, table, sourceView, affected);
            return output is null || output.HasTarget
                ? new SimulatedNonQuery(affected.Count)
                : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, ProjectMutationOutput(affected, output));
        }

        EnforceKeyConstraintsForUpdate(table, affected);
        EnforceUniqueIndexesForUpdate(table, affected, context.Batch);

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
        var lockableTable = IsLockableTable(table);
        // Capture pre-update payloads so the version-store CaptureWrite call
        // after UpdateAt can pair each row's stable Rid with its pre-update
        // bytes.
        var oldBytesPerAffected = Storage.VersionStore.IsVersioningEnabled(context.Batch.DatabaseFor(table)) && lockableTable
            ? new byte[affected.Count][]
            : null;
        if (oldBytesPerAffected is not null)
        {
            for (var i = 0; i < affected.Count; i++)
            {
                var (pageIndex, slotIndex, _, _) = affected[i];
                oldBytesPerAffected[i] = table.Heap.ReadSlotBytes(pageIndex, slotIndex) ?? [];
            }
        }
        for (var i = 0; i < affected.Count; i++)
        {
            var (pageIndex, slotIndex, fullNew, _) = affected[i];
            if (lockableTable)
                context.Batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive);
            var newImage = RowEncoder.EncodeRow(table.StoredColumns, ProjectStoredValues(table, fullNew), table.Heap);
            // The row-X above probed the key ranges the row is leaving; a key
            // change can also carry it INTO a range some SERIALIZABLE reader
            // holds, which only the post-update image reveals.
            if (lockableTable)
                context.Batch.ProbeKeyRangesForWrite(table, newImage);
            table.Heap.UpdateAt(pageIndex, slotIndex, newImage, undoLog, ReclaimSuperseded(table, context));
            if (lockableTable && oldBytesPerAffected is not null)
                Storage.VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytesPerAffected[i], Storage.VersionWriteKind.Update);
        }

        // Indexed-view maintenance: re-evaluate any unique-indexed view over
        // this table on the post-update base rows and enforce uniqueness
        // (Msg 2601). A violation rolls the statement back via the undo log.
        context.Batch.Connection.Simulation.EnforceIndexedViews(table, context.Batch);

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
                FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows);
            }
        }
        FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
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
        var lockableHistory = IsLockableTable(historyTable);
        foreach (var (_, _, _, oldFull) in affected)
        {
            if (oldFull is null)
                continue;
            var historyRow = new SqlValue[oldFull.Length];
            Array.Copy(oldFull, historyRow, oldFull.Length);
            historyRow[period.EndOrdinal] = stampedNow;
            var (newPage, newSlot) = historyTable.Heap.Insert(RowEncoder.EncodeRow(historyTable.StoredColumns, ProjectStoredValues(historyTable, historyRow), historyTable.Heap), undoLog);
            if (lockableHistory)
                context.Batch.AcquireRowLockTxScoped(historyTable, newPage, newSlot, LockMode.Exclusive);
        }
    }

    /// <summary>
    /// Lockability predicate for heap-table mutation sites: row-X acquire
    /// only applies to tables that participate in cross-connection
    /// contention. Table variables, local temp tables, and system tables
    /// all bypass.
    /// </summary>
    internal static bool IsLockableTable(HeapTable table) =>
        !table.IsTableVariable
        && !BatchContext.IsLocalTempName(table.Name)
        && !Simulation.SystemHeapTables.Values.Contains(table);

    /// <summary>
    /// Whether a superseding UPDATE / DELETE may reclaim the old row's off-row
    /// LOB chains when its undo entry commits. True exactly when no
    /// <see cref="HistoricalVersion"/> will pin those chains — the
    /// inverse of <see cref="VersionStore.WillCaptureVersions"/>. For
    /// the versioned case the chains are instead reclaimed by version-store GC
    /// once no snapshot needs them.
    /// </summary>
    internal static bool ReclaimSuperseded(HeapTable table, ParserContext context) =>
        !Storage.VersionStore.WillCaptureVersions(context.Batch.DatabaseFor(table), table);

    private static List<byte[]> ProjectMutationOutput(
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        OutputProjection output)
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
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        IReadOnlyList<int> updatedColumnOrdinals)
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
            insertedRows, deletedRows, affectedRowCount: affected.Count, updatedColumnOrdinals);
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
    /// <summary>
    /// Parse-time column-type resolver for the UPDATE target, so a subquery in
    /// a SET expression can bind the target's columns
    /// (<c>SET alias = (SELECT MAX(v) FROM (VALUES (t.name),(t.goes_by)) x(v))</c>,
    /// the shape ORMs emit for GREATEST / LEAST). A qualified reference must
    /// name the target table; anything else falls through to the enclosing
    /// scope, which is null at statement level and raises Msg 207 there.
    /// </summary>
    private static SqlType ResolveUpdateTargetColumnType(HeapTable table, MultiPartName name, Func<MultiPartName, SqlType>? enclosing)
    {
        if (name.ImmediateQualifier is null || BuiltInToken.Equals(name.ImmediateQualifier, table.Name))
        {
            foreach (var column in table.Columns)
            {
                if (BuiltInToken.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
        }

        return enclosing is not null
            ? enclosing(name)
            : throw SimulatedSqlException.InvalidColumnName(name);
    }

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
                    if (database.Collation.Equals(sourceView.OutputColumns[i].Name, colName))
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
                    if (database.Collation.Equals(table.Columns[i].Name, colName))
                    {
                        columnOrdinal = i;
                        break;
                    }
                }
                if (columnOrdinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(colName);
            }

            RejectUnmodifiableSetTarget(table, columnOrdinal, database);
            assignments.Add((columnOrdinal, expr));
        }
        return assignments;
    }

    /// <summary>
    /// Refuses a SET target the storage engine owns: an IDENTITY column, a
    /// computed column, a <c>rowversion</c>, or a GENERATED ALWAYS period
    /// column. Shared by the plain and join-view SET-list resolvers so both
    /// report the same error for the same column.
    /// </summary>
    private static void RejectUnmodifiableSetTarget(HeapTable table, int columnOrdinal, Database database)
    {
        var column = table.Columns[columnOrdinal];
        if (column.Identity is not null)
            throw SimulatedSqlException.CannotUpdateIdentityColumn(column.Name);
        if (column.Computed is not null)
            throw SimulatedSqlException.ColumnCannotBeModified(column.Name);
        if (column.Type == SqlType.RowVersion)
            throw SimulatedSqlException.CannotUpdateTimestampColumn();
        if (column.GeneratedAs != GeneratedAlwaysAsRow.None)
            throw SimulatedSqlException.CannotUpdateGeneratedAlways(QualifyTableName(table, database));
    }

    /// <summary>
    /// SELECT-checks every FROM source of a joined UPDATE / DELETE that is
    /// backed by a real table (the target and each join source) — real requires
    /// SELECT on each read source (probe M2). Derived-table / view sources with
    /// no backing table are skipped (their inner reads route through the
    /// standard <see cref="Parser.Selection"/> read-source sink).
    /// </summary>
    private static void CheckJoinedReadSources(BatchContext batch, FromSource[] sources, int targetIndex)
    {
        // Non-target join sources first, then the target — real surfaces the
        // additional-source SELECT denial ahead of the target's (probe M2,
        // reconfirmed against the reference: a joined UPDATE with neither
        // target nor source SELECT granted denies the source).
        for (var i = 0; i < sources.Length; i++)
        {
            if (i != targetIndex)
                CheckSourceSelect(batch, sources[i]);
        }
        CheckSourceSelect(batch, sources[targetIndex]);
    }

    /// <summary>
    /// SELECT-checks one joined FROM source against the securable it was written
    /// as — the synonym when the reference arrived through one, otherwise the
    /// backing table. Sources with neither (derived tables, views) are skipped;
    /// their inner reads route through the standard read-source sink.
    /// </summary>
    private static void CheckSourceSelect(BatchContext batch, FromSource source)
    {
        if (source.ViaSynonym is { } synonym)
            PermissionEnforcement.CheckSchemaObject(batch, "SELECT", synonym);
        else if (source.BackingTable is { } backing)
            PermissionEnforcement.CheckSchemaObject(batch, "SELECT", backing);
    }

    /// <summary>
    /// Whether any SET-list right-hand side references a column (i.e. the UPDATE
    /// reads the target). Detected by resolving each expression's static type
    /// with a probe resolver that flips a flag on the first column lookup;
    /// constants / parameters never touch the resolver. A resolution failure is
    /// treated conservatively as a read (the real execution surfaces the true
    /// error). Drives the read-implies-SELECT permission gate (probe M1c).
    /// </summary>
    private static bool AnySetExpressionReadsColumn(
        List<(string ColumnName, Expression Expr)> rawAssignments, HeapTable table, BatchContext batch)
    {
        var readsColumn = false;
        SqlType Resolve(MultiPartName name)
        {
            readsColumn = true;
            foreach (var column in table.Columns)
            {
                if (batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
            return SqlType.Int32;
        }
        foreach (var (_, expr) in rawAssignments)
        {
            try
            {
                _ = expr.GetSqlType(batch, Resolve);
            }
            catch (SimulatedSqlException)
            {
                return true;
            }
            if (readsColumn)
                return true;
        }
        return false;
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
                newValues[ci] = SqlValue.FromRowVersion(context.Batch.DatabaseFor(table).AllocateRowVersion());
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
    private static int FindMutationTargetIndex(Collation collation, FromSource[] sources, string leadingIdent, HeapTable? leadingTable)
    {
        for (var s = 0; s < sources.Length; s++)
        {
            if (sources[s].Qualifier is { } q && collation.Equals(q, leadingIdent))
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
    /// addresses are excluded from the heap-side scan. Mass-shift updates
    /// (<c>UPDATE t SET k = k + 1</c>) work correctly because (a) only
    /// compares new-vs-new among affected rows — overlap with the pre-shift
    /// snapshot via (b) only fires when a non-affected row's existing key
    /// genuinely collides with the new value (a true violation).
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
        var affectedKeys = new AffectedKeyIndex?[table.KeyConstraints.Count];

        for (var i = 0; i < affected.Count; i++)
        {
            var myStored = storedSnapshots[i];

            for (var c = 0; c < table.KeyConstraints.Count; c++)
            {
                var constraint = table.KeyConstraints[c];
                if (constraint.IsDisabled)
                    continue;
                if (!KeyTupleMoved(constraint.StorageOrdinals, myStored, affected[i], table))
                    continue;

                var keyed = affectedKeys[c] ??= AffectedKeyIndex.Build(storedSnapshots, constraint.StorageOrdinals, participates: null);
                if (keyed.SharedByAnotherRow(i))
                    throw KeyConstraintViolation(table, constraint, myStored);

                if (TryPrepareKeySeek(table, constraint.StorageOrdinals, myStored, out var commons, out var probe))
                {
                    foreach (var (p, s, _) in HeapSeekCache.For(table.Heap)
                        .MatchingRows(table.Heap, storedColumns, constraint.StorageOrdinals, commons, probe))
                    {
                        if (!affectedAddrs.Contains((p, s)))
                            throw KeyConstraintViolation(table, constraint, myStored);
                    }
                    continue;
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
                        throw KeyConstraintViolation(table, constraint, myStored);
                }
            }
        }
    }

    /// <summary>
    /// UPDATE-time counterpart to <see cref="EnforceUniqueIndexes"/>:
    /// walks each <see cref="HeapTable.Indexes"/> UNIQUE entry and raises
    /// Msg 2601 on the first key collision among updated rows or against
    /// other (non-affected) heap rows. Filter-aware in the same shape as
    /// the INSERT path — rows excluded by an index's <c>Index.Filter</c>
    /// are skipped on both sides of the comparison.
    /// </summary>
    private static void EnforceUniqueIndexesForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected, BatchContext batch)
    {
        if (table.Indexes.Count == 0)
            return;

        var hasUnique = false;
        foreach (var ix in table.Indexes)
        {
            if (ix.IsUnique && !ix.IsDisabled)
            {
                hasUnique = true;
                break;
            }
        }
        if (!hasUnique)
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
        SqlValue[]? existingRowValues = null;
        var qualifiedTableName = $"{Database.DefaultSchemaName}.{table.Name}";
        var affectedKeys = new AffectedKeyIndex?[table.Indexes.Count];

        for (var i = 0; i < affected.Count; i++)
        {
            var myStored = storedSnapshots[i];
            var myFull = affected[i].FullNew;

            for (var x = 0; x < table.Indexes.Count; x++)
            {
                var index = table.Indexes[x];
                if (!index.IsUnique || index.IsDisabled)
                    continue;
                if (index.Filter is { } rowFilter)
                {
                    if (Simulation.EvaluateIndexFilter(rowFilter, table, myFull, batch) != true)
                        continue;
                }
                // A standing key skips its own check (see KeyTupleMoved) — for an
                // unfiltered index only, which is why this is the else arm: a
                // filter can read columns outside the key, so a row whose key
                // stood still can still have moved into the filtered set and
                // collided there.
                else if (!KeyTupleMoved(index.KeyStorageOrdinals, myStored, affected[i], table))
                {
                    continue;
                }

                // A filtered index counts only the affected rows inside its set,
                // so the filter is evaluated once per row here rather than once
                // per pair as the walk this replaces did.
                var keyed = affectedKeys[x] ??= AffectedKeyIndex.Build(
                    storedSnapshots,
                    index.KeyStorageOrdinals,
                    index.Filter is not { } setFilter
                        ? null
                        : FilterMembership(table, setFilter, affected, batch));
                if (keyed.SharedByAnotherRow(i))
                    throw UniqueIndexViolation(index, qualifiedTableName, myStored);

                if (TryPrepareKeySeek(table, index.KeyStorageOrdinals, myStored, out var commons, out var probe))
                {
                    foreach (var (p, s, bytes) in HeapSeekCache.For(table.Heap)
                        .MatchingRows(table.Heap, storedColumns, index.KeyStorageOrdinals, commons, probe))
                    {
                        if (affectedAddrs.Contains((p, s)))
                            continue;
                        if (index.Filter is { } seekFilter
                            && Simulation.EvaluateIndexFilter(seekFilter, table, DecodeFullRow(table, bytes, ref existingRowValues), batch) != true)
                        {
                            continue;
                        }

                        throw UniqueIndexViolation(index, qualifiedTableName, myStored);
                    }
                    continue;
                }

                foreach (var (p, s, bytes) in table.Heap.EnumerateRowsWithAddress())
                {
                    if (affectedAddrs.Contains((p, s)))
                        continue;
                    if (index.Filter is { } filter
                        && Simulation.EvaluateIndexFilter(filter, table, DecodeFullRow(table, bytes, ref existingRowValues), batch) != true)
                    {
                        continue;
                    }

                    var allEqual = true;
                    for (var k = 0; k < index.KeyStorageOrdinals.Length; k++)
                    {
                        var ord = index.KeyStorageOrdinals[k];
                        var existing = RowDecoder.DecodeColumn(storedColumns, bytes, ord, lobStore);
                        if (!existing.Equals(myStored[ord]))
                        {
                            allEqual = false;
                            break;
                        }
                    }
                    if (allEqual)
                        throw UniqueIndexViolation(index, qualifiedTableName, myStored);
                }
            }
        }
    }

    /// <summary>
    /// The affected rows' post-update key tuples over one constraint's or index's
    /// key columns, plus how many rows carry each distinct tuple. Answers "does
    /// another affected row share this row's key?" with a hash probe, in place of
    /// comparing every affected row against every other — the difference between
    /// linear and quadratic on a statement that moves the key on every row it
    /// touches (measured at 2 582 ms for 20 000 rows before, ~90 ms after).
    /// <para>
    /// The tuples are compared exactly as the walk compared them:
    /// <see cref="SqlValueKey"/> delegates per component to
    /// <see cref="SqlValue.Equals(SqlValue)"/> and folds two NULLs together,
    /// which is UNIQUE's NULLs-collide rule, and hashes to agree. Unlike the
    /// heap-side seek, whose buckets drop NULL keys, this index carries them —
    /// so a NULL-bearing key still finds its duplicate among the affected rows.
    /// </para>
    /// </summary>
    private sealed class AffectedKeyIndex(SqlValueKey[] keys, Dictionary<SqlValueKey, int> occurrences)
    {
        private readonly SqlValueKey[] keys = keys;
        private readonly Dictionary<SqlValueKey, int> occurrences = occurrences;

        /// <summary>
        /// Builds the index over <paramref name="storedSnapshots"/>. A non-null
        /// <paramref name="participates"/> restricts it to the rows inside a
        /// filtered index's set; excluded rows are neither counted nor probeable,
        /// which is sound because a row outside the set never reaches the probe.
        /// </summary>
        public static AffectedKeyIndex Build(SqlValue[][] storedSnapshots, int[] storageOrdinals, bool[]? participates)
        {
            var keys = new SqlValueKey[storedSnapshots.Length];
            var occurrences = new Dictionary<SqlValueKey, int>(storedSnapshots.Length);
            for (var i = 0; i < storedSnapshots.Length; i++)
            {
                if (participates is not null && !participates[i])
                    continue;
                var components = new SqlValue[storageOrdinals.Length];
                for (var k = 0; k < storageOrdinals.Length; k++)
                    components[k] = storedSnapshots[i][storageOrdinals[k]];
                keys[i] = new SqlValueKey(components);
                occurrences[keys[i]] = occurrences.TryGetValue(keys[i], out var seen) ? seen + 1 : 1;
            }

            return new AffectedKeyIndex(keys, occurrences);
        }

        /// <summary>
        /// Whether an affected row other than <paramref name="row"/> carries the
        /// same key. Row <paramref name="row"/> counts itself, so more than one
        /// occurrence means a collision within the statement.
        /// </summary>
        public bool SharedByAnotherRow(int row) => this.occurrences[this.keys[row]] > 1;
    }

    /// <summary>
    /// Which affected rows fall inside <paramref name="filter"/>'s set, evaluated
    /// against each row's post-update values — a filtered unique index counts
    /// only its own members.
    /// </summary>
    private static bool[] FilterMembership(
        HeapTable table,
        BooleanExpression filter,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        BatchContext batch)
    {
        var membership = new bool[affected.Count];
        for (var i = 0; i < affected.Count; i++)
            membership[i] = Simulation.EvaluateIndexFilter(filter, table, affected[i].FullNew, batch) == true;
        return membership;
    }

    /// <summary>
    /// Whether the UPDATE moved this row's key tuple over
    /// <paramref name="storageOrdinals"/>. A row whose key stood still needs no
    /// uniqueness check of its own: it was unique before the statement,
    /// non-affected rows don't change, and a collision with a row whose key
    /// <i>did</i> move surfaces when that row is checked (every affected row
    /// stays a comparison target either way). That skip is what keeps a bulk
    /// UPDATE which never touches the key from building an
    /// <see cref="AffectedKeyIndex"/> or seeking at all — measured at 2.2 s for
    /// 20 000 rows on a keyed table against 33 ms on a keyless one before it.
    /// <para>
    /// The pre-update key comes from <paramref name="row"/>'s <c>FullOld</c>
    /// when the caller captured whole old rows (an OUTPUT clause, a trigger,
    /// MERGE's matched updates), and otherwise straight off the row's heap slot:
    /// validation runs before the rewrite phase, so the slot still holds the old
    /// bytes, and only the key columns need decoding. The plain UPDATE path
    /// captures nothing, which is the shape that matters here, so reading the
    /// slot is what makes the skip fire at all rather than a no-op.
    /// A negative page index is the sentinel address of a row that doesn't exist
    /// yet (MERGE's pending inserts) — no pre-update state, so it counts as
    /// moved and gets the full check, as does a slot that can't be read.
    /// </para>
    /// </summary>
    private static bool KeyTupleMoved(
        int[] storageOrdinals,
        SqlValue[] newStored,
        (int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld) row,
        HeapTable table)
    {
        if (row.FullOld is { } fullOld)
        {
            var oldStored = ProjectStoredValues(table, fullOld);
            foreach (var ordinal in storageOrdinals)
            {
                if (!newStored[ordinal].Equals(oldStored[ordinal]))
                    return true;
            }
            return false;
        }

        if (row.PageIndex < 0 || table.Heap.ReadSlotBytes(row.PageIndex, row.SlotIndex) is not { } oldBytes)
            return true;

        foreach (var ordinal in storageOrdinals)
        {
            if (!newStored[ordinal].Equals(RowDecoder.DecodeColumn(table.StoredColumns, oldBytes, ordinal, table.Heap)))
                return true;
        }
        return false;
    }
}
