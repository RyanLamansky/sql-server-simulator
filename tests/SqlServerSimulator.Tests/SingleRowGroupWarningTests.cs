using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// A GROUP BY whose groups each provably hold one row — its columns determine
/// a key of every source — aggregates no NULL away, so it sends no Msg 8153
/// (probed 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class SingleRowGroupWarningTests
{
    private const string Setup = """
        create table t (k int primary key, j int, v int); insert t values (1, 1, null), (2, 1, 3);
        create table u (k int primary key, w int); insert u values (1, null), (2, 3);
        create table n (k int, v int); insert n values (1, null), (2, 3);
        create table x (k int, v int); create unique index ux on x (k); insert x values (1, null), (2, 3);
        create table f (k int, v int); create unique index uf on f (k) where k > 0; insert f values (1, null), (2, 3);
        create table c (a int, b int, v int, primary key (a, b)); insert c values (1, 1, null), (1, 2, 3);
        """;

    [TestMethod]
    [DataRow("select k, sum(v) from t group by k", false)]
    [DataRow("select k, sum(v) from t group by k, v", false)]
    [DataRow("select k, min(v), max(v), avg(v), sum(distinct v) from t group by k", false)]
    [DataRow("select k, sum(v) from t where k > 0 group by k", false)]
    [DataRow("select j, sum(v) from t where k = 1 group by j", false)]
    [DataRow("select k, sum(v) from x group by k", false)]
    [DataRow("select a, b, sum(v) from c group by a, b", false)]
    [DataRow("select t.k, sum(t.v) from t join u on t.k = u.k group by t.k", false)]
    [DataRow("select t.k, sum(u.w) from t left join u on t.k = u.k group by t.k", false)]
    [DataRow("select t.k, sum(u.w) from t, u where t.k = u.k group by t.k", false)]
    [DataRow("select k, sum(v) from (select top 10 k, v from t) d group by k", false)]
    [DataRow("select k, sum(v) from (select distinct k, v from t) d group by k", false)]
    [DataRow("with w as (select k, v from t) select k, sum(v) from w group by k", false)]
    [DataRow("select x, sum(y) from (values (1, null), (2, 3)) v(x, y) group by x", false)]
    [DataRow("select x, sum(y) from (values (-1, null), (1, 3)) v(x, y) group by x", false)]
    [DataRow("select x, sum(y) from (select 1, null union all select 2, 3) v(x, y) group by x", false)]
    [DataRow("select k, sum(v) from n group by k", true)]
    [DataRow("select k, sum(v) from f group by k", true)]
    [DataRow("select a, sum(v) from c group by a", true)]
    [DataRow("select k + 0, sum(v) from t group by k + 0", true)]
    [DataRow("select k, sum(v) from t group by k with rollup", true)]
    [DataRow("select n.k, sum(n.v) from n join t on t.k = n.k group by n.k", true)]
    [DataRow("select x, sum(y) from (values (1, null), (1, 3)) v(x, y) group by x", true)]
    [DataRow("select x, sum(y) from (values ('a', null), ('A', 3)) v(x, y) group by x", true)]
    [DataRow("select k, sum(v) over (partition by k) from t", true)]
    public void NullEliminatedWarning(string sql, bool warns)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        _ = connection.CreateCommand(Setup).ExecuteNonQuery();
        var log = new List<int>();
        connection.InfoMessage += (_, e) => log.Add(e.Errors[0].Number);
        using (var reader = connection.CreateCommand(sql).ExecuteReader())
        {
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
        }
        AreEqual(warns ? 1 : 0, log.Count(number => number == 8153));
    }

    /// <summary>A view passes its table's key through to a GROUP BY over it.</summary>
    [TestMethod]
    public void ViewOverAKeyedTable_SendsNoWarning()
    {
        var simulation = new Simulation();
        simulation.ExecuteBatches(Setup, "create view vw as select k, v from t");
        var connection = simulation.CreateDbConnection();
        connection.Open();
        var log = new List<int>();
        connection.InfoMessage += (_, e) => log.Add(e.Errors[0].Number);
        _ = connection.CreateCommand("select k, sum(v) from vw group by k").ExecuteNonQuery();
        IsEmpty(log);
    }
}
