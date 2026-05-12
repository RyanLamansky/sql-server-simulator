using SqlServerSimulator.Parser;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator;

partial class Simulation
{
    /// <summary>
    /// Computes the updatability metadata for a freshly-parsed view body —
    /// the eventual base <see cref="HeapTable"/>, per-output-column base-
    /// ordinal map, and pre-bound visibility / CHECK OPTION closures. Walks
    /// view-on-view chains by composing through each intermediate view's
    /// own pre-computed metadata.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A view is updatable iff every level in its chain satisfies:
    /// </para>
    /// <list type="bullet">
    /// <item>Exactly one FROM source, no JOINs, no DISTINCT / aggregates /
    /// GROUP BY / HAVING / set ops / window functions
    /// (<see cref="Selection.UpdatabilityProfile"/> is non-null).</item>
    /// <item>The single source is either a heap table or another updatable
    /// view (<see cref="View.BaseTable"/> non-null).</item>
    /// <item>Every column referenced inside any WHERE clause up the chain
    /// maps to a real base-table column (no WHERE that references a
    /// derived projection above it).</item>
    /// </list>
    /// <para>
    /// The simulator's v1 of updatable views rejects JOIN-bodied views
    /// uniformly via the <see cref="ViewUpdatabilityRejection.MultipleSources"/>
    /// → Msg 4405 path (real SQL Server allows single-base-table UPDATEs
    /// through JOIN views; deferred to a follow-up bundle).
    /// </para>
    /// </remarks>
    private static (HeapTable? BaseTable, int[] BaseColumnOrdinals, ViewUpdatabilityRejection Rejection, Func<SqlValue[], BatchContext, bool>? VisibilityCheck, Func<SqlValue[], BatchContext, bool>? CheckOptionCheck)
        AnalyzeViewUpdatability(Selection bodySelection, bool withCheckOption)
    {
        if (bodySelection.UpdatabilityProfile is not { } profile)
            return (null, [], bodySelection.UpdatabilityRejection, null, null);

        var source = profile.Source;
        HeapTable baseTable;
        int[] sourceColumnToBaseOrdinal;
        Func<SqlValue[], BatchContext, bool>? upstreamVisibility = null;
        Func<SqlValue[], BatchContext, bool>? upstreamCheckOption = null;

        if (source.BackingTable is { } table)
        {
            baseTable = table;
            sourceColumnToBaseOrdinal = new int[source.Columns.Length];
            for (var i = 0; i < source.Columns.Length; i++)
                sourceColumnToBaseOrdinal[i] = i;
        }
        else if (source.BackingView is { BaseTable: { } upstreamBaseTable } upstreamView)
        {
            baseTable = upstreamBaseTable;
            sourceColumnToBaseOrdinal = upstreamView.BaseColumnOrdinals;
            upstreamVisibility = upstreamView.VisibilityCheck;
            upstreamCheckOption = upstreamView.CheckOptionCheck;
        }
        else
        {
            // Source is a derived table, CTE, OPENJSON, TVF, catalog view,
            // or a non-updatable view — none of which support DML
            // pass-through.
            return (null, [], ViewUpdatabilityRejection.UnsupportedShape, null, null);
        }

        var baseColumnOrdinals = new int[profile.Projections.Length];
        for (var i = 0; i < profile.Projections.Length; i++)
        {
            // A projection is "direct" if its outer shape unwraps to a bare
            // Reference (the wrapper may be NamedExpression from `AS alias`).
            // Anything else — arithmetic, function call, CAST, literal —
            // is a derived field; touching it at INSERT/UPDATE triggers
            // Msg 4406 at the DML site (gated per-column there, since DELETE
            // through a view with derived columns still works).
            if (UnwrapDirectRef(profile.Projections[i]) is { ReferencedName: { } refName })
            {
                var sourceOrd = -1;
                for (var j = 0; j < source.ColumnNames.Length; j++)
                {
                    if (Collation.Default.Equals(source.ColumnNames[j], refName.Leaf))
                    {
                        sourceOrd = j;
                        break;
                    }
                }
                baseColumnOrdinals[i] = sourceOrd >= 0 ? sourceColumnToBaseOrdinal[sourceOrd] : -1;
            }
            else
            {
                baseColumnOrdinals[i] = -1;
            }
        }

        // Build the column-name → base-ordinal dict for this level's WHERE
        // resolution. Each WHERE excluder references columns by the upstream
        // view's OutputColumn names (or the base table's column names when
        // the source is a heap). Translation uses sourceColumnToBaseOrdinal.
        var nameToBaseOrdinal = new Dictionary<string, int>(Collation.Default);
        for (var j = 0; j < source.ColumnNames.Length; j++)
            nameToBaseOrdinal[source.ColumnNames[j]] = sourceColumnToBaseOrdinal[j];

        foreach (var excluder in profile.Excluders)
        {
            var unmappable = false;
            excluder.VisitOperandExpressions(operand => operand.VisitColumnReferences(name =>
            {
                if (!nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) || ord < 0)
                    unmappable = true;
            }));
            if (unmappable)
                return (null, [], ViewUpdatabilityRejection.UnsupportedShape, null, null);
        }

        var thisLevelCheck = MakeWhereCheck(profile.Excluders, nameToBaseOrdinal);

        var combinedVisibility = ComposeAnd(thisLevelCheck, upstreamVisibility);

        // CHECK OPTION enforcement: each level with WITH CHECK OPTION
        // contributes its own visibility (= its WHERE composed with its
        // upstream visibility). Upstream's CHECK OPTION composes in
        // unchanged so deeper-chain check options still fire.
        var thisLevelCheckOption = withCheckOption ? combinedVisibility : null;
        var combinedCheckOption = ComposeAnd(thisLevelCheckOption, upstreamCheckOption);

        return (baseTable, baseColumnOrdinals, ViewUpdatabilityRejection.None, combinedVisibility, combinedCheckOption);
    }

    /// <summary>
    /// Returns the underlying <see cref="Reference"/> when <paramref name="expr"/>
    /// is a direct column reference (possibly wrapped in one or more
    /// <see cref="NamedExpression"/> layers from <c>AS alias</c>). Null
    /// otherwise — any other wrapper (arithmetic, CAST, function call,
    /// CASE, etc.) means the projection is derived. Same shape as the
    /// equivalent helper in <c>Selection.SelectInto.cs</c>; kept separate
    /// so the SELECT INTO logic isn't entangled with view DML.
    /// </summary>
    private static Reference? UnwrapDirectRef(Expression expr) => expr switch
    {
        Reference r => r,
        NamedExpression named => UnwrapDirectRef(named.Inner),
        _ => null,
    };

    /// <summary>
    /// Builds a per-level WHERE evaluator: given a base-table row's
    /// <see cref="SqlValue"/> array (indexed by base-table column ordinal)
    /// and a <see cref="BatchContext"/>, returns true iff every excluder
    /// evaluates to <c>true</c> (the WHERE-style three-valued rule, where
    /// UNKNOWN excludes). Returns null when <paramref name="excluders"/>
    /// is empty so callers can avoid wrapping a no-op closure.
    /// </summary>
    private static Func<SqlValue[], BatchContext, bool>? MakeWhereCheck(
        BooleanExpression[] excluders,
        Dictionary<string, int> nameToBaseOrdinal) => excluders.Length == 0
            ? null
            : (row, batch) =>
            {
                SqlValue Resolve(MultiPartName name) => nameToBaseOrdinal.TryGetValue(name.Leaf, out var ord) && ord >= 0
                    ? row[ord]
                    : throw SimulatedSqlException.InvalidColumnName(name);
                var runtime = new RuntimeContext(Resolve, batch);
                foreach (var excluder in excluders)
                {
                    if (excluder.Run(runtime) != true)
                        return false;
                }
                return true;
            };

    /// <summary>
    /// Composes two boolean closures into an AND. Either argument may be
    /// null (treated as <c>true</c>). When both are null, returns null —
    /// the caller treats that as "no predicate to evaluate, row is
    /// always visible". Avoids capturing always-true predicates in the
    /// chain so the common no-WHERE view runs without per-row closure
    /// invocation.
    /// </summary>
    private static Func<SqlValue[], BatchContext, bool>? ComposeAnd(
        Func<SqlValue[], BatchContext, bool>? a,
        Func<SqlValue[], BatchContext, bool>? b) => (a, b) switch
        {
            (null, _) => b,
            (_, null) => a,
            _ => (row, batch) => a(row, batch) && b(row, batch),
        };
}
