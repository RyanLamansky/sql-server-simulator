using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Detects an <c>&lt;qualifier&gt;.*</c> star reference at the current
    /// cursor position. When present, leaves the cursor on the <c>*</c>
    /// operator (caller appends the expansion then calls
    /// <see cref="ParserContext.MoveNextOptional"/>) and returns the
    /// qualifier name. When absent (the cursor sees a regular expression
    /// or a qualified non-star reference), restores to the original
    /// position and returns <see langword="false"/> so the caller can
    /// fall through to <see cref="Expression.Parse"/>.
    /// </summary>
    /// <remarks>
    /// Probe-confirmed against SQL Server 2025 (2026-05-13): <c>INSERTED.*</c>
    /// expands to every column of the destination table in storage order
    /// (including identity / computed / default-bearing columns); <c>DELETED.*</c>
    /// is the same shape; in MERGE OUTPUT the source-alias <c>s.*</c>
    /// expands to the source's projected columns. Each expanded column
    /// takes the underlying column's leaf name (not the qualified form).
    /// <c>foo.* AS alias</c> isn't a valid shape — real SQL Server raises
    /// Msg 102 ("Incorrect syntax near 'alias'"); the simulator inherits
    /// that since star expansion advances past <c>*</c> and the parser
    /// loop's alias-check sees the next token as either <c>,</c> or
    /// statement-boundary, never a bare Name.
    /// </remarks>
    private static bool TryDetectStarReference(ParserContext context, out string qualifier)
    {
        qualifier = string.Empty;
        if (context.Token is not Name namedToken)
            return false;
        var name = namedToken.Value;
        var checkpoint = context.SaveCheckpoint();
        if (!context.MoveNext() || context.Token is not Operator { Character: '.' })
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }
        if (!context.MoveNext() || context.Token is not Operator { Character: '*' })
        {
            context.RestoreCheckpoint(checkpoint);
            return false;
        }
        qualifier = name;
        return true;
    }

    /// <summary>
    /// Appends one synthesized <see cref="Reference"/> per column in
    /// <paramref name="columnNames"/> qualified by
    /// <paramref name="qualifier"/>. Used by all three OUTPUT parsers
    /// (mutation / insert / merge) for <c>INSERTED.* / DELETED.* /
    /// &lt;source&gt;.*</c> expansion. The qualified Reference resolves
    /// through the same per-row resolver that handles regular
    /// <c>INSERTED.col</c> refs — no separate runtime path.
    /// </summary>
    private static void AppendStarExpansion(
        string qualifier,
        string[] columnNames,
        List<Expression> expressions,
        List<string> names)
    {
        for (var i = 0; i < columnNames.Length; i++)
        {
            expressions.Add(new Reference(qualifier, columnNames[i]));
            names.Add(columnNames[i]);
        }
    }

    /// <summary>
    /// OUTPUT-clause parser for UPDATE / DELETE. Supports literal /
    /// parameter expressions and <c>INSERTED.&lt;col&gt;</c> /
    /// <c>DELETED.&lt;col&gt;</c> column references; star expansion
    /// (<c>INSERTED.*</c> / <c>DELETED.*</c>), bare column refs, and
    /// table-alias qualifiers aren't modeled (CLAUDE.md flags those).
    /// <paramref name="allowInserted"/> and <paramref name="allowDeleted"/>
    /// gate which qualifier the call site permits — UPDATE allows both;
    /// DELETE allows only DELETED (INSERTED.col on DELETE is rejected at
    /// parse time with Msg 4104, matching the probed real-server
    /// behavior). The returned <see cref="OutputProjection"/> is
    /// re-runnable once per affected row.
    /// </summary>
    /// <remarks>
    /// On entry the cursor sits at the first lookahead token after the
    /// preceding clause (SET assignments for UPDATE; the table-name slot
    /// for DELETE). The schema resolves at parse time with a type-only
    /// resolver that mirrors the run-time resolver — bare column refs
    /// (<c>name.Count == 1</c>) raise Msg 207
    /// (<c>"Invalid column name 'X'."</c>); qualifiers other than
    /// INSERTED / DELETED raise Msg 4104. INSERTED columns on DELETE and
    /// any star-form raise the same Msg 4104 (Msg 207 stays for the
    /// bare-name case to match SQL Server's probe-confirmed shape).
    /// </remarks>
    private static OutputProjection? TryParseOutputClauseForMutation(
        ParserContext context,
        HeapTable table,
        bool allowInserted,
        bool allowDeleted,
        ViewOutputShape? view = null,
        OutputProjection? logged = null)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;

        // An OUTPUT clause rejects NEXT VALUE FOR (Msg 11720), one of the
        // clauses real names in that message.
        using var rejection = context.EnterNextValueForScope(NextValueForScope.Clause);
        context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Output);
        return ParseOutputClauseBody(context, table, allowInserted, allowDeleted, view, logged);
    }

    /// <summary>Body of <see cref="TryParseOutputClauseForMutation"/>.</summary>
    private static OutputProjection? ParseOutputClauseBody(
        ParserContext context,
        HeapTable table,
        bool allowInserted,
        bool allowDeleted,
        ViewOutputShape? view,
        OutputProjection? logged)
    {
        var columns = view?.Columns ?? table.Columns;
        var expressions = new List<Expression>();
        var names = new List<string>();
        do
        {
            context.MoveNextRequired();
            if (TryDetectStarReference(context, out var starQualifier))
            {
                var insertedRef = BuiltInToken.Equals(starQualifier, "INSERTED");
                var deletedRef = BuiltInToken.Equals(starQualifier, "DELETED");
                if ((insertedRef && !allowInserted) || (deletedRef && !allowDeleted) || (!insertedRef && !deletedRef))
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{starQualifier}.*");
                var columnNameList = new string[columns.Length];
                for (var i = 0; i < columns.Length; i++)
                    columnNameList[i] = columns[i].Name;
                AppendStarExpansion(starQualifier, columnNameList, expressions, names);
                context.MoveNextOptional();
                continue;
            }
            var expr = ParseOutputItem(context);
            if (table.GraphKind != GraphTableKind.None)
                GraphColumns.BindPseudoReferences(expr, table.Columns);
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
            names.Add(expr.Name);
        } while (context.Token is Operator { Character: ',' });

        var outputTarget = TryParseOutputIntoTarget(context, expressions.Count, table.Name, logged);

        SqlType ResolveOutputType(MultiPartName reference)
        {
            if (reference.Count == 1)
                throw SimulatedSqlException.InvalidColumnName(reference);

            var insertedRef = BuiltInToken.Equals(reference.ImmediateQualifier, "INSERTED");
            var deletedRef = BuiltInToken.Equals(reference.ImmediateQualifier, "DELETED");

            if (insertedRef && !allowInserted)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
            if (deletedRef && !allowDeleted)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());
            if (!insertedRef && !deletedRef)
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(reference.ToString());

            for (var i = 0; i < columns.Length; i++)
            {
                if (context.Batch.CurrentDatabase.Collation.Equals(columns[i].Name, reference.Leaf))
                    return view?.Admit(i, insertedRef) ?? columns[i].Type;
            }
            // A pseudo-table's unknown column is Msg 207 on the leaf (probed
            // 2026-09-24 against SQL Server 2025).
            throw SimulatedSqlException.InvalidColumnName(new MultiPartName(reference.Leaf));
        }

        var schema = new SqlType[expressions.Count];
        try
        {
            for (var i = 0; i < expressions.Count; i++)
                schema[i] = expressions[i].GetSqlType(context.Batch, ResolveOutputType);
        }
        catch (SimulatedSqlException error) when (view?.Refusals is { Count: > 0 } refusals)
        {
            refusals.Add(error);
        }
        view?.ThrowRefusals();

        var projection = NoteClientOutput(context.Batch, new OutputProjection([.. expressions], [.. names], schema, table, source: null, context.Batch, outputTarget, view: view, logged: logged));
        return IsClientOutputAfterInto(context, projection)
            ? TryParseOutputClauseForMutation(context, table, allowInserted, allowDeleted, view, logged: projection)
            : projection;
    }

    /// <summary>
    /// Whether a client-bound <c>OUTPUT</c> clause follows an <c>OUTPUT … INTO</c>
    /// one, which real accepts in that order only: the statement writes the
    /// target and returns the second clause's rows (probed 2026-10-01 against
    /// SQL Server 2025 for all four verbs). The reverse order reads the second
    /// <c>OUTPUT</c> as the first clause's column alias, so it is the syntax
    /// error at the token after it, and a second <c>INTO</c> is Msg 156 at that
    /// keyword (see <see cref="TryParseOutputIntoTarget"/>).
    /// </summary>
    private static bool IsClientOutputAfterInto(ParserContext context, OutputProjection projection) =>
        projection.HasTarget && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output };

    /// <summary>Parses one <c>OUTPUT</c> item, where a subquery is Msg 10705.</summary>
    private static Expression ParseOutputItem(ParserContext context)
    {
        using var outputItem = ParserScope.Enter(ref context.InOutputItem, true);
        return Expression.Parse(context);
    }

    /// <summary>
    /// Renders the OUTPUT target's name for Msg 8101: schema-qualified for a
    /// regular table, bare for a <c>#temp</c> or <c>@table</c> variable (which
    /// have no schema), matching what real puts in the message slot.
    /// </summary>
    private static string QualifiedOutputTargetName(MultiPartName written, HeapTable table) =>
        BatchContext.IsTableVariableName(table.Name) || table.Name.StartsWith('#')
            ? table.Name
            : $"{(written.Count > 1 ? written.ImmediateQualifier : Database.DefaultSchemaName)}.{table.Name}";

    /// <summary>
    /// Parses an optional <c>INTO &lt;target&gt; [(col_list)]</c> suffix on an
    /// OUTPUT clause. Returns <see langword="null"/> when no INTO keyword is
    /// present. The target can be either a table variable (<c>@t</c>) or a
    /// regular heap table — probe-confirmed against SQL Server 2025
    /// (2026-05-12) that both shapes accept the same column-mapping rules.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Column mapping resolves at parse time: with no column list, projection
    /// column i maps to the target's i-th <em>non-identity</em> column, so the
    /// projection count must equal that narrower count — fewer is Msg 213,
    /// more would have to write the identity column and is Msg 8101
    /// (probe-confirmed matrix: a target of identity + N plain columns accepts
    /// exactly N). With a column list, projection column i maps to the target
    /// column whose name matches list[i], and naming the identity column is
    /// Msg 544 — which <c>SET IDENTITY_INSERT</c> on the target does not
    /// unlock.
    /// </para>
    /// <para>
    /// Probe-confirmed: a table variable must already be declared (Msg 1087
    /// otherwise); regular table targets surface Msg 208; the projection count
    /// must match a column list's — Msg 121 when it has more items, Msg 120
    /// when fewer (probed 2026-09-26 against SQL Server 2025); columns named in
    /// the list must exist in the target (Msg 207). Target columns not
    /// covered by the projection receive their column-level
    /// <c>DEFAULT</c> (or NULL when none is declared) — see
    /// <see cref="OutputTarget.Append"/>.
    /// </para>
    /// </remarks>
    /// <param name="context">Parser state, positioned after the clause's projection list.</param>
    /// <param name="projectionColumnCount">How many columns the clause projects.</param>
    /// <param name="mutationTargetName">The DML statement's own target, which Msg 544 names.</param>
    /// <param name="logged">
    /// The <c>OUTPUT … INTO</c> clause this one follows, if any: only one clause
    /// may write a target, so an <c>INTO</c> here is the syntax error at it.
    /// </param>
    private static OutputTarget? TryParseOutputIntoTarget(ParserContext context, int projectionColumnCount, string mutationTargetName, OutputProjection? logged)
    {
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Into })
            return null;
        if (logged is not null)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // Accept both @t (table variable) and regular heap-table targets. The
        // resolver's failure mode differs by leaf prefix: @t -> Msg 1087
        // ("must declare the table variable"), regular -> Msg 208 ("invalid
        // object name") — same convention the DML routing uses.
        var targetName = BatchContext.ParseObjectName(context, acceptTableVariable: true);
        var enteredSessionScoped = context.Batch.HasSessionScopedReference;
        if (!context.Batch.TryResolveTable(targetName, out var targetTable))
        {
            throw BatchContext.IsTableVariableName(targetName.Leaf)
                ? SimulatedSqlException.MustDeclareTableVariable(targetName.Leaf)
                : SimulatedSqlException.InvalidObjectName(targetName);
        }
        // A table variable the batch declares is found again by name in each
        // batch that runs the statement (see OutputTarget), so naming one leaves
        // the statement's plan shareable; a parameter's rows are another matter.
        var declaredTableVariable = targetTable is { IsTableVariable: true, IsTableValuedParameter: false };
        if (declaredTableVariable)
            context.Batch.HasSessionScopedReference = enteredSessionScoped;
        context.MoveNextOptional();

        int[] columnOrdinals;
        if (context.Token is Operator { Character: '(' })
        {
            var ordinals = new List<int>();
            do
            {
                if (context.GetNextRequired() is not StringToken columnNameTok)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var matched = -1;
                for (var i = 0; i < targetTable.Columns.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(targetTable.Columns[i].Name, columnNameTok.Value))
                    {
                        matched = i;
                        break;
                    }
                }
                if (matched < 0)
                    throw SimulatedSqlException.InvalidColumnName(new MultiPartName(columnNameTok.Value));
                ordinals.Add(matched);
                context.MoveNextRequired();
            } while (context.Token is Operator { Character: ',' });
            if (context.Token is not Operator { Character: ')' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            if (projectionColumnCount > ordinals.Count)
                throw SimulatedSqlException.InsertSelectListMoreThanInsertList();
            if (projectionColumnCount < ordinals.Count)
                throw SimulatedSqlException.InsertSelectListFewerThanInsertList();
            // An explicit list may not name the target's identity column, and
            // SET IDENTITY_INSERT on the target does not unlock it (probed).
            // The arity check above runs first, matching real's ordering.
            foreach (var ordinal in ordinals)
            {
                if (targetTable.Columns[ordinal].Identity is not null)
                    throw SimulatedSqlException.CannotInsertExplicitIdentity(mutationTargetName);
            }

            columnOrdinals = [.. ordinals];
        }
        else
        {
            // Positional mapping fills the target's *non-identity* columns in
            // order — the identity column is skipped and generates its own
            // value. So the projection is measured against that narrower count:
            // fewer is Msg 213, more would have to write the identity column
            // and is Msg 8101 (probe-confirmed matrix against SQL Server 2025 —
            // a target of identity + N plain columns accepts exactly N).
            var fillable = new List<int>(targetTable.Columns.Length);
            for (var i = 0; i < targetTable.Columns.Length; i++)
            {
                if (targetTable.Columns[i].Identity is null)
                    fillable.Add(i);
            }

            if (projectionColumnCount > fillable.Count)
            {
                throw fillable.Count == targetTable.Columns.Length
                    ? SimulatedSqlException.ColumnCountDoesNotMatchTableDefinition()
                    : SimulatedSqlException.ExplicitIdentityNeedsColumnList(QualifiedOutputTargetName(targetName, targetTable));
            }

            if (projectionColumnCount < fillable.Count)
                throw SimulatedSqlException.ColumnCountDoesNotMatchTableDefinition();
            columnOrdinals = [.. fillable];
        }

        return new OutputTarget(
            targetTable,
            columnOrdinals,
            declaredTableVariable ? targetName.Leaf : null,
            replayable: declaredTableVariable || (!targetTable.IsTableVariable && !BlocksDmlPlan(context.Batch, targetTable, clientOutput: false)));
    }

    /// <summary>
    /// Resolved <c>OUTPUT … INTO &lt;target&gt;</c> binding: the heap table the
    /// projection appends rows to (table variable or regular table), and
    /// <see cref="ProjectionToTargetOrdinal"/>, which maps projection
    /// column index to target table column ordinal (positional fill if INTO
    /// had no explicit column list).
    /// </summary>
    private sealed class OutputTarget(HeapTable target, int[] projectionToTargetOrdinal, string? tableVariable, bool replayable)
    {
        /// <summary>
        /// The table written, unless it is a table variable: that one belongs
        /// to the batch that declared it, so a plan shared across batches holds
        /// its name instead and finds it in the executing batch
        /// (<see cref="TargetIn"/>). Every batch running the same text declares
        /// it the same way, so the column mapping holds for each.
        /// </summary>
        private readonly HeapTable? table = tableVariable is null ? target : null;
        public readonly int[] ProjectionToTargetOrdinal = projectionToTargetOrdinal;

        /// <summary>
        /// Whether a cached DML plan may hold this target: a table variable the
        /// batch declares, or a table nothing in <see cref="BlocksDmlPlan"/> names.
        /// </summary>
        public readonly bool Replayable = replayable;

        /// <summary>The table this target writes in <paramref name="batch"/>.</summary>
        private HeapTable TargetIn(BatchContext batch) =>
            this.table
            ?? (batch.TableVariables.TryGetValue(tableVariable![1..], out var declared) ? declared : throw SimulatedSqlException.MustDeclareTableVariable(tableVariable));

        /// <summary>
        /// Appends one row to the target table. Columns named in the
        /// projection map by ordinal via <see cref="ProjectionToTargetOrdinal"/>;
        /// any target column not covered generates an identity value if it is
        /// an identity column, else evaluates the column's <c>DEFAULT</c>
        /// expression if declared, else falls through to NULL (probe-confirmed
        /// against SQL Server 2025: unfilled OUTPUT-INTO target columns receive
        /// the target's DEFAULT, not NULL, and an identity column fills from
        /// its own sequence). Mutations
        /// route through the same undo log as direct DML on the target:
        /// table variables use the per-statement
        /// <see cref="BatchContext.CurrentTableVarUndoLog"/>; regular tables
        /// use the connection's
        /// <see cref="BatchContext.CurrentUndoLog"/> of the executing <paramref name="batch"/>.
        /// </summary>
        public void Append(SqlValue[] projectedValues, BatchContext batch)
        {
            var target = this.TargetIn(batch);
            var targetValues = new SqlValue[target.Columns.Length];
            var covered = new bool[target.Columns.Length];
            for (var i = 0; i < projectedValues.Length; i++)
            {
                var ordinal = this.ProjectionToTargetOrdinal[i];
                // Coerce to the destination column's type, the same way the
                // uncovered-column defaults below do. The projection's type
                // comes from the source table, which need not match the
                // target's — an ORM writing `SELECT TOP 0 CAST(id AS bigint)
                // … INTO #tmp` then `OUTPUT INSERTED.id INTO #tmp` hands an
                // int to a bigint column. Storing it raw reaches the row
                // encoder's type check as a bare ArgumentException, which over
                // the wire aborts the response mid-stream and the client
                // reports a severe protocol error rather than anything useful.
                targetValues[ordinal] = CoerceForInsert(projectedValues[i], target.Columns[ordinal]);
                covered[ordinal] = true;
            }
            for (var i = 0; i < targetValues.Length; i++)
            {
                if (covered[i])
                    continue;
                var column = target.Columns[i];
                // An uncovered identity column generates its own value, as it
                // does for a direct INSERT that omits it — the positional map
                // skips identity columns entirely, and a column list may too.
                targetValues[i] = column.Identity is not null
                    ? CoerceForIdentity(GenerateIdentity(column), column)
                    : column.Default is { } defaultExpression
                        ? CoerceForInsert(defaultExpression.Run(new RuntimeContext(NoColumnResolver, batch)), column)
                        : SqlValue.Null(column.Type);
            }
            var undoLog = target.IsTableVariable ? batch.CurrentTableVarUndoLog : batch.CurrentUndoLog;
            _ = Simulation.InsertRow(batch, target, RowEncoder.EncodeRow(target.StoredColumns, targetValues, target.Heap), undoLog);
            batch.Connection.StatementIo?.CountWrite(target);
            target.ChangeTracking?.RecordRow(batch, target, targetValues, ChangeTrackingOperation.Insert);
        }
    }

    /// <summary>
    /// Detects the contextual <c>OUTPUT</c> keyword on the current token and,
    /// if present, parses the comma-separated projection list following the
    /// rules documented on <see cref="OutputProjection"/>. Returns
    /// <see langword="null"/> when <c>OUTPUT</c> is absent (the surrounding
    /// statement just continues with VALUES).
    /// </summary>
    /// <param name="context">Parser state, positioned on the token after the column-list closer.</param>
    /// <param name="destinationTable">The INSERT target — supplies the columns reachable through <c>INSERTED</c>.</param>
    /// <param name="sourceColumnNames">For MERGE only: the source alias's column names. <see langword="null"/> for plain INSERT.</param>
    /// <param name="view">The shape <c>INSERTED</c> takes when the INSERT writes through a view; null for a table.</param>
    /// <param name="logged">The <c>OUTPUT … INTO</c> clause a client-bound one follows; see <see cref="IsClientOutputAfterInto"/>.</param>
    private static OutputProjection? TryParseOutputClause(ParserContext context, HeapTable destinationTable, (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? sourceColumnNames, ViewOutputShape? view = null, OutputProjection? logged = null)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;

        // Msg 11720, as on the mutation-side OUTPUT entry above.
        using var rejection = context.EnterNextValueForScope(NextValueForScope.Clause);
        context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Output);
        return ParseInsertOutputClauseBody(context, destinationTable, sourceColumnNames, view, logged);
    }

    /// <summary>Body of <see cref="TryParseOutputClause"/>.</summary>
    private static OutputProjection? ParseInsertOutputClauseBody(ParserContext context, HeapTable destinationTable, (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? sourceColumnNames, ViewOutputShape? view, OutputProjection? logged)
    {
        var columns = view?.Columns ?? destinationTable.Columns;
        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(MultiPartName name)
        {
            // An unqualified name, or INSERTED's unknown column, is Msg 207 on
            // the leaf; any other qualifier is Msg 4104 (probed 2026-09-24
            // against SQL Server 2025).
            if (name.Count == 1)
                throw SimulatedSqlException.InvalidColumnName(name);
            if (BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED"))
            {
                for (var i = 0; i < columns.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(columns[i].Name, name.Leaf))
                        return view?.Admit(i, inserted: true) ?? columns[i].Type;
                }
                throw SimulatedSqlException.InvalidColumnName(new MultiPartName(name.Leaf));
            }
            else if (sourceColumnNames is var (sourceAlias, sourceCols, sourceTypes) && context.Batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias))
            {
                for (var i = 0; i < sourceCols.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(sourceCols[i], name.Leaf))
                        return sourceTypes[i];
                }
            }
            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
        }

        do
        {
            context.MoveNextRequired();
            if (TryDetectStarReference(context, out var starQualifier))
            {
                if (BuiltInToken.Equals(starQualifier, "INSERTED"))
                {
                    var cols = new string[columns.Length];
                    for (var i = 0; i < columns.Length; i++)
                        cols[i] = columns[i].Name;
                    AppendStarExpansion(starQualifier, cols, expressions, columnNames);
                }
                else
                {
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound($"{starQualifier}.*");
                }
                context.MoveNextOptional();
                continue;
            }
            var expr = Expression.Parse(context);
            if (destinationTable.GraphKind != GraphTableKind.None)
                GraphColumns.BindPseudoReferences(expr, destinationTable.Columns);
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

        var outputTarget = TryParseOutputIntoTarget(context, expressions.Count, destinationTable.Name, logged);

        var schema = new SqlType[expressions.Count];
        try
        {
            for (var i = 0; i < expressions.Count; i++)
                schema[i] = expressions[i].GetSqlType(context.Batch, ResolveOutputType);
        }
        catch (SimulatedSqlException error) when (view?.Refusals is { Count: > 0 } refusals)
        {
            refusals.Add(error);
        }
        view?.ThrowRefusals();

        var projection = NoteClientOutput(context.Batch, new OutputProjection(expressions, [.. columnNames], schema, destinationTable, sourceColumnNames, context.Batch, outputTarget, view: view, logged: logged));
        return IsClientOutputAfterInto(context, projection)
            ? TryParseOutputClause(context, destinationTable, sourceColumnNames, view, logged: projection)
            : projection;
    }

    /// <summary>
    /// Records a client-bound <c>OUTPUT</c> clause's shape on the statement, so
    /// an error a <c>TRY</c> frame catches can still end it with the empty
    /// result set real sends (see <c>CaughtWriteCount</c>).
    /// </summary>
    private static OutputProjection NoteClientOutput(BatchContext batch, OutputProjection projection)
    {
        if (!projection.HasTarget)
        {
            batch.CurrentStatement.ClientOutputShape = (projection.Schema, projection.ColumnNames);
            FunctionBodyShape.NoteClientOutput(batch);
        }
        return projection;
    }

    /// <summary>
    /// Holds the parsed <c>OUTPUT</c> projection together with its statically
    /// resolved schema and the column-name resolvers it needs at row time.
    /// Backs both <c>INSERT ... OUTPUT</c> and <c>MERGE ... OUTPUT</c>; the
    /// MERGE source-alias plumbing is opt-in via the constructor. Holds no
    /// batch: a cached DML plan shares it across executions, so each row
    /// projects against the batch its caller passes.
    /// </summary>
    private sealed class OutputProjection(
        IReadOnlyList<Expression> expressions,
        string[] columnNames,
        SqlType[] schema,
        HeapTable destinationTable,
        (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? source,
        BatchContext parseBatch,
        OutputTarget? outputTarget,
        DataMask?[]? sourceMasks = null,
        ViewOutputShape? view = null,
        OutputProjection? logged = null)
    {
        public readonly SqlType[] Schema = schema;

        /// <summary>
        /// The columns <c>INSERTED</c> / <c>DELETED</c> name: the target
        /// table's, or through a view the view's own.
        /// </summary>
        private readonly HeapColumn[] columns = view?.Columns ?? destinationTable.Columns;
        public readonly string[] ColumnNames = columnNames;

        /// <summary>
        /// Per OUTPUT column, how it masks for a principal without
        /// <c>UNMASK</c>: <c>INSERTED</c> / <c>DELETED</c> read the target's
        /// masked columns as a SELECT would (probed 2026-09-27 against SQL
        /// Server 2025), both to the client and into an <c>INTO</c> target,
        /// and a MERGE's source columns as its source query projects them.
        /// </summary>
        private readonly DataMask?[]? masks = MasksOf(parseBatch, expressions, destinationTable, source, sourceMasks, view);

        // A static helper rather than a field initializer's lambdas, so no
        // closure over the parsing batch outlives the constructor.
        private static DataMask?[]? MasksOf(
            BatchContext batch,
            IReadOnlyList<Expression> expressions,
            HeapTable destinationTable,
            (string SourceAlias, string[] SourceColumns, SqlType[] SourceTypes)? source,
            DataMask?[]? sourceMasks,
            ViewOutputShape? view) =>
            DataMask.OfProjection(
                batch,
                [.. expressions],
                name => BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED") || BuiltInToken.Equals(name.ImmediateQualifier, "DELETED")
                    ? Array.FindIndex(view?.Columns ?? destinationTable.Columns, column => batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf)) is var ordinal and >= 0
                        ? view is not null ? view.Columns[ordinal].DerivedMask : DataMask.ForTableColumn(destinationTable, ordinal)
                        : null
                    : source is var (sourceAlias, sourceColumns, _) && batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias)
                        && Array.FindIndex(sourceColumns, column => batch.CurrentDatabase.Collation.Equals(column, name.Leaf)) is var sourceOrdinal and >= 0
                        ? sourceMasks?[sourceOrdinal]
                        : null,
                typeOf: null);

        /// <summary>
        /// True when this OUTPUT clause includes an <c>INTO</c> target.
        /// The dispatching caller suppresses the per-row result-set yield in
        /// this case and surfaces the statement as a non-query (matches real
        /// SQL Server: <c>OUTPUT … INTO target</c> directs rows to the target
        /// only, without returning them to the client). A client-bound clause
        /// following an <c>INTO</c> one is false here and true for
        /// <see cref="WritesTarget"/>.
        /// </summary>
        public bool HasTarget => outputTarget is not null;

        /// <summary>True when this clause, or the <c>OUTPUT … INTO</c> clause it follows, writes a target.</summary>
        public bool WritesTarget => outputTarget is not null || logged is not null;

        /// <summary>
        /// Whether the target this clause or the one it follows writes keeps a
        /// cached DML plan from being shared (see <see cref="OutputTarget.Replayable"/>).
        /// </summary>
        public bool TargetBlocksDmlPlan => outputTarget is { Replayable: false } || logged?.TargetBlocksDmlPlan == true;

        // A non-persisted computed column is evaluated when OUTPUT reads it,
        // so an expression that fails for the row raises here rather than
        // having failed the write.
        private SqlValue ReadOutputColumn(SqlValue[] row, int ordinal, BatchContext batch) =>
            view is not null
                ? view.Read is { } read ? read(row, ordinal) : row[ordinal]
                : destinationTable.Columns[ordinal] is { Computed: not null, IsPersisted: false }
                ? EvaluateComputedColumn(destinationTable, row, ordinal, batch)
                : row[ordinal];

        /// <summary>
        /// Encodes one OUTPUT row, running each parsed expression against a
        /// per-row resolver over <c>INSERTED</c> / <c>DELETED</c> and — for
        /// MERGE — the source alias. Returns the encoded projection bytes, or
        /// <see langword="null"/> when an INTO target consumed the row (the
        /// caller then skips its per-row result-set append).
        /// </summary>
        /// <param name="batch">The executing batch.</param>
        /// <param name="insertedValues">Post-image row, or null where the statement has none (DELETE).</param>
        /// <param name="deletedValues">Pre-image row, or null where the statement has none (INSERT).</param>
        /// <param name="sourceValues">MERGE's matched source row; null for the other statements.</param>
        /// <param name="action">
        /// MERGE's per-row <c>$action</c> verb, or null when the statement has
        /// no <c>$action</c> to report.
        /// </param>
        /// <remarks>
        /// A reference to a side the statement doesn't have reads as a typed
        /// NULL rather than throwing. The parser already rejects the
        /// statically impossible cases (INSERTED on DELETE, say), so this only
        /// covers rows a path legitimately leaves unpopulated — a MERGE
        /// branch's absent half, or an UPDATE whose caller didn't pre-capture
        /// the old values.
        /// </remarks>
        public byte[]? ProjectRow(
            BatchContext batch,
            SqlValue[]? insertedValues,
            SqlValue[]? deletedValues,
            SqlValue[]? sourceValues = null,
            string? action = null)
        {
            // The OUTPUT … INTO clause a client-bound one follows writes its
            // row first; it returns nothing.
            _ = logged?.ProjectRow(batch, insertedValues, deletedValues, sourceValues, action);

            SqlValue Resolve(MultiPartName name)
            {
                if (BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED"))
                {
                    for (var i = 0; i < this.columns.Length; i++)
                    {
                        if (batch.CurrentDatabase.Collation.Equals(this.columns[i].Name, name.Leaf))
                            return insertedValues is null ? SqlValue.Null(this.columns[i].Type) : this.ReadOutputColumn(insertedValues, i, batch);
                    }
                }
                else if (BuiltInToken.Equals(name.ImmediateQualifier, "DELETED"))
                {
                    for (var i = 0; i < this.columns.Length; i++)
                    {
                        if (batch.CurrentDatabase.Collation.Equals(this.columns[i].Name, name.Leaf))
                            return deletedValues is null ? SqlValue.Null(this.columns[i].Type) : this.ReadOutputColumn(deletedValues, i, batch);
                    }
                }
                else if (source is var (sourceAlias, sourceCols, sourceTypes)
                    && batch.CurrentDatabase.Collation.Equals(name.ImmediateQualifier, sourceAlias))
                {
                    for (var i = 0; i < sourceCols.Length; i++)
                    {
                        if (batch.CurrentDatabase.Collation.Equals(sourceCols[i], name.Leaf))
                            return sourceValues is null ? SqlValue.Null(sourceTypes[i]) : sourceValues[i];
                    }
                }
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
            }

            var projected = new SqlValue[expressions.Count];
            var runtime = new RuntimeContext(Resolve, batch);
            for (var i = 0; i < expressions.Count; i++)
            {
                projected[i] = action is not null && IsMergeActionRef(expressions[i])
                    ? SqlValue.FromNVarchar(action)
                    : expressions[i].Run(runtime);
            }

            // Which masks apply is the executing principal's to answer; an
            // OUTPUT clause declaring none never asks.
            if (this.masks is not null && DataMasking.Applying(batch, this.masks) is { } applying)
                projected = DataMasking.MaskRowForStorage(projected, applying, this.Schema);

            if (outputTarget is null)
                return RowEncoder.EncodeRow(this.Schema, projected);
            outputTarget.Append(projected, batch);
            return null;
        }
    }
}
