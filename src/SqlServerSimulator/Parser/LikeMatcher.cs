using System.Globalization;
using System.Text;

namespace SqlServerSimulator.Parser;

/// <summary>
/// The compiled form of a SQL Server <c>LIKE</c> / <c>PATINDEX</c> pattern,
/// and the engine that matches it against a subject <strong>under the
/// resolved collation's full comparison semantics</strong> — not just its case
/// half. Single source of truth for the wildcard (<c>%</c>, <c>_</c>),
/// character-class (<c>[abc]</c>, <c>[^abc]</c>, <c>[a-z]</c>) and
/// escape-clause rules so a fix to one site fixes the other.
/// </summary>
/// <remarks>
/// <para>
/// The model real SQL Server implements, probe-confirmed against SQL Server
/// 2025 (the matrix is in <c>docs/claude/collations.md</c>):
/// </para>
/// <list type="bullet">
/// <item><description><strong>The subject is a sequence of characters, not of
/// UTF-16 units.</strong> A base character carries its combining marks
/// (<c>N'e' + NCHAR(0x0301)</c> is one character: it matches <c>N'_'</c> and
/// not <c>N'__'</c>), and a surrogate pair reads per
/// <see cref="SurrogateMatching"/>. Every match boundary — a literal run's
/// end, a <c>%</c>'s resume point, a <c>PATINDEX</c> start — lands on a
/// character boundary, which is why <c>PATINDEX(N'%e%', N'Xe' + NCHAR(0x0301) + N'Y')</c>
/// answers 0 under an accent-sensitive collation: the base <c>e</c> alone is
/// not a character there.</description></item>
/// <item><description><strong>A literal run compares linguistically.</strong>
/// Under <c>_CI_AI</c>, <c>N'café' LIKE N'cafe%'</c> matches; under
/// <c>_KS_</c> / <c>_WS_</c> the kana and width halves stop folding, exactly
/// as they do for <c>=</c>. The run is matched through
/// <see cref="CompareInfo.IsPrefix(ReadOnlySpan{char}, ReadOnlySpan{char}, CompareOptions, out int)"/>,
/// whose match length is how much of the subject the run consumed — the
/// accent-insensitive match of <c>cafe</c> against <c>cafe</c> + U+0301 eats
/// five UTF-16 units for a four-unit run.</description></item>
/// <item><description><strong>A character class is ordered by the
/// collation.</strong> <c>[a-c]</c> under <c>Latin1_General_CS_AS</c> matches
/// <c>A</c> and <c>B</c> but not <c>C</c>, because the collation interleaves
/// (<c>a</c> &lt; <c>A</c> &lt; <c>b</c> &lt; <c>B</c> &lt; <c>c</c> &lt;
/// <c>C</c>). Ranges test <see cref="Collation.Compare"/>, single members test
/// <see cref="Collation.Equals(string?, string?)"/>, and a reversed range
/// (<c>[c-a]</c>) matches nothing while the class's other members still
/// answer.</description></item>
/// <item><description><strong>Wildcards and the escape character are matched
/// by code point</strong>, never through the collation: a fullwidth
/// <c>％</c> is a literal, and <c>ESCAPE N'e'</c> doesn't make the pattern's
/// <c>E</c> — or its <c>é</c> under an accent-insensitive collation — an
/// escape.</description></item>
/// <item><description><strong>Trailing-space slack is the non-Unicode
/// family's</strong> and is decided by the caller (the
/// <c>trailingSpaceSlack</c> argument on <see cref="Find"/>): a subject may
/// carry U+0020 the pattern didn't consume only when neither operand is
/// <c>nvarchar</c> / <c>nchar</c>. The pattern's own trailing spaces are
/// significant either way.</description></item>
/// </list>
/// <para>
/// Cost: an all-printable-ASCII subject matched by an all-printable-ASCII
/// pattern takes an ordinal path that skips <see cref="CompareInfo"/>
/// entirely — see <see cref="Collation.PrintableAsciiFoldsCaseOnly"/> for why
/// that gives the same answer.
/// </para>
/// </remarks>
internal sealed class LikeMatcher
{
    private readonly Segment[] segments;

    private readonly Collation collation;

    /// <summary>Non-null exactly when the collation matches linguistically rather than by code unit; the matching itself goes through <see cref="Collation.IsPrefix"/>.</summary>
    private readonly CompareInfo? compareInfo;

    private readonly SurrogateMatching surrogates;

    private readonly bool caseSensitive;

    /// <summary>
    /// True when the pattern carries only printable ASCII and the collation
    /// folds nothing but ASCII case across that range, so an all-printable-
    /// ASCII subject can take the ordinal path.
    /// </summary>
    private readonly bool asciiEligible;

    /// <summary>
    /// True when the pattern holds a <c>%</c> with a literal right after it —
    /// the only shape whose matching wants to know whether the <em>whole</em>
    /// subject is printable ASCII, because that is what licenses skipping
    /// ahead with an ordinal <c>IndexOf</c> instead of walking character by
    /// character. Every other shape decides per literal run, over a window the
    /// length of the run, which is far less of the subject to look at.
    /// </summary>
    private readonly bool jumpsOverAny;

    /// <summary>False only for <c>PATINDEX</c> with a leading <c>%</c>: the reported position is where the rest of the pattern starts matching.</summary>
    private readonly bool anchorStart;

    /// <summary>False only for <c>PATINDEX</c> with a trailing <c>%</c>.</summary>
    private readonly bool anchorEnd;

    private LikeMatcher(Segment[] segments, Collation collation, bool asciiEligible, bool anchorStart, bool anchorEnd)
    {
        this.segments = segments;
        this.collation = collation;
        this.compareInfo = collation.LinguisticMatching?.Info;
        this.surrogates = collation.SurrogateMatching;
        this.caseSensitive = collation.CaseSensitive;
        this.asciiEligible = asciiEligible;
        for (var i = 0; asciiEligible && i + 1 < segments.Length; i++)
        {
            if (segments[i].Kind == SegmentKind.Any && segments[i + 1].Kind == SegmentKind.Literal)
                this.jumpsOverAny = true;
        }

        this.anchorStart = anchorStart;
        this.anchorEnd = anchorEnd;
    }

    /// <summary>
    /// True when the whole subject matches — the <c>LIKE</c> answer.
    /// <paramref name="trailingSpaceSlack"/> lets the subject carry U+0020 the
    /// pattern didn't consume (the non-Unicode operand rule).
    /// </summary>
    public bool IsMatch(string subject, bool trailingSpaceSlack) =>
        this.Find(subject, trailingSpaceSlack) >= 0;

    /// <summary>
    /// The zero-based UTF-16 index where the pattern starts matching, or
    /// <c>-1</c> when it doesn't — <c>PATINDEX</c>'s answer, one off its
    /// one-based result. A pattern compiled for <c>LIKE</c> is anchored at
    /// both ends, so the answer is only ever 0 or -1.
    /// </summary>
    public int Find(string subject, bool trailingSpaceSlack)
    {
        var s = subject.AsSpan();
        // Only the `%`-then-literal skip needs the whole subject classified;
        // every other shape asks per literal run, over that run's own window.
        var ascii = this.jumpsOverAny && IsPrintableAscii(s);
        if (this.anchorStart)
            return this.MatchFrom(s, 0, 0, ascii, trailingSpaceSlack) ? 0 : -1;

        var pos = 0;
        while (true)
        {
            if (this.MatchFrom(s, 0, pos, ascii, trailingSpaceSlack))
                return pos;
            if (pos >= s.Length)
                return -1;
            pos += this.AdvanceLength(s, pos, ascii);
        }
    }

    /// <summary>
    /// Matches <c>segments[segIndex..]</c> against the subject from
    /// <paramref name="pos"/>. Iterative for the deterministic segment kinds;
    /// <c>%</c> recurses once per candidate resume point, which is the only
    /// place the match branches.
    /// </summary>
    private bool MatchFrom(ReadOnlySpan<char> s, int segIndex, int pos, bool ascii, bool slack)
    {
        while (segIndex < this.segments.Length)
        {
            var segment = this.segments[segIndex];

            // `%` accepts the whole remainder when nothing follows it, trailing
            // spaces and unmatchable code units included.
            if (segment.Kind == SegmentKind.Any)
            {
                return segIndex == this.segments.Length - 1
                    || this.MatchAfterAny(s, segIndex, pos, ascii, slack);
            }

            var consumed = segment.Kind switch
            {
                SegmentKind.Literal => this.MatchLiteral(s, pos, segment.Text, ascii),
                SegmentKind.Single => NoneAsMiss(this.ElementLength(s, pos, ascii)),
                SegmentKind.Class => this.MatchClass(s, pos, segment.Class!, ascii),
                _ => -1, // Never — an empty or unterminated class
            };

            if (consumed < 0)
                return false;
            pos += consumed;
            segIndex++;
        }

        return !this.anchorEnd || pos == s.Length || (slack && IsAllSpaces(s[pos..]));
    }

    /// <summary>
    /// The <c>%</c> branch: try the rest of the pattern at every character
    /// boundary from <paramref name="pos"/> on. When the next segment is a
    /// literal and the ordinal path is in force, the candidate positions come
    /// from a vectorized <c>IndexOf</c> instead of a step-by-step walk — the
    /// shape <c>LIKE '%needle%'</c> takes.
    /// </summary>
    private bool MatchAfterAny(ReadOnlySpan<char> s, int segIndex, int pos, bool ascii, bool slack)
    {
        var next = this.segments[segIndex + 1];
        if (ascii && next.Kind == SegmentKind.Literal)
        {
            var comparison = this.caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            for (var p = pos; p <= s.Length;)
            {
                var found = s[p..].IndexOf(next.Text, comparison);
                if (found < 0)
                    return false;
                p += found;
                if (this.MatchFrom(s, segIndex + 1, p, ascii, slack))
                    return true;
                p++;
            }

            return false;
        }

        var candidate = pos;
        while (true)
        {
            if (this.MatchFrom(s, segIndex + 1, candidate, ascii, slack))
                return true;
            if (candidate >= s.Length)
                return false;
            candidate += this.AdvanceLength(s, candidate, ascii);
        }
    }

    /// <summary>
    /// Turns the end-of-subject reading (<c>0</c>) into a miss, so the
    /// one-character wildcard and the segment loop share one convention:
    /// negative means no match here.
    /// </summary>
    private static int NoneAsMiss(int length) => length == 0 ? -1 : length;

    /// <summary>
    /// How much of the subject at <paramref name="pos"/> the literal run
    /// consumes, or <c>-1</c> when it doesn't match there. The linguistic path
    /// takes the collation's own match length — which respects collation
    /// elements, so an accent-insensitive <c>e</c> swallows a following
    /// combining mark — and then requires that length to land on a character
    /// boundary, which is what keeps a bare <c>e</c> from matching the base of
    /// <c>e</c> + U+0301 under an accent-sensitive collation.
    /// </summary>
    private int MatchLiteral(ReadOnlySpan<char> s, int pos, string run, bool ascii)
    {
        var rest = s[pos..];
        // The ordinal path is exact when the run and the stretch of subject it
        // could touch are printable ASCII and the collation folds nothing but
        // case there: no expansion crosses the boundary and no combining mark
        // attaches. One extra character is classified past the run so a mark
        // sitting right after it sends the match down the linguistic path,
        // where the character-boundary rule refuses it.
        var ordinalPath = this.compareInfo is null
            || ascii
            || (this.asciiEligible && IsPrintableAscii(rest[..Math.Min(run.Length + 1, rest.Length)]));
        return ordinalPath
            ? rest.StartsWith(run, this.caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)
                ? run.Length
                : -1
            : this.collation.IsPrefix(rest, run, out var matched) && IsCharacterBoundary(s, pos + matched)
                ? matched
                : -1;
    }

    /// <summary>
    /// The one-character wildcard's reach at <paramref name="pos"/>: the
    /// UTF-16 length of the character there, <c>0</c> at the end of the
    /// subject, and <c>-1</c> for a surrogate the collation gives no character
    /// status (see <see cref="SurrogateMatching.Unmatchable"/>).
    /// </summary>
    private int ElementLength(ReadOnlySpan<char> s, int pos, bool ascii)
    {
        if (pos >= s.Length)
            return 0;
        if (ascii)
            return 1;

        var c = s[pos];
        if (char.IsSurrogate(c))
        {
            var pairs = char.IsHighSurrogate(c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]);
            return this.surrogates switch
            {
                SurrogateMatching.Unmatchable => -1,
                SurrogateMatching.CodeUnits => 1,
                _ => pairs ? 2 + TrailingMarks(s, pos + 2) : 1,
            };
        }

        // A binary collation compares code units and groups nothing, which is
        // what its `_` does too (probe-confirmed on Latin1_General_BIN2:
        // `N'e' + NCHAR(0x0301) LIKE N'__'` answers yes).
        return this.compareInfo is null ? 1 : 1 + TrailingMarks(s, pos + 1);
    }

    /// <summary>
    /// The step <c>%</c> and <c>PATINDEX</c>'s start scan take — the character
    /// length, except that a surrogate pair no wildcard can match is still
    /// stepped over whole rather than being an impassable wall.
    /// </summary>
    private int AdvanceLength(ReadOnlySpan<char> s, int pos, bool ascii)
    {
        var length = this.ElementLength(s, pos, ascii);
        return length > 0 ? length
            : char.IsHighSurrogate(s[pos]) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]) ? 2
            : 1;
    }

    private int MatchClass(ReadOnlySpan<char> s, int pos, CharClass characterClass, bool ascii)
    {
        var length = this.ElementLength(s, pos, ascii);
        if (length <= 0)
            return -1;

        var element = s.Slice(pos, length);
        var member = length == 1 && element[0] is >= ' ' and <= '~'
            ? characterClass.ContainsPrintableAscii(element[0])
            : characterClass.ContainsGeneral(new string(element), this.collation);
        return member != characterClass.Negated ? length : -1;
    }

    private static bool IsAllSpaces(ReadOnlySpan<char> s) => s.IndexOfAnyExcept(' ') < 0;

    /// <summary>Vectorized: U+0020..U+007E, the range the ordinal path is proven over.</summary>
    private static bool IsPrintableAscii(ReadOnlySpan<char> s) => s.IndexOfAnyExceptInRange(' ', '~') < 0;

    /// <summary>
    /// True when <paramref name="i"/> starts a character: the end of the
    /// subject, or a code unit that neither continues the preceding one (a
    /// combining mark, a halfwidth voiced sound mark) nor is the low half of a
    /// surrogate pair whose high half is behind it.
    /// </summary>
    private static bool IsCharacterBoundary(ReadOnlySpan<char> s, int i) =>
        i >= s.Length
        || !(IsCombiningMark(s[i]) || (i > 0 && char.IsHighSurrogate(s[i - 1]) && char.IsLowSurrogate(s[i])));

    private static int TrailingMarks(ReadOnlySpan<char> s, int from)
    {
        var count = 0;
        while (from + count < s.Length && IsCombiningMark(s[from + count]))
            count++;
        return count;
    }

    /// <summary>
    /// The code units that attach to the character before them. The Unicode
    /// mark categories, plus the halfwidth voiced sound marks (U+FF9E /
    /// U+FF9F), which are modifier letters by category but combine here —
    /// probe-confirmed: halfwidth <c>ｶﾞ</c> matches <c>N'_'</c> and the
    /// fullwidth <c>ガ</c> it folds onto under <c>Japanese_CI_AS</c>.
    /// </summary>
    private static bool IsCombiningMark(char c) =>
        c is '\uFF9E' or '\uFF9F'
        || (c >= '\u0300' && CharUnicodeInfo.GetUnicodeCategory(c)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark);

    private enum SegmentKind : byte
    {
        Literal = 0,
        Single = 1,
        Any = 2,
        Class = 3,

        /// <summary>An empty or unterminated class — SQL Server's silent never-match.</summary>
        Never = 4,
    }

    private readonly struct Segment(SegmentKind kind, string text, CharClass? characterClass)
    {
        public readonly SegmentKind Kind = kind;

        public readonly string Text = text;

        public readonly CharClass? Class = characterClass;
    }

    /// <summary>
    /// One <c>[...]</c> class. Membership for a printable-ASCII character is a
    /// bitmap lookup; the bitmap is filled at compile time by asking the
    /// collation about each of the 95 characters, so it is exact whatever the
    /// collation does (a fullwidth <c>Ａ</c> written into the class sets
    /// <c>a</c>'s bit under a width-insensitive collation). Everything else
    /// asks the collation per element.
    /// </summary>
    private sealed class CharClass
    {
        public readonly bool Negated;

        private readonly ulong asciiLow;

        private readonly ulong asciiHigh;

        private readonly string[] singles;

        /// <summary>Range bounds, low then high, two entries per range.</summary>
        private readonly string[] rangeBounds;

        internal CharClass(bool negated, string[] singles, string[] rangeBounds, Collation collation)
        {
            this.Negated = negated;
            this.singles = singles;
            this.rangeBounds = rangeBounds;
            for (var c = ' '; c <= '~'; c++)
            {
                if (!this.ContainsGeneral(c.ToString(), collation))
                    continue;
                var bit = c - ' ';
                if (bit < 64)
                    this.asciiLow |= 1UL << bit;
                else
                    this.asciiHigh |= 1UL << (bit - 64);
            }
        }

        public bool ContainsPrintableAscii(char c)
        {
            var bit = c - ' ';
            return bit < 64 ? (this.asciiLow & (1UL << bit)) != 0 : (this.asciiHigh & (1UL << (bit - 64))) != 0;
        }

        public bool ContainsGeneral(string element, Collation collation)
        {
            foreach (var single in this.singles)
            {
                if (collation.Equals(element, single))
                    return true;
            }

            for (var i = 0; i < this.rangeBounds.Length; i += 2)
            {
                // A reversed range is simply unsatisfiable, which is what real
                // does: `[c-a1]` still matches `1`.
                if (collation.Compare(element, this.rangeBounds[i]) >= 0
                    && collation.Compare(element, this.rangeBounds[i + 1]) <= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Compiles <paramref name="pattern"/> against <paramref name="collation"/>.
    /// <paramref name="forPatIndex"/> consumes a leading / trailing <c>%</c>
    /// into the anchoring decision instead of into the segment list, because
    /// <c>PATINDEX</c> reports where the pattern's <em>content</em> starts:
    /// <c>PATINDEX('%abc', 'xabc')</c> is 2, not 1.
    /// </summary>
    public static LikeMatcher Compile(string pattern, char? escapeChar, Collation collation, bool forPatIndex)
    {
        var anchorStart = true;
        var anchorEnd = true;
        var body = pattern;
        if (forPatIndex)
        {
            anchorStart = !StartsWithUnescapedPercent(pattern, escapeChar);
            anchorEnd = !EndsWithUnescapedPercent(pattern, escapeChar);
            var start = anchorStart ? 0 : 1;
            var end = !anchorEnd && pattern.Length > start ? pattern.Length - 1 : pattern.Length;
            body = pattern[start..end];
        }

        var segments = new List<Segment>();
        var literal = new StringBuilder();
        var asciiEligible = collation.PrintableAsciiFoldsCaseOnly;
        var i = 0;
        while (i < body.Length)
        {
            var c = body[i];
            if (escapeChar.HasValue && c == escapeChar.Value && i + 1 < body.Length)
            {
                _ = literal.Append(body[i + 1]);
                i += 2;
                continue;
            }

            switch (c)
            {
                case '%':
                    FlushLiteral(segments, literal);
                    // Consecutive `%` add nothing but branching.
                    if (segments.Count == 0 || segments[^1].Kind != SegmentKind.Any)
                        segments.Add(new Segment(SegmentKind.Any, string.Empty, null));
                    i++;
                    break;

                case '_':
                    FlushLiteral(segments, literal);
                    segments.Add(new Segment(SegmentKind.Single, string.Empty, null));
                    i++;
                    break;

                case '[':
                    FlushLiteral(segments, literal);
                    i = AppendClass(body, i, collation, segments);
                    break;

                default:
                    _ = literal.Append(c);
                    i++;
                    break;
            }
        }

        FlushLiteral(segments, literal);
        foreach (var c in body)
        {
            if (c is < ' ' or > '~')
                asciiEligible = false;
        }

        return new LikeMatcher([.. segments], collation, asciiEligible, anchorStart, anchorEnd);
    }

    private static void FlushLiteral(List<Segment> segments, StringBuilder literal)
    {
        if (literal.Length == 0)
            return;
        segments.Add(new Segment(SegmentKind.Literal, literal.ToString(), null));
        _ = literal.Clear();
    }

    /// <summary>
    /// Appends the class starting at <paramref name="start"/> (the <c>[</c>)
    /// and returns the position after its <c>]</c>. An unterminated <c>[</c>
    /// and an empty <c>[]</c> both compile to the never-match segment, real's
    /// silent failure for either. <c>[^]</c> is the any-character form: a
    /// negated class with no members.
    /// </summary>
    private static int AppendClass(string pattern, int start, Collation collation, List<Segment> segments)
    {
        var contentStart = start + 1;
        var end = pattern.IndexOf(']', contentStart);
        if (end < 0)
        {
            segments.Add(new Segment(SegmentKind.Never, string.Empty, null));
            return pattern.Length;
        }

        var content = pattern[contentStart..end];
        if (content.Length == 0)
        {
            segments.Add(new Segment(SegmentKind.Never, string.Empty, null));
            return end + 1;
        }

        var negated = content[0] == '^';
        if (negated)
            content = content[1..];

        // Class members are characters, not code units: `[e + U+0301]` is one
        // member (probe-confirmed — it matches the composed é and neither the
        // bare e nor the bare mark).
        var members = new List<string>();
        for (var i = 0; i < content.Length;)
        {
            var length = 1;
            if (char.IsHighSurrogate(content[i]) && i + 1 < content.Length && char.IsLowSurrogate(content[i + 1]))
                length = 2;
            while (i + length < content.Length && IsCombiningMark(content[i + length]))
                length++;
            members.Add(content.Substring(i, length));
            i += length;
        }

        var singles = new List<string>();
        var bounds = new List<string>();
        for (var i = 0; i < members.Count;)
        {
            // A `-` is a range only between two members; leading or trailing
            // it is an ordinary member (`[-a]` and `[a-]` both match it).
            if (i + 2 < members.Count && members[i + 1] == "-")
            {
                bounds.Add(members[i]);
                bounds.Add(members[i + 2]);
                i += 3;
                continue;
            }

            singles.Add(members[i]);
            i++;
        }

        segments.Add(new Segment(SegmentKind.Class, string.Empty, new CharClass(negated, [.. singles], [.. bounds], collation)));
        return end + 1;
    }

    private static bool StartsWithUnescapedPercent(string pattern, char? escapeChar) =>
        pattern.Length > 0 && pattern[0] == '%' && !(escapeChar.HasValue && pattern.Length >= 2 && pattern[0] == escapeChar.Value);

    /// <summary>
    /// True when the pattern's final character is <c>%</c> and isn't preceded
    /// by the escape character. Requires scanning from the start because an
    /// escape-char-then-<c>%</c> consumes the <c>%</c> as a literal, and the
    /// parity of consecutive escape chars decides whether the final one is
    /// itself escaped.
    /// </summary>
    private static bool EndsWithUnescapedPercent(string pattern, char? escapeChar)
    {
        if (pattern.Length == 0 || pattern[^1] != '%')
            return false;
        if (!escapeChar.HasValue)
            return true;
        var escape = escapeChar.Value;
        var escaped = false;
        for (var i = 0; i < pattern.Length - 1; i++)
            escaped = pattern[i] == escape && !escaped;
        return !escaped;
    }

    /// <summary>
    /// A one-entry memo over <see cref="Compile"/>, held by the expression node
    /// that does the matching. Compiling a pattern walks it, builds the segment
    /// list and fills each class's ASCII bitmap with 95 collation comparisons;
    /// a predicate evaluated once per row paid that per row, which dominated a
    /// scan (228k rows of <c>WHERE Description LIKE 'USB%'</c>: 285 ms and
    /// 782 MB allocated against 23 ms and 68 MB for the same scan reading the
    /// column and nothing else).
    /// <para>
    /// One entry is enough because a pattern is a literal or a parameter at
    /// essentially every call site, so it is the same string for every row of a
    /// statement; a genuinely per-row pattern misses and recompiles exactly as
    /// before. The key is the whole input — pattern text, escape character and
    /// resolved collation — so a cached entry can only ever be returned for
    /// inputs that would have compiled an identical matcher.
    /// </para>
    /// <para>
    /// A cached plan is shared across sessions, so the memo is read and written
    /// concurrently. <see cref="Volatile"/> publishes a fully-initialized entry
    /// and consumes it with acquire semantics; two threads racing to fill it
    /// simply compile equivalent matchers and one wins, which is why no lock is
    /// taken. <see cref="LikeMatcher"/> itself is immutable, so matching is
    /// thread-safe.
    /// </para>
    /// </summary>
    internal sealed class Cache(bool forPatIndex)
    {
        private readonly bool forPatIndex = forPatIndex;

        private Entry? memo;

        public LikeMatcher Get(string pattern, char? escapeChar, Collation collation)
        {
            var current = Volatile.Read(ref this.memo);
            if (current is not null
                && ReferenceEquals(current.Collation, collation)
                && current.EscapeChar == escapeChar
                && string.Equals(current.Pattern, pattern, StringComparison.Ordinal))
            {
                return current.Matcher;
            }

            var built = Compile(pattern, escapeChar, collation, this.forPatIndex);
            Volatile.Write(ref this.memo, new Entry(pattern, escapeChar, collation, built));
            return built;
        }

        private sealed class Entry(string pattern, char? escapeChar, Collation collation, LikeMatcher matcher)
        {
            public readonly Collation Collation = collation;

            public readonly char? EscapeChar = escapeChar;

            public readonly string Pattern = pattern;

            public readonly LikeMatcher Matcher = matcher;
        }
    }
}
