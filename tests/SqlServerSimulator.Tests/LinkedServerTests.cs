using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class LinkedServerTests
{
    /// <summary>
    /// Two-step registration: <c>AddRemoteSimulation</c> binds the name, but
    /// the linked server isn't reachable from SQL text until
    /// <c>sp_addlinkedserver</c> activates it, so a four-part reference before
    /// activation names a server <c>sys.servers</c> lacks (Msg 7202).
    /// </summary>
    [TestMethod]
    public void FourPartName_BeforeSpAddLinkedServer_Msg7202()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key, val int not null); insert remote_t values (1, 10), (2, 20)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);

        _ = local.AssertSqlError("select val from OTHER.simulated.dbo.remote_t where id = 1", 7202);
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
    public void Insert_ThroughFourPartName_LandsOnRemote()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        AreEqual(1, local.ExecuteNonQuery("insert OTHER.simulated.dbo.remote_t values (99)"));
        AreEqual(99, remote.ExecuteScalar("select id from remote_t"));
    }

    [TestMethod]
    public void Update_ThroughFourPartName_LandsOnRemote()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key, v int not null); insert remote_t values (1, 1), (2, 1)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        AreEqual(1, local.ExecuteNonQuery("update OTHER.simulated.dbo.remote_t set v = 2 where id = 1"));
        AreEqual(3, remote.ExecuteScalar("select sum(v) from remote_t"));
    }

    [TestMethod]
    public void Delete_ThroughFourPartName_LandsOnRemote()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key); insert remote_t values (1), (2)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        AreEqual(1, local.ExecuteNonQuery("delete from OTHER.simulated.dbo.remote_t where id = 1"));
        AreEqual(2, remote.ExecuteScalar("select id from remote_t"));
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

        _ = local.AssertSqlError("select count(*) from OTHER.simulated.dbo.t", 7202);
    }

    [TestMethod]
    public void SpDropServer_Missing_Msg15015()
    {
        var local = new Simulation();
        local.AssertSqlError("exec sp_dropserver 'NOT_REGISTERED'", 15015,
            "The server 'NOT_REGISTERED' does not exist. Use sp_helpserver to show available servers.");
    }

    [TestMethod]
    public void SpServerOption_DataAccessOn_KeepsReads()
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
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver @server = 'OTHER', @srvproduct = 'My Product', @provider = 'SQLNCLI11', @datasrc = 'My Source'");
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'THIRD', 'SQL Server'");

        AreEqual(3, local.ExecuteScalar("select count(*) from sys.servers"));
        AreEqual(2, local.ExecuteScalar("select count(*) from sys.servers where is_linked = 1"));
        AreEqual("My Product", local.ExecuteScalar("select product from sys.servers where name = 'OTHER'"));
        AreEqual("SQLNCLI11", local.ExecuteScalar("select provider from sys.servers where name = 'OTHER'"));
        AreEqual("My Source", local.ExecuteScalar("select data_source from sys.servers where name = 'OTHER'"));
    }

    /// <summary>
    /// A four-part catalog view reads the remote's catalog, not the local one.
    /// </summary>
    [TestMethod]
    public void FourPartName_ToRemoteCatalogView_ReadsRemoteCatalog()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.t (id int); create table dbo.u (id int)");

        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        AreEqual(2, local.ExecuteScalar("select count(*) from OTHER.simulated.sys.tables"));
        AreEqual("u", local.ExecuteScalar("select TABLE_NAME from OTHER.simulated.INFORMATION_SCHEMA.TABLES where TABLE_NAME > 't'"));
    }

    [TestMethod]
    public void FourPartName_MissingRemoteTable_Msg7314()
    {
        var remote = new Simulation();
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        local.AssertSqlError("select * from OTHER.simulated.dbo.no_such_table", 7314,
            "The OLE DB provider \"MSOLEDBSQL19\" for linked server \"OTHER\" does not contain the table \"\"simulated\".\"dbo\".\"no_such_table\"\". The table either does not exist or the current user does not have permissions on that table.");
    }

    [TestMethod]
    public void Merge_ThroughFourPartName_Msg5315()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table dbo.remote_t (id int not null primary key)");

        var local = new Simulation();
        _ = local.ExecuteNonQuery("create table dbo.src (id int not null)");
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'OTHER'");

        _ = local.AssertSqlError("merge OTHER.simulated.dbo.remote_t as t using dbo.src as s on s.id = t.id when matched then delete;", 5315);
    }

    /// <summary>
    /// Reactivating an existing linked-server name replaces the prior
    /// binding silently — matches real SQL Server's <c>sp_addlinkedserver</c>
    /// idempotency. Useful for BACPAC scripts that re-emit registration
    /// on every import.
    /// </summary>
    [TestMethod]
    public void SpAddLinkedServer_ExistingName_Msg15028_DropThenAddRebinds()
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
        var error = local.AssertSqlError("exec sp_addlinkedserver 'x'", 15028);
        AreEqual("The server 'x' already exists.", error.Errors[0].Message);
        AreEqual(102, error.LineNumber);
        _ = local.ExecuteNonQuery("exec sp_dropserver 'X'; exec sp_addlinkedserver 'X'");
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

    // The procedures refuse bad arguments with real's own signature rules
    // (probed 2026-09-25 against SQL Server 2025).
    [TestMethod]
    [DataRow("exec sp_addlinkedserver", 201)]
    [DataRow("exec sp_addlinkedserver null", 15004)]
    [DataRow("exec sp_addlinkedserver @server = 'a', @bogus = 1", 8145)]
    [DataRow("exec sp_addlinkedserver 'a','b','c','d','e','f','g','h'", 8114)]
    [DataRow("exec sp_addlinkedserver 'a','b','c','d','e','f','g',1,'i'", 8144)]
    [DataRow("exec sp_dropserver", 201)]
    [DataRow("exec sp_dropserver 'x', 'y', 'z'", 8144)]
    [DataRow("exec sp_dropserver null", 15015)]
    [DataRow("exec sp_dropserver 'x', 'bad'", 15600)]
    public void LinkedServerProcedures_RefuseBadArgumentsAsRealDoes(string sql, int number)
        => _ = new Simulation().AssertSqlError(sql, number);

    /// <summary>
    /// sys.servers carries real's 26 columns: the optional arguments land in
    /// location / provider_string / catalog, and a SQL Server product enables
    /// remote login and RPC out and names itself as its data source (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SysServers_CarriesRealsFlagsAndArguments()
    {
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", new Simulation());
        local.AddRemoteSimulation("THIRD", new Simulation());
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver @server = 'OTHER', @srvproduct = '', @provider = 'MSOLEDBSQL', @datasrc = 'h', @location = 'loc', @provstr = 'ps=1', @catalog = 'cat'");
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'THIRD', 'SQL Server'");
        const string Flags = "concat(name, '|', location, '|', provider_string, '|', catalog, '|', data_source, '|', is_remote_login_enabled, is_rpc_out_enabled, is_data_access_enabled, uses_remote_collation, is_remote_proc_transaction_promotion_enabled)";
        AreEqual("OTHER|loc|ps=1|cat|h|00111", local.ExecuteScalar($"select {Flags} from sys.servers where name = 'OTHER'"));
        AreEqual("THIRD||||THIRD|11111", local.ExecuteScalar($"select {Flags} from sys.servers where name = 'THIRD'"));
        AreEqual("SIMULATED||||SIMULATED|11011", local.ExecuteScalar($"select {Flags} from sys.servers where server_id = 0"));
        using var reader = local.ExecuteReader("select * from sys.servers");
        AreEqual(26, reader.FieldCount);
    }

    /// <summary>
    /// sp_addlinkedserver's product and provider rules, its transaction and
    /// duplicate refusals, and where it reports each (probed 2026-10-05 against
    /// SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_addlinkedserver 'X', 'SQL Server', @datasrc = 'h'", 15426, 24, "sp_addlinkedserver")]
    [DataRow("exec sp_addlinkedserver 'X', ''", 15427, 31, "sp_addlinkedserver")]
    [DataRow("exec sp_addlinkedserver 'X', 'SQL Server', 'MSOLEDBSQL'", 15428, 41, "sp_addlinkedserver")]
    [DataRow("exec sp_addlinkedserver 'X', null, 'MSOLEDBSQL'", 15429, 46, "sp_addlinkedserver")]
    [DataRow("exec sp_addlinkedserver 'X', '', 'MSDASQL'", 7222, 60, "sys.sp_MSaddserver_internal")]
    [DataRow("exec sp_addlinkedserver 'X', '', 'MSOLEDBSQL', @linkedstyle = 0", 15663, 60, "sys.sp_MSaddserver_internal")]
    [DataRow("begin tran; exec sp_addlinkedserver 'X'", 15002, 54, "sp_addlinkedserver")]
    [DataRow("exec sp_addlinkedserver ''", 15004, 17, "sys.sp_validname")]
    [DataRow("exec sp_addlinkedserver 'X', '', 'MSOLEDBSQL', @linkedstyle = 'x'", 8114, 0, "sp_addlinkedserver")]
    public void SpAddLinkedServer_RealRefusals(string sql, int number, int line, string procedure)
    {
        var local = new Simulation();
        local.AddRemoteSimulation("X", new Simulation());
        var error = local.AssertSqlError(sql, number);
        AreEqual(line, error.LineNumber);
        AreEqual(procedure, error.Procedure);
    }

    /// <summary>
    /// A product spelled 'sql server' is SQL Server's, SQLOLEDB is recorded as
    /// SQLNCLI, and the nvarchar(4000) properties keep what fits (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SpAddLinkedServer_RecordsAsReal()
    {
        var local = new Simulation();
        local.AddRemoteSimulation("X", new Simulation());
        local.AddRemoteSimulation("Y", new Simulation());
        _ = local.ExecuteNonQuery($"exec sp_addlinkedserver 'X', 'sql server'; exec sp_addlinkedserver 'Y', '', 'SQLOLEDB', '{new string('h', 5000)}'");
        AreEqual("X|SQL Server|SQLNCLI|X", local.ExecuteScalar("select concat(name, '|', product, '|', provider, '|', data_source) from sys.servers where name = 'X'"));
        AreEqual("SQLNCLI|4000", local.ExecuteScalar("select concat(provider, '|', len(data_source)) from sys.servers where name = 'Y'"));
    }

    /// <summary>
    /// Every sp_serveroption setting reaches sys.servers; an on/off option takes
    /// only true / on / false / off, system only the first two, a timeout a
    /// whole number and collation name a collation (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void SpServerOption_EveryOption()
    {
        var local = new Simulation();
        local.AddRemoteSimulation("X", new Simulation());
        _ = local.ExecuteNonQuery("""
            exec sp_addlinkedserver 'X', '', 'MSOLEDBSQL';
            exec sp_serveroption 'X', 'collation compatible', 'true';
            exec sp_serveroption 'X', 'lazy schema validation', 'on';
            exec sp_serveroption 'X', 'use remote collation', 'off';
            exec sp_serveroption 'X', 'collation name', 'Latin1_General_BIN';
            exec sp_serveroption 'X', 'rpc', 'true';
            exec sp_serveroption 'X', 'pub', 'true';
            exec sp_serveroption 'X', 'sub', 'true';
            exec sp_serveroption 'X', 'dist', 'true';
            exec sp_serveroption 'X', 'system', 'true';
            exec sp_serveroption 'X', 'connect timeout', '10';
            exec sp_serveroption 'X', 'query timeout', 30;
            """);
        AreEqual("1|1|0|Latin1_General_BIN|1|1|1|1|1|10|30", local.ExecuteScalar("""
            select concat_ws('|', cast(is_collation_compatible as int), cast(lazy_schema_validation as int), cast(uses_remote_collation as int), collation_name,
                cast(is_remote_login_enabled as int), cast(is_publisher as int), cast(is_subscriber as int), cast(is_distributor as int), cast(is_system as int), connect_timeout, query_timeout)
            from sys.servers where name = 'X'
            """));
        foreach (var refused in (string[])["'system', 'off'", "'connect timeout', 'true'", "'query timeout', '-1'", "'collation name', 'bogus'", "'rpc', '1'"])
            _ = local.AssertSqlError($"exec sp_serveroption 'X', {refused}", 15600);
    }

    /// <summary>
    /// A linked server maps every login to itself until sp_addlinkedsrvlogin
    /// replaces the mapping; sys.linked_logins and sp_helplinkedsrvlogin list
    /// them, sp_dropserver keeps a server mapping one without 'droplogins'
    /// (Msg 15190), and a server mapping none refuses access (Msg 7416) —
    /// probed 2026-10-05 against SQL Server 2025.
    /// </summary>
    [TestMethod]
    public void LinkedLogins()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table t (id int)");
        var local = new Simulation();
        local.AddRemoteSimulation("X", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'X', '', 'MSOLEDBSQL'");
        const string Logins = "select string_agg(concat(local_principal_id, '|', cast(uses_self_credential as int), '|', remote_name), ';') from sys.linked_logins";
        AreEqual("0|1|", local.ExecuteScalar(Logins));
        _ = local.ExecuteNonQuery("exec sp_addlinkedsrvlogin 'X', 'false', null, 'r', 'p'");
        AreEqual("0|0|r", local.ExecuteScalar(Logins));
        AreEqual("X||0|r", local.ExecuteScalar("""
            create table #h (s sysname, l sysname null, m smallint, r sysname null);
            insert #h exec sp_helplinkedsrvlogin 'X';
            select concat(s, '|', l, '|', m, '|', r) from #h
            """));
        AreEqual(15600, local.AssertSqlError("exec sp_addlinkedsrvlogin 'X', 'maybe'", 15600).Number);
        AreEqual(80, local.AssertSqlError("exec sp_addlinkedsrvlogin 'X', 'false', 'nosuch'", 15007).LineNumber);
        AreEqual(56, local.AssertSqlError("exec sp_dropserver 'X'", 15190).LineNumber);
        _ = local.ExecuteNonQuery("exec sp_droplinkedsrvlogin 'X', null");
        _ = local.AssertSqlError("select * from X.simulated.dbo.t", 7416);
        _ = local.ExecuteNonQuery("exec sp_dropserver 'X'");
        AreEqual(0, local.ExecuteScalar("select count(*) from sys.servers where name = 'X'"));
        AreEqual(0, local.ExecuteScalar("select count(*) from sys.remote_logins"));
    }

    /// <summary>
    /// A synonym over a four-part name reads the linked server's table, and a
    /// four-part name called as a function is Msg 4122 (probed 2026-10-05).
    /// </summary>
    [TestMethod]
    public void SynonymOverFourPartName_AndRemoteFunctionCall()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table t (id int); insert t values (1), (2)");
        var local = new Simulation();
        local.AddRemoteSimulation("X", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'X'; create synonym s for X.simulated.dbo.t");
        AreEqual(3, local.ExecuteScalar("select sum(s.id) from s"));
        _ = local.AssertSqlError("select * from X.simulated.dbo.f()", 4122);
    }

    /// <summary>
    /// The provider lists a decimal as numeric, a smallmoney as money and a
    /// sysname as nvarchar (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void FourPartRead_ProviderTypes()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table t (d decimal(5, 2), m smallmoney, n sysname); insert t values (1.25, 1.5, 'x')");
        var local = new Simulation();
        local.AddRemoteSimulation("X", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'X'");
        AreEqual("numeric;money;nvarchar|1.50", local.ExecuteScalar("""
            select * into #x from X.simulated.dbo.t;
            select concat((select string_agg(type_name(user_type_id), ';') within group (order by column_id) from tempdb.sys.columns where object_id = object_id('tempdb..#x')), '|', (select m from #x))
            """));
    }
}
