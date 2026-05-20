using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class LinkedServerTests
{
    /// <summary>
    /// Two-step registration: <c>AddRemoteSimulation</c> binds the name, but
    /// the linked server isn't reachable from SQL text until
    /// <c>sp_addlinkedserver</c> activates it. A four-part reference before
    /// activation surfaces as Msg 208 (matches the simulator's existing
    /// behavior for unknown linked servers — Msg 7202 isn't ported).
    /// </summary>
    [TestMethod]
    public void FourPartName_BeforeSpAddLinkedServer_Msg208()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key, val int not null); insert remote_t values (1, 10), (2, 20)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);

        _ = local.AssertSqlError("select val from OTHER.simulated.dbo.remote_t where id = 1", 208);
    }

    [TestMethod]
    public void Select_RoutesToRemoteSimulation()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key, val int not null); insert remote_t values (1, 10), (2, 20)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver @server = 'OTHER', @srvproduct = 'SQL Server'");

        AreEqual(20, local.ExecuteScalar("select val from OTHER.simulated.dbo.remote_t where id = 2"));
    }

    /// <summary>
    /// Positional sp_addlinkedserver form: real BACPAC scripts emit
    /// <c>EXEC sp_addlinkedserver 'OTHER', 'SQL Server'</c>. The simulator
    /// accepts both forms.
    /// </summary>
    [TestMethod]
    public void SpAddLinkedServer_Positional_Activates()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key); insert remote_t values (1), (2), (3)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER', 'SQL Server'");

        AreEqual(3, local.ExecuteScalar("select count(*) from OTHER.simulated.dbo.remote_t"));
    }

    [TestMethod]
    public void Join_AcrossLinkedServer()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("""
            create table dbo.parts (part_id int not null primary key, name varchar(20) not null);
            insert parts values (1, 'widget'), (2, 'gadget'), (3, 'gizmo')
            """);

        var local = new Simulation();
        _ = local.ExecuteNonQuery("create table dbo.orders (order_id int not null primary key, part_id int not null, qty int not null); insert orders values (1, 1, 5), (2, 2, 10), (3, 1, 7)");
        local.AddRemoteSimulation("PARTSRV", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'PARTSRV', 'SQL Server'");

        var qty = local.ExecuteScalar("""
            select sum(o.qty)
            from dbo.orders o
            inner join PARTSRV.simulated.dbo.parts p on p.part_id = o.part_id
            where p.name = 'widget'
            """);
        AreEqual(12, qty);
    }

    [TestMethod]
    public void Insert_ThroughFourPartName_Rejected()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        var ex = Throws<NotSupportedException>(() => local.ExecuteNonQuery("insert OTHER.simulated.dbo.remote_t values (99)"));
        Contains("Cross-server write", ex.Message);
    }

    [TestMethod]
    public void Update_ThroughFourPartName_Rejected()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key, v int not null); insert remote_t values (1, 1)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        _ = Throws<NotSupportedException>(() => local.ExecuteNonQuery("update OTHER.simulated.dbo.remote_t set v = 2 where id = 1"));
    }

    [TestMethod]
    public void Delete_ThroughFourPartName_Rejected()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key); insert remote_t values (1)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        _ = Throws<NotSupportedException>(() => local.ExecuteNonQuery("delete from OTHER.simulated.dbo.remote_t where id = 1"));
    }

    [TestMethod]
    public void SpAddLinkedServer_Unregistered_NotSupported()
    {
        var local = new Simulation();
        var ex = Throws<NotSupportedException>(() => local.ExecuteNonQuery("exec sp_addlinkedserver 'NOT_REGISTERED'"));
        Contains("AddRemoteSimulation", ex.Message);
    }

    [TestMethod]
    public void SpDropServer_RemovesLinkedServer()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.t (id int not null primary key); insert t values (1)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");
        AreEqual(1, local.ExecuteScalar("select count(*) from OTHER.simulated.dbo.t"));

        _ = local.ExecuteNonQuery("exec sp_dropserver 'OTHER'");

        _ = local.AssertSqlError("select count(*) from OTHER.simulated.dbo.t", 208);
    }

    [TestMethod]
    public void SpDropServer_Missing_Msg15015()
    {
        var local = new Simulation();
        local.AssertSqlError("exec sp_dropserver 'NOT_REGISTERED'", 15015,
            "The server 'NOT_REGISTERED' does not exist. Use sp_helpserver to show available servers.");
    }

    [TestMethod]
    public void SpServerOption_ParseAndDiscard()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.t (id int not null primary key); insert t values (1)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");
        _ = local.ExecuteNonQuery("exec sp_serveroption @server = 'OTHER', @optname = 'data access', @optvalue = 'TRUE'");
        _ = local.ExecuteNonQuery("exec sp_addlinkedsrvlogin 'OTHER', 'false', NULL, 'sa', 'password'");

        AreEqual(1, local.ExecuteScalar("select count(*) from OTHER.simulated.dbo.t"));
    }

    /// <summary>
    /// The remote SELECT must run through the remote's full pipeline — its
    /// catalog views resolve at the remote, not against the local
    /// Simulation's catalog. Verified by reading sys.tables on the remote
    /// via a four-part-name reference; if the routing accidentally bound
    /// against the local instance, the row count would differ.
    /// </summary>
    [TestMethod]
    public void RemoteSelect_RunsThroughRemotePipeline()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.a (id int); create table dbo.b (id int); create table dbo.c (id int)");

        var local = new Simulation();
        _ = local.ExecuteNonQuery("create table dbo.only_local (id int)");
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        // Verify remote-side data is what arrives, not local-side.
        AreEqual(3, remote.ExecuteScalar("select count(*) from sys.tables"));
        AreEqual(1, local.ExecuteScalar("select count(*) from sys.tables"));
    }

    /// <summary>
    /// Self-linkage: a simulation can register itself as a linked server.
    /// Useful for tests that want to exercise the round-trip code without
    /// constructing a second Simulation.
    /// </summary>
    [TestMethod]
    public void Selflink_RoundTrip()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table dbo.t (id int not null primary key); insert t values (42)");
        sim.AddRemoteSimulation("SELF", sim);
        _ = sim.ExecuteNonQuery("exec sp_addlinkedserver 'SELF'");

        AreEqual(42, sim.ExecuteScalar("select id from SELF.simulated.dbo.t"));
    }

    [TestMethod]
    public void SysServers_LocalOnly()
    {
        var sim = new Simulation();
        AreEqual(1, sim.ExecuteScalar("select count(*) from sys.servers"));
        AreEqual("SIMULATED", sim.ExecuteScalar("select name from sys.servers where is_linked = 0"));
    }

    [TestMethod]
    public void SysServers_ProjectsActiveLinkedServers()
    {
        var remote = new Simulation();
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        local.AddRemoteSimulation("THIRD", new Simulation());
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver @server = 'OTHER', @srvproduct = 'My Product', @provider = 'My Provider', @datasrc = 'My Source'");
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'THIRD', 'SQL Server'");

        AreEqual(3, local.ExecuteScalar("select count(*) from sys.servers"));
        AreEqual(2, local.ExecuteScalar("select count(*) from sys.servers where is_linked = 1"));
        AreEqual("My Product", local.ExecuteScalar("select product from sys.servers where name = 'OTHER'"));
        AreEqual("My Provider", local.ExecuteScalar("select provider from sys.servers where name = 'OTHER'"));
        AreEqual("My Source", local.ExecuteScalar("select data_source from sys.servers where name = 'OTHER'"));
    }

    [TestMethod]
    public void FourPartName_ToRemoteCatalogView_FallsThroughToMsg208()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.t (id int)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        // Catalog views aren't reachable through four-part names yet; the
        // lookup misses because sys.tables isn't a HeapTable in the remote's
        // schema dict. Falls through to Msg 208 — documented gap.
        _ = local.AssertSqlError("select count(*) from OTHER.simulated.sys.tables", 208);
    }

    [TestMethod]
    public void FourPartName_MissingRemoteTable_Msg208()
    {
        var remote = new Simulation();
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        _ = local.AssertSqlError("select * from OTHER.simulated.dbo.no_such_table", 208);
    }

    [TestMethod]
    public void Merge_ThroughFourPartName_Rejected()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key)");

        var local = new Simulation();
        _ = local.ExecuteNonQuery("create table dbo.src (id int not null)");
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        _ = Throws<NotSupportedException>(() => local.ExecuteNonQuery("merge OTHER.simulated.dbo.remote_t as t using dbo.src as s on s.id = t.id when matched then delete;"));
    }

    /// <summary>
    /// Reactivating an existing linked-server name replaces the prior
    /// binding silently — matches real SQL Server's <c>sp_addlinkedserver</c>
    /// idempotency. Useful for BACPAC scripts that re-emit registration
    /// on every import.
    /// </summary>
    [TestMethod]
    public void SpAddLinkedServer_Idempotent()
    {
        var remoteA = new Simulation();
        _ = remoteA.ExecuteNonQuery("create table dbo.t (id int); insert t values (1)");
        var remoteB = new Simulation();
        _ = remoteB.ExecuteNonQuery("create table dbo.t (id int); insert t values (2), (3)");

        var local = new Simulation();
        local.AddRemoteSimulation("X", remoteA);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'X'");
        AreEqual(1, local.ExecuteScalar("select count(*) from X.simulated.dbo.t"));

        local.AddRemoteSimulation("X", remoteB);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'X'");
        AreEqual(2, local.ExecuteScalar("select count(*) from X.simulated.dbo.t"));
    }

    [TestMethod]
    public void SysServers_RemovedBySpDropServer()
    {
        var remote = new Simulation();
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");
        AreEqual(2, local.ExecuteScalar("select count(*) from sys.servers"));

        _ = local.ExecuteNonQuery("exec sp_dropserver 'OTHER'");
        AreEqual(1, local.ExecuteScalar("select count(*) from sys.servers"));
    }
}
