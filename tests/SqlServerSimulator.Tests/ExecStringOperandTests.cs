using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>EXEC ( … )</c> takes string literals and variables joined by <c>+</c>
/// and nothing else. Every expectation probed 2026-09-26 against SQL Server
/// 2025.
/// </summary>
[TestClass]
public sealed class ExecStringOperandTests
{
    [TestMethod]
    [DataRow("exec ('select ' + '1' + '2')", 12)]
    [DataRow("declare @v varchar(9) = '1'; exec ('select ' + @v + ' + 1')", 2)]
    [DataRow("declare @v nvarchar(20) = N'select 3'; exec (@v)", 3)]
    public void LiteralsAndVariables_Concatenate(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    [TestMethod]
    [DataRow("exec (('select 1'))", 102, "Incorrect syntax near '('.")]
    [DataRow("exec ('select ' + upper('1'))", 102, "Incorrect syntax near 'upper'.")]
    [DataRow("exec (0x73656c6563742036)", 102, "Incorrect syntax near '0x73656c6563742036'.")]
    [DataRow("exec (null)", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("exec ('select 1' + null)", 156, "Incorrect syntax near the keyword 'null'.")]
    [DataRow("exec (N'select 3' collate Latin1_General_BIN)", 156, "Incorrect syntax near the keyword 'collate'.")]
    [DataRow("declare @x xml = '<a/>'; exec (@x)", 257, "Implicit conversion from data type xml to nvarchar is not allowed. Use the CONVERT function to run this query.")]
    public void AnythingElse_IsRefused(string sql, int number, string message)
        => new Simulation().AssertSqlError(sql, number, message);
}
