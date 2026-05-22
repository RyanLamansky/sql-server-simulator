using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ABS(numeric)</c>: returns the absolute value of the input.
/// Result type follows the shared math-scalar widening rule
/// (<see cref="MathScalars.WidenForResult"/>): tinyint / smallint widen to
/// int; smallmoney widens to money; real and bit widen to float;
/// everything else preserves the input type.
/// </summary>
/// <remarks>
/// <para>
/// Probe-confirmed against SQL Server 2025 (2026-05-09): integer overflow
/// raises Msg 8115 with the result type's family name —
/// <c>ABS(int.MinValue)</c> → <c>"Arithmetic overflow error converting expression to data type int."</c>
/// and <c>ABS(bigint.MinValue)</c> → <c>"...data type bigint."</c>. The
/// smallint case sneaks past overflow because widening to int absorbs the
/// asymmetric range (32768 fits in int even though it doesn't in smallint).
/// </para>
/// <para>
/// Decimal / money inputs never overflow on ABS — .NET <c>decimal</c>'s
/// range is symmetric, so <c>Math.Abs(decimal.MinValue) = decimal.MaxValue</c>
/// works. Float inputs likewise have symmetric range.
/// </para>
/// </remarks>
internal sealed class AbsoluteValue(ParserContext context) : Expression
{
    private readonly Expression source = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = MathScalars.CoerceImplicit(this.source.Run(runtime));
        var resultType = MathScalars.WidenForResult(v.Type);
        return v.IsNull ? SqlValue.Null(resultType) : resultType.Category switch
        {
            SqlTypeCategory.Integer => AbsInteger(MathScalars.AsLong(v), resultType),
            SqlTypeCategory.Decimal or SqlTypeCategory.Money => MathScalars.FromDecimalOrMoney(resultType, Math.Abs(MathScalars.AsDecimalOrMoney(v))),
            SqlTypeCategory.Approximate => SqlValue.FromDouble(Math.Abs(MathScalars.AsDouble(v))),
            _ => throw new NotSupportedException($"ABS doesn't support {v.Type}.")
        };
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
        => MathScalars.WidenForResult(this.source.GetSqlType(batch, resolveColumnType));

    /// <remarks>
    /// .NET's <c>Math.Abs(long)</c> throws <c>OverflowException</c> on
    /// <c>long.MinValue</c>; for an int-result widening, the long-form
    /// absolute value can also exceed <c>int.MaxValue</c> (only reachable
    /// from <c>int.MinValue</c> input post-widen-to-long). Both paths map
    /// to Msg 8115 with the result type's family name.
    /// </remarks>
    private static SqlValue AbsInteger(long value, SqlType resultType)
    {
        if (value == long.MinValue)
            throw SimulatedSqlException.ArithmeticOverflow("bigint");
        var abs = Math.Abs(value);
        return resultType == SqlType.Int32 && abs > int.MaxValue
            ? throw SimulatedSqlException.ArithmeticOverflow("int")
            : MathScalars.PromoteInteger(resultType, abs);
    }

    internal override string DebugDisplay() => $"ABS({this.source.DebugDisplay()})";
}
