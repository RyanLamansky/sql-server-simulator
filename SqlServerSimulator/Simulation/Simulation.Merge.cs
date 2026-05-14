using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Tokens;
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
        var destinationTable = context.Batch.TryResolveTable(destinationName, out var table)
            ? table
            : throw (BatchContext.IsTableVariableName(destinationName.Leaf)
                ? SimulatedSqlException.MustDeclareTableVariable(destinationName.Leaf)
                : SimulatedSqlException.InvalidObjectName(destinationName));
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
        _ = context.Batch.AcquireDataLockIfApplicable(destinationTable, default, isWrite: true);

        // Optional target alias: AS <alias> or bare <alias>.
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            context.MoveNextRequired();
        var targetAlias = context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Using } => destinationTable.Name,
            Name n => n.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (!Collation.Default.Equals(targetAlias, destinationTable.Name))
            context.MoveNextRequired();

        // USING (<source>) [AS] alias [(col, ...)]
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Using })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var (materializeSource, sourceAlias, sourceColumnNames, sourceSchema) = ParseMergeSource(context);

        // ON predicate — resolves target via targetAlias/destinationName, source via sourceAlias.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        SqlType ResolveTypeBoth(MultiPartName name)
        {
            if (Collation.Default.Equals(name.ImmediateQualifier, targetAlias)
                || Collation.Default.Equals(name.ImmediateQualifier, destinationTable.Name))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
            }
            if (Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceSchema[i];
                }
            }
            // Unqualified: try target then source.
            if (name.Count == 1)
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceSchema[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

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
        var whenClauses = ParseMergeWhenClauses(context, destinationTable, targetAlias, sourceAlias, sourceColumnNames, sourceSchema);

        // OUTPUT.
        var output = TryParseMergeOutputClause(context, destinationTable, sourceAlias, sourceColumnNames, sourceSchema);

        // Required trailing ; — but the dispatch loop may have already
        // consumed it (statement separators are flexible). If the cursor
        // sits on either ; or end-of-batch, accept; otherwise raise Msg 10713.
        return context.Token is not (null or Operator { Character: ';' })
            ? throw SimulatedSqlException.MergeMustBeTerminated()
            : ExecuteMerge(context, destinationTable, targetAlias, materializeSource, sourceAlias, sourceColumnNames, sourceSchema, onPredicate, whenClauses, output);
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
                sourceSchema[i] = tuples[0][i].GetSqlType(name => throw SimulatedSqlException.InvalidColumnName(name));
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
    private static List<WhenClause> ParseMergeWhenClauses(
        ParserContext context,
        HeapTable destinationTable,
        string targetAlias,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        var clauses = new List<WhenClause>();
        var matchedUnconditionalSeen = false;
        var nmbsUnconditionalSeen = false;
        var nmbtSeen = false;

        SqlType ResolveType(MultiPartName name)
        {
            if (Collation.Default.Equals(name.ImmediateQualifier, targetAlias)
                || Collation.Default.Equals(name.ImmediateQualifier, destinationTable.Name))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
            }
            if (Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceSchema[i];
                }
            }
            if (name.Count == 1)
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceSchema[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

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

            clauses.Add(ParseMergeAction(context, kind, searchCondition, destinationTable, ResolveType));
        }

        return clauses.Count == 0 ? throw SimulatedSqlException.SyntaxErrorNear(context) : clauses;
    }

    private static WhenClause ParseMergeAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        Func<MultiPartName, SqlType> resolveType) =>
        context.Token switch
        {
            ReservedKeyword { Keyword: Keyword.Insert } => ParseMergeInsertAction(context, kind, searchCondition, destinationTable, resolveType),
            ReservedKeyword { Keyword: Keyword.Update } => ParseMergeUpdateAction(context, kind, searchCondition, destinationTable, resolveType),
            ReservedKeyword { Keyword: Keyword.Delete } => ParseMergeDeleteAction(context, kind, searchCondition),
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };

    private static WhenClause ParseMergeInsertAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
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
                var col = destinationTable.Columns.FirstOrDefault(c => Collation.Default.Equals(c.Name, colTok.Value))
                    ?? throw SimulatedSqlException.InvalidColumnName(colTok.Value);
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
            var defaultCols = new List<HeapColumn>();
            foreach (var c in destinationTable.Columns)
            {
                if (c.Computed is null && c.Type != SqlType.RowVersion)
                    defaultCols.Add(c);
            }
            columns = [.. defaultCols];
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

                var ordinal = -1;
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, columnName))
                    {
                        ordinal = i;
                        break;
                    }
                }
                if (ordinal < 0)
                    throw SimulatedSqlException.InvalidColumnName(columnName);
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
    private static MergeOutputProjection? TryParseMergeOutputClause(
        ParserContext context,
        HeapTable destinationTable,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;

        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(MultiPartName name)
        {
            if (Collation.Default.Equals(name.ImmediateQualifier, "INSERTED")
                || Collation.Default.Equals(name.ImmediateQualifier, "DELETED"))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return destinationTable.Columns[i].Type;
                }
            }
            else if (Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
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
                if (Collation.Default.Equals(starQualifier, "INSERTED")
                    || Collation.Default.Equals(starQualifier, "DELETED"))
                {
                    cols = new string[destinationTable.Columns.Length];
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                        cols[i] = destinationTable.Columns[i].Name;
                }
                else if (Collation.Default.Equals(starQualifier, sourceAlias))
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
            schema[i] = IsMergeActionRef(expressions[i]) ? NVarcharSqlType.Get(10) : expressions[i].GetSqlType(ResolveOutputType);

        return new MergeOutputProjection(
            [.. expressions], [.. columnNames], schema,
            destinationTable, sourceAlias, sourceColumnNames, context.Batch);
    }

    /// <summary>
    /// Runs the prepared MERGE plan against the live target heap. The
    /// source <see cref="Selection"/> materializes into a list once;
    /// each target row is scanned, its action chosen via the first
    /// applicable WHEN clause, and queued. Unmatched source rows fall
    /// into the <c>WHEN NOT MATCHED [BY TARGET]</c> clause if present.
    /// All queued mutations apply atomically before triggers fire.
    /// </summary>
    private static SimulatedStatementOutcome ExecuteMerge(
        ParserContext context,
        HeapTable destinationTable,
        string targetAlias,
        Func<BatchContext, List<SqlValue[]>> materializeSource,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        BooleanExpression onPredicate,
        List<WhenClause> whenClauses,
        MergeOutputProjection? output)
    {
        var sourceRows = materializeSource(context.Batch);
        var sourceMatched = new bool[sourceRows.Count];

        // Resolve target columns by qualifier; null-source means BY-SOURCE branch (everything in source resolver returns NULL).
        SqlValue ResolveCombined(SqlValue[]? targetValues, SqlValue[]? sourceValues, MultiPartName name)
        {
            if (Collation.Default.Equals(name.ImmediateQualifier, targetAlias)
                || Collation.Default.Equals(name.ImmediateQualifier, destinationTable.Name))
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return targetValues is null ? SqlValue.Null(destinationTable.Columns[i].Type) : targetValues[i];
                }
            }
            if (Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            if (name.Count == 1)
            {
                for (var i = 0; i < destinationTable.Columns.Length; i++)
                {
                    if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                        return targetValues is null ? SqlValue.Null(destinationTable.Columns[i].Type) : targetValues[i];
                }
                for (var i = 0; i < sourceColumnNames.Length; i++)
                {
                    if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                        return sourceValues is null ? SqlValue.Null(sourceSchema[i]) : sourceValues[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

        var pendingInserts = new List<(SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingUpdates = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)>();
        var pendingDeletes = new List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)>();

        // Phase A: target × source scan.
        foreach (var (pageIndex, slotIndex, rowBytes) in destinationTable.Heap.EnumerateRowsWithAddress())
        {
            var targetValues = DecodeFullRow(destinationTable, rowBytes);
            EvaluateComputedColumns(destinationTable, targetValues, context.Batch);

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

                ApplyChosenMatchedAction(context, destinationTable, chosen, pageIndex, slotIndex, targetValues, sourceValues, ResolveCombined, pendingUpdates, pendingDeletes);
            }
            else
            {
                var chosen = PickClause(whenClauses, WhenClauseKind.NotMatchedBySource, targetValues, sourceValues: null, context.Batch, ResolveCombined);
                if (chosen is null)
                    continue;
                ApplyChosenMatchedAction(context, destinationTable, chosen, pageIndex, slotIndex, targetValues, sourceValues: null, ResolveCombined, pendingUpdates, pendingDeletes);
            }
        }

        // Phase B: unmatched source rows → WHEN NOT MATCHED BY TARGET.
        var nmbtClause = whenClauses.FirstOrDefault(c => c.Kind == WhenClauseKind.NotMatchedByTarget);
        if (nmbtClause is not null)
        {
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
                ApplyInsert(context, destinationTable, nmbtClause, sourceValues, ResolveCombined, pendingInserts, insteadOfInsert: HasInsteadOfTrigger(context.Batch, destinationTable, TriggerActions.Insert));
            }
        }

        // Phase C: commit mutations.
        return CommitMerge(context, destinationTable, pendingInserts, pendingUpdates, pendingDeletes, output);
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

    private static void ApplyChosenMatchedAction(
        ParserContext context,
        HeapTable destinationTable,
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

        pendingUpdates.Add((pageIndex, slotIndex, targetValues, newValues, sourceValues));
    }

    private static void ApplyInsert(
        ParserContext context,
        HeapTable destinationTable,
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
            && Collation.Default.Equals(activeTable, destinationTable.Name);

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

        pendingInserts.Add((rowValues, sourceValues));
    }

    private static SimulatedStatementOutcome CommitMerge(
        ParserContext context,
        HeapTable destinationTable,
        List<(SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingInserts,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[] NewValues, SqlValue[]? SourceValues)> pendingUpdates,
        List<(int Page, int Slot, SqlValue[] OldValues, SqlValue[]? SourceValues)> pendingDeletes,
        MergeOutputProjection? output)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);

        // Per-action INSTEAD OF detection. When set, the corresponding
        // pending list bypasses the heap-write + AFTER-trigger path and
        // fires its INSTEAD OF trigger with would-be values. Real SQL
        // Server allows a mixed MERGE where, say, INSERT routes through
        // INSTEAD OF while UPDATE writes to the heap normally — each
        // action is decided independently.
        var insteadOfInsert = pendingInserts.Count > 0 && HasInsteadOfTrigger(context.Batch, destinationTable, TriggerActions.Insert);
        var insteadOfUpdate = pendingUpdates.Count > 0 && HasInsteadOfTrigger(context.Batch, destinationTable, TriggerActions.Update);
        var insteadOfDelete = pendingDeletes.Count > 0 && HasInsteadOfTrigger(context.Batch, destinationTable, TriggerActions.Delete);

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
                destinationTable.Heap.DeleteAt(page, slot, undoLog);
            }
        }
        if (!insteadOfUpdate)
        {
            foreach (var (page, slot, _, _, _) in pendingUpdates)
            {
                if (lockableTable)
                    context.Batch.AcquireRowLockTxScoped(destinationTable, page, slot, LockMode.Exclusive);
                destinationTable.Heap.DeleteAt(page, slot, undoLog);
            }
            foreach (var (_, _, _, newValues, _) in pendingUpdates)
            {
                var (newPage, newSlot) = destinationTable.Heap.Insert(RowEncoder.EncodeRow(destinationTable.StoredColumns, ProjectStoredValues(destinationTable, newValues), destinationTable.Heap), undoLog);
                if (lockableTable)
                    context.Batch.AcquireRowLockTxScoped(destinationTable, newPage, newSlot, LockMode.Exclusive);
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
                EnforceIncomingForeignKeys(destinationTable, oldRows, affectedNewValues: null, context, "DELETE", depth: 0);
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
        var totalAffected = pendingInserts.Count + pendingUpdates.Count + pendingDeletes.Count;
        if (pendingInserts.Count > 0)
        {
            var insertedRows = new List<SqlValue[]>(pendingInserts.Count);
            foreach (var (newValues, _) in pendingInserts)
                insertedRows.Add(newValues);
            if (insteadOfInsert)
            {
                context.Connection.LastStatementRowCount = pendingInserts.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, destinationTable, TriggerActions.Insert,
                    destinationTable.Columns, insertedRows, deletedRows: null,
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
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, destinationTable, TriggerActions.Update,
                    destinationTable.Columns, insertedRows, deletedRows,
                    affectedRowCount: pendingUpdates.Count);
            }
            else if (HasAfterTrigger(context.Batch, destinationTable, TriggerActions.Update))
            {
                context.Connection.LastStatementRowCount = pendingUpdates.Count;
                context.Batch.Connection.Simulation.FireTriggers(
                    context.Batch, destinationTable, TriggerActions.Update,
                    insertedRows: insertedRows, deletedRows: deletedRows,
                    affectedRowCount: pendingUpdates.Count);
            }
        }
        if (pendingDeletes.Count > 0)
        {
            var deletedRows = new List<SqlValue[]>(pendingDeletes.Count);
            foreach (var (_, _, oldValues, _) in pendingDeletes)
                deletedRows.Add(oldValues);
            if (insteadOfDelete)
            {
                context.Connection.LastStatementRowCount = pendingDeletes.Count;
                _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
                    context.Batch, destinationTable, TriggerActions.Delete,
                    destinationTable.Columns, insertedRows: null, deletedRows: deletedRows,
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
        return output is not null
            ? new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!)
            : new SimulatedNonQuery(totalAffected);
    }

    /// <summary>
    /// Parses one or more comma-separated <c>(...)</c> tuples following a
    /// <c>VALUES</c> keyword. Enters with <see cref="ParserContext.Token"/>
    /// on <c>VALUES</c>; on return the cursor sits on the first token
    /// after the last tuple's closing paren. Shared with INSERT.
    /// </summary>
    private static List<Expression[]> ParseValuesTuples(ParserContext context)
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
                values.Add(Expression.Parse(context));
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
    /// column. Runtime evaluation goes through the <see cref="MergeOutputProjection.ProjectRow"/>
    /// per-row context which threads the action verb in directly; the
    /// <see cref="Expression.Run"/> override here exists only to satisfy
    /// the Expression contract and isn't called on the MERGE OUTPUT path.
    /// </summary>
    private sealed class MergeActionReference : Expression
    {
        public override SqlType GetSqlType(Func<MultiPartName, SqlType>? resolver) => NVarcharSqlType.Get(10);
        public override SqlValue Run(RuntimeContext runtime) => SqlValue.Null(NVarcharSqlType.Get(10));
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

    /// <summary>
    /// MERGE-flavored output projection. Mirrors <see cref="OutputProjection"/>'s
    /// shape but resolves INSERTED + DELETED + source-alias + <c>$action</c>
    /// references; the per-row caller supplies the action verb directly
    /// rather than the projection inferring it.
    /// </summary>
    private sealed class MergeOutputProjection(
        Expression[] expressions,
        string[] columnNames,
        SqlType[] schema,
        HeapTable destinationTable,
        string sourceAlias,
        string[] sourceColumnNames,
        BatchContext batch)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;

        public byte[]? ProjectRow(SqlValue[]? insertedValues, SqlValue[]? deletedValues, SqlValue[]? sourceValues, string action)
        {
            SqlValue Resolve(MultiPartName name)
            {
                if (Collation.Default.Equals(name.ImmediateQualifier, "INSERTED"))
                {
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                            return insertedValues is null ? SqlValue.Null(destinationTable.Columns[i].Type) : insertedValues[i];
                    }
                }
                else if (Collation.Default.Equals(name.ImmediateQualifier, "DELETED"))
                {
                    for (var i = 0; i < destinationTable.Columns.Length; i++)
                    {
                        if (Collation.Default.Equals(destinationTable.Columns[i].Name, name.Leaf))
                            return deletedValues is null ? SqlValue.Null(destinationTable.Columns[i].Type) : deletedValues[i];
                    }
                }
                else if (Collation.Default.Equals(name.ImmediateQualifier, sourceAlias))
                {
                    for (var i = 0; i < sourceColumnNames.Length; i++)
                    {
                        if (Collation.Default.Equals(sourceColumnNames[i], name.Leaf))
                            return sourceValues is null ? SqlValue.Null(this.Schema[0]) : sourceValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
            }

            var projected = new SqlValue[expressions.Length];
            for (var i = 0; i < expressions.Length; i++)
            {
                projected[i] = IsMergeActionRef(expressions[i])
                    ? SqlValue.FromNVarchar(action)
                    : expressions[i].Run(new RuntimeContext(Resolve, batch));
            }
            return RowEncoder.EncodeRow(this.Schema, projected);
        }
    }
}
