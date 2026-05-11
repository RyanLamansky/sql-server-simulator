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
    /// non-joined heap source — JOIN/UNION/derived-table drop identity.</item>
    /// <item>Nullability defers to <see cref="Expression.ResultIsNullable"/>
    /// — ISNULL non-null iff either arg is, CASE non-null iff every branch
    /// is, literals non-null unless bare NULL, everything else nullable.</item>
    /// </list>
    /// Validates the destination shape: every projection column must have a
    /// name (Msg 1038), no duplicate names allowed (Msg 2705).
    /// </summary>
    /// <param name="targetName">Destination table name, for error messages.</param>
    /// <param name="projections">Projection expressions (already named, with stars expanded).</param>
    /// <param name="outputSchema">Projection result types (parallel to <paramref name="projections"/>).</param>
    /// <param name="outputColumnNames">Projection column names (parallel to <paramref name="projections"/>).</param>
    /// <param name="sources">FROM-clause sources; null/empty for no-FROM SELECT.</param>
    /// <param name="joins">FROM-clause join specs; identity drops on any join.</param>
    internal static HeapColumn[] ComputeIntoDestSchema(
        string targetName,
        List<Expression> projections,
        SqlType[] outputSchema,
        string[] outputColumnNames,
        FromSource[] sources,
        JoinSpec[] joins)
    {
        var destColumns = new HeapColumn[projections.Count];
        var seenNames = new HashSet<string>(Collation.Default);
        // Identity propagation: requires exactly one FromSource that's a real
        // heap table, no joins. Anything else (joins, derived tables, CTEs
        // backed by Selection, OPENJSON) drops identity even on direct refs.
        var identityEligible = sources.Length == 1 && joins.Length == 0 && sources[0].BackingTable is not null;

        bool ResolveColumnNullable(MultiPartName name)
        {
            var (s, c) = FindSourceColumn(sources, name);
            return s == -1 || sources[s].Columns[c].Nullable;
        }

        for (var i = 0; i < projections.Count; i++)
        {
            var colName = outputColumnNames[i];
            if (string.IsNullOrEmpty(colName))
                throw SimulatedSqlException.SelectIntoMissingColumnName();
            if (!seenNames.Add(colName))
                throw SimulatedSqlException.DuplicateColumnInSelectInto(colName, targetName);

            var nullable = projections[i].ResultIsNullable(ResolveColumnNullable);
            IdentityState? identity = null;
            // Direct column ref → maybe propagate identity. NamedExpression
            // wraps the parser's renaming; the underlying Reference is what
            // we care about for identity rules.
            if (identityEligible && UnwrapDirectRef(projections[i]) is Reference reference)
            {
                var (s, c) = FindSourceColumn(sources, reference.ReferencedName);
                if (s == 0 && sources[0].BackingTable is { } sourceTable && sourceTable.Columns[c].Identity is { } sourceIdentity)
                {
                    // Each dest gets its own IdentityState starting fresh; the
                    // configured seed/increment match the source's.
                    identity = new IdentityState(sourceIdentity.Seed, sourceIdentity.Increment);
                }
            }

            destColumns[i] = new HeapColumn(
                colName,
                outputSchema[i],
                maxLength: null,
                nullable: nullable,
                identity: identity);
        }

        return destColumns;
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
