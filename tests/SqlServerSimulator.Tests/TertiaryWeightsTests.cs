using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>TERTIARY_WEIGHTS</c> over the SQL collations whose names carry
/// <c>Pref</c>: 2 for a lowercase letter, 1 for anything else, a pair for a
/// character that sorts as two, and NULL under any other collation (probed
/// 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class TertiaryWeightsTests
{
    [TestMethod]
    [DataRow("'abc' collate SQL_Latin1_General_Pref_CP1_CI_AS", "020202")]
    [DataRow("'ABC' collate SQL_Latin1_General_Pref_CP1_CI_AS", "010101")]
    [DataRow("'aBc' collate SQL_Latin1_General_Pref_CP1_CI_AS", "020102")]
    [DataRow("'a b  ' collate SQL_Latin1_General_Pref_CP1_CI_AS", "0201020101")]
    [DataRow("cast('a' as char(4)) collate SQL_Latin1_General_Pref_CP1_CI_AS", "02010101")]
    [DataRow("(char(223) + char(230) + char(198) + char(255)) collate SQL_Latin1_General_Pref_CP1_CI_AS", "01010202010101")]
    [DataRow("(char(140) + char(156) + char(254)) collate SQL_Danish_Pref_CP1_CI_AS", "010102020202")]
    [DataRow("'' collate SQL_Latin1_General_Pref_CP1_CI_AS", "")]
    public void TertiaryWeights_WeighEachCharacter(string operand, string expectedHex) =>
        AreEqual(expectedHex, Convert.ToHexString((byte[])new Simulation().ExecuteScalar($"select tertiary_weights({operand})")!));

    [TestMethod]
    [DataRow("'abc' collate SQL_Latin1_General_CP1_CI_AS")]
    [DataRow("'abc' collate Latin1_General_CI_AS")]
    [DataRow("cast(null as varchar(3)) collate SQL_Latin1_General_Pref_CP1_CI_AS")]
    public void TertiaryWeights_OutsideAPrefCollation_IsNull(string operand) =>
        AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select tertiary_weights({operand})"));

    [TestMethod]
    public void TertiaryWeights_OfAMaxValue_IsEmpty() =>
        IsEmpty((byte[])new Simulation().ExecuteScalar("""
            declare @m varchar(max) = 'aB';
            select tertiary_weights(@m collate SQL_Latin1_General_Pref_CP1_CI_AS)
            """)!);

    [TestMethod]
    public void TertiaryWeights_IsVarbinaryOfTwiceTheLengthCappedAt8000() =>
        AreEqual("t:20,u:8000,w:6", new Simulation().ExecuteScalar("""
            select tertiary_weights(cast('aB' as varchar(10)) collate SQL_Latin1_General_Pref_CP1_CI_AS) t,
                tertiary_weights(cast('a' as varchar(5000)) collate SQL_Latin1_General_Pref_CP1_CI_AS) u,
                tertiary_weights(cast('a' as varchar(3)) collate Latin1_General_CI_AS) w
            into #z;
            select string_agg(concat(name, ':', max_length), ',') within group (order by name)
            from tempdb.sys.columns where object_id = object_id('tempdb..#z') and type_name(system_type_id) = 'varbinary'
            """));

    [TestMethod]
    [DataRow("N'abc'", "nvarchar")]
    [DataRow("null", "NULL")]
    [DataRow("1", "int")]
    [DataRow("0x61", "varbinary")]
    public void TertiaryWeights_OfANonAnsiString_RaisesMsg8116(string operand, string type) =>
        new Simulation().AssertSqlError($"select tertiary_weights({operand})", 8116, $"Argument data type {type} is invalid for argument 1 of tertiary_weights function.");

    [TestMethod]
    public void TertiaryWeights_TakesOneArgument() =>
        new Simulation().AssertSqlError("select tertiary_weights('a', 'b')", 174, "The tertiary_weights function requires 1 argument(s).");
}
