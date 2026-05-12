using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>ISNUMERIC(expression)</c>: returns <c>int</c> <c>1</c> when the
/// argument is "numeric-shaped" and <c>0</c> otherwise. Famously lossy — the
/// implementation here mirrors the probed-against-real-SQL-Server (2025)
/// state machine, which accepts:
/// <list type="bullet">
/// <item><description>Numeric input types (any of <c>tinyint</c>/<c>smallint</c>/<c>int</c>/<c>bigint</c>/<c>decimal</c>/<c>numeric</c>/<c>float</c>/<c>real</c>/<c>money</c>/<c>smallmoney</c>) → always <c>1</c>.</description></item>
/// <item><description>String input: trimmed (leading + trailing whitespace OK); empty after trim → <c>0</c>.</description></item>
/// <item><description>Optional leading currency symbol AND/OR sign (either order, max one of each).</description></item>
/// <item><description>Digit / decimal-point / comma run; lone <c>-</c> / <c>.</c> / <c>,</c> / <c>$</c> all accepted (returning <c>1</c>).</description></item>
/// <item><description>Optional exponent <c>e</c>/<c>E</c>/<c>d</c>/<c>D</c> only with a leading digit and a trailing digit (after optional sign).</description></item>
/// <item><description>Anything else → <c>0</c>. Hex (<c>'0xa'</c>) → <c>0</c>. Internal whitespace → <c>0</c>. NULL → <c>0</c> (NOT NULL).</description></item>
/// </list>
/// </summary>
/// <remarks>Reference: https://learn.microsoft.com/en-us/sql/t-sql/functions/isnumeric-transact-sql</remarks>
internal sealed class IsNumeric(ParserContext context) : Expression
{
    private readonly Expression operand = Parse(context);

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.operand.Run(runtime);
        return v.IsNull ? SqlValue.FromInt32(0)
            : v.Type.Category is SqlTypeCategory.Integer or SqlTypeCategory.Decimal
                or SqlTypeCategory.Approximate or SqlTypeCategory.Money
                ? SqlValue.FromInt32(v.Type == SqlType.Bit ? 0 : 1)
            : !SqlType.IsStringCategory(v.Type) ? SqlValue.FromInt32(0)
            : SqlValue.FromInt32(LooksNumeric(v.AsString) ? 1 : 0);
    }

    public override SqlType GetSqlType(Func<MultiPartName, SqlType> resolveColumnType) => SqlType.Int32;

    /// <summary>
    /// Hand-rolled scanner matching the probed real-server acceptances. The
    /// algorithm: trim whitespace; consume up to one sign and up to one
    /// currency symbol in either order; consume a digit/decimal/comma run;
    /// optionally consume an exponent (requires a digit before AND after).
    /// At least one of {digit, decimal/comma, sign, currency} must have
    /// been consumed for the result to be true. This is intentionally
    /// generous — SQL Server's ISNUMERIC is the historical compatibility
    /// baseline, and tightening it would drop established matches.
    /// </summary>
    private static bool LooksNumeric(string raw)
    {
        var s = raw.AsSpan().Trim();
        if (s.IsEmpty)
            return false;

        var i = 0;
        var seenSign = false;
        var seenCurrency = false;
        // Sign and currency can appear in either order (probe: '$-1' = '-$1' = 1).
        while (i < s.Length)
        {
            if (!seenSign && (s[i] == '+' || s[i] == '-'))
            {
                seenSign = true;
                i++;
            }
            else if (!seenCurrency && IsCurrencySymbol(s[i]))
            {
                seenCurrency = true;
                i++;
            }
            else
            {
                break;
            }
        }

        var hasDigit = false;
        var hasDecimalOrComma = false;
        while (i < s.Length && (IsAsciiDigit(s[i]) || s[i] == '.' || s[i] == ','))
        {
            if (IsAsciiDigit(s[i]))
                hasDigit = true;
            else
                hasDecimalOrComma = true;
            i++;
        }

        if (i < s.Length && (s[i] == 'e' || s[i] == 'E' || s[i] == 'd' || s[i] == 'D'))
        {
            if (!hasDigit)
                return false;
            i++;
            if (i < s.Length && (s[i] == '+' || s[i] == '-'))
                i++;
            if (i >= s.Length || !IsAsciiDigit(s[i]))
                return false;
            while (i < s.Length && IsAsciiDigit(s[i]))
                i++;
        }

        return i == s.Length && (hasDigit || hasDecimalOrComma || seenSign || seenCurrency);
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
    private static bool IsCurrencySymbol(char c) => char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.CurrencySymbol;

    internal override string DebugDisplay() => $"ISNUMERIC({this.operand.DebugDisplay()})";
}
