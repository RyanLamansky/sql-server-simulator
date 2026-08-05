namespace SqlServerSimulator.Storage;

/// <summary>
/// The one place the simulator's <c>decimal</c> / <c>numeric</c> backing type
/// shows through. SQL Server carries 38 significant digits; a .NET
/// <see cref="decimal"/> carries 28–29, so a value real represents happily can
/// have no representation here at all.
/// <para>
/// That is a gap, not a SQL Server behavior, so it surfaces as a
/// <see cref="NotSupportedException"/> naming the ceiling rather than as a
/// <c>SimulatedSqlException</c> — reporting Msg 8114 / 8115 for it would claim
/// real rejects a statement it runs (probed 2026-08-05:
/// <c>CAST('123456789012345678901234567890' AS decimal(38, 0))</c> converts on
/// the server, and it is SqlClient that then refuses to hand the value to a
/// .NET caller).
/// </para>
/// </summary>
internal static class DecimalCeiling
{
    /// <summary>
    /// Significant digits a .NET <see cref="decimal"/> always holds. The
    /// 29-digit values above <c>decimal.MaxValue</c>'s leading 7 are why this
    /// is stated as a floor rather than a width.
    /// </summary>
    internal const int SignificantDigits = 28;

    /// <summary>
    /// Values at or above this need more than <see cref="SignificantDigits"/>
    /// integer digits, so no <see cref="decimal"/> holds them whatever their
    /// fractional part.
    /// </summary>
    internal const decimal LargestRepresentableMagnitude = 10000000000000000000000000000m;

    /// <summary>
    /// The refusal, naming the operation that reached the ceiling
    /// (<paramref name="operation"/> reads into "… while &lt;operation&gt;").
    /// </summary>
    internal static NotSupportedException Exceeded(string operation) =>
        new($"A decimal / numeric value needing more than {SignificantDigits} significant digits isn't modeled, and one arose while {operation}. "
            + "SQL Server carries 38 significant digits; the simulator stores decimal / numeric in a .NET decimal, which carries 28-29.");
}
