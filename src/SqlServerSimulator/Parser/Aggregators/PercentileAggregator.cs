using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>APPROX_PERCENTILE_CONT</c> / <c>APPROX_PERCENTILE_DISC</c>
/// (<c>WITHIN GROUP (ORDER BY v [ASC | DESC])</c>): the percentile of the
/// group's non-NULL values, computed exactly — what real's sketch answers over
/// the small sets anyone can compare it on (probed 2026-09-26 against SQL
/// Server 2025), where a large set would let real's approximation drift from
/// it. CONT interpolates linearly as <c>PERCENTILE_CONT</c> does, into
/// <c>float</c>; DISC takes the first value whose cumulative share reaches
/// the fraction, in the operand's type. A <c>DESC</c> order reads the
/// ascending values at <c>1 - p</c>, as real's does. The fraction arrives per
/// row (<see cref="SetFraction"/>) and must lie in <c>[0, 1]</c> (Msg 8727).
/// </summary>
internal sealed class PercentileAggregator(SqlType resultType, bool continuous, bool descending) : Aggregator
{
    private readonly List<SqlValue> values = [];
    private double? fraction;

    public void SetFraction(SqlValue value)
    {
        if (this.fraction is not null)
            return;
        var p = value.IsNull ? double.NaN : value.CoerceTo(SqlType.Float).AsDouble;
        this.fraction = p is >= 0 and <= 1 ? p : throw SimulatedSqlException.PercentileInputOutOfRange();
    }

    public override void Add(SqlValue value)
    {
        if (!value.IsNull)
            this.values.Add(value);
    }

    public override SqlValue Result()
    {
        if (this.values.Count == 0 || this.fraction is not { } p)
            return SqlValue.Null(resultType);
        this.values.Sort(static (a, b) => a.CompareTo(b));
        if (descending)
            p = 1 - p;
        var count = this.values.Count;
        if (!continuous)
            return this.values[Math.Max((int)Math.Ceiling(p * count) - 1, 0)];

        var position = p * (count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var low = this.values[lower].CoerceTo(SqlType.Float).AsDouble;
        var high = this.values[upper].CoerceTo(SqlType.Float).AsDouble;
        return SqlValue.FromDouble(low + ((position - lower) * (high - low)));
    }
}
