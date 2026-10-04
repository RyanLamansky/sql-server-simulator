using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A joined <c>UPDATE</c> / <c>DELETE</c> whose target is a view or CTE: the
/// leading name aliases a <c>FROM</c> source reading one, names the view the
/// clause reads, or names one the clause never introduced. Probed against SQL
/// Server 2025 on 2026-10-01.
/// </summary>
[TestClass]
public sealed class JoinedViewTargetTests
{
    private static Simulation Setup()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table t (id int primary key, v int, k int);
            create table u (k int, n int);
            create table log1 (msg varchar(100));
            insert t values (1, 10, 1), (2, 20, 2), (3, 30, 3), (4, 40, 1);
            insert u values (1, 100), (2, 200), (2, 201), (5, 500);
            """,
            "create view v as select id, v, k, v * 2 as d from t where id < 4",
            "create view v2 as select id as i2, v as v2, k from v where v > 15",
            "create view vj as select t.id, t.v, t.k, u.n from t join u on u.k = t.k",
            "create view vc as select id, v, k from t where v < 100 with check option",
            "create view vagg as select k, count(*) c from t group by k",
            "create view vtop as select top 2 id, v, k from t order by v desc");
        return simulation;
    }

    private const string Rows = "select string_agg(concat(id, ':', v), ' ') within group (order by id) from t";

    /// <summary>
    /// The write takes the view's base rows the join and WHERE pass, each once
    /// — a row joined to two partners takes the first one's values — and only
    /// the rows the view shows: its filter, row limit and chain apply.
    /// </summary>
    [TestMethod]
    [DataRow("update v set v = v.v + u.n from v join u on u.k = v.k", 2, "1:110 2:220 3:30 4:40", DisplayName = "Target named as the view")]
    [DataRow("update a set v = a.v + 1 from v as a join u on u.k = a.k where a.id > 1", 1, "1:10 2:21 3:30 4:40", DisplayName = "Aliased target")]
    [DataRow("update v set v = 0 from v where id = 2", 1, "1:10 2:0 3:30 4:40", DisplayName = "FROM naming the view alone")]
    [DataRow("update v set v = u.n from u where u.k = v.k", 2, "1:100 2:200 3:30 4:40", DisplayName = "View the FROM clause never names")]
    [DataRow("update a set v = 0 from v a join u on u.k = a.k where a.d = 40", 1, "1:10 2:0 3:30 4:40", DisplayName = "WHERE reading a derived column")]
    [DataRow("update a set v = a.d from v a join u on u.k = a.k", 2, "1:20 2:40 3:30 4:40", DisplayName = "SET reading a derived column")]
    [DataRow("update a set v = b.v from v a join v b on b.id = a.id + 1", 2, "1:20 2:30 3:30 4:40", DisplayName = "View joined to itself")]
    [DataRow("update a set v2 = a.v2 + u.n from v2 a join u on u.k = a.k", 1, "1:10 2:220 3:30 4:40", DisplayName = "View over a view")]
    [DataRow("update top (1) a set v = 0 from v a join u on u.k = a.k", 1, "1:0 2:20 3:30 4:40", DisplayName = "TOP")]
    [DataRow("update a set v = 0 from vtop a join u on u.k = a.k", 1, "1:10 2:20 3:30 4:0", DisplayName = "Row-limited view")]
    [DataRow("update a set v = a.v + 1 from vc a join u on u.k = a.k", 3, "1:11 2:21 3:30 4:41", DisplayName = "CHECK OPTION view, rows staying visible")]
    [DataRow("update a set v = 1 from vc a join vc b on b.id = a.id + 1", 3, "1:1 2:1 3:1 4:40", DisplayName = "CHECK OPTION view joined to itself")]
    [DataRow("update x set v = 5 from vj x join u on u.n = x.n where u.n = 100", 2, "1:5 2:20 3:30 4:5", DisplayName = "Join view")]
    [DataRow("update x set v = 1 from vj x join vj y on y.id = x.id", 3, "1:1 2:1 3:30 4:1", DisplayName = "Join view joined to itself")]
    [DataRow("update x set v = t.v + 1 from vj x join t on t.id = x.id + 1", 2, "1:21 2:31 3:30 4:40", DisplayName = "Join view joined to its own table")]
    [DataRow("with c as (select id, v, k from t where id > 1) update c set v = c.v + u.n from c join u on u.k = c.k", 2, "1:10 2:220 3:30 4:140", DisplayName = "CTE")]
    [DataRow("with c as (select id, v, k from t where id > 1) update c set v = 0 from u where u.k = c.k", 2, "1:10 2:0 3:30 4:0", DisplayName = "CTE the FROM clause never names")]
    [DataRow("with c as (select t.id, t.v, u.n, t.k from t join u on u.k = t.k) update c set v = 0 from c join u u2 on u2.k = c.k where u2.n = 100", 2, "1:0 2:20 3:30 4:0", DisplayName = "CTE over a join")]
    public void Update_WritesTheViewsBaseRows(string update, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(update));
        AreEqual(expected, simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    [DataRow("delete v from v join u on u.k = v.k", 2, "3:30 4:40", DisplayName = "Target named as the view")]
    [DataRow("delete from v from v join u on u.k = v.k where u.n = 100", 1, "2:20 3:30 4:40", DisplayName = "DELETE FROM … FROM")]
    [DataRow("delete v from u x where x.k = v.k", 2, "3:30 4:40", DisplayName = "View the FROM clause never names")]
    [DataRow("delete a from vtop a join u on u.k = a.k", 1, "1:10 2:20 3:30", DisplayName = "Row-limited view")]
    [DataRow("with c as (select id, v, k from t where id > 1) delete c from c join u on u.k = c.k", 2, "1:10 3:30", DisplayName = "CTE")]
    public void Delete_RemovesTheViewsBaseRows(string delete, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(delete));
        AreEqual(expected, simulation.ExecuteScalar(Rows));
    }

    /// <summary>
    /// Through a join view the SET list names the one base table written, the
    /// other table's columns included.
    /// </summary>
    [TestMethod]
    public void JoinView_WritesTheTableTheSetListNames()
    {
        var simulation = Setup();
        AreEqual(2, simulation.ExecuteNonQuery("update x set n = n + 1 from vj x where x.id = 2"));
        AreEqual("1:100 2:201 2:202 5:500", simulation.ExecuteScalar("select string_agg(concat(k, ':', n), ' ') within group (order by k, n) from u"));
    }

    /// <summary>
    /// The refusals name the target as written — the alias, or the view's name
    /// as the leading name spells it — except an <c>INSTEAD OF</c> trigger's,
    /// which names the view bare.
    /// </summary>
    [TestMethod]
    [DataRow("update a set d = 1 from dbo.v a join u on u.k = a.k", 4406, "Update or insert of view or function 'a' failed because it contains a derived or constant field.")]
    [DataRow("update v set d = 1 from dbo.v join u on u.k = v.k", 4406, "Update or insert of view or function 'v' failed because it contains a derived or constant field.")]
    [DataRow("update dbo.v set d = 1 from dbo.v join u on u.k = v.k", 4406, "Update or insert of view or function 'dbo.v' failed because it contains a derived or constant field.")]
    [DataRow("update x set v = 1, n = 2 from vj x", 4405, "View or function 'x' is not updatable because the modification affects multiple base tables.")]
    [DataRow("delete x from vj x join u on u.n = x.n", 4405, "View or function 'x' is not updatable because the modification affects multiple base tables.")]
    [DataRow("update a set k = 1 from vagg a join u on u.k = a.k", 4403, "Cannot update the view or function 'a' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.")]
    [DataRow("delete a from vagg a join u on u.k = a.k", 4403, "Cannot update the view or function 'a' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.")]
    [DataRow("update a set c = 1 from vagg a join u on u.k = a.k", 4406, "Update or insert of view or function 'a' failed because it contains a derived or constant field.")]
    [DataRow("with c as (select id, v + 1 w, k from t) update c set w = 1 from c join u on u.k = c.k", 4406, "Update or insert of view or function 'c' failed because it contains a derived or constant field.")]
    [DataRow("with c as (select t.id, t.v, u.n from t join u on u.k = t.k) delete c from c where c.n = 100", 4405, "View or function 'c' is not updatable because the modification affects multiple base tables.")]
    [DataRow("with c as (select id, v from t union select k, n from u) delete c from c join u on u.k = c.id", 4426, "View 'c' is not updatable because the definition contains a UNION operator.")]
    [DataRow("delete a output inserted.id from v a join u on u.k = a.k", 4104, "The multi-part identifier \"inserted.id\" could not be bound.")]
    public void Refusals_NameTheTargetAsWritten(string sql, int number, string message) =>
        Setup().AssertSqlError(sql, number, message);

    [TestMethod]
    public void CheckOption_RefusesARowLeavingTheView()
    {
        var simulation = Setup();
        _ = simulation.AssertSqlError("update a set v = 500 from vc a join u on u.k = a.k", 550);
        AreEqual("1:10 2:20 3:30 4:40", simulation.ExecuteScalar(Rows));
    }

    /// <summary>
    /// A view's <c>INSTEAD OF</c> trigger for the action refuses a joined write
    /// through it (Msg 414 / 415), the view joined in the FROM clause or not;
    /// another action's trigger leaves the write to the base table.
    /// </summary>
    [TestMethod]
    public void InsteadOfTrigger_RefusesTheJoinedWrite()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view vi as select id, v, k from t",
            "create trigger tr_vi on vi instead of update as insert log1 select 'iou'",
            "create view vd as select id, v, k from t",
            "create trigger tr_vd on vd instead of delete as insert log1 select 'iod'");
        simulation.AssertSqlError("update a set v = 0 from vi a join u on u.k = a.k", 414, "UPDATE is not allowed because the statement updates view \"vi\" which participates in a join and has an INSTEAD OF UPDATE trigger.");
        _ = simulation.AssertSqlError("update vi set v = 0 from u where u.k = vi.k", 414);
        simulation.AssertSqlError("delete a from dbo.vd a join u on u.k = a.k", 415, "DELETE is not allowed because the statement updates view \"dbo.vd\" which participates in a join and has an INSTEAD OF DELETE trigger.");
        simulation.AssertSqlError("delete a from vd a join u on u.k = a.k", 415, "DELETE is not allowed because the statement updates view \"vd\" which participates in a join and has an INSTEAD OF DELETE trigger.");
        AreEqual(3, simulation.ExecuteNonQuery("delete a from vi a join u on u.k = a.k"));
        AreEqual(1, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(0, simulation.ExecuteScalar("select count(*) from log1"));
    }

    /// <summary>
    /// A FROM clause naming the view alone, aliased or not, hands the view's
    /// rows its WHERE picks to the <c>INSTEAD OF</c> trigger, as the form with
    /// no FROM clause does: <c>INSERTED</c> / <c>DELETED</c> hold the view's
    /// rows, <c>@@ROWCOUNT</c> counts them, and <c>OUTPUT INSERTED</c> is Msg
    /// 404.
    /// </summary>
    [TestMethod]
    public void InsteadOfTrigger_TakesAFromClauseNamingTheViewAlone()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view vi as select id, v, k from t",
            "create trigger tr_vi on vi instead of update, delete as insert log1 select concat('i', id, ':', v) from inserted union all select concat('d', id, ':', v) from deleted");
        const string Log = "select string_agg(msg, ' ') within group (order by msg) from log1; delete log1";
        AreEqual(2, simulation.ExecuteScalar("update vi set v = v + 1 from vi where id < 3; select @@rowcount"));
        AreEqual("d1:10 d2:20 i1:11 i2:21", simulation.ExecuteScalar(Log));
        AreEqual(1, simulation.ExecuteScalar("update a set v = 0 from vi a where a.id = 3; select @@rowcount"));
        AreEqual("d3:30 i3:0", simulation.ExecuteScalar(Log));
        AreEqual(1, simulation.ExecuteScalar("delete vi from vi where id = 1; select @@rowcount"));
        AreEqual(1, simulation.ExecuteScalar("delete a from vi a where a.id = 2; select @@rowcount"));
        AreEqual("d1:10 d2:20", simulation.ExecuteScalar(Log));
        AreEqual(0, simulation.ExecuteScalar("update vi set v = 7 from vi where id > 30; select @@rowcount"));
        AreEqual("1:10 2:20 3:30 4:40", simulation.ExecuteScalar(Rows));
        _ = simulation.AssertSqlError("update vi set v = 7 output inserted.id from vi where id = 1", 404);
        _ = simulation.AssertSqlError("update vi set v = 7 output deleted.v from vi where id = 1", 334);
        _ = simulation.AssertSqlError("update vi set v = 1 from vi, vi b where vi.id = b.id", 414);
    }

    [TestMethod]
    public void BaseTableTriggers_FireOnce()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create trigger tr_t on t after update, delete as insert log1 select concat((select count(*) from inserted), '/', (select count(*) from deleted))");
        _ = simulation.ExecuteNonQuery("update a set v = 0 from v a join u on u.k = a.k");
        _ = simulation.ExecuteNonQuery("delete a from v a join u on u.k = a.k");
        AreEqual("2/2 0/2", simulation.ExecuteScalar("select string_agg(msg, ' ') from log1"));
        simulation.AssertSqlError("update a set v = 3 output inserted.v from v a join u on u.k = a.k", 334, "The target table 't' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.");
    }

    /// <summary>
    /// <c>INSERTED</c> / <c>DELETED</c> are the view's columns, a derived one
    /// computed from the row; through a join view <c>DELETED</c> reads the other
    /// table's columns as joined and <c>INSERTED</c> refuses them (Msg 404).
    /// </summary>
    [TestMethod]
    [DataRow("update a set v = 7 output inserted.*, deleted.d from v a join u on u.k = a.k", "1,7,1,14,20|2,7,2,14,40")]
    [DataRow("delete a output deleted.* from v a join u on u.k = a.k", "1,10,1,20|2,20,2,40")]
    [DataRow("update a set v2 = 7 output deleted.*, inserted.* from v2 a join u on u.k = a.k", "2,20,2,2,7,2")]
    [DataRow("update a set v = u.n output inserted.v from v a join u on u.k = a.k", "100|200")]
    [DataRow("update x set v = 1 output inserted.id, inserted.v, deleted.n from vj x join u y on y.n = x.n", "1,1,100|2,1,200|4,1,100")]
    [DataRow("with c as (select t.id, t.v, u.n from t join u on u.k = t.k) update c set v = 0 output inserted.id, inserted.v, deleted.n", "1,0,100|2,0,200|4,0,100")]
    public void Output_ReadsTheViewsColumns(string sql, string expected)
    {
        using var reader = Setup().ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            _ = reader.GetValues(values);
            rows.Add(string.Join(',', values));
        }
        AreEqual(expected, string.Join('|', rows));
    }

    [TestMethod]
    public void Output_IntoATableVariable()
        => AreEqual("1:6 2:6", Setup().ExecuteScalar("declare @o table (id int, d int); update a set v = 3 output inserted.id, inserted.d into @o from v a join u on u.k = a.k; select string_agg(concat(id, ':', d), ' ') within group (order by id) from @o"));

    [TestMethod]
    public void JoinView_InsertedRefusesTheOtherTablesColumns() =>
        Setup().AssertSqlError("update x set v = 1 output inserted.n from vj x", 404, "The column reference \"inserted.n\" is not allowed because it refers to a base table that is not being modified in this statement.");

    /// <summary>
    /// The alias form's <c>OUTPUT</c> clause, written ahead of the FROM clause
    /// naming its table, binds against that table; its trigger's Msg 334 names
    /// the alias.
    /// </summary>
    [TestMethod]
    public void AliasForm_OutputBindsAgainstTheTable()
    {
        var simulation = Setup();
        using (var reader = simulation.ExecuteReader("update a set v = 3 output inserted.v, deleted.v from t a join u on u.k = a.k"))
        {
            var deleted = new List<int>();
            while (reader.Read())
                deleted.Add(reader.GetInt32(1));
            CollectionAssert.AreEqual(new[] { 10, 20, 40 }, deleted);
        }
        AreEqual(3, simulation.ExecuteScalar("select count(*) from t where v = 3"));
        AreEqual("1,2,4", simulation.ExecuteScalar("declare @d table (id int); delete a output deleted.id into @d from t a join u on u.k = a.k; select string_agg(id, ',') within group (order by id) from @d"));
        simulation.ExecuteBatches("create trigger tr_t on t after update as select 1");
        simulation.AssertSqlError("update a set v = 3 output inserted.v from t a join u on u.k = a.k", 334, "The target table 'a' of the DML statement cannot have any enabled triggers if the statement contains an OUTPUT clause without INTO clause.");
    }
}
