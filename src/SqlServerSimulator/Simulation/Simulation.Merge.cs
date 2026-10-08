using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
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
        using var inMerge = ParserScope.Enter(ref context.InUpdateOrMerge, true);
        var start = context.SaveCheckpoint();
        if (ParseMerge(context, readsViewRows: null) is { } outcome)
            return outcome;
        // A view whose INSTEAD OF triggers cover none of the actions this
        // MERGE takes is written through after all.
        context.RestoreCheckpoint(start);
        return ParseMerge(context, readsViewRows: false)!;
    }

    /// <summary>
    /// <see cref="ParseMerge(ParserContext)"/>'s body. A view target is read
    /// one of two ways: through its base table, or — when its <c>INSTEAD OF</c>
    /// triggers take every action the statement performs, or when it has no
    /// base table to write — as the rows it yields, matched and acted on under
    /// its own column names (<paramref name="readsViewRows"/>; null guesses
    /// from whether the view has any <c>INSTEAD OF</c> trigger). Returns null
    /// when the guess proves wrong once the <c>WHEN</c> clauses have named the
    /// actions, and the caller parses again the other way.
    /// </summary>
    private static SimulatedStatementOutcome? ParseMerge(ParserContext context, bool? readsViewRows)
    {
        // Real binds the source, then ON, stopping there when ON fails; then
        // the insert column list, every WHEN condition, and every action
        // (probed 2026-09-27: an ON miss reports alone, and `… UPDATE SET x1 =
        // x2 … INSERT (a, x4) VALUES (x5, 1)` reports x4, x1, x2, x5).
        var bindErrors = context.Batch.BindErrors;
        bindErrors?.OpenScope(context.Token);
        bindErrors?.SetBarriers(BindClause.MergeInsertColumns);

        // MERGE [TOP (n) [PERCENT]] [INTO] target [AS] alias
        context.MoveNextRequired();
        var top = Selection.ParseDmlTopClause(context);
        if (context.Token is ReservedKeyword { Keyword: Keyword.Into })
            context.MoveNextRequired();

        var target = ParseDmlTarget(context, remoteKind: null);
        var (destinationName, resolvedView) = (target.Name, target.View);

        // View target (CTE bindings are already shadowed at the source level,
        // not the target). An updatable single-base view routes through its
        // base table for the actual mutation; a non-updatable view raises
        // Msg 4403 / Msg 4405 the same way INSERT / UPDATE / DELETE through
        // view do.
        View? sourceView = null;
        View? viewRowsTarget = null;
        HeapTable destinationTable;
        // A partitioned view is no MERGE target, whatever its members (probed
        // 2026-10-01 against SQL Server 2025).
        if (resolvedView is { PartitionedBase: not null } && !HasAnyInsteadOfTrigger(context.Batch, resolvedView))
            throw SimulatedSqlException.MergeTargetIsPartitionedView();
        if (resolvedView is not null)
        {
            // A view with no single base table is always matched as its own
            // rows: its INSTEAD OF triggers take them, or for a join view the
            // actions are carried to the one base table they name.
            if (resolvedView.BaseTable is not { } baseTable || (readsViewRows ?? HasAnyInsteadOfTrigger(context.Batch, resolvedView)))
            {
                // The view's own rows, under its own column names, stand in
                // for the target; nothing is written to them.
                viewRowsTarget = resolvedView;
                destinationTable = ViewShapedTable(context.Batch, resolvedView, ViewColumnsFor(context.Batch, resolvedView, destinationName));
            }
            else
            {
                sourceView = resolvedView;
                destinationTable = baseTable;
            }
        }
        else if (target.Table is { } table)
        {
            destinationTable = table;
        }
        else
        {
            ParseMissingMergeTail(context, destinationName);
            throw MissingDmlTargetError(context.Batch, destinationName);
        }
        NoteWriteTable(context.Batch, destinationName, destinationTable, "MERGE", persistent: viewRowsTarget is not null);

        // MERGE target hints: hint-then-alias placement
        // (probe-confirmed: `MERGE INTO t WITH (TABLOCK) AS x USING …` works,
        // `MERGE INTO t AS x WITH (TABLOCK) USING …` raises Msg 156). The
        // hint slot sits between the target name and the optional
        // <c>[AS] alias</c>, opposite of FROM / UPDATE / DELETE which use
        // alias-then-hint. Legacy bare-paren form is rejected. Table-
        // variable targets reject hints — skip the parser for `@t`.
        context.MoveNextRequired();
        var serializableHint = false;
        if (!BatchContext.IsTableVariableName(destinationName.Leaf))
        {
            var targetHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
            Selection.ValidateDmlTargetHints(targetHints, destinationName.ToString(), "MERGE");
            Selection.ValidateIndexHintArguments(context.Batch.CurrentDatabase.Collation, targetHints, destinationTable, Selection.IndexHintTableName(destinationName, destinationTable));
            serializableHint = targetHints.Serializable;
        }
        // Phase 1b: acquire table-IX on the MERGE target; row-X on each
        // affected row at mutation time.
        LockWriteTable(context.Batch, destinationTable, "MERGE", checkFilegroup: true);

        // Optional target alias: AS <alias> or bare <alias>.
        if (context.Token is ReservedKeyword { Keyword: Keyword.As })
            context.MoveNextRequired();
        // Default alias: the surface name the user typed — view's own name
        // when the target is a view (so `MERGE INTO vbase … ON vbase.col …`
        // works), otherwise the base table's name.
        var defaultTargetName = sourceView?.Name ?? destinationTable.Name;
        var triggerTarget = (SchemaObject?)viewRowsTarget ?? (SchemaObject?)sourceView ?? destinationTable;
        var targetAlias = context.Token switch
        {
            UnquotedString { ContextualKeyword: ContextualKeyword.Using } => defaultTargetName,
            Name n => n.Value,
            _ => throw SimulatedSqlException.SyntaxErrorNear(context),
        };
        if (!context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName))
            context.MoveNextRequired();
        if ((sourceView ?? viewRowsTarget) is { } checkedView && !HasInsteadOfTrigger(context.Batch, checkedView, TriggerActions.Insert | TriggerActions.Update | TriggerActions.Delete))
        {
            RejectCheckOptionOverRowLimit(
                checkedView,
                context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName) ? destinationName.ToString() : targetAlias);
        }

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
        bindErrors?.EnterClause(context.Token, BindClause.MergeSource);

        var (materializeSource, sourceAlias, sourceColumnNames, sourceSchema, sourceMasks, replayableSource, sourceNullability) = ParseMergeSource(context);
        if (context.Batch.CurrentDatabase.Collation.Equals(sourceAlias, targetAlias))
            throw SimulatedSqlException.MergeSourceAndTargetShareAName();

        // ON predicate — resolves target via targetAlias/destinationName, source via sourceAlias.
        if (context.Token is not ReservedKeyword { Keyword: Keyword.On })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        bindErrors?.EnterClause(context.Token, BindClause.MergeOn);
        context.MoveNextRequired();

        var resolveTypeBoth = new MergeColumnScope(context.CurrentDatabase.Collation, targetAlias, defaultTargetName, targetColumns, sourceAlias, sourceColumnNames, sourceSchema).Both;

        // Walk the ON's expression tree with the two-sided resolver so any
        // column reference type-checks correctly at parse time.
        var onAggregates = new List<AggregateExpression>();
        BooleanExpression onPredicate;
        using (ParserScope.Enter(ref context.OuterTypeResolver, resolveTypeBoth))
        // ON is one of the eight clauses Msg 11720 names, and a MERGE's ON
        // takes it like a join's (probe-confirmed).
        using (context.EnterNextValueForScope(NextValueForScope.Clause))
        using (ParserScope.Enter(ref context.AggregateCollector, onAggregates))
        {
            onPredicate = BooleanExpression.Parse(context);
        }
        Selection.RefuseClauseAggregates(context.Batch, onAggregates, static () => SimulatedSqlException.AggregateInOnClause());

        // Compile-time bind of the ON predicate against the same two-sided
        // resolver, so a cross-collation comparison, a legacy-LOB string-scalar
        // argument or an unknown column reports while compiling the way real
        // does rather than once a candidate row pairs up.
        onPredicate.Bind(context.Batch, resolveTypeBoth);

        // WHEN clauses.
        var whenClauses = ParseMergeWhenClauses(context, destinationTable, sourceView, destinationName.ToString(), targetAlias, defaultTargetName, sourceAlias, sourceColumnNames, sourceSchema);

        // Which actions INSTEAD OF triggers take is settled while compiling:
        // some but not all of the statement's is Msg 5316.
        var insteadOfActions = MergeInsteadOfActions(context.Batch, triggerTarget, whenClauses, destinationName);
        JoinViewMergePlan? joinWrite = null;
        if (viewRowsTarget is not null && !insteadOfActions)
        {
            if (viewRowsTarget.BaseTable is not null)
                return null;
            if (!viewRowsTarget.IsJoinUpdatable)
                throw RefuseMergeIntoNonUpdatableView(viewRowsTarget, destinationTable, whenClauses, destinationName);
            // The refusals name the target's alias when it has one (probed
            // 2026-10-04 against SQL Server 2025).
            joinWrite = PlanJoinViewMerge(
                context.Batch, viewRowsTarget, destinationTable, whenClauses,
                context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName) ? destinationName : new MultiPartName(targetAlias));
        }

        // OUTPUT. Through a join view INSERTED may name only the columns that
        // read the written table alone (Msg 404 otherwise); under INSTEAD OF
        // triggers it names none.
        var output = TryParseMergeOutputClause(
            context, destinationTable, sourceAlias, sourceColumnNames, sourceSchema, sourceMasks, MergeOutputNullability(whenClauses, sourceNullability),
            joinWrite is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output }
                ? new ViewOutputShape(destinationTable.Columns, read: null, insertedRefused: joinWrite.Path.Length == 1 || (joinWrite.Path.Length == 2 && joinWrite.Chain.Nested[joinWrite.Path[0]] is { Sources.Length: 1 })
                    ? ordinal => JoinViewColumnReadsOtherSource(context.Batch, joinWrite.Chain, joinWrite.Chain.Views.Length - 1, ordinal, joinWrite.Path[0])
                    : throw JoinOverJoinViewOutputNotModeled(viewRowsTarget!))
                : viewRowsTarget is not null
                ? new ViewOutputShape(destinationTable.Columns, read: null, insertedRefused: static _ => true)
                : sourceView is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output }
                ? SingleBaseViewOutputShape(context.Batch, sourceView, destinationName, destinationTable)
                : null);

        var writeMasks = context.Batch.Connection.Simulation.DeclaresDataMasks && !context.Batch.IsSkipping
            ? MergeWriteMasks(context.Batch, destinationTable, sourceView, targetAlias, defaultTargetName, targetColumns, sourceAlias, sourceColumnNames, sourceMasks, whenClauses)
            : null;

        // Msg 334 applies per action the MERGE actually performs, and the
        // message echoes the target as written — its alias when one was given
        // (probe-confirmed: `MERGE dbo.m AS t …` reports 't'), otherwise the
        // name from the statement.
        if (output is { HasTarget: false })
        {
            // Written through a view, the base table's triggers refuse it and
            // the message names that table, as for the other statements.
            var mergeTarget = sourceView is not null ? destinationTable.Name
                : joinWrite is not null ? joinWrite.Table.Name
                : context.Batch.CurrentDatabase.Collation.Equals(targetAlias, defaultTargetName)
                ? destinationName.ToString()
                : targetAlias;
            foreach (var clause in whenClauses)
            {
                RejectClientOutputOnTriggeredTarget(
                    context.Batch,
                    (SchemaObject?)joinWrite?.Table ?? (SchemaObject?)viewRowsTarget ?? destinationTable,
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

        // Required trailing ; — end-of-batch included, which real refuses as
        // it compiles, so a MERGE without one never runs (probed 2026-09-26).
        // A stray word where the ; belongs is a syntax error at the word
        // (probed 2026-09-30).
        Selection.ParseOptionalDmlOptionClause(context);
        if (context.Token is not Operator { Character: ';' })
            throw EndsStatement(context.Token) ? SimulatedSqlException.MergeMustBeTerminated() : SimulatedSqlException.SyntaxErrorNear(context);

        var plan = new MergePlan(
            destinationName, triggerTarget, destinationTable, sourceView, targetAlias, materializeSource, sourceAlias, sourceColumnNames, sourceSchema,
            onPredicate, whenClauses, writeMasks, output, serializableHint, viewRowsTarget, joinWrite, top);
        // Which INSTEAD OF triggers take the actions is settled above from
        // their enabled state, which no schema change records, so a target
        // carrying any keeps its statements parsing.
        NoteDmlPlan(
            context,
            plan,
            admitted: replayableSource
                && viewRowsTarget is null
                && !Array.Exists(TriggersAttachedTo(context.Batch, destinationTable), trigger => trigger.Timing == TriggerTiming.InsteadOf)
                && !whenClauses.Exists(clause => clause.Assignments?.Exists(assignment => assignment.Expr is JsonModify) == true)
                && AdmitsDmlPlan(context.Batch, destinationTable, sourceView, output));
        return RunMerge(context, plan);
    }

    /// <summary>
    /// A <c>MERGE</c>'s parse, which <see cref="RunMerge"/> executes — once as
    /// the statement parses, and again for each replay of a cached plan.
    /// </summary>
    private sealed class MergePlan(
        MultiPartName destinationName,
        SchemaObject triggerTarget,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        Func<BatchContext, List<SqlValue[]>> materializeSource,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        BooleanExpression onPredicate,
        List<WhenClause> whenClauses,
        DataMask?[]? writeMasks,
        OutputProjection? output,
        bool serializableHint,
        View? viewRowsTarget,
        JoinViewMergePlan? joinWrite,
        Selection.DmlTopLimit? top) : DmlStatementPlan
    {
        public readonly MultiPartName DestinationName = destinationName;
        public readonly SchemaObject TriggerTarget = triggerTarget;
        public readonly HeapTable DestinationTable = destinationTable;
        public readonly View? SourceView = sourceView;
        public readonly string TargetAlias = targetAlias;
        public readonly Func<BatchContext, List<SqlValue[]>> MaterializeSource = materializeSource;
        public readonly string SourceAlias = sourceAlias;
        public readonly string[] SourceColumnNames = sourceColumnNames;
        public readonly SqlType[] SourceSchema = sourceSchema;
        public readonly BooleanExpression OnPredicate = onPredicate;
        public readonly List<WhenClause> WhenClauses = whenClauses;

        /// <summary>See <see cref="MergeWriteMasks"/>.</summary>
        public readonly DataMask?[]? WriteMasks = writeMasks;
        public readonly OutputProjection? Output = output;
        public readonly bool SerializableHint = serializableHint;
        public readonly View? ViewRowsTarget = viewRowsTarget;
        public readonly JoinViewMergePlan? JoinWrite = joinWrite;
        public readonly Selection.DmlTopLimit? Top = top;

        public override SimulatedStatementOutcome Run(ParserContext context) => RunMerge(context, this);
    }
    /// <summary>Whether the view carries an INSTEAD OF trigger for any action.</summary>
    private static bool HasAnyInsteadOfTrigger(BatchContext batch, View view) =>
        HasInsteadOfTrigger(batch, view, TriggerActions.Insert)
        || HasInsteadOfTrigger(batch, view, TriggerActions.Update)
        || HasInsteadOfTrigger(batch, view, TriggerActions.Delete);

    private static bool HasMergeAction(List<WhenClause> whenClauses, MergeActionKind action) =>
        whenClauses.Exists(clause => clause.Action == action);

    /// <summary>
    /// Whether <c>INSTEAD OF</c> triggers on <paramref name="target"/> take
    /// the actions the statement's <c>WHEN</c> clauses perform: every one of
    /// them, or none. Some but not all is <strong>Msg 5316</strong>, raised
    /// while compiling — an un-taken branch's MERGE ends its batch, and a
    /// <c>DISABLE TRIGGER</c> earlier in the same batch hasn't run yet
    /// (probed 2026-09-27 against SQL Server 2025, for a table and a view).
    /// </summary>
    private static bool MergeInsteadOfActions(BatchContext batch, SchemaObject target, List<WhenClause> whenClauses, MultiPartName writtenName)
    {
        var any = false;
        var all = true;
        foreach (var (kind, action) in (ReadOnlySpan<(MergeActionKind, TriggerActions)>)[(MergeActionKind.Insert, TriggerActions.Insert), (MergeActionKind.Update, TriggerActions.Update), (MergeActionKind.Delete, TriggerActions.Delete)])
        {
            if (!HasMergeAction(whenClauses, kind))
                continue;
            var covered = HasInsteadOfTrigger(batch, target, action);
            any |= covered;
            all &= covered;
        }
        return any && !all ? throw SimulatedSqlException.MergeInsteadOfTriggerOnSomeActions(writtenName.ToString()) : any;
    }

    /// <summary>
    /// A MERGE into a view real can't write through, with no INSTEAD OF
    /// trigger to take it: once its <c>WHEN</c> clauses have bound against the
    /// view's columns, one naming a derived column — an <c>UPDATE SET</c>
    /// target, or an <c>INSERT</c> column, listed or implied — is
    /// <strong>Msg 4406</strong>, and otherwise the view's own refusal stands
    /// (probed 2026-09-27 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException RefuseMergeIntoNonUpdatableView(View view, HeapTable viewRows, List<WhenClause> whenClauses, MultiPartName writtenName)
    {
        if (view.DerivedOutputColumns is { } derived)
        {
            foreach (var clause in whenClauses)
            {
                var derivedTarget = clause.Action switch
                {
                    MergeActionKind.Update => clause.Assignments!.Exists(assignment => assignment.Ordinal >= 0 && derived[assignment.Ordinal]),
                    MergeActionKind.Insert => Array.Exists(clause.InsertColumns!, column => derived[Array.IndexOf(viewRows.Columns, column)]),
                    _ => false,
                };
                if (derivedTarget)
                    return SimulatedSqlException.ViewDmlTouchesDerivedField(view.UnionOwnerName ?? writtenName.ToString());
            }
        }
        return NonUpdatableViewError(view, writtenName.ToString());
    }

    /// <summary>
    /// Per target column (base-table ordinal), the mask a principal without
    /// <c>UNMASK</c> writes it through, or null when nothing masks; which
    /// principal that is, the execution half settles. Every action's value for
    /// the column — each
    /// <c>UPDATE SET</c> and the <c>INSERT</c>'s — meets in one column as a
    /// <c>CASE</c>'s arms do, so a bare masked source column stores its own
    /// function, any other expression over one <c>default()</c>, and an
    /// unmasked value written beside a masked one takes the mask too (probed
    /// 2026-09-27 against SQL Server 2025: <c>UPDATE SET s = s + src.x</c>
    /// with <c>INSERT VALUES (src.id, src.x)</c> stores <c>xxxx</c> both ways).
    /// </summary>
    private static DataMask?[]? MergeWriteMasks(
        BatchContext batch,
        HeapTable destinationTable,
        View? sourceView,
        string targetAlias,
        string defaultTargetName,
        HeapColumn[] targetColumns,
        string sourceAlias,
        string[] sourceColumnNames,
        DataMask?[]? sourceMasks,
        List<WhenClause> whenClauses)
    {
        var collation = batch.CurrentDatabase.Collation;
        DataMask? TargetMask(string leaf)
        {
            var index = Array.FindIndex(targetColumns, column => collation.Equals(column.Name, leaf));
            if (index < 0)
                return null;
            var ordinal = sourceView is null ? index : sourceView.BaseColumnOrdinals[index];
            return ordinal < 0 ? null : DataMask.ForTableColumn(destinationTable, ordinal);
        }
        DataMask? SourceMask(string leaf)
        {
            var index = Array.FindIndex(sourceColumnNames, column => collation.Equals(column, leaf));
            return index < 0 ? null : sourceMasks?[index];
        }
        DataMask? ColumnMask(MultiPartName name) =>
            collation.Equals(name.ImmediateQualifier, sourceAlias) ? SourceMask(name.Leaf)
            : collation.Equals(name.ImmediateQualifier, targetAlias) || collation.Equals(name.ImmediateQualifier, defaultTargetName) ? TargetMask(name.Leaf)
            : name.Count == 1 ? (Array.Exists(targetColumns, column => collation.Equals(column.Name, name.Leaf)) ? TargetMask(name.Leaf) : SourceMask(name.Leaf))
            : null;

        var columns = new DataMask?[destinationTable.Columns.Length];
        var written = new bool[columns.Length];
        void Write(int ordinal, Expression value)
        {
            if (ordinal < 0)
                return;
            columns[ordinal] = DataMask.Merge(columns[ordinal], DataMask.Of(value, ColumnMask, typeOf: null));
            written[ordinal] = true;
        }
        foreach (var clause in whenClauses)
        {
            if (clause.Assignments is { } assignments)
            {
                foreach (var (ordinal, expression) in assignments)
                    Write(ordinal, expression);
            }
            if (clause is { InsertColumns: { } insertColumns, InsertValues: { } insertValues })
            {
                for (var i = 0; i < insertColumns.Length && i < insertValues.Length; i++)
                    Write(Array.IndexOf(destinationTable.Columns, insertColumns[i]), insertValues[i]);
            }
        }
        return Array.Exists(columns, mask => mask is not null) ? columns : null;
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
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema, DataMask?[]? Masks, bool Replayable, bool[]? Nullability) ParseMergeSource(ParserContext context)
    {
        context.MoveNextRequired();
        return context.Token is Operator { Character: '(' }
            ? ParseParenthesizedMergeSource(context)
            : ParseBareTableMergeSource(context);
    }

    /// <summary>
    /// <c>USING (VALUES …) AS alias [(cols)]</c> or <c>USING (SELECT …) AS
    /// alias [(cols)]</c>. The alias is required here (matches real SQL
    /// Server). Cursor on entry: the opening <c>(</c>. Cursor on exit: the
    /// next un-consumed token (typically <c>ON</c>).
    /// </summary>
    /// <remarks>
    /// A CTE prefix inside the parens is real's <strong>Msg 156</strong>: the
    /// source is a table source, and no parenthesized query position accepts a
    /// WITH. The prefix belongs ahead of the MERGE itself
    /// (<c>WITH c AS (…) MERGE … USING c</c>), which ships.
    /// </remarks>
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema, DataMask?[]? Masks, bool Replayable, bool[]? Nullability) ParseParenthesizedMergeSource(ParserContext context)
    {
        context.MoveNextRequired();

        Func<BatchContext, List<SqlValue[]>> materialize;
        SqlType[] sourceSchema;
        string[] selectionColumnNames;
        DataMask?[]? masks = null;
        bool[]? nullability;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Values })
        {
            var tuples = ParseValuesTuples(context);
            sourceSchema = new SqlType[tuples[0].Length];
            for (var i = 0; i < tuples[0].Length; i++)
                sourceSchema[i] = tuples[0][i].GetSqlType(context.Batch, name => throw SimulatedSqlException.UnboundColumnReference(name));
            nullability = new bool[sourceSchema.Length];
            var valuesNullability = new NullabilityContext(context.Batch, static _ => true, name => throw SimulatedSqlException.UnboundColumnReference(name));
            foreach (var tuple in tuples)
            {
                for (var i = 0; i < tuple.Length && i < nullability.Length; i++)
                    nullability[i] |= tuple[i].ResultIsNullable(valuesNullability);
            }
            selectionColumnNames = new string[tuples[0].Length];
            materialize = batch =>
            {
                var runtime = new RuntimeContext(name => throw SimulatedSqlException.UnboundColumnReference(name), batch);
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
            if (context.Token is ReservedKeyword { Keyword: Keyword.With } withKeyword)
                throw SimulatedSqlException.SyntaxErrorNearKeyword(withKeyword);
            // A MERGE's USING source is one of the derived-table shapes real
            // refuses NEXT VALUE FOR in (Msg 11719, probe-confirmed).
            Selection selection;
            using (context.EnterNextValueForScope(NextValueForScope.Nested))
            {
                selection = Selection.Parse(context, QueryScope.Nested(QueryPosition.Derived, null));
            }

            sourceSchema = selection.Schema;
            selectionColumnNames = selection.ColumnNames;
            masks = selection.ColumnMasks;
            nullability = selection.ColumnNullability;
            materialize = batch =>
            {
                // The USING source is its own query expression, so it owns the
                // securable list of its reads and reaches none of the statement
                // check sites the MERGE target's own permissions run through.
                PermissionEnforcement.CheckSubqueryReads(batch, selection);
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

        // A query source is a nested query, which no cached plan holds (see
        // NoteDmlPlan); a VALUES list is replayable.
        return (materialize, alias, columnNames, sourceSchema, masks, Replayable: true, nullability);
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
    private static (Func<BatchContext, List<SqlValue[]>> Materialize, string Alias, string[] ColumnNames, SqlType[] Schema, DataMask?[]? Masks, bool Replayable, bool[]? Nullability) ParseBareTableMergeSource(ParserContext context)
    {
        var objectName = BatchContext.ParseObjectName(context, acceptTableVariable: true);

        Func<BatchContext, List<SqlValue[]>> materialize;
        SqlType[] sourceSchema;
        string[] columnNames;
        DataMask?[]? masks = null;
        bool[]? nullability = null;

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
            return (materialize, cteAlias, columnNames, sourceSchema, ctePlan.ColumnMasks, Replayable: false, ctePlan.ColumnNullability);
        }

        // A linked server's table reads through the same remote query a FROM
        // source's four-part name does.
        if (objectName.Count == 4)
        {
            if (!context.Batch.TryResolveLinkedServerTable(objectName, out var linkedServer, out var remoteName, out var remoteColumns, out var remoteDbName, out var remoteSchemaName))
                throw SimulatedSqlException.RemoteTableNotFound(RemoteWrite.ResolveServer(context.Batch, objectName[0]), RemoteWrite.QuotedName(objectName));
            _ = RemoteWrite.ResolveServer(context.Batch, objectName[0]);
            if (!context.Batch.IsSkipping && context.Connection.CurrentTransaction is { IsDistributed: true })
                RemoteWrite.RequireNoTransaction(context.Batch, linkedServer);
            context.Batch.HasSessionScopedReference = true;
            sourceSchema = new SqlType[remoteColumns.Length];
            columnNames = new string[remoteColumns.Length];
            for (var i = 0; i < remoteColumns.Length; i++)
            {
                sourceSchema[i] = remoteColumns[i].Type;
                columnNames[i] = remoteColumns[i].Name;
            }
            var remoteSelection = Selection.ForLinkedServer(linkedServer, remoteDbName, remoteSchemaName, remoteName, remoteColumns);
            materialize = batch =>
            {
                var rs = remoteSelection.Execute(batch);
                var rows = new List<SqlValue[]>();
                foreach (var rowBytes in rs.RowBytes)
                    rows.Add(RowDecoder.DecodeRow(sourceSchema.AsSpan(), rowBytes));
                return rows;
            };
        }
        else if (context.Batch.TryResolveView(objectName, out var resolvedView))
        {
            var viewColumns = context.Batch.Connection.Simulation.BindViewColumns(context.Batch, resolvedView, objectName, out _);
            sourceSchema = new SqlType[viewColumns.Length];
            columnNames = new string[viewColumns.Length];
            for (var i = 0; i < viewColumns.Length; i++)
            {
                sourceSchema[i] = viewColumns[i].Type;
                columnNames[i] = viewColumns[i].Name;
                if (viewColumns[i].DerivedMask is { } viewMask)
                    (masks ??= new DataMask?[viewColumns.Length])[i] = viewMask;
            }
            nullability = Array.ConvertAll(viewColumns, static column => column.Nullable);
            var viewSelection = Selection.ForView(resolvedView, viewColumns);
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
                if (context.Batch.Connection.Simulation.DeclaresDataMasks && DataMask.ForTableColumn(heapTable, i) is { } columnMask)
                    (masks ??= new DataMask?[heapTable.Columns.Length])[i] = columnMask;
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
                foreach (var rowBytes in RowSecurity.FilterRows(sourceTable, ClusteredScan.Rows(sourceTable, batch.Connection.StatementIo), batch))
                {
                    batch.PollCancellation();
                    var fullValues = DecodeFullRow(sourceTable, rowBytes);
                    EvaluateComputedColumns(sourceTable, fullValues, batch);
                    rows.Add(fullValues);
                }
                return rows;
            };
            // Another database's table answers permission checks the session's
            // standing here says nothing about.
            return (materialize, alias, columnNames, sourceSchema, masks, Replayable: ReferenceEquals(context.Batch.DatabaseFor(sourceTable), context.Batch.CurrentDatabase), Array.ConvertAll(heapTable.Columns, static column => column.Nullable));
        }
        else
        {
            throw BatchContext.IsTableVariableName(objectName.Leaf)
                ? SimulatedSqlException.MustDeclareTableVariable(objectName.Leaf)
                : SimulatedSqlException.InvalidObjectName(objectName);
        }

        var defaultAlias = Selection.ConsumeOptionalAlias(context) ?? objectName.Leaf;
        _ = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: true, commitOnLegacyParen: true);
        return (materialize, defaultAlias, columnNames, sourceSchema, masks, Replayable: false, nullability);
    }

    /// <summary>Which sides of a <c>MERGE</c> a clause's column names bind against.</summary>
    private enum MergeNameScope
    {
        Both,
        Target,
        Source,
    }

    /// <summary>Whether <paramref name="name"/>'s qualifier is one of the two spellings of a MERGE side.</summary>
    private static bool NamesMergeSide(Collation collation, MultiPartName name, string alias, string otherSpelling) =>
        collation.Equals(name.ImmediateQualifier, alias)
        // An alias hides the target's own name: `MERGE t AS x … ON t.id = …`
        // is Msg 4104 (probed 2026-10-01 against SQL Server 2025).
        || (collation.Equals(alias, otherSpelling) && collation.Equals(name.ImmediateQualifier, otherSpelling));

    /// <summary>
    /// Types a column reference against the MERGE's own two sides: the target
    /// (by alias, by the target's own name, or unqualified) and then the
    /// source. Installed as <see cref="ParserContext.OuterTypeResolver"/> while
    /// the ON predicate and the WHEN clauses parse, so a correlated subquery
    /// inside either binds to a MERGE column rather than failing to resolve.
    /// </summary>
    private static SqlType ResolveMergeColumnType(
        Collation collation,
        MultiPartName name,
        string targetAlias,
        string defaultTargetName,
        HeapColumn[] targetColumns,
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        MergeNameScope scope = MergeNameScope.Both)
    {
        // A clause reading one side sees nothing of the other: the other side's
        // qualifier is as unbound as any unknown one (Msg 4104).
        var namesTarget = scope != MergeNameScope.Source && NamesMergeSide(collation, name, targetAlias, defaultTargetName);
        var namesSource = scope != MergeNameScope.Target && collation.Equals(name.ImmediateQualifier, sourceAlias);
        var unqualified = name.Count == 1;
        if (scope != MergeNameScope.Source && (namesTarget || unqualified))
        {
            foreach (var column in targetColumns)
            {
                if (collation.Equals(column.Name, name.Leaf))
                {
                    // An unqualified name both sides carry is Msg 209.
                    if (unqualified && scope == MergeNameScope.Both && Array.Exists(sourceColumnNames, source => collation.Equals(source, name.Leaf)))
                        throw SimulatedSqlException.AmbiguousColumnName(name.Leaf);
                    return column.Type;
                }
            }
        }
        if (scope != MergeNameScope.Target && (namesSource || unqualified))
        {
            for (var i = 0; i < sourceColumnNames.Length; i++)
            {
                if (collation.Equals(sourceColumnNames[i], name.Leaf))
                    return sourceSchema[i];
            }
        }

        // Which side failed decides the message, matching real: a name whose
        // qualifier names one of the two sides (or carries no qualifier at all)
        // is a bad *column* — Msg 207 — while a qualifier neither side answers
        // to is an unbindable identifier — Msg 4104. Both probe-confirmed at
        // compile time, on an empty rowset and at CREATE of a module.
        return unqualified || namesTarget || namesSource
            ? throw SimulatedSqlException.InvalidColumnName(name)
            : throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
    }

    /// <summary>
    /// A <c>MERGE</c>'s two sides as the scopes its clauses' names bind in
    /// (<see cref="ResolveMergeColumnType"/>), held apart from the parse: a
    /// correlated subquery in a clause keeps the scope it parsed under, and a
    /// cached plan must reach no parse.
    /// </summary>
    private sealed class MergeColumnScope(
        Collation collation, string targetAlias, string defaultTargetName, HeapColumn[] targetColumns, string sourceAlias, string[] sourceColumnNames, SqlType[] sourceSchema)
    {
        private readonly Collation collation = collation;
        private readonly string targetAlias = targetAlias, defaultTargetName = defaultTargetName, sourceAlias = sourceAlias;
        private readonly HeapColumn[] targetColumns = targetColumns;
        private readonly string[] sourceColumnNames = sourceColumnNames;
        private readonly SqlType[] sourceSchema = sourceSchema;

        public SqlType Both(MultiPartName name) => this.Resolve(name, MergeNameScope.Both);

        public SqlType TargetOnly(MultiPartName name) => this.Resolve(name, MergeNameScope.Target);

        public SqlType SourceOnly(MultiPartName name) => this.Resolve(name, MergeNameScope.Source);

        /// <summary>
        /// A NOT MATCHED BY SOURCE condition reads only the target: any other
        /// name is Msg 5333, but for a miss qualified by the target, which
        /// stays the ordinary Msg 207.
        /// </summary>
        public SqlType BySourceCondition(MultiPartName name)
        {
            try
            {
                return this.TargetOnly(name);
            }
            catch (SimulatedSqlException miss) when (miss.Number is 207 or 4104 && !NamesMergeSide(this.collation, name, this.targetAlias, this.defaultTargetName))
            {
                throw SimulatedSqlException.MergeBySourceConditionOutOfScope(name);
            }
        }

        /// <summary>A NOT MATCHED condition reads only the source, as <see cref="BySourceCondition"/> the target (Msg 5334).</summary>
        public SqlType NotMatchedCondition(MultiPartName name)
        {
            try
            {
                return this.SourceOnly(name);
            }
            catch (SimulatedSqlException miss) when (miss.Number is 207 or 4104 && !NamesMergeSide(this.collation, name, this.sourceAlias, this.sourceAlias))
            {
                throw SimulatedSqlException.MergeNotMatchedConditionOutOfScope(name);
            }
        }

        private SqlType Resolve(MultiPartName name, MergeNameScope scope) => ResolveMergeColumnType(
            this.collation, name, this.targetAlias, this.defaultTargetName, this.targetColumns, this.sourceAlias, this.sourceColumnNames, this.sourceSchema, scope);
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
        View? sourceView,
        string writtenName,
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

        var scope = new MergeColumnScope(context.CurrentDatabase.Collation, targetAlias, defaultTargetName, targetColumns, sourceAlias, sourceColumnNames, sourceSchema);

        while (context.Token is ReservedKeyword { Keyword: Keyword.When } whenToken)
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

            // Real binds every MATCHED condition, then the NOT MATCHED ones,
            // then the NOT MATCHED BY SOURCE ones, whatever order they are
            // written in (probed 2026-09-28 against SQL Server 2025).
            context.Batch.BindErrors?.EnterClause(whenToken, kind switch
            {
                WhenClauseKind.Matched => BindClause.MergeCondition,
                WhenClauseKind.NotMatchedByTarget => BindClause.MergeNotMatchedCondition,
                _ => BindClause.MergeBySourceCondition,
            });
            Func<MultiPartName, SqlType> conditionResolver = kind switch
            {
                WhenClauseKind.Matched => scope.Both,
                WhenClauseKind.NotMatchedByTarget => scope.NotMatchedCondition,
                _ => scope.BySourceCondition,
            };

            // Optional AND search_condition.
            BooleanExpression? searchCondition = null;
            if (context.Token is ReservedKeyword { Keyword: Keyword.And })
            {
                context.MoveNextRequired();
                var conditionAggregates = new List<AggregateExpression>();
                using (ParserScope.Enter(ref context.OuterTypeResolver, conditionResolver))
                using (ParserScope.Enter(ref context.AggregateCollector, conditionAggregates))
                {
                    searchCondition = BooleanExpression.Parse(context);
                }
                Selection.RefuseClauseAggregates(context.Batch, conditionAggregates, static () => SimulatedSqlException.AggregateInMergeWhenClause());
                searchCondition.BindCarryingTypeChecks(context.Batch, conditionResolver);
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
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.MergeAction);
            context.MoveNextRequired();

            // An action reads its clause's side too, and misses there with the
            // ordinary binder errors (probed 2026-09-28).
            var clause = ParseMergeAction(context, kind, searchCondition, destinationTable, sourceView, writtenName, targetAlias, kind switch
            {
                WhenClauseKind.Matched => scope.Both,
                WhenClauseKind.NotMatchedByTarget => scope.SourceOnly,
                _ => scope.TargetOnly,
            });
            // A family takes each action at most once.
            if (clauses.Exists(earlier => earlier.Kind == kind && earlier.Action == clause.Action))
                throw SimulatedSqlException.MergeActionRepeated(kind == WhenClauseKind.Matched ? "WHEN MATCHED" : "WHEN NOT MATCHED BY SOURCE", clause.Action == MergeActionKind.Update ? "UPDATE" : "DELETE");
            clauses.Add(clause);
        }

        return clauses.Count == 0 ? throw SimulatedSqlException.SyntaxErrorNear(context) : clauses;
    }

    private static WhenClause ParseMergeAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        string writtenName,
        string targetAlias,
        Func<MultiPartName, SqlType> resolveType)
    {
        // A MERGE action's own expressions get real's dedicated refusal
        // (Msg 11742) rather than any of the query-shaped ones: the only
        // sequence a MERGE may draw from is one a default constraint on the
        // target names, which the insert path reaches without writing
        // NEXT VALUE FOR at all. Probe-confirmed for both the UPDATE SET list
        // and the INSERT VALUES tuple — and a CASE around the reference still
        // reports Msg 11741, which is why this is a floor rather than a set.
        // An action owns no aggregate: its SET list is Msg 157 as an UPDATE's
        // is, its VALUES list Msg 5310 as an INSERT's is.
        var actionAggregates = new List<AggregateExpression>();
        var isInsert = context.Token is ReservedKeyword { Keyword: Keyword.Insert };
        WhenClause clause;
        using (context.EnterNextValueForScope(NextValueForScope.MergeAction))
        using (ParserScope.Enter(ref context.AggregateCollector, actionAggregates))
        {
            clause = context.Token switch
            {
                ReservedKeyword { Keyword: Keyword.Insert } => ParseMergeInsertAction(context, kind, searchCondition, destinationTable, sourceView, writtenName, resolveType),
                ReservedKeyword { Keyword: Keyword.Update } => ParseMergeUpdateAction(context, kind, searchCondition, destinationTable, sourceView, writtenName, targetAlias, resolveType),
                ReservedKeyword { Keyword: Keyword.Delete } => ParseMergeDeleteAction(context, kind, searchCondition),
                _ => throw SimulatedSqlException.SyntaxErrorNear(context),
            };
        }
        Selection.RefuseClauseAggregates(
            context.Batch,
            actionAggregates,
            isInsert ? static () => SimulatedSqlException.AggregateInValuesList() : static () => SimulatedSqlException.AggregateInSetList());
        return clause;
    }

    private static WhenClause ParseMergeInsertAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        string writtenName,
        Func<MultiPartName, SqlType> resolveType)
    {
        if (kind == WhenClauseKind.Matched)
            throw SimulatedSqlException.MergeInsertNotAllowedInClause("WHEN MATCHED");
        if (kind == WhenClauseKind.NotMatchedBySource)
            throw SimulatedSqlException.MergeInsertNotAllowedInClause("WHEN NOT MATCHED BY SOURCE");

        var insertColumns = new List<HeapColumn>();
        var afterInsert = context.GetNextRequired();
        // INSERT DEFAULT VALUES: every column takes its default.
        if (afterInsert is ReservedKeyword { Keyword: Keyword.Default })
        {
            if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Values })
                throw SimulatedSqlException.SyntaxErrorNear(context);
            context.MoveNextOptional();
            return new WhenClause(kind, MergeActionKind.Insert, searchCondition, assignments: null, insertColumns: [], insertValues: [], insertColumnsImplied: true);
        }
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
                HeapColumn col;
                try
                {
                    col = ResolveInsertTargetColumn(context.Batch.CurrentDatabase.Collation, colTok.Value, destinationTable, sourceView, writtenName);
                }
                catch (SimulatedSqlException missing) when (missing.Number == 207 && context.Batch.BindErrors is { } report && report.Covers(colTok))
                {
                    // A stand-in keeps the list's length for the VALUES arity check.
                    report.Record(missing, colTok.StartIndex, BindClause.MergeInsertColumns);
                    col = new HeapColumn(colTok.Value, SqlType.Int32, null, nullable: true);
                }
                if (col.Computed is not null && col.GraphKind == GraphColumnKind.None && !col.IsColumnSet)
                    throw SimulatedSqlException.ColumnCannotBeModified(col.Name);
                if (GraphColumns.IsInternal(col.GraphKind))
                    throw SimulatedSqlException.InternalGraphColumnAccess(col.Name, state: 1);
                if (col.Type == SqlType.RowVersion)
                    throw SimulatedSqlException.CannotInsertExplicitTimestamp();
                // A period column takes no value from an insert action (probed
                // 2026-10-04 against SQL Server 2025).
                if (col.GeneratedAs != GeneratedAlwaysAsRow.None)
                    throw SimulatedSqlException.CannotInsertExplicitGeneratedAlways(QualifyTableName(destinationTable, context.Batch.DatabaseFor(destinationTable)));
                if (insertColumns.Contains(col))
                    throw SimulatedSqlException.ColumnAssignedMoreThanOnce(col.Name);
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
        using (ParserScope.Enter(ref context.OuterTypeResolver, resolveType))
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
                columns = BuildMergeInsertColumnsForView(sourceView, destinationTable);
            }
            else
            {
                var defaultCols = new List<HeapColumn>();
                foreach (var c in destinationTable.Columns)
                {
                    if (IsImplicitInsertColumn(c) && c.Type != SqlType.RowVersion)
                        defaultCols.Add(c);
                }
                columns = [.. defaultCols];
            }
        }
        else
        {
            columns = [.. insertColumns];
        }

        // The width mismatch reports what the ordinary INSERT forms report:
        // a written column list is measured against itself (Msg 109 / 110),
        // while the column-list-less form is measured against the target's
        // definition (Msg 213) — a view target included, whose definition is
        // its own projection rather than the base table's. Probe-confirmed
        // against SQL Server 2025.
        if (columns.Length != insertValues.Count)
        {
            throw insertColumns.Count == 0
                ? SimulatedSqlException.ColumnCountDoesNotMatchTableDefinition()
                : columns.Length < insertValues.Count
                    ? SimulatedSqlException.FewerInsertColumnsThanValues()
                    : SimulatedSqlException.MoreInsertColumnsThanValues();
        }

        // Each value meets its column's one-way assignment rule while
        // compiling (probe-confirmed against SQL Server 2025).
        for (var i = 0; i < columns.Length; i++)
        {
            if (insertValues[i] is not Parser.Expressions.DefaultValueExpression)
                AssignmentRules.RequireAssignable(insertValues[i], insertValues[i].GetSqlType(context.Batch, resolveType), columns[i].Type);
        }

        return new WhenClause(kind, MergeActionKind.Insert, searchCondition, assignments: null, insertColumns: columns, insertValues: [.. insertValues], insertColumnsImplied: insertColumns.Count == 0);
    }

    private static WhenClause ParseMergeUpdateAction(
        ParserContext context,
        WhenClauseKind kind,
        BooleanExpression? searchCondition,
        HeapTable destinationTable,
        View? sourceView,
        string writtenName,
        string targetAlias,
        Func<MultiPartName, SqlType> resolveType)
    {
        if (kind == WhenClauseKind.NotMatchedByTarget)
            throw SimulatedSqlException.MergeUpdateNotAllowedInNotMatched();

        if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var assignments = new List<(int Ordinal, Expression Expr)>();
        using (ParserScope.Enter(ref context.OuterTypeResolver, resolveType))
        {
            while (true)
            {
                if (context.GetNextRequired() is not StringToken first)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                var setTarget = new MultiPartName(first.Value);
                context.MoveNextRequired();
                while (context.Token is Operator { Character: '.' })
                {
                    if (context.GetNextRequired() is not StringToken part)
                        throw SimulatedSqlException.SyntaxErrorNear(context);
                    setTarget = setTarget.WithAddedPart(part.Value);
                    context.MoveNextRequired();
                }

                var columnName = setTarget.Leaf;
                Expression rhs;
                var setsDefault = false;
                var unboundTarget = false;
                if (setTarget.Count == 2 && sourceView is null && context.Token is Operator { Character: '(' }
                    && Collation.Baseline.Equals(columnName, "modify")
                    && Array.Find(destinationTable.Columns, c => context.Batch.CurrentDatabase.Collation.Equals(c.Name, setTarget[0])) is { Type: JsonSqlType })
                {
                    // The json type's `col.modify(path, value)` mutator, the
                    // whole clause (probed 2026-09-27 against SQL Server 2025).
                    columnName = setTarget[0];
                    rhs = JsonModify.ParseMethod(context, new Reference(new MultiPartName(targetAlias).WithAddedPart(columnName)), columnName);
                }
                else if (setTarget.Count == 2 && sourceView is null
                    && !context.Batch.CurrentDatabase.Collation.Equals(setTarget[0], targetAlias)
                    && Array.Find(destinationTable.Columns, c => context.Batch.CurrentDatabase.Collation.Equals(c.Name, setTarget[0])) is { Type: SpatialSqlType spatialType })
                {
                    // A spatial column's `col.STSrid = …`, as in an UPDATE.
                    columnName = setTarget[0];
                    rhs = ParseSpatialMutation(context, new Reference(new MultiPartName(targetAlias).WithAddedPart(columnName)), columnName, spatialType, setTarget.Leaf);
                }
                else
                {
                    if (context.Token is not Operator { Character: '=' })
                        throw SimulatedSqlException.SyntaxErrorNear(context);

                    // The assignment target admits only the merge target as
                    // written — its alias when one was given, so
                    // `MERGE t AS a … UPDATE SET t.v = 1` is Msg 4104 even though
                    // `t` is the base table (probed 2026-08-05).
                    if (setTarget.ImmediateQualifier is { } setQualifier
                        && !context.Batch.CurrentDatabase.Collation.Equals(setQualifier, targetAlias))
                    {
                        if (context.Batch.BindErrors?.Covers(first) != true)
                            throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(setTarget.ToString());
                        context.Batch.BindErrors.Record(SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(setTarget.ToString()), first.StartIndex);
                        unboundTarget = true;
                    }

                    context.MoveNextRequired();
                    setsDefault = context.Token is ReservedKeyword { Keyword: Keyword.Default };
                    rhs = setsDefault ? ColumnDefaultValue.Unbound : Expression.Parse(context);
                    if (setsDefault)
                        context.MoveNextOptional();
                }

                // A target already reported binds no further; its value still
                // does.
                if (unboundTarget || ResolveMergeSetOrdinal(context, first, columnName, destinationTable, sourceView, writtenName) is not { } ordinal)
                {
                    if (!setsDefault)
                        _ = rhs.GetSqlType(context.Batch, resolveType);
                }
                else
                {
                    var targetColumn = destinationTable.Columns[ordinal];
                    if (targetColumn.Identity is not null)
                        throw SimulatedSqlException.CannotUpdateIdentityColumn(targetColumn.Name);
                    if (targetColumn.Computed is not null && !targetColumn.IsColumnSet)
                        throw SimulatedSqlException.ColumnCannotBeModified(targetColumn.Name);
                    if (targetColumn.Type == SqlType.RowVersion)
                        throw SimulatedSqlException.CannotUpdateTimestampColumn();
                    if (targetColumn.GeneratedAs != GeneratedAlwaysAsRow.None)
                        throw SimulatedSqlException.CannotUpdateGeneratedAlways(QualifyTableName(destinationTable, context.Batch.DatabaseFor(destinationTable)));
                    if (setsDefault)
                        rhs = ColumnDefaultValue.Bind(targetColumn);
                    AssignmentRules.RequireAssignable(rhs, rhs.GetSqlType(context.Batch, resolveType), targetColumn.Type);
                    if (assignments.Exists(assignment => assignment.Ordinal == ordinal))
                        throw SimulatedSqlException.ColumnAssignedMoreThanOnce(sourceView is null ? targetColumn.Name : columnName);
                    assignments.Add((ordinal, rhs));
                }

                if (context.Token is not Operator { Character: ',' })
                    break;
            }
        }

        RejectColumnSetBesideSparse(destinationTable, assignments);
        return new WhenClause(kind, MergeActionKind.Update, searchCondition, assignments: assignments, insertColumns: null, insertValues: null);
    }

    /// <summary>
    /// Resolves a MERGE <c>UPDATE SET</c> target's user-facing column name to
    /// the base-table ordinal the WHEN executor mutates. A view translates
    /// through its OutputColumns and BaseColumnOrdinals (a derived projection
    /// refuses with Msg 4406); a table looks the name up directly. A name
    /// nothing answers is Msg 207 — recorded, with null returned, while the
    /// statement is read for its whole bind error report.
    /// </summary>
    private static int? ResolveMergeSetOrdinal(ParserContext context, Token target, string columnName, HeapTable destinationTable, View? sourceView, string writtenName)
    {
        var collation = context.Batch.CurrentDatabase.Collation;
        if (sourceView is not null)
        {
            for (var i = 0; i < sourceView.OutputColumns.Length; i++)
            {
                if (collation.Equals(sourceView.OutputColumns[i].Name, columnName))
                {
                    return sourceView.BaseColumnOrdinals[i] is var baseOrdinal and >= 0
                        ? baseOrdinal
                        : throw SimulatedSqlException.ViewDmlTouchesDerivedField(writtenName);
                }
            }
        }
        else
        {
            for (var i = 0; i < destinationTable.Columns.Length; i++)
            {
                if (collation.Equals(destinationTable.Columns[i].Name, columnName))
                    return i;
            }
        }

        if (context.Batch.BindErrors is not { } report || !report.Covers(target))
            throw SimulatedSqlException.InvalidColumnName(columnName);
        report.Record(SimulatedSqlException.InvalidColumnName(columnName), target.StartIndex);
        return null;
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
        string sourceAlias,
        string[] sourceColumnNames,
        SqlType[] sourceSchema,
        DataMask?[]? sourceMasks,
        MergeOutputSides sides,
        ViewOutputShape? view,
        OutputProjection? logged = null)
    {
        if (context.Token is not UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            return null;

        var targetColumns = view?.Columns ?? destinationTable.Columns;
        var expressions = new List<Expression>();
        var columnNames = new List<string>();

        SqlType ResolveOutputType(MultiPartName name)
        {
            if (BuiltInToken.EqualsAny(name.ImmediateQualifier, "INSERTED", "DELETED"))
            {
                for (var i = 0; i < targetColumns.Length; i++)
                {
                    if (context.Batch.CurrentDatabase.Collation.Equals(targetColumns[i].Name, name.Leaf))
                        return view?.Admit(i, BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED")) ?? targetColumns[i].Type;
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

        // A spatial column's property (`inserted.loc.STSrid`) tells itself
        // apart from a three-part column name by the pseudo-tables' types.
        var collation = context.Batch.CurrentDatabase.Collation;
        using var pseudoColumns = ParserScope.Enter(ref context.OuterTypeResolver, name =>
            name.Count == 2 && BuiltInToken.EqualsAny(name.ImmediateQualifier, "INSERTED", "DELETED")
                && Array.Find(targetColumns, column => collation.Equals(column.Name, name.Leaf)) is { } column
                ? column.Type
                : throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString()));

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
                    cols = new string[targetColumns.Length];
                    for (var i = 0; i < targetColumns.Length; i++)
                        cols[i] = targetColumns[i].Name;
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
            var expr = ParseOutputItem(context);
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
        try
        {
            for (var i = 0; i < expressions.Count; i++)
                schema[i] = IsMergeActionRef(expressions[i]) ? NVarcharSqlType.Get(10, context.Batch.CurrentDatabase.Collation, Coercibility.CoercibleDefault) : expressions[i].GetSqlType(context.Batch, ResolveOutputType);
        }
        catch (SimulatedSqlException error) when (view?.Refusals is { Count: > 0 } refusals)
        {
            refusals.Add(error);
        }

        // MERGE reaches the same INTO parser the other three statements use;
        // its own projection type predated that and never grew the branch.
        // Its errors follow the clause's Msg 404s (probed 2026-09-27).
        OutputTarget? outputTarget;
        try
        {
            outputTarget = TryParseOutputIntoTarget(context, expressions.Count, destinationTable.Name, logged);
        }
        catch (SimulatedSqlException error) when (view?.Refusals is { Count: > 0 } refusals)
        {
            refusals.Add(error);
            outputTarget = null;
        }
        view?.ThrowRefusals();
        outputTarget?.RequireAssignable(expressions, schema);

        bool ColumnIsNullable(MultiPartName name)
        {
            var collation = context.Batch.CurrentDatabase.Collation;
            if (BuiltInToken.Equals(name.ImmediateQualifier, "INSERTED"))
                return sides.InsertedMayBeAbsent || PseudoColumnIsNullable(collation, targetColumns, name);
            if (BuiltInToken.Equals(name.ImmediateQualifier, "DELETED"))
                return sides.DeletedMayBeAbsent || PseudoColumnIsNullable(collation, targetColumns, name);
            if (sides.SourceMayBeAbsent || sides.SourceNullability is not { } sourceNullability)
                return true;
            var ordinal = Array.FindIndex(sourceColumnNames, column => collation.Equals(column, name.Leaf));
            return ordinal < 0 || ordinal >= sourceNullability.Length || sourceNullability[ordinal];
        }

        var projection = NoteClientOutput(context.Batch, new OutputProjection(
            [.. expressions], [.. columnNames], schema, destinationTable,
            (sourceAlias, sourceColumnNames, sourceSchema), context.Batch, outputTarget, sourceMasks, view, logged)
        {
            Nullability = outputTarget is null ? InferOutputNullability(context.Batch, expressions, ColumnIsNullable, ResolveOutputType) : null,
        });
        return IsClientOutputAfterInto(context, projection)
            ? TryParseMergeOutputClause(context, destinationTable, sourceAlias, sourceColumnNames, sourceSchema, sourceMasks, sides, view, logged: projection)
            : projection;
    }

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
        // A table value constructor owns no aggregate (Msg 5310), but one
        // reading only an enclosing query's columns is that query's.
        var enclosingCollector = context.AggregateCollector;
        var valuesAggregates = new List<AggregateExpression>();
        List<Expression[]> tuples;
        // Nor a windowed function (Msg 4108), outside a subquery in a cell.
        using (ParserScope.Enter(ref context.AggregateCollector, valuesAggregates))
        using (ParserScope.Enter(ref context.AllowsWindowExpressions, false))
        {
            tuples = ParseValuesTupleList(context, allowDefault);
        }
        foreach (var aggregate in valuesAggregates)
        {
            var readsColumn = false;
            aggregate.Operand?.VisitColumnReferences(_ => readsColumn = true);
            if (!readsColumn || !Selection.MoveToEnclosingQuery(context.Batch, aggregate, enclosingCollector))
            {
                Selection.RefuseAggregatePlacement(context.Batch, aggregate, SimulatedSqlException.AggregateInValuesList());
                break;
            }
        }
        return tuples;
    }

    private static List<Expression[]> ParseValuesTupleList(ParserContext context, bool allowDefault)
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

    /// <summary>
    /// Which of a MERGE's row images an <c>OUTPUT</c> row can lack, settled by
    /// its clauses: <c>INSERTED</c> when a clause deletes, <c>DELETED</c> when
    /// one inserts, the source when one acts <c>BY SOURCE</c> — each such
    /// image reads nullable on the wire (probed 2026-10-01 against SQL Server
    /// 2025) — plus the source's own column nullability.
    /// </summary>
    private readonly struct MergeOutputSides(bool insertedMayBeAbsent, bool deletedMayBeAbsent, bool sourceMayBeAbsent, bool[]? sourceNullability)
    {
        public readonly bool InsertedMayBeAbsent = insertedMayBeAbsent;
        public readonly bool DeletedMayBeAbsent = deletedMayBeAbsent;
        public readonly bool SourceMayBeAbsent = sourceMayBeAbsent;
        public readonly bool[]? SourceNullability = sourceNullability;
    }

    /// <summary>The <see cref="MergeOutputSides"/> of a MERGE with <paramref name="whenClauses"/>.</summary>
    private static MergeOutputSides MergeOutputNullability(List<WhenClause> whenClauses, bool[]? sourceNullability)
    {
        bool deletes = false, inserts = false, bySource = false;
        foreach (var clause in whenClauses)
        {
            deletes |= clause.Action == MergeActionKind.Delete;
            inserts |= clause.Action == MergeActionKind.Insert;
            bySource |= clause.Kind == WhenClauseKind.NotMatchedBySource;
        }
        return new(deletes, inserts, bySource, sourceNullability);
    }

    private sealed class WhenClause(
        WhenClauseKind kind,
        MergeActionKind action,
        BooleanExpression? searchCondition,
        List<(int Ordinal, Expression Expr)>? assignments,
        HeapColumn[]? insertColumns,
        Expression[]? insertValues,
        bool insertColumnsImplied = false)
    {
        public readonly WhenClauseKind Kind = kind;
        public readonly MergeActionKind Action = action;
        public readonly BooleanExpression? SearchCondition = searchCondition;
        public readonly List<(int Ordinal, Expression Expr)>? Assignments = assignments;
        public readonly HeapColumn[]? InsertColumns = insertColumns;
        public readonly Expression[]? InsertValues = insertValues;

        /// <summary>
        /// True for an <c>INSERT</c> that wrote no column list — the
        /// <c>VALUES</c> form taking the target's shape, or <c>DEFAULT
        /// VALUES</c> — which names no base table to route a join view's
        /// write to.
        /// </summary>
        public readonly bool InsertColumnsImplied = insertColumnsImplied;
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

        internal override void Describe(NodeShape shape) { }
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
