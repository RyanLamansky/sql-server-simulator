using SqlServerSimulator.Parser.Expressions;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>STDEV</c> / <c>STDEVP</c> / <c>VAR</c> / <c>VARP</c>: sample
/// and population variance / standard deviation. All return
/// <see cref="SqlType.Float"/>. Sample variants need n &gt; 1 (single-row
/// or empty input → NULL); population variants accept any non-empty input
/// (single-row → 0). NULLs in input are skipped, and <c>DISTINCT</c> folds
/// each operand value once.
/// <para>
/// The moments are real's own: a running sum and sum of squares in
/// <c>float</c>, combined as <c>(Σx² − (Σx)² / n) / divisor</c> and clamped at
/// zero. That reproduced the last bit of all 126 random sample and population
/// variances probed against SQL Server 2025 (2026-10-01), where
/// <c>Σx² − n·mean²</c>, a two-pass sum of squared deviations and Welford's
/// update each missed some.
/// </para>
/// </summary>
internal sealed class StatisticalAggregator(AggregateKind kind, bool distinct) : Aggregator
{
    private readonly HashSet<SqlValue>? seen = distinct ? [] : null;
    private long count;
    private double sum;
    private double sumOfSquares;

    public override void Add(SqlValue value)
    {
        if (value.IsNull || (this.seen is not null && !this.seen.Add(value)))
            return;
        var x = value.CoerceTo(SqlType.Float).AsDouble;
        this.count++;
        this.sum += x;
        this.sumOfSquares += x * x;
        // A moment past float's range is Msg 8115 at the row that took it
        // there (probed 2026-09-26 against SQL Server 2025).
        if (double.IsInfinity(this.sumOfSquares) || double.IsInfinity(this.sum))
            throw SimulatedSqlException.ArithmeticOverflow("float");
    }

    // The sum / sum-of-squares moments subtract directly, so the statistical
    // aggregates slide incrementally over a window frame. A windowed aggregate
    // takes no DISTINCT.
    public override bool CanRemove => this.seen is null;

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

        var divisor = isPopulation ? this.count : this.count - 1;
        var variance = (this.sumOfSquares - (this.sum * this.sum / this.count)) / divisor;

        // Floating-point can produce a tiny-negative variance when the true
        // value is zero; clamp before sqrt to avoid NaN.
        if (variance < 0)
            variance = 0;

        return SqlValue.FromDouble(isStandardDeviation ? Math.Sqrt(variance) : variance);
    }
}
