using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A built-in's argument converts to the type its parameter is declared as
/// the way an assignment does, settled while compiling; probed 2026-09-25
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class BuiltInArgumentTypeTests
{
    [TestMethod]
    [DataRow("abs(cast('2020-01-01' as date))", "date")]
    [DataRow("ceiling(0x01)", "varbinary")]
    [DataRow("floor(cast('<a/>' as xml))", "xml")]
    [DataRow("sign(newid())", "uniqueidentifier")]
    [DataRow("sqrt(cast('10:00' as time))", "time")]
    [DataRow("sin(0x01)", "varbinary")]
    [DataRow("square(0x01)", "varbinary")]
    [DataRow("atn2(1, 0x01)", "varbinary")]
    [DataRow("power(2, 0x01)", "varbinary")]
    [DataRow("power(0x01, 2)", "varbinary")]
    [DataRow("log(2, 0x01)", "varbinary")]
    [DataRow("round(0x01, 0)", "varbinary")]
    [DataRow("degrees(0x01)", "varbinary")]
    [DataRow("str(0x01)", "varbinary")]
    public void MathArgument_WithNoConversionToFloat_RaisesMsg206(string call, string type)
        => new Simulation().AssertSqlError($"select {call}", 206, $"Operand type clash: {type} is incompatible with float");

    [TestMethod]
    [DataRow("char(cast('2020-01-01' as date))")]
    [DataRow("nchar(cast('2020-01-01' as date))")]
    [DataRow("space(cast('2020-01-01' as date))")]
    [DataRow("replicate('a', cast('2020-01-01' as date))")]
    [DataRow("left('a', cast('2020-01-01' as date))")]
    [DataRow("right('a', cast('2020-01-01' as date))")]
    [DataRow("eomonth('2020-01-01', cast('2020-01-01' as date))")]
    public void CountArgument_WithNoConversionToInt_RaisesMsg206(string call)
        => new Simulation().AssertSqlError($"select {call}", 206, "Operand type clash: date is incompatible with int");

    [TestMethod]
    [DataRow("year(newid())")]
    [DataRow("datepart(month, newid())")]
    [DataRow("datename(month, newid())")]
    [DataRow("dateadd(day, 1, newid())")]
    [DataRow("datediff(day, newid(), '2020-01-01')")]
    public void DateArgument_WithNoConversionToDatetime_RaisesMsg206(string call)
        => new Simulation().AssertSqlError($"select {call}", 206, "Operand type clash: uniqueidentifier is incompatible with datetime");

    [TestMethod]
    [DataRow("abs(cast(1 as sql_variant))", "float")]
    [DataRow("char(cast(1 as sql_variant))", "int")]
    [DataRow("year(cast(1 as sql_variant))", "datetime")]
    public void SqlVariantArgument_RaisesMsg257(string call, string target)
        => new Simulation().AssertSqlError($"select {call}", 257, $"Implicit conversion from data type sql_variant to {target} is not allowed. Use the CONVERT function to run this query.");

    [TestMethod]
    public void SqlVariantColumnArgument_OverAnEmptyTable_RaisesMsg260()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table e (v sql_variant, d date)");
        simulation.AssertSqlError("select sqrt(v) from e", 260, "Disallowed implicit conversion from data type sql_variant to data type float, table 'e', column 'v'. Use the CONVERT function to run this query.");
        simulation.AssertSqlError("select char(d) from e", 206, "Operand type clash: date is incompatible with int");
    }

    [TestMethod]
    public void AcceptedArguments_StillAnswer()
        => AreEqual("1|2.00|0.5|A|", new Simulation().ExecuteScalar("select concat(abs(cast(1 as bit)), '|', ceiling(cast(1.5 as money)), '|', sqrt('0.25'), '|', char(0x41), '|', year(null))"));

    [TestMethod]
    [DataRow("year(1.5)", 1900)]
    [DataRow("year(0x01)", 1900)]
    [DataRow("day(1.5)", 2)]
    [DataRow("day(cast(1 as money))", 2)]
    [DataRow("day(1.5e0)", 2)]
    [DataRow("datepart(hour, 1.5)", 12)]
    [DataRow("datediff(day, 1.5, '2020-01-01')", 43828)]
    [DataRow("datediff(day, 0x01, '2020-01-01')", 43829)]
    public void DateArgument_FromANumberOrBinary_ReadsAsDatetime(string call, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select {call}"));

    [TestMethod]
    public void DateAdd_OverANumber_AnswersDatetime()
        => AreEqual(new DateTime(1900, 1, 3, 12, 0, 0), new Simulation().ExecuteScalar("select dateadd(day, 1, 1.5)"));

    [TestMethod]
    public void DateName_OverANumber_ReadsAsDatetime()
        => AreEqual("January", new Simulation().ExecuteScalar("select datename(month, cast(1 as money))"));

    [TestMethod]
    [DataRow("datetrunc(day, 1.5)", "numeric", 2, "datetrunc")]
    [DataRow("datetrunc(day, 0x01)", "varbinary", 2, "datetrunc")]
    [DataRow("datetrunc(day, cast('<a/>' as xml))", "xml", 2, "datetrunc")]
    [DataRow("datetrunc(day, cast(null as int))", "int", 2, "datetrunc")]
    [DataRow("date_bucket(day, 1, 1)", "int", 3, "Date_Bucket")]
    [DataRow("date_bucket(day, 1, cast(1 as sql_variant))", "sql_variant", 3, "Date_Bucket")]
    [DataRow("eomonth(1e0)", "float", 1, "eomonth")]
    [DataRow("eomonth(cast('10:00' as time))", "time", 1, "eomonth")]
    [DataRow("eomonth(cast('<a/>' as xml))", "xml", 1, "eomonth")]
    public void DateOnlyFunction_OverANonDate_RaisesMsg8116(string call, string type, int index, string function)
        => new Simulation().AssertSqlError($"select {call}", 8116, $"Argument data type {type} is invalid for argument {index} of {function} function.");

    [TestMethod]
    public void DateOnlyFunction_OverAnEmptyTablesIntColumn_RaisesMsg8116()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table e (i int)");
        simulation.AssertSqlError("select datetrunc(day, i) from e", 8116, "Argument data type int is invalid for argument 2 of datetrunc function.");
    }

    [TestMethod]
    public void DateOnlyFunction_OverAStringOrNull_StillAnswers()
        => AreEqual("2020-01-31|2020-01-05 00:00:00.0000000||", new Simulation().ExecuteScalar("select concat(eomonth('2020-01-05'), '|', datetrunc(day, '2020-01-05 10:00'), '|', datetrunc(day, null), '|', eomonth(null))"));
}
