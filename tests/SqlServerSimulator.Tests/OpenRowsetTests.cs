using System.Text;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class OpenRowsetTests
{
    private static readonly byte[] Utf16File = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("hé")];

    private static Simulation Files() => BulkInsertTests.WithFileBytes(
        ("j.json", Encoding.UTF8.GetBytes("""[{"id":1,"name":"a"},{"id":2,"name":"bé"}]""")),
        ("bin.dat", [0, 1, 2, 255]),
        ("u16.txt", Utf16File),
        ("u8.txt", Encoding.UTF8.GetBytes("b\u00e9\u20ac")),
        ("bad.txt", [(byte)'b', 0xE9]),
        ("a.csv", Encoding.UTF8.GetBytes("1,alpha\n2,beta\n")),
        ("f.fmt", Encoding.UTF8.GetBytes("14.0\n2\n1 SQLCHAR 0 10 \",\" 1 id \"\"\n2 SQLCHAR 0 20 \"\\n\" 2 name SQL_Latin1_General_CP1_CI_AS\n")));

    [TestMethod]
    public void SingleBlob_IsTheFilesBytes()
        => CollectionAssert.AreEqual(new byte[] { 0, 1, 2, 255 }, (byte[])Files().ExecuteScalar("select BulkColumn from openrowset(bulk 'bin.dat', single_blob) as x")!);

    [TestMethod]
    public void SingleForms_ColumnTypes()
        => AreEqual("varbinary:-1|varchar:-1|nvarchar:-1", Files().ExecuteScalar("""
            select * into b from openrowset(bulk 'bin.dat', single_blob) x;
            select * into c from openrowset(bulk 'j.json', single_clob) x;
            select * into n from openrowset(bulk 'u16.txt', single_nclob) x;
            select string_agg(concat(type_name(system_type_id), ':', max_length), '|') within group (order by object_id)
            from sys.columns where name = 'BulkColumn' and object_id in (object_id('b'), object_id('c'), object_id('n'))
            """));

    [TestMethod]
    public void SingleClob_DecodesUtf8IntoTheCodePage()
        => AreEqual("bé€:3", Files().ExecuteScalar("select concat(BulkColumn, ':', datalength(BulkColumn)) from openrowset(bulk 'u8.txt', single_clob) x"));

    [TestMethod]
    public void SingleClob_InvalidUtf8_IsTruncation()
        => AreEqual(4, Files().AssertSqlError("select * from openrowset(bulk 'bad.txt', single_clob) x", 4863).State);

    [TestMethod]
    public void SingleNclob_NeedsAByteOrderMark()
    {
        var sim = Files();
        AreEqual("hé", sim.ExecuteScalar("select BulkColumn from openrowset(bulk 'u16.txt', single_nclob) x"));
        _ = sim.AssertSqlError("select * from openrowset(bulk 'j.json', single_nclob) x", 4809);
        _ = sim.AssertSqlError("select * from openrowset(bulk 'u16.txt', single_clob) x", 4806);
    }

    [TestMethod]
    public void OpenJsonOverSingleClob()
        => AreEqual("1:a,2:bé", Files().ExecuteScalar("""
            select string_agg(concat(j.id, ':', j.name), ',') within group (order by j.id)
            from openrowset(bulk 'j.json', single_clob) as f
            cross apply openjson(f.BulkColumn) with (id int, name nvarchar(10)) as j
            """));

    [TestMethod]
    public void XmlFromSingleBlob()
        => AreEqual(2, BulkInsertTests.WithFiles(("x.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><r><i/><i/></r>"))
            .ExecuteScalar("select cast(BulkColumn as xml).value('count(/r/i)', 'int') from openrowset(bulk 'x.xml', single_blob) x"));

    [TestMethod]
    public void ColumnAliasList_RenamesBulkColumn()
        => AreEqual(4L, Files().ExecuteScalar("select datalength(c) from openrowset(bulk 'bin.dat', single_blob) x(c)"));

    [TestMethod]
    public void Grammar()
    {
        var sim = Files();
        _ = sim.AssertSqlError("select * from openrowset(bulk 'bin.dat', single_blob)", 491);
        _ = sim.AssertSqlError("select * from openrowset(bulk 'bin.dat', single_blob, single_clob) x", 471);
        _ = sim.AssertSqlError("select * from openrowset(bulk 'a.csv') x", 15808);
        _ = sim.AssertSqlError("select * from openrowset(bulk 'a.csv', format = 'CSV') x", 472);
        _ = sim.AssertSqlError("select * from openrowset(bulk 'a.csv', format = 'CSV') with (id int) x", 5374);
        _ = sim.AssertSqlError("insert openrowset(bulk 'a.csv', single_clob) x values ('a')", 156);
        sim.ValidateSyntaxError("declare @p varchar(9) = 'a.csv'; select * from openrowset(bulk @p, single_clob) x", "@p");
    }

    [TestMethod]
    public void MissingFile_AtState4_UncaughtByTry()
    {
        var sim = Files();
        _ = sim.ExecuteNonQuery("create table log (s varchar(9))");
        AreEqual(4, sim.AssertSqlError("begin try select * from openrowset(bulk 'nope', single_blob) x; end try begin catch insert log values ('c'); end catch", 4860).State);
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void FormatFile_RowsAndFirstRow()
    {
        var sim = Files();
        AreEqual("1:alpha,2:beta", sim.ExecuteScalar("select string_agg(concat(id, ':', name), ',') from openrowset(bulk 'a.csv', formatfile = 'f.fmt') x"));
        AreEqual("beta", sim.ExecuteScalar("select name from openrowset(bulk 'a.csv', formatfile = 'f.fmt', firstrow = 2) x"));
        AreEqual("varchar:10", sim.ExecuteScalar("""
            select * into t from openrowset(bulk 'a.csv', formatfile = 'f.fmt') x;
            select concat(type_name(system_type_id), ':', max_length) from sys.columns where object_id = object_id('t') and name = 'id'
            """));
        AreEqual(3, sim.AssertSqlError("select * from openrowset(bulk 'a.csv', formatfile = 'nope.fmt') x", 4860).State);
    }

    [TestMethod]
    public void NonSysadmin_ReachesNoFile()
    {
        var sim = Files();
        _ = sim.ExecuteNonQuery("create login bl with password = 'Xx!12345678'; create user bl for login bl");
        AreEqual(75, sim.AssertSqlError("execute as login = 'bl'; select * from openrowset(bulk 'bin.dat', single_blob) x", 4860).State);
    }

    private const string EnableAdHoc = "exec sp_configure 'show advanced options', 1; reconfigure; exec sp_configure 'Ad Hoc Distributed Queries', 1; reconfigure;";

    [TestMethod]
    public void AdHoc_OffByDefault_RefusesTheBatch()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table log (s varchar(9))");
        _ = sim.AssertSqlError("insert log values ('ran'); select * from openrowset('MSOLEDBSQL', 'Server=localhost', 'select 1 a') x", 15281);
        AreEqual(0, sim.ExecuteScalar("select count(*) from log"));
    }

    [TestMethod]
    public void AdHoc_NonSqlServerProvider()
        => new Simulation().AssertSqlError("select * from openrowset('MSDASQL', 'x', 'select 1 a') x", 7222, "Only a SQL Server provider is allowed on this instance.");

    [TestMethod]
    public void AdHoc_QueryAndObjectForms_ReadTheLocalInstance()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc + " create table t (id int, name varchar(10)); insert t values (1, 'a'), (2, 'b')");
        AreEqual(42, sim.ExecuteScalar("select v from openrowset('SQLNCLI', 'Server=(local);Trusted_Connection=yes', 'select 42 v') x"));
        AreEqual("b", sim.ExecuteScalar("select name from openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.t) as x where id = 2"));
        AreEqual("simulated", sim.ExecuteScalar("select d from openrowset('MSOLEDBSQL', 'Server=localhost;Database=simulated', 'select db_name() d') x"));
    }

    [TestMethod]
    public void AdHoc_XmlColumn_NamesTheAlias()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc);
        Assert.Contains("Remote object 'y'", sim.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=localhost', 'select cast(''<a/>'' as xml) x') y", 9514).Message);
        Assert.Contains("Remote object 'OPENROWSET'", sim.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=localhost', 'select cast(''<a/>'' as xml) x')", 9514).Message);
    }

    [TestMethod]
    public void AdHoc_ObjectNaming()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc + " create table t (id int)");
        _ = sim.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=localhost', dbo.t) x", 7313);
        var missing = sim.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.nope) x", 7314);
        AreEqual("The OLE DB provider \"MSOLEDBSQL19\" for linked server \"(null)\" does not contain the table \"\"simulated\".\"dbo\".\"nope\"\". The table either does not exist or the current user does not have permissions on that table.", missing.Errors[0].Message);
        _ = sim.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=localhost', s.simulated.dbo.t) x", 117);
    }

    [TestMethod]
    public void AdHoc_ServerNameRoutesThroughTheRemoteRegistry()
    {
        var remote = new Simulation();
        _ = remote.ExecuteNonQuery("create table t (v varchar(9)); insert t values ('remote')");
        var local = new Simulation();
        local.AddRemoteSimulation("far", remote);
        _ = local.ExecuteNonQuery(EnableAdHoc);
        AreEqual("remote", local.ExecuteScalar("select v from openrowset('MSOLEDBSQL', 'Server=tcp:far,1433;UID=u;PWD=p', 'select v from simulated.dbo.t') x"));
        _ = local.AssertSqlError("select * from openrowset('MSOLEDBSQL', 'Server=nowhere', 'select 1 a') x", 2);
    }

    [TestMethod]
    public void AdHoc_Writes()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc + " create table t (id int primary key, name varchar(10)); insert t values (1, 'a'), (2, 'b')");
        _ = sim.ExecuteNonQuery("""
            insert openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.t) values (3, 'c');
            update openrowset('MSOLEDBSQL', 'Server=localhost', 'select id, name from simulated.dbo.t where id = 2') set name = 'z';
            delete x from openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.t) x where id = 1
            """);
        AreEqual("2:z,3:c", sim.ExecuteScalar("select string_agg(concat(id, ':', name), ',') within group (order by id) from t"));
    }

    [TestMethod]
    public void AdHoc_WriteInTransaction_IsTheLoopbacksRefusal()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc + " create table t (id int)");
        _ = sim.AssertSqlError("begin tran; insert openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.t) values (1)", 3910);
        AreEqual(0, sim.ExecuteScalar("select @@trancount"));
    }

    [TestMethod]
    public void AdHoc_MergeTarget_IsRemote()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc + " create table t (id int)");
        _ = sim.AssertSqlError("merge openrowset('MSOLEDBSQL', 'Server=localhost', simulated.dbo.t) as g using (select 1 id) s on g.id = s.id when not matched then insert values (1);", 5315);
    }

    [TestMethod]
    public void AdHoc_ViewBody_RefusedOnceTheOptionIsOff()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery(EnableAdHoc);
        sim.ExecuteBatches("create view v as select * from openrowset('MSOLEDBSQL', 'Server=localhost', 'select 1 a') x");
        AreEqual(1, sim.ExecuteScalar("select a from v"));
        _ = sim.ExecuteNonQuery("exec sp_configure 'Ad Hoc Distributed Queries', 0; reconfigure");
        var ex = sim.AssertSqlError("select a from v", 15281);
        AreEqual(4413, ex.Errors[^1].Number);
    }

    [TestMethod]
    public void OpenDataSource_HasNoDataLinkProvider()
    {
        var sim = new Simulation();
        _ = sim.AssertSqlError("select * from opendatasource('MSOLEDBSQL', 'Data Source=x').master.sys.objects", 15281);
        _ = sim.AssertSqlError("select * from opendatasource('MSDASQL', 'x').master.sys.objects", 7222);
        _ = sim.ExecuteNonQuery(EnableAdHoc);
        _ = sim.AssertSqlError("select * from opendatasource('MSOLEDBSQL', 'Data Source=x').master.sys.objects", 7302);
        _ = sim.AssertSqlError("insert opendatasource('MSOLEDBSQL', 'Data Source=x').db.dbo.t values (1)", 7302);
    }

    [TestMethod]
    public void FormatFileWithDecimalHostType_Raises4838()
        => BulkInsertTests.WithFiles(("p.txt", "1\n"), ("d.fmt", "14.0\n1\n1 SQLDECIMAL 0 19 \"\" 1 d \"\"\n"))
            .AssertSqlError("select * from openrowset(bulk 'p.txt', formatfile = 'd.fmt') x", 4838,
                "The bulk data source does not support the SQLNUMERIC or SQLDECIMAL data types.");
}
