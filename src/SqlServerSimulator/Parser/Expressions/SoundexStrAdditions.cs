using System.Globalization;
using System.Numerics;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// SQL <c>SOUNDEX(s)</c>: returns the 4-character SOUNDEX phonetic
/// encoding of an English-language input. Algorithm: keep the first
/// letter (uppercased), then encode each subsequent consonant via the
/// standard SOUNDEX digit map (B/F/P/V=1, C/G/J/K/Q/S/X/Z=2, D/T=3,
/// L=4, M/N=5, R=6), skipping vowels (A/E/I/O/U/Y) and H/W, which also
/// separate runs; runs of identical-code letters collapse to one digit;
/// the first non-letter after the first letter ends the code; result is
/// padded with <c>0</c> or truncated to length 4. Empty input returns
/// <c>'0000'</c>; NULL returns NULL.
/// </summary>
internal sealed class Soundex : Expression
{
    private readonly Expression input;

    public Soundex(ParserContext context)
    {
        this.input = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var v = this.input.Run(runtime);
        StringScalars.RejectLegacyLob(v, "soundex");
        return v.IsNull
            ? SqlValue.Null(SqlType.Varchar)
            : SqlValue.FromVarchar(Compute(v.CoerceTo(SqlType.NVarchar).AsString));
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        _ = StringScalars.BindArgument(this.input, batch, resolveColumnType, "soundex");
        return SqlType.Varchar;
    }

    internal override string DebugDisplay() => $"SOUNDEX({this.input.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.input);

    internal static string Compute(string source)
    {
        if (string.IsNullOrEmpty(source))
            return "0000";
        // A code starts from the first character, which must be a letter:
        // ' a', '1a' and '[a]' are all 0000 (probed 2026-09-25 against SQL
        // Server 2025).
        if (!char.IsLetter(source[0]))
            return "0000";
        var sb = new StringBuilder(4);
        var firstIdx = 0;
        var first = char.ToUpperInvariant(source[firstIdx]);
        _ = sb.Append(first);
        var prevCode = Encode(first);
        for (var i = firstIdx + 1; i < source.Length && sb.Length < 4; i++)
        {
            // Real's two departures from the textbook algorithm, probe-
            // confirmed 2026-09-23: H and W separate a run of equal codes as a
            // vowel does (`Ashcraft` is A226, not A261), and the first
            // non-letter ends the code (`A-hc` is A000).
            if (!char.IsLetter(source[i]))
                break;
            var c = char.ToUpperInvariant(source[i]);
            var code = Encode(c);
            if (code == '0')
            {
                prevCode = '0';
                continue;
            }
            if (code != prevCode)
            {
                _ = sb.Append(code);
                prevCode = code;
            }
        }
        while (sb.Length < 4)
            _ = sb.Append('0');
        return sb.ToString();
    }

    private static char Encode(char c) => c switch
    {
        'B' or 'F' or 'P' or 'V' => '1',
        'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
        'D' or 'T' => '3',
        'L' => '4',
        'M' or 'N' => '5',
        'R' => '6',
        _ => '0',
    };
}

/// <summary>
/// SQL <c>DIFFERENCE(s1, s2)</c>: returns an integer 0-4 measuring the
/// similarity of the two strings' SOUNDEX codes. The count is the
/// number of matching positions (out of 4) when the two codes are
/// compared character-by-character. NULL on either side returns NULL.
/// </summary>
internal sealed class Difference : Expression
{
    private readonly Expression left;
    private readonly Expression right;

    public Difference(ParserContext context)
    {
        this.left = Parse(context);
        if (context.Token is not Tokens.Operator { Character: ',' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.right = Parse(context.MoveNextRequiredReturnSelf());
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        var l = this.left.Run(runtime);
        var r = this.right.Run(runtime);
        // DIFFERENCE is the family's odd member: a `text` argument implicitly
        // converts to varchar and evaluates, while `ntext` / `image` raise
        // Msg 8116 — where its own SOUNDEX refuses all three (probe-confirmed
        // 2026-07-31).
        StringScalars.RejectLegacyLob(l, "difference", argumentIndex: 1, allowAnsiText: true);
        StringScalars.RejectLegacyLob(r, "difference", argumentIndex: 2, allowAnsiText: true);
        if (l.IsNull || r.IsNull)
            return SqlValue.Null(SqlType.Int32);
        var sl = Soundex.Compute(l.CoerceTo(SqlType.NVarchar).AsString);
        var sr = Soundex.Compute(r.CoerceTo(SqlType.NVarchar).AsString);
        var matches = 0;
        for (var i = 0; i < 4; i++)
        {
            if (sl[i] == sr[i])
                matches++;
        }
        return SqlValue.FromInt32(matches);
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // DIFFERENCE is the one member that takes a `text` argument — it
        // converts implicitly and evaluates — so both slots carry the
        // ANSI-text carve-out its Run does.
        _ = StringScalars.BindArgument(this.left, batch, resolveColumnType, "difference", allowAnsiText: true);
        _ = StringScalars.BindArgument(this.right, batch, resolveColumnType, "difference", argumentIndex: 2, allowAnsiText: true);
        return SqlType.Int32;
    }

    internal override string DebugDisplay() => $"DIFFERENCE({this.left.DebugDisplay()}, {this.right.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.left).Child(this.right);
}

/// <summary>
/// SQL <c>STR(float [, length [, decimals]])</c>: right-aligned
/// fixed-width numeric-to-string conversion. Default length is 10;
/// default decimals is 0 (rounds, not truncates). Negative or
/// excessive numbers that don't fit in <c>length</c> render as a
/// string of <c>*</c> characters. NULL input returns NULL, as does a NULL
/// or out-of-range <c>length</c> (below 1 or above 8000) and a negative
/// <c>decimals</c>; a NULL <c>decimals</c> reads as 0. The projected
/// result type is <c>varchar(length)</c> — the <c>length</c> argument
/// (default 10) capped at 8000 and floored at 1 when it is a constant,
/// else the <c>varchar(8000)</c> container. Probe-confirmed against SQL
/// Server 2025 (2026-07-22): <c>STR(3.14159, 6, 2)</c> → <c>varchar(6)</c>,
/// <c>STR(x)</c> → <c>varchar(10)</c>, a variable length → <c>varchar(8000)</c>.
/// </summary>
internal sealed class Str : Expression
{
    private readonly Expression numArg;
    private readonly Expression? lengthArg;
    private readonly Expression? decimalsArg;

    /// <summary>
    /// The projected result width: 10 with no <c>length</c> argument, the
    /// value of a constant <c>length</c> — a negated literal included — later
    /// clamped to 1..8000 by <see cref="StringScalars.SizedResultType"/>, or
    /// 8000 for a non-constant length (matching real's <c>varchar(8000)</c>
    /// container).
    /// </summary>
    private readonly int projectedLength;

    public Str(ParserContext context)
    {
        this.numArg = Parse(context);
        if (context.Token is Tokens.Operator { Character: ',' })
        {
            this.lengthArg = Parse(context.MoveNextRequiredReturnSelf());
            if (context.Token is Tokens.Operator { Character: ',' })
                this.decimalsArg = Parse(context.MoveNextRequiredReturnSelf());
        }
        if (context.Token is not Tokens.Operator { Character: ')' })
            throw SimulatedSqlException.SyntaxErrorNear(context);
        this.projectedLength = ProjectedLength(this.lengthArg, context);
    }

    public override SqlValue Run(RuntimeContext runtime)
    {
        // Project the same bounded varchar type GetSqlType reports so the
        // value's declared width matches the result-set schema.
        var resultType = (VarcharSqlType)StringScalars.SizedResultType(SqlType.Varchar, this.projectedLength, runtime.Batch);
        var v = this.numArg.Run(runtime);
        if (v.IsNull)
            return SqlValue.Null(resultType);
        var num = v.CoerceTo(SqlType.Float).AsDouble;
        int length;
        if (this.lengthArg is null)
        {
            length = 10;
        }
        else
        {
            var lengthValue = this.lengthArg.Run(runtime);
            if (lengthValue.IsNull)
                return SqlValue.Null(resultType);
            length = StringScalars.CoerceLengthArgument(lengthValue);
        }
        var decimals = 0;
        if (this.decimalsArg?.Run(runtime) is { IsNull: false } decimalsValue)
            decimals = StringScalars.CoerceLengthArgument(decimalsValue);
        if (length is < 1 or > 8000 || decimals < 0)
            return SqlValue.Null(resultType);

        var formatted = Format(num, length, Math.Min(decimals, 16));
        return formatted.Length > length
            ? SqlValue.FromVarchar(resultType, new string('*', length))
            : SqlValue.FromVarchar(resultType, formatted.PadLeft(length));
    }

    /// <summary>
    /// Renders <paramref name="num"/> the way real's STR does, probed
    /// 2026-09-23 against SQL Server 2025.
    /// The decimals shrink to whatever room the <em>unrounded</em> integer
    /// part and sign leave, so <c>STR(99.99, 4, 1)</c> rounds to
    /// <c>100.0</c> and overflows to <c>****</c> rather than falling back to
    /// <c>100</c>.
    /// The double's exact value is truncated to 17 significant digits and
    /// only then rounded half away from zero at the decimals, zero-filling
    /// past the 17th digit: <c>STR(2.675, 5, 2)</c> is <c>2.67</c>,
    /// <c>STR(2.5, 1)</c> is <c>3</c>, and <c>1234567890123456.75</c> keeps
    /// <c>.7</c> at five decimals.
    /// </summary>
    private static string Format(double num, int length, int decimals)
    {
        // A negative zero keeps its sign (`STR(-0e0, 4)` is `  -0`).
        var negative = double.IsNegative(num);
        var (digits, exponent) = LeadingDigits(Math.Abs(num));
        var integerDigits = Math.Max(exponent + 1, 1);
        decimals = Math.Clamp(length - integerDigits - (negative ? 1 : 0) - 1, 0, decimals);

        // scaled is the value times 10^decimals, as an integer.
        var kept = exponent + 1 + decimals;
        BigInteger scaled;
        if (kept >= 17)
        {
            scaled = digits * BigInteger.Pow(10, kept - 17);
        }
        else if (kept < 0)
        {
            scaled = BigInteger.Zero;
        }
        else
        {
            var divisor = BigInteger.Pow(10, 17 - kept);
            scaled = BigInteger.DivRem(digits, divisor, out var dropped);
            if (dropped * 2 >= divisor)
                scaled += 1;
        }

        var text = scaled.ToString(CultureInfo.InvariantCulture).PadLeft(decimals + 1, '0');
        var sign = negative ? "-" : "";
        return decimals == 0
            ? sign + text
            : string.Concat(sign, text.AsSpan(0, text.Length - decimals), ".", text.AsSpan(text.Length - decimals));
    }

    /// <summary>
    /// The first 17 significant digits of <paramref name="magnitude"/>'s exact
    /// value, truncated, as an integer in <c>[10^16, 10^17)</c>, with the
    /// decimal exponent of the leading digit; zero is <c>(0, 0)</c>.
    /// </summary>
    private static (BigInteger Digits, int Exponent) LeadingDigits(double magnitude)
    {
        if (magnitude == 0 || !double.IsFinite(magnitude))
            return (BigInteger.Zero, 0);
        var bits = BitConverter.DoubleToInt64Bits(magnitude);
        var biasedExponent = (int)(bits >> 52);
        var mantissa = bits & 0xF_FFFF_FFFF_FFFF;
        if (biasedExponent == 0)
            biasedExponent = 1;
        else
            mantissa |= 1L << 52;
        // magnitude = numerator / denominator exactly.
        var binaryExponent = biasedExponent - 1075;
        var numerator = binaryExponent >= 0 ? (BigInteger)mantissa << binaryExponent : mantissa;
        var denominator = binaryExponent >= 0 ? BigInteger.One : BigInteger.One << -binaryExponent;

        var lower = BigInteger.Pow(10, 16);
        var upper = lower * 10;
        var exponent = (int)Math.Floor(Math.Log10(magnitude));
        while (true)
        {
            var shift = 16 - exponent;
            var digits = shift >= 0
                ? numerator * BigInteger.Pow(10, shift) / denominator
                : numerator / (denominator * BigInteger.Pow(10, -shift));
            if (digits >= upper)
                exponent++;
            else if (digits < lower)
                exponent--;
            else
                return (digits, exponent);
        }
    }

    public override SqlType GetSqlType(BatchContext batch, Func<MultiPartName, SqlType> resolveColumnType)
    {
        // The length and decimals slots are judged before the value converts,
        // as ROUND's are (probed 2026-09-26 against SQL Server 2025).
        if (this.lengthArg is not null)
            ScalarArguments.RequireNumericSlot(this.lengthArg, batch, resolveColumnType, "str", 2, NumericSlot.Integer);
        if (this.decimalsArg is not null)
            ScalarArguments.RequireNumericSlot(this.decimalsArg, batch, resolveColumnType, "str", 3, NumericSlot.Integer);
        _ = AssignmentRules.ArgumentType(this.numArg, SqlType.Float, batch, resolveColumnType);
        return StringScalars.SizedResultType(SqlType.Varchar, this.projectedLength, batch);
    }

    private static int ProjectedLength(Expression? lengthArg, ParserContext context)
    {
        if (lengthArg is null)
            return 10;
        if (ConstantFolding.TryFold(lengthArg, context, out var constant) && !constant.IsNull
            && SqlType.IsIntegerCategory(constant.Type))
        {
            try
            {
                return constant.CoerceTo(SqlType.Int32).AsInt32;
            }
            catch (OverflowException)
            {
                return 8000;
            }
        }

        return 8000;
    }

    internal override string DebugDisplay() => $"STR({this.numArg.DebugDisplay()})";

    internal override void Describe(NodeShape shape) => shape.Child(this.numArg).Child(this.lengthArg).Child(this.decimalsArg);
}
