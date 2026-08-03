using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Execution-side trunk for <see cref="Selection"/>: the static planner
/// (<see cref="BuildSqlProjection"/>), per-row column resolution, and the
/// non-aggregate / non-window projection paths. Sibling partials own the
/// other phases — set ops (<c>Selection.Execution.SetOps.cs</c>), aggregates
/// (<c>Selection.Execution.Aggregate.cs</c>), windows
/// (<c>Selection.Execution.Window.cs</c>), join enumeration
/// (<c>Selection.Execution.Joins.cs</c>), and ORDER BY key handling
/// (<c>Selection.Execution.OrderBy.cs</c>). The parser-side counterpart lives
/// in <c>Selection.cs</c>.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Locates a column reference across all FROM sources. A qualified
    /// reference (<c>alias.col</c> / <c>tableName.col</c>) restricts the
    /// search to the source whose <see cref="FromSource.Qualifier"/>
    /// matches; an unqualified reference searches all sources and raises
    /// <see cref="SimulatedSqlException.AmbiguousColumnName"/> (Msg 209)
    /// if the column name appears in more than one. Returns
    /// <c>(-1, -1)</c> when no source resolves the name — the caller then
    /// falls through to the outer scope.
    /// </summary>
    internal static (int SourceIndex, int ColumnIndex) FindSourceColumn(FromSource[] sources, MultiPartName name)
    {
        if (name.ImmediateQualifier is { } qualifier)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is null || !BuiltInToken.Equals(sources[s].Qualifier, qualifier))
                    continue;
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
                        return (s, c);
                }
                // Qualifier matched but the column doesn't exist in that
                // source; fall through to outer (caller handles).
                return (-1, -1);
            }
            // No source's qualifier matches the prefix → outer fallthrough.
            return (-1, -1);
        }

        var foundSource = -1;
        var foundColumn = -1;
        var matches = 0;
        for (var s = 0; s < sources.Length; s++)
        {
            for (var c = 0; c < sources[s].ColumnNames.Length; c++)
            {
                if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
                {
                    if (matches == 0)
                    {
                        foundSource = s;
                        foundColumn = c;
                    }
                    matches++;
                }
            }
        }
        // Ambiguity across real sources is a compile error — unless a
        // placeholder source is in scope, in which case real SQL Server defers
        // the whole statement's binding (the missing object could own the name),
        // so bind to the first match and let the discarded statement carry on.
        return matches > 1
            ? AnyPlaceholderSource(sources) ? (foundSource, foundColumn) : throw SimulatedSqlException.AmbiguousColumnName(name.Leaf)
            : matches == 1 ? (foundSource, foundColumn) : (-1, -1);
    }

    /// <summary>
    /// Records the facts <c>CREATE INDEX</c> judges a view on, when a
    /// validation parse installed a collector (see
    /// <see cref="IndexedViewShape"/>). Everything the battery needs except
    /// subqueries and nondeterministic functions is already in scope here, so
    /// this is the single recording site for the structural half.
    /// </summary>
    private static void RecordIndexedViewShape(
        BatchContext parseBatch,
        FromSource[] sources,
        JoinSpec[] joins,
        FromClause fromClause,
        bool distinct,
        Expression? topExpression,
        List<AggregateExpression> aggregates)
    {
        if (parseBatch.Parser.IndexedViewShapeCollector is not { } shape)
            return;

        shape.HasDistinct |= distinct;
        shape.HasTopOrOffset |= topExpression is not null
            || fromClause.OffsetExpression is not null
            || fromClause.FetchExpression is not null;
        shape.HasGroupBy |= fromClause.GroupingSets.Count > 0;

        foreach (var join in joins)
        {
            if (join.Kind is JoinKind.Left or JoinKind.Right or JoinKind.Full)
            {
                shape.HasOuterJoin = true;
                break;
            }
        }

        // Self-join: two FROM sources resolving to the same base table. Real
        // names that table in Msg 1947, so the first duplicate is captured.
        for (var i = 0; shape.SelfJoinedTable is null && i < sources.Length; i++)
        {
            if (sources[i].BackingTable is not { } table)
                continue;
            for (var j = i + 1; j < sources.Length; j++)
            {
                if (ReferenceEquals(sources[j].BackingTable, table))
                {
                    shape.SelfJoinedTable = table;
                    break;
                }
            }
        }

        // SUM over an expression that can produce NULL is Msg 8662. Column
        // nullability comes from the FROM sources, the same source
        // Expression.ResultIsNullable consults for result metadata.
        var operandNullability = new NullabilityContext(
            parseBatch,
            name => ColumnIsNullableAcrossSources(sources, name),
            ColumnTypeResolverFor(sources));
        foreach (var aggregate in aggregates)
        {
            shape.Aggregates.Add(aggregate.Kind);
            if (aggregate.Kind == AggregateKind.Sum
                && aggregate.Operand is { } operand
                && operand.ResultIsNullable(operandNullability))
            {
                shape.SumsNullableExpression = true;
            }
        }
    }

    /// <summary>
    /// Column-nullability lookup across the FROM sources for
    /// <see cref="RecordIndexedViewShape"/>; an unresolvable name is treated as
    /// nullable, the conservative direction for a gate that rejects.
    /// </summary>
    private static bool ColumnIsNullableAcrossSources(FromSource[] sources, MultiPartName name)
    {
        var (sourceIndex, columnIndex) = FindSourceColumn(sources, name);
        return sourceIndex < 0 || sources[sourceIndex].Columns[columnIndex].Nullable;
    }

    /// <summary>
    /// Enforces the GROUP BY containment rule (Msg 8120 / 8121 / 8127): outside
    /// an aggregate, a column reference must resolve to a bare GROUP BY column.
    /// <see cref="Expression.VisitColumnReferences"/> already skips
    /// aggregate-internal columns (an <see cref="AggregateExpression"/> doesn't
    /// visit its operand), so it yields exactly the bare, non-aggregated
    /// references. Columns that appear only <em>inside</em> a compound GROUP BY
    /// expression (<c>GROUP BY a+b</c>) are the conservative seam: SQL Server
    /// licenses <c>SELECT a+b</c> but rejects a bare <c>SELECT a</c>, and
    /// telling those apart needs sub-expression structural matching we don't do,
    /// so such a reference is left unflagged rather than risk a false positive on
    /// the valid <c>SELECT (a+b)*2</c> shape. A reference to a column absent from
    /// GROUP BY entirely is unambiguously invalid and raised. A reference that
    /// doesn't resolve against these sources (correlated / outer) is not this
    /// query's grouping concern.
    /// </summary>
    private static void ValidateGroupByReferences(FromSource[] sources, List<Expression> expressions, List<OrderBySpec> orderBy, string[] outputColumnNames, FromClause fromClause, List<WindowExpression> windows)
    {
        var groupedBare = new HashSet<(int Source, int Column)>();
        var groupedComponent = new HashSet<(int Source, int Column)>();
        foreach (var grouping in fromClause.AllGroupingExpressions)
        {
            if (grouping is Reference bare)
            {
                if (TryResolveSourceColumn(sources, bare.ReferencedName) is { } id)
                    _ = groupedBare.Add(id);
            }
            else
            {
                grouping.VisitColumnReferences(name =>
                {
                    if (TryResolveSourceColumn(sources, name) is { } id)
                        _ = groupedComponent.Add(id);
                });
            }
        }

        void Check(MultiPartName name, Func<string, SimulatedSqlException> error)
        {
            if (TryResolveSourceColumn(sources, name) is not { } id
                || groupedBare.Contains(id) || groupedComponent.Contains(id))
            {
                return;
            }

            var source = sources[id.Source];
            var column = source.ColumnNames[id.Column];
            throw error(source.Qualifier is { } qualifier ? $"{qualifier}.{column}" : column);
        }

        foreach (var expression in expressions)
            expression.VisitColumnReferences(name => Check(name, SimulatedSqlException.ColumnNotInGroupByForSelect));

        // A window in an aggregate query runs over the *grouped* rows, so its
        // own operand and PARTITION BY / ORDER BY expressions are group-level
        // too: each may name a grouping column or an aggregate, but a bare
        // non-grouped column is Msg 8120 exactly as in the select list
        // (probe-confirmed for an operand, `SUM(amt) OVER ()`, and for a
        // partition key, `PARTITION BY region` — both report the select-list
        // wording). Reaching the window aggregate's operand *directly* is what
        // separates those from the legal nested `SUM(SUM(amt)) OVER ()`, where
        // the operand is itself an aggregate whose own operand stays skipped.
        // Windows are read off the parser's collector rather than walked out of
        // the projection trees, so one buried in an arithmetic expression is
        // covered identically.
        foreach (var window in windows)
        {
            void CheckWindowOperand(Expression? operand) =>
                operand?.VisitColumnReferences(name => Check(name, SimulatedSqlException.ColumnNotInGroupByForSelect));

            CheckWindowOperand(window.Operand);
            CheckWindowOperand(window.AggregateInfo?.Operand);
            CheckWindowOperand(window.DefaultArg);
            foreach (var partition in window.PartitionBy)
                CheckWindowOperand(partition);
            foreach (var item in window.OrderBy)
                CheckWindowOperand(item.Expr);
        }

        // The surviving walk, not the written one: real runs this pass over the
        // post-fold tree, so a HAVING conjunct it settled while compiling takes
        // its columns out of the check (`HAVING NULL <> b` and
        // `HAVING 1 = 0 AND b > 1` both answer no rows over an ungrouped `b`).
        fromClause.Having?.VisitSurvivingOperandExpressions(op =>
            op.VisitColumnReferences(name => Check(name, SimulatedSqlException.ColumnNotInGroupByForHaving)));

        foreach (var item in orderBy)
        {
            item.Expr?.VisitColumnReferences(name =>
            {
                // ORDER BY resolves an unqualified SELECT-output alias before a
                // source column — an alias names an already-validated projection,
                // so it can't be an ungrouped-column violation here.
                if (name.ImmediateQualifier is null)
                {
                    foreach (var outputName in outputColumnNames)
                    {
                        if (BuiltInToken.Equals(outputName, name.Leaf))
                            return;
                    }
                }

                Check(name, SimulatedSqlException.ColumnNotInGroupByForOrderBy);
            });
        }
    }

    /// <summary>
    /// Best-effort local resolution for GROUP BY validation: the resolved
    /// (source, column) pair, or null when the name is correlated / outer /
    /// unresolved (or ambiguous — which would already have failed type
    /// resolution). Never raises: recording a diagnostic must not itself throw.
    /// </summary>
    private static (int Source, int Column)? TryResolveSourceColumn(FromSource[] sources, MultiPartName name)
    {
        try
        {
            var (source, column) = FindSourceColumn(sources, name);
            return source < 0 ? null : (source, column);
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Static type-resolution counterpart to <see cref="FindSourceColumn"/>:
    /// returns the column's declared type if it resolves locally across
    /// sources; falls through to <paramref name="outerTypeResolver"/> if
    /// nothing matches; raises Msg 209 on unqualified ambiguity.
    /// </summary>
    private static SqlType ResolveColumnTypeAcrossSources(FromSource[] sources, MultiPartName name, Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        var (s, c) = FindSourceColumn(sources, name);
        if (s != -1)
        {
            // A HeapColumn can carry its MAX-ness in MaxLength while its .Type
            // stays a length-0 "value-width" variant (catalog-view columns
            // like sys.sql_modules.definition are declared this way). Fold that
            // back in so an expression referencing the column (ISNULL /
            // COALESCE / CASE — SMO reads proc bodies as
            // ISNULL(sql_modules.definition, …)) types as MAX and streams as
            // PLP over the wire, rather than losing MAX to the bounded 2-byte
            // length prefix and overflowing on a large value.
            return ColumnTypeWithMaxLength(sources[s].Columns[c]);
        }

        // A placeholder source (skip-mode stand-in for an unresolvable table)
        // means real SQL Server would defer this whole statement's binding, so
        // an unresolved column can't be a compile error — it just belongs to
        // the missing object. Return a placeholder type; the statement is
        // discarded before execution. Without a placeholder in scope, a genuine
        // missing column on a resolvable table stays a Msg 207 even in skip mode
        // (probe-confirmed: real SQL Server errors at compile time here).
        return AnyPlaceholderSource(sources)
            ? SqlType.Int32
            : outerTypeResolver is not null
                ? outerTypeResolver(name)
                : throw SimulatedSqlException.InvalidColumnName(name);
    }

    /// <summary>
    /// A column's declared type with its MAX-ness folded back in: a
    /// <see cref="HeapColumn"/> can carry that in <see cref="HeapColumn.MaxLength"/>
    /// while its <c>Type</c> stays a length-0 "value-width" variant.
    /// </summary>
    private static SqlType ColumnTypeWithMaxLength(HeapColumn column) =>
        column.MaxLength == SqlType.MaxLengthSentinel
            ? SqlType.AsMaxVariant(column.Type)
            : column.Type;

    /// <summary>
    /// <see cref="ResolveColumnTypeAcrossSources"/> as a resolver the DML
    /// paths can hand to <see cref="BooleanExpression.Bind"/> /
    /// <see cref="Expression.GetSqlType"/> — a joined UPDATE / DELETE parses
    /// its own <see cref="FromSource"/> set and needs the same
    /// compile-time column binding a SELECT gets.
    /// </summary>
    internal static Func<MultiPartName, SqlType> ColumnTypeResolverFor(FromSource[] sources) =>
        name => ResolveColumnTypeAcrossSources(sources, name, null);

    /// <summary>
    /// The compile-time resolver for DML written against a view whose body
    /// reads several sources: with no single base table to translate to, the
    /// name binds on its leaf against the view's own output columns and takes
    /// the type declared there. Qualifiers are ignored, matching the per-row
    /// resolver the join-view UPDATE path builds; anything else is Msg 207.
    /// </summary>
    internal static Func<MultiPartName, SqlType> ViewOutputColumnTypeResolver(BatchContext batch, Schemas.View view) =>
        name =>
        {
            var collation = batch.CurrentDatabase.Collation;
            foreach (var column in view.OutputColumns)
            {
                if (collation.Equals(column.Name, name.Leaf))
                    return ColumnTypeWithMaxLength(column);
            }
            throw SimulatedSqlException.InvalidColumnName(name);
        };

    /// <summary>
    /// The single-table DML counterpart: a compile-time resolver mirroring the
    /// per-row resolver an UPDATE / DELETE with no FROM clause builds — the
    /// name binds on its leaf against the target's columns, through the view's
    /// own projection when the statement goes through one, and anything else
    /// is Msg 207. Qualifiers are ignored on both sides, matching the per-row
    /// resolver exactly.
    /// </summary>
    internal static Func<MultiPartName, SqlType> TargetColumnTypeResolver(BatchContext batch, HeapTable table, Schemas.View? sourceView) =>
        name =>
        {
            var collation = batch.CurrentDatabase.Collation;
            if (sourceView is not null)
            {
                for (var v = 0; v < sourceView.OutputColumns.Length; v++)
                {
                    if (collation.Equals(sourceView.OutputColumns[v].Name, name.Leaf))
                    {
                        var baseOrdinal = sourceView.BaseColumnOrdinals[v];
                        return baseOrdinal < 0
                            ? throw SimulatedSqlException.InvalidColumnName(name)
                            : ColumnTypeWithMaxLength(table.Columns[baseOrdinal]);
                    }
                }
                throw SimulatedSqlException.InvalidColumnName(name);
            }
            for (var k = 0; k < table.Columns.Length; k++)
            {
                if (collation.Equals(table.Columns[k].Name, name.Leaf))
                    return ColumnTypeWithMaxLength(table.Columns[k]);
            }
            throw SimulatedSqlException.InvalidColumnName(name);
        };

    /// <summary>
    /// Parses a DML statement's <c>WHERE</c> with
    /// <paramref name="resolveColumnType"/> installed as the enclosing scope —
    /// the same chaining <see cref="ConsumeWhereOrderByWithOuterScope"/> gives
    /// a SELECT, so a subquery inside the predicate resolves the statement's
    /// target columns while typing its own projection — then binds the
    /// predicate itself through <see cref="BooleanExpression.Bind"/>.
    /// </summary>
    internal static BooleanExpression ParseAndBindPredicate(ParserContext context, Func<MultiPartName, SqlType> resolveColumnType)
    {
        var saved = context.OuterTypeResolver;
        context.OuterTypeResolver = resolveColumnType;
        BooleanExpression predicate;
        try
        {
            predicate = BooleanExpression.Parse(context);
        }
        finally
        {
            context.OuterTypeResolver = saved;
        }
        predicate.Bind(context.Batch, resolveColumnType);
        return BooleanExpression.SimplifyForFilter(predicate, context);
    }

    /// <summary>
    /// True when any source in the set is a skip-mode placeholder (a stand-in
    /// for an unresolvable table). See <see cref="FromSource.IsPlaceholder"/>.
    /// </summary>
    internal static bool AnyPlaceholderSource(FromSource[] sources)
    {
        foreach (var source in sources)
        {
            if (source.IsPlaceholder)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the plan for a SELECT whose FROM clause has at least one
    /// source (and possibly JOINs). Static work — output schema, validation
    /// of ordinal ORDER BY items, LOB-in-DISTINCT/ORDER-BY checks — happens
    /// here. The deferred closure runs per <see cref="Execute"/> call,
    /// accepting the outer-row resolver and dispatching to the aggregate or
    /// simple projection path; each row tuple (one byte[] per source, null
    /// in unmatched LEFT-JOIN slots) is decoded column-by-column on demand
    /// and projected through <see cref="Expression.Run(RuntimeContext)"/>.
    /// </summary>
    /// <summary>
    /// Finds a WHERE equality that can be pushed into the leftmost catalog-view
    /// source's row generator. Eligible when <c>sources[0]</c> is a
    /// pushdown-aware catalog view and some top-level AND-conjunct is
    /// <c>&lt;key&gt; = &lt;comparand&gt;</c> (either operand order) where the key
    /// is one of the view's <see cref="Schemas.CatalogView.PushdownColumns"/>
    /// qualified to this source, and the comparand is row-independent (reads no
    /// column, so it evaluates to one value for the whole scan). Returns the
    /// source, the canonical key-column name, and the comparand; null when
    /// nothing qualifies. Only the leftmost source is considered — a catalog view
    /// deeper in a JOIN keeps its full scan.
    /// </summary>
    private static (FromSource Source, string Column, Expression Comparand)? DetectCatalogPushdown(
        FromSource[] sources,
        List<BooleanExpression> excluders)
    {
        if (sources.Length == 0)
            return null;
        var source = sources[0];
        if (source.BackingCatalogView is not { PushdownColumns: { } pushColumns, FilteredRowGenerator: not null })
            return null;

        var singleSource = sources.Length == 1;
        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);

        foreach (var conjunct in conjuncts)
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            if (MatchPushdownKey(left, right, source.Qualifier, singleSource, pushColumns) is { } forward)
                return (source, forward.Column, forward.Comparand);
            if (MatchPushdownKey(right, left, source.Qualifier, singleSource, pushColumns) is { } reversed)
                return (source, reversed.Column, reversed.Comparand);
        }
        return null;
    }

    // When `keySide` is a column reference to the catalog source naming one of
    // `pushColumns`, and `valueSide` is row-independent, returns the canonical
    // column name (from `pushColumns`) paired with the comparand; else null.
    private static (string Column, Expression Comparand)? MatchPushdownKey(
        Expression keySide,
        Expression valueSide,
        string? sourceQualifier,
        bool singleSource,
        string[] pushColumns)
    {
        if (keySide is not Reference reference || !valueSide.IsRowIndependent)
            return null;

        var name = reference.ReferencedName;
        // Qualified (`c.object_id`) must match this source's alias / view name;
        // unqualified (`object_id`) is only unambiguous when it's the sole source.
        var qualifier = name.ImmediateQualifier;
        var qualifierMatches = qualifier is null ? singleSource : BuiltInToken.Equals(qualifier, sourceQualifier);
        if (!qualifierMatches)
            return null;

        foreach (var pushColumn in pushColumns)
        {
            if (BuiltInToken.Equals(pushColumn, name.Leaf))
                return (pushColumn, valueSide);
        }
        return null;
    }

    /// <summary>
    /// Rejects an aggregate whose operand reads only the <em>enclosing</em>
    /// query's columns, such as <c>(SELECT MAX(t.col) FROM u)</c> inside a
    /// query over <c>t</c>. Real SQL Server binds that aggregate to the outer
    /// query, which then becomes an aggregate query itself and collapses to
    /// one row; the simulator binds it to the query it is written in, so it
    /// would silently return one row per outer row instead.
    /// </summary>
    /// <remarks>
    /// A wrong answer is worse than a refusal, so this raises rather than
    /// guessing. An aggregate mixing inner and outer references, or reading no
    /// column at all (<c>COUNT(*)</c>, <c>MAX(1)</c>), is left alone — only the
    /// wholly-outer case is ambiguous.
    /// <para>A name that resolves in <em>no</em> scope isn't that case at all:
    /// it is real's Msg 207, which real reports at compile time (probe-confirmed
    /// — <c>HAVING MAX(nosuchcol) = 1</c> refuses a <c>CREATE VIEW</c> outright,
    /// while the genuinely-outer <c>(SELECT MAX(t.a) FROM u)</c> creates and
    /// only misbehaves at run time). The enclosing type resolver is what tells
    /// the two apart; it raises Msg 207 itself once the scope chain runs
    /// out.</para>
    /// </remarks>
    private static void RehomeAggregatesOverOuterScope(
        BatchContext parseBatch,
        FromSource[] sources,
        List<AggregateExpression> aggregates,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        // A placeholder source means the statement's whole binding defers to
        // the missing object, so an unresolved name says nothing about scope.
        if (AnyPlaceholderSource(sources))
            return;

        List<AggregateExpression>? rehomed = null;
        foreach (var aggregate in aggregates)
        {
            // Walk the operand, not the aggregate: VisitColumnReferences on an
            // AggregateExpression deliberately skips its own operand so the
            // GROUP BY containment rule sees only bare references.
            if (aggregate.Operand is not { } operand)
                continue;

            var referenced = 0;
            var resolvedHere = 0;
            MultiPartName? unresolved = null;
            operand.VisitColumnReferences(name =>
            {
                referenced++;
                if (FindSourceColumn(sources, name).SourceIndex >= 0)
                    resolvedHere++;
                else
                    unresolved ??= name;
            });

            if (referenced == 0 || resolvedHere > 0)
                continue;

            // Resolve the outer scope chain before concluding anything: with no
            // enclosing scope at all, or with one that doesn't know the name,
            // this is a bad column reference rather than an outer-bound
            // aggregate.
            if (outerTypeResolver is null)
                throw SimulatedSqlException.InvalidColumnName(unresolved!.Value);
            _ = outerTypeResolver(unresolved!.Value);

            // The aggregate reads only the enclosing query's columns, so it
            // belongs to that query: real evaluates it there, which makes the
            // enclosing query an aggregate query and collapses it to one row
            // per group. Move the same instance across — the expression tree
            // here keeps referencing it, so once the owning query binds its
            // per-group result this scope reads that value.
            if (parseBatch.Parser.EnclosingAggregateCollector is not { } enclosing)
                throw new NotSupportedException("An aggregate over an enclosing query's columns (which binds to the outer query on SQL Server) isn't modeled.");

            enclosing.Add(aggregate);
            (rehomed ??= []).Add(aggregate);
        }

        if (rehomed is not null)
            _ = aggregates.RemoveAll(rehomed.Contains);
    }

    /// <summary>
    /// The securable a FROM source's columns carry grants on — its backing table
    /// or view. Null when the source has none (derived table, catalog view,
    /// temp table), or when the source was reached through a synonym: a synonym
    /// takes no column grants at all, so such a reference is checked
    /// object-grain against the synonym itself.
    /// </summary>
    private static Schemas.SchemaObject? ColumnGrantableSecurable(FromSource source) =>
        source.ViaSynonym is not null ? null
            : source.BackingTable is { } table ? (table.Name.StartsWith('#') ? null : table)
            : source.BackingView;

    private static ColumnReadTarget NewReadTarget(FromSource source) =>
        source.BackingTable is { } table ? new ColumnReadTarget(table) : new ColumnReadTarget(source.BackingView!);

    /// <summary>
    /// Raises <b>Msg 451</b> when <paramref name="type"/> reached an output
    /// slot still carrying an unresolved collation. The tail names the clause
    /// and the slot's 1-based ordinal — <c>SELECT</c> and <c>ORDER BY</c> count
    /// from their own first term, <c>GROUP BY</c> from 2 because the grouped
    /// projection real builds carries one column ahead of the keys (all
    /// probe-confirmed against SQL Server 2025).
    /// </summary>
    private static void RequireSettledOutputCollation(SqlType type, string clause, int ordinal)
    {
        if (UnresolvedCollation.On(type) is { } conflict)
        {
            throw SimulatedSqlException.UnresolvedCollationInOutputColumn(
                conflict.RightName, conflict.LeftName, conflict.OperatorName, clause, ordinal);
        }
    }

    private static Selection BuildSqlProjection(
        BatchContext parseBatch,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        Expression? topExpression,
        bool topPercent,
        bool topWithTies,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        Func<MultiPartName, SqlType>? outerTypeResolver,
        bool isAssignmentOnly,
        MultiPartName? intoTarget,
        Dictionary<int, ColumnReadTarget>? readColumnSink,
        bool projectionDiscarded = false)
    {
        RecordIndexedViewShape(parseBatch, sources, joins, fromClause, distinct, topExpression, aggregates);

        RehomeAggregatesOverOuterScope(parseBatch, sources, aggregates, parseBatch.Parser.OuterTypeResolver ?? outerTypeResolver);

        // Convert a comma-join / CROSS JOIN carrying an equi-join predicate in
        // WHERE into an INNER JOIN, so it rides the equi-join seek / hash path
        // instead of the O(L×R) nested loop. Value-independent, so it's done
        // once here and the rewritten array is captured in the cached plan.
        // A parenthesized join group's connecting join spans multiple slots;
        // the comma→equi rewrite assumes a single-source right operand, so skip
        // it when a group is present (the group folds via the nested-loop path).
        if (!ContainsJoinGroup(joins))
            joins = RewriteCommaJoinsToEquiJoins(sources, joins, fromClause.Excluders);

        // Catalog-view predicate pushdown: when the leftmost source is a
        // pushdown-aware catalog view (sys.columns etc.) and WHERE carries a
        // top-level `<key> = <row-independent comparand>` conjunct, rebuild the
        // source's generator plan so it enumerates only matching objects instead
        // of materializing every row. Value-independent decision (compiled into
        // the shared plan); the comparand's value is resolved per execution. The
        // full WHERE still runs as a residual filter, so this can only narrow the
        // generator output, never change the result.
        if (DetectCatalogPushdown(sources, fromClause.Excluders) is (var pushSource, var pushColumn, var pushComparand))
        {
            sources[0] = new FromSource(
                qualifier: pushSource.Qualifier,
                columnNames: pushSource.ColumnNames,
                columns: pushSource.Columns,
                storedSchema: pushSource.StoredSchema,
                storageOrdinals: pushSource.StorageOrdinals,
                lobStore: pushSource.LobStore,
                rows: pushSource.Rows,
                lateralPlan: ForCatalogView(pushSource.BackingCatalogView!, pushSource.BackingCatalogDatabase!, pushColumn, pushComparand),
                materializeOnce: true,
                backingCatalogView: pushSource.BackingCatalogView,
                backingCatalogDatabase: pushSource.BackingCatalogDatabase);
        }

        var orderBy = fromClause.OrderBy;
        if (topWithTies && orderBy.Count == 0)
            throw SimulatedSqlException.TopWithTiesRequiresOrderBy();
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        // Column-level read tracking (parse-time, principal-independent): record
        // every table / view column this query reads into the shared sink so the
        // execution-time column-level SELECT check (Msg 230 / 229) can run
        // against the current principal. Pre-seed each such source with an
        // empty ordinal set — a read that names no column (COUNT(*) /
        // SELECT 1) then routes through the column path as "all columns". The
        // projection funnels through RecordingResolver (recording is free — the
        // schema resolution already visits these references); WHERE / JOIN ON /
        // GROUP BY / HAVING / ORDER BY / aggregate operands are walked
        // structurally below. The runtime row closure keeps the non-recording
        // ResolveColumnType, so this adds nothing to execution.
        void RecordReadColumn(MultiPartName name)
        {
            if (readColumnSink is null)
                return;
            // Best-effort, non-throwing resolution: unlike FindSourceColumn this
            // silently skips an unresolved (correlated / outer) or ambiguous name
            // rather than raising Msg 207 / 209 — recording must never alter query
            // semantics. A qualified name binds to its one qualifier-matching
            // source; an unqualified name binds only on a single match.
            var matchSource = -1;
            var matchColumn = -1;
            var matches = 0;
            var qualifier = name.ImmediateQualifier;
            for (var s = 0; s < sources.Length; s++)
            {
                if (qualifier is not null && (sources[s].Qualifier is null || !BuiltInToken.Equals(sources[s].Qualifier, qualifier)))
                    continue;
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
                    {
                        matchSource = s;
                        matchColumn = c;
                        matches++;
                    }
                }
                if (qualifier is not null)
                    break;
            }
            if (matches != 1 || ColumnGrantableSecurable(sources[matchSource]) is not { } securable)
                return;
            if (!readColumnSink.TryGetValue(securable.ObjectId, out var target))
                readColumnSink[securable.ObjectId] = target = NewReadTarget(sources[matchSource]);
            _ = target.Ordinals.Add(matchColumn + 1);
        }
        SqlType RecordingResolver(MultiPartName name)
        {
            RecordReadColumn(name);
            return ResolveColumnType(name);
        }
        if (readColumnSink is not null)
        {
            foreach (var source in sources)
            {
                if (ColumnGrantableSecurable(source) is { } securable)
                    _ = readColumnSink.TryAdd(securable.ObjectId, NewReadTarget(source));
            }
        }

        // A projection that reaches the client, or that materializes a column
        // (SELECT … INTO, a view's output), has to name one collation itself
        // and reports Msg 451 when the term it built carries an unresolved one.
        // An assignment target supplies the collation instead — real settles
        // the same conflict against it silently — so an INSERT … SELECT source,
        // a `SELECT @v = …` list, and an EXISTS body (whose projection is never
        // materialized) never demand one.
        var projectionFeedsAnAssignment = isAssignmentOnly || parseBatch.Parser.InInsertSourceSelect;
        var projectionNamesOwnCollation = !projectionFeedsAnAssignment && !projectionDiscarded;
        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].GetSqlType(parseBatch, readColumnSink is null ? ResolveColumnType : RecordingResolver);
            outputColumnNames[i] = expressions[i].Name;
        }

        // The select list is the *last* slot real settles: a WHERE / JOIN
        // predicate's Msg 4191, a GROUP BY term's Msg 451 and an ORDER BY
        // term's all report ahead of it, and an ORDER BY naming the conflicted
        // projection — by ordinal or by alias — reports as `ORDER BY statement
        // column <n>` rather than the select list's own slot (all
        // probe-confirmed against SQL Server 2025). So the select-list slot is
        // recorded here and raised only once every other clause has bound.
        var unsettledProjection = -1;
        for (var i = 0; i < outputSchema.Length && unsettledProjection < 0; i++)
        {
            if (UnresolvedCollation.On(outputSchema[i]) is null)
                continue;
            if (projectionNamesOwnCollation)
                unsettledProjection = i;
            else if (projectionFeedsAnAssignment)
                // A discarded projection converts nothing, so it settles
                // whatever the family; an assignment target settles only the
                // Unicode one.
                UnresolvedCollation.RequireAssignable(outputSchema[i]);
        }

        // Compile-time bind of the predicates and grouping terms. Real SQL
        // Server binds these while compiling — probe-confirmed that a
        // cross-collation comparison (Msg 468), a legacy-LOB string-scalar
        // argument (Msg 8116) and an unknown column (Msg 207) each report on
        // an empty rowset, in a never-taken branch, and at CREATE of a module
        // whose body carries them. Without this the only path to those errors
        // is the per-row resolver, so a row-less result passed silently.
        // Placed after the projection so a select-list error keeps reporting
        // first, and driven off the non-recording resolver because the
        // read-column sink walks these clauses structurally just below.
        foreach (var excluder in fromClause.Excluders)
            excluder.Bind(parseBatch, ResolveColumnType);
        foreach (var join in joins)
            join.OnPredicate?.Bind(parseBatch, ResolveColumnType);
        // A grouping term names a collation too, and real numbers those slots
        // from 2 — the grouped projection it builds carries one column ahead of
        // the keys (probe-confirmed: a lone `GROUP BY concat(a, b)` reports
        // column 2, and a second key reports column 3).
        var groupingOrdinal = 2;
        foreach (var grouping in fromClause.AllGroupingExpressions)
        {
            RequireSettledOutputCollation(grouping.GetSqlType(parseBatch, ResolveColumnType), "GROUP BY", groupingOrdinal++);
        }
        fromClause.Having?.Bind(parseBatch, ResolveColumnType);

        if (readColumnSink is not null)
        {
            foreach (var aggregate in aggregates)
                aggregate.Operand?.VisitColumnReferences(RecordReadColumn);
            foreach (var window in windows)
                window.AggregateInfo?.Operand?.VisitColumnReferences(RecordReadColumn);
            foreach (var excluder in fromClause.Excluders)
                excluder.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
            fromClause.Having?.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
            foreach (var grouping in fromClause.AllGroupingExpressions)
                grouping.VisitColumnReferences(RecordReadColumn);
            foreach (var orderItem in orderBy)
                orderItem.Expr?.VisitColumnReferences(RecordReadColumn);
            foreach (var join in joins)
                join.OnPredicate?.VisitOperandExpressions(op => op.VisitColumnReferences(RecordReadColumn));
        }

        // Validate ordinal ORDER BY items now that the projection count is
        // known. SQL Server fires Msg 108 at parse time, before any rows are
        // touched, so do the same.
        for (var i = 0; i < orderBy.Count; i++)
        {
            if (orderBy[i].IsOrdinal && (orderBy[i].Ordinal < 1 || orderBy[i].Ordinal > expressions.Count))
                throw SimulatedSqlException.OrderByPositionOutOfRange(orderBy[i].Ordinal);
        }

        // Msg 306: text/ntext/image can't appear in a sort or distinct slot.
        // DISTINCT also has to compare the values it dedups, so an unresolved
        // collation reports here — as Msg 446 State 11, which names the
        // producing operator and DISTINCT together rather than taking either
        // the output-column or the consuming-operation wording.
        if (distinct)
        {
            for (var i = 0; i < outputSchema.Length; i++)
            {
                if (outputSchema[i].IsLob)
                    throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
                if (UnresolvedCollation.On(outputSchema[i]) is { } conflict)
                {
                    throw SimulatedSqlException.UnresolvedCollationInOperation(
                        conflict.RightName, conflict.LeftName, conflict.OperatorName, "DISTINCT", 11);
                }
            }
        }
        // ORDER BY items resolve output-column aliases first (then fall back to
        // source columns), matching SQL Server and the runtime ComputeOrderKeys
        // — so `ORDER BY <select-alias>` and `ORDER BY <aggregate/expression>`
        // type-check here instead of failing as an unknown source column.
        SqlType ResolveOrderByType(MultiPartName name)
        {
            for (var j = 0; j < outputColumnNames.Length; j++)
            {
                if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                    return outputSchema[j];
            }

            return ResolveColumnType(name);
        }

        for (var i = 0; i < orderBy.Count; i++)
        {
            var keyType = orderBy[i].IsOrdinal
                ? outputSchema[orderBy[i].Ordinal - 1]
                : orderBy[i].Expr!.GetSqlType(parseBatch, ResolveOrderByType);
            if (keyType.IsLob)
                throw SimulatedSqlException.LobTypesCannotBeComparedOrSorted();
            RequireSettledOutputCollation(keyType, "ORDER BY", i + 1);
        }

        // Every other clause has bound, so a select-list slot that couldn't
        // settle its collation reports now.
        if (unsettledProjection >= 0)
            RequireSettledOutputCollation(outputSchema[unsettledProjection], "SELECT", unsettledProjection + 1);

        // Msg 8120 / 8121 / 8127: in an aggregate query (any aggregate, GROUP
        // BY, or HAVING present) every column referenced outside an aggregate
        // must be a GROUP BY column. SQL Server is strict — no
        // functional-dependency relaxation, a PK-grouped table doesn't license
        // its other columns — and binds this at parse time, before any row is
        // read, so it runs here on the cached plan build.
        if (aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null)
            ValidateGroupByReferences(sources, expressions, orderBy, outputColumnNames, fromClause, windows);

        var offsetExpression = fromClause.OffsetExpression;
        var fetchExpression = fromClause.FetchExpression;

        // Pre-resolve operand and result types for any aggregate windows so
        // the runtime path doesn't need a column-type resolver. ROW_NUMBER
        // windows leave both null — they don't carry an operand and have a
        // fixed bigint result.
        var windowOperandTypes = new SqlType[windows.Count];
        var windowResultTypes = new SqlType[windows.Count];
        for (var i = 0; i < windows.Count; i++)
        {
            if (windows[i].Kind == WindowKind.Aggregate)
            {
                var aggregate = windows[i].AggregateInfo!;
                windowOperandTypes[i] = aggregate.Operand?.GetSqlType(parseBatch, ResolveColumnType) ?? SqlType.Int32;
                windowResultTypes[i] = windows[i].GetSqlType(parseBatch, ResolveColumnType);
            }
        }

        // SELECT INTO schema inference: when an INTO clause was captured,
        // derive the destination HeapColumn[] now (parse-time) so the
        // dispatch handler can CREATE TABLE before executing. The inference
        // walk also enforces the SELECT-INTO-specific validations
        // (Msg 1038 unnamed projection, Msg 2705 duplicate name).
        var destColumnSchema = intoTarget is { } target
            ? ComputeIntoDestSchema(target, expressions, outputSchema, outputColumnNames, sources, joins, parseBatch, ResolveColumnType)
            : null;

        // Updatable-view shape capture: single source, no JOINs, no DISTINCT,
        // no aggregates / windows / GROUP BY / HAVING. TOP / OFFSET / FETCH are
        // allowed (SQL Server treats TOP views as updatable — probe-confirmed).
        // ORDER BY allowed too (it only affects reads). View.cs consumes this
        // to derive Msg 4403 / 4405 / 4406 metadata at CREATE VIEW.
        var (updatabilityProfile, updatabilityRejection) = ComputeViewUpdatabilityProfile(
            sources, joins, expressions, fromClause, distinct, aggregates, windows);

        var columnNullability = ComputeColumnNullability(expressions, sources, joins, parseBatch, ResolveColumnType);

        ReduceConstantCounts(aggregates, fromClause);

        // A HAVING real can see is never TRUE keeps no group, so the statement
        // answers nothing whatever the rest of it would have done — and real
        // then runs none of it, which is what makes
        // `SELECT a FROM t WHERE a / 0 IS NOT NULL GROUP BY a HAVING NULL IS
        // NOT NULL` answer no rows there rather than Msg 8134. Every binding
        // check above still ran, so Msg 207 / 8120 / 8121 report as they do on
        // real; only the row work is skipped.
        var resultIsProvablyEmpty = fromClause.Having?.IsNeverTrue == true;

        var selection = new Selection(outputSchema, outputColumnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topExpression is not null || offsetExpression is not null || fetchExpression is not null,
            (batch, outerResolver) =>
            {
                if (resultIsProvablyEmpty)
                    return [];
                // Per-execution count resolution: the expressions may carry
                // parameters, and this closure replays across executions of a
                // plan-cached SELECT (EF's Skip/Take shape), so the values
                // must come from the EXECUTING batch, not the parse.
                var top = topExpression is null
                    ? default
                    : topPercent
                        ? new TopSpec(null, ResolveTopPercentValue(topExpression, batch), topWithTies)
                        : new TopSpec(ResolveRowCountLimit(topExpression, RowLimitKind.Top, batch), null, topWithTies);
                var offsetCount = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, batch);
                var fetchCount = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, batch);
                // Materialize provably-uncorrelated catalog-view sources once
                // per execution (before the projection paths build their
                // resolver closures over the array), so a nested-loop join
                // stops re-generating them per outer row and the equi-join
                // hash path can key them. Correlated sources are left untouched.
                var execSources = MaterializeUncorrelatedDeferredSources(sources, batch);
                return aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null
                    ? BuildAggregateProjectionRows(execSources, joins, ResolveColumnType, expressions, fromClause, outputColumnNames, orderBy, aggregates, windows, windowOperandTypes, windowResultTypes, top, offsetCount, fetchCount, distinct, batch, outerResolver)
                    : windows.Count > 0
                        ? ProjectWindowedRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, windows, windowOperandTypes, windowResultTypes, batch, outerResolver)
                        : ProjectSqlRows(execSources, joins, expressions, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, batch, outerResolver);
            },
            isAssignmentOnly,
            intoTarget,
            destColumnSchema,
            updatabilityProfile,
            updatabilityRejection);
        // Capture the cursor-navigable FROM shape plus its ORDER BY, so a
        // KEYSET / DYNAMIC cursor can re-fold the live base heaps per FETCH
        // and order rows the same way a read would. A row limit rides along
        // unresolved — its operands re-evaluate against the batch that OPENs.
        selection.CursorShape = ComputeCursorShape(
            sources, joins, expressions, fromClause, distinct, aggregates, windows,
            selection.HasTopOrOffsetOrFetch
                ? new CursorRowLimit(topExpression, topPercent, topWithTies, offsetExpression, fetchExpression)
                : null);
        if (selection.CursorShape is not null)
            selection.CursorOrderBy = orderBy;
        selection.ColumnNullability = columnNullability;
        selection.ProjectionExpressions = [.. expressions];
        selection.ColumnIntegerLiteralDigits = LiteralDigitsOf(expressions);
        selection.ColumnReportsNumeric = ColumnReportsNumericOf(expressions, outputSchema);
        selection.BranchFromSources = sources;
        selection.AutoSourceNames = AutoSourceNamesOf(sources);
        (selection.AutoColumnSource, selection.AutoColumnOrdinal) = AutoColumnBindingOf(expressions, sources);
        return selection;
    }

    /// <summary>
    /// Applies real's <c>COUNT(&lt;expression it types NOT NULL&gt;)</c> →
    /// <c>COUNT(*)</c> reduction, which drops the argument without evaluating
    /// it: <c>SELECT COUNT(61 / 0)</c> and <c>SELECT COUNT(2000000000 * 3)</c>
    /// answer a count on real where the argument alone raises, while
    /// <c>COUNT(&lt;nullable column&gt; / 0)</c> and
    /// <c>COUNT(DISTINCT 61 / 0)</c> — and <c>SUM</c> / <c>MAX</c> of the same
    /// — raise on both (all probe-confirmed).
    /// <para>
    /// Two fences. The argument has to be a computation over non-NULL literals
    /// (<see cref="Expression.IsNonNullConstantComputation"/>), which is the
    /// nullability real's own reduction reads — narrower than the folded value,
    /// since a fold that raises has no value, and narrower than the projection
    /// metadata's, where arithmetic claims nullable even over two literals. And
    /// the query must carry no <em>grouping expression</em>: real evaluates the
    /// argument once a GROUP BY names one (<c>SELECT COUNT(61 / 0) FROM t GROUP
    /// BY a</c> is Msg 8134 there, while the same statement without the GROUP
    /// BY — and with <c>GROUP BY ()</c> — answers).
    /// </para>
    /// </summary>
    private static void ReduceConstantCounts(List<AggregateExpression> aggregates, FromClause fromClause)
    {
        if (fromClause.AllGroupingExpressions.Count > 0)
            return;
        foreach (var aggregate in aggregates)
        {
            if (aggregate.Kind is AggregateKind.Count or AggregateKind.CountBig
                && !aggregate.Distinct
                && aggregate.Operand?.IsNonNullConstantComputation == true)
            {
                aggregate.CountsRowsOnly = true;
            }
        }
    }

    /// <summary>
    /// Per-projection-column nullability for result-set metadata (the TDS
    /// COLMETADATA fNullable flag). Computed for the no-join shape with at
    /// most one source, where <see cref="Expression.ResultIsNullable"/>'s
    /// rules (direct refs preserve base-column nullability, literals NOT NULL,
    /// other expressions nullable) match SQL Server's result metadata; joined
    /// or multi-source shapes return null and the wire falls back to claiming
    /// every column nullable — outer joins NULL-fill the inner side, so
    /// base-column nullability alone would over-claim NOT NULL there. The
    /// zero-source (FROM-less) case is included so a bare literal projection
    /// reports NOT NULL like real (<c>select 1</c> → <c>Int</c>, not
    /// <c>IntN</c>); a column reference can't appear without a source, so the
    /// resolver is never consulted there. Load-bearing for DacFx bacpac
    /// export: its BCP data-file layout drops the per-value length prefix
    /// on fixed-width columns whose wire metadata says NOT NULL, and the
    /// bacpac loader reads the file per the model.xml declaration — the two
    /// must agree.
    /// </summary>
    private static bool[]? ComputeColumnNullability(
        List<Expression> expressions,
        FromSource[] sources,
        JoinSpec[] joins,
        BatchContext parseBatch,
        Func<MultiPartName, SqlType> resolveColumnType)
    {
        if (sources.Length > 1 || joins.Length != 0)
            return null;

        bool ResolveNullable(MultiPartName name)
        {
            var (s, c) = FindSourceColumn(sources, name);
            return s == -1 || sources[s].Columns[c].Nullable;
        }

        var context = new NullabilityContext(parseBatch, ResolveNullable, resolveColumnType);
        var nullability = new bool[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            nullability[i] = expressions[i].ResultIsNullable(context);
        return nullability;
    }

    /// <summary>
    /// The FOR XML AUTO / FOR JSON AUTO element name of each FROM source: the
    /// written alias-or-object-name when the source carries one, else its
    /// column-resolution qualifier.
    /// </summary>
    private static string?[] AutoSourceNamesOf(FromSource[] sources)
    {
        var names = new string?[sources.Length];
        for (var i = 0; i < sources.Length; i++)
            names[i] = sources[i].AutoElementName ?? sources[i].Qualifier;
        return names;
    }

    /// <summary>
    /// Binds each projection column to the FROM source and source column it
    /// reads, for the AUTO serializers' nesting levels (and FOR XML AUTO's
    /// binary <c>dbobject</c> addressing, which needs the base column). Only a
    /// bare column reference (through any number of <c>AS alias</c> wrappers)
    /// binds; every other expression — including a CAST or function call over a
    /// column — is SQL Server's "computed column" and reports -1 in both slots.
    /// </summary>
    private static (int[] Source, int[] Ordinal) AutoColumnBindingOf(List<Expression> expressions, FromSource[] sources)
    {
        var source = new int[expressions.Count];
        var ordinal = new int[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
        {
            if (UnwrapDirectRef(expressions[i]) is Reference reference)
            {
                (source[i], ordinal[i]) = FindSourceColumn(sources, reference.ReferencedName);
            }
            else
            {
                source[i] = -1;
                ordinal[i] = -1;
            }
        }
        return (source, ordinal);
    }

    /// <summary>
    /// The AUTO column binding of a projection with no FROM sources to bind to:
    /// every column reports SQL Server's "computed column" sentinel.
    /// </summary>
    internal static int[] NoSourceColumnBinding(int columnCount)
    {
        var map = new int[columnCount];
        Array.Fill(map, -1);
        return map;
    }

    /// <summary>
    /// Per-projection-column decimal-vs-numeric reported type name for
    /// result-set metadata. A column reports <c>numeric</c> only when its
    /// result is <c>decimal</c>-family AND the expression carries a
    /// numeric-named source (see <see cref="Expression.ResultReportsNumeric"/>);
    /// returns null when no column qualifies (the common case), so most plans
    /// carry no extra array. The two names share one <see cref="SqlType"/>, so
    /// this stays projection-time metadata and never influences storage.
    /// </summary>
    private static bool[]? ColumnReportsNumericOf(List<Expression> expressions, SqlType[] schema)
    {
        bool[]? reportsNumeric = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (schema[i] is DecimalSqlType && expressions[i].ResultReportsNumeric)
                (reportsNumeric ??= new bool[expressions.Count])[i] = true;
        }
        return reportsNumeric;
    }

    /// <summary>
    /// Decides whether the FROM-bearing SELECT's shape is eligible to back
    /// view DML and, if so, captures the source / projection / WHERE state.
    /// Eligible shapes — see <see cref="ViewUpdatabilityProfile"/> — also
    /// accept <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> / <c>ORDER BY</c>
    /// (these only affect reads) and a multi-source FROM, which
    /// <see cref="Simulation.AnalyzeViewUpdatability"/> then splits off as
    /// the join-updatable shape. Set-op chains are caught one level up in
    /// <see cref="CombineSetOps"/> which discards the profile by
    /// constructing a fresh Selection without one.
    /// </summary>
    private static (ViewUpdatabilityProfile?, ViewUpdatabilityRejection) ComputeViewUpdatabilityProfile(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows)
    {
        if (distinct)
            return (null, ViewUpdatabilityRejection.Distinct);
        if (aggregates.Count > 0)
            return (null, ViewUpdatabilityRejection.Aggregate);
        if (fromClause.GroupingSets.Count > 0 || fromClause.Having is not null)
            return (null, ViewUpdatabilityRejection.GroupBy);
        if (windows.Count > 0)
            return (null, ViewUpdatabilityRejection.UnsupportedShape);

        var profile = new ViewUpdatabilityProfile(
            sources: sources,
            joins: joins,
            projections: [.. expressions],
            excluders: [.. fromClause.Excluders]);
        return (profile, ViewUpdatabilityRejection.None);
    }

    /// <summary>
    /// Captures the FROM shape a KEYSET / DYNAMIC cursor may navigate, or null
    /// when the SELECT must fall back to a STATIC snapshot. Probe-confirmed
    /// against SQL Server 2025: a JOIN (any arity / kind), a comma FROM, a
    /// self-join, a derived table, a CTE, a view and an APPLY all stay DYNAMIC
    /// there, while DISTINCT / GROUP BY / aggregates / set ops convert to a
    /// read-only snapshot, so the statement-level gates below mirror that
    /// split. A <c>TOP</c> / <c>OFFSET</c> / <c>FETCH</c> row limit stays
    /// navigable and rides along as <see cref="CursorShape.RowLimit"/>: real
    /// converts such a cursor to KEYSET, whose membership the limit picks at
    /// OPEN. Whether each individual source bottoms out in base tables — the
    /// stable <c>(page, slot)</c> addresses cursor identity rides on — is
    /// settled later by <see cref="TryBuildCursorPlan"/>, which is where a view
    /// body gets parsed; this pass only rejects the shapes no source set can
    /// rescue.
    /// </summary>
    private static CursorShape? ComputeCursorShape(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        CursorRowLimit? rowLimit)
    {
        if (distinct || aggregates.Count > 0 || windows.Count > 0
            || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null
            || sources.Length == 0)
        {
            return null;
        }

        // A parenthesized join group spans several slots per level, which the
        // flat left-deep cursor fold doesn't model.
        foreach (var join in joins)
        {
            if (join.GroupCount != 1
                || join.Kind is not (JoinKind.Inner or JoinKind.Cross or JoinKind.Left or JoinKind.Right
                    or JoinKind.Full or JoinKind.CrossApply or JoinKind.OuterApply))
            {
                return null;
            }
        }

        return new CursorShape(sources, joins, [.. expressions], [.. fromClause.Excluders], rowLimit);
    }

    /// <summary>
    /// Replaces every <see cref="FromSource.MaterializeOnce"/> source — a
    /// provably-uncorrelated catalog view — with a copy whose rows are already
    /// materialized into a re-enumerable list, executing each such plan exactly
    /// once per query execution. Without this, a nested-loop join re-runs the
    /// catalog view's row generator (regenerating every column of every table,
    /// every type, etc.) for each outer row, so an <em>N</em>-column table's
    /// per-column property-bag query costs O(outer × Σ joined-view sizes). After
    /// materialization the source carries a plain <see cref="FromSource.Rows"/>
    /// list, so <c>TryPlanEquiJoin</c> keys it into the O(L + R) hash path and
    /// any residual nested loop re-scans the list instead of re-generating.
    /// Correlated / lateral sources never set the flag, so APPLY, correlated
    /// derived tables, VALUES, TVFs, and views keep their per-outer-row
    /// execution untouched. Returns the input array unchanged (no copy) when no
    /// source qualifies.
    /// </summary>
    private static FromSource[] MaterializeUncorrelatedDeferredSources(FromSource[] sources, BatchContext batch)
    {
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i] is not { MaterializeOnce: true, LateralPlan: { } plan })
                continue;
            // Uncorrelated by construction: the generator ignores the outer
            // resolver, so a null resolver produces identical rows.
            var materialized = new List<byte[]>(plan.Execute(batch, outerResolver: null).RowBytes);
            rewritten ??= (FromSource[])sources.Clone();
            rewritten[i] = sources[i].WithMaterializedRows(materialized);
        }
        return rewritten ?? sources;
    }

    private static IEnumerable<SqlValue[]> ProjectSqlRows(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        // ORDER BY elimination: when the sort matches a NOT-NULL leading-key
        // column, enumerate the source in key order and stream (no buffer + sort).
        // Residual WHERE and projection preserve order; OFFSET / FETCH / TOP then
        // read only the rows they need. TOP PERCENT / WITH TIES need the full
        // buffered rowcount / ORDER BY keys, so they skip the streaming paths.
        var hasJoinGroup = ContainsJoinGroup(joins);
        if (!hasJoinGroup && !distinct && !top.RequiresBuffering && orderBy.Count > 0
            && TryApplyOrderedScan(sources, joins, orderBy, excluders, batch, outerResolver, out var orderedSources))
        {
            return ProjectStreaming(orderedSources, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver);
        }

        if (!hasJoinGroup)
            sources = MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver);
        sources = NarrowLeftmostJoinSource(sources, excluders, batch, outerResolver);
        return !distinct && orderBy.Count == 0 && !top.RequiresBuffering
            ? ProjectStreaming(sources, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver)
            : ProjectBuffered(sources, joins, expressions, excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, batch, outerResolver);
    }

    /// <summary>
    /// Applies OFFSET (skip) and the row cap (take) to a row sequence in
    /// that order. The cap is whichever of TOP / FETCH is in play —
    /// they're mutually exclusive at parse time (Msg 10741), so callers
    /// pass <c>topCount ?? fetchCount</c> here.
    /// </summary>
    private static IEnumerable<T> ApplyOffsetTake<T>(IEnumerable<T> rows, int? offsetCount, int? topOrFetch)
    {
        if (offsetCount is { } offset && offset > 0)
            rows = rows.Skip(offset);
        if (topOrFetch is { } limit)
            rows = rows.Take(limit);
        return rows;
    }

    private static IEnumerable<SqlValue[]> ProjectStreaming(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        int? topCount,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        return ApplyOffsetTake(InnerStream(), offsetCount, topCount ?? fetchCount);

        IEnumerable<SqlValue[]> InnerStream()
        {
            // Hoisted per-row resolution scaffolding: one mutable-capture
            // tuple slot, one cached self-referencing resolver lambda, one
            // RuntimeContext — instead of a fresh closure + several delegates
            // per row (the allocation profile's dominant entry).
            var memo = new SourceColumnMemo();
            var currentTuple = default(byte[]?[])!;
            Func<MultiPartName, SqlValue> resolveColumn = null!;
            resolveColumn = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, resolveColumn, memo);
            var rowRuntime = new RuntimeContext(resolveColumn, batch);
            foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
            {
                currentTuple = tuple;
                var include = true;
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(rowRuntime) != true)
                    {
                        include = false;
                        break;
                    }
                }
                if (!include)
                    continue;

                // Per-row stamp bump so NEXT VALUE FOR in the projection
                // advances per output row (and dedupes across same-row
                // instances). Bump only on rows that pass WHERE — excluded
                // rows shouldn't burn sequence values.
                batch.BumpRowStamp();
                var projected = new SqlValue[expressions.Count];
                for (var i = 0; i < expressions.Count; i++)
                    projected[i] = expressions[i].Run(rowRuntime);

                yield return projected;
            }
        }
    }

    private static IEnumerable<SqlValue[]> ProjectBuffered(
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        string[] outputColumnNames,
        List<OrderBySpec> orderBy,
        bool distinct,
        TopSpec top,
        int? offsetCount,
        int? fetchCount,
        BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var buffer = new List<(SqlValue[] Projected, SqlValue[] Keys)>();

        // Hoisted per-row resolution scaffolding — see InnerStream above.
        var memo = new SourceColumnMemo();
        var currentTuple = default(byte[]?[])!;
        Func<MultiPartName, SqlValue> resolveSource = null!;
        resolveSource = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, resolveSource, memo);
        var rowRuntime = new RuntimeContext(resolveSource, batch);

        // Computed once: the column each projection reads, for the DISTINCT
        // ORDER BY check (a term may name the source column behind an alias).
        var projectionSources = ProjectionSourceReferences(expressions);
        foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
        {
            currentTuple = tuple;
            var include = true;
            foreach (var excluder in excluders)
            {
                if (excluder.Run(rowRuntime) != true)
                {
                    include = false;
                    break;
                }
            }
            if (!include)
                continue;

            // Per-row stamp bump — same rule as the streaming path: only
            // for rows that pass WHERE.
            batch.BumpRowStamp();
            var projected = new SqlValue[expressions.Count];
            for (var i = 0; i < expressions.Count; i++)
                projected[i] = expressions[i].Run(rowRuntime);

            var keys = orderBy.Count == 0 ? [] : ComputeOrderKeys(orderBy, projected, outputColumnNames, projectionSources, distinct, batch, resolveSource);
            buffer.Add((projected, keys));
        }

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> filtered = buffer;
        if (distinct)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            filtered = buffer.Where(item => seen.Add(item.Projected));
        }

        var materialized = filtered.ToList();

        if (orderBy.Count > 0)
            materialized.Sort((a, b) => CompareOrderKeys(a.Keys, b.Keys, orderBy));

        var cap = ComputeTopCap(materialized, item => item.Keys, orderBy, top, fetchCount);

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> windowed = materialized;
        if (offsetCount is { } offset && offset > 0)
            windowed = windowed.Skip(offset);
        if (cap is { } limit)
            windowed = windowed.Take(limit);

        foreach (var (projected, _) in windowed)
            yield return projected;
    }
}
