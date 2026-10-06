using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

// What real's optimizer knows of a query's uniqueness from the keys of what it
// reads: a GROUP BY whose groups each hold one row reduces its aggregates to
// the row's own values, so they skip no NULL and send no Msg 8153 (probed
// 2026-10-06 against SQL Server 2025).
partial class Selection
{
    /// <summary>
    /// Sets of output-column ordinals each of which no two rows of this query
    /// share — the key a reader's own GROUP BY can lean on — or null when none
    /// is known: an updatable single-source projection passes its source's
    /// keys through, a <c>DISTINCT</c> projection is keyed on all its columns,
    /// and a grouped one on its grouping columns.
    /// </summary>
    public int[][]? OutputKeys;

    /// <summary>
    /// Whether each group a single plain grouping set forms provably holds one
    /// row: the grouping columns, with the columns equalities in the
    /// <c>ON</c> and <c>WHERE</c> clauses tie to them or to constants,
    /// determine a key of every source (probed 2026-10-06 against SQL Server
    /// 2025: a table's primary key, unique constraint or unfiltered unique
    /// index, through inner and left joins, a <c>WHERE</c>, a view or derived
    /// table, and a <c>VALUES</c> list of distinct constants — where a
    /// filtered unique index, a grouping expression, <c>ROLLUP</c> or a join
    /// to a non-unique column leave the warning standing).
    /// </summary>
    private static bool GroupsHoldOneRow(FromSource[] sources, JoinSpec[] joins, FromClause fromClause)
    {
        if (fromClause is not { GroupingSets: [{ Length: > 0 } groupingSet], GroupingSetsWritten: false, GroupByAll: false })
            return false;
        var given = new List<(int Source, int Column)>();
        foreach (var expression in groupingSet)
        {
            if (Unwrapped(expression) is Reference reference && FindSourceColumnOfAnyKind(sources, reference.ReferencedName) is ( >= 0, >= 0) column)
                given.Add(column);
        }
        return DeterminesEverySource(sources, joins, fromClause.Excluders, given);
    }

    /// <summary>
    /// <see cref="OutputKeys"/> for a projection over <paramref name="sources"/>.
    /// </summary>
    private static int[][]? DeriveOutputKeys(FromSource[] sources, JoinSpec[] joins, FromClause fromClause, List<Expression> expressions, bool distinct, bool aggregates)
    {
        var projected = new (int Source, int Column)[expressions.Count];
        for (var i = 0; i < projected.Length; i++)
        {
            projected[i] = Unwrapped(expressions[i]) is Reference reference
                ? FindSourceColumnOfAnyKind(sources, reference.ReferencedName)
                : (-1, -1);
        }

        List<int[]>? keys = null;
        if (aggregates || fromClause.GroupingSets.Count > 0)
        {
            // Grouped: the grouping columns, when every one is projected bare.
            if (fromClause is not { GroupingSets: [{ Length: > 0 } groupingSet], GroupingSetsWritten: false, GroupByAll: false })
                return null;
            var key = new int[groupingSet.Length];
            for (var g = 0; g < groupingSet.Length; g++)
            {
                if (Unwrapped(groupingSet[g]) is not Reference reference
                    || FindSourceColumnOfAnyKind(sources, reference.ReferencedName) is not ( >= 0, >= 0) column
                    || Array.IndexOf(projected, column) is not (var ordinal and >= 0))
                {
                    return null;
                }
                key[g] = ordinal;
            }
            return [key];
        }

        if (distinct)
            keys = [[.. Enumerable.Range(0, expressions.Count)]];

        // Each source's own key, projected bare, that determines every source.
        for (var s = 0; s < sources.Length; s++)
        {
            foreach (var sourceKey in SourceKeys(sources[s]))
            {
                var key = new int[sourceKey.Length];
                var given = new List<(int Source, int Column)>(sourceKey.Length);
                for (var k = 0; k < sourceKey.Length && key is not null; k++)
                {
                    var ordinal = Array.IndexOf(projected, (s, sourceKey[k]));
                    if (ordinal < 0)
                    {
                        key = null;
                    }
                    else
                    {
                        key[k] = ordinal;
                        given.Add((s, sourceKey[k]));
                    }
                }
                if (key is not null && DeterminesEverySource(sources, joins, fromClause.Excluders, given))
                    (keys ??= []).Add(key);
            }
        }
        return keys is null ? null : [.. keys];
    }

    /// <summary>
    /// Whether <paramref name="given"/> columns, closed over the equalities the
    /// <c>ON</c> and <c>WHERE</c> conjuncts state and over the keys they come
    /// to cover, determine a key of every source — so they pick at most one
    /// row of the joined rowset.
    /// </summary>
    private static bool DeterminesEverySource(FromSource[] sources, JoinSpec[] joins, List<BooleanExpression> excluders, List<(int Source, int Column)> given)
    {
        foreach (var join in joins)
        {
            // An APPLY ties nothing a key can carry. An outer join does, real
            // sending no warning over a key equated across a full or right
            // join either (probed 2026-10-06 against SQL Server 2025).
            if (join.Kind is JoinKind.CrossApply or JoinKind.OuterApply)
                return false;
        }

        var determined = new bool[sources.Length][];
        for (var s = 0; s < sources.Length; s++)
            determined[s] = new bool[sources[s].Columns.Length];
        foreach (var (source, column) in given)
            determined[source][column] = true;

        var equalities = new List<((int Source, int Column) Left, (int Source, int Column) Right, bool RightConstant)>();
        foreach (var conjunct in CollectJoinAndWhereConjuncts(joins, excluders))
        {
            if (!conjunct.TryGetEqualityOperands(out var left, out var right))
                continue;
            var leftColumn = ColumnOf(sources, left);
            var rightColumn = ColumnOf(sources, right);
            if (leftColumn.Source >= 0 && rightColumn.Source >= 0)
                equalities.Add((leftColumn, rightColumn, false));
            else if (leftColumn.Source >= 0 && !right.ReadsAnyColumn())
                equalities.Add((leftColumn, default, true));
            else if (rightColumn.Source >= 0 && !left.ReadsAnyColumn())
                equalities.Add((rightColumn, default, true));
        }

        var covered = new bool[sources.Length];
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var (left, right, rightConstant) in equalities)
            {
                if (rightConstant)
                {
                    changed |= Mark(left);
                }
                else if (determined[left.Source][left.Column] != determined[right.Source][right.Column])
                {
                    changed |= Mark(left);
                    changed |= Mark(right);
                }
            }
            for (var s = 0; s < sources.Length; s++)
            {
                if (covered[s])
                    continue;
                foreach (var key in SourceKeys(sources[s]))
                {
                    if (Array.TrueForAll(key, column => determined[s][column]))
                    {
                        covered[s] = changed = true;
                        Array.Fill(determined[s], true);
                        break;
                    }
                }
            }
        }
        return Array.TrueForAll(covered, static isCovered => isCovered);

        bool Mark((int Source, int Column) column)
        {
            if (determined[column.Source][column.Column])
                return false;
            determined[column.Source][column.Column] = true;
            return true;
        }
    }

    private static (int Source, int Column) ColumnOf(FromSource[] sources, Expression expression) =>
        Unwrapped(expression) is Reference reference ? FindSourceColumnOfAnyKind(sources, reference.ReferencedName) : (-1, -1);

    private static Expression Unwrapped(Expression expression) =>
        expression is NamedExpression named ? Unwrapped(named.Inner) : expression;

    /// <summary>
    /// The column-ordinal sets no two rows of <paramref name="source"/> share:
    /// a table's primary key and unique constraints and its enabled,
    /// unfiltered unique indexes, or what a derived source's plan knows of its
    /// own output (<see cref="OutputKeys"/>).
    /// </summary>
    private static IEnumerable<int[]> SourceKeys(FromSource source)
    {
        if (source.BackingTable is { } table)
        {
            // The source must read the table's columns in place.
            if (!ReferenceEquals(source.Columns, table.Columns))
                yield break;
            foreach (var key in table.KeyConstraints)
            {
                if (key.IsDisabled)
                    continue;
                var ordinals = new int[key.StorageOrdinals.Length];
                for (var k = 0; k < ordinals.Length; k++)
                    ordinals[k] = Array.IndexOf(table.StorageOrdinals, key.StorageOrdinals[k]);
                if (Array.IndexOf(ordinals, -1) < 0)
                    yield return ordinals;
            }
            foreach (var index in table.Indexes)
            {
                if (index is { IsUnique: true, Filter: null, IsDisabled: false, IsHypothetical: false, IsColumnstore: false })
                    yield return index.KeyFullOrdinals;
            }
        }
        else if (source.LateralPlan?.OutputKeys is { } keys)
        {
            foreach (var key in keys)
                yield return key;
        }
    }

    /// <summary>
    /// <see cref="OutputKeys"/> for a <c>VALUES</c> list: each column whose
    /// cells are written constants of one type, at most one of them NULL, no
    /// two equal — strings compared case- and trailing-space-blind, as a
    /// grouping under any collation might fold them.
    /// </summary>
    internal static int[][]? ConstantRowKeys(List<Expression[]> tuples, int arity, BatchContext? batch)
    {
        List<int[]>? keys = null;
        for (var c = 0; c < arity; c++)
        {
            var strings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var others = new HashSet<SqlValue>();
            Type? type = null;
            var nulls = 0;
            var unique = true;
            foreach (var tuple in tuples)
            {
                if (ConstantOf(tuple[c], batch) is not { } constant)
                {
                    unique = false;
                }
                else if (constant.IsNull)
                {
                    unique = ++nulls <= 1;
                }
                else if ((type ??= constant.Type.GetType()) != constant.Type.GetType())
                {
                    unique = false;
                }
                else
                {
                    unique = constant.Type.Category == SqlTypeCategory.String
                        ? strings.Add(constant.AsString.TrimEnd(' '))
                        : others.Add(constant);
                }
                if (!unique)
                    break;
            }
            if (unique)
                (keys ??= []).Add([c]);
        }
        return keys is null ? null : [.. keys];
    }

    /// <summary>
    /// The value of a written constant — one folded over constants (<c>-1</c>)
    /// only given a <paramref name="batch"/> to run it in — or null when the
    /// cell isn't one or raises.
    /// </summary>
    private static SqlValue? ConstantOf(Expression cell, BatchContext? batch)
    {
        if (cell is Value { Constant: var constant })
            return constant;
        if (batch is null || !cell.IsWrittenConstant || cell.ReadsAnyColumn())
            return null;
        try
        {
            return cell.Run(new RuntimeContext(static name => throw SimulatedSqlException.InvalidColumnName(name), batch));
        }
        catch (SimulatedSqlException)
        {
            return null;
        }
    }
}
