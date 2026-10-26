using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Alternate / ANSI T-SQL syntaxes SQL Server 2025 accepts or rejects, each
/// probed against the live reference. Bucket A adds accept the form with the
/// correct result value; Bucket B / C tighten rejection to real's Msg number.
/// </summary>
[TestClass]
public sealed class SyntaxAlternativesTests
{
    // ----- Bucket A: || concatenation -----

    [TestMethod]
    public void PipeConcat_TwoStrings_Concatenates()
        => AreEqual("ab", new Simulation().ExecuteScalar("select 'a' || 'b'"));

    [TestMethod]
    public void PipeConcat_SamePrecedenceAsPlus()
        => AreEqual("abc", new Simulation().ExecuteScalar("select 'a' || 'b' + 'c'"));

    [TestMethod]
    public void PipeConcat_ImplicitlyConvertsNumeric()
        => AreEqual("a1", new Simulation().ExecuteScalar("select 'a' || 1"));

    [TestMethod]
    public void PipeConcat_NullYieldsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select 'a' || null"));

    [TestMethod]
    public void PipeConcat_OfConstants_FiltersACatalogView()
        => AreEqual(1, new Simulation().ExecuteScalar("create table ab (a int); select count(*) from sys.objects where name = 'a' || 'b'"));

    [TestMethod]
    public void PipeConcat_TwoNonStrings_RaisesMsg402()
        => _ = new Simulation().AssertSqlError("select 1 || 2", 402);

    [TestMethod]
    public void PipeConcat_BitOperand_RaisesMsg402()
        => _ = new Simulation().AssertSqlError("select 'a' || cast(1 as bit)", 402);

    // ----- Bucket A: ANSI TRIM -----

    [TestMethod]
    public void Trim_Both_StripsSpaces()
        => AreEqual("x", new Simulation().ExecuteScalar("select trim(both ' ' from ' x ')"));

    [TestMethod]
    public void Trim_Leading_StripsLeadingChars()
        => AreEqual("x", new Simulation().ExecuteScalar("select trim(leading '0' from '00x')"));

    [TestMethod]
    public void Trim_Trailing_StripsTrailingChars()
        => AreEqual("x", new Simulation().ExecuteScalar("select trim(trailing '0' from 'x00')"));

    [TestMethod]
    public void Trim_CharsFrom_StripsBothEnds()
        => AreEqual("x", new Simulation().ExecuteScalar("select trim('0' from '0x0')"));

    [TestMethod]
    public void Trim_CharSet_StripsAnyOfTheCharacters()
        => AreEqual("x", new Simulation().ExecuteScalar("select trim('ab' from 'abxba')"));

    // ----- Bucket A: 2-arg LTRIM / RTRIM -----

    [TestMethod]
    public void LeftTrim_TwoArg_StripsGivenChars()
        => AreEqual("x", new Simulation().ExecuteScalar("select ltrim('00x','0')"));

    [TestMethod]
    public void RightTrim_TwoArg_StripsGivenChars()
        => AreEqual("x", new Simulation().ExecuteScalar("select rtrim('x00','0')"));

    // ----- Bucket A: GREATEST / LEAST -----

    [TestMethod]
    public void Greatest_ReturnsMaximum()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("select greatest(1,2,3)"));

    [TestMethod]
    public void Least_ReturnsMinimum()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("select least(5,2,8)"));

    [TestMethod]
    public void Greatest_SkipsNulls()
        => AreEqual(3, new Simulation().ExecuteScalar<int>("select greatest(1,null,3)"));

    [TestMethod]
    public void Greatest_AllNull_ReturnsNull()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("select greatest(null,null)"));

    [TestMethod]
    public void Greatest_MixedNumeric_PromotesResult()
        => AreEqual(2m, new Simulation().ExecuteScalar<decimal>("select greatest(1.5, 2)"));

    // ----- Bucket A: type-name synonyms -----

    [TestMethod]
    public void Synonym_Integer_ResolvesToInt()
        => AreEqual(1, new Simulation().ExecuteScalar<int>("select cast(1 as integer)"));

    [TestMethod]
    public void Synonym_Dec_ResolvesToDecimal()
        => AreEqual(1m, new Simulation().ExecuteScalar<decimal>("select cast(1 as dec(5,2))"));

    [TestMethod]
    public void Synonym_DoublePrecision_ResolvesToFloat()
        => AreEqual(1.0, new Simulation().ExecuteScalar<double>("select cast(1 as double precision)"));

    [TestMethod]
    public void Synonym_CharacterVarying_ResolvesToVarchar()
        => AreEqual("ab", new Simulation().ExecuteScalar("select cast('ab' as character varying(5))"));

    [TestMethod]
    public void Synonym_Character_ResolvesToChar()
        => AreEqual("a    ", new Simulation().ExecuteScalar("select cast('a' as character(5))"));

    [TestMethod]
    public void Synonym_NationalCharacter_ResolvesToNChar()
        => AreEqual("a    ", new Simulation().ExecuteScalar("select cast(N'a' as national character(5))"));

    [TestMethod]
    public void Synonym_NationalCharacterVarying_ResolvesToNVarchar()
        => AreEqual("ab", new Simulation().ExecuteScalar("select cast(N'ab' as national character varying(5))"));

    [TestMethod]
    public void Synonym_InCreateTable_CharacterVaryingColumn()
        => AreEqual("hello", new Simulation().ExecuteScalar("""
            create table zz (s character varying(10));
            insert zz values ('hello');
            select s from zz
            """));

    [TestMethod]
    public void Synonym_InCreateTable_DoublePrecisionColumn()
        => AreEqual(2.5, new Simulation().ExecuteScalar<double>("""
            create table zz (f double precision);
            insert zz values (2.5);
            select f from zz
            """));

    // ----- Bucket A: GROUP BY WITH ROLLUP / CUBE -----

    [TestMethod]
    public void GroupBy_WithRollup_AddsGrandTotalRow()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10);
            select count(*) from (select a, count(*) c from t group by a with rollup) q
            """));

    [TestMethod]
    public void GroupBy_WithCube_MatchesRollupForSingleColumn()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            create table t (id int not null primary key, a int);
            insert t values (1, 10);
            select count(*) from (select a, count(*) c from t group by a with cube) q
            """));

    // ----- Bucket A: INSERT … DEFAULT VALUES -----

    [TestMethod]
    public void Insert_DefaultValues_InsertsOneAllDefaultRow()
        => AreEqual(2, new Simulation().ExecuteScalar<int>("""
            create table u (id int, a int);
            insert u (id, a) values (1, 10);
            insert into u default values;
            select count(*) from u
            """));

    [TestMethod]
    public void Insert_DefaultValues_AppliesColumnDefaults()
        => AreEqual(42, new Simulation().ExecuteScalar<int>("""
            create table u (id int, a int default 42);
            insert into u default values;
            select a from u
            """));

    // ----- Bucket B: reject LIMIT / ADD COLUMN -----

    [TestMethod]
    public void Limit_TrailingClause_RaisesMsg102()
        => _ = new Simulation().AssertSqlError("""
            create table t (id int not null primary key);
            insert t values (1), (2);
            select id from t limit 2
            """, 102);

    // ----- Bucket C: error-number alignment -----

    [TestMethod]
    public void Substring_AnsiFromForForm_RaisesMsg156()
        => _ = new Simulation().AssertSqlError("select substring('abc' from 1 for 2)", 156);

    [TestMethod]
    public void CreateTable_IfNotExists_RaisesMsg156()
        => _ = new Simulation().AssertSqlError("create table if not exists zz (id int)", 156);

    [TestMethod]
    public void GeneratedAlwaysAsIdentity_RaisesMsg156()
        => _ = new Simulation().AssertSqlError("create table zz (id int generated always as identity)", 156);

    // ----- DECLARE ending a batch (no initializer) -----

    [TestMethod]
    public void Declare_BareTypeAtEndOfBatch_Succeeds()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("declare @x int; select @x"));

    [TestMethod]
    public void Declare_AsKeywordAtEndOfBatch_Succeeds()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("declare @x as int; select @x"));

    [TestMethod]
    public void Declare_SizedTypeAtEndOfBatch_Succeeds()
        => AreEqual(DBNull.Value, new Simulation().ExecuteScalar("declare @x varchar(20); select @x"));

    // ----- Bucket A: TOP n PERCENT / WITH TIES -----

    private const string ThreeRowFixture =
        "create table t (id int not null primary key, a int); insert t values (1,10),(2,30),(3,50);";

    [TestMethod]
    public void TopPercent_TenPercentOfThree_ReturnsOneRow()
        => AreEqual(1, new Simulation().ExecuteScalar(
            ThreeRowFixture + "select count(*) from (select top 10 percent id from t) z"));

    [TestMethod]
    public void TopPercent_ThirtyFourPercentOfThree_CeilsToTwoRows()
        => AreEqual(2, new Simulation().ExecuteScalar(
            ThreeRowFixture + "select count(*) from (select top 34 percent id from t) z"));

    [TestMethod]
    public void TopPercent_ZeroPercent_ReturnsNoRows()
        => AreEqual(0, new Simulation().ExecuteScalar(
            ThreeRowFixture + "select count(*) from (select top 0 percent id from t) z"));

    [TestMethod]
    public void TopPercent_OverHundred_RaisesMsg1031()
        => _ = new Simulation().AssertSqlError(ThreeRowFixture + "select top 150 percent id from t", 1031);

    [TestMethod]
    public void TopWithTies_IncludesTiedBoundaryRows()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(
            "create table t (id int not null primary key, a int); insert t values (1,10),(2,10),(3,50);");
        // TOP 1 WITH TIES ORDER BY a — rows 1 and 2 both tie at a = 10.
        AreEqual(2, sim.ExecuteScalar("select count(*) from (select top 1 with ties id from t order by a) z"));
    }

    [TestMethod]
    public void TopWithTies_WithoutOrderBy_RaisesMsg1062()
        => _ = new Simulation().AssertSqlError(ThreeRowFixture + "select top 2 with ties id from t", 1062);

    // ----- Bucket A: WINDOW named-window clause (SQL Server 2022+) -----

    [TestMethod]
    public void NamedWindow_OverW_ResolvesToRunningSum()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(ThreeRowFixture);
        // SUM(a) OVER (ORDER BY id) — running total: 10, 40, 90.
        AreEqual(90, sim.ExecuteScalar(
            "select max(s) from (select sum(a) over w as s from t window w as (order by id)) z"));
    }

    [TestMethod]
    public void NamedWindow_Undefined_RaisesMsg5362()
        => _ = new Simulation().AssertSqlError(ThreeRowFixture + "select sum(a) over w from t", 5362);

    /// <summary>
    /// WINDOW is not reserved: it stays a valid identifier outside the clause.
    /// </summary>
    [TestMethod]
    public void Window_IsContextual_UsableAsColumnName()
        => AreEqual(1, new Simulation().ExecuteScalar("select window from (select 1 as window) z"));

    [TestMethod]
    public void Window_IsContextual_UsableAsTableAlias()
        => AreEqual(3, new Simulation().ExecuteScalar(ThreeRowFixture + "select count(*) from t window"));

    // ----- Bucket B: aggregate FILTER clause is unsupported (Msg 156) -----

    [TestMethod]
    public void AggregateFilterClause_RaisesMsg156()
        => _ = new Simulation().AssertSqlError(ThreeRowFixture + "select count(*) filter (where a > 0) over () from t", 156);

    // ----- Bucket A: TABLESAMPLE (approximate: returns all rows) -----

    [TestMethod]
    public void TableSample_Percent_Accepted()
        => AreEqual(3, new Simulation().ExecuteScalar(ThreeRowFixture + "select count(*) from t tablesample (50 percent)"));

    [TestMethod]
    public void TableSample_SystemRowsRepeatable_Accepted()
        => AreEqual(3, new Simulation().ExecuteScalar(
            ThreeRowFixture + "select count(*) from t tablesample system (2 rows) repeatable (5)"));

    // ----- Bucket A: ODBC escape sequences -----

    [TestMethod]
    public void OdbcDateEscape_YieldsDatetimeMidnight()
        => AreEqual("2020-01-01 00:00:00.000", new Simulation().ExecuteScalar("select convert(varchar(30), {d '2020-01-01'}, 121)"));

    [TestMethod]
    public void OdbcTimestampEscape_YieldsDatetime()
        => AreEqual("2020-01-01 12:00:00.000", new Simulation().ExecuteScalar("select convert(varchar(30), {ts '2020-01-01 12:00:00'}, 121)"));

    /// <summary>
    /// The {t} escape resolves to today's date at the given time (matching
    /// SQL Server); assert the time portion and the date-is-today invariant.
    /// </summary>
    [TestMethod]
    public void OdbcTimeEscape_UsesCurrentDate()
        => AreEqual("12:00:00", new Simulation().ExecuteScalar("select convert(varchar(8), cast({t '12:00:00'} as time), 108)"));

    [TestMethod]
    public void OdbcTimeEscape_DateIsToday()
        => AreEqual(0, new Simulation().ExecuteScalar("select datediff(day, cast({t '12:00:00'} as date), cast(getdate() as date))"));

    [TestMethod]
    public void OdbcGuidEscape_YieldsUniqueIdentifier()
        => AreEqual("6F9619FF-8B86-D011-B42D-00C04FC964FF", new Simulation().ExecuteScalar("select cast({guid '6F9619FF-8B86-D011-B42D-00C04FC964FF'} as varchar(50))"));

    [TestMethod]
    public void OdbcFnUcase_MapsToUpper()
        => AreEqual("ABC", new Simulation().ExecuteScalar("select {fn UCASE('abc')}"));

    [TestMethod]
    public void OdbcFnLcase_MapsToLower()
        => AreEqual("abc", new Simulation().ExecuteScalar("select {fn LCASE('ABC')}"));

    [TestMethod]
    public void OdbcFnLength_MapsToLen()
        => AreEqual(3, new Simulation().ExecuteScalar("select {fn LENGTH('abc')}"));

    [TestMethod]
    public void OdbcFnConcat_PassesThroughToTSqlConcat()
        => AreEqual("ab", new Simulation().ExecuteScalar("select {fn CONCAT('a','b')}"));

    [TestMethod]
    public void OdbcFnNativeName_PassesThrough()
        => AreEqual(5, new Simulation().ExecuteScalar("select {fn ABS(-5)}"));

    /// <summary>
    /// The escape's functions answer as real rewrites them, a name T-SQL
    /// reserves included (probed 2026-10-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("{fn LEFT('abcdef', 2)}", "ab")]
    [DataRow("{fn RIGHT('abcdef', 2)}", "ef")]
    [DataRow("{fn INSERT('abcdef', 2, 3, 'XY')}", "aXYef")]
    [DataRow("{fn MOD(-7, 3)}", "-1")]
    [DataRow("{fn TRUNCATE(-1.567, 1)}", "-1.500")]
    [DataRow("{fn TRUNCATE(156.7, -1)}", "150.0")]
    [DataRow("{fn DAYOFWEEK('2020-01-01')}", "4")]
    [DataRow("{fn DAYOFYEAR('2020-03-01')}", "61")]
    [DataRow("{fn HOUR('2020-01-01 13:45:30')}", "13")]
    [DataRow("{fn MINUTE('2020-01-01 13:45:30')}", "45")]
    [DataRow("{fn SECOND('2020-01-01 13:45:30')}", "30")]
    [DataRow("{fn WEEK('2020-03-01')}", "10")]
    [DataRow("{fn QUARTER('2020-05-01')}", "2")]
    [DataRow("{fn DAYNAME('2020-01-01')}", "Wednesday")]
    [DataRow("{fn MONTHNAME('2020-01-01')}", "January")]
    [DataRow("{fn BIT_LENGTH(N'ab')}", "32")]
    [DataRow("{fn OCTET_LENGTH('ab')}", "2")]
    [DataRow("{fn USER()}", "dbo")]
    [DataRow("{fn DATABASE()}", "simulated")]
    [DataRow("sql_variant_property({fn MOD(7, 3)}, 'BaseType')", "int")]
    public void OdbcFn_AnswersAsRealRewritesIt(string call, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast({call} as varchar(30))"));

    /// <summary>
    /// The clock escapes are text of <c>GETDATE()</c>'s value: the date as
    /// <c>varchar(10)</c>, the time and the timestamp as <c>varchar(30)</c>
    /// with their millisecond digits cut or zero-padded to the precision
    /// (probed 2026-10-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("case when {fn CURDATE()} = convert(varchar(10), getdate(), 23) then 1 else 0 end", "1")]
    [DataRow("case when {fn CURRENT_DATE()} = {fn CURDATE()} then 1 else 0 end", "1")]
    [DataRow("case when {fn CURTIME()} = convert(varchar(12), cast(getdate() as time(3))) then 1 else 0 end", "1")]
    [DataRow("case when {fn CURRENT_TIMESTAMP()} = convert(varchar(30), getdate(), 121) then 1 else 0 end", "1")]
    [DataRow("len({fn CURRENT_TIME(8)})", "17")]
    [DataRow("len({fn CURRENT_TIME(0)})", "8")]
    [DataRow("len({fn CURRENT_TIME(-1)})", "8")]
    [DataRow("len({fn CURRENT_TIME(25)})", "19")]
    [DataRow("len({fn CURRENT_TIME(null)})", "12")]
    [DataRow("len({fn CURRENT_TIMESTAMP(2.7)})", "22")]
    [DataRow("sql_variant_property({fn CURRENT_TIMESTAMP()}, 'MaxLength')", "30")]
    [DataRow("sql_variant_property({fn CURDATE()}, 'MaxLength')", "10")]
    public void OdbcClockFn_IsTextOfGetDate(string expression, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select cast({expression} as varchar(30))"));

    [TestMethod]
    [DataRow("select {fn CURTIME(1)}", 174)]
    [DataRow("select {fn CURRENT_DATE(1)}", 102)]
    public void OdbcClockFn_RefusesAnArgumentItTakesNone(string sql, int number)
        => new Simulation().AssertSqlError(sql, number);

    /// <summary>
    /// ODBC's outer-join escape groups its join chain as parentheses do, and
    /// any other word in its place is Msg 155 (probed 2026-10-26 against SQL
    /// Server 2025).
    /// </summary>
    [TestMethod]
    public void OdbcOuterJoinEscape_GroupsItsJoinChain()
        => AreEqual("1:5:7,2::", new Simulation().ExecuteScalar("""
            create table a (id int primary key); create table b (id int primary key, aid int); create table c (id int primary key, bid int);
            insert a values (1), (2); insert b values (5, 1); insert c values (7, 5);
            select string_agg(concat(a.id, ':', b.id, ':', c.id), ',') within group (order by a.id)
            from {oj a left outer join b on b.aid = a.id} left join c on c.bid = b.id
            """));

    [TestMethod]
    public void OdbcOuterJoinEscape_OtherWord_RaisesMsg155()
        => new Simulation().AssertSqlError("""
            create table a (id int primary key); create table b (id int primary key);
            select a.id from {xx a left join b on 1 = 1}
            """, 155, "'xx' is not a recognized ODBC outer join option.");

    // ----- A boolean predicate in the select list (a value context) -----
    // Real SQL Server reports Msg 156 near the predicate keyword, not the
    // generic Msg 102 (probe-confirmed 2026-07-21).

    [TestMethod]
    public void BarePredicate_LikeInSelectList_RaisesMsg156()
    {
        var ex = new Simulation().AssertSqlError("select 'a' LIKE '[a-z]'", 156);
        Assert.Contains("near the keyword 'LIKE'", ex.Message);
    }

    [TestMethod]
    public void BarePredicate_InInSelectList_RaisesMsg156()
        => new Simulation().AssertSqlError("select 5 IN (1, 2)", 156);

    [TestMethod]
    public void BarePredicate_IsNullInSelectList_RaisesMsg156()
        => new Simulation().AssertSqlError("select 5 IS NULL", 156);

    [TestMethod]
    public void BarePredicate_BetweenInSelectList_RaisesMsg156()
        => new Simulation().AssertSqlError("select 5 BETWEEN 1 AND 2", 156);
}
