using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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
        if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationName = BatchContext.ParseObjectName(context, acceptTableVariable: true);

        return context.Batch.TryResolveView(destinationName, out var destinationView)
            ? ProcessViewInsert(destinationView, context)
            : !context.Batch.TryResolveTable(destinationName, out var destinationTable)
                ? throw (BatchContext.IsTableVariableName(destinationName.Leaf)
                    ? SimulatedSqlException.MustDeclareTableVariable(destinationName.Leaf)
                    : SimulatedSqlException.InvalidObjectName(destinationName))
                : destinationTable.IsTableValuedParameter
                    ? throw SimulatedSqlException.TableValuedParameterIsReadOnly(destinationName.Leaf)
                    : ProcessHeapInsert(destinationTable, context);
    }

    /// <summary>
    /// INSERT through a view: validates the view's updatability shape
    /// (raising <strong>Msg 4403</strong> / <strong>Msg 4405</strong> based
    /// on <see cref="View.RejectionReason"/> when the shape doesn't
    /// support DML) and routes to <see cref="ProcessHeapInsert"/> against
    /// the view's <see cref="View.BaseTable"/> with the view passed as
    /// <paramref name="destinationView"/> so column-name lookups translate
    /// through <see cref="View.BaseColumnOrdinals"/> and any
    /// <see cref="View.CheckOptionCheck"/> fires post-row-construction
    /// (Msg 550). OUTPUT with a view target isn't modeled — raises
    /// <see cref="NotSupportedException"/>.
    /// </summary>
    private static SimulatedStatementOutcome ProcessViewInsert(View destinationView, ParserContext context) =>
        destinationView.BaseTable is { } baseTable
            ? ProcessHeapInsert(baseTable, context, destinationView)
            : throw (destinationView.RejectionReason == ViewUpdatabilityRejection.MultipleSources
                ? SimulatedSqlException.ViewUpdateAffectsMultipleTables($"{destinationView.Schema.Name}.{destinationView.Name}")
                : SimulatedSqlException.CannotUpdateNonUpdatableView($"{destinationView.Schema.Name}.{destinationView.Name}"));

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
    private static SimulatedStatementOutcome ProcessHeapInsert(HeapTable destinationTable, ParserContext context, View? destinationView = null)
    {
        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Connection.IdentityInsertTable is string activeTable
            && Collation.Default.Equals(activeTable, destinationTable.Name);

        HeapColumn[] destinationColumns;
        if (context.GetNextRequired() is Operator { Character: '(' })
        {
            var usedColumns = new List<HeapColumn>();
            while (true)
            {
                if (context.GetNextRequired() is not StringToken column)
                    throw SimulatedSqlException.SyntaxErrorNear(context);

                var columnName = column.Value;
                var tableColumn = ResolveInsertTargetColumn(columnName, destinationTable, destinationView);
                if (tableColumn.Computed is not null)
                    throw SimulatedSqlException.ColumnCannotBeModified(tableColumn.Name);
                if (tableColumn.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotInsertExplicitTimestamp();
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
                    ? [.. destinationTable.Columns.Where(c => c.Identity is null && c.Computed is null && c.Type != SqlType.RowVersion)]
                    : [.. destinationTable.Columns.Where(c => c.Computed is null && c.Type != SqlType.RowVersion)];
        }

        if (identityColumn is not null)
        {
            var identityListed = destinationColumns.Any(c => ReferenceEquals(c, identityColumn));
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        if (destinationView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            throw new NotSupportedException($"INSERT … OUTPUT through a view ('{destinationView.Schema.Name}.{destinationView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");

        var output = TryParseOutputClause(context, destinationTable, sourceColumnNames: null);

        var sourceRows = context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Values } => EvaluateValuesTuples(context),
            ReservedKeyword { Keyword: Keyword.Select } => ExecuteSelectSource(context, destinationColumns.Length),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

        decimal? lastIdentityValue = null;
        var outputRows = output is null ? null : new List<byte[]>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
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

            // Auto-generate rowversion for every row in a table that has one.
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                if (destinationTable.Columns[i].Type == SqlType.RowVersion)
                    rowValues[i] = SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion());
            }

            // Evaluate computed columns now — both persisted (whose result
            // gets stored) and non-persisted (whose result OUTPUT may reference
            // and which the row's full SqlValue array needs filled). Refs in
            // the expression bind only to stored columns thanks to Msg 1759
            // at CREATE TABLE.
            EvaluateComputedColumns(destinationTable, rowValues, context.Batch);
            EnforceNotNull(destinationTable, rowValues);
            EnforceCheckConstraints(destinationTable, rowValues, context.Batch);

            // WITH CHECK OPTION: the post-row-construction row must satisfy
            // every CHECK OPTION-bearing WHERE up the view chain. Fires
            // before the heap write so a violating INSERT leaves the heap
            // unchanged (matches SQL Server's "the statement has been
            // terminated" semantic on Msg 550).
            if (destinationView?.CheckOptionCheck is { } checkOption && !checkOption(rowValues, context.Batch))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            if (!context.Batch.IsSkipping)
            {
                var storedValues = ProjectStoredValues(destinationTable, rowValues);
                EnforceKeyConstraints(destinationTable, storedValues);
                destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, storedValues, destinationTable.Heap), destinationTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog);

                if (output is { } o)
                {
                    var projectedBytes = o.ProjectRow(rowValues, sourceRowValues: null);
                    if (projectedBytes is not null)
                        outputRows!.Add(projectedBytes);
                }
            }
        }

        // Per SQL Server: any INSERT updates SCOPE_IDENTITY/@@IDENTITY —
        // to the generated/explicit identity if the table has one, or to
        // NULL otherwise (resetting state from a prior identity insert).
        // Suppressed in skip mode so an un-taken IF branch's INSERT doesn't
        // perturb the session's identity history.
        if (!context.Batch.IsSkipping)
            context.Connection.LastIdentity = lastIdentityValue;

        // OUTPUT INTO @t directs rows to the target only — no result set
        // surfaces to the client (probe-confirmed). When the OUTPUT clause
        // has no target, the projected rows flow back as a result set.
        return output is { HasTarget: false } o2
            ? new SimulatedSqlResultSet(o2.Schema, o2.ColumnNames, outputRows!)
            : new SimulatedNonQuery(sourceRows.Count);
    }

    /// <summary>
    /// Parses INSERT's <c>VALUES (…), (…)</c> source via the shared
    /// <c>ParseValuesTuples</c> helper, then eagerly evaluates each cell
    /// expression to a <see cref="SqlValue"/>. VALUES expressions can't
    /// reference columns; the column-resolver hook always raises
    /// <see cref="SimulatedSqlException.InvalidColumnName(string)"/>.
    /// </summary>
    private static List<SqlValue[]> EvaluateValuesTuples(ParserContext context)
    {
        var tuples = ParseValuesTuples(context);
        var runtime = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch);
        var rows = new List<SqlValue[]>(tuples.Count);
        foreach (var tuple in tuples)
        {
            var values = new SqlValue[tuple.Length];
            for (var i = 0; i < tuple.Length; i++)
                values[i] = tuple[i].Run(runtime);
            rows.Add(values);
        }
        return rows;
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
    /// Looks up an INSERT column reference: against <paramref name="destinationTable"/>'s
    /// columns directly for a regular table target, or against
    /// <paramref name="destinationView"/>'s <see cref="View.OutputColumns"/>
    /// (translated through <see cref="View.BaseColumnOrdinals"/> to a base
    /// column) for a view target. A view-side reference that maps to a
    /// derived projection (ordinal <c>-1</c>) raises <strong>Msg 4406</strong>
    /// — the per-touched-column gate matching SQL Server's "INSERT through
    /// view with a derived field touched" rejection.
    /// </summary>
    private static HeapColumn ResolveInsertTargetColumn(string columnName, HeapTable destinationTable, View? destinationView)
    {
        if (destinationView is null)
        {
            return destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, columnName))
                ?? throw SimulatedSqlException.InvalidColumnName(columnName);
        }
        for (var i = 0; i < destinationView.OutputColumns.Length; i++)
        {
            if (Collation.Default.Equals(destinationView.OutputColumns[i].Name, columnName))
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
}
