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
    [DataRow("dateadd(second, 9223372036854775807, getdate())")]
    [DataRow("dateadd(year, 2147483648, getdate())")]
    [DataRow("dateadd(day, 9999999999, getdate())")]
    public void DateAddCountOutOfIntRange_RaisesMsg517(string expression)
    {
        var ex = Throws<SimulatedSqlException>(() => ExecuteScalar($"select {expression}"));
        AreEqual(517, ex.Number);
        Assert.Contains("caused an overflow", ex.Message);
    }

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

    /// <summary>
    /// An integer argument outside <c>int</c> range raises Msg 8115 rather than
    /// leaking .NET's narrowing exception. Probe-confirmed against SQL Server
    /// 2025 (2026-07-31) for every function here.
    /// </summary>
    [TestMethod]
    [DataRow("select substring('abcdef', 1, 3000000000)")]
    [DataRow("select substring('abcdef', 3000000000, 1)")]
    [DataRow("select charindex('a', 'abc', 3000000000)")]
    [DataRow("select stuff('abcdef', 3000000000, 1, 'x')")]
    [DataRow("select stuff('abcdef', 1, 3000000000, 'x')")]
    [DataRow("select replicate('a', 3000000000)")]
    [DataRow("select space(3000000000)")]
    [DataRow("select choose(3000000000, 'a', 'b')")]
    [DataRow("select char(3000000000)")]
    [DataRow("select nchar(3000000000)")]
    [DataRow("select parsename('a.b', 3000000000)")]
    [DataRow("select str(1.5, 3000000000, 2)")]
    [DataRow("select left('abcdef', 3000000000)")]
    [DataRow("select right('abcdef', 3000000000)")]
    public void OutOfIntRangeArgument_RaisesArithmeticOverflow(string sql)
        => new Simulation().AssertSqlError(sql, 8115, "Arithmetic overflow error converting expression to data type int.");

    /// <summary>
    /// No string function accepts a legacy LOB type as the operand it
    /// transforms — real raises Msg 8116 naming the function and argument
    /// (probe-confirmed 2026-07-31).
    /// </summary>
    [TestMethod]
    [DataRow("select len(cast('abc' as ntext))", "ntext", 1, "len")]
    [DataRow("select len(cast('abc' as text))", "text", 1, "len")]
    [DataRow("select left(cast('abc' as ntext), 2)", "ntext", 1, "left")]
    [DataRow("select right(cast('abc' as text), 2)", "text", 1, "right")]
    [DataRow("select upper(cast('abc' as ntext))", "ntext", 1, "upper")]
    [DataRow("select lower(cast('abc' as text))", "text", 1, "lower")]
    [DataRow("select ltrim(cast('abc' as ntext))", "ntext", 1, "ltrim")]
    [DataRow("select reverse(cast('abc' as ntext))", "ntext", 1, "reverse")]
    [DataRow("select replace(cast('abc' as ntext), 'a', 'b')", "ntext", 1, "replace")]
    [DataRow("select replace('abc', cast('a' as ntext), 'b')", "ntext", 2, "replace")]
    [DataRow("select replace('abc', 'a', cast('b' as ntext))", "ntext", 3, "replace")]
    [DataRow("select charindex(cast('a' as ntext), 'abc')", "ntext", 1, "charindex")]
    public void LegacyLobArgument_IsRejected(string sql, string typeName, int argument, string function)
        => new Simulation().AssertSqlError(
            sql, 8116, $"Argument data type {typeName} is invalid for argument {argument} of {function} function.");

    /// <summary>
    /// The exceptions are arguments that are read rather than transformed —
    /// CHARINDEX's haystack and SUBSTRING's source both take a LOB, which is
    /// how a legacy <c>text</c> column is meant to be read. Probe-confirmed.
    /// </summary>
    [TestMethod]
    [DataRow("select charindex('a', cast('abc' as ntext))", 1)]
    [DataRow("select charindex('a', cast('abc' as text))", 1)]
    public void LegacyLobSearchedArgument_IsAccepted(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(sql));

    /// <inheritdoc cref="LegacyLobSearchedArgument_IsAccepted"/>
    [TestMethod]
    [DataRow("select substring(cast('abc' as ntext), 1, 2)")]
    [DataRow("select substring(cast('abc' as text), 1, 2)")]
    public void LegacyLobSubstring_IsAccepted(string sql)
        => AreEqual("ab", new Simulation().ExecuteScalar(sql));

    /// <summary>
    /// The legacy LOB types are column-only; a local variable of one is
    /// Msg 2739, which is why no string function ever sees one through a
    /// variable. Probe-confirmed verbatim.
    /// </summary>
    [TestMethod]
    [DataRow("declare @v text")]
    [DataRow("declare @v ntext")]
    [DataRow("declare @v image")]
    public void LegacyLobLocalVariable_IsRejected(string sql)
        => new Simulation().AssertSqlError(
            sql, 2739, "The text, ntext, and image data types are invalid for local variables.");

    /// <summary>
    /// POWER reports an overflow differently per result type — probe-confirmed
    /// 2026-07-31 and not obviously principled: an infinite intermediate names
    /// <c>float</c> whatever the declared type, a <c>bigint</c> result names
    /// the type, and an <c>int</c> result gives the value-bearing Msg 232.
    /// </summary>
    [TestMethod]
    public void PowerOverflow_ReportsPerResultType()
    {
        new Simulation().AssertSqlError("select power(2, 10000)", 8115, "Arithmetic overflow error converting expression to data type float.");
        new Simulation().AssertSqlError("select power(cast(2 as bigint), 200)", 8115, "Arithmetic overflow error converting expression to data type bigint.");
        Assert.Contains("Arithmetic overflow error for type int", new Simulation().AssertSqlError("select power(2, 62)", 232).Message);
    }
}
