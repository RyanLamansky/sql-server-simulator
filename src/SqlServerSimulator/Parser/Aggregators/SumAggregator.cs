using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>SUM</c>. Result types follow SQL Server's rules
/// (see <see cref="Expressions.AggregateExpression"/>'s GetSqlType): integer
/// types accumulate at the result-type's width with overflow detection
/// (Msg 8115); decimal and money types accumulate as
/// <see cref="Decimal38"/> at the promoted result type, so a total past
/// <c>decimal(38, s)</c> raises real's own Msg 8115 state 2. float and real
/// alike sum in .NET <see cref="double"/> — real's result type is float, and
/// each single widens exactly on the way in — with infinity / NaN as
/// observed. Empty input or all-NULL input → NULL of result type.
/// DISTINCT dedups via <see cref="HashSet{SqlValue}"/> on the operand
/// before accumulation.
/// </summary>
internal static class SumAggregator
{
    /// <summary>
    /// Picks the right numeric-typed sum aggregator for the resolved result
    /// type. Centralizes the operand-family → accumulator dispatch so the
    /// Aggregator factory doesn't repeat the switch.
    /// </summary>
    public static Aggregator Create(SqlType resultType, bool distinct) => resultType switch
    {
        var t when t == SqlType.Int32 || t == SqlType.BigInt => new LongSum(resultType, distinct),
        var t when t == SqlType.Float => new DoubleSum(resultType, distinct),
        var t when t == SqlType.Money || t is DecimalSqlType => new DecimalSum(resultType, distinct),
        _ => throw new NotSupportedException($"SUM not supported for {resultType}."),
    };

    private sealed class LongSum(SqlType resultType, bool distinct) : NumericAggregator<long>(resultType, distinct)
    {
        protected override long Extract(SqlValue value) => this.ResultType == SqlType.BigInt ? value.AsInt64 : value.AsInt32;

        protected override long Finalize(long total, long count) => total;

        protected override SqlValue Wrap(long value, SqlType type) => type == SqlType.Int32
            ? value is > int.MaxValue or < int.MinValue
                ? throw SimulatedSqlException.ArithmeticOverflow(type.ToString()!)
                : SqlValue.FromInt32((int)value)
            : SqlValue.FromInt64(value);
    }

    private sealed class DecimalSum(SqlType resultType, bool distinct) : Decimal38Aggregator(resultType, distinct)
    {
        protected override Decimal38 Finalize(in Decimal38 total, long count) => total;
    }

    private sealed class DoubleSum(SqlType resultType, bool distinct) : NumericAggregator<double>(resultType, distinct)
    {
        protected override double Extract(SqlValue value) => value.AsDouble;

        protected override double Finalize(double total, long count) => total;

        protected override SqlValue Wrap(double value, SqlType type) => SqlValue.FromDouble(value);
    }
}
