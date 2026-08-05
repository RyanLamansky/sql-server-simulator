using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>COUNT(*)</c>, <c>COUNT(expr)</c>, <c>COUNT(DISTINCT expr)</c>,
/// <c>COUNT_BIG</c>, and <c>APPROX_COUNT_DISTINCT</c>. The <c>*</c> form
/// counts every row including NULL-bearing ones; the column form skips
/// NULL; the DISTINCT form additionally dedups via a
/// <see cref="HashSet{SqlValue}"/>. Empty input → 0 (the only aggregate
/// that doesn't return NULL on empty). The result is <see cref="int"/> for
/// <c>COUNT</c> and <see cref="long"/> for <c>COUNT_BIG</c> /
/// <c>APPROX_COUNT_DISTINCT</c>; an int-row count over 2^31 raises
/// Msg 8115 on result extraction (matching SQL Server, which steers users
/// toward <c>COUNT_BIG</c>).
/// </summary>
internal sealed class CountAggregator(bool isStar, bool isBigCount, bool distinct) : Aggregator
{
    private long count;
    private readonly HashSet<SqlValue>? seen = distinct ? [] : null;

    public override void Add(SqlValue value)
    {
        if (isStar)
        {
            this.count++;
            return;
        }
        if (value.IsNull)
            return;
        if (this.seen is { } seenSet)
        {
            if (seenSet.Add(value))
                this.count++;
        }
        else
        {
            this.count++;
        }
    }

    // A plain row/value count decrements cleanly; the DISTINCT form can't,
    // since the dedup set doesn't carry per-value multiplicity. (COUNT(DISTINCT)
    // / APPROX_COUNT_DISTINCT are illegal with OVER, so the window path never
    // requests a removable distinct count.)
    public override bool CanRemove => this.seen is null;

    public override void Remove(SqlValue value)
    {
        if (!isStar && value.IsNull)
            return;
        this.count--;
    }

    /// <summary>
    /// Exact for both forms: a plain count adds, and a DISTINCT count replays
    /// the other side's members through <see cref="Add"/>, which counts only
    /// the ones this side hadn't seen. Set union is order-free, so the merged
    /// count is the serial count.
    /// </summary>
    public override bool TryMergeFrom(Aggregator other)
    {
        var source = (CountAggregator)other;
        if (this.seen is { } mine)
        {
            foreach (var value in source.seen!)
            {
                if (mine.Add(value))
                    this.count++;
            }
            return true;
        }

        this.count += source.count;
        return true;
    }

    public override SqlValue Result() => isBigCount
        ? SqlValue.FromInt64(this.count)
        : this.count > int.MaxValue
            ? throw SimulatedSqlException.ArithmeticOverflow("int")
            : SqlValue.FromInt32((int)this.count);
}
