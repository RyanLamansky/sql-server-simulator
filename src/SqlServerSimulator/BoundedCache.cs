using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

/// <summary>
/// A concurrent dictionary holding about <paramref name="capacity"/>
/// entries, which forgets its least recently used half when an insert takes
/// it past that — the compile-side memos' shared shape (the plan cache, the
/// token memo, the compiled-batch and statement memos beside them, Query
/// Store's statement shapes).
/// </summary>
/// <remarks>
/// <para>
/// One simulation commonly serves a whole test run: a seed script and every
/// test's hand-written statements run once each, beside the application's
/// queries, which repeat. A cap that admitted the first texts and then
/// refused the rest let the one-off texts fill it, after which every repeated
/// query paid a full compile on every execution for the rest of the run.
/// Evicting by recency keeps whatever is still being executed.
/// </para>
/// <para>
/// Recency is an epoch that only an insert advances. A hit writes the epoch
/// into its entry only when it moved since the entry's last stamp, so the
/// steady state of a repeated query — many threads hitting one entry, nothing
/// inserted — reads the entry and writes nothing that other cores share.
/// Lookups take no lock; an eviction sweep runs on one thread at a time while
/// lookups and inserts go on beside it, and an insert finding a sweep under
/// way leaves the overshoot to the next one.
/// </para>
/// <para>
/// A full cache admits a new key only on its second arrival, remembered in
/// <see cref="sightings"/>. Admitting every one-off text instead kept each
/// one alive long enough to be promoted to an older garbage-collector
/// generation before the next sweep evicted it, and those collections ran
/// over a simulation's whole database: a stream of one-off statements over
/// an imported AdventureWorks ran about a quarter slower. Until the cache
/// first fills every key is admitted on arrival, so a simulation serving a
/// single test caches what it repeats from its first repeat.
/// </para>
/// <para>
/// A pinned entry is never evicted and doesn't count toward the capacity,
/// for an entry whose loss would change what the simulator reports rather
/// than what it costs.
/// </para>
/// </remarks>
internal sealed class BoundedCache<TKey, TValue>(int capacity, IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, Slot> slots = new(comparer);

    /// <summary>The unpinned entries <see cref="slots"/> holds, counted on add and remove, since the dictionary's own count takes every one of its locks.</summary>
    private int count;

    /// <summary>The insert clock a hit stamps its entry with.</summary>
    private long epoch;

    /// <summary>1 while a thread sweeps.</summary>
    private int sweeping;

    /// <summary>
    /// The hash of the last key refused admission at each position, by its
    /// hash: a key whose hash is found at its own position arrived before.
    /// Hashes rather than keys, so that remembering a refused key keeps
    /// nothing of it alive; a collision only admits or refuses a key early.
    /// Allocated once the cache first fills, which a simulation serving a
    /// single test never reaches; racing first refusals may each allocate
    /// one, losing a sighting or two.
    /// </summary>
    private int[]? sightings;

    /// <summary>Test-observable: entries the sweeps have evicted.</summary>
    public long Evictions;

    private sealed class Slot(TValue value, long lastUsed, bool pinned)
    {
        public readonly TValue Value = value;
        public readonly bool Pinned = pinned;
        public long LastUsed = lastUsed;
    }

    /// <summary>The unpinned entries held.</summary>
    public int Count => Volatile.Read(ref this.count);

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        if (this.slots.TryGetValue(key, out var slot))
        {
            this.Touch(slot);
            value = slot.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>
    /// <see cref="TryGetValue(TKey, out TValue)"/> through a key form the
    /// comparer accepts in place of <typeparamref name="TKey"/>, such as a
    /// span over the characters of a string key.
    /// </summary>
    public bool TryGetValue<TAlternate>(TAlternate key, [MaybeNullWhen(false)] out TValue value)
        where TAlternate : notnull, allows ref struct
    {
        if (this.slots.GetAlternateLookup<TAlternate>().TryGetValue(key, out var slot))
        {
            this.Touch(slot);
            value = slot.Value;
            return true;
        }
        value = default;
        return false;
    }

    public bool ContainsKey(TKey key) => this.slots.ContainsKey(key);

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/>, replacing
    /// an entry already there; a new key a full cache refuses (see
    /// <see cref="Admits"/>) stores nothing.
    /// </summary>
    public void Set(TKey key, TValue value, bool pinned = false)
    {
        Slot? slot = null;
        while (true)
        {
            if (this.slots.TryGetValue(key, out var existing))
            {
                slot ??= new Slot(value, Interlocked.Increment(ref this.epoch), pinned);
                if (!this.slots.TryUpdate(key, slot, existing))
                    continue;
                this.Recount(existing, -1);
                this.Recount(slot, 1);
                return;
            }
            if (!pinned && !this.Admits(key))
                return;
            slot ??= new Slot(value, Interlocked.Increment(ref this.epoch), pinned);
            if (this.slots.TryAdd(key, slot))
            {
                this.Recount(slot, 1);
                return;
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="value"/> under <paramref name="key"/> unless an
    /// entry is already there or a full cache refuses the key (see
    /// <see cref="Admits"/>).
    /// </summary>
    public bool TryAdd(TKey key, TValue value, bool pinned = false)
    {
        if (!pinned && !this.Admits(key))
            return false;
        var slot = new Slot(value, Interlocked.Increment(ref this.epoch), pinned);
        if (!this.slots.TryAdd(key, slot))
            return false;
        this.Recount(slot, 1);
        return true;
    }

    /// <summary>
    /// Whether a new <paramref name="key"/> may enter: always while the cache
    /// is under its capacity, and once it is full on the key's second arrival.
    /// A refusal remembers the arrival.
    /// </summary>
    private bool Admits(TKey key)
    {
        if (Volatile.Read(ref this.count) < capacity)
            return true;
        // Never 0, the hash an unwritten position holds.
        var hash = this.slots.Comparer.GetHashCode(key) | 1;
        var sightings = this.sightings ??= new int[int.Max(capacity * 2, 16)];
        ref var seen = ref sightings[(uint)hash % (uint)sightings.Length];
        if (seen == hash)
            return true;
        seen = hash;
        return false;
    }

    /// <summary>
    /// Replaces the entry under <paramref name="key"/> with
    /// <paramref name="value"/> when it still holds
    /// <paramref name="comparand"/>, compared by reference.
    /// </summary>
    public bool TryUpdate(TKey key, TValue value, TValue comparand)
    {
        if (!this.slots.TryGetValue(key, out var existing) || !ReferenceEquals(existing.Value, comparand))
            return false;
        var slot = new Slot(value, Interlocked.Increment(ref this.epoch), existing.Pinned);
        return this.slots.TryUpdate(key, slot, existing);
    }

    /// <summary>Removes every entry whose key <paramref name="match"/> accepts.</summary>
    public void RemoveWhere<TState>(TState state, Func<TState, TKey, bool> match)
    {
        foreach (var (key, slot) in this.slots)
        {
            if (match(state, key) && this.slots.TryRemove(KeyValuePair.Create(key, slot)))
                this.Recount(slot, -1);
        }
    }

    public void Clear() => this.RemoveWhere(0, static (_, _) => true);

    private void Touch(Slot slot)
    {
        var now = Volatile.Read(ref this.epoch);
        if (slot.LastUsed != now)
            slot.LastUsed = now;
    }

    private void Recount(Slot slot, int delta)
    {
        if (slot.Pinned)
            return;
        if (Interlocked.Add(ref this.count, delta) > capacity && delta > 0)
            this.Sweep();
    }

    /// <summary>
    /// Evicts the unpinned entries used less recently than the median one.
    /// Entries stamped alike at the median go too when that alone would free
    /// too little, so a run of entries inserted and never hit can't keep the
    /// cache over its capacity.
    /// </summary>
    private void Sweep()
    {
        if (Interlocked.Exchange(ref this.sweeping, 1) != 0)
            return;
        try
        {
            var stamps = new List<long>(capacity + 16);
            foreach (var (_, slot) in this.slots)
            {
                if (!slot.Pinned)
                    stamps.Add(slot.LastUsed);
            }
            if (stamps.Count <= capacity)
                return;
            stamps.Sort();
            var median = stamps[stamps.Count / 2];
            var inclusive = stamps.IndexOf(median) < stamps.Count / 4;
            var evicted = 0;
            foreach (var (key, slot) in this.slots)
            {
                if (!slot.Pinned && (slot.LastUsed < median || (inclusive && slot.LastUsed == median)) && this.slots.TryRemove(KeyValuePair.Create(key, slot)))
                {
                    _ = Interlocked.Decrement(ref this.count);
                    evicted++;
                }
            }
            _ = Interlocked.Add(ref this.Evictions, evicted);
        }
        finally
        {
            Volatile.Write(ref this.sweeping, 0);
        }
    }
}
