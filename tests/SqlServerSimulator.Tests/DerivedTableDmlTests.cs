using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Writes through a derived table — as a joined <c>UPDATE</c> / <c>DELETE</c>'s
/// target, and as a view, CTE or join view's source — which real takes as a
/// write through a view with that body, refusing a target with its own
/// derived-table messages. Probed against SQL Server 2025 on 2026-10-01.
/// </summary>
[TestClass]
public sealed class DerivedTableDmlTests
{
    private static Simulation Setup()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            """
            create table t (id int primary key, v int, x int);
            create table u (id int, w int);
            insert t values (1, 10, 5), (2, 20, 4), (3, 30, 3), (4, 40, 2);
            insert u values (1, 100), (2, 200), (2, 201);
            """);
        return simulation;
    }

    private const string Rows = "select string_agg(concat(id, ':', v), ' ') within group (order by id) from t";

    [TestMethod]
    [DataRow("update d set v = v + 1 from (select id, v from t where id < 3) d", 2, "1:11 2:21 3:30 4:40", DisplayName = "Filtered body")]
    [DataRow("update d set v = u.w from (select id, v from t) d join u on u.id = d.id", 2, "1:100 2:200 3:30 4:40", DisplayName = "Joined, first partner's value")]
    [DataRow("update d set b = b + 1 from (select id, v from t where id < 3) d (a, b)", 2, "1:11 2:21 3:30 4:40", DisplayName = "Column list")]
    [DataRow("update d set v = e.v + 1 from (select id, v from t) d join (select id, v from t) e on e.id = d.id + 1", 3, "1:21 2:31 3:41 4:40", DisplayName = "Joined to another derived table")]
    [DataRow("update e set v = 9 from (select id, v from (select id, v from t where id > 1) d) e where id = 2", 1, "1:10 2:9 3:30 4:40", DisplayName = "Over a derived table")]
    [DataRow("update top (1) d set v = 0 from (select id, v from t) d", 1, "1:0 2:20 3:30 4:40", DisplayName = "TOP")]
    [DataRow("update d set v = 5 from (select id, v from t where id = 99) d", 0, "1:10 2:20 3:30 4:40", DisplayName = "No row")]
    public void Update_WritesThroughTheDerivedTable(string update, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(update));
        AreEqual(expected, simulation.ExecuteScalar(Rows));
    }

    [TestMethod]
    [DataRow("delete d from (select top 2 * from t order by x) d", 2, "1:10 2:20", DisplayName = "Row-limited body")]
    [DataRow("delete d from (select id, row_number() over (order by v desc) rn from t) d where rn > 2", 2, "3:30 4:40", DisplayName = "Windowed body")]
    [DataRow("delete d from (select id, v from t) d join u on u.id = d.id", 2, "3:30 4:40", DisplayName = "Joined")]
    public void Delete_RemovesTheDerivedTablesRows(string delete, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(delete));
        AreEqual(expected, simulation.ExecuteScalar(Rows));
    }

    /// <summary>
    /// Through a derived table over a join, the SET list writes the one table
    /// its columns land in.
    /// </summary>
    [TestMethod]
    public void JoinBody_WritesTheTableTheSetListNames()
    {
        var simulation = Setup();
        AreEqual(3, simulation.ExecuteNonQuery("update d set w = 2 from (select t.id, t.v, u.w from t join u on t.id = u.id) d"));
        AreEqual("1:2 2:2 2:2", simulation.ExecuteScalar("select string_agg(concat(id, ':', w), ' ') within group (order by id) from u"));
    }

    /// <summary>
    /// The derived table's alias outranks a table of the same name as the
    /// target.
    /// </summary>
    [TestMethod]
    public void AliasNamingATable_WritesTheDerivedTable()
    {
        var simulation = Setup();
        simulation.ExecuteBatches("create table d (id int); insert d values (1)");
        AreEqual(1, simulation.ExecuteNonQuery("update d set v = 3 from (select id, v from t) d where id = 1"));
        AreEqual("1:3 2:20 3:30 4:40", simulation.ExecuteScalar(Rows));
    }

    /// <summary>
    /// <c>OUTPUT</c> reads the derived table's columns, under its column list.
    /// </summary>
    [TestMethod]
    public void Output_ReadsTheDerivedTablesColumns()
    {
        var simulation = Setup();
        AreEqual(
            "1:10>11 2:20>21",
            simulation.ExecuteScalar(
                """
                declare @o table (a int, before int, after int);
                update d set b = b + 1 output inserted.a, deleted.b, inserted.b into @o from (select id, v from t where id < 3) d (a, b);
                select string_agg(concat(a, ':', before, '>', after), ' ') within group (order by a) from @o
                """));
        _ = simulation.AssertSqlError("update d set v = 1 output inserted.x from (select id, v from t) d", 207);
    }

    /// <summary>
    /// Real's refusals of a derived-table target name the alias with its own
    /// messages — save a <c>DELETE</c> over a join, which is the view's Msg
    /// 4405 — and the source of a write through one reads it as a view.
    /// </summary>
    [TestMethod]
    [DataRow("update d set q = 1 from (select id, v + 1 q from t) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Derived column")]
    [DataRow("update d set c = 1 from (select id, count(*) c from t group by id) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Aggregate column")]
    [DataRow("update d set id = 1 from (select id, count(*) c from t group by id) d", 4418, "Derived table 'd' is not updatable because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", DisplayName = "Grouped body")]
    [DataRow("delete d from (select distinct id, v from t) d", 4418, "Derived table 'd' is not updatable because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", DisplayName = "DISTINCT body")]
    [DataRow("update d set v = 1 from (select id, v from t union all select id, w from u) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "UNION ALL, UPDATE")]
    [DataRow("delete d from (select id, v from t union all select id, w from u) d", 4417, "Derived table 'd' is not updatable because the definition contains a UNION operator.", DisplayName = "UNION ALL, DELETE")]
    [DataRow("update d set v = 1 from (select id, v from (select id, v from t union select id, w from u) x) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Over a UNION")]
    [DataRow("update d set v = 1, w = 2 from (select t.id, t.v, u.w from t join u on t.id = u.id) d", 4420, "Derived table 'd' is not updatable because the modification affects multiple base tables.", DisplayName = "SET spanning a join")]
    [DataRow("delete d from (select t.id, t.v, u.w from t join u on t.id = u.id) d", 4405, "View or function 'd' is not updatable because the modification affects multiple base tables.", DisplayName = "DELETE over a join")]
    [DataRow("update e set id = 1 from (select id from (select id, count(*) k from t group by id) d) e", 4418, "Derived table 'e' is not updatable because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.", DisplayName = "Over a grouped derived table")]
    public void Target_RefusedWithTheDerivedTableMessages(string statement, int number, string message)
    {
        var simulation = Setup();
        simulation.AssertSqlError(statement, number, message);
    }

    /// <summary>
    /// A refusal raised while the statement's <c>FROM</c> clause was still
    /// ahead of the parser leaves the batch's next statement to run, rather
    /// than resuming inside the derived table's body.
    /// </summary>
    [TestMethod]
    public void Refusal_ResumesAfterTheStatement()
    {
        var simulation = Setup();
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("update d set q = 1 from (select id, v + 1 q from t) d");
        var error = Throws<SimulatedSqlException>(() => command.ExecuteNonQuery());
        AreEqual(4421, error.Number);
        AreEqual(1, error.Errors.Count);
    }

    /// <summary>
    /// A view, CTE or join view whose body reads a derived table writes
    /// through it to the table it reads, the derived table's filter applied.
    /// </summary>
    [TestMethod]
    public void ViewOverDerivedTable_WritesThroughIt()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view v1 as select id, v from (select id, v, x from t where x > 2) d where v < 40",
            "create view v2 as select id, q from (select id, v + 1 q from t) d",
            "create view vj as select d.id, d.v, u.w from (select id, v from t) d join u on u.id = d.id");
        AreEqual(3, simulation.ExecuteNonQuery("update v1 set v = v + 1"));
        AreEqual(0, simulation.ExecuteNonQuery("delete v1 where id = 4"));
        AreEqual(1, simulation.ExecuteNonQuery("insert v1 (id, v) values (8, 80)"));
        AreEqual("1:11 2:21 3:31 4:40 8:80", simulation.ExecuteScalar(Rows));
        _ = simulation.AssertSqlError("update v2 set q = 1", 4406);
        AreEqual(1, simulation.ExecuteNonQuery("update vj set v = -1 where id = 1"));
        AreEqual(1, simulation.ExecuteNonQuery("with c as (select id, v from (select id, v from t) d) update c set v = -2 where id = 2"));
        AreEqual("1:-1 2:-2 3:31 4:40 8:80", simulation.ExecuteScalar(Rows));
        _ = simulation.AssertSqlError("delete vj", 4405);
    }

    /// <summary>
    /// An <c>APPLY</c>'s correlated body as a joined write's target writes
    /// through to the table it reads, each row once, as the join runs it.
    /// </summary>
    [TestMethod]
    [DataRow("update d set v = d.v + u.w from u cross apply (select id, v from t where t.id = u.id) d where u.w < 201", 2, "1:110 2:220 3:30 4:40", DisplayName = "CROSS APPLY, first partner")]
    [DataRow("update d set v = 0 from u cross apply (select top 1 id, v from t where t.id > u.id order by t.id) d", 2, "1:10 2:0 3:0 4:40", DisplayName = "Row-limited body")]
    [DataRow("delete d from u cross apply (select id from t where t.id = u.id) d where u.w = 201", 1, "1:10 3:30 4:40", DisplayName = "DELETE")]
    [DataRow("update d set v = 9 from u outer apply (select id, v from t where t.id = u.id + 1) d", 2, "1:10 2:9 3:9 4:40", DisplayName = "OUTER APPLY")]
    [DataRow("update d set v = -d.v from t a cross apply (select id, v from t where t.id = a.id and a.x > 3) d", 2, "1:-10 2:-20 3:30 4:40", DisplayName = "Over the table it writes")]
    public void ApplyBody_WritesThrough(string statement, int rowCount, string expected)
    {
        var simulation = Setup();
        AreEqual(rowCount, simulation.ExecuteNonQuery(statement));
        AreEqual(expected, simulation.ExecuteScalar(Rows));
    }

    /// <summary>
    /// A joined <c>UPDATE</c> through an <c>APPLY</c>'s correlated body that
    /// reads several sources is not modeled yet.
    /// </summary>
    [TestMethod]
    public void JoiningApplyBody_UpdateNotModeled()
    {
        var simulation = Setup();
        _ = Throws<NotSupportedException>(() => simulation.ExecuteNonQuery("update d set v = 0 from u cross apply (select t.id, t.v from t join u u2 on u2.id = t.id where t.id = u.id) d"));
    }

    /// <summary>
    /// A body reading a table value constructor or a rowset function takes
    /// every column as derived: an <c>UPDATE</c> or <c>INSERT</c> is Msg 4406
    /// (4421 through a derived table) and a <c>DELETE</c> Msg 4406 whatever
    /// the target — an <c>APPLY</c> body, a stored view, a CTE.
    /// </summary>
    [TestMethod]
    [DataRow("update d set a = 1 from (values (1)) d(a)", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "VALUES as the target")]
    [DataRow("delete d from (values (1)) d(a)", 4406, "Update or insert of view or function 'd' failed because it contains a derived or constant field.", DisplayName = "VALUES as the target, DELETE")]
    [DataRow("update d set value = 'x' from u cross apply string_split('a,b', ',') d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Rowset function as the target")]
    [DataRow("delete d from (select * from openjson('[1]') with (a int '$')) d", 4406, "Update or insert of view or function 'd' failed because it contains a derived or constant field.", DisplayName = "Derived table over OPENJSON, DELETE")]
    [DataRow("update d set a = 1 from (select a from (values (1)) x(a)) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Derived table over VALUES")]
    [DataRow("with c as (select * from (values (1)) x(a)) delete c", 4406, "Update or insert of view or function 'c' failed because it contains a derived or constant field.", DisplayName = "CTE over VALUES")]
    [DataRow("update vj set a = 1", 4406, "Update or insert of view or function 'vj' failed because it contains a derived or constant field.", DisplayName = "View over OPENJSON")]
    [DataRow("delete vj", 4406, "Update or insert of view or function 'vj' failed because it contains a derived or constant field.", DisplayName = "View over OPENJSON, DELETE")]
    [DataRow("insert vv values (1, 2)", 4406, "Update or insert of view or function 'vv' failed because it contains a derived or constant field.", DisplayName = "View over VALUES, INSERT")]
    [DataRow("update d set v = 5 from u cross apply (select count(*) v from t where t.id = u.id) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.", DisplayName = "Aggregate APPLY body")]
    public void ConstructedRows_EveryColumnDerived(string statement, int number, string message)
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create view vj as select * from openjson('[{\"a\":1}]') with (a int '$.a')",
            "create view vv as select * from (values (1, 2)) x(a, b)");
        simulation.AssertSqlError(statement, number, message);
    }

    /// <summary>
    /// An <c>UPDATE</c> whose target is a table value constructor reading an
    /// <c>APPLY</c>'s left side ends the session, even in a branch the batch
    /// never takes; a <c>DELETE</c> of one is Msg 4406.
    /// </summary>
    [TestMethod]
    public void UpdateOfACorrelatedConstructor_EndsTheSession()
    {
        var simulation = Setup();
        _ = simulation.AssertSqlError("delete d from u cross apply (values (u.id)) d(id)", 4406);
        using var connection = simulation.CreateOpenConnection();
        var ended = Throws<SimulatedSqlException>(() => connection.CreateCommand("select 1; if 1 = 0 update d set id = 5 from u cross apply (values (u.id)) d(id)").ExecuteNonQuery());
        AreEqual(596, ended.Number);
        AreEqual(System.Data.ConnectionState.Closed, connection.State);
    }

    /// <summary>
    /// A view, derived table or CTE over a view carrying an <c>INSTEAD OF</c>
    /// trigger hands the trigger the view's rows it shows, its filter
    /// applied, the statement naming the level's columns.
    /// </summary>
    [TestMethod]
    public void InsteadOfTriggerUnderTheTarget_Fires()
    {
        var simulation = Setup();
        simulation.ExecuteBatches(
            "create table log1 (n int identity, msg varchar(40))",
            "create view vi as select id, v, v * 2 dv from t",
            "create trigger tri on vi instead of update as set nocount on; insert log1 (msg) select concat('u:', i.id, ':', i.v, ':', d.v) from inserted i join deleted d on d.id = i.id",
            "create trigger trd on vi instead of delete as set nocount on; insert log1 (msg) select concat('d:', id) from deleted");
        AreEqual(2, simulation.ExecuteNonQuery("update d set v = d.q from (select id, v, v + 1 q from vi where id < 3) d"));
        AreEqual(1, simulation.ExecuteNonQuery("with c as (select id, v nv from vi where v > 35) update c set nv = 99"));
        AreEqual(1, simulation.ExecuteNonQuery("with c as (select id from vi) delete c where id = 2"));
        AreEqual(1, simulation.ExecuteNonQuery("delete d from (select id, dv from vi where dv = 60) d"));
        AreEqual("u:1:11:10 u:2:21:20 u:4:99:40 d:2 d:3", simulation.ExecuteScalar("select string_agg(msg, ' ') within group (order by n) from log1"));
        simulation.ExecuteBatches(
            "create trigger tii on vi instead of insert as set nocount on; insert log1 (msg) select concat('i:', id, ':', v, ':', dv) from inserted",
            "create view over_vi as select id, v as w from vi where v > 25");
        AreEqual(2, simulation.ExecuteNonQuery("update over_vi set w = w + 1"));
        AreEqual(1, simulation.ExecuteNonQuery("with c as (select id, v nv from vi) insert c values (9, 90)"));
        AreEqual(1, simulation.ExecuteNonQuery("insert over_vi (id) values (8)"));
        AreEqual("u:3:31:30 u:4:41:40 i:9:90: i:8::", simulation.ExecuteScalar("select string_agg(msg, ' ') within group (order by n) from log1 where n > 5"));
        AreEqual("1:10 2:20 3:30 4:40", simulation.ExecuteScalar(Rows));
        simulation.AssertSqlError("update d set q = 1 from (select id, v + 1 q from vi) d", 4421, "Derived table 'd' is not updatable because a column of the derived table is derived or constant.");
    }
}
