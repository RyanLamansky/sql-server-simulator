using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// An equality whose bucket holds most of a table reads it as a scan does:
/// its rows come back in the scan's order — the clustered key's, or a heap's
/// write order — a conjunct written after the equality raises on no row the
/// equality rejects, a READ COMMITTED read waits on another session's row the
/// equality rejects, and a REPEATABLE READ one keeps only the rows it returns.
/// Every expectation probed 2026-10-11 against SQL Server 2025, whose plan
/// scans these tables.
/// </summary>
[TestClass]
public sealed class LowCardinalityEqualityTests
{
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Two 2,000-row tables with nine rows in ten at <c>f = 1</c>, written out
    /// of key order: <c>t</c> clustered on <c>id</c>, <c>h</c> a heap. The one
    /// row with <c>v = 0</c> has <c>f = 2</c>, as does <c>t</c>'s row 1998.
    /// </summary>
    private static Simulation Flags()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (id int not null primary key, f int not null, v int not null, pad char(50) not null default 'x');
            create index ix_f on t (f);
            insert t (id, f, v) select 2001 - value, case when value % 10 = 3 then 2 else 1 end, case when value = 503 then 0 else 1 end
                from generate_series(1, 2000);
            create table h (id int not null, f int not null, v int not null, pad char(50) not null default 'x');
            create index ix_f on h (f);
            insert h (id, f, v) select (value * 7919) % 2000, case when value % 10 = 3 then 2 else 1 end, case when value = 503 then 0 else 1 end
                from generate_series(1, 2000)
            """);
        return simulation;
    }

    private static string Ids(Simulation simulation, string sql)
    {
        using var reader = simulation.ExecuteReader(sql);
        var ids = new List<string>();
        while (reader.Read())
            ids.Add($"{reader.GetInt32(0)}");
        return string.Join(",", ids);
    }

    [TestMethod]
    public void ClusteredTable_ReadsByTheKey()
    {
        var simulation = Flags();
        AreEqual("1,2,3,4,5,6,7,9,10,11", Ids(simulation, "select top 10 id from t where f = 1"));
        var all = Ids(simulation, "select id from t where f = 1").Split(',').Select(int.Parse).ToList();
        HasCount(1800, all);
        IsTrue(all.SequenceEqual(all.Order()));
    }

    [TestMethod]
    public void Heap_ReadsInWriteOrder() =>
        AreEqual("1919,1838,1676,1595,1514,1433,1352,1271,1190,1109,1028,866", Ids(Flags(), "select top 12 id from h where f = 1"));

    [TestMethod]
    [DataRow("select count(*) from t where f = 1 and 10 / v > 0")]
    [DataRow("select count(*) from t where 10 / v > 0 and f = 1")]
    [DataRow("select count(*) from h where f = 1 and 10 / v > 0")]
    [DataRow("select count(*) from h where 10 / v > 0 and f = 1")]
    public void ConjunctAfterTheEquality_RaisesOnNoRowTheEqualityRejects(string sql) =>
        AreEqual(1800, Flags().ExecuteScalar(sql));

    [TestMethod]
    public async Task ReadCommitted_WaitsOnARowTheEqualityRejects()
    {
        var simulation = Flags();
        using var writer = simulation.CreateOpenConnection();
        using var reader = simulation.CreateOpenConnection();
        _ = writer.CreateCommand("begin tran; update t set v = 2 where id = 1998").ExecuteNonQuery();

        var ex = await Task.Run(() =>
            Throws<SimulatedSqlException>(() =>
                reader.CreateCommand("set lock_timeout 0; select sum(v) from t where f = 1").ExecuteScalar()),
            TestContext.CancellationToken);
        AreEqual(1222, ex.Number);
        _ = writer.CreateCommand("rollback").ExecuteNonQuery();
    }

    [TestMethod]
    public void RepeatableRead_KeepsOnlyTheRowsItReturns()
    {
        var simulation = Flags();
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("set transaction isolation level repeatable read; begin tran").ExecuteNonQuery();
        AreEqual(1800, connection.CreateCommand("select sum(v) from t where f = 1").ExecuteScalar());
        AreEqual(1800, connection.CreateCommand("""
            select count(*) from sys.dm_tran_locks
            where request_session_id = @@spid and resource_type = 'KEY' and request_mode = 'S'
            """).ExecuteScalar());
        _ = connection.CreateCommand("rollback").ExecuteNonQuery();
    }
}
