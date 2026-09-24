using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Joins nested without parentheses — <c>A LEFT JOIN B JOIN C ON c1 ON c2</c>,
/// which reads as <c>A LEFT JOIN (B JOIN C ON c1) ON c2</c> — and the scope
/// each <c>ON</c> binds in: its own join's sources, never an earlier comma
/// item, an enclosing group's table or one that appears later (Msg 4104).
/// Probed 2026-09-24 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class NestedJoinOnTests
{
    private const string Seed = """
        create table a (id int); create table b (id int); create table c (id int); create table d (id int);
        insert a values (1), (2), (3); insert b values (1), (2); insert c values (1), (3); insert d values (1);
        """;

    private static string Rows(string from, string columns = "a.id, '/', b.id, '/', c.id") =>
        (string)new Simulation().ExecuteScalar(
            $"{Seed} select string_agg(concat({columns}), ',') within group (order by a.id, c.id) from {from}")!;

    [TestMethod]
    public void LeftJoinOverInnerJoin_NullExtendsTheWholeGroup()
        => AreEqual("1/1/1,2//,3//", Rows("a left join b join c on c.id = b.id on b.id = a.id"));

    [TestMethod]
    public void InnerJoinOverLeftJoin()
        => AreEqual("1/1/1,2/2/", Rows("a join b left join c on c.id = b.id on b.id = a.id"));

    [TestMethod]
    public void ThreeDeep()
        => AreEqual("1/1/1/1,2///,3///", Rows(
            "a left join b join c join d on d.id = c.id on c.id = b.id on b.id = a.id",
            "a.id, '/', b.id, '/', c.id, '/', d.id"));

    [TestMethod]
    public void CrossJoinInsideTheGroup_LeavesTheOnToTheOuterJoin()
        => AreEqual("1/1/1,2//,3//", Rows("a left join b cross join c on b.id = a.id and c.id = b.id"));

    [TestMethod]
    public void GroupFollowedByAnotherJoin()
        => AreEqual("1/1/1/1,2///,3///", Rows(
            "a left join b join c on c.id = b.id on b.id = a.id left join d on d.id = a.id",
            "a.id, '/', b.id, '/', c.id, '/', d.id"));

    [TestMethod]
    public void FullJoinOverRightJoin()
        => AreEqual("//3,1/1/1,2//,3//", Rows("a full join b right join c on c.id = b.id on b.id = a.id"));

    [TestMethod]
    public void GroupMissingItsOuterOn_RaisesMsg102()
        => new Simulation().AssertSqlError($"{Seed} select a.id from a left join b join c on c.id = b.id", 102);

    [TestMethod]
    [DataRow("a left join b join c on c.id = a.id on b.id = a.id", "a.id")]
    [DataRow("a left join (b join c on c.id = a.id) on b.id = a.id", "a.id")]
    [DataRow("a join b on b.id = a.id left join c join d on d.id = b.id on c.id = a.id", "b.id")]
    [DataRow("a, b join c on c.id = a.id", "a.id")]
    [DataRow("a join b on b.id = c.id join c on c.id = a.id", "c.id")]
    public void OnNamingATableOutsideItsJoin_RaisesMsg4104(string from, string name)
        => new Simulation().AssertSqlError($"{Seed} select a.id from {from}", 4104, $"The multi-part identifier \"{name}\" could not be bound.");

    [TestMethod]
    public void WhereStillSeesEveryCommaItem()
        => AreEqual(1, new Simulation().ExecuteScalar($"{Seed} select count(*) from a, b join c on c.id = b.id where a.id = c.id"));
}
