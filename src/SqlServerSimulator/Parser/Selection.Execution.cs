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
        var found = FindSourceColumnOfAnyKind(sources, name);
        // A graph table's hidden internal columns are there to be read only by
        // the pseudo-columns and MATCH; naming one in a query is Msg 13908
        // (probed 2026-09-27 against SQL Server 2025).
        return name.FromText && found.SourceIndex >= 0
            && sources[found.SourceIndex].Columns[found.ColumnIndex] is { IsHidden: true, GraphKind: not GraphColumnKind.None } internalColumn
            ? throw SimulatedSqlException.InternalGraphColumnAccess(internalColumn.Name, 1)
            : found;
    }

    private static (int SourceIndex, int ColumnIndex) FindSourceColumnOfAnyKind(FromSource[] sources, MultiPartName name)
    {
        if (name.ImmediateQualifier is { } qualifier)
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (sources[s].Qualifier is null || !BuiltInToken.Equals(sources[s].Qualifier, qualifier))
                    continue;
                if (name.Count >= 3 && !sources[s].AnswersPrefix(name, name.Count - 1))
                    return (-1, -1);
                for (var c = 0; c < sources[s].ColumnNames.Length; c++)
                {
                    if (BuiltInToken.Equals(sources[s].ColumnNames[c], name.Leaf))
                        return (s, c);
                }
                // Qualifier matched but the column doesn't exist in that
                // source; fall through to outer (caller handles) — unless the
                // name is a graph pseudo-column the source carries.
                return !name.LeafDelimited && name.Leaf.StartsWith('$') && GraphColumns.FindPseudoColumn(sources[s].ColumnNames, name.Leaf) is var pseudo and >= 0
                    ? (s, pseudo)
                    : (-1, -1);
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
        if (matches == 0 && !name.LeafDelimited && name.Leaf.StartsWith('$'))
        {
            for (var s = 0; s < sources.Length; s++)
            {
                if (GraphColumns.FindPseudoColumn(sources[s].ColumnNames, name.Leaf) is var pseudo and >= 0)
                {
                    if (matches++ == 0)
                        (foundSource, foundColumn) = (s, pseudo);
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
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        List<Expression> expressions)
    {
        if (parseBatch.Parser.IndexedViewShapeCollector is not { } shape)
            return;

        shape.HasDistinct |= distinct;
        shape.HasHaving |= fromClause.Having is not null;
        shape.HasGroupingSets |= fromClause.GroupingSets.Count > 1 || fromClause.GroupingSetsWritten;
        shape.HasWindow |= windows.Count > 0;
        foreach (var source in sources)
        {
            if (source.DerivedTable is { Correlated: false } derived)
                shape.DerivedTableAlias ??= derived.Alias;
            if (source.BackingView is { } referenced)
                shape.ReferencedView ??= $"{referenced.Schema.Name}.{referenced.Name}";
        }
        var typeOf = ColumnTypeResolverFor(sources);
        // A float or real column a WHERE or GROUP BY reads is Msg 1962.
        void NoteImpreciseColumn(Expression expression)
        {
            if (shape.ImpreciseFilterColumn is null && expression is Reference reference)
            {
                try
                {
                    if (typeOf(reference.ReferencedName) is FloatSqlType or RealSqlType)
                        shape.ImpreciseFilterColumn = reference.ReferencedName.Leaf;
                }
                catch (SimulatedSqlException)
                {
                }
            }
        }
        foreach (var excluder in fromClause.Excluders)
            VisitAll(excluder, NoteImpreciseColumn);
        foreach (var grouping in fromClause.AllGroupingExpressions)
            VisitAll(grouping, NoteImpreciseColumn);
        // A grouped view projects each GROUP BY expression (Msg 8660), and a
        // projection over an aggregate's result is Msg 8668.
        if (fromClause.AllGroupingExpressions.Count > 0)
        {
            foreach (var grouping in fromClause.AllGroupingExpressions)
            {
                var display = grouping.DebugDisplay();
                if (!expressions.Exists(projected => Unaliased(projected).DebugDisplay() == display))
                    shape.GroupingExpressionNotProjected = true;
            }
        }
        foreach (var projected in expressions)
        {
            var inner = Unaliased(projected);
            if (inner is AggregateExpression)
                continue;
            VisitAll(inner, expression => shape.ExpressionOverAggregate |= expression is AggregateExpression);
        }
        shape.HasTopOrOffset |= topExpression is not null
            || fromClause.OffsetExpression is not null
            || fromClause.FetchExpression is not null;
        shape.HasGroupBy |= fromClause.GroupingSets.Count > 0;

        foreach (var join in joins)
        {
            shape.HasApply |= join.Kind is JoinKind.CrossApply or JoinKind.OuterApply;
            shape.HasOuterJoin |= join.Kind is JoinKind.Left or JoinKind.Right or JoinKind.Full;
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
                && SumOperandIsNullable(operand, operandNullability))
            {
                shape.SumsNullableExpression = true;
            }
        }

        static Expression Unaliased(Expression expression) => expression is NamedExpression named ? named.Inner : expression;
    }

    /// <summary>
    /// Whether a <c>SUM</c>'s operand can be NULL for Msg 8662: arithmetic over
    /// operands none of which can is not, which real accepts (<c>SUM(v * 2)</c>
    /// over a <c>NOT NULL</c> column, probed 2026-10-04 against SQL Server
    /// 2025); anything else answers as its result metadata does.
    /// </summary>
    private static bool SumOperandIsNullable(Expression operand, NullabilityContext nullability)
    {
        if (operand is not TwoSidedExpression)
            return operand.ResultIsNullable(nullability);
        var shape = new NodeShape();
        operand.Describe(shape);
        foreach (var side in shape.ChildNodes)
        {
            if (side is Expression sideExpression && SumOperandIsNullable(sideExpression, nullability))
                return true;
        }
        return false;
    }

    /// <summary>Calls <paramref name="visitor"/> on every expression at or under <paramref name="node"/>.</summary>
    private static void VisitAll(ExpressionNode node, Action<Expression> visitor) =>
        node.Walk((visited, _) =>
        {
            if (visited is Expression expression)
                visitor(expression);
            return true;
        });

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
    /// How a <c>SELECT DISTINCT</c> query's ORDER BY term appears in its select
    /// list: 0 for a bare name the per-row resolver reads off the projection
    /// (an output alias, or the source column behind a projected one), the
    /// 1-based position of the select item an expression term matches, or -1
    /// when it appears nowhere — Msg 145 while compiling.
    /// An expression term matches an item by shape, the structural match a
    /// GROUP BY expression makes (columns by the source column they resolve
    /// to, parentheses transparent, a subquery only itself), never by the
    /// columns it reads: real accepts <c>ORDER BY id + 1</c> over
    /// <c>SELECT DISTINCT p.id + 1</c> and <c>ORDER BY COUNT(*)</c> over a
    /// projected <c>COUNT(*)</c>, and refuses <c>ORDER BY n + 0</c>,
    /// <c>ORDER BY 1 + id</c>, <c>ORDER BY n + 1.0</c>, a <c>CASE</c> or
    /// <c>COLLATE</c> over a projected column and a subquery written twice,
    /// even over projected columns alone (probed 2026-09-30 against SQL Server
    /// 2025).
    /// </summary>
    private static int DistinctOrderTermOrdinal(FromSource[] sources, List<Expression> expressions, string[] outputColumnNames, OrderBySpec term)
    {
        var peeled = term.Expr!;
        while (peeled is Parenthesized paren)
            peeled = paren.Wrapped;

        if (peeled is Reference reference)
        {
            var name = reference.ReferencedName;
            if (term.MayNameAlias && name.ImmediateQualifier is null && Array.Exists(outputColumnNames, output => BuiltInToken.Equals(output, name.Leaf)))
                return 0;
            if (ProjectionSourceReferences(expressions) is { } projected && Array.Exists(projected, source => source is { } column && SourceReferenceMatches(column, name)))
                return 0;
        }

        var key = GroupingKey(sources, peeled);
        for (var i = 0; i < expressions.Count; i++)
        {
            var projection = expressions[i] is NamedExpression named ? named.Inner : expressions[i];
            while (projection is Parenthesized wrapped)
                projection = wrapped.Wrapped;
            if (projection is not Reference && key.Equals(GroupingKey(sources, projection)))
                return i + 1;
        }

        return -1;
    }

    /// <summary>
    /// The predicates and sort terms this query binds beside its projections —
    /// WHERE, each join's ON, HAVING and ORDER BY — kept so an enclosing grouped
    /// query can judge the references they make to its own columns (see
    /// <see cref="VisitCorrelatedReferences"/>). Null on plan shapes that don't
    /// record them, which that check then reads as having none.
    /// </summary>
    internal ExpressionNode[]? ClauseExpressions;

    private static ExpressionNode[] ClauseExpressionsOf(FromClause fromClause, JoinSpec[] joins, List<OrderBySpec> orderBy)
    {
        var clauses = new List<ExpressionNode>(fromClause.Excluders);
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                clauses.Add(on);
        }
        if (fromClause.Having is { } having)
            clauses.Add(having);
        foreach (var item in orderBy)
        {
            if (item.Expr is { } term)
                clauses.Add(term);
        }
        return [.. clauses];
    }

    /// <summary>
    /// The subqueries a predicate carries at its own level — an <c>EXISTS</c>,
    /// an <c>IN</c> or a quantified comparison — rather than inside one of its
    /// value operands, which a walk of those operands reaches.
    /// </summary>
    private static List<Selection> PredicateSubqueries(BooleanExpression predicate)
    {
        var subqueries = new List<Selection>();
        predicate.Walk((node, shape) =>
        {
            if (node is Expression)
                return false;
            foreach (var local in shape.Locals)
            {
                if (local is Selection inner)
                    subqueries.Add(inner);
            }
            return true;
        });
        return subqueries;
    }

    /// <summary>
    /// Hands <paramref name="onReference"/> every column reference
    /// <paramref name="inner"/> — a subquery, its derived tables and the
    /// subqueries nested in either — makes outside an aggregate to a name none
    /// of those scopes binds, which is a reference to an enclosing query.
    /// A grouped query holds such a reference to the containment rule its own
    /// select list keeps: <c>SELECT p.id, (SELECT COUNT(*) FROM b AS u WHERE
    /// u.pid = b.pid) … GROUP BY p.id</c> is Msg 8120 on <c>b.pid</c>, in the
    /// subquery's select list, WHERE, ON or a derived table alike, and a
    /// subquery in HAVING or ORDER BY reports Msg 8121 / 8127, while an
    /// aggregate over the outer column alone is the outer query's own
    /// (<c>(SELECT MAX(b.v) FROM p)</c> runs) and a grouping expression still
    /// covers its match (<c>(SELECT b.v + 1)</c> under <c>GROUP BY b.v + 1</c>
    /// runs) — probed 2026-09-30 against SQL Server 2025.
    /// <paramref name="covers"/> answers that last match, asked at each node.
    /// </summary>
    private static void VisitCorrelatedReferences(
        Selection inner,
        Func<MultiPartName, bool> boundWithin,
        Func<ExpressionNode, bool> covers,
        Action<ExpressionNode, MultiPartName> onReference)
    {
        if (inner.BranchFromSources is not { } sources)
            return;

        bool Bound(MultiPartName name) => TryResolveSourceColumn(sources, name) is not null || boundWithin(name);

        void Visit(ExpressionNode root) => root.Walk((node, shape) =>
        {
            if (node is AggregateExpression or WindowExpression || covers(node))
                return false;
            foreach (var local in shape.Locals)
            {
                if (local is Selection nested)
                    VisitCorrelatedReferences(nested, Bound, covers, onReference);
            }
            if (shape.Column is { } name && !Bound(name))
                onReference(node, name);
            return true;
        });

        foreach (var projection in inner.ProjectionExpressions ?? [])
            Visit(projection);
        foreach (var clause in inner.ClauseExpressions ?? [])
            Visit(clause);
        foreach (var source in sources)
        {
            if (source.LateralPlan is { } derived)
                VisitCorrelatedReferences(derived, Bound, covers, onReference);
        }
    }

    /// <summary>
    /// Enforces the GROUP BY containment rule (Msg 8120 / 8121 / 8127): outside
    /// an aggregate, a column reference must resolve to a bare GROUP BY column.
    /// <see cref="Expression.VisitColumnReferences(Action{MultiPartName})"/> already skips
    /// aggregate-internal columns (an <see cref="AggregateExpression"/> doesn't
    /// visit its operand), so it yields exactly the bare, non-aggregated
    /// references. A reference that doesn't resolve against these sources
    /// (correlated / outer) is not this query's grouping concern.
    /// <para>
    /// A GROUP BY <em>expression</em> covers a projection sub-expression that
    /// matches it, not the columns it happens to name: real licenses
    /// <c>SELECT a + 1</c>, <c>SELECT (a + 1) * 2</c> and
    /// <c>SELECT YEAR(d) * 100 + MONTH(d)</c> against the matching clauses and
    /// refuses <c>SELECT a</c>, <c>SELECT a + 0</c> and <c>SELECT 1 + a</c>
    /// against <c>GROUP BY a + 1</c> — the match is structural, not algebraic
    /// (probed 2026-08-05). The walk is stopped at a matching node by
    /// <see cref="ColumnReferenceVisitor.CoversSubtree"/>, so every reference
    /// that reaches <c>Check</c> is one no grouping expression covered.
    /// </para>
    /// <para>
    /// In a grouped query — a <c>GROUP BY</c>, <c>GROUP BY ()</c> included, or a
    /// <c>HAVING</c> — real checks the tree it folded, so
    /// <paramref name="folds"/> is non-null there and a CASE-family arm the
    /// fold removes (<see cref="Expression.AddFoldedAwayOperands"/>) goes
    /// unchecked: <c>SELECT COALESCE(b, 5, a) … GROUP BY b</c> runs. A bare
    /// scalar aggregate folds nothing first, so <c>SELECT COALESCE(5, a),
    /// COUNT(*)</c> is Msg 8120 (probed 2026-09-24 against SQL Server 2025).
    /// </para>
    /// </summary>
    private static void ValidateGroupByReferences(FromSource[] sources, List<Expression> expressions, List<OrderBySpec> orderBy, string[] outputColumnNames, FromClause fromClause, List<WindowExpression> windows, NullabilityContext? folds, BindErrorReport? report)
    {
        var groupedBare = new HashSet<(int Source, int Column)>();
        var groupingKeys = new HashSet<ShapeKey>();
        foreach (var grouping in fromClause.AllGroupingExpressions)
        {
            // Parentheses are not part of the grouping key: `GROUP BY (region)`
            // groups by the column, not by a compound expression.
            var peeled = grouping;
            while (peeled is Parenthesized paren)
                peeled = paren.Wrapped;

            if (peeled is Reference bare)
            {
                if (TryResolveSourceColumn(sources, bare.ReferencedName) is { } id)
                    _ = groupedBare.Add(id);
            }
            else
            {
                _ = groupingKeys.Add(GroupingKey(sources, peeled));
            }
        }

        // A node's folded-away operands are recorded as the walk reaches the
        // node, which is before it reaches them.
        var foldedAway = new HashSet<ExpressionNode>(ReferenceEqualityComparer.Instance);

        // GROUPING() / GROUPING_ID() answer their own mismatch (Msg 8161), so
        // their arguments aren't this check's to report.
        bool coversSubtree(ExpressionNode node)
        {
            if (foldedAway.Contains(node))
                return true;
            if (node is not Expression expression)
                return false;
            if (folds is { } context)
                expression.AddFoldedAwayOperands(context, foldedAway);
            return expression switch
            {
                Grouping or GroupingId => true,
                NullIf { FoldedBeforeGroupingCheck: true } => true,
                Reference => false,
                _ => groupingKeys.Count != 0 && groupingKeys.Contains(GroupingKey(sources, expression)),
            };
        }

        void Check(MultiPartName name, Func<string, SimulatedSqlException> error)
        {
            if (TryResolveSourceColumn(sources, name) is not { } id || groupedBare.Contains(id))
                return;

            // Real names the object the FROM clause wrote — the alias is not
            // it, so `SELECT a FROM g1 AS x GROUP BY b` reports 'g1.a' — and
            // falls back to the alias where the source has no object of its own
            // (a derived table reports 'z.a', a CTE 'c.a'; probed 2026-08-05).
            var source = sources[id.Source];
            var column = source.ColumnNames[id.Column];
            var qualifier = source.WrittenObjectName ?? source.Qualifier;
            throw error(qualifier is null ? column : $"{qualifier}.{column}");
        }

        // Inside a subquery only a grouping expression's match covers: the
        // folds above type their operands against this query's sources alone.
        bool coversCorrelated(ExpressionNode node) =>
            node is Expression expression && groupingKeys.Count != 0 && groupingKeys.Contains(GroupingKey(sources, expression));

        Action<Selection> SubqueryCheck(Func<string, SimulatedSqlException> error) =>
            inner => VisitCorrelatedReferences(inner, static _ => false, coversCorrelated, (_, name) => Check(name, error));

        ColumnReferenceVisitor VisitorFor(Func<string, SimulatedSqlException> error) =>
            new(name => Check(name, error), coversSubtree, SubqueryCheck(error));

        if (report is not null)
        {
            RecordGroupingViolations(report, sources, expressions, orderBy, outputColumnNames, fromClause, windows, groupedBare, coversSubtree, coversCorrelated);
            return;
        }

        var selectVisitor = VisitorFor(SimulatedSqlException.ColumnNotInGroupByForSelect);
        foreach (var expression in expressions)
            expression.VisitColumnReferences(selectVisitor);

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
            void CheckWindowOperand(Expression? operand) => operand?.VisitColumnReferences(selectVisitor);

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
        var havingVisitor = VisitorFor(SimulatedSqlException.ColumnNotInGroupByForHaving);
        fromClause.Having?.VisitSurvivingOperandExpressions(op => op.VisitColumnReferences(havingVisitor));
        if (fromClause.Having is { } having)
        {
            foreach (var inner in PredicateSubqueries(having))
                havingVisitor.OnSubquery!(inner);
        }

        var orderByVisitor = new ColumnReferenceVisitor(
            name =>
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
            },
            coversSubtree,
            SubqueryCheck(SimulatedSqlException.ColumnNotInGroupByForOrderBy));
        foreach (var item in orderBy)
            item.Expr?.VisitColumnReferences(orderByVisitor);
    }

    /// <summary>
    /// <see cref="ValidateGroupByReferences"/> for a statement re-read for its
    /// whole bind error report: every violation is recorded rather than the
    /// first thrown, judged one expression at a time — the HAVING clause, each
    /// select-list item and window, each ORDER BY item — the way real does.
    /// An expression whose walk meets an unbindable name before any violation
    /// reports none, while one that found its violation first reports every
    /// one it holds, the unbindable names in it notwithstanding (probed
    /// 2026-09-27: <c>HAVING b &gt; 1 AND x1 = 1 AND c = 'x'</c> sends Msg 207
    /// then Msg 8121 for both <c>b</c> and <c>c</c>, <c>HAVING x1 &gt; 1 AND
    /// c = 'x'</c> the Msg 207 alone).
    /// </summary>
    private static void RecordGroupingViolations(
        BindErrorReport report,
        FromSource[] sources,
        List<Expression> expressions,
        List<OrderBySpec> orderBy,
        string[] outputColumnNames,
        FromClause fromClause,
        List<WindowExpression> windows,
        HashSet<(int Source, int Column)> groupedBare,
        Func<ExpressionNode, bool> coversSubtree,
        Func<ExpressionNode, bool> coversCorrelated)
    {
        void Judge(List<Expression> roots, Func<string, SimulatedSqlException> error, bool orderByTerm, List<Selection>? predicateSubqueries = null)
        {
            var references = new List<(Reference? Node, MultiPartName Name)>();
            void AddCorrelated(Selection inner) =>
                VisitCorrelatedReferences(inner, static _ => false, coversCorrelated, (node, name) => references.Add((node as Reference, name)));
            foreach (var root in roots)
            {
                root.Walk((node, shape) =>
                {
                    if (node is AggregateExpression or WindowExpression || coversSubtree(node))
                        return false;
                    foreach (var local in shape.Locals)
                    {
                        if (local is Selection inner)
                            AddCorrelated(inner);
                    }
                    if (shape.Column is { } name)
                        references.Add((node as Reference, name));
                    return true;
                });
            }
            predicateSubqueries?.ForEach(AddCorrelated);

            var start = int.MaxValue;
            var end = -1;
            foreach (var (node, _) in references)
            {
                if (report.Covers(node?.SourceToken))
                {
                    start = Math.Min(start, node!.SourceToken!.StartIndex);
                    end = Math.Max(end, node.SourceToken.StartIndex);
                }
            }
            if (end < 0)
                return;

            var violations = 0;
            foreach (var (node, name) in references)
            {
                var position = report.Covers(node?.SourceToken) ? node!.SourceToken!.StartIndex : -1;
                if (position >= 0 && report.FailedAt(position))
                {
                    if (violations == 0)
                        return;
                    continue;
                }
                if (orderByTerm && name.ImmediateQualifier is null && Array.Exists(outputColumnNames, output => BuiltInToken.Equals(output, name.Leaf)))
                    continue;
                if (TryResolveSourceColumn(sources, name) is not { } id || groupedBare.Contains(id))
                    continue;
                var source = sources[id.Source];
                var column = source.ColumnNames[id.Column];
                var qualifier = source.WrittenObjectName ?? source.Qualifier;
                violations++;
                report.RecordGroupingViolation(error(qualifier is null ? column : $"{qualifier}.{column}"), position < 0 ? end : position, start, end);
            }
        }

        if (fromClause.Having is { } having)
        {
            var operands = new List<Expression>();
            having.VisitSurvivingOperandExpressions(operands.Add);
            Judge(operands, SimulatedSqlException.ColumnNotInGroupByForHaving, orderByTerm: false, PredicateSubqueries(having));
        }
        foreach (var expression in expressions)
            Judge([expression], SimulatedSqlException.ColumnNotInGroupByForSelect, orderByTerm: false);
        foreach (var window in windows)
        {
            List<Expression> parts = [];
            foreach (var part in (Expression?[])[window.Operand, window.AggregateInfo?.Operand, window.DefaultArg])
            {
                if (part is not null)
                    parts.Add(part);
            }
            parts.AddRange(window.PartitionBy);
            foreach (var item in window.OrderBy)
            {
                if (item.Expr is { } term)
                    parts.Add(term);
            }
            Judge(parts, SimulatedSqlException.ColumnNotInGroupByForSelect, orderByTerm: false);
        }
        foreach (var item in orderBy)
        {
            if (item.Expr is { } term)
                Judge([term], SimulatedSqlException.ColumnNotInGroupByForOrderBy, orderByTerm: true);
        }
    }

    /// <summary>
    /// The structural identity a GROUP BY expression is matched on: its
    /// <see cref="ShapeKey"/>, with each column keyed by the source column it
    /// resolves to rather than by its spelling, so a projection and a grouping
    /// clause that qualify the same column differently still match
    /// (<c>SELECT a + 1 FROM t AS x GROUP BY x.a + 1</c> runs on real) while the
    /// same name read from two sides of a join does not. A name no source
    /// answers (an enclosing query's column) is keyed by its spelling.
    /// Parentheses are transparent to the match, so <c>GROUP BY (a + 1)</c>
    /// covers <c>SELECT a + 1</c> and the reverse.
    /// </summary>
    private static ShapeKey GroupingKey(FromSource[] sources, Expression expression) =>
        ShapeKey.Of(expression, name => TryResolveSourceColumn(sources, name) is { } id ? id : name.ToString());

    /// <summary>
    /// Best-effort local resolution for GROUP BY validation: the resolved
    /// (source, column) pair, or null when the name is correlated / outer /
    /// unresolved (or ambiguous — which would already have failed type
    /// resolution). Never raises: recording a diagnostic must not itself throw.
    /// </summary>
    internal static (int Source, int Column)? TryResolveSourceColumn(FromSource[] sources, MultiPartName name)
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
    /// A column-type resolver over <paramref name="sources"/> falling back to
    /// <paramref name="outerTypeResolver"/>. Built here rather than as a
    /// closure at the call site so it captures nothing else from there: a
    /// resolver can end up held by a cached plan, which must not reach the
    /// parsing batch.
    /// </summary>
    private static Func<MultiPartName, SqlType> TypeResolverOver(FromSource[] sources, Func<MultiPartName, SqlType>? outerTypeResolver) =>
        name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

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
            if (sources[s].BackingTable is { RefusesLegacyLobReads: true } && sources[s].Columns[c].Type.IsLegacyLob)
                throw SimulatedSqlException.LegacyLobColumnInPseudoTable();
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
        if (AnyPlaceholderSource(sources))
            return SqlType.Int32;
        if (outerTypeResolver is null)
            throw UnresolvedNameError(sources, name);
        if (name.ImmediateQualifier is null || !QualifiesAnySource(sources, name))
            return outerTypeResolver(name);
        try
        {
            return outerTypeResolver(name);
        }
        catch (SimulatedSqlException ex) when (ex.Number == MultiPartIdentifierNotBoundNumber)
        {
            // This scope exposes the qualifier, so the name is a known source's
            // unknown column rather than an unbindable multi-part identifier —
            // real splits the two errors exactly there. The outer scopes that
            // couldn't answer classified it 4104 without knowing about this one.
            throw SimulatedSqlException.InvalidColumnName(name);
        }
    }

    /// <summary>Msg 4104's number, so <see cref="ResolveColumnTypeAcrossSources"/>'s downgrade reads as what it matches.</summary>
    private const int MultiPartIdentifierNotBoundNumber = 4104;

    /// <summary>
    /// The error for a name no scope could bind. Real splits by <em>what</em>
    /// failed rather than where: a <b>qualified</b> name whose qualifier names
    /// no source in scope is <b>Msg 4104</b> on the whole written identifier
    /// (<c>SELECT zz.id FROM t</c>, and every sibling-scope shape — a derived
    /// table naming a source beside it, a generator's scalar-subquery argument
    /// doing the same), while a known qualifier's missing column
    /// (<c>SELECT t.nosuch FROM t</c>) and any unqualified miss stay
    /// <b>Msg 207</b> on the leaf. Probed against SQL Server 2025 (2026-08-05)
    /// across the select list, WHERE, GROUP BY, ORDER BY, an ON predicate, a
    /// scalar / IN subquery, a derived table, a FROM-less SELECT, a generator's
    /// arguments and an <c>INSERT … SELECT</c>.
    /// </summary>
    private static SimulatedSqlException UnresolvedNameError(FromSource[] sources, MultiPartName name) =>
        name.ImmediateQualifier is not null && !QualifiesAnySource(sources, name)
            ? SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString())
            : SimulatedSqlException.InvalidColumnName(name);

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
    internal static Func<MultiPartName, SqlType> ColumnTypeResolverFor(FromSource[] sources, Func<MultiPartName, SqlType>? outerTypeResolver = null) =>
        name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

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
    /// Whether <paramref name="name"/>'s qualifier — when it carries one — is
    /// the single-table DML target <em>as written</em>. That is the only
    /// qualifier such a statement admits: <c>UPDATE dbo.t SET id = t.id</c>,
    /// <c>UPDATE t SET id = dbo.t.id</c> and <c>UPDATE v SET id = v.id</c> all
    /// bind — a schema or database part only when it is the target's own —
    /// while <c>UPDATE v SET id = t.id</c> does not — even though
    /// <c>t</c> is the view's base table — and every other qualifier is
    /// Msg 4104 whether or not its leaf names a real column (probed against
    /// SQL Server 2025, 2026-08-05, for the SET list and the WHERE of both
    /// <c>UPDATE</c> and <c>DELETE</c>).
    /// </summary>
    internal static bool QualifierIsDmlTarget(Database database, MultiPartName targetName, MultiPartName name) =>
        name.ImmediateQualifier is not { } qualifier
            || (database.Collation.Equals(qualifier, targetName.Leaf) && FromSource.PrefixNames(FromSource.Resolved(targetName, database), name, name.Count - 1));

    /// <summary>
    /// The single-table DML counterpart: a compile-time resolver mirroring the
    /// per-row resolver an UPDATE / DELETE with no FROM clause builds — the
    /// name binds on its leaf against the target's columns, through the view's
    /// own projection when the statement goes through one, and a leaf that
    /// names nothing is Msg 207. A qualifier that isn't
    /// <paramref name="targetName"/> is Msg 4104 ahead of the leaf lookup, per
    /// <see cref="QualifierIsDmlTarget"/>.
    /// </summary>
    internal static Func<MultiPartName, SqlType> TargetColumnTypeResolver(BatchContext batch, MultiPartName targetName, HeapTable table, Schemas.View? sourceView) =>
        name =>
        {
            var collation = batch.CurrentDatabase.Collation;
            if (!QualifierIsDmlTarget(batch.CurrentDatabase, targetName, name))
                throw SimulatedSqlException.MultiPartIdentifierCouldNotBeBound(name.ToString());
            if (sourceView is not null)
            {
                for (var v = 0; v < sourceView.OutputColumns.Length; v++)
                {
                    if (collation.Equals(sourceView.OutputColumns[v].Name, name.Leaf))
                    {
                        var baseOrdinal = sourceView.BaseColumnOrdinals[v];
                        // A windowed or row-limited view's write reads its
                        // derived columns (a ROW_NUMBER's rn) off the body's
                        // own rows.
                        return baseOrdinal >= 0 ? ColumnTypeWithMaxLength(table.Columns[baseOrdinal])
                            : sourceView is { IsWindowed: true } or { IsRowLimited: true } ? ColumnTypeWithMaxLength(sourceView.OutputColumns[v])
                            : throw SimulatedSqlException.InvalidColumnName(name);
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
    internal static BooleanExpression ParseAndBindPredicate(ParserContext context, Func<MultiPartName, SqlType> resolveColumnType, FromSource[]? matchSources = null, JoinSpec[]? matchJoins = null)
    {
        // An aggregate the WHERE registers at its own level — one an enclosing
        // subquery moved here included — has no query to aggregate in: Msg 147.
        var whereAggregates = new List<AggregateExpression>();
        BooleanExpression predicate;
        // A joined UPDATE / DELETE's WHERE takes a MATCH over its FROM sources.
        using (ParserScope.Enter(ref context.MatchScope, matchSources is null ? null : new MatchScope { Sources = matchSources, Joins = matchJoins }))
        using (ParserScope.Enter(ref context.OuterTypeResolver, resolveColumnType))
        // A DML WHERE is one of the eight clauses real's Msg 11720 names, and
        // it says so for an UPDATE / DELETE / MERGE as readily as for a SELECT
        // (probe-confirmed 2026-08-05: `DELETE FROM t WHERE n = NEXT VALUE FOR s`).
        using (context.EnterNextValueForScope(NextValueForScope.Clause))
        using (ParserScope.Enter(ref context.AggregateCollector, whereAggregates))
        {
            predicate = BooleanExpression.Parse(context);
        }
        RefuseClauseAggregates(context.Batch, whereAggregates, SimulatedSqlException.AggregateInWhereClause());
        predicate.BindCarryingTypeChecks(context.Batch, resolveColumnType);
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
    /// <paramref name="expressions"/> with each bare reference to a source
    /// column that source re-draws per row (<see cref="FromSource.VolatileRefresh"/>)
    /// replaced by the expression that draws it — a body passing a nested
    /// body's drawn column through draws it too, as real merges the two.
    /// </summary>
    private static List<Expression> PassThroughDrawnColumns(List<Expression> expressions, FromSource[] sources)
    {
        List<Expression>? substituted = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (Unaliased(expressions[i]) is not Reference reference)
                continue;
            var (s, c) = FindSourceColumnOfAnyKind(sources, reference.ReferencedName);
            if (s < 0 || sources[s].VolatileRefresh is not { } drawn)
                continue;
            var at = Array.IndexOf(drawn.Ordinals, c);
            if (at < 0)
                continue;
            substituted ??= [.. expressions];
            substituted[i] = drawn.Expressions[at];
        }
        return substituted ?? expressions;
    }

    /// <summary>
    /// Whether an ORDER BY key reads a per-call-varying projection — written
    /// out, by ordinal, or by the select-list alias of such a column — which is
    /// what makes a <c>TOP</c> / <c>OFFSET</c> body draw the value itself.
    /// </summary>
    private static bool OrderKeysDrawPerCall(List<OrderBySpec> orderBy, List<Expression> expressions, string[] outputColumnNames)
    {
        foreach (var spec in orderBy)
        {
            var key = spec.IsOrdinal
                ? (spec.Ordinal >= 1 && spec.Ordinal <= expressions.Count ? expressions[spec.Ordinal - 1] : null)
                : spec.Expr;
            if (key is not null && VolatileProjection.DrawsPerCall(key))
                return true;
            if (spec is { MayNameAlias: true, Expr: { } written } && Unparenthesized(written) is Reference { ReferencedName: { ImmediateQualifier: null } name })
            {
                for (var i = 0; i < outputColumnNames.Length && i < expressions.Count; i++)
                {
                    if (BuiltInToken.Equals(outputColumnNames[i], name.Leaf) && VolatileProjection.DrawsPerCall(expressions[i]))
                        return true;
                }
            }
        }
        return false;

        static Expression Unparenthesized(Expression expression) =>
            expression is Parenthesized { Wrapped: var inner } ? Unparenthesized(inner) : expression;
    }

    /// <summary>
    /// Finds, per catalog-view source, an equality that can narrow it to a seek:
    /// a top-level AND-conjunct <c>&lt;key&gt; = &lt;comparand&gt;</c> (either
    /// operand order), or the equality family of an <c>IN</c> list over one key,
    /// where the key is one of the source's seekable columns
    /// (<see cref="SeekColumnsOf"/>) and every comparand holds one value for the
    /// execution (<see cref="IsConstantForOneExecution"/>). Where several
    /// conjuncts qualify, the best-ranked column wins. Returns each narrowed
    /// source with its column and comparands; the caller rebuilds its plan
    /// through the pushdown-carrying <c>ForCatalogView</c>.
    /// </summary>
    private static List<(int Index, string Column, Expression[] Comparands)> DetectCatalogPushdowns(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders)
    {
        List<(int, string, Expression[])> found = [];
        if (sources.Length == 0)
            return found;

        // WHERE conjuncts, plus the ON conjuncts of inner joins — for an inner
        // join the two are interchangeable, so an ON equality narrows the
        // generator exactly as safely as a WHERE one. An outer join's ON is
        // deliberately excluded here: dropping rows from the null-supplying
        // side turns matched rows into null-extended ones, and the residual
        // predicate that makes every other narrowing safe cannot undo that.
        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);
        var innerJoined = new bool[sources.Length];
        innerJoined[0] = true;
        for (var i = 0; i < joins.Length && i + 1 < sources.Length; i++)
        {
            if (joins[i].Kind != JoinKind.Inner)
                continue;
            innerJoined[i + 1] = innerJoined[i];
            joins[i].OnPredicate?.CollectConjuncts(conjuncts);
        }

        var singleSource = sources.Length == 1;
        for (var index = 0; index < sources.Length; index++)
        {
            if (!innerJoined[index] || SeekColumnsOf(sources[index]) is not { } pushColumns)
                continue;
            if (MatchBestConjunct(conjuncts, sources, index, singleSource, pushColumns) is { } direct)
                found.Add((index, direct.Column, direct.Comparands));
        }

        // A LEFT join's own ON conjunct that reads only its right side and a
        // constant narrows that side exactly: a right row failing it can match
        // no left row, and the join re-checks the whole ON regardless. Taken
        // only where the ON has no equi-join key, since a key lets the join
        // probe the whole view's persisted index, which a narrowed copy loses.
        for (var level = 1; level < sources.Length; level++)
        {
            var join = joins[level - 1];
            if (join is not { Kind: JoinKind.Left, GroupCount: 1, OnPredicate: { } on }
                || innerJoined[level]
                || SeekColumnsOf(sources[level]) is not { } pushColumns)
            {
                continue;
            }
            var onConjuncts = new List<BooleanExpression>();
            on.CollectConjuncts(onConjuncts);
            if (onConjuncts.Exists(conjunct => TryExtractEquiKey(conjunct, sources, level, out _)))
                continue;
            if (MatchBestConjunct(onConjuncts, sources, level, singleSource: false, pushColumns) is { } own)
                found.Add((level, own.Column, own.Comparands));
        }

        // Transitive closure, one hop, which is the shape real derives: given
        // `ic.object_id = t.object_id` in WHERE and `ic.object_id =
        // col.object_id` in an inner join's ON, real seeks BOTH catalog tables
        // by the outer value rather than seeking one and scanning the other.
        // Without this the joined side stays a full scan and dominates
        // everything the direct pushdown saved.
        foreach (var (index, column, comparands) in found.ToArray())
        {
            if (!innerJoined[index])
                continue;
            for (var other = 0; other < sources.Length; other++)
            {
                if (other == index || !innerJoined[other]
                    || found.Exists(f => f.Item1 == other)
                    || SeekColumnsOf(sources[other]) is not { } otherColumns)
                {
                    continue;
                }
                if (LinksSameColumn(conjuncts, sources, index, column, other, otherColumns) is { } linked)
                    found.Add((other, linked, comparands));
            }
        }
        return found;
    }

    /// <summary>
    /// The columns a pushed-down equality may narrow <paramref name="source"/>
    /// on, best first: every ranked seek column of a cacheable catalog view,
    /// else the columns a view's filtered generator keys; null for a source
    /// that isn't a catalog view or offers neither.
    /// </summary>
    private static string[]? SeekColumnsOf(FromSource source) => source.BackingCatalogView switch
    {
        { SeekColumns: { } ranked } => ranked,
        { PushdownColumns: { } keyed, FilteredRowGenerator: not null } => keyed,
        _ => null,
    };

    /// <summary>
    /// The conjunct keying <c>sources[index]</c> on its best-ranked column of
    /// <paramref name="pushColumns"/> against execution constants — an equality
    /// in either operand order, or an <c>IN</c> list / OR-of-equalities whose
    /// every member equates that one column. Ties go to the earlier conjunct.
    /// </summary>
    private static (string Column, Expression[] Comparands)? MatchBestConjunct(
        List<BooleanExpression> conjuncts,
        FromSource[] sources,
        int index,
        bool singleSource,
        string[] pushColumns)
    {
        (string Column, Expression[] Comparands)? best = null;
        var bestRank = int.MaxValue;
        foreach (var conjunct in conjuncts)
        {
            (string Column, Expression[] Comparands)? match = null;
            if (conjunct.TryGetEqualityOperands(out var left, out var right))
            {
                if ((MatchPushdownKey(left, right, sources, index, singleSource, pushColumns)
                    ?? MatchPushdownKey(right, left, sources, index, singleSource, pushColumns)) is { } single)
                {
                    match = (single.Column, [single.Comparand]);
                }
            }
            else if (conjunct.TryGetEqualityFamily(out var pairs))
            {
                match = MatchPushdownFamily(pairs, sources, index, singleSource, pushColumns);
            }
            if (match is not { } found)
                continue;
            var rank = Array.IndexOf(pushColumns, found.Column);
            if (rank < bestRank)
            {
                best = found;
                bestRank = rank;
            }
        }
        return best;
    }

    // Every member of an IN list / OR-of-equalities equating the same push
    // column of sources[index] with an execution constant: that column and the
    // constants, in written order; else null.
    private static (string Column, Expression[] Comparands)? MatchPushdownFamily(
        List<(Expression Left, Expression Right)> pairs,
        FromSource[] sources,
        int index,
        bool singleSource,
        string[] pushColumns)
    {
        string? column = null;
        var comparands = new Expression[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            var (left, right) = pairs[i];
            if ((MatchPushdownKey(left, right, sources, index, singleSource, pushColumns)
                ?? MatchPushdownKey(right, left, sources, index, singleSource, pushColumns)) is not { } member
                || (column is not null && !ReferenceEquals(column, member.Column)))
            {
                return null;
            }
            column = member.Column;
            comparands[i] = member.Comparand;
        }
        return column is null ? null : (column, comparands);
    }

    /// <summary>
    /// Whether some conjunct equates <c>sources[index].&lt;column&gt;</c> with a
    /// pushdown column of <c>sources[other]</c>, and if so which of the other
    /// source's columns it is — the link that carries an already-known
    /// comparand across an inner join.
    /// </summary>
    private static string? LinksSameColumn(
        List<BooleanExpression> conjuncts,
        FromSource[] sources,
        int index,
        string column,
        int other,
        string[] otherColumns)
    {
        foreach (var conjunct in conjuncts)
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            if (NamesSourceColumn(left, sources, index, column) && ResolvePushColumn(right, sources, other, otherColumns) is { } forward)
                return forward;
            if (NamesSourceColumn(right, sources, index, column) && ResolvePushColumn(left, sources, other, otherColumns) is { } reversed)
                return reversed;
        }
        return null;
    }

    private static bool NamesSourceColumn(Expression expression, FromSource[] sources, int index, string column)
        => expression is Reference { ReferencedName: { ImmediateQualifier: { } qualifier } name }
            && BuiltInToken.Equals(qualifier, sources[index].Qualifier)
            && BuiltInToken.Equals(column, name.Leaf);

    private static string? ResolvePushColumn(Expression expression, FromSource[] sources, int index, string[] pushColumns)
    {
        if (expression is not Reference { ReferencedName: { ImmediateQualifier: { } qualifier } name }
            || !BuiltInToken.Equals(qualifier, sources[index].Qualifier))
        {
            return null;
        }
        foreach (var pushColumn in pushColumns)
        {
            if (BuiltInToken.Equals(pushColumn, name.Leaf))
                return pushColumn;
        }
        return null;
    }

    // When `keySide` is a column reference to the catalog source naming one of
    // `pushColumns`, and `valueSide` holds one value for the whole execution,
    // returns the canonical column name (from `pushColumns`) paired with the
    // comparand; else null.
    private static (string Column, Expression Comparand)? MatchPushdownKey(
        Expression keySide,
        Expression valueSide,
        FromSource[] sources,
        int index,
        bool singleSource,
        string[] pushColumns)
    {
        var sourceQualifier = sources[index].Qualifier;
        if (keySide is not Reference reference || !IsConstantForOneExecution(valueSide, sources))
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
    /// Whether the comparand holds a single value across one execution of this
    /// plan, which is what lets the seek run once instead of per row.
    /// </summary>
    /// <remarks>
    /// A row-independent expression (literal, variable, parameter) obviously
    /// qualifies. So does a reference whose qualifier names no source of
    /// <em>this</em> query: it can only be an enclosing query's column, and a
    /// correlated body re-executes once per outer row, so it is fixed for the
    /// duration of each execution. That second case is what a correlated
    /// catalog read looks like — <c>WHERE ic.object_id = t.object_id</c>
    /// inside a <c>CROSS APPLY</c> — and admitting it is the difference
    /// between regenerating the whole view per outer row and seeking one
    /// object, which is how real plans the same query (an index seek carrying
    /// OUTER REFERENCES). An <em>unqualified</em> reference is never taken: it
    /// could bind to a local source and vary per row, and the pushdown only
    /// ever narrows the generator, so a wrong narrowing would lose rows the
    /// residual WHERE could not add back.
    /// </remarks>
    private static bool IsConstantForOneExecution(Expression valueSide, FromSource[] sources)
    {
        if (valueSide.IsRowIndependent)
            return true;
        if (valueSide is not Reference { ReferencedName.ImmediateQualifier: { } qualifier })
            return false;
        foreach (var source in sources)
        {
            if (BuiltInToken.Equals(qualifier, source.Qualifier))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Moves each aggregate whose operand reads only an <em>enclosing</em>
    /// query's columns, such as <c>(SELECT MAX(t.col) FROM u)</c> inside a
    /// query over <c>t</c>, to that query's collector: real binds it there,
    /// which makes the enclosing query an aggregate query collapsing to one row
    /// per group. The same instance moves, so the nested expression tree keeps
    /// referencing it and reads the value the owning query binds.
    /// </summary>
    /// <remarks>
    /// An aggregate reading no column at all (<c>COUNT(*)</c>, <c>MAX(1)</c>)
    /// stays, and one reading an enclosing query's column beside a column of
    /// its own is real's Msg 8124; an operand reading two enclosing scopes is
    /// judged again once it has moved one level out, where one of them is
    /// local.
    /// <para>A name that resolves in <em>no</em> scope isn't that case at all:
    /// it is real's Msg 207, which real reports at compile time (probe-confirmed
    /// — <c>HAVING MAX(nosuchcol) = 1</c> refuses a <c>CREATE VIEW</c> outright,
    /// while the genuinely-outer <c>(SELECT MAX(t.a) FROM u)</c> creates). The
    /// enclosing type resolver is what tells the two apart; it raises Msg 207
    /// itself once the scope chain runs out.</para>
    /// </remarks>
    internal static void RehomeAggregatesOverOuterScope(
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
            List<MultiPartName>? unresolved = null;
            try
            {
                void Visit(MultiPartName name)
                {
                    referenced++;
                    if (FindSourceColumn(sources, name).SourceIndex >= 0)
                        resolvedHere++;
                    else
                        (unresolved ??= []).Add(name);
                }
                operand.VisitColumnReferences(Visit);

                // An approximate percentile's fraction is part of what it
                // aggregates: an enclosing query's column there beside a column
                // of this scope is Msg 8124, and a column of the scope that owns
                // the call Msg 8726 — unless its source is one constant row,
                // which real folds to a constant first (probed 2026-09-29
                // against SQL Server 2025).
                var fractionVaries = false;
                if (aggregate.Kind is AggregateKind.ApproxPercentileCont or AggregateKind.ApproxPercentileDisc)
                {
                    aggregate.Separator?.VisitColumnReferences(name =>
                    {
                        Visit(name);
                        var sourceIndex = FindSourceColumn(sources, name).SourceIndex;
                        if (sourceIndex >= 0 && sources[sourceIndex].LateralPlan is not { IsSingleConstantRow: true })
                            fractionVaries = true;
                    });
                }

                if (unresolved is null)
                {
                    // Real settles it after the rest of the statement has
                    // bound, so any other binder error outranks it.
                    if (fractionVaries)
                    {
                        var notConstant = SimulatedSqlException.PercentileInputNotConstant(aggregate.Kind == AggregateKind.ApproxPercentileCont ? "APPROX_PERCENTILE_CONT" : "APPROX_PERCENTILE_DISC");
                        if (parseBatch.Parser.SecurableSink is null)
                            throw notConstant;
                        parseBatch.Parser.PendingBindError ??= notConstant;
                    }
                    continue;
                }

                // Resolve the outer scope chain before concluding anything: with no
                // enclosing scope at all, or with one that doesn't know the name,
                // this is a bad column reference rather than an outer-bound
                // aggregate.
                foreach (var name in unresolved)
                {
                    if (outerTypeResolver is null)
                        throw UnresolvedNameError(sources, name);
                    _ = outerTypeResolver(name);
                }
            }
            catch (SimulatedSqlException) when (parseBatch.BindErrors is not null)
            {
                // Typing the operand records each unbindable name where it
                // sits — COUNT never types its operand otherwise.
                _ = operand.GetSqlType(parseBatch, name => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver));
                continue;
            }

            // A refused aggregate leaves this scope's list too, so no later
            // placement check refuses it a second time.
            if (resolvedHere > 0)
                RefuseAggregatePlacement(parseBatch, aggregate, SimulatedSqlException.OuterReferenceMixedInAggregate());
            else if (!MoveToEnclosingQuery(parseBatch, aggregate, parseBatch.Parser.EnclosingAggregateCollector))
                throw new NotSupportedException("An aggregate over an enclosing query's columns, with no enclosing query collecting aggregates, isn't modeled.");
            (rehomed ??= []).Add(aggregate);
        }

        if (rehomed is not null)
            _ = aggregates.RemoveAll(rehomed.Contains);
    }

    /// <summary>
    /// Hands <paramref name="aggregate"/>, which reads only columns from outside
    /// the scope it was written in, to <paramref name="enclosing"/>, walking
    /// through any <c>APPLY</c> boundary on the way — one whose left side it
    /// reads is Msg 4101. False when there is no enclosing collector at all.
    /// </summary>
    internal static bool MoveToEnclosingQuery(BatchContext parseBatch, AggregateExpression aggregate, List<AggregateExpression>? enclosing)
    {
        while (enclosing is ApplyAggregateBoundary boundary)
        {
            var readsLeft = false;
            aggregate.Operand?.VisitColumnReferences(name => readsLeft |= FindSourceColumn(boundary.LeftSources, name).SourceIndex >= 0);
            if (readsLeft)
            {
                RefuseAggregatePlacement(parseBatch, aggregate, SimulatedSqlException.AggregateOverApplyLeftSide());
                return true;
            }
            enclosing = boundary.Enclosing;
        }
        if (enclosing is null)
            return false;
        aggregate.ReadsEnclosingGroup = true;
        enclosing.Add(aggregate);
        return true;
    }

    /// <summary>
    /// Raises <paramref name="error"/>, an aggregate standing where real
    /// refuses one, or records it in the statement's binder report when one is
    /// being gathered.
    /// </summary>
    internal static void RefuseAggregatePlacement(BatchContext parseBatch, AggregateExpression aggregate, SimulatedSqlException error)
    {
        if (parseBatch.BindErrors?.RecordAggregatePlacement(error, aggregate) != true)
            throw error;
    }

    /// <summary>
    /// Refuses with <paramref name="error"/> the first aggregate a clause that
    /// can own none collected — an <c>UPDATE</c>'s <c>SET</c> list, a
    /// <c>MERGE</c>'s <c>ON</c>, <c>WHEN</c> condition or action — whether
    /// written there or moved there from a subquery reading only the
    /// statement's columns.
    /// </summary>
    internal static void RefuseClauseAggregates(BatchContext parseBatch, List<AggregateExpression> collected, SimulatedSqlException error)
    {
        if (collected.Count > 0)
            RefuseAggregatePlacement(parseBatch, collected[0], error);
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
    /// The sorting / grouping rejection for a type <see cref="SqlType.IsIncomparable"/>
    /// marks, dispatched to the number real gives that family: <b>Msg 306</b>
    /// for the legacy <c>text</c> / <c>ntext</c> / <c>image</c> trio,
    /// <b>Msg 305</b> for <c>xml</c>, <b>Msg 42213</b> for <c>vector</c>,
    /// <b>Msg 13636</b> (state 2) for <c>json</c>, and
    /// <b>Msg 249</b> — the only one that names <paramref name="clause"/> — for
    /// the two spatial types. DISTINCT and the deduping set operators make no such split; they
    /// report one message across all three families.
    /// </summary>
    internal static SimulatedSqlException NotComparableInClause(SqlType type, string clause) =>
        type.IsLegacyLob ? SimulatedSqlException.LobTypesCannotBeComparedOrSorted()
        : type is XmlSqlType ? SimulatedSqlException.XmlCannotBeComparedOrSorted()
        : type is VectorSqlType ? SimulatedSqlException.VectorCannotBeComparedOrSorted()
        : type is JsonSqlType ? SimulatedSqlException.JsonCannotBeComparedOrSorted(2)
        : SimulatedSqlException.TypeNotComparableInClause(type, clause);

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
        QueryScope scope,
        bool isAssignmentOnly,
        MultiPartName? intoTarget,
        Dictionary<int, ColumnReadTarget>? readColumnSink)
    {
        RecordIndexedViewShape(parseBatch, sources, joins, fromClause, distinct, topExpression, aggregates, windows, expressions);

        RehomeAggregatesOverOuterScope(parseBatch, sources, aggregates, parseBatch.Parser.OuterTypeResolver ?? scope.OuterTypeResolver);

        // What WHERE aggregated is only legal once it has moved to the query
        // whose columns it reads.
        if (fromClause.WhereAggregates is { } whereAggregates && whereAggregates.Find(aggregates.Contains) is { } whereAggregate)
            RefuseAggregatePlacement(parseBatch, whereAggregate, SimulatedSqlException.AggregateInWhereClause());

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
        foreach (var (pushIndex, pushColumn, pushComparands) in DetectCatalogPushdowns(sources, joins, fromClause.Excluders))
        {
            var pushSource = sources[pushIndex];
            sources[pushIndex] = new FromSource(
                qualifier: pushSource.Qualifier,
                columnNames: pushSource.ColumnNames,
                columns: pushSource.Columns,
                storedSchema: pushSource.StoredSchema,
                storageOrdinals: pushSource.StorageOrdinals,
                lobStore: pushSource.LobStore,
                rows: pushSource.Rows,
                lateralPlan: ForCatalogView(pushSource.BackingCatalogView!, pushSource.BackingCatalogDatabase!, pushColumn, pushComparands),
                materializeOnce: true,
                backingCatalogView: pushSource.BackingCatalogView,
                backingCatalogDatabase: pushSource.BackingCatalogDatabase,
                writtenObjectName: pushSource.WrittenObjectName,
                unaliasedName: pushSource.UnaliasedName,
                catalogSeek: true);
        }

        var orderBy = fromClause.OrderBy;
        if (topWithTies && orderBy.Count == 0)
            throw SimulatedSqlException.TopWithTiesRequiresOrderBy();
        var browse = TakeBrowseStatement(parseBatch, scope, intoTarget, isAssignmentOnly);
        var browseHidden = browse && !distinct && aggregates.Count == 0 && fromClause.GroupingSets.Count == 0 && fromClause.Having is null
            ? AppendBrowseHiddenColumns(sources, expressions)
            : 0;
        var outputSchema = new SqlType[expressions.Count];
        var outputColumnNames = new string[expressions.Count];

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, scope.OuterTypeResolver);

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

        // A statement's own projection, which reaches the client or
        // materializes a column (SELECT … INTO, a view's output), has to name
        // one collation itself and reports Msg 451 when the term it built
        // carries an unresolved one. An assignment target supplies the
        // collation instead — so an INSERT … SELECT source and a
        // `SELECT @v = …` list never demand one — and a nested query hands the
        // unresolved collation on to whatever reads its column.
        var projectionFeedsAnAssignment = isAssignmentOnly || scope.FeedsInsert;
        var projectionNamesOwnCollation = !projectionFeedsAnAssignment && scope.NamesOutputCollation;
        // A .nodes() row column holds a node reference, which only the xml
        // methods and IS [NOT] NULL may read: selecting it is Msg 493, ahead of
        // any type rule its xml type would break.
        // Every other clause is held to the same rule (probed 2026-09-28): a
        // WHERE, GROUP BY, HAVING, ORDER BY or ON reading the column is Msg 493
        // (525 for a conversion) too.
        foreach (var expression in expressions)
            RejectDirectNodesColumnRead(expression, sources);
        foreach (var excluder in fromClause.Excluders)
            RejectDirectNodesColumnRead(excluder, sources);
        foreach (var groupingExpression in fromClause.AllGroupingExpressions)
            RejectDirectNodesColumnRead(groupingExpression, sources);
        if (fromClause.Having is { } having)
            RejectDirectNodesColumnRead(having, sources);
        foreach (var orderSpec in fromClause.OrderBy)
        {
            if (orderSpec.Expr is { } orderExpression)
                RejectDirectNodesColumnRead(orderExpression, sources);
        }
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on)
                RejectDirectNodesColumnRead(on, sources);
        }
        // A reference to a numeric-spelled column names what reads it
        // numeric, so each is marked against the column it binds to — ahead
        // of typing, whose refusals name the operand's type the same way
        // (`CHECKSUM_AGG(<numeric column>)` is Msg 8117 naming numeric).
        foreach (var expression in expressions)
        {
            // An unbindable name marks nothing; typing reports it below.
            Reference.MarkNumericSpelled(expression, name =>
                TryResolveSourceColumn(sources, name) is { } id && sources[id.Source].Columns[id.Column].SpelledNumeric);
            Reference.MarkAliasTyped(expression, name =>
                TryResolveSourceColumn(sources, name) is { } id ? sources[id.Source].Columns[id.Column].AliasType : null);
        }

        for (var i = 0; i < expressions.Count; i++)
        {
            outputSchema[i] = expressions[i].TypeCarryingTypeChecks(parseBatch, readColumnSink is null ? ResolveColumnType : RecordingResolver);
            outputColumnNames[i] = expressions[i] is Reference { ReferencedName.Leaf: ['$', ..] } pseudo && FindSourceColumn(sources, pseudo.ReferencedName) is ( >= 0, var pseudoColumn) and var (pseudoSource, _)
                // A graph pseudo-column names its result after the internal
                // column it reads (probed 2026-09-27 against SQL Server 2025).
                ? sources[pseudoSource].ColumnNames[pseudoColumn]
                : expressions[i].Name;
        }

        // A column a derived source filled only with bare NULLs has no type
        // either, so an aggregate or offset window over it is refused as over
        // the bare NULL itself (probed 2026-09-25 against SQL Server 2025).
        foreach (var aggregate in aggregates)
        {
            if (ReadsUntypedNullColumn(sources, aggregate.Operand))
                throw AggregateExpression.UntypedNullOperand(aggregate.Kind);
            // A WITHIN GROUP ordering binds as the rows are read; a statement
            // read for its whole bind error report binds it here, after the
            // aggregate's operand as real does.
            if (parseBatch.BindErrors is not null && aggregate.OrderBy is { } withinGroup)
            {
                foreach (var item in withinGroup)
                    _ = item.Expr?.GetSqlType(parseBatch, ResolveColumnType);
            }
        }
        foreach (var window in windows)
        {
            if (ReadsUntypedNullColumn(sources, window.Kind == WindowKind.Aggregate ? window.AggregateInfo!.Operand : window.Operand)
                && window.UntypedNullOperand() is { } refusal)
            {
                throw refusal;
            }
        }

        // An EXISTS body only counts rows, so real never evaluates its select
        // list — `EXISTS (SELECT 1/0 FROM t)` is true over a non-empty t
        // (probe-confirmed 2026-09-23). Once the list is bound, each term
        // becomes a typed NULL under its own name; a grouped or windowed body
        // keeps its terms, which its aggregation machinery reads.
        if (scope.ProjectionUnread && aggregates.Count == 0 && windows.Count == 0)
        {
            for (var i = 0; i < expressions.Count; i++)
                expressions[i] = new NamedExpression(new Value(SqlValue.Null(outputSchema[i])), outputColumnNames[i]);
        }

        // Nor does an EXISTS body's aggregate report a NULL it skipped
        // (probed 2026-09-23).
        if (scope.ProjectionUnread)
        {
            foreach (var aggregate in aggregates)
                aggregate.WarnsOnNullInput = false;
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
            excluder.BindCarryingTypeChecks(parseBatch, ResolveColumnType);
        foreach (var join in joins)
        {
            if (join.OnPredicate is not { } on)
                continue;
            if (join.ScopeEnd < 0)
            {
                on.BindCarryingTypeChecks(parseBatch, ResolveColumnType);
                continue;
            }
            var onScope = sources[join.ScopeStart..join.ScopeEnd];
            on.BindCarryingTypeChecks(parseBatch, name => ResolveColumnTypeAcrossSources(onScope, name, scope.OuterTypeResolver));
        }
        // A grouping term names a collation too, and real numbers those slots
        // from 2 — the grouped projection it builds carries one column ahead of
        // the keys (probe-confirmed: a lone `GROUP BY concat(a, b)` reports
        // column 2, and a second key reports column 3).
        var groupingOrdinal = 2;
        foreach (var grouping in fromClause.AllGroupingExpressions)
        {
            RequireSettledOutputCollation(grouping.TypeCarryingTypeChecks(parseBatch, ResolveColumnType), "GROUP BY", groupingOrdinal++);
        }
        fromClause.Having?.BindCarryingTypeChecks(parseBatch, ResolveColumnType);

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

        // Msg 421: a non-comparable type can't appear in a DISTINCT projection.
        // Unlike the sorting and grouping slots below, DISTINCT reports one
        // message for the legacy LOB trio, xml and the spatial pair alike,
        // naming the type — probe-confirmed against SQL Server 2025.
        // DISTINCT also has to compare the values it dedups, so an unresolved
        // collation reports here — as Msg 446 State 11, which names the
        // producing operator and DISTINCT together rather than taking either
        // the output-column or the consuming-operation wording.
        // Every non-comparable column reports, in select-list order (probed
        // 2026-10-02 against SQL Server 2025: two json columns, two Msg 421).
        if (distinct)
        {
            List<SimulatedSqlException>? incomparable = null;
            foreach (var type in outputSchema)
            {
                if (type.IsIncomparable)
                    (incomparable ??= []).Add(SimulatedSqlException.TypeCannotBeSelectedAsDistinct(type));
            }
            if (incomparable is not null)
                throw SimulatedSqlException.Aggregate(incomparable);
            for (var i = 0; i < outputSchema.Length; i++)
            {
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
        // Only a bare, unqualified term may name an alias
        // (OrderBySpec.MayNameAlias) — `ORDER BY x.a` never matches an output
        // column `a`, whatever x is (probed 2026-09-26 against SQL Server
        // 2025: an unknown x is Msg 4104 over an empty table too).
        var orderTermMayNameAlias = false;
        var orderTermAmbiguous = false;
        SqlType ResolveOrderByType(MultiPartName name)
        {
            if (orderTermMayNameAlias && name.ImmediateQualifier is null)
            {
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (!BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                        continue;
                    // A name two select items share is ambiguous, even when
                    // both read the same column (probed 2026-09-28 against
                    // SQL Server 2025: SELECT a, a … ORDER BY a is Msg 209).
                    for (var k = j + 1; k < outputColumnNames.Length; k++)
                    {
                        if (!BuiltInToken.Equals(outputColumnNames[k], name.Leaf))
                            continue;
                        orderTermAmbiguous = true;
                        throw SimulatedSqlException.AmbiguousColumnName(name.Leaf);
                    }
                    return outputSchema[j];
                }
            }

            return ResolveColumnType(name);
        }

        // A nested query's unbounded ORDER BY is refused once the query has
        // parsed (Msg 1033), ahead of any of its terms' bind errors; a FOR XML
        // one, which that check lets through, binds its terms per row instead.
        var orderByBinds = !scope.RefusesUnboundedOrderBy || topExpression is not null || fromClause.OffsetExpression is not null;
        for (var i = 0; orderByBinds && i < orderBy.Count; i++)
        {
            // A written constant reaching here is one whose fold raised (the
            // rest are Msg 408 while parsing) — under DISTINCT it is simply
            // not in the select list, which real reports while compiling.
            if (distinct && orderBy[i].Expr is { IsWrittenConstant: true })
                throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct();
            orderTermMayNameAlias = orderBy[i].MayNameAlias;
            SqlType keyType;
            var recorded = parseBatch.BindErrors?.Count ?? 0;
            try
            {
                keyType = orderBy[i].IsOrdinal
                    ? outputSchema[orderBy[i].Ordinal - 1]
                    : orderBy[i].Expr!.TypeCarryingTypeChecks(parseBatch, ResolveOrderByType);
                // Real follows an unknown name under DISTINCT with DISTINCT's
                // own complaint, the same as the throwing path below.
                // An ambiguous name is its own complaint, DISTINCT or not.
                if (distinct && !orderTermAmbiguous && parseBatch.BindErrors is { } report && report.Count > recorded && report.SpanOf(orderBy[i].Expr) is { } term)
                    report.Record(SimulatedSqlException.OrderByItemNotInSelectListWithDistinct(), term.End);
            }
            catch (SimulatedSqlException unknown) when (distinct && unknown.Number is 207 or 4104)
            {
                // Real follows the unknown name with DISTINCT's own complaint.
                throw SimulatedSqlException.Aggregate([unknown, SimulatedSqlException.OrderByItemNotInSelectListWithDistinct()]);
            }

            // A term whose binding recorded an error has had DISTINCT's own
            // complaint recorded after it above; a statement read again for
            // its binder report records this one where the term sits.
            if (distinct && !orderBy[i].IsOrdinal && (parseBatch.BindErrors?.Count ?? 0) == recorded)
            {
                var ordinal = DistinctOrderTermOrdinal(sources, expressions, outputColumnNames, orderBy[i]);
                if (ordinal < 0)
                {
                    if (parseBatch.BindErrors is not { } report || report.SpanOf(orderBy[i].Expr) is not { } term)
                        throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct();
                    report.Record(SimulatedSqlException.OrderByItemNotInSelectListWithDistinct(), term.End);
                }
                else if (ordinal > 0)
                {
                    orderBy[i] = OrderBySpec.FromOrdinal(ordinal, orderBy[i].Descending);
                }
            }

            if (keyType.IsIncomparable)
                throw NotComparableInClause(keyType, "ORDER BY");
            RequireSettledOutputCollation(keyType, "ORDER BY", i + 1);
        }

        // A grouping key is compared the same way a sort key is, and reports
        // the same per-family error — probe-confirmed that real raises it over
        // an empty rowset, so it binds here rather than at execution. Walking
        // the deduplicated union rather than the sets themselves keeps a
        // ROLLUP / CUBE expansion from re-typing one key per set it lands in.
        foreach (var groupingKey in fromClause.AllGroupingExpressions)
        {
            var groupingKeyType = groupingKey.GetSqlType(parseBatch, ResolveColumnType);
            if (groupingKeyType.IsIncomparable)
                throw NotComparableInClause(groupingKeyType, "GROUP BY");
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
        {
            // Real binds the GROUP BY items before judging the select list
            // against them, so an item's own held Msg 144 / 164 — raised once
            // the statement has parsed — outranks this check's (`SELECT a …
            // GROUP BY 1` is Msg 164; probed 2026-09-26).
            if (parseBatch.Parser.PendingBindError is null)
            {
                var grouped = fromClause.GroupingSets.Count > 0 || fromClause.Having is not null;
                ValidateGroupByReferences(
                    sources, expressions, orderBy, outputColumnNames, fromClause, windows,
                    grouped ? new NullabilityContext(parseBatch, static _ => true, ResolveColumnType) : null,
                    parseBatch.BindErrors);
            }
        }

        // STRING_AGG keeps no partial state a subtotal could merge, so real
        // refuses it under ROLLUP / CUBE / GROUPING SETS once they expand past
        // one set (Msg 8710); every other aggregate, the approximate pair and
        // CHECKSUM_AGG included, merges (probed 2026-10-01 against SQL Server
        // 2025).
        if (fromClause.GroupingSets.Count > 1 && aggregates.Exists(static a => a.Kind == AggregateKind.StringAgg))
            throw SimulatedSqlException.SubaggregatesNotMergeable();

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
            // A percentile's fraction reading this query's own columns is
            // real's Msg 8726, as for the approximate pair — an enclosing
            // query's column or a variable is one value for the statement
            // (probed 2026-10-01 against SQL Server 2025).
            if (windows[i] is { Kind: WindowKind.PercentileCont or WindowKind.PercentileDisc, PercentileArg: { } fraction } percentile)
            {
                fraction.VisitColumnReferences(name =>
                {
                    var sourceIndex = FindSourceColumn(sources, name).SourceIndex;
                    if (sourceIndex >= 0 && sources[sourceIndex].LateralPlan is not { IsSingleConstantRow: true })
                        throw SimulatedSqlException.PercentileInputNotConstant(percentile.Kind == WindowKind.PercentileCont ? "PERCENTILE_CONT" : "PERCENTILE_DISC");
                });
            }
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
        // no aggregates / GROUP BY / HAVING. A window function or a TOP /
        // OFFSET / FETCH row limit keeps the profile — the window's columns are
        // derived, and the view records either shape (View.IsWindowed /
        // View.IsRowLimited) for the write path, since real writes only to the
        // rows such a body yields. ORDER BY alone only affects reads. View.cs
        // consumes this to derive Msg 4403 / 4405 / 4406 metadata at CREATE VIEW.
        var (updatabilityProfile, updatabilityRejection) = ComputeViewUpdatabilityProfile(
            sources, joins, expressions, fromClause, distinct, aggregates);

        var columnNullability = ComputeColumnNullability(expressions, sources, joins, fromClause.GroupingSetsWritten, parseBatch, ResolveColumnType);

        ReduceConstantCounts(aggregates, fromClause);

        // A HAVING real can see is never TRUE keeps no group, so the statement
        // answers nothing whatever the rest of it would have done — and real
        // then runs none of it, which is what makes
        // `SELECT a FROM t WHERE a / 0 IS NOT NULL GROUP BY a HAVING NULL IS
        // NOT NULL` answer no rows there rather than Msg 8134. Every binding
        // check above still ran, so Msg 207 / 8120 / 8121 report as they do on
        // real; only the row work is skipped.
        var resultIsProvablyEmpty = fromClause.Having?.IsNeverTrue == true;

        // A WHERE settled never-true while compiling leaves real a constant
        // scan: an ungrouped statement then runs none of its sources, so a
        // derived table's `1/0` under `WHERE 1 = 0` doesn't raise there.
        var isGrouped = aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null;
        var whereIsNeverTrue = fromClause.Excluders.Exists(static excluder => excluder.IsNeverTrue);
        if (whereIsNeverTrue && !isGrouped)
            resultIsProvablyEmpty = true;

        // Without GROUP BY an aggregate answers one row at most, whose sort
        // real never runs: `SELECT COUNT(*) FROM t ORDER BY MAX(a) / 0`
        // returns its row there (probed 2026-09-26), so no key is evaluated.
        var aggregateOrderBy = fromClause.GroupingSets.Count == 0 ? [] : orderBy;

        // What real evaluates once as its plan starts, raising before any row
        // is read (see ConstantFolding.CollectStartupConstants). A scalar
        // aggregate's ORDER BY keys are never evaluated at all.
        var readsStorage = sources.Any(static source =>
            source.BackingTable is not null || source.BackingView is not null || source.BackingCatalogView is not null
            || source.LateralPlan?.ReadsStorage == true);
        var startupConstants = new List<Expression>();
        var projectionStartupConstants = new List<Expression>();
        var startsConstants = readsStorage && !whereIsNeverTrue
            && !(sources is [{ BackingTable: { } onlyTable } onlySource] && PinsUniqueKey(onlyTable, onlySource, fromClause.Excluders));
        if (startsConstants)
        {
            foreach (var expression in expressions)
                ConstantFolding.CollectStartupConstants(expression, parseBatch.Parser, projectionStartupConstants);
            foreach (var excluder in fromClause.Excluders)
                ConstantFolding.CollectStartupConstants(excluder, parseBatch.Parser, startupConstants);
            foreach (var join in joins)
            {
                if (join.OnPredicate is { } on)
                    ConstantFolding.CollectStartupConstants(on, parseBatch.Parser, startupConstants);
            }
            if (!(isGrouped && fromClause.GroupingSets.Count == 0))
            {
                foreach (var term in orderBy)
                {
                    if (term.Expr is { } sortKey)
                        ConstantFolding.CollectStartupConstants(sortKey, parseBatch.Parser, startupConstants);
                }
            }
        }

        // A four-part read leaves out the columns the query never names.
        NoteUnreadRemoteColumns(sources, joins, expressions, fromClause);

        // The plan the closure below belongs to, for recognizing an emptiness
        // probe of it (see HasAnyRow) or a row-address run (see
        // ExecuteWithRowAddresses); assigned once the plan exists.
        Selection? self = null;
        var installsRowAddresses = parseBatch.Parser.ReadsRowLocators;
        var selection = new Selection(outputSchema, outputColumnNames,
            hasOrderBy: orderBy.Count > 0,
            hasTopOrOffsetOrFetch: topExpression is not null || offsetExpression is not null || fetchExpression is not null,
            (batch, outerResolver) =>
            {
                if (resultIsProvablyEmpty)
                    return [];
                // A query reading a row locator has its sources' producers
                // record each row's address from here on (see RowLocator).
                if (installsRowAddresses)
                    batch.CurrentStatement.RowAddresses ??= new();
                // Per-execution count resolution: the expressions may carry
                // parameters, and this closure replays across executions of a
                // plan-cached SELECT (EF's Skip/Take shape), so the values
                // must come from the EXECUTING batch, not the parse.
                var top = topExpression is null
                    ? default
                    : topPercent
                        ? new TopSpec(null, ResolveTopPercentValue(topExpression, batch, outerResolver), topWithTies)
                        : new TopSpec(ResolveRowCountLimit(topExpression, RowLimitKind.Top, batch, outerResolver), null, topWithTies);
                var offsetCount = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, batch, outerResolver);
                var fetchCount = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, batch, outerResolver);
                // TOP 0 is a plan with nothing to start, and an emptiness
                // probe of an EXISTS reads no projection.
                if (top.Count != 0)
                {
                    RunStartupConstants(startupConstants, batch);
                    if (!ReferenceEquals(batch.ExistenceProbe, self))
                        RunStartupConstants(projectionStartupConstants, batch);
                }
                // Push the WHERE conjuncts a deferred source's own body can
                // apply into that body first, so the filter reaches its base
                // scan (and the index seek there) instead of running only after
                // the body produced every row. Before the materialization
                // below, so a source that materializes materializes the
                // narrowed rowset. Every pushed conjunct stays here as well.
                var execSources = PushWhereIntoDeferredSources(sources, fromClause.Excluders, batch);
                // Hand a ROW_NUMBER()-only body the row-number window the WHERE
                // above it leaves surviving, so it keeps a bounded per-partition
                // selection instead of sorting every partition in full and
                // projecting every row for the filter here to discard.
                execSources = BoundRowNumberBodies(execSources, fromClause.Excluders, batch);
                // Reduce a joined GROUP BY body to the key set its equi-join
                // partner actually carries, so the body aggregates the groups
                // the join can use instead of every group in the table. After
                // the push (a body already filtered on its own output takes the
                // reduction on top) and before the materialization below.
                execSources = ReduceGroupedBodiesByJoinKeys(execSources, joins, fromClause.Excluders, batch, outerResolver);
                // Materialize the deferred sources whose rows can't change
                // across this enumeration once per execution (before the
                // projection paths build their resolver closures over the
                // array), so a nested-loop join stops re-generating them per
                // outer row and the equi-join hash path can key them.
                // Correlated sources are left untouched.
                execSources = MaterializeUncorrelatedDeferredSources(execSources, joins, batch, outerResolver);
                // An emptiness probe of this plan (HasAnyRow) needs its rows,
                // not their values, so it projects nothing — unless an ORDER BY
                // reads the projection.
                var projection = orderBy.Count == 0 && ReferenceEquals(batch.ExistenceProbe, self) ? []
                    : ReferenceEquals(batch.RowAddressProbe, self) ? [.. expressions, new RowAddress(0)]
                    : expressions;
                return aggregates.Count > 0 || fromClause.GroupingSets.Count > 0 || fromClause.Having is not null
                    ? AheadOfRowProjection(BuildAggregateProjectionRows(execSources, joins, ResolveColumnType, projection, fromClause, outputColumnNames, aggregateOrderBy, aggregates, windows, windowOperandTypes, windowResultTypes, top, offsetCount, fetchCount, distinct, batch, outerResolver))
                    : windows.Count > 0
                        ? ProjectWindowedRows(execSources, joins, projection, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, windows, windowOperandTypes, windowResultTypes, batch, outerResolver)
                        : ProjectSqlRows(execSources, joins, projection, fromClause.Excluders, outputColumnNames, orderBy, distinct, top, offsetCount, fetchCount, batch, outerResolver);
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
        self = selection;
        selection.InstallsRowAddresses = installsRowAddresses;
        selection.CarriesRowAddresses = updatabilityProfile is { Sources.Length: 1 };
        selection.ColumnNullability = columnNullability;
        selection.ProjectionExpressions = [.. expressions];
        var drawnProjection = PassThroughDrawnColumns(expressions, sources);
        selection.VolatileColumns = VolatileProjection.Of(drawnProjection, distinct || OrderKeysDrawPerCall(orderBy, drawnProjection, outputColumnNames));
        selection.ColumnIntegerLiteralDigits = LiteralDigitsOf(expressions);
        selection.ColumnIsUntypedNull = UntypedNullsOf(expressions, sources);
        selection.ColumnReportsNumeric = ColumnReportsNumericOf(expressions, outputSchema);
        selection.ColumnAliasTypes = ColumnAliasTypesOf(expressions);
        var outerMask = parseBatch.Parser.OuterMaskResolver;
        selection.ColumnMasks = ProjectionMasks(parseBatch, expressions, sources, outerMask, ResolveColumnType);
        selection.ColumnIdentitySources = ColumnIdentitySourcesOf(expressions, sources, joins);
        selection.BranchFromSources = sources;
        selection.ClauseExpressions = ClauseExpressionsOf(fromClause, joins, orderBy);
        selection.AutoSourceNames = AutoSourceNamesOf(sources);
        (selection.AutoColumnSource, selection.AutoColumnOrdinal) = AutoColumnBindingOf(expressions, sources);
        selection.ColumnWireFlags = WireFlagsOf(expressions, sources, selection.AutoColumnSource, selection.AutoColumnOrdinal);
        selection.IsGrouped = isGrouped;
        selection.ReadsStorage = readsStorage;
        if (browse)
        {
            selection.Browse = BrowseInfoFor(sources, expressions, outputColumnNames, browseHidden);
            selection.HiddenColumnCount = browseHidden;
        }
        if (parseBatch.Parser.CursorStatement && scope.Position == QueryPosition.Statement)
        {
            parseBatch.Parser.CursorStatement = false;
            selection.CursorBrowse = BrowseInfoFor(sources, expressions, outputColumnNames, 0, markKeys: false);
        }
        selection.StartsConstants = startsConstants;
        selection.HasWindows = windows.Count > 0;
        selection.SimplyParameterizable = sources.Length == 1 && joins.Length == 0 && IsParameterizableSource(sources[0])
            && !distinct && windows.Count == 0 && !selection.HasTopOrOffsetOrFetch && fromClause.GroupingSets.Count == 0
            && fromClause.Having is null && intoTarget is null && !isAssignmentOnly;
        // A STRING_AGG whose separator casts a variable answers only as a
        // scalar aggregate (probed 2026-10-04 against SQL Server 2025).
        if (fromClause.GroupingSets.Count > 0 && aggregates.Find(static aggregate => aggregate.SeparatorCastsVariable) is not null)
            throw SimulatedSqlException.StringAggSeparatorNotLiteralOrVariable();
        // A plain SELECT-project-filter body can carry an enclosing statement's
        // WHERE conjunct: it applies its projection and its own WHERE to every
        // row and nothing else, so an extra filter there is the same filter one
        // level up. Anything that reads the row set as a whole — DISTINCT, a row
        // limit, a grouping, a window, an ORDER BY the limit would pair with —
        // would see a different row set and declines.
        if (!distinct && windows.Count == 0 && orderBy.Count == 0 && !selection.HasTopOrOffsetOrFetch
            && intoTarget is null && !isAssignmentOnly && sources.Length > 0)
        {
            if (aggregates.Count == 0 && fromClause.GroupingSets.Count == 0 && fromClause.Having is null)
            {
                var pushdownShape = new ProjectionPushdown(
                    outputSchema, outputColumnNames, sources, joins, expressions, fromClause.Excluders, orderBy);
                selection.PredicatePushdown = templates => BuildPushedProjection(pushdownShape, templates);
            }
            // A GROUP BY body takes a conjunct on a column it groups by: such a
            // filter removes whole groups, and a group the enclosing statement
            // discards anyway feeds no other group's aggregate or HAVING. The
            // slots that qualify are computed once here; a body grouped only by
            // expressions offers none and carries no delegate at all.
            else if (fromClause.GroupingSets.Count > 0 && !resultIsProvablyEmpty
                && GroupingColumnProjections(expressions, sources, fromClause) is { } groupingColumns)
            {
                var groupedShape = new AggregatePushdown(
                    outputSchema, outputColumnNames, sources, joins, ResolveColumnType, expressions, fromClause,
                    orderBy, aggregates, windows, windowOperandTypes, windowResultTypes, groupingColumns);
                selection.PredicatePushdown = templates => BuildPushedAggregate(groupedShape, templates);
                selection.PushdownIsGrouped = true;
            }
        }
        // A body whose only window function is a bare ROW_NUMBER() projection
        // can answer an enclosing WHERE's constant bound on that row number from
        // the top rows of each partition alone — see RowNumberWindowShape for
        // why the one-window rule is what makes that legal. Every other shape
        // gate is the pushdown block's: anything reading the row set as a whole
        // would see a different one once the bound drops rows.
        if (!distinct && orderBy.Count == 0 && !selection.HasTopOrOffsetOrFetch
            && intoTarget is null && !isAssignmentOnly && sources.Length > 0
            && aggregates.Count == 0 && fromClause.GroupingSets.Count == 0 && fromClause.Having is null
            && windows is [{ Kind: WindowKind.RowNumber, Frame: null } rowNumberWindow]
            && ProjectedWindowOrdinal(expressions, rowNumberWindow) is >= 0 and var rowNumberOrdinal)
        {
            var boundedShape = new RowNumberWindowShape(
                outputSchema, outputColumnNames, sources, joins, expressions, fromClause.Excluders,
                rowNumberWindow, rowNumberOrdinal);
            selection.RowNumberBoundPushdown = bound => BuildBoundedRowNumberPlan(boundedShape, bound);
        }

        return selection;
    }

    /// <summary>
    /// Evaluates the expressions real runs once as its plan starts, for the
    /// error one of them raises; see
    /// <see cref="ConstantFolding.CollectStartupConstants"/>.
    /// </summary>
    internal static void RunStartupConstants(List<Expression> constants, BatchContext batch)
    {
        if (constants.Count == 0)
            return;
        var runtime = new RuntimeContext(static _ => throw new InvalidOperationException("A startup constant reads no column."), batch);
        foreach (var constant in constants)
        {
            try
            {
                _ = constant.Run(runtime);
            }
            catch (SimulatedSqlException failure) when (ConstantFolding.FoldsClrParseFailure(failure, constant, batch))
            {
                throw SimulatedSqlException.ClrTypeParseFoldedAtCompile(failure);
            }
        }
    }

    /// <summary>
    /// The output ordinal at which <paramref name="window"/> is projected on its
    /// own (through an alias, unchanged), or -1 when the projection wraps it in
    /// an expression — where the enclosing statement's filter names a value the
    /// row number isn't, so no bound on that column bounds the window.
    /// </summary>
    private static int ProjectedWindowOrdinal(List<Expression> expressions, WindowExpression window)
    {
        for (var i = 0; i < expressions.Count; i++)
        {
            var projected = expressions[i];
            while (projected is NamedExpression named)
                projected = named.Inner;
            while (projected is Parenthesized parenthesized)
                projected = parenthesized.Wrapped;
            if (ReferenceEquals(projected, window))
                return i;
        }

        return -1;
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
    /// COLMETADATA fNullable flag), following
    /// <see cref="Expression.ResultIsNullable"/>'s rules (direct refs preserve
    /// base-column nullability, literals NOT NULL, other expressions nullable)
    /// over a per-source NULL-fill map, so a column on the preserved side of
    /// an outer join keeps its base nullability while one on the NULL-filled
    /// side reads nullable whatever the base column says. The
    /// zero-source (FROM-less) case is included so a bare literal projection
    /// reports NOT NULL like real (<c>select 1</c> → <c>Int</c>, not
    /// <c>IntN</c>); a column reference can't appear without a source, so the
    /// resolver is never consulted there. Load-bearing for DacFx bacpac
    /// export: its BCP data-file layout drops the per-value length prefix
    /// on fixed-width columns whose wire metadata says NOT NULL, and the
    /// bacpac loader reads the file per the model.xml declaration — the two
    /// must agree. Under written grouping sets
    /// (<see cref="FromClause.GroupingSetsWritten"/>) every column reference
    /// reads nullable, since a set that groups the column away emits it NULL;
    /// claiming NOT NULL there and then sending a NULL kills the TDS session.
    /// </summary>
    private static bool[]? ComputeColumnNullability(
        List<Expression> expressions,
        FromSource[] sources,
        JoinSpec[] joins,
        bool groupingSetsWritten,
        BatchContext parseBatch,
        Func<MultiPartName, SqlType> resolveColumnType)
    {
        var nullFilled = NullFilledSources(sources, joins);

        bool ResolveNullable(MultiPartName name)
        {
            if (groupingSetsWritten)
                return true;
            var (s, c) = FindSourceColumn(sources, name);
            return s == -1 || nullFilled[s] || sources[s].Columns[c].Nullable;
        }

        var context = new NullabilityContext(parseBatch, ResolveNullable, resolveColumnType);
        var nullability = new bool[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
            nullability[i] = expressions[i].ResultIsNullable(context);
        return nullability;
    }

    /// <summary>
    /// Per-source flags: true where an outer join can emit the source's slot
    /// NULL-filled, so every column it offers reads nullable regardless of the
    /// base column's own declaration.
    /// </summary>
    /// <remarks>
    /// Mirrors the fold the executor runs: <c>joins[level - 1]</c> attaches
    /// the operand spanning <c>[level, level + GroupCount)</c> to the
    /// accumulated left spine <c>[start, level)</c>. LEFT and OUTER APPLY
    /// NULL-fill the right operand, RIGHT the left spine, and FULL both; a
    /// parenthesized join group recurses so its interior structure is read the
    /// same way. Flags only ever turn on — a slot NULL-filled by any join in
    /// the chain stays nullable downstream.
    /// </remarks>
    internal static bool[] NullFilledSources(FromSource[] sources, JoinSpec[] joins)
    {
        var nullFilled = new bool[sources.Length];
        if (joins.Length > 0)
            MarkNullFilled(joins, start: 0, count: sources.Length, nullFilled);
        return nullFilled;
    }

    private static void MarkNullFilled(JoinSpec[] joins, int start, int count, bool[] nullFilled)
    {
        var level = start + 1;
        var end = start + count;
        while (level < end && level - 1 < joins.Length)
        {
            var join = joins[level - 1];
            var span = Math.Min(join.GroupCount, end - level);
            switch (join.Kind)
            {
                case JoinKind.Left or JoinKind.OuterApply:
                    for (var i = level; i < level + span; i++)
                        nullFilled[i] = true;
                    break;
                case JoinKind.Right:
                    for (var i = start; i < level; i++)
                        nullFilled[i] = true;
                    break;
                case JoinKind.Full:
                    for (var i = start; i < level + span; i++)
                        nullFilled[i] = true;
                    break;
                default:
                    break;
            }
            if (span > 1)
                MarkNullFilled(joins, level, span, nullFilled);
            level += span;
        }
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
    /// Per projection column, whether it is a scalar expression — neither a
    /// column reference nor an aggregate or window function, which real
    /// doesn't count as computed (probed 2026-09-24).
    /// </summary>
    internal static bool[]? ComputedColumnsOf(Selection selection)
    {
        if (selection.ProjectionExpressions is not { } expressions)
            return null;
        var computed = new bool[expressions.Length];
        for (var i = 0; i < expressions.Length; i++)
        {
            var expression = expressions[i];
            while (expression is Expressions.NamedExpression named)
                expression = named.Inner;
            computed[i] = expression is not (Expressions.Reference or Expressions.AggregateExpression or Expressions.WindowExpression or Expressions.NextValueFor);
        }
        return computed;
    }

    /// <summary>
    /// Per projection column, the COLMETADATA flags
    /// <see cref="SimulatedQueryResult.ColumnWireFlags"/> describes: a column
    /// read takes its source column's character, traced through a view or a
    /// derived table's own flags; anything else is an expression or an
    /// aggregate.
    /// </summary>
    private static byte[] WireFlagsOf(List<Expression> expressions, FromSource[] sources, int[] source, int[] ordinal)
    {
        var flags = new byte[expressions.Count];
        for (var i = 0; i < flags.Length; i++)
        {
            var expression = expressions[i];
            while (expression is Expressions.NamedExpression named)
                expression = named.Inner;
            flags[i] = expression switch
            {
                // A sequence draw reads as neither updatable nor computed
                // (probed 2026-10-04 against SQL Server 2025).
                Expressions.AggregateExpression or Expressions.WindowExpression or Expressions.NextValueFor => 0x00,
                Reference when source[i] >= 0 => SourceColumnWireFlags(sources[source[i]], ordinal[i]),
                Reference => 0x08,
                _ => 0x20,
            };
        }
        return flags;
    }

    private static byte SourceColumnWireFlags(FromSource from, int ordinal)
    {
        // A FOR SYSTEM_TIME source is read-only, as is a period column
        // (probed 2026-10-04 against SQL Server 2025).
        if (from.Rows is TemporalRowSource)
            return 0x00;
        // A derived table or CTE passes its columns' updatability through but
        // not their computed flag (probed 2026-09-26 against SQL Server 2025).
        if (from.LateralPlan is { ColumnWireFlags: { } inner } && ordinal < inner.Length)
            return from.LateralIsQueryBody ? (byte)(inner[ordinal] & ~0x20) : inner[ordinal];
        // An updatable view's column traces to the base column behind it, and
        // one of its expressions is computed.
        if (from.BackingView is { BaseTable: not null } expressionView && expressionView.BaseColumnOrdinals[ordinal] < 0)
            return 0x20;
        var column = from.BackingView is { BaseTable: { } baseTable } view && view.BaseColumnOrdinals[ordinal] is >= 0 and var baseOrdinal
            ? baseTable.Columns[baseOrdinal]
            : from.Columns[ordinal];
        return column switch
        {
            { Identity: not null } => 0x10,
            // A graph pseudo-column reads as updatable, not computed (probed
            // 2026-09-27 against SQL Server 2025).
            { Computed: not null, GraphKind: GraphColumnKind.None } => 0x20,
            { Type: RowVersionSqlType } or { GeneratedAs: not GeneratedAlwaysAsRow.None } => 0x00,
            _ => 0x08,
        };
    }

    /// <summary>
    /// Per projection column, the base-table column it reads directly, or null
    /// for an expression or a column of any other source; null when no column
    /// reads a base table.
    /// </summary>
    internal static HeapColumn?[]? BaseColumnOrigins(Selection selection)
    {
        if (selection.AutoColumnSource is not { } source || selection.AutoColumnOrdinal is not { } ordinal || selection.BranchFromSources is not { } sources)
            return null;
        HeapColumn?[]? origins = null;
        for (var i = 0; i < source.Length; i++)
        {
            if (source[i] < 0)
                continue;
            var from = sources[source[i]];
            // A view column traces through to the base column behind it.
            var column = from.BackingTable is not null ? from.Columns[ordinal[i]]
                : from.BackingView is { BaseTable: { } baseTable } view && view.BaseColumnOrdinals[ordinal[i]] is >= 0 and var baseOrdinal ? baseTable.Columns[baseOrdinal]
                : null;
            if (column is not null)
                (origins ??= new HeapColumn?[source.Length])[i] = column;
        }
        return origins;
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
    /// Raises Msg 493 for a <c>.nodes()</c> column <paramref name="expression"/>
    /// reads outside an xml method's receiver or an <c>IS [NOT] NULL</c> test.
    /// </summary>
    private static void RejectDirectNodesColumnRead(ExpressionNode expression, FromSource[] sources) =>
        expression.Walk((node, shape) => node switch
        {
            Reference reference when ReadsNodesColumn(sources, reference)
                => throw SimulatedSqlException.NodesColumnUsedDirectly(reference.ReferencedName.Leaf),
            // A conversion of the column is Msg 525 naming the target type
            // (probed 2026-09-25) — its shape reports the type, then the source.
            Cast or ConvertExpression when shape.ChildNodes[0] is Reference converted && ReadsNodesColumn(sources, converted)
                => throw SimulatedSqlException.NodesColumnCannotConvert((SqlType)shape.Locals[1]!),
            // IS [NOT] NULL may test the column itself, not a conversion of it.
            BooleanExpression.IsNullExpression when shape.ChildNodes[0] is Reference => false,
            XmlMethodCall or Reference => false,
            _ => true,
        });

    private static bool ReadsNodesColumn(FromSource[] sources, Reference reference) =>
        TryResolveSourceColumn(sources, reference.ReferencedName) is { } id && sources[id.Source].XmlReceiverName is not null;

    /// <summary>Whether <paramref name="operand"/> is a bare reference to a source column with no type.</summary>
    private static bool ReadsUntypedNullColumn(FromSource[] sources, Expression? operand)
    {
        return operand is Reference reference
            && TryResolveSourceColumn(sources, reference.ReferencedName) is { } id
            && sources[id.Source].Columns[id.Column].IsUntypedNull;
    }

    /// <summary>
    /// The Dynamic Data Masking mask a reference to <paramref name="name"/>
    /// reads through: a table column's own, or the one a derived source's
    /// column passes on; null for an unmasked or outer-scope column.
    /// </summary>
    internal static DataMask? SourceColumnMask(FromSource[] sources, MultiPartName name)
    {
        var (s, c) = FindSourceColumn(sources, name);
        if (s < 0)
            return null;
        var source = sources[s];
        var column = source.Columns[c];
        if (column.DerivedMask is { } derived)
            return derived;
        if (source.BackingTable is not { } table)
            return null;
        var index = ReferenceEquals(source.Columns, table.Columns) ? c : Array.IndexOf(table.Columns, column);
        return index < 0 ? null : DataMask.ForTableColumn(table, index);
    }

    // The projection's column masks. A static method of its own so the lambdas
    // capture only these parameters: written inline in BuildSqlProjection, the
    // one reading parseBatch hoisted it into the closure the plan's row source
    // shares, and every cached plan then held its parsing batch.
    private static DataMask?[]? ProjectionMasks(BatchContext parseBatch, List<Expression> expressions, FromSource[] sources, Func<MultiPartName, DataMask?>? outerMask, Func<MultiPartName, SqlType> resolveColumnType) =>
        DataMask.OfProjection(parseBatch, expressions, name => ScopedColumnMask(sources, name, outerMask), expression => expression.GetSqlType(parseBatch, resolveColumnType));

    /// <summary>
    /// <see cref="SourceColumnMask"/> for a name this scope's sources bind,
    /// else the enclosing scope's answer through <paramref name="outer"/>.
    /// </summary>
    internal static DataMask? ScopedColumnMask(FromSource[] sources, MultiPartName name, Func<MultiPartName, DataMask?>? outer) =>
        FindSourceColumn(sources, name).SourceIndex >= 0 ? SourceColumnMask(sources, name) : outer?.Invoke(name);

    private static Schemas.AliasType?[]? ColumnAliasTypesOf(List<Expression> expressions)
    {
        Schemas.AliasType?[]? aliases = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (expressions[i].ResultAliasType is { } alias)
                (aliases ??= new Schemas.AliasType?[expressions.Count])[i] = alias;
        }
        return aliases;
    }

    /// <summary>Computes <see cref="ColumnIdentitySources"/>.</summary>
    private static IdentityState?[]? ColumnIdentitySourcesOf(List<Expression> expressions, FromSource[] sources, JoinSpec[] joins)
    {
        if (Array.Exists(joins, join => join.Kind is not (JoinKind.CrossApply or JoinKind.OuterApply)))
            return null;
        IdentityState?[]? identity = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (UnwrapDirectRef(expressions[i]) is not { } reference)
                continue;
            var (s, c) = FindSourceColumn(sources, reference.ReferencedName);
            if (s >= 0 && (sources[s].Columns[c].Identity ?? sources[s].Columns[c].IdentitySource) is { } source)
                (identity ??= new IdentityState?[expressions.Count])[i] = source;
        }
        return identity;
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
        List<AggregateExpression> aggregates)
    {
        if (distinct)
            return (null, ViewUpdatabilityRejection.Distinct);
        if (aggregates.Count > 0)
            return (null, ViewUpdatabilityRejection.Aggregate);
        if (fromClause.GroupingSets.Count > 0 || fromClause.Having is not null)
            return (null, ViewUpdatabilityRejection.GroupBy);

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
    /// Replaces every deferred source whose rows are fixed for the duration of
    /// this enumeration with a copy whose rows are already materialized into a
    /// re-enumerable list, executing each such plan exactly once per query
    /// execution instead of once per left-side row. After materialization the
    /// source carries a plain <see cref="FromSource.Rows"/> list, so
    /// <c>TryPlanEquiJoin</c> keys it into the O(L + R) hash path and any
    /// residual nested loop re-scans the list instead of re-executing.
    /// Returns the input array unchanged (no copy) when no source qualifies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two kinds of source qualify, both decided by
    /// <see cref="MaterializesOncePerEnumeration"/>. A
    /// <see cref="FromSource.MaterializeOnce"/> catalog view is uncorrelated by
    /// construction — its generator ignores the outer resolver — and qualifies
    /// wherever it sits. Every other qualifying source is a
    /// <em>non-leftmost, non-APPLY</em> source whose plan is a query body with
    /// its own name scope: a derived table, a CTE reference or a view. SQL
    /// Server requires <c>APPLY</c> for laterality, so such a body cannot read
    /// a sibling FROM source (the simulator's parser reports Msg 207 where real
    /// reports Msg 4104); it can read an <em>enclosing</em> statement's row, but
    /// that row is fixed for one execution of this Selection — the enclosing
    /// query re-executes the whole plan per enclosing row — so every
    /// re-execution within one enumeration would return identical rows.
    /// </para>
    /// <para>
    /// The leftmost source is left deferred: a fold range's leftmost slot
    /// already executes its plan exactly once and streams, so materializing it
    /// buys nothing and costs the buffer. The leftmost slot of a parenthesized
    /// join group is skipped for the same reason.
    /// </para>
    /// <para>
    /// A source whose rows a <em>generator</em> produces — a TVF, VALUES,
    /// OPENJSON / OPENXML, STRING_SPLIT, PIVOT, a linked-server query — never
    /// qualifies: its arguments are parsed in the enclosing FROM's scope, so
    /// they can read a sibling source's column per row. Real rejects that shape
    /// outright (Msg 4104 for <c>FROM t JOIN STRING_SPLIT(t.csv, ',') s ON …</c>,
    /// probe-confirmed) while the simulator answers it, and materializing would
    /// silently freeze the first row's argument values instead.
    /// </para>
    /// <para>
    /// A per-call-varying built-in inside the plan keeps the reuse only where
    /// the plan says how its draws reach this reader
    /// (<see cref="VolatileColumns"/>): a body that reads the value itself
    /// (DISTINCT, an ORDER BY key under TOP) draws once per body row on real
    /// too, and a body whose every drawing column is re-drawn per output row
    /// (<see cref="FromSource.VolatileRefresh"/>) reads the same however often
    /// it runs. Any other plan that drew — its body unknown here, or a draw
    /// reading a body column — declines through the same
    /// <see cref="SimulatedDbConnection.VolatileEvaluations"/> gate the
    /// uncorrelated-subquery memo applies (see
    /// <see cref="UncorrelatedSubqueryCache"/>), keeping its per-row execution;
    /// the probing execution's rows are discarded — <c>NEXT VALUE FOR</c>, the
    /// other counter-bumping built-in, is Msg 11719 on real inside any of these
    /// bodies, so the discarded execution's only reachable side effect is an
    /// unobservable extra <c>NEWID()</c> draw.
    /// </para>
    /// </remarks>
    private static FromSource[] MaterializeUncorrelatedDeferredSources(
        FromSource[] sources, JoinSpec[] joins, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].LateralPlan is not { } plan || !MaterializesOncePerEnumeration(sources, joins, i))
                continue;
            // A whole cacheable catalog view is its cached rowset, handed over
            // as is so a hash join can probe the rowset's persisted index.
            if (sources[i] is { BackingCatalogView: { Cacheable: true } view, BackingCatalogDatabase: { } catalogDatabase, CatalogSeek: false })
            {
                PermissionEnforcement.CheckCatalogViewRead(batch, view, catalogDatabase);
                if (CachedCatalogRows(view, batch, catalogDatabase) is { } set)
                {
                    rewritten ??= (FromSource[])sources.Clone();
                    rewritten[i] = sources[i].WithMaterializedRows(set.Rows, set);
                    continue;
                }
            }
            if (StatementMaterializedRows(batch, plan) is not { } materialized)
            {
                var volatileEvaluationsAtStart = batch.Connection.VolatileEvaluations;
                // Inside an enclosing query the execution is watched, as a
                // subquery's is, for whether its rows can serve the statement.
                OuterRowProbe? probe = null;
                var enclosing = default(RuntimeContext);
                if (outerResolver is not null)
                {
                    enclosing = new RuntimeContext(outerResolver, batch);
                    probe = new OuterRowProbe(enclosing);
                }
                materialized = [.. plan.Execute(batch, probe?.Resolver ?? outerResolver).RowBytes];
                // A body that draws its values once, or whose every drawing column
                // the reader re-draws per output row, reads the same whatever
                // re-runs it; only an unknown or partly re-drawable body keeps its
                // per-row execution.
                if (batch.Connection.VolatileEvaluations != volatileEvaluationsAtStart
                    && plan.VolatileColumns is not ({ FixesValues: true } or { Complete: true }))
                {
                    continue;
                }
                if (probe is not null && probe.CanReplay(enclosing))
                    (batch.CurrentStatement.SubqueryResults ??= new Dictionary<object, object>(ReferenceEqualityComparer.Instance))[plan] = materialized;
            }
            rewritten ??= (FromSource[])sources.Clone();
            rewritten[i] = sources[i].WithMaterializedRows(materialized);
        }
        return rewritten ?? sources;
    }

    /// <summary>
    /// The rows an earlier enumeration in this statement materialized for
    /// <paramref name="plan"/>, or null when it has to run. An enumeration
    /// that runs inside an enclosing query re-runs per enclosing row — the
    /// body of a correlated <c>EXISTS</c> once per outer row — so a body that
    /// read neither that row nor a per-call-varying built-in is stored on the
    /// statement under its plan, as <see cref="UncorrelatedSubqueryCache"/>
    /// stores a subquery's result, and every later enumeration reuses its
    /// rows: the statement is the scope over which the data it read is fixed.
    /// </summary>
    private static List<byte[]>? StatementMaterializedRows(BatchContext batch, Selection plan) =>
        batch.CurrentStatement.SubqueryResults is { } memo && memo.TryGetValue(plan, out var rows) ? (List<byte[]>)rows : null;

    /// <summary>
    /// The row-source passes a <b>joined UPDATE / DELETE</b> takes before it
    /// enumerates its join tuples: the once-per-enumeration materialization of a
    /// deferred source, then the WHERE narrowing of every source but the
    /// mutation target. Returns <paramref name="sources"/> unchanged (no copy)
    /// when neither applies; <paramref name="joins"/> is never rewritten, so the
    /// written join order stands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A joined mutation reads its whole row set through
    /// <see cref="EnumerateJoinedRows"/> and writes nothing until that
    /// enumeration is over — the loop only collects <c>(page, slot)</c>
    /// addresses and computed values, and the commit phase applies them. A
    /// source reading the mutation target therefore sees the pre-statement rows
    /// however many times it runs, which is what makes running it once instead
    /// of once per target row a pure cost reduction rather than a change of
    /// answer, and is the direction real works anyway: probe-confirmed against
    /// SQL Server 2025, <c>UPDATE t SET v = d.m FROM #t t JOIN (SELECT MAX(v) AS
    /// m FROM #t) d ON 1 = 1</c> over <c>(10, 20, 30)</c> leaves every row at
    /// <c>30</c> there, and the <c>NEWID()</c> gate the materialization already
    /// carries is what keeps the shape real re-draws per row (five distinct
    /// values over five target rows, probe-confirmed) re-executing.
    /// </para>
    /// <para>
    /// The <b>target</b> source is left exactly as it enumerates. The write
    /// pipeline reaches each affected row through an address side-channel keyed
    /// by the <c>byte[]</c> instances that enumerator yields, and its lock / undo
    /// bookkeeping is settled per row it touches; narrowing it would be a change
    /// to the write path rather than to a read, so it stays out of this pass
    /// (the single-table mutation seek in <c>SeekMutationTarget</c> is where that
    /// question is answered). The narrowed-source-first reorder is declined
    /// outright for the same reason — the target's slot is identified by index.
    /// </para>
    /// </remarks>
    internal static FromSource[] PrepareMutationJoinSources(
        FromSource[] sources, JoinSpec[] joins, BooleanExpression? where, int targetIndex, BatchContext batch)
    {
        // Skip mode commits nothing, so both passes are pure cost there — and
        // the materializing execution would run a deferred body on behalf of a
        // statement that never runs, which can raise where the per-outer-row
        // execution never reached one (an empty target drives no rows). The
        // join-view write path declines its whole enumeration for the same
        // reason.
        if (batch.IsSkipping)
            return sources;
        // The read path's grouped-body reduction, reached with the mutation's
        // own WHERE: the pass only ever rewrites a deferred GROUP BY body's
        // slot, which the target — a base table the write pipeline addresses
        // row by row — can never be. It reads the partner side (which the
        // target may well be) ahead of the enumeration, where the pre-statement
        // rows are what a joined mutation reads however many times it runs.
        List<BooleanExpression> excluders = where is null ? [] : [where];
        sources = ReduceGroupedBodiesByJoinKeys(sources, joins, excluders, batch, outerResolver: null);
        sources = MaterializeUncorrelatedDeferredSources(sources, joins, batch, outerResolver: null);
        return where is null ? sources : NarrowMutationJoinSources(sources, where, targetIndex, batch);
    }

    /// <summary>
    /// Whether the deferred source at <paramref name="index"/> produces the same
    /// rows on every execution within one enumeration of
    /// <paramref name="sources"/>, so its plan can run once instead of once per
    /// left-side row. See <see cref="MaterializeUncorrelatedDeferredSources"/>
    /// for the reasoning behind each clause.
    /// </summary>
    private static bool MaterializesOncePerEnumeration(FromSource[] sources, JoinSpec[] joins, int index)
    {
        var source = sources[index];
        if (source.MaterializeOnce)
            return true;
        if (index == 0)
            return false;
        var join = joins[index - 1];
        return join.Kind is not (JoinKind.CrossApply or JoinKind.OuterApply)
            && join.GroupCount == 1;
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
            && TryApplyOrderedScan(sources, joins, orderBy, excluders, offsetCount ?? 0, batch, outerResolver, out var orderedSources, out var skipped))
        {
            return ProjectStreaming(orderedSources, joins, expressions, excluders, top.Count, offsetCount - skipped, fetchCount, batch, outerResolver);
        }
        if (!hasJoinGroup && !distinct && !top.RequiresBuffering && orderBy.Count > 0
            && TryApplyLeftmostOrderedScan(sources, joins, expressions, orderBy, excluders, batch, outerResolver) is { } leftmostOrdered)
        {
            return ProjectStreaming(leftmostOrdered, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver);
        }
        if (!hasJoinGroup && !distinct && !top.RequiresBuffering && orderBy.Count > 0
            && TrySortLeftmostSource(sources, joins, expressions, orderBy, excluders, batch, outerResolver) is { } leftmostSorted)
        {
            return ProjectStreaming(leftmostSorted, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver);
        }

        if (!hasJoinGroup)
            sources = MaybeApplyIndexSeek(sources, joins, excluders, batch, outerResolver);
        (sources, joins) = NarrowJoinSources(sources, joins, excluders, batch, outerResolver);
        return !distinct && orderBy.Count == 0 && !top.RequiresBuffering
            ? ProjectStreaming(sources, joins, expressions, excluders, top.Count, offsetCount, fetchCount, batch, outerResolver)
            : !distinct && orderBy.Count > 0
            ? ProjectSorted(sources, joins, expressions, excluders, outputColumnNames, orderBy, top, offsetCount, fetchCount, batch, outerResolver)
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

    /// <summary>
    /// A FROM source simple parameterization reads as one table: a permanent
    /// table, a view or a catalog view, where a <c>#temp</c> table, a table
    /// variable, a derived table, a CTE or a function's rows decline it.
    /// </summary>
    private static bool IsParameterizableSource(FromSource source) =>
        source.DerivedTable is null
        && (source.BackingCatalogView is not null
            || source.BackingView is not null
            || (source.BackingTable is { IsTableVariable: false } table && !table.Name.StartsWith('#')));

    /// <summary>
    /// <paramref name="rows"/> from an operator real runs to completion ahead
    /// of any row it passes on — an aggregate, a constant scan — so an error it
    /// raises is never one an <c>INSERT</c> draws an identity value for (see
    /// <see cref="SimulatedSqlException.RaisedInRowProjection"/>).
    /// </summary>
    internal static IEnumerable<T> AheadOfRowProjection<T>(IEnumerable<T> rows)
    {
        using var enumerator = rows.GetEnumerator();
        while (true)
        {
            bool moved;
            try
            {
                moved = enumerator.MoveNext();
            }
            catch (SimulatedSqlException error)
            {
                error.RaisedInRowProjection = false;
                throw;
            }
            if (!moved)
                yield break;
            yield return enumerator.Current;
        }
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
        return ApplyOffsetTake(InnerStream(), offsetCount: null, topCount ?? fetchCount);

        IEnumerable<SqlValue[]> InnerStream()
        {
            // The OFFSET's rows pass the WHERE but are never projected: real
            // evaluates the select list above its Top, so an expression that
            // would raise on a skipped row doesn't (probed 2026-09-30 against
            // SQL Server 2025), and a deep page skips the projection's cost.
            var skip = offsetCount ?? 0;
            // Hoisted per-row resolution scaffolding: one mutable-capture
            // tuple slot, one cached self-referencing resolver lambda, one
            // RuntimeContext — instead of a fresh closure + several delegates
            // per row (the allocation profile's dominant entry).
            var memo = new SourceColumnMemo();
            var currentTuple = default(byte[]?[])!;
            Func<MultiPartName, SqlValue> resolveColumn = null!;
            resolveColumn = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, memo);
            var rowRuntime = new RuntimeContext(resolveColumn, batch);
            foreach (var tuple in EnumerateJoinedRows(sources, joins, batch, outerResolver))
            {
                currentTuple = tuple;
                var include = true;
                try
                {
                    foreach (var excluder in excluders)
                    {
                        if (excluder.Run(rowRuntime) != true)
                        {
                            include = false;
                            break;
                        }
                    }
                }
                catch (SimulatedSqlException filterError)
                {
                    filterError.RaisedInRowProjection = false;
                    throw;
                }
                if (!include)
                    continue;
                if (skip > 0)
                {
                    skip--;
                    continue;
                }

                // Per-row stamp bump so NEXT VALUE FOR in the projection
                // advances per output row (and dedupes across same-row
                // instances). Bump only on rows that pass WHERE — excluded
                // rows shouldn't burn sequence values.
                batch.BumpRowStamp();
                var projected = new SqlValue[expressions.Count];
                try
                {
                    for (var i = 0; i < expressions.Count; i++)
                        projected[i] = expressions[i].Run(rowRuntime);
                }
                catch (SimulatedSqlException projectionError)
                {
                    projectionError.RaisedInRowProjection = true;
                    throw;
                }

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
        resolveSource = name => ResolveAcrossTuple(sources, currentTuple, name, batch, outerResolver, memo);
        var rowRuntime = new RuntimeContext(resolveSource, batch);

        // Computed once: the column each projection reads, for the DISTINCT
        // ORDER BY check (a term may name the source column behind an alias).
        var projectionSources = ProjectionSourceReferences(expressions);
        // Everything here is read ahead of the first row out, so an error any
        // row raises precedes every INSERT identity draw.
        try
        {
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
        }
        catch (SimulatedSqlException bufferedError)
        {
            bufferedError.RaisedInRowProjection = false;
            throw;
        }

        IEnumerable<(SqlValue[] Projected, SqlValue[] Keys)> filtered = buffer;
        if (distinct)
        {
            var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
            filtered = buffer.Where(item => seen.Add(item.Projected));
            NoteGroupingWorktable(batch, sources, expressions);
        }

        var materialized = filtered.ToList();

        if (orderBy.Count > 0)
        {
            var ranked = new TopRows<SqlValue[]>(int.MaxValue, orderBy, top.WithTies);
            for (var i = 0; i < materialized.Count; i++)
                ranked.Add(TopRowAdmission.Admitted, materialized[i].Projected, materialized[i].Keys, i);
            NoteSortWorktable(batch, sources, orderBy, expressions);
            foreach (var entry in ranked.Rank(offsetCount ?? 0, RankWindowEnd(top, offsetCount, fetchCount, ranked.Count)))
                yield return entry.Payload;
            yield break;
        }

        if (distinct)
            SortDistinctRows(materialized, static item => item.Projected, sources, joins, expressions);

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
