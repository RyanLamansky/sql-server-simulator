using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Schemas;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Parses and executes a <c>MERGE</c> statement. Supports the full
    /// branch family — <c>WHEN MATCHED</c> with <c>UPDATE</c> /
    /// <c>DELETE</c>, <c>WHEN NOT MATCHED [BY TARGET]</c> with
    /// <c>INSERT</c>, and <c>WHEN NOT MATCHED BY SOURCE</c> with
    /// <c>UPDATE</c> / <c>DELETE</c>. The source may be a <c>VALUES</c>
    /// list or any SELECT-expression (CTE / set-op chain / derived
    /// table). Each branch family allows multiple <c>AND
    /// search_condition</c>-gated clauses with an unconditional fallback
    /// last; <c>WHEN NOT MATCHED [BY TARGET]</c> is the exception — it
    /// admits at most one clause total (Msg 10714). <c>$action</c> is
    /// recognized in OUTPUT and projects the uppercase action verb per
    /// row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Execution is single-pass over the target heap × materialized
    /// source. For each target row, all matching source rows are
    /// gathered; the first applicable <c>WHEN MATCHED</c> clause wins. A
    /// target row matched by multiple source rows raises <strong>Msg
    /// 8672</strong> only when the chosen action is <c>UPDATE</c> —
    /// <c>DELETE</c> is forgiving (multiple matches collapse to one
    /// delete, probe-confirmed). Source rows that didn't match any
    /// target are candidates for <c>WHEN NOT MATCHED [BY TARGET]</c>;
    /// target rows that didn't match any source are candidates for
    /// <c>WHEN NOT MATCHED BY SOURCE</c>. Triggers fire in
    /// <c>INSERT → UPDATE → DELETE</c> order (probe-confirmed), each
    /// kind once with its combined affected rows.
    /// </para>
    /// </remarks>
    private static SimulatedStatementOutcome ParseMerge(ParserContext context)
    {
        // MERGE [INTO] target [AS] alias
        var afterMerge = context.GetNextRequired();
        if (afterMerge is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var destinationName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        context.Batch.RejectCrossDatabaseMutation(destinationName);

        // View target: resolve via TryResolveView first (CTE bindings are
        // already shadowed at the source level, not the target). An updatable
        // single-base view routes through its base table for the actual
        // mutation; a non-updatable view raises Msg 4403 / Msg 4405 the same
        // way INSERT / UPDATE / DELETE through view do.
        View? sourceView = null;
        HeapTable destinationTable;
        if (context.Batch.TryResolveView(destinationName, out var resolvedView))
        {
            sourceView = resolvedView;
            destinationTable = resolvedView.BaseTable
                ?? throw (resolvedView.RejectionReason == ViewUpdatabilityRejection.MultipleSources
                    ? SimulatedSqlException.ViewUpdateAffectsMultipleTables($"{resolvedView.Schema.Name}.{resolvedView.Name}")
                    : SimulatedSqlException.CannotUpdateNonUpdatableView($"{resolvedView.Schema.Name}.{resolvedView.Name}"));
        }
        else
        {
            destinationTable = context.Batch.TryResolveTable(destinationName, out var table)
                ? table
                : throw (BatchContext.IsTableVariableName(destinationName.Leaf)
                    ? SimulatedSqlException.MustDeclareTableVariable(destinationName.Leaf)
                    : SimulatedSqlException.InvalidObjectName(destinationName));
        }
        if (destinationTable.IsTableValuedParameter)
            throw SimulatedSqlException.TableValuedParameterIsReadOnly(destinationName.Leaf);

        // MERGE target hints: hint-then-alias placement
        // (probe-confirmed: `MERGE INTO t WITH (TABLOCK) AS x USING …` works,
        // `MERGE INTO t AS x WITH (TABLOCK) USING …` raises Msg 156). The
        // hint slot sits between the target name and the optional
        // <c>[AS] alias</c>, opposite of FROM / UPDATE / DELETE which use
        // alias-then-hint. Legacy bare-paren form is rejected. Table-
        // variable targets reject hints — skip the parser for `@t`.
        context.MoveNextRequired();
        if (!BatchContext.IsTableVariableName(destinationName.Leaf))
            Selection.ValidateDmlTargetHints(Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false));
        // Phase 1b: acquire table-IX on the MERGE target; row-X on each
        // affected row at mutation time.
        RejectDisabledClusteredIndex(destinationTable);
        _ = context.Batch.AcquireDataLockIfApplicable(destinationTable, default, isWrite: true);

        // Optional target alias: AS <alias> or bare <alias>.
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            context.MoveNextRequired();
        // Default alias: the surface name the user typed — view's own name
        // when the target is a view (so `MERGE INTO vbase … ON vbase.col …`
        // works), otherwise the base table's name.
        var defaultTargetName = sourceView?.Name ?? destinationTable.Name;
        var targetAlias = context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Using } => defaultTargetName,
            Name n => n.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (!context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName))
            context.MoveNextRequired();

        // Target-side parse-time column shape: the view's projection when
        // present (so user-typed names like `vbase.pk` resolve against the
        // view's renamed columns), otherwise the base table's columns. All
        // parse-time / runtime column lookups against the target go through
        // this array; writes translate back to the base via
        // <see cref="View.BaseColumnOrdinals"/> at action-resolve time.
        var targetColumns = sourceView?.OutputColumns ?? destinationTable.Columns;

        // USING (<source>) [AS] alias [(col, ...)]
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Using })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var (materializeSource, sourceAlias, sourceColumnNames, sourceSchema) = ParseMergeSource(context);

        // ON predicate — resolves target via targetAlias/destinationName, source via sourceAlias.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        SqlType ResolveTypeBoth(MultiPartName name) => ResolveMergeColumnType(
            context, name, targetAlias, defaultTargetName, targetColumns, sourceAlias, sourceColumnNames, sourceSchema);

        // Walk the ON's expression tree with the two-sided resolver so any
        // column reference type-checks correctly at parse time.
        var prevResolver = context.OuterTypeResolver;
        context.OuterTypeResolver = ResolveTypeBoth;
        BooleanExpression onPredicate;
        try
        {
            onPredicate = BooleanExpression.Parse(context);
        }
        finally
        {
            context.OuterTypeResolver = prevResolver;
        }

        // WHEN clauses.
        var whenClauses = ParseMergeWhenClauses(context, destinationTable, sourceView, targetAlias, defaultTargetName, sourceAlias, sourceColumnNames, sourceSchema);

        // OUTPUT.
        var output = TryParseMergeOutputClause(context, destinationTable, sourceView, sourceAlias, sourceColumnNames, sourceSchema);

        // Msg 334 applies per action the MERGE actually performs, and the
        // message echoes the target as written — its alias when one was given
        // (probe-confirmed: `MERGE dbo.m AS t …` reports 't'), otherwise the
        // name from the statement.
        if (output is { HasTarget: false })
        {
            var mergeTarget = context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName)
                ? destinationName.ToString()
                : targetAlias;
            foreach (var clause in whenClauses)
            {
                RejectClientOutputOnTriggeredTarget(
                    context.Batch,
                    (SchemaObject?)sourceView ?? destinationTable,
                    clause.Action switch
                    {
                        MergeActionKind.Insert => TriggerActions.Insert,
                        MergeActionKind.Delete => TriggerActions.Delete,
                        _ => TriggerActions.Update,
                    },
                    mergeTarget,
                    outputReturnsToClient: true);
            }
        }

        // Required trailing ; — but the dispatch loop may have already
        // consumed it (statement separators are flexible). If the cursor
        // sits on either ; or end-of-batch, accept; otherwise raise Msg 10713.
        if (context.Token is not (null or Operator { Character: ';' }))
            throw SimulatedSqlException.MergeMustBeTerminated();
        if (!context.Batch.IsSkipping)
            CheckMergePermissions(context.Batch, destinationTable, sourceView, whenClauses);
        return ExecuteMerge(context, destinationTable, sourceView, targetAlias, materializeSource, sourceAlias, sourceColumnNames, sourceSchema, onPredicate, whenClauses, output);
    }

    /// <summary>
    /// Checks MERGE permissions on the target: SELECT (the ON predicate reads
    /// it) plus the write permission of each action kind present (INSERT /
    /// UPDATE / DELETE). Denials surface as Msg 229. The source read is not
    /// separately checked — a documented gap.
    /// </summary>
    private static void CheckMergePermissions(BatchContext batch, HeapTable destinationTable, View? sourceView, IReadOnlyList<WhenClause> whenClauses)
    {
        void Check(string permission)
        {
            if (sourceView is not null)
                PermissionEnforcement.CheckView(batch, permission, sourceView);
            else
                PermissionEnforcement.CheckTable(batch, permission, destinationTable);
        }

        Check("SELECT");
        var insert = false;
        var update = false;
        var delete = false;
        foreach (var clause in whenClauses)
        {
            switch (clause.Action)
            {
                case MergeActionKind.Insert:
                    insert = true;
                    break;
                case MergeActionKind.Update:
                    update = true;
                    break;
                case MergeActionKind.Delete:
                    delete = true;
                    break;
            }
        }
        if (insert)
            Check("INSERT");
        if (update)
            Check("UPDATE");
        if (delete)
            Check("DELETE");
    }

    /// <summary>
    /// Parses the <c>USING (...)</c> source — either a <c>VALUES</c>
    /// tuple list or a parenthesized SELECT / set-op chain — followed
    /// by the required <c>[AS] alias</c> and the optional
    /// <c>(col1, col2, ...)</c> rename list. Also accepts a bare-table /
    /// view / temp-table / table-variable reference (<c>USING tbl [AS]
    /// alias</c>), probe-confirmed 2026-05-14 to match real SQL Server:
    /// alias is optional (defaults to the table's leaf name), optional
    /// <c>WITH (hint [, …])</c> sits between alias and ON (alias-then-hint
    /// placement, same as FROM source). Column-rename list is not
    /// supported in the bare-table form — real SQL Server parses the
    /// trailing <c>(...)</c> as a hint clause (probed Msg 321 with the
    /// first column name as the would-be hint name) and the simulator
    /// matches by routing through <see cref="Selection.ParseOptionalTableHints"/>.
    /// </summary>
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema) ParseMergeSource(ParserContext context)
    {
        context.MoveNextRequired();
        return context.Token is Operator { Character: '(' }
            ? ParseParenthesizedMergeSource(context)
            : ParseBareTableMergeSource(context);
    }

    /// <summary>
    /// <c>USING (VALUES …) AS alias [(cols)]</c> or
    /// <c>USING (SELECT … / WITH … SELECT …) AS alias [(cols)]</c>. The
    /// alias is required here (matches real SQL Server). Cursor on entry:
    /// the opening <c>(</c>. Cursor on exit: the next un-consumed token
    /// (typically <c>ON</c>).
    /// </summary>
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema) ParseParenthesizedMergeSource(ParserContext context)
    {
        context.MoveNextRequired();

        Func<BatchContext, List<SqlValue[]>> materialize;
        SqlType[] sourceSchema;
        string[] selectionColumnNames;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Values })
        {
            var tuples = ParseValuesTuples(context);
            sourceSchema = new SqlType[tuples[0].Length];
            for (var i = 0; i < tuples[0].Length; i++)
                sourceSchema[i] = tuples[0][i].GetSqlType(context.Batch, name => throw SimulatedSqlException.InvalidColumnName(name));
            selectionColumnNames = new string[tuples[0].Length];
            materialize = batch =>
            {
                var runtime = new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), batch);
                var rows = new List<SqlValue[]>(tuples.Count);
                foreach (var tuple in tuples)
                {
                    batch.BumpRowStamp();
                    var values = new SqlValue[tuple.Length];
                    for (var i = 0; i < tuple.Length; i++)
                        values[i] = tuple[i].Run(runtime);
                    rows.Add(values);
                }
                return rows;
            };
        }
        else
        {
            var selection = Selection.Parse(context, depth: 1);
            sourceSchema = selection.Schema;
            selectionColumnNames = selection.ColumnNames;
            materialize = batch =>
            {
                var rs = selection.Execute(batch);
                var rows = new List<SqlValue[]>();
                foreach (var rowBytes in rs.RowBytes)
                    rows.Add(RowDecoder.DecodeRow(sourceSchema.AsSpan(), rowBytes));
                return rows;
            };
        }

        if (context.Token is not Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var afterUsingClose = context.GetNextRequired();
        if (afterUsingClose is ReservedKeyword { Keyword: Keyword.As })
            afterUsingClose = context.GetNextRequired();

        var alias = (afterUsingClose as Name)?.Value
            ?? throw SimulatedSqlException.SyntaxErrorNear(context);

        context.MoveNextRequired();
        string[] columnNames;
        if (context.Token is Operator { Character: '(' })
        {
            var names = new List<string>();
            while (true)
            {
                if (context.GetNextRequired() is not Name colName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                names.Add(colName.Value);
                var sep = context.GetNextRequired();
                if (sep is Operator { Character: ')' })
                    break;
                if (sep is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            if (names.Count != sourceSchema.Length)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            columnNames = [.. names];
            context.MoveNextRequired();
        }
        else
        {
            columnNames = new string[selectionColumnNames.Length];
            for (var i = 0; i < selectionColumnNames.Length; i++)
            {
                if (string.IsNullOrEmpty(selectionColumnNames[i]))
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                columnNames[i] = selectionColumnNames[i];
            }
        }

        return (materialize, alias, columnNames, sourceSchema);
    }

    /// <summary>
    /// <c>USING tbl [AS] alias [WITH (hints)]</c> — the bare-table /
    /// view / temp-table / table-variable form. Alias is optional (per
    /// probe; defaults to the leaf name). Hints sit between alias and ON
    /// (alias-then-hint placement, same as FROM-source). Column-rename
    /// list is not a valid grammar here — real SQL Server parses a
    /// trailing <c>(c1, c2)</c> as a hint clause and rejects with
    /// Msg 321; the simulator matches by routing through the hint parser
    /// (which surfaces the same code when the first inner token isn't a
    /// known hint name). Cursor on entry: the first name segment of the
    /// table / view object. Cursor on exit: the next un-consumed token
    /// (typically <c>ON</c>).
    /// </summary>
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema) ParseBareTableMergeSource(ParserContext context)
    {
        var objectName = BatchContext.ParseObjectName(context, acceptTableVariable: true);

        Func<BatchContext, List<SqlValue[]>> materialize;
        SqlType[] sourceSchema;
        string[] columnNames;

        // CTE binding shadows table / view resolution: `WITH c AS (…) MERGE …
        // USING c ON …` references the CTE, not any same-named base object.
        // Only 1-part names route here; CTEs aren't schema-qualifiable.
        if (objectName.Count == 1
            && context.CteBindings is { } cteBindings
            && cteBindings.TryGetValue(objectName.Leaf, out var cteBinding))
        {
            if (cteBinding.Plan is null)
                throw SimulatedSqlException.RecursiveCteMissingUnionAll(cteBinding.Name);
            var ctePlan = cteBinding.Plan;
            sourceSchema = ctePlan.Schema;
            columnNames = cteBinding.ColumnNames;
            var cteAlias = Selection.ConsumeOptionalAlias(context) ?? cteBinding.Name;
            materialize = batch =>
            {
                var rs = ctePlan.Execute(batch);
                var rows = new List<SqlValue[]>();
                foreach (var rowBytes in rs.RowBytes)
                    rows.Add(RowDecoder.DecodeRow(sourceSchema.AsSpan(), rowBytes));
                return rows;
            };
            return (materialize, cteAlias, columnNames, sourceSchema);
        }

        if (context.Batch.TryResolveView(objectName, out var resolvedView))
        {
            sourceSchema = new SqlType[resolvedView.OutputColumns.Length];
            columnNames = new string[resolvedView.OutputColumns.Length];
            for (var i = 0; i < resolvedView.OutputColumns.Length; i++)
            {
                sourceSchema[i] = resolvedView.OutputColumns[i].Type;
                columnNames[i] = resolvedView.OutputColumns[i].Name;
            }
            var viewSelection = Selection.ForView(resolvedView);
            materialize = batch =>
            {
                var rs = viewSelection.Execute(batch);
                var rows = new List<SqlValue[]>();
                foreach (var rowBytes in rs.RowBytes)
                    rows.Add(RowDecoder.DecodeRow(sourceSchema.AsSpan(), rowBytes));
                return rows;
            };
        }
        else if (context.Batch.TryResolveTable(objectName, out var heapTable))
        {
            sourceSchema = new SqlType[heapTable.Columns.Length];
            columnNames = new string[heapTable.Columns.Length];
            for (var i = 0; i < heapTable.Columns.Length; i++)
            {
                sourceSchema[i] = heapTable.Columns[i].Type;
                columnNames[i] = heapTable.Columns[i].Name;
            }
            var sourceTable = heapTable;
            var alias = Selection.ConsumeOptionalAlias(context) ?? objectName.Leaf;
            var sourceHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: true, commitOnLegacyParen: true);
            // Phase 1b: MERGE bare-table source is a READ — table-IS plus
            // per-row probe (or hint-driven row-S / row-U / row-X).
            _ = context.Batch.AcquireDataLockIfApplicable(sourceTable, sourceHints, isWrite: false);
            materialize = batch =>
            {
                var rows = new List<SqlValue[]>();
                foreach (var rowBytes in sourceTable.Heap.EnumerateRows())
                {
                    var fullValues = DecodeFullRow(sourceTable, rowBytes);
                    EvaluateComputedColumns(sourceTable, fullValues, batch);
                    rows.Add(fullValues);
                }
                return rows;
            };
            return (materialize, alias, columnNames, sourceSchema);
        }
        else
        {
            throw BatchContext.IsTableVariableName(objectName.Leaf)
                ? SimulatedSqlException.MustDeclareTableVariable(objectName.Leaf)
                : SimulatedSqlException.InvalidObjectName(objectName);
        }

        var defaultAlias = Selection.ConsumeOptionalAlias(context) ?? objectName.Leaf;
        _ = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: true, commitOnLegacyParen: true);
        return (materialize, defaultAlias, columnNames, sourceSchema);
    }

    /// <summary>
    /// Parses the 1+ WHEN clauses following MERGE's ON predicate.
    /// Enforces the grammar rules SQL Server probes confirmed:
    /// <list type="bullet">
    /// <item>WHEN MATCHED admits UPDATE or DELETE (Msg 10711 rejects INSERT).</item>
    /// <item>WHEN NOT MATCHED [BY TARGET] admits INSERT only (Msg 10710 rejects UPDATE/DELETE), and may appear at most once (Msg 10714).</item>
    /// <item>WHEN NOT MATCHED BY SOURCE admits UPDATE or DELETE (Msg 10711 rejects INSERT).</item>
    /// <item>Within MATCHED and NOT MATCHED BY SOURCE families, an unconditional clause cannot be followed by a conditional one (Msg 5324).</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Types a column reference against the MERGE's own two sides: the target
    /// (by alias, by the target's own name, or unqualified) and then the
    /// source. Installed as <see cref="ParserContext.OuterTypeResolver"/> while
    /// the ON predicate and the WHEN clauses parse, so a correlated subquery
    /// inside either binds to a MERGE column rather than failing to resolve.
    /// </summary>
    private static SqlType ResolveMergeColumnType(
        ParserContext context,
        MultiPartName name,
        string targetAlias,
        string defaultTargetName,
        HeapColumn[] targetColumns,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        if (collation.Equals(name.ImmediateQualifier, targetAlias) || collation.Equals(name.ImmediateQualifier, defaultTargetName))
        {
            foreach (var column in targetColumns)
            {
                if (collation.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
        }
        if (collation.Equals(name.ImmediateQualifier, sourceAlias))
        {
            for (var i = 0; i < sourceColumnNames.Length; i++)
            {
                if (collation.Equals(sourceColumnNames[i], name.Leaf))
                    return sourceSchema[i];
            }
        }
        // Unqualified: try target then source.
        if (name.Count == 1)
        {
            foreach (var column in targetColumns)
            {
                if (collation.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
            for (var i = 0; i < sourceColumnNames.Length; i++)
            {
                if (collation.Equals(sourceColumnNames[i], name.Leaf))
                    return sourceSchema[i];
            }
        }
        throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
    }

    private static List<WhenClause> ParseMergeWhenClauses(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        string defaultTargetName,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        var clauses = new List<WhenClause>();
        var matchedUnconditionalSeen = false;
        var nmbsUnconditionalSeen = false;
        var nmbtSeen = false;

        // See ParseMerge's commentary on `targetColumns` — view target's
        // user-facing column shape is OutputColumns; base shape otherwise.
        var targetColumns = sourceView?.OutputColumns ?? destinationTable.Columns;

        SqlType ResolveType(MultiPartName name) => ResolveMergeColumnType(
            context, name, targetAlias, defaultTargetName, targetColumns, sourceAlias, sourceColumnNames, sourceSchema);

        while (context.Token is ReservedKeyword { Keyword: Keyword.When })
        {
            context.MoveNextRequired();
            bool isNotMatched;
            if (context.Token is ReservedKeyword { Keyword: Keyword.Not })
            {
                isNotMatched = true;
                context.MoveNextRequired();
            }
            else
            {
                isNotMatched = false;
            }

            if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Matched })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            // Optional BY {TARGET | SOURCE}. Default is BY TARGET for NOT
            // MATCHED; MATCHED never carries BY.
            WhenClauseKind kind;
            if (context.Token is ReservedKeyword { Keyword: Keyword.By })
            {
                if (!isNotMatched)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                kind = context.Token switch
                {
                    UnquotedString { ContextualKeyword: ContextualKeyword.Source } => WhenClauseKind.NotMatchedBySource,
                    UnquotedString { ContextualKeyword: ContextualKeyword.Target } => WhenClauseKind.NotMatchedByTarget,
                    _ => throw SimulatedSqlException.SyntaxErrorNear(context),
                };
                context.MoveNextRequired();
            }
            else
            {
                kind = isNotMatched ? WhenClauseKind.NotMatchedByTarget : WhenClauseKind.Matched;
            }

            // Optional AND search_condition.
            BooleanExpression? searchCondition = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.And })
            {
                context.MoveNextRequired();
                var prevResolver = context.OuterTypeResolver;
                context.OuterTypeResolver = ResolveType;
                try
                {
                    searchCondition = BooleanExpression.Parse(context);
                }
                finally
                {
                    context.OuterTypeResolver = prevResolver;
                }
            }

            // Family-level ordering checks.
            if (kind == WhenClauseKind.Matched)
            {
                if (matchedUnconditionalSeen)
                    throw SimulatedSqlException.MergeUnconditionalMustBeLast("WHEN MATCHED");
                if (searchCondition is null)
                    matchedUnconditionalSeen = true;
            }
            else if (kind == WhenClauseKind.NotMatchedBySource)
            {
                if (nmbsUnconditionalSeen)
                    throw SimulatedSqlException.MergeUnconditionalMustBeLast("WHEN NOT MATCHED BY SOURCE");
                if (searchCondition is null)
                    nmbsUnconditionalSeen = true;
            }
            else // NotMatchedByTarget
            {
                if (nmbtSeen)
                    throw SimulatedSqlException.MergeMultipleNotMatchedClauses();
                nmbtSeen = true;
            }

            if (context.Token is not ReservedKeyword { Keyword: Keyword.Then })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextRequired();

            clauses.Add(ParseMergeAction(context, kind, searchCondition, destinationTable, sourceView, ResolveType));
        }

        return clauses.Count == 0 ? throw SimulatedSqlException.SyntaxErrorNear(context) : clauses;
    }

    private static WhenClause ParseMergeAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        Func<MultiPartName, SqlType> resolveType) =>
        context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Insert } => ParseMergeInsertAction(context, kind, searchCondition, destinationTable, sourceView, resolveType),
            ReservedKeyword { Keyword: Keyword.Update } => ParseMergeUpdateAction(context, kind, searchCondition, destinationTable, sourceView, resolveType),
            ReservedKeyword { Keyword: Keyword.Delete } => ParseMergeDeleteAction(context, kind, searchCondition),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

    private static WhenClause ParseMergeInsertAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        Func<MultiPartName, SqlType> resolveType)
    {
        if (kind == WhenClauseKind.Matched)
            throw SimulatedSqlException.MergeInsertNotAllowedInClause("WHEN MATCHED");
        if (kind == WhenClauseKind.NotMatchedBySource)
            throw SimulatedSqlException.MergeInsertNotAllowedInClause("WHEN NOT MATCHED BY SOURCE");

        var insertColumns = new List<HeapColumn>();
        var afterInsert = context.GetNextRequired();
        if (afterInsert is Operator { Character: '(' })
        {
            while (true)
            {
                if (context.GetNextRequired() is not StringToken colTok)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                // Same helper INSERT … through view uses: looks up the
                // name against view.OutputColumns when applicable and
                // translates to the base table column, rejecting writes
                // to a derived projection (Msg 4406).
                var col = ResolveInsertTargetColumn(context.Batch.CurrentDatabase.Collation, colTok.Value, destinationTable, sourceView);
                if (col.Computed is not null)
                    throw SimulatedSqlException.ColumnCannotBeModified(col.Name);
                if (col.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotInsertExplicitTimestamp();
                insertColumns.Add(col);
                var sep = context.GetNextRequired();
                if (sep is Operator { Character: ')' })
                    break;
                if (sep is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextRequired();
        }

        if (context.Token is not ReservedKeyword { Keyword: Keyword.Values })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var insertValues = new List<Expression>();
        var prevResolver = context.OuterTypeResolver;
        context.OuterTypeResolver = resolveType;
        try
        {
            while (true)
            {
                context.MoveNextRequired();
                insertValues.Add(Expression.Parse(context));
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
        }
        finally
        {
            context.OuterTypeResolver = prevResolver;
        }
        context.MoveNextOptional();

        HeapColumn[] columns;
        if (insertColumns.Count == 0)
        {
            // Implicit column list: the writable shape of the view's
            // projection (mapped through BaseColumnOrdinals) when targeting
            // a view, otherwise the base table's writable columns.
            // IDENTITY_INSERT-on doesn't apply to MERGE — its source-list
            // structure precludes the same opt-in shape INSERT supports —
            // so implicit-list IDENTITY columns drop the same way real
            // SQL Server's MERGE INTO does.
            if (sourceView is not null)
            {
                columns = BuildImplicitInsertColumnsForView(sourceView, destinationTable, identityInsertOn: false);
            }
            else
            {
                var defaultCols = new List<HeapColumn>();
                foreach (var c in destinationTable.Columns)
                {
                    if (c.Computed is null && c.Type != SqlType.RowVersion)
                        defaultCols.Add(c);
                }
                columns = [.. defaultCols];
            }
        }
        else
        {
            columns = [.. insertColumns];
        }

        return columns.Length != insertValues.Count
            ? throw SimulatedSqlException.SyntaxErrorNear(context)
            : new WhenClause(kind, MergeActionKind.Insert, searchCondition, assignments: null, insertColumns: columns, insertValues: [.. insertValues]);
    }

    private static WhenClause ParseMergeUpdateAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        Func<MultiPartName, SqlType> resolveType)
    {
        if (kind == WhenClauseKind.NotMatchedByTarget)
            throw SimulatedSqlException.MergeUpdateNotAllowedInNotMatched();

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var assignments = new List<(int Ordinal, Expression Expr)>();
        var prevResolver = context.OuterTypeResolver;
        context.OuterTypeResolver = resolveType;
        try
        {
            while (true)
            {
                if (context.GetNextRequired() is not StringToken first)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                string columnName;
                var afterName = context.GetNextRequired();
                if (afterName is Operator { Character: '.' })
                {
                    if (context.GetNextRequired() is not StringToken col)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    columnName = col.Value;
                    context.MoveNextRequired();
                }
                else
                {
                    columnName = first.Value;
                }

                if (context.Token is not Operator { Character: '=' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                context.MoveNextRequired();
                var rhs = Expression.Parse(context);

                // Resolve user-facing column name into the base-table ordinal
                // that the WHEN executor will mutate. View paths translate
                // via OutputColumns + BaseColumnOrdinals (derived projections
                // reject with Msg 4406); table paths look up directly.
                int ordinal;
                if (sourceView is not null)
                {
                    var matched = -1;
                    for (var i = 0; i < sourceView.OutputColumns.Length; i++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(sourceView.OutputColumns[i].Name, columnName))
                        {
                            matched = i;
                            break;
                        }
                    }
                    if (matched < 0)
                        throw SimulatedSqlException.InvalidColumnName(columnName);
                    var baseOrd = sourceView.BaseColumnOrdinals[matched];
                    if (baseOrd < 0)
                        throw SimulatedSqlException.ViewDmlTouchesDerivedField($"{sourceView.Schema.Name}.{sourceView.Name}");
                    ordinal = baseOrd;
                }
                else
                {
                    ordinal = -1;
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (context.Batch.CurrentDatabase.Collation.Equals(destinationTable.Columns[i].Name, columnName))
                        {
                            ordinal = i;
                            break;
                        }
                    }
                    if (ordinal < 0)
                        throw SimulatedSqlException.InvalidColumnName(columnName);
                }
                var targetColumn = destinationTable.Columns[ordinal];
                if (targetColumn.Identity is not null)
                    throw SimulatedSqlException.CannotUpdateIdentityColumn(targetColumn.Name);
                if (targetColumn.Computed is not null)
                    throw SimulatedSqlException.ColumnCannotBeModified(targetColumn.Name);
                if (targetColumn.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotUpdateTimestampColumn();
                assignments.Add((ordinal, rhs));

                if (context.Token is not Operator { Character: ',' })
                    break;
            }
        }
        finally
        {
            context.OuterTypeResolver = prevResolver;
        }

        return new WhenClause(kind, MergeActionKind.Update, searchCondition, assignments: assignments, insertColumns: null, insertValues: null);
    }

    private static WhenClause ParseMergeDeleteAction(ParserContext context, WhenClauseKind kind, BooleanExpression? searchCondition)
    {
        if (kind == WhenClauseKind.NotMatchedByTarget)
            throw SimulatedSqlException.MergeUpdateNotAllowedInNotMatched();
        context.MoveNextOptional();
        return new WhenClause(kind, MergeActionKind.Delete, searchCondition, assignments: null, insertColumns: null, insertValues: null);
    }

    /// <summary>
    /// OUTPUT clause parser specialized for MERGE — supports INSERTED.col
    /// (NULL for DELETE rows), DELETED.col (NULL for INSERT rows),
    /// source-alias.col (NULL for WHEN NOT MATCHED BY SOURCE rows),
    /// and the <c>$action</c> pseudo-column (uppercase 'INSERT' /
    /// 'UPDATE' / 'DELETE' string).
    /// </summary>
    private static OutputProjection? TryParseMergeOutputClause(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;
        // OUTPUT through a view is not modeled — matches the existing pattern
        // for UPDATE / INSERT / DELETE OUTPUT through view. INSERTED.* /
        // DELETED.* projection through view OutputColumns + BaseColumnOrdinals
        // remains deferred.
        if (sourceView is not null)
            throw new NotSupportedException($"MERGE … OUTPUT through a view ('{sourceView.Schema.Name}.{sourceView.Name}') isn't modeled. Target the underlying table directly when OUTPUT is required.");

        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(MultiPartName name)
        {
            if (BuiltInToken.EqualsAny(name.ImmediateQualifier, "INSERTED", "DELETED"))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
            }
            else if (context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceSchema[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

        do
        {
            context.MoveNextRequired();
            // $action pseudo-column: detected by the tokenizer's $action
            // single-token emission. Synthesize a literal reference that
            // the runtime resolves to the action verb via the row context.
            if (context.Token is UnquotedString u && u.Value.Equals("$action", StringComparison.OrdinalIgnoreCase))
            {
                Expression actionExpr = new MergeActionReference();
                context.MoveNextOptional();
                switch (context.Token)
                {
                    case ReservedKeyword { Keyword: Keyword.As }:
                        actionExpr = Expression.AssignName(actionExpr, context.GetNextRequired<Name>());
                        context.MoveNextOptional();
                        break;
                    case Name actionAlias:
                        actionExpr = Expression.AssignName(actionExpr, actionAlias);
                        context.MoveNextOptional();
                        break;
                }
                expressions.Add(actionExpr);
                columnNames.Add(string.IsNullOrEmpty(actionExpr.Name) ? "$action" : actionExpr.Name);
                continue;
            }
            if (TryDetectStarReference(context, out var starQualifier))
            {
                string[]? cols = null;
                if (BuiltInToken.EqualsAny(starQualifier, "INSERTED", "DELETED"))
                {
                    cols = new string[destinationTable.Columns.Length];
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                        cols[i] = destinationTable.Columns[i].Name;
                }
                else if (context.Batch.CurrentDatabase.Collation.Equals(starQualifier, sourceAlias))
                {
                    cols = sourceColumnNames;
                }
                if (cols is null)
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{starQualifier}.*");
                AppendStarExpansion(starQualifier, cols, expressions, columnNames);
                context.MoveNextOptional();
                continue;
            }
            var expr = Expression.Parse(context);
            switch (context.Token)
            {
                case ReservedKeyword { Keyword: Keyword.As }:
                    expr = Expression.AssignName(expr, context.GetNextRequired<Name>());
                    context.MoveNextOptional();
                    break;
                case Name aliasName:
                    expr = Expression.AssignName(expr, aliasName);
                    context.MoveNextOptional();
                    break;
            }
            expressions.Add(expr);
            columnNames.Add(expr.Name);
        }
        while (context.Token is Operator { Character: ',' });

        var schema = new SqlType[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            schema[i] = IsMergeActionRef(expressions[i]) ? NVarcharSqlType.Get(10, context.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault) : expressions[i].GetSqlType(context.Batch, ResolveOutputType);

        // MERGE reaches the same INTO parser the other three statements use;
        // its own projection type predated that and never grew the branch.
        var outputTarget = TryParseOutputIntoTarget(context, expressions.Count, destinationTable.Name);

        return new OutputProjection(
            [.. expressions], [.. columnNames], schema, destinationTable,
            (sourceAlias, sourceColumnNames, sourceSchema), context.Batch, outputTarget);
    }

    /// <summary>
    /// Runs the prepared MERGE plan against the live target heap. The
    /// source <see cref="Selection"/> materializes into a list once;
    /// each target row is scanned, its action chosen via the first
    /// applicable WHEN clause, and queued. Unmatched source rows fall
    /// into the <c>WHEN NOT MATCHED [BY TARGET]</c> clause if present.
    /// All queued mutations apply atomically before triggers fire.
    /// </summary>
    /// <summary>
    /// Maps a user-typed target column name to its base-table ordinal and
    /// type. For a view target, looks up the name in
    /// <see cref="View.OutputColumns"/> and translates via
    /// <see cref="View.BaseColumnOrdinals"/>; derived view projections
    /// (ordinal <c>-1</c>) are reported as not-found so the caller raises
    /// the appropriate column-reference error. For a table target, looks up
    /// directly in <see cref="HeapTable.Columns"/>.
    /// </summary>
    private static bool TryLookupTargetColumn(Collation collation, string columnName, HeapTable destinationTable, View? sourceView, out int baseOrdinal, out SqlType type)
    {
        if (sourceView is not null)
        {
            for (var i = 0; i < sourceView.OutputColumns.Length; i++)
            {
                if (collation.Equals(sourceView.OutputColumns[i].Name, columnName))
                {
                    var baseOrd = sourceView.BaseColumnOrdinals[i];
                    if (baseOrd < 0)
                        break; // Derived projection — unreadable in MERGE context.
                    baseOrdinal = baseOrd;
                    type = sourceView.OutputColumns[i].Type;
                    return true;
                }
            }
        }
        else
        {
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                if (collation.Equals(destinationTable.Columns[i].Name, columnName))
                {
                    baseOrdinal = i;
                    type = destinationTable.Columns[i].Type;
                    return true;
                }
            }
        }
        baseOrdinal = 0;
        type = SqlType.Int32;
        return false;
    }

    private static SimulatedStatementOutcome ExecuteMerge(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        Func<BatchContext, List<SqlValue[]>> materializeSource,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        BooleanExpression onPredicate,
        List<WhenClause> whenClauses,
        OutputProjection? output)
    {
        var sourceRows = materializeSource(context.Batch);
        var sourceMatched = new bool[sourceRows.Count];
        var defaultTargetName = sourceView?.Name ?? destinationTable.Name;

        // Target-side column lookup: user-facing names match view OutputColumns
        // when a view target is in scope, otherwise base table columns. View
        // path translates the matched user-name to a base ordinal via
        // BaseColumnOrdinals so the heap-decoded targetValues array indexes
        // correctly. Derived view columns (BaseColumnOrdinals[i] == -1)
        // can be read at runtime by re-evaluating the projection's
        // expression — but writes to them were rejected at parse time.
        // Resolve target columns by qualifier; null-source means BY-SOURCE branch (everything in source resolver returns NULL).
        SqlValue ResolveCombined(SqlValue[]? targetValues, SqlValue[]? sourceValues, MultiPartName name)
        {
            if (context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, targetAlias)
                || context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, defaultTargetName))
            {
                if (TryLookupTargetColumn(context.Batch.CurrentDatabase.Collation, name.Leaf, destinationTable, sourceView, out var targetOrdinal, out var targetType))
                    return targetValues is null ? SqlValue.Null(targetType) : targetValues[targetOrdinal];
            }
            if (context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            if (name.Count == 1)
            {
                if (TryLookupTargetColumn(context.Batch.CurrentDatabase.Collation, name.Leaf, destinationTable, sourceView, out var targetOrdinal, out var targetType))
                    return targetValues is null ? SqlValue.Null(targetType) : targetValues[targetOrdinal];
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

        var pendingInserts = new List<(SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingUpdates = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingDeletes = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)>();

        // Phase A finds, per matched target row, the source rows it matches, then
        // applies the WHEN MATCHED / WHEN NOT MATCHED BY SOURCE action. The
        // target × source scan is O(target × source). When the ON carries a
        // seekable target equality and the target isn't a view (whose column names
        // don't map to the base heap), the inverted path seeks matching targets
        // per source row instead — turning the match phase into O(source × log
        // target). With no NOT MATCHED BY SOURCE clause it then touches only the
        // matched targets; with one (which must visit every target to find the
        // unmatched ones) it walks the heap once applying the precomputed matches,
        // dropping the inner source loop either way.
        var targetSeek = sourceView is null
            ? Selection.TryPrepareMergeTargetSeek(destinationTable, targetAlias, onPredicate, context.Batch)
            : null;

        if (targetSeek is not null)
        {
            var hasNotMatchedBySource = whenClauses.Any(c => c.Kind == WhenClauseKind.NotMatchedBySource);

            // Match phase: per source row, seek the matching target rows and group
            // them by target address — first-source-wins via source-index order
            // (sources iterate ascending). The seek matched the equality prefix
            // only, so the full ON is re-run per candidate (residual filter — a
            // term like … AND t.active = 1, or a stale cache entry, is dropped).
            var matchedByTarget = new Dictionary<(int Page, int Slot), List<int>>();
            for (var si = 0; si < sourceRows.Count; si++)
            {
                var sourceValues = sourceRows[si];
                foreach (var (page, slot, rowBytes) in targetSeek(name => ResolveCombined(null, sourceValues, name)))
                {
                    var candidateValues = DecodeFullRow(destinationTable, rowBytes);
                    EvaluateComputedColumns(destinationTable, candidateValues, context.Batch);
                    if (onPredicate.Run(new RuntimeContext(name => ResolveCombined(candidateValues, sourceValues, name), context.Batch)) != true)
                        continue;

                    sourceMatched[si] = true;
                    if (!matchedByTarget.TryGetValue((page, slot), out var sources))
                        matchedByTarget[(page, slot)] = sources = [];
                    sources.Add(si);
                }
            }

            if (hasNotMatchedBySource)
            {
                // Complement pass: every target row in heap order — matched rows take
                // their precomputed source list, unmatched rows fall to WHEN NOT
                // MATCHED BY SOURCE. Heap-order interleaving matches the scan path's
                // discovery order, but with no per-target source loop.
                foreach (var (pageIndex, slotIndex, rowBytes) in destinationTable.Heap.EnumerateRowsWithAddress())
                {
                    var targetValues = DecodeFullRow(destinationTable, rowBytes);
                    EvaluateComputedColumns(destinationTable, targetValues, context.Batch);
                    if (matchedByTarget.TryGetValue((pageIndex, slotIndex), out var matchedSources))
                    {
                        ApplyMergeMatched(context, destinationTable, sourceView, whenClauses, pageIndex, slotIndex, targetValues, sourceRows, matchedSources, ResolveCombined, pendingUpdates, pendingDeletes);
                    }
                    else
                    {
                        var chosen = PickClause(whenClauses, WhenClauseKind.NotMatchedBySource, targetValues, sourceValues: null, context.Batch, ResolveCombined);
                        if (chosen is not null)
                            ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, pageIndex, slotIndex, targetValues, sourceValues: null, ResolveCombined, pendingUpdates, pendingDeletes);
                    }
                }
            }
            else
            {
                // No NOT MATCHED BY SOURCE: visit only matched targets, restoring
                // heap order via the (page, slot) sort so the apply order matches
                // the scan path's.
                foreach (var address in matchedByTarget.Keys.OrderBy(a => a.Page).ThenBy(a => a.Slot))
                {
                    var targetValues = DecodeFullRow(destinationTable, destinationTable.Heap.ReadSlotBytes(address.Page, address.Slot)!);
                    EvaluateComputedColumns(destinationTable, targetValues, context.Batch);
                    ApplyMergeMatched(context, destinationTable, sourceView, whenClauses, address.Page, address.Slot, targetValues, sourceRows, matchedByTarget[address], ResolveCombined, pendingUpdates, pendingDeletes);
                }
            }
        }
        else
        {
            // Phase A: target × source scan.
            foreach (var (pageIndex, slotIndex, rowBytes) in destinationTable.Heap.EnumerateRowsWithAddress())
            {
                var targetValues = DecodeFullRow(destinationTable, rowBytes);
                EvaluateComputedColumns(destinationTable, targetValues, context.Batch);

                // View visibility filter: a base row not visible through the
                // view participates in neither the ON-predicate match nor the
                // BY-SOURCE enumeration. Mirrors UPDATE / DELETE through view
                // semantics.
                if (sourceView?.VisibilityCheck is { } vis && !vis(targetValues, context.Batch))
                    continue;

                // Find all matching source rows.
                var matchedSources = new List<int>();
                for (var si = 0; si < sourceRows.Count; si++)
                {
                    var pred = onPredicate.Run(new RuntimeContext(name => ResolveCombined(targetValues, sourceRows[si], name), context.Batch));
                    if (pred == true)
                    {
                        matchedSources.Add(si);
                        sourceMatched[si] = true;
                    }
                }

                if (matchedSources.Count > 0)
                {
                    var firstSourceIndex = matchedSources[0];
                    var sourceValues = sourceRows[firstSourceIndex];
                    var chosen = PickClause(whenClauses, WhenClauseKind.Matched, targetValues, sourceValues, context.Batch, ResolveCombined);
                    if (chosen is null)
                        continue;
                    if (chosen.Action == MergeActionKind.Update && matchedSources.Count > 1)
                        throw SimulatedSqlException.MergeMultiMatch();

                    ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, pageIndex, slotIndex, targetValues, sourceValues, ResolveCombined, pendingUpdates, pendingDeletes);
                }
                else
                {
                    var chosen = PickClause(whenClauses, WhenClauseKind.NotMatchedBySource, targetValues, sourceValues: null, context.Batch, ResolveCombined);
                    if (chosen is null)
                        continue;
                    ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, pageIndex, slotIndex, targetValues, sourceValues: null, ResolveCombined, pendingUpdates, pendingDeletes);
                }
            }
        }

        // Phase B: unmatched source rows → WHEN NOT MATCHED BY TARGET.
        var nmbtClause = whenClauses.FirstOrDefault(c => c.Kind == WhenClauseKind.NotMatchedByTarget);
        if (nmbtClause is not null)
        {
            // INSTEAD OF INSERT fires against the view when applicable;
            // otherwise the action targets the base table directly.
            var insteadOfInsertTarget = (SchemaObject?)sourceView ?? destinationTable;
            for (var si = 0; si < sourceRows.Count; si++)
            {
                if (sourceMatched[si])
                    continue;
                var sourceValues = sourceRows[si];
                if (nmbtClause.SearchCondition is { } cond
                    && cond.Run(new RuntimeContext(name => ResolveCombined(null, sourceValues, name), context.Batch)) != true)
                {
                    continue;
                }
                ApplyInsert(context, destinationTable, sourceView, nmbtClause, sourceValues, ResolveCombined, pendingInserts, insteadOfInsert: HasInsteadOfTrigger(context.Batch, insteadOfInsertTarget, TriggerActions.Insert));
            }
        }

        // Phase C: commit mutations.
        return CommitMerge(context, destinationTable, sourceView, pendingInserts, pendingUpdates, pendingDeletes, output, whenClauses);
    }

    /// <summary>
    /// Walks the WHEN clauses of a given kind in declaration order and
    /// returns the first one whose <c>AND</c> search condition is
    /// satisfied (or absent). Returns null when no clause of that kind
    /// applies.
    /// </summary>
    private static WhenClause? PickClause(
        List<WhenClause> clauses,
        WhenClauseKind kind,
        SqlValue[]? targetValues,
        SqlValue[]? sourceValues,
        BatchContext batch,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined)
    {
        foreach (var clause in clauses)
        {
            if (clause.Kind != kind)
                continue;
            if (clause.SearchCondition is { } cond)
            {
                var result = cond.Run(new RuntimeContext(name => resolveCombined(targetValues, sourceValues, name), batch));
                if (result != true)
                    continue;
            }
            return clause;
        }
        return null;
    }

    // Applies the WHEN MATCHED action to one matched target row, given the source
    // rows it matched (in source-index order, first wins). Picks the first
    // applicable MATCHED clause, enforces the multiple-source-match guard
    // (Msg 8672 for UPDATE), and queues the action. Shared by both inverted-seek
    // apply passes (matched-only and the NOT MATCHED BY SOURCE complement scan).
    private static void ApplyMergeMatched(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        List<WhenClause> whenClauses,
        int pageIndex,
        int slotIndex,
        SqlValue[] targetValues,
        List<SqlValue[]> sourceRows,
        List<int> matchedSources,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes)
    {
        var sourceValues = sourceRows[matchedSources[0]];
        var chosen = PickClause(whenClauses, WhenClauseKind.Matched, targetValues, sourceValues, context.Batch, resolveCombined);
        if (chosen is null)
            return;
        if (chosen.Action == MergeActionKind.Update && matchedSources.Count > 1)
            throw SimulatedSqlException.MergeMultiMatch();

        ApplyChosenMatchedAction(context, destinationTable, sourceView, chosen, pageIndex, slotIndex, targetValues, sourceValues, resolveCombined, pendingUpdates, pendingDeletes);
    }

    private static void ApplyChosenMatchedAction(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        WhenClause clause,
        int pageIndex,
        int slotIndex,
        SqlValue[] targetValues,
        SqlValue[]? sourceValues,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes)
    {
        if (clause.Action == MergeActionKind.Delete)
        {
            pendingDeletes.Add((pageIndex, slotIndex, targetValues, sourceValues));
            return;
        }
        // UPDATE: compute new row using assignments evaluated against the same pre-update snapshot.
        context.Batch.BumpRowStamp();
        var newValues = new SqlValue[destinationTable.Columns.Length];
        Array.Copy(targetValues, newValues, targetValues.Length);

        foreach (var (ord, expr) in clause.Assignments!)
        {
            var raw = expr.Run(new RuntimeContext(name => resolveCombined(targetValues, sourceValues, name), context.Batch));
            EnforceMaxLength(raw, destinationTable.Columns[ord], destinationTable.Name, context.Connection);
            newValues[ord] = CoerceForInsert(raw, destinationTable.Columns[ord].Type);
        }

        for (var ci = 0; ci < destinationTable.Columns.Length; ci++)
        {
            if (destinationTable.Columns[ci].Type == SqlType.RowVersion)
                newValues[ci] = SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion());
        }

        EvaluateComputedColumns(destinationTable, newValues, context.Batch);
        EnforceNotNull(destinationTable, newValues, "UPDATE");
        EnforceCheckConstraints(destinationTable, newValues, context.Batch, "UPDATE");

        // WITH CHECK OPTION: post-update row must still satisfy the view's
        // visibility chain. Raised before commit so a violating row leaves
        // the heap unchanged.
        if (sourceView?.CheckOptionCheck is { } co && !co(newValues, context.Batch))
            throw SimulatedSqlException.ViewCheckOptionViolation();

        pendingUpdates.Add((pageIndex, slotIndex, targetValues, newValues, sourceValues));
    }

    private static void ApplyInsert(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        WhenClause clause,
        SqlValue[] sourceValues,
        Func<SqlValue[]?, SqlValue[]?, MultiPartName, SqlValue> resolveCombined,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        bool insteadOfInsert)
    {
        context.Batch.BumpRowStamp();
        var rowValues = new SqlValue[destinationTable.Columns.Length];
        for (var i = 0; i < rowValues.Length; i++)
            rowValues[i] = SqlValue.Null(destinationTable.Columns[i].Type);

        var identityOrdinal = destinationTable.IdentityOrdinal;
        var identityColumn = identityOrdinal >= 0 ? destinationTable.Columns[identityOrdinal] : null;
        var identityInsertOn = identityColumn is not null
            && context.Connection.IdentityInsertTable is string activeTable
            && context.Batch.CurrentDatabase.Collation.Equals(activeTable, destinationTable.Name);

        var identityListed = false;
        for (var i = 0; i < clause.InsertColumns!.Length; i++)
        {
            if (ReferenceEquals(clause.InsertColumns[i], identityColumn))
            {
                identityListed = true;
                break;
            }
        }
        if (identityColumn is not null)
        {
            if (identityListed && !identityInsertOn)
                throw SimulatedSqlException.CannotInsertExplicitIdentity(destinationTable.Name);
            if (!identityListed && identityInsertOn)
                throw SimulatedSqlException.ExplicitIdentityRequired(destinationTable.Name);
        }

        // Defaults for columns absent from the INSERT branch's list.
        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            var column = destinationTable.Columns[i];
            if (column.Default is null) continue;
            var listed = false;
            for (var j = 0; j < clause.InsertColumns.Length; j++)
            {
                if (ReferenceEquals(clause.InsertColumns[j], column))
                {
                    listed = true;
                    break;
                }
            }
            if (listed) continue;
            var defaultValue = column.Default.Run(new RuntimeContext(name => throw SimulatedSqlException.InvalidColumnName(name), context.Batch));
            rowValues[i] = CoerceForInsert(defaultValue, column.Type);
        }

        for (var i = 0; i < clause.InsertColumns.Length; i++)
        {
            var targetColumn = clause.InsertColumns[i];
            var ordinal = -1;
            for (var j = 0; j < destinationTable.Columns.Length; j++)
            {
                if (ReferenceEquals(destinationTable.Columns[j], targetColumn))
                {
                    ordinal = j;
                    break;
                }
            }
            var source = clause.InsertValues![i].Run(new RuntimeContext(name => resolveCombined(null, sourceValues, name), context.Batch));
            EnforceMaxLength(source, targetColumn, destinationTable.Name, context.Connection);
            var coerced = CoerceForInsert(source, targetColumn.Type);
            rowValues[ordinal] = coerced;

            if (ReferenceEquals(targetColumn, identityColumn))
            {
                var explicitValue = coerced.CoerceTo(SqlType.BigInt).AsInt64;
                identityColumn.Identity!.ObserveExplicit(explicitValue);
            }
        }

        if (identityColumn is not null && !identityListed)
        {
            if (insteadOfInsert)
            {
                // INSTEAD OF INSERT: typed default for identity, matching
                // the table-target INSERT path (probe-confirmed).
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
            }
        }

        for (var i = 0; i < destinationTable.Columns.Length; i++)
        {
            if (destinationTable.Columns[i].Type == SqlType.RowVersion)
                rowValues[i] = SqlValue.FromRowVersion(context.CurrentDatabase.AllocateRowVersion());
        }

        EvaluateComputedColumns(destinationTable, rowValues, context.Batch);
        if (!insteadOfInsert)
        {
            EnforceNotNull(destinationTable, rowValues);
            EnforceCheckConstraints(destinationTable, rowValues, context.Batch);
        }

        // WITH CHECK OPTION on the post-insert row, matching INSERT-through-view.
        // Skipped for INSTEAD OF INSERT because the trigger body's own DML is
        // what actually lands a row; the view's CheckOption only gates direct
        // heap writes through it.
        if (!insteadOfInsert && sourceView?.CheckOptionCheck is { } co && !co(rowValues, context.Batch))
            throw SimulatedSqlException.ViewCheckOptionViolation();

        pendingInserts.Add((rowValues, sourceValues));
    }

    private static SimulatedStatementOutcome CommitMerge(
        ParserContext context,
        HeapTable destinationTable,
        View? sourceView,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes,
        OutputProjection? output,
        List<WhenClause> whenClauses)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        // UPDATE(col) / COLUMNS_UPDATED() report the statement's SET-clause
        // membership rather than what any row actually changed, so the mask
        // is the union of every WHEN MATCHED THEN UPDATE clause's targets
        // whether or not that clause fired.
        var updatedColumnOrdinals = new List<int>();
        foreach (var clause in whenClauses)
        {
            if (clause.Action != MergeActionKind.Update || clause.Assignments is null)
                continue;
            foreach (var (ordinal, _) in clause.Assignments)
            {
                if (!updatedColumnOrdinals.Contains(ordinal))
                    updatedColumnOrdinals.Add(ordinal);
            }
        }

        // Per-action INSTEAD OF detection. INSTEAD OF triggers live on the
        // view when the target is a view, otherwise on the base table.
        // When set, the corresponding pending list bypasses the heap-write +
        // AFTER-trigger path and fires its INSTEAD OF trigger with would-be
        // values. Real SQL Server allows a mixed MERGE where, say, INSERT
        // routes through INSTEAD OF while UPDATE writes to the heap normally
        // — each action is decided independently.
        var insteadOfTarget = (SchemaObject?)sourceView ?? destinationTable;
        var insteadOfInsert = pendingInserts.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Insert);
        var insteadOfUpdate = pendingUpdates.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Update);
        var insteadOfDelete = pendingDeletes.Count > 0 && HasInsteadOfTrigger(context.Batch, insteadOfTarget, TriggerActions.Delete);

        // Validate key constraints across only the actions that actually
        // hit the heap. INSTEAD OF action lists bypass key checks since
        // they never reach the heap — the trigger body's own DML lands
        // with its own validation.
        if ((!insteadOfInsert && pendingInserts.Count > 0) || (!insteadOfUpdate && pendingUpdates.Count > 0))
        {
            var pseudoAffected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
            if (!insteadOfUpdate)
            {
                foreach (var (page, slot, oldValues, newValues, _) in pendingUpdates)
                    pseudoAffected.Add((page, slot, newValues, oldValues));
            }
            if (!insteadOfInsert)
            {
                // For inserts, the "address" of the new row doesn't exist yet;
                // (-1, i) is sentinel — never collides with a real heap address.
                for (var i = 0; i < pendingInserts.Count; i++)
                    pseudoAffected.Add((-1, i, pendingInserts[i].NewValues, FullOld: null));
            }
            if (pseudoAffected.Count > 0)
            {
                EnforceKeyConstraintsForUpdate(destinationTable, pseudoAffected);
                EnforceUniqueIndexesForUpdate(destinationTable, pseudoAffected, context.Batch);
            }
        }

        var undoLog = destinationTable.IsTableVariable ? context.Batch.CurrentTableVarUndoLog : context.Batch.CurrentUndoLog;

        // Outgoing FK validation on inserts + updates (the post-image rows
        // that are about to land in the heap). Fires before mutation so a
        // violation rolls back cleanly via the statement-atomic exception.
        if (destinationTable.OutgoingForeignKeys.Count > 0)
        {
            var newRows = new List<SqlValue[]>(pendingInserts.Count + pendingUpdates.Count);
            if (!insteadOfInsert)
            {
                foreach (var (newValues, _) in pendingInserts)
                    newRows.Add(newValues);
            }
            if (!insteadOfUpdate)
            {
                foreach (var (_, _, _, newValues, _) in pendingUpdates)
                    newRows.Add(newValues);
            }
            if (newRows.Count > 0)
                EnforceOutgoingForeignKeys(destinationTable, newRows, context, "MERGE");
        }

        // Apply heap operations only for non-INSTEAD-OF actions.
        var lockableTable = IsLockableTable(destinationTable);
        if (!insteadOfDelete)
        {
            foreach (var (page, slot, _, _) in pendingDeletes)
            {
                if (lockableTable)
                    context.Batch.AcquireRowLockTxScoped(destinationTable, page, slot, LockMode.Exclusive);
                destinationTable.Heap.DeleteAt(page, slot, undoLog, ReclaimSuperseded(destinationTable, context));
            }
        }
        if (!insteadOfUpdate)
        {
            foreach (var (page, slot, _, newValues, _) in pendingUpdates)
            {
                if (lockableTable)
                    context.Batch.AcquireRowLockTxScoped(destinationTable, page, slot, LockMode.Exclusive);
                destinationTable.Heap.UpdateAt(page, slot, RowEncoder.EncodeRow(destinationTable.StoredColumns, ProjectStoredValues(destinationTable, newValues), destinationTable.Heap), undoLog, ReclaimSuperseded(destinationTable, context));
            }
        }
        if (!insteadOfInsert)
        {
            foreach (var (newValues, _) in pendingInserts)
            {
                var (newPage, newSlot) = destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, ProjectStoredValues(destinationTable, newValues), destinationTable.Heap), undoLog);
                if (lockableTable)
                    context.Batch.AcquireRowLockTxScoped(destinationTable, newPage, newSlot, LockMode.Exclusive);
            }
        }

        // Incoming-FK cascade for MERGE's DELETE/UPDATE actions on the
        // destination. INSTEAD OF paths bypass (the trigger handles its own
        // DML).
        if (destinationTable.IncomingForeignKeys.Count > 0)
        {
            if (!insteadOfDelete && pendingDeletes.Count > 0)
            {
                var oldRows = new List<SqlValue[]>(pendingDeletes.Count);
                foreach (var (_, _, oldValues, _) in pendingDeletes)
                    oldRows.Add(oldValues);
                EnforceIncomingForeignKeysOnDelete(destinationTable, oldRows, context, "DELETE", depth: 0);
            }
            if (!insteadOfUpdate && pendingUpdates.Count > 0)
            {
                var pairs = new List<(SqlValue[] OldFull, SqlValue[] NewFull)>(pendingUpdates.Count);
                foreach (var (_, _, oldValues, newValues, _) in pendingUpdates)
                    pairs.Add((oldValues, newValues));
                EnforceIncomingFkOnUpdate(destinationTable, pairs, context, depth: 0);
            }
        }

        // Identity counter: only advances when the inserts actually hit the
        // heap. INSTEAD OF INSERT doesn't allocate identity, so no update
        // to LastIdentity is needed.
        if (!insteadOfInsert && destinationTable.IdentityOrdinal >= 0 && pendingInserts.Count > 0)
        {
            var lastId = pendingInserts[^1].NewValues[destinationTable.IdentityOrdinal];
            context.Connection.LastIdentity = lastId.IsNull ? null : lastId.CoerceTo(SqlType.BigInt).AsInt64;
        }

        // Build OUTPUT result: INSERT rows, then UPDATE rows, then DELETE rows.
        var outputRows = output is null ? null : new List<byte[]>();
        if (output is not null)
        {
            var nullTarget = new SqlValue[destinationTable.Columns.Length];
            for (var i = 0; i < nullTarget.Length; i++)
                nullTarget[i] = SqlValue.Null(destinationTable.Columns[i].Type);

            foreach (var (newValues, sourceValues) in pendingInserts)
            {
                var bytes = output.ProjectRow(insertedValues: newValues, deletedValues: nullTarget, sourceValues: sourceValues, action: "INSERT");
                if (bytes is not null)
                    outputRows!.Add(bytes);
            }
            foreach (var (_, _, oldValues, newValues, sourceValues) in pendingUpdates)
            {
                var bytes = output.ProjectRow(insertedValues: newValues, deletedValues: oldValues, sourceValues: sourceValues, action: "UPDATE");
                if (bytes is not null)
                    outputRows!.Add(bytes);
            }
            foreach (var (_, _, oldValues, sourceValues) in pendingDeletes)
            {
                var bytes = output.ProjectRow(insertedValues: nullTarget, deletedValues: oldValues, sourceValues: sourceValues, action: "DELETE");
                if (bytes is not null)
                    outputRows!.Add(bytes);
            }
        }

        // Fire triggers in INSERT → UPDATE → DELETE order (probe-confirmed).
        // For each action, route to INSTEAD OF if attached, else AFTER (if
        // attached). The branch-presence checks short-circuit when there's
        // no trigger of either timing for the action.
        // INSTEAD OF triggers see view-shaped INSERTED / DELETED columns
        // when the target is a view; AFTER triggers continue to fire against
        // the base table with base-shaped values (AFTER triggers on views
        // aren't a thing in SQL Server).
        var pseudoColumns = sourceView?.OutputColumns ?? destinationTable.Columns;

        var totalAffected = pendingInserts.Count + pendingUpdates.Count + pendingDeletes.Count;
        if (pendingInserts.Count > 0)
        {
            var insertedRows = new List<SqlValue[]>(pendingInserts.Count);
            foreach (var (newValues, _) in pendingInserts)
                insertedRows.Add(newValues);
            if (insteadOfInsert)
            {
                var insertedViewRows = sourceView is null
                    ? insertedRows
                    : insertedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingInserts.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Insert,
                    pseudoColumns, insertedViewRows, deletedRows: null,
                    affectedRowCount: pendingInserts.Count);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Insert))
            {
                context.Connection.LastStatementRowCount = pendingInserts.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Insert,
                    insertedRows: insertedRows, deletedRows: null,
                    affectedRowCount: pendingInserts.Count);
            }
        }
        if (pendingUpdates.Count > 0)
        {
            var insertedRows = new List<SqlValue[]>(pendingUpdates.Count);
            var deletedRows = new List<SqlValue[]>(pendingUpdates.Count);
            foreach (var (_, _, oldValues, newValues, _) in pendingUpdates)
            {
                insertedRows.Add(newValues);
                deletedRows.Add(oldValues);
            }
            if (insteadOfUpdate)
            {
                var insertedViewRows = sourceView is null
                    ? insertedRows
                    : insertedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                var deletedViewRows = sourceView is null
                    ? deletedRows
                    : deletedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Update,
                    pseudoColumns, insertedViewRows, deletedViewRows,
                    affectedRowCount: pendingUpdates.Count, updatedColumnOrdinals);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Update))
            {
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Update,
                    insertedRows: insertedRows, deletedRows: deletedRows,
                    affectedRowCount: pendingUpdates.Count, updatedColumnOrdinals);
            }
        }
        if (pendingDeletes.Count > 0)
        {
            var deletedRows = new List<SqlValue[]>(pendingDeletes.Count);
            foreach (var (_, _, oldValues, _) in pendingDeletes)
                deletedRows.Add(oldValues);
            if (insteadOfDelete)
            {
                var deletedViewRows = sourceView is null
                    ? deletedRows
                    : deletedRows.ConvertAll(r => ProjectThroughView(sourceView, r));
                context.Connection.LastStatementRowCount = pendingDeletes.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, insteadOfTarget, TriggerActions.Delete,
                    pseudoColumns, insertedRows: null, deletedRows: deletedViewRows,
                    affectedRowCount: pendingDeletes.Count);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Delete))
            {
                context.Connection.LastStatementRowCount = pendingDeletes.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Delete,
                    insertedRows: null, deletedRows: deletedRows,
                    affectedRowCount: pendingDeletes.Count);
            }
        }

        context.Connection.LastStatementRowCount = totalAffected;
        // An INTO target consumed the rows, so the statement is a non-query —
        // the same suppression INSERT / UPDATE / DELETE apply.
        return output is { HasTarget: false }
            ? new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!)
            : new SimulatedNonQuery(totalAffected);
    }

    /// <summary>
    /// Parses one or more comma-separated <c>(...)</c> tuples following a
    /// <c>VALUES</c> keyword. Enters with <see cref="ParserContext.Token"/>
    /// on <c>VALUES</c>; on return the cursor sits on the first token
    /// after the last tuple's closing paren. Shared with INSERT and with
    /// the table-value-constructor derived table (<c>(VALUES …) alias(cols)</c>)
    /// parsed in <see cref="Selection"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="allowDefault"/> is set only by the INSERT VALUES path:
    /// when true, a bare <c>DEFAULT</c> keyword in a tuple position yields the
    /// <see cref="Parser.Expressions.DefaultValueExpression"/> sentinel (the
    /// INSERT encoder resolves it per target column). The FROM-clause
    /// table-value constructor leaves it false, so <c>DEFAULT</c> there falls
    /// through to <see cref="Expression.Parse"/> and raises Msg 156 — matching
    /// SQL Server, which permits <c>DEFAULT</c> only inside <c>INSERT … VALUES</c>.
    /// </remarks>
    internal static List<Expression[]> ParseValuesTuples(ParserContext context, bool allowDefault = false)
    {
        var tuples = new List<Expression[]>();
        do
        {
            if (context.GetNextRequired() is not Operator { Character: '(' })
                throw SimulatedSqlException.SyntaxErrorNear(context);

            var values = new List<Expression>();
            while (true)
            {
                context.MoveNextRequired();
                if (context.Token is Operator { Character: ',' or ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                if (allowDefault && context.Token is ReservedKeyword { Keyword: Keyword.Default })
                {
                    values.Add(Parser.Expressions.DefaultValueExpression.Instance);
                    context.MoveNextRequired();
                }
                else
                {
                    values.Add(Expression.Parse(context));
                }
                if (context.Token is Operator { Character: ')' })
                    break;
                if (context.Token is not Operator { Character: ',' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }

            tuples.Add([.. values]);
        }
        while (context.GetNextOptional() is Operator { Character: ',' });

        return tuples;
    }

    private enum WhenClauseKind
    {
        Matched,
        NotMatchedByTarget,
        NotMatchedBySource,
    }

    private enum MergeActionKind
    {
        Insert,
        Update,
        Delete,
    }

    private sealed class WhenClause(
        WhenClauseKind kind,
        MergeActionKind action,
        BooleanExpression? searchCondition,
        List<(int Ordinal, Expression Expr)>? assignments,
        HeapColumn[]? insertColumns,
        Expression[]? insertValues)
    {
        public readonly WhenClauseKind Kind = kind;
        public readonly MergeActionKind Action = action;
        public readonly BooleanExpression? SearchCondition = searchCondition;
        public readonly List<(int Ordinal, Expression Expr)>? Assignments = assignments;
        public readonly HeapColumn[]? InsertColumns = insertColumns;
        public readonly Expression[]? InsertValues = insertValues;
    }

    /// <summary>
    /// Synthetic expression representing MERGE's <c>$action</c> pseudo-
    /// column. Runtime evaluation goes through the <see cref="OutputProjection.ProjectRow"/>
    /// per-row context which threads the action verb in directly; the
    /// <see cref="Expression.Run"/> override here exists only to satisfy
    /// the Expression contract and isn't called on the MERGE OUTPUT path.
    /// </summary>
    private sealed class MergeActionReference : Expression
    {
        public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType>? resolver) => NVarcharSqlType.Get(10, batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault);
        public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(NVarcharSqlType.Get(10, runtime.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault));
        internal override string DebugDisplay() => "$action";
    }

    /// <summary>
    /// Drills past any <see cref="Parser.Expressions.NamedExpression"/>
    /// wrapper (from <c>AS alias</c>) to detect the
    /// <see cref="MergeActionReference"/> pseudo-column at any nesting
    /// depth.
    /// </summary>
    private static bool IsMergeActionRef(Expression expr) =>
        expr switch
        {
            MergeActionReference => true,
            Parser.Expressions.NamedExpression n => IsMergeActionRef(n.Inner),
            _ => false,
        };

}
