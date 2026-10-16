using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Schema;

namespace SqlServerSimulator.Schemas;

// Whether a simple type admits a value: the facet and value-constraint checks
// of the third pass, and the pattern facet's regular expressions.
internal sealed partial class XmlSchemaCompiler
{
    /// <summary>
    /// Whether <paramref name="type"/> admits <paramref name="raw"/>: its
    /// built-in's parse after the type's white-space handling, then each
    /// restriction step's own facets. <paramref name="parsed"/> is the atomic
    /// value, for an enumeration or bound to compare against. A type resting
    /// on something unread admits everything.
    /// </summary>
    private static bool IsValid(SimpleXsdType type, string raw, out object? parsed)
    {
        parsed = null;
        if (type.Unknown)
            return true;
        switch (type.Variety)
        {
            case SimpleVariety.Union:
                foreach (var member in type.Members)
                {
                    if (IsValid(member, raw, out parsed))
                        return true;
                }
                return type.Members.Count == 0;
            case SimpleVariety.List:
                var normalized = Normalize(raw, 2);
                var items = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (type.ItemType is { } itemType)
                {
                    foreach (var item in items)
                    {
                        if (!IsValid(itemType, item, out _))
                            return false;
                    }
                }
                parsed = normalized;
                // The built-in lists take at least one item.
                if (items.Length == 0 && type.MinimumItems)
                    return false;
                return FacetsHold(type, normalized, normalized, items.Length);
            default:
                if (type.IsQName || type.BuiltIn?.Datatype is not { } datatype)
                    return true;
                var lexical = Normalize(raw, type.WhiteSpace);
                if (!TryParse(datatype, lexical, type.WhiteSpace, out parsed))
                    return false;
                return FacetsHold(type, lexical, parsed!, length: null);
        }
    }

    /// <summary>
    /// Parses <paramref name="raw"/> under a built-in's datatype, which
    /// applies the built-in's own value space (<c>xs:byte</c>'s range).
    /// </summary>
    private static bool TryParse(XmlSchemaDatatype datatype, string raw, int whiteSpace, out object? value)
    {
        value = null;
        try
        {
            var lexical = Storage.XsdCanonical.PreParse(datatype, Normalize(raw, Math.Max(whiteSpace, datatype.TypeCode is XmlTypeCode.String ? 0 : datatype.TypeCode is XmlTypeCode.NormalizedString ? 1 : 2)));
            var names = new System.Xml.NameTable();
            value = datatype.ParseValue(lexical, names, new System.Xml.XmlNamespaceManager(names));
            // .NET reads a float past its range as infinity and takes any
            // time-zone offset; XSD takes neither.
            if (value is float or double && double.IsInfinity(Convert.ToDouble(value, CultureInfo.InvariantCulture)) && !lexical.EndsWith("INF", StringComparison.Ordinal))
                return false;
            if (datatype.TypeCode is >= XmlTypeCode.Duration and <= XmlTypeCode.GMonth && OffsetOutOfRange.IsMatch(lexical))
                return false;
            return true;
        }
        catch (Exception e) when (e is XmlSchemaException or FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    /// <summary>A trailing time-zone offset beyond fourteen hours.</summary>
    private static readonly Regex OffsetOutOfRange = new(@"[+-](1[5-9]|[2-9]\d):\d\d$|[+-]14:(0[1-9]|[1-5]\d)$", RegexOptions.CultureInvariant);

    private static string Normalize(string raw, int whiteSpace)
    {
        if (whiteSpace == 0)
            return raw;
        var replaced = raw.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        return whiteSpace == 1 ? replaced : string.Join(' ', replaced.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Each restriction step's own facets over a value its built-in admits.</summary>
    private static bool FacetsHold(SimpleXsdType type, string lexical, object value, int? length)
    {
        for (var step = type; step is { Node: not null } or { Synthetic: true }; step = step.Base)
        {
            var enumerated = false;
            var enumerationMatched = false;
            var patterned = false;
            var patternMatched = false;
            foreach (var facet in step.Facets)
            {
                switch (facet.Name)
                {
                    case "enumeration":
                        enumerated = true;
                        enumerationMatched |= facet.Parsed is not null && CompareValues(value, facet.Parsed) == 0;
                        break;
                    case "fractionDigits" when Count(facet, out var fraction) && value is decimal or long or int or short or sbyte or ulong or uint or ushort or byte:
                        if (FractionDigits(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) > (int)Math.Min(fraction, int.MaxValue))
                            return false;
                        break;
                    case "length" when Count(facet, out var exact):
                        if (LengthOf(value, lexical, length) is { } measured && (ulong)measured != exact)
                            return false;
                        break;
                    case "maxExclusive" when facet.Parsed is not null:
                        if (CompareValues(value, facet.Parsed) is >= 0 and not int.MaxValue)
                            return false;
                        break;
                    case "maxInclusive" when facet.Parsed is not null:
                        if (CompareValues(value, facet.Parsed) is > 0 and not int.MaxValue)
                            return false;
                        break;
                    case "maxLength" when Count(facet, out var most):
                        if (LengthOf(value, lexical, length) is { } longest && (ulong)longest > most)
                            return false;
                        break;
                    case "minExclusive" when facet.Parsed is not null:
                        if (CompareValues(value, facet.Parsed) is <= 0)
                            return false;
                        break;
                    case "minInclusive" when facet.Parsed is not null:
                        if (CompareValues(value, facet.Parsed) is < 0)
                            return false;
                        break;
                    case "minLength" when Count(facet, out var least):
                        if (LengthOf(value, lexical, length) is { } shortest && (ulong)shortest < least)
                            return false;
                        break;
                    case "pattern":
                        patterned = true;
                        patternMatched |= facet.Pattern is null || facet.Pattern.IsMatch(lexical);
                        break;
                    case "totalDigits" when Count(facet, out var total) && value is decimal or long or int or short or sbyte or ulong or uint or ushort or byte:
                        if (TotalDigits(Convert.ToDecimal(value, CultureInfo.InvariantCulture)) > (int)Math.Min(total, int.MaxValue))
                            return false;
                        break;
                }
            }
            if ((enumerated && !enumerationMatched) || (patterned && !patternMatched))
                return false;
        }
        return true;
    }

    /// <summary>The length a length facet measures: a list's items, a binary's octets, a string's characters.</summary>
    private static int? LengthOf(object value, string lexical, int? listLength) =>
        listLength ?? value switch
        {
            byte[] octets => octets.Length,
            decimal or double or float or long or int or short or sbyte or ulong or uint or ushort or byte or bool or DateTime or DateTimeOffset or TimeSpan => null,
            _ => CountCharacters(lexical),
        };

    private static int CountCharacters(string text)
    {
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]))
                count++;
        }
        return count;
    }

    private static int TotalDigits(decimal value)
    {
        var digits = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        if (digits.Contains('.', StringComparison.Ordinal))
            digits = digits.TrimEnd('0').TrimEnd('.');
        digits = digits.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        return Math.Max(digits.Length, 1);
    }

    private static int FractionDigits(decimal value)
    {
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.', StringComparison.Ordinal);
        return point < 0 ? 0 : text[(point + 1)..].TrimEnd('0').Length;
    }

    /// <summary>
    /// How two parsed values order: numbers numerically, date-times and
    /// durations by time, strings and binaries by content. A pair of kinds
    /// that don't compare answers <see cref="int.MaxValue"/>, which no facet
    /// check reads as a violation.
    /// </summary>
    private static int CompareValues(object? left, object? right)
    {
        if (left is null || right is null)
            return int.MaxValue;
        static decimal? Exact(object value) => value switch
        {
            decimal or long or int or short or sbyte or ulong or uint or ushort or byte => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
            _ => null,
        };
        static double? Approximate(object value) => value switch
        {
            double or float => Convert.ToDouble(value, CultureInfo.InvariantCulture),
            _ => null,
        };
        if (Exact(left) is { } leftExact && Exact(right) is { } rightExact)
            return leftExact.CompareTo(rightExact);
        if (Approximate(left) is { } leftApproximate && Approximate(right) is { } rightApproximate)
            return leftApproximate.Equals(rightApproximate) ? 0 : leftApproximate.CompareTo(rightApproximate);
        return (left, right) switch
        {
            (byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b) ? 0 : int.MaxValue,
            (string a, string b) => string.Equals(a, b, StringComparison.Ordinal) ? 0 : int.MaxValue,
            (IComparable a, _) when left.GetType() == right.GetType() => a.CompareTo(right),
            _ => left.Equals(right) ? 0 : int.MaxValue,
        };
    }

    // ---- patterns -------------------------------------------------------------

    /// <summary>
    /// Msg 572 for a pattern facet that isn't a regular expression, raised as
    /// the second pass reaches it (probed 2026-10-16 against SQL Server 2025),
    /// with real's state for what is wrong.
    /// </summary>
    private static void RejectInvalidPattern(XsdNode facet)
    {
        var pattern = facet.Element.Attribute("value")?.Value ?? string.Empty;
        _ = XsdPattern(pattern, out var error);
        if (error is { } found)
            throw SimulatedSqlException.XmlSchemaInvalidPattern(pattern, found.Offset, found.State);
    }

    /// <summary>
    /// The .NET regular expression matching an XSD pattern whole, or null for
    /// one using a construct the translation doesn't cover (a negated name
    /// escape inside a class, character class subtraction), which then
    /// matches everything.
    /// <paramref name="error"/> reports an expression that doesn't parse, as
    /// real's Msg 572 offset and state; an error .NET names that real's
    /// mapping isn't known for is left unreported.
    /// </summary>
    private static Regex? XsdPattern(string pattern, out (int Offset, byte State)? error)
    {
        error = null;
        var translated = new StringBuilder();
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            switch (c)
            {
                case '\\' when i + 1 < pattern.Length:
                    var escaped = pattern[i + 1];
                    if (escaped is 'i' or 'I' or 'c' or 'C')
                    {
                        // XML's name-start and name characters, which .NET
                        // lacks; a negated one inside a class isn't covered.
                        var members = escaped is 'i' or 'I' ? NameStartCharacters : NameCharacters;
                        if (inClass && escaped is 'I' or 'C')
                            return null;
                        _ = translated.Append(inClass ? members : escaped is 'i' or 'c' ? $"[{members}]" : $"[^{members}]");
                        i++;
                        continue;
                    }
                    if (!IsXsdEscape(escaped))
                    {
                        error = (i + 2, 74);
                        return null;
                    }
                    _ = translated.Append(c).Append(escaped);
                    i++;
                    continue;
                case '(' when !inClass && i + 1 < pattern.Length && pattern[i + 1] == '?':
                    error = (i + 2, 17);
                    return null;
                case '[':
                    if (inClass && i > 0 && pattern[i - 1] == '-')
                        return null;
                    inClass = true;
                    break;
                case ']':
                    inClass = false;
                    break;
                case '^' or '$' when !inClass || c == '$':
                    _ = translated.Append('\\').Append(c);
                    continue;
            }
            _ = translated.Append(c);
        }
        try
        {
            _ = new Regex(translated.ToString(), RegexOptions.CultureInvariant);
            return new Regex($"^(?:{translated})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        catch (RegexParseException e)
        {
            byte? state = e.Error switch
            {
                RegexParseError.InsufficientClosingParentheses => 16,
                RegexParseError.InsufficientOpeningParentheses => 6,
                RegexParseError.QuantifierAfterNothing => 17,
                RegexParseError.ReversedCharacterRange => 45,
                RegexParseError.ReversedQuantifierRange => 7,
                RegexParseError.UnterminatedBracket => 46,
                _ => null,
            };
            if (state is { } known)
                error = (Math.Clamp(e.Offset, 0, pattern.Length), known);
            return null;
        }
    }

    private const string NameStartCharacters = @"\p{L}\p{Nl}_:";
    private const string NameCharacters = @"\p{L}\p{Nl}\p{Nd}\p{Mn}\p{Mc}\p{Lm}_:.\-\u00B7";

    /// <summary>The characters a backslash may precede in an XSD pattern.</summary>
    private static bool IsXsdEscape(char c) =>
        c is 'n' or 'r' or 't' or '\\' or '|' or '.' or '?' or '*' or '+' or '(' or ')' or '{' or '}' or '-' or '[' or ']' or '^'
            or 's' or 'S' or 'd' or 'D' or 'w' or 'W' or 'p' or 'P';
}
