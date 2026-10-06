using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using static SqlServerSimulator.TestHelpers;

namespace SqlServerSimulator;

/// <summary>
/// The <c>$IDENTITY</c> and <c>$ROWGUID</c> pseudo-columns: a bare token, in
/// any case, reads the source's identity column or <c>ROWGUIDCOL</c> and
/// names the result after it — through a view or CTE that passes it straight
/// through, but never a derived table — while a delimited <c>[$identity]</c>
/// is an ordinary name (probed 2026-10-06 against SQL Server 2025).
/// </summary>
[TestClass]
public sealed class IdentityPseudoColumnTests
{
    private static Simulation WithTable()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table t (id int identity(5, 1), v int); insert t (v) values (1), (2)");
        return sim;
    }

    [TestMethod]
    public void Identity_ReadsTheIdentityColumn()
    {
        var sim = WithTable();
        using var reader = sim.ExecuteReader("select $identity, $IDENTITY + 1 as x from t order by $identity");
        AreEqual("id", reader.GetName(0));
        IsTrue(reader.Read());
        AreEqual(5, reader.GetInt32(0));
        AreEqual(6, reader.GetInt32(1));
        CollectionAssert.AreEqual(new[] { false, true }, sim.ColumnNullability("select $identity, $identity + 1 from t"));
    }

    [TestMethod]
    [DataRow("select t.$identity from t where $identity > 5", 6)]
    [DataRow("select x.$identity from t x where x.$identity = 6", 6)]
    [DataRow("with c as (select id as k from t) select max($identity) from c", 6)]
    [DataRow("declare @t table (id int identity(3, 1), v int); insert @t (v) values (4); select $identity from @t", 3)]
    [DataRow("select count(*) from t group by $identity having $identity = 5", 1)]
    public void Identity_ResolvesAcrossShapes(string sql, int expected)
        => AreEqual(expected, WithTable().ExecuteScalar(sql));

    [TestMethod]
    public void Identity_ThroughAView()
    {
        var sim = WithTable();
        sim.ExecuteBatches("create view v as select id, v from t");
        AreEqual(6, sim.ExecuteScalar("select max($identity) from v"));
    }

    [TestMethod]
    [DataRow("select $identity from (select id from t) d")]
    [DataRow("select d.$identity from (select * from t) d")]
    [DataRow("select [$identity] from t")]
    [DataRow("with c as (select id + 0 as id from t) select $identity from c")]
    public void Identity_Msg207WhereNothingCarriesIt(string sql)
        => WithTable().AssertSqlError(sql, 207, "Invalid column name '$identity'.");

    [TestMethod]
    public void Identity_TableWithoutOne_Msg207AsWritten()
        => AssertSqlError("create table u (v int); select $IDENTITY from u", 207, "Invalid column name '$IDENTITY'.");

    [TestMethod]
    public void Identity_TwoSourcesCarryingOne_Msg209()
    {
        var sim = WithTable();
        sim.ExecuteBatches("create table u (id2 bigint identity, w int); insert u (w) values (2)");
        _ = sim.AssertSqlError("select $identity from t cross join u", 209);
        AreEqual(5, sim.ExecuteScalar("select min($identity) from t cross join (select 1 z) x"));
    }

    [TestMethod]
    public void Identity_InWritesAndOutput()
    {
        var sim = WithTable();
        AreEqual(7, sim.ExecuteScalar("insert t (v) output inserted.$identity values (3)"));
        _ = sim.ExecuteNonQuery("update t set v = 9 where $identity = 5; delete t where $identity = 6");
        AreEqual("5:9,7:3", sim.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void RowGuid_ReadsTheRowGuidColumn()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create table g (k uniqueidentifier rowguidcol default '00000000-0000-0000-0000-000000000001', v int); insert g (v) values (1)");
        using var reader = sim.ExecuteReader("select $rowguid, $ROWGUID from g");
        AreEqual("k", reader.GetName(0));
        IsTrue(reader.Read());
        AreEqual(new Guid("00000000-0000-0000-0000-000000000001"), reader.GetGuid(1));
        _ = sim.AssertSqlError("create table h (v int); select $rowguid from h", 207);
    }
}
