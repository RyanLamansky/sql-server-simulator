using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Parse-time classification of an equi-correlated subquery body into the
/// decorrelated key plan an <c>EXISTS</c> / <c>[NOT] IN</c> site can answer
/// itself from once its outer side outgrows the per-row execution. The runtime
/// half — the adaptive switch and the structure it builds — lives in
/// <see cref="SemiJoinProbe"/>.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// The decorrelated key plan for this body, or null when it doesn't
    /// qualify. Set once at parse, before the plan is published, so a
    /// plan-cached <c>Selection</c> shared across executions carries a
    /// value-independent classification (see
    /// <c>docs/claude/plan-cache.md</c>). Read by the <c>EXISTS</c> /
    /// <c>IN (SELECT …)</c> expressions; every other consumer ignores it.
    /// </summary>
    internal SemiJoinShape? SemiJoin;

    /// <summary>
    /// Builds the decorrelated key plan for a body whose WHERE splits into
    /// <b>correlation equi-conjuncts</b> (<c>&lt;inner column&gt; =
    /// &lt;enclosing-scope expression&gt;</c>) plus a residual that reads only
    /// this body's own sources. The key plan is the same query with those
    /// conjuncts removed and its projection replaced by the inner columns they
    /// named — so the set of keys it produces is exactly the set of outer keys
    /// for which the correlated body returns a row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything that reads the row set as a whole declines: DISTINCT, TOP /
    /// OFFSET / FETCH, GROUP BY / HAVING, an aggregate or a window. Each makes
    /// the body's answer depend on <em>which</em> rows the correlation kept, so
    /// evaluating it once over every key would answer a different question.
    /// </para>
    /// <para>
    /// A reference the residual, an <c>ON</c> predicate or the projection makes
    /// to the enclosing scope declines too — its value varies per outer row, so
    /// one execution can't stand in for all of them. The check is the same
    /// <see cref="FindSourceColumn"/> resolution the per-row resolver runs, and
    /// it doesn't see into a nested subquery; the runtime correlation latch in
    /// <see cref="SemiJoinProbe"/> is what covers that.
    /// </para>
    /// </remarks>
    private static SemiJoinShape? TryBuildSemiJoinShape(
        BatchContext parseBatch,
        Selection plan,
        FromSource[] sources,
        JoinSpec[] joins,
        List<Expression> expressions,
        FromClause fromClause,
        bool distinct,
        Expression? topExpression,
        List<AggregateExpression> aggregates,
        List<WindowExpression> windows,
        Func<MultiPartName, SqlType>? outerTypeResolver)
    {
        if (distinct
            || topExpression is not null
            || aggregates.Count != 0
            || windows.Count != 0
            || fromClause.GroupingSets.Count != 0
            || fromClause.Having is not null
            || fromClause.OrderBy.Count != 0
            || fromClause.OffsetExpression is not null
            || fromClause.FetchExpression is not null
            || fromClause.Excluders.Count == 0
            || expressions.Count == 0
            || sources.Length == 0
            || AnyPlaceholderSource(sources)
            // A validation parse collecting a view's shape would see the key
            // plan's own sources and aggregates counted a second time.
            || parseBatch.Parser.IndexedViewShapeCollector is not null)
        {
            return null;
        }

        SqlType ResolveColumnType(MultiPartName name) => ResolveColumnTypeAcrossSources(sources, name, outerTypeResolver);

        var conjuncts = new List<BooleanExpression>();
        foreach (var excluder in fromClause.Excluders)
            excluder.CollectConjuncts(conjuncts);

        var innerKeys = new List<Expression>();
        var outerKeys = new List<Expression>();
        var keyTypes = new List<SqlType>();
        var residual = new List<BooleanExpression>();
        foreach (var conjunct in conjuncts)
        {
            if (TryClassifyCorrelationEquality(parseBatch, sources, conjunct, ResolveColumnType, out var innerKey, out var outerKey, out var keyType))
            {
                innerKeys.Add(innerKey);
                outerKeys.Add(outerKey);
                keyTypes.Add(keyType);
            }
            else
            {
                residual.Add(conjunct);
            }
        }

        if (innerKeys.Count == 0)
            return null;

        foreach (var conjunct in residual)
        {
            if (!ReadsOnlyLocalColumns(conjunct, sources))
                return null;
        }
        foreach (var join in joins)
        {
            if (join.OnPredicate is { } on && !ReadsOnlyLocalColumns(on, sources))
                return null;
        }
        foreach (var expression in expressions)
        {
            if (!ReadsOnlyLocalColumns(expression, sources))
                return null;
        }
        foreach (var source in sources)
        {
            // One pass over the body reads rows the per-row executions would
            // never have touched. Under a lock plan whose footprint that changes
            // — tx-scoped row locks (REPEATABLE READ, UPDLOCK / XLOCK), a
            // SERIALIZABLE / HOLDLOCK phantom fence, or READPAST's skip set —
            // the extra reads are observable, so the body keeps its per-row
            // execution. A read-committed probe or a dirty read takes nothing
            // the second pass could hold.
            if (source.HeapPlan is { } lockPlan
                && (lockPlan.RowTxScoped || lockPlan.SerializableRangeMode is not null || lockPlan.SkipBlockedRows))
            {
                return null;
            }
        }

        // The key plan projects the key columns, then — for the IN shape, whose
        // body projects exactly one column (Msg 116) — that column, which is
        // what the left side is compared against. A LOB projection declines the
        // value slot: it can't be hashed or compared, and an IN over one raises
        // from the comparison anyway.
        var keyProjection = new List<Expression>(innerKeys);
        if (expressions.Count == 1 && !plan.Schema[0].IsLob)
            keyProjection.Add(expressions[0]);

        var keyFromClause = new FromClause();
        keyFromClause.Excluders.AddRange(residual);

        try
        {
            var keyPlan = BuildSqlProjection(
                parseBatch,
                [.. sources],
                joins,
                keyProjection,
                keyFromClause,
                distinct: false,
                topExpression: null,
                topPercent: false,
                topWithTies: false,
                aggregates: [],
                windows: [],
                outerTypeResolver,
                isAssignmentOnly: false,
                intoTarget: null,
                readColumnSink: null,
                projectionDiscarded: true);
            return new SemiJoinShape(keyPlan, [.. outerKeys], [.. keyTypes], SeekableInnerTable(sources, innerKeys));
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            // The body itself parsed; only the derived plan declined, so the
            // site keeps its per-row execution rather than failing the query.
            return null;
        }
    }

    /// <summary>
    /// The base table this body's per-row execution <b>seeks</b> on a
    /// correlation column — a lone base-table source with a key / index leading
    /// on one of them — or null when the per-row path is a scan (or a re-run
    /// plan) instead. The adaptive switch reads it to decide how large the
    /// outer has to grow before one pass over that table beats the seeks it
    /// replaces; see <see cref="SemiJoinProbe"/>.
    /// </summary>
    private static HeapTable? SeekableInnerTable(FromSource[] sources, List<Expression> innerKeys)
    {
        if (sources.Length != 1 || sources[0].LateralPlan is not null || sources[0].BackingTable is not { } table)
            return null;
        foreach (var key in innerKeys)
        {
            if (TryIdentifyIndexableColumn(sources[0], key, out var ordinal) && LeadsSomeKeyOrIndex(table, ordinal))
                return table;
        }
        return null;
    }

    /// <summary>
    /// Recognizes <c>&lt;bare column of this body&gt; = &lt;enclosing-scope
    /// expression&gt;</c> in either operand order, reporting the promotion
    /// target the runtime <c>=</c> would compare under. The value side takes the
    /// same stability rule an index seek's probe does — a literal, a variable,
    /// an enclosing column, or deterministic arithmetic over those — and has to
    /// name at least one enclosing column, which is what makes the conjunct a
    /// <em>correlation</em> rather than an ordinary filter the residual can keep.
    /// </summary>
    private static bool TryClassifyCorrelationEquality(
        BatchContext parseBatch,
        FromSource[] sources,
        BooleanExpression conjunct,
        Func<MultiPartName, SqlType> resolveColumnType,
        out Expression innerKey,
        out Expression outerKey,
        out SqlType keyType)
    {
        innerKey = null!;
        outerKey = null!;
        keyType = null!;
        if (!conjunct.TryGetEqualityOperands(out var left, out var right))
            return false;

        if (!TryOrderCorrelationOperands(sources, left, right, out var column, out var value)
            && !TryOrderCorrelationOperands(sources, right, left, out column, out value))
        {
            return false;
        }

        SqlType columnType;
        SqlType valueType;
        try
        {
            columnType = resolveColumnType(column.ReferencedName);
            valueType = value.GetSqlType(parseBatch, resolveColumnType);
        }
        catch (Exception ex) when (ex is SimulatedSqlException or NotSupportedException)
        {
            return false;
        }

        if (!TryPromoteComparableKeyTypes(columnType, valueType, out var common))
            return false;

        (innerKey, outerKey, keyType) = (column, value, common);
        return true;
    }

    // The column side has to be a bare reference into this body's own sources;
    // the value side has to be stable for one execution of it (never a sibling
    // column) and to actually reach the enclosing scope.
    private static bool TryOrderCorrelationOperands(
        FromSource[] sources, Expression columnSide, Expression valueSide, out Reference column, out Expression value)
    {
        column = null!;
        value = valueSide;
        if (columnSide is not Reference reference || ResolvesLocally(sources, reference.ReferencedName) != true)
            return false;
        if (!IsStableValueSide(valueSide, sources[0], allowCorrelatedColumnValue: true, planSources: sources)
            || !NamesEnclosingColumn(valueSide, sources))
        {
            return false;
        }

        column = reference;
        return true;
    }

    // True when some column reference in the expression resolves outside this
    // body's sources — the reference that makes the equality correlated.
    private static bool NamesEnclosingColumn(Expression expression, FromSource[] sources)
    {
        var found = false;
        expression.VisitColumnReferences(name => found |= ResolvesLocally(sources, name) == false);
        return found;
    }

    // True when every column reference a predicate / projection makes resolves
    // against this body's own sources. An ambiguous or unresolvable name reports
    // false, which declines — the conservative direction for a rewrite.
    private static bool ReadsOnlyLocalColumns(Expression expression, FromSource[] sources)
    {
        var local = true;
        expression.VisitColumnReferences(name => local &= ResolvesLocally(sources, name) == true);
        return local;
    }

    private static bool ReadsOnlyLocalColumns(BooleanExpression predicate, FromSource[] sources)
    {
        var local = true;
        predicate.VisitOperandExpressions(operand => local &= ReadsOnlyLocalColumns(operand, sources));
        return local;
    }

    // Whether a name binds to one of this body's sources — null when the
    // resolution is ambiguous (Msg 209 at bind time), which neither side claims.
    private static bool? ResolvesLocally(FromSource[] sources, MultiPartName name)
    {
        try
        {
            return FindSourceColumn(sources, name).SourceIndex >= 0;
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }
}
