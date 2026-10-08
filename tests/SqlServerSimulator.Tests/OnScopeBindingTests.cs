using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An unqualified name in an <c>ON</c> binds in that join's own scope — its
/// chain's sources up to and including its right operand — and an
/// <c>APPLY</c> body's in its chain's sources to its left, so a source joined
/// later, or an earlier comma item, carrying a column of the same name
/// neither makes it ambiguous nor takes it, there or in a subquery
/// correlating through that scope; a name the scope lacks binds to an
/// enclosing query even when a later source carries it. Probed 2026-10-08
/// against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class OnScopeBindingTests
{
    private const string Seed = """
        create table a (id int, x int, ax int);
        create table b (id int, y int);
        create table c (id int, x int, y int, cz int);
        create table e (id int, oz int);
        create table o (id int, oz int);
        create table o3 (id int, ov int);
        insert a values (1, 1, 1), (2, 2, 2);
        insert b values (1, 1), (2, 3);
        insert c values (1, 10, 1, 1), (2, 20, 3, 2);
        insert e values (1, 1), (2, 2);
        insert o values (1, 1), (2, 5);
        insert o3 values (1, 1), (2, 5);
        """;

    private static object? Rows(string query) =>
        new Simulation().ExecuteScalar($"{Seed} select string_agg(r, ',') within group (order by r) from ({query}) q(r)");

    [TestMethod]
    [DataRow("select concat(a.id, b.id, c.id) from a join b on x = y join c on c.id = a.id", "111")]
    [DataRow("select concat(a.id, '/', b.id, '/', c.id) from a left join b on x = y join c on c.id = a.id", "1/1/1,2//2")]
    [DataRow("select concat(a.id, '/', b.id, '/', c.id) from a right join b on x = y join c on c.id = b.id", "/2/2,1/1/1")]
    [DataRow("select concat(a.id, '/', b.id, '/', c.id) from a full join b on x = y left join c on c.id = a.id", "/2/,1/1/1,2//2")]
    [DataRow("select concat(a.id, c.id) from a join b on x = y, c", "11,12")]
    [DataRow("select concat(a.id, c.id) from c, a join b on x = y", "11,12")]
    [DataRow("select concat(a.id, c.id) from a join b on x = y cross join c", "11,12")]
    [DataRow("select concat(a.id, b.id, c.id) from a join (b join c on x = 10) on a.id = b.id", "111,221")]
    [DataRow("select concat(a.id, b.id, c.id) from a join b join c on x = 10 on a.id = b.id", "111,221")]
    [DataRow("select concat(a.id, b.id, c.id) from (a join b on x = y) join c on c.id = a.id", "111")]
    [DataRow("select concat(a.id, d.x) from a join b on x = y join (select id, x from c) d on d.id = a.id", "110")]
    [DataRow("select concat(a.id, d.x) from a join b on x = y cross apply (select c.x from c where c.id = a.id) d", "110")]
    [DataRow("select concat(a.id, b.id) from a join b on x between y - 1 and y + 1 join c on c.id = a.id", "11,21,22")]
    [DataRow("select concat(a.id, c.id) from a inner hash join b on x = y join c on c.id = a.id", "11")]
    [DataRow("select a.id from a join b on y = 1 join c on 1 = 1", "1,1,2,2")]
    public void LaterSourceCarryingTheName_LeavesTheOnItsOwnBinding(string query, string expected)
        => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select concat(a.id, c.id) from a join b on exists (select 1 from e where e.id = y and e.oz = x) join c on c.id = a.id", "11")]
    [DataRow("select a.id from a join b on a.id = b.id and a.id = (select max(e.id) from e where e.oz = x) join c on c.id = a.id", "1,2")]
    [DataRow("select a.id from a join b on exists (select 1 from e join o on o.id = e.id and o.oz = y) join c on c.id = a.id", "1,2")]
    public void SubqueryInTheOn_CorrelatesThroughTheOnsScope(string query, string expected)
        => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select concat(a.id, d.v) from a cross apply (select x as v) d join c on c.id = a.id", "11,22")]
    [DataRow("select concat(a.id, '/', d.v) from a outer apply (select x as v where x = 1) d join c on c.id = a.id", "1/1,2/")]
    [DataRow("select concat(a.id, d.v) from a cross apply (select (select max(e.id) from e where e.oz = x) as v) d join c on c.id = a.id", "11,22")]
    [DataRow("select concat(a.id, s.value) from a cross apply string_split(cast(x as varchar(10)), ',') s join c on c.id = a.id", "11,22")]
    [DataRow("select concat(a.id, v.vx) from a cross apply (values (x)) v(vx) join c on c.id = a.id", "11,22")]
    public void ApplyBody_BindsInItsLeftSide(string query, string expected)
        => AreEqual(expected, Rows(query));

    [TestMethod]
    [DataRow("select 1 from a, b cross apply (select ax as v) d", "ax")]
    [DataRow("select 1 from a, b cross apply string_split(cast(ax as varchar(5)), ',') s", "ax")]
    [DataRow("select 1 from a left join b cross apply (select ax as v) d on b.id = a.id", "ax")]
    public void ApplyBodyNamingAColumnOutsideItsChain_RaisesMsg207(string query, string name)
        => new Simulation().AssertSqlError($"{Seed} {query}", 207, $"Invalid column name '{name}'.");

    [TestMethod]
    public void ApplyBodyNamingAnEarlierCommaItem_RaisesMsg4104()
        => new Simulation().AssertSqlError($"{Seed} select 1 from a, b cross apply (select a.x as v) d", 4104, "The multi-part identifier \"a.x\" could not be bound.");

    [TestMethod]
    [DataRow("select o.id from o where exists (select 1 from a join b on a.id = b.id and a.id = oz join e on e.id = a.id)")]
    [DataRow("select o3.id from o3 where exists (select 1 from a join b on a.id = b.id and a.id = ov join (select 1 as ov) z on 1 = 1)")]
    [DataRow("select o3.id from o3 where exists (select 1 from a cross apply (select e.id from e where e.id = ov and e.id = a.id) d join (select 1 as ov) z on 1 = 1)")]
    public void NameTheScopeLacks_BindsToTheEnclosingQueryOverALaterSource(string query)
        => AreEqual("1", Rows(query));

    [TestMethod]
    [DataRow("select 1 from a join b on a.id = b.id join c on x = 1", "x")]
    [DataRow("select 1 from a join (b join c on b.id = c.id) on x = 1", "x")]
    [DataRow("select 1 from a cross apply (select x from c where c.id = a.id) d join b on x = y", "x")]
    [DataRow("select 1 from a join b on a.id = b.id join c on c.id = a.id where x = 1", "x")]
    public void NameTwoSourcesInScopeCarry_RaisesMsg209(string query, string name)
        => new Simulation().AssertSqlError($"{Seed} {query}", 209, $"Ambiguous column name '{name}'.");

    [TestMethod]
    public void NameOnlyALaterSourceCarries_RaisesMsg207()
        => new Simulation().AssertSqlError($"{Seed} select 1 from a join b on a.id = b.id and cz = 1 join c on 1 = 1", 207, "Invalid column name 'cz'.");

    [TestMethod]
    [DataRow("update a set ax = 9 from a join b on x = y join c on c.id = a.id", "1:9,2:2")]
    [DataRow("update t set ax = 9 from b join a t on x = y join c on c.id = t.id", "1:9,2:2")]
    [DataRow("update a set ax = 9 from b join c on x = 10 where a.id = b.id", "1:9,2:9")]
    [DataRow("delete a from a join b on x = y join c on c.id = a.id", "2:2")]
    [DataRow("delete a from b join c on x = 10 where a.id = b.id", "")]
    public void JoinedWrite_BindsEachOnInItsOwnScope(string statement, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"{Seed} {statement}; select isnull(string_agg(concat(id, ':', ax), ',') within group (order by id), '') from a"));

    [TestMethod]
    public void RepeatedExecutionAndProcedureReplay_KeepTheBinding()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Seed, "create procedure p as select count(*) from a join b on x = y join c on c.id = a.id");
        for (var i = 0; i < 3; i++)
        {
            AreEqual(1, simulation.ExecuteScalar("select count(*) from a join b on x = y join c on c.id = a.id"));
            AreEqual(1, simulation.ExecuteScalar("exec p"));
        }
    }
}
