using System.Collections.Concurrent;
using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The key locks of one key or index of a table — the resources real SQL
/// Server's <c>KEY</c> locks name. Each lock anchors on one key tuple of the
/// index, or on the infinity position past its last key, and a range mode held
/// there covers the gap below the anchor down to the next lower key present at
/// the time a writer tests it. That is real's own shape, which is what makes
/// the next-key lock, the infinity lock and an empty range's lock come out as
/// real's do: a SERIALIZABLE read of <c>k BETWEEN 15 AND 25</c> over keys 10 /
/// 20 / 30 anchors on 20 and 30, so an insert of 12 or 28 waits and one of 35
/// doesn't (probed 2026-09-28 against SQL Server 2025).
/// <para>
/// The coverage is never stored: a writer inserting a key asks the seek cache
/// for the first key above it and tests that anchor, as real's insert tests the
/// next key's lock, so a gap that widens or narrows between the reader's lock
/// and the writer's insert is judged as it stands.
/// </para>
/// <para>
/// A table's clustered key or index is its <see cref="IsRowGroup"/>: a row's
/// identity, so its anchors conflict with every write of the row and with a
/// reader's own row lock. A nonclustered index's anchors conflict only with a
/// write that changes a column the index row carries — real's update of a
/// column no index names takes no lock on that index.
/// </para>
/// </summary>
internal sealed class KeyLockGroup
{
    /// <summary>The table whose key this is.</summary>
    public readonly HeapTable Table;

    /// <summary>The <see cref="KeyConstraint"/> or <see cref="Index"/> the key belongs to.</summary>
    public readonly object Owner;

    /// <summary>
    /// Storage ordinals of the anchor tuple: the index key, followed for a
    /// non-unique nonclustered index by the clustered key columns it doesn't
    /// already name — the row locator real appends, which is what makes each
    /// entry of a duplicated value an anchor of its own.
    /// </summary>
    public readonly int[] Ordinals;

    /// <summary>Per ordinal, the stored column type an anchor component is held in.</summary>
    public readonly SqlType[] Commons;

    /// <summary>Whether the key is the table's clustered key.</summary>
    public readonly bool IsRowGroup;

    /// <summary>
    /// Whether an equality on every key column finds at most one entry, which
    /// is what lets real take a plain key lock on a hit rather than a range.
    /// </summary>
    public readonly bool IsUnique;

    /// <summary>
    /// Number of leading <see cref="Ordinals"/> that are the key proper, the
    /// rest being the appended row locator.
    /// </summary>
    public readonly int KeyLength;

    // Storage ordinals a nonclustered index row carries (key, INCLUDE columns
    // and the clustered key), for the update filter; null for the row group.
    private readonly bool[]? carried;

    // Where real's index row carries a non-unique clustered index's
    // uniquifier, which the anchor tuple leaves out (see KeyLockHash).
    private readonly KeyLockUniquifier uniquifier;

    /// <summary>Anchors interned per key tuple, leaking the way <see cref="HeapTable.RowLocks"/> does.</summary>
    public readonly ConcurrentDictionary<SqlValueKey, LockResource> Anchors = new();

    /// <summary>The anchor past the last key — real's <c>ffffffffffff</c> resource.</summary>
    public readonly LockResource Infinity;

    /// <summary>
    /// Holds live across this group's anchors, maintained by
    /// <see cref="LockManager"/> under its gate, so a writer tests only the
    /// groups somebody holds a lock in.
    /// </summary>
    public int Holds;

    private KeyLockGroup(HeapTable table, object owner, int[] ordinals, int keyLength, bool isRowGroup, bool isUnique, bool[]? carried, KeyLockUniquifier uniquifier)
    {
        this.uniquifier = uniquifier;
        this.Table = table;
        this.Owner = owner;
        this.Ordinals = ordinals;
        this.KeyLength = keyLength;
        this.IsRowGroup = isRowGroup;
        this.IsUnique = isUnique;
        this.carried = carried;
        this.Commons = new SqlType[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
            this.Commons[i] = table.StoredColumns[ordinals[i]].Type;
        this.Infinity = new LockResource { OwningTable = table, KeyGroup = this };
    }

    /// <summary>The anchor on <paramref name="key"/>, or the infinity anchor for null, interned on first use.</summary>
    public LockResource GetOrCreate(SqlValueKey? key) =>
        key is { } k
            ? this.Anchors.GetOrAdd(k, static (anchor, group) => new LockResource { OwningTable = group.Table, KeyGroup = group, AnchorKey = anchor }, this)
            : this.Infinity;

    /// <summary>The anchor on <paramref name="key"/> if one was ever interned, the infinity anchor for null.</summary>
    public LockResource? Find(SqlValueKey? key) =>
        key is { } k
            ? this.Anchors.TryGetValue(k, out var resource) ? resource : null
            : this.Infinity;

    /// <summary>
    /// Reads <paramref name="image"/>'s anchor tuple; false when a component is
    /// NULL, a key the seek cache doesn't order and so can't anchor.
    /// </summary>
    public bool TryReadKey(byte[] image, out SqlValueKey key) =>
        HeapSeekCache.TryComputeKey(image, this.Ordinals, this.Commons, this.Table.StoredColumns, this.Table.Heap, out key);

    /// <summary>
    /// Whether rewriting <paramref name="oldImage"/> as <paramref name="newImage"/>
    /// touches this index's row: always for the row group, and for a
    /// nonclustered index only when a column the index row carries changes.
    /// </summary>
    public bool RowChanges(byte[] oldImage, byte[] newImage)
    {
        if (this.carried is not { } carried)
            return true;
        var schema = this.Table.StoredColumns;
        for (var ordinal = 0; ordinal < carried.Length; ordinal++)
        {
            if (!carried[ordinal])
                continue;
            var before = RowDecoder.DecodeColumn(schema, oldImage, ordinal, this.Table.Heap);
            var after = RowDecoder.DecodeColumn(schema, newImage, ordinal, this.Table.Heap);
            if (before.IsNull != after.IsNull || (!before.IsNull && !before.IsIdenticalTo(after)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The <c>resource_description</c> <c>sys.dm_tran_locks</c> reports for the
    /// anchor on <paramref name="key"/>: real's hash of the key
    /// (<see cref="KeyLockHash"/>), or its <c>(ffffffffffff)</c> for the
    /// infinity anchor. A non-unique nonclustered index over a heap hashes its
    /// key alone, where real's carries the row's RID too.
    /// </summary>
    public string Describe(SqlValueKey? key) =>
        key is { } k ? KeyLockHash.Describe(k, this.Commons, this.uniquifier) : "(ffffffffffff)";

    /// <summary>
    /// The group for <paramref name="owner"/> — one of <paramref name="table"/>'s
    /// key constraints or indexes — or null for a key the seek cache can't
    /// order: a columnstore index, a key naming a non-persisted computed column
    /// or a LOB column.
    /// </summary>
    public static KeyLockGroup? For(HeapTable table, object owner)
    {
        if (table.KeyLockGroups.TryGetValue(owner, out var existing))
            return existing;
        if (Create(table, owner) is not { } created)
            return null;
        return table.KeyLockGroups.GetOrAdd(owner, created);
    }

    /// <summary>The group of <paramref name="table"/>'s clustered key, or null for a heap.</summary>
    public static KeyLockGroup? RowGroupOf(HeapTable table) =>
        ClusteredOwner(table) is { } owner ? For(table, owner) : null;

    /// <summary>
    /// <paramref name="table"/>'s clustered key constraint or index, or null
    /// for a heap. A disabled clustered index makes the table unreadable, so
    /// it anchors nothing.
    /// </summary>
    public static object? ClusteredOwner(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsClustered)
                return key.IsDisabled ? null : key;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsClustered && !index.IsColumnstore)
                return index.IsDisabled ? null : index;
        }
        return null;
    }

    private static KeyLockGroup? Create(HeapTable table, object owner)
    {
        int[] keyOrdinals;
        bool isClustered;
        bool isUnique;
        int[] included;
        switch (owner)
        {
            case KeyConstraint key:
                keyOrdinals = key.StorageOrdinals;
                isClustered = key.IsClustered;
                isUnique = true;
                included = [];
                break;
            case Index index when !index.IsColumnstore && index.KeyColumns.Length != 0:
                keyOrdinals = index.KeyStorageOrdinals;
                isClustered = index.IsClustered;
                isUnique = index.IsUnique;
                included = index.IncludedColumns;
                break;
            default:
                return null;
        }

        var schema = table.StoredColumns;
        foreach (var ordinal in keyOrdinals)
        {
            if ((uint)ordinal >= (uint)schema.Length || schema[ordinal].Type.IsLob)
                return null;
        }

        if (isClustered)
            return new KeyLockGroup(table, owner, keyOrdinals, keyOrdinals.Length, isRowGroup: true, isUnique, carried: null, isUnique ? KeyLockUniquifier.None : KeyLockUniquifier.First);

        var clusteredOwner = ClusteredOwner(table);
        var clustered = clusteredOwner switch
        {
            KeyConstraint key => key.StorageOrdinals,
            Index index => index.KeyStorageOrdinals,
            _ => [],
        };
        var ordinals = new List<int>(keyOrdinals);
        if (!isUnique)
        {
            foreach (var ordinal in clustered)
            {
                if (ordinal >= 0 && !ordinals.Contains(ordinal))
                    ordinals.Add(ordinal);
            }
        }

        var carried = new bool[schema.Length];
        foreach (var ordinal in keyOrdinals)
            carried[ordinal] = true;
        foreach (var ordinal in included)
        {
            if ((uint)ordinal < (uint)carried.Length)
                carried[ordinal] = true;
        }
        foreach (var ordinal in clustered)
        {
            if ((uint)ordinal < (uint)carried.Length)
                carried[ordinal] = true;
        }

        return new KeyLockGroup(table, owner, [.. ordinals], keyOrdinals.Length, isRowGroup: false, isUnique, carried,
            !isUnique && clusteredOwner is Index { IsUnique: false } ? KeyLockUniquifier.Last : KeyLockUniquifier.None);
    }
}
