using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

internal abstract class TwoSidedExpression(Expression left, ParserContext context) : Expression
{
    private Expression left = left, right = Parse(context.MoveNextRequiredReturnSelf());

    public TwoSidedExpression AdjustForPrecedence()
    {
        if (this.right is not TwoSidedExpression rightTwo || rightTwo.Precedence < this.Precedence)
            return this;

        (rightTwo.left, this.right) = (this, rightTwo.left);
        return rightTwo;
    }

    public sealed override SqlValue Run(Func<List<string>, SqlValue> getColumnValue)
        => Run(left.Run(getColumnValue), right.Run(getColumnValue));

    protected abstract SqlValue Run(SqlValue left, SqlValue right);

    public sealed override SqlType GetSqlType(Func<List<string>, SqlType> resolveColumnType) =>
        SqlType.Promote(left.GetSqlType(resolveColumnType), right.GetSqlType(resolveColumnType));

    /// <summary>
    /// Evaluates an integer-family binary operation, promoting both operands to
    /// SQL Server's common integer type before computing in <c>long</c> arithmetic
    /// and narrowing the result back to that common type. NULL propagates.
    /// </summary>
    private protected static SqlValue IntegerArithmetic(SqlValue left, SqlValue right, char op, Func<long, long, long> compute)
    {
        if (!SqlType.IsIntegerCategory(left.Type) || !SqlType.IsIntegerCategory(right.Type))
            throw new NotSupportedException($"Operator '{op}' currently supports only integer operands; got {left.Type} and {right.Type}.");

        var common = SqlType.Promote(left.Type, right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(common);

        var result = compute(ToInt64(left), ToInt64(right));
        return common == SqlType.Bit ? SqlValue.FromBoolean(result != 0)
            : common == SqlType.TinyInt ? SqlValue.FromByte((byte)result)
            : common == SqlType.SmallInt ? SqlValue.FromInt16((short)result)
            : common == SqlType.Int32 ? SqlValue.FromInt32((int)result)
            : SqlValue.FromInt64(result);
    }

    private protected static long ToInt64(SqlValue v) =>
        v.Type == SqlType.Bit ? (v.AsBoolean ? 1L : 0L)
        : v.Type == SqlType.TinyInt ? v.AsByte
        : v.Type == SqlType.SmallInt ? v.AsInt16
        : v.Type == SqlType.Int32 ? v.AsInt32
        : v.AsInt64;

    /// <summary>
    /// Dispatcher for <c>+</c> / <c>-</c>: routes integer×integer to
    /// <see cref="IntegerArithmetic"/> and any pair involving a date/time
    /// type to the date-arithmetic path. Date arithmetic only supports the
    /// legacy <c>datetime</c> and <c>smalldatetime</c> types; non-legacy
    /// operands raise Msg 402 / 8117 (per SQL Server's exact rules).
    /// </summary>
    private protected static SqlValue AdditiveArithmetic(SqlValue left, SqlValue right, char op, string operatorName, Func<long, long, long> compute) =>
        SqlType.IsDateTimeCategory(left.Type) || SqlType.IsDateTimeCategory(right.Type)
            ? DateAdditiveArithmetic(left, right, operatorName, compute)
            : IntegerArithmetic(left, right, op, compute);

    /// <summary>
    /// Date arithmetic for <c>+</c> / <c>-</c>: works only when both
    /// operands resolve to a legacy datetime tick offset (i.e. each side is
    /// either an integer treated as days-since-1900-01-01, or a
    /// <c>datetime</c>/<c>smalldatetime</c> value). Result is rendered as
    /// the higher-precedence date type (datetime > smalldatetime). NULL
    /// propagates. Three error variants:
    /// <list type="bullet">
    /// <item>Both non-legacy date types (e.g. <c>date + date</c>,
    /// <c>dt2 + date</c>) → Msg 8117 with the left operand's type;</item>
    /// <item>One legacy and one non-legacy date type (e.g. <c>dt + date</c>)
    /// → Msg 402 with both names and the operator;</item>
    /// <item>Non-legacy date + integer (e.g. <c>date + 1</c>) → Msg 206
    /// from <see cref="SqlType.Promote"/>'s integer-vs-non-legacy rule.</item>
    /// </list>
    /// Out-of-range arithmetic results raise Msg 8115 with the result type
    /// name (matching the int→datetime overflow path).
    /// </summary>
    private static SqlValue DateAdditiveArithmetic(SqlValue left, SqlValue right, string operatorName, Func<long, long, long> compute)
    {
        var leftIsLegacy = left.Type == SqlType.DateTime || left.Type == SqlType.SmallDateTime;
        var rightIsLegacy = right.Type == SqlType.DateTime || right.Type == SqlType.SmallDateTime;
        var leftIsNonLegacyDateTime = SqlType.IsDateTimeCategory(left.Type) && !leftIsLegacy;
        var rightIsNonLegacyDateTime = SqlType.IsDateTimeCategory(right.Type) && !rightIsLegacy;

        // Both non-legacy date types — including different-non-legacy pairs
        // like `date + dt2`. SQL Server reports just the left operand's type
        // in Msg 8117, so we don't need both names.
        if (leftIsNonLegacyDateTime && rightIsNonLegacyDateTime)
            throw SimulatedSqlException.OperandDataTypeInvalid(left.Type, operatorName);

        // One legacy, one non-legacy date type — e.g. `dt + date`, `dt2 + dt`.
        if ((leftIsLegacy && rightIsNonLegacyDateTime) || (leftIsNonLegacyDateTime && rightIsLegacy))
            throw SimulatedSqlException.IncompatibleDataTypesInOperator(left.Type, right.Type, operatorName);

        // Promote handles the remaining cases: legacy×legacy, legacy×int,
        // int×non-legacy (which throws Msg 206 from inside Promote).
        var common = SqlType.Promote(left.Type, right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(common);

        long resultTicks;
        try
        {
            resultTicks = checked(compute(TicksFromBase(left), TicksFromBase(right)));
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow(common.ToString()!);
        }

        return common == SqlType.SmallDateTime
            ? SqlValue.CoerceTicksSinceBaseToSmallDateTime(resultTicks)
            : SqlValue.CoerceTicksSinceBaseToDateTime(resultTicks);
    }

    /// <summary>
    /// Resolves an arithmetic operand to ticks measured from 1900-01-01.
    /// Integer operands treat the value as a whole-day count
    /// (multiplied by <see cref="TimeSpan.TicksPerDay"/> with overflow
    /// checking — bigint × TicksPerDay can exceed <see cref="long"/>);
    /// legacy date types subtract their base-date ticks. Caller must have
    /// already filtered out non-legacy date types.
    /// </summary>
    private static long TicksFromBase(SqlValue v) =>
        SqlType.IsIntegerCategory(v.Type) ? checked(SqlValue.AsInt64Widened(v) * TimeSpan.TicksPerDay)
        : v.Type == SqlType.DateTime ? v.AsDateTime.Ticks - new DateTime(1900, 1, 1).Ticks
        : v.Type == SqlType.SmallDateTime ? v.AsSmallDateTime.Ticks - new DateTime(1900, 1, 1).Ticks
        : throw new InvalidOperationException($"TicksFromBase received unexpected type {v.Type}.");

    protected abstract char Operator { get; }

#if DEBUG
    public sealed override string ToString() => $"{left} {Operator} {right}";
#endif
}
