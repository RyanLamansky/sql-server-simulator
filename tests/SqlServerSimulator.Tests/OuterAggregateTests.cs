using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An aggregate whose operand reads only an enclosing query's columns belongs to
/// that query, wherever it is written, and the clauses that can own no aggregate
/// refuse one moved to them. Each expectation is real's answer for the same text
/// (probed 2026-09-28 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class OuterAggregateTests
{
    private const string Setup = """
        create table t (a int, b int); insert t values (1, 10), (2, 20), (3, 30);
        create table u (c int, d int); insert u values (1, 5);
        """;

    private static Simulation Fixture()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        return simulation;
    }

    /// <summary>Every row of <paramref name="sql"/>'s result, each rendered as its columns joined by <c>|</c>, rows joined by a space.</summary>
    private static string Rows(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture))));
        return string.Join(' ', rows);
    }

    /// <summary>
    /// A subquery reading the owning query's per-group value re-runs per group
    /// rather than replaying its first execution.
    /// </summary>
    [TestMethod]
    [DataRow("select b, (select sum(t.a) from u) from t group by b", "10|1 20|2 30|3")]
    [DataRow("select b, (select m from (select max(t.a) m from u) d) from t group by b", "10|1 20|2 30|3")]
    [DataRow("select b, (select x from (values (max(t.a))) v(x)) from t group by b", "10|1 20|2 30|3")]
    [DataRow("select b, (select sum(max(t.a)) over () from u) from t group by b", "10|1 20|2 30|3")]
    [DataRow("select b, case when exists (select 1 from u having max(t.a) > 2) then 1 else 0 end from t group by b", "10|0 20|0 30|1")]
    [DataRow("select b from t group by b having exists (select 1 from u where max(t.a) > u.c)", "20 30")]
    [DataRow("select b, (select 1 from u having sum(t.a) > 1) from t group by b", "10|NULL 20|1 30|1")]
    [DataRow("select b, (select count(*) from u join u u2 on u.c < max(t.a)) from t group by b", "10|0 20|1 30|1")]
    [DataRow("select b, (select grouping(t.b) from u) from t group by rollup(b)", "10|0 20|0 30|0 NULL|1")]
    public void PerGroupValue_ReachesEveryGroup(string sql, string expected)
        => AreEqual(expected, Rows(Fixture(), sql));

    [TestMethod]
    [DataRow("select (select max(t.a) from u) from t", "3")]
    [DataRow("select (select (select max(t.a) from u u2) from u) from t", "3")]
    [DataRow("select (select x.m from u cross apply (select max(t.a) m from u u2) x) from t", "3")]
    [DataRow("select (select max(t.a + t.b) from u) from t", "33")]
    [DataRow("declare @v int = 1; select (select max(t.a + @v) from u) from t", "4")]
    [DataRow("select (select max(t.a) + count(*) from u) from t", "4")]
    [DataRow("select (select m from (select max(t.a) m from u) d) from t", "3")]
    [DataRow("select (select x from (values (max(t.a))) v(x)) from t", "3")]
    [DataRow("select (select count(*) from u u2 where u2.c < max(t.a)) from t", "1")]
    [DataRow("select * from t cross apply (values ((select t.a + u.c from u))) x(m)", "1|10|2 2|20|3 3|30|4")]
    public void AggregateOverEnclosingColumns_CollapsesTheEnclosingQuery(string sql, string expected)
        => AreEqual(expected, Rows(Fixture(), sql));

    [TestMethod]
    [DataRow("select (select max(t.a + u.c) from u) from t")]
    [DataRow("select (select count(distinct t.a + u.c) from u) from t")]
    [DataRow("select (select max(case when u.c = 1 then t.a end) from u) from t")]
    [DataRow("select (select (select max(t.a + u.c) from u u2) from u) from t")]
    [DataRow("select (select count(*) from u where u.c < max(t.a + u.c)) from t")]
    [DataRow("select (select top 1 c from u order by max(t.a + u.c)) from t")]
    [DataRow("select * from t cross apply (select max(t.a + u.c) m, max(t.a) n from u) x")]
    public void OuterReferenceBesideAnotherScopesColumn_IsMsg8124(string sql)
        => _ = Fixture().AssertSqlError(sql, 8124);

    [TestMethod]
    [DataRow("select * from t cross apply (select max(t.a) m from u) x")]
    [DataRow("select * from t outer apply (select count(t.a) n from u) x")]
    [DataRow("select * from t cross apply (select (select max(t.a) from u u2) m from u) x")]
    [DataRow("select * from t cross apply (select max(t.a) m) x")]
    [DataRow("select * from t cross apply (values ((select max(t.a) from u))) x(m)")]
    [DataRow("select * from t cross apply (values (max(t.a))) x(m)")]
    [DataRow("select (select top 1 x.m from u cross apply (select max(u.c) m from u u2) x) from t")]
    [DataRow("select * from t cross apply (select max(t.a) n, max(t.a + u.c) m from u) x")]
    public void AggregateOverApplyLeftSide_IsMsg4101(string sql)
        => _ = Fixture().AssertSqlError(sql, 4101);

    [TestMethod]
    [DataRow("select * from t join u on count(*) = 1", 1015)]
    [DataRow("select * from t join u on max(u.c) = 1", 1015)]
    [DataRow("select * from t left join u on u.c = (select count(t.a) from u u2)", 1015)]
    [DataRow("update t set b = 0 from t join u on u.c = (select max(t.a) from u u2)", 1015)]
    [DataRow("delete t from t join u on u.c = (select max(t.a) from u u2)", 1015)]
    [DataRow("merge u using t on u.c = max(t.a) when matched then delete;", 1015)]
    [DataRow("merge u using t on u.c = (select max(t.a) from u u2) when matched then delete;", 1015)]
    [DataRow("update t set b = max(a)", 157)]
    [DataRow("update t set b = (select max(t.a) from u)", 157)]
    [DataRow("update t set b = (select (select max(t.a) from u u2) from u)", 157)]
    [DataRow("update x set b = (select max(x.a) from u) from t x", 157)]
    [DataRow("merge u using t on u.c = t.a when matched then update set d = max(t.a);", 157)]
    [DataRow("merge u using t on u.c = t.a when matched then update set d = (select max(t.a) from u u2);", 157)]
    [DataRow("merge u using t on u.c = t.a when matched and count(*) > 0 then delete;", 5319)]
    [DataRow("merge u using t on u.c = t.a when not matched and 1 = (select count(t.b) from u u2) then insert values (t.a, 0);", 5319)]
    [DataRow("insert u values (count(*), 0)", 5310)]
    [DataRow("insert u values (1, 0), (sum(2), 0)", 5310)]
    [DataRow("select * from (values (max(1))) v(x)", 5310)]
    [DataRow("merge u using t on u.c = t.a when not matched then insert values (max(t.a), 0);", 5310)]
    [DataRow("merge u using (values (max(1))) s(x) on u.c = s.x when matched then delete;", 5310)]
    [DataRow("select a from t where a = (select max(t.a) from u)", 147)]
    [DataRow("update t set b = max(a) where count(*) > 0", 147)]
    [DataRow("select a from t order by a offset (select max(t.a) from u) rows", 4115)]
    [DataRow("delete t output (select max(deleted.a) from u) where a = 1", 10705)]
    [DataRow("merge u using t on u.c = t.a when matched then delete output (select max(t.a) from u u2);", 10705)]
    public void ClauseOwningNoAggregate_RefusesOneMovedThere(string sql, int number)
        => _ = Fixture().AssertSqlError(sql, number);

    /// <summary>A row-count operand names the column's leaf however it was qualified.</summary>
    [TestMethod]
    public void RowCountOperand_NamesTheLeaf()
        => Fixture().AssertSqlError("select top (t.a) a from t", 4115, "The reference to column \"a\" is not allowed in an argument to a TOP, OFFSET, or FETCH clause. Only references to columns at an outer scope or standalone expressions and subqueries are allowed here.");

    /// <summary>
    /// An alias-form UPDATE's SET subquery binds against the statement's FROM,
    /// the shape EF Core's <c>ExecuteUpdate</c> emits for a correlated count.
    /// </summary>
    [TestMethod]
    public void AliasFormUpdate_SetSubqueryReadsTheAlias()
    {
        var simulation = Fixture();
        _ = simulation.ExecuteNonQuery("update [x] set [x].[b] = (select count(*) from [u] as [p] where [x].[a] = [p].[c]) from [t] as [x]");
        AreEqual("1|1 2|0 3|0", Rows(simulation, "select a, b from t"));
    }

    /// <summary>A scalar subquery's Msg 512 ends a writing statement with Msg 3621.</summary>
    [TestMethod]
    public void MultiRowScalarSubqueryInAWrite_IsFollowedByMsg3621()
    {
        var ex = new Simulation().AssertSqlError(Setup + "insert u values (2, 6); insert u select (select max(t.a) from u), 0 from t", 512);
        Contains(3621, ex.Errors.Cast<SimulatedError>().Select(error => error.Number));
    }
}
