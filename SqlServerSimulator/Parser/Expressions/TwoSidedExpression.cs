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

    public sealed override SqlValue Run(RuntimeContext runtime)
        => Run(left.Run(runtime), right.Run(runtime));

    protected abstract SqlValue Run(SqlValue left, SqlValue right);

    public sealed override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) =>
        SqlType.PromoteForArithmetic(left.GetSqlType(resolveColumnType), right.GetSqlType(resolveColumnType), this.Operator);

    /// <summary>
    /// Numeric binary-operator dispatcher. Despite the name, this is the
    /// entry point for all numeric arithmetic — the SQL Server precedence
    /// chart routes <c>float &gt; decimal &gt; money &gt; integer</c>, and
    /// each family runs in its own helper. Dispatch is structured as an
    /// outer switch on the left operand's <see cref="SqlTypeCategory"/>
    /// with each arm a switch on the right operand's category, keeping the
    /// hot path one byte-comparison deep and jump-table-friendly.
    /// </summary>
    /// <remarks>
    /// Cross-category integer ↔ string is normalized at the top: the string
    /// operand parses to the integer side's specific type (<c>tinyint + '3'</c>
    /// stays tinyint, <c>bigint + '3'</c> stays bigint — verified against
    /// SQL Server 2025), so the rest of the dispatcher stays integer ↔
    /// integer. Bit is the sole exception: bit + string raises Msg 402 (for
    /// <c>+ - %</c>) or Msg 8117 (for <c>* /</c>) without parsing, mirroring
    /// SQL Server's same treatment of bit arithmetic with another bit and
    /// matching the bitwise-operator restrictions on strings (which also
    /// fail rather than coerce).
    /// </remarks>
    private protected static SqlValue IntegerArithmetic(SqlValue left, SqlValue right, char op, Func<long, long, long> compute)
    {
        if (left.Type.Category == SqlTypeCategory.Integer && right.Type.Category == SqlTypeCategory.String && op is not '&' and not '|' and not '^')
        {
            if (left.Type == SqlType.Bit)
                throw BitWithStringArithmetic(left.Type, right.Type, op);
            right = right.IsNull ? SqlValue.Null(left.Type) : right.CoerceTo(left.Type);
        }
        else if (left.Type.Category == SqlTypeCategory.String && right.Type.Category == SqlTypeCategory.Integer && op is not '&' and not '|' and not '^')
        {
            if (right.Type == SqlType.Bit)
                throw BitWithStringArithmetic(left.Type, right.Type, op);
            left = left.IsNull ? SqlValue.Null(right.Type) : left.CoerceTo(right.Type);
        }

        return left.Type.Category switch
        {
            SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
            SqlTypeCategory.Decimal => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal or SqlTypeCategory.Integer or SqlTypeCategory.Money => DecimalArithmetic(left, right, op),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            SqlTypeCategory.Money => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal => DecimalArithmetic(left, right, op),
                SqlTypeCategory.Money or SqlTypeCategory.Integer => MoneyArithmetic(left, right, op),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            SqlTypeCategory.Integer => right.Type.Category switch
            {
                SqlTypeCategory.Approximate => ApproximateArithmetic(left, right, op),
                SqlTypeCategory.Decimal => DecimalArithmetic(left, right, op),
                SqlTypeCategory.Money => MoneyArithmetic(left, right, op),
                SqlTypeCategory.Integer => PureIntegerArithmetic(left, right, compute),
                _ => throw UnsupportedNumericPair(left, right, op),
            },
            _ => throw UnsupportedNumericPair(left, right, op),
        };
    }

    private static SimulatedSqlException BitWithStringArithmetic(SqlType left, SqlType right, char op) =>
        op is '*' or '/'
            ? SimulatedSqlException.OperandDataTypeInvalid(left, OperatorWord(op))
            : SimulatedSqlException.IncompatibleDataTypesInOperator(left, right, OperatorWord(op));

    private static string OperatorWord(char op) => op switch
    {
        '+' => "add",
        '-' => "subtract",
        '*' => "multiply",
        '/' => "divide",
        '%' => "modulo",
        _ => op.ToString(),
    };

    /// <summary>
    /// Integer-only path: both sides are guaranteed integer-category.
    /// Promotes to SQL Server's common integer type, runs the compute
    /// callback in <c>long</c> arithmetic, and narrows the result back to
    /// the common type. NULL propagates.
    /// </summary>
    private static SqlValue PureIntegerArithmetic(SqlValue left, SqlValue right, Func<long, long, long> compute)
    {
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

    private static NotSupportedException UnsupportedNumericPair(SqlValue left, SqlValue right, char op) =>
        new($"Operator '{op}' currently supports only integer operands; got {left.Type} and {right.Type}.");

    /// <summary>
    /// Decimal arithmetic. The result-type computation is delegated to
    /// <see cref="SqlType.PromoteForArithmetic"/> (which encodes the same
    /// per-operator scale formulas verified against SQL Server 2025), so
    /// the static <see cref="GetSqlType"/> path and this runtime path
    /// always agree on the schema. Integer / money operands canonicalize
    /// to their decimal equivalent before the .NET-decimal compute step.
    /// </summary>
    private protected static SqlValue DecimalArithmetic(SqlValue left, SqlValue right, char op)
    {
        var resultType = (DecimalSqlType)SqlType.PromoteForArithmetic(left.Type, right.Type, op);
        var resultPrecision = (int)resultType.precision;
        var resultScale = (int)resultType.scale;

        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = ToDecimal(left);
        var r = ToDecimal(right);

        decimal raw;
        try
        {
            raw = op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                '/' => l / r,
                '%' => l % r,
                _ => throw new NotSupportedException($"Operator '{op}'."),
            };
        }
        catch (DivideByZeroException)
        {
            throw SimulatedSqlException.DivideByZero();
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflowToNumeric();
        }

        // .NET decimal.Round caps at 28 fractional digits; skip when the
        // result-type scale is wider (the value can't have more fractional
        // bits than .NET decimal stores).
        var rounded = resultScale > 28 ? raw : decimal.Round(raw, resultScale, MidpointRounding.AwayFromZero);
        // Final overflow check against the result type's declared precision.
        // Cap integer-digit count at 28 for the same .NET-decimal-range
        // reason — values that fit .NET decimal can't exceed 28 integer
        // digits, so the overflow doesn't fire spuriously for high-precision
        // result types.
        var integerDigits = Math.Min(28, resultPrecision - resultScale);
        var maxIntegerPart = Pow10Decimal(integerDigits) - 1;
        return integerDigits < 28 && decimal.Abs(decimal.Truncate(rounded)) > maxIntegerPart
            ? throw SimulatedSqlException.ArithmeticOverflowToNumeric()
            : SqlValue.FromDecimal(resultType, rounded);
    }

    private static decimal ToDecimal(SqlValue v) =>
        v.Type is DecimalSqlType ? v.AsDecimal
        : SqlType.IsMoneyCategory(v.Type) ? v.AsMoney
        : SqlValue.AsInt64Widened(v);

    private static decimal Pow10Decimal(int n)
    {
        var result = 1m;
        for (var i = 0; i < n; i++)
            result *= 10m;
        return result;
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
    /// Float / real arithmetic. Both sides convert to <see cref="double"/>;
    /// result is <c>float</c> unless both operands were <c>real</c>, in
    /// which case it stays <c>real</c>. Divide-by-zero raises Msg 8134 to
    /// match the decimal path; native IEEE infinities/NaN aren't surfaced
    /// (real SQL Server raises 8134 for divide-by-zero on float too,
    /// verified earlier).
    /// </summary>
    /// <summary>
    /// Money / smallmoney arithmetic. Result stays in money when both sides
    /// are money or when one side is integer (verified <c>$5 + $3 → money</c>,
    /// <c>$5 * 3 → money</c>). Same-money-pair preserves the wider of the
    /// two; mixed money / smallmoney widens to money. Math runs on the
    /// underlying decimal values; the result re-rounds half-away-from-zero
    /// to scale 4 inside <see cref="SqlValue.FromMoney"/>.
    /// </summary>
    private protected static SqlValue MoneyArithmetic(SqlValue left, SqlValue right, char op)
    {
        var resultType = SqlType.IsMoneyCategory(left.Type) && SqlType.IsMoneyCategory(right.Type)
            ? (left.Type == SqlType.Money || right.Type == SqlType.Money ? SqlType.Money : SqlType.SmallMoney)
            : (SqlType.IsMoneyCategory(left.Type) ? left.Type : right.Type);
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = MoneyOrIntegerToDecimal(left);
        var r = MoneyOrIntegerToDecimal(right);
        decimal raw;
        try
        {
            raw = op switch
            {
                '+' => l + r,
                '-' => l - r,
                '*' => l * r,
                '/' => r == 0m ? throw SimulatedSqlException.DivideByZero() : l / r,
                '%' => r == 0m ? throw SimulatedSqlException.DivideByZero() : l % r,
                _ => throw new NotSupportedException($"Operator '{op}' on money operands isn't implemented."),
            };
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflowToTarget(resultType.ToString()!);
        }
        return SqlValue.FromMoney(resultType, raw);
    }

    private static decimal MoneyOrIntegerToDecimal(SqlValue v) =>
        SqlType.IsMoneyCategory(v.Type) ? v.AsMoney : SqlValue.AsInt64Widened(v);

    private protected static SqlValue ApproximateArithmetic(SqlValue left, SqlValue right, char op)
    {
        var resultIsReal = left.Type == SqlType.Real && right.Type == SqlType.Real;
        SqlType resultType = resultIsReal ? SqlType.Real : SqlType.Float;
        if (left.IsNull || right.IsNull)
            return SqlValue.Null(resultType);

        var l = ToDouble(left);
        var r = ToDouble(right);
        var raw = op switch
        {
            '+' => l + r,
            '-' => l - r,
            '*' => l * r,
            '/' => r == 0.0 ? throw SimulatedSqlException.DivideByZero() : l / r,
            '%' => r == 0.0 ? throw SimulatedSqlException.DivideByZero() : l % r,
            _ => throw new NotSupportedException($"Operator '{op}' on float operands isn't implemented."),
        };
        return resultIsReal ? SqlValue.FromSingle((float)raw) : SqlValue.FromDouble(raw);
    }

    private static double ToDouble(SqlValue v) =>
        v.Type == SqlType.Float ? v.AsDouble
        : v.Type == SqlType.Real ? v.AsSingle
        : v.Type is DecimalSqlType ? (double)v.AsDecimal
        : SqlType.IsMoneyCategory(v.Type) ? (double)v.AsMoney
        : SqlValue.AsInt64Widened(v);

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

    internal sealed override string DebugDisplay() => $"{left.DebugDisplay()} {Operator} {right.DebugDisplay()}";
}
