using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text;
using System.Text.RegularExpressions;

namespace SqlServerSimulator.Parser.Expressions;

/// <summary>
/// Which member family raised a pattern error. SQL Server 2025 numbers the
/// <c>REGEXP_*</c> pattern diagnostics identically for every member but shifts
/// the error state between the scalar / predicate family and the rowset family
/// — probe-confirmed, see <c>docs/claude/scalars.md</c>.
/// </summary>
internal enum RegexCallSite
{
    /// <summary><c>REGEXP_COUNT</c> / <c>INSTR</c> / <c>REPLACE</c> / <c>SUBSTR</c> and the <c>REGEXP_LIKE</c> predicate.</summary>
    Scalar,

    /// <summary><c>REGEXP_MATCHES</c> / <c>REGEXP_SPLIT_TO_TABLE</c>.</summary>
    Rowset,
}

/// <summary>
/// Translates SQL Server 2025's <c>REGEXP_*</c> pattern dialect — RE2, as
/// shipped in the box product — into an equivalent .NET
/// <see cref="Regex"/>, rejecting every construct RE2 rejects with real's own
/// wording so the simulator can't accept a pattern production would refuse.
/// </summary>
/// <remarks>
/// <para>
/// The engine underneath the box's <c>REGEXP_*</c> members is RE2: its parser
/// error strings surface verbatim inside Msg 19300 (<c>invalid escape
/// sequence: \1</c>, <c>invalid perl operator: (?=</c>, <c>bad repetition
/// operator: ++</c>), and its octal-escape quirk reproduces exactly —
/// <c>\1</c> alone is rejected as a backreference while <c>\101</c> parses as
/// octal, which is RE2's C++ escape parser and not Go's. Probe-confirmed
/// against SQL Server 2025 (17.0.4065.4).
/// </para>
/// <para>
/// .NET's <see cref="Regex"/> accepts a strict superset, so the walk below
/// serves two purposes at once: it raises real's error for the RE2-illegal
/// constructs (backreferences, lookaround, atomic groups, possessive
/// quantifiers, inline comments, free-spacing mode, <c>\K</c> / <c>\Z</c> /
/// <c>\e</c> / <c>\c</c>), and it rewrites the constructs whose <i>meaning</i>
/// differs between the two engines:
/// </para>
/// <list type="bullet">
/// <item><description><c>$</c> outside multiline mode — RE2 anchors at end of
/// text, .NET at end of text <i>or</i> before a final newline — is emitted as
/// <c>\z</c>.</description></item>
/// <item><description><c>\d</c> / <c>\D</c> / <c>\s</c> / <c>\S</c> / <c>\w</c> /
/// <c>\W</c> are ASCII-only in RE2 and Unicode-aware in .NET, so each is
/// expanded to its explicit ASCII class (or, inside a character class, to the
/// spliced ranges).</description></item>
/// <item><description><c>\b</c> / <c>\B</c> follow RE2's ASCII word set, so
/// they expand to the equivalent lookaround pair.</description></item>
/// <item><description>POSIX classes (<c>[[:digit:]]</c>), <c>\x{…}</c>, octal
/// escapes, <c>\Q…\E</c> and <c>(?P&lt;name&gt;…)</c> have no .NET spelling and
/// are rewritten to one.</description></item>
/// <item><description><c>(?U)</c> (ungreedy) has no .NET option, so the walk
/// swaps each quantifier's greediness while the flag is in scope.</description></item>
/// </list>
/// <para>
/// <b>Divergences.</b> Matching runs over UTF-16 code units where RE2 runs over
/// code points, so a supplementary character counts as two positions in
/// <c>REGEXP_INSTR</c> / <c>REGEXP_MATCHES</c>. RE2 script names
/// (<c>\p{Greek}</c>) raise <see cref="NotSupportedException"/> — .NET's
/// <c>\p</c> covers general categories and named blocks, not scripts; the
/// general categories themselves pass through unchanged.
/// </para>
/// </remarks>
internal static class RegexDialect
{
    /// <summary>
    /// Upper bound on a single match attempt. Real's RE2 is backtracking-free,
    /// so no pattern can run away there; the simulator reaches the same
    /// guarantee by compiling with <see cref="RegexOptions.NonBacktracking"/>
    /// wherever the translation allows it, and this timeout bounds the
    /// remaining lookaround-bearing patterns (the <c>\b</c> / <c>\B</c>
    /// expansions, which .NET's non-backtracking engine can't take). Reached
    /// only by a pathological pattern, and surfaced as the
    /// <see cref="RegexMatchTimeoutException"/> .NET raises rather than a
    /// simulated SQL error — real has no such error to mirror.
    /// </summary>
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Compiled-pattern cache keyed by the translated .NET pattern plus its
    /// options. Bounded: a workload that generates unbounded distinct patterns
    /// (a per-row pattern column) clears rather than grows without limit.
    /// </summary>
    private static readonly ConcurrentDictionary<(string Pattern, RegexOptions Options), Regex> Cache = new();

    private const int CacheCapacity = 512;

    /// <summary>Members RE2 assigns to <c>\d</c>.</summary>
    private const string DigitMembers = "0-9";

    /// <summary>Members RE2 assigns to <c>\w</c>.</summary>
    private const string WordMembers = "0-9A-Za-z_";

    /// <summary>
    /// Members RE2 assigns to <c>\s</c> — tab, newline, form feed, carriage
    /// return and space. Vertical tab is <i>not</i> included, unlike .NET's
    /// <c>\s</c>; probe-confirmed against SQL Server 2025.
    /// </summary>
    private const string SpaceMembers = "\\u0009\\u000A\\u000C\\u000D\\u0020";

    /// <summary>Complement ranges of <see cref="DigitMembers"/> over the UTF-16 range.</summary>
    private const string NonDigitMembers = "\\u0000-\\u002F\\u003A-\\uFFFF";

    /// <summary>Complement ranges of <see cref="WordMembers"/> over the UTF-16 range.</summary>
    private const string NonWordMembers = "\\u0000-\\u002F\\u003A-\\u0040\\u005B-\\u005E\\u0060\\u007B-\\uFFFF";

    /// <summary>Complement ranges of <see cref="SpaceMembers"/> over the UTF-16 range.</summary>
    private const string NonSpaceMembers = "\\u0000-\\u0008\\u000B\\u000E-\\u001F\\u0021-\\uFFFF";

    /// <summary>ASCII word-boundary expansion for <c>\b</c>.</summary>
    private const string WordBoundary = "(?:(?<![" + WordMembers + "])(?=[" + WordMembers + "])|(?<=[" + WordMembers + "])(?![" + WordMembers + "]))";

    /// <summary>ASCII non-word-boundary expansion for <c>\B</c>.</summary>
    private const string NonWordBoundary = "(?:(?<![" + WordMembers + "])(?![" + WordMembers + "])|(?<=[" + WordMembers + "])(?=[" + WordMembers + "]))";

    /// <summary>
    /// Translates <paramref name="pattern"/> and returns the compiled .NET
    /// equivalent honoring <paramref name="flags"/>.
    /// </summary>
    public static Regex Compile(string pattern, RegexFlags flags, RegexCallSite callSite)
    {
        var walker = new Translator(pattern, flags, callSite);
        var translated = walker.Translate();

        var options = RegexOptions.CultureInvariant;
        if (flags.IgnoreCase)
            options |= RegexOptions.IgnoreCase;
        if (flags.Multiline)
            options |= RegexOptions.Multiline;
        if (flags.DotMatchesNewline)
            options |= RegexOptions.Singleline;
        // The non-backtracking engine gives RE2's linear-time guarantee but
        // refuses lookaround, which only the \b / \B expansions emit.
        if (!walker.EmittedLookaround)
            options |= RegexOptions.NonBacktracking;

        var key = (translated, options);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        if (Cache.Count >= CacheCapacity)
            Cache.Clear();
        return Cache.GetOrAdd(key, new Regex(translated, options, MatchTimeout));
    }

    /// <summary>
    /// One left-to-right pass over the RE2 source, emitting .NET syntax. The
    /// position / flag / repetition bookkeeping lives in fields so each
    /// construct's handler reads as a small step; every rejection path throws
    /// before the output is read.
    /// </summary>
    private sealed class Translator(string pattern, RegexFlags flags, RegexCallSite callSite)
    {
        private readonly StringBuilder output = new(pattern.Length + 16);

        /// <summary>Nesting stack of (multiline, ungreedy) as they stood when each group opened.</summary>
        private readonly List<(bool Multiline, bool Ungreedy)> groupStack = [];

        private int index;

        private bool multiline = flags.Multiline;

        private bool ungreedy;

        /// <summary>True once a repeatable atom has been emitted in the current branch.</summary>
        private bool atomPending;

        /// <summary>Source text of the repetition operator just applied, or null when none.</summary>
        private string? repetition;

        /// <summary>True once the current repetition already took its lazy <c>?</c> suffix.</summary>
        private bool lazyApplied;

        /// <summary>Set when the emitted pattern contains a lookaround, which bars the non-backtracking engine.</summary>
        public bool EmittedLookaround;

        public string Translate()
        {
            while (this.index < pattern.Length)
            {
                switch (pattern[this.index])
                {
                    case '(':
                        this.TranslateGroupOpen();
                        break;
                    case ')':
                        this.TranslateGroupClose();
                        break;
                    case '[':
                        this.TranslateCharacterClass();
                        break;
                    case '\\':
                        this.TranslateEscape();
                        break;
                    case '|':
                        this.index++;
                        _ = this.output.Append('|');
                        this.atomPending = false;
                        this.repetition = null;
                        break;
                    case '*' or '+' or '?':
                        this.TranslateSimpleRepetition();
                        break;
                    case '{':
                        this.TranslateBraceRepetition();
                        break;
                    case '^':
                        this.index++;
                        _ = this.output.Append('^');
                        this.MarkAtom();
                        break;
                    case '$':
                        this.index++;
                        // RE2's `$` is end-of-text unless multiline is in
                        // scope; .NET's also matches before a trailing
                        // newline, so the non-multiline form becomes `\z`.
                        _ = this.output.Append(this.multiline ? "$" : "\\z");
                        this.MarkAtom();
                        break;
                    case '.':
                        this.index++;
                        _ = this.output.Append('.');
                        this.MarkAtom();
                        break;
                    default:
                        this.AppendLiteral(pattern[this.index++]);
                        this.MarkAtom();
                        break;
                }
            }

            return this.groupStack.Count > 0
                ? throw SimulatedSqlException.RegexMissingCloseParen(pattern, callSite)
                : this.output.ToString();
        }

        /// <summary>Records that a repeatable atom was just emitted.</summary>
        private void MarkAtom()
        {
            this.atomPending = true;
            this.repetition = null;
            this.lazyApplied = false;
        }

        /// <summary>Appends <paramref name="c"/> as a literal, escaping .NET metacharacters.</summary>
        private void AppendLiteral(char c)
        {
            if (c is '\\' or '*' or '+' or '?' or '|' or '{' or '}' or '[' or ']' or '(' or ')' or '^' or '$' or '.' or '#' or ' ')
                _ = this.output.Append('\\');
            _ = this.output.Append(c);
        }

        private SimulatedSqlException Invalid(string detail) =>
            SimulatedSqlException.RegexInvalidPattern(pattern, detail, callSite);

        // ---- repetition -------------------------------------------------

        /// <summary><c>*</c> / <c>+</c> / <c>?</c>, including the lazy <c>?</c> suffix.</summary>
        private void TranslateSimpleRepetition()
        {
            var op = pattern[this.index];
            // A `?` directly after a repetition is the lazy modifier, not a
            // new operator — but only the first one (`a*??` is rejected).
            if (op == '?' && this.repetition is not null && !this.lazyApplied)
            {
                this.index++;
                this.lazyApplied = true;
                // Under (?U) the base quantifier was already emitted swapped,
                // so the trailing `?` restores greediness by removing it.
                if (this.ungreedy)
                    this.output.Length--;
                else
                    _ = this.output.Append('?');
                // Keep the suffix in the recorded operator text so a third
                // repetition reports RE2's full `*??` rather than just `*?`.
                this.repetition += "?";
                return;
            }
            this.index++;
            this.ApplyRepetition(op.ToString());
        }

        /// <summary>
        /// A <c>{</c> that opens a well-formed <c>{n}</c> / <c>{n,}</c> /
        /// <c>{n,m}</c> counted repetition; anything else is a literal brace
        /// (RE2 reads <c>a{,2}</c> as five literal characters).
        /// </summary>
        private void TranslateBraceRepetition()
        {
            if (!TryReadBraceRepetition(pattern, this.index, out var end, out var min, out var max))
            {
                this.AppendLiteral(pattern[this.index++]);
                this.MarkAtom();
                return;
            }
            var text = pattern[this.index..end];
            this.index = end;
            // RE2 caps a counted repetition at 1000 and requires min <= max.
            if (min > 1000 || max > 1000 || (max >= 0 && min > max))
                throw this.Invalid($"invalid repetition size: {text}");
            this.ApplyRepetition(text);
        }

        /// <summary>
        /// Emits <paramref name="text"/> as a repetition over the preceding
        /// atom, raising RE2's diagnostics for a missing operand or a stacked
        /// operator.
        /// </summary>
        private void ApplyRepetition(string text)
        {
            if (this.repetition is not null)
                throw this.Invalid($"bad repetition operator: {this.repetition}{text}");
            if (!this.atomPending)
                throw this.Invalid($"no argument for repetition operator: {text}");
            _ = this.output.Append(text);
            if (this.ungreedy)
                _ = this.output.Append('?');
            this.repetition = text;
            this.lazyApplied = false;
        }

        /// <summary>
        /// Reads a counted-repetition suffix starting at the <c>{</c> in
        /// <paramref name="source"/>[<paramref name="start"/>]. Reports the
        /// index just past the closing <c>}</c>, the minimum, and the maximum
        /// (-1 for the open-ended <c>{n,}</c> form).
        /// </summary>
        private static bool TryReadBraceRepetition(string source, int start, out int end, out int min, out int max)
        {
            end = 0;
            min = 0;
            max = 0;
            var i = start + 1;
            var digits = 0;
            while (i < source.Length && char.IsAsciiDigit(source[i]))
            {
                min = Math.Min((min * 10) + (source[i] - '0'), 100000);
                i++;
                digits++;
            }
            if (digits == 0)
                return false;
            if (i < source.Length && source[i] == ',')
            {
                i++;
                var maxDigits = 0;
                while (i < source.Length && char.IsAsciiDigit(source[i]))
                {
                    max = Math.Min((max * 10) + (source[i] - '0'), 100000);
                    i++;
                    maxDigits++;
                }
                if (maxDigits == 0)
                    max = -1;
            }
            else
            {
                max = min;
            }
            if (i >= source.Length || source[i] != '}')
                return false;
            end = i + 1;
            return true;
        }

        // ---- groups -----------------------------------------------------

        /// <summary>
        /// <c>(</c> — a capture group, <c>(?:</c>, <c>(?P&lt;name&gt;</c>, or an
        /// inline flag group. Every other Perl extension is an RE2 rejection.
        /// </summary>
        private void TranslateGroupOpen()
        {
            this.index++;
            this.groupStack.Add((this.multiline, this.ungreedy));

            if (this.index >= pattern.Length || pattern[this.index] != '?')
            {
                _ = this.output.Append('(');
                this.atomPending = false;
                this.repetition = null;
                return;
            }

            this.index++; // consume '?'
            if (this.index >= pattern.Length)
                throw this.Invalid("invalid perl operator: (?");

            switch (pattern[this.index])
            {
                case ':':
                    this.index++;
                    _ = this.output.Append("(?:");
                    this.atomPending = false;
                    this.repetition = null;
                    return;
                case 'P':
                    this.TranslateNamedGroup();
                    return;
                case '-' or 'U' or 'i' or 'm' or 's':
                    this.TranslateFlagGroup();
                    return;
                default:
                    throw this.Invalid($"invalid perl operator: (?{pattern[this.index]}");
            }
        }

        /// <summary>
        /// <c>(?P&lt;name&gt;…)</c>. The name is unobservable through the SQL
        /// surface — <c>REGEXP_SUBSTR</c>'s group argument and
        /// <c>REGEXP_MATCHES</c>'s <c>substring_matches</c> are both positional
        /// — so a validated name becomes a plain capture group, which sidesteps
        /// .NET's stricter naming rules (RE2 accepts <c>(?P&lt;1x&gt;…)</c>).
        /// </summary>
        private void TranslateNamedGroup()
        {
            this.index++; // consume 'P'
            if (this.index >= pattern.Length || pattern[this.index] != '<')
                throw this.Invalid("invalid perl operator: (?P");
            var nameStart = ++this.index;
            while (this.index < pattern.Length && pattern[this.index] != '>')
                this.index++;
            var closed = this.index < pattern.Length;
            var name = pattern[nameStart..this.index];
            if (closed)
                this.index++;
            if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                throw this.Invalid($"invalid named capture group: (?P<{name}{(closed ? ">" : string.Empty)}");
            _ = this.output.Append('(');
            this.atomPending = false;
            this.repetition = null;
        }

        /// <summary>
        /// <c>(?imsU)</c> (scope-wide) or <c>(?imsU:…)</c> (group-scoped). The
        /// <c>U</c> (ungreedy) flag has no .NET equivalent and is applied by
        /// swapping each quantifier's greediness while it's in scope.
        /// </summary>
        private void TranslateFlagGroup()
        {
            var start = this.index;
            var negate = false;
            var on = new StringBuilder();
            var off = new StringBuilder();
            while (this.index < pattern.Length && pattern[this.index] is '-' or 'U' or 'i' or 'm' or 's')
            {
                var c = pattern[this.index++];
                if (c == '-')
                {
                    negate = true;
                    continue;
                }
                if (c == 'U')
                {
                    this.ungreedy = !negate;
                    continue;
                }
                if (c == 'm')
                    this.multiline = !negate;
                _ = (negate ? off : on).Append(c);
            }

            if (this.index >= pattern.Length || pattern[this.index] is not (')' or ':'))
                throw this.Invalid($"invalid perl operator: (?{pattern[start]}");

            var scoped = pattern[this.index] == ':';
            this.index++;

            if (on.Length > 0 || off.Length > 0)
            {
                _ = this.output.Append("(?").Append(on);
                if (off.Length > 0)
                    _ = this.output.Append('-').Append(off);
                _ = this.output.Append(scoped ? ":" : ")");
            }
            else if (scoped)
            {
                _ = this.output.Append("(?:");
            }

            if (scoped)
                return;

            // A scope-wide `(?i)` closes its own group immediately: it isn't a
            // repeatable atom (`(?i)*` is RE2's "no argument for repetition
            // operator"), and its flags stay in effect until the enclosing
            // group closes — so the frame is dropped without restoring.
            this.groupStack.RemoveAt(this.groupStack.Count - 1);
            this.atomPending = false;
            this.repetition = null;
        }

        private void TranslateGroupClose()
        {
            if (this.groupStack.Count == 0)
                throw SimulatedSqlException.RegexUnexpectedCloseParen(pattern, callSite);
            this.index++;
            (this.multiline, this.ungreedy) = this.groupStack[^1];
            this.groupStack.RemoveAt(this.groupStack.Count - 1);
            _ = this.output.Append(')');
            this.MarkAtom();
        }

        // ---- escapes ----------------------------------------------------

        /// <summary>A backslash escape outside a character class.</summary>
        private void TranslateEscape()
        {
            if (this.index + 1 >= pattern.Length)
                throw SimulatedSqlException.RegexTrailingBackslash(pattern, callSite);
            switch (pattern[this.index + 1])
            {
                case 'A':
                    this.index += 2;
                    _ = this.output.Append("\\A");
                    this.MarkAtom();
                    return;
                case 'B':
                    this.index += 2;
                    this.EmittedLookaround = true;
                    _ = this.output.Append(NonWordBoundary);
                    this.MarkAtom();
                    return;
                case 'D':
                    this.EmitShorthand($"[^{DigitMembers}]");
                    return;
                case 'Q':
                    this.TranslateQuotedRun();
                    return;
                case 'S':
                    this.EmitShorthand($"[^{SpaceMembers}]");
                    return;
                case 'W':
                    this.EmitShorthand($"[^{WordMembers}]");
                    return;
                case 'b':
                    this.index += 2;
                    this.EmittedLookaround = true;
                    _ = this.output.Append(WordBoundary);
                    this.MarkAtom();
                    return;
                case 'd':
                    this.EmitShorthand($"[{DigitMembers}]");
                    return;
                case 's':
                    this.EmitShorthand($"[{SpaceMembers}]");
                    return;
                case 'w':
                    this.EmitShorthand($"[{WordMembers}]");
                    return;
                case 'z':
                    this.index += 2;
                    _ = this.output.Append("\\z");
                    this.MarkAtom();
                    return;
                case 'p' or 'P':
                    _ = this.output.Append(this.ReadUnicodeClass(insideClass: false));
                    this.MarkAtom();
                    return;
                default:
                    this.EmitCodePoint(this.ReadSimpleEscape());
                    this.MarkAtom();
                    return;
            }
        }

        private void EmitShorthand(string expansion)
        {
            this.index += 2;
            _ = this.output.Append(expansion);
            this.MarkAtom();
        }

        /// <summary>
        /// <c>\Q…\E</c> — a literal run. .NET has no equivalent, so each
        /// character is emitted escaped. An unterminated run extends to the end
        /// of the pattern, matching RE2.
        /// </summary>
        private void TranslateQuotedRun()
        {
            this.index += 2;
            while (this.index < pattern.Length)
            {
                if (pattern[this.index] == '\\' && this.index + 1 < pattern.Length && pattern[this.index + 1] == 'E')
                {
                    this.index += 2;
                    break;
                }
                this.AppendLiteral(pattern[this.index++]);
                this.MarkAtom();
            }
        }

        /// <summary>
        /// Resolves the escapes that denote a single character — control
        /// letters, hex, octal, and escaped punctuation — leaving the cursor
        /// past the escape. Raises RE2's <c>invalid escape sequence</c> for
        /// everything else, which is where backreferences (<c>\1</c>),
        /// <c>\K</c>, <c>\Z</c>, <c>\e</c> and <c>\c</c> land.
        /// </summary>
        private int ReadSimpleEscape()
        {
            var c = pattern[this.index + 1];
            switch (c)
            {
                case 'a':
                    this.index += 2;
                    return '\a';
                case 'f':
                    this.index += 2;
                    return '\f';
                case 'n':
                    this.index += 2;
                    return '\n';
                case 'r':
                    this.index += 2;
                    return '\r';
                case 't':
                    this.index += 2;
                    return '\t';
                case 'v':
                    this.index += 2;
                    return '\v';
                case 'x':
                    return this.ReadHexEscape();
                // RE2's C++ parser reads `\0` plus up to two octal digits, and
                // accepts a `\1`-`\7` lead only when another octal digit
                // follows — so `\101` is 'A' while a bare `\1` is the
                // unsupported backreference. Probe-confirmed on SQL Server 2025.
                case '0':
                    return this.ReadOctalEscape();
                case >= '1' and <= '7' when this.index + 2 < pattern.Length && pattern[this.index + 2] is >= '0' and <= '7':
                    return this.ReadOctalEscape();
                default:
                    if (char.IsAsciiLetterOrDigit(c))
                        throw this.Invalid($"invalid escape sequence: \\{c}");
                    this.index += 2;
                    return c;
            }
        }

        /// <summary><c>\xhh</c> or <c>\x{h…}</c>.</summary>
        private int ReadHexEscape()
        {
            var start = this.index;
            this.index += 2; // consume "\x"
            if (this.index < pattern.Length && pattern[this.index] == '{')
            {
                var digitsStart = ++this.index;
                while (this.index < pattern.Length && Uri.IsHexDigit(pattern[this.index]))
                    this.index++;
                var digits = pattern[digitsStart..this.index];
                // Real reports the offending text without its closing brace —
                // probe-confirmed: `\x{110000}` reports `\x{110000`.
                if (this.index >= pattern.Length || pattern[this.index] != '}' || digits.Length == 0
                    || !int.TryParse(digits, System.Globalization.NumberStyles.HexNumber, null, out var wide)
                    || wide > 0x10FFFF)
                {
                    throw this.Invalid($"invalid escape sequence: {pattern[start..this.index]}");
                }
                this.index++; // consume '}'
                return wide;
            }
            var value = 0;
            var count = 0;
            while (count < 2 && this.index < pattern.Length && Uri.IsHexDigit(pattern[this.index]))
            {
                value = (value * 16) + Uri.FromHex(pattern[this.index]);
                this.index++;
                count++;
            }
            return count == 0 ? throw this.Invalid("invalid escape sequence: \\x") : value;
        }

        /// <summary><c>\0</c> plus up to two more octal digits, or <c>\NNN</c>.</summary>
        private int ReadOctalEscape()
        {
            this.index++; // consume '\'
            var value = 0;
            var count = 0;
            while (count < 3 && this.index < pattern.Length && pattern[this.index] is >= '0' and <= '7')
            {
                value = (value * 8) + (pattern[this.index] - '0');
                this.index++;
                count++;
            }
            return value;
        }

        /// <summary>
        /// <c>\pX</c> / <c>\p{Name}</c> and their negated <c>\P</c> forms,
        /// rendered as the .NET spelling. RE2's <c>Any</c> becomes an explicit
        /// full range; script names have no .NET equivalent and raise.
        /// </summary>
        private string ReadUnicodeClass(bool insideClass)
        {
            var letter = pattern[this.index + 1];
            var negated = letter == 'P';
            this.index += 2;
            if (this.index >= pattern.Length)
                throw this.Invalid($"invalid escape sequence: \\{letter}");

            string name;
            if (pattern[this.index] == '{')
            {
                var nameStart = ++this.index;
                while (this.index < pattern.Length && pattern[this.index] != '}')
                    this.index++;
                if (this.index >= pattern.Length)
                    throw this.Invalid($"invalid character class range: \\{letter}{{{pattern[nameStart..]}");
                name = pattern[nameStart..this.index];
                this.index++;
            }
            else
            {
                name = pattern[this.index].ToString();
                this.index++;
            }

            return name == "Any" && !negated
                ? insideClass ? "\\u0000-\\uFFFF" : "[\\s\\S]"
                : IsUnicodeGeneralCategory(name)
                ? $"\\{letter}{{{name}}}"
                : throw (Re2ScriptNames.Contains(name)
                    ? new NotSupportedException($"RE2 Unicode script names in a REGEXP_* pattern (\\p{{{name}}}) are not modeled; general categories such as \\p{{L}} are.")
                    : this.Invalid($"invalid character class range: \\{letter}{{{name}}}"));
        }

        /// <summary>
        /// Emits a resolved code point, escaping it when .NET would read it as
        /// syntax. Supplementary values render as their surrogate pair.
        /// </summary>
        private void EmitCodePoint(int codePoint)
        {
            if (codePoint > 0xFFFF)
                _ = this.output.Append(char.ConvertFromUtf32(codePoint));
            else
                this.AppendLiteral((char)codePoint);
        }

        // ---- character classes ------------------------------------------

        /// <summary>
        /// <c>[…]</c>. RE2 reads a <c>]</c> in first position as a literal, so
        /// <c>[]</c> is an unterminated class rather than an empty one.
        /// </summary>
        private void TranslateCharacterClass()
        {
            var body = new StringBuilder();
            this.index++; // consume '['
            if (this.index < pattern.Length && pattern[this.index] == '^')
            {
                this.index++;
                _ = body.Append('^');
            }
            var first = true;
            var closed = false;
            while (this.index < pattern.Length)
            {
                if (pattern[this.index] == ']' && !first)
                {
                    this.index++;
                    closed = true;
                    break;
                }
                first = false;
                if (pattern[this.index] == '[' && this.TryTranslatePosixClass(body))
                    continue;
                this.TranslateClassMember(body);
            }
            if (!closed)
                throw SimulatedSqlException.RegexMissingCloseBracket(pattern, callSite);
            _ = this.output.Append('[').Append(body).Append(']');
            this.MarkAtom();
        }

        /// <summary>
        /// One member of a character class: a class-valued escape (which can't
        /// be a range endpoint), a range, or a single character.
        /// </summary>
        private void TranslateClassMember(StringBuilder body)
        {
            if (pattern[this.index] != '\\')
            {
                this.AppendClassRangeOrChar(body, pattern[this.index++]);
                return;
            }
            if (this.index + 1 >= pattern.Length)
                throw SimulatedSqlException.RegexTrailingBackslash(pattern, callSite);
            switch (pattern[this.index + 1])
            {
                case 'D':
                    this.AppendClassShorthand(body, NonDigitMembers);
                    return;
                case 'S':
                    this.AppendClassShorthand(body, NonSpaceMembers);
                    return;
                case 'W':
                    this.AppendClassShorthand(body, NonWordMembers);
                    return;
                // \b is backspace in a .NET class but has no meaning in an RE2
                // class — real rejects it.
                case 'b':
                    throw this.Invalid("invalid escape sequence: \\b");
                case 'd':
                    this.AppendClassShorthand(body, DigitMembers);
                    return;
                case 's':
                    this.AppendClassShorthand(body, SpaceMembers);
                    return;
                case 'w':
                    this.AppendClassShorthand(body, WordMembers);
                    return;
                case 'p' or 'P':
                    _ = body.Append(this.ReadUnicodeClass(insideClass: true));
                    return;
                default:
                    this.AppendClassRangeOrChar(body, this.ReadSimpleEscape());
                    return;
            }
        }

        /// <summary>
        /// Splices a shorthand class's members into the enclosing class. A
        /// shorthand can't be a range endpoint — RE2 reports the escape itself
        /// as invalid for <c>[a-\d]</c>.
        /// </summary>
        private void AppendClassShorthand(StringBuilder body, string members)
        {
            var escaped = pattern[this.index + 1];
            this.index += 2;
            if (this.index + 1 < pattern.Length && pattern[this.index] == '-' && pattern[this.index + 1] != ']')
                throw this.Invalid($"invalid escape sequence: \\{escaped}");
            _ = body.Append(members);
        }

        /// <summary>
        /// Emits <paramref name="low"/> as a single class member, or as the
        /// start of a range when a <c>-</c> plus endpoint follows.
        /// </summary>
        private void AppendClassRangeOrChar(StringBuilder body, int low)
        {
            if (this.index >= pattern.Length || pattern[this.index] != '-'
                || this.index + 1 >= pattern.Length || pattern[this.index + 1] == ']')
            {
                AppendClassChar(body, low);
                return;
            }
            this.index++; // consume '-'
            int high;
            if (pattern[this.index] == '\\')
            {
                if (pattern[this.index + 1] is 'D' or 'P' or 'S' or 'W' or 'b' or 'd' or 'p' or 's' or 'w')
                    throw this.Invalid($"invalid escape sequence: \\{pattern[this.index + 1]}");
                high = this.ReadSimpleEscape();
            }
            else
            {
                high = pattern[this.index++];
            }
            if (high < low)
                throw this.Invalid($"invalid character class range: {char.ConvertFromUtf32(low)}-{char.ConvertFromUtf32(high)}");
            AppendClassChar(body, low);
            _ = body.Append('-');
            AppendClassChar(body, high);
        }

        /// <summary>Appends one code point to a class body, escaping .NET class syntax.</summary>
        private static void AppendClassChar(StringBuilder body, int codePoint)
        {
            if (codePoint > 0xFFFF)
            {
                _ = body.Append(char.ConvertFromUtf32(codePoint));
                return;
            }
            var c = (char)codePoint;
            if (c is '\\' or ']' or '^' or '-' or '[')
                _ = body.Append('\\');
            _ = body.Append(c);
        }

        /// <summary>
        /// <c>[:name:]</c> / <c>[:^name:]</c> inside a class. Returns false
        /// when the text isn't a POSIX class at all, leaving the <c>[</c> to be
        /// read as a literal member (RE2 reads <c>[[:alpha]]</c> that way).
        /// </summary>
        private bool TryTranslatePosixClass(StringBuilder body)
        {
            if (this.index + 1 >= pattern.Length || pattern[this.index + 1] != ':')
                return false;
            var close = pattern.IndexOf(":]", this.index + 2, StringComparison.Ordinal);
            if (close < 0)
                return false;
            var name = pattern[(this.index + 2)..close];
            var negated = name.StartsWith('^');
            if (negated)
                name = name[1..];
            var members = (negated ? NegatedPosixMembers(name) : PosixClassMembers(name))
                ?? throw this.Invalid($"invalid character class range: [:{(negated ? "^" : string.Empty)}{name}:]");
            this.index = close + 2;
            // .NET has no negated-class-inside-a-class syntax, so a negated
            // POSIX name splices its complement ranges instead.
            _ = body.Append(members);
            return true;
        }
    }

    /// <summary>The ASCII members RE2 assigns to each POSIX class name, or null when the name isn't one.</summary>
    private static string? PosixClassMembers(string name) => name switch
    {
        "alnum" => "0-9A-Za-z",
        "alpha" => "A-Za-z",
        "ascii" => "\\u0000-\\u007F",
        "blank" => "\\u0009\\u0020",
        "cntrl" => "\\u0000-\\u001F\\u007F",
        "digit" => "0-9",
        "graph" => "\\u0021-\\u007E",
        "lower" => "a-z",
        "print" => "\\u0020-\\u007E",
        "punct" => "\\u0021-\\u002F\\u003A-\\u0040\\u005B-\\u0060\\u007B-\\u007E",
        "space" => "\\u0009-\\u000D\\u0020",
        "upper" => "A-Z",
        "word" => "0-9A-Za-z_",
        "xdigit" => "0-9A-Fa-f",
        _ => null,
    };

    /// <summary>The complement of <see cref="PosixClassMembers"/> over the UTF-16 range, or null when the name isn't a POSIX class.</summary>
    private static string? NegatedPosixMembers(string name) => name switch
    {
        "alnum" => "\\u0000-\\u002F\\u003A-\\u0040\\u005B-\\u0060\\u007B-\\uFFFF",
        "alpha" => "\\u0000-\\u0040\\u005B-\\u0060\\u007B-\\uFFFF",
        "ascii" => "\\u0080-\\uFFFF",
        "blank" => "\\u0000-\\u0008\\u000A-\\u001F\\u0021-\\uFFFF",
        "cntrl" => "\\u0020-\\u007E\\u0080-\\uFFFF",
        "digit" => NonDigitMembers,
        "graph" => "\\u0000-\\u0020\\u007F-\\uFFFF",
        "lower" => "\\u0000-\\u0060\\u007B-\\uFFFF",
        "print" => "\\u0000-\\u001F\\u007F-\\uFFFF",
        "punct" => "\\u0000-\\u0020\\u0030-\\u0039\\u0041-\\u005A\\u0061-\\u007A\\u007F-\\uFFFF",
        "space" => "\\u0000-\\u0008\\u000E-\\u001F\\u0021-\\uFFFF",
        "upper" => "\\u0000-\\u0040\\u005B-\\uFFFF",
        "word" => NonWordMembers,
        "xdigit" => "\\u0000-\\u002F\\u003A-\\u0040\\u0047-\\u0060\\u0067-\\uFFFF",
        _ => null,
    };

    /// <summary>
    /// True for the Unicode general-category names both engines spell the same
    /// way, which pass through the translation untouched.
    /// </summary>
    private static bool IsUnicodeGeneralCategory(string name) => name switch
    {
        "C" or "Cc" or "Cf" or "Cn" or "Co" or "Cs"
            or "L" or "Ll" or "Lm" or "Lo" or "Lt" or "Lu"
            or "M" or "Mc" or "Me" or "Mn"
            or "N" or "Nd" or "Nl" or "No"
            or "P" or "Pc" or "Pd" or "Pe" or "Pf" or "Pi" or "Po" or "Ps"
            or "S" or "Sc" or "Sk" or "Sm" or "So"
            or "Z" or "Zl" or "Zp" or "Zs" => true,
        _ => false,
    };

    /// <summary>
    /// RE2's Unicode script names. Membership separates the unmodeled-script
    /// diagnostic from real's own rejection of an unknown class name: real
    /// compiles <c>\p{Greek}</c> and raises Msg 19300 for <c>\p{Foo}</c>, so the
    /// simulator has to tell the two apart.
    /// </summary>
    private static readonly FrozenSet<string> Re2ScriptNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Adlam", "Ahom", "Anatolian_Hieroglyphs", "Arabic", "Armenian", "Avestan",
        "Balinese", "Bamum", "Bassa_Vah", "Batak", "Bengali", "Bhaiksuki", "Bopomofo",
        "Brahmi", "Braille", "Buginese", "Buhid", "Canadian_Aboriginal", "Carian",
        "Caucasian_Albanian", "Chakma", "Cham", "Cherokee", "Chorasmian", "Common",
        "Coptic", "Cuneiform", "Cypriot", "Cypro_Minoan", "Cyrillic", "Deseret",
        "Devanagari", "Dives_Akuru", "Dogra", "Duployan", "Egyptian_Hieroglyphs",
        "Elbasan", "Elymaic", "Ethiopic", "Georgian", "Glagolitic", "Gothic",
        "Grantha", "Greek", "Gujarati", "Gunjala_Gondi", "Gurmukhi", "Han", "Hangul",
        "Hanifi_Rohingya", "Hanunoo", "Hatran", "Hebrew", "Hiragana",
        "Imperial_Aramaic", "Inherited", "Inscriptional_Pahlavi",
        "Inscriptional_Parthian", "Javanese", "Kaithi", "Kannada", "Katakana", "Kawi",
        "Kayah_Li", "Kharoshthi", "Khitan_Small_Script", "Khmer", "Khojki",
        "Khudawadi", "Lao", "Latin", "Lepcha", "Limbu", "Linear_A", "Linear_B",
        "Lisu", "Lycian", "Lydian", "Mahajani", "Makasar", "Malayalam", "Mandaic",
        "Manichaean", "Marchen", "Masaram_Gondi", "Medefaidrin", "Meetei_Mayek",
        "Mende_Kikakui", "Meroitic_Cursive", "Meroitic_Hieroglyphs", "Miao", "Modi",
        "Mongolian", "Mro", "Multani", "Myanmar", "Nabataean", "Nag_Mundari",
        "Nandinagari", "New_Tai_Lue", "Newa", "Nko", "Nushu",
        "Nyiakeng_Puachue_Hmong", "Ogham", "Ol_Chiki", "Old_Hungarian", "Old_Italic",
        "Old_North_Arabian", "Old_Permic", "Old_Persian", "Old_Sogdian",
        "Old_South_Arabian", "Old_Turkic", "Old_Uyghur", "Oriya", "Osage", "Osmanya",
        "Pahawh_Hmong", "Palmyrene", "Pau_Cin_Hau", "Phags_Pa", "Phoenician",
        "Psalter_Pahlavi", "Rejang", "Runic", "Samaritan", "Saurashtra", "Sharada",
        "Shavian", "Siddham", "SignWriting", "Sinhala", "Sogdian", "Sora_Sompeng",
        "Soyombo", "Sundanese", "Syloti_Nagri", "Syriac", "Tagalog", "Tagbanwa",
        "Tai_Le", "Tai_Tham", "Tai_Viet", "Takri", "Tamil", "Tangsa", "Tangut",
        "Telugu", "Thaana", "Thai", "Tibetan", "Tifinagh", "Tirhuta", "Toto",
        "Ugaritic", "Vai", "Vithkuqi", "Wancho", "Warang_Citi", "Yezidi", "Yi",
        "Zanabazar_Square",
    }.ToFrozenSet(StringComparer.Ordinal);
}
