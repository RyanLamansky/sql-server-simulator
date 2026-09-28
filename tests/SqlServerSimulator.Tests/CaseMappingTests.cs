using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>UPPER</c> / <c>LOWER</c> map by the argument's collation's own table:
/// one for the unversioned, <c>_90</c> and <c>SQL_</c> collations, one for
/// <c>_100</c> and one for <c>_140</c>, with the Turkish I pair under Turkish
/// and Azeri <c>_100</c> collations (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class CaseMappingTests
{
    [TestMethod]
    [DataRow("select unicode(upper(N'µ'))", 0xB5)]
    [DataRow("select unicode(upper(N'µ' collate Latin1_General_100_CI_AS))", 0xB5)]
    [DataRow("select unicode(upper(N'µ' collate Japanese_XJIS_140_CI_AS))", 0x39C)]
    [DataRow("select unicode(upper(nchar(384)))", 384)]
    [DataRow("select unicode(upper(nchar(384) collate Latin1_General_100_CI_AS))", 0x243)]
    [DataRow("select unicode(lower(nchar(4256)))", 0x10D0)]
    [DataRow("select unicode(lower(nchar(4256) collate Latin1_General_100_BIN2))", 0x2D00)]
    [DataRow("select unicode(upper(nchar(912)))", 0x3AA)]
    [DataRow("select unicode(upper(nchar(912) collate Japanese_XJIS_140_CI_AS))", 912)]
    [DataRow("select unicode(lower(nchar(978) collate Japanese_XJIS_140_CI_AS))", 0x3C5)]
    [DataRow("select unicode(upper(N'ı'))", 'I')]
    [DataRow("select unicode(lower(N'İ'))", 'i')]
    [DataRow("select unicode(upper(N'ß'))", 0xDF)]
    public void CaseTable_FollowsCollationVersion(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    [DataRow("Turkish_CI_AS")]
    [DataRow("Turkish_100_CS_AS")]
    [DataRow("Turkish_BIN2")]
    [DataRow("SQL_Latin1_General_CP1254_CI_AS")]
    [DataRow("Azeri_Latin_100_CI_AS")]
    [DataRow("Azeri_Cyrillic_100_CI_AS")]
    public void TurkishCollations_MapTheDottedAndDotlessI(string collation)
        => AreEqual("İI|ıi", new Simulation().ExecuteScalar(
            $"select upper(N'iı' collate {collation}) + N'|' + lower(N'Iİ' collate {collation})"));

    [TestMethod]
    [DataRow("Latin1_General_100_CI_AS")]
    [DataRow("Lithuanian_100_CI_AS")]
    public void OtherCollations_MapTheIsTheEnglishWay(string collation)
        => AreEqual("II|ii", new Simulation().ExecuteScalar(
            $"select upper(N'iı' collate {collation}) + N'|' + lower(N'Iİ' collate {collation})"));

    [TestMethod]
    public void SupplementaryCharacters_NeverMap()
        => AreEqual(0x10428, new Simulation().ExecuteScalar(
            "select unicode(upper(nchar(55297) + nchar(56360) collate Latin1_General_100_CI_AS_SC))"));

    /// <summary>
    /// A <c>COLLATE</c> moving a <c>varchar</c> to another code page converts
    /// it there and then, so <c>À</c> reaching code page 437 is already
    /// <c>A</c> when <c>LOWER</c> reads it.
    /// </summary>
    [TestMethod]
    public void CollateToAnotherCodePage_ConvertsBeforeCaseMapping()
        => AreEqual("0x41|0x61|0x43", new Simulation().ExecuteScalar("""
            select convert(varchar(10), cast(cast(char(192) as varchar(1)) collate SQL_Latin1_General_CP437_CI_AS as varbinary(4)), 1)
                + '|' + convert(varchar(10), cast(lower(cast(char(192) as varchar(1)) collate SQL_Latin1_General_CP437_CI_AS) as varbinary(4)), 1)
                + '|' + convert(varchar(10), cast(upper(cast(char(162) as varchar(1)) collate Polish_CI_AS) as varbinary(4)), 1)
            """));
}
