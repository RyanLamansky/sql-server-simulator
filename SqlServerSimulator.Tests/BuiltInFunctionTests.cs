using System.Data.Common;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

[TestClass]
public sealed class BuiltInFunctionTests
{
    [TestMethod]
    public void UnrecognizedBuiltInFunction()
    {
        var exception = Throws<DbException>(() => ExecuteScalar<int>("select frog()"));
        AreEqual("'frog' is not a recognized built-in function name.", exception.Message);
    }

    [TestMethod]
    [DataRow("abs")]
    [DataRow("datalength")]
    public void NullPassThrough(string function)
    {
        AreEqual(function.ToLowerInvariant(), function);
        _ = IsInstanceOfType<DBNull>(ExecuteScalar($"select {function}(null)"));
        _ = IsInstanceOfType<DBNull>(ExecuteScalar($"select {function.ToUpperInvariant()}(null)"));
    }

    [TestMethod]
    [DataRow("datalength", "1", 4)]
    [DataRow("abs", "1", 1)]
    [DataRow("abs", "0", 0)]
    [DataRow("abs", "-1", 1)]
    public void BuiltInFunction(string function, string input, object output)
    {
        AreEqual(function.ToLowerInvariant(), function);
        AreEqual(output, ExecuteScalar($"select {function}({input})"));
        AreEqual(output, ExecuteScalar($"select {function.ToUpperInvariant()}({input})"));
    }

    [TestMethod]
    [DataRow("len('abc')", 3)]
    [DataRow("len('abc   ')", 3)]                   // trailing spaces excluded
    [DataRow("len('   ')", 0)]                      // all-trailing-space → 0
    [DataRow("len('')", 0)]
    [DataRow("len('  abc')", 5)]                    // leading spaces preserved
    [DataRow("datalength('abc')", 3)]               // for varchar: 1 byte/char in CP1252
    [DataRow("datalength('café')", 4)]              // 'café' is 4 CP1252 bytes
    [DataRow("datalength(N'café')", 8)]             // nvarchar: 2 bytes/char
    [DataRow("upper('AbC')", "ABC")]
    [DataRow("upper('café')", "CAFÉ")]
    [DataRow("lower('AbC')", "abc")]
    [DataRow("lower('CAFÉ')", "café")]
    [DataRow("ltrim('   abc')", "abc")]
    [DataRow("ltrim('   abc   ')", "abc   ")]       // only leading
    [DataRow("rtrim('abc   ')", "abc")]
    [DataRow("rtrim('   abc   ')", "   abc")]       // only trailing
    [DataRow("trim('   abc   ')", "abc")]
    [DataRow("trim('abc')", "abc")]
    [DataRow("reverse('abc')", "cba")]
    [DataRow("reverse('')", "")]
    [DataRow("left('hello', 3)", "hel")]
    [DataRow("left('hi', 10)", "hi")]                // count past length clamps
    [DataRow("left('abc', 0)", "")]
    [DataRow("right('hello', 3)", "llo")]
    [DataRow("right('hi', 10)", "hi")]
    [DataRow("right('abc', 0)", "")]
    [DataRow("substring('hello', 2, 3)", "ell")]    // 1-indexed
    [DataRow("substring('hello', 1, 5)", "hello")]
    [DataRow("substring('hello', 6, 5)", "")]       // start past end → empty
    [DataRow("substring('hello', 0, 3)", "he")]     // start <= 0 truncates window
    [DataRow("charindex('lo', 'hello')", 4)]
    [DataRow("charindex('xx', 'hello')", 0)]        // not found
    [DataRow("charindex('LO', 'hello')", 4)]        // case-insensitive
    [DataRow("charindex('l', 'hello', 4)", 4)]      // start at l-position
    [DataRow("charindex('l', 'hello', 5)", 0)]      // start past last l
    [DataRow("replace('hello', 'l', 'L')", "heLLo")]
    [DataRow("replace('hello', 'L', 'X')", "heXXo")] // case-insensitive match
    [DataRow("replace('hello', 'xyz', '!')", "hello")]
    public void StringFunction(string expression, object expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("len")]
    [DataRow("upper")]
    [DataRow("lower")]
    [DataRow("ltrim")]
    [DataRow("rtrim")]
    [DataRow("trim")]
    [DataRow("reverse")]
    public void StringFunction_NullPassThrough(string function) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {function}(null)"));

    [TestMethod]
    [DataRow("left(null, 3)")]
    [DataRow("left('abc', null)")]
    [DataRow("right(null, 3)")]
    [DataRow("substring(null, 1, 3)")]
    [DataRow("substring('abc', null, 3)")]
    [DataRow("substring('abc', 1, null)")]
    [DataRow("charindex(null, 'abc')")]
    [DataRow("charindex('a', null)")]
    [DataRow("replace(null, 'a', 'b')")]
    [DataRow("replace('abc', null, 'b')")]
    [DataRow("replace('abc', 'a', null)")]
    public void MultiArgStringFunction_NullPropagates(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("left('abc', -1)")]
    [DataRow("right('abc', -1)")]
    [DataRow("substring('abc', 1, -1)")]
    public void NegativeLength_RaisesMsg536(string expression)
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar($"select {expression}"));
        AreEqual(536, ex.Number);
        Assert.Contains("Invalid length parameter", ex.Message);
    }

    [TestMethod]
    [DataRow("1/0")]
    [DataRow("1%0")]
    [DataRow("cast(1 as bigint) / 0")]
    [DataRow("10 % cast(0 as bigint)")]
    [DataRow("isnull(1/0, 0)")]
    public void IntegerDivideByZero_RaisesMsg8134(string expression)
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar($"select {expression}"));
        AreEqual(8134, ex.Number);
        Assert.Contains("Divide by zero", ex.Message);
    }

    [TestMethod]
    [DataRow("left('abc', 2147483648)")]
    [DataRow("right('abc', 2147483648)")]
    public void IntArgumentOverflow_RaisesMsg8115(string expression)
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar($"select {expression}"));
        AreEqual(8115, ex.Number);
        Assert.Contains("Arithmetic overflow", ex.Message);
    }

    [TestMethod]
    [DataRow("substring('abc', -2147483648, 2147483647)", "")]
    [DataRow("substring('abc', 2147483647, 2147483647)", "")]
    [DataRow("substring('abcdef', 0, 3)", "ab")]
    [DataRow("substring('abcdef', -2, 5)", "ab")]
    public void Substring_ClampsExtremesInsteadOfThrowing(string expression, string expected)
        => AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("switchoffset(sysdatetimeoffset(), 9999)")]
    [DataRow("switchoffset(sysdatetimeoffset(), '+20:00')")]
    [DataRow("todatetimeoffset(getdate(), 9999)")]
    [DataRow("todatetimeoffset(getdate(), -2000)")]
    public void OffsetOutOfRange_RaisesMsg9812(string expression)
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar($"select {expression}"));
        AreEqual(9812, ex.Number);
        Assert.Contains("timezone provided to builtin function", ex.Message);
    }

    [TestMethod]
    public void DecimalLiteralBeyondMaxPrecision_RaisesMsg1007()
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar("select 1.23456789012345678901234567890123456789012"));
        AreEqual(1007, ex.Number);
        Assert.Contains("out of the range for numeric representation", ex.Message);
    }

    [TestMethod]
    public void ReplaceWithEmptySearch_ReturnsInputUnchanged()
        => AreEqual("abc", ExecuteScalar("select replace('abc', '', 'X')"));

    [TestMethod]
    public void FunctionOfColumn_FromTable()
    {
        // Regression: Expression.Parse used to advance past the function's
        // closing ')' AND let the surrounding while loop advance again,
        // skipping a trailing FROM. The `select abs(n) from t` shape never had
        // a test before this arc, so the bug surfaced only via EF Core.
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 5 )").ExecuteNonQuery();
        AreEqual(5, connection.CreateCommand("select abs(v) from t").ExecuteScalar());
    }

    [TestMethod]
    public void StringFunctionOfColumn_FromTable()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("create table t ( name varchar(20) )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 'alice' )").ExecuteNonQuery();
        AreEqual("ALICE", connection.CreateCommand("select upper(name) from t").ExecuteScalar());
    }

}
