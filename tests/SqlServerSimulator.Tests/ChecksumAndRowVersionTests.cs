using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Tests for <c>CHECKSUM</c>, <c>BINARY_CHECKSUM</c>, and
/// <c>MIN_ACTIVE_ROWVERSION</c>. Every expected hash was read from SQL Server
/// 2025 on 2026-09-25.
/// </summary>
[TestClass]
public sealed class ChecksumAndRowVersionTests
{
    [TestMethod]
    public void Checksum_SameInput_SameOutput()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        AreEqual(a, b);
    }

    [TestMethod]
    public void Checksum_DifferentInput_DifferentOutput()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('bar')")!;
        AreNotEqual(a, b);
    }

    [TestMethod]
    public void Checksum_MultipleArgs_Works()
    {
        var result = new Simulation().ExecuteScalar("select checksum(1, 'foo', cast('2024-01-15' as date))");
        IsTrue(result is int);
    }

    [TestMethod]
    public void Checksum_CaseInsensitive_SameAsLower()
    {
        var a = (int)new Simulation().ExecuteScalar("select checksum('FOO')")!;
        var b = (int)new Simulation().ExecuteScalar("select checksum('foo')")!;
        AreEqual(a, b);
    }

    [TestMethod]
    public void BinaryChecksum_CaseSensitive_DiffersFromCaseChange()
    {
        var a = (int)new Simulation().ExecuteScalar("select binary_checksum('FOO')")!;
        var b = (int)new Simulation().ExecuteScalar("select binary_checksum('foo')")!;
        AreNotEqual(a, b);
    }

    [TestMethod]
    public void MinActiveRowVersion_Returns8Bytes()
    {
        var result = new Simulation().ExecuteScalar("select min_active_rowversion()");
        IsTrue(result is byte[] { Length: 8 });
    }

    [TestMethod]
    [DataRow("'a'", 97)]
    [DataRow("'abcdefghijklmnopqrstuvwxyz'", -878079993)]
    [DataRow("'é'", -23)]
    [DataRow("N'€'", 8364)]
    [DataRow("'a '", 97)]
    [DataRow("'a', 'b'", 1650)]
    [DataRow("1, cast('ab' as varchar(5))", 1634)]
    [DataRow("cast(-1 as smallint)", 65535)]
    [DataRow("cast(-2 as bigint)", 1)]
    [DataRow("1.5e0", 1073217536)]
    [DataRow("cast(1.5 as real)", 1069547520)]
    [DataRow("cast(-1 as money)", 9999)]
    [DataRow("cast(-1 as smallmoney)", -10000)]
    [DataRow("cast('2020-01-01 10:00' as datetime)", 10772661)]
    [DataRow("cast('2020-01-01 10:00' as smalldatetime)", -1422589352)]
    [DataRow("cast('2020-01-01 10:00' as datetime2(0))", -777208986)]
    [DataRow("cast('10:00' as time)", -777252781)]
    [DataRow("cast('2020-01-01 10:00 +01:00' as datetimeoffset)", 1877410686)]
    [DataRow("cast('0001-01-01' as date)", -693595)]
    [DataRow("cast('6F9619FF-8B86-D011-B42D-00C04FC964FF' as uniqueidentifier)", -1916545669)]
    [DataRow("cast(0x0100 as varbinary(10))", 1)]
    [DataRow("cast(null as int), 1", -10)]
    [DataRow("cast('ab' as sql_variant)", 1649)]
    [DataRow("cast(1.5e0 as sql_variant)", 1073217541)]
    [DataRow("1, cast('a' as text)", 1)]
    public void BinaryChecksum_MatchesRealsFold(string arguments, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select binary_checksum({arguments})"));

    [TestMethod]
    [DataRow("'a'", 142)]
    [DataRow("'A'", 142)]
    [DataRow("'ab'", 2159)]
    [DataRow("'é'", 914)]
    [DataRow("'€'", 66)]
    [DataRow("'abcdefgh'", 1721285341)]
    [DataRow("' x'", 677)]
    [DataRow("'  x'", 8869)]
    [DataRow("1, 2", 18)]
    [DataRow("cast('2020-01-01' as date)", 43829)]
    [DataRow("'ß'", 7072)]
    [DataRow("'aß'", 38304)]
    [DataRow("'ßa'", 113294)]
    [DataRow("'Straße'", -1434035566)]
    [DataRow("'ßß'", 1817504)]
    [DataRow("'a' + char(198)", 38770)]
    [DataRow("'a' + char(230)", 38770)]
    [DataRow("cast(1 as decimal(38, 10))", -1374215283)]
    [DataRow("cast(-1000 as numeric(5, 0))", -1374215283)]
    [DataRow("cast(0.1 as decimal(38, 37))", -1374215283)]
    [DataRow("cast(0.25 as decimal(10, 2))", 442323529)]
    [DataRow("cast(0.025 as decimal(10, 3))", 1803929767)]
    [DataRow("cast(123.456 as decimal(10, 3))", 543253508)]
    [DataRow("cast(0.0000000001 as decimal(38, 10))", -1038096291)]
    [DataRow("cast(4294967296 as decimal(38, 0))", -1038096291)]
    [DataRow("cast(-0.0000562843675942956559203341541209548 as decimal(37, 37))", 886111688)]
    [DataRow("cast(0.00000000000000000926400000000000000 as decimal(38, 35))", 1713322816)]
    [DataRow("cast(855635929800982819073476366777 as decimal(31, 0))", -1377870596)]
    [DataRow("cast(0 as decimal(5, 0))", 0)]
    [DataRow("cast(7 as sql_variant)", 380181642)]
    [DataRow("cast(cast(12.3456 as money) as sql_variant)", 543253504)]
    [DataRow("cast(cast(-0.5 as smallmoney) as sql_variant)", 884647302)]
    public void Checksum_MatchesRealsFold(string arguments, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select checksum({arguments})"));

    /// <summary>
    /// <c>varchar</c> data under the SQL collations over code pages 850 and 437
    /// folds each byte's weight under the name's sort order — æ, Æ and ß taking
    /// two places where it expands them, a binary name's bytes as signed bytes —
    /// strings probed against SQL Server 2025 (2026-10-17), written as their
    /// code points.
    /// </summary>
    [TestMethod]
    [DataRow("SQL_1xCompat_CP850_CI_AS", "10,23,112,108", 45644)]
    [DataRow("SQL_AltDiction_CP850_CI_AI", "97,39,250,65,167,89,248,94,122,32", -610079097)]
    [DataRow("SQL_AltDiction_CP850_CI_AS", "221,201,213,8215,52", 26917591)]
    [DataRow("SQL_AltDiction_CP850_CS_AS", "1,117,9552,18,9492", 5985916)]
    [DataRow("SQL_AltDiction_Pref_CP850_CI_AS", "223,230,32,32,65,230,223,32,223,230,223,230,230", 127611075)]
    [DataRow("SQL_Latin1_General_CP437_BIN", "9557,9618,82,966,71,77,1,9571,9516,214,948,9575", 1028754502)]
    [DataRow("SQL_Latin1_General_CP437_BIN2", "70,105,178,73,189,79,107,8993,14,80,9488,198", -163495170)]
    [DataRow("SQL_Latin1_General_CP437_CI_AI", "51,9554,122,47,230,56,11,103,177,224,41,99,92,32", -926841741)]
    [DataRow("SQL_Latin1_General_CP437_CI_AS", "35,53,94,225,9554,122,41,35,4,8801", 970305901)]
    [DataRow("SQL_Latin1_General_CP437_CS_AS", "44,98,8319,960,243,60,8805,118,33", 1746910500)]
    [DataRow("SQL_Latin1_General_CP850_BIN", "6,225,9604,105,224,161,172,180", 1029633856)]
    [DataRow("SQL_Latin1_General_CP850_BIN2", "77,28,2,12,49,203,253", 1287844819)]
    [DataRow("SQL_Latin1_General_CP850_CI_AI", "115,18,4,187,100,214,255", -1642039019)]
    [DataRow("SQL_Latin1_General_CP850_CI_AS", "206,76,86,9580,43,67,126,28,82,117", 1245392003)]
    [DataRow("SQL_Latin1_General_CP850_CS_AS", "233,188,6,58", 7953232)]
    [DataRow("SQL_Latin1_General_Pref_CP437_CI_AS", "65,223,65,32", 38158)]
    [DataRow("SQL_Latin1_General_Pref_CP850_CI_AS", "9472,247,12,113,14,402,62,64,58,12", 1571255157)]
    [DataRow("SQL_Scandinavian_CP850_CI_AS", "198,97,198,65,223,198,230,198,223,223,32,230,198", 1017937921)]
    [DataRow("SQL_Scandinavian_CP850_CS_AS", "305,65,62,15,11,28,198,9580,237,253,202,193,104,222", 990968401)]
    [DataRow("SQL_Scandinavian_Pref_CP850_CI_AS", "173,91,81,2,86,242,124", 1894685438)]
    public void Checksum_OfVarcharUnderACodePage850Or437SortOrder_MatchesReal(string collation, string codePoints, int expected)
    {
        var text = string.Join(" + ", codePoints.Split(',').Select(static point => $"nchar({point})"));
        AreEqual(expected, new Simulation().ExecuteScalar($"select checksum(cast({text} collate {collation} as varchar(20)))"));
    }

    [TestMethod]
    public void ChecksumStar_HashesEveryColumn()
        => AreEqual("181|104", new Simulation().ExecuteScalar("""
            create table t (a int, b varchar(10));
            insert t values (1, 'x');
            select concat(checksum(*), '|', binary_checksum(*)) from t
            """));

    [TestMethod]
    public void ChecksumStar_WithoutAFromClause_RaisesMsg263()
        => new Simulation().AssertSqlError("select checksum(*)", 263, "Must specify table to select from.");

    [TestMethod]
    [DataRow("checksum(cast('<a/>' as xml))", "xml", 1)]
    [DataRow("checksum(1, cast('a' as text))", "text", 2)]
    [DataRow("checksum(geography::Point(1, 2, 4326))", "sys.geography", 1)]
    public void Checksum_OfAnUnhashableType_RaisesMsg8116(string call, string type, int index)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {index} of checksum function.");

    [TestMethod]
    public void BinaryChecksum_OfOnlyUnhashableTypes_RaisesMsg8184()
        => new Simulation().AssertSqlError("create table t (x xml); select binary_checksum(x) from t", 8184, "Error in binarychecksum. There are no comparable columns in the binarychecksum input.");

    /// <summary>
    /// A string a Latin1-General table compares folds its characters' primary
    /// sort weights, so case and accents fold away whatever the name's
    /// sensitivity, a hyphen weighs nothing and trailing spaces drop, under the
    /// default collation's <c>nvarchar</c> and a Windows name's data of either
    /// width; a binary collation folds the characters (probed 2026-10-06 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("N'a'", 114)]
    [DataRow("N'A'", 114)]
    [DataRow("N'ab'", 151912974)]
    [DataRow("N'aB' collate Latin1_General_CS_AS", 151912974)]
    [DataRow("'ab' collate Latin1_General_CI_AS", 151912974)]
    [DataRow("N'a-b '", 151912974)]
    [DataRow("N'é'", 81)]
    [DataRow("N'abc'", 1132495864)]
    [DataRow("N'aaaaaaaa'", -1912959494)]
    [DataRow("N'x y'", -2115402921)]
    [DataRow("N'a  b'", 422974071)]
    [DataRow("N'a' + nchar(160)", 67568142)]
    [DataRow("N'æ'", 554566158)]
    [DataRow("N'ab' collate Latin1_General_BIN2", 1650)]
    public void Checksum_NationalString_FoldsPrimaryWeights(string operand, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select checksum({operand})"));
}
