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
        // Real binds FROM and WHERE first and stops there when either fails,
        // then every SET target, stopping again, then the SET values and
        // OUTPUT (probed 2026-09-27: `UPDATE t SET x1 = x2 WHERE x3 = 1`
        // reports only x3, `UPDATE t SET x1 = x2, b = x3` only x1).
        var bindErrors = context.Batch.BindErrors;
        bindErrors?.OpenScope(context.Token);
        bindErrors?.SetBarriers(BindClause.SetTarget, BindClause.SetValue);
        using var inUpdate = ParserScope.Enter(ref context.InUpdateOrMerge, true);
        context.MoveNextRequired();
        var top = Selection.ParseDmlTopClause(context);
        // A name that resolves to nothing is the alias of the FROM clause
        // that follows (multi-table form), which must then provide the binding
        // via alias-matching; aliases are always single-segment, so a
        // multi-part name that fails to resolve is always Msg 208.
        var target = ParseDmlTarget(context, RemoteWriteKind.Update);
        var (leadingIdent, remoteWrite, leadingView, leadingTable) = (target.Name, target.Remote, target.View, target.Table);

        // View target: route to base table with view-aware column lookups,
        // visibility filtering, and (optional) WITH CHECK OPTION enforcement.
        // A FROM clause makes it the joined form, whose target is a source of
        // that clause (ExecuteJoinedViewTargetUpdate).
        DmlViewRoute? viewRoute = null;
        if (leadingView is not null)
        {
            // A multi-source body has no single base table to route to up
            // front — which base the statement writes is the SET list's to
            // say — so it leaves `leadingTable` null and the join-view path
            // below picks up once the SET list has parsed. An INSTEAD OF
            // UPDATE trigger takes the write whatever the view's shape.
            viewRoute = RouteViewWrite(context.Batch, leadingView, TriggerActions.Update);
            if (viewRoute == DmlViewRoute.Refused)
            {
                context.MoveNextOptional();
                throw RefuseNonUpdatableViewWrite(context, leadingView, leadingIdent, isUpdate: true);
            }
            if (viewRoute is DmlViewRoute.BaseTable or DmlViewRoute.JoinView)
                RejectCheckOptionOverRowLimit(leadingView, leadingIdent.ToString());
        }

        context.MoveNextRequired();
        var targetHints = Selection.ParseOptionalTableHints(context, allowLegacyParenForm: false);
        Selection.ValidateDmlTargetHints(targetHints, leadingIdent.ToString(), "UPDATE");
        // A write's target takes no NOEXPAND, an indexed view's included
        // (probed 2026-10-04 against SQL Server 2025).
        if (targetHints.NoExpand)
            throw SimulatedSqlException.NoExpandHintInvalid(leadingIdent.ToString(), state: 1);
        // A target the FROM clause names — an alias, or a view written through
        // in a join — is read from that clause ahead of the SET list, whose
        // values bind against its sources.
        var preParsedFrom = context.Token is ReservedKeyword { Keyword: Keyword.Set } && (leadingTable is null || leadingView is not null)
            ? Selection.PreParseMutationFrom(context)
            : null;
        var joinedView = leadingView is not null && preParsedFrom is not null;
        // A target the batch creates ahead of the statement defers its whole
        // bind, its SET list's refusals included (probed 2026-10-01 against
        // SQL Server 2025: Msg 157 and 4108 come only once it runs).
        if (context.Batch.IsSkipping && leadingTable is null && leadingView is null && remoteWrite is null && preParsedFrom is null)
            context.Batch.CurrentStatement.BindsDeferredSource = true;
        // Phase 1a: when the leading identifier resolved to a concrete table
        // (the simple `UPDATE t SET …` case), lock it now. The
        // multi-table-alias form's target is determined later via the FROM
        // clause; that path's lock is deferred to phase 1b.
        if (leadingTable is not null && !joinedView)
            LockWriteTable(context.Batch, leadingTable, "UPDATE", hints: targetHints);
        if (context.Token is not ReservedKeyword { Keyword: Keyword.Set })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        bindErrors?.EnterClause(context.Token, BindClause.SetValue);

        // Phase-1 SET parsing: raw (columnName, expr) pairs without ordinal
        // resolution — target may not be known yet. Each entry recognizes
        // single or qualified column names on the LHS, plain '=' or compound
        // arithmetic-assignment (+= -= *= /= %= &= |= ^=) on the operator
        // slot. Compound forms desugar to FromCompoundOp(op, Reference(col),
        // rhs) so the per-row ResolveOriginal resolver evaluates the column's
        // pre-update value as the LHS — matches probe-confirmed
        // "UPDATE t SET v += rhs" semantics on a real SQL Server instance.
        // A clause assigning only a variable carries a null column name.
        var rawAssignments = new List<(string? ColumnName, Expression Expr)>();

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
        if (leadingTable is { } scopeTable && !joinedView)
        {
            context.OuterTypeResolver = UpdateTargetTypeResolver(context.CurrentDatabase, leadingIdent, scopeTable, savedOuterTypeResolver);
        }
        else if (preParsedFrom is { } preFrom)
        {
            context.OuterTypeResolver = Selection.ColumnTypeResolverFor([.. preFrom.Sources], savedOuterTypeResolver);
        }

        // The SET list owns no aggregate (Msg 157), a subquery's over the
        // target's columns included. Restored with the scope after the loop.
        var savedCollector = context.AggregateCollector;
        var setAggregates = new List<AggregateExpression>();
        context.AggregateCollector = setAggregates;

        // A write through a partitioned view refuses a SET value reading one
        // of its members (Msg 4439), so the list's reads are recorded.
        var partitionedReads = viewRoute == DmlViewRoute.Partitioned || WritesPartitionedSource(context, preParsedFrom, leadingIdent) ? new List<SchemaObject>() : null;
        using var recordingReads = ParserScope.Enter(ref context.PartitionedWriteReads, partitionedReads);

        while (true)
        {
            if (context.GetNextRequired() is AtPrefixedString variable)
            {
                ParseVariableSetClause(context, leadingIdent, variable, rawAssignments);
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }
            if (context.Token is not StringToken first)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // The assignment target carries the same multi-part grammar a read
            // does — `t.col`, `schema.t.col`, `db.schema.t.col` all bind, and a
            // fifth segment is Msg 4104 from WithAddedPart.
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

            // `col.modify('…')` is the mutator form of a SET clause — the
            // whole clause, with no assignment operator. Only a one-part
            // column name carries it: real answers Msg 102 for
            // `t.col.modify(…)`, which falls out of the assignment-operator
            // check below since the three-part shape lands here instead.
            // A mutator on a qualified column is a syntax error at the method's
            // name (probed 2026-09-27 against SQL Server 2025).
            if (setTarget.Count > 2 && context.Token is Operator { Character: '(' } && Collation.Baseline.Equals(columnName, "modify"))
                throw SimulatedSqlException.SyntaxErrorNearText(columnName);

            if (setTarget.Count == 2 && context.Token is Operator { Character: '(' }
                && Collation.Baseline.Equals(columnName, "modify") && IsJsonMutatorTarget(context, leadingTable, setTarget[0]))
            {
                rawAssignments.Add((setTarget[0], JsonModify.ParseMethod(context, new Reference(leadingIdent.WithAddedPart(setTarget[0])), setTarget[0])));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }

            // `col.WRITE(expression, offset, length)` rewrites part of a MAX
            // string or binary column's value.
            if (setTarget.Count == 2 && context.Token is Operator { Character: '(' } && Collation.Baseline.Equals(columnName, "write")
                && ClrTypeColumn(context, leadingTable, setTarget[0]) is null)
            {
                rawAssignments.Add((setTarget[0], WriteMutator.ParseMethod(context, new Reference(leadingIdent.WithAddedPart(setTarget[0])), setTarget[0])));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }

            // A spatial column's member: `SET loc.STSrid = …` re-stamps the
            // value, and anything else is refused as it compiles.
            if (setTarget.Count == 2 && !Selection.QualifierIsDmlTarget(context.CurrentDatabase, leadingIdent, setTarget)
                && SpatialSetTargetType(context, leadingIdent, leadingView, setTarget[0]) is { } spatialColumnType)
            {
                rawAssignments.Add((setTarget[0], ParseSpatialMutation(context, new Reference(leadingIdent.WithAddedPart(setTarget[0])), setTarget[0], spatialColumnType, columnName)));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }

            // A CLR user-defined type column's property, field or mutator
            // method: `SET col.X = …` or `SET col.Mutate(…)`.
            if (setTarget.Count == 2 && context.Token is Operator { Character: '(' or '=' }
                && ClrTypeColumn(context, leadingTable, setTarget[0]) is { } clrColumnType)
            {
                rawAssignments.Add((setTarget[0], ClrTypeMutation.Parse(new Reference(leadingIdent.WithAddedPart(setTarget[0])), setTarget[0], clrColumnType, columnName, context)));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }

            if (setTarget.Count == 2 && XmlMethodCall.IsKnownMethodName(columnName) && context.Token is Operator { Character: '(' })
            {
                rawAssignments.Add(ParseXmlMutatorSetClause(context, leadingIdent, leadingTable, setTarget[0], columnName));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }

            if (TryConsumeAssignmentOperator(context) is not char assignOp)
                throw SimulatedSqlException.SyntaxErrorNear(context);

            // The only qualifier an assignment target admits is the write
            // target as written — the leading name, alias included. Real
            // reports every other one as Msg 4104 naming the whole dotted
            // form, ahead of the leaf lookup, whether the leaf names a real
            // column or not, and whether the statement joins or not (probed
            // against SQL Server 2025, 2026-08-05: `UPDATE t SET zz.id = 5`,
            // `UPDATE a SET t.id = 5 FROM t a`, `UPDATE a SET b.w = 1 FROM t a
            // JOIN u b …`, `UPDATE v SET t.id = 5` through a view, and
            // `MERGE t AS a … UPDATE SET t.v = 1`).
            var targetBinds = Selection.QualifierIsDmlTarget(context.CurrentDatabase, leadingIdent, setTarget);
            if (!targetBinds)
            {
                if (bindErrors?.Covers(first) != true)
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(setTarget.ToString());
                bindErrors.Record(SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(setTarget.ToString()), first.StartIndex, BindClause.SetTarget);
            }

            var lhsForCompound = new Reference(setTarget);

            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Default })
            {
                if (assignOp != '=')
                    throw SimulatedSqlException.DefaultOnCompoundAssignment(assignOp);
                context.MoveNextOptional();
                rawAssignments.Add((columnName, ColumnDefaultValue.Unbound));
                if (context.Token is Operator { Character: ',' })
                    continue;
                break;
            }
            var rhs = Expression.Parse(context);
            var finalExpr = assignOp == '=' ? rhs : TwoSidedExpression.FromCompoundOp(assignOp, lhsForCompound, rhs, context);
            // A target already reported unbindable binds no further; its value
            // still does.
            rawAssignments.Add((targetBinds ? columnName : null, finalExpr));
            bindErrors?.NoteSetTarget(finalExpr, first);

            if (context.Token is Operator { Character: ',' })
                continue;
            break;
        }

        context.OuterTypeResolver = savedOuterTypeResolver;
        context.AggregateCollector = savedCollector;
        Selection.RefuseClauseAggregates(context.Batch, setAggregates, static () => SimulatedSqlException.AggregateInSetList());

        // A linked server's table: the SET list is what the replay writes,
        // and real's provider refuses an OUTPUT clause outright.
        if (context.Batch.CurrentStatement.RemoteWrite is { } remoteTarget)
        {
            var setNames = new List<string>();
            foreach (var (columnName, _) in rawAssignments)
            {
                if (columnName is not null)
                    setNames.Add(columnName);
            }
            remoteTarget.CheckSetColumns(context.Batch, setNames);
            SettleRemoteMutation(context, remoteTarget, remoteWrite);
        }

        // A view or CTE the FROM clause names as the target writes through it
        // joined to the clause's other sources.
        if (preParsedFrom is not null && (joinedView || (leadingTable is null && JoinedViewTargetIndex(context, preParsedFrom, leadingIdent, leadingView: null) >= 0)))
            return ExecuteJoinedViewTargetUpdate(context, leadingIdent, leadingView, rawAssignments, top, preParsedFrom, partitionedReads);
        if (leadingView is not null && viewRoute == DmlViewRoute.Partitioned)
            return ExecutePartitionedViewUpdate(context, leadingIdent, leadingView, rawAssignments, top, from: null, targetIndex: 0, partitionedReads!);

        // An INSTEAD OF UPDATE trigger on a view takes the write, reading the
        // view's own rows; a multi-source view's SET list names the base
        // table it writes, so its OUTPUT binds only once that is known.
        if (leadingView is not null && viewRoute == DmlViewRoute.InsteadOf)
            return ExecuteInsteadOfViewUpdate(context, leadingIdent, leadingView, rawAssignments, top, targetHints.Serializable);
        if (leadingView is not null && viewRoute == DmlViewRoute.JoinView)
            return ExecuteJoinViewUpdate(context, leadingIdent, leadingView, rawAssignments, top);

        // A table target's OUTPUT clause may read the other sources of a FROM
        // clause that follows it, which is read ahead for it.
        if (preParsedFrom is null && remoteWrite is null && leadingTable is not null && context.Token is UnquotedString { ContextualKeyword: ContextualKeyword.Output })
            preParsedFrom = Selection.PreParseMutationFrom(context, fromCursor: true);
        var output = ParseMutationOutput(context, leadingIdent, leadingTable ?? JoinedTargetTable(context, preParsedFrom, leadingIdent), leadingView, TriggerActions.Update, preParsedFrom);

        if (context.Token is ReservedKeyword { Keyword: Keyword.From })
            return ExecuteJoinedUpdate(context, leadingIdent, leadingTable, rawAssignments, output, top, preParsedFrom);

        var table = RequireMutationTable(context, leadingIdent, leadingTable, "UPDATE");
        return ExecuteUpdateAgainstTable(context, leadingIdent, table, rawAssignments, output, top, targetHints.Serializable, leadingView);
    }

    /// <summary>
    /// Whether an alias-form joined write's target, the source of
    /// <paramref name="from"/> the leading name aliases, reaches a partitioned
    /// view.
    /// </summary>
    private static bool WritesPartitionedSource(ParserContext context, Selection.PreParsedFrom? from, MultiPartName leadingIdent)
    {
        if (from is null || leadingIdent.Count != 1)
            return false;
        foreach (var source in from.Sources)
        {
            if (source.Qualifier is { } qualifier && context.CurrentDatabase.Collation.Equals(qualifier, leadingIdent.Leaf))
                return source.UpdatableView() is { PartitionedBase: not null };
        }
        return false;
    }

    /// <summary>
    /// Parses a SET clause assigning a variable: <c>@v = expr</c>, a compound
    /// <c>@v += expr</c>, or <c>@v = col = expr</c> (<c>@v = col += expr</c>),
    /// which gives the column and the variable the same value. The first two
    /// add a variable-only entry (no column name); the third adds the
    /// column's entry, its expression the <see cref="AssignmentExpression"/>
    /// that <c>ComputeUpdatedRow</c> runs among the variables. Entered on the
    /// variable, left on the token after the clause.
    /// </summary>
    private static void ParseVariableSetClause(
        ParserContext context,
        MultiPartName leadingIdent,
        AtPrefixedString variable,
        List<(string? ColumnName, Expression Expr)> rawAssignments)
    {
        // A table variable qualifying a column (`SET @t.a = …`) is the syntax
        // error at its dot (probed 2026-10-01 against SQL Server 2025).
        var atVariable = context.SaveCheckpoint();
        if (context.GetNextOptional() is Operator { Character: '.' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.RestoreCheckpoint(atVariable);
        var slot = context.Batch.GetVariableSlot(variable.Value);
        context.MoveNextRequired();
        if (TryConsumeAssignmentOperator(context) is not char assignOp)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();

        // `@v = col = expr` — a column name followed by an assignment
        // operator, compound (`@v = col += expr`) or plain. Real takes neither
        // a compound operator on the variable nor a second variable in the
        // column's place (Msg 102 near each).
        if (assignOp == '=' && context.Token is Name first)
        {
            var afterVariable = context.SaveCheckpoint();
            var column = new MultiPartName(first.Value);
            // Optional: the name may be the whole value, ending the batch.
            context.MoveNextOptional();
            while (context.Token is Operator { Character: '.' } && context.GetNextRequired() is StringToken part)
            {
                column = column.WithAddedPart(part.Value);
                context.MoveNextRequired();
            }
            if (TryConsumeAssignmentOperator(context) is char columnOp)
            {
                if (!Selection.QualifierIsDmlTarget(context.CurrentDatabase, leadingIdent, column))
                    throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(column.ToString());
                context.MoveNextRequired();
                var value = Expression.Parse(context);
                rawAssignments.Add((column.Leaf, new AssignmentExpression(variable.Value, slot, columnOp == '='
                    ? value
                    : TwoSidedExpression.FromCompoundOp(columnOp, new Reference(column), value, context))));
                return;
            }
            context.RestoreCheckpoint(afterVariable);
        }

        var rhs = Expression.Parse(context);
        // `@v += col = expr` puts a compound operator on the variable of the
        // three-part shape, which real refuses at that operator (probed
        // 2026-09-24 against SQL Server 2025).
        if (assignOp != '=' && rhs is Reference)
        {
            var afterValue = context.SaveCheckpoint();
            if (TryConsumeAssignmentOperator(context) is not null)
                throw SimulatedSqlException.SyntaxErrorNearText(assignOp + "=");
            context.RestoreCheckpoint(afterValue);
        }
        rawAssignments.Add((null, new AssignmentExpression(variable.Value, slot, assignOp == '='
            ? rhs
            : TwoSidedExpression.FromCompoundOp(assignOp, new VariableReference(variable, context), rhs, context))));
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
    /// <remarks>
    /// The re-read carries the write target's own qualifier — the leading name
    /// exactly as the statement wrote it — because a bare column name resolves
    /// against every FROM source, and an assignment target is scoped to the
    /// write target alone. AdventureWorks' <c>Person.iuPerson</c> is the shape
    /// that separates the two: <c>UPDATE Person.Person SET Demographics.modify(…)
    /// FROM inserted</c>, where <c>inserted</c> carries a <c>Demographics</c> of
    /// its own and the unqualified read would be Msg 209.
    /// </remarks>
    private static (string ColumnName, Expression Expr) ParseXmlMutatorSetClause(
        ParserContext context, MultiPartName targetName, HeapTable? targetTable, string columnName, string methodName)
    {
        var resolver = context.OuterTypeResolver;
        var expression = XmlModify.Parse(
            new Reference(targetName.WithAddedPart(columnName)),
            columnName,
            methodName,
            context,
            resolver is null ? null : name => resolver(XmlDml.ColumnNameOf(name)),
            XmlSchemaCollectionOf(context, targetTable, columnName),
            // The alias form names its target through the FROM clause, which
            // parses after this SET list — so neither the schema collection nor
            // the object name real's diagnostics carry is knowable here, and
            // the body's compile waits for both.
            deferDml: targetTable is null,
            receiverName: targetTable is null ? string.Empty : $"{targetName}.{columnName}");
        return (columnName, expression);
    }

    /// <summary>
    /// Compiles any deferred <c>.modify()</c> body in the SET list now that
    /// <paramref name="table"/> is known. A no-op for the shapes that named
    /// their target up front, whose bodies compiled while the list parsed.
    /// <paramref name="writtenTargetName"/> is the target as the FROM clause
    /// spelled it, which is what real's diagnostics name — the alias the SET
    /// list wrote never appears there.
    /// </summary>
    private static void BindDeferredXmlMutators(
        ParserContext context,
        HeapTable table,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        string writtenTargetName)
    {
        foreach (var (columnName, expr) in rawAssignments)
        {
            if (expr is XmlModify mutator && columnName is not null)
                mutator.BindDeferredDml(context, XmlSchemaCollectionOf(context, table, columnName), $"{writtenTargetName}.{columnName}");
        }
    }

    /// <summary>
    /// The name real's XQuery diagnostics give <paramref name="source"/>: the
    /// object as the FROM clause wrote it, falling back to the alias for a
    /// source carrying no object name of its own and to the table's own name
    /// for neither.
    /// </summary>
    private static string WrittenNameOf(FromSource source, HeapTable table) =>
        source.WrittenObjectName ?? source.Qualifier ?? table.Name;

    /// <summary>
    /// The CLR user-defined type of <paramref name="targetTable"/>'s column
    /// <paramref name="columnName"/>, or <see langword="null"/>.
    /// </summary>
    private static ClrUdtSqlType? ClrTypeColumn(ParserContext context, HeapTable? targetTable, string columnName)
    {
        if (targetTable is null || !context.Simulation.EnableClr)
            return null;
        var collation = context.CurrentDatabase.Collation;
        foreach (var column in targetTable.Columns)
        {
            if (collation.Equals(column.Name, columnName))
                return column.Type as ClrUdtSqlType;
        }

        return null;
    }

    /// <summary>
    /// The spatial type of the SET list's target column <paramref name="columnName"/>: a view
    /// target's own column, or else through the resolver the list's values bind with — the target
    /// table's, or the pre-read FROM clause's for the alias form; null for any other column or none.
    /// </summary>
    private static SpatialSqlType? SpatialSetTargetType(ParserContext context, MultiPartName leadingIdent, View? leadingView, string columnName)
    {
        if (leadingView is not null)
        {
            var collation = context.CurrentDatabase.Collation;
            return Array.Find(ViewColumnsFor(context.Batch, leadingView, leadingIdent), column => collation.Equals(column.Name, columnName))?.Type as SpatialSqlType;
        }
        if (context.OuterTypeResolver is not { } resolve)
            return null;
        try
        {
            return resolve(new MultiPartName(columnName)) as SpatialSqlType;
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a SET clause naming a member of the spatial column <paramref name="receiverName"/>,
    /// entered on the token after the member's name. Only <c>STSrid</c> takes an assignment, plain
    /// or compound; real refuses every other property (Msg 6595), a name the type lacks (Msg 6592),
    /// and any method call, none of which is a mutator (Msg 6201, or Msg 6506 for a name that is no
    /// method), all while the batch compiles — after a syntax error later in the clause (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static SpatialSridMutation ParseSpatialMutation(ParserContext context, Reference receiver, string receiverName, SpatialSqlType type, string memberName)
    {
        if (context.Token is Operator { Character: '(' })
        {
            if (context.GetNextRequired() is not Operator { Character: ')' })
            {
                _ = Expression.Parse(context);
                while (context.Token is Operator { Character: ',' })
                {
                    context.MoveNextRequired();
                    _ = Expression.Parse(context);
                }
                if (context.Token is not Operator { Character: ')' })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
            }
            context.MoveNextOptional();
            if (TryConsumeAssignmentOperator(context) is not null)
                throw SimulatedSqlException.SyntaxErrorNear(context);
            throw SpatialMethodCall.IsMethodOf(memberName, type.IsGeography)
                ? SimulatedSqlException.ClrNotMutator(memberName, type.ClrTypeName, "Microsoft.SqlServer.Types")
                : SimulatedSqlException.ClrMethodNotFound(memberName, type.ClrTypeName, "Microsoft.SqlServer.Types", state: 10);
        }

        if (TryConsumeAssignmentOperator(context) is not char assignOp)
            throw SimulatedSqlException.SyntaxErrorNear(context);
        context.MoveNextRequired();
        var rhs = Expression.Parse(context);
        if (!memberName.Equals("STSrid", StringComparison.Ordinal))
        {
            throw SpatialMethodCall.IsPropertyOf(memberName, type.IsGeography)
                ? SimulatedSqlException.ClrPropertyReadOnly(memberName, type.ClrTypeName)
                : SimulatedSqlException.ClrPropertyNotFound(memberName, type.ClrTypeName);
        }
        var srid = assignOp == '=' ? rhs : TwoSidedExpression.FromCompoundOp(assignOp, SpatialMethodCall.Property(receiver, memberName), rhs, context);
        return new SpatialSridMutation(receiver, receiverName, type, srid);
    }

    /// <summary>
    /// Whether <c>col.modify(…)</c> is the <c>json</c> type's mutator rather
    /// than xml's: the column's own type says so where the target is known.
    /// The alias form names its target only in the FROM clause, which parses
    /// after the SET list, so there the argument count decides — the json
    /// method takes a path and a value where xml's takes one XML-DML string.
    /// The cursor stays on the <c>(</c>.
    /// </summary>
    private static bool IsJsonMutatorTarget(ParserContext context, HeapTable? targetTable, string columnName)
    {
        if (targetTable is not null)
        {
            var collation = context.CurrentDatabase.Collation;
            foreach (var column in targetTable.Columns)
            {
                if (collation.Equals(column.Name, columnName))
                {
                    // A column of neither type has no mutator, which real
                    // reports ahead of reading the arguments.
                    return column.Type switch
                    {
                        JsonSqlType => true,
                        XmlSqlType => false,
                        _ => throw SimulatedSqlException.CannotCallMethodsOn(SimulatedSqlException.FamilyRootName(column.Type)),
                    };
                }
            }
            return false;
        }

        var checkpoint = context.SaveCheckpoint();
        context.MoveNextRequired();
        _ = Expression.Parse(context);
        var twoArguments = context.Token is Operator { Character: ',' };
        context.RestoreCheckpoint(checkpoint);
        return twoArguments;
    }

    /// <summary>
    /// The <c>xml(&lt;collection&gt;)</c> binding <paramref name="columnName"/>
    /// carries on <paramref name="targetTable"/>, or null when the column is
    /// untyped — or when the statement is the alias form, whose target the FROM
    /// clause only names after the SET list has parsed.
    /// </summary>
    private static Schemas.XmlSchemaCollection? XmlSchemaCollectionOf(ParserContext context, HeapTable? targetTable, string columnName)
    {
        if (targetTable is null)
            return null;
        var collation = context.CurrentDatabase.Collation;
        foreach (var column in targetTable.Columns)
        {
            if (collation.Equals(column.Name, columnName))
                return column.XmlSchemaCollection;
        }

        return null;
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
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        View? sourceView = null)
    {
        BindDeferredXmlMutators(context, table, rawAssignments, targetName.ToString());
        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase, sourceView, context.Batch.BindErrors, derivedLabel: targetName.ToString());
        // Through a view, a name is the view's column and masks as the base
        // column it reads (probed 2026-09-27 against SQL Server 2025).
        var setMasks = UpdateSetMasks(context.Batch, assignments, name =>
            sourceView is not null
                ? Array.FindIndex(sourceView.OutputColumns, column => context.Batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf)) is var v and >= 0
                    && sourceView.BaseColumnOrdinals[v] is var baseOrdinal and >= 0
                    ? DataMask.ForTableColumn(table, baseOrdinal)
                    : null
                : Array.FindIndex(table.Columns, column => context.Batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf)) is var k and >= 0
                    ? DataMask.ForTableColumn(table, k)
                    : null);

        // Compile-time bind of the predicate and the SET values, matching
        // real's compiling binder — a cross-collation comparison, a legacy-LOB
        // string-scalar argument and an unknown column all report here rather
        // than waiting for a row to reach the per-row resolver (so an empty
        // table and a module body at CREATE report them too).
        var targetTypeResolver = Selection.TargetColumnTypeResolver(context.CurrentDatabase, targetName, table, sourceView);
        RejectColumnSetBesideSparse(table, assignments);
        BindSetValues(context.Batch, table, assignments, targetTypeResolver, name =>
            sourceView is null
            && Array.FindIndex(table.Columns, column => context.Batch.CurrentDatabase.Collation.Equals(column.Name, name.Leaf)) is var n and >= 0
            && table.Columns[n].SpelledNumeric);

        BooleanExpression? where = null;
        PositionedCursorTarget? positionedCursor = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            if (context.Token is ReservedKeyword { Keyword: Keyword.Current })
            {
                positionedCursor = ParseWhereCurrentOf(context, table, [.. SetColumnNames(rawAssignments)], sourceView);
            }
            else
            {
                using var scope = EnterTargetScope(context, targetName, table, sourceView);
                where = Selection.ParseAndBindPredicate(context, targetTypeResolver);
            }
        }
        Selection.ParseOptionalDmlOptionClause(context);
        if (sourceView is null)
            LoadPredicateStatistics(context.Batch, table, where);

        var plan = new UpdatePlan(targetName, table, rawAssignments, assignments, setMasks, where, positionedCursor, output, top, serializableHint, sourceView);
        NoteDmlPlan(
            context,
            plan,
            admitted: positionedCursor is null
                && !rawAssignments.Exists(assignment => assignment.ColumnName is null || assignment.Expr is AssignmentExpression or XmlModify or JsonModify or ClrTypeMutation)
                && AdmitsDmlPlan(context.Batch, table, sourceView, output));
        return RunUpdate(context, plan);
    }

    /// <summary>
    /// A single-table <c>UPDATE</c>'s parse, which <see cref="RunUpdate"/>
    /// executes — once as the statement parses, and again for each replay of
    /// a cached plan.
    /// </summary>
    private sealed class UpdatePlan(
        MultiPartName targetName,
        HeapTable table,
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        List<(int Ordinal, Expression Expr)> assignments,
        DataMask?[]? setMasks,
        BooleanExpression? where,
        PositionedCursorTarget? positionedCursor,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        bool serializableHint,
        View? sourceView) : DmlStatementPlan
    {
        public readonly MultiPartName TargetName = targetName;
        public readonly HeapTable Table = table;
        public readonly List<(string? ColumnName, Expression Expr)> RawAssignments = rawAssignments;
        public readonly List<(int Ordinal, Expression Expr)> Assignments = assignments;
        /// <summary>
        /// Per SET assignment, the mask a principal without <c>UNMASK</c>
        /// writes its value through — which principal that is, the execution
        /// half settles.
        /// </summary>
        public readonly DataMask?[]? SetMasks = setMasks;
        public readonly BooleanExpression? Where = where;
        public readonly PositionedCursorTarget? PositionedCursor = positionedCursor;
        public readonly OutputProjection? Output = output;
        public readonly Selection.DmlTopLimit? Top = top;
        public readonly bool SerializableHint = serializableHint;
        public readonly View? SourceView = sourceView;

        /// <summary>
        /// The columns the statement reads and assigns, which the permission
        /// check of each execution asks about — collected from the parse once,
        /// since which columns a statement names doesn't depend on who runs it.
        /// </summary>
        public (ColumnReadTarget Read, ColumnReadTarget Assigned)? ColumnTargets;

        public override SimulatedStatementOutcome Run(ParserContext context) => RunUpdate(context, this);
    }

    /// <summary>
    /// The execution half of a single-table <c>UPDATE</c>: the permission
    /// checks, the row walk evaluating WHERE and the SET list against each
    /// row's pre-update image, and the commit. Reads no tokens.
    /// </summary>
    private static SimulatedStatementOutcome RunUpdate(ParserContext context, UpdatePlan plan)
    {
        var (targetName, table, rawAssignments, assignments, where) = (plan.TargetName, plan.Table, plan.RawAssignments, plan.Assignments, plan.Where);
        var (positionedCursor, output, top, serializableHint, sourceView) = (plan.PositionedCursor, plan.Output, plan.Top, plan.SerializableHint, plan.SourceView);
        CheckUpdatePermissions(context, targetName, table, sourceView, rawAssignments, where, plan);
        RowSecurity.NoteWrite(context.Batch, table);
        var setMasks = DataMasking.Applying(context.Batch, plan.SetMasks);
        var enforceConstraints = !ReplacedByInsteadOfTrigger(context.Batch, table, sourceView);
        if (positionedCursor is null)
            Selection.SettleSerializableWriteFence(table, where, serializableHint, context.Batch);

        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var storedColumns = table.StoredColumns;
        var lobStore = table.Heap;

        // The walk reads the rows as it goes and never meets a row another
        // session deleted meanwhile; real's read meets the deleted key and
        // waits on it. Those still in flight are waited out once the walk is
        // done, before the statement holds anything, and a key one of them —
        // or any delete since the seek below chose the rows — put back sends
        // the statement to run again (BatchContext.TargetKeyReinserted).
        // Noted before the seek: a delete and reinsert settling between the
        // seek and the note left the key in neither.
        var keysPutBack = Volatile.Read(ref table.KeysPutBack);

        // Seek the target when WHERE carries an indexable equality / range
        // (positioned UPDATE leaves where null, so it keeps the full scan). The
        // loop re-runs WHERE below, so the seek only narrows the rows considered.
        // The compile walk holds no schema lock, so it doesn't read the
        // target's rows or seek cache at all: another session redefining the
        // table meanwhile left them in a layout the walk decoded wrongly.
        var rowSource = context.Batch.IsSkipping ? [] : MutationRowSource(table, where, context.Batch);
        // Skip mode commits nothing (CommitUpdate returns early), so the walk
        // is pure cost — and running WHERE / SET against live rows can raise a
        // runtime error (a division by zero, a conversion failure) on behalf of
        // a statement that never ran. That matters at CREATE-time module
        // binding, where it would refuse a body real accepts. Everything the
        // bind needs — the target, the SET column ordinals, the predicate — was
        // resolved above.
        // TOP (0) reads no row at all, so nothing per row can raise either.
        var readsNoRow = context.Batch.IsSkipping || DmlTopIsZero(top, context.Batch);
        if (readsNoRow)
            rowSource = [];
        else if (positionedCursor is null && Selection.MutationPlanStarts(table, where))
            RunUpdateStartupConstants(context, table, where is null ? [] : [where], assignments);
        var viewRows = MaterializeRowSelectiveViewRows(context, sourceView, positionedCursor is not null);
        // A write wholly through a seek of the clustered key waits in X, as
        // real's plan writes through that seek without reading ahead. Only
        // another session's write in flight makes the walk wait at all; a
        // shared reader is waited out by the X the row is then written under.
        var contended = positionedCursor is null && sourceView is null && where is not null && !readsNoRow
            && (Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0 || !table.SupersededKeyImages.IsEmptyLockFree());
        var targetWait = contended && Selection.WhereIsClusteredKeySeek(table, where!) ? LockMode.Exclusive : LockMode.Update;
        var throughIndex = contended && targetWait == LockMode.Update ? Selection.MutationSeekIndex(table, where!) : null;
        // A seek chose its rows from the images they carried; one a wait here
        // let settle may carry another.
        if (positionedCursor is null && !readsNoRow && !table.SupersededKeyImages.IsEmptyLockFree()
            && AwaitSupersededTargetRows(context.Batch, table, (address, prior) => JudgeRow(address.Page, address.Slot, prior, predicateOnly: true) is not null, targetWait, throughIndex)
            && where is not null)
        {
            rowSource = MutationRowSource(table, where, context.Batch);
        }
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        // A row-count TOP stops the walk once it has its rows, so a SET value
        // only a later row would raise never runs (probed 2026-10-06 against
        // SQL Server 2025: `UPDATE TOP (1) t SET g.STSrid = 55` past a NULL g).
        var rowCap = top is { Percent: false } countLimit && !readsNoRow ? Selection.ResolveDmlTopCap(countLimit, int.MaxValue, context.Batch) : int.MaxValue;
        foreach (var (pageIndex, slotIndex, scannedBytes) in rowSource)
        {
            if (affected.Count >= rowCap)
                break;
            context.Batch.PollCancellation();
            // Positioned UPDATE (WHERE CURRENT OF): target only the row the
            // cursor is sitting on, identified by its stable heap address.
            if (positionedCursor is { } positioned && !CursorRowMatches(positioned, (pageIndex, slotIndex)))
                continue;

            // Judged as another session's write leaves it: the walk waits out,
            // in U, a row that session holds.
            var rowBytes = scannedBytes;
            if (!context.Batch.AwaitTargetRowWriters(table, pageIndex, slotIndex, ref rowBytes, targetWait, throughIndex)
                || JudgeRow(pageIndex, slotIndex, rowBytes) is not { } judged)
            {
                continue;
            }
            affected.Add((pageIndex, slotIndex, judged.NewValues, judged.OldSnapshot));
            judgedRows.Add((pageIndex, slotIndex, rowBytes));
        }

        if (positionedCursor is null && !readsNoRow)
        {
            if (!table.SupersededKeyImages.IsEmptyLockFree())
                _ = AwaitSupersededTargetRows(context.Batch, table, (address, prior) => JudgeRow(address.Page, address.Slot, prior, predicateOnly: true) is not null, targetWait, throughIndex);
            if (Volatile.Read(ref table.KeysPutBack) != keysPutBack)
                context.Batch.TargetKeyReinserted = true;
        }
        ApplyDmlTopCap(top, affected, context.Batch);
        HoldQualifyingRows(context.Batch, table, affected, judgedRows, walkGeneration, RowLockPurpose.UpdatePreImage, (i, rowBytes) =>
        {
            var (pageIndex, slotIndex, _, _) = affected[i];
            if (JudgeRow(pageIndex, slotIndex, rowBytes) is not { } judged)
                return false;
            affected[i] = (pageIndex, slotIndex, judged.NewValues, judged.OldSnapshot);
            return true;
        });

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

        return CommitUpdate(context, table, affected, output, [.. SetColumnOrdinals(assignments)], sourceView, rowsLocked: true);

        // The row's new values and, when something reads the pre-update image,
        // that image; null for a row the statement doesn't update. With
        // predicateOnly the row is only tested, its decoded image standing in
        // for the new values, and nothing in the SET list runs.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot)? JudgeRow(int pageIndex, int slotIndex, byte[] rowBytes, bool predicateOnly = false)
        {
            var fullValues = DecodeFullRow(table, rowBytes);
            EvaluateComputedColumns(table, fullValues, context.Batch);

            // A row the table's filter predicate hides is no candidate, and is
            // judged before anything the statement itself evaluates.
            if (!RowSecurity.Admits(context.Batch, table, fullValues))
                return null;

            // View visibility filter: rows not visible in the view aren't
            // candidates for UPDATE through it. AND-of-WHEREs up the chain
            // (no-op when sourceView is null or the chain has no WHERE).
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues, context.Batch))
                return null;

            // A windowed or row-limited target writes only to the rows its body yields.
            SqlValue[]? viewRow = null;
            if (viewRows is not null && !viewRows.TryGetValue((pageIndex, slotIndex), out viewRow))
                return null;

            SqlValue ResolveOriginal(MultiPartName name) => ReadTargetRowColumn(context.Batch, table, sourceView, fullValues, viewRow, (pageIndex, slotIndex), name);

            if (where is not null && where.Run(new RuntimeContext(ResolveOriginal, context.Batch)) != true)
                return null;
            if (predicateOnly)
                return (fullValues, null);

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list expressions.
            context.Batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, ResolveOriginal, setMasks, enforceConstraints);

            // WITH CHECK OPTION: the post-update row must satisfy every
            // CHECK OPTION-bearing WHERE in the chain. Fires before
            // CommitUpdate so a violating UPDATE leaves the heap unchanged.
            if (sourceView?.CheckOptionCheck is { } co && !co(newValues, context.Batch))
                throw SimulatedSqlException.ViewCheckOptionViolation();

            var oldSnapshot = UpdateNeedsOldRows(context.Batch, table, (SchemaObject?)sourceView ?? table, output) ? fullValues : null;
            return (newValues, oldSnapshot);
        }
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
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        BooleanExpression? where,
        UpdatePlan? plan = null)
    {
        var updateSecurable = context.Batch.IsSkipping
            ? null
            : PermissionEnforcement.SecurableFor(context.Batch, targetName, (SchemaObject?)sourceView ?? table);
        if (updateSecurable is null)
            return;
        if (!PermissionEnforcement.Applies(context.Batch, context.Batch.DatabaseFor(updateSecurable)))
        {
            // A module body's reference to the view is chained, but the
            // view's own reference to its base table still breaks on an
            // owner change.
            CheckBrokenChainMutation(context.Batch, sourceView, TriggerActions.Update, where, rawAssignments);
            return;
        }

        if (updateSecurable is Synonym synonym)
        {
            // A synonym takes no column grants at all, so a reference
            // through one is checked object-grain against the synonym.
            var synonymDenied = where is not null || AnySetExpressionReadsColumn(rawAssignments, table, context.Batch)
                ? PermissionEnforcement.SchemaObjectDenial(context.Batch, "SELECT", synonym)
                : null;
            if (PermissionEnforcement.Combine(synonymDenied, PermissionEnforcement.SchemaObjectDenial(context.Batch, "UPDATE", synonym)) is { } synonymRefusal)
                throw synonymRefusal;
            CheckBrokenChainMutation(context.Batch, sourceView, TriggerActions.Update, where, rawAssignments);
            return;
        }

        // Column-grain on a base table and a view alike: the WHERE +
        // SET-RHS columns require SELECT (checked first, per probe M1
        // ordering), each SET-target column requires UPDATE — first
        // inaccessible column → Msg 230 (or Msg 229 when the object is
        // wholly inaccessible for that permission). Through a view the
        // ordinals are the view's own, matching what
        // `GRANT UPDATE (col) ON <view>` stored.
        var (read, assigned) = plan is null ? UpdateColumnTargets(table, sourceView, rawAssignments, where) : plan.ColumnTargets ??= UpdateColumnTargets(table, sourceView, rawAssignments, where);
        var readDenied = PermissionEnforcement.ColumnsDenial(context.Batch, Permission.Select, read);
        if (PermissionEnforcement.Combine(readDenied, PermissionEnforcement.ColumnsDenial(context.Batch, Permission.Update, assigned)) is { } refusal)
            throw refusal;
        CheckBrokenChainMutation(context.Batch, sourceView, TriggerActions.Update, where, rawAssignments);
    }

    /// <summary>The columns an <c>UPDATE</c>'s WHERE and SET expressions read, and those its SET list assigns.</summary>
    private static (ColumnReadTarget Read, ColumnReadTarget Assigned) UpdateColumnTargets(
        HeapTable table, View? sourceView, List<(string? ColumnName, Expression Expr)> rawAssignments, BooleanExpression? where)
    {
        var read = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
        where?.VisitOperandExpressions(op => op.VisitColumnReferences(read.Add));
        foreach (var (_, expr) in rawAssignments)
            expr.VisitColumnReferences(read.Add);
        var assigned = sourceView is not null ? new ColumnReadTarget(sourceView) : new ColumnReadTarget(table);
        foreach (var columnName in SetColumnNames(rawAssignments))
            assigned.Add(columnName);
        return (read, assigned);
    }

    /// <summary>
    /// SI writer's pre-flight: walks the version chain dict for entries whose
    /// live heap slot is tombstoned or was changed by another transaction
    /// since the snapshot, decodes each snapshot-visible historical payload,
    /// evaluates the UPDATE / DELETE WHERE predicate against it, and raises
    /// Msg 3960 (with auto-rollback) if any match. Closes the conflict paths
    /// the regular live-heap iteration misses: a deleted row (heap iteration
    /// skips tombstoned slots) and a row whose change took it out of the
    /// WHERE. No-op for non-SI sessions and for sessions whose snapshot
    /// hasn't been allocated yet.
    /// </summary>
    private static void CheckSnapshotConflictOnTombstonedRows(ParserContext context, HeapTable table, BooleanExpression? where, View? sourceView)
    {
        var batch = context.Batch;
        if (Storage.VersionStore.WriterSnapshotXid(batch, table) is not { } sx)
            return;
        foreach (var kv in table.Heap.RowVersions)
        {
            // A live row another transaction changed since the snapshot is
            // judged by the version the snapshot sees too, so an UPDATE whose
            // WHERE matched the row before another transaction moved its key
            // is the conflict it is on real (probed 2026-09-28 against SQL
            // Server 2025).
            var hist = table.Heap.IsSlotTombstoned(kv.Key.PageIndex, kv.Key.SlotIndex)
                ? Storage.VersionStore.ResolveTombstonedSlotForSnapshot(kv.Value, sx, batch.Connection.LockOwner)
                : Storage.VersionStore.ResolveChangedLiveSlotForSnapshot(kv.Value, sx, batch.Connection.LockOwner);
            if (hist is null)
                continue;
            var fullValues = DecodeFullRow(table, hist);
            EvaluateComputedColumns(table, fullValues, batch);
            if (sourceView?.VisibilityCheck is { } vis && !vis(fullValues, batch))
                continue;
            SqlValue Resolve(MultiPartName name) => ReadTargetRowColumn(batch, table, sourceView, fullValues, viewRow: null, (kv.Key.PageIndex, kv.Key.SlotIndex), name);
            if (where is not null && where.Run(new RuntimeContext(Resolve, batch)) != true)
                continue;
            if (table.IsMemoryOptimized)
                throw SimulatedSqlException.MemoryOptimizedWriteConflict(delete: false);
            batch.Connection.CurrentTransaction?.EndRollback();
            throw SimulatedSqlException.SnapshotIsolationUpdateConflict($"{Database.DefaultSchemaName}.{table.Name}", batch.DatabaseFor(table).Name, table.HasClusteredIndex());
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
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        OutputProjection? output,
        Selection.DmlTopLimit? top,
        Selection.PreParsedFrom? preParsedFrom)
    {
        var sourcesList = preParsedFrom?.Sources ?? [];
        var joinsList = preParsedFrom?.Joins ?? [];
        var binding = preParsedFrom?.Binding;
        if (preParsedFrom is not null)
        {
            // The SET list's scope already parsed the sources; resume past them.
            context.RestoreCheckpoint(preParsedFrom.After);
        }
        else
        {
            // Real leaves NEXT VALUE FOR legal in a joined UPDATE / DELETE's own
            // FROM-clause derived table, where every other derived table refuses it
            // (probe-confirmed 2026-08-05, both spellings, against the Msg 11719
            // the SELECT / INSERT … SELECT / MERGE … USING forms take).
            using (ParserScope.Enter(ref context.AllowNextValueForInFromClause, true))
            {
                context.Batch.BindErrors?.EnterClause(context.Token, BindClause.From);
                binding = Selection.ParseSourcesAndJoins(context, QueryScope.Statement, sourcesList, joinsList);
            }
        }
        if (ReadJoinedTailPastMissingTarget(context, sourcesList, joinsList, leadingIdent, leadingTable))
            return new SimulatedNonQuery(0);
        var targetIndex = FindOrAppendMutationTarget(context, sourcesList, joinsList, leadingIdent, leadingTable, binding);
        // A leading name that is a table's but aliases a view, CTE or derived
        // table in the FROM clause writes through that source.
        if (output is null && sourcesList[targetIndex] is { BackingTable: null } aliased && aliased.WriteTargetView() is not null)
            return ExecuteJoinedViewTargetUpdate(context, leadingIdent, leadingView: null, rawAssignments, top, new Selection.PreParsedFrom(sourcesList, joinsList, context.SaveCheckpoint()));
        var sources = sourcesList.ToArray();
        var joins = joinsList.ToArray();

        var table = BindJoinedMutationTable(context, sources, targetIndex, "UPDATE");

        BindDeferredXmlMutators(context, table, rawAssignments, WrittenNameOf(sources[targetIndex], table));
        var assignments = ResolveSetAssignments(rawAssignments, table, context.CurrentDatabase, bindErrors: context.Batch.BindErrors);
        RejectColumnSetBesideSparse(table, assignments);
        var setMasks = UpdateSetMasks(context.Batch, assignments, name => Selection.SourceColumnMask(sources, name));

        // Compile-time bind of the predicate and the SET values — see
        // ExecuteUpdateAgainstTable for why.
        var tupleTypeResolver = Selection.ColumnTypeResolverFor(sources);
        BindSetValues(context.Batch, table, assignments, tupleTypeResolver, name =>
            Selection.TryResolveSourceColumn(sources, name) is { } id && sources[id.Source].Columns[id.Column].SpelledNumeric);

        BooleanExpression? where = null;
        if (context.Token is ReservedKeyword { Keyword: Keyword.Where })
        {
            context.Batch.BindErrors?.EnterClause(context.Token, BindClause.Where);
            context.MoveNextRequired();
            where = Selection.ParseAndBindPredicate(context, tupleTypeResolver, sources, joins);
        }
        Selection.ParseOptionalDmlOptionClause(context);
        BindJoinPredicatesWhileReporting(context.Batch, joins, tupleTypeResolver);
        Selection.ValidateForcedSeeks(context, sources, joins, where);
        LoadJoinedPredicateStatistics(context.Batch, sources, joins, where);

        var plan = new JoinedUpdatePlan(table, assignments, setMasks, sources, joins, targetIndex, where, output, top);
        NoteDmlPlan(
            context,
            plan,
            admitted: !rawAssignments.Exists(assignment => assignment.ColumnName is null || assignment.Expr is AssignmentExpression or XmlModify or JsonModify or ClrTypeMutation)
                && AdmitsDmlPlan(context.Batch, table, view: null, output));
        return RunJoinedUpdate(context, plan);
    }

    /// <summary>
    /// A joined <c>UPDATE</c>'s parse — its <c>FROM</c> sources and joins as
    /// written, the target among them, the SET list and the predicate — which
    /// <see cref="RunJoinedUpdate"/> executes, once as the statement parses
    /// and again for each replay of a cached plan.
    /// </summary>
    private sealed class JoinedUpdatePlan(
        HeapTable table,
        List<(int Ordinal, Expression Expr)> assignments,
        DataMask?[]? setMasks,
        FromSource[] sources,
        JoinSpec[] joins,
        int targetIndex,
        BooleanExpression? where,
        OutputProjection? output,
        Selection.DmlTopLimit? top) : DmlStatementPlan
    {
        public readonly HeapTable Table = table;
        public readonly List<(int Ordinal, Expression Expr)> Assignments = assignments;

        /// <summary>As <see cref="UpdatePlan.SetMasks"/>.</summary>
        public readonly DataMask?[]? SetMasks = setMasks;

        /// <summary>The sources as parsed; each execution narrows and reorders a copy.</summary>
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;
        public readonly int TargetIndex = targetIndex;
        public readonly BooleanExpression? Where = where;
        public readonly OutputProjection? Output = output;
        public readonly Selection.DmlTopLimit? Top = top;

        public override SimulatedStatementOutcome Run(ParserContext context) => RunJoinedUpdate(context, this);
    }

    /// <summary>
    /// The execution half of a joined <c>UPDATE</c>: the join walk applying
    /// WHERE per tuple and deduping its target rows by address, and the
    /// commit. Reads no tokens.
    /// </summary>
    private static SimulatedStatementOutcome RunJoinedUpdate(ParserContext context, JoinedUpdatePlan plan)
    {
        // Skip mode has bound everything it needs; enumerating the join would
        // run its sources, a NEXT VALUE FOR among them.
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);
        var (table, assignments, where, output, top) = (plan.Table, plan.Assignments, plan.Where, plan.Output, plan.Top);
        var sources = (FromSource[])plan.Sources.Clone();
        var joins = plan.Joins;
        var targetIndex = plan.TargetIndex;
        var setMasks = DataMasking.Applying(context.Batch, plan.SetMasks);
        var enforceConstraints = !ReplacedByInsteadOfTrigger(context.Batch, table, sourceView: null);
        if (where?.IsNeverTrue != true && !DmlTopIsZero(top, context.Batch))
            RunUpdateStartupConstants(context, table, JoinedPredicates(joins, where), assignments);

        Selection.SettleSerializableWriteFence(table, where, serializableHint: false, context.Batch, sources[targetIndex].Qualifier);
        // Noted before the target is read, as the plain walk notes it.
        var keysPutBack = Volatile.Read(ref table.KeysPutBack);
        var targetAddresses = new RowAddressMap();
        sources[targetIndex] = sources[targetIndex].AsWriteTarget(targetAddresses);
        // Settled over the FROM clause as written, ahead of the reordering.
        var targetWait = JoinedTargetWait(table, sources, targetIndex, joins, where);
        sources = Selection.PrepareMutationJoinSources(sources, ref joins, where is null ? [] : [where], ref targetIndex, MutationMayReorder(top, context.Batch), context.Batch);

        var seen = new HashSet<(int Page, int Slot)>();
        var affected = new List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)>();
        var walkGeneration = Volatile.Read(ref table.Heap.MutationGeneration);
        var judgedRows = new List<(int PageIndex, int SlotIndex, byte[] Bytes)>();
        var oldSnapshotNeeded = UpdateNeedsOldRows(context.Batch, table, table, output);
        var partners = output is { ReadsPartners: true } ? new OutputPartnerRows(sources) : null;

        // Hoisted per-row scaffolding: one mutable tuple slot, one cached
        // delegate and one runtime, so the per-row loop allocates none.
        byte[]?[] currentTuple = [];
        SqlValue resolveAcrossTuple(MultiPartName name) => ResolveAcrossMutationTuple(sources, currentTuple, name, context.Batch);
        Func<MultiPartName, SqlValue> resolveTuple = resolveAcrossTuple;
        var runtime = new RuntimeContext(resolveTuple, context.Batch);

        if (!table.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(context.Batch, table, (_, prior) => QualifyingTuple(prior) is not null, targetWait);
        foreach (var tuple in Selection.EnumerateJoinedRows(sources, joins, context.Batch, outerResolver: null))
        {
            currentTuple = tuple;

            if (where is not null && where.Run(runtime) != true)
                continue;

            var targetBytes = tuple[targetIndex];
            if (targetBytes is null)
                continue;
            if (!targetAddresses.TryGet(targetBytes, out var addr))
                continue;
            if (!seen.Add(addr))
                continue;

            // Judged as another session's write leaves it: the walk waits out,
            // in U or X, a row that session holds, and judges it again.
            var rowBytes = targetBytes;
            if (!context.Batch.AwaitTargetRowWriters(table, addr.Page, addr.Slot, ref rowBytes, targetWait))
                continue;
            var judged = ReferenceEquals(rowBytes, targetBytes) || rowBytes.AsSpan().SequenceEqual(targetBytes)
                ? JudgeTuple(targetBytes)
                : Rejudge(rowBytes);
            if (judged is not { } entry)
                continue;
            affected.Add((addr.Page, addr.Slot, entry.NewValues, entry.OldSnapshot));
            judgedRows.Add((addr.Page, addr.Slot, rowBytes));
            partners?.Note(addr, currentTuple);
        }

        // A target row whose key another session deleted and put back
        // elsewhere during the walk was never paired: the statement runs
        // again, as the plain walk's does.
        if (!table.SupersededKeyImages.IsEmptyLockFree())
            _ = AwaitSupersededTargetRows(context.Batch, table, (_, prior) => QualifyingTuple(prior) is not null, targetWait);
        if (Volatile.Read(ref table.KeysPutBack) != keysPutBack)
            context.Batch.TargetKeyReinserted = true;

        ApplyDmlTopCap(top, affected, context.Batch);
        HoldQualifyingRows(context.Batch, table, affected, judgedRows, walkGeneration, RowLockPurpose.UpdatePreImage, (i, rowBytes) =>
        {
            if (Rejudge(rowBytes) is not { } judged)
                return false;
            affected[i] = (affected[i].PageIndex, affected[i].SlotIndex, judged.NewValues, judged.OldSnapshot);
            partners?.Note((affected[i].PageIndex, affected[i].SlotIndex), currentTuple);
            return true;
        });

        return CommitUpdate(context, table, affected, output, [.. SetColumnOrdinals(assignments)], sourceView: null, rowsLocked: true, partners);

        // The target row of the current tuple's new values, and its old image
        // when something reads that.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot) JudgeTuple(byte[] targetBytes)
        {
            var fullValues = DecodeFullRow(table, targetBytes);
            EvaluateComputedColumns(table, fullValues, context.Batch);

            // Per-row stamp bump for NEXT VALUE FOR in the SET-list.
            context.Batch.BumpRowStamp();
            var newValues = ComputeUpdatedRow(context, table, fullValues, assignments, resolveTuple, setMasks, enforceConstraints);
            return (newValues, oldSnapshotNeeded ? fullValues : null);
        }

        // The target row as rowBytes, judged again against its partners.
        (SqlValue[] NewValues, SqlValue[]? OldSnapshot)? Rejudge(byte[] rowBytes) =>
            QualifyingTuple(rowBytes) is { } targetBytes ? JudgeTuple(targetBytes) : null;

        // The target row as rowBytes when the join and WHERE still pass it
        // with some partner, the first such tuple current; else null.
        byte[]? QualifyingTuple(byte[] rowBytes)
        {
            foreach (var tuple in Selection.EnumerateJoinedRows(WithTargetNarrowedTo(sources, targetIndex, rowBytes), joins, context.Batch, outerResolver: null))
            {
                currentTuple = tuple;
                if (tuple[targetIndex] is { } targetBytes && (where is null || where.Run(runtime) == true))
                    return targetBytes;
            }
            return null;
        }
    }

    /// <summary>
    /// The rows a single-target UPDATE or DELETE walks: a seek when
    /// <paramref name="where"/> carries an indexable equality or range, the
    /// table's scan otherwise.
    /// </summary>
    private static IEnumerable<(int Page, int Slot, byte[] Bytes)> MutationRowSource(HeapTable table, BooleanExpression? where, BatchContext batch) =>
        (where is null ? null : Selection.SeekMutationTarget(table, where, batch))
            ?? ClusteredScan.RowsWithAddress(table, batch.Connection.StatementIo);

    /// <summary>
    /// <paramref name="sources"/> with the joined write's target source at
    /// <paramref name="targetIndex"/> standing for the one row
    /// <paramref name="rowBytes"/>, to judge that row again against its
    /// partners.
    /// </summary>
    private static FromSource[] WithTargetNarrowedTo(FromSource[] sources, int targetIndex, byte[] rowBytes)
    {
        var narrowed = (FromSource[])sources.Clone();
        narrowed[targetIndex] = SingleRowSource(sources[targetIndex], rowBytes, sources[targetIndex].LobStore);
        return narrowed;
    }

    /// <summary>
    /// <paramref name="original"/> yielding only <paramref name="row"/>, its
    /// off-row values read from <paramref name="lobStore"/>.
    /// </summary>
    private static FromSource SingleRowSource(FromSource original, byte[] row, Heap? lobStore) =>
        new(
            qualifier: original.Qualifier,
            columnNames: original.ColumnNames,
            columns: original.Columns,
            storedSchema: original.StoredSchema,
            storageOrdinals: original.StorageOrdinals,
            lobStore: lobStore,
            rows: [row],
            backingTable: original.BackingTable,
            unaliasedName: original.UnaliasedName);

    /// <summary>
    /// How many times a statement writing a table runs its target read again
    /// when rows it waited on came back deleted with their keys reinserted
    /// (<see cref="BatchContext.TargetKeyReinserted"/>); the last read's rows
    /// stand whatever it met. Each run again answers a delete and reinsert
    /// another session committed meanwhile, so the cap only bounds a session
    /// starved by others rewriting one key without pause.
    /// </summary>
    internal const int MaxTargetWalks = 64;

    /// <summary>
    /// Readies a writer's target read for the rows it can't reach. The walk
    /// reaches a target row through the image it carries — a seek by its key,
    /// a join pairing it, a scan, which never meets a deleted slot — and waits
    /// in U only on the rows it reaches, as real's plan does (probed
    /// 2026-10-01 against SQL Server 2025: a joined UPDATE waits
    /// <c>LCK_M_U</c> on a joining row another session holds and passes a
    /// non-joining one). That misses a row another session has deleted, or
    /// rewritten so the walk no longer reaches or qualifies it, whose image
    /// before that write would have qualified, where real's read meets the
    /// deleted key or the old index key under that session's X, waits in U
    /// and, after a rollback, writes the restored row. So each such row still
    /// in flight (<see cref="HeapTable.SupersededKeyImages"/>) whose prior
    /// image <paramref name="priorQualifies"/> is waited out in U here, before
    /// the walk, which then judges the row as that session left it — the same
    /// outcome as real's wait at the row's turn, moved earlier within the
    /// statement. True when some such row was in flight, waited out or
    /// settling as it was met, the walk's seek then to be read again. Callers test
    /// <see cref="HeapTable.SupersededKeyImages"/> for emptiness first, a
    /// lock-free read, so a statement with no such write in flight on
    /// <paramref name="table"/> doesn't build <paramref name="priorQualifies"/>.
    /// <paramref name="mode"/> is the walk's wait, U or X, and
    /// <paramref name="throughIndex"/> the nonclustered index its seek reads
    /// through (<see cref="BatchContext.AwaitTargetRowWriters"/>).
    /// </summary>
    private static bool AwaitSupersededTargetRows(BatchContext batch, HeapTable table, Func<(int Page, int Slot), byte[], bool> priorQualifies, LockMode mode = LockMode.Update, KeyLockGroup? throughIndex = null)
    {
        if (batch.IsSkipping || batch.SupersededTargetRows(table) is not { } superseded)
            return false;
        var matched = false;
        foreach (var (address, priorImage, resource) in superseded)
        {
            bool qualifies;
            try
            {
                qualifies = priorQualifies(address, priorImage);
            }
            catch (SimulatedSqlException)
            {
                // An image the statement never judges can't raise for it;
                // whether the row qualifies is the settled row's to say.
                qualifies = true;
            }
            if (qualifies)
            {
                _ = batch.AwaitSupersededTargetRow(table, resource, mode, throughIndex, priorImage);
                matched = true;
            }
        }
        return matched;
    }

    /// <summary>
    /// Takes the X each row a writer's target walk judged qualifying is
    /// written under (<see cref="BatchContext.HoldQualifyingTargetRow"/>), once
    /// the walk and its TOP are done, in walk order, so the walk's own locks
    /// never send a later row off <see cref="BatchContext.AwaitTargetRow"/>'s
    /// lock-free check, a row TOP drops is never locked, and sessions running
    /// one statement over the same rows never wait on each other in a cycle
    /// (<see cref="BatchContext.AwaitTargetRowWriters"/>). A row another
    /// session changed since its judgement is judged again by
    /// <paramref name="rejudge"/>, which replaces its entry and says whether
    /// it still qualifies; one that doesn't, or is gone, is dropped.
    /// <paramref name="judged"/> runs beside <paramref name="rows"/>: each
    /// row's address and the image it was judged on — past the end of
    /// <paramref name="rows"/> for a row TOP dropped.
    /// </summary>
    private static void HoldQualifyingRows<TRow>(
        BatchContext batch,
        HeapTable table,
        List<TRow> rows,
        List<(int PageIndex, int SlotIndex, byte[] Bytes)> judged,
        long walkGeneration,
        RowLockPurpose purpose,
        Func<int, byte[], bool> rejudge)
    {
        var kept = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            var (pageIndex, slotIndex, rowBytes) = judged[i];
            var hold = TargetRowHold.None;
            var qualifies = true;
            while (qualifies && !batch.HoldQualifyingTargetRow(table, pageIndex, slotIndex, ref hold, ref rowBytes, walkGeneration, purpose))
                qualifies = hold != TargetRowHold.Gone && rejudge(i, rowBytes);
            if (qualifies)
                rows[kept++] = rows[i];
            else
                batch.ReleaseTargetRow(table, pageIndex, slotIndex, hold);
        }
        rows.RemoveRange(kept, rows.Count - kept);
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
        // The limit is read when the statement runs; a skipped statement's
        // variables were declared but never assigned.
        if (batch.IsSkipping)
            return;
        var cap = top is { } limit ? Selection.ResolveDmlTopCap(limit, rows.Count, batch) : rows.Count;
        // SET ROWCOUNT caps a DML statement's affected rows the same way TOP
        // does, and the two compose as a minimum (probe-confirmed: TOP 5 under
        // ROWCOUNT 3 changes 3 rows, TOP 3 under ROWCOUNT 5 changes 3). This is
        // the seam every INSERT / UPDATE / DELETE already collects its rows
        // through, so the cap lands once for all of them.
        if (batch.Connection.RowCountLimit is > 0 and var rowCountLimit && rowCountLimit < cap)
            cap = (int)rowCountLimit;
        if (cap < rows.Count)
            rows.RemoveRange(cap, rows.Count - cap);
    }

    /// <summary>
    /// Whether an UPDATE walk keeps each row's decoded old image for
    /// <see cref="CommitUpdate"/>, which reads it for OUTPUT's and the
    /// triggers' DELETED, the history row, and the incoming foreign keys'
    /// checks and cascades. Those skip a row whose image is null, so every
    /// UPDATE walk asks here, since one answering without the foreign-key
    /// term would change a referenced key with no Msg 547 and no cascade.
    /// </summary>
    private static bool UpdateNeedsOldRows(BatchContext batch, HeapTable table, SchemaObject insteadOfParent, OutputProjection? output) =>
        output is not null
        || HasAfterTrigger(batch, table, TriggerActions.Update)
        || HasInsteadOfTrigger(batch, insteadOfParent, TriggerActions.Update)
        || table.SystemVersioning is not null
        || table.IncomingForeignKeys.Count > 0;

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
        View? sourceView = null,
        bool rowsLocked = false,
        OutputPartnerRows? partners = null)
    {
        if (context.Batch.IsSkipping)
            return new SimulatedNonQuery(0);
        RejectWriteToUnwritableFilegroup(table, context.Batch, "UPDATE", updatedColumnOrdinals);

        var insteadOfParent = (SchemaObject?)sourceView ?? table;
        var insteadOfActive = HasInsteadOfTrigger(context.Batch, insteadOfParent, TriggerActions.Update);

        if (affected.Count == 0)
        {
            // AFTER triggers still fire when the statement matched nothing —
            // real runs the body with empty INSERTED / DELETED and @@ROWCOUNT
            // 0, and UPDATE(col) still reports the SET-clause columns
            // (probe-confirmed for UPDATE / DELETE / INSERT…SELECT / MERGE).
            // An INSTEAD OF UPDATE trigger runs over the empty set the same
            // way (probed 2026-09-27 against SQL Server 2025).
            if (insteadOfActive)
                FireInsteadOfUpdateTrigger(context, table, sourceView, affected, updatedColumnOrdinals);
            else
                FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
            return output is null ? new SimulatedNonQuery(0) : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, Array.Empty<byte[]>(), 0) { ColumnNullability = output.Nullability };
        }

        // SNAPSHOT isolation write-conflict: each affected row must have
        // a live version no newer than my snapshot, otherwise Msg 3960
        // fires and the SI tx auto-rolls-back. Probe-confirmed against
        // SQL Server 2025.
        foreach (var (pageIndex, slotIndex, _, _) in affected)
            Storage.VersionStore.CheckSnapshotUpdateConflict(context.Batch, table, (pageIndex, slotIndex));

        if (insteadOfActive)
        {
            // OUTPUT INTO's rows land before the body runs (probed 2026-09-27
            // against SQL Server 2025); to the client it is Msg 334.
            var outputRows = output is null ? null : ProjectMutationOutput(affected, output, context.Batch, partners);
            FireInsteadOfUpdateTrigger(context, table, sourceView, affected, updatedColumnOrdinals);
            return output is null || output.HasTarget
                ? new SimulatedNonQuery(affected.Count)
                : new SimulatedSqlResultSet(output.Schema, output.ColumnNames, outputRows!, affected.Count) { ColumnNullability = output.Nullability };
        }

        var keyGuard = BeginUniqueKeyGuard(context.Batch, table);
        EnforceKeysForUpdate(table, affected, context.Batch);

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
        var oldBytesPerAffected = Storage.VersionStore.WillCaptureVersions(context.Batch.DatabaseFor(table), table) && lockableTable
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
        // Change tracking reads each row's old key before the write replaces it.
        var tracking = table.ChangeTracking;
        var keyOrdinals = tracking is null ? [] : TableChangeTracking.KeyOrdinals(table);
        var trackedColumns = tracking?.UpdatedColumns(table, keyOrdinals, updatedColumnOrdinals);
        var setsKey = tracking is not null && TableChangeTracking.SetsKey(keyOrdinals, updatedColumnOrdinals);
        List<(SqlValue[] OldKey, SqlValue[] NewKey)>? keyMoves = null;
        var lobColumns = LegacyLobColumnsAmong(table, updatedColumnOrdinals);
        for (var i = 0; i < affected.Count; i++)
        {
            var (pageIndex, slotIndex, fullNew, fullOld) = affected[i];
            table.OwningDatabase?.RejectWriteWhenReadOnly();
            if (lobColumns is not null)
                NoteRootedLobNulls(table, lobColumns, pageIndex, slotIndex, fullOld, fullNew);
            tracking?.RecordUpdate(context.Batch, table, keyOrdinals, fullOld ?? DecodeFullRow(table, table.Heap.ReadSlotBytes(pageIndex, slotIndex)!), fullNew, trackedColumns, setsKey, ref keyMoves);
            if (lockableTable)
            {
                // A row the target walk held is under its X already.
                if (!rowsLocked)
                    context.Batch.AcquireRowLockTxScoped(table, pageIndex, slotIndex, LockMode.Exclusive, RowLockPurpose.UpdatePreImage);
                context.Batch.NoteSupersededRow(table, pageIndex, slotIndex);
            }
            var storedNew = ProjectStoredValues(table, fullNew);
            var newImage = RowEncoder.EncodeRow(table.StoredColumns, storedNew, table.Heap);
            // The row-X above tested the clustered key the row is leaving; a
            // nonclustered index the update touches, and a key change carrying
            // the row INTO a gap some SERIALIZABLE reader fences, are only
            // judged once the post-update image is known.
            if (lockableTable)
                context.Batch.ProbeKeyLocksForUpdate(table, pageIndex, slotIndex, newImage);
            // Captured ahead of the rewrite, so a snapshot never meets the new
            // image before the chain marks it in flight.
            if (lockableTable && oldBytesPerAffected is not null)
                Storage.VersionStore.CaptureWrite(context.Batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytesPerAffected[i], Storage.VersionWriteKind.Update);
            UpdateCheckedRow(context.Batch, table, affected, i, newImage, storedNew, undoLog, ReclaimSuperseded(table, context), keyGuard);
            ClusteredScan.NoteKeyAssignment(table, updatedColumnOrdinals, (pageIndex, slotIndex), undoLog);
            // Row by row, as real's pipeline judges them: a function the
            // CHECK calls sees this row's new image and the rows before it.
            EnforceLandedRowChecks(table, fullNew, context.Batch, "UPDATE");
        }
        tracking?.RecordKeyMoves(context.Batch, table, keyMoves);
        table.NoteColumnsUpdated(updatedColumnOrdinals, affected.Count);

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
            var rows = ProjectMutationOutput(affected, output, context.Batch, partners);
            // OUTPUT INTO @t suppresses the result set (probe-confirmed).
            if (!output.HasTarget)
            {
                FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
                return new SimulatedSqlResultSet(output.Schema, output.ColumnNames, rows, affected.Count) { ColumnNullability = output.Nullability };
            }
        }
        FireAfterUpdateTriggers(context, table, affected, updatedColumnOrdinals);
        return new SimulatedNonQuery(affected.Count);
    }

    /// <summary>
    /// Writes a history row for each row affected by a system-versioned
    /// UPDATE. Each history row preserves the pre-update full column set,
    /// with ROW END overwritten to the write's system time. The
    /// resulting period is <c>[original ROW START, system time)</c> — the
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
        foreach (var (_, _, newFull, oldFull) in affected)
        {
            if (oldFull is not null)
                WriteHistoryRow(parent, historyTable, period, oldFull, context, undoLog, newFull);
        }
    }

    /// <summary>
    /// Writes <paramref name="oldFull"/>, a row of system-versioned
    /// <paramref name="parent"/> a statement is about to rewrite or delete, to
    /// its history sibling, its ROW END the write's system time
    /// (<see cref="BatchContext.SystemTimeUtc"/>) — the end of the half-open
    /// interval during which it was current. A row another transaction
    /// rewrote after this one began would end before it started, which real
    /// refuses with Msg 13535, ending the statement (probed 2026-10-03
    /// against SQL Server 2025, for an UPDATE, a DELETE and a MERGE).
    /// </summary>
    internal static void WriteHistoryRow(
        HeapTable parent, HeapTable historyTable, (int StartOrdinal, int EndOrdinal) period, SqlValue[] oldFull, ParserContext context, UndoLog? undoLog, SqlValue[]? replacement = null)
    {
        var historyRow = (SqlValue[])oldFull.Clone();
        if (parent.HasLedgerColumns())
            StampLedgerEnd(parent, historyRow, replacement, context.Batch);
        var end = SqlValue.FromDateTime2(parent.Columns[period.EndOrdinal].Type, context.Batch.SystemTimeUtc);
        if (oldFull[period.StartOrdinal] is { IsNull: false } start && start.AsDateTime2 > end.AsDateTime2)
            throw SimulatedSqlException.SystemTimeBeforePeriodStart(QualifyTableName(parent, context.Batch.DatabaseFor(parent)));
        historyRow[period.EndOrdinal] = end;
        _ = InsertRow(context.Batch, historyTable, RowEncoder.EncodeRow(historyTable.StoredColumns, ProjectStoredValues(historyTable, historyRow), historyTable.Heap), undoLog);
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
    /// Inserts a row a statement writes. On a table other sessions can read,
    /// the row's X lock and — with <paramref name="captureVersion"/> — its
    /// version-store entry are published under the heap's latch together
    /// with the row (<see cref="Heap.Insert{TState}"/>), so a READ COMMITTED
    /// reader meets the row already locked and a snapshot meets it already in
    /// flight, rather than reading an uncommitted row as committed. The
    /// key-range test, which can wait, runs first, outside the latch.
    /// With a <paramref name="guard"/>, the uniqueness check that cleared
    /// <paramref name="storedValues"/> takes its last look under the latch,
    /// and a refusal writes nothing and returns <c>(-1, -1)</c>.
    /// </summary>
    internal static (int PageIndex, int SlotIndex) InsertRow(
        BatchContext batch, HeapTable table, ReadOnlySpan<byte> image, UndoLog? undoLog, bool captureVersion = true, UniqueKeyWriteGuard? guard = null, SqlValue[]? storedValues = null)
    {
        RejectLobOnEmptyFilegroup(batch, table, image);
        if (!IsLockableTable(table))
            return table.Heap.Insert(image, undoLog);
        batch.ProbeKeyLocksForInsert(table, image);
        var address = table.Heap.Insert(
            image,
            undoLog,
            (batch, table, captureVersion),
            static (state, address) =>
            {
                state.batch.AcquireInsertedRowLock(state.table, address.PageIndex, address.SlotIndex);
                if (state.captureVersion)
                    VersionStore.CaptureWrite(state.batch, state.table, address, oldRid: null, oldPayload: null, VersionWriteKind.Insert);
            },
            guard,
            storedValues);
        if (address.PageIndex >= 0)
            batch.NoteKeyPutBack(table, image);
        return address;
    }

    /// <summary>
    /// The guard for a statement's writes of <paramref name="table"/> whose
    /// keys a check about to start clears (see <see cref="UniqueKeyWriteGuard"/>),
    /// or null when no other session can write the table or it enforces no
    /// unique key.
    /// </summary>
    private static UniqueKeyWriteGuard? BeginUniqueKeyGuard(BatchContext batch, HeapTable table) =>
        batch.IsSkipping || !IsLockableTable(table) ? null : UniqueKeyWriteGuard.Begin(table);

    /// <summary>
    /// Rewrites row <paramref name="row"/> of <paramref name="affected"/>, an
    /// UPDATE or MERGE target row whose keys the statement's check cleared,
    /// checking its keys again whenever <paramref name="guard"/> finds another
    /// session wrote one of them since.
    /// </summary>
    private static void UpdateCheckedRow(
        BatchContext batch,
        HeapTable table,
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        int row,
        byte[] newImage,
        SqlValue[] storedValues,
        UndoLog? undoLog,
        bool reclaimSuperseded,
        UniqueKeyWriteGuard? guard)
    {
        var (pageIndex, slotIndex, _, _) = affected[row];
        RejectLobOnEmptyFilegroup(batch, table, newImage);
        if (guard is null)
        {
            table.Heap.UpdateAt(pageIndex, slotIndex, newImage, undoLog, reclaimSuperseded);
            return;
        }
        // The statement's guard keeps judging the rows after this one; the
        // second check vouches for this row alone, so its retry takes a guard
        // of its own.
        while (!table.Heap.TryUpdateAt(pageIndex, slotIndex, newImage, undoLog, reclaimSuperseded, guard, storedValues))
            guard = RecheckAffectedRow(batch, table, affected, row);
    }

    /// <summary>
    /// Checks row <paramref name="row"/> of <paramref name="affected"/> again
    /// after a guard refused its write, returning the guard its retry writes
    /// under: the uniqueness check, which meets the other session's row and
    /// waits on it, then raises or lets the write go ahead.
    /// </summary>
    private static UniqueKeyWriteGuard RecheckAffectedRow(
        BatchContext batch, HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected, int row)
    {
        var guard = UniqueKeyWriteGuard.Begin(table)!;
        EnforceKeysForUpdate(table, affected, batch, row);
        return guard;
    }

    /// <summary>
    /// Records the pre-write version of the row a MERGE is about to delete or
    /// rewrite, ahead of the heap write as UPDATE and DELETE record theirs, so
    /// a snapshot reads the row as it stood until the MERGE commits.
    /// </summary>
    private static void CaptureMergeVersion(BatchContext batch, HeapTable table, int pageIndex, int slotIndex, VersionWriteKind kind)
    {
        if (VersionStore.WillCaptureVersions(batch.DatabaseFor(table), table) && table.Heap.ReadSlotBytes(pageIndex, slotIndex) is { } oldBytes)
            VersionStore.CaptureWrite(batch, table, (pageIndex, slotIndex), (pageIndex, slotIndex), oldBytes, kind);
    }

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
        OutputProjection output,
        BatchContext batch,
        OutputPartnerRows? partners = null)
    {
        var rows = new List<byte[]>(affected.Count);
        foreach (var (page, slot, fullNew, fullOld) in affected)
        {
            var projectedBytes = output.ProjectRow(batch, insertedValues: fullNew, deletedValues: fullOld, partners: partners?.For(page, slot));
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
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        IReadOnlyList<int> updatedColumnOrdinals)
    {
        // UPDATE(col) / COLUMNS_UPDATED() read the SET list here as under an
        // AFTER trigger — through the view, at the view's own positions.
        var updatedOrdinals = new List<int>(updatedColumnOrdinals.Count);
        foreach (var ordinal in updatedColumnOrdinals)
        {
            if (sourceView is null)
            {
                updatedOrdinals.Add(ordinal);
                continue;
            }
            for (var i = 0; i < sourceView.BaseColumnOrdinals.Length; i++)
            {
                if (sourceView.BaseColumnOrdinals[i] == ordinal)
                    updatedOrdinals.Add(i);
            }
        }
        var insertedRows = new List<SqlValue[]>(affected.Count);
        var deletedRows = new List<SqlValue[]>(affected.Count);
        foreach (var (_, _, fullNew, fullOld) in affected)
        {
            if (sourceView is null)
            {
                insertedRows.Add(fullNew);
                deletedRows.Add(fullOld ?? new SqlValue[table.Columns.Length]);
                continue;
            }
            // Through a view the new row is the old one with the SET list's
            // columns replaced: a computed or derived column keeps the value
            // the old row shows (probed 2026-10-07 against SQL Server 2025).
            var deleted = fullOld is null ? new SqlValue[sourceView.OutputColumns.Length] : ProjectThroughView(context.Batch, table, sourceView, fullOld);
            var inserted = (SqlValue[])deleted.Clone();
            foreach (var ordinal in updatedOrdinals)
                inserted[ordinal] = fullNew[sourceView.BaseColumnOrdinals[ordinal]];
            insertedRows.Add(inserted);
            deletedRows.Add(deleted);
        }
        context.Connection.LastStatementRowCount = affected.Count;
        var pseudoColumns = sourceView?.OutputColumns ?? table.Columns;
        var parent = (SchemaObject?)sourceView ?? table;
        _ = context.Batch.Connection.Simulation.TryFireInsteadOfTrigger(
            context.Batch, parent, TriggerActions.Update,
            pseudoColumns, insertedRows, deletedRows,
            affectedRowCount: affected.Count, updatedOrdinals);
    }

    /// <summary>
    /// Projects a base-table row through a view's
    /// <see cref="View.BaseColumnOrdinals"/> map to the view's
    /// <see cref="View.OutputColumns"/> shape, a derived column computed from
    /// the row (probed 2026-10-07 against SQL Server 2025: a positioned
    /// <c>DELETE</c>'s <c>deleted</c> reads it) — save a windowed view's,
    /// which one row can't settle, and every one when no
    /// <paramref name="batch"/> is given (a MERGE's rows), which read NULL.
    /// Used by INSTEAD OF UPDATE / DELETE on updatable views.
    /// </summary>
    private static SqlValue[] ProjectThroughView(BatchContext? batch, HeapTable? table, View view, SqlValue[] baseRow)
    {
        var projected = new SqlValue[view.OutputColumns.Length];
        Func<SqlValue[], int, SqlValue>? readDerived = null;
        for (var i = 0; i < view.OutputColumns.Length; i++)
        {
            var baseOrd = view.BaseColumnOrdinals[i];
            projected[i] = baseOrd >= 0 ? baseRow[baseOrd]
                : batch is null || table is null || view.IsWindowed ? SqlValue.Null(view.OutputColumns[i].Type)
                : (readDerived ??= SingleBaseViewReader(batch, view, table))(baseRow, i);
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
        // A SET list assigning only variables changes no column, and the
        // trigger reads empty INSERTED / DELETED (probed 2026-10-04 against
        // SQL Server 2025).
        if (updatedColumnOrdinals.Count > 0)
        {
            foreach (var (_, _, fullNew, fullOld) in affected)
            {
                insertedRows.Add(fullNew);
                deletedRows.Add(fullOld ?? new SqlValue[table.Columns.Length]);
            }
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
    /// <c>schema.table</c> — the two-part form the messages that name a table
    /// without its database use (Msg 2729 / 2799, probe-confirmed).
    /// </summary>
    internal static string SchemaQualifyTableName(HeapTable table, Database database)
    {
        foreach (var entry in database.Schemas)
        {
            if (entry.Value.SchemaId == table.SchemaId)
                return $"{entry.Key}.{table.Name}";
        }
        return table.Name;
    }

    /// <summary>
    /// Parse-time column-type resolver for the UPDATE target, so a subquery in
    /// a SET expression can bind the target's columns
    /// (<c>SET alias = (SELECT MAX(v) FROM (VALUES (t.name),(t.goes_by)) x(v))</c>,
    /// the shape ORMs emit for GREATEST / LEAST). A qualified reference must
    /// name the target table; anything else falls through to the enclosing
    /// scope, which is null at statement level and raises Msg 207 there.
    /// </summary>
    private static SqlType ResolveUpdateTargetColumnType(Database database, MultiPartName targetName, HeapTable table, MultiPartName name, Func<MultiPartName, SqlType>? enclosing)
    {
        var qualifierIsTarget = Selection.QualifierIsDmlTarget(database, targetName, name);
        if (qualifierIsTarget)
        {
            foreach (var column in table.Columns)
            {
                if (BuiltInToken.Equals(column.Name, name.Leaf))
                    return column.Type;
            }
        }

        return enclosing is not null
            ? enclosing(name)
            : qualifierIsTarget
                ? throw SimulatedSqlException.InvalidColumnName(name)
                : throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
    }

    /// <summary>
    /// <see cref="ResolveUpdateTargetColumnType"/> as the scope an <c>UPDATE</c>'s
    /// SET list parses under, built apart from the parse so it holds the
    /// database rather than the parsing context: a subquery in the list keeps
    /// it, and a cached plan must reach no parse.
    /// </summary>
    private static Func<MultiPartName, SqlType> UpdateTargetTypeResolver(Database database, MultiPartName targetName, HeapTable table, Func<MultiPartName, SqlType>? enclosing) =>
        name => ResolveUpdateTargetColumnType(database, targetName, table, name, enclosing);

    /// <summary>
    /// Types a joined UPDATE's or DELETE's <c>ON</c> predicates while the
    /// statement is read for its whole bind error report. They otherwise bind
    /// as the join runs, which a statement read without running never
    /// reaches, and real reports their names ahead of the <c>WHERE</c>'s.
    /// </summary>
    private static void BindJoinPredicatesWhileReporting(BatchContext batch, JoinSpec[] joins, Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (batch.BindErrors is null)
            return;
        foreach (var join in joins)
            join.OnPredicate?.Bind(batch, resolveColumnType);
    }

    /// <summary>
    /// Resolves the raw <c>SET</c> column-name pairs to ordinals against the
    /// target table, rejecting writes to identity / computed / rowversion /
    /// GENERATED ALWAYS columns up-front so the per-row loop never has to
    /// re-check.
    /// </summary>
    private static List<(int Ordinal, Expression Expr)> ResolveSetAssignments(
        List<(string? ColumnName, Expression Expr)> rawAssignments,
        HeapTable table,
        Database database,
        View? sourceView = null,
        BindErrorReport? bindErrors = null,
        string? derivedLabel = null)
    {
        var assignments = new List<(int Ordinal, Expression Expr)>(rawAssignments.Count);
        var assigned = new HashSet<int>();
        foreach (var (colName, expr) in rawAssignments)
        {
            if (colName is null)
            {
                assignments.Add((-1, expr));
                continue;
            }
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
                {
                    if (bindErrors?.RecordSetTarget(SimulatedSqlException.InvalidColumnName(colName), expr) == true)
                    {
                        assignments.Add((-1, expr));
                        continue;
                    }
                    throw SimulatedSqlException.InvalidColumnName(colName);
                }
                columnOrdinal = sourceView.BaseColumnOrdinals[viewOrd];
                if (columnOrdinal < 0)
                    throw SimulatedSqlException.ViewDmlTouchesDerivedField(derivedLabel ?? sourceView.Name, sourceView.IsDerivedTable);
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
                if (columnOrdinal < 0 && colName.StartsWith('$'))
                    columnOrdinal = Array.FindIndex(table.Columns, c => GraphColumns.IsPseudoColumnFor(c.Name, colName));
                if (columnOrdinal < 0)
                {
                    if (bindErrors?.RecordSetTarget(SimulatedSqlException.InvalidColumnName(colName), expr) == true)
                    {
                        assignments.Add((-1, expr));
                        continue;
                    }
                    throw SimulatedSqlException.InvalidColumnName(colName);
                }
            }

            if (!assigned.Add(columnOrdinal))
            {
                throw SimulatedSqlException.ColumnAssignedMoreThanOnce(sourceView is null
                    ? table.Columns[columnOrdinal].Name
                    : colName);
            }
            RejectUnmodifiableSetTarget(table, columnOrdinal, database);
            if (ReferenceEquals(expr, ColumnDefaultValue.Unbound))
            {
                assignments.Add((columnOrdinal, ColumnDefaultValue.Bind(table.Columns[columnOrdinal])));
                continue;
            }
            if (expr is AssignmentExpression { DeclaredType: var variableType })
            {
                var column = table.Columns[columnOrdinal];
                if (variableType.SqlServerName != column.Type.SqlServerName)
                    throw SimulatedSqlException.ReceivingVariableTypeMismatch(variableType.SqlServerName, column.Type.SqlServerName, colName);
                RejectNarrowerReceivingVariable(variableType, column, colName);
            }
            assignments.Add((columnOrdinal, expr));
        }
        return assignments;
    }

    /// <summary>
    /// <c>SET @v = col = expr</c>'s variable must hold every value the column
    /// can: a shorter string or binary is Msg 426, naming both lengths in
    /// bytes (a MAX column's as 8100), and a <c>decimal</c> with fewer integral
    /// or fractional digits Msg 4187 (probed 2026-10-01 against SQL Server
    /// 2025).
    /// </summary>
    private static void RejectNarrowerReceivingVariable(SqlType variableType, HeapColumn column, string columnName)
    {
        if (variableType is DecimalSqlType variableDecimal && column.Type is DecimalSqlType columnDecimal)
        {
            if (variableDecimal.scale < columnDecimal.scale || variableDecimal.precision - variableDecimal.scale < columnDecimal.precision - columnDecimal.scale)
                throw SimulatedSqlException.ReceivingVariableLosesData(variableType.SqlServerName, column.Type.SqlServerName, columnName);
            return;
        }
        var (variableLength, national) = variableType switch
        {
            VarcharSqlType v => (v.length, false),
            CharSqlType c => (c.length, false),
            VarbinarySqlType b => (b.length, false),
            BinarySqlType b => (b.length, false),
            NVarcharSqlType n => (n.length, true),
            NCharSqlType n => (n.length, true),
            _ => ((short)0, false),
        };
        if (variableLength <= 0 || column.MaxLength is not int columnLength)
            return;
        var columnBytes = columnLength == SqlType.MaxLengthSentinel ? 8100 : national ? columnLength * 2 : columnLength;
        var variableBytes = national ? variableLength * 2 : variableLength;
        if (variableBytes < columnBytes)
            throw SimulatedSqlException.ReceivingVariableTooShort(variableBytes, columnBytes, columnName);
    }

    /// <summary>
    /// Binds each SET value against <paramref name="resolveColumnType"/>: an
    /// unresolved collation settles (Msg 456), and a column refuses a value it
    /// can't take without an explicit conversion (Msg 206 / 257, see
    /// <see cref="AssignmentRules"/>). A reference to a numeric-spelled column
    /// is marked so a <c>sql_variant</c> target keeps the name.
    /// </summary>
    private static void BindSetValues(BatchContext batch, HeapTable table, List<(int Ordinal, Expression Expr)> assignments, Func<MultiPartName, SqlType> resolveColumnType, Func<MultiPartName, bool> isNumericColumn)
    {
        foreach (var (ordinal, expr) in assignments)
        {
            Reference.MarkNumericSpelled(expr, isNumericColumn);
            // A statement read for its whole bind error report records a type
            // check here and binds the next value on; an error-typed value
            // takes any target.
            var recorded = batch.BindErrors?.Count ?? 0;
            SqlType type;
            try
            {
                type = expr.GetSqlType(batch, resolveColumnType);
                UnresolvedCollation.RequireAssignable(type);
            }
            catch (SimulatedSqlException error) when (batch.BindErrors is { } report && report.CarriesPastTypeCheck(error, recorded, expr))
            {
                continue;
            }
            if (ordinal < 0 || expr is AssignmentExpression || batch.BindErrors?.Count > recorded)
                continue;
            try
            {
                AssignmentRules.RequireAssignable(expr, type, table.Columns[ordinal].Type);
            }
            catch (SimulatedSqlException error) when (batch.BindErrors?.TryRecordAssignmentCheck(error, expr) == true)
            {
            }
        }
    }

    /// <summary>The columns a SET list writes, leaving out its variable-only assignments.</summary>
    private static IEnumerable<string> SetColumnNames(List<(string? ColumnName, Expression Expr)> rawAssignments)
    {
        foreach (var (columnName, _) in rawAssignments)
        {
            if (columnName is not null)
                yield return columnName;
        }
    }

    /// <summary>The ordinals a resolved SET list writes, leaving out its variable-only assignments.</summary>
    private static IEnumerable<int> SetColumnOrdinals(List<(int Ordinal, Expression Expr)> assignments)
    {
        foreach (var (ordinal, _) in assignments)
        {
            if (ordinal >= 0)
                yield return ordinal;
        }
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
        // A memory-optimized table's primary key columns can't be updated
        // (probed 2026-10-02 against SQL Server 2025).
        if (table.IsMemoryOptimized && table.KeyConstraints.Exists(key => key.Kind == KeyConstraintKind.PrimaryKey && Array.IndexOf(key.FullOrdinals, columnOrdinal) >= 0))
            throw SimulatedSqlException.MemoryOptimizedPrimaryKeyUpdate();
        if (GraphColumns.IsInternal(column.GraphKind))
            throw SimulatedSqlException.InternalGraphColumnAccess(column.Name, state: 3);
        if (column.Identity is not null)
            throw SimulatedSqlException.CannotUpdateIdentityColumn(column.Name);
        if (column.Computed is not null && !column.IsColumnSet)
            throw SimulatedSqlException.ColumnCannotBeModified(column.Name);
        if (column.Type == SqlType.RowVersion)
            throw SimulatedSqlException.CannotUpdateTimestampColumn();
        if (column.GeneratedAs != GeneratedAlwaysAsRow.None)
            throw SimulatedSqlException.CannotUpdateGeneratedAlways(QualifyTableName(table, database));
    }

    /// <summary>
    /// Msg 360, as the statement compiles: a SET list naming the table's
    /// sparse column set beside a sparse column it covers (probed 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    private static void RejectColumnSetBesideSparse(HeapTable table, List<(int Ordinal, Expression Expr)> assignments)
    {
        if (assignments.Exists(assignment => assignment.Ordinal >= 0 && table.Columns[assignment.Ordinal].IsColumnSet)
            && assignments.Exists(assignment => assignment.Ordinal >= 0 && table.Columns[assignment.Ordinal].IsSparse))
        {
            throw SimulatedSqlException.ColumnSetAndSparseColumnWritten();
        }
    }

    /// <summary>A SET of the table's sparse column set writes its sparse columns.</summary>
    private static void WriteAssignedColumnSet(HeapTable table, List<(int Ordinal, Expression Expr)> assignments, SqlValue[] newValues)
    {
        foreach (var (ordinal, _) in assignments)
        {
            if (ordinal >= 0 && table.Columns[ordinal].IsColumnSet)
                Parser.Expressions.ColumnSetValue.Write(table, table.Columns[ordinal], newValues, newValues[ordinal]);
        }
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
    /// their inner reads route through the standard read-source sink. Made
    /// while the statement parses (<see cref="PermissionEnforcement.CheckWhileParsing(BatchContext, CompiledPermissionCheck)"/>).
    /// </summary>
    private static void CheckSourceSelect(BatchContext batch, FromSource source)
    {
        if (source.ViaSynonym is { } synonym)
            PermissionEnforcement.CheckWhileParsing(batch, "SELECT", writtenName: null, synonym);
        else if (source.BackingTable is { } backing)
            PermissionEnforcement.CheckWhileParsing(batch, "SELECT", writtenName: null, backing);
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
        List<(string? ColumnName, Expression Expr)> rawAssignments, HeapTable table, BatchContext batch)
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
            // A column's default reads no column.
            if (ReferenceEquals(expr, ColumnDefaultValue.Unbound))
                continue;
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
    internal static SqlValue[] DecodeFullRow(HeapTable table, byte[] rowBytes)
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
    /// Whether a DML statement's <c>TOP</c> caps it at no row, which leaves
    /// real's plan nothing to start: <c>UPDATE TOP (0) t SET v = 'toolong'</c>
    /// raises nothing there.
    /// </summary>
    private static bool DmlTopIsZero(Selection.DmlTopLimit? top, BatchContext batch) =>
        top is { } limit && Selection.ResolveDmlTopCap(limit, int.MaxValue, batch) == 0;

    /// <summary>
    /// The mode a joined UPDATE's or DELETE's walk waits on a target row
    /// another session holds in: X when real's plan writes the target through
    /// a seek of its clustered key, everything beside it one row of constants
    /// (<see cref="Selection.JoinedWriteIsClusteredKeySeek"/>), U otherwise —
    /// and U without asking while no other session writes the table, which
    /// makes the walk wait for nothing either way.
    /// </summary>
    private static LockMode JoinedTargetWait(HeapTable table, FromSource[] sources, int targetIndex, JoinSpec[] joins, BooleanExpression? where) =>
        (Volatile.Read(ref table.ActiveDataWriters) != 0 || Volatile.Read(ref table.ActiveUpdateLocks) != 0 || !table.SupersededKeyImages.IsEmptyLockFree())
        && Selection.JoinedWriteIsClusteredKeySeek(table, sources, targetIndex, joins, where)
            ? LockMode.Exclusive
            : LockMode.Update;

    /// <summary>A joined UPDATE / DELETE's predicates: every JOIN ON, then the WHERE.</summary>
    private static List<BooleanExpression> JoinedPredicates(JoinSpec[] joins, BooleanExpression? where)
    {
        var predicates = new List<BooleanExpression>();
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                predicates.Add(on);
        }
        if (where is not null)
            predicates.Add(where);
        return predicates;
    }

    /// <summary>
    /// Evaluates what real's UPDATE / DELETE plan evaluates once as it starts,
    /// before reading a row: the predicates' and SET values' runtime constants
    /// (see <see cref="ConstantFolding.CollectStartupConstants"/>), and a
    /// statement-wide value — a literal, a variable or parameter, a
    /// computation over those — converted to the column it is assigned to, so
    /// <c>UPDATE t SET v = 'toolong' WHERE id = 999</c> and the same through
    /// an over-long <c>@p</c> raise Msg 2628 over no matching row as they do
    /// there (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static void RunUpdateStartupConstants(
        ParserContext context, HeapTable table, List<BooleanExpression> predicates, List<(int Ordinal, Expression Expr)> assignments)
    {
        var constants = new List<Expression>();
        foreach (var predicate in predicates)
            ConstantFolding.CollectStartupConstants(predicate, context, constants);
        var startupValues = new List<(HeapColumn Column, Expression Value)>();
        // A four-part target's SET list is evaluated by the server, row by
        // row, so nothing of it runs ahead of the rows here (probed 2026-10-06
        // against SQL Server 2025: `SET b = 1 / 0` over no row raises nothing).
        if (context.Batch.CurrentStatement.RemoteWrite is { Kind: RemoteWriteKind.Update, WrittenName: not null } remote && ReferenceEquals(remote.Proxy, table))
            assignments = [];
        foreach (var (ordinal, expr) in assignments)
        {
            if (ordinal >= 0 && expr is not AssignmentExpression && ConstantFolding.IsStartupValue(expr))
                startupValues.Add((table.Columns[ordinal], expr));
            else
                ConstantFolding.CollectStartupConstants(expr, context, constants);
        }
        Selection.RunStartupConstants(constants, context.Batch);
        ConvertStartupValues(context, table, startupValues);
    }

    /// <summary>
    /// Converts each statement-wide value to the column it is written to, for
    /// the error real raises doing so as its plan starts (a truncation, a
    /// failed conversion, an overflow) — see <see cref="ConstantFolding.IsStartupValue"/>.
    /// </summary>
    private static void ConvertStartupValues(ParserContext context, HeapTable table, List<(HeapColumn Column, Expression Value)> values)
    {
        if (values.Count == 0)
            return;
        var runtime = new RuntimeContext(static _ => throw new InvalidOperationException("A startup value reads no column."), context.Batch);
        foreach (var (column, value) in values)
        {
            try
            {
                _ = CoerceForWrite(EnforceMaxLength(value.Run(runtime), column, table, context.Connection), column, context.Batch);
            }
            catch (SimulatedSqlException failure) when (ConstantFolding.FoldsClrParseFailure(failure, value, context.Batch))
            {
                throw SimulatedSqlException.ClrTypeParseFoldedAtCompile(failure);
            }
        }
    }

    /// <summary>
    /// Per SET assignment, the mask its value reads through, or null when no
    /// assignment reads a masked column: a principal without <c>UNMASK</c>
    /// writes what it would read (probed 2026-09-27 against SQL Server 2025 —
    /// <c>SET plain = LEFT(masked, 10)</c> stores <c>xxxx</c>, and
    /// <c>SET s = s + '!'</c> overwrites the column with its own mask). These
    /// are the masks' definitions, the same for every principal; which of them
    /// apply is settled once per execution (<see cref="DataMasking.Applying"/>).
    /// </summary>
    private static DataMask?[]? UpdateSetMasks(BatchContext batch, List<(int Ordinal, Expression Expr)> assignments, Func<MultiPartName, DataMask?> columnMask)
    {
        if (!batch.Connection.Simulation.DeclaresDataMasks)
            return null;
        DataMask?[]? masks = null;
        for (var i = 0; i < assignments.Count; i++)
        {
            if (assignments[i].Expr is not AssignmentExpression && DataMask.Of(assignments[i].Expr, columnMask, typeOf: null) is { } mask)
                (masks ??= new DataMask?[assignments.Count])[i] = mask;
        }
        return masks;
    }

    /// <summary>
    /// Whether an <c>INSTEAD OF UPDATE</c> trigger on the target replaces the
    /// statement, which then never writes: its rows meet no NOT NULL, CHECK or
    /// block predicate, only the trigger body's own write does (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    private static bool ReplacedByInsteadOfTrigger(BatchContext batch, HeapTable table, View? sourceView) =>
        HasInsteadOfTrigger(batch, (SchemaObject?)sourceView ?? table, TriggerActions.Update);

    /// <summary>
    /// Computes the post-SET row from the pre-update <paramref name="fullValues"/>
    /// snapshot: every SET RHS evaluates against the same snapshot (matching
    /// SQL Server: <c>UPDATE t SET a = 100, b = a + 1</c> over a row with
    /// <c>(a=10, b=20)</c> yields <c>(a=100, b=11)</c>); rowversion auto-bumps;
    /// computed columns recompute against the new values; NOT NULL and CHECK
    /// fire here (per-row constraint validation); PK / UNIQUE wait for phase 2.
    /// A non-null <paramref name="setMasks"/> entry masks that assignment's value.
    /// </summary>
    private static SqlValue[] ComputeUpdatedRow(
        ParserContext context,
        HeapTable table,
        SqlValue[] fullValues,
        List<(int Ordinal, Expression Expr)> assignments,
        Func<MultiPartName, SqlValue> resolver,
        MaskingFunction?[]? setMasks = null,
        bool enforceConstraints = true)
    {
        if (enforceConstraints)
            RowSecurity.EnforceBlock(context.Batch, table, BlockOperation.BeforeUpdate, fullValues);
        var newValues = new SqlValue[table.Columns.Length];
        Array.Copy(fullValues, newValues, fullValues.Length);

        // Real assigns every variable first, in written order and against the
        // pre-update row, and only then the columns, which read the variables
        // as just assigned (probed 2026-09-24 against SQL Server 2025).
        // `@v = col = expr` is an AssignmentExpression on its column's entry:
        // the variable pass runs it, and the column takes the variable's value.
        var runtime = new RuntimeContext(resolver, context.Batch);
        foreach (var (_, expr) in assignments)
        {
            if (expr is AssignmentExpression variableAssignment)
                _ = variableAssignment.Run(runtime);
        }

        for (var i = 0; i < assignments.Count; i++)
        {
            var (ordinal, expr) = assignments[i];
            if (ordinal < 0)
                continue;
            SqlValue raw;
            try
            {
                raw = expr is AssignmentExpression { VariableName: var assigned } ? context.Batch.GetVariableSlot(assigned).Value : expr.Run(runtime);
            }
            catch (SimulatedSqlException error) when (context.Batch.CurrentStatement.RemoteWrite is { Kind: RemoteWriteKind.Update, WrittenName: not null } remote && ReferenceEquals(remote.Proxy, table))
            {
                // A four-part target's SET list is the server's to evaluate.
                throw error.RelayedFromRemoteUpdate(context.Batch);
            }
            // The mask takes the target column's type: `SET plain = LEFT(masked, 2)`
            // stores xxxx into a varchar(20) (probed 2026-09-27 against SQL Server 2025).
            if (setMasks?[i] is { } mask)
                raw = DataMasking.ForStorage(mask.Apply(raw, table.Columns[ordinal].Type));
            raw = EnforceMaxLength(raw, table.Columns[ordinal], table, context.Connection);
            SqlValue converted;
            try
            {
                converted = CoerceForWrite(raw, table.Columns[ordinal], context.Batch);
            }
            catch (SimulatedSqlException error) when (context.Batch.CurrentStatement.RemoteWrite is { Kind: RemoteWriteKind.Update, WrittenName: not null } remote && ReferenceEquals(remote.Proxy, table))
            {
                throw error.RelayedFromRemoteUpdate(context.Batch);
            }
            newValues[ordinal] = SqlValue.NameVariantBase(raw, converted, expr.ResultReportsNumeric);
            EnforceRule(table, newValues, ordinal, context.Batch);
        }
        WriteAssignedColumnSet(table, assignments, newValues);

        // The pre-update ROW START surfaces in `fullValues` for the
        // history-row copy that CommitUpdate writes.
        StampUpdatedRow(table, newValues, context.Batch);
        if (enforceConstraints)
        {
            EnforceNotNull(table, newValues, "UPDATE");
            // The CHECKs calling a user function wait for CommitUpdate's write.
            EnforceCheckConstraints(table, newValues, context.Batch, "UPDATE", deferFunctionChecks: true);
            RowSecurity.EnforceBlock(context.Batch, table, BlockOperation.AfterUpdate, newValues);
        }

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
    private static int FindMutationTargetIndex(Collation collation, List<FromSource> sources, string leadingIdent, HeapTable? leadingTable)
    {
        for (var s = 0; s < sources.Count; s++)
        {
            if (sources[s].Qualifier is { } q && collation.Equals(q, leadingIdent))
                return s;
        }
        if (leadingTable is not null)
        {
            // A table the FROM clause reads twice, under aliases neither of
            // which the target names, is Msg 8154 (probed 2026-10-01 against
            // SQL Server 2025). A source read FOR SYSTEM_TIME is no instance
            // of the table, which the write then reads as one more source
            // (probed 2026-10-06).
            var found = -1;
            for (var s = 0; s < sources.Count; s++)
            {
                if (ReferenceEquals(sources[s].BackingTable, leadingTable) && sources[s].Rows is not TemporalRowSource)
                {
                    if (found >= 0)
                        throw SimulatedSqlException.AmbiguousTable(leadingIdent);
                    found = s;
                }
            }
            return found;
        }
        return -1;
    }

    /// <summary>
    /// Locates the mutation target, adding it to the FROM clause when the
    /// clause introduced no source for it. Real binds such a target as an
    /// additional, implicitly cross-joined source rather than refusing the
    /// statement — <c>UPDATE u SET id = d.n FROM (SELECT 1 AS n) d</c> is
    /// <c>u CROSS JOIN d</c>, with <c>u</c>'s own columns in scope for the SET
    /// list, the WHERE, the OUTPUT clause and any correlated subquery.
    /// Probe-confirmed against SQL Server 2025 (2026-08-05) for a parenthesized
    /// derived table alone and joined to a table, two derived tables,
    /// <c>d LEFT JOIN t</c>, <c>d CROSS APPLY (…)</c>, a plain unparenthesized
    /// table (<c>UPDATE u SET id = u2.id FROM u2</c>), a schema-qualified
    /// target, a <c>#temp</c> target, and the <c>DELETE t FROM …</c> /
    /// <c>DELETE FROM t FROM …</c> spellings: each target row is written once
    /// however many join rows it meets, so the cross join multiplies rows
    /// examined, not rows affected.
    /// <para>
    /// The implicit source joins at the <em>tail</em>, leaving the written
    /// FROM's own leftmost source and join tree untouched. A leading identifier
    /// that named no table at all stays Msg 208 — an alias the FROM never
    /// defined is real's error too.
    /// </para>
    /// </summary>
    /// <returns>The target's index in <paramref name="sources"/>.</returns>
    private static int FindOrAppendMutationTarget(
        ParserContext context,
        List<FromSource> sources,
        List<JoinSpec> joins,
        MultiPartName leadingIdent,
        HeapTable? leadingTable,
        Selection.PartialScopeBinding? binding)
    {
        var found = FindMutationTargetIndex(context.Batch.CurrentDatabase.Collation, sources, leadingIdent.Leaf, leadingTable);
        if (found >= 0)
            return found;
        if (leadingTable is null)
            throw SimulatedSqlException.InvalidObjectName(leadingIdent);

        var columnNames = new string[leadingTable.Columns.Length];
        for (var c = 0; c < columnNames.Length; c++)
            columnNames[c] = leadingTable.Columns[c].Name;
        // The same read plan an explicitly-written source of this table takes;
        // the write lock the caller acquires afterwards is unchanged.
        var plan = context.Batch.AcquireDataLockIfApplicable(leadingTable, default, isWrite: false);
        joins.Add(new JoinSpec(JoinKind.Cross, onPredicate: null));
        sources.Add(new FromSource(
            qualifier: leadingIdent.Leaf,
            columnNames: columnNames,
            columns: leadingTable.Columns,
            storedSchema: leadingTable.StoredColumns,
            storageOrdinals: leadingTable.StorageOrdinals,
            lobStore: leadingTable.Heap,
            rows: plan.NoLockReader ? new UnlockedScanRows(leadingTable) : new LockCheckedScanRows(leadingTable, plan),
            backingTable: leadingTable,
            heapPlan: plan,
            autoElementName: leadingIdent.ToString(),
            unaliasedName: FromSource.Resolved(leadingIdent, context.Batch.CurrentDatabase)));
        // The appended target is outside every ON's scope, so a name an ON
        // bound that the target also carries is pinned to its binder too.
        binding?.Pin(sources);
        return sources.Count - 1;
    }

    /// <summary>
    /// Whether a joined write may drive its join from the source its WHERE
    /// narrowed hardest rather than the one its FROM names first: not when a
    /// <c>TOP</c> or <c>SET ROWCOUNT</c> keeps the rows the walk reaches
    /// first, which the written order settles.
    /// </summary>
    private static bool MutationMayReorder(Selection.DmlTopLimit? top, BatchContext batch) =>
        top is null && batch.Connection.RowCountLimit is not > 0;

    /// <summary>
    /// Multi-source column resolver for joined UPDATE / DELETE: resolves a
    /// reference against any source's columns by qualifier-aware lookup,
    /// decodes the column from the source's tuple slot. NULL-filled tuple
    /// slots (LEFT JOIN no-match) surface as typed NULL, and a non-persisted
    /// computed column evaluates its expression the way the SELECT path's
    /// resolver does — which is what lets AdventureWorks'
    /// <c>Sales.iduSalesOrderDetail</c> read <c>inserted.LineTotal</c>, a
    /// column with no storage slot of its own.
    /// </summary>
    private static SqlValue ResolveAcrossMutationTuple(
        FromSource[] sources, byte[]?[] tuple, MultiPartName name, BatchContext batch)
    {
        var (s, c) = Selection.FindSourceColumn(sources, name);
        if (s == -1)
        {
            throw RowLocator.IsLocatorName(name)
                ? new NotSupportedException("TEXTPTR in a joined UPDATE, DELETE or a write through a join view isn't modeled: its rows don't carry the address a text pointer names.")
                : SimulatedSqlException.InvalidColumnName(name);
        }

        var bytes = tuple[s];
        return bytes is null
            ? SqlValue.Null(sources[s].Columns[c].Type)
            : Selection.DecodeOrCompute(sources[s], c, bytes, batch);
    }

    /// <summary>
    /// PK / UNIQUE validation for the rows an UPDATE (or a MERGE, or a
    /// cascade) rewrites, across the table's key constraints and unique
    /// indexes alike: the first row breaking any key raises, for the key with
    /// the lowest <c>index_id</c> it breaks — real's order, which
    /// <see cref="EnforceRowKeys"/> describes. <c>IGNORE_DUP_KEY</c> has no
    /// say here; real keeps raising for an update. <paramref name="onlyRow"/>,
    /// when set, checks that one row against the heap again — the second
    /// check a <see cref="UniqueKeyWriteGuard"/> refusal asks for, the
    /// comparison among the affected rows already made.
    /// </summary>
    private static void EnforceKeysForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected, BatchContext batch, int onlyRow = -1)
    {
        UpdateKeyViolations? violations = null;
        EnforceKeyConstraintsForUpdate(table, affected, batch, onlyRow, ref violations);
        EnforceUniqueIndexesForUpdate(table, affected, batch, onlyRow, ref violations);
        if (violations is not null)
            throw violations.First;
    }

    /// <summary>
    /// The duplicates <see cref="EnforceKeysForUpdate"/> has found so far: per
    /// affected row the lowest-ranked key it breaks, and the first row
    /// breaking any. Allocated with the first duplicate, so an update that
    /// breaks no key never builds one.
    /// </summary>
    private sealed class UpdateKeyViolations(int rowCount)
    {
        private readonly RowKeyChecks[] rows = new RowKeyChecks[rowCount];
        private int firstRow = int.MaxValue;

        /// <summary>The error of the first row breaking a key.</summary>
        public SimulatedSqlException First => this.rows[this.firstRow].Hard!;

        /// <summary>
        /// Whether row <paramref name="row"/>'s check of the key numbered
        /// <paramref name="indexId"/> can no longer change what raises.
        /// </summary>
        public bool Settles(int row, int indexId) => row > this.firstRow || this.rows[row].Settles(indexId, ignoreDupKey: false);

        public static void Note(ref UpdateKeyViolations? violations, HeapTable table, int rowCount, int row, KeyConstraint key, SimulatedSqlException error)
        {
            violations ??= new(rowCount);
            violations.rows[row].Note(table, key, error);
            violations.firstRow = Math.Min(violations.firstRow, row);
        }

        /// <inheritdoc cref="Note(ref UpdateKeyViolations?, HeapTable, int, int, KeyConstraint, SimulatedSqlException)"/>
        public static void Note(ref UpdateKeyViolations? violations, HeapTable table, int rowCount, int row, Storage.Index key, SimulatedSqlException error)
        {
            violations ??= new(rowCount);
            violations.rows[row].Note(table, key, error);
            violations.firstRow = Math.Min(violations.firstRow, row);
        }
    }

    /// <summary>
    /// The key-constraint half of <see cref="EnforceKeysForUpdate"/>: each affected row's new stored-key
    /// tuple is checked against (a) every other affected row's new key and
    /// (b) every non-affected heap row's existing key. Self-collision (a row
    /// matching its own pre-update self) is impossible because affected
    /// addresses are excluded from the heap-side scan. Mass-shift updates
    /// (<c>UPDATE t SET k = k + 1</c>) work correctly because (a) only
    /// compares new-vs-new among affected rows — overlap with the pre-shift
    /// snapshot via (b) only fires when a non-affected row's existing key
    /// genuinely collides with the new value (a true violation).
    /// <paramref name="onlyRow"/> is <see cref="EnforceKeysForUpdate"/>'s, and
    /// a duplicate is noted in <paramref name="violations"/> rather than raised.
    /// </summary>
    private static void EnforceKeyConstraintsForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected, BatchContext batch, int onlyRow, ref UpdateKeyViolations? violations)
    {
        if (table.KeysMayExceedLimit)
        {
            for (var i = Math.Max(onlyRow, 0); i < (onlyRow < 0 ? affected.Count : onlyRow + 1); i++)
                EnforceIndexKeyLength(table, affected[i].FullNew);
        }
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
        var affectedKeys = new AffectedKeyIndex?[table.KeyConstraints.Count];
        var existingComputedKeys = new HashSet<SqlValueKey>?[table.KeyConstraints.Count];

        for (var i = Math.Max(onlyRow, 0); i < (onlyRow < 0 ? affected.Count : onlyRow + 1); i++)
        {
            var myStored = storedSnapshots[i];

            for (var c = 0; c < table.KeyConstraints.Count; c++)
            {
                var constraint = table.KeyConstraints[c];
                if (constraint.IsDisabled || violations?.Settles(i, constraint.IndexId) == true)
                    continue;

                // A UNIQUE constraint over a non-persisted computed column
                // compares evaluated full rows — see the unique-index path.
                if (!constraint.KeysAreStored)
                {
                    if (!ComputedKeyMoved(constraint.FullOrdinals, affected[i], table, batch))
                        continue;
                    if ((onlyRow < 0 && ComputedKeySharedByAnotherAffectedRow(affected, i, constraint.FullOrdinals, table, filter: null, batch))
                        || (existingComputedKeys[c] ??= BuildComputedKeySet(table, constraint.FullOrdinals, filter: null, batch, affectedAddrs))
                            .Contains(new SqlValueKey(ReadKeyByFullOrdinals(constraint.FullOrdinals, affected[i].FullNew))))
                    {
                        UpdateKeyViolations.Note(ref violations, table, affected.Count, i, constraint, KeyConstraintViolationOnComputedKey(table, constraint, affected[i].FullNew));
                    }
                    continue;
                }

                if (!KeyTupleMoved(constraint.StorageOrdinals, myStored, affected[i], table))
                    continue;

                if (onlyRow < 0 && (affectedKeys[c] ??= AffectedKeyIndex.Build(storedSnapshots, constraint.StorageOrdinals, participates: null)).SharedByAnotherRow(i))
                {
                    UpdateKeyViolations.Note(ref violations, table, affected.Count, i, constraint, KeyConstraintViolation(table, constraint, myStored));
                    continue;
                }

                if (TryPrepareKeySeek(table, constraint.StorageOrdinals, myStored, out var commons, out var probe))
                {
                    AwaitUncommittedKeyWriters(batch, table, constraint.StorageOrdinals, commons, probe);
                    foreach (var (p, s, _) in HeapSeekCache.For(table.Heap)
                        .MatchingRows(table.Heap, storedColumns, constraint.StorageOrdinals, commons, probe))
                    {
                        if (!affectedAddrs.Contains((p, s)))
                        {
                            UpdateKeyViolations.Note(ref violations, table, affected.Count, i, constraint, KeyConstraintViolation(table, constraint, myStored));
                            break;
                        }
                    }
                    continue;
                }

                if (ScanFindsKey(table, constraint.StorageOrdinals, myStored, filter: null, affectedAddrs, batch))
                    UpdateKeyViolations.Note(ref violations, table, affected.Count, i, constraint, KeyConstraintViolation(table, constraint, myStored));
            }
        }
    }

    /// <summary>
    /// UPDATE-time counterpart to <see cref="EnforceUniqueIndexes"/>:
    /// walks each <see cref="HeapTable.Indexes"/> UNIQUE entry and notes
    /// Msg 2601 for each key collision among updated rows or against
    /// other (non-affected) heap rows. Filter-aware in the same shape as
    /// the INSERT path — rows excluded by an index's <c>Index.Filter</c>
    /// are skipped on both sides of the comparison. <paramref name="onlyRow"/>
    /// is <see cref="EnforceKeyConstraintsForUpdate"/>'s.
    /// </summary>
    private static void EnforceUniqueIndexesForUpdate(HeapTable table, List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected, BatchContext batch, int onlyRow, ref UpdateKeyViolations? violations)
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
        SqlValue[]? existingRowValues = null;
        var qualifiedTableName = QualifiedForViolation(table);
        var affectedKeys = new AffectedKeyIndex?[table.Indexes.Count];
        var existingComputedKeys = new HashSet<SqlValueKey>?[table.Indexes.Count];

        for (var i = Math.Max(onlyRow, 0); i < (onlyRow < 0 ? affected.Count : onlyRow + 1); i++)
        {
            var myStored = storedSnapshots[i];
            var myFull = affected[i].FullNew;

            for (var x = 0; x < table.Indexes.Count; x++)
            {
                var index = table.Indexes[x];
                if (!index.IsUnique || index.IsDisabled || violations?.Settles(i, index.IndexId) == true)
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
                // collided there. A computed key is compared on the full row,
                // where the old image is only available when the caller kept one.
                else if (index.KeysAreStored
                    ? !KeyTupleMoved(index.KeyStorageOrdinals, myStored, affected[i], table)
                    : !ComputedKeyMoved(index.KeyFullOrdinals, affected[i], table, batch))
                {
                    continue;
                }

                // A filtered index counts only the affected rows inside its set,
                // so the filter is evaluated once per row here rather than once
                // per pair as the walk this replaces did.
                // A key naming a non-persisted computed column has no storage
                // ordinals to compare, so both halves of the check read the
                // evaluated full row: the affected rows against each other, and
                // the unaffected rows out of a set built once for the statement.
                if (!index.KeysAreStored)
                {
                    if ((onlyRow < 0 && ComputedKeySharedByAnotherAffectedRow(affected, i, index.KeyFullOrdinals, table, index.Filter, batch))
                        || (existingComputedKeys[x] ??= BuildComputedKeySet(table, index.KeyFullOrdinals, index.Filter, batch, affectedAddrs))
                            .Contains(new SqlValueKey(ReadKeyByFullOrdinals(index.KeyFullOrdinals, myFull))))
                    {
                        UpdateKeyViolations.Note(ref violations, table, affected.Count, i, index, UniqueIndexViolationOnComputedKey(index, qualifiedTableName, myFull));
                    }
                    continue;
                }

                if (onlyRow < 0
                    && (affectedKeys[x] ??= AffectedKeyIndex.Build(
                        storedSnapshots,
                        index.KeyStorageOrdinals,
                        index.Filter is not { } setFilter
                            ? null
                            : FilterMembership(table, setFilter, affected, batch))).SharedByAnotherRow(i))
                {
                    UpdateKeyViolations.Note(ref violations, table, affected.Count, i, index, UniqueIndexViolation(index, qualifiedTableName, myStored));
                    continue;
                }

                if (TryPrepareKeySeek(table, index.KeyStorageOrdinals, myStored, out var commons, out var probe))
                {
                    AwaitUncommittedKeyWriters(batch, table, index.KeyStorageOrdinals, commons, probe);
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

                        UpdateKeyViolations.Note(ref violations, table, affected.Count, i, index, UniqueIndexViolation(index, qualifiedTableName, myStored));
                        break;
                    }
                    continue;
                }

                if (ScanFindsKey(table, index.KeyStorageOrdinals, myStored, index.Filter, affectedAddrs, batch))
                    UpdateKeyViolations.Note(ref violations, table, affected.Count, i, index, UniqueIndexViolation(index, qualifiedTableName, myStored));
            }
        }
    }

    /// <summary>
    /// Whether another affected row's post-update key equals row
    /// <paramref name="self"/>'s, over a key read by full ordinal — the
    /// computed-key counterpart of <see cref="AffectedKeyIndex"/>.
    /// </summary>
    /// <remarks>
    /// Built per call rather than memoized like its stored-key sibling: an
    /// UPDATE that moves a computed key on many rows is rare enough that the
    /// pairwise-free hash build is the whole win, and the set is small (one
    /// entry per affected row).
    /// </remarks>
    private static bool ComputedKeySharedByAnotherAffectedRow(
        List<(int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld)> affected,
        int self,
        int[] fullOrdinals,
        HeapTable table,
        BooleanExpression? filter,
        BatchContext batch)
    {
        var mine = new SqlValueKey(ReadKeyByFullOrdinals(fullOrdinals, affected[self].FullNew));
        for (var i = 0; i < affected.Count; i++)
        {
            if (i == self)
                continue;
            if (filter is not null && EvaluateIndexFilter(filter, table, affected[i].FullNew, batch) != true)
                continue;
            if (mine.Equals(new SqlValueKey(ReadKeyByFullOrdinals(fullOrdinals, affected[i].FullNew))))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the UPDATE moved this row's computed key. The pre-update image
    /// comes from the caller's captured <c>FullOld</c> when it kept one and from
    /// the row's own heap slot otherwise — validation runs before the rewrite,
    /// so the slot still holds the old bytes. A row with no pre-update state
    /// (MERGE's pending inserts) counts as moved.
    /// </summary>
    private static bool ComputedKeyMoved(
        int[] fullOrdinals,
        (int PageIndex, int SlotIndex, SqlValue[] FullNew, SqlValue[]? FullOld) row,
        HeapTable table,
        BatchContext batch)
    {
        SqlValue[] oldFull;
        if (row.FullOld is { } captured)
        {
            oldFull = captured;
        }
        else
        {
            if (row.PageIndex < 0 || table.Heap.ReadSlotBytes(row.PageIndex, row.SlotIndex) is not { } oldBytes)
                return true;
            SqlValue[]? buffer = null;
            oldFull = DecodeFullRowWithComputed(table, oldBytes, batch, ref buffer);
        }

        // The captured old image carries stored values only, so its computed
        // slots are re-evaluated here rather than trusted.
        var evaluated = (SqlValue[])oldFull.Clone();
        EvaluateComputedColumns(table, evaluated, batch);
        foreach (var ordinal in fullOrdinals)
        {
            if (!row.FullNew[ordinal].Equals(evaluated[ordinal]))
                return true;
        }

        return false;
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
