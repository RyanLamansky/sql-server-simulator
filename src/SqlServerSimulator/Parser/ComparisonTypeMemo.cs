using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The type a comparison's differently-typed operands compare in, kept for
/// the first pair of operand types that passed the pair's legality check, so
/// the rows after it skip the check and the promotion: a column beside a
/// literal brings one pair to every row of a scan.
/// </summary>
/// <remarks>
/// Keyed by the two types' identity, which holds for a column (its schema's
/// type) and for a literal or variable (one instance per parse). A pair that
/// raises, or that reads its exact <c>datetime</c> order instead, is never
/// kept, so an error still surfaces on every row it always did. Only the first
/// pair is kept: an operand whose type varies per row misses at the cost of
/// two reference compares rather than allocating an entry per row. A cached
/// plan is shared across sessions, so the entry is immutable and published
/// once.
/// </remarks>
internal sealed class ComparisonTypeMemo
{
    private Entry? entry;

    /// <summary>The type <paramref name="left"/> and <paramref name="right"/> compare in, or null when they are not the kept pair.</summary>
    public SqlType? Find(SqlType left, SqlType right) =>
        Volatile.Read(ref this.entry) is { } kept && ReferenceEquals(kept.Left, left) && ReferenceEquals(kept.Right, right) ? kept.Common : null;

    /// <summary>Keeps <paramref name="common"/> for the pair, unless a pair is kept already.</summary>
    public void Keep(SqlType left, SqlType right, SqlType common)
    {
        if (Volatile.Read(ref this.entry) is null)
            _ = Interlocked.CompareExchange(ref this.entry, new Entry(left, right, common), null);
    }

    private sealed class Entry(SqlType left, SqlType right, SqlType common)
    {
        public readonly SqlType Left = left;
        public readonly SqlType Right = right;
        public readonly SqlType Common = common;
    }
}
