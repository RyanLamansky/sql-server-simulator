using System.Globalization;
using System.Text;
using System.Xml.Schema;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The canonical text SQL Server stores for a typed <c>xml</c> simple value.
/// A write against an <c>xml(&lt;collection&gt;)</c> binding doesn't keep the
/// lexical form it was handed: real re-renders every element and attribute
/// whose declared type it knows, so <c>&lt;Total&gt;1.00&lt;/Total&gt;</c>
/// comes back as <c>&lt;Total&gt;1&lt;/Total&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two steps per value: the type's <b>whiteSpace facet</b> normalizes the raw
/// text, then the primitive's own canonical form re-renders it. The rendering
/// works from that text rather than from a CLR round-trip, because the round
/// trip is lossy exactly where real is not — <c>DateTime</c> drops the written
/// offset that real preserves (<c>2020-01-02T03:04:05-05:00</c> stays put),
/// and <c>decimal</c> keeps the trailing zeros real sheds.
/// </para>
/// <para>
/// Every rule here was probed against SQL Server 2025 on 2026-08-08, one
/// primitive at a time. The approximate pair is the one that isn't XSD's own
/// canonical form: real renders <c>xs:double</c> / <c>xs:float</c> by XQuery's
/// <c>fn:string</c> rule — plain inside <c>[1e-6, 1e6)</c> and scientific
/// outside it, with a mantissa carrying at least one fractional digit — at SQL
/// Server's own 15 / 7 significant digits rather than a shortest round trip.
/// </para>
/// </remarks>
internal static class XsdCanonical
{
    /// <summary>
    /// The lower bound of the window <c>xs:double</c> / <c>xs:float</c> render
    /// plainly in; below it real writes scientific notation.
    /// </summary>
    private const double PlainLowerBound = 1e-6;

    /// <summary>The exclusive upper bound of that same window.</summary>
    private const double PlainUpperBound = 1e6;

    /// <summary>
    /// Normalizes <paramref name="raw"/> under <paramref name="datatype"/>'s
    /// whiteSpace facet — the step that has to run before the value is
    /// validated, since it is what a facet and a parse both read.
    /// </summary>
    internal static string ApplyWhitespaceFacet(XmlSchemaDatatype datatype, string raw) =>
        datatype.TypeCode switch
        {
            // xs:string alone keeps its text exactly; xs:normalizedString maps
            // each tab / CR / LF to a space without collapsing runs; everything
            // else — the token family, the numerics, the date/time family, the
            // binaries — collapses.
            XmlTypeCode.String => raw,
            XmlTypeCode.NormalizedString => Replace(raw),
            _ => Collapse(raw),
        };

    /// <summary>
    /// The one rewrite that has to happen before a value is validated rather
    /// than after: the <c>24:00:00</c> end-of-day spelling becomes the
    /// following midnight (or just <c>00:00:00</c> for <c>xs:time</c>, which
    /// carries no date). XSD admits it and real accepts it, but .NET's parser
    /// refuses it, so leaving it until the rendering step would reject an
    /// instance real stores.
    /// </summary>
    internal static string PreParse(XmlSchemaDatatype datatype, string normalized) =>
        datatype.TypeCode is XmlTypeCode.DateTime or XmlTypeCode.Time
            && normalized.Contains("24:00:00", StringComparison.Ordinal)
            ? RollMidnight(datatype.TypeCode, normalized)
            : normalized;

    /// <summary>
    /// The canonical text for <paramref name="normalized"/> — already through
    /// <see cref="ApplyWhitespaceFacet"/> and already known valid — under
    /// <paramref name="simpleType"/>.
    /// </summary>
    internal static string Render(XmlSchemaSimpleType simpleType, string normalized) =>
        VarietyDefinitionOf(simpleType) switch
        {
            // A list renders each item under the item type and rejoins on a
            // single space: `  1.50   2.00  ` is `1.5 2`.
            XmlSchemaSimpleTypeList list => RenderList(list, normalized),
            // A union renders under whichever member type accepted the value,
            // which is why `1` under `decimal | boolean` stays `1` rather than
            // becoming `true` — the members are tried in declaration order.
            XmlSchemaSimpleTypeUnion union => RenderUnion(union, normalized),
            _ => RenderAtomic(simpleType.Datatype?.TypeCode ?? XmlTypeCode.String, normalized),
        };

    /// <summary>
    /// The <c>list</c> or <c>union</c> content this type ultimately derives
    /// from, or null when it is atomic. A restriction of a list declares an
    /// <see cref="XmlSchemaSimpleTypeRestriction"/> of its own, so the walk
    /// climbs the base chain rather than reading one level.
    /// </summary>
    private static XmlSchemaSimpleTypeContent? VarietyDefinitionOf(XmlSchemaSimpleType simpleType)
    {
        if (simpleType.Datatype?.Variety is not (XmlSchemaDatatypeVariety.List or XmlSchemaDatatypeVariety.Union))
            return null;
        for (XmlSchemaType? type = simpleType; type is XmlSchemaSimpleType current; type = type.BaseXmlSchemaType)
        {
            if (current.Content is XmlSchemaSimpleTypeList or XmlSchemaSimpleTypeUnion)
                return current.Content;
        }

        return null;
    }

    private static string RenderList(XmlSchemaSimpleTypeList list, string normalized)
    {
        if (normalized.Length == 0)
            return normalized;
        var itemType = list.BaseItemType ?? list.ItemType;
        var items = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < items.Length; i++)
            items[i] = itemType is null ? items[i] : Render(itemType, items[i]);
        return string.Join(' ', items);
    }

    private static string RenderUnion(XmlSchemaSimpleTypeUnion union, string normalized)
    {
        foreach (var member in union.BaseMemberTypes ?? [])
        {
            if (member.Datatype is not { } datatype)
                continue;
            try
            {
                _ = datatype.ParseValue(normalized, null, null);
            }
            catch (Exception e) when (e is XmlSchemaException or FormatException or OverflowException or ArgumentException)
            {
                continue;
            }

            return Render(member, normalized);
        }

        return normalized;
    }

    private static string RenderAtomic(XmlTypeCode typeCode, string value) => typeCode switch
    {
        XmlTypeCode.Boolean => value is "1" or "true" ? "true" : "false",
        XmlTypeCode.Decimal
            or XmlTypeCode.Integer
            or XmlTypeCode.NonPositiveInteger
            or XmlTypeCode.NegativeInteger
            or XmlTypeCode.Long
            or XmlTypeCode.Int
            or XmlTypeCode.Short
            or XmlTypeCode.Byte
            or XmlTypeCode.NonNegativeInteger
            or XmlTypeCode.UnsignedLong
            or XmlTypeCode.UnsignedInt
            or XmlTypeCode.UnsignedShort
            or XmlTypeCode.UnsignedByte
            or XmlTypeCode.PositiveInteger => RenderDecimal(value),
        XmlTypeCode.Double => RenderApproximate(value, significantDigits: 15),
        XmlTypeCode.Float => RenderApproximate(value, significantDigits: 7),
        XmlTypeCode.HexBinary => value.ToUpperInvariant(),
        XmlTypeCode.Base64Binary => StripWhitespace(value),
        XmlTypeCode.Duration
            or XmlTypeCode.YearMonthDuration
            or XmlTypeCode.DayTimeDuration => RenderDuration(value),
        XmlTypeCode.DateTime
            or XmlTypeCode.Date
            or XmlTypeCode.Time
            or XmlTypeCode.GYear
            or XmlTypeCode.GYearMonth
            or XmlTypeCode.GMonth
            or XmlTypeCode.GMonthDay
            or XmlTypeCode.GDay => RenderDateTime(typeCode, value),
        _ => value,
    };

    /// <summary>
    /// Canonical <c>xs:decimal</c>: a leading <c>+</c> and every redundant zero
    /// go, a bare leading or trailing point gains or loses its digit, and a
    /// zero value loses its sign — <c>+007</c> is <c>7</c>, <c>.5</c> is
    /// <c>0.5</c>, <c>5.</c> is <c>5</c>, <c>-0.0</c> is <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Done on the digits rather than through a numeric type so the whole of
    /// XSD's arbitrary-precision domain survives: <c>decimal</c> would overflow
    /// at 29 digits, and the 37-digit values real round-trips would be lost.
    /// </remarks>
    private static string RenderDecimal(string value)
    {
        var negative = value.StartsWith('-');
        var digits = negative || value.StartsWith('+') ? value[1..] : value;

        var point = digits.AsSpan().IndexOf('.');
        var whole = (point < 0 ? digits : digits[..point]).TrimStart('0');
        var fraction = (point < 0 ? string.Empty : digits[(point + 1)..]).TrimEnd('0');

        if (whole.Length == 0)
            whole = "0";
        if (whole == "0" && fraction.Length == 0)
            return "0";

        var sign = negative ? "-" : string.Empty;
        return fraction.Length == 0 ? sign + whole : $"{sign}{whole}.{fraction}";
    }

    /// <summary>
    /// Canonical <c>xs:double</c> / <c>xs:float</c>, which is XQuery's
    /// <c>fn:string</c> rule rather than XSD's own: <c>INF</c> / <c>-INF</c>
    /// pass through, zero is always <c>0.0E0</c> (signed when it was), a
    /// magnitude inside <c>[1e-6, 1e6)</c> renders plainly and everything else
    /// renders as a mantissa in <c>[1, 10)</c> carrying at least one fractional
    /// digit, an <c>E</c>, and an exponent with no <c>+</c> and no padding.
    /// </summary>
    private static string RenderApproximate(string value, int significantDigits)
    {
        if (value is "INF" or "-INF")
            return value;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return value;

        // The significant-digit cap is SQL Server's, not a shortest round trip:
        // 1234567890123456789 comes back as 1.23456789012346E18 for a double
        // and 1.234568E18 for a float.
        var rounded = double.Parse(
            parsed.ToString($"G{significantDigits}", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

        if (rounded == 0)
            return double.IsNegative(rounded) ? "-0.0E0" : "0.0E0";

        var magnitude = Math.Abs(rounded);
        var sign = rounded < 0 ? "-" : string.Empty;
        if (magnitude is >= PlainLowerBound and < PlainUpperBound)
        {
            // "G17" then re-trimmed would reintroduce the digits the cap just
            // shed, so the capped rendering is what the plain form formats.
            var plain = magnitude.ToString($"G{significantDigits}", CultureInfo.InvariantCulture);
            return sign + (plain.Contains('E', StringComparison.Ordinal)
                ? ExpandExponent(magnitude, significantDigits)
                : plain);
        }

        var exponent = (int)Math.Floor(Math.Log10(magnitude));
        var mantissa = magnitude / Math.Pow(10, exponent);

        // Log10's own rounding can land the mantissa just outside [1, 10).
        if (mantissa >= 10)
        {
            mantissa /= 10;
            exponent++;
        }
        else if (mantissa < 1)
        {
            mantissa *= 10;
            exponent--;
        }

        var digits = mantissa.ToString($"G{significantDigits}", CultureInfo.InvariantCulture);
        if (!digits.Contains('.', StringComparison.Ordinal))
            digits += ".0";
        return $"{sign}{digits}E{exponent.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Writes a magnitude inside the plain window that .NET chose to format
    /// exponentially anyway (it does so from 1e-5 down, where real does not).
    /// </summary>
    private static string ExpandExponent(double magnitude, int significantDigits)
    {
        var text = magnitude.ToString($"F{significantDigits + 6}", CultureInfo.InvariantCulture);
        return text.Contains('.', StringComparison.Ordinal) ? text.TrimEnd('0').TrimEnd('.') : text;
    }

    /// <summary>
    /// Canonical <c>xs:duration</c>: every zero-valued field drops out and a
    /// second count sheds its trailing fractional zeros, with the all-zero
    /// duration written <c>PT0S</c> (so <c>P0Y</c> is <c>PT0S</c>).
    /// </summary>
    private static string RenderDuration(string value)
    {
        var negative = value.StartsWith('-');
        var body = negative ? value[1..] : value;
        if (body.Length == 0 || body[0] != 'P')
            return value;

        var date = new StringBuilder();
        var time = new StringBuilder();
        var inTime = false;
        var index = 1;
        var number = new StringBuilder();
        while (index < body.Length)
        {
            var c = body[index++];
            if (c == 'T')
            {
                inTime = true;
                continue;
            }

            if (char.IsAsciiDigit(c) || c == '.')
            {
                _ = number.Append(c);
                continue;
            }

            var field = RenderDecimal(number.ToString());
            _ = number.Clear();
            if (field == "0")
                continue;
            _ = (inTime ? time : date).Append(field).Append(c);
        }

        if (date.Length == 0 && time.Length == 0)
            return negative ? "-PT0S" : "PT0S";
        var sign = negative ? "-" : string.Empty;
        return time.Length == 0 ? $"{sign}P{date}" : $"{sign}P{date}T{time}";
    }

    /// <summary>
    /// Canonical form for the date/time family: a fractional-second part sheds
    /// its trailing zeros (and its point when nothing survives), a UTC offset
    /// written either way becomes <c>Z</c>, and the <c>24:00:00</c> spelling
    /// becomes <c>00:00:00</c> — rolling the date with it for the types that
    /// carry one.
    /// </summary>
    private static string RenderDateTime(XmlTypeCode typeCode, string value)
    {
        var (body, zone) = SplitTimeZone(value);
        body = TrimFractionalSeconds(body);
        if (body.Contains("24:00:00", StringComparison.Ordinal))
            body = RollMidnight(typeCode, body);
        return body + zone;
    }

    /// <summary>
    /// Splits the trailing timezone designator off, mapping either spelling of
    /// a zero offset to <c>Z</c>. The sign search starts past the date's own
    /// hyphens, and past the leading one a negative year would write.
    /// </summary>
    private static (string Body, string Zone) SplitTimeZone(string value)
    {
        if (value.EndsWith('Z'))
            return (value[..^1], "Z");
        if (value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':')
        {
            var zone = value[^6..];
            return (value[..^6], zone is "+00:00" or "-00:00" ? "Z" : zone);
        }

        return (value, string.Empty);
    }

    private static string TrimFractionalSeconds(string body)
    {
        var point = body.AsSpan().IndexOf('.');
        if (point < 0)
            return body;
        var end = point + 1;
        while (end < body.Length && char.IsAsciiDigit(body[end]))
            end++;
        var trimmed = body[(point + 1)..end].TrimEnd('0');
        return trimmed.Length == 0 ? body[..point] + body[end..] : $"{body[..point]}.{trimmed}{body[end..]}";
    }

    /// <summary>
    /// Rewrites the <c>24:00:00</c> spelling as <c>00:00:00</c> on the day
    /// after — except for <c>xs:time</c>, which carries no date to advance and
    /// simply becomes <c>00:00:00</c>.
    /// </summary>
    private static string RollMidnight(XmlTypeCode typeCode, string body)
    {
        if (typeCode == XmlTypeCode.Time)
            return body.Replace("24:00:00", "00:00:00", StringComparison.Ordinal);

        var split = body.AsSpan().IndexOf('T');
        if (split < 0 || !DateTime.TryParseExact(
                body[..split],
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return body.Replace("24:00:00", "00:00:00", StringComparison.Ordinal);
        }

        var rest = body[split..].Replace("24:00:00", "00:00:00", StringComparison.Ordinal);
        return date.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + rest;
    }

    /// <summary>The whiteSpace <c>replace</c> facet: each tab, CR and LF becomes a space.</summary>
    private static string Replace(string raw)
    {
        if (raw.AsSpan().IndexOfAny('\t', '\n', '\r') < 0)
            return raw;
        var buffer = new StringBuilder(raw.Length);
        foreach (var c in raw)
            _ = buffer.Append(c is '\t' or '\n' or '\r' ? ' ' : c);
        return buffer.ToString();
    }

    /// <summary>
    /// The whiteSpace <c>collapse</c> facet: <c>replace</c>, then every run of
    /// spaces folds to one and the leading and trailing runs go entirely.
    /// </summary>
    private static string Collapse(string raw)
    {
        var buffer = new StringBuilder(raw.Length);
        var pendingSpace = false;
        foreach (var c in raw)
        {
            if (c is ' ' or '\t' or '\n' or '\r')
            {
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                _ = buffer.Append(' ');
                pendingSpace = false;
            }

            _ = buffer.Append(c);
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Drops every space from an already-collapsed value — base64's own
    /// canonical form, which is why <c>YW Jj</c> stores as <c>YWJj</c>.
    /// </summary>
    private static string StripWhitespace(string value) =>
        value.Contains(' ', StringComparison.Ordinal) ? value.Replace(" ", string.Empty, StringComparison.Ordinal) : value;
}
