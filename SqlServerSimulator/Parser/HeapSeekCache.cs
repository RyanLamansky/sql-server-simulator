using System.Runtime.CompilerServices;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Incrementally-maintained leading-prefix seek index for one <see cref="Heap"/>:
/// maps a promoted key tuple to the row addresses carrying it. Keyed by the
/// leading key-column ordinal; each entry remembers the full prefix (ordinals +
/// promoted types) it was built for. The first seek builds an entry from a full
/// scan and activates the heap's seek journal (<see cref="Heap.ActivateSeekJournal"/>);
/// thereafter, when the heap's <see cref="Heap.MutationGeneration"/> has moved, the
/// entry applies the journal delta (<see cref="Heap.SnapshotSeekJournalSince"/>)
/// rather than rebuilding — the "no warm-up" path. A full rebuild happens only when
/// the requested prefix differs, the journal can't cover the delta (a large bulk
/// mutation trimmed it, or a rollback / TRUNCATE invalidated it), or the heap was
/// never journaled.
/// <para>
/// Attached per-<see cref="Heap"/> through a <see cref="ConditionalWeakTable{TKey,TValue}"/>
/// keyed on the heap, so it costs nothing until first seeked and is collected with
/// the heap; <see cref="For"/> is the single accessor. Two consumers share it: the
/// query planner (<see cref="Selection"/>'s equality / range / ORDER BY / keyset
/// seeks) and constraint enforcement (<see cref="Simulation"/>'s foreign-key
/// existence + cascade lookups).
/// </para>
/// <para>
/// Buckets hold row addresses, not row bytes, so the cache costs a few words per row
/// rather than a copy of the table. A stale bucket membership is only ever a
/// false-positive — the maintenance never drops a live candidate (inserts and
/// update-new-keys recompute from live row bytes). The query path discards stale
/// entries via the residual WHERE it always keeps; the foreign-key path has no
/// residual filter, so <see cref="AnyRowMatches"/> / <see cref="MatchingRows"/>
/// re-verify each candidate against its live bytes.
/// </para>
/// </summary>
internal sealed class HeapSeekCache
{
    private static readonly ConditionalWeakTable<Heap, HeapSeekCache> caches = [];

    /// <summary>The seek cache attached to <paramref name="heap"/>, created on first use.</summary>
    public static HeapSeekCache For(Heap heap) => caches.GetValue(heap, static _ => new HeapSeekCache());

    private static readonly List<(int Page, int Slot)> Empty = [];

    private readonly Dictionary<int, CacheEntry> byLeadOrdinal = [];

    // Build / replay / read are serialized: the per-Heap cache is shared
    // across connections, so two readers can seek the same heap at once, and
    // a returned bucket is copied out under this lock by the caller's
    // AddRange before any concurrent mutation can patch it.
    private readonly Lock gate = new();

    public List<(int Page, int Slot)> Seek(
        Heap heap,
        HeapColumn[] schema,
        Heap? lobStore,
        int[] ordinals,
        SqlType[] commons,
        SqlValueKey probeKey)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons);
            return entry.Buckets.TryGetValue(probeKey, out var bucket) ? bucket : Empty;
        }
    }

    // Single-column range scan: resolves the [ordinal] entry (build / replay,
    // same as an equality seek on that one column), then unions the row
    // addresses whose key falls within the bounds. An absent lower/upper means
    // unbounded on that side. Returns a freshly-built list, so the caller can
    // enumerate it after the lock is released.
    public List<(int Page, int Slot)> RangeScan(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int ordinal, SqlType common,
        bool hasLower, SqlValue lower, bool lowerInclusive, bool hasUpper, SqlValue upper, bool upperInclusive)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, [ordinal], [common]);
            return entry.RangeCandidates(hasLower, lower, lowerInclusive, hasUpper, upper, upperInclusive);
        }
    }

    // Ordered scan for ORDER BY elimination over the composite prefix
    // <paramref name="ordinals"/>. The optional composite bound keys carve out
    // a contiguous slice of the ordered view: an equality-pinned prefix sets
    // lower == upper to the pinned tuple; a same-column range continues that
    // prefix with one more bounded component; a keyset cursor passes a
    // lexicographic lower (or, for a descending order, upper) tuple. Rows come
    // out in ascending key order, or — for an all-DESC order — reversed (the
    // descending caller passes its cursor as the upper bound, so reversing the
    // ascending in-range list yields the descending page). Reuses the same
    // ordered view the equality / range seeks build, inheriting the
    // incremental no-warm-up maintenance; within-key tie order is arbitrary
    // either way, matching ORDER BY's unspecified tie-break.
    public List<(int Page, int Slot)> OrderedSeek(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, bool descending,
        SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons);
            var ordered = entry.OrderedCandidates(lower, lowerInclusive, upper, upperInclusive);
            if (descending)
                ordered.Reverse();
            return ordered;
        }
    }

    /// <summary>
    /// True when some live row's <paramref name="ordinals"/> tuple equals
    /// <paramref name="probeKey"/>. The foreign-key parent-existence check: seek
    /// narrows the candidates, then each is verified against its live bytes so a
    /// stale bucket entry can't produce a phantom match.
    /// </summary>
    public bool AnyRowMatches(Heap heap, HeapColumn[] schema, int[] ordinals, SqlType[] commons, SqlValueKey probeKey)
    {
        foreach (var (_, _, _) in this.MatchingRows(heap, schema, ordinals, commons, probeKey))
            return true;
        return false;
    }

    /// <summary>
    /// The live rows whose <paramref name="ordinals"/> tuple equals
    /// <paramref name="probeKey"/>, each verified against its live bytes
    /// (tombstoned slots skipped, addresses de-duplicated). The seek narrows the
    /// candidate set; the verify makes it exact — the foreign-key child-lookup /
    /// cascade path, which has no residual WHERE to discard stale candidates.
    /// </summary>
    public IEnumerable<(int Page, int Slot, byte[] Bytes)> MatchingRows(
        Heap heap, HeapColumn[] schema, int[] ordinals, SqlType[] commons, SqlValueKey probeKey)
    {
        List<(int Page, int Slot)> candidates;
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, heap, ordinals, commons);
            candidates = entry.Buckets.TryGetValue(probeKey, out var bucket) ? [.. bucket] : [];
        }

        var seen = new HashSet<(int, int)>();
        foreach (var (page, slot) in candidates)
        {
            if (!seen.Add((page, slot)) || heap.IsSlotTombstoned(page, slot))
                continue;
            if (heap.ReadSlotBytes(page, slot) is { } bytes
                && TryComputeKey(bytes, ordinals, commons, schema, heap, out var liveKey)
                && liveKey.Equals(probeKey))
            {
                yield return (page, slot, bytes);
            }
        }
    }

    // Resolves the cache entry for a prefix: reuses it (replaying the journal
    // delta when the heap moved on), or rebuilds from a scan when the prefix /
    // promoted types differ or the delta can't be replayed. Caller holds the gate.
    private CacheEntry ResolveEntry(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons)
    {
        var lead = ordinals[0];
        if (this.byLeadOrdinal.TryGetValue(lead, out var entry) && entry.Matches(ordinals, commons))
        {
            if (entry.Generation != heap.MutationGeneration)
            {
                var events = heap.SnapshotSeekJournalSince(entry.Generation, out var currentGen);
                if (events is not null)
                {
                    IndexSeekDiagnostics.Sink?.Add("CacheReplay");
                    entry.Apply(events, schema, lobStore, currentGen);
                }
                else
                {
                    entry = this.Rebuild(heap, schema, lobStore, ordinals, commons, lead);
                }
            }

            return entry;
        }

        return this.Rebuild(heap, schema, lobStore, ordinals, commons, lead);
    }

    private CacheEntry Rebuild(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, int lead)
    {
        // Activate journaling and capture the build generation BEFORE scanning,
        // so any mutation that lands during the scan is journaled at a later
        // generation and replayed on the next seek — never silently missed.
        // (A write the scan happened to also see just replays as a harmless
        // re-add; the residual WHERE and the materializer's dedup absorb it.)
        IndexSeekDiagnostics.Sink?.Add("CacheBuild");
        var buildGen = heap.ActivateSeekJournal();
        var buckets = new Dictionary<SqlValueKey, List<(int Page, int Slot)>>();
        foreach (var (page, slot, bytes) in heap.EnumerateRowsWithAddress())
        {
            if (TryComputeKey(bytes, ordinals, commons, schema, lobStore, out var key))
                AddRid(buckets, key, (page, slot));
        }

        var entry = new CacheEntry(buildGen, (int[])ordinals.Clone(), (SqlType[])commons.Clone(), buckets);
        this.byLeadOrdinal[lead] = entry;
        return entry;
    }

    // Decodes this entry's key tuple from a row image, coercing each component
    // to the entry's promoted type. Returns false when any component is NULL —
    // a NULL key can never equal a (non-NULL by construction) probe, so the
    // row joins no bucket.
    private static bool TryComputeKey(
        ReadOnlySpan<byte> image, int[] ordinals, SqlType[] commons, HeapColumn[] schema, Heap? lobStore, out SqlValueKey key)
    {
        var components = new SqlValue[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
        {
            var value = RowDecoder.DecodeColumn(schema, image, ordinals[i], lobStore);
            if (value.IsNull)
            {
                key = default;
                return false;
            }

            components[i] = value.CoerceTo(commons[i]);
        }

        key = new SqlValueKey(components);
        return true;
    }

    private static void AddRid(Dictionary<SqlValueKey, List<(int Page, int Slot)>> buckets, SqlValueKey key, (int Page, int Slot) rid)
    {
        if (!buckets.TryGetValue(key, out var bucket))
            buckets[key] = bucket = [];
        bucket.Add(rid);
    }

    // Orders key tuples component-by-component, comparing only as far as the
    // shorter of the two (so a shorter prefix-probe key sorts equal to every
    // full key sharing that prefix — the basis of the equality-prefix ordered
    // seek's GetViewBetween(prefix, prefix)). Within one entry every key has
    // the same arity and every component is coerced to the entry's promoted
    // type, so over the set's own elements this is a total order — exactly
    // what SortedSet needs; the ragged-arity case only ever arises for the
    // synthetic bound keys passed to GetViewBetween, never set members.
    private sealed class KeyTupleComparer : IComparer<SqlValueKey>
    {
        public static readonly KeyTupleComparer Instance = new();

        public int Compare(SqlValueKey x, SqlValueKey y)
        {
            var n = Math.Min(x.ComponentCount, y.ComponentCount);
            for (var i = 0; i < n; i++)
            {
                var c = x.ComponentAt(i).CompareTo(y.ComponentAt(i));
                if (c != 0)
                    return c;
            }

            return 0;
        }
    }

    private sealed class CacheEntry(
        long generation, int[] ordinals, SqlType[] commons, Dictionary<SqlValueKey, List<(int Page, int Slot)>> buckets)
    {
        public long Generation = generation;
        public readonly Dictionary<SqlValueKey, List<(int Page, int Slot)>> Buckets = buckets;
        private readonly int[] ordinals = ordinals;
        private readonly SqlType[] commons = commons;

        // Ordered view of the bucket keys, built lazily on the first range scan
        // and then maintained in lockstep with Buckets (a key joins / leaves it
        // exactly when its bucket appears / empties). Null until a range scan
        // needs it, so equality-only workloads never pay for it.
        private SortedSet<SqlValueKey>? sortedKeys;

        // Reuse is sound only when the cached prefix is the same column
        // sequence promoted to the same types as the incoming probe.
        public bool Matches(int[] requestedOrdinals, SqlType[] requestedCommons)
        {
            if (this.ordinals.Length != requestedOrdinals.Length)
                return false;
            for (var i = 0; i < this.ordinals.Length; i++)
            {
                if (this.ordinals[i] != requestedOrdinals[i] || !ReferenceEquals(this.commons[i], requestedCommons[i]))
                    return false;
            }

            return true;
        }

        // Applies the journal delta to this entry's buckets. Insert adds the
        // new key's address; Delete removes the old key's; Update does both.
        // A key recomputed from a superseded (Delete / Update-old) image whose
        // off-row chain was already reclaimed may be wrong, which can only
        // leave a stale address in a bucket — a false-positive the residual
        // WHERE and the materializer's tombstone-skip discard. The add side
        // (Insert / Update-new) decodes a live image, so it never goes wrong.
        public void Apply(Heap.SeekJournalEvent[] events, HeapColumn[] schema, Heap? lobStore, long currentGen)
        {
            foreach (var e in events)
            {
                switch (e.Kind)
                {
                    case Heap.SeekJournalKind.Insert:
                        if (TryComputeKey(e.NewImage, this.ordinals, this.commons, schema, lobStore, out var insertKey))
                            this.AddRid(insertKey, (e.Page, e.Slot));
                        break;
                    case Heap.SeekJournalKind.Delete:
                        if (TryComputeKey(e.OldImage, this.ordinals, this.commons, schema, lobStore, out var deleteKey))
                            this.RemoveRid(deleteKey, (e.Page, e.Slot));
                        break;
                    case Heap.SeekJournalKind.Update:
                        if (TryComputeKey(e.OldImage, this.ordinals, this.commons, schema, lobStore, out var oldKey))
                            this.RemoveRid(oldKey, (e.Page, e.Slot));
                        if (TryComputeKey(e.NewImage, this.ordinals, this.commons, schema, lobStore, out var newKey))
                            this.AddRid(newKey, (e.Page, e.Slot));
                        break;
                }
            }

            this.Generation = currentGen;
        }

        // Instance add that keeps the lazily-built sorted view in sync — a key
        // joins sortedKeys exactly when its bucket is first created.
        private void AddRid(SqlValueKey key, (int Page, int Slot) rid)
        {
            if (!this.Buckets.TryGetValue(key, out var bucket))
            {
                this.Buckets[key] = bucket = [];
                _ = this.sortedKeys?.Add(key);
            }

            bucket.Add(rid);
        }

        private void RemoveRid(SqlValueKey key, (int Page, int Slot) rid)
        {
            if (this.Buckets.TryGetValue(key, out var bucket))
            {
                _ = bucket.Remove(rid);
                if (bucket.Count == 0)
                {
                    _ = this.Buckets.Remove(key);
                    _ = this.sortedKeys?.Remove(key);
                }
            }
        }

        private SortedSet<SqlValueKey> EnsureSorted()
        {
            if (this.sortedKeys is null)
            {
                this.sortedKeys = new SortedSet<SqlValueKey>(KeyTupleComparer.Instance);
                foreach (var key in this.Buckets.Keys)
                    _ = this.sortedKeys.Add(key);
            }

            return this.sortedKeys;
        }

        // Single-column range seek: the in-range keys of a one-column entry,
        // in ascending order. Thin wrapper that builds arity-1 composite bounds.
        public List<(int Page, int Slot)> RangeCandidates(
            bool hasLower, SqlValue lower, bool lowerInclusive, bool hasUpper, SqlValue upper, bool upperInclusive) =>
            this.OrderedCandidates(
                hasLower ? new SqlValueKey([lower]) : null, lowerInclusive,
                hasUpper ? new SqlValueKey([upper]) : null, upperInclusive);

        // Unions, in ascending key order, the row addresses whose key lies
        // within the optional composite bounds. Each bound is a (possibly
        // shorter-than-the-key) tuple compared under the ragged-arity comparer,
        // so it constrains a leading run of components and an exclusive bound
        // drops every key sharing that leading run. This one shape serves them
        // all: a null/null pair is the whole entry in order (pure multi-column
        // ORDER BY); lower == upper == a pinned tuple is the contiguous equality
        // run ordered by the trailing key columns (WHERE a = @x ORDER BY b); a
        // pinned tuple extended by one bounded component is a same-column range
        // (WHERE a = @x AND b > 5 ORDER BY b); a single exclusive lexicographic
        // bound is a keyset cursor (WHERE a > @x OR (a = @x AND b > @y)).
        // SortedSet.GetViewBetween gives the in-range keys in O(log n + matches).
        public List<(int Page, int Slot)> OrderedCandidates(
            SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
        {
            var sorted = this.EnsureSorted();
            var result = new List<(int Page, int Slot)>();
            if (sorted.Count == 0)
                return result;

            var lowerKey = lower ?? sorted.Min;
            var upperKey = upper ?? sorted.Max;
            if (KeyTupleComparer.Instance.Compare(lowerKey, upperKey) > 0)
                return result;

            foreach (var key in sorted.GetViewBetween(lowerKey, upperKey))
            {
                if (lower is { } lk && !lowerInclusive && KeyTupleComparer.Instance.Compare(key, lk) == 0)
                    continue;
                if (upper is { } uk && !upperInclusive && KeyTupleComparer.Instance.Compare(key, uk) == 0)
                    continue;
                if (this.Buckets.TryGetValue(key, out var bucket))
                    result.AddRange(bucket);
            }

            return result;
        }
    }
}
