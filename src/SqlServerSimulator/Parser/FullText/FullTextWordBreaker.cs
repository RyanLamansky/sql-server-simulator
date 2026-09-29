using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Parser.FullText;

/// <summary>What a broken term stands for.</summary>
internal enum FullTextTermKind : byte
{
    /// <summary>A searchable term.</summary>
    Word,

    /// <summary>
    /// The sentence break real reports as <c>End Of Sentence</c>; it occupies
    /// no term but pushes the next word eight positions on.
    /// </summary>
    EndOfSentence,

    /// <summary>
    /// The paragraph break real reports as <c>End of Paragraph</c> — any line
    /// break — which pushes the next word 128 positions on.
    /// </summary>
    EndOfParagraph,
}

/// <summary>
/// One broken term and the 1-based ordinal it occupies in the document.
/// Several terms can share a position: a compound's composite sits with its
/// first part (<c>red-hot</c>@1, <c>red</c>@1, <c>hot</c>@2), and a number,
/// date or time carries its normalized companion beside it (<c>42</c>@1,
/// <c>nn42</c>@1) — real's numbering, visible in <c>sys.dm_fts_parser</c>'s
/// <c>occurrence</c> column.
/// </summary>
internal readonly struct FullTextTerm(string text, int position, FullTextTermKind kind = FullTextTermKind.Word)
{
    public readonly string Text = text;
    public readonly int Position = position;
    public readonly FullTextTermKind Kind = kind;
}

/// <summary>
/// The word breaker the query pipeline runs over both the indexed column values
/// and the search condition's own terms: SQL Server 2025's English (LCID 1033)
/// breaker, which the neutral breaker (LCID 0) matches term for term. Its
/// token classes, the normalized companions it emits and the lexicons it
/// consults are catalogued, with the probes that pinned them, in
/// <c>docs/claude/full-text.md</c>.
/// </summary>
internal static partial class FullTextWordBreaker
{
    /// <summary>
    /// Breaks <paramref name="text"/> into positioned terms. Stopwords are
    /// included — they occupy a position in real's index, which is what makes
    /// <c>"over the lazy dog"</c> match while <c>"jumps over lazy"</c> does
    /// not — and so are the position gaps sentence and paragraph breaks leave,
    /// but not the break markers themselves.
    /// </summary>
    public static List<FullTextTerm> Break(string text, bool accentSensitive) =>
        new Run(text, accentSensitive, markers: false).Execute();

    /// <summary>
    /// <see cref="Break"/> plus the sentence and paragraph markers, in the
    /// order <c>sys.dm_fts_parser</c> lists them.
    /// </summary>
    public static List<FullTextTerm> BreakWithMarkers(string text, bool accentSensitive) =>
        new Run(text, accentSensitive, markers: true).Execute();

    /// <summary>
    /// Applies the folds every term goes through — compatibility forms and
    /// case always, accents only for an accent-insensitive catalog.
    /// </summary>
    public static string Normalize(ReadOnlySpan<char> term, bool accentSensitive)
    {
        // Lower case, not CA1308's preferred upper: real's own normal form for
        // a full-text term is lower (sys.dm_fts_parser reports every
        // display_term that way), and the stoplist is stored to match.
#pragma warning disable CA1308
        var lowered = FoldCompatibility(term).ToLowerInvariant();
#pragma warning restore CA1308
        return accentSensitive ? lowered : FullTextLexicon.FoldAccents(lowered);
    }

    /// <summary>
    /// The folds real applies whatever the accent setting: full-width and
    /// half-width forms, ligatures and digraphs to their compatibility
    /// spelling (<c>Ａｂｃ</c> → <c>abc</c>, <c>ﬁ</c> → <c>fi</c>), and
    /// <c>ß</c>, <c>æ</c>, <c>œ</c> and <c>ĳ</c> to their two-letter forms.
    /// </summary>
    private static string FoldCompatibility(ReadOnlySpan<char> term)
    {
        var needsFold = false;
        foreach (var ch in term)
        {
            if (NeedsCompatibilityFold(ch))
            {
                needsFold = true;
                break;
            }
        }
        if (!needsFold)
            return term.ToString();
        var builder = new StringBuilder(term.Length + 4);
        foreach (var ch in term)
        {
            _ = ch switch
            {
                '\u00DF' => builder.Append("ss"),
                '\u00E6' or '\u00C6' => builder.Append("ae"),
                '\u0153' or '\u0152' => builder.Append("oe"),
                '\u0133' or '\u0132' => builder.Append("ij"),
                _ when NeedsCompatibilityFold(ch) => builder.Append(ch.ToString().Normalize(NormalizationForm.FormKC)),
                _ => builder.Append(ch),
            };
        }
        return builder.ToString();
    }

    private static bool NeedsCompatibilityFold(char ch) =>
        ch is '\u00DF' or '\u00E6' or '\u00C6' or '\u0153' or '\u0152' or '\u0133' or '\u0132'
            or (>= '\u01C4' and <= '\u01CC') or (>= '\u01F1' and <= '\u01F3') or (>= '\uFB00' and <= '\uFB06') or (>= '\uFF01' and <= '\uFFEE');

    /// <summary>
    /// A character that belongs to a word: letters, digits, the marks
    /// attached to them, and anything outside the Basic Multilingual Plane
    /// (real keeps an emoji as a term while dropping a BMP symbol such as
    /// <c>❤</c>).
    /// </summary>
    private static bool IsWordChar(char ch) =>
        ch switch
        {
            // Letter-like symbols, number forms, enclosed and squared
            // characters, and the ordinal indicators: real drops them.
            '\u00AA' or '\u00BA' or (>= '\u2100' and <= '\u218F') or (>= '\u2460' and <= '\u24FF') or (>= '\u3300' and <= '\u33FF') => false,
            // Variation selectors ride on a symbol, not a word.
            >= '\uFE00' and <= '\uFE0F' => false,
            _ => char.IsLetterOrDigit(ch) || char.IsSurrogate(ch)
                || CharUnicodeInfo.GetUnicodeCategory(ch) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark,
        };

    /// <summary><c>°C</c> / <c>°F</c>: the degree sign leads a temperature unit as one word.</summary>
    private static bool IsDegreeUnit(string s, int index) =>
        s[index] == '\u00B0' && index + 1 < s.Length && s[index + 1] is 'C' or 'F' && (index + 2 == s.Length || !IsWordChar(s[index + 2]));

    /// <summary>A vulgar fraction, which real keeps as a term of its own.</summary>
    private static bool IsFraction(char ch) => ch is '\u00BC' or '\u00BD' or '\u00BE' or (>= '\u2150' and <= '\u215E');

    /// <summary>The hyphenated words real keeps whole, without parts.</summary>
    private static bool IsWholeHyphenWord(ReadOnlySpan<char> compound) =>
        compound.Equals("e-mail", StringComparison.OrdinalIgnoreCase) || compound.Equals("e-mails", StringComparison.OrdinalIgnoreCase);

    /// <summary>Zero-width joiners vanish inside a word rather than breaking it.</summary>
    private static bool IsInvisibleJoiner(char ch) => ch is '\u200C' or '\u200D';

    private static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';

    private static bool IsApostrophe(char ch) => ch is '\'' or '\u2018' or '\u2019' or '`';

    private static bool IsHyphen(char ch) => ch is '-' or '_' or '\u2013' or '\u2014';

    private static bool IsLineBreak(char ch) => ch is '\n' or '\r' or '\u2028' or '\u2029' or '\f' or '\v';

    private static bool AllDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return false;
        foreach (var ch in span)
        {
            if (!IsAsciiDigit(ch))
                return false;
        }
        return true;
    }

    private static bool AllLetters(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return false;
        foreach (var ch in span)
        {
            if (!char.IsLetter(ch))
                return false;
        }
        return true;
    }

    /// <summary>
    /// One pass over one text. Holds the position bookkeeping: the last
    /// occupied position and the break (sentence or paragraph) waiting to push
    /// the next word on.
    /// </summary>
    private sealed class Run(string text, bool accentSensitive, bool markers)
    {
        private readonly List<FullTextTerm> terms = [];
        private readonly List<Chunk> chunks = [];
        private int last;
        private FullTextTermKind pending = FullTextTermKind.Word;

        /// <summary>
        /// Where a multi-chunk shape that ended inside its last chunk left
        /// off; 0 when it consumed its chunks whole.
        /// </summary>
        private int resumeOffset;

        /// <summary>How many chunks the last clock-with-meridiem shape consumed.</summary>
        private int timeChunks;

        public List<FullTextTerm> Execute()
        {
            SplitChunks();
            for (var i = 0; i < this.chunks.Count;)
            {
                if (this.chunks[i].ParagraphBefore)
                    this.pending = FullTextTermKind.EndOfParagraph;
                this.resumeOffset = 0;
                var consumed = TryMultiChunk(i);
                if (consumed == 0)
                {
                    ScanChunk(this.chunks[i], 0);
                    consumed = 1;
                }
                else if (this.resumeOffset > 0)
                {
                    // The shape ended inside its last chunk; the rest of that
                    // chunk breaks as usual.
                    ScanChunk(this.chunks[i + consumed - 1], this.resumeOffset);
                }
                i += consumed;
            }
            return this.terms;
        }

        /// <summary>
        /// Splits the text at whitespace, remembering which gaps held a line
        /// break — a paragraph break to real.
        /// </summary>
        private void SplitChunks()
        {
            var index = 0;
            var sawLineBreak = false;
            while (index < text.Length)
            {
                var ch = text[index];
                if (char.IsWhiteSpace(ch))
                {
                    if (IsLineBreak(ch))
                        sawLineBreak = true;
                    index++;
                    continue;
                }
                var start = index;
                while (index < text.Length && !char.IsWhiteSpace(text[index]))
                    index++;
                this.chunks.Add(new Chunk(start, index, sawLineBreak));
                sawLineBreak = false;
            }
        }

        private string ChunkText(int i) => text[this.chunks[i].Start..this.chunks[i].End];

        /// <summary>
        /// Claims the next position for a new term, placing a pending break's
        /// marker first. A sentence break leaves the next word nine positions
        /// past the last one and a paragraph break 129, with the marker one
        /// short of the word.
        /// </summary>
        private int NewUnit()
        {
            int position;
            if (this.pending != FullTextTermKind.Word)
            {
                var gap = this.pending == FullTextTermKind.EndOfSentence ? 8 : 128;
                if (markers)
                    this.terms.Add(new FullTextTerm(string.Empty, this.last + gap, this.pending));
                position = this.last + gap + 1;
            }
            else
            {
                position = this.last + 1;
            }
            this.pending = FullTextTermKind.Word;
            this.last = position;
            return position;
        }

        private void Add(int position, string term) =>
            this.terms.Add(new FullTextTerm(term, position));

        private void AddNormalized(int position, ReadOnlySpan<char> term) =>
            Add(position, Normalize(term, accentSensitive));

        // ---- multi-chunk shapes -------------------------------------------------

        /// <summary>
        /// The shapes real joins across a space: a currency written apart from
        /// its amount (<c>$ 5</c>, <c>USD 5</c>), an hour and its meridiem
        /// (<c>10 am</c>, <c>10:30 PM</c>, <c>3 o'clock</c>), a date spelled with
        /// a month name (<c>Aug 2, 2026</c>, <c>2 August 2026</c>), and digit
        /// groups separated by spaces (<c>1 000</c>). Returns how many chunks
        /// the shape consumed, or 0.
        /// </summary>
        private int TryMultiChunk(int i)
        {
            if (i + 1 >= this.chunks.Count || this.chunks[i + 1].ParagraphBefore)
                return 0;
            var first = ChunkText(i);
            var second = ChunkText(i + 1);

            // Month-name dates: `Aug 2, 2026` / `Aug 2 2026` / `2 August 2026`.
            if (i + 2 < this.chunks.Count && !this.chunks[i + 2].ParagraphBefore
                && TryMonthNameDate(first, second, ChunkText(i + 2)))
            {
                return 3;
            }

            // An amount spread over several chunks.
            if (TryAmountAcrossChunks(i) is var amountChunks and > 0)
                return amountChunks;

            // An hour and its meridiem, `3 o'clock` included.
            if (TryTimeWithMeridiem(i))
                return this.timeChunks;

            return 0;
        }

        /// <summary>
        /// An amount real reads across spaces: a currency written apart
        /// before it (<c>$ 5</c>, <c>USD 5</c>) or after it (<c>5 USD</c>,
        /// <c>$405 USD</c>, <c>20 R&amp;D</c> — the letter code needs no space
        /// after it), and digit groups spaced in threes (<c>1 000 000</c>,
        /// <c>46 283.64</c>, <c>£560 915</c>). The composite is the amount as
        /// written; its companion concatenates the digits and ends in the
        /// currency, the trailing one when there are two. Only a plain run of
        /// spaced groups also breaks into its groups. Returns the chunks
        /// consumed, the last one possibly in part, or 0.
        /// </summary>
        private int TryAmountAcrossChunks(int i)
        {
            var j = i;
            List<string> pieces = [];
            string? currency = null;
            if (Currency.IsSpacedPrefix(ChunkText(j)))
            {
                if (!Continues(j))
                    return 0;
                currency = ChunkText(j);
                pieces.Add(currency);
                j++;
            }

            var chunk = ChunkText(j);
            var index = 0;
            // Opening punctuation, and a minus, ahead of a currency symbol
            // is dropped (`-$405 USD` reads as `$405 usd`).
            var lead = 0;
            while (currency is null && lead < chunk.Length && chunk[lead] is '-' or '(' or '[' or '"' or '\'')
                lead++;
            if (currency is null && Currency.MatchSymbolPrefix(chunk, lead) is var symbolLength and > 0 && !char.IsLetter(chunk[lead]))
            {
                currency = chunk.Substring(lead, symbolLength);
                index = lead + symbolLength;
            }
            else
            {
                lead = 0;
            }
            if (!NumberShape.TryRead(chunk, index, out var number, allowGrouping: true, allowDecimal: true))
                return 0;
            var end = index + number.Length;
            var digits = number.Normalized;
            var groupsOnly = currency is null && !number.IsDecorated && number.Length <= 3 && end == chunk.Length;
            List<string> groups = [chunk[index..end]];

            // Spaced groups of three, the last possibly with a decimal.
            var fraction = string.Empty;
            if (!number.IsDecorated && number.Length <= 3 && end == chunk.Length)
            {
                var raw = chunk[index..end];
                while (Continues(j))
                {
                    var next = ChunkText(j + 1);
                    if (next.Length < 3 || !IsAsciiDigit(next[0]) || !IsAsciiDigit(next[1]) || !IsAsciiDigit(next[2])
                        || (next.Length > 3 && IsAsciiDigit(next[3])))
                    {
                        break;
                    }
                    var groupEnd = 3;
                    var groupFraction = string.Empty;
                    if (groupEnd + 1 < next.Length && next[groupEnd] == '.' && IsAsciiDigit(next[groupEnd + 1]))
                    {
                        var fractionEnd = groupEnd + 1;
                        while (fractionEnd < next.Length && IsAsciiDigit(next[fractionEnd]))
                            fractionEnd++;
                        groupFraction = next[(groupEnd + 1)..fractionEnd];
                        groupEnd = fractionEnd;
                    }
                    var attached = currency is null ? Currency.MatchSymbolSuffix(next, groupEnd) : 0;
                    if (groupEnd + attached < next.Length && IsWordChar(next[groupEnd + attached]))
                        break;
                    j++;
                    pieces.Add(chunk[lead..end]);
                    lead = 0;
                    chunk = next;
                    raw += next[..3];
                    groups.Add(next[..3]);
                    fraction = groupFraction;
                    end = groupEnd;
                    if (attached > 0)
                    {
                        currency = next.Substring(groupEnd, attached);
                        end += attached;
                    }
                    if (end < next.Length || fraction.Length > 0 || attached > 0)
                        break;
                }
                var trimmedFraction = fraction.TrimEnd('0');
                digits = groups.Count > 1 ? raw + (trimmedFraction.Length > 0 ? "d" + trimmedFraction : string.Empty) : digits;
            }

            // A currency attached after the amount (`5€`).
            var suffixAttached = currency is not null && end > 0 && !IsAsciiDigit(chunk[end - 1]);
            if (currency is null && Currency.MatchSymbolSuffix(chunk, end) is var attachedSuffix and > 0
                && (end + attachedSuffix == chunk.Length || !IsWordChar(chunk[end + attachedSuffix])))
            {
                currency = chunk.Substring(end, attachedSuffix);
                end += attachedSuffix;
                suffixAttached = true;
            }
            else if (end < chunk.Length && IsWordChar(chunk[end]))
            {
                return 0;
            }

            // A letter code or symbol spaced after the amount, but not one
            // leading the next amount (`5 $6` is two amounts). After an
            // amount that already leads with a currency only an upper-case
            // code of two letters or more joins (`$5 USD`, `$5 DM`, but not
            // `$5 Ft` or `$5 R`).
            var suffixChunk = false;
            if (end == chunk.Length && !suffixAttached && Continues(j)
                && Currency.MatchSymbolSuffix(ChunkText(j + 1), 0) is var spacedLength and > 0
                && (spacedLength == ChunkText(j + 1).Length || !IsWordChar(ChunkText(j + 1)[spacedLength]))
                && (currency is null || IsUpperCode(ChunkText(j + 1).AsSpan(0, spacedLength))))
            {
                pieces.Add(chunk[lead..end]);
                lead = 0;
                j++;
                chunk = ChunkText(j);
                currency = chunk[..spacedLength];
                end = spacedLength;
                suffixChunk = true;
            }

            if (j == i)
                return 0;

            pieces.Add(chunk[lead..end]);
            var position = NewUnit();
            AddNormalized(position, string.Join(' ', pieces));
            Add(position, "nn" + digits + (currency is null ? string.Empty : Currency.Suffix(currency)));
            if (groupsOnly && groups.Count > 1 && !suffixChunk && fraction.Length == 0 && currency is null)
            {
                for (var g = 0; g < groups.Count; g++)
                {
                    var groupPosition = g == 0 ? position : NewUnit();
                    Add(groupPosition, groups[g]);
                    Add(groupPosition, "nn" + NumberShape.NormalizeInteger(groups[g]));
                }
            }
            if (end < chunk.Length)
                this.resumeOffset = end;
            return j - i + 1;
        }

        private static bool IsUpperCode(ReadOnlySpan<char> code)
        {
            if (code.Length < 2)
                return false;
            foreach (var ch in code)
            {
                if (!char.IsUpper(ch))
                    return false;
            }
            return true;
        }

        /// <summary>True when chunk <paramref name="j"/> has a successor in the same paragraph.</summary>
        private bool Continues(int j) => j + 1 < this.chunks.Count && !this.chunks[j + 1].ParagraphBefore;

        private static string TrimTrailingPunctuation(string chunk)
        {
            var end = chunk.Length;
            while (end > 0 && chunk[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '"' or '\'')
                end--;
            return chunk[..end];
        }

        private bool TryTimeWithMeridiem(int i)
        {
            var first = ChunkText(i);
            var second = ChunkText(i + 1);
            if (!TimeShape.TryParse(first, out var time, allowSeconds: false) || time.Consumed != first.Length || time.HasMeridiem)
                return false;
            var trimmed = second.TrimEnd(',', ';', '!', '?', ')');
            var meridiemLength = Meridiem.Match(trimmed, out var pm, out var oclock);
            if (meridiemLength == 0 || (oclock && time.HasMinutes))
                return false;
            var written = first + " " + trimmed[..meridiemLength];
            var minute = time.Minute;
            this.timeChunks = 2;
            var closing = second;
            var closingStart = meridiemLength;
            // `5 o'clock 33` reads the number after it as the minutes, even
            // when more follows it in its chunk (`16:03`).
            if (oclock && meridiemLength == second.Length && Continues(i + 1))
            {
                var third = ChunkText(i + 2);
                var digits = 0;
                while (digits < third.Length && IsAsciiDigit(third[digits]))
                    digits++;
                if (digits == 2 && (digits == third.Length || !IsWordChar(third[digits]))
                    && int.Parse(third.AsSpan(0, digits), CultureInfo.InvariantCulture) is var minutes and <= 59)
                {
                    written += " " + third[..digits];
                    minute = minutes;
                    this.timeChunks = 3;
                    closing = third;
                    closingStart = digits;
                    if (digits < third.Length && third[digits] is not ('.' or ',' or ';' or '!' or '?' or ')'))
                        this.resumeOffset = digits;
                }
            }
            // `3 o'clock` reads both ways, like a bare `3:00`.
            EmitClock(NewUnit(), written, time.Hour, minute, time.Second, meridiem: oclock ? null : pm);
            // A meridiem written with its own dots (`p.m.`) consumes the final
            // one, so it closes no sentence. A chunk the shape left part of
            // closes its own sentence when its rest is broken.
            if (this.resumeOffset == 0)
                SetSentenceEnd(closing, closingStart, isAbbreviation: false);
            return true;
        }

        private bool TryMonthNameDate(string first, string second, string third)
        {
            // `Aug 2, 2026` / `Aug 2 2026` / `Dec. 25, 2026`
            if (MonthNames.TryGet(first, out var month))
            {
                var dayText = second.EndsWith(',') ? second[..^1] : second;
                var yearText = TrimTrailingPunctuation(third);
                if (AllDigits(dayText) && dayText.Length <= 2 && DateShape.TryYear(yearText, out var years)
                    && int.Parse(dayText, CultureInfo.InvariantCulture) is >= 1 and <= 31 and var day)
                {
                    EmitDate(first + " " + second + " " + yearText, years, month, day, [first, dayText, yearText]);
                    SetSentenceEnd(third, yearText.Length, isAbbreviation: false);
                    return true;
                }
            }
            // `2 August 2026` / `25 Dec 2026`
            if (AllDigits(first) && first.Length <= 2 && MonthNames.TryGet(second, out month))
            {
                var yearText = TrimTrailingPunctuation(third);
                if (DateShape.TryYear(yearText, out var years) && int.Parse(first, CultureInfo.InvariantCulture) is >= 1 and <= 31 and var day)
                {
                    EmitDate(first + " " + second + " " + yearText, years, month, day, [first, second, yearText]);
                    SetSentenceEnd(third, yearText.Length, isAbbreviation: false);
                    return true;
                }
            }
            return false;
        }

        // ---- single chunk ---------------------------------------------------------

        /// <summary>
        /// Breaks one whitespace-free chunk left to right, trying the
        /// recognizers in priority order at each word start and dropping the
        /// punctuation between matches.
        /// </summary>
        private void ScanChunk(Chunk chunk, int startOffset)
        {
            var s = text[chunk.Start..chunk.End];
            var index = startOffset;
            var lastWordEnd = startOffset;
            var lastWasAbbreviation = false;
            while (index < s.Length)
            {
                var consumed = TryWhole(s, index, out var abbreviation);
                if (consumed > 0)
                {
                    index += consumed;
                    lastWordEnd = index;
                    lastWasAbbreviation = abbreviation;
                    continue;
                }
                // A vulgar fraction is a term of its own.
                if (IsFraction(s[index]))
                {
                    Add(NewUnit(), s[index].ToString());
                    index++;
                    lastWordEnd = index;
                    lastWasAbbreviation = false;
                    continue;
                }
                // A sentence terminal closed by a quote or bracket ends the
                // sentence even with no space after it (`end.'next`).
                if (s[index] is '.' or '!' or '?' && index + 1 < s.Length && s[index + 1] is '\'' or '"' or ')' or '(' or '\u2019' or '\u201D'
                    && !(s[index] == '.' && lastWasAbbreviation))
                {
                    if (this.pending == FullTextTermKind.Word)
                        this.pending = FullTextTermKind.EndOfSentence;
                    index += 2;
                    continue;
                }
                // A lone currency symbol is a term of its own (`$` is the
                // English stoplist's only symbol) unless it prefixes a word.
                if (s[index] is '$' or '\u20AC' or '\u00A3' or '\u00A5' or '\u00A2' && (index + 1 >= s.Length || !IsWordChar(s[index + 1])))
                {
                    Add(NewUnit(), s[index].ToString());
                    index++;
                    lastWordEnd = index;
                    lastWasAbbreviation = false;
                    continue;
                }
                // An emoticon is a term: `:)`, `;-(`, `%^/`, `:-D`.
                if (Emoticon.Match(s, index) is var emoticonLength and > 0)
                {
                    AddNormalized(NewUnit(), s.AsSpan(index, emoticonLength));
                    index += emoticonLength;
                    lastWordEnd = index;
                    lastWasAbbreviation = false;
                    continue;
                }
                index++;
            }
            SetSentenceEnd(s, lastWordEnd, lastWasAbbreviation);
        }

        /// <summary>
        /// Reads the punctuation trailing a chunk's last term and records the
        /// sentence break it makes, if any: <c>!</c>, <c>?</c> and <c>…</c>
        /// always, a period or two unless the term before is an abbreviation
        /// (<c>Mr.</c>, <c>etc.</c>) or a single capital (<c>A.</c>) — never an
        /// ellipsis written as three periods, and none that anything but a
        /// closing quote or bracket follows.
        /// </summary>
        private void SetSentenceEnd(string chunk, int tailStart, bool isAbbreviation)
        {
            var dots = 0;
            var terminal = false;
            var afterTerminal = false;
            for (var i = tailStart; i < chunk.Length; i++)
            {
                switch (chunk[i])
                {
                    case '!':
                    case '?':
                    case '\u2026':
                        terminal = true;
                        afterTerminal = true;
                        break;
                    case '.':
                        dots++;
                        afterTerminal = true;
                        break;
                    case '\'' or '"' or ')' or ']' or '\u2019' or '\u201D' or '\u00BB':
                        break;
                    default:
                        // Anything but a closing quote or bracket after the
                        // terminal cancels it (`$5., And` goes on).
                        if (afterTerminal)
                            return;
                        break;
                }
            }
            // An abbreviation's own period is no terminal, but the ones after
            // it count (`and...` ends a sentence where `one...` does not).
            if (isAbbreviation && dots > 0 && tailStart < chunk.Length && chunk[tailStart] == '.')
                dots--;
            if (!terminal && dots is 1 or 2)
                terminal = true;
            if (terminal && this.pending == FullTextTermKind.Word)
                this.pending = FullTextTermKind.EndOfSentence;
        }

        /// <summary>
        /// Tries every recognizer at <paramref name="start"/>, emitting the
        /// first match. Returns the characters consumed (0 for none);
        /// <paramref name="abbreviation"/> reports a match whose own final
        /// period is no sentence end.
        /// </summary>
        private int TryWhole(string s, int start, out bool abbreviation)
        {
            abbreviation = false;
            var ch = s[start];
            // A degree sign leads a unit (`°C`) as part of the word.
            var startsWord = IsWordChar(ch) || IsDegreeUnit(s, start);

            // The lexicon's whole tokens: `c#`, `c++`, `j#`, `j++`, `.net`.
            if (Lexicon.Match(s, start) is var lexiconLength and > 0 && (start == 0 || !IsWordChar(s[start - 1])))
            {
                AddNormalized(NewUnit(), s.AsSpan(start, lexiconLength));
                var end = start + lexiconLength;
                // `c##` and `c+++` keep the lexicon token and drop the rest of
                // the run.
                while (end < s.Length && s[end] == s[start + lexiconLength - 1])
                    end++;
                return end - start;
            }

            if (startsWord && TryUrl(s, start) is var urlLength and > 0)
                return urlLength;

            if (startsWord && TryEmail(s, start) is var emailLength and > 0)
                return emailLength;

            if (startsWord && TryPath(s, start) is var pathLength and > 0)
                return pathLength;

            if (startsWord && TryAcronym(s, start) is var acronymLength and > 0)
                return acronymLength;

            if (IsAsciiDigit(ch) && TryNumericDate(s, start) is var dateLength and > 0)
                return dateLength;

            if (startsWord && TryDashedMonthDate(s, start) is var dashedLength and > 0)
                return dashedLength;

            if (IsAsciiDigit(ch) && TryClock(s, start) is var clockLength and > 0)
                return clockLength;

            if (TryMoney(s, start) is var moneyLength and > 0)
                return moneyLength;

            if (startsWord)
                return WordCompound(s, start, out abbreviation);

            return 0;
        }

        // ---- words and compounds ---------------------------------------------------

        /// <summary>
        /// One word run: letters and digits, joined across a single interior
        /// apostrophe (<c>don't</c>) and across <c>&amp;</c> when one side is a
        /// single letter (<c>at&amp;t</c>). Returns its end (exclusive) and the
        /// word's text with invisible joiners removed.
        /// </summary>
        private static int ReadJoinedWord(string s, int start, out string word)
        {
            var builder = new StringBuilder();
            var index = start;
            var segmentStart = start;
            while (index < s.Length)
            {
                var ch = s[index];
                if (IsWordChar(ch) || (index == start && IsDegreeUnit(s, index)))
                {
                    _ = builder.Append(ch);
                    index++;
                    continue;
                }
                if (IsInvisibleJoiner(ch) && index + 1 < s.Length && IsWordChar(s[index + 1]) && builder.Length > 0)
                {
                    index++;
                    continue;
                }
                if (IsApostrophe(ch) && index + 1 < s.Length && IsWordChar(s[index + 1]) && builder.Length > 0)
                {
                    _ = builder.Append(ch);
                    index++;
                    segmentStart = index;
                    continue;
                }
                if (ch == '&' && index + 1 < s.Length && char.IsLetter(s[index + 1]))
                {
                    var leftLength = index - segmentStart;
                    var rightEnd = index + 1;
                    while (rightEnd < s.Length && IsWordChar(s[rightEnd]))
                        rightEnd++;
                    var left = s.AsSpan(segmentStart, leftLength);
                    var right = s.AsSpan(index + 1, rightEnd - index - 1);
                    if (AllLetters(left) && AllLetters(right) && (left.Length == 1 || right.Length == 1))
                    {
                        _ = builder.Append('&');
                        index++;
                        segmentStart = index;
                        continue;
                    }
                }
                break;
            }
            word = builder.ToString();
            return index;
        }

        /// <summary>
        /// A word, or a compound of words: a hyphen-class chain
        /// (<c>red-hot</c>, <c>snake_case</c>) emits the composite and then
        /// each part, and a dotted chain whose tail reads as a file extension
        /// or domain (<c>file.txt</c>, <c>example.com</c>) does the same. A
        /// chain that isn't a compound breaks at its punctuation.
        /// </summary>
        private int WordCompound(string s, int start, out bool abbreviation)
        {
            abbreviation = false;
            var end = ReadJoinedWord(s, start, out var first);
            List<string> parts = [first];

            // Hyphen-class chain.
            var hyphenChain = false;
            while (end + 1 < s.Length && IsHyphen(s[end]) && IsWordChar(s[end + 1]))
            {
                // A digit part after a hyphen would read as a signed number
                // only without a left neighbour; inside a chain it is a part.
                var partEnd = ReadJoinedWord(s, end + 1, out var part);
                parts.Add(part);
                end = partEnd;
                hyphenChain = true;
            }
            if (hyphenChain)
            {
                var position = NewUnit();
                if (IsWholeHyphenWord(s.AsSpan(start, end - start)))
                {
                    AddNormalized(position, s.AsSpan(start, end - start));
                    return end - start;
                }
                // Underscores hugging the compound belong to its composite
                // (`_y7c_7`, `a_1_`), though to no part.
                var compositeStart = start;
                while (compositeStart > 0 && s[compositeStart - 1] == '_')
                    compositeStart--;
                var compositeEnd = end;
                while (compositeEnd < s.Length && s[compositeEnd] == '_')
                    compositeEnd++;
                AddNormalized(position, s.AsSpan(compositeStart, compositeEnd - compositeStart));
                for (var i = 0; i < parts.Count; i++)
                    EmitWordAt(i == 0 ? position : NewUnit(), parts[i]);
                return compositeEnd - start;
            }

            // Dotted chain: `name.ext`, `host.example.com`.
            List<string> dotted = [first];
            var dottedEnd = end;
            List<int> ends = [end];
            while (dottedEnd + 1 < s.Length && s[dottedEnd] == '.' && IsWordChar(s[dottedEnd + 1]))
            {
                var segmentEnd = ReadJoinedWord(s, dottedEnd + 1, out var segment);
                dotted.Add(segment);
                dottedEnd = segmentEnd;
                ends.Add(segmentEnd);
            }
            if (dotted.Count > 1 && !(AllDigits(dotted[0]) && AllDigits(dotted[1])))
            {
                var keep = Domains.CompoundLength(dotted);
                if (keep >= 2)
                {
                    var compoundEnd = ends[keep - 1];
                    // A `www.` host, like an address, takes the chunk's
                    // trailing punctuation into the composite — all but a
                    // final period.
                    if (keep == dotted.Count && dotted[0].Equals("www", StringComparison.OrdinalIgnoreCase))
                    {
                        var withTail = TrimAddressTail(s, s.Length, compoundEnd);
                        var position0 = NewUnit();
                        AddNormalized(position0, s.AsSpan(start, withTail - start));
                        for (var i = 0; i < keep; i++)
                            AddNormalized(i == 0 ? position0 : NewUnit(), dotted[i]);
                        return withTail - start;
                    }
                    var position = NewUnit();
                    AddNormalized(position, s.AsSpan(start, compoundEnd - start));
                    for (var i = 0; i < keep; i++)
                        AddNormalized(i == 0 ? position : NewUnit(), dotted[i]);
                    return compoundEnd - start;
                }
            }

            // A plain word; numbers, currencies and the rest were tried first.
            EmitWordAt(NewUnit(), first);
            abbreviation = Abbreviations.Contains(first) || (first.Length == 1 && char.IsUpper(first[0]));
            return end - start;
        }

        /// <summary>
        /// Emits one word at <paramref name="position"/>: a digit run also
        /// gets its <c>nn</c> companion, and an hour written with its
        /// meridiem (<c>5pm</c>) its <c>tt</c> one.
        /// </summary>
        private void EmitWordAt(int position, string word)
        {
            AddNormalized(position, word);
            if (AllDigits(word))
            {
                Add(position, "nn" + NumberShape.NormalizeInteger(word));
                return;
            }
            if (TimeShape.TryHourMeridiem(word, out var hour, out var pm))
                Add(position, TimeShape.Format(TimeShape.To24(hour, pm), 0, 0));
        }

        // ---- numbers and money ---------------------------------------------------

        /// <summary>
        /// A number with its optional sign and currency: <c>42</c>,
        /// <c>1,000.50</c>, <c>-5</c>, <c>$5</c>, <c>5€</c>, <c>USD5</c>. Emits
        /// the written form and the normalized <c>nn</c> companion, which drops
        /// grouping and leading zeros, writes the decimal point as <c>d</c>,
        /// trims trailing fractional zeros and appends the sign and currency.
        /// </summary>
        private int TryMoney(string s, int start)
        {
            var index = start;
            var sign = '\0';
            string? prefix = null;
            if (s[index] is '-' or '+' && index + 2 < s.Length && s[index + 1] == '.' && IsAsciiDigit(s[index + 2])
                && (index == 0 || !IsWordChar(s[index - 1])))
            {
                // A signed fraction with no integer part (`+.7`, `-.5`).
                var fractionEnd = index + 2;
                while (fractionEnd < s.Length && IsAsciiDigit(s[fractionEnd]))
                    fractionEnd++;
                if (fractionEnd < s.Length && IsWordChar(s[fractionEnd]))
                    return 0;
                var trimmed = s[(index + 2)..fractionEnd].TrimEnd('0');
                var bare = NewUnit();
                AddNormalized(bare, s.AsSpan(start, fractionEnd - start));
                Add(bare, "nn0" + (trimmed.Length > 0 ? "d" + trimmed : string.Empty) + (s[index] == '-' ? "-" : string.Empty));
                return fractionEnd - start;
            }
            if (s[index] is '-' or '+' && index + 1 < s.Length && IsAsciiDigit(s[index + 1]))
            {
                // A sign only when nothing word-like sits to its left; `12-34`
                // is a hyphen compound.
                if (index > 0 && IsWordChar(s[index - 1]) && s[index] == '-')
                    return 0;
                sign = s[index];
                index++;
            }
            else if (Currency.MatchSymbolPrefix(s, index) is var symbolLength and > 0 && index + symbolLength < s.Length && IsAsciiDigit(s[index + symbolLength]))
            {
                prefix = s.Substring(index, symbolLength);
                index += symbolLength;
            }
            else if (!IsAsciiDigit(s[index]))
            {
                return 0;
            }

            // A letter or digit run continuing the number makes it part of a
            // word (`5kg`, `3rd`), so the reading backs off — first the
            // decimal (`3.5kg` is `3` and `5kg`), then the grouping
            // (`1,000usd` is `1` and `000usd`).
            for (var attempt = 0; attempt < 3; attempt++)
            {
                if (!NumberShape.TryRead(s, index, out var number, allowGrouping: attempt < 2, allowDecimal: attempt < 1))
                    return 0;
                var end = index + number.Length;
                string? suffix = null;
                if (prefix is null && sign == '\0' && Currency.MatchSymbolSuffix(s, end) is var suffixLength and > 0)
                {
                    suffix = s.Substring(end, suffixLength);
                    end += suffixLength;
                }
                if (end < s.Length && IsWordChar(s[end]))
                {
                    if (number.IsDecorated)
                        continue;
                    return 0;
                }
                // An apostrophe joins digits into a word (`5'10`).
                if (end + 1 < s.Length && IsApostrophe(s[end]) && IsWordChar(s[end + 1]))
                    return 0;
                // A bare integer followed by a hyphen chain is that chain's
                // first part.
                if (sign == '\0' && prefix is null && suffix is null && !number.IsDecorated
                    && end + 1 < s.Length && IsHyphen(s[end]) && IsWordChar(s[end + 1]))
                {
                    return 0;
                }

                var position = NewUnit();
                AddNormalized(position, s.AsSpan(start, end - start));
                var currency = prefix ?? suffix;
                Add(position, "nn" + number.Normalized + (sign == '-' ? "-" : string.Empty) + (currency is null ? string.Empty : Currency.Suffix(currency)));
                return end - start;
            }
            return 0;
        }

        // ---- dates and times ---------------------------------------------------

        /// <summary>
        /// <c>2026-08-02</c>, <c>08/02/2026</c>, <c>31.12.2026</c>,
        /// <c>26-08-02</c>: three digit groups, one separator, read as real
        /// reads them (see <see cref="DateShape"/>). A shape that is no valid
        /// date falls through to the number and compound paths.
        /// </summary>
        private int TryNumericDate(string s, int start)
        {
            if (start > 0 && (IsWordChar(s[start - 1]) || s[start - 1] is '-' or '/' or '.'))
                return 0;
            if (!DateShape.TryParse(s, start, out var date))
                return 0;
            List<string> parts = [date.Parts[0], date.Parts[1], date.Parts[2]];
            EmitDate(s.Substring(start, date.Length), date.Years, date.Month, date.Day, parts);
            return date.Length;
        }

        /// <summary><c>2-Aug-2026</c> and <c>25-Dec-26</c>.</summary>
        private int TryDashedMonthDate(string s, int start)
        {
            var dayEnd = start;
            while (dayEnd < s.Length && IsAsciiDigit(s[dayEnd]))
                dayEnd++;
            if (dayEnd == start || dayEnd - start > 2 || dayEnd >= s.Length || s[dayEnd] != '-')
                return 0;
            var monthEnd = dayEnd + 1;
            while (monthEnd < s.Length && char.IsLetter(s[monthEnd]))
                monthEnd++;
            if (monthEnd >= s.Length || s[monthEnd] != '-' || !MonthNames.TryGet(s[(dayEnd + 1)..monthEnd], out var month))
                return 0;
            var yearEnd = monthEnd + 1;
            while (yearEnd < s.Length && IsAsciiDigit(s[yearEnd]))
                yearEnd++;
            if (yearEnd < s.Length && IsWordChar(s[yearEnd]))
                return 0;
            var dayText = s[start..dayEnd];
            var yearText = s[(monthEnd + 1)..yearEnd];
            var day = int.Parse(dayText, CultureInfo.InvariantCulture);
            if (!DateShape.TryYear(yearText, out var years) || day is < 1 or > 31)
                return 0;
            EmitDate(s[start..yearEnd], years, month, day, [dayText, s[(dayEnd + 1)..monthEnd], yearText]);
            return yearEnd - start;
        }

        /// <summary>
        /// Emits a date: the written form beside one <c>dd</c> companion per
        /// reading — a two-digit year reads as both 19xx and 20xx, the 19xx
        /// companion listed first — followed by the date's fields as plain
        /// terms at their own positions.
        /// </summary>
        private void EmitDate(string written, int[] years, int month, int day, List<string> parts)
        {
            var position = NewUnit();
            if (years.Length == 2)
            {
                Add(position, DateShape.Format(years[0], month, day));
                AddNormalized(position, written);
                Add(position, DateShape.Format(years[1], month, day));
            }
            else
            {
                AddNormalized(position, written);
                Add(position, DateShape.Format(years[0], month, day));
            }
            for (var i = 0; i < parts.Count; i++)
                AddNormalized(i == 0 ? position : NewUnit(), parts[i]);
        }

        /// <summary>
        /// <c>10:30</c>, <c>10:30:45</c>, <c>10h30</c>, <c>10:30am</c>: a
        /// clock time and its <c>tt</c> companions.
        /// </summary>
        private int TryClock(string s, int start)
        {
            if (start > 0 && IsWordChar(s[start - 1]))
                return 0;
            if (!TimeShape.TryParse(s.AsSpan(start), out var time, allowSeconds: true) || !time.HasMinutes)
                return 0;
            var end = start + time.Consumed;
            if (end < s.Length && IsWordChar(s[end]))
                return 0;
            EmitClock(NewUnit(), s[start..end], time.Hour, time.Minute, time.Second, time.HasMeridiem ? time.IsPm : null);
            return time.Consumed;
        }

        /// <summary>
        /// Emits a time. With a meridiem, or an hour no meridiem could change
        /// (0, or 13 and up), the written form is followed by one <c>tt</c>
        /// companion; an hour from 1 to 12 alone reads both ways, listed
        /// morning companion, written form, afternoon companion.
        /// </summary>
        private void EmitClock(int position, string written, int hour, int minute, int second, bool? meridiem)
        {
            if (meridiem is { } pm)
            {
                AddNormalized(position, written);
                Add(position, TimeShape.Format(TimeShape.To24(hour, pm), minute, second));
                return;
            }
            if (hour is >= 1 and <= 12)
            {
                Add(position, TimeShape.Format(hour % 12, minute, second));
                AddNormalized(position, written);
                Add(position, TimeShape.Format((hour % 12) + 12, minute, second));
                return;
            }
            AddNormalized(position, written);
            Add(position, TimeShape.Format(hour % 24, minute, second));
        }

        // ---- acronyms, addresses and paths ----------------------------------------

        /// <summary>
        /// A dotted acronym of single letters, <c>U.S.A.</c> — the written form
        /// and the letters run together — or one of the dotted abbreviations
        /// real keeps as one term without its final period (<c>e.g.</c>).
        /// Either consumes its final period, which then closes no sentence.
        /// </summary>
        private int TryAcronym(string s, int start)
        {
            if (start > 0 && s[start - 1] == '.')
                return 0;
            if (DottedAbbreviations.TryGet(s, start, out var abbreviationLength, out var canonical))
            {
                AddNormalized(NewUnit(), canonical);
                return abbreviationLength;
            }
            // Single letters, each followed by its period.
            var index = start;
            var letters = new StringBuilder();
            while (index + 1 < s.Length && char.IsLetter(s[index]) && s[index + 1] == '.'
                && (index + 2 == s.Length || !IsWordChar(s[index + 2]) || (index + 3 < s.Length && char.IsLetter(s[index + 2]) && s[index + 3] == '.')))
            {
                _ = letters.Append(s[index]);
                index += 2;
            }
            var written = s[start..index];
            if (letters.Length < 2 || (index < s.Length && IsWordChar(s[index])))
                return 0;
            var position = NewUnit();
            AddNormalized(position, written);
            AddNormalized(position, letters.ToString());
            return index - start;
        }

        /// <summary>
        /// <c>name@host.tld</c>: the whole address, then each letter-and-digit
        /// run in it.
        /// </summary>
        private int TryEmail(string s, int start)
        {
            var at = -1;
            var index = start;
            while (index < s.Length && (IsWordChar(s[index]) || s[index] is '.' or '_' or '-' or '+' or '@'))
            {
                if (s[index] == '@')
                {
                    if (at >= 0)
                        return 0;
                    at = index;
                }
                index++;
            }
            while (index > start && s[index - 1] is '.' or '-' or '_' or '+')
                index--;
            if (at <= start || at + 1 >= index || !IsWordChar(s[at - 1]))
                return 0;
            var domain = s.AsSpan(at + 1, index - at - 1);
            var dot = domain.LastIndexOf('.');
            if (dot <= 0 || dot == domain.Length - 1)
                return 0;
            var position = NewUnit();
            AddNormalized(position, s.AsSpan(start, index - start));
            EmitRuns(s, start, index, position);
            return index - start;
        }

        /// <summary>
        /// <c>scheme://host/path</c>: the whole address, then each
        /// letter-and-digit run in it.
        /// </summary>
        private int TryUrl(string s, int start)
        {
            var schemeEnd = start;
            while (schemeEnd < s.Length && char.IsLetter(s[schemeEnd]))
                schemeEnd++;
            if (schemeEnd == start || schemeEnd + 3 > s.Length || s[schemeEnd] != ':' || s[schemeEnd + 1] != '/' || s[schemeEnd + 2] != '/')
                return 0;
            var hostStart = schemeEnd + 3;
            var end = TrimAddressTail(s, s.Length, hostStart);
            if (end == hostStart)
                return 0;
            // The address runs to the end of the chunk, trailing punctuation
            // included, except for a final period, which ends a sentence
            // instead. A host whose first label is a single character keeps
            // only scheme and host in the composite.
            var composite = end;
            var labelEnd = hostStart;
            while (labelEnd < end && IsWordChar(s[labelEnd]))
                labelEnd++;
            if (labelEnd - hostStart == 1)
            {
                composite = labelEnd;
                while (composite < end && (IsWordChar(s[composite]) || s[composite] is '.' or '-'))
                    composite++;
            }
            var position = NewUnit();
            AddNormalized(position, s.AsSpan(start, composite - start));
            EmitRuns(s, start, end, position);
            return end - start;
        }

        /// <summary>
        /// Trims what an address leaves out of its composite at the end of a
        /// chunk: periods, commas, semicolons, closing brackets and quotes —
        /// while <c>!</c>, <c>?</c> and <c>:</c> stay in.
        /// </summary>
        private static int TrimAddressTail(string s, int end, int floor)
        {
            while (end > floor && s[end - 1] is '.' or ',' or ';' or ')' or ']' or '"' or '\'')
                end--;
            return end;
        }

        /// <summary>
        /// <c>C:\dir\file</c> and <c>\\server\share</c>: the whole path, then
        /// each letter-and-digit run in it.
        /// </summary>
        private int TryPath(string s, int start)
        {
            var driveForm = start + 2 < s.Length && char.IsLetter(s[start]) && s[start + 1] == ':' && s[start + 2] == '\\'
                && (start == 0 || !IsWordChar(s[start - 1]));
            var begin = start;
            if (!driveForm)
            {
                // `\\server` (one segment is enough) or `\a\b` (two at least).
                if (start < 1 || s[start - 1] != '\\')
                    return 0;
                begin = start >= 2 && s[start - 2] == '\\' ? start - 2 : start - 1;
                if (begin > 0 && s[begin - 1] == '\\')
                    return 0;
            }
            var end = start;
            while (end < s.Length && (IsWordChar(s[end]) || s[end] is '\\' or ':' or '.' or '_' or '-'))
                end++;
            // Trailing periods stay with the path, closing no sentence.
            while (end > start && s[end - 1] is '\\' or ':')
                end--;
            if (!driveForm && begin == start - 1 && s.AsSpan(start, end - start).IndexOf('\\') < 0)
                return 0;
            var position = NewUnit();
            AddNormalized(position, s.AsSpan(begin, end - begin));
            EmitRuns(s, start, end, position);
            return end - start;
        }

        /// <summary>
        /// Emits each letter-and-digit run in <paramref name="s"/>[start..end]
        /// as a part, the first at <paramref name="firstPosition"/>.
        /// </summary>
        private void EmitRuns(string s, int start, int end, int firstPosition)
        {
            var first = true;
            var index = start;
            while (index < end)
            {
                if (!IsWordChar(s[index]))
                {
                    index++;
                    continue;
                }
                // A port keeps its colon (`:8080`).
                var runStart = index > start && s[index - 1] == ':' && IsAsciiDigit(s[index]) ? index - 1 : index;
                while (index < end && IsWordChar(s[index]))
                    index++;
                AddNormalized(first ? firstPosition : NewUnit(), s.AsSpan(runStart, index - runStart));
                first = false;
            }
        }
    }

    private readonly struct Chunk(int start, int end, bool paragraphBefore)
    {
        public readonly int Start = start;
        public readonly int End = end;
        public readonly bool ParagraphBefore = paragraphBefore;
    }
}
