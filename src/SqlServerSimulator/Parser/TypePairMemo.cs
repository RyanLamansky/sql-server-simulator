using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The type an operator settles for its two operand types — the type a
/// comparison's differently-typed operands compare in, the type a string
/// <c>+</c> yields — kept for the first pair that settled without raising,
/// so the rows after it skip the settling: a column beside a literal brings
/// one pair to every row of a scan.
/// </summary>
/// <remarks>
/// Keyed by the two types' identity, which holds for a column (its schema's
/// type) and for a literal or variable (one instance per parse). A pair that
/// raises, or that a comparison reads its exact <c>datetime</c> order for
/// instead, is never kept, so an error still surfaces on every row it always
/// did. Only the first pair is kept: an operand whose type varies per row
/// misses at the cost of two reference compares rather than allocating an
/// entry per row. A cached plan is shared across sessions, so the entry is
/// immutable and published once.
/// </remarks>
internal sealed class TypePairMemo
{
    private Entry? entry;

    /// <summary>The type settled for <paramref name="left"/> and <paramref name="right"/>, or null when they are not the kept pair.</summary>
    public SqlType? Find(SqlType left, SqlType right) =>
        Volatile.Read(ref this.entry) is { } kept && ReferenceEquals(kept.Left, left) && ReferenceEquals(kept.Right, right) ? kept.Settled : null;

    /// <summary>Keeps <paramref name="settled"/> for the pair, unless a pair is kept already.</summary>
    public void Keep(SqlType left, SqlType right, SqlType settled)
    {
        if (Volatile.Read(ref this.entry) is null)
            _ = Interlocked.CompareExchange(ref this.entry, new Entry(left, right, settled), null);
    }

    private sealed class Entry(SqlType left, SqlType right, SqlType settled)
    {
        public readonly SqlType Left = left;
        public readonly SqlType Right = right;
        public readonly SqlType Settled = settled;
    }
}
