namespace SqlServerSimulator;

/// <summary>
/// Direct-SQL coverage for <c>LIKE</c> / <c>NOT LIKE</c> with optional <c>ESCAPE</c>.
/// Trailing-space rule, bracket-class corner cases, and ESCAPE validation reflect
/// real SQL Server 2025 findings.
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
    public void Basic(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    /// <summary>
    /// LIKE doesn't ANSI-pad: pattern's trailing spaces literal, but subject's trailing U+0020 silently accepted.
    /// </summary>
    [TestMethod]
    [DataRow("'abc' like 'abc '", 0)]
    [DataRow("'abc ' like 'abc'", 1)]
    [DataRow("'abc ' like 'abc '", 1)]
    [DataRow("'abc' like 'abc%'", 1)]
    [DataRow("'abc ' like 'abc%'", 1)]
    [DataRow("'   ' like ''", 1)]
    [DataRow("'' like '   '", 0)]
    [DataRow("'   ' like ' '", 1)]
    public void TrailingSpaces(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    /// <summary>Only literal U+0020 counts as a trailing blank.</summary>
    [TestMethod]
    [DataRow("abc\t", "abc", 0)]
    [DataRow("abc\n", "abc", 0)]
    [DataRow("abc\r", "abc", 0)]
    [DataRow("abc\0", "abc", 0)]
    [DataRow("abc\t", "abc%", 1)]
    public void TrailingWhitespace_OnlySpaceCounts(string subject, string pattern, int expectedRows)
        => AssertParameterizedRowCount(subject, pattern, expectedRows);

    [TestMethod]
    [DataRow("a\nb", "a_b", 1)]
    [DataRow("a\rb", "a_b", 1)]
    [DataRow("a\nb", "a%b", 1)]
    [DataRow("\n", "_", 1)]
    [DataRow("\n", "%", 1)]
    public void NewlineCrossing(string subject, string pattern, int expectedRows)
        => AssertParameterizedRowCount(subject, pattern, expectedRows);

    [TestMethod]
    [DataRow("'a' like '[abc]'", 1)]
    [DataRow("'d' like '[abc]'", 0)]
    [DataRow("'a' like '[^abc]'", 0)]
    [DataRow("'d' like '[^abc]'", 1)]
    [DataRow("'5' like '[0-9]'", 1)]
    [DataRow("'a' like '[a-c]'", 1)]
    [DataRow("'B' like '[a-c]'", 1)]    // case-insensitive default
    public void CharacterClasses(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

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
    public void BracketEdgeCases(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'a%' like 'a[%]'", 1)]
    [DataRow("'aX' like 'a[%]'", 0)]
    [DataRow("'a_' like 'a[_]'", 1)]
    public void WildcardsAsLiteralsInClass(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'A' like 'a'", 1)]
    [DataRow("'AbC' like 'aBc'", 1)]
    public void DefaultCollationIsCaseInsensitive(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'a' not like 'b'", 1)]
    [DataRow("'a' not like 'a'", 0)]
    [DataRow("'abc' not like 'a%'", 0)]
    public void NotLike(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("null like 'a'", 0)]
    [DataRow("'a' like null", 0)]
    [DataRow("null like null", 0)]
    [DataRow("null not like 'a'", 0)]
    public void NullOperand_IsUnknown(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("'a%b' like 'a!%b' escape '!'", 1)]
    [DataRow("'aXb' like 'a!%b' escape '!'", 0)]
    [DataRow("'a_b' like 'a!_b' escape '!'", 1)]
    [DataRow("'a[b' like 'a![b' escape '!'", 1)]
    [DataRow("'abc' like 'abc' escape '!'", 1)]                 // unused escape
    [DataRow("'aXb' like 'a!Xb' escape '!'", 1)]                // escape before non-special: literal char
    [DataRow("'a' like 'a!' escape '!'", 0)]                    // trailing escape: literal '!'
    public void EscapeClause(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    public void EscapeClause_MultiCharRaisesMsg506()
    {
        var ex = new Simulation().AssertSqlError("select 1 where 'a' like 'a' escape 'xy'", 506);
        Assert.Contains("invalid escape character", ex.Message);
        Assert.Contains("\"xy\"", ex.Message);
    }

    [TestMethod]
    public void EscapeClause_EmptyRaisesMsg506()
    {
        var ex = new Simulation().AssertSqlError("select 1 where 'a' like 'a' escape ''", 506);
        Assert.Contains("invalid escape character", ex.Message);
    }

    [TestMethod]
    public void NonStringSubject_RaisesOperandTypeClash()
    {
        // int↔string implicit conversion isn't implemented here; CAST required.
        var ex = new Simulation().AssertSqlError("select 1 where 123 like '1%'", 206);
        Assert.Contains("Operand type clash", ex.Message);
    }

    [TestMethod]
    public void LikeAgainstColumn_FiltersRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, name nvarchar(40) );
            insert t values (1, 'apple'), (2, 'banana'), (3, 'apricot'), (4, 'cherry')
            """);

        using var reader = simulation.CreateCommand("select id from t where name like 'a%' order by id").ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 1, 3 }, ids);
    }

    [TestMethod]
    public void LikePatternFromParameter_Works()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, name nvarchar(40) );
            insert t values (1, 'apple'), (2, 'banana')
            """);

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("select id from t where name like @p", ("@p", "ban%"));
        using var reader = command.ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    /// <summary>
    /// Postfix <c>COLLATE</c> on either operand of LIKE flips the regex's
    /// case-folding flag. <c>_CS_</c> and <c>_BIN</c> are case-sensitive;
    /// <c>_CI_</c> stays case-insensitive (matches the default). Probe-
    /// confirmed against SQL Server 2025.
    /// </summary>
    [TestMethod]
    [DataRow("'A' like 'a' collate Latin1_General_CS_AS", 0)]
    [DataRow("'A' like 'A' collate Latin1_General_CS_AS", 1)]
    [DataRow("'a' like 'a' collate Latin1_General_CS_AS", 1)]
    [DataRow("'A' like 'a' collate Latin1_General_CI_AS", 1)]
    [DataRow("'A' like 'a' collate Latin1_General_BIN", 0)]
    [DataRow("'A' like 'A' collate Latin1_General_BIN", 1)]
    [DataRow("'A' like 'a' collate Latin1_General_BIN2", 0)]
    [DataRow("'A' like 'A' collate Latin1_General_BIN2", 1)]
    [DataRow("'A' collate Latin1_General_CS_AS like 'a'", 0)]
    [DataRow("'A' collate Latin1_General_CS_AS like 'A'", 1)]
    [DataRow("'A' collate Latin1_General_CS_AS like 'A' collate Latin1_General_CS_AS", 1)]
    [DataRow("'abc ' like 'abc' collate Latin1_General_CS_AS", 1)]               // trailing-space slack survives CS
    [DataRow("'A' like '[a-z]' collate Latin1_General_CS_AS", 0)]                // character class honors case
    [DataRow("'A' like '[A-Z]' collate Latin1_General_CS_AS", 1)]
    [DataRow("'a%b' like 'a!%b' collate Latin1_General_CS_AS escape '!'", 1)]    // ESCAPE + COLLATE compose either order
    [DataRow("'a%b' like 'a!%b' escape '!' collate Latin1_General_CS_AS", 1)]
    public void Collate(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    [DataRow("null like 'a' collate Latin1_General_CS_AS", 0)]
    [DataRow("'A' collate Latin1_General_CS_AS like null", 0)]
    public void Collate_NullOperand_IsUnknown(string condition, int expectedRows) => AssertRowCount(condition, expectedRows);

    [TestMethod]
    public void Collate_UnknownCollationName_RaisesMsg448()
    {
        var ex = new Simulation().AssertSqlError("select 1 where 'A' like 'a' collate Made_Up_Foo", 448);
        Assert.Contains("Invalid collation 'Made_Up_Foo'.", ex.Message);
    }

    [TestMethod]
    public void Collate_ChainedCollate_RaisesMsg156()
    {
        var ex = new Simulation().AssertSqlError(
            "select 1 where 'A' like 'a' collate Latin1_General_CI_AS collate Latin1_General_CS_AS",
            156);
        Assert.Contains("collate", ex.Message);
    }

    [TestMethod]
    public void Collate_BothSidesDifferentCollation_RaisesMsg468()
    {
        var ex = new Simulation().AssertSqlError(
            "select 1 where 'A' collate Latin1_General_CS_AS like 'a' collate Latin1_General_CI_AS",
            468);
        Assert.Contains("collation conflict", ex.Message);
        Assert.Contains("\"Latin1_General_CS_AS\"", ex.Message);
        Assert.Contains("\"Latin1_General_CI_AS\"", ex.Message);
        Assert.Contains("like", ex.Message);
    }

    [TestMethod]
    public void Collate_NonStringExpression_RaisesMsg447()
    {
        var ex = new Simulation().AssertSqlError("select 1 collate Latin1_General_CS_AS", 447);
        Assert.Contains("invalid for COLLATE clause", ex.Message);
    }

    [TestMethod]
    public void Collate_AgainstColumn_FiltersRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, name nvarchar(40) );
            insert t values (1, 'Apple'), (2, 'apricot'), (3, 'BANANA')
            """);

        using var reader = simulation.CreateCommand(
            "select id from t where name like 'a%' collate Latin1_General_CS_AS order by id")
            .ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        // Only 'apricot' starts with a lowercase 'a' under CS_AS; 'Apple' / 'BANANA' don't.
        CollectionAssert.AreEqual(new[] { 2 }, ids);
    }

    private static void AssertRowCount(string condition, int expectedRows) =>
        Assert.AreEqual(
            expectedRows,
            new Simulation().ExecuteReader($"select 1 where {condition}").EnumerateRecords().Count());

    private static void AssertParameterizedRowCount(string subject, string pattern, int expectedRows)
    {
        // Subject and pattern travel as parameters so non-printables (tab, LF, CR, NUL) reach the predicate intact.
        using var connection = new Simulation().CreateOpenConnection();
        using var command = connection.CreateCommand("select 1 where @s like @p", ("@s", subject), ("@p", pattern));
        using var reader = command.ExecuteReader();
        Assert.AreEqual(expectedRows, reader.EnumerateRecords().Count());
    }
}
