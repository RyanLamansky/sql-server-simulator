namespace SqlServerSimulator;

/// <summary>
/// <strong>Msg 102</strong> names the offending token, and the name it uses is
/// not always the way the token was spelled. A character literal is reported by
/// its body — delimiters, <c>N</c> prefix and escape doubling all gone — a
/// delimited identifier by its undelimited body, and a binary literal by its
/// parsed bytes re-rendered as lowercase hex. Everything else is reported as
/// written. All wordings probe-confirmed against SQL Server 2025 (2026-07-31),
/// each in the trailing-token position the matrix shares.
/// </summary>
[TestClass]
public sealed class SyntaxErrorTokenRenderingTests
{
    /// <summary>
    /// A value token following an already-aliased select-list element is one
    /// too many, so <c>select 1 x</c> parses and Msg 102 lands on whatever
    /// follows — the shortest position that reaches every spelling in the
    /// matrix.
    /// </summary>
    private static void AssertNear(string trailingToken, string expectedName)
        => new Simulation().AssertSqlError(
            $"select 1 x {trailingToken}",
            102,
            $"Incorrect syntax near '{expectedName}'.");

    [TestMethod]
    public void CharacterLiteral_IsNamedWithoutItsQuotes()
        => AssertNear("'b'", "b");

    /// <summary>The <c>N</c> prefix is not part of the name either.</summary>
    [TestMethod]
    public void NPrefixedLiteral_IsNamedWithoutPrefixOrQuotes()
        => AssertNear("N'nvarchar-lit'", "nvarchar-lit");

    /// <summary>
    /// The doubling that escapes an embedded quote collapses: real reports the
    /// one character it stands for.
    /// </summary>
    [TestMethod]
    public void CharacterLiteral_CollapsesDoubledQuotes()
    {
        AssertNear("'it''s'", "it's");
        AssertNear("'has''quote'", "has'quote");
    }

    /// <summary>An empty literal leaves the slot empty rather than showing bare quotes.</summary>
    [TestMethod]
    public void EmptyLiteral_LeavesTheSlotEmpty()
    {
        AssertNear("''", "");
        AssertNear("N''", "");
    }

    /// <summary>
    /// A character body is named as it was written, not as the value the
    /// collation stores. Under the default CP1252 server collation these
    /// characters store as <c>??</c>, yet the message still shows them — so the
    /// rendering reads the source, not the coerced <c>varchar</c>.
    /// </summary>
    [TestMethod]
    public void CharacterLiteral_KeepsCharactersTheCodePageCannotStore()
        => AssertNear("'日本'", "日本");

    /// <summary>Tabs and other in-body whitespace survive verbatim.</summary>
    [TestMethod]
    public void CharacterLiteral_KeepsEmbeddedWhitespace()
        => AssertNear("'tab\tinside'", "tab\tinside");

    /// <summary>
    /// A binary literal is the one spelling real re-renders from its value
    /// rather than echoing: the hex lowercases, and an odd digit count regains
    /// the leading zero that made it a whole byte.
    /// </summary>
    [TestMethod]
    public void BinaryLiteral_IsRenderedFromItsParsedBytes()
    {
        AssertNear("0x0102", "0x0102");
        AssertNear("0xABCDef", "0xabcdef");
        AssertNear("0xABC", "0x0abc");
        AssertNear("0x", "0x");
    }

    /// <summary>
    /// Numeric and currency literals keep their source text — a currency
    /// literal's leading zeros survive, so the name is the spelling and not the
    /// <c>money</c> value it denotes.
    /// </summary>
    [TestMethod]
    public void NumericAndCurrencyLiterals_KeepTheirSourceText()
    {
        AssertNear("12345", "12345");
        AssertNear("1.25", "1.25");
        AssertNear("$5", "$5");
        AssertNear("$00005", "$00005");
    }

    /// <summary>
    /// A delimited identifier is named by its body, with the doubling that
    /// escaped a closing bracket collapsed the same way a literal's is.
    /// </summary>
    [TestMethod]
    public void DelimitedIdentifier_IsNamedWithoutItsBrackets()
    {
        AssertNear("[y]", "y");
        AssertNear("[a]]b]", "a]b");
    }

    /// <summary>
    /// A double-quoted token is named by its body under either
    /// <c>QUOTED_IDENTIFIER</c> setting, though only one of the two settings
    /// makes it an identifier — the other makes it a character literal, and
    /// both render the same way.
    /// </summary>
    [TestMethod]
    public void DoubleQuotedToken_IsNamedWithoutItsQuotesUnderEitherSetting()
    {
        new Simulation().AssertSqlError(
            "set quoted_identifier on; select 1 x \"y\"", 102, "Incorrect syntax near 'y'.");
        new Simulation().AssertSqlError(
            "set quoted_identifier off; select 1 x \"y\"", 102, "Incorrect syntax near 'y'.");
        new Simulation().AssertSqlError(
            "set quoted_identifier off; select 1 x \"it\"\"s\"", 102, "Incorrect syntax near 'it\"s'.");
    }

    /// <summary>A variable is named as written, sigil included.</summary>
    [TestMethod]
    public void Variable_IsNamedWithItsSigil()
        => AssertNear("@somevar", "@somevar");

    /// <summary>
    /// Real clips a character literal's body to 129 UTF-16 code units. The
    /// clip counts the body <em>after</em> escapes collapse, so 200 doubled
    /// quotes report 129 apostrophes rather than 129 source characters' worth.
    /// </summary>
    [TestMethod]
    public void LongCharacterLiteral_ClipsTo129Characters()
    {
        AssertNear($"'{new string('z', 129)}'", new string('z', 129));
        AssertNear($"'{new string('z', 200)}'", new string('z', 129));
        AssertNear($"'{new string('é', 200)}'", new string('é', 129));
        AssertNear($"'{string.Concat(Enumerable.Repeat("''", 200))}'", new string('\'', 129));
    }

    /// <summary>
    /// The clip counts code units, not text elements: an astral-plane
    /// character costs two, and the 129th unit is the leading half of a
    /// surrogate pair whose trailing half is dropped.
    /// </summary>
    [TestMethod]
    public void LongCharacterLiteral_ClipsMidSurrogatePair()
    {
        var body = string.Concat(Enumerable.Repeat("\U0001F600", 200));
        AssertNear($"N'{body}'", body[..129]);
    }

    /// <summary>
    /// A source-spelled token clips one character earlier, at 128 — the length
    /// an identifier is also capped to. Real reaches this clip through a
    /// 200-digit numeric literal, which the simulator's <c>decimal</c> backing
    /// can't represent; an over-long variable name is the same token shape from
    /// the clip's point of view. Real precedes its Msg 102 with a Msg 103 for
    /// the over-long name, which the simulator doesn't raise for a
    /// non-identifier token — a separate gap that leaves this wording intact.
    /// </summary>
    [TestMethod]
    public void LongSourceSpelledToken_ClipsTo128Characters()
        => AssertNear($"@{new string('v', 200)}", $"@{new string('v', 127)}");

    /// <summary>
    /// A binary literal clips at 258 bytes — twice the character body's 129,
    /// consistent with one shared buffer that a character body fills two bytes
    /// per code unit. The rendered name is the <c>0x</c> prefix plus two hex
    /// digits per surviving byte.
    /// </summary>
    [TestMethod]
    public void LongBinaryLiteral_ClipsTo258Bytes()
    {
        AssertNear($"0x{string.Concat(Enumerable.Repeat("ab", 258))}", $"0x{string.Concat(Enumerable.Repeat("ab", 258))}");
        AssertNear($"0x{string.Concat(Enumerable.Repeat("ab", 300))}", $"0x{string.Concat(Enumerable.Repeat("ab", 258))}");
    }

    /// <summary>
    /// The rule belongs to the token, not to one clause's parser: a second,
    /// unseparated GROUP BY element renders the same way. A
    /// <c>FOR SYSTEM_TIME CONTAINED IN</c> argument list written without its
    /// parentheses is the third position covered, in
    /// <see cref="TemporalTableTests"/>.
    /// </summary>
    [TestMethod]
    public void GroupByTrailingLiteral_IsNamedWithoutItsQuotes()
        => new Simulation().AssertSqlError(
            "create table t (c int); select 1 from t group by c 'b'",
            102,
            "Incorrect syntax near 'b'.");
}
