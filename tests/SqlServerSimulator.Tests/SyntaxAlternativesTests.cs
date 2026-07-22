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
        => AreEqual("a    ", new Simulation().ExecuteScalar("select cast(n'a' as national character(5))"));

    [TestMethod]
    public void Synonym_NationalCharacterVarying_ResolvesToNVarchar()
        => AreEqual("ab", new Simulation().ExecuteScalar("select cast(n'ab' as national character varying(5))"));

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
}
