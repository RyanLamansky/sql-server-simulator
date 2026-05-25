using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>STDEV</c> / <c>STDEVP</c> / <c>VAR</c> / <c>VARP</c>: sample
/// and population variance / standard deviation. All return
/// <see cref="SqlType.Float"/>. Sample variants need n &gt; 1 (single-row
/// or empty input → NULL); population variants accept any non-empty input
/// (single-row → 0). NULLs in input are skipped. Implementation uses the
/// classical sum / sum-of-squares formulation for simplicity; a Welford-
/// style two-pass would be more numerically stable but the simulator
/// matches SQL Server's documented behavior at common precisions.
/// </summary>
internal sealed class StatisticalAggregator(AggregateKind kind) : Aggregator
{
    private long count;
    private double sum;
    private double sumOfSquares;

    public override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        var x = value.CoerceTo(SqlType.Float).AsDouble;
        this.count++;
        this.sum += x;
        this.sumOfSquares += x * x;
    }

    // The classical sum / sum-of-squares moments subtract directly, so the
    // statistical aggregates slide incrementally over a window frame.
    public override bool CanRemove => true;

    public override void Remove(SqlValue value)
    {
        if (value.IsNull)
            return;
        var x = value.CoerceTo(SqlType.Float).AsDouble;
        this.count--;
        this.sum -= x;
        this.sumOfSquares -= x * x;
    }

    public override SqlValue Result()
    {
        var isPopulation = kind is AggregateKind.StdevP or AggregateKind.VarP;
        var isStandardDeviation = kind is AggregateKind.Stdev or AggregateKind.StdevP;

        if (this.count == 0)
            return SqlValue.Null(SqlType.Float);
        if (!isPopulation && this.count == 1)
            return SqlValue.Null(SqlType.Float);

        var mean = this.sum / this.count;
        var divisor = isPopulation ? this.count : this.count - 1;
        var variance = (this.sumOfSquares - (this.count * (mean * mean))) / divisor;

        // Floating-point can produce a tiny-negative variance when the true
        // value is zero; clamp before sqrt to avoid NaN.
        if (variance < 0)
            variance = 0;

        return SqlValue.FromDouble(isStandardDeviation ? Math.Sqrt(variance) : variance);
    }
}
