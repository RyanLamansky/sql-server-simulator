using System.Globalization;
using System.Text;
using System.Xml;
using SqlServerSimulator.Parser.Tokens;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The bindings a <c>WITH XMLNAMESPACES ('uri' AS prefix | DEFAULT 'uri', …)</c>
/// prefix declares for every <c>FOR XML</c> clause in its statement: they make
/// a prefixed name legal where Msg 6846 would otherwise fire, and they emit as
/// <c>xmlns</c> attributes on the serialized document's outermost element(s).
/// </summary>
/// <remarks>
/// The prefix comparison is ordinal — real declares <c>p</c> and still refuses
/// <c>P:a</c> (probe-confirmed) — and the emitted declarations run in
/// <em>reverse</em> declaration order, which is the order real writes them.
/// </remarks>
internal sealed class ForXmlNamespaces
{
    /// <summary>The one predefined prefix, which needs no declaration and emits none.</summary>
    private const string XmlPrefix = "xml";

    /// <summary>The URI the <c>xml</c> prefix is permanently bound to.</summary>
    private const string XmlPrefixUri = "http://www.w3.org/XML/1998/namespace";

    /// <summary>The declared prefixes, ordinal, excluding the DEFAULT binding.</summary>
    private readonly HashSet<string> prefixes;

    /// <summary>
    /// The <c>xmlns</c> attribute text this clause contributes, ready to append
    /// after an element's name — leading space included, empty when the clause
    /// declared nothing that emits (only the predefined <c>xml</c> prefix).
    /// </summary>
    public readonly string DeclarationText;

    private ForXmlNamespaces(HashSet<string> prefixes, string declarationText)
    {
        this.prefixes = prefixes;
        this.DeclarationText = declarationText;
    }

    /// <summary>Whether <paramref name="prefix"/> was declared (ordinal match).</summary>
    public bool IsDeclared(string prefix) => this.prefixes.Contains(prefix);

    /// <summary>
    /// Parses the clause with <see cref="ParserContext.Token"/> on the
    /// <c>XMLNAMESPACES</c> keyword, leaving the cursor on the first token past
    /// the closing parenthesis. Every binding is validated as it is read, in
    /// real's own order — reserved prefix (Msg 6871), prefix XML can't carry
    /// (Msg 6870), the <c>xml</c> prefix / URI pairing (Msg 6872), an empty URI
    /// (Msg 6874), then a prefix already bound (Msg 6869) — so the clause
    /// raises even on a statement carrying no <c>FOR XML</c> at all.
    /// </summary>
    internal static ForXmlNamespaces Parse(ParserContext context)
    {
        if (context.GetNextRequired() is not Operator { Character: '(' })
            throw SimulatedSqlException.SyntaxErrorNear(context);

        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        var declared = new List<string>();
        var sawDefault = false;

        while (true)
        {
            string? prefix;
            string uri;
            if (context.GetNextRequired() is ReservedKeyword { Keyword: Keyword.Default })
            {
                prefix = null;
                uri = ReadUri(context.GetNextRequired(), context);
            }
            else
            {
                uri = ReadUri(context.Token, context);
                if (context.GetNextRequired() is not ReservedKeyword { Keyword: Keyword.As })
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                // A delimited identifier reaches the prefix rules the same way
                // an unquoted one does — real reports Msg 6870 for [p q].
                if (context.GetNextRequired() is not Name prefixName)
                    throw SimulatedSqlException.SyntaxErrorNear(context);
                prefix = prefixName.Value;
                ValidatePrefix(prefix);
            }

            var isXmlPrefix = prefix == XmlPrefix;
            if (isXmlPrefix != (uri == XmlPrefixUri))
                throw SimulatedSqlException.XmlNamespaceXmlPrefixMisbound(isXmlPrefix ? (byte)1 : (byte)2);
            if (uri.Length == 0)
                throw SimulatedSqlException.XmlNamespaceEmptyUri();

            if (prefix is null)
            {
                // Real names the DEFAULT binding 'default' in the redefinition
                // message, lower-cased whatever the keyword's written case.
                if (sawDefault)
                    throw SimulatedSqlException.XmlNamespaceRedefined("default");
                sawDefault = true;
                declared.Add(Declaration("xmlns", uri));
            }
            else if (!isXmlPrefix)
            {
                if (!prefixes.Add(prefix))
                    throw SimulatedSqlException.XmlNamespaceRedefined(prefix);
                declared.Add(Declaration("xmlns:" + prefix, uri));
            }

            if (context.GetNextRequired() is Operator { Character: ')' })
                break;
            if (context.Token is not Operator { Character: ',' })
                throw SimulatedSqlException.SyntaxErrorNear(context);
        }

        context.MoveNextRequired();

        var text = new StringBuilder();
        for (var i = declared.Count - 1; i >= 0; i--)
            _ = text.Append(declared[i]);
        return new ForXmlNamespaces(prefixes, text.ToString());
    }

    /// <summary>One <c>xmlns</c> attribute, its URI attribute-value escaped.</summary>
    private static string Declaration(string name, string uri)
    {
        var sb = new StringBuilder(" ").Append(name).Append("=\"");
        Selection.AppendForXmlText(sb, uri, isAttribute: true);
        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Reads one binding's URI literal, which real requires to be written out
    /// (a variable in the position is Msg 102).
    /// </summary>
    private static string ReadUri(Token? token, ParserContext context) =>
        token is Literal { Value.Type.Category: SqlTypeCategory.String } literal
            ? literal.Value.AsString
            : throw SimulatedSqlException.SyntaxErrorNear(context);

    /// <summary>
    /// Enforces the two rules on a written prefix: <c>xmlns</c> is reserved
    /// (Msg 6871) and the rest must be an XML name (Msg 6870, naming the first
    /// character at fault).
    /// </summary>
    private static void ValidatePrefix(string prefix)
    {
        if (prefix == "xmlns")
            throw SimulatedSqlException.XmlNamespacePrefixReserved();
        for (var i = 0; i < prefix.Length; i++)
        {
            var c = prefix[i];
            if (i == 0 ? !XmlConvert.IsStartNCNameChar(c) : !XmlConvert.IsNCNameChar(c))
                throw SimulatedSqlException.XmlNamespacePrefixNotAName(prefix, c);
        }
    }

    /// <summary>
    /// The declaration text a serializer writes on the document's outermost
    /// element: the <c>xsi</c> binding <c>ELEMENTS XSINIL</c> needs first, then
    /// this clause's own. Empty when neither applies.
    /// </summary>
    internal static string TopLevelDeclarations(bool xsinil, ForXmlNamespaces? namespaces)
    {
        var own = namespaces?.DeclarationText ?? "";
        return xsinil ? string.Create(CultureInfo.InvariantCulture, $" xmlns:xsi=\"{Selection.XsiNamespace}\"{own}") : own;
    }
}
