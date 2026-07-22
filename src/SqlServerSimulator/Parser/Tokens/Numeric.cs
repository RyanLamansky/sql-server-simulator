using SqlServerSimulator.Storage;
using System.Globalization;

namespace SqlServerSimulator.Parser.Tokens;

/// <summary>
/// A SQL numeric literal — integer (<c>123</c>), decimal (<c>123.45</c>), or
/// scientific (<c>1.5e2</c>). The constructor classifies the source span and
/// produces a typed <see cref="SqlValue"/>: integer-only digits land on
/// <see cref="SqlType.Int32"/>, fractional-without-exponent on
/// <c>decimal(p, s)</c> with <c>p = significant-digit count</c> and
/// <c>s = digits-after-decimal</c> (matching SQL Server's literal-type
/// inference, verified against SQL Server 2025: <c>100.5 → decimal(4, 1)</c>,
/// <c>0.5 → decimal(1, 1)</c>). Scientific-notation literals (e.g. <c>1e2</c>)
/// produce a <c>float</c> in SQL Server, which the simulator hasn't modeled
/// yet — those raise <see cref="NotSupportedException"/> for now.
/// </summary>
internal sealed class Numeric : Token
{
    public readonly SqlValue Value;

    /// <summary>
    /// Significant-decimal-digit count when this token is a non-negative
    /// <b>integer</b> literal (<c>int</c> / <c>bigint</c> branch), <c>0</c> for
    /// decimal / scientific literals. SQL Server types an integer literal as
    /// <c>numeric(digit_count, 0)</c> — not <c>int</c>'s fixed precision 10 —
    /// when it is unified with a decimal/numeric partner in arithmetic /
    /// <c>CASE</c> / set-op promotion (probe-confirmed against SQL Server 2025:
    /// <c>10.0/3</c> → <c>numeric(8, 6)</c>, i.e. the <c>3</c> contributes
    /// <c>(1, 0)</c>). A non-literal <c>int</c> (<c>CAST(3 AS int)</c>, a column)
    /// keeps <c>(10, 0)</c>. Consumed by the promotion sites via
    /// <see cref="Expression.IntegerLiteralDigits"/>.
    /// </summary>
    public readonly int IntegerLiteralDigitCount;

    public Numeric(string command, int index, int length) : base(command, index, length)
    {
        var number = base.Source;

        var hasExponent = number.IndexOfAny(['e', 'E']) >= 0;
        if (hasExponent)
        {
            this.Value = SqlValue.FromDouble(double.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture));
            return;
        }

        var dotIndex = number.IndexOf('.');
        if (dotIndex >= 0)
        {
            // Decimal literal — derive precision and scale by counting
            // significant digits. SQL Server's rule: precision is the total
            // significant-digit count (leading zeros excluded), scale is the
            // digit count after the decimal point. <c>0.5</c> has precision 1
            // and scale 1; <c>100.5</c> has precision 4 and scale 1.
            var integerPart = number[..dotIndex].TrimStart('0').Length;
            var fractionalPart = number.Length - dotIndex - 1;
            var precision = Math.Max(1, integerPart) + fractionalPart;
            // A literal carrying more than 38 significant digits exceeds the
            // numeric type's maximum precision — SQL Server reports Msg 1007
            // rather than letting it reach the type factory.
            if (precision > 38)
                throw SimulatedSqlException.NumberOutOfRangeForNumeric(number.ToString());
            var scale = fractionalPart;
            var parsed = decimal.Parse(number, NumberStyles.Float, CultureInfo.InvariantCulture);
            this.Value = SqlValue.FromDecimal(SqlType.GetDecimal(precision, scale), parsed);
            return;
        }

        // Significant-digit count (leading zeros excluded, floored to 1) — the
        // precision an integer literal contributes when unified with a decimal.
        this.IntegerLiteralDigitCount = Math.Max(1, number.TrimStart('0').Length);

        if (int.TryParse(number, out var int32))
        {
            this.Value = SqlValue.FromInt32(int32);
            return;
        }

        // Past int range — try bigint, then fall back to decimal at scale 0
        // for literals up to 38 digits. Real SQL Server promotes 10-19-digit
        // literals to bigint and 20-38-digit literals to numeric.
        if (long.TryParse(number, out var int64))
        {
            this.Value = SqlValue.FromInt64(int64);
            return;
        }

        // 20-38-digit integer literal — already a scale-0 decimal whose
        // declared precision equals its digit count, so no separate literal
        // annotation is needed (the decimal path sees the right precision).
        this.IntegerLiteralDigitCount = 0;

        if (decimal.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bigDec))
        {
            this.Value = SqlValue.FromDecimal(SqlType.GetDecimal(number.Length, 0), bigDec);
            return;
        }

        throw new NotSupportedException($"Simulated command tokenizer couldn't parse {number} as a number.");
    }
}
