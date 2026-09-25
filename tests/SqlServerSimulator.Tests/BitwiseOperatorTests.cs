using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Which operand types the bitwise <c>&amp;</c> / <c>|</c> / <c>^</c>
/// operators accept, and what each refusal reports — probed 2026-09-25
/// against SQL Server 2025 over every ordered pair of empty-table columns.
/// </summary>
[TestClass]
public sealed class BitwiseOperatorTests
{
    private const string Columns = "create table t (i int, b bit, d decimal(5,2), f float, s varchar(5), n nvarchar(5), vb varbinary(5), dt datetime, u uniqueidentifier, v sql_variant, h hierarchyid, g geography); ";

    [TestMethod]
    [DataRow("i & s")]
    [DataRow("s | b")]
    [DataRow("vb ^ i")]
    [DataRow("i & null")]
    [DataRow("null | b")]
    public void IntegerPartner_IsAccepted(string expression)
    {
        using var reader = new Simulation().ExecuteBatchesReader($"{Columns}select {expression} from t");
        IsFalse(reader.Read());
    }

    [TestMethod]
    [DataRow("i & d", "int", "decimal", "&")]
    [DataRow("s | s", "varchar", "varchar", "|")]
    [DataRow("vb ^ vb", "varbinary", "varbinary", "^")]
    [DataRow("v & i", "sql_variant", "int", "&")]
    [DataRow("dt | i", "datetime", "int", "|")]
    [DataRow("d & null", "decimal", "NULL", "&")]
    [DataRow("null ^ s", "NULL", "varchar", "^")]
    public void IncompatiblePair_RaisesMsg402(string expression, string left, string right, string op)
        => new Simulation().AssertSqlError($"{Columns}select {expression} from t", 402, $"The data types {left} and {right} are incompatible in the '{op}' operator.");

    [TestMethod]
    [DataRow("d & f", "decimal", "&")]
    [DataRow("f | d", "float", "|")]
    [DataRow("dt ^ u", "datetime", "^")]
    public void TwoNonIntegers_RaiseMsg8117NamingTheLeft(string expression, string type, string op)
        => new Simulation().AssertSqlError($"{Columns}select {expression} from t", 8117, $"Operand data type {type} is invalid for '{op}' operator.");

    [TestMethod]
    [DataRow("h & i", "hierarchyid")]
    [DataRow("i & g", "geography")]
    [DataRow("null & h", "hierarchyid")]
    public void ClrOperand_RaisesMsg403(string expression, string type)
        => new Simulation().AssertSqlError($"{Columns}select {expression} from t", 403, $"Invalid operator for data type. Operator equals '&', type equals {type}.");

    [TestMethod]
    public void TwoNulls_RaiseMsg402()
        => new Simulation().AssertSqlError("select null & null", 402, "The data types NULL and NULL are incompatible in the '&' operator.");

    [TestMethod]
    public void DecimalLiteralBesideAnIntegerLiteral_RaisesMsg402()
        => new Simulation().AssertSqlError("select 1.5 & 1", 402, "The data types numeric and int are incompatible in the '&' operator.");

    [TestMethod]
    public void StringPartner_ConvertsToTheInteger()
        => AreEqual("0|12|4", new Simulation().ExecuteScalar("select concat_ws('|', cast(5 as tinyint) & '', 0 | '12', '6' & 5)"));
}
