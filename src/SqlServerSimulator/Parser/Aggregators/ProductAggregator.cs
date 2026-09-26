using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Aggregators;

/// <summary>
/// Backs SQL Server 2025's <c>PRODUCT</c>: the product of the group's
/// non-NULL values, NULL over none. Integer operands multiply at the result
/// type's width (<c>int</c> for <c>tinyint</c> / <c>smallint</c> / <c>int</c>,
/// else <c>bigint</c>), <c>real</c> / <c>float</c> in <see cref="double"/>,
/// money at money's four places, and a decimal at <c>decimal(38, 6)</c> —
/// <c>decimal(38, 0)</c> for a scale-0 operand — rounding at each step; a
/// product past the result type is Msg 8115 state 2 naming it (<c>numeric</c>
/// for a decimal), or NULL under <c>ANSI_WARNINGS OFF</c> (probed 2026-09-26
/// against SQL Server 2025).
/// </summary>
internal sealed class ProductAggregator(SqlType resultType, bool distinct) : Aggregator
{
    private readonly HashSet<SqlValue>? seen = distinct ? [] : null;
    private bool any;
    private bool overflowed;
    private long integer;
    private double approximate;
    private Decimal38 exact;

    /// <summary>The batch whose session decides whether an overflow answers NULL.</summary>
    public BatchContext? Batch;

    public override void Add(SqlValue value)
    {
        if (value.IsNull || this.overflowed || (this.seen is not null && !this.seen.Add(value)))
            return;
        try
        {
            this.Accumulate(value);
            this.any = true;
        }
        catch (SimulatedSqlException error) when (this.Batch?.AbsorbsArithmeticFault(error) == true)
        {
            this.overflowed = true;
        }
    }

    private void Accumulate(SqlValue value)
    {
        if (resultType == SqlType.Int32 || resultType == SqlType.BigInt)
        {
            var factor = value.CoerceTo(SqlType.BigInt).AsInt64;
            long product;
            try
            {
                product = this.any ? checked(this.integer * factor) : factor;
            }
            catch (OverflowException)
            {
                throw SimulatedSqlException.ArithmeticOverflow(resultType.ToString()!);
            }
            if (resultType == SqlType.Int32 && product is > int.MaxValue or < int.MinValue)
                throw SimulatedSqlException.ArithmeticOverflow(resultType.ToString()!);
            this.integer = product;
        }
        else if (resultType == SqlType.Float)
        {
            var factor = value.CoerceTo(SqlType.Float).AsDouble;
            this.approximate = this.any ? this.approximate * factor : factor;
        }
        else
        {
            var isMoney = resultType == SqlType.Money;
            var factor = isMoney ? value.CoerceTo(SqlType.Money).AsMoneyDecimal38 : value.AsDecimal38;
            var (precision, scale) = isMoney ? (19, 4) : (38, ((DecimalSqlType)resultType).scale);
            var start = this.any ? this.exact : Decimal38.One;
            if (!Decimal38.TryMultiply(start, factor, precision, scale, out var product))
                throw SimulatedSqlException.ArithmeticOverflow(isMoney ? "money" : "numeric");
            this.exact = product;
        }
    }

    public override SqlValue Result() =>
        !this.any || this.overflowed ? SqlValue.Null(resultType)
        : resultType == SqlType.Int32 ? SqlValue.FromInt32((int)this.integer)
        : resultType == SqlType.BigInt ? SqlValue.FromInt64(this.integer)
        : resultType == SqlType.Float ? SqlValue.FromDouble(this.approximate)
        : resultType == SqlType.Money ? SqlValue.FromMoney(resultType, this.exact)
        : SqlValue.FromDecimal(resultType, this.exact);
}
