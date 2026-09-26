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
    /// Appends, for each base table in FROM order, its key columns and then
    /// its rowversion column that the select list doesn't already read, as
    /// the hidden columns real carries in every row; returns how many.
    /// </summary>
    private static int AppendBrowseHiddenColumns(FromSource[] sources, List<Expression> expressions)
    {
        var hidden = 0;
        for (var s = 0; s < sources.Length; s++)
        {
            var source = sources[s];
            if (source.BackingTable is not { } table || source.IsPlaceholder)
                continue;
            foreach (var ordinal in BrowseColumnOrdinals(table))
            {
                if (ProjectsSourceColumn(expressions, sources, s, ordinal))
                    continue;
                var name = table.Columns[ordinal].Name;
                expressions.Add(source.Qualifier is { } qualifier ? new Reference(qualifier, name) : new Reference(name));
                hidden++;
            }
        }
        return hidden;
    }

    /// <summary>
    /// The browse metadata for a statement whose last <paramref name="hidden"/>
    /// columns are browse mode's own.
    /// </summary>
    private static BrowseInfo BrowseInfoFor(FromSource[] sources, List<Expression> expressions, string[] outputColumnNames, int hidden)
    {
        var tables = new List<string[]>();
        var tableNumbers = new int[sources.Length];
        for (var s = 0; s < sources.Length; s++)
        {
            if (sources[s].BackingTable is { } table && !sources[s].IsPlaceholder)
            {
                tables.Add((sources[s].WrittenObjectName ?? table.Name).Split('.'));
                tableNumbers[s] = tables.Count;
            }
        }

        var columns = new (byte Table, byte Status, string? BaseName)[expressions.Count];
        for (var i = 0; i < expressions.Count; i++)
        {
            var isHidden = i >= expressions.Count - hidden;
            if (Unaliased(expressions[i]) is not Reference reference)
            {
                columns[i] = (0, 0x04, null);
                continue;
            }

            var (sourceIndex, columnIndex) = FindSourceColumn(sources, reference.ReferencedName);
            if (sourceIndex < 0 || tableNumbers[sourceIndex] == 0 || sources[sourceIndex].BackingTable is not { } table)
            {
                columns[i] = (0, 0, null);
                continue;
            }

            var baseName = table.Columns[columnIndex].Name;
            var renamed = !string.Equals(outputColumnNames[i], baseName, StringComparison.Ordinal);
            var status = (Array.IndexOf(BrowseKeyOrdinals(table), columnIndex) >= 0 ? 0x08 : 0)
                | (isHidden ? 0x10 : 0)
                | (renamed ? 0x20 : 0);
            columns[i] = ((byte)tableNumbers[sourceIndex], (byte)status, renamed ? baseName : null);
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

    private static bool ProjectsSourceColumn(List<Expression> expressions, FromSource[] sources, int sourceIndex, int ordinal)
    {
        foreach (var expression in expressions)
        {
            if (Unaliased(expression) is Reference reference && FindSourceColumn(sources, reference.ReferencedName) == (sourceIndex, ordinal))
                return true;
        }
        return false;
    }

    private static Expression Unaliased(Expression expression) =>
        expression is NamedExpression named ? named.Inner : expression;
}
