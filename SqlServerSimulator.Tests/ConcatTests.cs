using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// <c>CONCAT</c> and <c>CONCAT_WS</c>: variadic string-concatenation scalars
/// that skip NULL inputs (rather than propagating). Probe-anchored against
/// SQL Server 2025 (2026-05-09).
/// </summary>
[TestClass]
public sealed class ConcatTests
{
    [TestMethod]
    [DataRow("concat('a', 'b')", "ab")]
    [DataRow("concat('a', 'b', 'c')", "abc")]
    [DataRow("concat('a', '', 'c')", "ac")]
    [DataRow("concat(1, 2)", "12")]
    [DataRow("concat('a', 1)", "a1")]
    [DataRow("concat(1, 2, 3, 4, 5)", "12345")]
    [DataRow("concat('x=', cast(3.14 as decimal(10,2)))", "x=3.14")]
    [DataRow("concat('today: ', cast('2026-05-09' as date))", "today: 2026-05-09")]
    [DataRow("concat('t=', cast('2026-05-09T12:34:56' as datetime2(7)))", "t=2026-05-09 12:34:56.0000000")]
    [DataRow("concat('z=', cast('2026-05-09T12:34:56+05:00' as datetimeoffset(7)))", "z=2026-05-09 12:34:56.0000000 +05:00")]
    [DataRow("concat('b=', cast(1 as bit))", "b=1")]
    [DataRow("concat('m=', cast(123.45 as money))", "m=123.45")]
    public void Concat_Basic(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("concat('a', null, 'b')", "ab")]
    [DataRow("concat(null, 'a', null, 'b', null)", "ab")]
    [DataRow("concat(null, null)", "")]
    [DataRow("concat(null, null, null)", "")]
    [DataRow("concat('a', null)", "a")]
    [DataRow("concat(null, 'a')", "a")]
    public void Concat_SkipsNulls(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("concat()")]
    [DataRow("concat('a')")]
    public void Concat_TooFewArguments_RaisesMsg189(string expression) =>
        AssertSqlError($"select {expression}", 189, "The concat function requires 2 to 254 arguments.");

    [TestMethod]
    [DataRow("concat_ws(',', 'a', 'b', 'c')", "a,b,c")]
    [DataRow("concat_ws('-', 'a', 'b')", "a-b")]
    [DataRow("concat_ws('', 'a', 'b')", "ab")]
    [DataRow("concat_ws('-', 1, 2, 3)", "1-2-3")]
    [DataRow("concat_ws('-', 'a', 1, cast('2026-05-09' as date))", "a-1-2026-05-09")]
    public void ConcatWs_Basic(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("concat_ws(',', 'a', null, 'b')", "a,b")]
    [DataRow("concat_ws(',', null, 'a', 'b')", "a,b")]
    [DataRow("concat_ws(',', 'a', 'b', null)", "a,b")]
    [DataRow("concat_ws(',', null, null, null)", "")]
    [DataRow("concat_ws(',', 'a', null, null, 'b')", "a,b")]
    public void ConcatWs_SkipsNullValues(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("concat_ws(null, 'a', 'b')", "ab")]
    [DataRow("concat_ws(null, 'a', 'b', 'c')", "abc")]
    [DataRow("concat_ws(null, null, null)", "")]
    public void ConcatWs_NullSeparator_DegradesToEmpty(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("concat_ws()")]
    [DataRow("concat_ws(',')")]
    [DataRow("concat_ws(',', 'a')")]
    [DataRow("concat_ws(',', null)")]
    public void ConcatWs_TooFewArguments_RaisesMsg189(string expression) =>
        AssertSqlError($"select {expression}", 189, "The concat_ws function requires 3 to 254 arguments.");

    [TestMethod]
    public void Concat_ResultIsVarcharByDefault()
    {
        // All-ASCII string args → varchar; round-trip through DataLength
        // (varchar = 1 byte/char) confirms no nvarchar promotion.
        AreEqual(3, ExecuteScalar("select datalength(concat('a', 'b', 'c'))"));
    }

    [TestMethod]
    public void Concat_AnyNVarcharArg_PromotesToNVarchar()
    {
        // nvarchar = 2 bytes/char in CP1252 / UTF-16; presence of N'...'
        // on any arg promotes the result.
        AreEqual(6, ExecuteScalar("select datalength(concat('a', 'b', N'c'))"));
    }

    [TestMethod]
    public void Concat_NullsOnly_ReturnsEmptyString_NotNull()
    {
        // Probe-confirmed SQL Server quirk: typed metadata says NOT NULL
        // even when every input is NULL; runtime returns ''.
        AreEqual("", ExecuteScalar("select concat(null, null)"));
    }

    [TestMethod]
    public void ConcatWs_NullsOnly_ReturnsEmptyString_NotNull() =>
        AreEqual("", ExecuteScalar("select concat_ws(',', null, null, null)"));

    [TestMethod]
    public void Concat_OfColumns_FromTable() =>
        AreEqual("foobar", new Simulation().ExecuteScalar("""
            create table t (a varchar(10), b varchar(10), c varchar(10));
            insert t values ('foo', null, 'bar');
            select concat(a, b, c) from t
            """));

    [TestMethod]
    public void ConcatWs_OfColumns_FromTable() =>
        AreEqual("foo|bar", new Simulation().ExecuteScalar("""
            create table t (a varchar(10), b varchar(10), c varchar(10));
            insert t values ('foo', null, 'bar');
            select concat_ws('|', a, b, c) from t
            """));
}

/// <summary>
/// String <c>+</c> operator: NULL-propagating concatenation distinct from
/// CONCAT's NULL-skipping. EF Core 10 emits this for <c>string.Concat</c> and
/// <c>+</c>-chains over server-evaluated string operands. Probe-anchored
/// against SQL Server 2025 (2026-05-09).
/// </summary>
[TestClass]
public sealed class StringPlusOperatorTests
{
    [TestMethod]
    [DataRow("'a' + 'b'", "ab")]
    [DataRow("'' + 'a'", "a")]
    [DataRow("'a' + ''", "a")]
    [DataRow("'a' + 'b' + 'c'", "abc")]
    [DataRow("'a' + 'b' + 'c' + 'd'", "abcd")]
    [DataRow("N'a' + N'b'", "ab")]
    [DataRow("'a' + N'b'", "ab")]
    [DataRow("N'a' + 'b'", "ab")]
    public void Plus_StringConcat(string expression, string expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("'a' + null")]
    [DataRow("null + 'a'")]
    [DataRow("'a' + null + 'b'")]
    [DataRow("cast(null as varchar(10)) + 'a'")]
    [DataRow("'a' + cast(null as nvarchar(10))")]
    public void Plus_PropagatesNull(string expression) =>
        IsInstanceOfType<DBNull>(ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Plus_VarcharVarchar_StaysVarchar() =>
        AreEqual(2, ExecuteScalar("select datalength('a' + 'b')"));

    [TestMethod]
    [DataRow("'a' + N'b'")]
    [DataRow("N'a' + 'b'")]
    public void Plus_NVarcharOnEitherSide_PromotesToNVarchar(string expression) =>
        AreEqual(4, ExecuteScalar($"select datalength({expression})"));

    /// <summary>
    /// <c>char(5) + char(5)</c> → <c>char(10)</c> (probe-confirmed). Trailing-space
    /// padding on each operand survives the concat as a side-effect of the storage rep.
    /// </summary>
    [TestMethod]
    public void Plus_CharPair_PreservesFixedLengthAndCombinesLengths() =>
        AreEqual("a    b    ", new Simulation().ExecuteScalar("""
            create table t (a char(5), b char(5));
            insert t values ('a', 'b');
            select a + b from t
            """));

    /// <summary><c>char(5)+char(3)</c> → <c>char(8)</c>, one byte per char.</summary>
    [TestMethod]
    public void Plus_CharPair_DataLength_MatchesCombinedLength() =>
        AreEqual(8, new Simulation().ExecuteScalar("""
            create table t (a char(5), b char(3));
            insert t values ('x', 'y');
            select datalength(a + b) from t
            """));

    /// <summary><c>nchar(5)+nchar(5)</c> → <c>nchar(10)</c>, two bytes per char → datalength = 20.</summary>
    [TestMethod]
    public void Plus_NCharPair_PromotesToNCharWithCombinedLength() =>
        AreEqual(20, new Simulation().ExecuteScalar("""
            create table t (a nchar(5), b nchar(5));
            insert t values (N'x', N'y');
            select datalength(a + b) from t
            """));

    /// <summary><c>char(5) + nchar(5)</c> → <c>nchar(10)</c>, datalength 20.</summary>
    [TestMethod]
    public void Plus_CharNCharMix_PromotesToNCharWithCombinedLength() =>
        AreEqual(20, new Simulation().ExecuteScalar("""
            create table t (a char(5), b nchar(5));
            insert t values ('x', N'y');
            select datalength(a + b) from t
            """));

    [TestMethod]
    public void Plus_TextOperand_RaisesMsg402() =>
        AssertSqlError("select cast('a' as text) + 'b'", 402,
            "The data types text and varchar are incompatible in the add operator.");

    [TestMethod]
    public void Plus_NTextOperand_RaisesMsg402() =>
        AssertSqlError("select 'a' + cast(N'b' as ntext)", 402,
            "The data types varchar and ntext are incompatible in the add operator.");

    /// <summary>
    /// <c>'5'</c> parses to int via existing integer↔string promotion; result is int.
    /// </summary>
    [TestMethod]
    [DataRow("'5' + 1", 6)]
    [DataRow("1 + '5'", 6)]
    public void Plus_StringPlusInteger_RoutesThroughIntegerArithmetic(string expression, int expected) =>
        AreEqual(expected, ExecuteScalar($"select {expression}"));

    [TestMethod]
    public void Plus_OfColumns_FromTable() =>
        AreEqual("foobar", new Simulation().ExecuteScalar("""
            create table t (a varchar(10), b varchar(10));
            insert t values ('foo', 'bar');
            select a + b from t
            """));

    [TestMethod]
    public void Plus_ColumnAndNull_PropagatesNull() =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar("""
            create table t (a varchar(10), b varchar(10));
            insert t values ('foo', null);
            select a + b from t
            """));
}
