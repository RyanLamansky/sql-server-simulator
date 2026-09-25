using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// DML naming one of its statement's own CTEs as the target writes through it
/// as through a view with that body. Probed against SQL Server 2025 on
/// 2026-09-25.
/// </summary>
[TestClass]
public sealed class CteDmlTests
{
    private const string Setup = "create table t (id int, v int); insert t values (1, 1), (1, 2), (2, 3);";

    [TestMethod]
    [DataRow("with d as (select id, v from t where id = 1) delete from d where v = 2; select count(*) from t", 2)]
    [DataRow("with d as (select id, v from t) update d set v = 9 where id = 2; select sum(v) from t", 12)]
    [DataRow("with d (i, w) as (select id, v from t) update d set w = 0 where i = 1; select sum(v) from t", 3)]
    [DataRow("with d as (select id, v from t) insert into d values (5, 5); select count(*) from t", 4)]
    [DataRow("with d as (select id, v from t where id = 1) delete from d; select count(*) from t", 1)]
    public void WritesPassThroughToTheTable(string sql, int expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(Setup + sql));

    [TestMethod]
    [DataRow("with d as (select id, v * 2 w from t) update d set w = 1; select 5", 4406, "Update or insert of view or function 'd' failed because it contains a derived or constant field.")]
    [DataRow("with d as (select id, count(*) c from t group by id) delete from d; select 5", 4403, "Cannot update the view or function 'd' because it contains aggregates, or a DISTINCT or GROUP BY clause, or PIVOT or UNPIVOT operator.")]
    [DataRow("with d as (select id, count(*) c from t group by id) update d set id = 2 where id = 1; select 5", 4403, null)]
    public void RefusalsEndTheBatch(string sql, int number, string? message)
    {
        var ex = new Simulation().AssertSqlError(Setup + sql, number);
        HasCount(1, ex.Errors);
        if (message is not null)
            AreEqual(message, ex.Errors[0].Message);
    }

    /// <summary>
    /// A row-limited or windowed body writes only to the rows it yields — the
    /// dedupe idiom among them — and reads its derived columns off those rows.
    /// </summary>
    [TestMethod]
    [DataRow("with d as (select *, row_number() over (partition by id order by v) rn from t) delete from d where rn > 1; select string_agg(concat(id, ':', v), ',') within group (order by id) from t", "1:1,2:3")]
    [DataRow("with d as (select *, row_number() over (partition by id order by v desc) rn from t) update d set v = 0 where rn = 1; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:0,1:1,2:0")]
    [DataRow("with d as (select top 1 * from t order by v desc) delete from d; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:1,1:2")]
    [DataRow("create table u (id int, v int); insert u values (1, 1), (2, 2), (3, 3); with d as (select top 2 id, v * 2 w from u order by v desc) delete from d where w > 4; select string_agg(concat(id, ':', v), ',') within group (order by id) from u", "1:1,2:2")]
    [DataRow("with d as (select id, v from t order by v offset 1 rows fetch next 1 rows only) delete from d; select string_agg(concat(id, ':', v), ',') within group (order by id, v) from t", "1:1,2:3")]
    public void RowSelectingBody_WritesTheRowsItYields(string sql, string expected)
        => AreEqual(expected, new Simulation().ExecuteScalar(Setup + sql));

    /// <summary>
    /// A limit that picks between rows its body's projection can't tell apart
    /// refuses rather than guess, as does a MERGE through a row-selecting body.
    /// </summary>
    [TestMethod]
    [DataRow("create table u (id int, v int); insert u values (1, 5), (1, 9); with d as (select top 1 id from u order by v desc) delete from d")]
    [DataRow("with d as (select top 1 * from t order by v) merge d using (values (1)) s(x) on d.id = s.x when matched then delete;")]
    public void UndecidableRowSelection_IsRefusedWithoutWriting(string sql)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        _ = Throws<NotSupportedException>(() => command.ExecuteNonQuery());
        AreEqual(3, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void RowLimitedView_WritesTheRowsItYields()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(
            Setup,
            "create view vtop as select top 1 * from t order by v desc",
            "create view vrn as select *, row_number() over (partition by id order by v) rn from t");
        _ = simulation.ExecuteNonQuery("delete from vtop");
        AreEqual(3, simulation.ExecuteScalar("select sum(v) from t"));
        _ = simulation.ExecuteNonQuery("delete from vrn where rn > 1");
        AreEqual(1, simulation.ExecuteScalar("select sum(v) from t"));
        _ = simulation.AssertSqlError("update vrn set rn = 5", 4406);
        AreEqual(1, simulation.ExecuteNonQuery("insert vtop values (4, 4)"));
    }
}
