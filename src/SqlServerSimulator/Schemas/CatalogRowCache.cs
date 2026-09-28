using System.Collections.Concurrent;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Schemas;

/// <summary>
/// The encoded rows of the metadata catalog views that opt in
/// (<see cref="CatalogView.Cacheable"/>), kept across statements until the
/// next change to what they project, with hash indexes built over them on
/// demand. It stands in for the indexed base tables real's catalog views read
/// (<c>sysschobjs</c>, <c>syscolpars</c>, <c>sysiscols</c>): an equality or
/// join on a catalog column becomes a lookup instead of a projection of every
/// object in the database per execution.
/// </summary>
/// <remarks>
/// <para>
/// One instance per <see cref="Simulation"/>. Every rowset belongs to the
/// generation of the <see cref="Invalidate"/> count it was built under, and a
/// read under a later count starts a fresh generation, dropping every earlier
/// rowset at once. Invalidation is conservative by construction: it rides every
/// <see cref="Simulation.BumpSchemaVersion"/>, plus the few statements that
/// change what a cacheable view projects without passing the DDL arm (each
/// calls <see cref="Invalidate"/> itself). A view with a column ordinary DML
/// moves carries a <see cref="CatalogView.LiveStamp"/> instead, compared on
/// every hit.
/// </para>
/// <para>
/// A rowset is served only where its rows are what the view would generate
/// for every reader: never for <c>tempdb</c>, whose listing includes the
/// reading session's own <c>#temp</c> tables, and never to a session whose
/// metadata visibility filters the view. Debug builds regenerate the view on
/// the first hit per statement and throw on any difference, so a missing
/// invalidation fails the test that exposes it rather than serving stale
/// metadata.
/// </para>
/// </remarks>
internal sealed class CatalogRowCache
{
    private long version;
    private Generation current = new(0);

    /// <summary>
    /// Discards every rowset built so far; the next read of each view
    /// regenerates it.
    /// </summary>
    public void Invalidate() => Interlocked.Increment(ref this.version);

    /// <summary>
    /// The live rowset for <paramref name="view"/> over <paramref name="database"/>,
    /// building it with <paramref name="generate"/> on a miss. A rowset built
    /// while an invalidation lands is returned to its reader but not kept.
    /// </summary>
    public CatalogRowSet GetOrBuild(CatalogView view, Database database, BatchContext batch, Func<CatalogView, BatchContext, Database, List<byte[]>> generate)
    {
        var stamp = Volatile.Read(ref this.version);
        var generation = Volatile.Read(ref this.current);
        if (generation.Version < stamp)
        {
            var fresh = new Generation(stamp);
            var prior = Interlocked.CompareExchange(ref this.current, fresh, generation);
            generation = ReferenceEquals(prior, generation) ? fresh : prior;
        }

        var key = (view, database);
        var live = view.LiveStamp?.Invoke(database);
        if (generation.Version == stamp && generation.Sets.TryGetValue(key, out var cached)
            && (live is null || live.AsSpan().SequenceEqual(cached.LiveStamp)))
        {
            CatalogPushdownDiagnostics.Sink?.Add($"CacheHit({view.Name})");
#if DEBUG
            AuditAgainstRegeneration(cached, batch, database, generate);
#endif
            return cached;
        }

        CatalogPushdownDiagnostics.Sink?.Add($"CacheBuild({view.Name})");
        var built = new CatalogRowSet(view, generate(view, batch, database), live ?? []);
        if (generation.Version != stamp || Volatile.Read(ref this.version) != stamp)
            return built;
        generation.Sets[key] = built;
        return built;
    }

#if DEBUG
    /// <summary>
    /// Regenerates a cached view once per statement and compares it row for
    /// row, turning a missing <see cref="Invalidate"/> call into a failure of
    /// whichever test exposes it.
    /// </summary>
    private static void AuditAgainstRegeneration(CatalogRowSet cached, BatchContext batch, Database database, Func<CatalogView, BatchContext, Database, List<byte[]>> generate)
    {
        var audited = batch.CurrentStatement.AuditedCatalogRowSets ??= [];
        if (!audited.Add(cached))
            return;
        var fresh = generate(cached.View, batch, database);
        var same = fresh.Count == cached.Rows.Count;
        for (var i = 0; same && i < fresh.Count; i++)
            same = fresh[i].AsSpan().SequenceEqual(cached.Rows[i]);
        if (!same)
            throw new InvalidOperationException($"The cached rows of catalog view {cached.View.Name} in {database.Name} are stale: a change to what the view projects did not invalidate the catalog row cache.");
    }
#endif

    private sealed class Generation(long version)
    {
        public readonly long Version = version;
        public readonly ConcurrentDictionary<(CatalogView View, Database Database), CatalogRowSet> Sets = new();
    }
}

/// <summary>
/// One cached projection of a catalog view: its encoded rows in the view's own
/// order, and the hash indexes built over them so far. Immutable once
/// published, so any number of sessions read it concurrently.
/// </summary>
internal sealed class CatalogRowSet(CatalogView view, List<byte[]> rows, Int128[] liveStamp)
{
    /// <summary>
    /// How many distinct indexes one rowset keeps; past it an index is built
    /// for its caller and dropped, bounding what an unusual stream of key-type
    /// pairings can accumulate.
    /// </summary>
    private const int MaxIndexes = 64;

    public readonly CatalogView View = view;

    /// <summary>The rows, never mutated after construction.</summary>
    public readonly List<byte[]> Rows = rows;

    /// <summary>The view's <see cref="CatalogView.LiveStamp"/> when the rows were generated; empty for a view without one.</summary>
    public readonly Int128[] LiveStamp = liveStamp;

    private readonly ConcurrentDictionary<CatalogIndexKey, CatalogIndex> indexes = new();

    /// <summary>
    /// The index over the columns at <paramref name="ordinals"/>, each value
    /// coerced to the matching <paramref name="keyTypes"/> entry before it is
    /// hashed, which is what makes a bucket hit mean exactly what the
    /// <c>=</c> operator would answer between two values of those types.
    /// </summary>
    public CatalogIndex IndexOn(int[] ordinals, SqlType[] keyTypes)
    {
        var key = new CatalogIndexKey(ordinals, keyTypes);
        if (this.indexes.TryGetValue(key, out var index))
            return index;
        index = CatalogIndex.Build(this.View.Columns, this.Rows, ordinals, keyTypes);
        return this.indexes.Count < MaxIndexes ? this.indexes.GetOrAdd(key, index) : index;
    }

    /// <summary>
    /// The rows whose column at <paramref name="ordinal"/> equals one of
    /// <paramref name="values"/>, in the view's order. Every row when a value's
    /// type can't key the column exactly — the caller's residual predicate then
    /// decides, since a seek may over-produce but never drop a row.
    /// </summary>
    public IEnumerable<byte[]> Seek(int ordinal, SqlValue[] values)
    {
        var columnType = this.View.Columns[ordinal].Type;
        if (values.Length == 1)
        {
            var only = values[0];
            if (only.IsNull)
                return [];
            if (KeyTypeFor(columnType, only.Type) is not { } keyType)
                return this.Rows;
            var index = this.IndexOn([ordinal], [keyType]);
            return index.Buckets.TryGetValue(new SqlValueKey([only.CoerceTo(keyType)]), out var chain)
                ? index.Chain(this.Rows, chain.Head)
                : [];
        }

        var ordinals = new List<int>();
        foreach (var value in values)
        {
            if (value.IsNull)
                continue;
            if (KeyTypeFor(columnType, value.Type) is not { } keyType)
                return this.Rows;
            var index = this.IndexOn([ordinal], [keyType]);
            if (!index.Buckets.TryGetValue(new SqlValueKey([value.CoerceTo(keyType)]), out var chain))
                continue;
            for (var row = chain.Head; row != -1; row = index.Next[row])
                ordinals.Add(row);
        }
        ordinals.Sort();
        var result = new List<byte[]>(ordinals.Count);
        for (var i = 0; i < ordinals.Count; i++)
        {
            if (i == 0 || ordinals[i] != ordinals[i - 1])
                result.Add(this.Rows[ordinals[i]]);
        }
        return result;
    }

    /// <summary>
    /// The type a seek keys a column of <paramref name="columnType"/> on for a
    /// comparand of <paramref name="comparandType"/>, or null when coercing
    /// both to one type could change what <c>=</c> answers: integers key as
    /// <c>bigint</c>, strings as <c>nvarchar(max)</c> under the collation the
    /// comparison resolves to, and every other pairing declines.
    /// </summary>
    internal static SqlType? KeyTypeFor(SqlType columnType, SqlType comparandType)
    {
        if (columnType.Category != comparandType.Category)
            return null;
        return columnType.Category switch
        {
            SqlTypeCategory.Integer => SqlType.BigInt,
            SqlTypeCategory.String when !columnType.IsIncomparable && !comparandType.IsIncomparable
                && Collation.Resolve(columnType, comparandType) is { } resolved
                => NVarcharSqlType.Get(SqlType.MaxLengthSentinel, resolved.Collation, Coercibility.Implicit),
            _ => null,
        };
    }
}

/// <summary>
/// A hash index over a <see cref="CatalogRowSet"/>: each distinct key's rows
/// as a forward chain over row ordinals, in row order — the shape the hash
/// equi-join builds for itself, so a join can probe it in place of its own
/// build.
/// </summary>
internal sealed class CatalogIndex(Dictionary<SqlValueKey, (int Head, int Tail)> buckets, List<int> next)
{
    public readonly Dictionary<SqlValueKey, (int Head, int Tail)> Buckets = buckets;

    /// <summary>Each row's successor in its key's chain; -1 ends a chain.</summary>
    public readonly List<int> Next = next;

    public static CatalogIndex Build(HeapColumn[] columns, List<byte[]> rows, int[] ordinals, SqlType[] keyTypes)
    {
        var buckets = new Dictionary<SqlValueKey, (int Head, int Tail)>();
        var next = new List<int>(rows.Count);
        var scratch = new SqlValue[ordinals.Length];
        for (var r = 0; r < rows.Count; r++)
        {
            next.Add(-1);
            var hasNull = false;
            for (var k = 0; k < ordinals.Length; k++)
            {
                var value = RowDecoder.DecodeColumn(columns, rows[r], ordinals[k]);
                if (value.IsNull)
                {
                    hasNull = true;
                    break;
                }
                scratch[k] = value.CoerceTo(keyTypes[k]);
            }
            if (hasNull)
                continue;
            ref var chain = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(buckets, new SqlValueKey(scratch), out var existed);
            if (existed)
            {
                next[chain.Tail] = r;
                chain.Tail = r;
            }
            else
            {
                chain = (r, r);
                scratch = new SqlValue[ordinals.Length];
            }
        }
        return new CatalogIndex(buckets, next);
    }

    /// <summary>The rows of the chain starting at <paramref name="head"/>, in row order.</summary>
    public IEnumerable<byte[]> Chain(List<byte[]> rows, int head)
    {
        for (var row = head; row != -1; row = this.Next[row])
            yield return rows[row];
    }
}

/// <summary>Which columns an index keys, and the type each is coerced to.</summary>
internal readonly struct CatalogIndexKey(int[] ordinals, SqlType[] keyTypes) : IEquatable<CatalogIndexKey>
{
    private readonly int[] ordinals = ordinals;
    private readonly SqlType[] keyTypes = keyTypes;

    public bool Equals(CatalogIndexKey other) =>
        this.ordinals.AsSpan().SequenceEqual(other.ordinals) && this.keyTypes.AsSpan().SequenceEqual(other.keyTypes);

    public override bool Equals(object? obj) => obj is CatalogIndexKey other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var ordinal in this.ordinals)
            hash.Add(ordinal);
        foreach (var type in this.keyTypes)
            hash.Add(type);
        return hash.ToHashCode();
    }
}
