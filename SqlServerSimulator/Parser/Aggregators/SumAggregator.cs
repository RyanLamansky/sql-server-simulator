using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>SUM</c>. Result types follow SQL Server's rules
/// (see <see cref="Expressions.AggregateExpression"/>'s GetSqlType): integer
/// types accumulate at the result-type's width with overflow detection
/// (Msg 8115); decimal types accumulate as .NET decimal (precision 28-29
/// digits — the simulator's documented decimal limitation). float / real
/// pass through .NET <see cref="double"/> arithmetic with infinity / NaN
/// as observed. Empty input or all-NULL input → NULL of result type.
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
        var t when t == SqlType.Float || t == SqlType.Real => new DoubleSum(resultType, distinct),
        var t when t == SqlType.Money || t == SqlType.SmallMoney || t is DecimalSqlType => new DecimalSum(resultType, distinct),
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

    private sealed class DecimalSum(SqlType resultType, bool distinct) : NumericAggregator<decimal>(resultType, distinct)
    {
        protected override decimal Extract(SqlValue value) =>
            value.Type == SqlType.Money || value.Type == SqlType.SmallMoney ? value.AsMoney : value.AsDecimal;

        protected override decimal Finalize(decimal total, long count) => total;

        protected override SqlValue Wrap(decimal value, SqlType type) =>
            type == SqlType.Money || type == SqlType.SmallMoney
                ? SqlValue.FromMoney(type, value)
                : SqlValue.FromDecimal(type, value);
    }

    private sealed class DoubleSum(SqlType resultType, bool distinct) : NumericAggregator<double>(resultType, distinct)
    {
        protected override double Extract(SqlValue value) => this.ResultType == SqlType.Real ? value.AsSingle : value.AsDouble;

        protected override double Finalize(double total, long count) => total;

        protected override SqlValue Wrap(double value, SqlType type) =>
            type == SqlType.Real ? SqlValue.FromSingle((float)value) : SqlValue.FromDouble(value);
    }
}
