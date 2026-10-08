using System.Collections.Concurrent;

namespace SqlServerSimulator;

/// <summary>
/// An equality comparer over <paramref name="inner"/> whose hash is memoized
/// by ordinal text — for the dictionaries name resolution asks for the same
/// few names over and over: a schema's objects (<see cref="Collation.NameKeys"/>),
/// the catalog views, a batch's variables.
/// </summary>
/// <remarks>
/// A collation's hash weighs every character (Latin1-General's table walk,
/// about 80 ns for a short name, or an ICU sort key), and resolving one
/// object name hashes it once per dictionary the resolver tries — the catalog
/// views, then a schema's views, synonyms, functions and tables — on each of
/// the two parses a one-off text gets; a CPU profile put those hashes at 6–9%
/// of a one-off <c>SELECT</c>. The hash is a pure function of the name's text,
/// so a hit in the memo costs an ordinal hash instead. The memo is bounded in
/// entries and name length and stops growing when full, past which a name
/// pays the inner hash again.
/// </remarks>
internal sealed class MemoizedHashComparer(IEqualityComparer<string> inner) : IEqualityComparer<string>
{
    private const int MemoCapacity = 4096;
    private const int MemoMaxLength = 128;

    private readonly ConcurrentDictionary<string, int> hashes = new(StringComparer.Ordinal);

    private int memoized;

    // Identical text is always equal, which settles the common case — a
    // name spelled as it was declared — without the inner compare.
    public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal) || inner.Equals(x, y);

    public int GetHashCode(string obj)
    {
        if (this.hashes.TryGetValue(obj, out var hash))
            return hash;
        hash = inner.GetHashCode(obj);
        if (obj.Length <= MemoMaxLength && Volatile.Read(ref this.memoized) < MemoCapacity && this.hashes.TryAdd(obj, hash))
            _ = Interlocked.Increment(ref this.memoized);
        return hash;
    }
}
