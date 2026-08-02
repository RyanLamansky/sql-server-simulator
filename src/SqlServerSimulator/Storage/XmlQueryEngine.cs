using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;

namespace SqlServerSimulator.Storage;

/// <summary>
/// Minimal XQuery evaluator backing the <c>xml</c> type's <c>.value()</c> /
/// <c>.nodes()</c> / <c>.query()</c> / <c>.exist()</c> methods, and supplying
/// the prolog parsing and XPath translation <c>.modify()</c>'s target paths run
/// through (<see cref="Parser.XmlDml"/>). Covers the subset SQL Server's sample
/// databases (AdventureWorks / WideWorldImporters) exercise: an optional
/// prolog of <c>declare default element namespace "uri";</c> and
/// <c>declare namespace prefix="uri";</c> declarations followed by a path
/// expression built from child / attribute steps (prefixed or unprefixed,
/// element names may contain <c>.</c>), <c>text()</c> node tests,
/// <c>string(.)</c>, parenthesized sub-paths with a positional predicate
/// (<c>(…)[1]</c>), and a trailing continuation path after such a group.
/// </summary>
/// <remarks>
/// <para>
/// The path is translated to XPath 1.0 and evaluated through
/// <see cref="XPathNavigator"/>. Each unprefixed/prefixed name test becomes a
/// <c>*[local-name()='…' and namespace-uri()='…']</c> (attribute steps use
/// <c>@*[…]</c>) predicate form, so the default-element-namespace binding —
/// which XPath 1.0 has no syntax for — is resolved at translation time
/// without a namespace manager. Attributes are never in the default element
/// namespace, matching XQuery's scoping rule.
/// </para>
/// <para>
/// The navigator is positioned on the document element of the parsed input,
/// so a relative path (<c>Edu.Level</c>) resolves against that element while
/// an absolute path (<c>/Resume/…</c>) resolves from the document root — the
/// dual behavior <c>.nodes()</c>-produced node references rely on, since each
/// row's value is the serialized outer XML of one matched node.
/// </para>
/// </remarks>
internal static class XmlQueryEngine
{
    /// <summary>
    /// Evaluates a <c>.value()</c> expression against <paramref name="xmlText"/>.
    /// Returns the string value of the first selected node (or the
    /// <c>string(.)</c> result), or null when the path selects nothing — the
    /// caller maps null to a typed SQL NULL.
    /// </summary>
    public static string? EvaluateScalar(string xmlText, string xquery)
    {
        var (defaultNamespace, prefixes, body) = ParsePrologAndBody(xquery);
        var navigator = XDocument.Parse(xmlText).Root!.CreateNavigator();

        // string(.) yields the context node's string value directly; the
        // trailing positional predicate XQuery allows on it (string(.)[1]) has
        // no XPath 1.0 equivalent, so it's handled before translation.
        if (body.Replace(" ", string.Empty, StringComparison.Ordinal).StartsWith("string(.)", StringComparison.Ordinal))
            return navigator.Value;

        var result = navigator.Evaluate(TranslateToXPath(body, defaultNamespace, prefixes));
        return result is XPathNodeIterator iterator
            ? iterator.MoveNext() ? iterator.Current!.Value : null
            : result?.ToString();
    }

    /// <summary>
    /// Evaluates a <c>.nodes()</c> expression against <paramref name="xmlText"/>,
    /// yielding the serialized outer XML of each matched node. In-scope
    /// namespace declarations are re-emitted on each fragment so a subsequent
    /// relative <c>.value()</c> / <c>.nodes()</c> against the fragment resolves
    /// the same names.
    /// </summary>
    public static IEnumerable<string> EvaluateNodes(string xmlText, string xquery)
    {
        var (defaultNamespace, prefixes, body) = ParsePrologAndBody(xquery);
        var navigator = XDocument.Parse(xmlText).Root!.CreateNavigator();
        var iterator = (XPathNodeIterator)navigator.Evaluate(TranslateToXPath(body, defaultNamespace, prefixes))!;
        while (iterator.MoveNext())
            yield return iterator.Current!.OuterXml;
    }

    /// <summary>
    /// Evaluates a <c>.exist()</c> expression against <paramref name="xmlText"/>:
    /// true when the path selects at least one node (or a true boolean / a
    /// non-empty string / a non-zero number). The caller maps a NULL instance
    /// to a typed SQL NULL before calling.
    /// </summary>
    public static bool EvaluateExists(string xmlText, string xquery)
    {
        var (defaultNamespace, prefixes, body) = ParsePrologAndBody(xquery);
        var navigator = XDocument.Parse(xmlText).Root!.CreateNavigator();
        return navigator.Evaluate(TranslateToXPath(body, defaultNamespace, prefixes)) switch
        {
            XPathNodeIterator iterator => iterator.MoveNext(),
            bool boolean => boolean,
            string text => text.Length > 0,
            double number => number != 0,
            var other => other is not null,
        };
    }

    /// <summary>
    /// Evaluates a <c>.query()</c> expression against <paramref name="xmlText"/>,
    /// returning the serialized concatenation of the matched nodes (in document
    /// order; empty string when nothing matches). The caller maps a NULL
    /// instance to a typed SQL NULL before calling.
    /// </summary>
    public static string EvaluateQuery(string xmlText, string xquery) =>
        string.Concat(EvaluateNodes(xmlText, xquery));

    /// <summary>
    /// Splits the leading <c>declare … ;</c> prolog from the path body and
    /// returns the default element namespace (null when none declared) plus
    /// the prefix→URI map.
    /// </summary>
    internal static (string? DefaultNamespace, Dictionary<string, string> Prefixes, string Body) ParsePrologAndBody(string xquery)
    {
        string? defaultNamespace = null;
        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);
        var index = 0;
        while (true)
        {
            while (index < xquery.Length && char.IsWhiteSpace(xquery[index]))
                index++;
            if (!xquery.AsSpan(index).StartsWith("declare", StringComparison.Ordinal))
                break;

            var semicolonOffset = xquery.AsSpan(index).IndexOf(';');
            if (semicolonOffset < 0)
                throw new NotSupportedException("Unterminated XQuery namespace declaration.");
            var declaration = xquery[index..(index + semicolonOffset)];

            var firstQuote = declaration.IndexOf('"', StringComparison.Ordinal);
            var secondQuote = firstQuote + 1 + declaration.AsSpan(firstQuote + 1).IndexOf('"');
            var uri = declaration[(firstQuote + 1)..secondQuote];
            if (declaration.Contains("default element namespace", StringComparison.Ordinal))
            {
                defaultNamespace = uri;
            }
            else
            {
                var keywordEnd = declaration.IndexOf("namespace", StringComparison.Ordinal) + "namespace".Length;
                var prefix = declaration[keywordEnd..declaration.IndexOf('=', StringComparison.Ordinal)].Trim();
                prefixes[prefix] = uri;
            }
            index += semicolonOffset + 1;
        }
        return (defaultNamespace, prefixes, xquery[index..].Trim());
    }

    /// <summary>
    /// Rewrites the path <paramref name="body"/> into XPath 1.0: each element /
    /// attribute name test becomes a namespace-explicit
    /// <c>*[local-name()='…' and namespace-uri()='…']</c> predicate; structural
    /// tokens (<c>/ ( ) [ ]</c>), node tests / functions written as
    /// <c>name(</c> (<c>text()</c>, <c>string(</c>), and predicate contents
    /// pass through unchanged.
    /// </summary>
    internal static string TranslateToXPath(string body, string? defaultNamespace, Dictionary<string, string> prefixes)
    {
        var output = new StringBuilder(body.Length * 4);
        var index = 0;
        var attributeAxis = false;
        while (index < body.Length)
        {
            var c = body[index];
            if (c == '@')
            {
                attributeAxis = true;
                index++;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = index;
                while (index < body.Length && (char.IsLetterOrDigit(body[index]) || body[index] is '.' or '-' or '_' or ':'))
                    index++;
                var name = body[start..index];

                // A name immediately followed by '(' is a function or node test
                // (text(), string(.)); emit it verbatim.
                var peek = index;
                while (peek < body.Length && char.IsWhiteSpace(body[peek]))
                    peek++;
                if (peek < body.Length && body[peek] == '(')
                {
                    _ = output.Append(name);
                    attributeAxis = false;
                    continue;
                }

                var colon = name.IndexOf(':', StringComparison.Ordinal);
                var local = colon >= 0 ? name[(colon + 1)..] : name;
                var uri = colon >= 0
                    ? (prefixes.TryGetValue(name[..colon], out var mapped)
                        ? mapped
                        : throw new NotSupportedException($"Undeclared XML namespace prefix '{name[..colon]}'."))
                    : attributeAxis ? string.Empty : defaultNamespace ?? string.Empty;

                _ = output.Append(attributeAxis ? "@*" : "*")
                    .Append("[local-name()='").Append(local)
                    .Append("' and namespace-uri()='").Append(uri).Append("']");
                attributeAxis = false;
                continue;
            }
            _ = output.Append(c);
            index++;
        }
        return output.ToString();
    }
}
