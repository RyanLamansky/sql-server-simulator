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
            this.Accumulator = checked(this.Accumulator + Extract(value.CoerceTo(this.ResultType)));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(this.ResultType.ToString()!);
        }
        this.Count++;
    }

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
            throw SimulatedSqlException.ArithmeticOverflow(this.ResultType.ToString()!);
        }
    }

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
