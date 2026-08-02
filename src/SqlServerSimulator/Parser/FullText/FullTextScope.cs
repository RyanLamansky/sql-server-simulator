namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// Binds a full-text predicate's column specification against the FROM sources
/// the enclosing query already parsed. <see cref="ParserContext.FullTextSources"/>
/// carries that scope: <c>Selection</c> installs it once the FROM clause is
/// bound — which happens before the select list <i>and</i> before WHERE, so
/// both a <c>WHERE CONTAINS(…)</c> and a <c>CASE WHEN CONTAINS(…)</c> in the
/// projection resolve.
/// </summary>
internal static class FullTextScope
{
    /// <summary>
    /// Resolves the specification to a table and its searched columns, raising
    /// real's errors along the way: <b>Msg 1046</b> where no query scope exists
    /// at all (a CHECK constraint, which real classifies the same way),
    /// <b>Msg 207</b> for a column no source has, and <b>Msg 7601</b> for a
    /// table or column outside the full-text index.
    /// </summary>
    public static FullTextBinding Bind(ParserContext context, FullTextColumnSpec.Spec spec)
    {
        if (context.FullTextSources is not { Length: > 0 } sources)
            throw SimulatedSqlException.FullTextPredicateNotAllowedHere();

        var collation = context.Batch.CurrentDatabase.Collation;
        var source = spec.AllColumns
            ? FindStarSource(sources, spec.StarQualifier, collation)
            : FindColumnSource(sources, spec.Columns[0], collation);

        return source.BackingTable is { } table
            ? FullTextColumnSpec.Bind(spec, table, source.Qualifier ?? table.Name, context.Batch.CurrentDatabase, collation, source.Qualifier)
            : throw SimulatedSqlException.FullTextTableNotIndexed(source.Qualifier ?? string.Empty);
    }

    /// <summary>
    /// <c>CONTAINS(*, …)</c> searches every full-text-indexed column of the
    /// query's indexed table; <c>alias.*</c> names which one when several are
    /// in scope.
    /// </summary>
    private static FromSource FindStarSource(FromSource[] sources, string? qualifier, Collation collation)
    {
        if (qualifier is not null)
        {
            foreach (var source in sources)
            {
                if (source.Qualifier is { } name && collation.Equals(name, qualifier))
                    return source;
            }
            throw SimulatedSqlException.FullTextTableNotIndexed(qualifier);
        }
        foreach (var indexedSource in sources)
        {
            if (indexedSource.BackingTable?.FullTextIndex is not null)
                return indexedSource;
        }
        return sources[0];
    }

    /// <summary>
    /// Locates the source a written column name belongs to — by alias when the
    /// name is qualified, otherwise by which source declares the column.
    /// </summary>
    private static FromSource FindColumnSource(FromSource[] sources, MultiPartName written, Collation collation)
    {
        if (written.Count > 1 && written.ImmediateQualifier is { } qualifier)
        {
            foreach (var source in sources)
            {
                if (source.Qualifier is { } name && collation.Equals(name, qualifier))
                    return source;
            }
            throw SimulatedSqlException.InvalidColumnName(written.ToString());
        }
        foreach (var source in sources)
        {
            foreach (var columnName in source.ColumnNames)
            {
                if (collation.Equals(columnName, written.Leaf))
                    return source;
            }
        }
        throw SimulatedSqlException.InvalidColumnName(written.Leaf);
    }
}
