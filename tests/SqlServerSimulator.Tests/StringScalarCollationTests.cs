using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The character-matching string scalars — <c>CHARINDEX</c>, <c>REPLACE</c>,
/// <c>TRANSLATE</c>, <c>STRING_SPLIT</c> and the <c>TRIM</c> family — searching
/// under the collation their arguments resolve to, the way <c>=</c> and
/// <c>LIKE</c> already do. Every expected value is a row of the probe matrix
/// run against SQL Server 2025 (2026-08-05), cited by its <c>R&lt;n&gt;.&lt;nn&gt;</c>
/// number; the model is documented on <c>Collation.IndexOf</c> and in the
/// string-scalar section of <c>docs/claude/collations.md</c>.
/// </summary>
[TestClass]
public sealed class StringScalarCollationTests
{
    // ---- CHARINDEX -------------------------------------------------------

    /// <summary>
    /// The accent half folds for the search exactly as it does for <c>=</c>:
    /// under <c>_AI</c> a bare <c>e</c> finds a composed <c>é</c> and a
    /// decomposed <c>e</c> + U+0301 alike, and under <c>_AS</c> it finds
    /// neither — not even the decomposed sequence's base letter, because that
    /// letter is not a character on its own there (R1.01-R1.08).
    /// </summary>
    [TestMethod]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", 4)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("charindex(nchar(233), cast(N'cafe' as nvarchar(30)) collate Latin1_General_CI_AI)", 4)]
    [DataRow("charindex(N'e', cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI)", 4)]
    [DataRow("charindex(N'e', cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("charindex(N'X', cast(N'caf' + nchar(233) + N'X' as nvarchar(30)) collate Latin1_General_CI_AI)", 5)]
    [DataRow("charindex(N'X', cast(N'cafe' + nchar(769) + N'X' as nvarchar(30)) collate Latin1_General_CI_AI)", 6)]
    [DataRow("charindex(nchar(233), cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS)", 4)]
    [DataRow("charindex(N'cafe', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", 1)]
    [DataRow("charindex(N'fe', cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("charindex(N'fe', cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI)", 3)]
    public void CharIndex_FoldsAccentsWhenTheCollationDoes(string expression, int expected)
        => AreEqual(expected, Scalar(expression));

    /// <summary>
    /// The case, width and kanatype halves fold on their own terms. The
    /// <c>_CS_AI</c> row is the one that needs care: <c>CompareInfo</c>'s search
    /// APIs drop the case level once <c>IgnoreNonSpace</c> is set, so the hit is
    /// re-read through <c>Compare</c> — real holds <c>N'E'</c> and <c>N'é'</c>
    /// apart there (R1.13-R1.20, R10.17, R10.18).
    /// </summary>
    [TestMethod]
    [DataRow("charindex(N'E', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", 4)]
    [DataRow("charindex(N'E', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CS_AI)", 0)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CS_AI)", 4)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_BIN2)", 0)]
    [DataRow("charindex(nchar(65313), cast(N'xax' as nvarchar(30)) collate Latin1_General_CI_AS)", 2)]
    [DataRow("charindex(nchar(65313), cast(N'xax' as nvarchar(30)) collate Latin1_General_CI_AS_KS_WS)", 0)]
    [DataRow("charindex(nchar(12354), cast(nchar(12450) + N'x' as nvarchar(30)) collate Japanese_CI_AS)", 1)]
    [DataRow("charindex(nchar(12354), cast(nchar(12450) + N'x' as nvarchar(30)) collate Japanese_CI_AS_KS)", 0)]
    [DataRow("charindex(N'e' collate Latin1_General_CI_AI, N'caf' + nchar(233))", 4)]
    [DataRow("charindex('e', cast('caf' + char(233) as varchar(30)) collate Latin1_General_CI_AI)", 4)]
    [DataRow("charindex('e', cast('caf' + char(233) as varchar(30)) collate SQL_Latin1_General_CP1_CI_AI)", 4)]
    public void CharIndex_FoldsTheCaseWidthAndKanaHalves(string expression, int expected)
        => AreEqual(expected, Scalar(expression));

    /// <summary>
    /// A needle the collation gives no weight is <em>not</em> found: an empty
    /// string, and a bare combining mark under an accent-insensitive collation,
    /// each answer 0 rather than matching everywhere at zero length
    /// (R1.24, R10.01, R10.02, R10.09, R10.10). The <c>start</c> argument still
    /// clamps at 1 and still counts in the result's own units
    /// (R1.21, R10.11, R10.14, R10.15).
    /// </summary>
    [TestMethod]
    [DataRow("charindex(N'', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", 0)]
    [DataRow("charindex(N'', cast(N'' as nvarchar(30)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("charindex(nchar(769), cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AI)", 0)]
    [DataRow("charindex(nchar(769), cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AS)", 0)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) + N'e' as nvarchar(30)) collate Latin1_General_CI_AI, 5)", 5)]
    [DataRow("charindex(N'e', cast(N'cafe' + nchar(769) + N'e' as nvarchar(30)) collate Latin1_General_CI_AI, 5)", 6)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, 0)", 4)]
    [DataRow("charindex(N'e', cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, -3)", 4)]
    [DataRow("charindex(N'e ', cast(N'caf' + nchar(233) + N' z' as nvarchar(30)) collate Latin1_General_CI_AI)", 4)]
    public void CharIndex_TreatsAWeightlessNeedleAsAbsent(string expression, int expected)
        => AreEqual(expected, Scalar(expression));

    // ---- REPLACE ---------------------------------------------------------

    /// <summary>
    /// <c>REPLACE</c> removes <em>what the subject gave up</em>, not the
    /// pattern's own length: an accent-insensitive <c>e</c> matching a
    /// decomposed <c>e</c> + U+0301 takes the mark with it, so the result is
    /// four characters and not five (R3.01-R3.03, R3.10, R3.12, R3.18,
    /// R10.12, R10.13).
    /// </summary>
    [TestMethod]
    [DataRow("replace(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'e', N'X')", "cafX")]
    [DataRow("replace(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AS, N'e', N'X')", "café")]
    [DataRow("replace(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI, N'e', N'X')", "cafX")]
    [DataRow("replace(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI, N'cafe', N'Q')", "Q")]
    [DataRow("replace(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS, N'cafe', N'Q')", "café")]
    [DataRow("replace(cast(N'e' + nchar(233) + N'e' as nvarchar(30)) collate Latin1_General_CI_AI, N'ee', N'X')", "Xe")]
    [DataRow("replace(cast(N'caf' + nchar(233) + N'e' as nvarchar(30)) collate Latin1_General_CI_AI, N'e', N'YZ')", "cafYZYZ")]
    [DataRow("replace(cast(N'a' + nchar(233) + N'b' as nvarchar(30)) collate Latin1_General_CI_AI, N'aeb', N'Z')", "Z")]
    [DataRow("replace(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_BIN2, N'e', N'X')", "café")]
    [DataRow("replace(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'', N'X')", "café")]
    [DataRow("replace(cast(N'xax' as nvarchar(30)) collate Latin1_General_CI_AS, nchar(65345), N'Q')", "xQx")]
    [DataRow("replace(cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AI, nchar(769), N'Q')", "abc")]
    [DataRow("replace(cast('caf' + char(233) as varchar(30)) collate Latin1_General_CI_AI, 'e', 'X')", "cafX")]
    public void Replace_ConsumesTheMatchNotThePattern(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    /// <summary>
    /// An explicit <c>COLLATE</c> on <em>any</em> of the three arguments
    /// decides the whole call — including the replacement, which is never
    /// compared (R3.13, R3.14).
    /// </summary>
    [TestMethod]
    [DataRow("replace(N'caf' + nchar(233), N'e' collate Latin1_General_CI_AI, N'X')", "cafX")]
    [DataRow("replace(N'caf' + nchar(233), N'e', N'X' collate Latin1_General_CI_AI)", "cafX")]
    [DataRow("replace(N'caf' + nchar(233), N'e' collate Latin1_General_CI_AS, N'X')", "café")]
    public void Replace_TakesAnExplicitCollateFromAnyArgument(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    // ---- TRANSLATE -------------------------------------------------------

    /// <summary>
    /// <c>TRANSLATE</c> looks each input character up in the character list
    /// under the collation, and substitutes by the <em>position</em> the lookup
    /// reports. The input is walked one code unit at a time, so a combining
    /// mark is its own character: a decomposed <c>café</c> keeps its mark and
    /// only the base letter is substituted, under both <c>_AI</c> and
    /// <c>_AS</c> (R4.01-R4.09, R6.01-R6.03).
    /// </summary>
    [TestMethod]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'e', N'Z')", "cafZ")]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AS, N'e', N'Z')", "café")]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'E', N'Z')", "cafZ")]
    [DataRow("translate(cast(N'cafe' as nvarchar(30)) collate Latin1_General_CI_AS, N'E', N'Z')", "cafZ")]
    [DataRow("translate(cast(N'cafe' as nvarchar(30)) collate Latin1_General_CS_AS, N'E', N'Z')", "cafe")]
    [DataRow("translate(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI, N'e', N'Z')", "cafŹ")]
    [DataRow("translate(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS, N'e', N'Z')", "cafŹ")]
    [DataRow("translate(cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI, nchar(769), N'Z')", "cafeZ")]
    [DataRow("translate(cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AI, nchar(769), N'Q')", "abc")]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'e' + nchar(233), N'12')", "caf1")]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, nchar(233) + N'e', N'12')", "caf1")]
    [DataRow("translate(cast(N'xax' as nvarchar(30)) collate Latin1_General_CI_AS, nchar(65345), N'Q')", "xQx")]
    [DataRow("translate(cast(nchar(12450) + N'x' as nvarchar(30)) collate Japanese_CI_AS, nchar(12354), N'Q')", "Qx")]
    [DataRow("translate(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_BIN2, N'e', N'Z')", "café")]
    [DataRow("translate(N'caf' + nchar(233), N'e' collate Latin1_General_CI_AI, N'Z')", "cafZ")]
    [DataRow("translate(N'caf' + nchar(233), N'e', N'Z' collate Latin1_General_CI_AI)", "cafZ")]
    public void Translate_LooksEachCharacterUpUnderTheCollation(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    // ---- STRING_SPLIT ----------------------------------------------------

    /// <summary>
    /// The separator is matched under the collation, so an accent-insensitive
    /// split on <c>N'e'</c> also splits at an <c>é</c> and a width-insensitive
    /// split on <c>N' '</c> also splits at an ideographic space. What the split
    /// <em>consumes</em> is one separator character, whatever the match ate:
    /// a decomposed <c>café</c> split on <c>N'e'</c> leaves the mark at the head
    /// of the next segment (R5.01-R5.10, R6.09, R11.07, R12.08-R12.10).
    /// </summary>
    [TestMethod]
    [DataRow("cast(N'caf' + nchar(233) + N'Xcafe' as nvarchar(30)) collate Latin1_General_CI_AI", "N'e'", "[caf],[Xcaf],[]")]
    [DataRow("cast(N'caf' + nchar(233) + N'Xcafe' as nvarchar(30)) collate Latin1_General_CI_AS", "N'e'", "[caféXcaf],[]")]
    [DataRow("cast(N'cafe' + nchar(769) + N'X' as nvarchar(30)) collate Latin1_General_CI_AI", "N'e'", "[caf],[́X]")]
    [DataRow("cast(N'cafe' + nchar(769) + N'X' as nvarchar(30)) collate Latin1_General_CI_AS", "N'e'", "[caféX]")]
    [DataRow("cast(N'aebec' as nvarchar(30)) collate Latin1_General_CI_AS", "N'E'", "[a],[b],[c]")]
    [DataRow("cast(N'aebec' as nvarchar(30)) collate Latin1_General_CS_AS", "N'E'", "[aebec]")]
    [DataRow("cast(N'cafeX' as nvarchar(30)) collate Latin1_General_CI_AI", "nchar(233)", "[caf],[X]")]
    [DataRow("cast(N'caf' + nchar(233) + N'Xcafe' as nvarchar(30)) collate Latin1_General_BIN2", "N'e'", "[caféXcaf],[]")]
    [DataRow("N'caf' + nchar(233) + N'Xcafe'", "N'e' collate Latin1_General_CI_AI", "[caf],[Xcaf],[]")]
    [DataRow("cast(N'a' + nchar(12288) + N'b' as nvarchar(30)) collate Latin1_General_CI_AS", "N' '", "[a],[b]")]
    [DataRow("cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AI", "nchar(769)", "[abc]")]
    public void StringSplit_MatchesTheSeparatorUnderTheCollation(string input, string separator, string expected)
        => AreEqual(
            expected,
            ScalarString($"(select string_agg(N'[' + value + N']', N',') from string_split({input}, {separator}))"));

    // ---- TRIM / LTRIM / RTRIM --------------------------------------------

    /// <summary>
    /// The explicit character set is matched under the collation, in every one
    /// of the family's forms (R5.20-R5.31, R6.10, R11.02, R11.05, R11.13).
    /// </summary>
    [TestMethod]
    [DataRow("trim(N'e' from cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", "caf")]
    [DataRow("trim(N'e' from cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AS)", "café")]
    [DataRow("trim(N'ee' from cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", "caf")]
    [DataRow("trim(N'e' from cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI)", "café")]
    [DataRow("trim(nchar(769) from cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AS)", "cafe")]
    [DataRow("trim(nchar(769) from cast(N'cafe' + nchar(769) as nvarchar(30)) collate Latin1_General_CI_AI)", "cafe")]
    [DataRow("trim(nchar(769) from cast(N'abc' as nvarchar(30)) collate Latin1_General_CI_AI)", "abc")]
    [DataRow("trim(N'E' from cast(N'cafe' as nvarchar(30)) collate Latin1_General_CI_AS)", "caf")]
    [DataRow("trim(N'E' from cast(N'cafe' as nvarchar(30)) collate Latin1_General_CS_AS)", "cafe")]
    [DataRow("trim(nchar(65345) from cast(N'axa' as nvarchar(30)) collate Latin1_General_CI_AS)", "x")]
    [DataRow("trim(nchar(12354) from cast(nchar(12450) + N'x' as nvarchar(30)) collate Japanese_CI_AS)", "x")]
    [DataRow("trim(N'e' from cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_BIN2)", "café")]
    [DataRow("trim(N'e' collate Latin1_General_CI_AI from N'caf' + nchar(233))", "caf")]
    [DataRow("ltrim(cast(nchar(233) + N'caf' as nvarchar(30)) collate Latin1_General_CI_AI, N'e')", "caf")]
    [DataRow("rtrim(cast(N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI, N'e')", "caf")]
    [DataRow("trim(leading N'e' from cast(nchar(233) + N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", "café")]
    [DataRow("trim(trailing N'e' from cast(nchar(233) + N'caf' + nchar(233) as nvarchar(30)) collate Latin1_General_CI_AI)", "écaf")]
    [DataRow("ltrim(cast(nchar(12288) + N'x' as nvarchar(30)) collate Latin1_General_CI_AS, N' ')", "x")]
    public void Trim_MatchesTheCharacterSetUnderTheCollation(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    /// <summary>
    /// The legacy one-argument forms are <strong>not</strong> collation-driven:
    /// real strips U+0020 and nothing else there, so an ideographic space
    /// survives a bare <c>TRIM</c> even under the width-insensitive collation
    /// whose two-argument <c>N' '</c> set removes it — the one place the two
    /// forms disagree (R11.01, R11.02, R11.03, R11.06).
    /// </summary>
    [TestMethod]
    [DataRow("ltrim(cast(nchar(12288) + N'x' as nvarchar(30)) collate Latin1_General_CI_AS)", "　x")]
    [DataRow("rtrim(cast(N'x' + nchar(12288) as nvarchar(30)) collate Latin1_General_CI_AS)", "x　")]
    [DataRow("trim(cast(nchar(12288) + N'x' + nchar(12288) as nvarchar(30)) collate Latin1_General_CI_AS)", "　x　")]
    [DataRow("ltrim(cast(N'  x' as nvarchar(30)) collate Latin1_General_CI_AS)", "x")]
    [DataRow("trim(cast(N'  x  ' as nvarchar(30)) collate Latin1_General_CI_AS)", "x")]
    public void Trim_OneArgumentFormStripsOnlyTheAsciiSpace(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    /// <summary>
    /// The existing set / NULL / empty rules are untouched by the collation
    /// routing: the characters are a set rather than a substring, a NULL set
    /// makes the whole call NULL, and an empty set removes nothing.
    /// </summary>
    [TestMethod]
    [DataRow("trim(N'ab' from N'abxba')", "x")]
    [DataRow("trim(N'' from N'  x  ')", "  x  ")]
    [DataRow("ltrim(N'aax', N'')", "aax")]
    [DataRow("rtrim(N'xaa', N'a')", "x")]
    public void Trim_KeepsItsSetSemantics(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    /// <summary>
    /// The all-printable-ASCII path <c>Collation.ElementMatcher</c> hoists — the
    /// one every ordinary <c>TRANSLATE</c> / <c>TRIM</c> call takes — has to
    /// give the linguistic answer, case pairs included, and has to stay off it
    /// for a case-sensitive collation and for a non-ASCII character (R14).
    /// </summary>
    [TestMethod]
    [DataRow("translate(N'Hello World' collate Latin1_General_CI_AS, N'lo', N'01')", "He001 W1r0d")]
    [DataRow("translate(N'Hello World' collate Latin1_General_CI_AS, N'LO', N'01')", "He001 W1r0d")]
    [DataRow("translate(N'Hello World' collate Latin1_General_CS_AS, N'LO', N'01')", "Hello World")]
    [DataRow("translate(N'Hello World' collate Latin1_General_CS_AS, N'lo', N'01')", "He001 W1r0d")]
    [DataRow("trim(N'DLROhe' from N'Hello World' collate Latin1_General_CI_AS)", " W")]
    [DataRow("trim(N'DLROhe' from N'Hello World' collate Latin1_General_CS_AS)", "Hello World")]
    [DataRow("translate(N'a-b_c' collate Latin1_General_CI_AS, N'-_', N'..')", "a.b.c")]
    public void ElementLookup_TakesTheAsciiPathWithTheSameAnswer(string expression, string expected)
        => AreEqual(expected, ScalarString(expression));

    /// <summary>A NULL character set makes the call NULL, in every form.</summary>
    [TestMethod]
    [DataRow("trim(cast(null as nvarchar(10)) from N'abc')")]
    [DataRow("ltrim(N'abc', cast(null as nvarchar(10)))")]
    [DataRow("rtrim(N'abc', cast(null as nvarchar(10)))")]
    public void Trim_NullCharacterSetYieldsNull(string expression)
        => IsTrue(new Simulation().ExecuteScalar($"select {expression}") is null or DBNull);

    private static int Scalar(string expression) =>
        new Simulation().ExecuteScalar<int>($"select {expression}");

    private static string ScalarString(string expression) =>
        (string)new Simulation().ExecuteScalar($"select {expression}")!;
}
