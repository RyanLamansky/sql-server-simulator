using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The RE2 pattern dialect SQL Server 2025's <c>REGEXP_*</c> members accept.
/// .NET's <c>Regex</c> takes a strict superset, so the boundary matters in both
/// directions: the constructs RE2 refuses have to raise real's error, and the
/// constructs both accept have to mean the same thing. Every case here was
/// probed against a live SQL Server 2025 (17.0.4065.4) reference instance.
/// </summary>
[TestClass]
public sealed class RegexpDialectTests
{
    /// <summary>
    /// Constructs .NET supports and RE2 doesn't. Accepting any of these would
    /// be the dangerous divergence direction — a pattern that works here and
    /// fails in production.
    /// </summary>
    [TestMethod]
    // Backreferences: RE2 has none, and its escape parser reports the digit.
    [DataRow(@"(ab)\1", @"invalid escape sequence: \1")]
    [DataRow(@"\1a", @"invalid escape sequence: \1")]
    [DataRow(@"\8", @"invalid escape sequence: \8")]
    // Lookaround, atomic groups, inline comments, free-spacing mode.
    [DataRow("a(?=b)", "invalid perl operator: (?=")]
    [DataRow("a(?!b)", "invalid perl operator: (?!")]
    [DataRow("(?<=a)b", "invalid perl operator: (?<")]
    [DataRow("(?<x>b)", "invalid perl operator: (?<")]
    [DataRow("(?>a+)a", "invalid perl operator: (?>")]
    [DataRow("a(?#comment)bc", "invalid perl operator: (?#")]
    [DataRow("(?x) a b c", "invalid perl operator: (?x")]
    [DataRow("(?P=x)", "invalid perl operator: (?P")]
    [DataRow("(?flags)", "invalid perl operator: (?f")]
    [DataRow("(?", "invalid perl operator: (?")]
    // Possessive and stacked quantifiers.
    [DataRow("a++", "bad repetition operator: ++")]
    [DataRow("a**", "bad repetition operator: **")]
    [DataRow("a*??", "bad repetition operator: *??")]
    [DataRow("a+*", "bad repetition operator: +*")]
    [DataRow("a?*", "bad repetition operator: ?*")]
    [DataRow("a{1,2}*", "bad repetition operator: {1,2}*")]
    [DataRow("a*{1,2}", "bad repetition operator: *{1,2}")]
    [DataRow("(a){1000}{1000}", "bad repetition operator: {1000}{1000}")]
    // A repetition with nothing to repeat.
    [DataRow("?a", "no argument for repetition operator: ?")]
    [DataRow("+a", "no argument for repetition operator: +")]
    [DataRow("|*", "no argument for repetition operator: *")]
    [DataRow("(*a)", "no argument for repetition operator: *")]
    [DataRow("(?i)*", "no argument for repetition operator: *")]
    [DataRow("{1,2}a", "no argument for repetition operator: {1,2}")]
    // RE2 caps a counted repetition at 1000 and requires min <= max.
    [DataRow("a{1001}", "invalid repetition size: {1001}")]
    [DataRow("a{1001,}", "invalid repetition size: {1001,}")]
    [DataRow("a{2,1}", "invalid repetition size: {2,1}")]
    // Escapes RE2 doesn't define.
    [DataRow(@"ab\Kc", @"invalid escape sequence: \K")]
    [DataRow(@"\Aabc\Z", @"invalid escape sequence: \Z")]
    [DataRow(@"\e", @"invalid escape sequence: \e")]
    [DataRow(@"\cA", @"invalid escape sequence: \c")]
    [DataRow(@"\N{LATIN SMALL LETTER A}", @"invalid escape sequence: \N")]
    [DataRow(@"\x{110000}", @"invalid escape sequence: \x{110000")]
    // Character-class rejections.
    [DataRow(@"[\b]", @"invalid escape sequence: \b")]
    [DataRow(@"[a-\d]", @"invalid escape sequence: \d")]
    [DataRow("[z-a]", "invalid character class range: z-a")]
    [DataRow("[[:foo:]]", "invalid character class range: [:foo:]")]
    [DataRow(@"\p{Foo}", @"invalid character class range: \p{Foo}")]
    // Named-group naming rules.
    [DataRow("(?P<>a)", "invalid named capture group: (?P<>")]
    [DataRow("(?P<a-b>a)", "invalid named capture group: (?P<a-b>")]
    public void RejectedPattern_Msg19300(string pattern, string detail) =>
        new Simulation().AssertSqlError(
            $"select regexp_count('abc', '{pattern.Replace("'", "''", StringComparison.Ordinal)}')",
            19300,
            $"An invalid Pattern '{pattern}' was provided. Error '{detail}' occurred during evaluation of the Pattern.");

    /// <summary>
    /// The four structural pattern failures real gives their own message
    /// numbers, rather than folding into Msg 19300.
    /// </summary>
    [TestMethod]
    [DataRow("(", 19308, "Missing ')' in the Pattern (.")]
    [DataRow("(?:", 19308, "Missing ')' in the Pattern (?:.")]
    [DataRow("[a", 19308, "Missing ']' in the Pattern [a.")]
    [DataRow("[]", 19308, "Missing ']' in the Pattern [].")]
    [DataRow("[^]", 19308, "Missing ']' in the Pattern [^].")]
    [DataRow(")", 19307, "Encountered an unexpected ')' in the Pattern ).")]
    [DataRow(@"\", 19309, @"Invalid trailing backslash (\) provided at the end of the Pattern \.")]
    public void StructuralPatternFailure(string pattern, int number, string message) =>
        new Simulation().AssertSqlError($"select regexp_count('abc', '{pattern}')", number, message);

    /// <summary>
    /// The pattern-error states real assigns are per member family, not per
    /// call: the scalars and the predicate report one set, the two rowset
    /// members another.
    /// </summary>
    [TestMethod]
    [DataRow("select regexp_count('a', '(?=a')", 19300, (byte)1)]
    [DataRow("select 1 where regexp_like('a', '(?=a')", 19300, (byte)1)]
    [DataRow("select * from regexp_matches('a', '(?=a')", 19300, (byte)2)]
    [DataRow("select * from regexp_split_to_table('a', '(?=a')", 19300, (byte)2)]
    [DataRow("select regexp_count('a', ')')", 19307, (byte)1)]
    [DataRow("select * from regexp_matches('a', ')')", 19307, (byte)2)]
    [DataRow("select regexp_count('a', '(')", 19308, (byte)1)]
    [DataRow("select regexp_count('a', '[a')", 19308, (byte)2)]
    [DataRow("select * from regexp_matches('a', '(')", 19308, (byte)3)]
    [DataRow("select * from regexp_matches('a', '[a')", 19308, (byte)4)]
    [DataRow(@"select regexp_count('a', '\')", 19309, (byte)1)]
    [DataRow(@"select * from regexp_matches('a', '\')", 19309, (byte)2)]
    public void PatternErrorState_SplitsByMemberFamily(string sql, int number, byte state) =>
        AreEqual(state, new Simulation().AssertSqlError(sql, number).State);

    /// <summary>
    /// Constructs both engines accept, with the values real produces.
    /// <c>\101</c> is RE2's C++ octal quirk: the same leading digit that makes
    /// a bare <c>\1</c> an unsupported backreference reads as octal when
    /// another octal digit follows.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_count('a1', '[[:digit:]]')", 1)]
    [DataRow(@"regexp_count('a1', '[\d]')", 1)]
    [DataRow(@"regexp_count('aA', '\p{L}')", 2)]
    [DataRow(@"regexp_count('aA', '\p{Lu}')", 1)]
    [DataRow(@"regexp_count('a', '\p{Any}')", 1)]
    [DataRow(@"regexp_count('a', '\pL')", 1)]
    [DataRow(@"regexp_count('a', '\PL')", 0)]
    [DataRow(@"regexp_count('a.b', '\Qa.b\E')", 1)]
    [DataRow(@"regexp_count('axb', '\Qa.b\E')", 0)]
    [DataRow("regexp_count('ABC', '(?i)abc')", 1)]
    [DataRow("regexp_count('abc', '(?i)ABC', 1, 'c')", 1)]
    [DataRow("regexp_count('ABC', '(?-i)abc', 1, 'i')", 0)]
    [DataRow("regexp_count('abc', '(?s:a.c)')", 1)]
    [DataRow("regexp_count('abc', '(?-i:ABC)')", 0)]
    [DataRow(@"regexp_count('abc', '\Aabc\z')", 1)]
    [DataRow(@"regexp_count('A', '\x41')", 1)]
    [DataRow(@"regexp_count('A', '\101')", 1)]
    [DataRow(@"regexp_count('a', '\12')", 0)]
    [DataRow(@"regexp_count('a', '\0')", 0)]
    [DataRow("regexp_count('abc', '(?P<x>b)')", 1)]
    [DataRow("regexp_count('abc', '(?P<1x>a)')", 1)]
    [DataRow("regexp_count('abc', '(?P<x>a)(?P<x>b)')", 1)]
    // RE2 reads a leading `]`, a trailing `-`, and an unterminated POSIX name
    // as literal class members.
    [DataRow("regexp_count('a', '[]a]')", 1)]
    [DataRow("regexp_count('a', '[^]a]')", 0)]
    [DataRow("regexp_count('a', '[-a]')", 1)]
    [DataRow("regexp_count('a', '[a-]')", 1)]
    [DataRow("regexp_count('a', '[a-b-c]')", 1)]
    [DataRow(@"regexp_count('a', '[\p{L}-z]')", 1)]
    [DataRow("regexp_count('a', '[[:alpha]]')", 0)]
    [DataRow("regexp_count('a', '[[:^alpha:]]')", 0)]
    [DataRow("regexp_count('a', 'a{')", 0)]
    [DataRow("regexp_count('a', 'a}')", 0)]
    [DataRow("regexp_count('a', 'a{,2}')", 0)]
    [DataRow("regexp_count('a', 'a{1000}')", 0)]
    public void AcceptedPattern_Matrix(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    /// <summary>
    /// RE2's shorthand classes and word boundary are ASCII-only where .NET's
    /// are Unicode-aware; the translation has to hold that line.
    /// </summary>
    [TestMethod]
    // Arabic-Indic digit, Latin small e with acute.
    [DataRow(@"regexp_count(nchar(1632), '\d')", 0)]
    [DataRow(@"regexp_count(nchar(233), '\w')", 0)]
    [DataRow(@"regexp_count(nchar(1632), '[[:digit:]]')", 0)]
    [DataRow(@"regexp_count(nchar(233) + 'x', nchar(233) + '\bx')", 1)]
    [DataRow(@"regexp_count('a' + nchar(233), 'a\b')", 1)]
    [DataRow(@"regexp_count('ab', 'a\B')", 1)]
    [DataRow("regexp_count('hello world', '\\bworld\\b')", 1)]
    [DataRow("regexp_count('helloworld', '\\bworld\\b')", 0)]
    // RE2's \s excludes vertical tab, which .NET's includes.
    [DataRow(@"regexp_count(char(11), '\s')", 0)]
    [DataRow(@"regexp_count(char(9) + char(10) + char(12) + char(13) + ' ', '\s')", 5)]
    [DataRow(@"regexp_count('_', '\w')", 1)]
    public void AsciiOnlyShorthandClasses(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    /// <summary>
    /// RE2's <c>$</c> anchors at end of text; .NET's also matches before a
    /// trailing newline, so the translation rewrites it. The <c>m</c> flag
    /// restores the end-of-line meaning.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_count('a' + char(10), 'a$', 1, '')", 0)]
    [DataRow("regexp_count('a' + char(10), 'a$', 1, 'm')", 1)]
    [DataRow("regexp_count('a' + char(10) + 'b', 'b$', 1, '')", 1)]
    [DataRow("regexp_count('a' + char(10) + 'b', 'a$', 1, 'm')", 1)]
    [DataRow("regexp_count('a' + char(10), '.', 1, 's')", 2)]
    public void EndAnchor_IsEndOfTextWithoutMultiline(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    /// <summary>
    /// Alternation is leftmost-first (Perl semantics), not POSIX
    /// leftmost-longest, and <c>(?U)</c> swaps every quantifier's greediness.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_substr('abc', 'a|ab')", "a")]
    [DataRow("regexp_substr('abc', 'ab|a')", "ab")]
    [DataRow("regexp_substr('aaa', 'a{2}|a{3}')", "aa")]
    [DataRow("regexp_substr('<a><b>', '<.+>')", "<a><b>")]
    [DataRow("regexp_substr('<a><b>', '<.+?>')", "<a>")]
    [DataRow("regexp_substr('<a><b>', '(?U)<.+>')", "<a>")]
    [DataRow("regexp_substr('aaa', '(?U)a+')", "a")]
    public void MatchPreference_IsLeftmostFirst(string expression, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString($"select {expression}"));

    /// <summary>
    /// RE2 Unicode script names have no .NET spelling. They aren't modeled yet
    /// and say so, rather than being silently mis-mapped to a Unicode block —
    /// distinct from a name RE2 itself rejects, which raises Msg 19300.
    /// </summary>
    [TestMethod]
    public void UnicodeScriptName_NotModeled() =>
        Assert.Contains(
            "script names",
            Throws<NotSupportedException>(() => new Simulation().ExecuteScalar(@"select regexp_count('a', '\p{Greek}')")).Message);
}
