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
        SqlTypeCategory.Decimal => (double)v.AsDecimal,
        SqlTypeCategory.Money => (double)v.AsMoney,
        SqlTypeCategory.Approximate => v.Type == SqlType.Float ? v.AsDouble : v.AsSingle,
        _ => throw new NotSupportedException($"Math function doesn't accept {v.Type} operand.")
    };

    /// <summary>
    /// Decimal-or-money read accessor: routes <c>decimal</c> / <c>numeric</c>
    /// through <see cref="SqlValue.AsDecimal"/> and <c>money</c> /
    /// <c>smallmoney</c> through <see cref="SqlValue.AsMoney"/> (which
    /// converts the scaled-int storage). Both return a <see cref="decimal"/>.
    /// </summary>
    public static decimal AsDecimalOrMoney(SqlValue v) => v.Type.Category == SqlTypeCategory.Money ? v.AsMoney : v.AsDecimal;

    /// <summary>
    /// Decimal-or-money write helper: dispatches to
    /// <see cref="SqlValue.FromMoney"/> when <paramref name="resultType"/>
    /// is money / smallmoney, otherwise <see cref="SqlValue.FromDecimal"/>.
    /// Lets each math-scalar function emit a single line for the
    /// <c>Decimal</c>-or-<c>Money</c> arm of its dispatch.
    /// </summary>
    public static SqlValue FromDecimalOrMoney(SqlType resultType, decimal value) =>
        resultType.Category == SqlTypeCategory.Money
            ? SqlValue.FromMoney(resultType, value)
            : SqlValue.FromDecimal(resultType, value);
}
