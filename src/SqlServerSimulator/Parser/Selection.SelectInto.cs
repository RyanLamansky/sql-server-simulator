using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Computes the destination <see cref="HeapColumn"/> schema for a
    /// <c>SELECT … INTO target</c> projection. Applies SQL Server's
    /// schema-inference rules (probe-confirmed 2026-05-11):
    /// <list type="bullet">
    /// <item>Direct column ref preserves source column's nullability + identity.</item>
    /// <item>Identity propagates only when the FROM clause is a single
    /// non-joined source — a table, or a view or derived table over one —
    /// and the column is named once; JOIN / UNION drop it.</item>
    /// <item>Nullability defers to <see cref="Expression.ResultIsNullable"/>,
    /// whose XML doc carries the whole rule set — the same inference real
    /// applies to result metadata, so a destination column and the wire's
    /// COLMETADATA <c>fNullable</c> flag agree cell for cell.</item>
    /// </list>
    /// Validates the destination shape: every projection column must have a
    /// name (Msg 1038), no duplicate names allowed (Msg 2705). An
    /// <see cref="IdentityFunction"/> item is the destination's identity
    /// column, which is Msg 8109 for a second one and Msg 8108 beside a column
    /// inheriting identity from its source (probed 2026-09-24 against SQL
    /// Server 2025).
    /// </summary>
    /// <param name="targetName">Destination table name, for error messages.</param>
    /// <param name="projections">Projection expressions (already named, with stars expanded).</param>
    /// <param name="outputSchema">Projection result types (parallel to <paramref name="projections"/>).</param>
    /// <param name="outputColumnNames">Projection column names (parallel to <paramref name="projections"/>).</param>
    /// <param name="sources">FROM-clause sources; null/empty for no-FROM SELECT.</param>
    /// <param name="joins">FROM-clause join specs; identity drops on any join.</param>
    /// <param name="parseBatch">The parsing batch, threaded into the nullability inference.</param>
    /// <param name="resolveColumnType">Column-type resolver, threaded into the nullability inference.</param>
    internal static HeapColumn[] ComputeIntoDestSchema(
        MultiPartName targetName,
        List<Expression> projections,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        FromSource[] sources,
        JoinSpec[] joins,
        BatchContext parseBatch,
        Func<MultiPartName, SqlType> resolveColumnType)
    {
        var destColumns = new HeapColumn[projections.Count];
        var seenNames = new HashSet<string>(BuiltInToken.Comparer);
        // Identity propagation: requires exactly one FromSource and no joins.
        // A view or derived table carries its base column's identity through
        // (probed 2026-10-01 against SQL Server 2025); a source with none —
        // OPENJSON say — drops it even on direct refs. Naming the identity
        // column twice drops it from both copies.
        var identityEligible = sources.Length == 1 && joins.Length == 0;
        var identityReferences = 0;
        if (identityEligible)
        {
            foreach (var projection in projections)
            {
                if (UnwrapDirectRef(projection) is Reference counted && SourceIdentity(sources, counted) is not null)
                    identityReferences++;
            }
        }

        // The destination's declaration follows the same inference the wire's
        // COLMETADATA reports, NULL-fill map included: a column read from the
        // inner side of an outer join has to land nullable however its base
        // column is declared, or the first NULL-extended row contradicts the
        // table just created for it.
        var nullFilled = NullFilledSources(sources, joins);

        bool ResolveColumnNullable(MultiPartName name)
        {
            var (s, c) = FindSourceColumn(sources, name);
            return s == -1 || nullFilled[s] || sources[s].Columns[c].Nullable;
        }

        var nullabilityContext = new NullabilityContext(parseBatch, ResolveColumnNullable, resolveColumnType);
        string? inheritedIdentity = null;
        var identityFunctions = 0;

        for (var i = 0; i < projections.Count; i++)
        {
            var colName = outputColumnNames[i];
            if (string.IsNullOrEmpty(colName))
                throw SimulatedSqlException.SelectIntoMissingColumnName();
            if (!seenNames.Add(colName))
                throw SimulatedSqlException.DuplicateColumnInSelectInto(colName, targetName.ToString());

            var nullable = projections[i].ResultIsNullable(nullabilityContext);
            IdentityState? identity = null;
            if (projections[i] is NamedExpression { Inner: IdentityFunction { Identity: { } declared } })
            {
                if (++identityFunctions > 1)
                    throw SimulatedSqlException.MultipleIdentityFunctions(targetName.Leaf);
                identity = new IdentityState(declared.Seed, declared.Increment);
            }
            // Direct column ref → maybe propagate identity. NamedExpression
            // wraps the parser's renaming; the underlying Reference is what
            // we care about for identity rules.
            if (identityReferences == 1 && UnwrapDirectRef(projections[i]) is Reference reference)
            {
                if (SourceIdentity(sources, reference) is { } sourceIdentity)
                {
                    // Each dest gets its own IdentityState starting fresh; the
                    // configured seed/increment match the source's.
                    identity = new IdentityState(sourceIdentity.Seed, sourceIdentity.Increment);
                    inheritedIdentity ??= colName;
                }
            }

            // A stored column is plain text, whatever JSON producer filled it
            // (probed 2026-10-02 against SQL Server 2025).
            var columnType = outputSchema[i] is NVarcharSqlType { jsonText: true } jsonText
                ? NVarcharSqlType.Get(jsonText.length, jsonText.Collation, jsonText.Coercibility)
                : outputSchema[i];
            destColumns[i] = new HeapColumn(
                colName,
                columnType,
                maxLength: null,
                nullable: nullable,
                identity: identity,
                // A character column keeps its expression's collation, which
                // sys.columns reports (probed 2026-09-25 against SQL Server
                // 2025: a Latin1_General_BIN source copies as Latin1_General_BIN).
                collation: outputSchema[i] is VarcharSqlType or NVarcharSqlType or CharSqlType or NCharSqlType
                    ? outputSchema[i].Collation?.Name
                    : null,
                spelledNumeric: projections[i].ResultReportsNumeric)
            {
                // A #temp table's types resolve in tempdb, which has no
                // user alias types (probed 2026-09-26).
                AliasType = targetName.Leaf.StartsWith('#') ? null : projections[i].ResultAliasType,
            };
        }

        return identityFunctions > 0 && inheritedIdentity is not null
            ? throw SimulatedSqlException.IdentityFunctionWithInheritedIdentity(targetName.Leaf, inheritedIdentity)
            : destColumns;
    }

    /// <summary>
    /// The identity a direct reference to the lone FROM source reads — the
    /// column's own, or the one a view or derived table carries through.
    /// </summary>
    private static IdentityState? SourceIdentity(FromSource[] sources, Reference reference)
    {
        var (s, c) = FindSourceColumn(sources, reference.ReferencedName);
        return s == 0 ? sources[0].Columns[c].Identity ?? sources[0].Columns[c].IdentitySource : null;
    }

    /// <summary>
    /// Returns the underlying <see cref="Reference"/> if <paramref name="expr"/>
    /// is a direct column reference (possibly wrapped in one or more
    /// <see cref="NamedExpression"/> layers from <c>AS alias</c> or
    /// star-expansion). Returns null otherwise — any wrapping in an
    /// arithmetic / CAST / function call disqualifies for identity
    /// propagation.
    /// </summary>
    private static Reference? UnwrapDirectRef(Expression expr) => expr switch
    {
        Reference r => r,
        NamedExpression named => UnwrapDirectRef(named.Inner),
        _ => null,
    };
}
