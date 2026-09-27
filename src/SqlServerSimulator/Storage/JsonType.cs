using System.Globalization;
using System.Numerics;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server 2025's <c>json</c>: a JSON object or array, held here as its
/// canonical text (<see cref="JsonDocumentText"/>) and stored as UTF-8 off-row
/// like the other always-LOB types. Real stores a binary form whose length is
/// what <c>DATALENGTH</c> reports; <see cref="JsonDocumentText.BinaryLength"/>
/// reproduces that length from the text.
/// </summary>
/// <remarks>
/// It converts to and from the character strings (and <c>vector</c>) alone,
/// and to a string only explicitly; it can't be compared, sorted or grouped
/// (probed 2026-09-26 against SQL Server 2025).
/// </remarks>
internal sealed class JsonSqlType() : SqlType(SqlTypeCategory.Other, TypePairClass.Json)
{
    /// <summary>Above every character string and <c>vector</c>, the only types it unifies with.</summary>
    public override int Precedence => 28;

    /// <summary>The text form, which is what a client that doesn't negotiate SQL Server 2025's json support reads.</summary>
    public override Type ClrType => typeof(string);

    public override string SqlServerName => "json";

    public override bool IsFixedLength => false;

    /// <summary>True — a json column is always LOB-stored, and takes no width.</summary>
    public override bool IsLob => true;

    public override int GetVariableByteCount(SqlValue value) => Encoding.UTF8.GetByteCount(value.AsString);

    public override int Encode(SqlValue value, Span<byte> destination) => Encoding.UTF8.GetBytes(value.AsString, destination);

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromJson(Encoding.UTF8.GetString(source));

    public override SqlValue ConvertParameter(object raw) => raw switch
    {
        string text => SqlValue.FromJson(JsonDocumentText.Canonicalize(text)),
        _ => throw new NotSupportedException($"No conversion from {raw.GetType()} to json."),
    };

    public override string ToString() => "json";
}

/// <summary>
/// Reads text into the <c>json</c> type the way real's reader does, over the
/// text's UTF-8 bytes (so a Msg 13609 position counts bytes, as the vector
/// reader's does), and writes the canonical form real hands back: no
/// whitespace, a repeated property name dropped with its value (the first
/// one stays), property names kept exactly as written, string values
/// re-escaped (<c>\"</c>, <c>\\</c>, <c>\b \f \n \r \t</c>, other controls as
/// upper-case <c>\u00XX</c>, everything else literal, <c>/</c> included), a
/// plain number kept as written but for a negative zero's sign, and a number
/// written with an exponent — or with more than 38 digits — read as a
/// double and written as <c>decimal(38, 10)</c> (probed 2026-09-26 against
/// SQL Server 2025).
/// </summary>
internal static class JsonDocumentText
{
    /// <summary>The canonical text of <paramref name="text"/>, or real's refusal of it.</summary>
    public static string Canonicalize(string text)
    {
        var reader = new Reader(Encoding.UTF8.GetBytes(text), emit: true);
        reader.ReadDocument();
        return Encoding.UTF8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(reader.output));
    }

    /// <summary>
    /// The length of real's binary form of a canonical document: an 18-byte
    /// header; a non-empty array 4 bytes plus 4 an element, a non-empty object
    /// 4 plus 6 a member; a dictionary of the distinct property names, 4 bytes
    /// when there is one plus 8 and the name's own length each; and each
    /// value's payload — nothing for <c>true</c> / <c>false</c> / <c>null</c>,
    /// an empty container, an empty string or an integer in 30 bits, 9 bytes
    /// for any other 64-bit integer, a decimal's storage length plus 4 for
    /// any other number, and a type byte, a 7-bit varint length and the UTF-8
    /// bytes for a string (a name's own length is spelled the same way).
    /// Fitted to well over a hundred probed documents (2026-09-26 against SQL
    /// Server 2025).
    /// </summary>
    public static int BinaryLength(string canonical)
    {
        var reader = new Reader(Encoding.UTF8.GetBytes(canonical), emit: false);
        reader.ReadDocument();
        return reader.Length;
    }

    private static int SizedLength(int byteCount) =>
        byteCount == 0 ? 0 : 1 + (byteCount < 1 << 7 ? 1 : byteCount < 1 << 14 ? 2 : byteCount < 1 << 21 ? 3 : 4) + byteCount;

    private static int DecimalPayload(BigInteger mantissa)
    {
        var digits = BigInteger.Abs(mantissa).ToString(CultureInfo.InvariantCulture).Length;
        return 4 + (digits <= 9 ? 5 : digits <= 19 ? 9 : digits <= 28 ? 13 : 17);
    }

    private sealed class Reader(byte[] input, bool emit)
    {
        private const int MaxDepth = 128;
        private const int MaxItems = 65535;
        private const int MaxKeys = 32768;
        private static readonly BigInteger ScaleFactor = BigInteger.Pow(10, 10);
        private static readonly BigInteger DecimalLimit = BigInteger.Pow(10, 38);

        public readonly List<byte> output = [];
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);
        private int keyBytes;
        private int payload;
        private int position;
        private int depth;

        /// <summary>
        /// Nonzero while reading the value of a repeated property name, which
        /// is checked but neither written nor counted.
        /// </summary>
        private int suppressed;

        public int Length => 18 + payload + (this.keys.Count == 0 ? 0 : 4 + this.keyBytes);

        public void ReadDocument()
        {
            this.SkipWhitespace();
            if (this.position == input.Length)
                throw this.Malformed(this.position);
            if (input[this.position] is not ((byte)'{' or (byte)'['))
                throw this.Malformed(this.position);
            this.ReadValue();
            this.SkipWhitespace();
            if (this.position < input.Length)
                throw this.Malformed(this.position);
        }

        private SimulatedSqlException Malformed(int at) =>
            SimulatedSqlException.JsonTypeMalformed(at < input.Length ? (char)input[at] : Parser.JsonText.EndOfText, at);

        private void SkipWhitespace()
        {
            while (this.position < input.Length && input[this.position] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
                this.position++;
        }

        private void Emit(byte value)
        {
            if (emit && this.suppressed == 0)
                this.output.Add(value);
        }

        private void Emit(ReadOnlySpan<byte> value)
        {
            if (emit && this.suppressed == 0)
                this.output.AddRange(value);
        }

        private void Count(int bytes)
        {
            if (this.suppressed == 0)
                this.payload += bytes;
        }

        private void ReadValue()
        {
            if (this.position == input.Length)
                throw this.Malformed(this.position);
            switch (input[this.position])
            {
                case (byte)'"':
                    var value = this.ReadString(raw: false);
                    this.Count(SizedLength(value.Length));
                    this.EmitEscaped(value);
                    return;
                case (byte)'[':
                    this.ReadArray();
                    return;
                case (byte)'{':
                    this.ReadObject();
                    return;
            }
            var start = this.position;
            var end = VectorSqlType.TokenEnd(input, start);
            var token = input.AsSpan(start, end - start);
            if (token.SequenceEqual("true"u8) || token.SequenceEqual("false"u8) || token.SequenceEqual("null"u8))
            {
                this.Emit(token);
            }
            else if (VectorSqlType.IsJsonNumber(token))
            {
                this.ReadNumber(Encoding.ASCII.GetString(token));
            }
            else
            {
                throw this.Malformed(start);
            }
            this.position = end;
        }

        private void Enter()
        {
            if (++this.depth > MaxDepth)
                throw SimulatedSqlException.JsonNestingTooDeep();
        }

        private void ReadArray()
        {
            this.Enter();
            this.Emit((byte)'[');
            this.position++;
            this.SkipWhitespace();
            var count = 0;
            if (this.position < input.Length && input[this.position] == ']')
            {
                this.position++;
            }
            else
            {
                while (true)
                {
                    if (++count > MaxItems)
                        throw SimulatedSqlException.JsonTooManyItems();
                    if (count > 1)
                        this.Emit((byte)',');
                    this.ReadValue();
                    this.SkipWhitespace();
                    if (this.position == input.Length)
                        throw this.Malformed(this.position);
                    if (input[this.position] == ']')
                    {
                        this.position++;
                        break;
                    }
                    if (input[this.position] != ',')
                        throw this.Malformed(this.position);
                    this.position++;
                    this.SkipWhitespace();
                }
                this.Count(4 + (4 * count));
            }
            this.Emit((byte)']');
            this.depth--;
        }

        private void ReadObject()
        {
            this.Enter();
            this.Emit((byte)'{');
            this.position++;
            this.SkipWhitespace();
            if (this.position < input.Length && input[this.position] == '}')
            {
                this.position++;
                this.Emit((byte)'}');
                this.depth--;
                return;
            }
            HashSet<string>? names = null;
            var kept = 0;
            var count = 0;
            while (true)
            {
                if (++count > MaxItems)
                    throw SimulatedSqlException.JsonTooManyItems();
                if (this.position == input.Length || input[this.position] != '"')
                    throw this.Malformed(this.position);
                var name = this.ReadString(raw: true);
                var nameText = Encoding.UTF8.GetString(name);
                var repeated = !(names ??= new(StringComparer.Ordinal)).Add(nameText);
                if (repeated)
                {
                    this.suppressed++;
                }
                else
                {
                    if (kept++ > 0)
                        this.Emit((byte)',');
                    if (this.suppressed == 0 && this.keys.Add(nameText))
                    {
                        if (this.keys.Count > MaxKeys)
                            throw SimulatedSqlException.JsonTooManyKeys();
                        this.keyBytes += 8 + SizedLength(name.Length);
                    }
                    this.Emit((byte)'"');
                    this.Emit(name);
                    this.Emit("\":"u8);
                }
                this.SkipWhitespace();
                if (this.position == input.Length || input[this.position] != ':')
                    throw this.Malformed(this.position);
                this.position++;
                this.SkipWhitespace();
                this.ReadValue();
                if (repeated)
                    this.suppressed--;
                this.SkipWhitespace();
                if (this.position == input.Length)
                    throw this.Malformed(this.position);
                if (input[this.position] == '}')
                {
                    this.position++;
                    break;
                }
                if (input[this.position] != ',')
                    throw this.Malformed(this.position);
                this.position++;
                this.SkipWhitespace();
            }
            this.Count(4 + (6 * kept));
            this.Emit((byte)'}');
            this.depth--;
        }

        /// <summary>
        /// Reads a string token with the cursor on its opening quote: its
        /// bytes as written when <paramref name="raw"/> (a property name),
        /// else its decoded UTF-8 bytes, an unpaired surrogate escape decoding
        /// to U+FFFD. Anything that spoils it — a raw control character, a
        /// bad escape, no closing quote — is named at the opening quote.
        /// </summary>
        private byte[] ReadString(bool raw)
        {
            var quote = this.position;
            var decoded = new List<byte>();
            var i = quote + 1;
            Span<byte> utf8 = stackalloc byte[4];
            while (true)
            {
                if (i == input.Length || input[i] < 0x20)
                    throw this.Malformed(quote);
                var b = input[i];
                if (b == '"')
                    break;
                if (b != '\\')
                {
                    decoded.Add(b);
                    i++;
                    continue;
                }
                if (++i == input.Length)
                    throw this.Malformed(quote);
                var escape = input[i++];
                switch (escape)
                {
                    case (byte)'"' or (byte)'\\' or (byte)'/':
                        decoded.Add(escape);
                        break;
                    case (byte)'b':
                        decoded.Add(0x08);
                        break;
                    case (byte)'f':
                        decoded.Add(0x0C);
                        break;
                    case (byte)'n':
                        decoded.Add(0x0A);
                        break;
                    case (byte)'r':
                        decoded.Add(0x0D);
                        break;
                    case (byte)'t':
                        decoded.Add(0x09);
                        break;
                    case (byte)'u':
                        var unit = this.ReadHex4(ref i, quote);
                        var codePoint = unit;
                        if (char.IsHighSurrogate((char)unit) && i + 1 < input.Length && input[i] == '\\' && input[i + 1] == 'u')
                        {
                            var probe = i + 2;
                            var low = this.ReadHex4(ref probe, quote);
                            if (char.IsLowSurrogate((char)low))
                            {
                                codePoint = char.ConvertToUtf32((char)unit, (char)low);
                                i = probe;
                            }
                        }
                        if (codePoint is >= 0xD800 and <= 0xDFFF)
                            codePoint = 0xFFFD;
                        var written = new Rune(codePoint).EncodeToUtf8(utf8);
                        decoded.AddRange(utf8[..written]);
                        break;
                    default:
                        throw this.Malformed(quote);
                }
            }
            this.position = i + 1;
            return raw ? input.AsSpan(quote + 1, i - quote - 1).ToArray() : [.. decoded];
        }

        private int ReadHex4(ref int i, int quote)
        {
            if (i + 4 > input.Length)
                throw this.Malformed(quote);
            var value = 0;
            for (var k = 0; k < 4; k++)
            {
                var digit = input[i + k] switch
                {
                    >= (byte)'0' and <= (byte)'9' and var d => d - '0',
                    >= (byte)'a' and <= (byte)'f' and var d => d - 'a' + 10,
                    >= (byte)'A' and <= (byte)'F' and var d => d - 'A' + 10,
                    _ => throw this.Malformed(quote),
                };
                value = (value << 4) | digit;
            }
            i += 4;
            return value;
        }

        private void EmitEscaped(byte[] value)
        {
            this.Emit((byte)'"');
            foreach (var b in value)
            {
                switch (b)
                {
                    case (byte)'"':
                        this.Emit("\\\""u8);
                        break;
                    case (byte)'\\':
                        this.Emit("\\\\"u8);
                        break;
                    case 0x08:
                        this.Emit("\\b"u8);
                        break;
                    case 0x09:
                        this.Emit("\\t"u8);
                        break;
                    case 0x0A:
                        this.Emit("\\n"u8);
                        break;
                    case 0x0C:
                        this.Emit("\\f"u8);
                        break;
                    case 0x0D:
                        this.Emit("\\r"u8);
                        break;
                    case < 0x20:
                        this.Emit(Encoding.ASCII.GetBytes($"\\u00{b:X2}"));
                        break;
                    default:
                        this.Emit(b);
                        break;
                }
            }
            this.Emit((byte)'"');
        }

        private void ReadNumber(string token)
        {
            var exponent = token.AsSpan().IndexOfAny('e', 'E') >= 0;
            if (!exponent)
            {
                var negative = token[0] == '-';
                var body = negative ? token[1..] : token;
                var dot = body.IndexOf('.', StringComparison.Ordinal);
                var integral = dot < 0 ? body : body[..dot];
                var fraction = dot < 0 ? "" : body[(dot + 1)..];
                if ((integral == "0" ? 0 : integral.Length) + fraction.Length <= 38)
                {
                    var mantissa = BigInteger.Parse(integral + fraction, CultureInfo.InvariantCulture);
                    this.Emit(Encoding.ASCII.GetBytes(negative && !mantissa.IsZero ? token : body));
                    if (negative)
                        mantissa = -mantissa;
                    this.Count(fraction.Length > 0 || !long.TryParse(negative ? "-" + integral : integral, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)
                        ? DecimalPayload(mantissa)
                        : whole is >= -(1 << 29) and < 1 << 29 ? 0 : 9);
                    return;
                }
            }

            // Read as a double and held at decimal(38, 10), the double's exact
            // value rounded half away from zero at the tenth place.
            var state = exponent ? (byte)5 : (byte)3;
            var number = double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (double.IsInfinity(number))
                throw SimulatedSqlException.JsonNumberOutOfRange(token, state);
            var scaled = ScaleExactly(number);
            if (BigInteger.Abs(scaled) >= DecimalLimit)
                throw SimulatedSqlException.JsonNumberOutOfRange(token, state);
            var digits = BigInteger.Abs(scaled).ToString(CultureInfo.InvariantCulture).PadLeft(11, '0');
            var text = $"{(scaled.Sign < 0 ? "-" : "")}{digits[..^10]}.{digits[^10..]}";
            this.Emit(Encoding.ASCII.GetBytes(text));
            this.Count(DecimalPayload(scaled));
        }

        private static BigInteger ScaleExactly(double number)
        {
            var bits = BitConverter.DoubleToInt64Bits(number);
            var negative = bits < 0;
            var exponentBits = (int)((bits >> 52) & 0x7FF);
            var fractionBits = bits & 0xFFFFFFFFFFFFFL;
            if (exponentBits == 0 && fractionBits == 0)
                return BigInteger.Zero;
            var significand = exponentBits == 0 ? fractionBits : fractionBits | (1L << 52);
            var power = (exponentBits == 0 ? 1 : exponentBits) - 1075;
            BigInteger result;
            if (power >= 0)
            {
                result = new BigInteger(significand) * BigInteger.Pow(2, power) * ScaleFactor;
            }
            else
            {
                var numerator = new BigInteger(significand) * ScaleFactor;
                var denominator = BigInteger.Pow(2, -power);
                result = BigInteger.DivRem(numerator, denominator, out var remainder);
                if (remainder * 2 >= denominator)
                    result += 1;
            }
            return negative ? -result : result;
        }
    }
}
