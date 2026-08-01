using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The trailing <c>FOR JSON { PATH | AUTO } [, ROOT[('name')]]
/// [, INCLUDE_NULL_VALUES] [, WITHOUT_ARRAY_WRAPPER]</c> result serializer.
/// Output strings are probed verbatim against SQL Server 2025 (2026-07-22):
/// PATH nests dotted aliases, options wrap / include-null / drop the array
/// wrapper, every value type has a fixed JSON form (float / real use
/// scientific notation, the date/time types drop an all-zero fraction, strings
/// escape <c>/</c> as <c>\/</c>), nested FOR JSON / JSON_QUERY columns embed as
/// raw JSON, an empty input rowset yields SQL NULL, a duplicate / reopened path
/// raises Msg 13601 and an unnamed column raises Msg 13605.
/// </summary>
[TestClass]
public class ForJsonTests
{
    private const string Fixture =
        "create table u (id int not null primary key, a int); insert u values (1,10),(2,20); " +
        "create table t (id int not null primary key, a int, b int, s varchar(20), n nvarchar(40), d datetime); " +
        "insert t values (1,10,20,'x',N'y','2020-01-01'),(2,30,40,'z',N'w','2021-06-15'),(3,50,60,'q',N'r','2022-03-10'); ";

    private static object? Json(string select) => new Simulation().ExecuteScalar(Fixture + select);

    [TestMethod]
    public void Path_ObjectPerRow_InSelectOrder()
        => AreEqual("[{\"id\":1,\"a\":10,\"s\":\"x\"},{\"id\":2,\"a\":30,\"s\":\"z\"},{\"id\":3,\"a\":50,\"s\":\"q\"}]",
            Json("select id, a, s from t order by id for json path"));

    [TestMethod]
    public void Path_DottedAliasesNest()
        => AreEqual("[{\"x\":{\"id\":1,\"a\":10},\"y\":{\"z\":{\"s\":\"x\"}}}]",
            Json("select id as [x.id], a as [x.a], s as [y.z.s] from t where id=1 for json path"));

    [TestMethod]
    public void Path_ArbitraryDepthNesting()
        => AreEqual("[{\"a\":{\"b\":{\"c\":{\"d\":1}}}}]",
            Json("select id as [a.b.c.d] from t where id=1 for json path"));

    [TestMethod]
    public void Auto_FlatSingleTable_SameAsPath()
        => AreEqual("[{\"id\":1,\"a\":10},{\"id\":2,\"a\":30},{\"id\":3,\"a\":50}]",
            Json("select id, a from t order by id for json auto"));

    [TestMethod]
    public void Auto_DottedAlias_IsLiteralKey()
        => AreEqual("[{\"x.y\":1}]",
            Json("select id as [x.y] from t where id=1 for json auto"));

    [TestMethod]
    public void Root_NoParens_DefaultsToRoot()
        => AreEqual("{\"root\":[{\"id\":1}]}",
            Json("select id from t where id=1 for json path, root"));

    [TestMethod]
    public void Root_Named()
        => AreEqual("{\"data\":[{\"id\":1}]}",
            Json("select id from t where id=1 for json path, root('data')"));

    [TestMethod]
    public void Root_EmptyName()
        => AreEqual("{\"\":[{\"id\":1}]}",
            Json("select id from t where id=1 for json path, root('')"));

    [TestMethod]
    public void IncludeNullValues_EmitsNull()
        => AreEqual("[{\"id\":1,\"x\":null}]",
            Json("select id, null as x from t where id=1 for json path, include_null_values"));

    [TestMethod]
    public void DefaultOmitsNullColumns()
        => AreEqual("[{\"id\":1}]",
            Json("select id, null as x from t where id=1 for json path"));

    [TestMethod]
    public void WithoutArrayWrapper_NoBrackets()
        => AreEqual("{\"id\":1},{\"id\":2},{\"id\":3}",
            Json("select id from t order by id for json path, without_array_wrapper"));

    [TestMethod]
    public void EmptyRowset_YieldsNull()
        => IsNull(Json("select id from t where 1=0 for json path"));

    // Value formatting (each string asserted verbatim against SQL Server 2025).

    [TestMethod]
    public void Format_IntegerFamily()
        => AreEqual("[{\"bi\":5,\"si\":5,\"ti\":5}]",
            Json("select cast(5 as bigint) as bi, cast(5 as smallint) as si, cast(5 as tinyint) as ti for json path"));

    [TestMethod]
    public void Format_DecimalMoneyFloatReal()
        => AreEqual("[{\"dec\":1.50,\"mn\":12.3400,\"sm\":12.3400,\"f\":1.500000000000000e+000,\"r\":1.5000000e+000,\"fz\":0.000000000000000e+000}]",
            Json("select cast(1.5 as decimal(10,2)) as dec, cast(12.34 as money) as mn, cast(12.34 as smallmoney) as sm, "
               + "cast(1.5 as float) as f, cast(1.5 as real) as r, cast(0 as float) as fz for json path"));

    [TestMethod]
    public void Format_Bit()
        => AreEqual("[{\"bt\":true,\"bf\":false}]",
            Json("select cast(1 as bit) as bt, cast(0 as bit) as bf for json path"));

    [TestMethod]
    public void Format_TemporalTypes()
        => AreEqual("[{\"d\":\"2020-01-02\",\"dt2\":\"2020-01-02T03:04:05.1234567\",\"tm\":\"03:04:05.1234567\","
                  + "\"dto\":\"2020-01-02T03:04:05.1234567+05:30\",\"dt\":\"2020-01-02T03:04:05.123\",\"sdt\":\"2020-01-02T03:04:00\"}]",
            Json("select cast('2020-01-02' as date) as d, cast('2020-01-02 03:04:05.1234567' as datetime2(7)) as dt2, "
               + "cast('03:04:05.1234567' as time) as tm, cast('2020-01-02 03:04:05.1234567 +05:30' as datetimeoffset) as dto, "
               + "cast('2020-01-02 03:04:05.123' as datetime) as dt, cast('2020-01-02 03:04:00' as smalldatetime) as sdt for json path"));

    [TestMethod]
    public void Format_DateTimeDropsAllZeroFraction()
        => AreEqual("[{\"id\":1,\"d\":\"2020-01-01T00:00:00\"}]",
            Json("select id, d from t where id=1 for json path"));

    [TestMethod]
    public void Format_DateTimeKeepsNonZeroFractionTrailingZeros()
        => AreEqual("[{\"a\":\"2020-01-02T03:04:05.100\",\"b\":\"2020-01-02T03:04:05.003\"}]",
            Json("select cast('2020-01-02 03:04:05.100' as datetime) as a, cast('2020-01-02 03:04:05.003' as datetime) as b for json path"));

    [TestMethod]
    public void Format_GuidAndBinary()
        => AreEqual("[{\"g\":\"12345678-1234-1234-1234-1234567890AB\",\"b\":\"AQL/\"}]",
            Json("select cast('12345678-1234-1234-1234-1234567890AB' as uniqueidentifier) as g, cast(0x0102FF as varbinary(10)) as b for json path"));

    [TestMethod]
    public void Format_SqlVariant_UnwrapsInner()
        => AreEqual("[{\"v\":5}]", Json("select cast(5 as sql_variant) as v for json path"));

    // String escaping.

    [TestMethod]
    public void Escape_SlashQuoteBackslash()
        => AreEqual("[{\"s\":\"a\\/b\\\"c\\\\d\"}]",
            Json("select 'a/b\"c\\d' as s for json path"));

    [TestMethod]
    public void Escape_ControlChars()
        => AreEqual("[{\"s\":\"a\\b\\t\\n\\f\\r\\u0001\\u001f\"}]",
            Json("select concat('a', nchar(8), nchar(9), nchar(10), nchar(12), nchar(13), nchar(1), nchar(31)) as s for json path"));

    [TestMethod]
    public void Escape_NonAsciiVerbatim()
        => AreEqual("[{\"s\":\"café ア €\"}]", Json("select N'café ア €' as s for json path"));

    [TestMethod]
    public void Escape_XmlForwardSlash()
        => AreEqual("[{\"x\":\"<a>1<\\/a>\"}]", Json("select cast('<a>1</a>' as xml) as x for json path"));

    // Raw embedding of nested JSON producers.

    [TestMethod]
    public void RawEmbed_NestedForJsonSubquery()
        => AreEqual("[{\"id\":1,\"kids\":[{\"a\":10}]}]",
            Json("select t.id, (select u.a from u where u.id=t.id for json path) as kids from t where t.id=1 for json path"));

    [TestMethod]
    public void RawEmbed_JsonQueryColumn()
        => AreEqual("[{\"id\":1,\"arr\":[1,2]}]",
            Json("select id, json_query('[1,2]','$') as arr from t where id=1 for json path"));

    // Nested-object NULL handling.

    [TestMethod]
    public void NestedObject_AllNullOmitted()
        => AreEqual("[{\"id\":1}]",
            Json("select id, null as [x.p], null as [x.q] from t where id=1 for json path"));

    [TestMethod]
    public void NestedObject_AllNullIncluded()
        => AreEqual("[{\"id\":1,\"x\":{\"p\":null,\"q\":null}}]",
            Json("select id, null as [x.p], null as [x.q] from t where id=1 for json path, include_null_values"));

    [TestMethod]
    public void NestedObject_PartialNullOmitted()
        => AreEqual("[{\"x\":{\"p\":1}}]",
            Json("select id as [x.p], null as [x.q] from t where id=1 for json path"));

    [TestMethod]
    public void RootRow_AllNull_EmitsEmptyObject()
        => AreEqual("[{}]", Json("select null as a, null as b from t where id=1 for json path"));

    // Error paths.

    [TestMethod]
    public void UnnamedColumn_Raises13605()
    {
        var ex = new Simulation().AssertSqlError(Fixture + "select id, id+1 from t for json path", 13605);
        Contains("without names or aliases", ex.Message);
    }

    [TestMethod]
    public void DuplicateKey_Raises13601()
    {
        var ex = new Simulation().AssertSqlError(Fixture + "select id as k, a as k from t where id=1 for json path", 13601);
        Contains("Property 'k'", ex.Message);
    }

    [TestMethod]
    public void PrefixConflict_Raises13601()
        => _ = new Simulation().AssertSqlError(Fixture + "select id as a, id as [a.b] from t where id=1 for json path", 13601);

    [TestMethod]
    public void ReopenedObject_Raises13601()
        => _ = new Simulation().AssertSqlError(Fixture + "select id as [x.a], a as [y.b], b as [x.c] from t where id=1 for json path", 13601);

    [TestMethod]
    public void RootWithWithoutArrayWrapper_Raises13620()
        => _ = new Simulation().AssertSqlError(Fixture + "select id from t for json path, root('r'), without_array_wrapper", 13620);

    [TestMethod]
    public void Auto_NoFromClause_Raises13600()
    {
        var ex = new Simulation().AssertSqlError("select 1 as a for json auto", 13600);
        Contains("requires at least one table", ex.Message);
    }

    [TestMethod]
    public void Auto_SetOperation_NotModeled()
        => _ = Throws<NotSupportedException>(
            () => new Simulation().ExecuteScalar(Fixture + "select id from t union all select id from u for json auto"));

    // ---- AUTO join nesting ----

    /// <summary>
    /// The FOR XML AUTO fixture (see <c>ForXmlTests</c>), reused so the two
    /// serializers' nesting matrices sit on the same probed data.
    /// </summary>
    private const string JoinFixture = """
        create table pp (id int, nm nvarchar(20));
        create table cc (id int, pid int, cnm nvarchar(20), amt decimal(9,2));
        create table gg (id int, pid int, gnm nvarchar(20));
        insert pp values (1,'alpha'),(2,'beta'),(3,'gamma'),(4,'alpha');
        insert cc values (10,1,'a1',1.5),(11,1,'a2',2.5),(12,2,'b1',null),(13,4,'d1',9.0);
        insert gg values (100,1,'g1'),(101,1,'g2');
        """;

    private static string JoinJson(string query)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(JoinFixture);
        return (string)sim.ExecuteScalar(query)!;
    }

    [TestMethod]
    public void AutoNesting_SecondTableIsASubArray()
        => AreEqual("""[{"id":1,"nm":"alpha","c":[{"id":10,"cnm":"a1"},{"id":11,"cnm":"a2"}]},{"id":2,"nm":"beta","c":[{"id":12,"cnm":"b1"}]},{"id":4,"nm":"alpha","c":[{"id":13,"cnm":"d1"}]}]""",
            JoinJson("select p.id, p.nm, c.id, c.cnm from pp p join cc c on c.pid=p.id order by p.id, c.id for json auto"));

    [TestMethod]
    public void AutoNesting_ColumnsGroupByTable_NotSelectOrder()
        => AreEqual("""[{"id":1,"nm":"alpha","c":[{"cnm":"a1"},{"cnm":"a2"}]},{"id":2,"nm":"beta","c":[{"cnm":"b1"}]},{"id":4,"nm":"alpha","c":[{"cnm":"d1"}]}]""",
            JoinJson("select p.id, c.cnm, p.nm from pp p join cc c on c.pid=p.id order by p.id, c.id for json auto"));

    [TestMethod]
    public void AutoNesting_ThreeTables_NestLinearly()
        => AreEqual("""[{"id":1,"c":[{"cnm":"a1","g":[{"gnm":"g1"},{"gnm":"g2"}]},{"cnm":"a2","g":[{"gnm":"g1"},{"gnm":"g2"}]}]}]""",
            JoinJson("select p.id, c.cnm, g.gnm from pp p join cc c on c.pid=p.id join gg g on g.pid=p.id order by p.id, c.id, g.id for json auto"));

    [TestMethod]
    public void AutoNesting_OuterJoinNullSide_IsAnEmptyObject()
        => AreEqual("""[{"id":1,"c":[{"cnm":"a1"},{"cnm":"a2"}]},{"id":2,"c":[{"cnm":"b1"}]},{"id":3,"c":[{}]},{"id":4,"c":[{"cnm":"d1"}]}]""",
            JoinJson("select p.id, c.cnm from pp p left join cc c on c.pid=p.id order by p.id, c.id for json auto"));

    [TestMethod]
    public void AutoNesting_IncludeNullValues_ReachesNestedLevels()
        => AreEqual("""[{"id":1,"c":[{"cnm":"a1"},{"cnm":"a2"}]},{"id":2,"c":[{"cnm":"b1"}]},{"id":3,"c":[{"cnm":null}]},{"id":4,"c":[{"cnm":"d1"}]}]""",
            JoinJson("select p.id, c.cnm from pp p left join cc c on c.pid=p.id order by p.id, c.id for json auto, include_null_values"));

    [TestMethod]
    public void AutoNesting_ComputedColumn_JoinsPrecedingTable()
        => AreEqual("""[{"id":1,"c":[{"cnm":"a1","calc":"alphaX"},{"cnm":"a2","calc":"alphaX"}]},{"id":2,"c":[{"cnm":"b1","calc":"betaX"}]},{"id":4,"c":[{"cnm":"d1","calc":"alphaX"}]}]""",
            JoinJson("select p.id, c.cnm, p.nm+'X' as calc from pp p join cc c on c.pid=p.id order by p.id, c.id for json auto"));

    [TestMethod]
    public void AutoNesting_TableWithNoColumns_HasNoLevel()
        => AreEqual("""[{"cnm":"a1"},{"cnm":"a2"},{"cnm":"b1"},{"cnm":"d1"}]""",
            JoinJson("select c.cnm from pp p join cc c on c.pid=p.id order by c.id for json auto"));

    [TestMethod]
    public void AutoNesting_InnermostLevel_NeverCollapses()
        => AreEqual("""[{"id":1,"c":[{"cnm":"a1"},{"cnm":"a1"},{"cnm":"a2"},{"cnm":"a2"}]}]""",
            JoinJson("select p.id, c.cnm from pp p join cc c on c.pid=p.id join gg g on g.pid=1 where p.id=1 order by c.id, g.id for json auto"));

    [TestMethod]
    public void AutoNesting_Root()
        => AreEqual("""{"r":[{"id":1,"c":[{"cnm":"a1"},{"cnm":"a2"}]}]}""",
            JoinJson("select p.id, c.cnm from pp p join cc c on c.pid=p.id where p.id=1 order by c.id for json auto, root('r')"));

    [TestMethod]
    public void AutoNesting_WithoutArrayWrapper_DropsOnlyTheOuterArray()
        => AreEqual("""{"id":1,"c":[{"cnm":"a1"},{"cnm":"a2"}]}""",
            JoinJson("select p.id, c.cnm from pp p join cc c on c.pid=p.id where p.id=1 order by c.id for json auto, without_array_wrapper"));
}
