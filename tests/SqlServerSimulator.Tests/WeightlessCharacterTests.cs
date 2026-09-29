using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which characters a collation gives no weight, per collation version —
/// probed 2026-09-29 against SQL Server 2025 by testing
/// <c>N'a' + NCHAR(n) + N'b' = N'ab'</c> for every BMP code unit.
/// </summary>
[TestClass]
public sealed class WeightlessCharacterTests
{
    [TestMethod]
    [DataRow("SQL_Latin1_General_CP1_CI_AS", 0x0378, 1)]
    [DataRow("Latin1_General_100_CI_AS", 0x0378, 1)]
    [DataRow("Japanese_XJIS_140_CI_AS", 0x0378, 1)]
    [DataRow("Latin1_General_CI_AS", 0x04D8, 1)]
    [DataRow("Japanese_90_CI_AS", 0x04D8, 0)]
    [DataRow("Japanese_90_CI_AS", 0x01F6, 1)]
    [DataRow("Latin1_General_100_CI_AS", 0x01F6, 0)]
    [DataRow("Latin1_General_100_CI_AS", 0x0370, 1)]
    [DataRow("Japanese_XJIS_140_CI_AS", 0x0370, 0)]
    [DataRow("Latin1_General_CI_AS", 0x02B9, 0)]
    [DataRow("Latin1_General_CI_AI", 0x02B9, 1)]
    [DataRow("Latin1_General_CI_AS", 0xFFFF, 1)]
    [DataRow("Latin1_General_BIN2", 0x0378, 0)]
    // CompareInfo ignores these where real weighs them.
    [DataRow("Latin1_General_CI_AS", 0x0001, 0)]
    [DataRow("Latin1_General_CI_AS", 0x00AD, 0)]
    [DataRow("Latin1_General_100_CI_AS", 0x00AD, 1)]
    [DataRow("Latin1_General_CI_AS", 0x200B, 0)]
    [DataRow("Japanese_XJIS_140_CI_AS", 0xFEFF, 1)]
    [DataRow("Latin1_General_100_CI_AS", 0xFEFF, 0)]
    public void Equality_PerVersion(string collation, int codeUnit, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select iif(N'a' + nchar({codeUnit}) + N'b' = N'ab' collate {collation}, 1, 0)"));

    [TestMethod]
    [DataRow("Latin1_General_CI_AS", 55357, 56832, 1)]
    [DataRow("SQL_Latin1_General_CP1_CI_AS", 55357, 56832, 1)]
    [DataRow("Latin1_General_100_CI_AS", 55357, 56832, 0)]
    [DataRow("Latin1_General_100_CI_AS_SC", 55424, 56320, 0)]
    public void SurrogatePair_WeighsFromVersion90(string collation, int high, int low, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select iif(N'a' + nchar({high}) + nchar({low}) + N'b' = N'ab' collate {collation}, 1, 0)"));

    [TestMethod]
    [DataRow(55424, 1)]
    [DataRow(56191, 1)]
    [DataRow(55357, 0)]
    [DataRow(56320, 0)]
    public void LoneSurrogate_Version100IgnoresOnlyUnassignedPlaneHighHalves(int codeUnit, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select iif(N'a' + nchar({codeUnit}) + N'b' = N'ab' collate Latin1_General_100_CI_AS, 1, 0)"));

    [TestMethod]
    public void Grouping_FoldsWeightlessSpellings()
        => AreEqual(1, new Simulation().ExecuteScalar("""
            select count(distinct v) from (values (N'ab'), (N'a' + nchar(888) + N'b'), (N'a' + nchar(55357) + N'b')) t(v)
            """));

    [TestMethod]
    public void Ordering_MinimalWeightCharacterSortsAfterItsAbsence()
        => AreEqual("a|a\u0001|ab|a\u0001b|", new Simulation().ExecuteScalar("""
            select string_agg(v, '|') within group (order by v collate Latin1_General_CI_AS) + '|'
            from (values (N'ab'), (N'a' + nchar(1)), (N'a'), (N'a' + nchar(1) + N'b')) t(v)
            """));

    [TestMethod]
    [DataRow("charindex(nchar(888), N'a' + nchar(888) + N'b')", 0)]
    [DataRow("charindex(N'b', N'a' + nchar(888) + N'b')", 3)]
    [DataRow("charindex(nchar(55357), N'a' + nchar(55357) + N'b')", 0)]
    [DataRow("charindex(nchar(55357), N'a' + nchar(55357) + N'b' collate Latin1_General_100_CI_AS)", 2)]
    [DataRow("len(replace(N'a' + nchar(888) + N'b', N'ab', N'X'))", 1)]
    public void Search_WeightlessNeedleIsNotFound(string expression, int expected)
        => AreEqual(expected, Convert.ToInt32(new Simulation().ExecuteScalar($"select {expression}"), System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    [DataRow("N'a' + nchar(888) + N'b' like N'ab'", 1)]
    [DataRow("N'a' + nchar(888) + N'b' like N'a_b'", 0)]
    [DataRow("N'a' + nchar(888) + N'b' like N'a__b'", 0)]
    [DataRow("N'a' + nchar(8205) + N'b' like N'a_b'", 0)]
    [DataRow("N'a' + nchar(0) + N'b' like N'a_b'", 0)]
    [DataRow("N'a' + nchar(1) + N'b' like N'a_b'", 1)]
    [DataRow("nchar(888) + N'b' like N'b'", 1)]
    [DataRow("nchar(888) + N'b' like N'_b'", 0)]
    [DataRow("N'a' + nchar(888) like N'a'", 1)]
    [DataRow("nchar(55357) + nchar(56832) collate Latin1_General_CI_AS like nchar(55357) + nchar(56832)", 1)]
    [DataRow("N'x' + nchar(55357) + nchar(56832) collate Latin1_General_CI_AS like N'x'", 1)]
    public void Like_WeightlessCharacterRidesWithItsNeighbor(string condition, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select iif({condition}, 1, 0)"));
}
