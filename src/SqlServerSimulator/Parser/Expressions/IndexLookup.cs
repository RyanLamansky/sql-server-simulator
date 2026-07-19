using SqlServerSimulator.Storage;
using Index = SqlServerSimulator.Storage.Index;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared lookup machinery for the <c>INDEX_COL</c> /
/// <c>INDEXKEY_PROPERTY</c> / <c>STATS_DATE</c> functions: resolves an
/// <c>index_id</c> back to the corresponding <see cref="Index"/> or
/// <see cref="KeyConstraint"/> on a <see cref="HeapTable"/>, mirroring the
/// emission order used by <c>sys.indexes</c> / <c>sys.index_columns</c>.
/// </summary>
/// <remarks>
/// Resolution defers entirely to <see cref="HeapTable.IndexIdentities"/> — the
/// single index-id allocation authority — so <c>INDEX_COL</c> /
/// <c>INDEXKEY_PROPERTY</c> / <c>STATS_DATE</c> agree with <c>sys.indexes</c>
/// on every id. The clustered entry is <c>index_id = 1</c>; a heap's synthetic
/// <c>index_id = 0</c> row has no key columns (this function returns <c>null</c>
/// for ids below 1), and remaining indexes occupy 2..N.
/// </remarks>
internal static class IndexLookup
{
    /// <summary>
    /// Resolves <paramref name="indexId"/> against <paramref name="table"/>.
    /// Returns either a <see cref="KeyConstraint"/> (PK / UQ) or a
    /// <see cref="Index"/>, never both. Returns <c>null</c> when the
    /// id is out of range or refers to the heap row.
    /// </summary>
    public static (KeyConstraint? Constraint, Index? Index)? ResolveByIndexId(HeapTable table, int indexId)
    {
        if (indexId < 1)
            return null;

        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IndexId == indexId)
                return (identity.Constraint, identity.Index);
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
