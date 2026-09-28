using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Writes, remote calls and transactions through a linked server, as SQL Server
/// 2025 behaves through a loopback MSOLEDBSQL linked server and against a second
/// instance (probed 2026-09-28).
/// </summary>
[TestClass]
public class LinkedServerWriteTests
{
    // A local Simulation linking a second one as OTHER, set up by remoteSetup.
    private static (Simulation Local, Simulation Remote) Linked(string remoteSetup, string product = "SQL Server")
    {
        var remote = new Simulation();
        if (remoteSetup.Length > 0)
            _ = remote.ExecuteNonQuery(remoteSetup);
        var local = new Simulation();
        local.AddRemoteSimulation("OTHER", remote);
        _ = local.ExecuteNonQuery($"exec sp_addlinkedserver 'OTHER', '{product}'");
        return (local, remote);
    }

    private static int[] Numbers(SimulatedSqlException error) => [.. error.Errors.Cast<SimulatedError>().Select(entry => entry.Number)];

    [TestMethod]
    public void Insert_Values_LeavesIdentityScalarsNull()
    {
        var (local, remote) = Linked("create table t (id int identity primary key, v int)");
        _ = local.ExecuteNonQuery("create table l (id int identity(100, 1), v int)");
        AreEqual("2||", local.ExecuteScalar("""
            insert l (v) values (1);
            insert OTHER.simulated.dbo.t (v) values (1), (2);
            select concat(@@rowcount, '|', scope_identity(), '|', @@identity)
            """));
        AreEqual(3, remote.ExecuteScalar("select sum(id) from t"));
    }

    [TestMethod]
    public void Insert_SelectAndDefaultValues()
    {
        var (local, remote) = Linked("create table t (id int identity primary key, v int default 7, s varchar(5))");
        _ = local.ExecuteNonQuery("create table l (v int); insert l values (1), (2)");
        AreEqual(2, local.ExecuteNonQuery("insert OTHER.simulated.dbo.t (v) select v from l"));
        AreEqual(1, local.ExecuteNonQuery("insert OTHER.simulated.dbo.t default values"));
        AreEqual(1, local.ExecuteNonQuery("insert OTHER.simulated.dbo.t (s) values ('x')"));
        AreEqual("1,2,7,7", remote.ExecuteScalar("select string_agg(v, ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void Update_JoinedToLocalTable()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int); insert t values (1, 10), (2, 20), (3, 30)");
        _ = local.ExecuteNonQuery("create table l (id int, v int); insert l values (1, 100), (3, 300)");
        AreEqual(2, local.ExecuteNonQuery("update r set v = l.v from OTHER.simulated.dbo.t r join l on l.id = r.id"));
        AreEqual("100,20,300", remote.ExecuteScalar("select string_agg(v, ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void Delete_JoinedAndTop()
    {
        var (local, remote) = Linked("create table t (id int primary key); insert t values (1), (2), (3), (4)");
        _ = local.ExecuteNonQuery("create table l (id int); insert l values (1), (3)");
        AreEqual(2, local.ExecuteNonQuery("delete r from OTHER.simulated.dbo.t r join l on l.id = r.id"));
        AreEqual(1, local.ExecuteNonQuery("delete top (1) OTHER.simulated.dbo.t"));
        AreEqual(1, remote.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>A keyless table's rows are found by their values, one at a time.</summary>
    [TestMethod]
    public void Update_KeylessDuplicates_TouchesOne()
    {
        var (local, remote) = Linked("create table t (v int); insert t values (1), (1), (2)");
        AreEqual(1, local.ExecuteNonQuery("update top (1) OTHER.simulated.dbo.t set v = 9 where v = 1"));
        AreEqual("1,2,9", remote.ExecuteScalar("select string_agg(v, ',') within group (order by v) from t"));
    }

    /// <summary>
    /// An INSERT arrives row by row, and an UPDATE or DELETE with no FROM clause
    /// as one statement, while a joined one goes row by row: the remote
    /// trigger's firings show which.
    /// </summary>
    [TestMethod]
    public void RemoteTrigger_FiresPerRowOrPerStatementAsRealDoes()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int); create table log1 (n int)");
        _ = remote.ExecuteNonQuery("create trigger tr on t after insert, update, delete as insert log1 select (select count(*) from inserted) + (select count(*) from deleted)");
        _ = local.ExecuteNonQuery("create table l (id int); insert l values (1), (2)");
        _ = local.ExecuteNonQuery("""
            insert OTHER.simulated.dbo.t values (1, 1), (2, 2);
            update OTHER.simulated.dbo.t set v = 0;
            update r set v = 5 from OTHER.simulated.dbo.t r join l on l.id = r.id;
            update OTHER.simulated.dbo.t set v = 1 where 1 = 0;
            delete OTHER.simulated.dbo.t
            """);
        AreEqual("1,1,4,2,2,0,2", remote.ExecuteScalar("select string_agg(n, ',') from log1"));
    }

    /// <summary>
    /// The server's constraint error arrives with its Msg 3621 ahead of it at
    /// state 1, at the remote statement's line 1, and ends the batch.
    /// </summary>
    [TestMethod]
    public void RemoteConstraintError_RelayedAndEndsBatch()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int check (v < 100)); insert t values (1, 1)");
        var error = local.AssertSqlError("""
            select 1;
            insert OTHER.simulated.dbo.t values (2, 2), (3, 500);
            select 2
            """, 547);
        AreEqual(1, error.State);
        AreEqual(1, error.LineNumber);
        CollectionAssert.AreEqual(new[] { 547, 3621 }, Numbers(error));
        AreEqual(1, remote.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void RemoteKeyError_CaughtByTry()
        => AreEqual("2627|1", Linked("create table t (id int primary key); insert t values (1)").Local.ExecuteScalar("""
            begin try insert OTHER.simulated.dbo.t values (1) end try
            begin catch select concat(error_number(), '|', error_line()) end catch
            """));

    [TestMethod]
    public void RemoteTriggerRollback_Relays3609First()
    {
        var (local, remote) = Linked("create table t (id int)");
        _ = remote.ExecuteNonQuery("create trigger tr on t after insert as begin raiserror('nope', 16, 1); rollback; end");
        var error = local.AssertSqlError("insert OTHER.simulated.dbo.t values (1)", 3609);
        CollectionAssert.AreEqual(new[] { 3609, 50000 }, Numbers(error));
        AreEqual(0, remote.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void Truncation_IsLegacyMsg8152State14()
    {
        var error = Linked("create table t (s varchar(5))").Local.AssertSqlError("insert OTHER.simulated.dbo.t values ('toolongvalue')", 8152);
        AreEqual(14, error.State);
    }

    [TestMethod]
    public void IdentityColumnInInsertList_Msg7344()
        => Linked("create table t (id int identity, v int)").Local.AssertSqlError("insert OTHER.simulated.dbo.t (id, v) values (1, 5)", 7344,
            "The OLE DB provider \"MSOLEDBSQL19\" for linked server \"OTHER\" could not INSERT INTO table \"[OTHER].[simulated].[dbo].[t]\" because of column \"id\". The user did not have permission to write to the column.");

    [TestMethod]
    [DataRow("update OTHER.simulated.dbo.t set id = 3", 8102)]
    [DataRow("update OTHER.simulated.dbo.t set c = 3", 271)]
    [DataRow("update OTHER.simulated.dbo.t set rv = 0x01", 272)]
    public void UnwritableSetColumn_PrefixedByMsg8180(string sql, int cause)
    {
        var error = Linked("create table t (id int identity, v int, c as v * 2, rv rowversion)").Local.AssertSqlError(sql, 8180);
        CollectionAssert.AreEqual(new[] { 8180, cause }, Numbers(error));
    }

    [TestMethod]
    [DataRow("insert OTHER.simulated.dbo.t output inserted.id values (1)", 405)]
    [DataRow("update OTHER.simulated.dbo.t set id = 2 output deleted.id", 405)]
    [DataRow("delete OTHER.simulated.dbo.t output deleted.id", 405)]
    [DataRow("insert OTHER.simulated.dbo.t select id from (delete l output deleted.id) d", 405)]
    [DataRow("merge OTHER.simulated.dbo.t as t using l on l.id = t.id when matched then delete;", 5315)]
    [DataRow("insert OTHER.simulated..t values (1)", 7313)]
    [DataRow("insert OTHER.simulated.dbo.nope values (1)", 7314)]
    [DataRow("insert NOWHERE.simulated.dbo.t values (1)", 7202)]
    public void RefusedWrite(string sql, int number)
    {
        var (local, _) = Linked("create table t (id int)");
        _ = local.ExecuteNonQuery("create table l (id int)");
        _ = local.AssertSqlError(sql, number);
    }

    [TestMethod]
    public void RemoteView_TakesWrites()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int)");
        _ = remote.ExecuteNonQuery("create view vt as select id, v from t");
        _ = local.ExecuteNonQuery("insert OTHER.simulated.dbo.vt values (1, 1)");
        _ = local.ExecuteNonQuery("update OTHER.simulated.dbo.vt set v = 2");
        AreEqual(2, remote.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Synonym_ForRemoteTableAndProcedure()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int)");
        _ = remote.ExecuteNonQuery("create procedure p as select count(*) from t");
        _ = local.ExecuteNonQuery("create synonym st for OTHER.simulated.dbo.t; create synonym sp for OTHER.simulated.dbo.p");
        _ = local.ExecuteNonQuery("insert st values (1, 1); update st set v = 5");
        AreEqual(5, remote.ExecuteScalar("select v from t"));
        AreEqual(1, local.ExecuteScalar("exec sp"));
    }

    [TestMethod]
    public void Merge_UsingRemoteSource()
    {
        var (local, _) = Linked("create table t (id int primary key, v int); insert t values (1, 5), (2, 6)");
        _ = local.ExecuteNonQuery("create table l (id int, v int); insert l values (1, 0)");
        AreEqual(2, local.ExecuteNonQuery("merge l using OTHER.simulated.dbo.t s on l.id = s.id when matched then update set v = s.v when not matched then insert values (s.id, s.v);"));
        AreEqual(11, local.ExecuteScalar("select sum(v) from l"));
    }

    [TestMethod]
    public void OpenQuery_AsWriteTarget()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int); insert t values (1, 1), (2, 2)");
        _ = local.ExecuteNonQuery("insert openquery(OTHER, 'select id, v from simulated.dbo.t') values (3, 3)");
        _ = local.ExecuteNonQuery("update openquery(OTHER, 'select id, v from simulated.dbo.t') set v = 9 where id = 2");
        _ = local.ExecuteNonQuery("delete openquery(OTHER, 'select id, v from simulated.dbo.t where id = 1')");
        AreEqual("2:9,3:3", remote.ExecuteScalar("select string_agg(concat(id, ':', v), ',') within group (order by id) from t"));
    }

    [TestMethod]
    [DataRow("update openquery(OTHER, 'select count(*) c from simulated.dbo.t') set c = 1")]
    [DataRow("delete openquery(OTHER, 'select 1 x')")]
    public void OpenQuery_NoUpdatableCursor_Msg16955(string sql)
    {
        var error = Linked("create table t (id int)").Local.AssertSqlError(sql, 16955);
        CollectionAssert.AreEqual(new[] { 16955, 7412 }, Numbers(error));
    }

    [TestMethod]
    public void ExecAt_BindsPlaceholdersAndOutput()
    {
        var (local, _) = Linked("");
        // An INSERT … EXEC's own transaction would enlist the server.
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'remote proc transaction promotion', 'false'");
        AreEqual("5|x|42", local.ExecuteScalar("""
            declare @o int;
            exec ('select ? = 42', @o output) at OTHER;
            create table #r (a int, b nvarchar(5));
            insert #r exec ('select ?, ?', 5, 'x') at OTHER;
            select concat(a, '|', b, '|', @o) from #r
            """));
    }

    [TestMethod]
    public void ExecAt_RemoteDml_SetsRowCount()
    {
        var (local, remote) = Linked("create table t (id int)");
        AreEqual(2, local.ExecuteScalar("exec ('insert simulated.dbo.t values (1), (2)') at OTHER; select @@rowcount"));
        AreEqual(2, remote.ExecuteScalar("select count(*) from t"));
    }

    /// <summary>
    /// The server's batch runs on past its error, which reaches the client
    /// among the results and outside the caller's control flow: no CATCH of the
    /// caller's runs, and the caller's batch carries on.
    /// </summary>
    [TestMethod]
    public void ExecAt_RemoteErrorPassesThroughToTheClient()
    {
        var (local, remote) = Linked("create table t (id int)");
        _ = local.ExecuteNonQuery("create table l (id int)");
        _ = local.AssertSqlError("""
            begin try exec ('select 1/0; insert simulated.dbo.t values (1)') at OTHER end try
            begin catch insert l values (1) end catch;
            insert l values (2)
            """, 8134);
        AreEqual(1, remote.ExecuteScalar("select count(*) from t"));
        AreEqual(2, local.ExecuteScalar("select sum(id) from l"));
    }

    [TestMethod]
    public void RemoteProcedure_WriteErrorRelaysNoticeFirst()
    {
        var (local, remote) = Linked("create table t (id int primary key); insert t values (1)");
        _ = remote.ExecuteNonQuery("create procedure p as begin insert t values (1); select 3; end");
        var error = local.AssertSqlError("exec OTHER.simulated.dbo.p", 2627);
        AreEqual("simulated.dbo.p", error.Procedure);
        CollectionAssert.AreEqual(new[] { 2627, 3621 }, Numbers(error));
    }

    [TestMethod]
    public void OpenQuery_JoinedUpdateTarget()
    {
        var (local, remote) = Linked("create table t (id int primary key, v int); insert t values (1, 1), (2, 2)");
        _ = local.ExecuteNonQuery("create table l (id int); insert l values (2)");
        AreEqual(1, local.ExecuteNonQuery("update q set v = 9 from openquery(OTHER, 'select id, v from simulated.dbo.t') q join l on l.id = q.id"));
        AreEqual(10, remote.ExecuteScalar("select sum(v) from t"));
    }

    [TestMethod]
    [DataRow("exec ('select 1') at OTHER", 7411, "Server 'OTHER' is not configured for RPC.")]
    [DataRow("exec ('select 1') at NOWHERE", 7202, "Could not find server 'NOWHERE' in sys.servers. Verify that the correct server name was specified. If necessary, execute the stored procedure sp_addlinkedserver to add the server to sys.servers.")]
    [DataRow("exec ('select ?', 5)", 102, "Incorrect syntax near ')'.")]
    [DataRow("exec ('select ?', 5 output) at OTHER", 179, "Cannot use the OUTPUT option when passing a constant to a stored procedure.")]
    public void ExecAt_Refused(string sql, int number, string message)
        => Linked("", product: "").Local.AssertSqlError(sql, number, message);

    [TestMethod]
    public void RemoteProcedure_OutputAndReturnCode()
    {
        var (local, remote) = Linked("create table t (id int)");
        _ = remote.ExecuteNonQuery("create procedure p @x int, @y int output as begin insert t values (@x); set @y = @x * 2; return 7; end");
        AreEqual("10|7", local.ExecuteScalar("declare @y int, @r int; exec @r = OTHER.simulated.dbo.p 5, @y output; select concat(@y, '|', @r)"));
        AreEqual(5, remote.ExecuteScalar("select id from t"));
    }

    [TestMethod]
    public void RemoteProcedure_ErrorsNameTheCall()
    {
        var (local, remote) = Linked("");
        _ = remote.ExecuteNonQuery("create procedure p as select 1/0");
        AreEqual("simulated.dbo.p", local.AssertSqlError("exec OTHER.simulated.dbo.p", 8134).Procedure);
        local.AssertSqlError("exec OTHER.simulated.dbo.nope", 2812, "Could not find stored procedure 'simulated.dbo.nope'.");
    }

    [TestMethod]
    public void RpcOutOff_Msg7411()
    {
        var (local, remote) = Linked("");
        _ = remote.ExecuteNonQuery("create procedure p as select 1");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'rpc out', 'false'");
        local.AssertSqlError("exec OTHER.simulated.dbo.p", 7411, "Server 'OTHER' is not configured for RPC.");
    }

    [TestMethod]
    public void DataAccessOff_Msg7411ForReadsAndWrites()
    {
        var (local, _) = Linked("create table t (id int)");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'data access', 'off'");
        local.AssertSqlError("select * from OTHER.simulated.dbo.t", 7411, "Server 'OTHER' is not configured for DATA ACCESS.");
        _ = local.AssertSqlError("insert OTHER.simulated.dbo.t values (1)", 7411);
        _ = local.AssertSqlError("select * from openquery(OTHER, 'select 1 a')", 7411);
        AreEqual(1, local.ExecuteScalar("exec ('select 1') at OTHER"));
    }

    [TestMethod]
    public void SpServerOption_SetsSysServersFlags()
    {
        var (local, _) = Linked("", product: "");
        _ = local.ExecuteNonQuery("""
            exec sp_serveroption 'OTHER', 'RPC OUT', 'on';
            exec sp_serveroption @server = 'OTHER', @optname = 'data access', @optvalue = 'false';
            exec sp_serveroption 'OTHER', 'remote proc transaction promotion', 'FALSE';
            exec sp_serveroption 'OTHER', 'lazy schema validation', 'true'
            """);
        AreEqual("100", local.ExecuteScalar("select concat(is_rpc_out_enabled, is_data_access_enabled, is_remote_proc_transaction_promotion_enabled) from sys.servers where name = 'OTHER'"));
    }

    [TestMethod]
    [DataRow("exec sp_serveroption 'NOWHERE', 'rpc out', 'true'", 15015)]
    [DataRow("exec sp_serveroption 'OTHER', 'bogus', 'true'", 15600)]
    [DataRow("exec sp_serveroption 'OTHER', 'rpc out', 'maybe'", 15600)]
    [DataRow("exec sp_serveroption 'OTHER', 'rpc out', 1", 15600)]
    [DataRow("exec sp_serveroption 'OTHER', 'rpc out'", 201)]
    public void SpServerOption_Refused(string sql, int number)
        => _ = Linked("").Local.AssertSqlError(sql, number);

    /// <summary>
    /// A write inside a local transaction needs a distributed transaction,
    /// which a remote server's coordinator refuses out of the box: the
    /// coordinator's message, then Msg 7391, the batch ends and the
    /// transaction rolls back.
    /// </summary>
    [TestMethod]
    public void WriteInTransaction_Msg7391RollsBack()
    {
        var (local, remote) = Linked("create table t (id int)");
        _ = local.ExecuteNonQuery("create table l (id int)");
        using var connection = local.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("""
            begin tran;
            insert l values (1);
            insert OTHER.simulated.dbo.t values (1);
            select 'unreached'
            """).ExecuteNonQuery());
        AreEqual(7391, error.Number);
        AreEqual(3, error.LineNumber);
        CollectionAssert.AreEqual(new[] { 7391, 7412 }, Numbers(error));
        AreEqual(0, connection.CreateCommand("select @@trancount + (select count(*) from l)").ExecuteScalar());
        AreEqual(0, remote.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    public void WriteInTransaction_CaughtDooms()
        => AreEqual("7391|-1|1", Linked("create table t (id int)").Local.ExecuteScalar("""
            begin tran;
            begin try insert OTHER.simulated.dbo.t values (1) end try
            begin catch select concat(error_number(), '|', xact_state(), '|', @@trancount) end catch;
            rollback
            """));

    [TestMethod]
    public void Loopback_WriteInTransaction_Msg3910()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (id int)");
        sim.AddRemoteSimulation("SELF", sim);
        _ = sim.ExecuteNonQuery("exec sp_addlinkedserver 'SELF'");
        var error = sim.AssertSqlError("\nbegin tran;\ninsert SELF.simulated.dbo.t values (1)", 3910);
        AreEqual(1, error.LineNumber);
        AreEqual(0, sim.ExecuteScalar("select count(*) from t"));
    }

    [TestMethod]
    [DataRow("set implicit_transactions on; insert OTHER.simulated.dbo.t values (1)")]
    [DataRow("begin distributed tran; select * from OTHER.simulated.dbo.t")]
    [DataRow("begin tran; exec ('select 1') at OTHER")]
    [DataRow("create table #x (id int); insert #x exec ('select 1') at OTHER")]
    public void DistributedTransactionNeeded_Msg7391(string sql)
        => _ = Linked("create table t (id int)").Local.AssertSqlError(sql, 7391);

    [TestMethod]
    public void RemoteCallInTransaction_RunsOutsideWithoutPromotion()
    {
        var (local, _) = Linked("create table t (id int)");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'remote proc transaction promotion', 'false'");
        AreEqual(1, local.ExecuteScalar("begin tran; exec ('select 1') at OTHER; rollback"));
    }

    [TestMethod]
    public void Loopback_RemoteCallInTransaction_Runs()
    {
        var sim = new Simulation();
        sim.AddRemoteSimulation("SELF", sim);
        _ = sim.ExecuteNonQuery("exec sp_addlinkedserver 'SELF', 'SQL Server'");
        using var connection = sim.CreateOpenConnection();
        AreEqual(7, connection.CreateCommand("begin tran; exec ('select 7') at SELF").ExecuteScalar());
        AreEqual(1, connection.CreateCommand("select @@trancount").ExecuteScalar());
    }

    [TestMethod]
    public void ReadInLocalTransaction_NeedsNoPromotion()
        => AreEqual(0, Linked("create table t (id int)").Local.ExecuteScalar("begin tran; select count(*) from OTHER.simulated.dbo.t; commit"));

    [TestMethod]
    public void RemoteSession_StartsInMasterOrTheCatalog()
    {
        var (local, _) = Linked("create database cat");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'rpc out', 'true'");
        AreEqual("master", local.ExecuteScalar("exec ('select db_name()') at OTHER"));
        AreEqual("master", local.ExecuteScalar("select d from openquery(OTHER, 'select db_name() d')"));
        _ = local.ExecuteNonQuery("exec sp_dropserver 'OTHER'; exec sp_addlinkedserver 'OTHER', 'SQL Server', @catalog = 'cat'");
        AreEqual("cat", local.ExecuteScalar("select d from openquery(OTHER, 'select db_name() d')"));
    }

    [TestMethod]
    [DataRow("exec OTHER.simulated.dbo.p_ret", 3)]
    [DataRow("exec ('select a from simulated.dbo.t; return') at OTHER", 3)]
    [DataRow("exec ('select a from simulated.dbo.t; declare @x int = 5') at OTHER", 3)]
    [DataRow("exec ('declare @x int') at OTHER", 2)]
    [DataRow("exec ('select a from nowhere') at OTHER", 0)]
    public void RemoteCall_RowCountIsTheLastCountReported(string call, int expected)
    {
        var (local, remote) = Linked("create table t (a int); insert t values (1), (2), (3)");
        _ = remote.ExecuteNonQuery("create procedure p_ret as begin select a from t; return 7; end");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'rpc out', 'true'");
        _ = local.ExecuteNonQuery("create table rc (n int)");
        // The server's error reaches the client after the caller's batch ran on.
        try
        {
            _ = local.ExecuteNonQuery($"select 1 union all select 2; {call}; insert rc select @@rowcount");
        }
        catch (SimulatedSqlException error) when (error.Number == 208)
        {
        }
        AreEqual(expected, local.ExecuteScalar("select n from rc"));
    }

    [TestMethod]
    [DataRow("create table t (x xml, a int)", "select 1; select a from OTHER.simulated.dbo.t", 9514, "Xml data type is not supported in distributed queries. Remote object 'OTHER.simulated.dbo.t' has xml column(s).")]
    [DataRow("create table t (x xml, a int)", "delete OTHER.simulated.dbo.t", 9514, "Xml data type is not supported in distributed queries. Remote object 'OTHER.simulated.dbo.t' has xml column(s).")]
    [DataRow("create table t (g geography, a int)", "select a from OTHER.simulated.dbo.t", 7325, "Objects exposing columns with CLR types are not allowed in distributed queries. Please use a pass-through query to access remote object '\"simulated\".\"dbo\".\"t\"'.")]
    [DataRow("create table t (h hierarchyid)", "update OTHER.simulated.dbo.t set h = null", 7325, "Objects exposing columns with CLR types are not allowed in distributed queries. Please use a pass-through query to access remote object '\"simulated\".\"dbo\".\"t\"'.")]
    [DataRow("create table t (j json)", "select * from OTHER.simulated.dbo.t", 7357, "Cannot process the object \"\"simulated\".\"dbo\".\"t\"\". The OLE DB provider \"MSOLEDBSQL19\" for linked server \"OTHER\" indicates that either the object has no columns or the current user does not have permissions on that object.")]
    [DataRow("", "select * from openquery(OTHER, 'select cast(''<a/>'' as xml) x')", 9514, "Xml data type is not supported in distributed queries. Remote object 'OPENQUERY' has xml column(s).")]
    public void ProviderRefusesTheColumnsItCannotCarry(string remoteSetup, string sql, int number, string message)
        => Linked(remoteSetup).Local.AssertSqlError(sql, number, message);

    /// <summary>
    /// A four-part name's xml column is reported at line 12 wherever the
    /// statement sits, and refuses the whole batch while it compiles.
    /// </summary>
    [TestMethod]
    public void XmlColumn_RefusesTheBatchAtLine12()
    {
        var (local, _) = Linked("create table t (x xml, a int)");
        _ = local.ExecuteNonQuery("create table l (a int)");
        var error = local.AssertSqlError("insert l values (1);\nselect a from OTHER.simulated.dbo.t", 9514);
        AreEqual(12, error.LineNumber);
        AreEqual(0, local.ExecuteScalar("select count(*) from l"));
    }

    [TestMethod]
    public void RemoteCallRowsetWithXml_EndsTheBatch()
    {
        var (local, _) = Linked("");
        _ = local.ExecuteNonQuery("exec sp_serveroption 'OTHER', 'rpc out', 'true'; create table l (a int)");
        local.AssertSqlError("exec ('select cast(''<a/>'' as xml) x') at OTHER; insert l values (1)", 9514, "Xml data type is not supported in distributed queries. Remote object 'IROWSET' has xml column(s).");
        AreEqual(0, local.ExecuteScalar("select count(*) from l"));
    }

    [TestMethod]
    public void JsonColumn_IsNotListed()
    {
        var (local, remote) = Linked("create table t (id int primary key, j json, a int); insert t values (1, '{}', 1)");
        AreEqual("id,a", local.ExecuteScalar("select * into #x from OTHER.simulated.dbo.t; select string_agg(name, ',') within group (order by column_id) from tempdb.sys.columns where object_id = object_id('tempdb..#x')"));
        _ = local.AssertSqlError("select j from OTHER.simulated.dbo.t", 207);
        _ = local.ExecuteNonQuery("update OTHER.simulated.dbo.t set a = 2");
        AreEqual(2, remote.ExecuteScalar("select a from t"));
    }

    [TestMethod]
    public void VectorColumn_ListsAsVarbinaryWhoseValuesCannotBeRead()
    {
        var (local, _) = Linked("create table t (id int primary key, v vector(3)); insert t values (1, null)");
        AreEqual("varbinary 20", local.ExecuteScalar("select * into #x from OTHER.simulated.dbo.t; select concat(type_name(system_type_id), ' ', max_length) from tempdb.sys.columns where object_id = object_id('tempdb..#x') and name = 'v'"));
        _ = Linked("create table t (id int primary key, v vector(3)); insert t values (1, '[1,2,3]')").Local
            .AssertSqlError("select * from OTHER.simulated.dbo.t", 7346);
    }

    /// <summary>
    /// A keyless table whose only columns are text, image or vector has its
    /// rows found again by their bytes, where real positions by bookmark.
    /// </summary>
    [TestMethod]
    public void KeylessLobAndVectorRows_AreFoundAgain()
    {
        var (local, remote) = Linked("create table t (x text, i image); insert t values ('a', 0x01), ('a', 0x01), ('A', 0x01)");
        AreEqual(1, local.ExecuteNonQuery("update top (1) OTHER.simulated.dbo.t set i = 0x02"));
        AreEqual("a:0x02,a:0x01,A:0x01", remote.ExecuteScalar("select string_agg(concat(cast(x as varchar(5)), ':', convert(varchar(10), cast(i as varbinary(5)), 1)), ',') from t"));
        var (vectorLocal, vectorRemote) = Linked("create table t (v vector(2)); insert t values ('[1,2]'), ('[3,4]')");
        AreEqual(2, vectorLocal.ExecuteNonQuery("delete OTHER.simulated.dbo.t"));
        AreEqual(0, vectorRemote.ExecuteScalar("select count(*) from t"));
    }
}
