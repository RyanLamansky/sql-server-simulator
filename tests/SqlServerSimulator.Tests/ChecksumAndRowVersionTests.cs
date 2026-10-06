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
    public void Checksum_MatchesRealsFold(string arguments, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select checksum({arguments})"));

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
