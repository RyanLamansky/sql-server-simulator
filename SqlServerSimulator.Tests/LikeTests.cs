using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Direct-SQL coverage for the <c>LIKE</c> / <c>NOT LIKE</c> predicate with
/// optional <c>ESCAPE</c> clause. Behavior was probed against real SQL Server
/// 2025 before encoding; the trailing-space rule (subject U+0020 leftovers
/// accepted, pattern's must match), bracket-class corner cases, and ESCAPE
/// validation all reflect real-server findings.
/// </summary>
[TestClass]
public class LikeTests
{
    [TestMethod]
    [DataRow("'abc' like 'abc'", 1)]
    [DataRow("'abc' like 'a_c'", 1)]
    [DataRow("'abc' like 'a%c'", 1)]
    [DataRow("'abc' like 'a%'", 1)]
    [DataRow("'abc' like '%c'", 1)]
    [DataRow("'abc' like '%b%'", 1)]
    [DataRow("'aaa' like 'a'", 0)]
    [DataRow("'aaa' like 'a%'", 1)]
    [DataRow("'aaa' like '%a'", 1)]
    [DataRow("'a' like '_'", 1)]
    [DataRow("'' like '_'", 0)]
    [DataRow("'' like '%'", 1)]
    [DataRow("'' like ''", 1)]
    [DataRow("'a' like ''", 0)]
    public void Basic(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    /// <summary>
    /// LIKE does not ANSI-pad like <c>=</c>: the pattern's trailing spaces
    /// are taken literally, but the subject's trailing U+0020 leftovers are
    /// silently accepted after the pattern is exhausted.
    /// </summary>
    [TestMethod]
    [DataRow("'abc' like 'abc '", 0)]      // pattern requires trailing space
    [DataRow("'abc ' like 'abc'", 1)]      // subject's trailing space ignored
    [DataRow("'abc ' like 'abc '", 1)]     // both have trailing space
    [DataRow("'abc' like 'abc%'", 1)]      // % swallows everything
    [DataRow("'abc ' like 'abc%'", 1)]
    [DataRow("'   ' like ''", 1)]          // all-space subject vs empty pattern
    [DataRow("'' like '   '", 0)]          // empty subject can't satisfy 3-space pattern
    [DataRow("'   ' like ' '", 1)]         // 1-space pattern + leftover spaces
    public void TrailingSpaces(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    /// <summary>
    /// Only literal U+0020 counts as a trailing blank; tabs, CR, LF, NUL do not.
    /// </summary>
    [TestMethod]
    [DataRow("abc\t", "abc", 0)]
    [DataRow("abc\n", "abc", 0)]
    [DataRow("abc\r", "abc", 0)]
    [DataRow("abc\0", "abc", 0)]
    [DataRow("abc\t", "abc%", 1)]   // % swallows tab
    public void TrailingWhitespace_OnlySpaceCounts(string subject, string pattern, int expectedRows) =>
        AssertParameterizedRowCount(subject, pattern, expectedRows);

    /// <summary>
    /// <c>_</c> and <c>%</c> both cross newlines; <c>_</c> matches a single
    /// newline character.
    /// </summary>
    [TestMethod]
    [DataRow("a\nb", "a_b", 1)]
    [DataRow("a\rb", "a_b", 1)]
    [DataRow("a\nb", "a%b", 1)]
    [DataRow("\n", "_", 1)]
    [DataRow("\n", "%", 1)]
    public void NewlineCrossing(string subject, string pattern, int expectedRows) =>
        AssertParameterizedRowCount(subject, pattern, expectedRows);

    [TestMethod]
    [DataRow("'a' like '[abc]'", 1)]
    [DataRow("'d' like '[abc]'", 0)]
    [DataRow("'a' like '[^abc]'", 0)]
    [DataRow("'d' like '[^abc]'", 1)]
    [DataRow("'5' like '[0-9]'", 1)]
    [DataRow("'a' like '[a-c]'", 1)]
    [DataRow("'B' like '[a-c]'", 1)] // case-insensitive default
    public void CharacterClasses(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    /// <summary>
    /// Bracket parsing edge cases verified against real SQL Server 2025:
    /// leading/trailing hyphens are literal; <c>[]</c> is empty (never
    /// matches); reversed ranges (<c>[c-a]</c>) never match; unterminated
    /// <c>[</c> never matches; <c>[^]</c> matches any single char.
    /// </summary>
    [TestMethod]
    [DataRow("'-' like '[-a]'", 1)]
    [DataRow("'a' like '[a-]'", 1)]
    [DataRow("'-' like '[a-]'", 1)]
    [DataRow("'[' like '[[]'", 1)]
    [DataRow("'a' like '[]'", 0)]
    [DataRow("'a' like '[]a'", 0)]
    [DataRow("']' like ']'", 1)]
    [DataRow("'[' like '['", 0)]
    [DataRow("'a' like '[abc'", 0)]
    [DataRow("'b' like '[c-a]'", 0)]
    [DataRow("'a' like '[^]'", 1)]
    [DataRow("'^' like '[^]'", 1)]
    public void BracketEdgeCases(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    /// <summary>
    /// Wildcards inside a class are taken literally — the documented way to
    /// match a literal <c>%</c> or <c>_</c> without using ESCAPE.
    /// </summary>
    [TestMethod]
    [DataRow("'a%' like 'a[%]'", 1)]
    [DataRow("'aX' like 'a[%]'", 0)]
    [DataRow("'a_' like 'a[_]'", 1)]
    public void WildcardsAsLiteralsInClass(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'A' like 'a'", 1)]
    [DataRow("'AbC' like 'aBc'", 1)]
    public void DefaultCollationIsCaseInsensitive(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'a' not like 'b'", 1)]
    [DataRow("'a' not like 'a'", 0)]
    [DataRow("'abc' not like 'a%'", 0)]
    public void NotLike(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    /// <summary>
    /// Any NULL operand makes LIKE / NOT LIKE evaluate to UNKNOWN, which
    /// drops the row from a WHERE filter.
    /// </summary>
    [TestMethod]
    [DataRow("null like 'a'", 0)]
    [DataRow("'a' like null", 0)]
    [DataRow("null like null", 0)]
    [DataRow("null not like 'a'", 0)]
    public void NullOperand_IsUnknown(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'a%b' like 'a!%b' escape '!'", 1)]
    [DataRow("'aXb' like 'a!%b' escape '!'", 0)]
    [DataRow("'a_b' like 'a!_b' escape '!'", 1)]
    [DataRow("'a[b' like 'a![b' escape '!'", 1)]
    [DataRow("'abc' like 'abc' escape '!'", 1)]                 // unused escape
    [DataRow("'aXb' like 'a!Xb' escape '!'", 1)]                // escape before non-special: takes char as literal
    [DataRow("'a' like 'a!' escape '!'", 0)]                    // trailing escape: literal '!' after 'a' makes 2-char pattern
    public void EscapeClause(string condition, int expectedRows) =>
        AssertRowCount(condition, expectedRows);

    [TestMethod]
    public void EscapeClause_MultiCharRaisesMsg506()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteScalar("select 1 where 'a' like 'a' escape 'xy'"));
        Assert.Contains("invalid escape character", ex.Message);
        Assert.Contains("\"xy\"", ex.Message);
    }

    [TestMethod]
    public void EscapeClause_EmptyRaisesMsg506()
    {
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteScalar("select 1 where 'a' like 'a' escape ''"));
        Assert.Contains("invalid escape character", ex.Message);
    }

    [TestMethod]
    public void NonStringSubject_RaisesOperandTypeClash()
    {
        // The simulator's general gap — int↔string implicit conversion isn't
        // implemented. Real SQL Server auto-converts; here a CAST is required.
        var simulation = new Simulation();
        var ex = Assert.Throws<DbException>(() =>
            simulation.ExecuteScalar("select 1 where 123 like '1%'"));
        Assert.Contains("Operand type clash", ex.Message);
    }

    [TestMethod]
    public void LikeAgainstColumn_FiltersRows()
    {
        // Real-world use: LIKE against a varchar column from a table.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, name nvarchar(40) )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'apple'), (2, 'banana'), (3, 'apricot'), (4, 'cherry')");

        using var reader = simulation
            .CreateCommand("select id from t where name like 'a%' order by id")
            .ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    [TestMethod]
    public void LikePatternFromParameter_Works()
    {
        // EF Core parameterizes the pattern; verify the pattern can come from
        // a SqlParameter rather than a literal.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, name nvarchar(40) )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'apple'), (2, 'banana')");

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("select id from t where name like @p", ("@p", "ban%"));
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    private static void AssertRowCount(string condition, int expectedRows) =>
        Assert.AreEqual(
            expectedRows,
            new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    /// <summary>
    /// Subject and pattern travel as parameters so non-printable / control
    /// characters (tab, LF, CR, NUL) reach the predicate intact rather than
    /// requiring the unimplemented <c>CHAR(N)</c> built-in.
    /// </summary>
    private static void AssertParameterizedRowCount(string subject, string pattern, int expectedRows)
    {
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1 where @s like @p", ("@s", subject), ("@p", pattern));
        using var reader = command.ExecuteReader();
        Assert.AreEqual(expectedRows, reader.EnumerateRecords().Count());
    }
}
