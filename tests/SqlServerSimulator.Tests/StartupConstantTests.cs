using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The runtime constants SQL Server evaluates once as a plan starts — before
/// it reads a row — so an error in one raises over an empty table or a WHERE
/// that excludes every row, and the shapes whose plan never starts that way.
/// Probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class StartupConstantTests
{
    private static Simulation OneRow()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("""
            create table t (id int, v varchar(3));
            insert t values (1, 'a');
            create table e (id int);
            create table pk (id int primary key, v int);
            insert pk values (1, 10)
            """);
        return sim;
    }

    private static string Rows(Simulation sim, string sql)
    {
        using var reader = sim.ExecuteReader(sql);
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object[reader.FieldCount];
            _ = reader.GetValues(values);
            rows.Add(string.Join(",", values.Select(value => value is DBNull ? "NULL" : value.ToString())));
        }

        return string.Join("|", rows);
    }

    [TestMethod]
    [DataRow("select 1/0 from t where id = 999", 8134)]
    [DataRow("select 1/0 from e", 8134)]
    [DataRow("select id + 1/0 from e", 8134)]
    [DataRow("select cast('x' as int) from e", 245)]
    [DataRow("select power(2, 40) from e", 232)]
    [DataRow("select id from t where id = 999 and id in (1, 1/0)", 8134)]
    [DataRow("select id from t where id = 999 and id between 1/0 and 2", 8134)]
    [DataRow("select id from t where id = 999 or 1/0 = 1", 8134)]
    [DataRow("select isnull(id, 1/0) from t where id = 999", 8134)]
    [DataRow("select 1 from t join e on 1/0 = 1", 8134)]
    [DataRow("select * from (select 1/0 x from t) d where x = 5", 8134)]
    [DataRow("select 1/0 from pk where id in (98, 99)", 8134)]
    [DataRow("select id from e order by 1/0", 8134)]
    [DataRow("select id from e order by id, cast('x' as int)", 245)]
    [DataRow("select count(*) from e group by id order by 1/0", 8134)]
    public void ErrorInARuntimeConstant_RaisesWithNoRowRead(string sql, int number) =>
        OneRow().AssertSqlError(sql, number);

    [TestMethod]
    public void ComputationOverVariables_RaisesWithNoRowRead()
    {
        var sim = OneRow();
        _ = sim.AssertSqlError("declare @x int = 1; select @x / 0 from t where id = 999", 8134);
        _ = sim.AssertSqlError("declare @x int = 0; select id from t where id = 999 and id = 1 / @x", 8134);
    }

    [TestMethod]
    [DataRow("select choose(id, 1, 1/0) from t where id = 999")]
    [DataRow("select nullif(id, 1/0) from t where id = 999")]
    [DataRow("select case when id = 5 then 1/0 end from t where id = 999")]
    [DataRow("select iif(id = 5, 1/0, 0) from t where id = 999")]
    [DataRow("select coalesce(id, 1/0) from t where id = 999")]
    [DataRow("select 1/0 from t where 1 = 0")]
    [DataRow("select top 0 1/0 from t")]
    [DataRow("select 1/0 from (values (1)) v(x) where x = 5")]
    [DataRow("select * from (select 1/0 x from t) d where 1 = 0")]
    [DataRow("select id from e where exists (select 1/0 from t)")]
    [DataRow("select 1/0 from pk where id = 99")]
    [DataRow("select id from pk where id = 99 and v > 1/0")]
    [DataRow("select id from t where id = 999 group by id having count(*) > 1/0")]
    public void ShapeThatNeverStartsTheConstant_ReturnsNoRows(string sql) =>
        AreEqual("", Rows(OneRow(), sql));

    [TestMethod]
    public void AggregateOperand_IsNotAStartupConstant() =>
        AreEqual("NULL", Rows(OneRow(), "select sum(1/0) from e"));

    [TestMethod]
    public void OrderByConstantWhoseFoldRaises_IsASortKeyNotMsg408()
    {
        var sim = OneRow();
        _ = sim.AssertSqlError("select id from t order by 1/1", 408);
        _ = sim.AssertSqlError("select id from t order by 2147483647 + 1", 8115);
        _ = sim.AssertSqlError("select distinct id from t order by 1/0", 145);
        _ = sim.AssertSqlError("select id from t union all select 3 order by 1/0", 104);
    }

    [TestMethod]
    public void ScalarAggregateOrderBy_IsNeverEvaluated()
    {
        var sim = OneRow();
        AreEqual("1", Rows(sim, "select count(*) from t order by 1/0"));
        AreEqual("1", Rows(sim, "select count(*) from t order by max(id) / 0"));
    }

    [TestMethod]
    public void WindowKeyWhoseFoldRaises_IsNeverEvaluated()
    {
        var sim = OneRow();
        _ = sim.ExecuteNonQuery("insert t values (2, 'b')");
        AreEqual("1,1|2,2", Rows(sim, "select id, row_number() over (order by 1/0, id) from t order by id"));
        AreEqual("1,1|2,1", Rows(sim, "select id, rank() over (partition by cast('x' as int) order by 1/0) from t order by id"));
        AreEqual("1,1|2,3", Rows(sim, "select id, sum(id) over (order by cast('x' as int) rows unbounded preceding) from t order by id"));
    }

    [TestMethod]
    public void OrderByRowHash_IsNoConstant()
    {
        var sim = OneRow();
        _ = sim.ExecuteNonQuery("insert t values (2, 'b')");
        HasCount(2, Rows(sim, "select id from t order by checksum(*)").Split('|'));
        HasCount(2, Rows(sim, "select id from t order by binary_checksum(*)").Split('|'));
    }
}
