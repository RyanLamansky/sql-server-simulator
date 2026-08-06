using System.Numerics;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// The DISTINCT / row-count / merge plumbing SUM and AVG share whatever they
/// accumulate in. The accumulator itself lives in a subclass — generic math
/// over a .NET primitive for the integer and float families
/// (<see cref="NumericAggregator{TAccumulator}"/>),
/// <see cref="Decimal38"/> at the promoted result type for the exact-numeric
/// one (<see cref="Decimal38Aggregator"/>).
/// </summary>
internal abstract class NumericAggregatorBase : Aggregator
{
    private readonly bool distinct;
    private readonly HashSet<SqlValue>? seen;

    protected readonly SqlType ResultType;
    protected long Count;

    protected NumericAggregatorBase(SqlType resultType, bool distinct)
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
        Accumulate(value);
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
        Deduct(value);
        this.Count--;
    }

    /// <summary>
    /// Exact for the <see cref="long"/> and <see cref="Decimal38"/>
    /// accumulators the parallel gate admits — integer and decimal addition is
    /// associative, so a partitioned total is the serial total. A DISTINCT
    /// accumulator replays the other side's members through
    /// <see cref="Add"/> rather than adding totals, since a value present on
    /// both sides must be counted once.
    /// <para>
    /// The <see cref="double"/> accumulator is <b>not</b> exact under
    /// re-association and the gate never admits it; the overflow guard in the
    /// subclass is what catches a partitioned total that overflows where the
    /// serial one would have — the serial re-run then reports whatever real
    /// reports.
    /// </para>
    /// </summary>
    public sealed override bool TryMergeFrom(Aggregator other)
    {
        var source = (NumericAggregatorBase)other;
        if (this.distinct)
        {
            foreach (var value in source.seen!)
                this.Add(value);
            return true;
        }

        if (!TryMergeTotals(source))
            return false;
        this.Count += source.Count;
        return true;
    }

    /// <summary>
    /// A running total that outgrew the result type. Real names the family
    /// rather than the declared type — a decimal / numeric total reports
    /// <c>numeric</c> — at Msg 8115 state 2, the same shape a <c>+</c> of the
    /// same two values raises (probe-confirmed).
    /// </summary>
    protected Exception AccumulatorOverflow() =>
        SimulatedSqlException.ArithmeticOverflow(this.ResultType is DecimalSqlType ? "numeric" : this.ResultType.ToString()!);

    /// <summary>Folds one non-NULL, non-duplicate operand into the running total.</summary>
    protected abstract void Accumulate(SqlValue value);

    /// <summary>Backs one operand out of the running total, for the sliding window.</summary>
    protected abstract void Deduct(SqlValue value);

    /// <summary>Adds another partition's total to this one, false when it doesn't fit.</summary>
    protected abstract bool TryMergeTotals(NumericAggregatorBase other);
}

/// <summary>
/// Shared accumulation core for the numeric aggregates SUM and AVG whose
/// running total is a .NET primitive: <see cref="long"/> for int / bigint
/// columns and <see cref="double"/> for float / real ones. Generic-math
/// constraints (<see cref="INumber{TSelf}"/>) let one Add/Sum implementation
/// cover both families without per-type switches; concrete subclasses provide
/// only the SqlValue ↔ TAccumulator extract / wrap and the result-type
/// metadata.
/// </summary>
internal abstract class NumericAggregator<TAccumulator>(SqlType resultType, bool distinct)
    : NumericAggregatorBase(resultType, distinct)
    where TAccumulator : struct, INumber<TAccumulator>
{
    protected TAccumulator Accumulator;

    protected sealed override void Accumulate(SqlValue value)
    {
        try
        {
            this.Accumulator = checked(this.Accumulator + this.ExtractCoerced(value));
        }
        catch (OverflowException)
        {
            throw this.AccumulatorOverflow();
        }
    }

    protected sealed override void Deduct(SqlValue value) => this.Accumulator -= this.ExtractCoerced(value);

    protected sealed override bool TryMergeTotals(NumericAggregatorBase other)
    {
        var source = (NumericAggregator<TAccumulator>)other;
        try
        {
            this.Accumulator = checked(this.Accumulator + source.Accumulator);
        }
        catch (OverflowException)
        {
            return false;
        }

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

/// <summary>
/// The exact-numeric accumulation core: a <see cref="Decimal38"/> running total
/// carried at the promoted result type's own width, so an overflow is real's
/// own Msg 8115 rather than a backing-type limit. A <c>decimal</c> result
/// accumulates at <c>decimal(38, s)</c> — the type real promotes SUM and AVG to
/// — and a <c>money</c> one at money's <c>(19, 4)</c>, which is what puts the
/// overflow of a money total on the money target.
/// </summary>
internal abstract class Decimal38Aggregator : NumericAggregatorBase
{
    /// <summary>The width the running total is carried at.</summary>
    protected readonly int Precision;

    /// <summary>The result type's scale, which the finalize step divides at.</summary>
    protected readonly int Scale;

    protected Decimal38 Accumulator;

    protected Decimal38Aggregator(SqlType resultType, bool distinct) : base(resultType, distinct)
    {
        (this.Precision, this.Scale) = resultType is DecimalSqlType declared
            ? (declared.precision, declared.scale)
            : (MoneySqlType.Precision, MoneySqlType.Scale);
        this.Accumulator = Decimal38.FromParts(UInt128.Zero, isNegative: false, this.Scale);
    }

    protected sealed override void Accumulate(SqlValue value)
    {
        if (!Decimal38.TryAdd(this.Accumulator, Extract(value), this.Precision, this.Scale, out var total))
            throw this.AccumulatorOverflow();
        this.Accumulator = total;
    }

    protected sealed override void Deduct(SqlValue value)
    {
        if (!Decimal38.TrySubtract(this.Accumulator, Extract(value), this.Precision, this.Scale, out var total))
            throw this.AccumulatorOverflow();
        this.Accumulator = total;
    }

    protected sealed override bool TryMergeTotals(NumericAggregatorBase other)
    {
        if (!Decimal38.TryAdd(this.Accumulator, ((Decimal38Aggregator)other).Accumulator, this.Precision, this.Scale, out var total))
            return false;
        this.Accumulator = total;
        return true;
    }

    public sealed override SqlValue Result()
    {
        if (this.Count == 0)
            return SqlValue.Null(this.ResultType);
        var finalized = Finalize(this.Accumulator, this.Count);
        return this.ResultType is DecimalSqlType
            ? SqlValue.FromDecimal(this.ResultType, finalized)
            : SqlValue.FromMoney(this.ResultType, finalized);
    }

    /// <summary>
    /// The operand as an exact number. A <c>money</c> operand crosses through
    /// its scaled integer; a <c>decimal</c> one is read as it stands, since the
    /// running total carries the result type's own scale and the addition
    /// aligns the operands itself.
    /// </summary>
    private static Decimal38 Extract(SqlValue value) =>
        value.Type is DecimalSqlType ? value.AsDecimal38
        : SqlType.IsMoneyCategory(value.Type) ? value.AsMoneyDecimal38
        : Decimal38.FromInt64(SqlValue.AsInt64Widened(value));

    /// <summary>SUM hands the total back unchanged; AVG divides it by the row count.</summary>
    protected abstract Decimal38 Finalize(in Decimal38 total, long count);
}
