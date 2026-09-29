using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server 2025's <c>vector(n)</c>: <c>n</c> single-precision floats, 1
/// through 1998 of them — or with the preview <c>float16</c> base type
/// <c>vector(n, float16)</c>, <c>n</c> half-precision ones, 1 through 3996 —
/// each declared dimension count and base type a distinct singleton so
/// reference equality keeps working through promotion. The value is carried
/// as its storage bytes, which are also its <c>DATALENGTH</c>: an 8-byte
/// header (the one SqlClient's vector wire format uses — <c>0xA9</c>, version
/// 1, the dimension count as a little-endian <c>uint16</c>, base type 0 for
/// float32 or 1 for float16, three reserved zero bytes) followed by the
/// elements little-endian.
/// </summary>
/// <remarks>
/// Text is the type's only conversion partner: a JSON array of numbers reads
/// in, and the text form writes each element as C's <c>%.7e</c> with a
/// three-digit exponent (<c>[1.0000000e+000,2.0000000e+000]</c>), probed
/// 2026-09-26 against SQL Server 2025.
/// </remarks>
internal sealed class VectorSqlType : SqlType
{
    /// <summary>The widest float32 vector real declares.</summary>
    public const int MaxDimensions = 1998;

    /// <summary>The widest float16 vector real declares.</summary>
    public const int MaxFloat16Dimensions = 3996;

    /// <summary>
    /// The <c>declaredScale</c> a type spec's <c>float32</c> base-type
    /// argument reads as — no integer scale can take it.
    /// </summary>
    public const int Float32BaseType = int.MinValue;

    /// <summary>
    /// The <c>declaredScale</c> a type spec's <c>float16</c> base-type
    /// argument reads as, which only a database with <c>PREVIEW_FEATURES</c>
    /// on accepts.
    /// </summary>
    public const int Float16BaseType = int.MinValue + 1;

    /// <summary>Bytes ahead of the elements.</summary>
    public const int HeaderLength = 8;

    /// <summary>The declared dimension count.</summary>
    public readonly int dimensions;

    /// <summary>Whether the elements are half-precision, the <c>float16</c> base type.</summary>
    public readonly bool IsFloat16;

    private VectorSqlType(int dimensions, bool float16) : base(SqlTypeCategory.Other, TypePairClass.Vector) => (this.dimensions, this.IsFloat16) = (dimensions, float16);

    private static readonly VectorSqlType?[] Cache = new VectorSqlType?[MaxDimensions + 1];

    private static readonly VectorSqlType?[] Float16Cache = new VectorSqlType?[MaxFloat16Dimensions + 1];

    /// <summary>The float32 singleton for <paramref name="dimensions"/> (1 through <see cref="MaxDimensions"/>).</summary>
    public static VectorSqlType Get(int dimensions) => Get(dimensions, float16: false);

    /// <summary>The singleton for <paramref name="dimensions"/> of the base type <paramref name="float16"/> names.</summary>
    public static VectorSqlType Get(int dimensions, bool float16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, float16 ? MaxFloat16Dimensions : MaxDimensions);
        var cache = float16 ? Float16Cache : Cache;
        return cache[dimensions] ?? Interlocked.CompareExchange(ref cache[dimensions], new VectorSqlType(dimensions, float16), null) ?? cache[dimensions]!;
    }

    /// <summary>The base type's name, as <c>VECTORPROPERTY</c> and the catalog report it.</summary>
    public string BaseTypeName => this.IsFloat16 ? "float16" : "float32";

    /// <summary>
    /// Below every other type: a vector meeting a character string unifies as
    /// the string, and it unifies with nothing else.
    /// </summary>
    public override int Precedence => 0;

    /// <summary>
    /// The text form, which is what a client that doesn't negotiate SQL Server
    /// 2025's vector support reads.
    /// </summary>
    public override Type ClrType => typeof(string);

    public override string SqlServerName => "vector";

    public override bool IsFixedLength => false;

    /// <summary>The storage length every value of this type has: the header plus four (float32) or two (float16) bytes per dimension.</summary>
    public int ByteLength => HeaderLength + ((this.IsFloat16 ? 2 : 4) * this.dimensions);

    public override int GetVariableByteCount(SqlValue value) => value.AsVectorBytes.Length;

    public override int Encode(SqlValue value, Span<byte> destination)
    {
        var bytes = value.AsVectorBytes;
        bytes.CopyTo(destination);
        return bytes.Length;
    }

    public override SqlValue Decode(ReadOnlySpan<byte> source) => SqlValue.FromVector(this, source.ToArray());

    public override SqlValue ConvertParameter(object raw) => raw switch
    {
        string text => SqlValue.FromVector(this, Parse(text, this.dimensions, float16: this.IsFloat16)),
        float[] elements => SqlValue.FromVector(this, ToBytes(elements, this.dimensions, float16: this.IsFloat16)),
        _ => throw new NotSupportedException($"No conversion from {raw.GetType()} to vector."),
    };

    public override string ToString() => this.IsFloat16
        ? $"vector({this.dimensions.ToString(CultureInfo.InvariantCulture)}, float16)"
        : $"vector({this.dimensions.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// The storage bytes for <paramref name="elements"/>, which must number
    /// <paramref name="dimensions"/> — else Msg 42204 at
    /// <paramref name="mismatchState"/> — each narrowed to half precision
    /// when <paramref name="float16"/> (the caller has refused any that
    /// overflow it).
    /// </summary>
    public static byte[] ToBytes(ReadOnlySpan<float> elements, int dimensions, byte mismatchState = 4, bool float16 = false)
    {
        if (elements.Length != dimensions)
            throw SimulatedSqlException.VectorDimensionsMismatch(dimensions, elements.Length, mismatchState);
        var size = float16 ? 2 : 4;
        var bytes = new byte[HeaderLength + (size * elements.Length)];
        bytes[0] = 0xA9;
        bytes[1] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)elements.Length);
        bytes[4] = float16 ? (byte)1 : (byte)0;
        for (var i = 0; i < elements.Length; i++)
        {
            if (float16)
                BinaryPrimitives.WriteHalfLittleEndian(bytes.AsSpan(HeaderLength + (2 * i)), (Half)elements[i]);
            else
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(HeaderLength + (4 * i)), elements[i]);
        }
        return bytes;
    }

    /// <summary>Whether a vector's storage bytes hold float16 elements, which the header's base-type byte says.</summary>
    public static bool IsFloat16Bytes(ReadOnlySpan<byte> bytes) => bytes[4] == 1;

    /// <summary>The elements of a vector's storage bytes, a float16 vector's widened to float32.</summary>
    public static float[] Elements(ReadOnlySpan<byte> bytes)
    {
        if (IsFloat16Bytes(bytes))
        {
            var halves = new float[(bytes.Length - HeaderLength) / 2];
            for (var i = 0; i < halves.Length; i++)
                halves[i] = (float)BinaryPrimitives.ReadHalfLittleEndian(bytes[(HeaderLength + (2 * i))..]);
            return halves;
        }
        var elements = new float[(bytes.Length - HeaderLength) / 4];
        for (var i = 0; i < elements.Length; i++)
            elements[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes[(HeaderLength + (4 * i))..]);
        return elements;
    }

    /// <summary>
    /// The text form: each element as C's <c>%.7e</c> with a three-digit
    /// exponent, comma-separated without spaces, a negative zero printing
    /// as a positive one.
    /// </summary>
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        var elements = Elements(bytes);
        var builder = new StringBuilder(2 + (elements.Length * 16));
        _ = builder.Append('[');
        for (var i = 0; i < elements.Length; i++)
        {
            if (i > 0)
                _ = builder.Append(',');
            var element = elements[i];
            _ = element == 0
                ? builder.Append("0.0000000e+000")
                : builder.Append(((double)element).ToString("E7", CultureInfo.InvariantCulture).Replace('E', 'e'));
        }
        return builder.Append(']').ToString();
    }

    /// <summary>
    /// Reads text as a vector of <paramref name="dimensions"/> elements the
    /// way real's reader does: over the text's UTF-8 bytes, left to right,
    /// raising at the first thing it can't take. A malformed token is Msg
    /// 13609 at its first byte; a well-formed value that isn't a number is Msg
    /// 13670 as soon as it is read — a nested container once its first member
    /// shows it isn't empty — and an element outside float32's range is Msg
    /// 42241 — for a <paramref name="float16"/> vector, one the float32 range
    /// holds but half precision doesn't at state 2. Only a complete array with
    /// nothing but whitespace after it has its length checked, as Msg 42204 at
    /// <paramref name="mismatchState"/> (4 for text, 2 for a json value).
    /// </summary>
    public static byte[] Parse(string text, int dimensions, byte mismatchState = 4, bool float16 = false)
    {
        var input = Encoding.UTF8.GetBytes(text);
        var i = 0;
        SkipWhitespace(input, ref i);
        if (i == input.Length)
            throw Malformed(input, i);
        if (input[i] != '[')
            throw NonNumericValue(input, ref i, topLevel: true);
        i++;
        SkipWhitespace(input, ref i);
        if (i == input.Length)
            throw Malformed(input, i);
        if (input[i] == ']')
            throw SimulatedSqlException.VectorJsonInvalid("Empty Array not Supported", 8);

        var elements = new List<float>(dimensions);
        while (true)
        {
            if (i == input.Length)
                throw Malformed(input, i);
            if (input[i] is (byte)'[' or (byte)'{' or (byte)'"')
                throw NonNumericValue(input, ref i, topLevel: false);
            var start = i;
            var end = TokenEnd(input, i);
            var token = input.AsSpan(start, end - start);
            if (!IsJsonNumber(token))
                throw NonNumericValue(input, ref i, topLevel: false);
            var element = (float)double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (float.IsInfinity(element))
                throw SimulatedSqlException.VectorElementOutOfRange(float16);
            if (float16 && Half.IsInfinity((Half)element))
                throw SimulatedSqlException.VectorElementOutOfRange(float16: true, state: 2);
            elements.Add(element);
            i = end;
            SkipWhitespace(input, ref i);
            if (i == input.Length)
                throw Malformed(input, i);
            if (input[i] == ']')
                break;
            if (input[i] != ',')
                throw Malformed(input, i);
            i++;
            SkipWhitespace(input, ref i);
        }
        i++;
        SkipWhitespace(input, ref i);
        if (i < input.Length)
            throw Malformed(input, i);
        return ToBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(elements), dimensions, mismatchState, float16);
    }

    /// <summary>
    /// The refusal for a value at <paramref name="i"/> that isn't an element
    /// number: a container, a string, a literal, a number standing where an
    /// array belongs, or a malformed token.
    /// </summary>
    private static SimulatedSqlException NonNumericValue(byte[] input, ref int i, bool topLevel)
    {
        switch (input[i])
        {
            case (byte)'"':
                for (var j = i + 1; j < input.Length; j++)
                {
                    if (input[j] == '\\')
                        j++;
                    else if (input[j] == '"')
                        return SimulatedSqlException.VectorJsonInvalid("String not Supported", 5);
                }
                return Malformed(input, i);
            case (byte)'[':
            case (byte)'{':
                var open = input[i];
                i++;
                SkipWhitespace(input, ref i);
                return i == input.Length ? Malformed(input, i)
                    : open == '[' ? (input[i] == ']' ? SimulatedSqlException.VectorJsonInvalid("Empty Array not Supported", 8) : SimulatedSqlException.VectorJsonInvalid("Malformed JSON", 1))
                    : input[i] == '}' ? SimulatedSqlException.VectorJsonInvalid("Empty object Not Suppoted", 7)
                    : SimulatedSqlException.VectorJsonInvalid("Object Not Supported", 10);
        }
        var token = input.AsSpan(i, TokenEnd(input, i) - i);
        return token switch
        {
            [(byte)'n', (byte)'u', (byte)'l', (byte)'l'] => SimulatedSqlException.VectorJsonInvalid("Null Not Supported", 4),
            [(byte)'t', (byte)'r', (byte)'u', (byte)'e'] => SimulatedSqlException.VectorJsonInvalid("Boolean not supported", 9),
            [(byte)'f', (byte)'a', (byte)'l', (byte)'s', (byte)'e'] => SimulatedSqlException.VectorJsonInvalid("Boolean Not Supported", 9),
            _ when topLevel && IsJsonNumber(token) => SimulatedSqlException.VectorJsonInvalid("Malformed JSON", 1),
            _ => Malformed(input, i),
        };
    }

    private static SimulatedSqlException Malformed(byte[] input, int i) =>
        SimulatedSqlException.VectorJsonMalformed(i < input.Length ? (char)input[i] : Parser.JsonText.EndOfText, i);

    private static void SkipWhitespace(byte[] input, ref int i)
    {
        while (i < input.Length && input[i] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            i++;
    }

    /// <summary>
    /// Where a bare token — a number or a literal — starting at
    /// <paramref name="start"/> ends: at whitespace, structural punctuation,
    /// a quote or the end of the text. Anything else belongs to the token, so
    /// a stray byte inside one condemns the whole token from its first byte.
    /// </summary>
    internal static int TokenEnd(byte[] input, int start)
    {
        var end = start;
        while (end < input.Length && input[end] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)':' or (byte)'"'))
            end++;
        return end;
    }

    /// <summary>JSON's number grammar: <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</c>.</summary>
    internal static bool IsJsonNumber(ReadOnlySpan<byte> token)
    {
        var i = 0;
        if (i < token.Length && token[i] == '-')
            i++;
        if (i == token.Length)
            return false;
        if (token[i] == '0')
        {
            i++;
        }
        else if (char.IsAsciiDigit((char)token[i]))
        {
            while (i < token.Length && char.IsAsciiDigit((char)token[i]))
                i++;
        }
        else
        {
            return false;
        }
        if (i < token.Length && token[i] == '.')
        {
            i++;
            var digits = i;
            while (i < token.Length && char.IsAsciiDigit((char)token[i]))
                i++;
            if (i == digits)
                return false;
        }
        if (i < token.Length && token[i] is (byte)'e' or (byte)'E')
        {
            i++;
            if (i < token.Length && token[i] is (byte)'+' or (byte)'-')
                i++;
            var digits = i;
            while (i < token.Length && char.IsAsciiDigit((char)token[i]))
                i++;
            if (i == digits)
                return false;
        }
        return i == token.Length;
    }
}
