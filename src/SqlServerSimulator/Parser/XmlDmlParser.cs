using System.Globalization;
using System.Text;
using SqlServerSimulator.Storage;

namespace SqlServerSimulator.Parser;

/// <summary>
/// Recursive-descent parser for the XML-DML text a <c>.modify()</c> call
/// carries, run once at compile time. It produces an <see cref="XmlDml"/> and
/// raises every diagnostic real settles before the first row is read: the
/// statement-shape errors (Msg 6305 / 2205 / 2209) and the static target /
/// content type checks (Msg 2226 / 2207 / 2258 / 2240 / 2249 / 2337 / 2356 /
/// 2264 / 9310), in real's own check order.
/// </summary>
internal sealed class XmlDmlParser(
    string text,
    string? defaultNamespace,
    Dictionary<string, string> prefixes,
    ParserContext context,
    Func<string, SqlType>? resolveColumnType)
{
    private readonly string text = text;
    private readonly string? defaultNamespace = defaultNamespace;
    private readonly Dictionary<string, string> prefixes = prefixes;
    private readonly ParserContext context = context;
    private readonly Func<string, SqlType>? resolveColumnType = resolveColumnType;
    private int index;

    /// <summary>Parses the whole statement, leaving nothing unconsumed.</summary>
    public XmlDml ParseStatement()
    {
        this.SkipWhitespace();
        // Real distinguishes "this parses as XQuery but isn't XML-DML" (Msg
        // 6305) from "this isn't XQuery at all" (Msg 2209); the simulator
        // splits on the leading keyword, so a path expression reports 6305 and
        // anything else that fails after a DML keyword reports 2209.
        return this.TryKeyword("insert") ? this.ParseInsert()
            : this.TryKeyword("delete") ? this.ParseDelete()
            : this.TryKeyword("replace") ? this.ParseReplaceValueOf()
            : throw SimulatedSqlException.XmlDmlExpressionRequired();
    }

    private XmlDml ParseDelete()
    {
        var target = this.ParsePath(this.text.Length);
        return target.Kind == XmlDmlNodeKind.Document
            ? throw SimulatedSqlException.XmlDmlOnlyNodesDeletable(target.Describe())
            : XmlDml.CreateDelete(target, this.defaultNamespace, this.prefixes);
    }

    private XmlDml ParseReplaceValueOf()
    {
        if (!this.TryKeyword("value") || !this.TryKeyword("of"))
            throw this.SyntaxError();

        var withIndex = this.FindKeyword("with");
        if (withIndex < 0)
            throw SimulatedSqlException.XmlDmlWithExpected();
        var target = this.ParsePath(withIndex);
        this.index = withIndex + "with".Length;

        // Real checks the target's cardinality first, then its kind — an
        // unbracketed path reports 2337 whatever it selects.
        if (!target.Singleton)
            throw SimulatedSqlException.XmlDmlReplaceTargetNotSingleton(target.Describe());
        if (target.Kind is not (XmlDmlNodeKind.Attribute or XmlDmlNodeKind.Text))
            throw SimulatedSqlException.XmlDmlReplaceTargetNotSimpleContent(target.Describe());

        this.SkipWhitespace();
        if (this.Current is '<')
            throw SimulatedSqlException.XmlDmlReplaceWithConstructedXml();
        var value = this.ParseTerms(terminator: '\0');
        this.SkipWhitespace();
        return this.index < this.text.Length
            ? throw this.SyntaxError()
            : XmlDml.CreateReplaceValueOf(target, value, this.defaultNamespace, this.prefixes);
    }

    private XmlDml ParseInsert()
    {
        var content = this.ParseContentSequence();

        XmlDmlPosition position;
        if (this.TryKeyword("as"))
        {
            position = this.TryKeyword("first") ? XmlDmlPosition.AsFirst
                : this.TryKeyword("last") ? XmlDmlPosition.AsLast
                : throw this.SyntaxError();
            if (!this.TryKeyword("into"))
                throw this.SyntaxError();
        }
        else
        {
            position = this.TryKeyword("into") ? XmlDmlPosition.Into
                : this.TryKeyword("before") ? XmlDmlPosition.Before
                : this.TryKeyword("after") ? XmlDmlPosition.After
                : throw this.SyntaxError();
        }

        var target = this.ParsePath(this.text.Length);

        // Check order is real's, probed one shape at a time: target
        // cardinality, then the content's own type, then the
        // attribute-with-a-position rule, then the target's node kind.
        if (!target.Singleton)
            throw SimulatedSqlException.XmlDmlInsertTargetNotSingleton(target.Describe());
        foreach (var item in content)
        {
            if (item.Kind == XmlDmlItemKind.Value && item.Enclosed[0][0].StaticType is { } staticType && staticType is not XmlSqlType)
                throw SimulatedSqlException.XmlDmlOnlyNodesInsertable(XmlDml.XQueryTypeName(staticType, item.Enclosed[0][0].IsLiteral));
        }
        var positional = position is XmlDmlPosition.Before or XmlDmlPosition.After;
        if (positional)
        {
            foreach (var item in content)
            {
                if (item.Kind == XmlDmlItemKind.Attribute)
                    throw SimulatedSqlException.XmlDmlAttributeInsertHasPosition($"attribute({item.Name},xdt:untypedAtomic)");
            }
        }
        if (positional)
        {
            if (target.Kind is XmlDmlNodeKind.Attribute or XmlDmlNodeKind.Document)
                throw SimulatedSqlException.XmlDmlInsertBeforeAfterTargetKind(target.Describe());
        }
        else if (target.Kind is not (XmlDmlNodeKind.Element or XmlDmlNodeKind.Document))
        {
            throw SimulatedSqlException.XmlDmlInsertIntoTargetKind(target.Describe());
        }

        return XmlDml.CreateInsert(target, content, position, this.defaultNamespace, this.prefixes);
    }

    /// <summary>
    /// Parses an <c>insert</c>'s content: either a parenthesized sequence of
    /// items or a single item.
    /// </summary>
    private XmlDmlItem[] ParseContentSequence()
    {
        this.SkipWhitespace();
        if (this.Current != '(')
            return [this.ParseContentItem()];

        this.index++;
        var items = new List<XmlDmlItem>();
        while (true)
        {
            items.Add(this.ParseContentItem());
            this.SkipWhitespace();
            if (this.Current == ',')
            {
                this.index++;
                continue;
            }
            if (this.Current != ')')
                throw this.SyntaxError();
            this.index++;
            return [.. items];
        }
    }

    private XmlDmlItem ParseContentItem()
    {
        this.SkipWhitespace();
        var c = this.Current;
        if (c == '<')
            return this.ParseDirectConstructor();
        if (c is '"' or '\'' || char.IsAsciiDigit(c) || c == '-')
            return XmlDmlItem.Value(this.ParseTerms(terminator: '\0', single: true));

        var word = this.PeekWord();
        switch (word)
        {
            case "attribute":
                this.index += word.Length;
                var attributeName = this.ReadConstructorName();
                return XmlDmlItem.Computed(XmlDmlItemKind.Attribute, attributeName, this.ParseBracedTerms());
            case "comment":
                this.index += word.Length;
                return XmlDmlItem.Computed(XmlDmlItemKind.Comment, string.Empty, this.ParseBracedTerms());
            case "element":
                throw new NotSupportedException("A computed 'element {…}' constructor in XML-DML content isn't modeled; write the element directly.");
            case "processing-instruction":
                this.index += word.Length;
                var target = this.ReadConstructorName();
                return XmlDmlItem.Computed(XmlDmlItemKind.ProcessingInstruction, target, this.ParseBracedTerms());
            case "text":
                this.index += word.Length;
                return XmlDmlItem.Computed(XmlDmlItemKind.Text, string.Empty, this.ParseBracedTerms());
            default:
                return XmlDmlItem.Value(this.ParseTerms(terminator: '\0', single: true));
        }
    }

    /// <summary>
    /// Scans a direct constructor — an element with arbitrary nesting, a
    /// comment, or a processing instruction — keeping its markup as literal
    /// segments interleaved with the enclosed <c>{…}</c> expressions. Doubled
    /// braces are the XQuery escape for a literal brace.
    /// </summary>
    private XmlDmlItem ParseDirectConstructor()
    {
        if (this.text.AsSpan(this.index).StartsWith("<!--", StringComparison.Ordinal))
            return this.ScanDelimited("-->");
        if (this.text.AsSpan(this.index).StartsWith("<?", StringComparison.Ordinal))
            return this.ScanDelimited("?>");

        var literals = new List<string>();
        var enclosed = new List<XmlDmlTerm[]>();
        var inAttribute = new List<bool>();
        var segment = new StringBuilder();
        var depth = 0;
        var inTag = false;
        var closingTag = false;
        var quote = '\0';
        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (c == '{' && this.Peek(1) == '{')
            {
                _ = segment.Append('{');
                this.index += 2;
                continue;
            }
            if (c == '}' && this.Peek(1) == '}')
            {
                _ = segment.Append('}');
                this.index += 2;
                continue;
            }
            if (c == '{')
            {
                literals.Add(segment.ToString());
                _ = segment.Clear();
                this.index++;
                enclosed.Add(this.ParseTerms(terminator: '}'));
                inAttribute.Add(quote != '\0');
                this.index++;
                continue;
            }
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (!inTag)
            {
                if (c == '<')
                {
                    inTag = true;
                    closingTag = this.Peek(1) == '/';
                }
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                _ = segment.Append(c);
                this.index++;
                continue;
            }
            if (c == '/' && this.Peek(1) == '>')
            {
                _ = segment.Append("/>");
                this.index += 2;
                inTag = false;
                if (depth == 0)
                    return Finish();
                continue;
            }
            if (c == '>')
            {
                _ = segment.Append('>');
                this.index++;
                inTag = false;
                if (closingTag)
                {
                    depth--;
                    if (depth == 0)
                        return Finish();
                }
                else
                {
                    depth++;
                }
                continue;
            }
            _ = segment.Append(c);
            this.index++;
        }
        throw this.SyntaxError();

        XmlDmlItem Finish()
        {
            literals.Add(segment.ToString());
            return XmlDmlItem.Markup([.. literals], [.. enclosed], [.. inAttribute]);
        }
    }

    /// <summary>
    /// Scans a comment / processing-instruction constructor, whose body is
    /// literal text up to its closing delimiter.
    /// </summary>
    private XmlDmlItem ScanDelimited(string terminator)
    {
        var end = this.text.IndexOf(terminator, this.index, StringComparison.Ordinal);
        if (end < 0)
            throw this.SyntaxError();
        var markup = this.text[this.index..(end + terminator.Length)];
        this.index = end + terminator.Length;
        return XmlDmlItem.Markup([markup], [], []);
    }

    /// <summary>Reads the name of a computed <c>attribute</c> / <c>processing-instruction</c> constructor.</summary>
    private string ReadConstructorName()
    {
        this.SkipWhitespace();
        var start = this.index;
        while (this.index < this.text.Length && (char.IsLetterOrDigit(this.text[this.index]) || this.text[this.index] is '_' or '-' or '.' or ':'))
            this.index++;
        return this.index == start ? throw this.SyntaxError() : this.text[start..this.index];
    }

    /// <summary>Reads a <c>{ terms }</c> body.</summary>
    private XmlDmlTerm[] ParseBracedTerms()
    {
        this.SkipWhitespace();
        if (this.Current != '{')
            throw this.SyntaxError();
        this.index++;
        var terms = this.ParseTerms(terminator: '}');
        this.index++;
        return terms;
    }

    /// <summary>
    /// Parses one or more value terms, optionally parenthesized as a sequence,
    /// up to <paramref name="terminator"/> (or the end of the text when it is
    /// <c>'\0'</c>). <paramref name="single"/> refuses the sequence form, which
    /// is how a bare content item stays one item.
    /// </summary>
    private XmlDmlTerm[] ParseTerms(char terminator, bool single = false)
    {
        this.SkipWhitespace();
        if (!single && this.Current == '(')
        {
            this.index++;
            var sequence = new List<XmlDmlTerm> { this.ParseTerm() };
            while (true)
            {
                this.SkipWhitespace();
                if (this.Current == ',')
                {
                    this.index++;
                    sequence.Add(this.ParseTerm());
                    continue;
                }
                if (this.Current != ')')
                    throw this.SyntaxError();
                this.index++;
                this.SkipWhitespace();
                return terminator != '\0' && this.Current != terminator ? throw this.SyntaxError() : [.. sequence];
            }
        }

        var term = this.ParseTerm();
        this.SkipWhitespace();
        return terminator != '\0' && this.Current != terminator ? throw this.SyntaxError() : [term];
    }

    private XmlDmlTerm ParseTerm()
    {
        this.SkipWhitespace();
        var c = this.Current;
        if (c is '"' or '\'')
            return XmlDmlTerm.FromLiteral(SqlValue.FromString(SqlType.NVarchar, this.ReadQuoted(c)));
        if (char.IsAsciiDigit(c) || c == '-')
            return XmlDmlTerm.FromLiteral(this.ReadNumber());

        var word = this.PeekWord();
        if (word is not ("sql:column" or "sql:variable"))
            throw this.SyntaxError();
        this.index += word.Length;
        this.SkipWhitespace();
        if (this.Current != '(')
            throw this.SyntaxError();
        this.index++;
        this.SkipWhitespace();
        if (this.Current is not ('"' or '\''))
            throw this.SyntaxError();
        var name = this.ReadQuoted(this.Current);
        this.SkipWhitespace();
        if (this.Current != ')')
            throw this.SyntaxError();
        this.index++;

        if (word[4] == 'v')
        {
            // The Variables dict is keyed without the '@' the XQuery text
            // writes. The parse-time lookup validates the variable was declared
            // (Msg 137) and supplies the static type Msg 2207 reports.
            var bare = name.StartsWith('@') ? name[1..] : name;
            return XmlDmlTerm.FromVariable(bare, this.context.Batch.GetVariableSlot(bare).DeclaredType);
        }
        return XmlDmlTerm.FromColumn(name, this.resolveColumnType?.Invoke(name));
    }

    private string ReadQuoted(char quote)
    {
        this.index++;
        var sb = new StringBuilder();
        while (this.index < this.text.Length)
        {
            var c = this.text[this.index];
            if (c == quote)
            {
                // A doubled delimiter is XQuery's escape for one of itself.
                if (this.Peek(1) == quote)
                {
                    _ = sb.Append(quote);
                    this.index += 2;
                    continue;
                }
                this.index++;
                return sb.ToString();
            }
            _ = sb.Append(c);
            this.index++;
        }
        throw this.SyntaxError();
    }

    private SqlValue ReadNumber()
    {
        var start = this.index;
        if (this.Current == '-')
            this.index++;
        var fractional = false;
        while (this.index < this.text.Length && (char.IsAsciiDigit(this.text[this.index]) || this.text[this.index] == '.'))
        {
            fractional |= this.text[this.index] == '.';
            this.index++;
        }
        var span = this.text.AsSpan(start, this.index - start);
        if (!fractional && int.TryParse(span, CultureInfo.InvariantCulture, out var integer))
            return SqlValue.FromInt32(integer);
        if (!decimal.TryParse(span, CultureInfo.InvariantCulture, out var number))
            throw this.SyntaxError();
        var digits = span.TrimStart('-');
        var scale = digits.Length - digits.IndexOf('.') - 1;
        return SqlValue.FromDecimal(DecimalSqlType.Get(Math.Max(digits.Length - 1, scale + 1), scale), number);
    }

    /// <summary>
    /// Takes the path text between the cursor and <paramref name="end"/>, and
    /// derives the static node type real reports in the target-check messages.
    /// </summary>
    private XmlDmlPath ParsePath(int end)
    {
        this.SkipWhitespace();
        var body = this.text[this.index..end].Trim();
        this.index = end;
        if (body.Length == 0)
            throw this.SyntaxError();

        // Only a positional predicate over the whole path makes it singular;
        // a predicate on an inner step (`/r/a[1]/text()`) leaves the static
        // type plural, which is what real reports.
        var analyzed = body;
        var singleton = false;
        while (analyzed.Length > 1 && analyzed[0] == '(' && MatchingParen(analyzed) is var close && close > 0)
        {
            var trailer = analyzed[(close + 1)..].TrimStart();
            if (trailer.Length == 0)
            {
                analyzed = analyzed[1..close].Trim();
                continue;
            }
            if (trailer[0] != '[' || trailer[^1] != ']')
                break;
            singleton = true;
            analyzed = analyzed[1..close].Trim();
        }

        var compiled = XmlQueryEngine.CompileBody(body, this.defaultNamespace, this.prefixes, "modify");
        if (analyzed == ".")
            return new XmlDmlPath(body, compiled, XmlDmlNodeKind.Document, string.Empty, singleton);

        var (kind, name) = ClassifyStep(LastStep(analyzed));
        return new XmlDmlPath(body, compiled, kind, name, singleton);
    }

    /// <summary>Index of the <c>)</c> closing the parenthesis at position 0, or -1.</summary>
    private static int MatchingParen(string body)
    {
        var depth = 0;
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == '(')
                depth++;
            else if (body[i] == ')' && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>The path's final step, ignoring <c>/</c> inside parentheses or predicates.</summary>
    private static string LastStep(string path)
    {
        var depth = 0;
        for (var i = path.Length - 1; i >= 0; i--)
        {
            var c = path[i];
            if (c is ')' or ']')
                depth++;
            else if (c is '(' or '[')
                depth--;
            else if (c == '/' && depth == 0)
                return path[(i + 1)..].Trim();
        }
        return path.Trim();
    }

    /// <summary>Maps a final step to the node kind and name real names in its target-check message.</summary>
    private static (XmlDmlNodeKind Kind, string Name) ClassifyStep(string step)
    {
        var bracket = step.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
            step = step[..bracket].TrimEnd();
        if (step.StartsWith('@'))
        {
            var attribute = step[1..];
            var colon = attribute.LastIndexOf(':');
            return (XmlDmlNodeKind.Attribute, colon >= 0 ? attribute[(colon + 1)..] : attribute);
        }
        if (step.StartsWith("text(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.Text, string.Empty);
        if (step.StartsWith("comment(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.Comment, string.Empty);
        if (step.StartsWith("processing-instruction(", StringComparison.Ordinal))
            return (XmlDmlNodeKind.ProcessingInstruction, string.Empty);
        var localColon = step.LastIndexOf(':');
        return (XmlDmlNodeKind.Element, localColon >= 0 ? step[(localColon + 1)..] : step);
    }

    /// <summary>The next word starting at the cursor, without consuming it.</summary>
    private string PeekWord()
    {
        var end = this.index;
        while (end < this.text.Length && (char.IsLetter(this.text[end]) || this.text[end] is '-' or ':'))
            end++;
        return this.text[this.index..end];
    }

    /// <summary>Consumes <paramref name="word"/> when it sits at the cursor as a whole word.</summary>
    private bool TryKeyword(string word)
    {
        this.SkipWhitespace();
        if (!this.PeekWord().Equals(word, StringComparison.Ordinal))
            return false;
        this.index += word.Length;
        return true;
    }

    /// <summary>
    /// Finds <paramref name="word"/> as a whole word outside quotes, braces,
    /// parentheses and markup — how the <c>with</c> that splits
    /// <c>replace value of</c> is located.
    /// </summary>
    private int FindKeyword(string word)
    {
        var depth = 0;
        var quote = '\0';
        for (var i = this.index; i < this.text.Length; i++)
        {
            var c = this.text[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }
            if (c is '(' or '[' or '{' or '<')
            {
                depth++;
                continue;
            }
            if (c is ')' or ']' or '}' or '>')
            {
                depth--;
                continue;
            }
            if (depth != 0 || c != word[0] || !this.text.AsSpan(i).StartsWith(word, StringComparison.Ordinal))
                continue;
            if ((i > 0 && IsWordChar(this.text[i - 1])) || (i + word.Length < this.text.Length && IsWordChar(this.text[i + word.Length])))
                continue;
            return i;
        }
        return -1;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or ':';

    private char Current => this.index < this.text.Length ? this.text[this.index] : '\0';

    private char Peek(int offset) => this.index + offset < this.text.Length ? this.text[this.index + offset] : '\0';

    private void SkipWhitespace()
    {
        while (this.index < this.text.Length && char.IsWhiteSpace(this.text[this.index]))
            this.index++;
    }

    /// <summary>
    /// Msg 2209 naming the token at the cursor — real quotes the offending
    /// word, or <c>&lt;eof&gt;</c> when the text ran out.
    /// </summary>
    private SimulatedSqlException SyntaxError()
    {
        this.SkipWhitespace();
        if (this.index >= this.text.Length)
            return SimulatedSqlException.XmlDmlSyntaxError("<eof>");
        var word = this.PeekWord();
        return SimulatedSqlException.XmlDmlSyntaxError(word.Length > 0 ? word : this.text[this.index].ToString());
    }
}
