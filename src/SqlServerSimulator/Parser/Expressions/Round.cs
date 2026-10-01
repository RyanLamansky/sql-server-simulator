using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ROUND(value, length [, function])</c>: rounds (default) or
/// truncates (any non-zero <c>function</c> argument) <c>value</c> at
/// decimal position <c>length</c>. Negative <c>length</c> rounds left of
/// the decimal point (<c>ROUND(127, -1)</c> → 130). Result type matches
/// the input — a <c>decimal(p,s)</c> input produces a <c>decimal(p,s)</c>
/// result with the same scale (the value's "rounded" portion is padded
/// with zeros). Tinyint and smallint widen to int; string-typed value
/// implicit-casts to <c>float</c> via <see cref="MathScalars.CoerceImplicit"/>.
/// Probe-confirmed against SQL Server 2025: rounding is half-away-from-zero
/// for both decimal and float inputs (NOT banker's rounding); length /
/// function args stay strict-int — Msg 8116 on string for either (the
/// <c>InvalidArgumentDataType</c> paths below). NULL on any argument
/// propagates to NULL.
/// </summary>
internal sealed class Round : Expression
{
    private readonly Expression value;
    private readonly Expression length;
    private readonly Expression? function;

    public Round(ParserContext context)
    {
        this.value = MathScalars.FloatForBareNull(Parse(context));
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.length = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is Tokens.Operator { Character: ',' })
            this.function = Parse(context.MoveNextRequiredReturnSelf());
    }

    internal override bool ParallelSafe => this.value.ParallelSafe && this.length.ParallelSafe && this.function?.ParallelSafe != false;

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.value.Run(runtime));
        var resultType = IsUntypedNullLiteral(this.length) ? SqlType.Float : MathScalars.WidenForResult(v.Type);
        if (v.IsNull) return SqlValue.Null(resultType);

        var lenValue = this.length.Run(runtime);
        if (lenValue.IsNull) return SqlValue.Null(resultType);
        // A decimal, money or float length truncates to int the way CAST does
        // (probed 2026-09-25: ROUND(12.345, 2.7) rounds to 2 places).
        // An exact-numeric length clamps to numeric's 38 digits; a float's
        // reaches as far as a double has digits (ROUND(2.5e-300, 300) is
        // 2E-300, probed 2026-10-01), past which every double is whole or zero.
        var lengthLimit = resultType.Category == SqlTypeCategory.Approximate ? 400 : Decimal38.MaxPrecision;
        var len = Math.Clamp(ScalarArguments.CoerceToInt(lenValue), -lengthLimit, lengthLimit);

        var truncate = false;
        if (this.function is not null)
        {
            // A NULL function rounds as 0 does rather than answering NULL
            // (probed 2026-09-26 against SQL Server 2025).
            var fv = this.function.Run(runtime);
            truncate = !fv.IsNull && ScalarArguments.CoerceToInt(fv) != 0;
        }

        return resultType.Category switch
        {
            SqlTypeCategory.Integer => MathScalars.PromoteInteger(resultType, RoundLong(MathScalars.AsLong(v), len, truncate)),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimal38OrMoney(resultType, RoundWithinPrecision(resultType, MathScalars.AsDecimal38OrMoney(v), len, truncate)),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(MathScalars.UnsignedZeroFromNonZero(RoundDouble(MathScalars.AsDouble(v), len, truncate), MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"ROUND doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The length and function slots are judged before the value converts
        // (probed 2026-09-26 against SQL Server 2025: ROUND(0x41, 0x41) is the
        // length's Msg 8116, not the value's Msg 206).
        ScalarArguments.RequireNumericSlot(this.length, batch, resolveColumnType, "round", 2, NumericSlot.AnyNumber);
        if (this.function is not null)
            ScalarArguments.RequireNumericSlot(this.function, batch, resolveColumnType, "round", 3, NumericSlot.AnyNumber);
        var valueType = MathScalars.WidenForResult(AssignmentRules.ArgumentType(this.value, SqlType.Float, batch, resolveColumnType));
        // A bare NULL length types the result float whatever the value (probed
        // 2026-10-01 against SQL Server 2025: ROUND(1.5, NULL) and ROUND(5,
        // NULL) are float, ROUND(1.5, CAST(NULL AS int)) numeric(2, 1)).
        return IsUntypedNullLiteral(this.length) ? SqlType.Float : valueType;
    }

    internal override bool ResultReportsNumeric => this.value.ResultReportsNumeric;

    internal override bool ResultIsNullable(NullabilityContext context) =>
        this.value.ResultIsNullable(context)
        || this.length.ResultIsNullable(context)
        || (this.function is not null && this.function.ResultIsNullable(context));

    internal override string DebugDisplay() => $"ROUND({this.value.DebugDisplay()}, {this.length.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.value).Child(this.length).Child(this.function);

    /// <summary>
    /// The rounded value settled back into the argument's own declared
    /// precision, which is what the result carries — so a carry out of it is
    /// an arithmetic overflow rather than a wider value. Probe-confirmed:
    /// <c>ROUND(CAST(7.2 AS decimal(2, 1)), -1)</c> raises Msg 8115 at state 2
    /// where the same value declared <c>decimal(3, 1)</c> answers
    /// <c>10.0</c>.
    /// </summary>
    private static Decimal38 RoundWithinPrecision(SqlType resultType, in Decimal38 value, int length, bool truncate)
    {
        var rounded = MathScalars.RoundAtPosition(value, length, truncate);
        return resultType is DecimalSqlType d && rounded.Magnitude >= Decimal38.Pow10[d.precision]
            ? throw SimulatedSqlException.ArithmeticOverflow("numeric")
            : rounded;
    }

    /// <remarks>
    /// Integer ROUND only matters for negative <paramref name="length"/>
    /// (e.g. <c>ROUND(127, -1)</c> → 130). Non-negative length on integer
    /// input is a no-op.
    /// </remarks>
    private static long RoundLong(long value, int length, bool truncate)
    {
        if (length >= 0) return value;
        var scale = Pow10Long(-length);
        if (scale == 0) return 0;
        if (truncate) return value / scale * scale;
        var half = scale / 2;
        try
        {
            var absRounded = checked((Math.Abs(value) + half) / scale * scale);
            return value < 0 ? -absRounded : absRounded;
        }
        catch (OverflowException)
        {
            throw SimulatedSqlException.ArithmeticOverflow("bigint");
        }
    }

    /// <summary>
    /// Rounds (or truncates) a <c>float</c> at <paramref name="length"/>
    /// decimal places, deciding by the double's exact binary value rather
    /// than by its scaled product: <c>ROUND(2.675e0, 2)</c> is 2.67 because
    /// 2.675 is stored as 2.67499999…, and <c>ROUND(1.45e0, 1)</c> is 1.4
    /// (probed 2026-10-01 against SQL Server 2025). The scaled product decides
    /// on its own when it lies clear of a rounding boundary; only a value
    /// within reach of one takes the exact path. The result is the double
    /// nearest the rounded decimal.
    /// </summary>
    private static double RoundDouble(double value, int length, bool truncate)
    {
        if (length >= 0)
        {
            var p = Math.Pow(10, length);
            // Past 2^52 the scaled value is already whole, so rounding leaves
            // the value as it is — where scaling it could overflow to
            // infinity (probed 2026-09-26 against SQL Server 2025:
            // ROUND(1e308, 1) is 1e308).
            var scaledUp = value * p;
            if (double.IsInfinity(scaledUp) || Math.Abs(scaledUp) >= 4503599627370496.0)
                return value;
            return ClearOfBoundary(scaledUp, truncate)
                ? (truncate ? Math.Truncate(scaledUp) : Math.Round(scaledUp, MidpointRounding.AwayFromZero)) / p
                : RoundExactly(value, length, truncate);
        }
        // A double's magnitude stays below 10^309, so it rounds to zero there.
        if (length < -308)
            return 0;
        var scale = Math.Pow(10, -length);
        var scaled = value / scale;
        if (!ClearOfBoundary(scaled, truncate))
            return RoundExactly(value, length, truncate);
        var rounded = truncate ? Math.Truncate(scaled) : Math.Round(scaled, MidpointRounding.AwayFromZero);
        return rounded * scale;
    }

    /// <summary>
    /// Whether <paramref name="scaled"/>'s fraction is far enough from the
    /// boundary the operation turns on — one half when rounding, a whole
    /// number when truncating — that the error the scaling put into it can't
    /// move it across.
    /// </summary>
    private static bool ClearOfBoundary(double scaled, bool truncate)
    {
        var fraction = Math.Abs(scaled - Math.Truncate(scaled));
        var margin = Math.Max(1e-9, Math.Abs(scaled) * 1e-12);
        return truncate
            ? fraction > margin && 1 - fraction > margin
            : Math.Abs(fraction - 0.5) > margin;
    }

    /// <summary>
    /// <see cref="RoundDouble"/>'s exact path: the double as the fraction
    /// <c>m·2^e</c> scaled by <c>10^length</c>, rounded half away from zero (or
    /// truncated) as an integer, and read back as the nearest double.
    /// </summary>
    private static double RoundExactly(double value, int length, bool truncate)
    {
        if (value == 0 || !double.IsFinite(value))
            return value;
        var bits = BitConverter.DoubleToInt64Bits(value);
        var exponentBits = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponentBits == 0)
            exponentBits = 1;
        else
            mantissa |= 1L << 52;
        var exponent = exponentBits - 1075;
        var numerator = new System.Numerics.BigInteger(mantissa);
        var denominator = System.Numerics.BigInteger.One;
        if (exponent >= 0)
            numerator <<= exponent;
        else
            denominator <<= -exponent;
        if (length >= 0)
            numerator *= System.Numerics.BigInteger.Pow(10, length);
        else
            denominator *= System.Numerics.BigInteger.Pow(10, -length);
        var whole = System.Numerics.BigInteger.DivRem(numerator, denominator, out var remainder);
        if (!truncate && remainder * 2 >= denominator)
            whole += 1;
        var magnitude = double.Parse($"{whole}E{-length}", System.Globalization.CultureInfo.InvariantCulture);
        return value < 0 ? -magnitude : magnitude;
    }

    private static long Pow10Long(int exponent)
    {
        long result = 1;
        for (var i = 0; i < exponent && result <= long.MaxValue / 10; i++)
            result *= 10;
        return result;
    }
}
