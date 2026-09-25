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
    /// A row-limited or windowed body writes only to the rows it yields on
    /// real, which the simulator refuses rather than write every row its
    /// filter admits.
    /// </summary>
    [TestMethod]
    [DataRow("with d as (select top 1 * from t order by v desc) delete from d")]
    [DataRow("with d as (select *, row_number() over (partition by id order by v) rn from t) delete from d where rn > 1")]
    public void RowSelectingBody_IsRefusedWithoutWriting(string sql)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery(Setup);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand(sql);
        _ = Throws<NotSupportedException>(() => command.ExecuteNonQuery());
        AreEqual(3, simulation.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void RowLimitedView_WriteIsRefusedWithoutWriting()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Setup, "create view vtop as select top 1 * from t order by v desc");
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("delete from vtop");
        _ = Throws<NotSupportedException>(() => command.ExecuteNonQuery());
        AreEqual(3, simulation.ExecuteScalar("select count(*) from t"));
        AreEqual(1, simulation.ExecuteNonQuery("insert vtop values (4, 4)"));
    }
}
