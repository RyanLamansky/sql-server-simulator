using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>CRYPT_GEN_RANDOM(length [, seed])</c>. Every expectation probed
/// 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class CryptGenRandomTests
{
    [TestMethod]
    [DataRow("datalength(crypt_gen_random(4))", 4)]
    [DataRow("datalength(crypt_gen_random(8000))", 8000)]
    [DataRow("datalength(crypt_gen_random(4, 0x0102030405))", 4)]
    [DataRow("datalength(crypt_gen_random(1, 0x))", 1)]
    [DataRow("datalength(crypt_gen_random(4, null))", 4)]
    [DataRow("sql_variant_property(crypt_gen_random(4), 'MaxLength')", 8000)]
    [DataRow("count(distinct crypt_gen_random(8)) from (values (1), (2), (3), (4), (5)) v (x)", 5)]
    public void Draws_TheRequestedBytes(string expression, int expected)
        => AreEqual(expected, Convert.ToInt32(new Simulation().ExecuteScalar($"select {expression}"), System.Globalization.CultureInfo.InvariantCulture));

    [TestMethod]
    [DataRow("crypt_gen_random(0)")]
    [DataRow("crypt_gen_random(8001)")]
    [DataRow("crypt_gen_random(-1)")]
    [DataRow("crypt_gen_random(cast(null as int))")]
    [DataRow("crypt_gen_random(4, 0x010203)")]
    public void AnswersNull(string expression)
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("crypt_gen_random(null)", 8116, "Argument data type NULL is invalid for argument 1 of Crypt_Gen_Random function.")]
    [DataRow("crypt_gen_random(cast(3 as tinyint))", 8116, "Argument data type tinyint is invalid for argument 1 of Crypt_Gen_Random function.")]
    [DataRow("crypt_gen_random(4.7)", 8116, "Argument data type numeric is invalid for argument 1 of Crypt_Gen_Random function.")]
    [DataRow("crypt_gen_random(4, 'ab')", 8116, "Argument data type varchar is invalid for argument 2 of Crypt_Gen_Random function.")]
    [DataRow("crypt_gen_random()", 189, "The Crypt_Gen_Random function requires 1 to 2 arguments.")]
    [DataRow("crypt_gen_random(1, 0x01, 1)", 189, "The Crypt_Gen_Random function requires 1 to 2 arguments.")]
    [DataRow("crypt_gen_random(4, cast(replicate(cast('a' as varchar(max)), 9000) as varbinary(max)))", 8152, "String or binary data would be truncated.")]
    public void RefusesAsRealDoes(string expression, int number, string message)
        => new Simulation().AssertSqlError($"select {expression}", number, message);

    [TestMethod]
    public void IsNondeterministic()
    {
        _ = new Simulation().AssertSqlError("create table t (a int, b as crypt_gen_random(4) persisted)", 4936);
        new Simulation().AssertSqlError("create function dbo.f () returns varbinary(10) as begin return crypt_gen_random(4) end", 443,
            "Invalid use of a side-effecting operator 'Crypt_Gen_Random' within a function.");
    }
}
