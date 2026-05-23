using SqlServerSimulator.Storage;
using Index = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared lookup machinery for the <c>INDEX_COL</c> /
/// <c>INDEXKEY_PROPERTY</c> / <c>STATS_DATE</c> functions: resolves an
/// <c>index_id</c> back to the corresponding <see cref="Storage.Index"/> or
/// <see cref="KeyConstraint"/> on a <see cref="HeapTable"/>, mirroring the
/// emission order used by <c>sys.indexes</c> / <c>sys.index_columns</c>.
/// </summary>
/// <remarks>
/// The emission order (probed against SQL Server 2025) is:
/// <list type="number">
/// <item><description><c>index_id = 1</c>: the table's PK as a clustered
/// index — or, on a heap, a synthetic <c>index_id = 0</c> "HEAP" row that
/// has no key columns (this function returns <c>null</c> for that case).</description></item>
/// <item><description><c>index_id ≥ 2</c> (or ≥ 1 on a heap): remaining
/// <see cref="KeyConstraint"/>s (UNIQUE) plus user
/// <see cref="Storage.Index"/>es, sorted by <c>ObjectId</c>.</description></item>
/// </list>
/// </remarks>
internal static class IndexLookup
{
    /// <summary>
    /// Resolves <paramref name="indexId"/> against <paramref name="table"/>.
    /// Returns either a <see cref="KeyConstraint"/> (PK / UQ) or a
    /// <see cref="Storage.Index"/>, never both. Returns <c>null</c> when the
    /// id is out of range or refers to the heap row.
    /// </summary>
    public static (KeyConstraint? Constraint, Index? Index)? ResolveByIndexId(HeapTable table, int indexId)
    {
        if (indexId < 1)
            return null;

        KeyConstraint? primaryKey = null;
        foreach (var k in table.KeyConstraints)
        {
            if (k.Kind == KeyConstraintKind.PrimaryKey)
            {
                primaryKey = k;
                break;
            }
        }

        var hasPk = primaryKey is not null;
        if (hasPk && indexId == 1)
            return (primaryKey, null);

        var others = new List<(int ObjectId, KeyConstraint? Key, Index? Index)>();
        foreach (var k in table.KeyConstraints)
        {
            if (!ReferenceEquals(k, primaryKey))
                others.Add((k.ObjectId, k, null));
        }
        foreach (var ix in table.Indexes)
            others.Add((ix.ObjectId, null, ix));
        others.Sort(static (a, b) => a.ObjectId.CompareTo(b.ObjectId));

        var ordinal = hasPk ? 2 : 1;
        foreach (var (_, key, index) in others)
        {
            if (ordinal == indexId)
                return (key, index);
            ordinal++;
        }
        return null;
    }

    /// <summary>
    /// Returns the (storage-ordinal, IsDescending) pair for the
    /// <paramref name="keyId"/>-th (1-based) key column of the resolved
    /// index, or <c>null</c> if <paramref name="keyId"/> is out of range or
    /// refers to an INCLUDE column. PK / UQ constraints don't track per-
    /// column DESC, so they always report <c>false</c>.
    /// </summary>
    public static (int StorageOrdinal, bool IsDescending)? GetKeyColumn(KeyConstraint? constraint, Index? index, int keyId)
    {
        if (keyId < 1)
            return null;
        if (constraint is not null)
        {
            return keyId > constraint.StorageOrdinals.Length
                ? default((int, bool)?)
                : (constraint.StorageOrdinals[keyId - 1], false);
        }
        return index is null
            ? null
            : keyId > index.KeyColumns.Length
                ? default((int, bool)?)
                : (index.KeyColumns[keyId - 1].StorageOrdinal, index.KeyColumns[keyId - 1].IsDescending);
    }

    /// <summary>
    /// Returns the 1-based declaration ordinal (i.e. <c>sys.columns.column_id</c>)
    /// for the given storage ordinal on <paramref name="table"/>, or 0 if
    /// the storage ordinal isn't found.
    /// </summary>
    public static int StorageOrdinalToColumnId(HeapTable table, int storageOrdinal)
    {
        for (var i = 0; i < table.StorageOrdinals.Length; i++)
        {
            if (table.StorageOrdinals[i] == storageOrdinal)
                return i + 1;
        }
        return storageOrdinal + 1;
    }
}
