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
/// extreme is always the first key, with per-value multiplicity for removal.
/// GROUP BY and forward-cumulative windows never remove, so they keep the
/// cheaper two-field running-extreme path.
/// </para>
/// </summary>
internal sealed class MinMaxAggregator : Aggregator
{
    private readonly SqlType resultType;
    private readonly bool isMax;
    private readonly SortedDictionary<SqlValue, int>? multiset;

    private SqlValue current;
    private bool sawAny;

    public MinMaxAggregator(SqlType resultType, bool isMax, bool removable = false)
    {
        this.resultType = resultType;
        this.isMax = isMax;
        this.current = SqlValue.Null(resultType);
        if (removable)
        {
            this.multiset = new SortedDictionary<SqlValue, int>(
                Comparer<SqlValue>.Create(isMax ? static (a, b) => b.CompareTo(a) : static (a, b) => a.CompareTo(b)));
        }
    }

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        if (this.multiset is { } bag)
        {
            _ = bag.TryGetValue(value, out var n);
            bag[value] = n + 1;
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
        var bag = this.multiset!;
        var n = bag[value];
        if (n == 1)
            _ = bag.Remove(value);
        else
            bag[value] = n - 1;
    }

    public override SqlValue Result()
    {
        if (this.multiset is { } bag)
        {
            foreach (var pair in bag)
                return pair.Key;
            return SqlValue.Null(this.resultType);
        }

        return this.sawAny ? this.current : SqlValue.Null(this.resultType);
    }
}
