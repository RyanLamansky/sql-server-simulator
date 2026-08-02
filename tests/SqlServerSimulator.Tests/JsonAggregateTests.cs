using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>JSON_ARRAYAGG</c> / <c>JSON_OBJECTAGG</c> aggregate + window forms.
/// Output strings and null-clause defaults probed verbatim against SQL Server
/// 2025 (2026-05-27): <c>JSON_ARRAYAGG</c> defaults to <c>ABSENT ON NULL</c>,
/// but <c>JSON_OBJECTAGG</c> defaults to <c>NULL ON NULL</c>. A group of zero
/// rows yields SQL NULL; a group whose values are all absent yields the empty
/// container. <c>JSON_ARRAYAGG</c> takes an in-parens <c>ORDER BY</c> (mutually
/// exclusive with <c>OVER</c>); <c>JSON_OBJECTAGG</c> takes neither an
/// <c>ORDER BY</c> nor the <c>key VALUE value</c> form. Both support
/// <c>OVER</c> windows including running frames.
/// </summary>
[TestClass]
public class JsonAggregateTests
{
    // Four rows; one (id 3) has a NULL value, and key 'a' repeats (ids 1 + 4)
    // so grouping / duplicate-key behavior is exercised.
    private const string Seed = """
        create table #t (id int, k nvarchar(50), v nvarchar(50), n int);
        insert into #t values (1,'a','apple',10),(2,'b','banana',20),(3,'c',null,null),(4,'a','avocado',30);
        """;

    private static object? Scalar(string query) => new Simulation().ExecuteScalar(Seed + "\n" + query);

    // --- JSON_ARRAYAGG, grouped/scalar form ---

    [TestMethod]
    public void ArrayAgg_Basic_AbsentOnNullDefault()
        => AreEqual("[\"apple\",\"banana\",\"avocado\"]", Scalar("select json_arrayagg(v) from #t"));

    [TestMethod]
    public void ArrayAgg_NumericValues_Unquoted()
        => AreEqual("[10,20,30]", Scalar("select json_arrayagg(n) from #t"));

    [TestMethod]
    public void ArrayAgg_OrderByDescending()
        => AreEqual("[\"avocado\",\"banana\",\"apple\"]", Scalar("select json_arrayagg(v order by id desc) from #t"));

    [TestMethod]
    public void ArrayAgg_NullOnNull_KeepsNullInOrder()
        => AreEqual("[\"apple\",\"banana\",null,\"avocado\"]",
            Scalar("select json_arrayagg(v order by id null on null) from #t"));

    [TestMethod]
    public void ArrayAgg_EmptyInput_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select json_arrayagg(v) from #t where 1=0"));

    [TestMethod]
    public void ArrayAgg_Grouped_AllNullGroupYieldsEmptyArray()
        => AreEqual("[\"apple\",\"avocado\"]||[\"banana\"]||[]",
            Scalar("select string_agg(r, '||') within group (order by k) from (select k, json_arrayagg(v) r from #t group by k) z"));

    // --- JSON_OBJECTAGG, grouped/scalar form ---

    [TestMethod]
    public void ObjectAgg_Basic_NullOnNullDefault_KeepsNullAndDuplicateKeys()
        => AreEqual("{\"a\":\"apple\",\"b\":\"banana\",\"c\":null,\"a\":\"avocado\"}",
            Scalar("select json_objectagg(k:v) from #t"));

    [TestMethod]
    public void ObjectAgg_AbsentOnNull_OmitsNullPair()
        => AreEqual("{\"a\":\"apple\",\"b\":\"banana\",\"a\":\"avocado\"}",
            Scalar("select json_objectagg(k:v absent on null) from #t"));

    [TestMethod]
    public void ObjectAgg_EmptyInput_ReturnsNull()
        => AreEqual(DBNull.Value, Scalar("select json_objectagg(k:v) from #t where 1=0"));

    [TestMethod]
    public void ObjectAgg_Grouped_PerGroupObjects()
        => AreEqual("{\"a\":\"apple\",\"a\":\"avocado\"}||{\"b\":\"banana\"}||{\"c\":null}",
            Scalar("select string_agg(r, '||') within group (order by k) from (select k, json_objectagg(k:v) r from #t group by k) z"));

    [TestMethod]
    public void ObjectAgg_NullKey_RaisesMsg13638()
        => new Simulation().AssertSqlError(Seed + "\nselect json_objectagg(nullif(k,'a'):v) from #t", 13638);

    // --- nested raw embedding ---

    [TestMethod]
    public void ArrayAgg_OfJsonObject_EmbedsRaw()
        => AreEqual(
            "[{\"k\":\"a\",\"v\":\"apple\"},{\"k\":\"b\",\"v\":\"banana\"},{\"k\":\"c\",\"v\":null},{\"k\":\"a\",\"v\":\"avocado\"}]",
            Scalar("select json_arrayagg(json_object('k':k,'v':v null on null) order by id) from #t"));

    // --- window forms ---

    [TestMethod]
    public void ArrayAgg_Over_PartitionBy_Broadcast()
        => AreEqual("[\"apple\",\"avocado\"]||[\"banana\"]||[]||[\"apple\",\"avocado\"]",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_arrayagg(v) over(partition by k) r from #t) z"));

    [TestMethod]
    public void ArrayAgg_Over_PartitionOrderBy_RunningFrame()
        => AreEqual("[\"apple\"]||[\"banana\"]||[]||[\"apple\",\"avocado\"]",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_arrayagg(v) over(partition by k order by id) r from #t) z"));

    [TestMethod]
    public void ArrayAgg_Over_OrderByNoPartition_Running()
        => AreEqual("[\"apple\"]||[\"apple\",\"banana\"]||[\"apple\",\"banana\"]||[\"apple\",\"banana\",\"avocado\"]",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_arrayagg(v) over(order by id) r from #t) z"));

    [TestMethod]
    public void ArrayAgg_Over_ExplicitRowsFrame()
        => AreEqual("[\"apple\"]||[\"apple\",\"banana\"]||[\"banana\"]||[\"avocado\"]",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_arrayagg(v) over(order by id rows between 1 preceding and current row) r from #t) z"));

    [TestMethod]
    public void ObjectAgg_Over_PartitionBy_Broadcast()
        => AreEqual("{\"a\":\"apple\",\"a\":\"avocado\"}||{\"b\":\"banana\"}||{\"c\":null}||{\"a\":\"apple\",\"a\":\"avocado\"}",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_objectagg(k:v) over(partition by k) r from #t) z"));

    [TestMethod]
    public void ObjectAgg_Over_PartitionOrderBy_RunningFrame()
        => AreEqual("{\"a\":\"apple\"}||{\"b\":\"banana\"}||{\"c\":null}||{\"a\":\"apple\",\"a\":\"avocado\"}",
            Scalar("select string_agg(r, '||') within group (order by id) from (select id, json_objectagg(k:v) over(partition by k order by id) r from #t) z"));

    [TestMethod]
    public void ArrayAgg_InParensOrderByWithOver_Rejected()
        => new Simulation().AssertSqlError(Seed + "\nselect json_arrayagg(v order by id) over() from #t", 156);

    [TestMethod]
    public void ObjectAgg_OrderBy_Rejected()
        => new Simulation().AssertSqlError(Seed + "\nselect json_objectagg(k:v order by id) from #t", 156);

    [TestMethod]
    public void ObjectAgg_ValueKeyword_Rejected()
        => new Simulation().AssertSqlError(Seed + "\nselect json_objectagg(k value v) from #t", 102);

    /// <summary>
    /// Both aggregates escape <c>/</c> as <c>\/</c> the way the scalar
    /// builders do, keys included (probe-confirmed against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("json_arrayagg(s)", """["a\/b","c\/d"]""")]
    [DataRow("json_objectagg(s:s)", """{"a\/b":"a\/b","c\/d":"c\/d"}""")]
    public void Aggregates_EscapeSolidus(string aggregate, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(
            $"create table #s (id int, s nvarchar(20)); insert into #s values (1,'a/b'),(2,'c/d');\nselect {aggregate} from #s"));
}
