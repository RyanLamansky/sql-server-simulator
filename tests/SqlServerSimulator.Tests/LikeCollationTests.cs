using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>LIKE</c> / <c>PATINDEX</c> matching under the resolved collation's full
/// comparison semantics, and the trailing-space rule the operand types decide.
/// Every expected value below is a row of the probe matrix run against SQL
/// Server 2025 (2026-08-05); the model it establishes is documented on
/// <c>LikeMatcher</c> and in the LIKE section of
/// <c>docs/claude/collations.md</c>.
/// <para>
/// Note the <c>cast(… as nvarchar(20)) collate …</c> shape used to build a
/// multi-character subject: it predates the parenthesized-group form
/// (<see cref="ParenthesizedCollateTests"/>) and is transparent to everything
/// these tests measure, so it stays as written.
/// </para>
/// </summary>
[TestClass]
public sealed class LikeCollationTests
{
    // ---- trailing spaces -------------------------------------------------

    /// <summary>
    /// The subject may carry trailing U+0020 the pattern didn't consume — but
    /// only when the comparison is non-Unicode. One <c>nvarchar</c> operand
    /// makes the whole pair Unicode and the slack disappears, which is real's
    /// documented rule and the direction that matters: the simulator used to
    /// return rows real excludes.
    /// </summary>
    [TestMethod]
    [DataRow("'x  ' like 'x'", 1)]                    // varchar subject: slack
    [DataRow("N'x  ' like N'x'", 0)]                  // nvarchar subject: none
    [DataRow("N'x  ' like 'x'", 0)]                   // one Unicode operand decides
    [DataRow("'x  ' like N'x'", 0)]                   // either side
    [DataRow("'x' like 'x  '", 0)]                    // pattern spaces are significant
    [DataRow("N'x' like N'x  '", 0)]
    [DataRow("N'x  ' like N'x '", 0)]                 // one of the two consumed, one left
    [DataRow("'x  ' like 'x  '", 1)]                  // exact
    [DataRow("'x  y  ' like 'x  y'", 1)]              // only the trailing run is slack
    [DataRow("'  x' like 'x'", 0)]                    // leading spaces are not
    [DataRow("'x' like 'x_'", 0)]                     // no phantom pad for `_`
    [DataRow("'x  ' like 'x_'", 1)]                   // one consumed, one slack
    [DataRow("N'x  ' like N'x__'", 1)]
    [DataRow("'   ' like ''", 1)]
    [DataRow("N'' like N' '", 0)]
    [DataRow("N' ' like N''", 0)]
    [DataRow("N'x  ' like N'x%'", 1)]
    [DataRow("N'x  ' like N'x%  '", 1)]
    [DataRow("'x  ' like 'x[ ]'", 1)]                 // a class consumes one, slack the other
    [DataRow("'x' + char(9) + ' ' like 'x' + char(9)", 1)]  // tab then a slack space
    [DataRow("'x ' + char(9) like 'x'", 0)]           // only U+0020 is slack
    public void TrailingSpaceSlack_IsTheNonUnicodeFamilys(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// The same rule through storage rather than literals: <c>char</c> /
    /// <c>nchar</c> pad to their declared width, and the pad is slack for the
    /// one and significant for the other — so a <c>char(10)</c> holding
    /// <c>'x'</c> matches <c>'x'</c> and an <c>nchar(10)</c> doesn't.
    /// </summary>
    [TestMethod]
    public void TrailingSpaceSlack_ThroughColumnStorage()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, c char(10), v varchar(10), nc nchar(10), nv nvarchar(10));
            insert t values (1, 'x', 'x', N'x', N'x'), (2, 'x  ', 'x  ', N'x  ', N'x  ')
            """);
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and c like 'x'"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and c like 'x%'"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and c like 'x         '"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and c like 'x_'"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and c like N'x'"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t where id = 1 and nc like N'x'"));
        AreEqual(1, sim.ExecuteScalar<int>("select count(*) from t where id = 2 and v like 'x'"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t where id = 2 and nv like N'x'"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t where id = 2 and v like N'x'"));
        AreEqual(0, sim.ExecuteScalar<int>("select count(*) from t where id = 2 and nv like 'x'"));
    }

    /// <summary>
    /// The register entry this closes: <c>N'x  ' LIKE N'x'</c> answered yes
    /// here and no on real, so a row real excludes came back — the dangerous
    /// direction.
    /// </summary>
    [TestMethod]
    public void RegisterEntry_NvarcharSubjectTrailingSpaces_NoLongerOverMatch()
        => AreEqual(0, Rows("N'x  ' like N'x'"));

    // ---- collation halves ------------------------------------------------

    /// <summary>
    /// A literal run compares under every half of the collation, not just its
    /// case half: <c>_AI</c> folds the accent, <c>_CS</c> keeps the case,
    /// <c>_KS</c> / <c>_WS</c> stop folding kana type and width.
    /// </summary>
    [TestMethod]
    [DataRow("N'café' collate Latin1_General_CI_AS like N'cafe'", 0)]
    [DataRow("N'café' collate Latin1_General_CI_AI like N'cafe'", 1)]
    [DataRow("N'CAFÉ' collate Latin1_General_CI_AI like N'cafe'", 1)]
    [DataRow("N'CAFÉ' collate Latin1_General_CS_AI like N'cafe'", 0)]
    [DataRow("N'café' collate Latin1_General_CS_AI like N'cafe'", 1)]
    [DataRow("N'CAFE' collate Latin1_General_CS_AS like N'cafe'", 0)]
    [DataRow("N'CAFE' collate Latin1_General_CI_AS like N'cafe'", 1)]
    [DataRow("N'CAFE' collate Latin1_General_BIN2 like N'cafe'", 0)]
    [DataRow("N'café' collate Latin1_General_BIN2 like N'cafe'", 0)]
    [DataRow("N'Xcafé' collate Latin1_General_CI_AI like N'%cafe%'", 1)]
    [DataRow("N'café Y' collate Latin1_General_CI_AI like N'cafe%'", 1)]
    [DataRow("N'Xcafé' collate Latin1_General_CI_AI like N'%cafe'", 1)]
    [DataRow("N'cafe' collate Latin1_General_CI_AI like N'café'", 1)]      // the pattern carries it
    [DataRow("N'café' collate SQL_Latin1_General_CP1_CI_AI like N'cafe'", 1)]
    [DataRow("N'café' collate SQL_Latin1_General_CP1_CI_AS like N'cafe'", 0)]
    [DataRow("cast('café' as varchar(10)) collate Latin1_General_CI_AI like 'cafe'", 1)]  // varchar side
    [DataRow("cast('café' as varchar(10)) collate Latin1_General_CI_AS like 'cafe'", 0)]
    [DataRow("cast('CAFE' as varchar(10)) collate Latin1_General_CS_AS like 'cafe'", 0)]
    public void LiteralRun_ReadsEveryCollationHalf(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// The width and kana halves, which <c>_WS</c> / <c>_KS</c> turn on.
    /// Fullwidth <c>Ａ</c> is <c>nchar(65313)</c>; the katakana / hiragana /
    /// halfwidth trio is <c>ア</c> / <c>あ</c> / <c>ｱ</c>.
    /// </summary>
    [TestMethod]
    [DataRow("nchar(65313) collate Latin1_General_CI_AS like N'a'", 1)]
    [DataRow("nchar(65313) collate Latin1_General_CI_AS_KS_WS like N'a'", 0)]
    [DataRow("N'ア' collate Japanese_CI_AS like N'あ'", 1)]
    [DataRow("N'ア' collate Japanese_CI_AS_KS_WS like N'あ'", 0)]
    [DataRow("N'ｱ' collate Japanese_CI_AS like N'ア'", 1)]
    [DataRow("N'ｱ' collate Japanese_CI_AS_KS_WS like N'ア'", 0)]
    [DataRow("cast(N'ｶ' + nchar(65438) as nvarchar(20)) collate Japanese_CI_AS like N'ガ'", 1)]
    [DataRow("N'I' collate Turkish_CI_AS like N'i'", 0)]                          // locale case rules
    [DataRow("N'İ' collate Turkish_CI_AS like N'i'", 1)]
    [DataRow("N'I' collate Latin1_General_CI_AS like N'i'", 1)]
    public void LiteralRun_ReadsTheWidthKanaAndLocaleHalves(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// The register entry this closes: an accent-insensitive column matched
    /// accent-sensitively, so rows real returns were dropped.
    /// </summary>
    [TestMethod]
    public void RegisterEntry_AccentInsensitiveColumn_MatchesAccentInsensitively()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, s nvarchar(40) collate Latin1_General_CI_AI);
            insert t values (1, N'ÜSB cable'), (2, N'usb cable'), (3, N'other')
            """);
        AreEqual(2, sim.ExecuteScalar<int>("select count(*) from t where s like N'USB%'"));
    }

    // ---- characters, not code units --------------------------------------

    /// <summary>
    /// <c>_</c> matches one character: a base plus its combining marks, a
    /// halfwidth kana plus its voiced sound mark, or a bare mark on its own.
    /// <c>nchar(769)</c> is COMBINING ACUTE ACCENT and <c>nchar(807)</c>
    /// COMBINING CEDILLA.
    /// </summary>
    [TestMethod]
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'caf_'", 1)]
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'caf__'", 0)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'_'", 1)]
    [DataRow("cast(N'e' + nchar(769) + nchar(807) as nvarchar(20)) collate Latin1_General_CI_AS like N'_'", 1)]
    [DataRow("nchar(769) collate Latin1_General_CI_AS like N'_'", 1)]
    [DataRow("cast(N'ｶ' + nchar(65438) as nvarchar(20)) collate Japanese_CI_AS like N'_'", 1)]
    [DataRow("cast(N'Xe' + nchar(769) + N'Y' as nvarchar(20)) collate Latin1_General_CI_AS like N'X_Y'", 1)]
    [DataRow("cast(N'Xe' + nchar(769) + N'Y' as nvarchar(20)) collate Latin1_General_CI_AS like N'X__Y'", 0)]
    [DataRow("cast(N'Xe' + nchar(769) + N'Y' as nvarchar(20)) collate Latin1_General_CI_AS like N'X%Y'", 1)]
    // A decomposed subject compares equal to its composed spelling, and the
    // accent-sensitive collation refuses to see the base letter alone.
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'café'", 1)]
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AI like N'cafe'", 1)]
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AI like N'cafe%'", 1)]
    [DataRow("cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AI like N'%cafe'", 1)]
    // A binary collation groups nothing: two code units, two `_`.
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_BIN2 like N'__'", 1)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_BIN2 like N'_'", 0)]
    // CR + LF are two characters, and a Devanagari cluster is base+virama then
    // the next consonant.
    [DataRow("cast(nchar(13) + nchar(10) as nvarchar(20)) collate Latin1_General_CI_AS like N'__'", 1)]
    [DataRow("cast(nchar(13) + nchar(10) as nvarchar(20)) collate Latin1_General_CI_AS like N'_'", 0)]
    [DataRow("cast(N'क' + nchar(2381) + N'ष' as nvarchar(20)) collate Latin1_General_CI_AS like N'__'", 1)]
    [DataRow("cast(N'क' + nchar(2381) + N'ष' as nvarchar(20)) collate Latin1_General_CI_AS like N'___'", 0)]
    public void SingleWildcard_MatchesOneCharacter(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// A soft hyphen (<c>nchar(173)</c>) is its own character, so a pattern
    /// that doesn't account for it doesn't match — even though the collation's
    /// comparer gives it no weight.
    /// </summary>
    [TestMethod]
    [DataRow("cast(N'x' + nchar(173) as nvarchar(20)) collate Latin1_General_CI_AS like N'x'", 0)]
    [DataRow("cast(N'x' + nchar(173) as nvarchar(20)) collate Latin1_General_CI_AS like N'x_'", 1)]
    [DataRow("cast(N'x' + nchar(173) as nvarchar(20)) collate SQL_Latin1_General_CP1_CI_AS like N'x'", 0)]
    [DataRow("cast(N'x' + nchar(173) as nvarchar(20)) collate SQL_Latin1_General_CP1_CI_AS like N'x%'", 1)]
    [DataRow("nchar(173) collate Latin1_General_CI_AS like N'_'", 1)]
    public void SingleWildcard_CountsAZeroWeightCharacter(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// A surrogate pair reads three different ways, and which one is the
    /// collation's vintage: an unversioned name matches it with no number of
    /// <c>_</c> at all, a versioned one with two, and a
    /// supplementary-character-aware one with a single <c>_</c>. A literal
    /// still matches the pair under every one of them, and so does <c>%</c>.
    /// </summary>
    [TestMethod]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'_'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'__'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate SQL_Latin1_General_CP1_CI_AS like N'__'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_100_CI_AS like N'__'", 1)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_100_CI_AS like N'_'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Japanese_90_CI_AS like N'__'", 1)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_BIN2 like N'__'", 1)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_100_CI_AS_SC like N'_'", 1)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_100_CI_AS_SC like N'__'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'%'", 1)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like nchar(55357) + nchar(56832)", 1)]
    [DataRow("cast(N'a' + nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'a_'", 0)]
    [DataRow("cast(N'a' + nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'a%'", 1)]
    [DataRow("nchar(55357) collate Latin1_General_CI_AS like N'_'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_CI_AS like N'[^a-z]'", 0)]
    [DataRow("cast(nchar(55357) + nchar(56832) as nvarchar(20)) collate Latin1_General_100_CI_AS_SC like N'[^a-z]'", 1)]
    public void SurrogatePair_ReadsPerCollationVintage(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    // ---- character classes -----------------------------------------------

    /// <summary>
    /// A range is an interval in the collation's own order, which interleaves
    /// the cases under a case-sensitive collation and puts an accented letter
    /// beside its base — so <c>[a-c]</c> holds <c>A</c> and <c>B</c> but not
    /// <c>C</c>, and holds <c>á</c> under an accent-sensitive collation too. A
    /// binary collation orders by code point instead.
    /// </summary>
    [TestMethod]
    [DataRow("N'A' collate Latin1_General_CS_AS like N'[a-c]'", 1)]
    [DataRow("N'B' collate Latin1_General_CS_AS like N'[a-c]'", 1)]
    [DataRow("N'C' collate Latin1_General_CS_AS like N'[a-c]'", 0)]
    [DataRow("N'a' collate Latin1_General_CS_AS like N'[A-C]'", 0)]
    [DataRow("N'b' collate Latin1_General_CS_AS like N'[A-C]'", 1)]
    [DataRow("N'B' collate Latin1_General_CS_AS like N'[b-B]'", 1)]
    [DataRow("N'A' collate Latin1_General_CS_AS like N'[b-B]'", 0)]
    [DataRow("N'á' collate Latin1_General_CI_AS like N'[a-c]'", 1)]
    [DataRow("N'á' collate Latin1_General_CI_AI like N'[a-c]'", 1)]
    [DataRow("N'á' collate Latin1_General_CI_AS like N'[^a-c]'", 0)]
    [DataRow("N'á' collate Latin1_General_CS_AS like N'[a-b]'", 1)]
    [DataRow("N'Á' collate Latin1_General_CS_AS like N'[a-b]'", 1)]
    [DataRow("N'á' collate Latin1_General_CI_AI like N'[a]'", 1)]
    [DataRow("N'á' collate Latin1_General_CI_AI like N'[^a]'", 0)]
    [DataRow("N'á' collate SQL_Latin1_General_CP1_CI_AS like N'[a-c]'", 1)]
    [DataRow("N'A' collate Latin1_General_BIN2 like N'[a-c]'", 0)]
    [DataRow("N'A' collate Latin1_General_BIN2 like N'[A-Z]'", 1)]
    [DataRow("N'a' collate Latin1_General_BIN2 like N'[A-Z]'", 0)]
    [DataRow("nchar(65313) collate Latin1_General_CI_AS like N'[a-c]'", 1)]  // fullwidth Ａ
    [DataRow("nchar(65301) collate Latin1_General_CI_AS like N'[0-9]'", 1)]  // fullwidth ５
    [DataRow("nchar(65301) collate Latin1_General_CI_AS_KS_WS like N'[0-9]'", 1)]  // still in range
    public void CharacterClass_RangeIsOrderedByTheCollation(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// A class member is a character, so a combining sequence written into one
    /// is a single member — and a reversed range matches nothing without
    /// taking the class's other members down with it.
    /// </summary>
    [TestMethod]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'[' + N'e' + nchar(769) + N']'", 1)]
    [DataRow("N'e' collate Latin1_General_CI_AS like N'[' + N'e' + nchar(769) + N']'", 0)]
    [DataRow("nchar(769) collate Latin1_General_CI_AS like N'[' + N'e' + nchar(769) + N']'", 0)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'[é]'", 1)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AI like N'[e]'", 1)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'[e]'", 0)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AI like N'[d-f]'", 1)]
    [DataRow("cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS like N'[d-f]'", 1)]
    [DataRow("N'1' collate Latin1_General_CI_AS like N'[c-a1]'", 1)]  // reversed range, live member
    [DataRow("N'1' collate Latin1_General_CI_AS like N'[1c-a]'", 1)]
    [DataRow("N'b' collate Latin1_General_CI_AS like N'[c-a]'", 0)]
    [DataRow("N'a' collate Latin1_General_CI_AS like N'[-a]'", 1)]
    [DataRow("N'-' collate Latin1_General_CI_AS like N'[-a]'", 1)]
    [DataRow("N'-' collate Latin1_General_CI_AS like N'[a-]'", 1)]
    public void CharacterClass_MembersAreCharacters(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    // ---- ESCAPE and wildcard identity ------------------------------------

    /// <summary>
    /// The escape character is matched by code point — its case isn't folded
    /// and an accent-insensitive collation doesn't fold it either — while what
    /// it escapes is still an ordinary literal that compares under the
    /// collation.
    /// </summary>
    [TestMethod]
    [DataRow("N'a%b' collate Latin1_General_CI_AS like N'aE%b' escape N'e'", 0)]      // case of the escape
    [DataRow("N'a%b' collate Latin1_General_CI_AS like N'ae%b' escape N'e'", 1)]
    [DataRow("N'aE%b' collate Latin1_General_CI_AS like N'aE%b' escape N'e'", 1)]
    [DataRow("N'a%b' collate Latin1_General_CI_AI like N'aé%b' escape N'e'", 0)]      // not accent-folded
    [DataRow("N'a%b' collate Latin1_General_CI_AI like N'aé%b' escape N'é'", 1)]
    [DataRow("N'100%' collate Latin1_General_CI_AS like N'100!%' escape N'!'", 1)]
    [DataRow("N'café%' collate Latin1_General_CI_AI like N'cafe!%' escape N'!'", 1)]  // escaped, still linguistic
    [DataRow("N'café' collate Latin1_General_CI_AI like N'caf!e' escape N'!'", 1)]
    [DataRow("N'A_B' collate Latin1_General_CS_AS like N'a!_b' escape N'!'", 0)]
    [DataRow("N'A_B' collate Latin1_General_CS_AS like N'A!_B' escape N'!'", 1)]
    public void EscapeCharacter_IsMatchedByCodePoint(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    /// <summary>
    /// A fullwidth <c>％</c> (<c>nchar(65285)</c>) is a literal, not a
    /// wildcard, even under a width-insensitive collation that folds it onto
    /// <c>%</c> for comparison.
    /// </summary>
    [TestMethod]
    [DataRow("N'aXb' collate Latin1_General_CI_AS like N'a' + nchar(65285) + N'b'", 0)]
    [DataRow("cast(N'a' + nchar(65285) + N'b' as nvarchar(20)) collate Latin1_General_CI_AS like N'a' + nchar(65285) + N'b'", 1)]
    [DataRow("N'aXb' collate Latin1_General_CI_AS like N'a' + nchar(65343) + N'b'", 0)]  // fullwidth low line
    public void Wildcards_AreMatchedByCodePoint(string condition, int expectedRows)
        => AreEqual(expectedRows, Rows(condition));

    // ---- PATINDEX --------------------------------------------------------

    /// <summary>
    /// <c>PATINDEX</c> reads the same collation and the same trailing-space
    /// rule, and reports the position in UTF-16 units — so a combining mark
    /// ahead of the match counts toward it, exactly as <c>CHARINDEX</c>'s
    /// answer does.
    /// </summary>
    [TestMethod]
    [DataRow("patindex(N'%cafe%', cast(N'Xcafé' as nvarchar(20)) collate Latin1_General_CI_AI)", 2)]
    [DataRow("patindex(N'%cafe%', cast(N'Xcafé' as nvarchar(20)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("patindex(N'%A%', N'xax' collate Latin1_General_CS_AS)", 0)]
    [DataRow("patindex(N'%A%', N'xax' collate Latin1_General_CI_AS)", 2)]
    [DataRow("patindex(N'%[a-c]%', N'xBz' collate Latin1_General_CS_AS)", 2)]
    [DataRow("patindex(N'_', cast(N'e' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS)", 1)]
    [DataRow("patindex(N'%caf_%', cast(N'cafe' + nchar(769) as nvarchar(20)) collate Latin1_General_CI_AS)", 1)]
    [DataRow("patindex(N'%e%', cast(N'Xe' + nchar(769) + N'Y' as nvarchar(20)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("patindex(N'%Y%', cast(N'Xe' + nchar(769) + N'Y' as nvarchar(20)) collate Latin1_General_CI_AS)", 4)]
    [DataRow("patindex('x', 'x  ')", 1)]      // the non-Unicode slack reaches here too
    [DataRow("patindex(N'x', N'x  ')", 0)]
    [DataRow("patindex('%x', 'x  ')", 1)]
    [DataRow("patindex(N'%x', N'x  ')", 0)]
    [DataRow("patindex('%x  ', 'x  ')", 1)]
    [DataRow("patindex('x%', 'x  ')", 1)]
    public void PatIndex_ReadsTheSameCollationAndSlack(string expression, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    private static int Rows(string condition) =>
        new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count();
}
