using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs <c>AVG</c>. SQL Server's documented behavior: integer types
/// truncate (<c>AVG(int)</c> → int via integer division); decimal types
/// promote to <c>decimal(38, max(s, 6))</c>; float passes through and real
/// widens to it; both money types report <c>money</c>.
/// Internally accumulates the same way <see cref="SumAggregator"/>
/// does, then divides by count at finalize time. Empty / all-NULL input
/// → NULL of result type. SQL Server's int-AVG also detects intermediate-
/// sum overflow (Msg 8115) — the simulator does the same.
/// </summary>
internal static class AverageAggregator
{
    public static Aggregator Create(SqlType resultType, bool distinct) => resultType switch
    {
        var t when t == SqlType.Int32 || t == SqlType.BigInt => new LongAvg(resultType, distinct),
        var t when t == SqlType.Float => new DoubleAvg(resultType, distinct),
        var t when t == SqlType.Money || t is DecimalSqlType => new DecimalAvg(resultType, distinct),
        _ => throw new NotSupportedException($"AVG not supported for {resultType}."),
    };

    private sealed class LongAvg(SqlType resultType, bool distinct) : NumericAggregator<long>(resultType, distinct)
    {
        protected override long Extract(SqlValue value) => this.ResultType == SqlType.BigInt ? value.AsInt64 : value.AsInt32;

        protected override long Finalize(long total, long count) =>
            this.ResultType == SqlType.Int32 ? checked((int)total) / count : total / count;

        protected override SqlValue Wrap(long value, SqlType type) =>
            type == SqlType.Int32 ? SqlValue.FromInt32((int)value) : SqlValue.FromInt64(value);
    }

    private sealed class DecimalAvg(SqlType resultType, bool distinct) : NumericAggregator<decimal>(resultType, distinct)
    {
        protected override decimal Extract(SqlValue value) =>
            value.Type == SqlType.Money ? value.AsMoney : value.AsDecimal;

        protected override decimal Finalize(decimal total, long count) => total / count;

        protected override SqlValue Wrap(decimal value, SqlType type) =>
            type == SqlType.Money ? SqlValue.FromMoney(type, value) : SqlValue.FromDecimal(type, value);
    }

    private sealed class DoubleAvg(SqlType resultType, bool distinct) : NumericAggregator<double>(resultType, distinct)
    {
        protected override double Extract(SqlValue value) => value.AsDouble;

        protected override double Finalize(double total, long count) => total / count;

        protected override SqlValue Wrap(double value, SqlType type) => SqlValue.FromDouble(value);
    }
}
