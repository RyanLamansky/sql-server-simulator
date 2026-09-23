using System.Text;
using System.Xml;

namespace SqlServerSimulator.Storage;

/// <summary>
/// The well-formedness check real's XML parser applies to every value
/// converted to <c>xml</c>: one linear pass over the text that raises the
/// 9400-family error real raises, at the position real reports.
/// </summary>
/// <remarks>
/// <para>
/// An instance is CONTENT, not a document — several top-level elements, text
/// beside them, and empty input are all legal — so what the pass checks is
/// that every markup construct is complete and nested, every reference
/// resolves, every character is an XML one, and every prefix is declared.
/// </para>
/// <para>
/// Probed against SQL Server 2025 (2026-09-23), a few hundred malformed shapes
/// each one statement per batch. The rules the messages follow:
/// </para>
/// <list type="bullet">
/// <item>A position is the character the parser stopped on, and the last
/// character when the input ran out first — which is why most end-of-input
/// shapes are Msg 9400 but some report the construct they were in the middle
/// of (<c>&lt;a x</c> is Msg 9414, <c>&lt;a&gt;&lt;/a</c> Msg 9412,
/// <c>&amp;am</c> Msg 9411).</item>
/// <item>The checks on a whole start tag — declarations, prefixes, duplicate
/// attributes — run when its closing <c>&gt;</c> is read and report there:
/// declarations first, then the element's prefix, then each attribute in
/// order, so of two faults the earlier attribute's wins.</item>
/// <item>A reference is checked at its <c>;</c>: an undeclared entity, or a
/// character reference that isn't an XML character.</item>
/// <item>An end tag with nothing but whitespace before it is Msg 9455 at the
/// <c>/</c>; after any content it is a mismatch, Msg 9436 at its
/// <c>&gt;</c>.</item>
/// <item>A declared encoding is checked against the source's width: a
/// <c>varchar</c> source can't switch to UTF-16 and an <c>nvarchar</c> one
/// can't switch to an 8-bit encoding.</item>
/// </list>
/// </remarks>
internal static class XmlWellFormedness
{
    private const string XmlNamespaceUri = "http://www.w3.org/XML/1998/namespace";

    /// <summary>
    /// Returns <paramref name="text"/> when it is well-formed XML content,
    /// else raises the error real raises for it. <paramref name="nationalSource"/>
    /// says whether the value arrived as UTF-16 (<c>nvarchar</c> and its
    /// family, or an <c>xml</c> parameter), which decides the encodings an
    /// XML declaration may name.
    /// </summary>
    public static string Checked(string text, bool nationalSource)
    {
        new Scanner(text, nationalSource).Run();
        return text;
    }

    private sealed class Scanner(string text, bool nationalSource)
    {
        private readonly string s = text;
        private readonly int n = text.Length;
        private readonly bool nationalSource = nationalSource;
        private readonly int documentStart = text is ['\uFEFF', ..] ? 1 : 0;
        private readonly List<(int Start, int Length)> openElements = [];
        private readonly List<List<(string Prefix, string Uri)>?> namespaceFrames = [];
        private readonly List<Attribute> attributes = [];
        private int p;

        /// <summary>
        /// True while nothing but whitespace, comments and processing
        /// instructions has been read, the state in which <c>&lt;/</c> reads
        /// as a malformed name rather than a stray end tag.
        /// </summary>
        private bool prologOnly = true;

        public void Run()
        {
            this.p = this.documentStart;
            while (this.p < this.n)
            {
                var c = this.s[this.p];
                switch (c)
                {
                    case '&':
                        this.Reference();
                        this.prologOnly = false;
                        break;
                    case '<':
                        this.Markup();
                        break;
                    default:
                        if (c == ']' && this.At(this.p + 1, ']') && this.At(this.p + 2, '>'))
                            throw this.Error(XmlParseError.CDataEndInContent, this.p);
                        if (!IsWhitespace(c))
                            this.prologOnly = false;
                        this.Character();
                        break;
                }
            }

            if (this.openElements.Count > 0)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
        }

        private void Markup()
        {
            var lessThan = this.p;
            if (lessThan + 1 >= this.n)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
            switch (this.s[lessThan + 1])
            {
                case '!':
                    this.Bang();
                    break;
                case '/':
                    this.EndTag();
                    break;
                case '?':
                    this.ProcessingInstruction(lessThan == this.documentStart);
                    break;
                default:
                    this.StartTag();
                    break;
            }
        }

        private void StartTag()
        {
            this.p++;
            var name = this.QualifiedName();
            this.attributes.Clear();
            var afterElementName = true;
            while (true)
            {
                var whitespace = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                switch (this.s[this.p])
                {
                    case '>':
                        this.CompleteStartTag(name, this.p, empty: false);
                        this.p++;
                        return;
                    case '/':
                        if (this.p + 1 >= this.n)
                            throw this.Error(XmlParseError.GreaterThanExpected, this.n - 1);
                        if (this.s[this.p + 1] != '>')
                            throw this.Error(XmlParseError.GreaterThanExpected, this.p + 1);
                        this.CompleteStartTag(name, this.p + 1, empty: true);
                        this.p += 2;
                        return;
                }

                if (whitespace == 0)
                    throw this.Error(afterElementName ? XmlParseError.IllegalQualifiedNameCharacter : XmlParseError.WhitespaceExpected, this.p);
                afterElementName = false;

                var attributeName = this.QualifiedName();
                _ = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.EqualExpected, this.n - 1);
                if (this.s[this.p] != '=')
                    throw this.Error(XmlParseError.EqualExpected, this.p);
                this.p++;
                _ = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.StringLiteralExpected, this.n - 1);
                var quote = this.s[this.p];
                if (quote is not ('"' or '\''))
                    throw this.Error(XmlParseError.StringLiteralExpected, this.p);
                this.p++;
                var valueStart = this.p;
                while (true)
                {
                    if (this.p >= this.n)
                        throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                    var c = this.s[this.p];
                    if (c == quote)
                        break;
                    if (c == '<')
                        throw this.Error(XmlParseError.LessThanInAttributeValue, this.p);
                    if (c == '&')
                        this.Reference();
                    else
                        this.Character();
                }

                this.attributes.Add(new Attribute(attributeName, valueStart, this.p));
                this.p++;
            }
        }

        /// <summary>
        /// The checks real makes once a start tag is complete, reporting at
        /// <paramref name="at"/>, its closing <c>&gt;</c>.
        /// </summary>
        private void CompleteStartTag(QName name, int at, bool empty)
        {
            List<(string Prefix, string Uri)>? declared = null;
            foreach (var attribute in this.attributes)
            {
                if (this.DeclaredPrefix(attribute.Name) is not { } prefix)
                    continue;
                if (prefix == "xmlns")
                    throw this.Error(XmlParseError.XmlnsPrefixDeclared, at);
                var uri = this.s[attribute.ValueStart..attribute.ValueEnd];
                // The xml prefix and its URI belong only to each other.
                if (prefix == "xml" ? uri != XmlNamespaceUri : uri == XmlNamespaceUri)
                    throw this.Error(XmlParseError.XmlPrefixRebound, at);
                if (prefix.Length > 0 && uri.Length == 0)
                    throw this.Error(XmlParseError.EmptyNamespaceUri, at);
                declared ??= [];
                foreach (var (existing, _) in declared)
                {
                    if (existing == prefix)
                        throw this.Error(XmlParseError.RedeclaredPrefix, at);
                }
                declared.Add((prefix, uri));
            }

            if (name.HasPrefix && this.ResolvePrefix(this.Prefix(name), declared) is null)
                throw this.Error(XmlParseError.UndeclaredPrefix, at);

            List<(string Uri, string Local)>? seen = null;
            foreach (var attribute in this.attributes)
            {
                if (this.DeclaredPrefix(attribute.Name) is not null)
                    continue;
                var uri = attribute.Name.HasPrefix
                    ? this.ResolvePrefix(this.Prefix(attribute.Name), declared) ?? throw this.Error(XmlParseError.UndeclaredPrefix, at)
                    : "";
                var local = this.Local(attribute.Name);
                seen ??= [];
                foreach (var (seenUri, seenLocal) in seen)
                {
                    if (seenUri == uri && seenLocal == local)
                        throw this.Error(XmlParseError.DuplicateAttribute, at);
                }
                seen.Add((uri, local));
            }

            this.prologOnly = false;
            if (!empty)
            {
                this.openElements.Add((name.Start, name.Length));
                this.namespaceFrames.Add(declared);
            }
        }

        /// <summary>
        /// The prefix an <c>xmlns</c> / <c>xmlns:p</c> attribute declares
        /// (<c>""</c> for the default namespace), or null for any other
        /// attribute.
        /// </summary>
        private string? DeclaredPrefix(QName name) =>
            name.HasPrefix
                ? this.Prefix(name) == "xmlns" ? this.Local(name) : null
                : this.s.AsSpan(name.Start, name.Length) is "xmlns" ? "" : null;

        private string? ResolvePrefix(string prefix, List<(string Prefix, string Uri)>? declared)
        {
            if (prefix == "xml")
                return XmlNamespaceUri;
            if (FindPrefix(declared, prefix) is { } own)
                return own;
            for (var i = this.namespaceFrames.Count - 1; i >= 0; i--)
            {
                if (FindPrefix(this.namespaceFrames[i], prefix) is { } inherited)
                    return inherited;
            }
            return null;
        }

        private static string? FindPrefix(List<(string Prefix, string Uri)>? frame, string prefix)
        {
            if (frame is not null)
            {
                foreach (var (declaredPrefix, uri) in frame)
                {
                    if (declaredPrefix == prefix)
                        return uri;
                }
            }
            return null;
        }

        private void EndTag()
        {
            if (this.openElements.Count == 0 && this.prologOnly)
                throw this.Error(XmlParseError.IllegalQualifiedNameCharacter, this.p + 1);
            this.p += 2;
            if (this.p >= this.n)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
            var name = this.QualifiedName();
            _ = this.SkipWhitespace();
            if (this.p >= this.n)
                throw this.Error(XmlParseError.GreaterThanExpected, this.n - 1);
            if (this.s[this.p] != '>')
                throw this.Error(XmlParseError.GreaterThanExpected, this.p);

            var depth = this.openElements.Count;
            if (depth == 0
                || !this.s.AsSpan(name.Start, name.Length).SequenceEqual(this.s.AsSpan(this.openElements[depth - 1].Start, this.openElements[depth - 1].Length)))
            {
                throw this.Error(XmlParseError.EndTagMismatch, this.p);
            }
            this.openElements.RemoveAt(depth - 1);
            this.namespaceFrames.RemoveAt(depth - 1);
            this.p++;
        }

        private void Bang()
        {
            var at = this.p + 2;
            if (at >= this.n)
                throw this.Error(XmlParseError.IncorrectDocumentSyntax, this.n - 1);
            switch (this.s[at])
            {
                case '-':
                    this.Comment();
                    return;
                case '[':
                    this.CData();
                    return;
                case 'D' when this.openElements.Count == 0:
                    this.DocumentType();
                    return;
                default:
                    throw this.Error(XmlParseError.IncorrectDocumentSyntax, at);
            }
        }

        private void Comment()
        {
            var second = this.p + 3;
            if (second >= this.n)
                throw this.Error(XmlParseError.IncorrectCommentSyntax, this.n - 1);
            if (this.s[second] != '-')
                throw this.Error(XmlParseError.IncorrectCommentSyntax, second);
            this.p = second + 1;
            while (true)
            {
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.IncorrectCommentSyntax, this.n - 1);
                if (this.s[this.p] == '-' && this.At(this.p + 1, '-'))
                {
                    // `--` may only close the comment.
                    if (this.p + 2 >= this.n)
                        throw this.Error(XmlParseError.GreaterThanExpected, this.n - 1);
                    if (this.s[this.p + 2] != '>')
                        throw this.Error(XmlParseError.GreaterThanExpected, this.p + 2);
                    this.p += 3;
                    return;
                }
                this.Character();
            }
        }

        private void CData()
        {
            const string Opening = "<![CDATA[";
            for (var i = 2; i < Opening.Length; i++)
            {
                if (this.p + i >= this.n)
                    throw this.Error(XmlParseError.IncorrectCDataSyntax, this.n - 1);
                if (this.s[this.p + i] != Opening[i])
                    throw this.Error(XmlParseError.IncorrectCDataSyntax, this.p + i);
            }

            this.p += Opening.Length;
            this.prologOnly = false;
            while (true)
            {
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.IncorrectCDataSyntax, this.n - 1);
                if (this.s[this.p] == ']' && this.At(this.p + 1, ']') && this.At(this.p + 2, '>'))
                {
                    this.p += 3;
                    return;
                }
                this.Character();
            }
        }

        /// <summary>
        /// A <c>DOCTYPE</c> is refused once it is complete (Msg 6359, the one
        /// member of the family with no position); an incomplete one runs
        /// out of input first.
        /// </summary>
        private void DocumentType()
        {
            const string Keyword = "<!DOCTYPE";
            for (var i = 2; i < Keyword.Length; i++)
            {
                if (this.p + i >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                if (this.s[this.p + i] != Keyword[i])
                    throw this.Error(XmlParseError.IncorrectDocumentSyntax, this.p + i);
            }

            var subsetDepth = 0;
            char? quote = null;
            for (var i = this.p + Keyword.Length; i < this.n; i++)
            {
                var c = this.s[i];
                if (quote is { } open)
                {
                    if (c == open)
                        quote = null;
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '[')
                {
                    subsetDepth++;
                }
                else if (c == ']')
                {
                    subsetDepth--;
                }
                else if (c == '>' && subsetDepth <= 0)
                {
                    throw SimulatedSqlException.XmlInternalSubsetDtdNotAllowed();
                }
            }
            throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
        }

        private void ProcessingInstruction(bool atDocumentStart)
        {
            var targetStart = this.p + 2;
            if (targetStart >= this.n)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
            if (this.NameCharWidth(targetStart, start: true) == 0)
                throw this.Error(XmlParseError.IllegalNameCharacter, targetStart);
            this.p = targetStart;
            this.SkipName(allowColon: true);
            var target = this.s.AsSpan(targetStart, this.p - targetStart);

            if (target is "xml")
            {
                if (!atDocumentStart)
                    throw this.Error(XmlParseError.XmlDeclarationNotAtBeginning, this.p);
                this.XmlDeclaration();
                return;
            }
            if (target.Equals("xml", StringComparison.OrdinalIgnoreCase))
                throw this.Error(XmlParseError.ReservedXmlName, this.p);

            if (this.p >= this.n)
                throw this.Error(XmlParseError.IncorrectProcessingInstructionSyntax, this.n - 1);
            if (this.s[this.p] != '?' && !IsWhitespace(this.s[this.p]))
                throw this.Error(XmlParseError.IllegalNameCharacter, this.p);
            while (true)
            {
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.IncorrectProcessingInstructionSyntax, this.n - 1);
                if (this.s[this.p] == '?' && this.At(this.p + 1, '>'))
                {
                    this.p += 2;
                    return;
                }
                this.Character();
            }
        }

        /// <summary>
        /// <c>&lt;?xml version="1.0" [encoding="…"] [standalone="yes|no"] ?&gt;</c>,
        /// the pseudo-attributes in that order. Every departure is Msg 9441 —
        /// at the character after a misplaced name, or at the closing quote of
        /// a bad value — and a declaration missing its version reports at the
        /// <c>&gt;</c>.
        /// </summary>
        private void XmlDeclaration()
        {
            string? encoding = null;
            var nextExpected = 0;
            while (true)
            {
                var whitespace = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                if (this.s[this.p] == '?')
                {
                    if (this.p + 1 >= this.n)
                        throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                    if (this.s[this.p + 1] != '>' || nextExpected == 0)
                        throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, this.p + 1);
                    if (encoding is not null)
                        this.CheckEncoding(encoding, this.p + 1);
                    this.p += 2;
                    return;
                }
                if (whitespace == 0)
                    throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, this.p);

                var nameStart = this.p;
                while (this.p < this.n && char.IsAsciiLetter(this.s[this.p]))
                    this.p++;
                var slot = this.s.AsSpan(nameStart, this.p - nameStart) switch
                {
                    "encoding" when nextExpected is 1 => 1,
                    "standalone" when nextExpected is 1 or 2 => 2,
                    "version" when nextExpected is 0 => 0,
                    _ => throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, this.p),
                };

                _ = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                if (this.s[this.p] != '=')
                    throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, this.p);
                this.p++;
                _ = this.SkipWhitespace();
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                var quote = this.s[this.p];
                if (quote is not ('"' or '\''))
                    throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, this.p);
                var valueStart = this.p + 1;
                var valueEnd = this.s.IndexOf(quote, valueStart);
                if (valueEnd < 0)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                var value = this.s.AsSpan(valueStart, valueEnd - valueStart);
                switch (slot)
                {
                    case 0 when value is not "1.0":
                    case 2 when value is not ("yes" or "no"):
                        throw this.Error(XmlParseError.IncorrectXmlDeclarationSyntax, valueEnd);
                    case 1:
                        encoding = value.ToString();
                        break;
                }
                nextExpected = slot + 1;
                this.p = valueEnd + 1;
            }
        }

        /// <summary>
        /// Whether the declared encoding is one real recognizes, and whether
        /// the value's own width can switch to it, reported at the
        /// declaration's closing <c>&gt;</c>.
        /// </summary>
        private void CheckEncoding(string encoding, int at)
        {
            bool twoByte;
            if (encoding.Equals("utf-16", StringComparison.OrdinalIgnoreCase) || encoding.Equals("ucs-2", StringComparison.OrdinalIgnoreCase))
            {
                twoByte = true;
            }
            else
            {
                try
                {
                    var resolved = Encoding.GetEncoding(encoding);
                    if (resolved is UTF32Encoding or UnicodeEncoding)
                        throw this.Error(XmlParseError.UnrecognizedEncoding, at);
                }
                catch (ArgumentException)
                {
                    throw this.Error(XmlParseError.UnrecognizedEncoding, at);
                }
                twoByte = false;
            }

            if (twoByte != this.nationalSource)
                throw this.Error(XmlParseError.UnableToSwitchEncoding, at);
        }

        /// <summary>
        /// An entity or character reference, starting at its <c>&amp;</c>.
        /// </summary>
        private void Reference()
        {
            this.p++;
            if (this.p >= this.n)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);

            if (this.s[this.p] == '#')
            {
                this.p++;
                var hex = this.At(this.p, 'x');
                if (hex)
                    this.p++;
                var digitError = hex ? XmlParseError.HexadecimalDigitExpected : XmlParseError.DecimalDigitExpected;
                if (this.p >= this.n)
                    throw this.Error(digitError, this.n - 1);
                var value = 0;
                var digits = 0;
                while (this.p < this.n && DigitValue(this.s[this.p], hex) is >= 0 and var digit)
                {
                    value = Math.Min((value * (hex ? 16 : 10)) + digit, 0x110000);
                    digits++;
                    this.p++;
                }
                if (digits == 0)
                    throw this.Error(digitError, this.p);
                this.RequireSemicolon();
                if (!IsXmlCodePoint(value))
                    throw this.Error(XmlParseError.IllegalXmlCharacter, this.p);
                this.p++;
                return;
            }

            if (this.NameCharWidth(this.p, start: true) == 0)
                throw this.Error(XmlParseError.IllegalNameCharacter, this.p);
            var nameStart = this.p;
            this.SkipName(allowColon: true);
            this.RequireSemicolon();
            if (this.s.AsSpan(nameStart, this.p - nameStart) is not ("amp" or "apos" or "gt" or "lt" or "quot"))
                throw this.Error(XmlParseError.UndeclaredEntity, this.p);
            this.p++;
        }

        private void RequireSemicolon()
        {
            if (this.p >= this.n)
                throw this.Error(XmlParseError.SemicolonExpected, this.n - 1);
            if (this.s[this.p] != ';')
                throw this.Error(XmlParseError.SemicolonExpected, this.p);
        }

        /// <summary>
        /// Reads an element or attribute name — an NCName with at most one
        /// prefix — advancing past it.
        /// </summary>
        private QName QualifiedName()
        {
            var start = this.p;
            if (this.p >= this.n)
                throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
            if (this.NameCharWidth(this.p, start: true) == 0)
                throw this.Error(XmlParseError.IllegalQualifiedNameCharacter, this.p);
            this.SkipName(allowColon: false);

            var colon = -1;
            if (this.At(this.p, ':'))
            {
                colon = this.p;
                this.p++;
                if (this.p >= this.n)
                    throw this.Error(XmlParseError.UnexpectedEndOfInput, this.n - 1);
                if (this.NameCharWidth(this.p, start: true) == 0)
                    throw this.Error(XmlParseError.IllegalQualifiedNameCharacter, this.p);
                this.SkipName(allowColon: false);
                if (this.At(this.p, ':'))
                    throw this.Error(XmlParseError.MultipleColons, this.p);
            }

            return new QName(start, this.p - start, colon);
        }

        private string Prefix(QName name) => this.s[name.Start..name.Colon];

        private string Local(QName name) =>
            name.HasPrefix ? this.s[(name.Colon + 1)..(name.Start + name.Length)] : this.s.Substring(name.Start, name.Length);

        private void SkipName(bool allowColon)
        {
            while (this.p < this.n)
            {
                var width = this.NameCharWidth(this.p, start: false);
                if (width == 0 && allowColon && this.s[this.p] == ':')
                    width = 1;
                if (width == 0)
                    return;
                this.p += width;
            }
        }

        /// <summary>
        /// The width in UTF-16 units of the name character at
        /// <paramref name="index"/> (a supplementary character takes two), or
        /// 0 when there isn't one; the colon is never an NCName character.
        /// </summary>
        private int NameCharWidth(int index, bool start)
        {
            var c = this.s[index];
            if (char.IsHighSurrogate(c))
                return index + 1 < this.n && char.IsLowSurrogate(this.s[index + 1]) ? 2 : 0;
            return (start ? XmlConvert.IsStartNCNameChar(c) : XmlConvert.IsNCNameChar(c)) ? 1 : 0;
        }

        /// <summary>
        /// Advances past one character of text, raising Msg 9420 for one XML
        /// doesn't admit — a control character, a lone surrogate, U+FFFE / U+FFFF.
        /// </summary>
        private void Character()
        {
            var c = this.s[this.p];
            if (char.IsHighSurrogate(c))
            {
                if (this.p + 1 < this.n && char.IsLowSurrogate(this.s[this.p + 1]))
                {
                    this.p += 2;
                    return;
                }
                throw this.Error(XmlParseError.IllegalXmlCharacter, this.p);
            }
            if (!IsXmlCodePoint(c))
                throw this.Error(XmlParseError.IllegalXmlCharacter, this.p);
            this.p++;
        }

        private int SkipWhitespace()
        {
            var start = this.p;
            while (this.p < this.n && IsWhitespace(this.s[this.p]))
                this.p++;
            return this.p - start;
        }

        private bool At(int index, char c) => index < this.n && this.s[index] == c;

        private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r';

        private static bool IsXmlCodePoint(int c) =>
            c is '\t' or '\n' or '\r' or (>= 0x20 and <= 0xD7FF) or (>= 0xE000 and <= 0xFFFD) or (>= 0x10000 and <= 0x10FFFF);

        private static int DigitValue(char c, bool hex) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' when hex => c - 'a' + 10,
            >= 'A' and <= 'F' when hex => c - 'A' + 10,
            _ => -1,
        };

        /// <summary>
        /// The error at <paramref name="index"/>, positioned as real positions
        /// it: the line, counting LF, CR LF and a lone CR as one break each,
        /// and the character within it, counting a surrogate pair once.
        /// </summary>
        private SimulatedSqlException Error(XmlParseError error, int index)
        {
            var line = 1;
            var lineStart = this.documentStart;
            for (var i = this.documentStart; i < index; i++)
            {
                if (this.s[i] == '\n' || (this.s[i] == '\r' && !this.At(i + 1, '\n')))
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            var character = 0;
            for (var i = lineStart; i <= index && i < this.n; i++)
            {
                if (!(char.IsLowSurrogate(this.s[i]) && i > lineStart && char.IsHighSurrogate(this.s[i - 1])))
                    character++;
            }
            return SimulatedSqlException.XmlParsingFailed(error, line, character);
        }

        private readonly struct QName(int start, int length, int colon)
        {
            public readonly int Start = start;
            public readonly int Length = length;

            /// <summary>The index of the prefix's colon, or -1 when there is no prefix.</summary>
            public readonly int Colon = colon;

            public bool HasPrefix => this.Colon >= 0;
        }

        private readonly struct Attribute(QName name, int valueStart, int valueEnd)
        {
            public readonly QName Name = name;
            public readonly int ValueStart = valueStart;
            public readonly int ValueEnd = valueEnd;
        }
    }
}
