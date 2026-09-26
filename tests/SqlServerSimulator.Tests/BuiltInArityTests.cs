using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A built-in called with the wrong number of arguments is refused by count
/// before its arguments bind, in real's own wording and state. Every
/// expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BuiltInArityTests
{
    [TestMethod]
    [DataRow("select left('abc')", 174, 1, "The left function requires 2 argument(s).")]
    [DataRow("select getdate(1)", 174, 1, "The getdate function requires 0 argument(s).")]
    [DataRow("select abs(1, 2, 3)", 174, 1, "The abs function requires 1 argument(s).")]
    [DataRow("select datefromparts(2020, 1)", 174, 1, "The datefromparts function requires 3 argument(s).")]
    [DataRow("select datetimefromparts(1, 2, 3, 4, 5, 6)", 174, 1, "The datetimefromparts function requires 7 argument(s).")]
    [DataRow("select json_value('{}')", 174, 2, "The json_value function requires 2 argument(s).")]
    [DataRow("select json_value('{}', '$', 1)", 174, 3, "The json_value function requires 2 argument(s).")]
    [DataRow("select substring('abc')", 189, 1, "The substring function requires 2 to 3 arguments.")]
    [DataRow("select concat('a')", 189, 1, "The concat function requires 2 to 254 arguments.")]
    [DataRow("select object_id('a', 'U', 1)", 189, 1, "The object_id function requires 1 to 2 arguments.")]
    [DataRow("select db_name(1, 2)", 189, 1, "The db_name function requires 0 to 1 arguments.")]
    [DataRow("select choose(1)", 1076, 1, "Function 'choose' requires at least 2 argument(s).")]
    [DataRow("select checksum()", 1076, 1, "Function 'checksum' requires at least 1 argument(s).")]
    public void WrongCount_IsRefusedByCount(string sql, int number, int state, string message)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual((byte)state, ex.State);
    }

    [TestMethod]
    [DataRow("select trim()", 189, 1, "The Trim function requires 1 to 3 arguments.")]
    [DataRow("select trim('a', 'b')", 174, 1, "The trim function requires 1 argument(s).")]
    [DataRow("select object_definition()", 189, 1, "The object_definition function requires 1 to 3 arguments.")]
    [DataRow("select object_definition(1, 2, 3)", 174, 5, "The object_definition function requires 1 argument(s).")]
    public void TooFewAndTooMany_CanBeRefusedDifferently(string sql, int number, int state, string message)
    {
        var ex = new Simulation().AssertSqlError(sql, number);
        AreEqual(message, ex.Errors[0].Message);
        AreEqual((byte)state, ex.State);
    }

    [TestMethod]
    [DataRow("select left()", "Incorrect syntax near ')'.")]
    [DataRow("select isnull(1, 2,)", "Incorrect syntax near ')'.")]
    [DataRow("select count(distinct 1, 2)", "Incorrect syntax near ','.")]
    [DataRow("select left(1 +, 1)", "Incorrect syntax near ','.")]
    public void TheListsOwnSyntaxError_ComesFirst(string sql, string message)
        => new Simulation().AssertSqlError(sql, 102, message);

    [TestMethod]
    public void TheCount_OutranksABindError()
        => new Simulation().AssertSqlError("create table t (a int); select nosuch, left(a) from t", 174);

    [TestMethod]
    public void ADeadBranch_IsStillCounted()
        => new Simulation().AssertSqlError("if 1 = 0 select getdate(1)", 174);

    [TestMethod]
    public void NestedCommas_DoNotCount()
        => AreEqual(1.50m, new Simulation().ExecuteScalar("select convert(decimal(5, 2), left('1.5x', (select 3)))"));
}
