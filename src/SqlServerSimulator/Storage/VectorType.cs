using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Storage;

/// <summary>
/// SQL Server 2025's <c>vector(n)</c>: <c>n</c> single-precision floats, 1
/// through 1998 of them, each declared dimension count a distinct singleton so
/// reference equality keeps working through promotion. The value is carried
/// as its storage bytes, which are also its <c>DATALENGTH</c>: an 8-byte
/// header (the one SqlClient's vector wire format uses — <c>0xA9</c>, version
/// 1, the dimension count as a little-endian <c>uint16</c>, base type 0 for
/// float32, three reserved zero bytes) followed by the elements little-endian.
/// </summary>
/// <remarks>
/// Text is the type's only conversion partner: a JSON array of numbers reads
/// in, and the text form writes each element as C's <c>%.7e</c> with a
/// three-digit exponent (<c>[1.0000000e+000,2.0000000e+000]</c>), probed
/// 2026-09-26 against SQL Server 2025.
/// </remarks>
internal sealed class VectorSqlType : SqlType
{
    /// <summary>The widest vector real declares.</summary>
    public const int MaxDimensions = 1998;

    /// <summary>
    /// The <c>declaredScale</c> a type spec's <c>float32</c> base-type
    /// argument reads as — no integer scale can take it.
    /// </summary>
    public const int Float32BaseType = int.MinValue;

    /// <summary>Bytes ahead of the elements.</summary>
    public const int HeaderLength = 8;

    /// <summary>The declared dimension count.</summary>
    public readonly int dimensions;

    private VectorSqlType(int dimensions) : base(SqlTypeCategory.Other, TypePairClass.Vector) => this.dimensions = dimensions;

    private static readonly VectorSqlType?[] Cache = new VectorSqlType?[MaxDimensions + 1];

    /// <summary>The singleton for <paramref name="dimensions"/> (1 through <see cref="MaxDimensions"/>).</summary>
    public static VectorSqlType Get(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dimensions, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(dimensions, MaxDimensions);
        return Cache[dimensions] ?? Interlocked.CompareExchange(ref Cache[dimensions], new VectorSqlType(dimensions), null) ?? Cache[dimensions]!;
    }

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

    /// <summary>The storage length every value of this type has: the header plus four bytes per dimension.</summary>
    public int ByteLength => HeaderLength + (4 * this.dimensions);

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
        string text => SqlValue.FromVector(this, Parse(text, this.dimensions)),
        float[] elements => SqlValue.FromVector(this, ToBytes(elements, this.dimensions)),
        _ => throw new NotSupportedException($"No conversion from {raw.GetType()} to vector."),
    };

    public override string ToString() => $"vector({this.dimensions.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>The storage bytes for <paramref name="elements"/>, which must number <paramref name="dimensions"/>.</summary>
    public static byte[] ToBytes(ReadOnlySpan<float> elements, int dimensions)
    {
        if (elements.Length != dimensions)
            throw SimulatedSqlException.VectorDimensionsMismatch(dimensions, elements.Length, 4);
        var bytes = new byte[HeaderLength + (4 * elements.Length)];
        bytes[0] = 0xA9;
        bytes[1] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), (ushort)elements.Length);
        for (var i = 0; i < elements.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(HeaderLength + (4 * i)), elements[i]);
        return bytes;
    }

    /// <summary>The elements of a vector's storage bytes.</summary>
    public static float[] Elements(ReadOnlySpan<byte> bytes)
    {
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
        var count = (bytes.Length - HeaderLength) / 4;
        var builder = new StringBuilder(2 + (count * 16));
        _ = builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
                _ = builder.Append(',');
            var element = BinaryPrimitives.ReadSingleLittleEndian(bytes[(HeaderLength + (4 * i))..]);
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
    /// 42241. Only a complete array with nothing but whitespace after it has
    /// its length checked, as Msg 42204.
    /// </summary>
    public static byte[] Parse(string text, int dimensions)
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
                throw SimulatedSqlException.VectorElementOutOfRange();
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
        return ToBytes(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(elements), dimensions);
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
    private static int TokenEnd(byte[] input, int start)
    {
        var end = start;
        while (end < input.Length && input[end] is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r' or (byte)',' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)':' or (byte)'"'))
            end++;
        return end;
    }

    /// <summary>JSON's number grammar: <c>-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?</c>.</summary>
    private static bool IsJsonNumber(ReadOnlySpan<byte> token)
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
