using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Rebuilds this plan with extra WHERE conjuncts an enclosing statement
    /// pushed into it, or returns null when the plan's shape can't take them.
    /// Set only on the two plans a FROM source reads a <em>query body</em>
    /// through — the projection plan <see cref="BuildSqlProjection"/> builds for
    /// a derived table / CTE / view body, and the <see cref="ForView"/> wrapper
    /// that parses a view's body per execution — so every other
    /// <see cref="FromSource.LateralPlan"/> (a TVF, VALUES, OPENJSON, PIVOT, a
    /// catalog view, a linked-server query) declines by carrying no delegate at
    /// all.
    /// <para>
    /// The conjuncts arrive as <em>templates</em>: each column operand is a
    /// <see cref="ProjectionSlot"/> naming an output-column ordinal of this
    /// plan, and every other operand is already an evaluated constant. That is
    /// what lets one template cross into a view body the caller hasn't parsed
    /// yet (and into a child <see cref="BatchContext"/> holding none of the
    /// caller's variables). Rebinding replaces each slot with the body's own
    /// projection expression, so it declines a slot that projects anything but
    /// a plain column.
    /// </para>
    /// <para>
    /// Never mutates: a push returns a new <see cref="Selection"/> over the same
    /// parse-time tree, per the shared-plan contract in
    /// <c>docs/claude/plan-cache.md</c>.
    /// </para>
    /// </summary>
    internal Func<List<BooleanExpression>, Selection?>? PredicatePushdown;

    /// <summary>
    /// True when <see cref="PredicatePushdown"/> belongs to a <b>GROUP BY</b>
    /// body — one whose eligible slots are plain projections of grouping
    /// columns. Such a body aggregates every row underneath it however few of
    /// its groups the enclosing statement keeps, which is what makes it worth
    /// reducing to a join's key set
    /// (<see cref="ReduceGroupedBodiesByJoinKeys"/>); a plain
    /// project-filter body carries no such multiplier and is left to the
    /// ordinary passes.
    /// </summary>
    internal bool PushdownIsGrouped;

    /// <summary>
    /// Pushes the enclosing statement's eligible WHERE conjuncts into every FROM
    /// source that reads through a query body — a view, a derived table or a CTE
    /// reference — so the filter reaches the base scan (where the index seek can
    /// use it) instead of running only after the body has produced every row.
    /// Returns <paramref name="sources"/> unchanged (no copy) when nothing
    /// pushes; otherwise a clone whose pushed slots read through a rebuilt plan.
    /// <para>
    /// <b>The pushed conjunct stays in the enclosing WHERE.</b> That is the same
    /// residual invariant the join-source narrowing rests on (see
    /// <see cref="NarrowJoinSources"/>), and it is what makes the push
    /// semantics-preserving for every join kind: the conjunct shapes that push
    /// are all NULL-rejecting, so a tuple an outer join NULL-extends because the
    /// pushed side lost its match reads UNKNOWN for the very conjunct that
    /// justified the push and is excluded — exactly as the matched-but-failing
    /// tuple was excluded before.
    /// </para>
    /// <para>
    /// Runs <em>before</em> <see cref="MaterializeUncorrelatedDeferredSources"/>,
    /// so a source that then materializes materializes the narrowed rowset.
    /// Recursion needs no loop: the rebuilt body runs this same pass over its own
    /// FROM, which is what carries a filter down a chain of views.
    /// </para>
    /// </summary>
    private static FromSource[] PushWhereIntoDeferredSources(
        FromSource[] sources, List<BooleanExpression> excluders, BatchContext batch)
    {
        if (excluders.Count == 0)
            return sources;

        List<BooleanExpression>? conjuncts = null;
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].LateralPlan is not { } plan || plan.PredicatePushdown is not { } push)
                continue;
            if (conjuncts is null)
            {
                conjuncts = [];
                foreach (var excluder in excluders)
                    excluder.CollectConjuncts(conjuncts);
            }

            List<BooleanExpression>? templates = null;
            foreach (var conjunct in conjuncts)
            {
                if (TryTemplateConjunct(conjunct, sources, i, batch) is { } template)
                    (templates ??= []).Add(template);
            }

            if (templates is null || push(templates) is not { } pushed)
                continue;
            rewritten ??= (FromSource[])sources.Clone();
            rewritten[i] = sources[i].WithPushedPlan(pushed);
        }

        return rewritten ?? sources;
    }

    /// <summary>
    /// Turns one top-level WHERE conjunct into a template the source at
    /// <paramref name="index"/> can carry into its body, or returns null when
    /// the conjunct doesn't qualify.
    /// <para>
    /// Every column operand has to resolve to <em>that</em> source — a sibling's
    /// column isn't in the body's scope, and an enclosing scope's column could
    /// silently rebind to a same-named body column — and it becomes a
    /// <see cref="ProjectionSlot"/> naming the source's column ordinal. Every
    /// other operand has to be row-independent (a literal, a variable, a
    /// parameter, or arithmetic over those), and is evaluated <b>here</b>, once:
    /// the value is fixed for the whole enumeration by construction, and a
    /// constant is the only operand a view body's own batch can read, since it
    /// holds none of the caller's variables. An operand that raises while
    /// evaluating declines the conjunct rather than reporting early.
    /// </para>
    /// <para>
    /// The shape whitelist lives in
    /// <see cref="BooleanExpression.TryRebindOperands"/> — the comparison
    /// family, <c>BETWEEN</c>, and the <c>IN</c> / OR-of-equalities family, all
    /// of them NULL-rejecting, which is what the residual invariant above needs.
    /// </para>
    /// </summary>
    private static BooleanExpression? TryTemplateConjunct(
        BooleanExpression conjunct, FromSource[] sources, int index, BatchContext batch)
    {
        var namesTheSource = false;
        var template = RebindTemplate(conjunct, operand =>
        {
            while (operand is Parenthesized parenthesized)
                operand = parenthesized.Wrapped;
            if (operand is Reference reference)
            {
                var (source, column) = FindSourceColumn(sources, reference.ReferencedName);
                if (source != index)
                    return null;
                namesTheSource = true;
                return new ProjectionSlot(column);
            }

            if (!operand.IsRowIndependent)
                return null;
            try
            {
                return Value.NonLiteral(operand.Run(new RuntimeContext(
                    static name => throw SimulatedSqlException.ColumnReferenceNotAllowed(name), batch)));
            }
            catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
            {
                return null;
            }
        });
        // A conjunct naming no column of this source filters nothing here.
        return namesTheSource ? template : null;
    }

    /// <summary>
    /// Records the pushdown shape of a plain SELECT-project-filter plan — no
    /// DISTINCT, no TOP / OFFSET / FETCH, no GROUP BY / HAVING / aggregate, no
    /// window, no ORDER BY — which is the one shape an enclosing WHERE conjunct
    /// can move into unchanged, since the body applies its projection and its
    /// own WHERE to every row and nothing else. A join body qualifies: the
    /// conjunct lands in the body's WHERE and the body's own narrowing passes
    /// take it from there.
    /// </summary>
    private sealed class ProjectionPushdown(
        SqlType[] schema,
        string[] columnNames,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        List<BooleanExpression> excluders,
        List<OrderBySpec> orderBy)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;
        public readonly List<Expression> Expressions = expressions;
        public readonly List<BooleanExpression> Excluders = excluders;

        /// <summary>The plan's own (empty, by the shape rule) ORDER BY, kept so the rebuilt row source calls <see cref="ProjectSqlRows"/> with the same argument the original does.</summary>
        public readonly List<OrderBySpec> OrderBy = orderBy;
    }

    /// <summary>
    /// The projection plan's <see cref="PredicatePushdown"/>: rebinds each
    /// template's <see cref="ProjectionSlot"/>s to the body's own projection
    /// expressions and returns a plan reading with them appended to the body's
    /// WHERE. Declines (null) when no template survives — a slot whose output
    /// column is an expression rather than a plain column projection can't be
    /// written as a filter over the body's row.
    /// <para>
    /// The appended conjuncts go <em>after</em> the body's own, so the body's
    /// WHERE still decides first for every row it excluded before.
    /// </para>
    /// </summary>
    private static Selection? BuildPushedProjection(ProjectionPushdown shape, List<BooleanExpression> templates)
    {
        var bound = RebindTemplates(templates, ordinal => BodyColumnReference(shape.Expressions, ordinal));
        if (bound is null)
            return null;

        var excluders = new List<BooleanExpression>(shape.Excluders.Count + bound.Count);
        excluders.AddRange(shape.Excluders);
        excluders.AddRange(bound);
        return new Selection(
            shape.Schema,
            shape.ColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            valueRowSource: (batch, outerResolver) =>
            {
                var execSources = PushWhereIntoDeferredSources(shape.Sources, excluders, batch);
                execSources = BoundRowNumberBodies(execSources, excluders, batch);
                execSources = ReduceGroupedBodiesByJoinKeys(execSources, shape.Joins, excluders, batch, outerResolver);
                execSources = MaterializeUncorrelatedDeferredSources(execSources, shape.Joins, batch, outerResolver);
                return ProjectSqlRows(
                    execSources, shape.Joins, shape.Expressions, excluders, shape.ColumnNames, shape.OrderBy,
                    distinct: false, top: default, offsetCount: null, fetchCount: null, batch: batch, outerResolver: outerResolver);
            });
    }

    /// <summary>
    /// The column reference a body projects at <paramref name="ordinal"/>, or
    /// null when that output column is anything but a plain column projection
    /// (identity or rename) — an expression, a CAST, an aggregate. Reusing the
    /// body's own node is what makes the rebound conjunct resolve in the body's
    /// FROM scope exactly as its select list does; the node is parse-time
    /// immutable and read-only at runtime, so two trees may hold it.
    /// </summary>
    private static Reference? BodyColumnReference(List<Expression> expressions, int ordinal) =>
        ordinal >= 0 && ordinal < expressions.Count ? UnwrapDirectRef(expressions[ordinal]) : null;

    /// <summary>
    /// Rebinds every template that can be rebound, dropping the rest; null when
    /// none survives. <paramref name="slotColumn"/> answers what the body reads
    /// at an output ordinal (null declines that template).
    /// </summary>
    private static List<BooleanExpression>? RebindTemplates(
        List<BooleanExpression> templates, Func<int, Reference?> slotColumn)
    {
        List<BooleanExpression>? bound = null;
        foreach (var template in templates)
        {
            var rebound = RebindTemplate(template, operand => operand switch
            {
                ProjectionSlot slot => slotColumn(slot.Ordinal),
                Value constant => constant,
                _ => null,
            });
            if (rebound is not null)
                (bound ??= []).Add(rebound);
        }

        return bound;
    }

    /// <summary>
    /// <see cref="BooleanExpression.TryRebindOperands"/> plus the one predicate
    /// shape this file builds itself — the join-key <see cref="KeySetMembership"/>,
    /// whose value set isn't an operand list and so rebinds through its own
    /// method. Every rebinding site goes through here, which is what lets a
    /// reduced key set keep travelling down a chain of bodies exactly as a
    /// written conjunct does.
    /// </summary>
    private static BooleanExpression? RebindTemplate(
        BooleanExpression template, Func<Expression, Expression?> rebind) =>
        template is KeySetMembership membership
            ? membership.TryRebind(rebind)
            : BooleanExpression.TryRebindOperands(template, rebind);

    /// <summary>
    /// Records the pushdown shape of a <b>GROUP BY</b> body — a grouped
    /// SELECT with no DISTINCT, no row limit, no window and no ORDER BY — whose
    /// eligible output columns are the ones projecting a grouping column
    /// unchanged. A conjunct on such a column commutes with the grouping: it
    /// removes whole groups, and a group the enclosing statement was going to
    /// discard anyway contributes to no other group's aggregate (nor to any
    /// other group's HAVING, which is evaluated per group). A grouping
    /// <em>expression</em> — <c>GROUP BY MONTH(d)</c> — is not such a column and
    /// declines, since the filter above names the expression's value, not
    /// anything the body's rows carry.
    /// </summary>
    private sealed class AggregatePushdown(
        SqlType[] schema,
        string[] columnNames,
        FromSource[] sources,
        JoinSpec[] joins,
        Func<MultiPartName, SqlType> resolveColumnType,
        List<Expression> expressions,
        FromClause fromClause,
        List<OrderBySpec> orderBy,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        SqlType[] windowOperandTypes,
        SqlType[] windowResultTypes,
        Reference?[] groupingColumns)
    {
        public readonly SqlType[] Schema = schema;
        public readonly string[] ColumnNames = columnNames;
        public readonly FromSource[] Sources = sources;
        public readonly JoinSpec[] Joins = joins;
        public readonly Func<MultiPartName, SqlType> ResolveColumnType = resolveColumnType;
        public readonly List<Expression> Expressions = expressions;
        public readonly FromClause From = fromClause;

        /// <summary>The plan's own (empty, by the shape rule) ORDER BY / window state, kept so the rebuilt row source calls the aggregate projector with the same arguments the original does.</summary>
        public readonly List<OrderBySpec> OrderBy = orderBy;
        public readonly List<AggregateExpression> Aggregates = aggregates;
        public readonly List<WindowExpression> Windows = windows;
        public readonly SqlType[] WindowOperandTypes = windowOperandTypes;
        public readonly SqlType[] WindowResultTypes = windowResultTypes;

        /// <summary>
        /// Per output ordinal, the body column a pushed conjunct may filter on —
        /// non-null only where the projection is a plain column reference that
        /// is also one of the body's grouping columns.
        /// </summary>
        public readonly Reference?[] GroupingColumns = groupingColumns;
    }

    /// <summary>
    /// A GROUP BY body's <see cref="PredicatePushdown"/>: the projection
    /// counterpart above, restricted to the slots
    /// <see cref="AggregatePushdown.GroupingColumns"/> admits, with the bound
    /// conjuncts appended to the body's WHERE — <em>below</em> the grouping, so
    /// the aggregate never sees the rows of a group the filter removed.
    /// <para>
    /// The rebuilt plan carries the pushdown delegate on, so a second push (a
    /// written conjunct and then a join-key reduction, or a reduction reaching
    /// through a chain) lands on the same body rather than declining.
    /// </para>
    /// </summary>
    private static Selection? BuildPushedAggregate(AggregatePushdown shape, List<BooleanExpression> templates)
    {
        var bound = RebindTemplates(
            templates,
            ordinal => ordinal >= 0 && ordinal < shape.GroupingColumns.Length ? shape.GroupingColumns[ordinal] : null);
        if (bound is null)
            return null;

        var from = shape.From.WithExtraExcluders(bound);
        return new Selection(
            shape.Schema,
            shape.ColumnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            valueRowSource: (batch, outerResolver) =>
            {
                var execSources = PushWhereIntoDeferredSources(shape.Sources, from.Excluders, batch);
                execSources = BoundRowNumberBodies(execSources, from.Excluders, batch);
                execSources = ReduceGroupedBodiesByJoinKeys(execSources, shape.Joins, from.Excluders, batch, outerResolver);
                execSources = MaterializeUncorrelatedDeferredSources(execSources, shape.Joins, batch, outerResolver);
                return BuildAggregateProjectionRows(
                    execSources, shape.Joins, shape.ResolveColumnType, shape.Expressions, from, shape.ColumnNames,
                    shape.OrderBy, shape.Aggregates, shape.Windows, shape.WindowOperandTypes, shape.WindowResultTypes,
                    top: default, offsetCount: null, fetchCount: null, distinct: false, batch: batch, outerResolver: outerResolver);
            })
        {
            PushdownIsGrouped = true,
            PredicatePushdown = more => BuildPushedAggregate(shape, [.. templates, .. more]),
        };
    }

    /// <summary>
    /// Per output ordinal, the body column an enclosing conjunct may filter a
    /// grouped body on: the projection has to be a plain column reference
    /// (identity or rename) that resolves to a FROM source, and one of the
    /// body's grouping expressions has to be a reference resolving to that same
    /// source column. Returns null when no ordinal qualifies, which is what
    /// declines a body grouped only by expressions.
    /// </summary>
    private static Reference?[]? GroupingColumnProjections(
        List<Expression> expressions, FromSource[] sources, FromClause fromClause)
    {
        Reference?[]? columns = null;
        for (var i = 0; i < expressions.Count; i++)
        {
            if (UnwrapDirectRef(expressions[i]) is not { } projected)
                continue;
            var (source, column) = FindSourceColumn(sources, projected.ReferencedName);
            if (source < 0)
                continue;
            foreach (var grouping in fromClause.AllGroupingExpressions)
            {
                if (grouping is not Reference key || FindSourceColumn(sources, key.ReferencedName) != (source, column))
                    continue;
                columns ??= new Reference?[expressions.Count];
                columns[i] = projected;
                break;
            }
        }

        return columns;
    }

    /// <summary>
    /// How many distinct partner keys a grouped body may be reduced to, and how
    /// many partner rows the reduction will read to find them. Past it the
    /// reduction declines silently and the body aggregates as before: a large
    /// key set neither narrows the body much nor stays cheap to carry, and the
    /// probe has to stay bounded because it reads the partner ahead of the join.
    /// </summary>
    private const int GroupedReductionKeyCap = 1024;

    /// <summary>
    /// Reduces a joined <b>GROUP BY</b> body to the join's own key set: for a
    /// still-deferred grouped source equi-joined on one of its grouping columns,
    /// the partner side's distinct values of the joined column are collected and
    /// pushed below the body's grouping as a membership predicate. The body then
    /// aggregates the rows of the groups the join can actually use instead of
    /// every group in the table — real's semi-join reduction, which is why the
    /// WWI point join over a 663-group / 228k-row aggregate reads as a seek
    /// there and as a full aggregation here.
    /// <para>
    /// <b>Legality.</b> The reduction is implied by the join rather than added
    /// to it: the equi-join <c>ON</c> stays exactly as written, and for every
    /// surviving tuple the body's key equals the partner's, so a body row whose
    /// key no partner row carries can match nothing. The partner may itself be
    /// narrowed here by the enclosing WHERE, which is sound for the same reason
    /// the narrowing pass is — those conjuncts stay residual over the whole
    /// result, so a partner row they exclude belongs to no surviving tuple.
    /// The body must not be <em>preserved</em> by an outer join
    /// (<see cref="BodyIsReducible"/>): dropping a row of a preserved side would
    /// drop a result row real returns, while dropping one from an inner or
    /// NULL-supplied side can only drop tuples the join or the WHERE discarded.
    /// </para>
    /// <para>
    /// Runs after <see cref="PushWhereIntoDeferredSources"/> (so a body already
    /// filtered on its own output takes the reduction on top of that) and before
    /// <see cref="MaterializeUncorrelatedDeferredSources"/> (so what materializes
    /// is the reduced body). Clones rather than mutates, per the shared-plan
    /// contract.
    /// </para>
    /// </summary>
    private static FromSource[] ReduceGroupedBodiesByJoinKeys(
        FromSource[] sources,
        JoinSpec[] joins,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        if (sources.Length < 2)
            return sources;

        List<BooleanExpression>? conjuncts = null;
        FromSource[]? rewritten = null;
        for (var i = 0; i < sources.Length; i++)
        {
            if (sources[i].LateralPlan is not { PushdownIsGrouped: true } plan
                || plan.PredicatePushdown is not { } push
                || !BodyIsReducible(joins, i))
            {
                continue;
            }

            conjuncts ??= CollectJoinAndWhereConjuncts(joins, excluders);
            foreach (var conjunct in conjuncts)
            {
                if (!TryExtractEquiEdge(conjunct, sources, out var edge)
                    || (edge.LeftSource != i && edge.RightSource != i))
                {
                    continue;
                }

                var bodyOnLeft = edge.LeftSource == i;
                var body = bodyOnLeft ? edge.LeftColumn : edge.RightColumn;
                var partner = bodyOnLeft ? edge.RightSource : edge.LeftSource;
                var partnerColumn = bodyOnLeft ? edge.RightColumn : edge.LeftColumn;
                if (!TryPromoteEquiKeyTypes(sources, body, partnerColumn, out var common)
                    || CollectPartnerKeys(sources, partner, partnerColumn, common, excluders, batch, outerResolver) is not { } keys)
                {
                    continue;
                }

                var (_, bodyOrdinal) = FindSourceColumn(sources, body.ReferencedName);
                if (push([new KeySetMembership(new ProjectionSlot(bodyOrdinal), common, keys)]) is not { } reduced)
                    continue;
                rewritten ??= (FromSource[])sources.Clone();
                rewritten[i] = sources[i].WithPushedPlan(reduced);
                JoinDiagnostics.Sink?.Add($"KeyReduction({sources[i].Qualifier},keys={keys.Length})");
                break;
            }
        }

        return rewritten ?? sources;
    }

    /// <summary>
    /// Whether the source at <paramref name="index"/> may lose rows without the
    /// statement losing an output row it owes: true unless some outer join
    /// <em>preserves</em> that slot. A <c>LEFT</c> join preserves everything
    /// left of the source it attaches, a <c>RIGHT</c> join preserves the source
    /// it attaches, and <c>FULL</c> preserves both sides — a reduced body there
    /// would delete rows real returns. <c>APPLY</c> declines outright (its right
    /// side re-executes per outer row rather than reading a fixed rowset), as
    /// does a parenthesized join group, whose interior joins as a unit.
    /// </summary>
    private static bool BodyIsReducible(JoinSpec[] joins, int index)
    {
        for (var j = 0; j < joins.Length; j++)
        {
            if (joins[j].GroupCount != 1)
                return false;
            switch (joins[j].Kind)
            {
                case JoinKind.CrossApply or JoinKind.OuterApply or JoinKind.Full:
                    return false;
                case JoinKind.Left when index <= j:
                case JoinKind.Right when index == j + 1:
                    return false;
                default:
                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Every top-level conjunct that could pair two sources: the <c>ON</c>
    /// predicates plus the statement's own WHERE (which is where a comma-FROM
    /// writes its join predicate). An <c>ON</c> naming two sources sits at the
    /// join that attaches the later of them, so collecting them all is the same
    /// set the join graph offers, without the level bookkeeping.
    /// </summary>
    private static List<BooleanExpression> CollectJoinAndWhereConjuncts(
        JoinSpec[] joins, List<BooleanExpression> excluders)
    {
        List<BooleanExpression> conjuncts = [];
        foreach (var join in joins)
            join.OnPredicate?.CollectConjuncts(conjuncts);
        foreach (var excluder in excluders)
            excluder.CollectConjuncts(conjuncts);
        return conjuncts;
    }

    /// <summary>
    /// The distinct non-NULL values a reduction's partner side carries in its
    /// joined column, coerced to the <c>=</c> operator's promotion target, or
    /// null when the partner isn't a cheap bounded read.
    /// <para>
    /// The partner is narrowed by the enclosing WHERE first (the same seek the
    /// narrowing pass would apply, and discarded afterwards — the pass runs on
    /// its own later), so a filtered partner offers the small key set that makes
    /// the reduction worth anything. It must be a re-enumerable rowset rather
    /// than another deferred body, must not carry <c>READPAST</c> (whose skip
    /// set is what the two reads would disagree about) or a lock plan whose
    /// footprint the extra read would change (a SERIALIZABLE fence, tx-scoped
    /// row locks), and must fit <see cref="GroupedReductionKeyCap"/> rows. A
    /// NULL key is simply omitted: NULL never equi-joins, so no body row it
    /// would have kept can survive the join anyway.
    /// </para>
    /// </summary>
    private static SqlValue[]? CollectPartnerKeys(
        FromSource[] sources,
        int partnerIndex,
        Reference partnerColumn,
        SqlType common,
        List<BooleanExpression> excluders,
        BatchContext batch,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        var partner = sources[partnerIndex];
        if (partner.LateralPlan is not null || partner.IsPlaceholder)
            return null;
        if (partner.HeapPlan is { SerializableRangeMode: not null } or { RowTxScoped: true } or { SkipBlockedRows: true })
            return null;

        var (_, ordinal) = FindSourceColumn(sources, partnerColumn.ReferencedName);
        if (ordinal < 0 || partner.Columns[ordinal] is { Computed: not null, IsPersisted: false })
            return null;
        if (partner.BackingTable is not null && excluders.Count > 0)
        {
            partner = MaybeApplyIndexSeek(
                [partner], NoJoins, excluders, batch, outerResolver, planSources: sources)[0];
        }

        var keys = new List<SqlValue>();
        var seen = new HashSet<SqlValue>();
        var examined = 0;
        foreach (var row in partner.Rows)
        {
            if (++examined > GroupedReductionKeyCap)
                return null;
            var value = DecodeOrCompute(partner, ordinal, row, batch, ThrowOnColumnReference);
            if (value.IsNull)
                continue;
            var coerced = value.CoerceTo(common);
            if (seen.Add(coerced))
                keys.Add(coerced);
        }

        return [.. keys];
    }

    /// <summary>
    /// The column resolver a key-collecting decode never needs: a persisted or
    /// stored column decodes from its own bytes, and a non-persisted computed
    /// one — the only shape that would evaluate an expression over the row —
    /// declines the reduction before this can be reached.
    /// </summary>
    private static readonly Func<MultiPartName, SqlValue> ThrowOnColumnReference =
        static name => throw SimulatedSqlException.InvalidColumnName(name);

    /// <summary>
    /// A pushed <c>&lt;body column&gt; IN (&lt;the partner's keys&gt;)</c>: the
    /// join-key reduction's predicate. Written as a set rather than the
    /// equality family an <c>IN</c> list decomposes into so that a body the
    /// filter can't seek pays one hash lookup per row instead of one comparison
    /// per key per row — and it still <em>exposes</em> that family
    /// (<see cref="TryGetEqualityFamily"/>), which is what lets the index seek
    /// underneath the body probe once per key.
    /// <para>
    /// Membership is the <c>=</c> operator's own semantics: both sides are
    /// coerced to the key type, the promotion target
    /// <see cref="TryPromoteComparableKeyTypes"/> settled for the joined pair,
    /// and <see cref="SqlValue"/>'s equality is the collation-aware,
    /// trailing-space-folding comparison the operator performs — the same
    /// contract the equi-join hash buckets rest on. A NULL subject reads UNKNOWN
    /// (the set holds no NULL), so the predicate is NULL-rejecting like every
    /// other pushable shape.
    /// </para>
    /// </summary>
    private sealed class KeySetMembership(Expression subject, SqlType keyType, SqlValue[] keys) : BooleanExpression
    {
        private readonly HashSet<SqlValue> set = [.. keys];

        public override bool? Run(RuntimeContext runtime)
        {
            var value = subject.Run(runtime);
            return value.IsNull ? null : this.set.Contains(value.CoerceTo(keyType));
        }

        /// <summary>
        /// The same membership over a rebound subject — the counterpart of
        /// <see cref="BooleanExpression.TryRebindOperands"/> for a predicate
        /// whose values are already evaluated rather than operand expressions.
        /// </summary>
        internal KeySetMembership? TryRebind(Func<Expression, Expression?> rebind) =>
            rebind(subject) is { } rebound ? new KeySetMembership(rebound, keyType, keys) : null;

        internal override bool TryGetEqualityFamily([NotNullWhen(true)] out List<(Expression Left, Expression Right)>? pairs)
        {
            pairs = new List<(Expression Left, Expression Right)>(keys.Length);
            foreach (var key in keys)
                pairs.Add((subject, Value.NonLiteral(key)));
            return pairs.Count > 0;
        }

        internal override void VisitOperandExpressions(Action<Expression> visitor) => visitor(subject);

        internal override string DebugDisplay() => $"{subject.DebugDisplay()} IN (<{keys.Length} join keys>)";
    }

    /// <summary>
    /// A template's stand-in for "the column this plan projects at
    /// <see cref="Ordinal"/>", carried from the query that wrote the conjunct to
    /// the body that will run it. Ordinals are the one thing the two agree on
    /// without sharing a name scope — which is what lets a template cross into a
    /// view body parsed later, and lets it keep crossing down a chain of them.
    /// Replaced by the body's own projection expression before the predicate is
    /// ever evaluated; reaching <see cref="Run"/> would mean a rebind escaped.
    /// </summary>
    private sealed class ProjectionSlot(int ordinal) : Expression
    {
        public readonly int Ordinal = ordinal;

        public override SqlValue Run(RuntimeContext runtime) =>
            throw new NotSupportedException("A pushed-predicate projection slot must be rebound before it runs.");

        public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType) =>
            throw new NotSupportedException("A pushed-predicate projection slot must be rebound before it binds.");

        internal override string DebugDisplay() => $"<projection slot {this.Ordinal}>";
    }
}
