using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// SQL Server 2025's native <c>REGEXP_*</c> surface: the four scalars, the
/// <c>REGEXP_LIKE</c> predicate, and the two rowset members. Every expected
/// value here was probed against a live SQL Server 2025 (17.0.4065.4)
/// reference instance.
/// </summary>
[TestClass]
public sealed class RegexpFunctionTests
{
    [TestMethod]
    [DataRow("regexp_count('aXaXa', 'a')", 3)]
    [DataRow("regexp_count('aXaXa', 'a', 2)", 2)]
    [DataRow("regexp_count('aXaXa', 'a', 2, 'i')", 2)]
    [DataRow("regexp_count('aXaXa', 'a', 99)", 0)]
    [DataRow("regexp_count('abc', '')", 4)]
    [DataRow("regexp_count('abc', 'x*')", 4)]
    [DataRow("regexp_count('aXbXc', 'X*')", 6)]
    [DataRow("regexp_count('aaaa', 'aa')", 2)]
    [DataRow("regexp_count('ABC', 'abc', 1, 'i')", 1)]
    [DataRow("regexp_count('ABC', 'abc', 1, 'c')", 0)]
    public void Count_Matrix(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    [TestMethod]
    [DataRow("regexp_instr('aXaXa', 'a')", 1)]
    [DataRow("regexp_instr('aXaXa', 'a', 2)", 3)]
    [DataRow("regexp_instr('aXaXa', 'a', 2, 2)", 5)]
    [DataRow("regexp_instr('aXaXa', 'a', 2, 2, 1)", 6)]
    [DataRow("regexp_instr('aXaXa', 'a', 2, 2, 1, 'i')", 6)]
    [DataRow("regexp_instr('aXaXa', 'a', 2, 2, 1, 'i', 1)", 0)]
    [DataRow("regexp_instr('a1b2', '(a)(1)', 1, 1, 0, 'i', 2)", 2)]
    [DataRow("regexp_instr('a1b2', '(a)(1)', 1, 1, 0, 'i', 0)", 1)]
    [DataRow("regexp_instr('a1b2', '(a)(1)', 1, 1, 0, 'i', 3)", 0)]
    [DataRow("regexp_instr('aXaXa', 'a', 99)", 0)]
    // A NULL return_option is the family's one non-propagating argument.
    [DataRow("regexp_instr('aXa', 'a', 1, 1, null)", 1)]
    [DataRow("regexp_instr('aXa', 'a', 1, 1, 1)", 2)]
    public void Instr_Matrix(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    [TestMethod]
    [DataRow("regexp_replace('aXaXa', 'a', 'Z')", "ZXZXZ")]
    [DataRow("regexp_replace('aXaXa', 'a', 'Z', 2)", "aXZXZ")]
    [DataRow("regexp_replace('aXaXa', 'a', 'Z', 2, 2)", "aXaXZ")]
    [DataRow("regexp_replace('aXaXa', 'a', 'Z', 1, 0)", "ZXZXZ")]
    // The two-argument form deletes every match.
    [DataRow("regexp_replace('aXaXa', 'a')", "XX")]
    [DataRow("regexp_replace('abc', 'z', '-')", "abc")]
    // Oracle-style backslash backreferences; `$` carries no meaning.
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '\\2\\1')", "1a2b")]
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '$2$1')", "$2$1$2$1")]
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '\\3')", "")]
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '\\0')", "\\0\\0")]
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '\\\\1')", "\\1\\1")]
    [DataRow("regexp_replace('a1b2', '([a-z])([0-9])', '\\10')", "a0b0")]
    [DataRow("regexp_replace('abc', 'b', '\\n')", "a\\nc")]
    // An empty pattern is a no-op even though `x*` — which also matches
    // empty — replaces at every position.
    [DataRow("regexp_replace('abc', '', '-')", "abc")]
    [DataRow("regexp_replace('abc', 'x*', '-')", "-a-b-c-")]
    [DataRow("regexp_replace('aXbXc', 'X*', '-')", "-a--b--c-")]
    public void Replace_Matrix(string expression, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString($"select {expression}"));

    [TestMethod]
    [DataRow("regexp_substr('abcabc', 'b.')", "bc")]
    [DataRow("regexp_substr('aXaXa', 'a', 2, 2)", "a")]
    [DataRow("regexp_substr('a1b2', '(a)(1)', 1, 1, 'i', 2)", "1")]
    [DataRow("regexp_substr('a1b2', '(a)(1)', 1, 1, 'i', 0)", "a1")]
    [DataRow("regexp_substr('aaa', 'a+?')", "a")]
    [DataRow("regexp_substr('abc', '')", "")]
    public void Substr_Matrix(string expression, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString($"select {expression}"));

    [TestMethod]
    [DataRow("regexp_substr('abc', 'z')")]
    [DataRow("regexp_substr('aXaXa', 'a', 99)")]
    [DataRow("regexp_substr('a1b2', '(a)(1)', 1, 1, 'i', 3)")]
    public void Substr_NoMatchOrMissingGroup_IsNull(string expression) =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select {expression}"));

    /// <summary>
    /// A NULL in any argument yields NULL, and the check runs before the
    /// pattern compiles — so <c>regexp_count(null, '(')</c> is NULL rather than
    /// a pattern error.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_count(null, 'a')")]
    [DataRow("regexp_count('aXa', null)")]
    [DataRow("regexp_count('aXa', 'a', null)")]
    [DataRow("regexp_count('aXa', 'a', 1, null)")]
    [DataRow("regexp_count(null, '(')")]
    [DataRow("regexp_count(null, 'a', 0)")]
    [DataRow("regexp_instr(null, 'a')")]
    [DataRow("regexp_instr('aXa', 'a', 1, null)")]
    [DataRow("regexp_instr('a(1)', '(a)', 1, 1, 0, 'i', null)")]
    [DataRow("regexp_replace(null, 'a', 'Z')")]
    [DataRow("regexp_replace('aXa', 'a', null)")]
    [DataRow("regexp_replace('aXa', 'a', 'Z', null)")]
    [DataRow("regexp_substr(null, 'a')")]
    [DataRow("regexp_substr('aXa', null)")]
    public void NullArgument_YieldsNull(string expression) =>
        IsInstanceOfType<DBNull>(new Simulation().ExecuteScalar($"select {expression}"));

    [TestMethod]
    [DataRow("regexp_count('ABC', 'abc', 1, 'ic')", 0)]
    [DataRow("regexp_count('ABC', 'abc', 1, 'ci')", 1)]
    [DataRow("regexp_count('ABC', 'abc', 1, 'ii')", 1)]
    [DataRow("regexp_count('ABC', 'abc', 1, 'im')", 1)]
    [DataRow("regexp_count('a' + char(10) + 'b', '^b', 1, 'm')", 1)]
    [DataRow("regexp_count('a' + char(10) + 'b', '^b', 1, '')", 0)]
    [DataRow("regexp_count('a' + char(10) + 'b', 'a.b', 1, 's')", 1)]
    [DataRow("regexp_count('a' + char(10) + 'b', 'a.b', 1, '')", 0)]
    [DataRow("regexp_count('a' + char(10), 'a$', 1, 'm')", 1)]
    public void Flags_Matrix(string expression, int expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar<int>($"select {expression}"));

    /// <summary>
    /// Real quotes the whole flags string rather than the offending character,
    /// matches case-sensitively, and rejects Oracle's <c>x</c> (free-spacing).
    /// </summary>
    [TestMethod]
    [DataRow("x")]
    [DataRow("z")]
    [DataRow("I")]
    [DataRow("g")]
    [DataRow("imsxc")]
    [DataRow(" i")]
    public void InvalidFlag_Msg19303(string flags) =>
        new Simulation().AssertSqlError(
            $"select regexp_count('abc', 'abc', 1, '{flags}')",
            19303,
            $"Invalid flag provided. '{flags}' are not valid flags. Only {{c,i,s,m}} flags are valid.");

    /// <summary>
    /// Msg 19301's per-(function, argument) state, and the two places real's
    /// wording is looser than the bound it enforces — <c>REGEXP_INSTR</c>'s
    /// <c>RETURN_OPTION</c> says "0" while rejecting 2, and its <c>GROUP</c>
    /// says "1" while accepting 0.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_count('a', 'a', 0)", "START", 1, "REGEXP_COUNT", 0)]
    [DataRow("regexp_replace('a', 'a', 'b', 0)", "START", 1, "REGEXP_REPLACE", 0)]
    [DataRow("regexp_replace('a', 'a', 'b', 1, -1)", "OCCURRENCE", 0, "REGEXP_REPLACE", -1)]
    [DataRow("regexp_instr('a', 'a', 0)", "START", 1, "REGEXP_INSTR", 0)]
    [DataRow("regexp_instr('a', 'a', 1, 0)", "OCCURRENCE", 1, "REGEXP_INSTR", 0)]
    [DataRow("regexp_instr('a', 'a', 1, 1, 0, '', -1)", "GROUP", 1, "REGEXP_INSTR", -1)]
    [DataRow("regexp_instr('a', 'a', 1, 1, -1)", "RETURN_OPTION", 0, "REGEXP_INSTR", -1)]
    [DataRow("regexp_instr('a', 'a', 1, 1, 2)", "RETURN_OPTION", 0, "REGEXP_INSTR", 2)]
    [DataRow("regexp_substr('a', 'a', 0)", "START", 1, "REGEXP_SUBSTR", 0)]
    [DataRow("regexp_substr('a', 'a', 1, 0)", "OCCURRENCE", 1, "REGEXP_SUBSTR", 0)]
    [DataRow("regexp_substr('a', 'a', 1, 1, '', -1)", "GROUP", 0, "REGEXP_SUBSTR", -1)]
    public void ArgumentBelowMinimum_Msg19301(string expression, string argument, int reportedMinimum, string function, int provided) =>
        new Simulation().AssertSqlError(
            $"select {expression}",
            19301,
            $"'{argument}' value should be greater than or equal to {reportedMinimum} but '{provided}' is provided in '{function}' function.");

    /// <summary>
    /// Per-function arity, reported as Msg 189 with the lowercase name. The
    /// minimum is 2 for all four.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_count('a', 'a', 2, 'i', 1)", "regexp_count", 4)]
    [DataRow("regexp_instr('a', 'a', 1, 1, 0, 'i', 1, 1)", "regexp_instr", 7)]
    [DataRow("regexp_replace('a', 'a', 'b', 1, 1, 'i', 1)", "regexp_replace", 6)]
    [DataRow("regexp_substr('a', 'a', 1, 1, 'i', 1, 1)", "regexp_substr", 6)]
    public void TooManyArguments_Msg189(string expression, string function, int maximum) =>
        new Simulation().AssertSqlError(
            $"select {expression}",
            189,
            $"The {function} function requires 2 to {maximum} arguments.");

    /// <summary>
    /// The string operands take no implicit conversion — real rejects a
    /// numeric, binary or legacy-LOB operand rather than rendering it, and the
    /// rejection fires even when the value is a typed NULL.
    /// </summary>
    [TestMethod]
    [DataRow("select regexp_count(123, '2')", "int", 1, "regexp_count")]
    [DataRow("select regexp_count('abc', 1)", "int", 2, "regexp_count")]
    [DataRow("select regexp_replace('abc', 'b', 1)", "int", 3, "regexp_replace")]
    [DataRow("select regexp_count(cast(null as int), 'a')", "int", 1, "regexp_count")]
    [DataRow("select regexp_count(cast(0x41 as varbinary(10)), 'A')", "varbinary", 1, "regexp_count")]
    public void InvalidArgumentType_Msg8116(string sql, string typeName, int argument, string function) =>
        new Simulation().AssertSqlError(
            sql,
            8116,
            $"Argument data type {typeName} is invalid for argument {argument} of {function} function.");

    /// <summary>
    /// A start, occurrence, return option or group takes <c>smallint</c>,
    /// <c>int</c>, <c>bigint</c> or a string, and every other type is Msg 8116
    /// while compiling, a typed NULL included; the flags argument takes only a
    /// bounded <c>varchar</c> / <c>char</c>.
    /// </summary>
    [TestMethod]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as tinyint))", "tinyint", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1.0)", "numeric", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as decimal(5, 0)))", "decimal", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as money))", "money", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as float))", "float", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as bit))", "bit", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as datetime))", "datetime", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast('2000-01-01' as date))", "date", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', newid())", "uniqueidentifier", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as sql_variant))", "sql_variant", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', cast(null as tinyint))", "tinyint", 3, "regexp_count")]
    [DataRow("select regexp_count(null, 'b', cast(1 as tinyint))", "tinyint", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', case when 1 = 1 then cast(1 as tinyint) end)", "tinyint", 3, "regexp_count")]
    [DataRow("select regexp_instr('abcb', 'b', 1, cast(1 as bit), 0, 'c', cast(1 as money))", "bit", 4, "regexp_instr")]
    [DataRow("select regexp_instr('abcb', 'b', 1, 1, cast(0 as real))", "real", 5, "regexp_instr")]
    [DataRow("select regexp_instr('abcb', '(b)', 1, 1, 0, 'c', cast(1 as smallmoney))", "smallmoney", 7, "regexp_instr")]
    [DataRow("select regexp_substr('abcb', 'b', 1, cast(1 as tinyint))", "tinyint", 4, "regexp_substr")]
    [DataRow("select regexp_substr('abcb', '(b)', 1, 1, 'c', cast('2000-01-01' as datetime2))", "datetime2", 6, "regexp_substr")]
    [DataRow("select regexp_replace('abcb', 'b', 'z', cast(1 as numeric(5, 2)))", "numeric", 4, "regexp_replace")]
    [DataRow("select regexp_replace('abcb', 'b', 'z', 1, cast('00:00' as time))", "time", 5, "regexp_replace")]
    [DataRow("select regexp_count('abcb', 'b', cast(1 as bit), cast(1 as int))", "bit", 3, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1, cast(null as int))", "int", 4, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1, N'i')", "nvarchar", 4, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1, cast('i' as nchar(1)))", "nchar", 4, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1, cast('i' as nvarchar(max)))", "nvarchar(max)", 4, "regexp_count")]
    [DataRow("select regexp_count('abcb', 'b', 1, cast(null as nvarchar(5)))", "nvarchar", 4, "regexp_count")]
    [DataRow("select regexp_replace('abcb', 'b', 'z', 1, 0, N'i')", "nvarchar", 6, "regexp_replace")]
    [DataRow("select regexp_instr('abcb', 'b', 1, 1, 0, N'i')", "nvarchar", 6, "regexp_instr")]
    [DataRow("select regexp_substr('abcb', 'b', 1, 1, N'i')", "nvarchar", 5, "regexp_substr")]
    [DataRow("select 1 where regexp_like('abc', 'b', N'i')", "nvarchar", 3, "regexp_like")]
    [DataRow("select 1 where regexp_like('abc', 'b', cast('i' as nvarchar(max)))", "nvarchar(max)", 3, "regexp_like")]
    public void ArgumentType_Msg8116(string sql, string typeName, int argument, string function) =>
        new Simulation().AssertSqlError(
            sql,
            8116,
            $"Argument data type {typeName} is invalid for argument {argument} of {function} function.");

    [TestMethod]
    [DataRow("regexp_count('abcb', 'b', cast(3 as smallint))", "1")]
    [DataRow("regexp_count('abcb', 'b', cast(3 as bigint))", "1")]
    [DataRow("regexp_count('abcb', 'b', '3')", "1")]
    [DataRow("regexp_count('abcb', 'b', N'3')", "1")]
    [DataRow("regexp_count('abcb', 'b', cast('3' as varchar(max)))", "1")]
    [DataRow("regexp_count('abcb', 'b', cast(1 as tinyint) + 0)", "2")]
    [DataRow("regexp_count('abcb', 'b', cast(null as int))", null)]
    [DataRow("regexp_count('abcb', 'b', null)", null)]
    [DataRow("regexp_count('abcB', 'b', 1, cast('i' as varchar(8000)))", "2")]
    [DataRow("regexp_count('abcB', 'b', 1, cast('i' as char(1)))", "2")]
    [DataRow("regexp_instr('abcb', 'b', 1, 1, '1')", "3")]
    [DataRow("regexp_instr('abcb', 'b', 1, 1, cast(null as int))", "2")]
    [DataRow("regexp_substr('abcb', '(b)', 1, 1, 'c', '1')", "b")]
    [DataRow("regexp_replace('abcb', 'b', cast('z' as nvarchar(max)))", "azcz")]
    [DataRow("regexp_count(cast('abcb' as nvarchar(max)), 'b')", "2")]
    public void ArgumentType_Accepted(string expression, string? expected) =>
        AreEqual(expected, new Simulation().ExecuteScalar($"select cast({expression} as varchar(10))") as string);

    /// <summary>
    /// A MAX-typed pattern, and a <c>varchar(max)</c> flags argument, are
    /// refused with real's own <c>VARCHAR(MAX)/NVARCHAR(MAX)</c> wording at a
    /// state of each member's own; the rowset members test the flags' MAX
    /// first and report a Unicode one's type error after it.
    /// </summary>
    [TestMethod]
    [DataRow("select regexp_count('abcb', cast('b' as varchar(max)))", 2, "regexp_count", 13)]
    [DataRow("select regexp_count('abcb', cast(N'b' as nvarchar(max)))", 2, "regexp_count", 13)]
    [DataRow("select regexp_count('abcb', 'b', 1, cast('c' as varchar(max)))", 4, "regexp_count", 14)]
    [DataRow("select regexp_replace('abcb', cast('b' as varchar(max)), 'z')", 2, "regexp_replace", 17)]
    [DataRow("select regexp_replace('abcb', 'b', 'z', 1, 0, cast('c' as varchar(max)))", 6, "regexp_replace", 18)]
    [DataRow("select regexp_substr('abcb', cast('b' as varchar(max)))", 2, "regexp_substr", 19)]
    [DataRow("select regexp_substr('abcb', 'b', 1, 1, cast('c' as varchar(max)))", 5, "regexp_substr", 20)]
    [DataRow("select regexp_instr('abcb', cast('b' as varchar(max)))", 2, "regexp_instr", 21)]
    [DataRow("select regexp_instr('abcb', 'b', 1, 1, 0, cast('c' as varchar(max)))", 6, "regexp_instr", 22)]
    [DataRow("select 1 where regexp_like('abc', cast('b' as varchar(max)))", 2, "regexp_like", 15)]
    [DataRow("select 1 where regexp_like('abc', 'b', cast('i' as varchar(max)))", 3, "regexp_like", 16)]
    [DataRow("select * from regexp_matches('abc', cast('b' as varchar(max)))", 2, "regexp_matches", 23)]
    [DataRow("select * from regexp_matches('abc', 'b', cast('i' as varchar(max)))", 3, "regexp_matches", 24)]
    [DataRow("select * from regexp_split_to_table('abc', cast('b' as varchar(max)))", 2, "regexp_split_to_table", 26)]
    [DataRow("select * from regexp_split_to_table('abc', 'b', cast('i' as varchar(max)))", 3, "regexp_split_to_table", 27)]
    public void MaxArgument_Msg8116(string sql, int argument, string function, int state)
    {
        var error = new Simulation().AssertSqlError(sql, 8116);
        AreEqual($"Argument data type VARCHAR(MAX)/NVARCHAR(MAX) is invalid for argument {argument} of {function} function.", error.Errors[0].Message);
        AreEqual((byte)state, error.Errors[0].State);
    }

    [TestMethod]
    [DataRow("select * from regexp_matches(1, 'b')", "int", 1, "regexp_matches", 25)]
    [DataRow("select * from regexp_matches('abc', 'b', N'i')", "nvarchar", 3, "regexp_matches", 25)]
    [DataRow("select * from regexp_split_to_table('abc', 1)", "int", 2, "regexp_split_to_table", 28)]
    [DataRow("select * from regexp_split_to_table('abc', 'b', 1)", "int", 3, "regexp_split_to_table", 28)]
    public void RowsetArgumentType_Msg8116State(string sql, string typeName, int argument, string function, int state)
    {
        var error = new Simulation().AssertSqlError(sql, 8116);
        AreEqual($"Argument data type {typeName} is invalid for argument {argument} of {function} function.", error.Errors[0].Message);
        AreEqual((byte)state, error.Errors[0].State);
    }

    [TestMethod]
    public void RowsetUnicodeMaxFlags_ReportsBoth()
    {
        var error = new Simulation().AssertSqlError("select * from regexp_matches('abc', 'b', cast('i' as nvarchar(max)))", 8116);
        AreEqual(2, error.Errors.Count);
        AreEqual("Argument data type VARCHAR(MAX)/NVARCHAR(MAX) is invalid for argument 3 of regexp_matches function.", error.Errors[0].Message);
        AreEqual("Argument data type nvarchar(max) is invalid for argument 3 of regexp_matches function.", error.Errors[1].Message);
        AreEqual((byte)25, error.Errors[1].State);
    }

    /// <summary>
    /// The argument types are settled while compiling, so an untaken branch,
    /// an empty table and a procedure body at CREATE all raise.
    /// </summary>
    [TestMethod]
    [DataRow("if 1 = 0 select regexp_count('a', 'a', cast(1 as tinyint))")]
    [DataRow("create table t (b tinyint, s varchar(10)); exec('select regexp_count(s, ''a'', b) from t')")]
    [DataRow("create table t (f nvarchar(10), s varchar(10)); exec('select s from t where regexp_like(s, ''a'', f)')")]
    [DataRow("create procedure p as select regexp_count('a', 'a', cast(1 as tinyint))")]
    public void ArgumentType_SettledWhileCompiling(string sql) =>
        new Simulation().AssertSqlError(sql, 8116);

    [TestMethod]
    public void LegacyLobArgument_Msg8116() =>
        new Simulation().AssertSqlError(
            "create table t (c text); insert t values ('a'); select regexp_count(c, 'a') from t",
            8116,
            "Argument data type text is invalid for argument 1 of regexp_count function.");

    /// <summary>
    /// Result types follow the input: <c>REGEXP_REPLACE</c> can grow so it
    /// projects the family container width and truncates silently at it;
    /// <c>REGEXP_SUBSTR</c> can only shrink so it keeps the input's declared
    /// width; MAX carries through unbounded.
    /// </summary>
    [TestMethod]
    [DataRow("len(regexp_replace(replicate('a', 5000), 'a', 'bb'))", 8000)]
    [DataRow("len(regexp_replace(cast(replicate(N'a', 5000) as nvarchar(max)), 'a', 'bb'))", 8000)]
    [DataRow("len(regexp_replace(cast(replicate('a', 5000) as varchar(max)), 'a', 'bb'))", 10000)]
    [DataRow("len(regexp_replace(cast(replicate(N'a', 3000) as nvarchar(4000)), 'a', 'bb'))", 4000)]
    [DataRow("len(regexp_substr(cast(replicate('a', 5000) as varchar(max)), 'a+'))", 5000)]
    public void ResultWidth_Matrix(string expression, int expected) =>
        AreEqual(expected, Convert.ToInt32(new Simulation().ExecuteScalar($"select {expression}"), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Regex matching ignores collation entirely: a case-insensitive column
    /// still matches case-sensitively unless the <c>i</c> flag says otherwise.
    /// </summary>
    [TestMethod]
    public void Collation_DoesNotAffectMatching()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (ci varchar(20) collate SQL_Latin1_General_CP1_CI_AS);
            insert t values ('ABC')
            """);
        AreEqual(0, sim.ExecuteScalar<int>("select regexp_count(ci, 'abc') from t"));
        AreEqual(1, sim.ExecuteScalar<int>("select regexp_count(ci, 'abc', 1, 'i') from t"));
    }

    [TestMethod]
    public void PatternCanVaryPerRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20), p varchar(20));
            insert t values ('abc', 'b'), ('xyz', 'q')
            """);
        AreEqual(1, sim.ExecuteScalar<int>("select sum(regexp_count(v, p)) from t"));
    }

    [TestMethod]
    public void ComputedColumnAndCheckConstraint_Accept()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (v varchar(20), c as regexp_count(v, 'a'),
                            digits varchar(20) check (regexp_like(digits, '^[0-9]+$')));
            insert t (v, digits) values ('aa', '12')
            """);
        AreEqual(2, sim.ExecuteScalar<int>("select c from t"));
        _ = new Simulation().AssertSqlError("""
            create table t (digits varchar(20) check (regexp_like(digits, '^[0-9]+$')));
            insert t values ('x1')
            """, 547);
    }
}

internal static class RegexpTestExtensions
{
    /// <summary>
    /// String-returning companion to <c>ExecuteScalar&lt;T&gt;</c>, which is
    /// constrained to value types.
    /// </summary>
    public static string ExecuteScalarString(this Simulation simulation, string commandText) =>
        Assert.IsInstanceOfType<string>(simulation.ExecuteScalar(commandText));
}
