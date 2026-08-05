using System.Numerics;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Shared accumulation core for the numeric aggregates SUM and AVG. The
/// accumulator type <typeparamref name="TAccumulator"/> is whatever .NET
/// numeric type holds the running sum precisely for the operand family —
/// <see cref="long"/> for int / bigint columns,
/// <see cref="decimal"/> for decimal / money columns, <see cref="double"/>
/// for float / real columns. Generic-math constraints
/// (<see cref="INumber{TSelf}"/>) let one Add/Sum implementation cover all
/// three families without per-type switches; concrete subclasses provide
/// only the SqlValue ↔ TAccumulator extract / wrap and the result-type
/// metadata.
/// </summary>
internal abstract class NumericAggregator<TAccumulator> : Aggregator
    where TAccumulator : struct, INumber<TAccumulator>
{
    private readonly bool distinct;
    private readonly HashSet<SqlValue>? seen;

    protected readonly SqlType ResultType;
    protected TAccumulator Accumulator;
    protected long Count;

    protected NumericAggregator(SqlType resultType, bool distinct)
    {
        this.ResultType = resultType;
        this.distinct = distinct;
        if (distinct)
            this.seen = [];
    }

    public sealed override void Add(SqlValue value)
    {
        if (value.IsNull)
            return;
        if (this.distinct && !this.seen!.Add(value))
            return;
        try
        {
            this.Accumulator = checked(this.Accumulator + this.ExtractCoerced(value));
        }
        catch (OverflowException)
        {
            throw this.AccumulatorOverflow();
        }
        this.Count++;
    }

    // A running sum subtracts cleanly, so SUM / AVG slide incrementally — but
    // only without DISTINCT, whose dedup set can't tell whether a removed value
    // still has surviving duplicates. (DISTINCT is illegal with OVER anyway, so
    // the window path never requests it.)
    public sealed override bool CanRemove => !this.distinct;

    public sealed override void Remove(SqlValue value)
    {
        if (value.IsNull)
            return;
        this.Accumulator -= this.ExtractCoerced(value);
        this.Count--;
    }

    /// <summary>
    /// Exact for the <see cref="long"/> and <see cref="decimal"/> accumulators
    /// the parallel gate admits — integer and decimal addition is associative,
    /// so a partitioned total is the serial total. A DISTINCT accumulator
    /// replays the other side's members through <see cref="Add"/> rather than
    /// adding totals, since a value present on both sides must be counted once.
    /// <para>
    /// The <see cref="double"/> accumulator is <b>not</b> exact under
    /// re-association and the gate never admits it; the overflow guard here is
    /// what catches a partitioned total that overflows where the serial one
    /// would have — the serial re-run then reports whatever real reports.
    /// </para>
    /// </summary>
    public sealed override bool TryMergeFrom(Aggregator other)
    {
        var source = (NumericAggregator<TAccumulator>)other;
        if (this.distinct)
        {
            foreach (var value in source.seen!)
                this.Add(value);
            return true;
        }

        try
        {
            this.Accumulator = checked(this.Accumulator + source.Accumulator);
        }
        catch (OverflowException)
        {
            return false;
        }
        this.Count += source.Count;
        return true;
    }

    /// <summary>
    /// The accumulator-typed value of one operand, coerced to the result type
    /// first. Overridable so a family whose coercion is a per-row allocation
    /// can skip it where the crossing provably changes nothing.
    /// </summary>
    protected virtual TAccumulator ExtractCoerced(SqlValue value) => Extract(value.CoerceTo(this.ResultType));

    public sealed override SqlValue Result()
    {
        if (this.Count == 0)
            return SqlValue.Null(this.ResultType);
        try
        {
            return Wrap(Finalize(this.Accumulator, this.Count), this.ResultType);
        }
        catch (OverflowException)
        {
            throw this.AccumulatorOverflow();
        }
    }

    /// <summary>
    /// A running total that outgrew its accumulator. For an integer or float
    /// family that is real's own Msg 8115; for a decimal one it never is —
    /// real's <c>numeric</c> reaches 38 digits, so a total that overflowed a
    /// .NET decimal at 29 is one real would have carried.
    /// </summary>
    private Exception AccumulatorOverflow() =>
        this.ResultType is DecimalSqlType && this.Accumulator is decimal
            ? DecimalCeiling.Exceeded($"accumulating a running {this.ResultType} total")
            : SimulatedSqlException.ArithmeticOverflow(this.ResultType.ToString()!);

    /// <summary>Pulls the accumulator-typed value out of a coerced <see cref="SqlValue"/>.</summary>
    protected abstract TAccumulator Extract(SqlValue value);

    /// <summary>
    /// Computes the per-aggregate finalize step from the accumulated total
    /// and the row count: SUM returns the total unchanged; AVG divides by
    /// count.
    /// </summary>
    protected abstract TAccumulator Finalize(TAccumulator total, long count);

    /// <summary>Wraps the finalized accumulator value as a typed <see cref="SqlValue"/>.</summary>
    protected abstract SqlValue Wrap(TAccumulator value, SqlType resultType);
}
