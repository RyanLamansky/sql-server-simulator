using System.Xml;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The byte count <c>DATALENGTH</c> reports for an <c>xml</c> value, which is
/// the size of real SQL Server's <em>parsed binary</em> form rather than of the
/// text it was written as. Two instances that parse identically report the same
/// size — <c>&lt;a/&gt;</c> and <c>&lt;a&gt;&lt;/a&gt;</c> are both 16 — and
/// text is counted in UTF-16 code units, so an astral character counts twice.
/// </summary>
/// <remarks>
/// <para>
/// Derived by probing SQL Server 2025 across element-name and text lengths,
/// sibling and nesting counts, attribute counts, namespace declarations and
/// prefix use. The structure that falls out is a name dictionary: a name costs
/// its full length the first time it appears and a flat 3 bytes thereafter,
/// which is why a document of repeated elements grows far more slowly than one
/// of distinct ones.
/// </para>
/// <para>
/// The namespace terms are an empirical fit and are the part most likely to be
/// wrong on a shape that wasn't probed; the element / attribute / text / comment
/// / PI terms each rest on a swept range. <c>XmlDataLengthCorpusTests</c> is the
/// guard — it replays a corpus of documents whose expected sizes came from the
/// reference server.
/// </para>
/// </remarks>
internal static class XmlBinarySize
{
    /// <summary>The empty-document overhead every instance pays.</summary>
    private const int DocumentOverhead = 5;

    /// <summary>A name's first appearance: this plus two bytes per character.</summary>
    private const int NewNameOverhead = 9;

    /// <summary>A name already in the dictionary, whatever its length.</summary>
    private const int SeenNameCost = 3;

    /// <summary>A prefix's first appearance: this plus two bytes per character.</summary>
    private const int NewPrefixOverhead = 6;

    /// <summary>
    /// Paid when a local name reappears under a different namespace than the one
    /// it was first seen in — the dictionary keys on the local name, so the
    /// binding has to be restated.
    /// </summary>
    private const int NamespaceSwitchCost = 4;

    /// <summary>An xmlns declaration: this, plus 4 per URI character and 2 per prefix character.</summary>
    private const int DeclarationOverhead = 20;

    /// <summary>
    /// Re-declaring a prefix already declared earlier in the document: this
    /// plus two per URI character, rather than the full
    /// <see cref="DeclarationOverhead"/> form.
    /// </summary>
    private const int RedeclarationOverhead = 4;

    /// <summary>
    /// Re-pointing the <em>default</em> namespace at a URI the document hasn't
    /// used yet: this plus four per URI character. A named prefix takes the
    /// cheaper <see cref="RedeclarationOverhead"/> form whatever URI it moves
    /// to, which is the one asymmetry in the namespace pricing.
    /// </summary>
    private const int DefaultRebindOverhead = 6;

    /// <summary>
    /// The extra bytes a counted string's length prefix takes once it no longer
    /// fits seven bits. The prefix is a 7-bit varint, so a string gains a byte
    /// at 128 characters and another at 16384 (both probe-confirmed at the
    /// boundary). Applies to every counted string: a name's first appearance, a
    /// text or comment node, a processing instruction's target and value, an
    /// attribute value and a namespace URI. A name already in the dictionary
    /// encodes no length, so it takes none.
    /// </summary>
    private static int LengthPrefix(int length)
    {
        var extra = 0;
        for (var remaining = length >> 7; remaining > 0; remaining >>= 7)
            extra++;
        return extra;
    }

    public static int Measure(string xml)
    {
        // CONTENT-typed: the value model admits several top-level elements and
        // top-level text, which only a fragment reader will accept.
        var settings = new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment,
            IgnoreWhitespace = false,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            DtdProcessing = DtdProcessing.Ignore,
        };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var declaredPrefixes = new HashSet<string>(StringComparer.Ordinal);
        var declaredUris = new HashSet<string>(StringComparer.Ordinal);
        var total = DocumentOverhead;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    total += NameCost(names, prefixes, reader.LocalName, reader.Prefix, reader.NamespaceURI);
                    if (reader.HasAttributes)
                    {
                        total++;
                        total += AttributeCosts(reader, names, prefixes, declaredPrefixes, declaredUris);
                    }
                    break;
                // Whitespace-only element content is not a node at all —
                // `<a>  </a>` measures exactly as `<a/>` does. Text that merely
                // *contains* surrounding spaces still counts in full, which is
                // why only the insignificant-whitespace kind is skipped.
                case XmlNodeType.Text:
                case XmlNodeType.CDATA:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.Comment:
                    total += 2 + (2 * reader.Value.Length) + LengthPrefix(reader.Value.Length);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    total += 5 + (2 * reader.Name.Length) + (2 * reader.Value.Length)
                        + LengthPrefix(reader.Name.Length) + LengthPrefix(reader.Value.Length);
                    break;
                default:
                    break;
            }
        }
        return total;
    }

    private static int AttributeCosts(
        XmlReader reader,
        Dictionary<string, string> names,
        HashSet<string> prefixes,
        HashSet<string> declaredPrefixes,
        HashSet<string> declaredUris)
    {
        var total = 0;
        if (!reader.MoveToFirstAttribute())
            return total;
        do
        {
            // An xmlns declaration is carried as an attribute but priced
            // differently: its URI costs four bytes a character, not the two a
            // written value costs, and its own name never enters the
            // dictionary. `xmlns="…"` reports prefix "" / local "xmlns";
            // `xmlns:p="…"` reports prefix "xmlns" / local "p".
            if (reader.Prefix == "xmlns" || (reader.Prefix.Length == 0 && reader.LocalName == "xmlns"))
            {
                // The default declaration prices as though it had a
                // one-character prefix, which is what separates it from
                // `xmlns:p` costing the same rather than two bytes more.
                var declaredPrefix = reader.Prefix == "xmlns" ? reader.LocalName : "";
                var prefixLength = declaredPrefix.Length == 0 ? 1 : declaredPrefix.Length;
                // A prefix declared once already is far cheaper to re-declare.
                // The exception is the default namespace being re-pointed at a
                // URI the document hasn't carried before, which pays a URI
                // cost again; a named prefix does not, however new its URI.
                var firstUseOfUri = declaredUris.Add(reader.Value);
                total += declaredPrefixes.Add(declaredPrefix)
                    ? DeclarationOverhead + (4 * reader.Value.Length) + (2 * prefixLength)
                        + LengthPrefix(reader.Value.Length)
                    : declaredPrefix.Length == 0 && firstUseOfUri
                        ? DefaultRebindOverhead + (4 * reader.Value.Length) + LengthPrefix(reader.Value.Length)
                        : RedeclarationOverhead + (2 * reader.Value.Length) + LengthPrefix(reader.Value.Length);
                continue;
            }
            // An ordinary attribute is one byte cheaper than the same name in
            // element position.
            total += NameCost(names, prefixes, reader.LocalName, reader.Prefix, reader.NamespaceURI) - 1
                + 2 + (2 * reader.Value.Length) + LengthPrefix(reader.Value.Length);
        }
        while (reader.MoveToNextAttribute());
        _ = reader.MoveToElement();
        return total;
    }

    private static int NameCost(
        Dictionary<string, string> names,
        HashSet<string> prefixes,
        string localName,
        string prefix,
        string namespaceUri)
    {
        var cost = prefix.Length > 0 && prefixes.Add(prefix)
            ? NewPrefixOverhead + (2 * prefix.Length) + LengthPrefix(prefix.Length)
            : 0;
        if (!names.TryGetValue(localName, out var firstNamespace))
        {
            names[localName] = namespaceUri;
            return cost + NewNameOverhead + (2 * localName.Length) + LengthPrefix(localName.Length);
        }
        return cost + SeenNameCost
            + (string.Equals(firstNamespace, namespaceUri, StringComparison.Ordinal) ? 0 : NamespaceSwitchCost);
    }
}
