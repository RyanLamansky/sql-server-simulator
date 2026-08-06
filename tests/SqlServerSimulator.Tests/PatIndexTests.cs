using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for <c>PATINDEX(pattern, subject)</c>. Shares the
/// LIKE-pattern wildcard semantics via the centralized
/// <c>LikeMatcher</c>, with anchoring decided by leading / trailing
/// <c>%</c>. Probe-confirmed against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class PatIndexTests
{
    [TestMethod]
    public void FindsMiddleSubstring_ReturnsOneBasedPosition()
        => AreEqual(2, ExecuteScalar<int>("select PATINDEX('%abc%', 'xabcx')"));

    [TestMethod]
    public void NoLeadingOrTrailingPercent_AnchorsBothEnds_NotEqual_ReturnsZero()
        => AreEqual(0, ExecuteScalar<int>("select PATINDEX('abc', 'xabcx')"));

    [TestMethod]
    public void NoLeadingOrTrailingPercent_AnchorsBothEnds_NotEqualLongerSubject_ReturnsZero()
        => AreEqual(0, ExecuteScalar<int>("select PATINDEX('abc', 'abcx')"));

    [TestMethod]
    public void TrailingPercent_AnchorsAtStartOnly_PrefixMatch_ReturnsOne()
        => AreEqual(1, ExecuteScalar<int>("select PATINDEX('abc%', 'abcx')"));

    [TestMethod]
    public void LeadingPercent_AnchorsAtEndOnly_ReturnsMatchedSubstringStart()
        => AreEqual(2, ExecuteScalar<int>("select PATINDEX('%abc', 'xabc')"));

    [TestMethod]
    public void CharacterClass_DigitWildcard()
        => AreEqual(5, ExecuteScalar<int>("select PATINDEX('%[0-9]%', 'abc 4 xyz')"));

    [TestMethod]
    public void UnderscoreMatchesSingleChar()
        => AreEqual(1, ExecuteScalar<int>("select PATINDEX('a_c', 'abc')"));

    [TestMethod]
    public void EmptyPattern_DoesNotMatchNonEmptySubject_ReturnsZero()
        => AreEqual(0, ExecuteScalar<int>("select PATINDEX('', 'abc')"));

    [TestMethod]
    public void EmptyPatternAndEmptySubject_ReturnsOne()
        => AreEqual(1, ExecuteScalar<int>("select PATINDEX('', '')"));

    [TestMethod]
    public void SinglePercentPattern_AnyNonEmptySubject_ReturnsOne()
        => AreEqual(1, ExecuteScalar<int>("select PATINDEX('%', 'abc')"));

    [TestMethod]
    public void NoMatch_ReturnsZero()
        => AreEqual(0, ExecuteScalar<int>("select PATINDEX('%xyz%', 'abc')"));

    [TestMethod]
    public void CaseInsensitiveDefault()
        => AreEqual(2, ExecuteScalar<int>("select PATINDEX('%ABC%', 'xabcx')"));

    [TestMethod]
    public void NullPattern_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select PATINDEX(NULL, 'abc')"));

    /// <summary>
    /// Probe-confirmed: the rejection is about the subject's <em>type</em>,
    /// not its value. A bare <c>NULL</c> literal has no type for the binder to
    /// match, so it raises Msg 8116; a subject that carries a string type and
    /// happens to hold NULL propagates NULL like any other string scalar.
    /// </summary>
    [TestMethod]
    public void UntypedNullSubject_RaisesMsg8116()
        => AreEqual(8116, ConvertToInt(AssertSqlError("select PATINDEX('%a%', NULL)", 8116)));

    [TestMethod]
    public void TypedNullSubject_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar("select PATINDEX('%a%', cast(NULL as varchar(10)))"));

    [TestMethod]
    public void NullValuedVariableSubject_ReturnsNull()
        => IsInstanceOfType<DBNull>(ExecuteScalar(
            "declare @v varchar(1000) = NULL; select PATINDEX('%a%', @v)"));

    [TestMethod]
    public void IntegerSubject_RaisesMsg8116()
        => AssertSqlError("select PATINDEX('%abc%', 123)", 8116);

    [TestMethod]
    public void IntegerPattern_CoercedToString_ReturnsMatchAgainstStringForm()
        => AreEqual(0, ExecuteScalar<int>("select PATINDEX(123, 'abc')"));

    [TestMethod]
    public void MaxSubject_ResultIsBigint()
        => AreEqual(1L, ExecuteScalar<long>("select PATINDEX('%a%', cast('abc' as varchar(max)))"));

    [TestMethod]
    public void NVarcharPattern_AndNVarcharSubject_WorksAcrossUnicode()
        => AreEqual(2, ExecuteScalar<int>("select PATINDEX(N'%ñ%', N'xñy')"));

    private static int ConvertToInt(DbException ex) => int.Parse(ex.Data["HelpLink.EvtID"]!.ToString()!);
}
