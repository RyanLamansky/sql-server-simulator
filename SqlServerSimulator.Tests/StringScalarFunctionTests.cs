using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for the string-manipulation scalar functions added in
/// the EF.Functions bundle: <c>STUFF</c>, <c>QUOTENAME</c>, <c>REPLICATE</c>,
/// <c>SPACE</c>. PATINDEX has its own file because the centralized pattern-
/// compiler refactor warrants concentrated coverage; FORMAT lives separately
/// to keep its culture / format-token cases together.
/// </summary>
[TestClass]
public sealed class StringScalarFunctionTests
{
    // ===== STUFF =====
    [TestMethod]
    public void Stuff_Basic_ReplacesMiddleRange()
        => AreEqual("aXYZef", ExecuteScalar("select STUFF('abcdef', 2, 3, 'XYZ')"));

    [TestMethod]
    public void Stuff_LengthZero_PureInsert()
        => AreEqual("aXYZbcdef", ExecuteScalar("select STUFF('abcdef', 2, 0, 'XYZ')"));

    [TestMethod]
    public void Stuff_NullReplacement_PureDelete()
        => AreEqual("aef", ExecuteScalar("select STUFF('abcdef', 2, 3, NULL)"));

    [TestMethod]
    public void Stuff_NullInput_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF(NULL, 2, 3, 'XYZ')"));

    [TestMethod]
    public void Stuff_StartZero_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF('abcdef', 0, 3, 'XYZ')"));

    [TestMethod]
    public void Stuff_StartNegative_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF('abcdef', -1, 3, 'XYZ')"));

    [TestMethod]
    public void Stuff_StartPastLength_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF('abcdef', 10, 3, 'XYZ')"));

    [TestMethod]
    public void Stuff_StartOnePastLength_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF('abcdef', 7, 0, 'XYZ')"));

    [TestMethod]
    public void Stuff_NegativeLength_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select STUFF('abcdef', 2, -1, 'XYZ')"));

    [TestMethod]
    public void Stuff_LengthBeyondRemainder_ClampsToEnd()
        => AreEqual("aXYZ", ExecuteScalar("select STUFF('abcdef', 2, 100, 'XYZ')"));

    [TestMethod]
    public void Stuff_NvarcharInput_PromotesResultToNvarchar()
        => AreEqual("aXYZef", ExecuteScalar("select STUFF(N'abcdef', 2, 3, 'XYZ')"));

    // ===== QUOTENAME =====
    [TestMethod]
    public void QuoteName_Default_BracketsAndDoublesClosing()
        => AreEqual("[a]]b]", ExecuteScalar("select QUOTENAME('a]b')"));

    [TestMethod]
    public void QuoteName_QuoteDelim_DoublesEmbeddedQuote()
        => AreEqual("'a''b'", ExecuteScalar("select QUOTENAME('a''b', '''')"));

    [TestMethod]
    public void QuoteName_DoubleQuoteDelim_DoublesEmbedded()
        => AreEqual("\"a\"\"b\"", ExecuteScalar("select QUOTENAME('a\"b', '\"')"));

    /// <summary>
    /// Probed: the closing character is doubled inside the body, irrespective
    /// of which paired character the caller supplied as the delimiter argument.
    /// </summary>
    [TestMethod]
    public void QuoteName_ParenDelim_DoublesClosingParen()
        => AreEqual("(a))b)", ExecuteScalar("select QUOTENAME('a)b', '(')"));

    [TestMethod]
    public void QuoteName_AngleDelim_DoublesClosingAngle()
        => AreEqual("<a>>b>", ExecuteScalar("select QUOTENAME('a>b', '<')"));

    [TestMethod]
    public void QuoteName_NullInput_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select QUOTENAME(NULL)"));

    [TestMethod]
    public void QuoteName_NullDelim_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select QUOTENAME('abc', NULL)"));

    [TestMethod]
    public void QuoteName_UnsupportedDelim_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select QUOTENAME('abc', '!')"));

    [TestMethod]
    public void QuoteName_MultiCharDelim_PicksFirstChar()
        => AreEqual("<abc>", ExecuteScalar("select QUOTENAME('abc', '<<')"));

    [TestMethod]
    public void QuoteName_Over128Chars_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select QUOTENAME(replicate('a', 129))"));

    [TestMethod]
    public void QuoteName_Exactly128Chars_Wraps()
        => AreEqual("[" + new string('a', 128) + "]", ExecuteScalar("select QUOTENAME(replicate('a', 128))"));

    // ===== REPLICATE =====
    [TestMethod]
    public void Replicate_Basic_RepeatsString()
        => AreEqual("ababab", ExecuteScalar("select REPLICATE('ab', 3)"));

    [TestMethod]
    public void Replicate_ZeroCount_ReturnsEmpty()
        => AreEqual(string.Empty, ExecuteScalar("select REPLICATE('ab', 0)"));

    [TestMethod]
    public void Replicate_NullString_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select REPLICATE(NULL, 3)"));

    [TestMethod]
    public void Replicate_NullCount_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select REPLICATE('ab', NULL)"));

    [TestMethod]
    public void Replicate_NegativeCount_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select REPLICATE('ab', -1)"));

    [TestMethod]
    public void Replicate_NonMaxVarchar_TruncatesAt8000Bytes()
        => AreEqual(8000, ExecuteScalar<int>("select datalength(REPLICATE('a', 10000))"));

    /// <summary>
    /// Probe-confirmed: <c>REPLICATE(varchar(MAX), N)</c> produces a result whose
    /// length isn't capped at 8000. The DATALENGTH probe value is asserted via
    /// the simulator's <c>int</c>-typed DATALENGTH return — real SQL Server
    /// returns <c>bigint</c> for MAX inputs, a pre-existing simulator
    /// divergence orthogonal to REPLICATE itself.
    /// </summary>
    [TestMethod]
    public void Replicate_VarcharMax_NoTruncation()
        => AreEqual(10000, ExecuteScalar<int>("select datalength(REPLICATE(cast('a' as varchar(max)), 10000))"));

    [TestMethod]
    public void Replicate_NvarcharMax_NoTruncation_DoubleBytes()
        => AreEqual(20000, ExecuteScalar<int>("select datalength(REPLICATE(cast('a' as nvarchar(max)), 10000))"));

    // ===== SPACE =====
    [TestMethod]
    public void Space_Basic_ReturnsRequestedSpaces()
        => AreEqual("   ", ExecuteScalar("select SPACE(3)"));

    [TestMethod]
    public void Space_Zero_ReturnsEmpty()
        => AreEqual(string.Empty, ExecuteScalar("select SPACE(0)"));

    [TestMethod]
    public void Space_Null_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select SPACE(NULL)"));

    [TestMethod]
    public void Space_Negative_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select SPACE(-1)"));

    [TestMethod]
    public void Space_HugeCount_TruncatesAt8000()
        => AreEqual(8000, ExecuteScalar<int>("select datalength(SPACE(10000))"));
}
