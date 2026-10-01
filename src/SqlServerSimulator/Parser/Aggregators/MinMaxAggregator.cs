using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>MAX</c> and <c>MIN</c>: tracks the running extreme via
/// <see cref="SqlValue.CompareTo"/>. NULL operands are skipped; empty input
/// returns NULL of the operand type. The DISTINCT keyword is honored at
/// parse time but doesn't affect the result (DISTINCT extremes equal plain
/// extremes; SQL Server accepts the keyword as a no-op here).
/// <para>
/// A single running extreme can't be un-done (dropping the current extreme
/// leaves the next one unknown), so the removable mode requested for sliding
/// window frames keeps a directional multiset instead — ordered so the wanted
/// extreme is always the first key, holding the frame's own values in arrival
/// order for removal. Values that compare equal can render differently (a
/// trailing space, a case-insensitive collation), and a frame's answer is the
/// first of them still inside it — keying on the first one ever added
/// answered <c>'x'</c> for a frame holding only <c>'x '</c>.
/// GROUP BY and forward-cumulative windows never remove, so they keep the
/// cheaper two-field running-extreme path.
/// </para>
/// </summary>
internal sealed class MinMaxAggregator : Aggregator
{
    private readonly SqlType resultType;
    private readonly bool isMax;
    private readonly SortedDictionary<SqlValue, Queue<SqlValue>>? multiset;

    private SqlValue current;
    private bool sawAny;

    public MinMaxAggregator(SqlType resultType, bool isMax, bool removable = false)
    {
        this.resultType = resultType;
        this.isMax = isMax;
        this.current = SqlValue.Null(resultType);
        if (removable)
        {
            this.multiset = new SortedDictionary<SqlValue, Queue<SqlValue>>(
                Comparer<SqlValue>.Create(isMax ? static (a, b) => b.CompareTo(a) : static (a, b) => a.CompareTo(b)));
        }
    }

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        if (this.multiset is { } bag)
        {
            if (!bag.TryGetValue(value, out var peers))
                bag[value] = peers = new Queue<SqlValue>(1);
            peers.Enqueue(value);
            return;
        }
        if (!this.sawAny)
        {
            this.current = value;
            this.sawAny = true;
            return;
        }
        var cmp = value.CompareTo(this.current);
        if ((this.isMax && cmp > 0) || (!this.isMax && cmp < 0))
            this.current = value;
    }

    public override bool CanRemove => this.multiset is not null;

    public override void Remove(SqlValue value)
    {
        if (value.IsNull)
            return;
        // A frame's start advances in arrival order, so the value leaving is
        // the oldest of its peers.
        var bag = this.multiset!;
        var peers = bag[value];
        _ = peers.Dequeue();
        if (peers.Count == 0)
            _ = bag.Remove(value);
    }

    /// <summary>
    /// Exact whenever the two extremes are ordered, and whenever a tie is
    /// between values a serial scan could not have told apart. A tie between
    /// values that compare equal but render differently — <c>'abc'</c> against
    /// <c>'ABC'</c> under a case-insensitive collation, a trailing-space
    /// varchar, a decimal carrying a different scale — is where serial order
    /// becomes observable (a running extreme keeps the <em>first</em> of a tie,
    /// and a merge has no scan order to consult), so it declines and the
    /// statement re-runs serially.
    /// </summary>
    public override bool TryMergeFrom(Aggregator other)
    {
        var source = (MinMaxAggregator)other;
        if (this.multiset is not null || source.multiset is not null)
            return false;
        if (!source.sawAny)
            return true;
        if (!this.sawAny)
        {
            this.current = source.current;
            this.sawAny = true;
            return true;
        }

        var cmp = source.current.CompareTo(this.current);
        if ((this.isMax && cmp > 0) || (!this.isMax && cmp < 0))
        {
            this.current = source.current;
            return true;
        }
        return cmp != 0 || this.current.IsIdenticalTo(source.current);
    }

    public override SqlValue Result()
    {
        if (this.multiset is { } bag)
        {
            foreach (var pair in bag)
                return pair.Value.Peek();
            return SqlValue.Null(this.resultType);
        }

        return this.sawAny ? this.current : SqlValue.Null(this.resultType);
    }
}
