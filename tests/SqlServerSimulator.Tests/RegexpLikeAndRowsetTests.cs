using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>REGEXP_LIKE</c> predicate — including the compatibility-level 170
/// keyword reservation that makes <c>dbo.REGEXP_LIKE(...)</c> a syntax error
/// there — and the two rowset members <c>REGEXP_MATCHES</c> /
/// <c>REGEXP_SPLIT_TO_TABLE</c>. Probed against a live SQL Server 2025
/// (17.0.4065.4) reference instance.
/// </summary>
[TestClass]
public sealed class RegexpLikeAndRowsetTests
{
    [TestMethod]
    [DataRow("regexp_like('abc', 'a.c')", 1)]
    [DataRow("regexp_like('ABC', 'a.c', 'i')", 1)]
    [DataRow("regexp_like('ABC', 'a.c')", 0)]
    [DataRow("regexp_like('abc', 'a.c') and 1 = 1", 1)]
    // NULL yields UNKNOWN, so neither the predicate nor its negation passes.
    [DataRow("regexp_like(null, 'a')", 0)]
    [DataRow("not regexp_like(null, 'a')", 0)]
    [DataRow("regexp_like('a', null)", 0)]
    [DataRow("regexp_like('a', 'a', null)", 0)]
    public void Predicate_Matrix(string predicate, int expectedRows) =>
        AreEqual(expectedRows, new Simulation().ExecuteScalar<int>(
            $"select count(*) from (select 1 as n) q where {predicate}"));

    [TestMethod]
    public void Predicate_InHaving() =>
        AreEqual(1, new Simulation().ExecuteScalar<int>(
            "select count(*) from (select 1 as n) q having regexp_like('abc', 'abc')"));

    [TestMethod]
    public void Predicate_InIfAndCase()
    {
        var sim = new Simulation();
        AreEqual("y", sim.ExecuteScalarString("select case when regexp_like('abc', 'a.c') then 'y' else 'n' end"));
        AreEqual("n", sim.ExecuteScalarString("select case when regexp_like('a', null) then 'y' else 'n' end"));
        AreEqual(1, sim.ExecuteScalar<int>("declare @r int = 0; if regexp_like('abc', 'a.c') set @r = 1; select @r"));
    }

    /// <summary>
    /// Boolean-only: real raises Msg 156 for the construct in scalar position,
    /// so modeling it as a bit-returning built-in would accept a shape real
    /// rejects.
    /// </summary>
    [TestMethod]
    public void Predicate_InScalarPosition_Msg156() =>
        new Simulation().AssertSqlError(
            "select REGEXP_LIKE('abc', 'a.c') as x",
            156,
            "Incorrect syntax near the keyword 'REGEXP_LIKE'.");

    /// <summary>
    /// Arity is enforced by the grammar, not by Msg 189 — real reports Msg 102
    /// near the offending token, unlike the four scalars.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 where regexp_like('ABC', 'a.c', 'i', 1)", ",")]
    [DataRow("select 1 where regexp_like('ABC')", ")")]
    public void Predicate_WrongArity_Msg102(string sql, string near) =>
        new Simulation().AssertSqlError(sql, 102, $"Incorrect syntax near '{near}'.");

    [TestMethod]
    public void Predicate_InvalidFlagAndPattern()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("select 1 where regexp_like('a', 'a', 'z')", 19303);
        _ = sim.AssertSqlError("select 1 where regexp_like('a', '(')", 19308);
    }

    // ---- compatibility-level 170 reservation --------------------------------

    /// <summary>
    /// <c>REGEXP_LIKE</c> is reserved at compatibility level 170 and only
    /// there. The unbracketed <c>dbo.REGEXP_LIKE(...)</c> spelling mssql-django
    /// installs as a CLR UDF therefore fails to parse at 170 and resolves
    /// normally at 160 — the behavior real has, and the reason the escape hatch
    /// is a bracketed name.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 as REGEXP_LIKE")]
    [DataRow("select dbo.REGEXP_LIKE('a', 'a')")]
    [DataRow("create table REGEXP_LIKE (a int)")]
    public void Reserved_AtCompat170(string sql) =>
        new Simulation().AssertSqlError(sql, 156, "Incorrect syntax near the keyword 'REGEXP_LIKE'.");

    [TestMethod]
    public void NotReserved_AtCompat160()
    {
        var sim = AtCompatibilityLevel(160);
        AreEqual(1, sim.ExecuteScalar<int>("select 1 as REGEXP_LIKE"));
        AreEqual(1, sim.ExecuteScalar<int>("declare @REGEXP_LIKE int = 1; select @REGEXP_LIKE"));
        // The name resolves as an ordinary two-part function name, so the miss
        // is the function-not-found error rather than a syntax error.
        _ = sim.AssertSqlError("select dbo.REGEXP_LIKE('a', 'a')", 4121);
    }

    /// <summary>
    /// Bracketing (or double-quoting) the name is the escape hatch at 170, and
    /// the reservation covers only <c>REGEXP_LIKE</c> — the other six names
    /// stay usable as identifiers.
    /// </summary>
    [TestMethod]
    [DataRow("select 1 as [REGEXP_LIKE]")]
    [DataRow("select 1 as \"REGEXP_LIKE\"")]
    [DataRow("declare @REGEXP_LIKE int = 1; select @REGEXP_LIKE")]
    [DataRow("select 1 as REGEXP_COUNT")]
    [DataRow("select 1 as REGEXP_INSTR")]
    [DataRow("select 1 as REGEXP_REPLACE")]
    [DataRow("select 1 as REGEXP_SUBSTR")]
    [DataRow("select 1 as REGEXP_MATCHES")]
    [DataRow("select 1 as REGEXP_SPLIT_TO_TABLE")]
    public void ReservationScope_AtCompat170(string sql) =>
        AreEqual(1, new Simulation().ExecuteScalar<int>(sql));

    /// <summary>
    /// The predicate and the two rowset members ship only at 170; the four
    /// scalars carry no compatibility gate at all.
    /// </summary>
    [TestMethod]
    public void MemberAvailability_ByCompatibilityLevel()
    {
        var sim = AtCompatibilityLevel(160);
        new Simulation().AssertSqlError(
            "alter database master set compatibility_level = 160; select 1 where REGEXP_LIKE('a', 'a')",
            195,
            "'REGEXP_LIKE' is not a recognized built-in function name.");
        _ = sim.AssertSqlError("select * from regexp_matches('a', 'a')", 208);
        _ = sim.AssertSqlError("select * from regexp_split_to_table('a', 'a')", 208);
        AreEqual(1, sim.ExecuteScalar<int>("select regexp_count('a', 'a')"));
        AreEqual(1, sim.ExecuteScalar<int>("select regexp_instr('a', 'a')"));
        AreEqual("b", sim.ExecuteScalarString("select regexp_replace('a', 'a', 'b')"));
        AreEqual("a", sim.ExecuteScalarString("select regexp_substr('a', 'a')"));
    }

    // ---- REGEXP_MATCHES -----------------------------------------------------

    [TestMethod]
    public void Matches_ProjectsPositionsAndGroupJson()
    {
        var sim = new Simulation();
        using var reader = sim.ExecuteReader("select * from regexp_matches('abcABC', 'a(b)(c)', 'i')");
        AreEqual(5, reader.FieldCount);
        AreEqual("match_id", reader.GetName(0));
        AreEqual("start_position", reader.GetName(1));
        AreEqual("end_position", reader.GetName(2));
        AreEqual("match_value", reader.GetName(3));
        AreEqual("substring_matches", reader.GetName(4));

        IsTrue(reader.Read());
        AreEqual(1L, reader.GetInt64(0));
        AreEqual(1, reader.GetInt32(1));
        AreEqual(3, reader.GetInt32(2));
        AreEqual("abc", reader.GetString(3));
        AreEqual("""[{"value":"b","start":2,"length":1},{"value":"c","start":3,"length":1}]""", reader.GetString(4));

        IsTrue(reader.Read());
        AreEqual(2L, reader.GetInt64(0));
        AreEqual(4, reader.GetInt32(1));
        AreEqual(6, reader.GetInt32(2));
        AreEqual("ABC", reader.GetString(3));
        IsFalse(reader.Read());
    }

    /// <summary>
    /// A pattern with no capture groups reports the whole match as the single
    /// <c>substring_matches</c> entry, and a group that didn't participate
    /// reports null members.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_matches('abc', 'b')", """[{"value":"b","start":2,"length":1}]""")]
    [DataRow("regexp_matches('abc', '(?:b)')", """[{"value":"b","start":2,"length":1}]""")]
    [DataRow("regexp_matches('ab', '(x)?(b)')", """[{"value":null,"start":null,"length":null},{"value":"b","start":2,"length":1}]""")]
    [DataRow("""regexp_matches('a"b', '(a"b)')""", """[{"value":"a\"b","start":1,"length":3}]""")]
    public void Matches_SubstringMatchesJson(string source, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString($"select substring_matches from {source}"));

    /// <summary>
    /// A zero-width match reports the same value for both positions, clamped to
    /// the input's length — so the trailing empty match on <c>'aa'</c> reports
    /// 2 rather than 3, and <c>('', '')</c> reports 0.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_matches('aa', 'a*')", "1:1-2:aa|2:2-2:")]
    [DataRow("regexp_matches('', '')", "1:0-0:")]
    [DataRow("regexp_matches('abc', 'x*')", "1:1-1:|2:2-2:|3:3-3:|4:3-3:")]
    [DataRow("regexp_matches('ab', 'b*')", "1:1-1:|2:2-2:b|3:2-2:")]
    [DataRow("regexp_matches('aXbXc', 'X*')", "1:1-1:|2:2-2:X|3:3-3:|4:4-4:X|5:5-5:|6:5-5:")]
    public void Matches_ZeroWidthPositions(string source, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString($"""
            select string_agg(concat(match_id, ':', start_position, '-', end_position, ':', match_value), '|')
            from {source}
            """));

    [TestMethod]
    [DataRow("regexp_matches(null, 'a')")]
    [DataRow("regexp_matches('a', null)")]
    [DataRow("regexp_matches('a', 'a', null)")]
    [DataRow("regexp_matches('abc', 'x')")]
    [DataRow("regexp_split_to_table(null, ',')")]
    [DataRow("regexp_split_to_table('a', null)")]
    [DataRow("regexp_split_to_table('a,b', ',', null)")]
    public void Rowset_NullArgument_YieldsNoRows(string source) =>
        AreEqual(0, new Simulation().ExecuteScalar<int>($"select count(*) from {source} t"));

    // ---- REGEXP_SPLIT_TO_TABLE ---------------------------------------------

    /// <summary>
    /// The split runs on a <i>different</i> match enumeration than the scalars:
    /// a zero-width match landing exactly where the previous one ended is
    /// discarded. That single rule is why <c>REGEXP_COUNT('aXbXc', 'X*')</c> is
    /// 6 while the same pattern splits into just three segments.
    /// </summary>
    [TestMethod]
    [DataRow("regexp_split_to_table('a,b,c', ',')", "a|b|c")]
    [DataRow("regexp_split_to_table('aXbxc', 'x', 'i')", "a|b|c")]
    [DataRow("regexp_split_to_table('abc', '')", "a|b|c")]
    [DataRow("regexp_split_to_table(',a,', ',')", "|a|")]
    [DataRow("regexp_split_to_table('a', 'z')", "a")]
    [DataRow("regexp_split_to_table('a1b22c', '[0-9]+')", "a|b|c")]
    [DataRow("regexp_split_to_table('aXa', 'X*')", "a|a")]
    [DataRow("regexp_split_to_table('aXbXc', 'X*')", "a|b|c")]
    [DataRow("regexp_split_to_table('aa', 'a*')", "|")]
    public void Split_Matrix(string source, string expected) =>
        AreEqual(expected, new Simulation().ExecuteScalarString(
            $"select string_agg(value, '|') within group (order by ordinal) from {source}"));

    [TestMethod]
    public void Split_ProjectsOrdinal() =>
        AreEqual("b", new Simulation().ExecuteScalarString(
            "select t.value from regexp_split_to_table('a,b', ',') t where t.ordinal = 2"));

    /// <summary>
    /// The rowset members report arity as a table-valued function's — Msg 313 /
    /// Msg 8144 at state 3 — rather than the scalars' Msg 189.
    /// </summary>
    [TestMethod]
    [DataRow("select * from regexp_split_to_table('a')", 313, "An insufficient number of arguments were supplied for the procedure or function REGEXP_SPLIT_TO_TABLE.")]
    [DataRow("select * from regexp_split_to_table('a,b', ',', 'i', 1)", 8144, "Procedure or function REGEXP_SPLIT_TO_TABLE has too many arguments specified.")]
    [DataRow("select * from regexp_matches('a', 'a', 'i', 1)", 8144, "Procedure or function REGEXP_MATCHES has too many arguments specified.")]
    public void Rowset_WrongArity(string sql, int number, string message) =>
        new Simulation().AssertSqlError(sql, number, message);

    [TestMethod]
    public void Rowset_ComposesWithApply()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, csv varchar(50));
            insert t values (1, 'a,b'), (2, 'c')
            """);
        AreEqual("a|b|c", sim.ExecuteScalarString("""
            select string_agg(s.value, '|') within group (order by t.id, s.ordinal)
            from t cross apply regexp_split_to_table(t.csv, ',') s
            """));
    }

    /// <summary>
    /// A fresh simulation whose default database sits at
    /// <paramref name="level"/>.
    /// </summary>
    private static Simulation AtCompatibilityLevel(int level)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery($"alter database current set compatibility_level = {level}");
        return sim;
    }
}
