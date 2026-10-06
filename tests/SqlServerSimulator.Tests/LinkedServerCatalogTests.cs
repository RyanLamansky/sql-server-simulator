using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// The linked-server catalog — <c>sysservers</c>, <c>sp_linkedservers</c>,
/// <c>sp_testlinkedserver</c>, <c>sp_catalogs</c> — the names real reads past
/// the four-part grammar, and what the provider does with a few column kinds
/// (probed 2026-10-06 against SQL Server 2025 through an MSOLEDBSQL loopback).
/// </summary>
[TestClass]
public sealed class LinkedServerCatalogTests
{
    // A Simulation linking itself as SELF through the MSOLEDBSQL provider.
    private static Simulation Loopback(string setup = "")
    {
        var sim = new Simulation();
        if (setup.Length > 0)
            _ = sim.ExecuteNonQuery(setup);
        sim.AddRemoteSimulation("SELF", sim);
        _ = sim.ExecuteNonQuery("exec sp_addlinkedserver @server = 'SELF', @srvproduct = '', @provider = 'MSOLEDBSQL', @datasrc = 'localhost'; exec sp_serveroption 'SELF', 'rpc out', 'true'");
        return sim;
    }

    private static List<int> Messages(SimulatedDbConnection connection)
    {
        var numbers = new List<int>();
        connection.InfoMessage += (_, e) => numbers.AddRange(e.Errors.Cast<SimulatedError>().Select(error => error.Number));
        return numbers;
    }

    [TestMethod]
    public void Sysservers_PacksTheOptionsIntoSrvstatus()
    {
        var sim = Loopback();
        AreEqual("SIMULATED:1089:1:SIMULATED                     |SELF:1248:0:", sim.ExecuteScalar("""
            select string_agg(concat(srvname, ':', srvstatus, ':', cast(isremote as int), ':', srvnetname), '|') within group (order by srvid)
            from sys.sysservers
            """));
        AreEqual("MSOLEDBSQL:0:1:1:1", sim.ExecuteScalar("select concat(providername, ':', cast(rpc as int), ':', cast(rpcout as int), ':', cast(dataaccess as int), ':', cast(useremotecollation as int)) from master.dbo.sysservers where srvname = 'SELF'"));
        AreEqual(-212, sim.ExecuteScalar("select object_id('sysservers')"));
        _ = sim.ExecuteNonQuery("exec sp_serveroption 'SELF', 'lazy schema validation', 'true'; exec sp_serveroption 'SELF', 'collation compatible', 'true'");
        AreEqual((short)(1248 | 2048 | 256), sim.ExecuteScalar("select srvstatus from sysservers where srvname = 'SELF'"));
    }

    [TestMethod]
    public void SpLinkedServers_ListsEveryServer()
    {
        using var reader = Loopback().ExecuteReader("exec sp_linkedservers");
        AreEqual("SRV_NAME|SRV_PROVIDERNAME|SRV_PRODUCT|SRV_DATASOURCE|SRV_PROVIDERSTRING|SRV_LOCATION|SRV_CAT", string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        var rows = new List<string>();
        while (reader.Read())
            rows.Add($"{reader[0]}:{reader[1]}:{reader[2]}:{reader[3]}");
        AreEqual("SIMULATED:SQLNCLI:SQL Server:SIMULATED|SELF:MSOLEDBSQL::localhost", string.Join("|", rows));
    }

    [TestMethod]
    public void SpTestLinkedServer_TakesOnlyAUnicodeName()
    {
        var sim = Loopback();
        AreEqual(0, sim.ExecuteScalar("declare @r int; exec @r = sp_testlinkedserver N'SELF'; select @r"));
        _ = sim.ExecuteNonQuery("exec sp_testlinkedserver SELF; declare @s sysname = 'SELF'; exec sp_testlinkedserver @s");
        var error = sim.AssertSqlError("exec sp_testlinkedserver 'SELF'", 214);
        AreEqual("Procedure expects parameter '@servername' of type 'sysname'.", error.Errors[0].Message);
        AreEqual(1, error.LineNumber);
        _ = sim.AssertSqlError("exec sp_testlinkedserver null", 214);
        AreEqual(1, sim.AssertSqlError("exec sp_testlinkedserver", 201).LineNumber);
        _ = sim.AssertSqlError("exec sp_testlinkedserver N'SELF', 1", 8144);
        AreEqual(1, sim.ExecuteScalar("declare @r int; begin try exec @r = sp_testlinkedserver N'nosuch' end try begin catch end catch; select @r"));
        _ = sim.AssertSqlError("exec sp_testlinkedserver N'nosuch'", 7202);
    }

    [TestMethod]
    public void SpCatalogs_ListsTheServersDatabases()
    {
        var sim = Loopback("create database zeta; create database Alpha");
        using var reader = sim.ExecuteReader("exec sp_catalogs 'SELF'");
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add((string)reader[0]);
            IsTrue(reader.IsDBNull(1));
        }
        AreEqual("Alpha,master,model,msdb,simulated,tempdb,zeta", string.Join(",", names));
        AreEqual(7, sim.AssertSqlError("exec sp_catalogs 'nosuch'", 7202).LineNumber);
    }

    [TestMethod]
    public void NamesPastTheFourPartGrammar()
    {
        var sim = Loopback("create table t (a int); create table master.dbo.mt (b int); insert master.dbo.mt values (5); create synonym sy for t; exec ('create function f (@a int) returns int as begin return @a end')");
        sim.AssertSqlError("select * from SELF...t", 7314, "The OLE DB provider \"MSOLEDBSQL19\" for linked server \"SELF\" does not contain the table \"t\". The table either does not exist or the current user does not have permissions on that table.");
        AreEqual(5, sim.ExecuteScalar("select b from SELF...mt"));
        _ = sim.AssertSqlError("exec SELF...p 1", 2812);
        sim.AssertSqlError("select * from SELF.simulated.dbo.t.a", 117, "The object name 'SELF.simulated.dbo.t.a' contains more than the maximum number of prefixes. The maximum is 3.");
        sim.AssertSqlError("select SELF.simulated.dbo.f(1)", 344, "Remote function reference 'SELF.simulated.dbo.f' is not allowed, and the column name 'SELF' could not be found or is ambiguous.");
        _ = sim.AssertSqlError("select * from SELF.simulated.dbo.sy", 7357);
    }

    [TestMethod]
    public void TempTableNamedThroughAServer_ReadsTheSessionsOwn()
    {
        var sim = Loopback();
        using var connection = (SimulatedDbConnection)sim.CreateOpenConnection();
        _ = connection.CreateCommand("create table #tt (a int); insert #tt values (4)").ExecuteNonQuery();
        var messages = Messages(connection);
        AreEqual(4, connection.CreateCommand("select a from nosrv.simulated.dbo.#tt").ExecuteScalar());
        AreEqual("2701", string.Join(",", messages));
        messages.Clear();
        AreEqual(4, connection.CreateCommand("select a from tempdb..#tt").ExecuteScalar());
        AreEqual("2701", string.Join(",", messages));
    }

    [TestMethod]
    public void InsertListingAComputedOrRowversionColumn_RefusedByTheProvider()
    {
        var sim = Loopback("create table u (a int primary key, b int, c as a + 1, r rowversion)");
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("select 1; insert SELF.simulated.dbo.u (a, b, c) values (2, 1, 3)").ExecuteNonQuery());
        AreEqual("7344,7412", string.Join(",", error.Errors.Cast<SimulatedError>().Select(entry => entry.Number).Order()));
        StartsWith("The OLE DB provider \"MSOLEDBSQL19\" for linked server \"SELF\" could not INSERT INTO table \"[SELF].[simulated].[dbo].[u]\" because of column \"c\".", error.Errors[0].Message);
        _ = sim.AssertSqlError("insert SELF.simulated.dbo.u (a, r) values (2, 0x01)", 7344);
    }

    [TestMethod]
    public void ExecAt_ADecimalVariableArrivesAsNumeric()
    {
        var sim = Loopback();
        AreEqual("numeric", sim.ExecuteScalar("declare @d decimal(10, 2) = 2.5; exec ('select sql_variant_property(?, ''BaseType'')', @d) at SELF"));
        AreEqual("decimal", sim.ExecuteScalar("exec ('select sql_variant_property(?, ''BaseType'')', 1.5) at SELF"));
    }

    [TestMethod]
    public void SupplementaryCharacterCollation_ListedWithoutSc()
    {
        var sim = Loopback("create table sc (a nvarchar(10) collate Latin1_General_100_CI_AS_SC); insert sc values (N'x' + nchar(0xD83D) + nchar(0xDE00))");
        AreEqual("3:Latin1_General_100_CI_AS", sim.ExecuteScalar("select concat(len(a), ':', cast(sql_variant_property(a, 'Collation') as sysname)) from SELF.simulated.dbo.sc"));
        AreEqual(2, sim.ExecuteScalar("select len(a) from sc"));
    }

    [TestMethod]
    public void RemoteUpdate_SetListErrorRelayedFromTheServer()
    {
        var sim = Loopback("create table u (a int primary key, b int, s varchar(10)); insert u values (1, 1, 'x')");
        using var connection = sim.CreateOpenConnection();
        var error = Throws<SimulatedSqlException>(() => connection.CreateCommand("print 'a'\nupdate SELF.simulated.dbo.u set b = 1 / 0 where a = 1\nprint 'b'").ExecuteNonQuery());
        AreEqual("3621:1,8134:1", string.Join(",", error.Errors.Cast<SimulatedError>().Where(entry => entry.Number != 0).Select(entry => $"{entry.Number}:{entry.LineNumber}").Order()));
        // Nothing runs ahead of the rows the server reads.
        AreEqual(0, connection.CreateCommand("update SELF.simulated.dbo.u set b = 1 / 0 where a = 99").ExecuteNonQuery());
        AreEqual(1, sim.ExecuteScalar("select b from u"));
    }

    [TestMethod]
    public void BareWordArgument_IsUnicode()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create proc pv @p sql_variant as select concat(cast(sql_variant_property(@p, 'BaseType') as sysname), ':', cast(sql_variant_property(@p, 'MaxLength') as int))");
        AreEqual("nvarchar:6", sim.ExecuteScalar("exec pv abc"));
    }
}
