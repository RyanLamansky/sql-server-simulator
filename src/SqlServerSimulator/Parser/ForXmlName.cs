using System.Globalization;
using System.Text;
using System.Xml;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The two ways FOR XML turns a SQL identifier into an XML name: RAW / AUTO
/// <see cref="Encode">escape</see> whatever can't appear in an XML name as
/// <c>_xHHHH_</c>, while PATH and the explicit <c>RAW('elem')</c> /
/// <c>PATH('row')</c> / <c>ROOT('name')</c> arguments
/// <see cref="ValidateSimpleName">reject</see> it (Msg 6850 / 6846 / 6867 /
/// 6849).
/// </summary>
/// <remarks>
/// The character classification is the XML 1.0 fourth-edition <c>Name</c>
/// production, which <see cref="XmlConvert.IsStartNCNameChar"/> /
/// <see cref="XmlConvert.IsNCNameChar"/> implement and SQL Server 2025 matches
/// character for character (probed across the Latin-1, combining-mark, extender
/// and fullwidth boundaries — <c>é</c> and <c>·</c> pass where <c>×</c>,
/// <c>Ͷ</c>, <c>℘</c> and <c>Ａ</c> don't, so the wider fifth-edition ranges are
/// out). Two SQL-Server-specific rules sit on top: a <c>:</c> is a name
/// character everywhere but the first position, and a supplementary code point
/// encodes as one six-hex-digit escape (<c>_x01D400_</c>) though the validator
/// accepts it verbatim.
/// </remarks>
internal static class ForXmlName
{
    /// <summary>
    /// The RAW / AUTO name transform: returns <paramref name="name"/> with every
    /// character an XML name can't carry replaced by <c>_xHHHH_</c> — so a
    /// column aliased <c>[a b]</c> becomes <c>a_x0020_b</c> and
    /// <c>FROM #tmp</c> yields <c>&lt;_x0023_tmp&gt;</c>. An underscore
    /// followed by <c>x</c> escapes itself (<c>_x005F_</c>) whatever comes
    /// after, keeping the encoding round-trippable; a supplementary code point
    /// takes six hex digits. Returns the same instance when nothing needs
    /// escaping.
    /// </summary>
    internal static string Encode(string name)
    {
        StringBuilder? encoded = null;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1]);
            var codePoint = pair ? char.ConvertToUtf32(c, name[i + 1]) : c;
            // A supplementary character always escapes, even though the
            // validator accepts one — probed both ways against SQL Server 2025.
            var literal = !pair
                && (c == '_' ? i + 1 >= name.Length || name[i + 1] != 'x' : IsNameChar(c, i == 0));
            if (literal)
            {
                _ = encoded?.Append(c);
                continue;
            }

            encoded ??= new StringBuilder(name.Length + 8).Append(name, 0, i);
            _ = encoded.Append("_x")
                .Append(codePoint.ToString(pair ? "X6" : "X4", CultureInfo.InvariantCulture))
                .Append('_');
            if (pair)
                i++;
        }
        return encoded?.ToString() ?? name;
    }

    /// <summary>
    /// Validates one whole XML name — a <c>RAW('elem')</c> / <c>PATH('row')</c>
    /// row tag or a <c>ROOT('name')</c> argument, which are single names rather
    /// than paths (a <c>/</c> in one is simply an invalid character).
    /// </summary>
    internal static void ValidateSimpleName(string name, ForXmlNameKind kind, ForXmlNamespaces? namespaces) =>
        ValidateStep(name, name, kind, namespaces);

    /// <summary>
    /// Validates a <c>FOR XML PATH</c> column alias, which is a path of
    /// <c>/</c>-separated steps: an empty step raises Msg 6849, the last step's
    /// leading <c>@</c> (attribute) is stripped before checking and its
    /// <c>text()</c> / <c>data()</c> node function is exempt, and every
    /// remaining step goes through the same name rules as a row tag. Messages
    /// name the whole alias, not the offending step. An unnamed column (empty
    /// alias) maps to text content and carries no name to check.
    /// </summary>
    internal static void ValidatePathColumn(string alias, ForXmlNamespaces? namespaces)
    {
        if (alias.Length == 0)
            return;

        var segments = alias.Split('/');
        for (var s = 0; s < segments.Length; s++)
        {
            var step = segments[s];
            if (step.Length == 0)
                throw SimulatedSqlException.ForXmlPathSlashPlacement(alias);
            if (s < segments.Length - 1)
            {
                ValidateStep(alias, step, ForXmlNameKind.Column, namespaces);
                continue;
            }

            if (step[0] == '@')
            {
                // A bare '@' has no name behind it; real reports the '@' itself.
                if (step.Length == 1)
                    throw SimulatedSqlException.ForXmlInvalidName(ForXmlNameKind.Column, alias, '@');
                step = step[1..];
            }
            else if (Collation.Baseline.Equals(step, "text()") || Collation.Baseline.Equals(step, "data()"))
            {
                continue;
            }
            ValidateStep(alias, step, ForXmlNameKind.Column, namespaces);
        }
    }

    /// <summary>
    /// Validates one step of a name: the reserved <c>xmlns</c> (Msg 6867) comes
    /// first, then a namespace prefix, which must be the predefined <c>xml</c>
    /// or one <paramref name="namespaces"/> declares — every other prefix is
    /// Msg 6846 — and finally the character rules on what's left (Msg 6850,
    /// naming the first character at fault). <paramref name="fullName"/> is
    /// what the messages quote.
    /// </summary>
    private static void ValidateStep(string fullName, string step, ForXmlNameKind kind, ForXmlNamespaces? namespaces)
    {
        var colon = step.IndexOf(':', StringComparison.Ordinal);
        var prefix = colon > 0 ? step[..colon] : null;
        if ((prefix ?? step) == "xmlns")
            throw SimulatedSqlException.ForXmlXmlnsName();
        // The prefix comparison is ordinal in both directions: real accepts
        // 'xml:' and rejects 'XML:', and a clause declaring 'p' still refuses
        // 'P:a'.
        if (prefix is not (null or "xml") && namespaces?.IsDeclared(prefix) != true)
            throw SimulatedSqlException.ForXmlUndeclaredPrefix(prefix, fullName, kind);

        var local = prefix is null ? step : step[(colon + 1)..];
        for (var i = 0; i < local.Length; i++)
        {
            var c = local[i];
            // A surrogate pair passes verbatim (real emits <𝐀a> unescaped).
            if (char.IsHighSurrogate(c) && i + 1 < local.Length && char.IsLowSurrogate(local[i + 1]))
            {
                i++;
                continue;
            }
            if (!IsNameChar(c, i == 0))
                throw SimulatedSqlException.ForXmlInvalidName(kind, fullName, c);
        }
    }

    /// <summary>
    /// Whether <paramref name="c"/> may appear in an XML name, at the first
    /// position when <paramref name="first"/> — where the digits, <c>-</c>,
    /// <c>.</c>, <c>:</c>, the combining marks and the extenders are all
    /// excluded.
    /// </summary>
    private static bool IsNameChar(char c, bool first) =>
        first ? XmlConvert.IsStartNCNameChar(c) : XmlConvert.IsNCNameChar(c) || c == ':';
}

/// <summary>Which FOR XML name a diagnostic is about; the messages word each differently.</summary>
internal enum ForXmlNameKind
{
    /// <summary>A column alias (PATH's node path).</summary>
    Column,

    /// <summary>The <c>ROOT('name')</c> wrapper.</summary>
    Root,

    /// <summary>The row tag — <c>RAW('elem')</c> / <c>PATH('row')</c>.</summary>
    Row,
}
