using SqlServerSimulator.Parser;

namespace SqlServerSimulator.Storage;

/// <summary>What a statement did with an index, as <c>sys.dm_db_index_usage_stats</c> counts it.</summary>
internal enum IndexOperation : byte
{
    Seek,
    Scan,
    Lookup,
    Update,
}

/// <summary>
/// One index's counters since the simulation started: the uses
/// <c>sys.dm_db_index_usage_stats</c> reports — each kind counted once per
/// statement execution that performs it, with when it last happened — and the
/// row-level work <c>sys.dm_db_index_operational_stats</c> reports. Every
/// field moves through <see cref="Interlocked"/>, since sessions write tables
/// concurrently.
/// </summary>
/// <remarks>
/// What counts, probed 2026-10-16 against SQL Server 2025: a read seeking an
/// index is a user seek, one scanning it a user scan; a write is a user update
/// of every index it maintains — an <c>INSERT</c>'s and <c>DELETE</c>'s every
/// index, an <c>UPDATE</c>'s base index and each nonclustered index holding a
/// column it sets — whether or not it changes a row, a rolled-back or failed
/// statement included; a foreign key's check seeks the referenced key once per
/// statement. Per row: an inserted entry is a leaf insert, a deleted one a
/// leaf ghost (a heap's a leaf delete), a changed one a leaf update, or a
/// ghost and an insert where its key moved; an equality probe of a unique key
/// is a singleton lookup, and any other seek or scan a range scan each time it
/// runs.
/// </remarks>
internal sealed class IndexUsage
{
    public long UserSeeks;
    public long UserScans;
    public long UserLookups;
    public long UserUpdates;
    public long LastUserSeekTicks;
    public long LastUserScanTicks;
    public long LastUserLookupTicks;
    public long LastUserUpdateTicks;
    public long SystemScans;
    public long LastSystemScanTicks;

    /// <summary>Whether <c>sys.dm_db_index_usage_stats</c> lists the index, which it does from its first use.</summary>
    public bool Used;

    public long LeafInserts;
    public long LeafDeletes;
    public long LeafUpdates;
    public long LeafGhosts;
    public long RangeScans;
    public long SingletonLookups;

    /// <summary>Counts one <paramref name="operation"/> by a user statement at <paramref name="whenUtc"/>.</summary>
    public void NoteUser(IndexOperation operation, DateTime whenUtc)
    {
        this.Used = true;
        switch (operation)
        {
            case IndexOperation.Seek:
                _ = Interlocked.Increment(ref this.UserSeeks);
                _ = Interlocked.Exchange(ref this.LastUserSeekTicks, whenUtc.Ticks);
                break;
            case IndexOperation.Scan:
                _ = Interlocked.Increment(ref this.UserScans);
                _ = Interlocked.Exchange(ref this.LastUserScanTicks, whenUtc.Ticks);
                break;
            case IndexOperation.Lookup:
                _ = Interlocked.Increment(ref this.UserLookups);
                _ = Interlocked.Exchange(ref this.LastUserLookupTicks, whenUtc.Ticks);
                break;
            default:
                _ = Interlocked.Increment(ref this.UserUpdates);
                _ = Interlocked.Exchange(ref this.LastUserUpdateTicks, whenUtc.Ticks);
                break;
        }
    }

    /// <summary>Counts a scan the engine makes on its own account — a statistic's build reading its table.</summary>
    public void NoteSystemScan(DateTime whenUtc)
    {
        this.Used = true;
        _ = Interlocked.Increment(ref this.SystemScans);
        _ = Interlocked.Exchange(ref this.LastSystemScanTicks, whenUtc.Ticks);
    }
}

/// <summary>
/// The engine's side of <see cref="IndexUsage"/>: the read, write and
/// constraint-check paths report what they did here, and it charges the
/// table's indexes. A table no database owns — a <c>#temp</c> table, a table
/// variable, a trigger's pseudo-table — isn't counted.
/// </summary>
internal static class IndexUsageAccounting
{
    /// <summary>The index a scan of <paramref name="table"/>'s rows reads: the clustered one, else the heap.</summary>
    public static int BaseIndexId(HeapTable table) => table.HasClusteredIndex() ? 1 : 0;

    /// <summary>
    /// A read of <paramref name="table"/> by <paramref name="batch"/>'s
    /// statement: a seek of the index whose leading key columns are
    /// <paramref name="seekOrdinals"/> (storage ordinals, an equality prefix
    /// or a range's leading column), the base index when they name none, or
    /// with <paramref name="seek"/> false a scan of the base index. Only an
    /// <paramref name="equality"/> probe of a whole unique key is a singleton
    /// lookup.
    /// </summary>
    public static void NoteRead(BatchContext batch, HeapTable table, int[]? seekOrdinals, bool seek, bool equality = false, SeekedIndexMemo? memo = null)
    {
        if (table.OwningDatabase is null)
            return;
        if (!seek)
        {
            Charge(batch, table, BaseIndexId(table), IndexOperation.Scan);
            _ = Interlocked.Increment(ref table.UsageOf(BaseIndexId(table)).RangeScans);
            return;
        }
        (int IndexId, bool Singleton) seeked;
        if (memo is { Ordinals: { } known } && seekOrdinals is not null && known.AsSpan().SequenceEqual(seekOrdinals))
        {
            seeked = memo.Index;
        }
        else
        {
            seeked = SeekedIndex(table, seekOrdinals);
            if (memo is not null)
                (memo.Ordinals, memo.Index) = (seekOrdinals, seeked);
        }
        var (indexId, singleton) = seeked;
        Charge(batch, table, indexId, IndexOperation.Seek);
        var usage = table.UsageOf(indexId);
        _ = singleton && equality ? Interlocked.Increment(ref usage.SingletonLookups) : Interlocked.Increment(ref usage.RangeScans);
    }

    /// <summary>
    /// The index the last seek of one table a loop charged named, by its
    /// ordinals, so a seek per outer row of a join asks the table's indexes
    /// once rather than per row; the indexes can't change while the loop runs.
    /// </summary>
    public sealed class SeekedIndexMemo
    {
        public int[]? Ordinals;
        public (int IndexId, bool Singleton) Index;
    }

    /// <summary>
    /// A foreign key's check of <paramref name="rows"/> rows against
    /// <paramref name="table"/>'s key on <paramref name="keyOrdinals"/>: one
    /// seek of the statement, a singleton lookup per row when the key is
    /// unique — or, with no index leading on those columns, a scan.
    /// </summary>
    public static void NoteKeyCheck(BatchContext batch, HeapTable table, int[] keyOrdinals, int rows)
    {
        if (table.OwningDatabase is null)
            return;
        if (FindIndexLeadingOn(table, keyOrdinals) is not { } found)
        {
            NoteRead(batch, table, null, seek: false);
            return;
        }
        Charge(batch, table, found.IndexId, IndexOperation.Seek);
        var usage = table.UsageOf(found.IndexId);
        if (found.Unique && found.KeyCount == keyOrdinals.Length)
            _ = Interlocked.Add(ref usage.SingletonLookups, rows);
        else
            _ = Interlocked.Add(ref usage.RangeScans, rows);
    }

    /// <summary>
    /// A statement's write of <paramref name="rows"/> rows to
    /// <paramref name="table"/> — zero when it only announces the write, which
    /// is what counts a write that changes no row: an insert or delete charges
    /// every index, an update (<paramref name="updatedOrdinals"/> its set
    /// columns' full ordinals, the same list on every call) the base index and
    /// each nonclustered index holding a set column. A row-at-a-time writer
    /// calls it per row, so what to charge is settled once per statement
    /// (<see cref="StatementContext.IndexWrites"/>).
    /// </summary>
    public static void NoteWrite(BatchContext batch, HeapTable table, WriteKind kind, long rows, IReadOnlyList<int>? updatedOrdinals = null)
    {
        if (table.OwningDatabase is null || batch.IsSkipping)
            return;
        // A query notification subscription over the table fires on the write's commit.
        if (batch.Connection.Simulation.QuerySubscriptionCount != 0)
            batch.Connection.Simulation.NoteSubscribedWrite(batch, table, kind switch { WriteKind.Insert => "insert", WriteKind.Delete => "delete", _ => "update" });
        var statement = batch.CurrentStatement;
        var plan = statement.IndexWrites;
        if (plan is null || !ReferenceEquals(plan.Table, table) || plan.Kind != kind || !ReferenceEquals(plan.UpdatedOrdinals, updatedOrdinals))
            statement.IndexWrites = plan = PlanWrite(batch, table, kind, updatedOrdinals);
        if (rows == 0)
            return;
        foreach (var usage in plan.Inserts)
            _ = Interlocked.Add(ref usage.LeafInserts, rows);
        foreach (var usage in plan.Ghosts)
            _ = Interlocked.Add(ref usage.LeafGhosts, rows);
        foreach (var usage in plan.Updates)
            _ = Interlocked.Add(ref usage.LeafUpdates, rows);
        foreach (var usage in plan.Deletes)
            _ = Interlocked.Add(ref usage.LeafDeletes, rows);
    }

    /// <summary>Which indexes a write maintains and how, each charged its user update as the plan is made.</summary>
    private static IndexWritePlan PlanWrite(BatchContext batch, HeapTable table, WriteKind kind, IReadOnlyList<int>? updatedOrdinals)
    {
        var plan = new IndexWritePlan(table, kind, updatedOrdinals);
        var clusteredKey = ClusteredKeyOrdinals(table);
        var keyMoves = kind == WriteKind.Update && clusteredKey is not null && Intersects(clusteredKey, updatedOrdinals);
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.Index is { IsHypothetical: true })
                continue;
            var usage = table.UsageOf(identity.IndexId);
            switch (kind)
            {
                case WriteKind.Insert:
                    plan.Inserts.Add(usage);
                    break;
                case WriteKind.Delete:
                    (identity.IsHeap ? plan.Deletes : plan.Ghosts).Add(usage);
                    break;
                default:
                    var (keys, holds) = identity.IndexId <= 1 ? (keyMoves, true) : NonclusteredTouch(identity, updatedOrdinals, keyMoves);
                    if (!holds)
                        continue;
                    if (keys)
                    {
                        plan.Ghosts.Add(usage);
                        plan.Inserts.Add(usage);
                    }
                    else
                    {
                        plan.Updates.Add(usage);
                    }
                    break;
            }
            Charge(batch, table, identity.IndexId, IndexOperation.Update);
        }
        return plan;
    }

    /// <summary>
    /// An index build's or <c>UPDATE STATISTICS</c>' read of
    /// <paramref name="table"/>'s rows, a scan of the base index on the
    /// engine's own account when it holds any. Real loads no statistic for a
    /// trivial plan, so the simulator's automatic creations and refreshes,
    /// which follow its own predicates, count nothing.
    /// </summary>
    public static void NoteStatisticsScan(BatchContext batch, HeapTable table)
    {
        if (table.OwningDatabase is null || batch.IsSkipping || table.Heap.RowCount == 0)
            return;
        var usage = table.UsageOf(BaseIndexId(table));
        usage.NoteSystemScan(batch.CurrentStatement.UtcNow);
        _ = Interlocked.Increment(ref usage.RangeScans);
    }

    private static void Charge(BatchContext batch, HeapTable table, int indexId, IndexOperation operation)
    {
        var statement = batch.CurrentStatement;
        var counted = statement.CountedIndexUses ??= [];
        if (counted.Add((table, indexId, operation)))
            table.UsageOf(indexId).NoteUser(operation, statement.UtcNow);
    }

    /// <summary>
    /// Whether an update setting <paramref name="updatedOrdinals"/> maintains
    /// a nonclustered index — a key column, an included one, or the
    /// clustering key it carries moving — and whether its entry moves rather
    /// than changes in place.
    /// </summary>
    private static (bool Keys, bool Holds) NonclusteredTouch(IndexIdentity identity, IReadOnlyList<int>? updatedOrdinals, bool clusteringKeyMoves)
    {
        if (clusteringKeyMoves)
            return (true, true);
        if (identity.Constraint is { } constraint)
            return Intersects(constraint.FullOrdinals, updatedOrdinals) ? (true, true) : (false, false);
        if (identity.Index is not { } index)
            return (false, false);
        if (Intersects(index.KeyFullOrdinals, updatedOrdinals))
            return (true, true);
        if (Intersects(index.IncludedColumnOrdinals, updatedOrdinals))
            return (false, true);
        return (false, false);
    }

    private static int[]? ClusteredKeyOrdinals(HeapTable table)
    {
        foreach (var key in table.KeyConstraints)
        {
            if (key.IsClustered)
                return key.FullOrdinals;
        }
        foreach (var index in table.Indexes)
        {
            if (index.IsClustered && !index.IsColumnstore)
                return index.KeyFullOrdinals;
        }
        return null;
    }

    private static bool Intersects(int[] ordinals, IReadOnlyList<int>? updated)
    {
        if (updated is null)
            return false;
        foreach (var ordinal in ordinals)
        {
            for (var i = 0; i < updated.Count; i++)
            {
                if (updated[i] == ordinal)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The index a seek on <paramref name="seekOrdinals"/> reads — the one
    /// whose leading storage key columns they are, the clustered index or
    /// key first, then the narrowest — and whether the probe is a singleton
    /// (every key column of a unique index); the base index, not a
    /// singleton, when no index leads on them.
    /// </summary>
    private static (int IndexId, bool Singleton) SeekedIndex(HeapTable table, int[]? seekOrdinals) =>
        seekOrdinals is { Length: > 0 } && FindIndexLeadingOn(table, seekOrdinals, storage: true) is { } found
            ? (found.IndexId, found.Unique && found.KeyCount == seekOrdinals.Length)
            : (BaseIndexId(table), false);

    private static (int IndexId, bool Unique, int KeyCount)? FindIndexLeadingOn(HeapTable table, int[] ordinals, bool storage = false)
    {
        (int IndexId, bool Unique, int KeyCount)? best = null;
        foreach (var identity in table.IndexIdentities())
        {
            if (identity.IsHeap || identity.Index is { IsHypothetical: true } or { IsColumnstore: true })
                continue;
            var keys = identity.Constraint is { } constraint
                ? (storage ? constraint.StorageOrdinals : constraint.FullOrdinals)
                : storage ? identity.Index!.KeyStorageOrdinals : identity.Index!.KeyFullOrdinals;
            if (!LeadsWith(keys, ordinals))
                continue;
            var unique = identity.Constraint is not null || identity.Index!.IsUnique;
            var candidate = (identity.IndexId, unique, keys.Length);
            if (best is not { } current
                || (identity.IndexId == 1 && current.IndexId != 1)
                || (current.IndexId != 1 && keys.Length < current.KeyCount))
            {
                best = candidate;
            }
        }
        return best;
    }

    private static bool LeadsWith(int[] keys, int[] ordinals)
    {
        if (ordinals.Length > keys.Length)
            return false;
        for (var i = 0; i < ordinals.Length; i++)
        {
            if (keys[i] != ordinals[i])
                return false;
        }
        return true;
    }
}

/// <summary>
/// The indexes one statement's write to one table maintains, by the counter
/// each row moves, as <see cref="IndexUsageAccounting.NoteWrite"/> settles them.
/// </summary>
internal sealed class IndexWritePlan(HeapTable table, WriteKind kind, IReadOnlyList<int>? updatedOrdinals)
{
    public readonly HeapTable Table = table;
    public readonly WriteKind Kind = kind;
    public readonly IReadOnlyList<int>? UpdatedOrdinals = updatedOrdinals;
    public readonly List<IndexUsage> Inserts = [];
    public readonly List<IndexUsage> Ghosts = [];
    public readonly List<IndexUsage> Updates = [];
    public readonly List<IndexUsage> Deletes = [];

    private InsertGroup? inserted;

    private (int PageIndex, int SlotIndex)? firstInserted;

    /// <summary>
    /// Notes the row the statement inserted at <paramref name="address"/>,
    /// publishing the statement's range of rows to the table's insert ranges
    /// (<see cref="HeapTable.AddInsertGroup"/>) once it has inserted a second.
    /// </summary>
    public void NoteInsertedRow((int PageIndex, int SlotIndex) address, bool tuples)
    {
        if (this.inserted is { } group)
        {
            group.Last = address;
            group.Rows++;
            return;
        }
        if (this.firstInserted is not { } first)
        {
            this.firstInserted = address;
            return;
        }
        this.inserted = group = new InsertGroup(first, tuples) { Last = address, Rows = 2 };
        this.Table.AddInsertGroup(group);
    }
}

/// <summary>Which rows a write statement changed, as <see cref="IndexUsageAccounting.NoteWrite"/> charges them.</summary>
internal enum WriteKind : byte
{
    Insert,
    Delete,
    Update,
}
