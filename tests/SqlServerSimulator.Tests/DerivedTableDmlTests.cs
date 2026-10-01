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
}
