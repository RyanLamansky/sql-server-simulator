using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Set-operator combination (UNION / UNION ALL / INTERSECT / EXCEPT) and the
/// top-level ORDER BY pass that wraps a set-op chain. NULL semantics here are
/// the multiset variant — two NULLs are equal when comparing rows for dedup or
/// matching, the opposite of <c>=</c>'s tri-state behavior.
/// </summary>
internal sealed partial class Selection
{
    /// <summary>
    /// Combines two SELECT plans via a set operator (UNION / UNION ALL /
    /// INTERSECT / EXCEPT). Validates that the branches have the same
    /// column count (Msg 205), promotes per-column types via
    /// <see cref="SqlType.Promote"/>, and rejects per-branch ORDER BY
    /// (which the parser tolerates greedily for the first branch via
    /// <see cref="HasOrderBy"/>; if it stuck around when a set operator
    /// follows, that's a syntax error).
    /// </summary>
    internal static Selection CombineSetOps(Selection left, Selection right, SetOpKind kind)
    {
        if (left.HasOrderBy)
        {
            var setOpKeyword = kind switch
            {
                SetOpKind.Union or SetOpKind.UnionAll => "union",
                SetOpKind.Intersect => "intersect",
                SetOpKind.Except => "except",
                _ => throw new InvalidOperationException($"Unknown SetOpKind {kind}."),
            };
            throw SimulatedSqlException.PerBranchOrderByRejected(setOpKeyword);
        }

        if (left.Schema.Length != right.Schema.Length)
            throw SimulatedSqlException.SetOpUnequalColumnCount();

        // SELECT INTO on a set-op chain: probe-confirmed against SQL Server
        // 2025 that INTO is only valid on the FIRST branch (parses inside
        // the first SELECT's projection-clause-end). A right branch carrying
        // its own INTO is a syntax error in real SQL Server too — the
        // simulator detects this and rejects. Identity is always dropped on
        // set-op results (probed), so the combined Selection emits a
        // synthesized dest schema with no identity even if the left branch
        // had one.
        if (right.IntoTarget is not null)
            throw SimulatedSqlException.SyntaxErrorNearKeyword("into");

        var combinedSchema = new SqlType[left.Schema.Length];
        for (var i = 0; i < combinedSchema.Length; i++)
            combinedSchema[i] = SqlType.Promote(left.Schema[i], right.Schema[i]);

        // Result column names come from the first (leftmost) branch.
        var combinedNames = left.ColumnNames;

        // Propagate INTO from the left branch; strip identity on each
        // destination column since set-op results lose the source's
        // identity property.
        HeapColumn[]? combinedDestSchema = null;
        if (left.IntoTarget is not null && left.DestColumnSchema is { } leftDest)
        {
            combinedDestSchema = new HeapColumn[combinedSchema.Length];
            for (var i = 0; i < combinedDestSchema.Length; i++)
            {
                combinedDestSchema[i] = new HeapColumn(
                    leftDest[i].Name,
                    combinedSchema[i],
                    maxLength: null,
                    nullable: leftDest[i].Nullable,
                    identity: null);
            }
        }

        return new Selection(combinedSchema, combinedNames,
            hasOrderBy: false,
            hasTopOrOffsetOrFetch: left.HasTopOrOffsetOrFetch || right.HasTopOrOffsetOrFetch,
            (batch, outerResolver) => kind switch
        {
            SetOpKind.UnionAll => ConcatBranchRows(left, right, combinedSchema, batch, outerResolver),
            SetOpKind.Union => DedupeUnionRows(left, right, combinedSchema, batch, outerResolver),
            SetOpKind.Intersect => IntersectRows(left, right, combinedSchema, batch, outerResolver),
            SetOpKind.Except => ExceptRows(left, right, combinedSchema, batch, outerResolver),
            _ => throw new InvalidOperationException($"Unknown SetOpKind {kind}."),
        }, intoTarget: left.IntoTarget, destColumnSchema: combinedDestSchema);
    }

    /// <summary>
    /// Materializes a branch's rows and coerces each value to the
    /// combined schema's per-column type. Pass-through fast path when
    /// the branch's schema already matches; otherwise decode, coerce,
    /// re-encode each row.
    /// </summary>
    private static IEnumerable<byte[]> CoerceBranchRows(Selection branch, SqlType[] targetSchema, BatchContext batch, Func<MultiPartName, SqlValue>? outerResolver)
    {
        var resultSet = branch.Execute(batch, outerResolver);
        var sourceSchema = resultSet.Schema;

        var sameTypes = true;
        for (var i = 0; i < sourceSchema.Length; i++)
        {
            if (sourceSchema[i] != targetSchema[i]) { sameTypes = false; break; }
        }

        if (sameTypes)
        {
            foreach (var rowBytes in resultSet.RowBytes)
                yield return rowBytes;
            yield break;
        }

        foreach (var rowBytes in resultSet.RowBytes)
        {
            var values = new SqlValue[targetSchema.Length];
            for (var i = 0; i < sourceSchema.Length; i++)
            {
                var v = RowDecoder.DecodeColumn(sourceSchema, rowBytes, i);
                values[i] = v.IsNull ? SqlValue.Null(targetSchema[i]) : v.CoerceTo(targetSchema[i]);
            }
            yield return RowEncoder.EncodeRow(targetSchema, values);
        }
    }

    private static IEnumerable<byte[]> ConcatBranchRows(Selection left, Selection right, SqlType[] schema, BatchContext batch, Func<MultiPartName, SqlValue>? outer)
    {
        foreach (var r in CoerceBranchRows(left, schema, batch, outer)) yield return r;
        foreach (var r in CoerceBranchRows(right, schema, batch, outer)) yield return r;
    }

    /// <summary>
    /// Decodes a row's bytes into a <see cref="SqlValue"/> array using
    /// the combined schema. Used by the dedup-aware set ops (UNION /
    /// INTERSECT / EXCEPT) for HashSet keying via
    /// <see cref="RowEqualityComparer"/>.
    /// </summary>
    private static SqlValue[] DecodeRowToValues(byte[] bytes, SqlType[] schema)
    {
        var values = new SqlValue[schema.Length];
        for (var i = 0; i < schema.Length; i++)
            values[i] = RowDecoder.DecodeColumn(schema, bytes, i);
        return values;
    }

    private static IEnumerable<byte[]> DedupeUnionRows(Selection left, Selection right, SqlType[] schema, BatchContext batch, Func<MultiPartName, SqlValue>? outer)
    {
        var seen = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, batch, outer).Concat(CoerceBranchRows(right, schema, batch, outer)))
        {
            if (seen.Add(DecodeRowToValues(rowBytes, schema)))
                yield return rowBytes;
        }
    }

    private static IEnumerable<byte[]> IntersectRows(Selection left, Selection right, SqlType[] schema, BatchContext batch, Func<MultiPartName, SqlValue>? outer)
    {
        var rightSet = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rb in CoerceBranchRows(right, schema, batch, outer))
            _ = rightSet.Add(DecodeRowToValues(rb, schema));

        var emitted = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, batch, outer))
        {
            var values = DecodeRowToValues(rowBytes, schema);
            if (rightSet.Contains(values) && emitted.Add(values))
                yield return rowBytes;
        }
    }

    private static IEnumerable<byte[]> ExceptRows(Selection left, Selection right, SqlType[] schema, BatchContext batch, Func<MultiPartName, SqlValue>? outer)
    {
        var rightSet = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rb in CoerceBranchRows(right, schema, batch, outer))
            _ = rightSet.Add(DecodeRowToValues(rb, schema));

        var emitted = new HashSet<SqlValue[]>(RowEqualityComparer.Instance);
        foreach (var rowBytes in CoerceBranchRows(left, schema, batch, outer))
        {
            var values = DecodeRowToValues(rowBytes, schema);
            if (!rightSet.Contains(values) && emitted.Add(values))
                yield return rowBytes;
        }
    }

    /// <summary>
    /// Wraps a Selection (typically the combined result of a set-op
    /// chain) with a top-level ORDER BY pass. References resolve against
    /// the inner plan's projected column names and ordinals only — there
    /// are no source columns to fall back to. (Single-SELECT queries
    /// with ORDER BY take a different path: ORDER BY stays inside the
    /// branch's projection so it can reference non-projected source
    /// columns, matching SQL Server's documented behavior.)
    /// </summary>
    private static Selection ApplyTopLevelOrderBy(Selection inner, List<OrderBySpec> orderBy, Expression? offsetExpression, Expression? fetchExpression)
    {
        var schema = inner.Schema;
        var columnNames = inner.ColumnNames;

        return new Selection(schema, columnNames,
            hasOrderBy: true,
            hasTopOrOffsetOrFetch: inner.HasTopOrOffsetOrFetch || offsetExpression is not null || fetchExpression is not null,
            (batch, outerResolver) =>
        {
            // Per-execution count resolution: the expressions may carry
            // parameters, and this closure replays across executions of a
            // plan-cached SELECT.
            var offsetCount = ResolveRowCountLimit(offsetExpression, RowLimitKind.Offset, batch);
            var fetchCount = ResolveRowCountLimit(fetchExpression, RowLimitKind.Fetch, batch);
            var allRows = inner.Execute(batch, outerResolver).RowBytes.ToList();

            IEnumerable<byte[]> ordered;
            if (orderBy.Count == 0 || allRows.Count <= 1)
            {
                ordered = allRows;
            }
            else
            {
                var keyed = new List<(byte[] Row, SqlValue[] Keys)>(allRows.Count);
                foreach (var rowBytes in allRows)
                {
                    var values = DecodeRowToValues(rowBytes, schema);
                    SqlValue ResolveByOutputName(MultiPartName name)
                    {
                        for (var j = 0; j < columnNames.Length; j++)
                        {
                            if (BuiltInToken.Equals(columnNames[j], name.Leaf))
                                return values[j];
                        }
                        throw SimulatedSqlException.InvalidColumnName(name);
                    }

                    var keys = ComputeOrderKeys(orderBy, values, columnNames, distinct: false, batch, ResolveByOutputName);
                    keyed.Add((rowBytes, keys));
                }

                keyed.Sort((a, b) => CompareOrderKeys(a.Keys, b.Keys, orderBy));
                ordered = keyed.Select(r => r.Row);
            }

            return ApplyOffsetTake(ordered, offsetCount, fetchCount);
        }, intoTarget: inner.IntoTarget, destColumnSchema: inner.DestColumnSchema);
    }
}

/// <summary>
/// Equality comparer for projected rows (<see cref="SqlValue"/> tuples). Used
/// by DISTINCT to dedupe based on the same equality semantics as the
/// <c>=</c> operator: collation-aware string comparison, ANSI trailing-space
/// padding, two NULLs of the same type compare equal, and
/// <c>datetimeoffset</c> compares by UTC instant.
/// </summary>
internal sealed class RowEqualityComparer : IEqualityComparer<SqlValue[]>
{
    public static readonly RowEqualityComparer Instance = new();

    public bool Equals(SqlValue[]? x, SqlValue[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null || x.Length != y.Length) return false;
        for (var i = 0; i < x.Length; i++)
            if (!x[i].Equals(y[i])) return false;
        return true;
    }

    public int GetHashCode(SqlValue[] obj)
    {
        var hash = new HashCode();
        foreach (var v in obj)
            hash.Add(v);
        return hash.ToHashCode();
    }
}
