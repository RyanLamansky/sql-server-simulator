using System.Globalization;

namespace SqlServerSimulator.ExtendedEvents;

/// <summary>
/// How Extended Events compares and converts a value of one of its types:
/// the family a predicate's comparator must belong to, and what a constant
/// written against the type converts to.
/// </summary>
internal enum XeTypeFamily : byte
{
    /// <summary>A type no comparator takes (<c>xml</c>, <c>callstack</c>, <c>filetime</c> …).</summary>
    Other,

    /// <summary><c>int8</c> to <c>int64</c>, compared by the <c>*_int64</c> comparators.</summary>
    Signed,

    /// <summary><c>uint8</c> to <c>uint64</c>, compared by the <c>*_uint64</c> comparators.</summary>
    Unsigned,

    /// <summary>A map type, compared on its key by the <c>*_uint64</c> comparators.</summary>
    Map,

    /// <summary><c>float32</c> and <c>float64</c>.</summary>
    Float,

    Boolean,

    /// <summary><c>unicode_string</c> and its pointer form.</summary>
    UnicodeString,

    /// <summary><c>ansi_string</c> and its pointer form.</summary>
    AnsiString,

    Guid,

    BinaryData,

    Pointer,
}

/// <summary>
/// The type of one event field, predicate source, action or customizable
/// attribute, resolved against the engine's catalog: its family, the map
/// behind a map type, and the range an integer type holds.
/// </summary>
internal sealed class XeType
{
    public readonly string Name;
    public readonly XeTypeFamily Family;

    /// <summary>The map object of a map type, whose values a string constant names.</summary>
    public readonly XeObject? Map;

    /// <summary>The smallest and largest value an integer type holds.</summary>
    public readonly long SignedMin;
    public readonly long SignedMax;
    public readonly ulong UnsignedMax;

    private XeType(string name, XeTypeFamily family, XeObject? map, long signedMin = 0, long signedMax = 0, ulong unsignedMax = 0)
    {
        this.Name = name;
        this.Family = family;
        this.Map = map;
        this.SignedMin = signedMin;
        this.SignedMax = signedMax;
        this.UnsignedMax = unsignedMax;
    }

    /// <summary>The type <paramref name="typeName"/> from <paramref name="package"/>, as a column or object names it.</summary>
    public static XeType Of(string typeName, XePackage? package)
    {
        switch (typeName)
        {
            case "ansi_string" or "ansi_string_ptr": return new(typeName, XeTypeFamily.AnsiString, null);
            case "binary_data": return new(typeName, XeTypeFamily.BinaryData, null);
            case "boolean": return new(typeName, XeTypeFamily.Boolean, null);
            case "float32" or "float64": return new(typeName, XeTypeFamily.Float, null);
            case "guid" or "guid_ptr": return new(typeName, XeTypeFamily.Guid, null);
            case "int16": return new(typeName, XeTypeFamily.Signed, null, short.MinValue, short.MaxValue);
            case "int32": return new(typeName, XeTypeFamily.Signed, null, int.MinValue, int.MaxValue);
            case "int64": return new(typeName, XeTypeFamily.Signed, null, long.MinValue, long.MaxValue);
            case "int8": return new(typeName, XeTypeFamily.Signed, null, sbyte.MinValue, sbyte.MaxValue);
            case "ptr": return new(typeName, XeTypeFamily.Pointer, null, unsignedMax: ulong.MaxValue);
            case "uint16": return new(typeName, XeTypeFamily.Unsigned, null, unsignedMax: ushort.MaxValue);
            case "uint32": return new(typeName, XeTypeFamily.Unsigned, null, unsignedMax: uint.MaxValue);
            case "uint64": return new(typeName, XeTypeFamily.Unsigned, null, unsignedMax: ulong.MaxValue);
            case "uint8": return new(typeName, XeTypeFamily.Unsigned, null, unsignedMax: byte.MaxValue);
            case "unicode_string" or "unicode_string_ptr": return new(typeName, XeTypeFamily.UnicodeString, null);
            default:
                var map = package is null ? null : XeMetadata.Instance.Find(package, typeName, "map");
                return map is null
                    ? new(typeName, XeTypeFamily.Other, null)
                    : new(typeName, XeTypeFamily.Map, map, unsignedMax: uint.MaxValue);
        }
    }

    /// <summary>The family a comparator's own type puts it in.</summary>
    public static XeTypeFamily ComparatorFamily(XeObject comparator) => comparator.TypeName switch
    {
        "ansi_string" => XeTypeFamily.AnsiString,
        "binary_data" => XeTypeFamily.BinaryData,
        "boolean" => XeTypeFamily.Boolean,
        "float64" => XeTypeFamily.Float,
        "guid" => XeTypeFamily.Guid,
        "int64" => XeTypeFamily.Signed,
        "ptr" => XeTypeFamily.Pointer,
        "uint64" => XeTypeFamily.Unsigned,
        "unicode_string" => XeTypeFamily.UnicodeString,
        _ => XeTypeFamily.Other,
    };

    /// <summary>Whether a comparator of <paramref name="comparatorFamily"/> takes a value of this type; a map compares as its unsigned key.</summary>
    public bool TakesComparator(XeTypeFamily comparatorFamily) =>
        comparatorFamily == this.Family || (comparatorFamily == XeTypeFamily.Unsigned && this.Family == XeTypeFamily.Map);
}

/// <summary>
/// One value an event carries or a predicate compares against: an integer
/// (signed or unsigned), a float, a boolean, a string, bytes or a GUID, with
/// a map key's text alongside it when it has one.
/// </summary>
internal readonly struct XeValue
{
    public readonly XeTypeFamily Family;
    public readonly long Signed;
    public readonly ulong Unsigned;
    public readonly double Float;
    public readonly string? Text;
    public readonly byte[]? Bytes;
    public readonly Guid Guid;

    private XeValue(XeTypeFamily family, long signed = 0, ulong unsigned = 0, double @float = 0, string? text = null, byte[]? bytes = null, Guid guid = default)
    {
        this.Family = family;
        this.Signed = signed;
        this.Unsigned = unsigned;
        this.Float = @float;
        this.Text = text;
        this.Bytes = bytes;
        this.Guid = guid;
    }

    public static XeValue FromSigned(long value) => new(XeTypeFamily.Signed, signed: value);

    public static XeValue FromUnsigned(ulong value) => new(XeTypeFamily.Unsigned, unsigned: value);

    /// <summary>A map key, with the map value it stands for.</summary>
    public static XeValue FromMap(ulong key, string? text) => new(XeTypeFamily.Map, unsigned: key, text: text);

    public static XeValue FromFloat(double value) => new(XeTypeFamily.Float, @float: value);

    public static XeValue FromBoolean(bool value) => new(XeTypeFamily.Boolean, unsigned: value ? 1UL : 0UL);

    public static XeValue FromUnicode(string value) => new(XeTypeFamily.UnicodeString, text: value);

    public static XeValue FromAnsi(string value) => new(XeTypeFamily.AnsiString, text: value);

    public static XeValue FromBinary(byte[] value) => new(XeTypeFamily.BinaryData, bytes: value);

    public static XeValue FromGuid(Guid value) => new(XeTypeFamily.Guid, guid: value);

    public bool Boolean => this.Unsigned != 0;

    /// <summary>The text an event's XML carries in its <c>&lt;value&gt;</c> element (CDATA wrapping is the writer's).</summary>
    public string XmlText => this.Family switch
    {
        XeTypeFamily.Signed => this.Signed.ToString(CultureInfo.InvariantCulture),
        XeTypeFamily.Unsigned or XeTypeFamily.Map or XeTypeFamily.Pointer => this.Unsigned.ToString(CultureInfo.InvariantCulture),
        XeTypeFamily.Float => this.Float.ToString("R", CultureInfo.InvariantCulture),
        XeTypeFamily.Boolean => this.Boolean ? "true" : "false",
        XeTypeFamily.BinaryData => this.Bytes is { } bytes ? Convert.ToHexString(bytes) : "",
        XeTypeFamily.Guid => this.Guid.ToString("D").ToUpperInvariant(),
        _ => this.Text ?? "",
    };
}
