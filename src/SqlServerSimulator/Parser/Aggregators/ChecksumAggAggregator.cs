using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>CHECKSUM_AGG(expr)</c>: the XOR of its <c>int</c> operands, NULLs
/// skipped, and NULL when no value arrived — probed 2026-09-25 against SQL
/// Server 2025 (<c>1, 2, 4</c> fold to 7, <c>-1, 5</c> to -6, two equal values
/// cancel). <c>DISTINCT</c> folds each value once. The operand's type was
/// settled while compiling.
/// </summary>
internal sealed class ChecksumAggAggregator(bool distinct) : Aggregator
{
    private readonly HashSet<int>? seen = distinct ? [] : null;
    private int folded;
    private long count;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        var operand = value.AsInt32;
        if (this.seen is not null && !this.seen.Add(operand))
            return;
        this.folded ^= operand;
        this.count++;
    }

    // XOR is its own inverse, so re-folding a value removes it — the window
    // frame slides incrementally. A windowed aggregate takes no DISTINCT.
    public override bool CanRemove => this.seen is null;

    public override void Remove(SqlValue value)
    {
        if (value.IsNull)
            return;
        this.folded ^= value.AsInt32;
        this.count--;
    }

    /// <summary>
    /// Exact without DISTINCT — XOR is associative and commutative, so any
    /// partition of the same multiset folds to the same value.
    /// </summary>
    public override bool TryMergeFrom(Aggregator other)
    {
        if (this.seen is not null)
            return false;
        var partial = (ChecksumAggAggregator)other;
        this.folded ^= partial.folded;
        this.count += partial.count;
        return true;
    }

    public override SqlValue Result() => this.count == 0 ? SqlValue.Null(SqlType.Int32) : SqlValue.FromInt32(this.folded);
}
