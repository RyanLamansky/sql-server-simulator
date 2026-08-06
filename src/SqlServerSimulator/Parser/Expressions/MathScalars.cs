using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Shared helpers for the math scalar expressions (<see cref="AbsoluteValue"/>,
/// <see cref="Floor"/>, <see cref="Ceiling"/>, <see cref="Round"/>,
/// <see cref="Power"/>, <see cref="Sqrt"/>, <see cref="Sign"/>,
/// <see cref="Log"/>, <see cref="Exp"/>, <see cref="Log10"/>). Centralizes the
/// type-widening rule and the integer / decimal / double accessors that
/// each function dispatches through.
/// </summary>
internal static class MathScalars
{
    /// <summary>
    /// Probe-confirmed against SQL Server 2025 (2026-05-09) via
    /// <c>SELECT INTO #t</c> + <c>tempdb.information_schema.columns</c>:
    /// <c>tinyint</c> / <c>smallint</c> widen to <c>int</c>;
    /// <c>smallmoney</c> widens to <c>money</c>; <c>real</c> widens to
    /// <c>float</c>; <c>bit</c> widens to <c>float</c> (sic — same rule
    /// across <c>ABS</c> / <c>FLOOR</c> / <c>CEILING</c> / <c>SIGN</c> /
    /// <c>ROUND</c>). String inputs implicit-cast to <c>float</c>
    /// (probe-confirmed 2026-05-22: <c>ABS('-5.5')</c> /
    /// <c>CEILING('5.5')</c> / <c>FLOOR('5.5')</c> / <c>SIGN('-5')</c> all
    /// emit float; the function result type is float regardless of the
    /// source numeric form). Everything else preserves its input type.
    /// </summary>
    public static SqlType WidenForResult(SqlType input) =>
        input == SqlType.TinyInt || input == SqlType.SmallInt ? SqlType.Int32
        : input == SqlType.Bit || input == SqlType.Real ? SqlType.Float
        : input == SqlType.SmallMoney ? SqlType.Money
        : SqlType.IsStringCategory(input) ? SqlType.Float
        : input;

    /// <summary>
    /// Result type for <c>CEILING</c> / <c>FLOOR</c>: the input's post-widen
    /// type, except an exact-numeric (<c>decimal</c> / <c>numeric</c>) input
    /// keeps its precision but drops to scale 0 — the result is integer-valued.
    /// Probe-confirmed against SQL Server 2025 (2026-07-22):
    /// <c>CEILING(1.1)</c> → <c>numeric(2, 0)</c>,
    /// <c>CEILING(123.456)</c> → <c>numeric(6, 0)</c>,
    /// <c>CEILING(CAST(1 AS decimal(38,10)))</c> → <c>decimal(38, 0)</c>;
    /// <c>money</c> stays <c>money</c>, <c>float</c> stays <c>float</c>,
    /// <c>int</c> stays <c>int</c>. (The simulator has no <c>numeric</c>-vs-
    /// <c>decimal</c> name distinction — it reports <c>decimal</c> either way;
    /// only the precision / scale are matched.)
    /// </summary>
    public static SqlType FloorCeilingResult(SqlType input)
    {
        var widened = WidenForResult(input);
        return widened is DecimalSqlType d ? SqlType.GetDecimal(d.precision, 0) : widened;
    }

    /// <summary>
    /// Result type for <c>POWER</c>: the base's post-widen type, except an
    /// exact-numeric (<c>decimal</c> / <c>numeric</c>) base widens its
    /// precision to 38 while keeping its scale — so the result can hold the
    /// exponentiated magnitude. Probe-confirmed against SQL Server 2025
    /// (2026-07-22): <c>POWER(2.0, 10)</c> → <c>numeric(38, 1)</c>,
    /// <c>POWER(2.00, 10)</c> → <c>numeric(38, 2)</c>,
    /// <c>POWER(CAST(2 AS decimal(5,3)), 10)</c> → <c>decimal(38, 3)</c>;
    /// <c>money</c> stays <c>money</c>, <c>int</c> stays <c>int</c>,
    /// <c>bigint</c> stays <c>bigint</c> (<c>tinyint</c> / <c>smallint</c>
    /// widen to <c>int</c>), and a <c>float</c> / <c>real</c> base → <c>float</c>.
    /// </summary>
    public static SqlType PowerResult(SqlType baseType)
    {
        var widened = WidenForResult(baseType);
        return widened is DecimalSqlType d ? SqlType.GetDecimal(38, d.scale) : widened;
    }

    /// <summary>
    /// Applies SQL Server's implicit string-to-float cast for math scalar
    /// inputs. String operands route through
    /// <see cref="SqlValue.CoerceTo"/> targeting <c>float</c> (Msg 8114
    /// from the string-to-float parser on bad strings); non-string
    /// operands pass through unchanged. Probe-confirmed 2026-05-22 against
    /// SQL Server 2025: <c>ABS('-5')</c>, <c>SQRT('16')</c>,
    /// <c>DEGREES(N'1')</c>, <c>SIGN('0')</c>, etc. all accept varchar /
    /// nvarchar / nchar / char input.
    /// </summary>
    public static SqlValue CoerceImplicit(SqlValue value) =>
        SqlType.IsStringCategory(value.Type) ? value.CoerceTo(SqlType.Float) : value;

    /// <summary>
    /// Returns <paramref name="applied"/> typed at <paramref name="resultType"/>
    /// — the post-widen integer category. Used by type-preserving math
    /// (<c>FLOOR</c>, <c>CEILING</c>, <c>ROUND</c>, <c>SIGN</c>) when the
    /// input is an integer category.
    /// </summary>
    public static SqlValue PromoteInteger(SqlType resultType, long applied) =>
        resultType == SqlType.BigInt ? SqlValue.FromInt64(applied) : SqlValue.FromInt32((int)applied);

    /// <summary>
    /// Returns the integer value held by <paramref name="v"/> as a long.
    /// Bit, tinyint, and smallint widen through their respective accessors.
    /// </summary>
    public static long AsLong(SqlValue v) =>
        v.Type == SqlType.Bit ? (v.AsBoolean ? 1L : 0L)
        : v.Type == SqlType.TinyInt ? v.AsByte
        : v.Type == SqlType.SmallInt ? v.AsInt16
        : v.Type == SqlType.Int32 ? v.AsInt32
        : v.AsInt64;

    /// <summary>
    /// Coerces <paramref name="v"/> to a <see cref="double"/> for the
    /// always-float math functions (<c>SQRT</c>, <c>LOG</c>, <c>EXP</c>,
    /// <c>LOG10</c>) and for the float-widened input categories
    /// (<c>bit</c> → float, <c>real</c> → float). Strings aren't accepted
    /// — those raise the same type errors at the SQL Server level.
    /// </summary>
    public static double AsDouble(SqlValue v) => v.Type.Category switch
    {
        SqlTypeCategory.Integer => AsLong(v),
        SqlTypeCategory.Decimal => v.AsDecimal38.ToDouble(),
        SqlTypeCategory.Money => v.AsMoneyDecimal38.ToDouble(),
        SqlTypeCategory.Approximate => v.Type == SqlType.Float ? v.AsDouble : v.AsSingle,
        _ => throw new NotSupportedException($"Math function doesn't accept {v.Type} operand.")
    };

    /// <summary>
    /// Decimal-or-money read accessor at full width: a <c>decimal</c> /
    /// <c>numeric</c> as it stands, a <c>money</c> / <c>smallmoney</c> through
    /// its scaled-int storage at scale 4.
    /// </summary>
    public static Decimal38 AsDecimal38OrMoney(SqlValue v) =>
        v.Type.Category == SqlTypeCategory.Money ? v.AsMoneyDecimal38 : v.AsDecimal38;

    /// <summary>
    /// Decimal-or-money write helper: the result type's own scale is restored
    /// by the constructor, so a scalar that produced an integer-valued result
    /// comes back carrying the declared fractional zeros. Lets each math-scalar
    /// function emit a single line for the <c>Decimal</c>-or-<c>Money</c> arm
    /// of its dispatch.
    /// </summary>
    public static SqlValue FromDecimal38OrMoney(SqlType resultType, in Decimal38 value) =>
        resultType.Category == SqlTypeCategory.Money
            ? SqlValue.FromMoney(resultType, value)
            : SqlValue.FromDecimal(resultType, value);

    /// <summary>
    /// A <see cref="double"/>-computed result landed on an exact-numeric result
    /// type, reading the operand's exact binary value and rounding half away
    /// from zero at the declared scale. A magnitude the type can't hold is
    /// real's Msg 8115 at state 2 — the arithmetic-overflow shape, since the
    /// value came out of a computation rather than a conversion.
    /// </summary>
    public static SqlValue FromDoubleAsDecimalOrMoney(SqlType resultType, double value)
    {
        var (precision, scale) = resultType is DecimalSqlType d
            ? (d.precision, d.scale)
            : (MoneySqlType.Precision, MoneySqlType.Scale);
        return Decimal38.TryFromDouble(value, precision, scale, out var converted)
            ? FromDecimal38OrMoney(resultType, converted)
            : throw SimulatedSqlException.ArithmeticOverflow(resultType is DecimalSqlType ? "numeric" : resultType.ToString()!);
    }

    /// <summary>
    /// The smallest integer-valued number at or above <paramref name="value"/>,
    /// at scale 0 — <c>CEILING</c>'s exact-numeric arm.
    /// </summary>
    public static Decimal38 Ceiling(in Decimal38 value) => StepToInteger(value, up: true);

    /// <summary>
    /// The largest integer-valued number at or below <paramref name="value"/>,
    /// at scale 0 — <c>FLOOR</c>'s exact-numeric arm.
    /// </summary>
    public static Decimal38 Floor(in Decimal38 value) => StepToInteger(value, up: false);

    private static Decimal38 StepToInteger(in Decimal38 value, bool up)
    {
        _ = Decimal38.TryTruncate(value, Decimal38.MaxPrecision, 0, out var truncated);
        var comparison = value.CompareTo(truncated);
        if (up ? comparison <= 0 : comparison >= 0)
            return truncated;

        var stepped = up
            ? Decimal38.TryAdd(truncated, Decimal38.One, Decimal38.MaxPrecision, 0, out var moved)
            : Decimal38.TrySubtract(truncated, Decimal38.One, Decimal38.MaxPrecision, 0, out moved);
        return stepped ? moved : throw SimulatedSqlException.ArithmeticOverflow("numeric");
    }

    /// <summary>
    /// <paramref name="value"/> settled at decimal position
    /// <paramref name="length"/> — rounding half away from zero, or dropping
    /// the digits toward zero when <paramref name="truncate"/> — keeping the
    /// value's own scale, so <c>ROUND(CAST(1.5 AS decimal(10, 4)), 0)</c> is
    /// real's <c>2.0000</c> and <c>ROUND(127, -1)</c> is <c>130</c>.
    /// </summary>
    public static Decimal38 RoundAtPosition(in Decimal38 value, int length, bool truncate)
    {
        var shift = value.Scale - length;
        if (shift <= 0)
            return value;
        if (shift > Decimal38.MaxPrecision)
            return Decimal38.FromParts(UInt128.Zero, isNegative: false, value.Scale);

        // Reading the magnitude as though it carried `shift` fractional digits
        // puts the cut where the caller asked for it; the settled digits then go
        // back to the place they came from, which is what keeps the scale.
        var digits = Decimal38.FromParts(value.Magnitude, value.IsNegative, shift);
        _ = truncate
            ? Decimal38.TryTruncate(digits, Decimal38.MaxPrecision, 0, out var settled)
            : Decimal38.TryRescale(digits, Decimal38.MaxPrecision, 0, out settled);

        var magnitude = settled.Magnitude * Decimal38.Pow10[shift];
        return magnitude >= Decimal38.Pow10[Decimal38.MaxPrecision]
            ? throw SimulatedSqlException.ArithmeticOverflow("numeric")
            : Decimal38.FromParts(magnitude, value.IsNegative, value.Scale);
    }

}
