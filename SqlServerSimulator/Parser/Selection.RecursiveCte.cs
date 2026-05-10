using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Recursive CTE execution: anchor rows feed the first iteration; each
/// subsequent iteration runs the recursive branches against the previous
/// iteration's output until the recursion produces zero rows or
/// <see cref="CteBinding.MaxRecursion"/> trips. Anchor and recursive
/// branches arrive as separate <see cref="Selection"/> arrays so each
/// branch's parsed plan re-runs unchanged per iteration.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Builds a Selection that, when executed, runs the recursive-CTE
    /// fixed-point loop: anchor rows then iteration-bound recursive rows
    /// until empty (or <see cref="CteBinding.MaxRecursion"/> trips Msg 530).
    /// The schema and column names come from the first anchor branch — the
    /// recursive-CTE body parser has already enforced strict per-column type
    /// equality (Msg 240) across all branches.
    /// </summary>
    public static Selection FromRecursiveCte(
        Selection[] anchorBranches,
        Selection[] recursiveBranches,
        CteBinding binding)
    {
        var schema = anchorBranches[0].Schema;
        var columnNames = anchorBranches[0].ColumnNames;
        return new Selection(schema, columnNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: false,
            outerResolver => RunRecursiveCte(anchorBranches, recursiveBranches, binding, outerResolver));
    }

    private static IEnumerable<byte[]> RunRecursiveCte(
        Selection[] anchorBranches,
        Selection[] recursiveBranches,
        CteBinding binding,
        Func<MultiPartName, SqlValue>? outerResolver)
    {
        // Phase 1: union all anchor branches into the seed rowset.
        var seed = new List<byte[]>();
        foreach (var anchor in anchorBranches)
        {
            foreach (var rowBytes in anchor.Execute(outerResolver).RowBytes)
                seed.Add(rowBytes);
        }
        foreach (var rowBytes in seed)
            yield return rowBytes;

        // Phase 2: iterate. Each iteration binds CurrentIterationRows to
        // the previous iteration's output, runs all recursive branches,
        // unions their outputs, yields them, and feeds them as the next
        // iteration's input. Stops when an iteration produces no rows.
        var maxRecursion = binding.MaxRecursion;
        var iteration = 0;
        var current = seed;
        while (current.Count > 0)
        {
            iteration++;
            if (maxRecursion > 0 && iteration > maxRecursion)
                throw SimulatedSqlException.MaxRecursionExceeded(maxRecursion);

            binding.CurrentIterationRows = current;
            var next = new List<byte[]>();
            try
            {
                foreach (var rec in recursiveBranches)
                {
                    foreach (var rowBytes in rec.Execute(outerResolver).RowBytes)
                        next.Add(rowBytes);
                }
            }
            finally
            {
                binding.CurrentIterationRows = null;
            }

            foreach (var rowBytes in next)
                yield return rowBytes;

            current = next;
        }
    }
}
