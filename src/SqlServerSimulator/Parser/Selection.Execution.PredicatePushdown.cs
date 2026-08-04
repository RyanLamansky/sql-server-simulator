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
        var template = BooleanExpression.TryRebindOperands(conjunct, operand =>
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
        List<BooleanExpression>? bound = null;
        foreach (var template in templates)
        {
            var rebound = BooleanExpression.TryRebindOperands(template, operand => operand switch
            {
                ProjectionSlot slot => BodyColumnReference(shape.Expressions, slot.Ordinal),
                Value constant => constant,
                _ => null,
            });
            if (rebound is not null)
                (bound ??= []).Add(rebound);
        }

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
