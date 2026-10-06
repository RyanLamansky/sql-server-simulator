using System.Globalization;
using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// Browse mode: a SELECT statement run under SET NO_BROWSETABLE ON — which
// SqlClient wraps a CommandBehavior.KeyInfo command in — carries its base
// tables' key and rowversion columns as hidden trailing columns, and tells
// the client each column's base table and column (the TDS TABNAME and COLINFO
// tokens), from which SqlClient's GetSchemaTable fills BaseTableName,
// BaseColumnName, IsKey, IsHidden, IsExpression and IsAliased.
internal sealed partial class Selection
{
    /// <summary>
    /// Whether this query specification is a browse-mode statement's own —
    /// the statement's query rather than a nested one, not a set operation's
    /// branch (the set operation's result is described as a whole), not an
    /// <c>INTO</c> or an assignment — consuming the statement's flag either
    /// way. A grouped or DISTINCT one is described but gets no hidden columns
    /// (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    private static bool TakeBrowseStatement(BatchContext parseBatch, QueryScope scope, MultiPartName? intoTarget, bool isAssignmentOnly)
    {
        var context = parseBatch.Parser;
        if (!context.BrowseStatement || scope.Position != QueryPosition.Statement)
            return false;
        context.BrowseStatement = false;
        return intoTarget is null && !isAssignmentOnly
            && context.Token is not ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect };
    }

    /// <summary>
    /// Browse metadata for a set operation's result, whose columns name no
    /// base table: every one reads as an expression.
    /// </summary>
    internal static BrowseInfo SetOperationBrowseInfo(int columns)
    {
        var info = new (byte Table, byte Status, string? BaseName)[columns];
        Array.Fill(info, (0, 0x04, null));
        return new BrowseInfo([], info);
    }

    /// <summary>
    /// Whether this query block is a body a browse statement flattens (see
    /// <see cref="ParserContext.BrowseFlattenBody"/>) — not a set operation's
    /// branch, an <c>INTO</c> or an assignment.
    /// </summary>
    private static bool FlattensForBrowse(ParserContext context, MultiPartName? intoTarget, bool isAssignmentOnly) =>
        context.BrowseFlattenBody && intoTarget is null && !isAssignmentOnly
        && context.Token is not ReservedKeyword { Keyword: Keyword.Union or Keyword.Except or Keyword.Intersect };

    /// <summary>
    /// Whether this plan is a body a browse statement flattens: its columns
    /// describe down to its base tables, and its last
    /// <see cref="HiddenColumnCount"/> columns carry their hidden browse columns.
    /// </summary>
    internal bool BrowseFlattened;

    /// <summary>
    /// Whether a flattened body passes its rows through whole — no grouping,
    /// DISTINCT or HAVING — so the statement over it carries their keys.
    /// </summary>
    internal bool BrowsePassesRows;

    /// <summary>
    /// Appends, for each base table a browse statement reaches — in FROM order,
    /// through the derived tables, views and CTEs it flattens — its key columns
    /// and then its rowversion column that the select list doesn't already
    /// read, as the hidden columns real carries in every row; returns how many.
    /// A table reached through a grouped or DISTINCT body contributes none.
    /// </summary>
    private static int AppendBrowseHiddenColumns(FromSource[] sources, List<Expression> expressions)
    {
        var hidden = 0;
        foreach (var (table, keyed) in BrowseTables(sources))
        {
            if (!keyed)
                continue;
            foreach (var ordinal in BrowseColumnOrdinals(table.BackingTable!))
            {
                if (expressions.Exists(expression => BrowseBaseColumn(sources, expression) is { Table: { } projected, Ordinal: var o } && ReferenceEquals(projected, table) && o == ordinal)
                    || BrowseReference(sources, table, ordinal) is not { } reference)
                {
                    continue;
                }
                expressions.Add(reference);
                hidden++;
            }
        }
        return hidden;
    }

    /// <summary>
    /// Each base table <paramref name="sources"/> reach, flattened through the
    /// bodies a browse statement flattens, in FROM order — and whether its
    /// rows reach the statement whole, so its key columns can ride them.
    /// </summary>
    private static IEnumerable<(FromSource Table, bool Keyed)> BrowseTables(FromSource[] sources, bool keyed = true)
    {
        foreach (var source in sources)
        {
            if (source.BrowseBody is { } body)
            {
                if (body is { BrowseFlattened: true, BranchFromSources: { } inner })
                {
                    foreach (var table in BrowseTables(inner, keyed && body.BrowsePassesRows))
                        yield return table;
                }
            }
            else if (source.BackingTable is not null && !source.IsPlaceholder)
            {
                yield return (source, keyed);
            }
        }
    }

    /// <summary>
    /// The base-table column <paramref name="expression"/> reads, through any
    /// flattened body between — the base table's FROM source and the column's
    /// ordinal — or, where it reads none, whether it is an expression (a
    /// computed value, or a column of a set operation's body).
    /// </summary>
    private static (FromSource? Table, int Ordinal, bool IsExpression) BrowseBaseColumn(FromSource[] sources, Expression expression)
    {
        if (Unaliased(expression) is not Reference reference)
            return (null, -1, true);
        var (sourceIndex, columnIndex) = FindSourceColumn(sources, reference.ReferencedName);
        if (sourceIndex < 0)
            return (null, -1, false);
        var source = sources[sourceIndex];
        if (source.BrowseBody is { } body)
        {
            if (body.IsSetOperationResult)
                return (null, -1, true);
            // A body's aggregate reads as no expression (probed 2026-10-06
            // against SQL Server 2025), though the statement's own does.
            return body is { BrowseFlattened: true, BranchFromSources: { } inner, ProjectionExpressions: { } projections } && columnIndex < projections.Length
                ? Unaliased(projections[columnIndex]) is AggregateExpression ? (null, -1, false) : BrowseBaseColumn(inner, projections[columnIndex])
                : (null, -1, false);
        }
        return source.BackingTable is not null && !source.IsPlaceholder ? (source, columnIndex, false) : (null, -1, false);
    }

    /// <summary>
    /// A reference, from the level of <paramref name="sources"/>, to column
    /// <paramref name="ordinal"/> of the base table <paramref name="table"/> —
    /// the table's own column, or the column of the body that reaches it which
    /// reads that column, under the base column's name — or null where nothing
    /// passes it up.
    /// </summary>
    private static Expression? BrowseReference(FromSource[] sources, FromSource table, int ordinal)
    {
        var name = table.BackingTable!.Columns[ordinal].Name;
        foreach (var source in sources)
        {
            if (ReferenceEquals(source, table))
                return source.Qualifier is { } qualifier ? new Reference(qualifier, name) : new Reference(name);
            if (source.BrowseBody is { BrowseFlattened: true, BranchFromSources: { } inner, ProjectionExpressions: { } projections })
            {
                for (var i = 0; i < projections.Length && i < source.ColumnNames.Length; i++)
                {
                    if (BrowseBaseColumn(inner, projections[i]) is { Table: { } reached, Ordinal: var o } && ReferenceEquals(reached, table) && o == ordinal)
                        return new NamedExpression(new Reference(source.Qualifier!, source.ColumnNames[i]), name);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// The name a flattened body's hidden column <paramref name="index"/> goes
    /// by in the FROM source over it — one no identifier can spell, so only the
    /// browse statement's own hidden references reach it.
    /// </summary>
    internal static string BrowseHiddenName(int index) => "\u0001browse" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The browse metadata for a statement whose last <paramref name="hidden"/>
    /// columns are browse mode's own.
    /// </summary>
    private static BrowseInfo BrowseInfoFor(FromSource[] sources, List<Expression> expressions, string[] outputColumnNames, int hidden, bool markKeys = true)
    {
        var tables = new List<string[]>();
        var tableNumbers = new Dictionary<FromSource, int>(ReferenceEqualityComparer.Instance);
        foreach (var (table, _) in BrowseTables(sources))
        {
            tables.Add((table.WrittenObjectName ?? table.BackingTable!.Name).Split('.'));
            tableNumbers[table] = tables.Count;
        }

        var columns = new (byte Table, byte Status, string? BaseName)[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
        {
            var isHidden = i >= expressions.Count - hidden;
            var (table, ordinal, isExpression) = BrowseBaseColumn(sources, expressions[i]);
            if (table is null || !tableNumbers.TryGetValue(table, out var tableNumber))
            {
                columns[i] = (0, (byte)(isExpression ? 0x04 : 0), null);
                continue;
            }

            var heap = table.BackingTable!;
            var baseName = heap.Columns[ordinal].Name;
            var renamed = !string.Equals(outputColumnNames[i], baseName, StringComparison.Ordinal);
            var status = (markKeys && Array.IndexOf(BrowseKeyOrdinals(heap), ordinal) >= 0 ? 0x08 : 0)
                | (isHidden ? 0x10 : 0)
                | (renamed ? 0x20 : 0);
            columns[i] = ((byte)tableNumber, (byte)status, renamed ? baseName : null);
        }

        return new BrowseInfo([.. tables], columns);
    }

    // The key browse mode identifies a table's rows by — its PRIMARY KEY,
    // else a UNIQUE constraint, else an enabled unfiltered unique index — then
    // its rowversion column, which a keyless table contributes on its own.
    private static IEnumerable<int> BrowseColumnOrdinals(HeapTable table)
    {
        foreach (var ordinal in BrowseKeyOrdinals(table))
            yield return ordinal;
        for (var c = 0; c < table.Columns.Length; c++)
        {
            if (table.Columns[c].Type == SqlType.RowVersion)
                yield return c;
        }
    }

    private static int[] BrowseKeyOrdinals(HeapTable table)
    {
        foreach (var kind in (KeyConstraintKind[])[KeyConstraintKind.PrimaryKey, KeyConstraintKind.Unique])
        {
            foreach (var constraint in table.KeyConstraints)
            {
                if (constraint.Kind == kind && !constraint.IsDisabled)
                    return constraint.FullOrdinals;
            }
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && !index.IsDisabled && index.Filter is null)
                return index.KeyFullOrdinals;
        }
        return [];
    }

    private static Expression Unaliased(Expression expression) =>
        expression is NamedExpression named ? named.Inner : expression;

    /// <summary>
    /// The base-table column output column <paramref name="index"/> reads
    /// directly — the table and its column ordinal — or null for a computed
    /// column, one read through anything but a base table, or a shape that
    /// kept no sources.
    /// </summary>
    internal (HeapTable Table, int Ordinal)? ProjectionBaseColumn(int index)
    {
        if (this.BranchFromSources is not { } sources || this.ProjectionExpressions is not { } expressions
            || index >= expressions.Length || Unaliased(expressions[index]) is not Reference reference)
        {
            return null;
        }
        var (sourceIndex, columnIndex) = FindSourceColumn(sources, reference.ReferencedName);
        return sourceIndex >= 0 && sources[sourceIndex] is { BackingTable: { } table, IsPlaceholder: false, LateralPlan: null }
            ? (table, columnIndex)
            : null;
    }

    /// <summary>A selection that yields <paramref name="rows"/>, encoded to
    /// <paramref name="schema"/>, every time it runs.</summary>
    internal static Selection FromRows(SqlType[] schema, string[] columnNames, List<byte[]> rows) =>
        new(schema, columnNames, hasOrderBy: false, hasTopOrOffsetOrFetch: false, (_, _) => rows);
}
