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

    private const string SchemaRowsetSetup = """
        create database zzscratch;
        """;

    private const string SchemaRowsetObjects = """
        use zzscratch;
        create table dbo.lt(id bigint primary key, d datetime2, n nvarchar(10), v varchar(20), m decimal(10,2), b bit, g uniqueidentifier, x xml, vb varbinary(max), f float, r real, dt datetime, sm smallint, ti tinyint, i int, c char(3), nc nchar(4), mo money, da date, t time(3), dto datetimeoffset, bi binary(5), sd smalldatetime, smo smallmoney, txt text, ntx ntext, img image, sv sql_variant, hid hierarchyid, rv rowversion);
        create type dbo.zzalias from varchar(7) not null;
        """;

    private const string SchemaRowsetAliasedTable = """
        use zzscratch;
        create table dbo.lt2(a int identity(5,2) not null, vm varchar(max), nm nvarchar(max), d1 decimal(38,10), d2 numeric(5,0), dt2 datetime2(3), t7 time(7), t0 time(0), sn sysname, al dbo.zzalias, cc as a + 1, dflt int default 42, vc varchar(8000), nv nvarchar(4000), j json, vec vector(3), dto3 datetimeoffset(3), geo geography, fl float(24), bx binary(1));
        """;

    private static Simulation SchemaRowsetLoopback()
    {
        var sim = Loopback(SchemaRowsetSetup);
        _ = sim.ExecuteNonQuery(SchemaRowsetObjects);
        _ = sim.ExecuteNonQuery(SchemaRowsetAliasedTable);
        _ = sim.ExecuteNonQuery("use zzscratch; exec('create view dbo.lv as select id from dbo.lt'); create synonym dbo.lsyn for dbo.lt");
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
            rows.Add(string.Join('|', values.Select(static v => v is DBNull ? "NULL" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture))));
        }
        return string.Join("~", rows);
    }

    /// <summary>
    /// <c>sp_tables_ex</c> lists a database's tables, views and synonyms by
    /// type, schema and name, the server's own system views among them;
    /// names match as patterns unless <c>@fUsePattern</c> is 0, and
    /// <c>@table_type</c> takes a list (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("exec sp_tables_ex 'SELF', @table_catalog = 'zzscratch', @table_schema = 'dbo'", "zzscratch|dbo|lsyn|SYNONYM|NULL~zzscratch|dbo|lt|TABLE|NULL~zzscratch|dbo|lt2|TABLE|NULL~zzscratch|dbo|lv|VIEW|NULL")]
    [DataRow("exec sp_tables_ex 'SELF', 'lt_', 'dbo', 'zzscratch'", "zzscratch|dbo|lt2|TABLE|NULL")]
    [DataRow("exec sp_tables_ex 'SELF', 'l%', 'dbo', 'zzscratch', @fUsePattern = 0", "")]
    [DataRow("exec sp_tables_ex 'SELF', null, 'dbo', 'zzscratch', 'TABLE,VIEW'", "zzscratch|dbo|lt|TABLE|NULL~zzscratch|dbo|lt2|TABLE|NULL~zzscratch|dbo|lv|VIEW|NULL")]
    [DataRow("exec sp_tables_ex 'SELF', 'tables', 'INFORMATION_SCHEMA', 'zzscratch'", "zzscratch|INFORMATION_SCHEMA|TABLES|SYSTEM VIEW|NULL")]
    [DataRow("exec sp_tables_ex 'SELF', 'lt'", "")]
    [DataRow("exec sp_tables_ex 'SELF', @table_catalog = 'nosuchdb'", "")]
    public void SpTablesEx_ListsTheServersTables(string sql, string expected) =>
        AreEqual(expected, Rows(SchemaRowsetLoopback(), sql));

    /// <summary>
    /// <c>sp_columns_ex</c> describes columns as SQL Server's OLE DB provider
    /// reports them, leaving out the types it has no mapping for (probed
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SpColumnsEx_DescribesColumnsInTheProvidersTerms()
    {
        var sim = SchemaRowsetLoopback();
        AreEqual(string.Join("~",
            "zzscratch|dbo|lt|id|2|bigint|19|21|0|10|0|NULL|NULL|2|NULL|NULL|1|NO|108",
            "zzscratch|dbo|lt|d|-9|datetime2|27|54|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|2|YES|0",
            "zzscratch|dbo|lt|n|-9|nvarchar|10|20|NULL|NULL|1|NULL|NULL|-9|NULL|20|3|YES|39",
            "zzscratch|dbo|lt|v|12|varchar|20|20|NULL|NULL|1|NULL|NULL|12|NULL|20|4|YES|39",
            "zzscratch|dbo|lt|m|2|numeric|10|12|2|10|1|NULL|NULL|2|NULL|NULL|5|YES|108",
            "zzscratch|dbo|lt|b|-7|bit|1|1|0|NULL|1|NULL|NULL|-7|NULL|NULL|6|YES|50",
            "zzscratch|dbo|lt|g|-11|uniqueidentifier|16|36|NULL|NULL|1|NULL|NULL|-11|NULL|NULL|7|YES|36",
            "zzscratch|dbo|lt|x|-9|xml|0|NULL|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|8|YES|39",
            "zzscratch|dbo|lt|vb|-3|varbinary|0|NULL|NULL|NULL|1|NULL|NULL|-3|NULL|0|9|YES|37",
            "zzscratch|dbo|lt|f|6|float|15|8|NULL|10|1|NULL|NULL|6|NULL|NULL|10|YES|109",
            "zzscratch|dbo|lt|r|7|real|7|4|NULL|10|1|NULL|NULL|7|NULL|NULL|11|YES|109",
            "zzscratch|dbo|lt|dt|11|datetime|23|16|3|NULL|1|NULL|NULL|9|3|NULL|12|YES|111",
            "zzscratch|dbo|lt|sm|5|smallint|5|2|0|10|1|NULL|NULL|5|NULL|NULL|13|YES|38",
            "zzscratch|dbo|lt|ti|-6|tinyint|3|1|0|10|1|NULL|NULL|-6|NULL|NULL|14|YES|38",
            "zzscratch|dbo|lt|i|4|int|10|4|0|10|1|NULL|NULL|4|NULL|NULL|15|YES|38",
            "zzscratch|dbo|lt|c|1|char|3|3|NULL|NULL|1|NULL|NULL|1|NULL|3|16|YES|39",
            "zzscratch|dbo|lt|nc|-8|nvarchar|4|8|NULL|NULL|1|NULL|NULL|-8|NULL|8|17|YES|39",
            "zzscratch|dbo|lt|mo|3|money|19|21|4|10|1|NULL|NULL|3|NULL|NULL|18|YES|110",
            "zzscratch|dbo|lt|da|-9|date|10|20|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|19|YES|0",
            "zzscratch|dbo|lt|t|-9|time|12|24|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|20|YES|0",
            "zzscratch|dbo|lt|dto|-9|datetimeoffset|34|68|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|21|YES|0",
            "zzscratch|dbo|lt|bi|-2|binary|5|5|NULL|NULL|1|NULL|NULL|-2|NULL|5|22|YES|37",
            "zzscratch|dbo|lt|sd|11|smalldatetime|16|16|0|NULL|1|NULL|NULL|9|3|NULL|23|YES|111",
            "zzscratch|dbo|lt|smo|3|smallmoney|10|12|4|10|1|NULL|NULL|3|NULL|NULL|24|YES|110",
            "zzscratch|dbo|lt|txt|-1|text|2147483647|2147483647|NULL|NULL|1|NULL|NULL|-1|NULL|2147483647|25|YES|35",
            "zzscratch|dbo|lt|ntx|-10|ntext|1073741823|2147483646|NULL|NULL|1|NULL|NULL|-10|NULL|2147483646|26|YES|35",
            "zzscratch|dbo|lt|img|-4|image|2147483647|2147483647|NULL|NULL|1|NULL|NULL|-4|NULL|2147483647|27|YES|34",
            "zzscratch|dbo|lt|sv|-9|sql_variant|16|NULL|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|28|YES|39",
            "zzscratch|dbo|lt|rv|-2|timestamp|8|8|NULL|NULL|0|NULL|NULL|-2|NULL|8|30|NO|45",
            ""), Rows(sim, "exec sp_columns_ex 'SELF', 'lt', 'dbo', 'zzscratch'") + "~");
        AreEqual(string.Join("~",
            "zzscratch|dbo|lt2|a|4|int|10|4|0|10|0|NULL|NULL|4|NULL|NULL|1|NO|56",
            "zzscratch|dbo|lt2|vm|12|varchar|0|NULL|NULL|NULL|1|NULL|NULL|12|NULL|0|2|YES|39",
            "zzscratch|dbo|lt2|nm|-9|nvarchar|0|NULL|NULL|NULL|1|NULL|NULL|-9|NULL|0|3|YES|39",
            "zzscratch|dbo|lt2|d1|2|numeric|38|40|10|10|1|NULL|NULL|2|NULL|NULL|4|YES|108",
            "zzscratch|dbo|lt2|d2|2|numeric|5|7|0|10|1|NULL|NULL|2|NULL|NULL|5|YES|108",
            "zzscratch|dbo|lt2|dt2|-9|datetime2|23|46|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|6|YES|0",
            "zzscratch|dbo|lt2|t7|-9|time|16|32|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|7|YES|0",
            "zzscratch|dbo|lt2|t0|-9|time|8|16|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|8|YES|0",
            "zzscratch|dbo|lt2|sn|-9|nvarchar|128|256|NULL|NULL|0|NULL|NULL|-9|NULL|256|9|NO|39",
            "zzscratch|dbo|lt2|al|12|varchar|7|7|NULL|NULL|0|NULL|NULL|12|NULL|7|10|NO|39",
            "zzscratch|dbo|lt2|cc|4|int|10|4|0|10|1|NULL|NULL|4|NULL|NULL|11|YES|38",
            "zzscratch|dbo|lt2|dflt|4|int|10|4|0|10|1|NULL|((42))|4|NULL|NULL|12|YES|38",
            "zzscratch|dbo|lt2|vc|12|varchar|8000|8000|NULL|NULL|1|NULL|NULL|12|NULL|8000|13|YES|39",
            "zzscratch|dbo|lt2|nv|-9|nvarchar|4000|8000|NULL|NULL|1|NULL|NULL|-9|NULL|8000|14|YES|39",
            "zzscratch|dbo|lt2|vec|-3|varbinary|20|20|NULL|NULL|1|NULL|NULL|-3|NULL|20|16|YES|37",
            "zzscratch|dbo|lt2|dto3|-9|datetimeoffset|30|60|NULL|NULL|1|NULL|NULL|-9|NULL|NULL|17|YES|0",
            "zzscratch|dbo|lt2|fl|7|real|7|4|NULL|10|1|NULL|NULL|7|NULL|NULL|19|YES|109",
            "zzscratch|dbo|lt2|bx|-2|binary|1|1|NULL|NULL|1|NULL|NULL|-2|NULL|1|20|YES|37",
            ""), Rows(sim, "exec sp_columns_ex 'SELF', 'lt2', 'dbo', 'zzscratch'") + "~");
        AreEqual("zzscratch|dbo|lt|id|2|bigint|19|21|0|10|0|NULL|NULL|2|NULL|NULL|1|NO|108~zzscratch|dbo|lt|i|4|int|10|4|0|10|1|NULL|NULL|4|NULL|NULL|15|YES|38~"
            + "zzscratch|dbo|lt|img|-4|image|2147483647|2147483647|NULL|NULL|1|NULL|NULL|-4|NULL|2147483647|27|YES|34",
            Rows(sim, "exec sp_columns_ex 'SELF', 'lt%', 'dbo', 'zzscratch', 'i%'"));
    }

    /// <summary>
    /// A server <c>sys.servers</c> doesn't hold is Msg 7202 from either
    /// procedure, at its own line (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    public void SchemaRowsets_OfAMissingServer_RaiseMsg7202()
    {
        var sim = Loopback();
        AreEqual(41, sim.AssertSqlError("exec sp_tables_ex 'nosuch'", 7202).LineNumber);
        AreEqual(177, sim.AssertSqlError("exec sp_columns_ex 'nosuch'", 7202).LineNumber);
    }
}
