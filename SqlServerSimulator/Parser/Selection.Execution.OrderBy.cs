using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-row ORDER BY key computation and lexicographic key comparison. Shared
/// by the buffered, windowed, and top-level set-op ORDER BY paths.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Evaluates each ORDER BY item against the current row. Ordinal items
    /// index directly into the projected row. Expression items resolve column
    /// references through an output-first resolver; without DISTINCT, names
    /// not in the output fall back to source columns (matching SQL Server's
    /// rule that ORDER BY can reference non-selected source columns). With
    /// DISTINCT, source fallback would be ambiguous post-dedup so a missing
    /// output match raises Msg 145.
    /// </summary>
    private static SqlValue[] ComputeOrderKeys(
        List<OrderBySpec> orderBy,
        SqlValue[] projected,
        string[] outputColumnNames,
        bool distinct,
        BatchContext batch,
        Func<MultiPartName, SqlValue> resolveSource)
    {
        var keys = new SqlValue[orderBy.Count];
        for (var i = 0; i < orderBy.Count; i++)
        {
            var spec = orderBy[i];
            if (spec.IsOrdinal)
            {
                keys[i] = projected[spec.Ordinal - 1];
                continue;
            }

            keys[i] = spec.Expr!.Run(new RuntimeContext(name =>
            {
                for (var j = 0; j < outputColumnNames.Length; j++)
                {
                    if (BuiltInToken.Equals(outputColumnNames[j], name.Leaf))
                        return projected[j];
                }
                return distinct
                    ? throw SimulatedSqlException.OrderByItemNotInSelectListWithDistinct()
                    : resolveSource(name);
            }, batch));
        }
        return keys;
    }

    /// <summary>
    /// Lexicographic compare of two key tuples per the per-key descending
    /// flags. NULL is treated as the smallest value (NULL first under ASC,
    /// NULL last under DESC), matching SQL Server. Cross-type keys are
    /// promoted via <see cref="SqlType.Promote"/> before comparison.
    /// </summary>
    private static int CompareOrderKeys(SqlValue[] a, SqlValue[] b, List<OrderBySpec> orderBy)
    {
        for (var i = 0; i < a.Length; i++)
        {
            var lk = a[i];
            var rk = b[i];
            int c;
            if (lk.IsNull && rk.IsNull)
            {
                c = 0;
            }
            else if (lk.IsNull)
            {
                c = -1;
            }
            else if (rk.IsNull)
            {
                c = 1;
            }
            else if (lk.Type == rk.Type)
            {
                c = lk.CompareTo(rk);
            }
            else
            {
                var common = SqlType.Promote(lk.Type, rk.Type);
                c = lk.CoerceTo(common).CompareTo(rk.CoerceTo(common));
            }

            if (orderBy[i].Descending)
                c = -c;
            if (c != 0)
                return c;
        }
        return 0;
    }

    /// <summary>
    /// Compares two non-NULL scalar values — the single-key, ascending form of
    /// <see cref="CompareOrderKeys"/>'s per-key compare (promoting to a common
    /// type when the declared types differ). Used to sort the WITHIN GROUP
    /// sort-key values for <c>PERCENTILE_CONT</c> / <c>PERCENTILE_DISC</c>.
    /// </summary>
    private static int CompareScalarValues(SqlValue a, SqlValue b)
    {
        if (a.Type == b.Type)
            return a.CompareTo(b);
        var common = SqlType.Promote(a.Type, b.Type);
        return a.CoerceTo(common).CompareTo(b.CoerceTo(common));
    }
}
