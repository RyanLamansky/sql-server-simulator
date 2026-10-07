namespace SqlServerSimulator.Parser.FullText;

/// <summary>
/// A parsed <c>contains_search_condition</c> — the string literal inside
/// <c>CONTAINS</c> / <c>CONTAINSTABLE</c>, or the word list inside
/// <c>FREETEXT</c> / <c>FREETEXTTABLE</c> — together with the two facts the
/// caller needs after parsing: whether any term was dropped as a stopword (real
/// reports the severity-10 <b>Msg 9927</b> for that) and the root of the match
/// tree.
/// </summary>
/// <remarks>
/// <para>Modeled grammar, each form probe-confirmed against SQL Server 2025:</para>
/// <list type="bullet">
/// <item><description>Simple term (<c>word</c>) and phrase
/// (<c>"two words"</c>).</description></item>
/// <item><description>Prefix (<c>"pre*"</c>) — the star has meaning only
/// <b>inside</b> the quotes, and there it applies per whitespace-separated
/// word, so <c>"al* be*"</c> asks for two prefixes. Unquoted <c>pre*</c> is the
/// ordinary word <c>pre</c>, and a star anywhere but a word's end is a break
/// character.</description></item>
/// <item><description><c>AND</c> / <c>OR</c> / <c>AND NOT</c> with their symbol
/// spellings <c>&amp;</c> / <c>|</c> / <c>&amp;!</c>, and
/// parentheses.</description></item>
/// <item><description><c>NEAR</c> / <c>~</c> and the generic form
/// <c>NEAR((a, b), distance [, TRUE|FALSE])</c>, where the distance counts
/// <b>intervening terms</b> (0 = adjacent) and <c>MAX</c> or an omitted
/// distance means "anywhere in the same row".</description></item>
/// <item><description><c>FORMSOF(INFLECTIONAL | THESAURUS, word, …)</c> —
/// the inflectional form expands through <see cref="FullTextLexicon.Stem"/>,
/// and the thesaurus form matches only the word itself, which is what real's
/// shipped (empty) thesaurus files give.</description></item>
/// <item><description><c>ISABOUT(term [WEIGHT(n)], …)</c> — an OR for
/// row matching; the weights steer <c>RANK</c> only.</description></item>
/// </list>
/// <para>
/// Syntax errors report real's <b>Msg 7630</b> with the condition text quoted
/// whole, at the state real chose for each shape: state 1 when input ran out
/// (<c>'(quick'</c> → near <c>&lt;end of input&gt;</c>), state 2 when a
/// punctuation token stood where a term belonged (<c>'ISABOUT()'</c> → near
/// <c>)</c>; an unterminated quote reports near <c>"</c>), and state 3 when a
/// word did (<c>'quick AND AND fox'</c> → near <c>fox</c>, because an operator
/// keyword standing in operand position is an ordinary word).
/// </para>
/// </remarks>
internal sealed class FullTextSearchCondition(FullTextNode root, bool sawStopword)
{
    public readonly FullTextNode Root = root;

    /// <summary>
    /// True when at least one term in the condition word-broke to a system
    /// stopword. Real answers such a query with no rows and the informational
    /// Msg 9927; the simulator raises the same message through the
    /// <c>InfoMessage</c> surface.
    /// </summary>
    public readonly bool SawStopword = sawStopword;

    /// <summary>Tests one row's terms against the condition.</summary>
    public bool Matches(FullTextDocument document) => this.Root.Matches(document);

    /// <summary>
    /// Parses a <c>CONTAINS</c>-style condition. <paramref name="accentSensitive"/>
    /// comes from the catalog backing the table's index, so the condition's own
    /// terms fold exactly the way the indexed content did;
    /// <paramref name="language"/> names the stoplist and morphology, and
    /// <paramref name="stoplist"/> is false when the index uses none.
    /// <paramref name="report"/>, when given, collects the leaves the way
    /// <c>sys.dm_fts_parser</c> lists them.
    /// </summary>
    public static FullTextSearchCondition ParseContains(string condition, bool accentSensitive, FullTextLanguage language, bool stoplist, FullTextParserReport? report = null)
    {
        var parser = new ConditionParser(condition, accentSensitive, language, stoplist ? language : null, report);
        var root = parser.ParseOr();
        parser.ExpectEnd();
        return new FullTextSearchCondition(root, parser.SawStopword);
    }

    /// <summary>
    /// Parses a <c>FREETEXT</c> string: the whole text is word-broken, every
    /// stopword drops out, and what remains is OR-ed together after inflectional
    /// expansion — probe-confirmed (<c>FREETEXT(body, 'quick geese')</c> returns
    /// the rows holding either, and <c>FREETEXT(body, 'mouse')</c> finds a row
    /// holding <c>mice</c>). Punctuation and quotes carry no operator meaning
    /// here; they are break characters like any other.
    /// </summary>
    public static FullTextSearchCondition ParseFreeText(string condition, bool accentSensitive, FullTextLanguage language, bool stoplist)
    {
        var sawStopword = false;
        List<FullTextNode> alternatives = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var term in FullTextWordBreaker.Break(condition, accentSensitive))
        {
            if (stoplist && language.IsNoise(term.Text))
            {
                sawStopword = true;
                // A noise word the lexicon inflects still reaches its forms.
                if (language.EnglishMorphology && FullTextLexicon.HasIrregularForms(term.Text) && seen.Add(term.Text))
                    alternatives.Add(FullTextTermNode.Word(term.Text, inflectional: true));
                continue;
            }
            if (seen.Add(term.Text))
                alternatives.Add(FullTextTermNode.Word(term.Text, inflectional: language.EnglishMorphology));
        }
        // A string with nothing to search at all (`'!'`) reports noise too
        // (probed 2026-10-05 against SQL Server 2025).
        sawStopword |= alternatives.Count == 0;
        var root = alternatives.Count switch
        {
            0 => FullTextNode.NeverMatches,
            1 => alternatives[0],
            _ => FullTextBooleanNode.Or(alternatives),
        };
        return new FullTextSearchCondition(root, sawStopword);
    }

    /// <summary>
    /// Recursive-descent parser over the condition text. Holds the cursor and
    /// the accent fold; every error it raises carries the whole original
    /// condition, matching real's message.
    /// </summary>
    private sealed class ConditionParser(string condition, bool accentSensitive, FullTextLanguage language, FullTextLanguage? stoplist, FullTextParserReport? report)
    {
        private readonly string text = condition;
        private int index;

        public bool SawStopword;

        public FullTextNode ParseOr()
        {
            var left = ParseAnd();
            while (true)
            {
                var checkpoint = this.index;
                if (ReadOperator() is not ConditionOperator.Or)
                {
                    this.index = checkpoint;
                    return left;
                }
                left = FullTextBooleanNode.Or([left, ParseAnd()]);
            }
        }

        private FullTextNode ParseAnd()
        {
            var left = ParseNear();
            while (true)
            {
                var checkpoint = this.index;
                var op = ReadOperator();
                if (op is not (ConditionOperator.And or ConditionOperator.AndNot))
                {
                    this.index = checkpoint;
                    return left;
                }
                var right = ParseNear();
                left = op == ConditionOperator.And
                    ? FullTextBooleanNode.And([left, right])
                    : FullTextBooleanNode.AndNot(left, right);
            }
        }

        private FullTextNode ParseNear()
        {
            var left = ParsePrimary();
            List<FullTextNode>? chain = null;
            while (true)
            {
                var checkpoint = this.index;
                if (ReadOperator() is not ConditionOperator.Near)
                {
                    this.index = checkpoint;
                    break;
                }
                chain ??= [left];
                chain.Add(ParsePrimary());
            }
            // The infix form carries no distance, and real matches it at row
            // scope: `aaa NEAR bbb` returned every row holding both, however
            // far apart (probe over rows with 0 through 12 intervening terms).
            return chain is null ? left : FullTextProximityNode.Create(chain, maximumDistance: null, ordered: false);
        }

        private FullTextNode ParsePrimary()
        {
            SkipWhitespace();
            if (this.index >= this.text.Length)
                throw SimulatedSqlException.FullTextSyntaxErrorAtEnd(this.text);

            var ch = this.text[this.index];
            switch (ch)
            {
                case '(':
                    this.index++;
                    var grouped = ParseOr();
                    SkipWhitespace();
                    if (this.index >= this.text.Length)
                        throw SimulatedSqlException.FullTextSyntaxErrorAtEnd(this.text);
                    if (this.text[this.index] != ')')
                        throw ErrorAtCurrentToken();
                    this.index++;
                    return grouped;

                case '"':
                    return ParseQuotedTerm(inflectional: false);

                case ')':
                case ',':
                    throw SimulatedSqlException.FullTextSyntaxErrorNearPunctuation(ch.ToString(), this.text);

                default:
                    return ParseWordPrimary();
            }
        }

        /// <summary>
        /// A bare word, or one of the three parenthesized constructs it can
        /// introduce. An operator keyword reaching operand position is an
        /// ordinary word — real's own reading, which is why
        /// <c>'quick AND AND fox'</c> reports near <c>fox</c> rather than near
        /// the second <c>AND</c>.
        /// </summary>
        private FullTextNode ParseWordPrimary()
        {
            var word = ReadWord();
            if (word.Length == 0)
                throw ErrorAtCurrentToken();

            var afterWord = this.index;
            SkipWhitespace();
            var followedByParen = this.index < this.text.Length && this.text[this.index] == '(';
            if (followedByParen)
            {
                if (IsKeyword(word, "FORMSOF"))
                    return ParseFormsOf();
                if (IsKeyword(word, "ISABOUT"))
                    return ParseIsAbout();
                if (IsKeyword(word, "NEAR"))
                    return ParseGenericNear();
            }
            this.index = afterWord;
            return BuildTermFromText(word, quoted: false, inflectional: false);
        }

        /// <summary>
        /// <c>"phrase"</c> or <c>"prefix*"</c>. A trailing star inside the
        /// quotes makes the last element a prefix; anywhere else the star is
        /// just a break character (probe: <c>'"*quick"'</c> matches rows holding
        /// <c>quick</c>).
        /// </summary>
        private FullTextNode ParseQuotedTerm(bool inflectional)
        {
            this.index++; // opening quote
            var start = this.index;
            while (this.index < this.text.Length && this.text[this.index] != '"')
                this.index++;
            if (this.index >= this.text.Length)
                throw SimulatedSqlException.FullTextSyntaxErrorNearPunctuation("\"", this.text);
            var body = this.text[start..this.index];
            this.index++; // closing quote

            return BuildTermFromText(body, quoted: true, inflectional);
        }

        /// <summary>
        /// Word-breaks a term's own text the same way the indexed content was
        /// broken, so a multi-position result becomes a phrase whose positions
        /// each accept any term the breaker put there — a compound's composite
        /// or its first part, a number or its <c>nn</c> companion. A
        /// whitespace-separated word written with a trailing star inside quotes
        /// becomes a prefix element: real applies the star per word, not once
        /// per quoted term (probe: <c>'"al* be*"'</c> matches <c>alpha beta</c>),
        /// so a starred phrase breaks word by word. Records whether any
        /// position held only noise.
        /// </summary>
        private FullTextNode BuildTermFromText(string body, bool quoted, bool inflectional)
        {
            List<FullTextTerm> terms;
            HashSet<int> prefixPositions = [];
            var words = body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var starred = false;
            foreach (var word in words)
                starred |= quoted && word.EndsWith('*');
            if (!starred)
            {
                terms = FullTextWordBreaker.BreakWithMarkers(body, accentSensitive);
            }
            else
            {
                terms = [];
                var basePosition = 0;
                foreach (var chunk in words)
                {
                    // The star means "prefix" only inside quotes; unquoted
                    // `ch*` is the ordinary word `ch`, which is what real
                    // matches on.
                    var word = chunk.TrimEnd('*');
                    var broken = FullTextWordBreaker.BreakWithMarkers(word, accentSensitive);
                    var last = 0;
                    foreach (var term in broken)
                    {
                        terms.Add(new FullTextTerm(term.Text, basePosition + term.Position, term.Kind));
                        last = Math.Max(last, term.Position);
                        // A starred compound prefixes every part (`"red-hot*"`
                        // finds `reds hotter`).
                        if (chunk.EndsWith('*') && term.Kind == FullTextTermKind.Word)
                            _ = prefixPositions.Add(basePosition + term.Position);
                    }
                    basePosition += last;
                }
            }

            var morphological = inflectional && language.EnglishMorphology;
            // The source a report lists is the term as written, a quoted one
            // without its stars.
            report?.AddLeaf(quoted ? string.Join(' ', body.Split(' ').Select(static w => w.TrimEnd('*'))) : body, terms, morphological, stoplist);

            List<FullTextElement> elements = [];
            var firstPosition = 0;
            for (var i = 0; i < terms.Count;)
            {
                if (terms[i].Kind != FullTextTermKind.Word)
                {
                    i++;
                    continue;
                }
                var position = terms[i].Position;
                if (firstPosition == 0)
                    firstPosition = position;
                List<string> all = [];
                List<string> searchable = [];
                var noiseAtPosition = false;
                for (; i < terms.Count && terms[i].Position == position; i++)
                {
                    if (terms[i].Kind != FullTextTermKind.Word)
                        continue;
                    all.Add(terms[i].Text);
                    if (stoplist is null || !stoplist.IsNoise(terms[i].Text))
                        searchable.Add(terms[i].Text);
                    else
                        noiseAtPosition = true;
                }
                var offset = position - firstPosition;
                if (prefixPositions.Contains(position))
                {
                    // A starred position prefixes every term there, noise
                    // included: `"the*"` finds `theory`, and `"07/26/*"`
                    // reaches `nn79` through `07`'s companion `nn7`.
                    elements.Add(new FullTextElement(offset, [.. all], prefix: true, wildcard: false));
                    continue;
                }
                // A noise part of a compound reports noise although the
                // composite searches on (`well-known`, probed 2026-10-05
                // against SQL Server 2025).
                this.SawStopword |= noiseAtPosition;
                if (searchable.Count > 0)
                {
                    elements.Add(new FullTextElement(offset, [.. searchable], prefix: false, wildcard: false));
                    continue;
                }
                this.SawStopword = true;
                var singleCharacter = all.Find(static t => t.Length == 1 && char.IsLetterOrDigit(t[0]));
                if (morphological && singleCharacter is not null)
                {
                    // Inflection reaches a noise word of a single character:
                    // a letter becomes its forms (`x` → `x's`), which then
                    // have to match, and a digit has none, so its leaf
                    // matches nothing; a longer one (`of`) has no forms and
                    // stays out.
                    elements.Add(new FullTextElement(offset, [.. FullTextLexicon.InflectionalForms(singleCharacter)], prefix: false, wildcard: false));
                    continue;
                }
                // So does one the lexicon inflects (`see` → `saw`, `seen`):
                // the word itself never reaches the index, so its stem finds
                // just its forms.
                if (morphological && all.Find(FullTextLexicon.HasIrregularForms) is { } inflected)
                {
                    elements.Add(new FullTextElement(offset, [inflected], prefix: false, wildcard: false));
                    continue;
                }
                elements.Add(new FullTextElement(offset, [], prefix: false, wildcard: true));
            }
            // A term with no word in it at all (`'`, `""`) reports noise as
            // a stopword does (probed 2026-10-05 against SQL Server 2025).
            this.SawStopword |= firstPosition == 0;
            // A starred phrase holding a noise word matches nothing (probe:
            // `"word of mou*"` found no `word of mouth`, `"red-ho*"` found
            // `red-hot`).
            var noiseInPrefix = prefixPositions.Count > 0 && elements.Exists(static e => e.Wildcard);
            return elements.Count == 0 || noiseInPrefix
                ? FullTextNode.NeverMatches
                : FullTextTermNode.Create([.. elements], morphological);
        }

        private FullTextNode ParseFormsOf()
        {
            ExpectPunctuation('(');
            SkipWhitespace();
            var kindStart = this.index;
            var kind = ReadWord();
            var inflectional = IsKeyword(kind, "INFLECTIONAL");
            if (!inflectional && !IsKeyword(kind, "THESAURUS"))
            {
                this.index = kindStart;
                throw ErrorAtCurrentToken();
            }
            List<FullTextNode> alternatives = [];
            do
            {
                ExpectPunctuation(',');
                SkipWhitespace();
                if (this.index < this.text.Length && this.text[this.index] == '"')
                {
                    alternatives.Add(ParseQuotedTerm(inflectional));
                }
                else
                {
                    var word = ReadWord();
                    if (word.Length == 0)
                        throw ErrorAtCurrentToken();
                    // THESAURUS expands through the shipped thesaurus files,
                    // which are empty out of the box — probe: FORMSOF(THESAURUS,
                    // run) matches only literal `run`.
                    alternatives.Add(BuildTermFromText(word, quoted: false, inflectional));
                }
                SkipWhitespace();
            }
            while (this.index < this.text.Length && this.text[this.index] == ',');
            ExpectPunctuation(')');
            return alternatives.Count == 1 ? alternatives[0] : FullTextBooleanNode.Or(alternatives);
        }

        private FullTextNode ParseIsAbout()
        {
            ExpectPunctuation('(');
            List<FullTextNode> alternatives = [];
            List<double> weights = [];
            while (true)
            {
                SkipWhitespace();
                alternatives.Add(ParsePrimary());
                SkipWhitespace();
                var weight = 1.0;
                var checkpoint = this.index;
                var word = ReadWord();
                if (IsKeyword(word, "WEIGHT"))
                {
                    ExpectPunctuation('(');
                    weight = ReadWeight();
                    ExpectPunctuation(')');
                }
                else
                {
                    this.index = checkpoint;
                }
                weights.Add(weight);
                SkipWhitespace();
                if (this.index < this.text.Length && this.text[this.index] == ',')
                {
                    this.index++;
                    continue;
                }
                break;
            }
            ExpectPunctuation(')');
            return FullTextBooleanNode.Weighted(alternatives, [.. weights]);
        }

        /// <summary>
        /// <c>NEAR(a, b, …)</c> or <c>NEAR((a, b, …) [, distance [, TRUE|FALSE]])</c>.
        /// The distance counts intervening terms and <c>MAX</c> lifts the bound;
        /// the third argument requires the terms in the written order (probe:
        /// <c>NEAR((bbb, aaa), 2, TRUE)</c> matched only the row spelling
        /// <c>bbb aaa</c>).
        /// </summary>
        private FullTextNode ParseGenericNear()
        {
            ExpectPunctuation('(');
            SkipWhitespace();
            var parenthesizedList = this.index < this.text.Length && this.text[this.index] == '(';
            if (parenthesizedList)
                this.index++;

            List<FullTextNode> terms = [];
            while (true)
            {
                SkipWhitespace();
                terms.Add(ParsePrimary());
                SkipWhitespace();
                if (this.index < this.text.Length && this.text[this.index] == ',')
                {
                    // In the bare form the comma always introduces another
                    // term; in the parenthesized form the list ends at its own
                    // `)` and the distance / order arguments follow.
                    this.index++;
                    continue;
                }
                break;
            }
            // A proximity needs two terms: `NEAR(apple)` and `NEAR((apple), 2)`
            // are Msg 7630 at the list's closing parenthesis.
            if (terms.Count < 2)
                throw ErrorAtCurrentToken();

            int? maximumDistance = null;
            var ordered = false;
            if (parenthesizedList)
            {
                ExpectPunctuation(')');
                SkipWhitespace();
                if (this.index < this.text.Length && this.text[this.index] == ',')
                {
                    this.index++;
                    SkipWhitespace();
                    var distanceWord = ReadWord();
                    if (distanceWord.Length == 0)
                        throw ErrorAtCurrentToken();
                    if (!IsKeyword(distanceWord, "MAX"))
                    {
                        // A distance is a whole number up to 4294967295;
                        // anything else that reads as a number — a sign, a
                        // fraction, a wider count — is Msg 9987 (probed
                        // 2026-10-05 against SQL Server 2025).
                        if (!uint.TryParse(distanceWord, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                        {
                            throw !distanceWord.All(static c => char.IsAsciiDigit(c) || c is '.' or '+' or '-')
                                ? SimulatedSqlException.FullTextSyntaxErrorNearWord(distanceWord, this.text)
                                : SimulatedSqlException.FullTextNearDistanceInvalid();
                        }
                        maximumDistance = (int)Math.Min(parsed, int.MaxValue);
                    }
                    SkipWhitespace();
                    if (this.index < this.text.Length && this.text[this.index] == ',')
                    {
                        this.index++;
                        SkipWhitespace();
                        var orderWord = ReadWord();
                        if (IsKeyword(orderWord, "TRUE"))
                            ordered = true;
                        else if (!IsKeyword(orderWord, "FALSE"))
                            throw SimulatedSqlException.FullTextSyntaxErrorNearWord(orderWord, this.text);
                    }
                }
            }
            ExpectPunctuation(')');
            return FullTextProximityNode.Create(terms, maximumDistance, ordered);
        }

        /// <summary>Consumes trailing whitespace and refuses any leftover token.</summary>
        public void ExpectEnd()
        {
            SkipWhitespace();
            if (this.index < this.text.Length)
                throw ErrorAtCurrentToken();
        }

        /// <summary>
        /// Builds the Msg 7630 real reports for whatever stands at the cursor:
        /// state 3 when it is a word, state 2 when it is punctuation, state 1 at
        /// end of input.
        /// </summary>
        private SimulatedSqlException ErrorAtCurrentToken()
        {
            SkipWhitespace();
            if (this.index >= this.text.Length)
                return SimulatedSqlException.FullTextSyntaxErrorAtEnd(this.text);
            var checkpoint = this.index;
            var word = ReadWord();
            this.index = checkpoint;
            return word.Length > 0
                ? SimulatedSqlException.FullTextSyntaxErrorNearWord(word, this.text)
                : SimulatedSqlException.FullTextSyntaxErrorNearPunctuation(this.text[this.index].ToString(), this.text);
        }

        private void ExpectPunctuation(char expected)
        {
            SkipWhitespace();
            if (this.index >= this.text.Length)
                throw SimulatedSqlException.FullTextSyntaxErrorAtEnd(this.text);
            if (this.text[this.index] != expected)
                throw ErrorAtCurrentToken();
            this.index++;
        }

        private double ReadWeight()
        {
            SkipWhitespace();
            var start = this.index;
            if (this.index < this.text.Length && this.text[this.index] is '-' or '+')
                this.index++;
            while (this.index < this.text.Length && (char.IsDigit(this.text[this.index]) || this.text[this.index] is '.' or 'e' or 'E'))
                this.index++;
            if (this.index == start || !double.TryParse(this.text[start..this.index], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                this.index = start;
                throw ErrorAtCurrentToken();
            }
            // A weight reads as any number, then must lie in [0, 1] (probed
            // 2026-10-05 against SQL Server 2025: `1.5`, `-1` and `.5e1` are
            // all Msg 7632).
            return value is >= 0 and <= 1 ? value : throw SimulatedSqlException.FullTextWeightOutOfRange();
        }

        private void SkipWhitespace()
        {
            while (this.index < this.text.Length && char.IsWhiteSpace(this.text[this.index]))
                this.index++;
        }

        /// <summary>
        /// Reads the operator standing at the cursor, consuming it. Returns
        /// <see cref="ConditionOperator.None"/> (cursor left wherever the scan
        /// reached — callers restore their own checkpoint) for anything else.
        /// </summary>
        private ConditionOperator ReadOperator()
        {
            SkipWhitespace();
            if (this.index >= this.text.Length)
                return ConditionOperator.None;
            switch (this.text[this.index])
            {
                case '&':
                    this.index++;
                    if (this.index < this.text.Length && this.text[this.index] == '!')
                    {
                        this.index++;
                        return ConditionOperator.AndNot;
                    }
                    return ConditionOperator.And;
                case '|':
                    this.index++;
                    return ConditionOperator.Or;
                case '~':
                    this.index++;
                    return ConditionOperator.Near;
                default:
                    break;
            }
            var word = ReadWord();
            if (IsKeyword(word, "AND"))
            {
                var checkpoint = this.index;
                SkipWhitespace();
                var next = ReadWord();
                if (IsKeyword(next, "NOT"))
                    return ConditionOperator.AndNot;
                this.index = checkpoint;
                return ConditionOperator.And;
            }
            return IsKeyword(word, "OR") ? ConditionOperator.Or
                : IsKeyword(word, "NEAR") ? ConditionOperator.Near
                : ConditionOperator.None;
        }

        /// <summary>
        /// Reads a run of characters that isn't whitespace or one of the
        /// grammar's punctuation marks. The star rides along so an unquoted
        /// <c>ch*</c> reaches the word breaker as ordinary text.
        /// </summary>
        private string ReadWord()
        {
            SkipWhitespace();
            var start = this.index;
            while (this.index < this.text.Length)
            {
                var ch = this.text[this.index];
                if (char.IsWhiteSpace(ch) || ch is '(' or ')' or ',' or '"' or '&' or '|' or '~' or '!')
                    break;
                this.index++;
            }
            return this.text[start..this.index];
        }

        private static bool IsKeyword(string candidate, string keyword) =>
            string.Equals(candidate, keyword, StringComparison.OrdinalIgnoreCase);
    }

    private enum ConditionOperator
    {
        None,
        And,
        AndNot,
        Or,
        Near,
    }
}
