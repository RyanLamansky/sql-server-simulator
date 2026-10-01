using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The <c>OUTPUT</c> clause of a joined <c>UPDATE</c> / <c>DELETE</c> reading
/// the <c>FROM</c> clause's other sources: each written row's values come from
/// the partner it was written with, the first one the join meets. Probed
/// against SQL Server 2025 on 2026-10-01.
/// </summary>
[TestClass]
public sealed class JoinedWriteOutputTests
{
    private static Simulation Setup()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table t2 (id int primary key, v int);
            create table u2 (id int, w int);
            insert t2 values (1, 10), (2, 20), (3, 30);
            insert u2 values (1, 100), (2, 200), (2, 201);
            """,
            "create view vt as select id, v from t2",
            "create view vj as select t2.id, t2.v, u2.w from t2 join u2 on u2.id = t2.id");
        return simulation;
    }

    /// <summary>The OUTPUT rows, one string per row, its values joined by commas.</summary>
    private static string Output(Simulation simulation, string statement)
    {
        using var reader = simulation.ExecuteReader(statement);
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            _ = reader.GetValues(values);
            rows.Add(string.Join(",", values));
        }
        rows.Sort(StringComparer.Ordinal);
        return string.Join(" ", rows);
    }

    [TestMethod]
    [DataRow("update t2 set v = u2.w output u2.w, u2.id, inserted.v, deleted.v from t2 join u2 on u2.id = t2.id", "100,1,100,10 200,2,200,20", DisplayName = "Table target")]
    [DataRow("update a set v = b.w output b.w, inserted.v, b.* from t2 a join u2 b on b.id = a.id", "100,100,1,100 200,200,2,200", DisplayName = "Alias form, star")]
    [DataRow("update t2 set v = d.x output d.x from t2 join (select id, w * 2 x from u2) d on d.id = t2.id", "200 400", DisplayName = "Derived table source")]
    [DataRow("update t2 set v = 1 output inserted.v + u2.w from t2 join u2 on u2.id = t2.id", "101 201", DisplayName = "Expression")]
    [DataRow("update top (1) t2 set v = 1 output u2.w from t2 join u2 on u2.id = t2.id", "100", DisplayName = "TOP")]
    [DataRow("update t2 set v = 1 output c.z from t2 cross apply (select t2.v * 3 z) c where t2.id = 1", "30", DisplayName = "APPLY")]
    [DataRow("update t2 set v = 0 output deleted.id, u2.w from t2 as x left join u2 on u2.id = x.id", "1,100 2,200 3,", DisplayName = "Outer join's missing partner")]
    [DataRow("delete t2 output deleted.id, u2.w from t2 join u2 on u2.id = t2.id", "1,100 2,200", DisplayName = "DELETE")]
    [DataRow("delete a output deleted.id, b.w from t2 a join u2 b on b.id = a.id", "1,100 2,200", DisplayName = "DELETE, alias form")]
    [DataRow("update vt set v = u2.w output u2.w, inserted.v from vt join u2 on u2.id = vt.id", "100,100 200,200", DisplayName = "View target")]
    [DataRow("delete vt output deleted.v, u2.w from vt join u2 on u2.id = vt.id", "10,100 20,200", DisplayName = "View target, DELETE")]
    [DataRow("update vj set v = 5 output x.w, inserted.v from vj join u2 x on x.id = vj.id", "100,5 200,5", DisplayName = "Join view target")]
    [DataRow("with c as (select id, v from t2) update c set v = u2.w output u2.w, deleted.v from c join u2 on u2.id = c.id", "100,10 200,20", DisplayName = "CTE target")]
    [DataRow("update d set v = u2.w output u2.w, inserted.v from (select id, v from t2) d join u2 on u2.id = d.id", "100,100 200,200", DisplayName = "Derived table target")]
    public void Output_ReadsTheWrittenRowsPartner(string statement, string expected) =>
        AreEqual(expected, Output(Setup(), statement));

    [TestMethod]
    public void OutputInto_ReadsThePartner() =>
        AreEqual("1:100 2:200", Setup().ExecuteScalar("""
            declare @o table (a int, b int);
            update t2 set v = u2.w output inserted.id, u2.w into @o from t2 join u2 on u2.id = t2.id;
            select string_agg(concat(a, ':', b), ' ') within group (order by a) from @o
            """));

    /// <summary>
    /// The target's own name or alias is no qualifier the clause binds (Msg
    /// 4104), an unqualified name is Msg 207, and a partner's unknown column
    /// Msg 207 on the leaf.
    /// </summary>
    [TestMethod]
    [DataRow("update t2 set v = 1 output t2.v, inserted.v from t2 join u2 on u2.id = t2.id", 4104)]
    [DataRow("update a set v = 1 output a.v from t2 a join u2 on u2.id = a.id", 4104)]
    [DataRow("update t2 set v = 1 output w from t2 join u2 on u2.id = t2.id", 207)]
    [DataRow("update t2 set v = 1 output u2.zz from t2 join u2 on u2.id = t2.id", 207)]
    [DataRow("update t2 set v = 1 output inserted.w from t2 join u2 on u2.id = t2.id", 207)]
    [DataRow("update t2 set v = 1 output t2.id where id = 1", 4104)]
    public void Output_Refusals(string statement, int number) =>
        _ = Setup().AssertSqlError(statement, number);
}
