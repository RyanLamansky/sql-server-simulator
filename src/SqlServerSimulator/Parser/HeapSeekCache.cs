using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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
/// mutation trimmed it, or a TRUNCATE invalidated it), or the heap was never
/// journaled. A rolled-back write journals its reversal, so it replays too.
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

    private readonly Dictionary<int, CacheEntry> byLeadOrdinal = [];

    // Build / replay / read are serialized: the per-Heap cache is shared
    // across connections, so two readers can seek the same heap at once. What
    // a read hands back outlives the lock — see Seek for why that's sound.
    private readonly Lock gate = new();

    // Returns a span over the entry's live bucket, not a copy — hence the
    // read-only type, which the range / ordered seeks below deliberately don't
    // share (theirs build a fresh list the caller owns and may reorder). The
    // span also snapshots pointer and length together, so a concurrent Add
    // that grows the bucket leaves the reader on the intact old array rather
    // than racing a List's separate _items / _size reads. `widenTo` names the
    // full key of the index the seek reads, whose order the candidates follow
    // (see ResolveEntry).
    public ReadOnlySpan<(int Page, int Slot)> Seek(
        Heap heap,
        HeapColumn[] schema,
        Heap? lobStore,
        int[] ordinals,
        SqlType[] commons,
        SqlValueKey probeKey,
        int[]? widenTo = null)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, widenTo: widenTo);
            return entry.EqualityCandidates(probeKey);
        }
    }

    // Single-column range scan: resolves the [ordinal] entry (build / replay,
    // same as an equality seek on that one column), then unions the row
    // addresses whose key falls within the bounds. An absent lower/upper means
    // unbounded on that side. Returns a freshly-built list, so the caller can
    // enumerate it after the lock is released — or null once the in-range rid
    // count passes <paramref name="candidateCap"/>, which is the caller's
    // "this range is too wide to be worth seeking" signal and stops the walk
    // there rather than building a list it will throw away.
    public List<(int Page, int Slot)>? RangeScan(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int ordinal, SqlType common,
        bool hasLower, SqlValue lower, bool lowerInclusive, bool hasUpper, SqlValue upper, bool upperInclusive,
        int candidateCap, int[]? widenTo = null)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, [ordinal], [common], widenTo: widenTo);
            return entry.RangeCandidates(hasLower, lower, lowerInclusive, hasUpper, upper, upperInclusive, candidateCap);
        }
    }

    // Below this many rids in the equality-prefix group, a range continuation
    // returns the whole group (the residual WHERE filters it) instead of
    // slicing the ordered view: enumerating a SortedSet view pays per-node
    // comparer calls, which for string keys costs about as much as the
    // residual's per-row filter — measured ~1.3× SLOWER than the plain group
    // seek on a 211-rid nvarchar group with a 144-key slice. The ordered slice
    // wins when the group dwarfs that per-key overhead (a 5 000-rid group with
    // a small date slice measured ~5.6× faster), so small groups skip it.
    private const int RangeSliceMinGroupRids = 256;

    // Equality-prefix + range-continuation seek: the group of rows matching
    // the (shorter-than-entry) prefixKey, narrowed to the composite ordered
    // slice between the bounds when the group is large enough for the slice
    // to pay (see RangeSliceMinGroupRids). Both shapes over-approximate the
    // true match set at worst (the caller's residual WHERE filters), so the
    // threshold is pure cost policy, never correctness.
    public List<(int Page, int Slot)> PrefixRangeSeek(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons,
        SqlValueKey prefixKey, SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive, int[]? widenTo = null)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, widenTo: widenTo);
            var group = entry.EqualityCandidates(prefixKey);
            if (group.Length <= RangeSliceMinGroupRids)
            {
                IndexSeekDiagnostics.Sink?.Add("PrefixRangeGroup");
                return [.. group];
            }

            IndexSeekDiagnostics.Sink?.Add("PrefixRangeSlice");
            return entry.OrderedCandidates(lower, lowerInclusive, upper, upperInclusive);
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
    // either way, matching ORDER BY's unspecified tie-break. The addresses
    // always come back ascending — a descending caller reads them from the
    // end — and an unbounded walk is the entry's memoized key order, shared
    // with every reader until the next write, so the caller must not change
    // it.
    public (int Page, int Slot)[] OrderedSeek(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons,
        SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons);
            return lower is null && upper is null
                ? entry.KeyOrder()
                : [.. entry.OrderedCandidates(lower, lowerInclusive, upper, upperInclusive)];
        }
    }

    /// <summary>
    /// The live row addresses in ascending key order for a scan that has to
    /// follow the key (see <c>ClusteredScan</c>), or <see langword="null"/>
    /// when ascending heap addresses already read the rows in that order — the
    /// common case, a key assigned in insertion order, where the caller's
    /// plain heap walk is the cheaper way to the same sequence. Declines too
    /// (null) when the entry serving the key has widened past it onto a
    /// nullable column, whose NULL-keyed rows no bucket holds.
    /// </summary>
    /// <remarks>
    /// <paramref name="keys"/>, when given, receives each address's key at the
    /// same index: a scan reaching an address deleted since the order was taken
    /// finds by it the row a reinsert of the key put elsewhere. With
    /// <paramref name="keyed"/> the order comes back even when the heap's own
    /// order is the key's, for a scan that needs those keys.
    /// </remarks>
    public List<(int Page, int Slot)>? KeyOrderUnlessHeapOrdered(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, List<SqlValueKey>? keys = null, bool keyed = false)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, traced: false);
            if (entry.HeapOrdered && !keyed)
                return null;
            foreach (var ordinal in entry.Ordinals)
            {
                if (schema[ordinal].Nullable)
                    return null;
            }
            var ordered = entry.CappedCandidates(null, false, null, false, int.MaxValue, keys)!;
            return entry.NoteKeyOrderWalk(ordered) && !keyed ? null : ordered;
        }
    }

    /// <summary>
    /// Whether ascending heap addresses read the rows in ascending order of the
    /// key <paramref name="ordinals"/>, erring toward false as the entry's
    /// tracking does (a restored or reused address clears it until an ordered
    /// walk finds the heap in order again).
    /// </summary>
    public bool HeapInKeyOrder(Heap heap, HeapColumn[] schema, int[] ordinals, SqlType[] commons)
    {
        lock (this.gate)
            return this.ResolveEntry(heap, schema, heap, ordinals, commons, traced: false).HeapOrdered;
    }

    /// <summary>
    /// The keys a SERIALIZABLE read of the interval between the (possibly
    /// shorter-than-the-key) bounds locks, in ascending order with each key's
    /// row addresses: every key inside it, then the first key past its upper
    /// bound — real's next-key lock — with a null key standing for the
    /// infinity position when no key follows. Returns null once more than
    /// <paramref name="cap"/> keys are in hand, the caller's signal that the
    /// read would escalate. A null bound leaves that side open.
    /// </summary>
    public List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>? KeyLockAnchors(
        Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons,
        SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive, int cap)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, traced: false);
            return entry.Anchors(ordinals.Length, lower, lowerInclusive, upper, upperInclusive, cap);
        }
    }

    /// <summary>
    /// The first key strictly above <paramref name="probe"/>, or null when none
    /// is — the anchor whose range an insert of <paramref name="probe"/> lands
    /// in, real's next-key test.
    /// </summary>
    public SqlValueKey? NextKeyAbove(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, SqlValueKey probe)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, traced: false);
            return entry.NextAbove(ordinals.Length, probe, inclusive: true);
        }
    }

    /// <summary>
    /// The last key below <paramref name="bound"/> — below every key sharing
    /// its components when <paramref name="inclusive"/> says the bound itself
    /// was inside the read, at or below it otherwise — or null when none is:
    /// where a descending read of an interval stops.
    /// </summary>
    public SqlValueKey? PreviousKeyBelow(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, SqlValueKey bound, bool inclusive)
    {
        lock (this.gate)
        {
            var entry = this.ResolveEntry(heap, schema, lobStore, ordinals, commons, traced: false);
            return entry.PreviousBelow(ordinals.Length, bound, inclusive);
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
            candidates = [.. entry.EqualityCandidates(probeKey)];
        }

        // A uniqueness probe of a fresh key — every row an insert checks —
        // finds no candidate, and a unique key one: only two can repeat.
        var seen = candidates.Count > 1 ? new HashSet<(int, int)>() : null;
        foreach (var (page, slot) in candidates)
        {
            if (seen is not null && !seen.Add((page, slot)))
                continue;
            if (heap.ReadLiveRow(page, slot) is { } bytes
                && TryComputeKey(bytes, ordinals, commons, schema, heap, out var liveKey)
                && liveKey.Equals(probeKey))
            {
                yield return (page, slot, bytes);
            }
        }
    }

    // Resolves the cache entry for a prefix: reuses it when its prefix covers the
    // request (replaying the journal delta when the heap moved on), or rebuilds
    // from a scan when the request isn't covered or the delta can't be replayed.
    // A journal-fail rebuild keeps the entry's own (possibly wider) prefix, so a
    // widened entry never narrows back and starts thrashing. Caller holds the gate.
    // `traced` is false for the clustered-scan order check, which reads the
    // cache without being a seek, so the seek diagnostics stay the seeks'.
    //
    // `widenTo`, a key the requested ordinals lead, asks for an entry that wide:
    // its buckets then list a shorter probe's rows by the rest of that key, the
    // order the index it names reads them in, where a narrower entry lists them
    // by address. An entry already reaching past the request along some other
    // key is left as it is, so two indexes sharing a leading column never take
    // turns rebuilding it; widening along the key's own path only grows it, so
    // whatever the narrower entry served the wider one serves too.
    [MethodImpl(Tiering.OptimizeFirstCall)]
    private CacheEntry ResolveEntry(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, bool traced = true, int[]? widenTo = null)
    {
        var lead = ordinals[0];
        if (widenTo is not null && widenTo.Length > ordinals.Length && LeadsKey(ordinals, widenTo))
        {
            var existing = this.byLeadOrdinal.TryGetValue(lead, out var current) && current.Covers(ordinals, commons) ? current : null;
            if (existing is null || (existing.Ordinals.Length < widenTo.Length && LeadsKey(existing.Ordinals, widenTo)))
            {
                var basis = existing?.Commons ?? commons;
                var widened = new SqlType[widenTo.Length];
                basis.CopyTo(widened, 0);
                for (var i = basis.Length; i < widened.Length; i++)
                    widened[i] = schema[widenTo[i]].Type;
                (ordinals, commons) = (widenTo, widened);
            }
        }

        if (this.byLeadOrdinal.TryGetValue(lead, out var entry) && entry.Covers(ordinals, commons))
        {
            if (entry.Generation != heap.MutationGeneration)
            {
                var events = heap.SnapshotSeekJournalSince(entry.Generation, out var currentGen);
                if (events is not null)
                {
                    if (traced)
                        IndexSeekDiagnostics.Sink?.Add("CacheReplay");
                    entry.Apply(events, schema, lobStore, currentGen);
                }
                else
                {
                    entry = this.Rebuild(heap, schema, lobStore, entry.Ordinals, entry.Commons, lead, traced);
                }
            }

            return entry;
        }

        return this.Rebuild(heap, schema, lobStore, ordinals, commons, lead, traced);
    }

    private static bool LeadsKey(int[] ordinals, int[] key) =>
        ordinals.Length <= key.Length && key.AsSpan(0, ordinals.Length).SequenceEqual(ordinals);

    [MethodImpl(Tiering.OptimizeFirstCall)]
    private CacheEntry Rebuild(Heap heap, HeapColumn[] schema, Heap? lobStore, int[] ordinals, SqlType[] commons, int lead, bool traced)
    {
        // Activate journaling and capture the build generation BEFORE scanning,
        // so any mutation that lands during the scan is journaled at a later
        // generation and replayed on the next seek — never silently missed.
        // (A write the scan happened to also see just replays as a harmless
        // re-add; the residual WHERE and the materializer's dedup absorb it.)
        if (traced)
            IndexSeekDiagnostics.Sink?.Add("CacheBuild");
        var buildGen = heap.ActivateSeekJournal();
        var buckets = new Dictionary<SqlValueKey, List<(int Page, int Slot)>>();
        var heapOrdered = true;
        SqlValueKey? maxKey = null;
        (int Page, int Slot) lastRid = (-1, -1);
        foreach (var (page, slot, bytes) in heap.EnumerateRowsWithAddress())
        {
            if (TryComputeEntryKey(bytes, ordinals, commons, schema, lobStore, out var key))
            {
                AddRid(buckets, key, (page, slot));
                if (maxKey is { } max && KeyTupleComparer.Instance.Compare(key, max) < 0)
                    heapOrdered = false;
                else
                    maxKey = key;
            }
            else
            {
                heapOrdered = false;
            }
            lastRid = (page, slot);
        }

        var entry = new CacheEntry(buildGen, (int[])ordinals.Clone(), (SqlType[])commons.Clone(), buckets);
        entry.StartHeapOrder(heapOrdered, lastRid, maxKey);
        this.byLeadOrdinal[lead] = entry;
        return entry;
    }

    // Decodes a key tuple from a row image, coercing each component to the
    // entry's promoted type. Returns false when any component is NULL — a NULL
    // key can never equal a (non-NULL by construction) probe.
    internal static bool TryComputeKey(
        ReadOnlySpan<byte> image, int[] ordinals, SqlType[] commons, HeapColumn[] schema, Heap? lobStore, out SqlValueKey key) =>
        TryComputeKey(image, ordinals, commons, schema, lobStore, nullAfterLead: false, out key);

    // The key a row files under in an entry's buckets: NULL only in the lead
    // column keeps it out. A later NULL component still files the row, since a
    // shorter probe — `WHERE r = 1` against an entry widened to (r, d) by a
    // uniqueness check — matches on the prefix alone and must reach the rows
    // whose d is NULL; a full-arity probe never contains NULL, so such a key
    // never equals one.
    private static bool TryComputeEntryKey(
        ReadOnlySpan<byte> image, int[] ordinals, SqlType[] commons, HeapColumn[] schema, Heap? lobStore, out SqlValueKey key) =>
        TryComputeKey(image, ordinals, commons, schema, lobStore, nullAfterLead: true, out key);

    private static bool TryComputeKey(
        ReadOnlySpan<byte> image, int[] ordinals, SqlType[] commons, HeapColumn[] schema, Heap? lobStore, bool nullAfterLead, out SqlValueKey key)
    {
        var components = new SqlValue[ordinals.Length];
        for (var i = 0; i < ordinals.Length; i++)
        {
            var value = RowDecoder.DecodeColumn(schema, image, ordinals[i], lobStore);
            if (value.IsNull)
            {
                if (i == 0 || !nullAfterLead)
                {
                    key = default;
                    return false;
                }

                components[i] = SqlValue.Null(commons[i]);
                continue;
            }

            components[i] = value.CoerceTo(commons[i]);
        }

        key = new SqlValueKey(components);
        return true;
    }

    [MethodImpl(Tiering.OptimizeFirstCall)]
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
    /// <summary>Orders two key tuples as the ordered view orders them.</summary>
    internal static int CompareKeys(SqlValueKey x, SqlValueKey y) => KeyTupleComparer.Instance.Compare(x, y);

    private sealed class KeyTupleComparer : IComparer<SqlValueKey>
    {
        public static readonly KeyTupleComparer Instance = new();

        public int Compare(SqlValueKey x, SqlValueKey y)
        {
            var n = Math.Min(x.ComponentCount, y.ComponentCount);
            for (var i = 0; i < n; i++)
            {
                // A NULL component (never the lead, see TryComputeEntryKey)
                // sorts first, as an index orders it.
                var a = x.ComponentAt(i);
                var b = y.ComponentAt(i);
                if (a.IsNull || b.IsNull)
                {
                    if (a.IsNull != b.IsNull)
                        return a.IsNull ? -1 : 1;
                    continue;
                }
                var c = a.CompareTo(b);
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
        public readonly int[] Ordinals = ordinals;
        public readonly SqlType[] Commons = commons;

        // Ordered view of the bucket keys, built lazily on the first range scan
        // and then maintained in lockstep with Buckets (a key joins / leaves it
        // exactly when its bucket appears / empties). Null until a range scan
        // needs it, so equality-only workloads never pay for it.
        private SortedSet<SqlValueKey>? sortedKeys;

        // Every address in ascending key order, as the unbounded ordered walk
        // lists them, kept until a write changes a bucket (AddRid / RemoveRid
        // drop it). A paged ordered read — the same ORDER BY over an unchanged
        // table, page after page — then indexes straight to its OFFSET instead
        // of walking the ordered view to build the list again.
        private (int Page, int Slot)[]? keyOrder;

        public (int Page, int Slot)[] KeyOrder() =>
            this.keyOrder ??= [.. this.OrderedCandidates(null, false, null, false)];

        // Hash views for shorter-arity equality probes against this (widened)
        // entry, keyed by probe arity, each mapping a leading-prefix key to the
        // full keys under it. Built lazily on the first probe of an arity and
        // then maintained in lockstep with Buckets by AddRid / RemoveRid.
        // Restores the O(1) hash hit a narrow probe had before the entry
        // widened (walking the ordered view instead measured ~2× on a 500-row
        // group lookup). Null until a narrow probe occurs, so exact-arity
        // workloads never pay for it.
        private Dictionary<int, Dictionary<SqlValueKey, NarrowGroup>>? narrowViews;

        // One leading prefix's share of a widened entry: its full keys in
        // ascending order, and their addresses in the order an index seek on
        // the prefix reads them — by the rest of the key, then by address as
        // each bucket lists them. The address list is a cache, dropped by a
        // write that lands anywhere but its end and rebuilt by the next probe,
        // so a reader still holding the old list keeps an intact one.
        private sealed class NarrowGroup
        {
            public readonly List<SqlValueKey> Keys = [];
            public List<(int Page, int Slot)>? Rids;
        }

        // Reuse is sound when the cached prefix COVERS the request: the request's
        // column sequence and promoted types are a leading prefix of the entry's.
        // A shorter-arity probe is then served from the ordered view (every key
        // sharing the probe's leading components sorts equal to it under the
        // ragged-arity comparer), so an entry widened by an equality+range or
        // multi-column seek keeps serving the narrower seeks that built it —
        // alternating `a = @x` / `a = @x AND b > @y` shapes reuse one entry
        // instead of rebuilding per query.
        public bool Covers(int[] requestedOrdinals, SqlType[] requestedCommons)
        {
            if (this.Ordinals.Length < requestedOrdinals.Length)
                return false;
            for (var i = 0; i < requestedOrdinals.Length; i++)
            {
                if (this.Ordinals[i] != requestedOrdinals[i] || !ReferenceEquals(this.Commons[i], requestedCommons[i]))
                    return false;
            }

            return true;
        }

        // Equality candidates for a probe of this entry's full arity (one hash
        // bucket) or a shorter leading prefix (one group of the lazily-built
        // narrow view for that arity) — both O(1) per probe. Either comes back
        // in the order an index seek reads equal keys, by row locator, which
        // is address order here (a bucket keeps its addresses ascending).
        public ReadOnlySpan<(int Page, int Slot)> EqualityCandidates(SqlValueKey probeKey)
        {
            if (probeKey.ComponentCount == this.Ordinals.Length)
                return this.Buckets.TryGetValue(probeKey, out var bucket) ? CollectionsMarshal.AsSpan(bucket) : [];
            return this.EnsureNarrowView(probeKey.ComponentCount).TryGetValue(probeKey, out var group)
                ? CollectionsMarshal.AsSpan(this.GroupRids(group))
                : [];
        }

        private Dictionary<SqlValueKey, NarrowGroup> EnsureNarrowView(int arity)
        {
            this.narrowViews ??= [];
            if (!this.narrowViews.TryGetValue(arity, out var view))
            {
                this.narrowViews[arity] = view = [];
                foreach (var (key, _) in this.Buckets)
                {
                    var prefix = key.Prefix(arity);
                    if (!view.TryGetValue(prefix, out var group))
                        view[prefix] = group = new();
                    group.Keys.Add(key);
                }
                foreach (var (_, group) in view)
                {
                    if (group.Keys.Count > 1)
                        group.Keys.Sort(KeyTupleComparer.Instance);
                }
            }

            return view;
        }

        private List<(int Page, int Slot)> GroupRids(NarrowGroup group)
        {
            if (group.Rids is { } rids)
                return rids;
            rids = [];
            foreach (var key in group.Keys)
            {
                if (this.Buckets.TryGetValue(key, out var bucket))
                    rids.AddRange(bucket);
            }
            return group.Rids = rids;
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
                        if (TryComputeEntryKey(e.NewImage, this.Ordinals, this.Commons, schema, lobStore, out var insertKey))
                        {
                            this.AddRid(insertKey, (e.Page, e.Slot));
                            this.NoteInsert(insertKey, (e.Page, e.Slot));
                        }
                        else
                        {
                            this.HeapOrdered = false;
                        }
                        break;
                    case Heap.SeekJournalKind.Delete:
                        if (TryComputeEntryKey(e.OldImage, this.Ordinals, this.Commons, schema, lobStore, out var deleteKey))
                            this.RemoveRid(deleteKey, (e.Page, e.Slot));
                        break;
                    case Heap.SeekJournalKind.Update:
                        var hadOldKey = TryComputeEntryKey(e.OldImage, this.Ordinals, this.Commons, schema, lobStore, out var oldKey);
                        if (hadOldKey)
                            this.RemoveRid(oldKey, (e.Page, e.Slot));
                        var hasNewKey = TryComputeEntryKey(e.NewImage, this.Ordinals, this.Commons, schema, lobStore, out var newKey);
                        if (hasNewKey)
                            this.AddRid(newKey, (e.Page, e.Slot));
                        // A row keeps its address across an UPDATE, so only a
                        // changed key can put it out of order.
                        if (!hadOldKey || !hasNewKey || KeyTupleComparer.Instance.Compare(oldKey, newKey) != 0)
                            this.HeapOrdered = false;
                        break;
                }
            }

            this.Generation = currentGen;
        }

        // Whether ascending heap addresses read this entry's rows in ascending
        // key order (ties aside), with the last live address and the highest
        // key it has seen — the two bounds a new row has to clear to keep it
        // so. A delete leaves both where they were, which can only make a
        // later insert look out of order when it isn't: the flag errs toward
        // false, costing the scan its fast path but never its order.
        // NoteKeyOrderWalk restores it once an ordered walk finds the heap in
        // order again.
        public bool HeapOrdered;
        private (int Page, int Slot) lastRid;
        private SqlValueKey? maxKey;

        public void StartHeapOrder(bool heapOrdered, (int Page, int Slot) lastRid, SqlValueKey? maxKey)
        {
            this.HeapOrdered = heapOrdered;
            this.lastRid = lastRid;
            this.maxKey = maxKey;
        }

        private void NoteInsert(SqlValueKey key, (int Page, int Slot) rid)
        {
            if (!this.HeapOrdered)
                return;
            if (rid.CompareTo(this.lastRid) <= 0 || (this.maxKey is { } max && KeyTupleComparer.Instance.Compare(key, max) < 0))
            {
                this.HeapOrdered = false;
                return;
            }
            this.lastRid = rid;
            this.maxKey = key;
        }

        // Called with a full ascending-key walk: when its addresses ascend too,
        // the heap is back in key order and the flag comes back on.
        public bool NoteKeyOrderWalk(List<(int Page, int Slot)> ordered)
        {
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].CompareTo(ordered[i - 1]) <= 0)
                    return false;
            }
            this.StartHeapOrder(true, ordered.Count > 0 ? ordered[^1] : (-1, -1), this.sortedKeys is { Count: > 0 } sorted ? sorted.Max : null);
            return true;
        }

        // Instance add that keeps the lazily-built sorted and narrow views in
        // sync — a key joins sortedKeys exactly when its bucket is first created.
        // A bucket keeps its addresses ascending, the row-locator order an index
        // reads equal keys in (probed 2026-10-10 against SQL Server 2025): an
        // address below the bucket's last — a rolled-back delete's row back at
        // its address, a row an UPDATE moves to this key — goes in at its
        // place rather than at the end, in a fresh list so a reader still
        // enumerating the old one keeps an intact one. An address already
        // listed (a write the building scan saw, replayed) is left alone.
        private void AddRid(SqlValueKey key, (int Page, int Slot) rid)
        {
            this.keyOrder = null;
            var added = false;
            if (!this.Buckets.TryGetValue(key, out var bucket))
            {
                this.Buckets[key] = bucket = [];
                _ = this.sortedKeys?.Add(key);
                added = true;
            }

            var count = bucket.Count;
            if (count == 0 || bucket[count - 1].CompareTo(rid) < 0)
            {
                bucket.Add(rid);
            }
            else
            {
                var at = bucket.BinarySearch(rid);
                if (at >= 0)
                    return;
                var listed = CollectionsMarshal.AsSpan(bucket);
                var placed = new List<(int Page, int Slot)>(count + 1);
                placed.AddRange(listed[..~at]);
                placed.Add(rid);
                placed.AddRange(listed[~at..]);
                this.Buckets[key] = placed;
            }

            if (this.narrowViews is not { } views)
                return;
            foreach (var (arity, view) in views)
            {
                var prefix = key.Prefix(arity);
                if (!view.TryGetValue(prefix, out var group))
                    view[prefix] = group = new();
                var last = group.Keys.Count == 0 || KeyTupleComparer.Instance.Compare(group.Keys[^1], key) <= 0;
                if (added)
                {
                    if (last)
                        group.Keys.Add(key);
                    else
                        group.Keys.Insert(~group.Keys.BinarySearch(key, KeyTupleComparer.Instance), key);
                }
                // The group's addresses stay in order on an append to its last
                // key, or of a new last key; anything else rebuilds them on the
                // next probe.
                if (group.Rids is { } rids && (!last || (!added && rids.Count != 0 && rids[^1].CompareTo(rid) > 0)))
                    group.Rids = null;
                else
                    group.Rids?.Add(rid);
            }
        }

        private void RemoveRid(SqlValueKey key, (int Page, int Slot) rid)
        {
            this.keyOrder = null;
            if (this.Buckets.TryGetValue(key, out var bucket))
            {
                var at = bucket.BinarySearch(rid);
                if (at >= 0)
                    bucket.RemoveAt(at);
                var emptied = bucket.Count == 0;
                if (emptied)
                {
                    _ = this.Buckets.Remove(key);
                    _ = this.sortedKeys?.Remove(key);
                }

                if (this.narrowViews is { } views)
                {
                    foreach (var (arity, view) in views)
                    {
                        var prefix = key.Prefix(arity);
                        if (!view.TryGetValue(prefix, out var group))
                            continue;
                        if (emptied)
                        {
                            var keyAt = group.Keys.BinarySearch(key, KeyTupleComparer.Instance);
                            if (keyAt >= 0)
                                group.Keys.RemoveAt(keyAt);
                            if (group.Keys.Count == 0)
                            {
                                _ = view.Remove(prefix);
                                continue;
                            }
                        }
                        _ = group.Rids?.Remove(rid);
                    }
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

        // See HeapSeekCache.KeyLockAnchors. An entry widened past the requested
        // arity holds longer tuples; each is cut back to the anchor's width,
        // and the ascending walk puts the pieces of one anchor side by side, so
        // they merge as they come.
        public List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>? Anchors(
            int arity, SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive, int cap)
        {
            var sorted = this.EnsureSorted();
            var result = new List<(SqlValueKey? Key, (int Page, int Slot)[] Rids)>();
            if (sorted.Count != 0)
            {
                var lowerKey = lower ?? sorted.Min;
                var upperKey = upper ?? sorted.Max;
                if (KeyTupleComparer.Instance.Compare(lowerKey, upperKey) <= 0)
                {
                    SqlValueKey? pending = null;
                    List<(int Page, int Slot)> pendingRids = [];
                    foreach (var key in sorted.GetViewBetween(lowerKey, upperKey))
                    {
                        if (lower is { } lk && !lowerInclusive && KeyTupleComparer.Instance.Compare(key, lk) == 0)
                            continue;
                        if (upper is { } uk && !upperInclusive && KeyTupleComparer.Instance.Compare(key, uk) == 0)
                            continue;
                        var anchor = key.ComponentCount > arity ? key.Prefix(arity) : key;
                        if (pending is not { } open || !open.Equals(anchor))
                        {
                            if (pending is { } done)
                                result.Add((done, [.. pendingRids]));
                            if (result.Count >= cap)
                                return null;
                            pending = anchor;
                            pendingRids.Clear();
                        }
                        if (this.Buckets.TryGetValue(key, out var bucket))
                            pendingRids.AddRange(bucket);
                    }
                    if (pending is { } last)
                        result.Add((last, [.. pendingRids]));
                }
            }

            var next = upper is { } bound ? this.NextAbove(arity, bound, upperInclusive) : null;
            result.Add((next, next is { } nextKey ? this.RidsOf(nextKey) : []));
            return result;
        }

        // The first key above `bound` under the ragged-arity comparer, cut to
        // `arity` — past every key sharing its components when `inclusive`
        // says the bound itself was inside the read, at or past it otherwise —
        // or null when none is.
        public SqlValueKey? NextAbove(int arity, SqlValueKey bound, bool inclusive)
        {
            var sorted = this.EnsureSorted();
            if (sorted.Count == 0 || KeyTupleComparer.Instance.Compare(bound, sorted.Max) > 0)
                return null;
            foreach (var key in sorted.GetViewBetween(bound, sorted.Max))
            {
                var c = KeyTupleComparer.Instance.Compare(key, bound);
                if (c > 0 || (c == 0 && !inclusive))
                    return key.ComponentCount > arity ? key.Prefix(arity) : key;
            }
            return null;
        }

        // The mirror of NextAbove: the last key below `bound`, cut to `arity`.
        public SqlValueKey? PreviousBelow(int arity, SqlValueKey bound, bool inclusive)
        {
            var sorted = this.EnsureSorted();
            if (sorted.Count == 0 || KeyTupleComparer.Instance.Compare(bound, sorted.Min) < 0)
                return null;
            foreach (var key in sorted.GetViewBetween(sorted.Min, bound).Reverse())
            {
                var c = KeyTupleComparer.Instance.Compare(key, bound);
                if (c < 0 || (c == 0 && !inclusive))
                    return key.ComponentCount > arity ? key.Prefix(arity) : key;
            }
            return null;
        }

        // Every row address whose key starts with `prefix`.
        private (int Page, int Slot)[] RidsOf(SqlValueKey prefix)
        {
            if (prefix.ComponentCount == this.Ordinals.Length)
                return this.Buckets.TryGetValue(prefix, out var bucket) ? [.. bucket] : [];
            List<(int Page, int Slot)> rids = [];
            foreach (var key in this.EnsureSorted().GetViewBetween(prefix, prefix))
            {
                if (this.Buckets.TryGetValue(key, out var bucket))
                    rids.AddRange(bucket);
            }
            return [.. rids];
        }

        // Single-column range seek: the in-range keys of a one-column entry,
        // in ascending order, or null once the walk passes candidateCap. Thin
        // wrapper that builds arity-1 composite bounds.
        public List<(int Page, int Slot)>? RangeCandidates(
            bool hasLower, SqlValue lower, bool lowerInclusive, bool hasUpper, SqlValue upper, bool upperInclusive,
            int candidateCap) =>
            this.CappedCandidates(
                hasLower ? new SqlValueKey([lower]) : null, lowerInclusive,
                hasUpper ? new SqlValueKey([upper]) : null, upperInclusive, candidateCap);

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
            SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive) =>
            this.CappedCandidates(lower, lowerInclusive, upper, upperInclusive, int.MaxValue)!;

        // The walk above with an abort: returns null the moment the collected rid
        // count passes <paramref name="candidateCap"/>. A caller that knows the
        // range stops paying for itself past some share of the table passes that
        // share here, so a whole-table range stops after a quarter of the walk
        // instead of building a list it discards. int.MaxValue is uncapped.
        public List<(int Page, int Slot)>? CappedCandidates(
            SqlValueKey? lower, bool lowerInclusive, SqlValueKey? upper, bool upperInclusive, int candidateCap, List<SqlValueKey>? keys = null)
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
                {
                    if (result.Count + bucket.Count > candidateCap)
                        return null;
                    result.AddRange(bucket);
                    for (var i = 0; keys is not null && i < bucket.Count; i++)
                        keys.Add(key);
                }
            }

            return result;
        }
    }
}
