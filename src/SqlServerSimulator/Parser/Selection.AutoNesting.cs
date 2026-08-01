using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

partial class Selection
{
    /// <summary>
    /// Splits a projection into the nesting levels <c>FOR XML AUTO</c> /
    /// <c>FOR JSON AUTO</c> serialize it as, following SQL Server's rules
    /// (probe-confirmed against SQL Server 2025):
    /// <list type="bullet">
    /// <item>Each FROM source contributing at least one bare column reference
    /// becomes one level; a source no column reads contributes none.</item>
    /// <item>Level order is the order of each source's <em>first</em> column in
    /// the select list — not FROM order — and the levels always nest as a
    /// linear chain, whatever the join topology.</item>
    /// <item>A column joins its own source's level even when another table's
    /// columns intervene, keeping its relative order within that level.</item>
    /// <item>A computed column (any expression that isn't a bare column
    /// reference, including a CAST or function call over another table's
    /// column) joins the level of the nearest preceding table column; one that
    /// precedes every table column joins the first level, ahead of that
    /// level's own columns.</item>
    /// <item>A projection of nothing but computed columns still nests one
    /// level, named after the first FROM source.</item>
    /// </list>
    /// </summary>
    /// <param name="inner">The wrapped SELECT, carrying the source binding.</param>
    /// <param name="forJson">Selects the FOR JSON error wording over FOR XML's.</param>
    internal static AutoLevel[] BuildAutoLevels(Selection inner, bool forJson)
    {
        if (inner.AutoSourceNames is not { } sourceNames || inner.AutoColumnSource is not { } columnSource)
        {
            throw new NotSupportedException(forJson
                ? "FOR JSON AUTO over a set-operation result isn't modeled; use FOR JSON PATH."
                : "FOR XML AUTO over a set-operation result isn't modeled; use FOR XML PATH.");
        }

        // No FROM clause at all: AUTO has no table to name a level after.
        if (sourceNames.Length == 0)
            throw forJson ? SimulatedSqlException.ForJsonAutoRequiresTable() : SimulatedSqlException.ForXmlAutoRequiresTable();

        var levels = new List<AutoLevel>();
        var levelOfSource = new int[sourceNames.Length];
        Array.Fill(levelOfSource, -1);

        // Computed columns seen before any table column: they belong to the
        // first level, which isn't known until that first table column.
        var leading = new List<int>();
        var current = -1;

        for (var i = 0; i < columnSource.Length; i++)
        {
            var source = columnSource[i];
            if (source < 0)
            {
                if (current < 0)
                    leading.Add(i);
                else
                    levels[current].Columns.Add(i);
                continue;
            }

            if (levelOfSource[source] < 0)
            {
                levelOfSource[source] = levels.Count;
                levels.Add(new AutoLevel(AutoLevelName(sourceNames, source, forJson)));
            }
            current = levelOfSource[source];
            levels[current].Columns.Add(i);
        }

        if (levels.Count == 0)
            levels.Add(new AutoLevel(AutoLevelName(sourceNames, 0, forJson)));
        levels[0].Columns.InsertRange(0, leading);

        // SQL Server can't compare xml, so a level holding an xml column never
        // groups: every row opens a fresh element / object there, even when
        // the values are identical (probe-confirmed, XML and JSON alike).
        foreach (var level in levels)
        {
            foreach (var column in level.Columns)
            {
                if (inner.Schema[column] is XmlSqlType)
                {
                    level.AlwaysRestarts = true;
                    break;
                }
            }
        }
        return [.. levels];
    }

    /// <summary>
    /// The element / property name of one level, rejecting a source with no
    /// exposed name the way real rejects an unnamed table.
    /// </summary>
    private static string AutoLevelName(string?[] sourceNames, int source, bool forJson) =>
        sourceNames[source]
        ?? throw (forJson ? SimulatedSqlException.ForJsonColumnWithoutName() : SimulatedSqlException.ForXmlUnnamedColumn());

    /// <summary>
    /// True when two consecutive rows carry the same values in every column of
    /// <paramref name="level"/> — the test the AUTO serializers use to decide
    /// whether a row extends the open element / object or starts a new one.
    /// Two NULLs count as equal (probe-confirmed: consecutive rows whose parent
    /// column is NULL collapse into one element).
    /// </summary>
    internal static bool AutoLevelValuesEqual(AutoLevel level, SqlType[] schema, byte[] previous, byte[] current)
    {
        if (level.AlwaysRestarts)
            return false;
        foreach (var column in level.Columns)
        {
            var before = RowDecoder.DecodeColumn(schema, previous, column);
            var after = RowDecoder.DecodeColumn(schema, current, column);
            if (before.IsNull != after.IsNull)
                return false;
            if (!before.IsNull && !before.Equals(after))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The index of the outermost level whose values changed between two
    /// consecutive rows — every level from there inward closes and reopens.
    /// The innermost level always restarts (SQL Server emits one element /
    /// object per row there, even for two identical rows), so the result is
    /// capped one short of the level count.
    /// </summary>
    internal static int AutoRestartDepth(AutoLevel[] levels, SqlType[] schema, byte[] previous, byte[] current)
    {
        for (var i = 0; i < levels.Length - 1; i++)
        {
            if (!AutoLevelValuesEqual(levels[i], schema, previous, current))
                return i;
        }
        return levels.Length - 1;
    }
}

/// <summary>
/// One nesting level of a <c>FOR XML AUTO</c> / <c>FOR JSON AUTO</c>
/// projection: the name taken from a FROM source, plus the result-column
/// indices that serialize under it, in select order.
/// </summary>
internal sealed class AutoLevel(string name)
{
    public readonly string Name = name;
    public readonly List<int> Columns = [];

    /// <summary>
    /// True when this level holds an <c>xml</c> column, which SQL Server can't
    /// compare — so the level never groups consecutive rows and every row
    /// opens a fresh element / object. Finalized by
    /// <see cref="Selection.BuildAutoLevels"/> once the columns are known.
    /// </summary>
    public bool AlwaysRestarts;
}
