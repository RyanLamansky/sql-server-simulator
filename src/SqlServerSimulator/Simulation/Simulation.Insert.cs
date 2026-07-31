using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses the INSERT preamble (<c>INTO</c> keyword, table name, optional
    /// column list, and VALUES tuples) and writes the resulting rows to the
    /// destination table's heap.
    /// </summary>
    private static SimulatedStatementOutcome ParseInsert(ParserContext context)
    {
        context.MoveNextRequired();
        var top = Selection.ParseDmlTopClause(context);
        if (context.Token is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        context.Batch.RejectCrossDatabaseMutation(destinationName);

        // Advance past the target name so the optional WITH (hint …) clause
        // has a token to peek at. INSERT accepts the WITH form only — the
        // legacy bare-paren form is unambiguously a column list here
        // (probe-confirmed: `INSERT t (TABLOCK) VALUES …` raises Msg 207
        // "Invalid column name 'TABLOCK'"). Table-variable targets reject
        // hints entirely: real SQL Server raises Msg 156 near 'with'; the
        // simulator falls through to Msg 102 at the column-list / VALUES
        // dispatch since we don't call the hint parser for `@t`.
        context.MoveNextRequired();
        if (!BatchContext.IsTableVariableName(destinationName.Leaf))
            Selection.ValidateDmlTargetHints(Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false));

        if (context.Batch.TryResolveView(destinationName, out var destinationView))
            return ProcessViewInsert(destinationView, context, top, destinationName);
        if (!context.Batch.TryResolveTable(destinationName, out var destinationTable))
        {
            throw BatchContext.IsTableVariableName(destinationName.Leaf)
                ? SimulatedSqlException.MustDeclareTableVariable(destinationName.Leaf)
                : context.Batch.UnresolvableObjectName(destinationName);
        }
        if (destinationTable.IsTableValuedParameter)
            throw SimulatedSqlException.TableValuedParameterIsReadOnly(destinationName.Leaf);
        if (!context.Batch.IsSkipping)
            PermissionEnforcement.CheckObject(context.Batch, "INSERT", destinationTable.ObjectId, destinationTable.SchemaId, destinationTable.Name, destinationName.ImmediateQualifier ?? Database.DefaultSchemaName);
        // Phase 1b: acquire table-IX on the INSERT target (escalates to
        // table-X via TABLOCK*); row-X is taken per inserted row in
        // ProcessHeapInsert.
        _ = context.Batch.AcquireDataLockIfApplicable(destinationTable, default, isWrite: true);
        return ProcessHeapInsert(destinationTable, context, top, destinationName);
    }

    /// <summary>
    /// INSERT through a view. An attached INSTEAD OF INSERT trigger
    /// pre-empts the heap-write path entirely — the trigger body becomes
    /// responsible for any side effects and INSERTED is populated with
    /// source-provided values shaped to <see cref="View.OutputColumns"/>.
    /// Otherwise the view's updatability shape gates routing: an updatable
    /// view delegates to <see cref="ProcessHeapInsert"/> against the view's
    /// <see cref="View.BaseTable"/> with column-name lookups translated
    /// through <see cref="View.BaseColumnOrdinals"/>; a non-updatable
    /// view raises <strong>Msg 4403</strong> / <strong>Msg 4405</strong>
    /// from <see cref="View.RejectionReason"/>. OUTPUT with a view target
    /// is rejected at the inner site (NotSupportedException).
    /// </summary>
    private static SimulatedStatementOutcome ProcessViewInsert(View destinationView, ParserContext context, Selection.DmlTopLimit? top, MultiPartName destinationName)
    {
        if (!context.Batch.IsSkipping)
            PermissionEnforcement.CheckObject(context.Batch, "INSERT", destinationView.ObjectId, destinationView.SchemaId, destinationView.Name, destinationView.Schema.Name);
        return ProcessViewInsertCore(destinationView, context, top, destinationName);
    }

    private static SimulatedStatementOutcome ProcessViewInsertCore(View destinationView, ParserContext context, Selection.DmlTopLimit? top, MultiPartName destinationName) =>
        HasInsteadOfTrigger(context.Batch, destinationView, TriggerActions.Insert)
            ? ProcessInsteadOfInsertOnView(destinationView, context, top)
            : destinationView.BaseTable is { } baseTable
                ? ProcessHeapInsert(baseTable, context, top, destinationName, destinationView)
                : throw (destinationView.RejectionReason == ViewUpdatabilityRejection.MultipleSources
                    ? SimulatedSqlException.ViewUpdateAffectsMultipleTables($"{destinationView.Schema.Name}.{destinationView.Name}")
                    : SimulatedSqlException.CannotUpdateNonUpdatableView($"{destinationView.Schema.Name}.{destinationView.Name}"));

    /// <summary>
    /// INSERT into a view whose INSTEAD OF INSERT trigger replaces the
    /// underlying DML. INSERTED is shaped to <see cref="View.OutputColumns"/>;
    /// unspecified columns get the column type's typed default (per the
    /// probe — INSTEAD OF triggers see source-provided values verbatim,
    /// not values computed via DEFAULT / IDENTITY because the view itself
    /// has no such metadata to run). The trigger body is responsible for
    /// any actual heap writes; this path simply fires the trigger and
    /// returns the would-be affected row count.
    /// </summary>
    private static SimulatedNonQuery ProcessInsteadOfInsertOnView(View destinationView, ParserContext context, Selection.DmlTopLimit? top)
    {
        var viewColumns = destinationView.OutputColumns;

        HeapColumn[] destinationColumns;
        if (context.Token is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                var columnName = column.Value;
                var resolved = ResolveViewColumnForInsteadOf(context.Batch.CurrentDatabase.Collation, columnName, viewColumns);
                usedColumns.Add(resolved);

                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            destinationColumns = [.. usedColumns];
            context.MoveNextRequired();
        }
        else
        {
            destinationColumns = viewColumns;
        }

        if (context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"INSERT … OUTPUT through a view ('{destinationView.Schema.Name}.{destinationView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");

        var sourceRows = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Values } => EvaluateValuesTuples(context),
            ReservedKeyword { Keyword: Keyword.Select } => ExecuteSelectSource(context, destinationColumns.Length),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        ApplyDmlTopCap(top, sourceRows, context.Batch);

        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(sourceRows.Count);

        var insertedRows = new List<SqlValue[]>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
            context.Batch.BumpRowStamp();
            var rowValues = new SqlValue[viewColumns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(viewColumns[i].Type);
            for (var i = 0; i < destinationColumns.Length; i++)
            {
                var targetColumn = destinationColumns[i];
                var ordinal = -1;
                for (var j = 0; j < viewColumns.Length; j++)
                {
                    if (ReferenceEquals(viewColumns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }
                rowValues[ordinal] = CoerceForInsert(sourceRow[i], targetColumn.Type);
            }
            insertedRows.Add(rowValues);
        }

        _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
            context.Batch, destinationView, TriggerActions.Insert,
            viewColumns, insertedRows, deletedRows: null,
            affectedRowCount: sourceRows.Count);

        return new SimulatedNonQuery(sourceRows.Count);
    }

    /// <summary>
    /// Resolves an INSERT column reference against a view's OutputColumns
    /// for the INSTEAD OF INSERT path. Unlike <see cref="ResolveInsertTargetColumn"/>,
    /// derived columns (BaseColumnOrdinals[i] = -1) are still writable
    /// here — the trigger body, not the simulator's heap writer, decides
    /// what to do with them.
    /// </summary>
    private static HeapColumn ResolveViewColumnForInsteadOf(Collation collation, string columnName, HeapColumn[] viewColumns)
    {
        for (var i = 0; i < viewColumns.Length; i++)
        {
            if (collation.Equals(viewColumns[i].Name, columnName))
                return viewColumns[i];
        }
        throw SimulatedSqlException.InvalidColumnName(columnName);
    }

    /// <summary>
    /// INSERT processor. Parses the column subset, optional <c>OUTPUT</c>
    /// clause, and VALUES tuples; converts each value token to a
    /// <see cref="SqlValue"/> typed to its target column; encodes each row
    /// via <see cref="RowEncoder"/> and appends the bytes to
    /// <paramref name="destinationTable"/>'s heap. When <c>OUTPUT</c> is
    /// present, the projected per-row results stream out as a
    /// <see cref="SimulatedSqlResultSet"/> (consumed by
    /// <c>ExecuteReader</c>); otherwise a plain <see cref="SimulatedNonQuery"/>
    /// is returned.
    /// </summary>
    private static SimulatedStatementOutcome ProcessHeapInsert(HeapTable destinationTable, ParserContext context, Selection.DmlTopLimit? top, MultiPartName destinationName, View? destinationView = null)
    {
        RejectDisabledClusteredIndex(destinationTable);
        // Direct INSERT into a history sibling is rejected — history rows
        // are populated only by the engine via UPDATE / DELETE on the parent.
        if (destinationTable.IsHistoryTable)
            throw SimulatedSqlException.CannotInsertIntoTemporalHistoryTable(QualifyTableName(destinationTable, context));

        // INSTEAD OF INSERT on the table target replaces the heap-write
        // path entirely: identity allocation is skipped (the column shows
        // the type's typed default in INSERTED — probe-confirmed), CHECK /
        // NOT NULL / key constraints aren't enforced, and AFTER triggers
        // don't run. The trigger body is responsible for any side effects.
        // A view target with an INSTEAD OF trigger is routed through
        // ProcessInsteadOfInsertOnView before ever reaching this method.
        var insteadOfActive = destinationView is null
            && HasInsteadOfTrigger(context.Batch, destinationTable, TriggerActions.Insert);

        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Connection.IdentityInsertTable is string activeTable
            && context.Batch.CurrentDatabase.Collation.Equals(activeTable, destinationTable.Name);

        HeapColumn[] destinationColumns;
        if (context.Token is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                var columnName = column.Value;
                var tableColumn = ResolveInsertTargetColumn(context.Batch.CurrentDatabase.Collation, columnName, destinationTable, destinationView);
                if (tableColumn.Computed is not null)
                    throw SimulatedSqlException.ColumnCannotBeModified(tableColumn.Name);
                if (tableColumn.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotInsertExplicitTimestamp();
                if (tableColumn.GeneratedAs != GeneratedAlwaysAsRow.None)
                    throw SimulatedSqlException.CannotInsertExplicitGeneratedAlways(QualifyTableName(destinationTable, context));
                usedColumns.Add(tableColumn);

                var separator = context.GetNextRequired();
                if (separator is Operator { Character: ')' })
                    break;
                if (separator is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            destinationColumns = [.. usedColumns];

            context.MoveNextRequired();
        }
        else
        {
            // No column list: target every regular column (skip identity when
            // IDENTITY_INSERT is OFF and skip every computed column — neither
            // is a writable destination from the VALUES side; rowversion is
            // also auto-generated and never accepts explicit values). When
            // the target is a view, the implicit list is the view's writable
            // (non-derived) projected columns mapped to their base ordinals;
            // base columns the view doesn't project pick up their defaults
            // or implicit-NULL via the standard insert path.
            destinationColumns = destinationView is not null
                ? BuildImplicitInsertColumnsForView(destinationView, destinationTable, identityInsertOn)
                : (identityColumn is not null && !identityInsertOn)
                    ? [.. destinationTable.Columns.Where(c => c.Identity is null && c.Computed is null && c.Type != SqlType.RowVersion && c.GeneratedAs == GeneratedAlwaysAsRow.None)]
                    : [.. destinationTable.Columns.Where(c => c.Computed is null && c.Type != SqlType.RowVersion && c.GeneratedAs == GeneratedAlwaysAsRow.None)];
        }

        if (destinationView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"INSERT … OUTPUT through a view ('{destinationView.Schema.Name}.{destinationView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");

        var output = TryParseOutputClause(context, destinationTable, sourceColumnNames: null);
        RejectClientOutputOnTriggeredTarget(
            context.Batch, (SchemaObject?)destinationView ?? destinationTable, TriggerActions.Insert, destinationName.ToString(), output is { HasTarget: false });

        // OUTPUT combined with an INSERT … EXEC source is rejected outright
        // (Msg 483) — probe-confirmed. The check runs regardless of skip
        // state since it's a structural incompatibility.
        if (output is not null && context.Token is ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute })
            throw SimulatedSqlException.OutputClauseNotAllowedInInsertExec();

        // A VALUES source is parsed up front (before evaluation and before the
        // identity diagnostics) so per-cell DEFAULT keywords — legal only
        // inside INSERT … VALUES — are visible: an identity column receiving
        // DEFAULT raises Msg 339, and non-identity DEFAULT cells resolve to the
        // column default in the row-encode loop. SELECT / EXEC / DEFAULT VALUES
        // sources carry no DEFAULT keyword, so this stays null for them.
        List<Expression[]>? valueTuples = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Values })
        {
            // Collect the sequences the tuples reference so the Msg 11731 gate
            // below can compare them against the target's DEFAULT clauses.
            var savedCollector = context.SequenceCollector;
            var tupleSequences = new List<Schemas.Sequence>();
            context.SequenceCollector = tupleSequences;
            try
            {
                valueTuples = ParseValuesTuples(context, allowDefault: true);
            }
            finally
            {
                context.SequenceCollector = savedCollector;
            }

            RejectSequenceDefaultOutsideColumnList(valueTuples, tupleSequences, destinationTable, destinationColumns);
        }

        if (identityColumn is not null)
        {
            var identityListed = destinationColumns.Any(c => ReferenceEquals(c, identityColumn));
            // DEFAULT (per Msg 339's wording, also NULL) as an explicit identity
            // value is rejected before the IDENTITY_INSERT gate — probe-confirmed
            // to fire with IDENTITY_INSERT both ON and OFF.
            if (identityListed && valueTuples is not null && TupleColumnIsDefault(valueTuples, destinationColumns, identityColumn))
                throw SimulatedSqlException.DefaultOrNullNotAllowedForIdentity();
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        List<SqlValue[]> sourceRows;
        long[]? valueTupleStamps = null;
        if (valueTuples is not null)
        {
            sourceRows = EvaluateParsedTuples(valueTuples, context.Batch, out valueTupleStamps);
        }
        else if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
        {
            // `INSERT INTO t DEFAULT VALUES` — one row with every column
            // defaulted. Clearing the destination list routes every column
            // through the default / identity-allocation / implicit-NULL path
            // below, so a NOT NULL column with no default hits the same
            // constraint error an explicit all-defaults insert would.
            context.MoveNextRequired();
            if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            destinationColumns = [];
            sourceRows = [[]];
        }
        else
        {
            sourceRows = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Select } => ExecuteSelectSource(context, destinationColumns.Length),
                ReservedKeyword { Keyword: Keyword.Exec or Keyword.Execute } => ExecuteExecSource(context, destinationColumns.Length),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
        }

        ApplyDmlTopCap(top, sourceRows, context.Batch);
        // Keep the parsed tuples aligned 1:1 with sourceRows so per-cell DEFAULT
        // lookup by row index stays valid — TOP trims from the tail, matching
        // ApplyDmlTopCap's tail removal on sourceRows.
        if (valueTuples is not null && valueTuples.Count > sourceRows.Count)
            valueTuples.RemoveRange(sourceRows.Count, valueTuples.Count - sourceRows.Count);

        decimal? lastIdentityValue = null;
        var outputRows = output is null ? null : new List<byte[]>(sourceRows.Count);
        var hasInsertTriggers = !insteadOfActive && HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Insert);
        var triggerRows = (hasInsertTriggers || insteadOfActive) ? new List<SqlValue[]>(sourceRows.Count) : null;
        // Rows actually written. Equals sourceRows.Count unless an
        // IGNORE_DUP_KEY key dropped a duplicate, which real excludes from
        // rows-affected and @@ROWCOUNT alike (probe-confirmed).
        var insertedCount = 0;
        for (var rowIndex = 0; rowIndex < sourceRows.Count; rowIndex++)
        {
            var sourceRow = sourceRows[rowIndex];
            // Per-row evaluation context for the DEFAULT-clause expressions
            // below, so NEXT VALUE FOR in a DEFAULT advances once per row
            // inserted (probe-confirmed against SQL Server 2025).
            // For a VALUES source the row's stamp is *restored* rather than
            // bumped: all references to one sequence within a single row —
            // including a DEFAULT-clause one — must return the same value, so
            // the DEFAULT has to re-enter the stamp its tuple was evaluated
            // under and hit the per-row cache. Bumping here drew a second
            // value instead. SELECT / EXEC sources carry no per-tuple stamp
            // and keep the fresh bump.
            if (valueTupleStamps is not null && rowIndex < valueTupleStamps.Length)
                context.Batch.CurrentRowStamp = valueTupleStamps[rowIndex];
            else
                context.Batch.BumpRowStamp();
            var rowValues = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < rowValues.Length; i++)
                rowValues[i] = SqlValue.Null(destinationTable.Columns[i].Type);

            // Defaults run only for columns not in the destination column list:
            // when an INSERT supplies an explicit value (including explicit
            // NULL), the column's DEFAULT must not fire. This also keeps
            // sequence-advancing defaults (NEWSEQUENTIALID) from being burned
            // on rows that already provide the value.
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                var column = destinationTable.Columns[i];
                if (column.Default is null) continue;
                var listed = false;
                for (var j = 0; j < destinationColumns.Length; j++)
                {
                    if (ReferenceEquals(destinationColumns[j], column))
                    {
                        listed = true;
                        break;
                    }
                }
                if (listed) continue;
                var defaultValue = column.Default.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch));
                rowValues[i] = CoerceForInsert(defaultValue, column.Type);
            }

            for (var i = 0; i < destinationColumns.Length; i++)
            {
                var targetColumn = destinationColumns[i];
                var ordinal = -1;
                for (var j = 0; j < destinationTable.Columns.Length; j++)
                {
                    if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                    {
                        ordinal = j;
                        break;
                    }
                }

                // A DEFAULT keyword in this VALUES cell resolves exactly like an
                // omitted column: the column's DEFAULT constraint value, or NULL
                // when it has none (a NOT NULL no-default column then trips the
                // NULL check below with Msg 515). Identity + DEFAULT was already
                // rejected upstream with Msg 339, so it never reaches here.
                if (valueTuples is not null && i < valueTuples[rowIndex].Length && valueTuples[rowIndex][i] is Parser.Expressions.DefaultValueExpression)
                {
                    rowValues[ordinal] = targetColumn.Default is { } columnDefault
                        ? CoerceForInsert(columnDefault.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch)), targetColumn.Type)
                        : SqlValue.Null(targetColumn.Type);
                    continue;
                }

                var source = sourceRow[i];
                EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Connection);
                var coerced = CoerceForInsert(source, targetColumn.Type);
                rowValues[ordinal] = coerced;

                if (ReferenceEquals(targetColumn, identityColumn))
                {
                    var explicitValue = coerced.CoerceTo(SqlType.BigInt).AsInt64;
                    identityColumn.Identity!.ObserveExplicit(explicitValue);
                    lastIdentityValue = explicitValue;
                }
            }

            if (identityColumn is not null && !destinationColumns.Any(c => ReferenceEquals(c, identityColumn)))
            {
                if (insteadOfActive)
                {
                    // Probe-confirmed: INSTEAD OF INSERT doesn't allocate
                    // identity values — INSERTED's identity column shows
                    // the type's typed default (0 for int family) rather
                    // than the next sequential value. Identity columns
                    // are restricted to the integer family + decimal/numeric
                    // (Msg 11702 enforces this at CREATE TABLE).
                    rowValues[identityOrdinal] = CoerceForIdentity(0L, identityColumn);
                }
                else
                {
                    long generated;
                    try
                    {
                        generated = identityColumn.Identity!.GenerateNext();
                    }
                    catch (OverflowException)
                    {
                        throw SimulatedSqlException.IdentityOverflow(identityColumn.Type.ToString()!);
                    }

                    rowValues[identityOrdinal] = CoerceForIdentity(generated, identityColumn);
                    lastIdentityValue = generated;
                }
            }

            // Auto-generate rowversion for every row in a table that has one.
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                if (destinationTable.Columns[i].Type == SqlType.RowVersion)
                    rowValues[i] = SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion());
            }

            // Auto-populate period columns whose ordinals carry the GENERATED
            // ALWAYS markers: ROW START = the statement's frozen UtcNow, ROW
            // END = max datetime2 ('9999-12-31 23:59:59.9999999' —
            // DateTime.MaxValue at datetime2(7) precision). Gating on the
            // per-column GeneratedAs (not on the table-level SystemVersioning
            // link) matches real SQL Server's behavior probed 2026-05-13:
            // after ALTER TABLE … SET (SYSTEM_VERSIONING = OFF), the parent's
            // GENERATED ALWAYS column markers persist and INSERT continues to
            // auto-populate. The (former) history sibling never reaches here:
            // BuildHistoryTable strips the GENERATED markers, so its
            // PeriodColumns ordinals carry GeneratedAs.None and the gate
            // skips. (While versioning is still ON, INSERT into the history
            // sibling is rejected upstream by Msg 13559.)
            if (destinationTable.PeriodColumns is { } pc
                && destinationTable.Columns[pc.StartOrdinal].GeneratedAs != GeneratedAlwaysAsRow.None)
            {
                rowValues[pc.StartOrdinal] = SqlValue.FromDateTime2(destinationTable.Columns[pc.StartOrdinal].Type, context.Batch.CurrentStatement.UtcNow);
                rowValues[pc.EndOrdinal] = SqlValue.FromDateTime2(destinationTable.Columns[pc.EndOrdinal].Type, DateTime.MaxValue);
            }

            // Evaluate computed columns now — both persisted (whose result
            // gets stored) and non-persisted (whose result OUTPUT may reference
            // and which the row's full SqlValue array needs filled). Refs in
            // the expression bind only to stored columns thanks to Msg 1759
            // at CREATE TABLE. INSTEAD OF mode still evaluates computed
            // columns so INSERTED carries the would-be values (probe-
            // confirmed: c AS v * 2 appears in INSERTED with the computed
            // result, not NULL).
            EvaluateComputedColumns(destinationTable, rowValues, context.Batch);
            if (!insteadOfActive)
            {
                EnforceNotNull(destinationTable, rowValues);
                EnforceCheckConstraints(destinationTable, rowValues, context.Batch);
            }

            // WITH CHECK OPTION: the post-row-construction row must satisfy
            // every CHECK OPTION-bearing WHERE up the view chain. Fires
            // before the heap write so a violating INSERT leaves the heap
            // unchanged (matches SQL Server's "the statement has been
            // terminated" semantic on Msg 550).
            if (destinationView?.CheckOptionCheck is { } checkOption && !checkOption(rowValues, context.Batch))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            if (!context.Batch.IsSkipping)
            {
                if (!insteadOfActive)
                {
                    var storedValues = ProjectStoredValues(destinationTable, rowValues);
                    // A duplicate against an IGNORE_DUP_KEY key drops this row and
                    // the statement carries on: no heap write, no OUTPUT row, no
                    // trigger row, and it doesn't count toward rows-affected.
                    // Real does emit the identity value it burned on the way (the
                    // sequence is consumed whether or not the row lands), which is
                    // why lastIdentityValue is already assigned above.
                    if (EnforceKeyConstraints(destinationTable, storedValues, context.Batch) == RowKeyVerdict.SkipDuplicate
                        || EnforceUniqueIndexes(destinationTable, rowValues, storedValues, context.Batch) == RowKeyVerdict.SkipDuplicate)
                    {
                        continue;
                    }

                    EnforceOutgoingForeignKeys(destinationTable, [rowValues], context, "INSERT");
                    var (pageIndex, slotIndex) = destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, storedValues, destinationTable.Heap), destinationTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog);
                    if (IsLockableTable(destinationTable))
                    {
                        context.Batch.AcquireRowLockTxScoped(destinationTable, pageIndex, slotIndex, LockMode.Exclusive);
                        Storage.VersionStore.CaptureWrite(context.Batch, destinationTable, (pageIndex, slotIndex), oldRid: null, oldPayload: null, Storage.VersionWriteKind.Insert);
                    }
                }

                if (output is { } o)
                {
                    var projectedBytes = o.ProjectRow(insertedValues: rowValues, deletedValues: null);
                    if (projectedBytes is not null)
                        outputRows!.Add(projectedBytes);
                }

                triggerRows?.Add((SqlValue[])rowValues.Clone());
                insertedCount++;
            }
        }

        // Indexed-view maintenance: after all base rows are written, re-evaluate
        // any unique-indexed view over this table and enforce its uniqueness
        // (Msg 2601). Throwing here rolls the statement back via RunMutation's
        // undo log. Zero-cost when the table has no dependent indexed views.
        if (!context.Batch.IsSkipping && !insteadOfActive)
            context.Batch.Connection.Simulation.EnforceIndexedViews(destinationTable, context.Batch);

        // Per SQL Server: any INSERT updates SCOPE_IDENTITY/@@IDENTITY —
        // to the generated/explicit identity if the table has one, or to
        // NULL otherwise (resetting state from a prior identity insert).
        // Suppressed in skip mode so an un-taken IF branch's INSERT doesn't
        // perturb the session's identity history. INSTEAD OF mode doesn't
        // perturb SCOPE_IDENTITY either (no allocation happened).
        if (!context.Batch.IsSkipping && !insteadOfActive)
            context.Connection.LastIdentity = lastIdentityValue;

        // Trigger fire: INSTEAD OF replaces the would-be DML; AFTER fires
        // post-heap-write. Bodies throwing propagate up; the parent
        // statement's undo log unwinds the heap inserts.
        if (triggerRows is { Count: > 0 })
        {
            context.Connection.LastStatementRowCount = triggerRows.Count;
            if (insteadOfActive)
            {
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, destinationTable, TriggerActions.Insert,
                    destinationTable.Columns, insertedRows: triggerRows, deletedRows: null,
                    affectedRowCount: triggerRows.Count);
            }
            else
            {
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Insert,
                    insertedRows: triggerRows, deletedRows: null,
                    affectedRowCount: triggerRows.Count);
            }
        }

        // OUTPUT INTO @t directs rows to the target only — no result set
        // surfaces to the client (probe-confirmed). When the OUTPUT clause
        // has no target, the projected rows flow back as a result set.
        return output is { HasTarget: false } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(insertedCount);
    }

    /// <summary>
    /// Parses INSERT's <c>VALUES (…), (…)</c> source via the shared
    /// <c>ParseValuesTuples</c> helper, then eagerly evaluates each cell
    /// expression to a <see cref="SqlValue"/>. VALUES expressions can't
    /// reference columns; the column-resolver hook always raises
    /// <see cref="SimulatedSqlException.InvalidColumnName(string)"/>.
    /// </summary>
    /// <summary>
    /// <b>Msg 11731</b> — a multi-row <c>VALUES</c> constructor may not
    /// reference a sequence that one of the target's unlisted columns also
    /// defaults from, because the row's single sequence value would have to
    /// serve both and real declines to define that for a row constructor.
    /// The <em>single-row</em> form is legal and shares one value across both
    /// references (probe-confirmed) — hence the row-count gate.
    /// <para>The DEFAULT side is matched by peeling parens / casts to a bare
    /// <c>NEXT VALUE FOR</c>, which is the shape a sequence default takes in
    /// practice; a sequence buried in a larger default expression
    /// (<c>NEXT VALUE FOR s + 1</c>) isn't detected.</para>
    /// </summary>
    private static void RejectSequenceDefaultOutsideColumnList(
        List<Expression[]> valueTuples,
        List<Schemas.Sequence> tupleSequences,
        HeapTable? destinationTable,
        HeapColumn[] destinationColumns)
    {
        if (valueTuples.Count < 2 || tupleSequences.Count == 0 || destinationTable is null)
            return;

        foreach (var column in destinationTable.Columns)
        {
            if (column.Default is not { } defaultExpression || DefaultSequenceOf(defaultExpression) is not { } sequence)
                continue;
            if (!tupleSequences.Contains(sequence))
                continue;
            var listed = false;
            foreach (var destination in destinationColumns)
            {
                if (ReferenceEquals(destination, column))
                {
                    listed = true;
                    break;
                }
            }
            if (!listed)
                throw SimulatedSqlException.SequenceDefaultColumnMustBeListed();
        }
    }

    /// <summary>
    /// Peels parenthesization / pure conversions off a DEFAULT expression and
    /// returns the sequence it advances, or null when it isn't a bare
    /// <c>NEXT VALUE FOR</c>.
    /// </summary>
    private static Schemas.Sequence? DefaultSequenceOf(Expression defaultExpression)
    {
        var peeled = defaultExpression;
        while (peeled.PureConversionOperand is { } inner)
            peeled = inner;
        return (peeled as Parser.Expressions.NextValueFor)?.Sequence;
    }

    private static List<SqlValue[]> EvaluateValuesTuples(ParserContext context)
        => EvaluateParsedTuples(ParseValuesTuples(context), context.Batch, out _);

    /// <summary>
    /// Evaluates already-parsed <c>VALUES</c> tuples to <see cref="SqlValue"/>
    /// rows. A <see cref="Parser.Expressions.DefaultValueExpression"/> cell (a
    /// <c>DEFAULT</c> keyword, only produced when <c>ParseValuesTuples</c> is
    /// called with <c>allowDefault: true</c>) carries no value of its own; it
    /// gets a throwaway NULL placeholder here — the INSERT row encoder detects
    /// the sentinel by position and resolves it against the target column's
    /// default before this placeholder is ever read.
    /// </summary>
    private static List<SqlValue[]> EvaluateParsedTuples(List<Expression[]> tuples, BatchContext batch, out long[] tupleStamps)
    {
        var runtime = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch);
        var rows = new List<SqlValue[]>(tuples.Count);
        tupleStamps = new long[tuples.Count];
        var tupleIndex = 0;
        foreach (var tuple in tuples)
        {
            // Per-row stamp bump so NEXT VALUE FOR advances on the next
            // tuple (and dedupes across multiple NEXT VALUE FOR instances
            // within one tuple). See BatchContext.CurrentRowStamp.
            // The stamp is recorded so the row-encode loop can re-enter this
            // row's evaluation context rather than starting a new one — a
            // DEFAULT-clause NEXT VALUE FOR has to return the *same* value the
            // tuple's own reference did (probe-confirmed).
            batch.BumpRowStamp();
            tupleStamps[tupleIndex++] = batch.CurrentRowStamp;
            var values = new SqlValue[tuple.Length];
            for (var i = 0; i < tuple.Length; i++)
            {
                values[i] = tuple[i] is Parser.Expressions.DefaultValueExpression
                    ? SqlValue.Null(SqlType.Int32)
                    : tuple[i].Run(runtime);
            }
            rows.Add(values);
        }
        return rows;
    }

    /// <summary>
    /// Reports whether any parsed <c>VALUES</c> tuple supplies the
    /// <c>DEFAULT</c> keyword in the position mapped to
    /// <paramref name="column"/> (identified by reference in
    /// <paramref name="destinationColumns"/>). Used to raise Msg 339 when an
    /// identity column receives an explicit <c>DEFAULT</c>.
    /// </summary>
    private static bool TupleColumnIsDefault(List<Expression[]> tuples, HeapColumn[] destinationColumns, HeapColumn column)
    {
        var position = -1;
        for (var i = 0; i < destinationColumns.Length; i++)
        {
            if (ReferenceEquals(destinationColumns[i], column))
            {
                position = i;
                break;
            }
        }
        if (position < 0)
            return false;
        foreach (var tuple in tuples)
        {
            if (position < tuple.Length && tuple[position] is Parser.Expressions.DefaultValueExpression)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Parses and executes the <c>SELECT</c>-source side of <c>INSERT … SELECT</c>.
    /// Validates the projection-count vs insert-list count at parse time
    /// (Msg 120 / Msg 121, matching SQL Server's pre-execution diagnostic),
    /// then buffers the result into a list of rows so the existing per-row
    /// encode loop can run unchanged. Buffering also makes self-insert
    /// (<c>INSERT t SELECT … FROM t</c>) safe — the source materializes
    /// before any destination write.
    /// </summary>
    private static List<SqlValue[]> ExecuteSelectSource(ParserContext context, int expectedColumnCount)
    {
        var selection = Selection.Parse(context, depth: 0);
        if (!context.Batch.IsSkipping)
            PermissionEnforcement.CheckReadSources(context.Batch, selection.ReferencedSecurables, selection.ReadColumnsByObject);

        if (selection.Schema.Length < expectedColumnCount)
            throw SimulatedSqlException.InsertSelectListFewerThanInsertList();
        if (selection.Schema.Length > expectedColumnCount)
            throw SimulatedSqlException.InsertSelectListMoreThanInsertList();

        var resultSet = selection.Execute(context.Batch);
        var rows = new List<SqlValue[]>();
        foreach (var rowBytes in resultSet.RowBytes)
            rows.Add(RowDecoder.DecodeRow(resultSet.Schema, rowBytes));
        return rows;
    }

    /// <summary>
    /// Parses and executes the <c>EXEC</c>-source side of <c>INSERT … EXEC</c>.
    /// The EXEC clause (a stored-procedure call or a parenthesized dynamic-SQL
    /// batch) runs through the shared EXEC machinery; every result set it
    /// yields is appended to the destination, so a multi-<c>SELECT</c> body
    /// lands all of its rows (probe-confirmed) and <c>@@ROWCOUNT</c> becomes
    /// the total across result sets. Non-tabular outcomes (a pure-DML
    /// procedure) contribute no rows and the INSERT succeeds with zero rows.
    /// Each result set's column count must match the INSERT target's column
    /// list — a mismatch raises <strong>Msg 213</strong> (State 7); value
    /// coercion into the target types happens later in the shared per-row
    /// encode loop, so a bad value surfaces the usual conversion error.
    /// An <c>INSERT … EXEC</c> reached while another is draining on the same
    /// connection raises <strong>Msg 8164</strong> (nesting is disallowed).
    /// </summary>
    private static List<SqlValue[]> ExecuteExecSource(ParserContext context, int expectedColumnCount)
    {
        var batch = context.Batch;
        var connection = batch.Connection;
        if (connection.InsertExecActive)
            throw SimulatedSqlException.InsertExecCannotBeNested();

        var rows = new List<SqlValue[]>();
        connection.InsertExecActive = true;
        try
        {
            foreach (var outcome in connection.Simulation.ParseExec(batch))
            {
                if (outcome is not SimulatedSqlResultSet resultSet)
                    continue;
                if (resultSet.Schema.Length != expectedColumnCount)
                    throw SimulatedSqlException.InsertExecColumnCountMismatch();
                foreach (var rowBytes in resultSet.RowBytes)
                    rows.Add(RowDecoder.DecodeRow(resultSet.Schema, rowBytes));
            }
        }
        finally
        {
            connection.InsertExecActive = false;
        }
        return rows;
    }

    /// <summary>
    /// Looks up an INSERT column reference: against <paramref name="destinationTable"/>'s
    /// columns directly for a regular table target, or against
    /// <paramref name="destinationView"/>'s <see cref="View.OutputColumns"/>
    /// (translated through <see cref="View.BaseColumnOrdinals"/> to a base
    /// column) for a view target. A view-side reference that maps to a
    /// derived projection (ordinal <c>-1</c>) raises <strong>Msg 4406</strong>
    /// — the per-touched-column gate matching SQL Server's "INSERT through
    /// view with a derived field touched" rejection.
    /// </summary>
    private static HeapColumn ResolveInsertTargetColumn(Collation collation, string columnName, HeapTable destinationTable, View? destinationView)
    {
        if (destinationView is null)
        {
            return destinationTable.Columns.FirstOrDefault(c => collation.Equals(c.Name, columnName))
                ?? throw SimulatedSqlException.InvalidColumnName(columnName);
        }
        for (var i = 0; i < destinationView.OutputColumns.Length; i++)
        {
            if (collation.Equals(destinationView.OutputColumns[i].Name, columnName))
            {
                var baseOrd = destinationView.BaseColumnOrdinals[i];
                return baseOrd < 0
                    ? throw SimulatedSqlException.ViewDmlTouchesDerivedField($"{destinationView.Schema.Name}.{destinationView.Name}")
                    : destinationTable.Columns[baseOrd];
            }
        }
        throw SimulatedSqlException.InvalidColumnName(columnName);
    }

    /// <summary>
    /// Implicit-column-list expansion for an INSERT through a view (no
    /// explicit <c>(col, …)</c> list after the view name). The implicit
    /// list is the view's projected columns mapped to their base ordinals,
    /// filtered to writable shape (skip computed, skip rowversion, skip
    /// identity unless <c>SET IDENTITY_INSERT ON</c> is active on the base
    /// table). Derived view columns drop out of the implicit list since
    /// they can't be written anyway — INSERTs that omit them are valid
    /// (only an explicit name reference to a derived column would raise
    /// Msg 4406).
    /// </summary>
    private static HeapColumn[] BuildImplicitInsertColumnsForView(View destinationView, HeapTable baseTable, bool identityInsertOn)
    {
        var implicitList = new List<HeapColumn>();
        for (var i = 0; i < destinationView.OutputColumns.Length; i++)
        {
            var baseOrd = destinationView.BaseColumnOrdinals[i];
            if (baseOrd < 0)
                continue;
            var col = baseTable.Columns[baseOrd];
            if (col.Computed is not null || col.Type == SqlType.RowVersion)
                continue;
            if (col.Identity is not null && !identityInsertOn)
                continue;
            implicitList.Add(col);
        }
        return [.. implicitList];
    }

    private static string QualifyTableName(HeapTable table, ParserContext context) =>
        Simulation.QualifyTableName(table, context.CurrentDatabase);
}
