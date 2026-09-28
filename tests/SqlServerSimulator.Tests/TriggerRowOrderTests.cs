using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The order a trigger reads <c>INSERTED</c> / <c>DELETED</c> in without an
/// <c>ORDER BY</c>: the reverse of the statement's write order for an AFTER
/// trigger, forward for INSTEAD OF (probed 2026-09-28 against SQL Server 2025).
/// See <c>docs/claude/triggers.md</c>.
/// </summary>
[TestClass]
public sealed class TriggerRowOrderTests
{
    [TestMethod]
    [DataRow("create table t (id int, v int)", "insert t values (3, 0), (1, 0), (2, 0), (5, 0), (4, 0)", "ins:4,5,2,1,3")]
    [DataRow("create table t (id int primary key, v int)", "insert t values (3, 0), (1, 0), (2, 0), (5, 0), (4, 0)", "ins:4,5,2,1,3")]
    [DataRow("create table t (id int, v int); create table src (id int primary key); insert src values (3), (1), (2)", "insert t select id, 0 from src", "ins:3,2,1")]
    [DataRow("create table t (id int primary key, v int); insert t values (3, 0), (1, 0), (2, 0)", "update t set v = 1", "ins:3,2,1 del:3,2,1")]
    [DataRow("create table t (id int, v int); insert t values (3, 0), (1, 0), (2, 0)", "delete t", "del:2,1,3")]
    [DataRow("create table t (id int primary key, v int); insert t values (2, 0)", "merge t using (values (3), (1), (2)) s(id) on t.id = s.id when matched then update set v = 9 when not matched then insert values (s.id, 0);", "ins:1,3 ins:2 del:2")]
    public void AfterTriggerReadsInReverse(string setup, string statement, string expected)
        => AreEqual(expected, new Simulation().ExecuteBatchesScalar(
            $"{setup}; create table log (n int identity, s varchar(100))",
            """
            create trigger tr on t after insert, update, delete as
            begin
                insert log (s) select 'ins:' + string_agg(cast(id as varchar(10)), ',') from inserted having count(*) > 0;
                insert log (s) select 'del:' + string_agg(cast(id as varchar(10)), ',') from deleted having count(*) > 0;
            end
            """,
            $"{statement}; select string_agg(s, ' ') within group (order by n) from log"));

    [TestMethod]
    public void InsteadOfTriggerReadsForward()
        => AreEqual("3,1,2", new Simulation().ExecuteBatchesScalar(
            "create table t (id int, v int); create table log (s varchar(100))",
            "create trigger tr on t instead of insert as insert log select string_agg(cast(id as varchar(10)), ',') from inserted",
            "insert t values (3, 0), (1, 0), (2, 0); select s from log"));

    [TestMethod]
    public void TopOneTakesTheLastWrittenRow()
        => AreEqual(2, new Simulation().ExecuteBatchesScalar(
            "create table t (id int, v int); create table log (id int)",
            "create trigger tr on t after insert as insert log select top (1) id from inserted",
            "insert t values (3, 0), (1, 0), (2, 0); select id from log"));
}
