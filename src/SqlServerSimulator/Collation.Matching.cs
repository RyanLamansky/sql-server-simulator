using System.Globalization;

namespace SqlServerSimulator;

/// <summary>
/// The collation's substring search — the primitive every character-matching
/// string scalar reads (<c>CHARINDEX</c>, <c>REPLACE</c>, <c>TRANSLATE</c>,
/// <c>STRING_SPLIT</c>, the <c>TRIM</c> family), so one collation decides them
/// the way it decides <c>=</c> and <c>LIKE</c>.
/// </summary>
/// <remarks>
/// <para>
/// The model, probe-confirmed against SQL Server 2025 (matrix in
/// <c>docs/claude/collations.md</c>): a search folds exactly the halves the
/// collation's name declares — case, accent, kanatype and width — so
/// <c>CHARINDEX(N'e', N'café')</c> answers 4 under an <c>_AI</c> collation and
/// 0 under an <c>_AS</c> one, and a binary collation folds nothing.
/// </para>
/// <para>
/// <strong>How much of the subject a match consumes is not the needle's
/// length.</strong> Under <c>_AI</c> a one-character <c>e</c> matches a
/// decomposed <c>e</c> + U+0301 and eats both code units, which is why
/// <c>REPLACE</c> over a decomposed <c>café</c> comes back four characters
/// long. <see cref="IndexOf"/> reports that length so each caller can apply its
/// own rule: <c>REPLACE</c> resumes past the whole match, while
/// <c>STRING_SPLIT</c> resumes one character past the separator's start
/// (probe-confirmed — splitting <c>N'assb'</c> on <c>ß</c> on real yields
/// <c>a</c> and <c>sb</c>, not <c>a</c> and <c>b</c>).
/// </para>
/// <para>
/// <strong>A zero-length match is not a match.</strong> A needle the collation
/// gives no weight — an empty string, or a bare combining mark under an
/// accent-insensitive collation — matches at every position with a length of
/// zero as far as <see cref="System.Globalization.CompareInfo"/> is concerned;
/// real reports "not found" for both (<c>CHARINDEX(N'', N'abc')</c> and
/// <c>CHARINDEX(NCHAR(0x0301), N'abc')</c> under <c>_CI_AI</c> are each 0), so
/// the length is what the miss is keyed on rather than a special case per
/// caller.
/// </para>
/// </remarks>
internal abstract partial class Collation
{
    /// <summary>
    /// The index in <paramref name="subject"/> at or after
    /// <paramref name="start"/> where <paramref name="needle"/> first matches
    /// under this collation, or <c>-1</c> when it doesn't;
    /// <paramref name="matchLength"/> receives how many of the subject's code
    /// units the match consumed (never zero on a hit).
    /// </summary>
    internal int IndexOf(string subject, string needle, int start, out int matchLength)
    {
        var from = Math.Clamp(start, 0, subject.Length);
        var found = this.IndexOfCore(subject.AsSpan(from), needle, out matchLength);
        // A weightless needle "matches" everywhere with no length at all; real
        // reports not-found for the searching scalars, so the length is what the
        // miss is keyed on.
        if (found < 0 || matchLength == 0)
        {
            matchLength = 0;
            return -1;
        }

        return from + found;
    }

    /// <summary>
    /// The 0-based index in <paramref name="set"/> where
    /// <paramref name="element"/> — one character taken from an input — first
    /// matches, or <c>-1</c>. This is the shape <c>TRANSLATE</c> and the
    /// <c>TRIM</c> family need: they walk their input one character at a time
    /// and ask whether the character set holds it, and <c>TRANSLATE</c>
    /// substitutes by the <em>position</em> the search reports (probed:
    /// <c>TRANSLATE(N'aßb', N'ass', N'123')</c> on real is <c>12b</c>, the
    /// ligature answering at the second character's position).
    /// </summary>
    /// <remarks>
    /// <para>An element the collation gives no weight — a bare combining mark
    /// under an accent-insensitive collation, a lone surrogate half — falls back
    /// to an ordinal reading, so it matches itself and nothing else. That is
    /// real's own split: <c>TRIM(NCHAR(0x0301) FROM N'abc')</c> under
    /// <c>_CI_AI</c> strips nothing, while the same call over
    /// <c>N'cafe' + NCHAR(0x0301)</c> strips the mark.</para>
    /// <para>Real goes further and equates a standalone mark with <em>any</em>
    /// other standalone mark, which <c>CompareInfo</c> does not — see the
    /// standalone-combining-mark entry in <c>docs/claude/backlog.md</c>.</para>
    /// </remarks>
    internal int IndexOfElement(string set, ReadOnlySpan<char> element)
    {
        if (element.Length == 1 && char.IsSurrogate(element[0]))
            return set.AsSpan().IndexOf(element[0]);
        var found = this.IndexOfCore(set, element, out var matchLength);
        // The ordinal reading is consulted only for a *weightless* element,
        // which the zero match length is the tell for: a plain miss can't hide
        // an ordinal hit, since a string always matches itself linguistically.
        // Keeping it off the miss path is what stops a `TRANSLATE` — which asks
        // this per input character, and mostly misses — from paying two
        // searches per character.
        return found >= 0 && matchLength == 0
            ? set.AsSpan().IndexOf(element, StringComparison.Ordinal)
            : found;
    }

    /// <summary>
    /// <see cref="IndexOfElement"/> with the character set bound once. The
    /// callers that need it — <c>TRANSLATE</c> and the <c>TRIM</c> family — ask
    /// the same set about every character of their input, and the eligibility
    /// test for the ordinal path (does the collation fold nothing but ASCII case
    /// across the printable range, and is the set printable ASCII?) is a
    /// property of the set, not of the character. Hoisting it turns the common
    /// call into one vectorized scan of a short span.
    /// </summary>
    internal readonly struct ElementMatcher
    {
        private readonly Collation collation;

        private readonly string set;

        private readonly bool asciiFast;

        private readonly bool caseSensitive;

        internal ElementMatcher(Collation collation, string set)
        {
            this.collation = collation;
            this.set = set;
            this.caseSensitive = collation.CaseSensitive;
            this.asciiFast = collation.PrintableAsciiFoldsCaseOnly && IsPrintableAscii(set);
        }

        /// <summary>The 0-based index in the set where <paramref name="element"/> matches, or <c>-1</c>.</summary>
        internal int IndexOf(ReadOnlySpan<char> element)
        {
            if (!this.asciiFast || element.Length != 1 || element[0] is < ' ' or > '~')
                return this.collation.IndexOfElement(this.set, element);
            var c = element[0];
            return !this.caseSensitive && char.IsAsciiLetter(c)
                ? this.set.AsSpan().IndexOfAny(char.ToLowerInvariant(c), char.ToUpperInvariant(c))
                : this.set.AsSpan().IndexOf(c);
        }
    }

    /// <summary>
    /// True when <paramref name="run"/> matches at the start of
    /// <paramref name="subject"/> under this collation, with
    /// <paramref name="matchLength"/> receiving how much of the subject it
    /// consumed. <c>LIKE</c>'s literal runs read this; the caller adds its own
    /// character-boundary rule.
    /// </summary>
    internal bool IsPrefix(ReadOnlySpan<char> subject, ReadOnlySpan<char> run, out int matchLength)
    {
        matchLength = 0;
        if (this.LinguisticMatching is not { } linguistic)
            return false;
        if (!linguistic.Info.IsPrefix(subject, run, linguistic.Options, out var length)
            || !this.MatchIsExact(subject[..length], run, linguistic))
        {
            return false;
        }

        matchLength = length;
        return true;
    }

    /// <summary>
    /// The raw search. <paramref name="matchLength"/> can come back <c>0</c> for
    /// a weightless needle, and the two entry points above read that
    /// differently — see their own remarks.
    /// </summary>
    private int IndexOfCore(ReadOnlySpan<char> window, ReadOnlySpan<char> needle, out int matchLength)
    {
        matchLength = 0;
        if (needle.IsEmpty)
            return -1;

        if (this.LinguisticMatching is not { } linguistic)
        {
            var ordinal = window.IndexOf(needle, StringComparison.Ordinal);
            if (ordinal < 0)
                return -1;
            matchLength = needle.Length;
            return ordinal;
        }

        // The same proof the LIKE matcher's fast path rests on: across
        // U+0020..U+007E this collation folds nothing but ASCII case, and an
        // all-printable-ASCII pair admits no expansion and no combining mark
        // that could reach across a match boundary — so an ordinal search gives
        // the linguistic answer with a vectorized scan.
        if (this.PrintableAsciiFoldsCaseOnly && IsPrintableAscii(needle) && IsPrintableAscii(window))
        {
            // A one-character needle is the shape `TRANSLATE` and the `TRIM`
            // family ask in, once per input character; a single-value
            // `IndexOfAny` beats routing a length-1 span through the
            // string-comparison machinery there.
            var fast = needle.Length == 1
                ? this.CaseSensitive || !char.IsAsciiLetter(needle[0])
                    ? window.IndexOf(needle[0])
                    : window.IndexOfAny(char.ToLowerInvariant(needle[0]), char.ToUpperInvariant(needle[0]))
                : window.IndexOf(needle, this.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            if (fast < 0)
                return -1;
            matchLength = needle.Length;
            return fast;
        }

        var offset = 0;
        while (offset <= window.Length)
        {
            var found = linguistic.Info.IndexOf(window[offset..], needle, linguistic.Options, out var length);
            if (found < 0)
                return -1;
            found += offset;
            if (this.MatchIsExact(window.Slice(found, length), needle, linguistic))
            {
                matchLength = length;
                return found;
            }

            offset = found + 1;
        }

        return -1;
    }

    /// <summary>
    /// Re-reads a hit through <c>CompareInfo.Compare</c>, which is the only one
    /// of the three APIs that keeps every half of the options. <c>IndexOf</c>
    /// and <c>IsPrefix</c> silently drop the case level once
    /// <c>CompareOptions.IgnoreNonSpace</c> is set, so under a <c>_CS_AI</c>
    /// collation they report <c>N'E'</c> as matching <c>N'é'</c> where
    /// <c>Compare</c> — and real — hold the two apart
    /// (<c>CHARINDEX(N'E', N'café' COLLATE Latin1_General_CS_AI)</c> is 0 on
    /// SQL Server 2025). Only a case-sensitive collation pays the extra read.
    /// </summary>
    private bool MatchIsExact(ReadOnlySpan<char> matched, ReadOnlySpan<char> needle, (CompareInfo Info, CompareOptions Options) linguistic) =>
        !this.CaseSensitive || linguistic.Info.Compare(matched, needle, linguistic.Options) == 0;

    /// <summary>Vectorized: U+0020..U+007E, the range the ordinal path is proven over.</summary>
    private static bool IsPrintableAscii(ReadOnlySpan<char> s) => s.IndexOfAnyExceptInRange(' ', '~') < 0;
}
