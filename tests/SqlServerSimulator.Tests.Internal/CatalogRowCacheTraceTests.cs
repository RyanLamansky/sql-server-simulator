using System.Data.Common;
using SqlServerSimulator.Parser;
using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// Guards when <c>Schemas.CatalogRowCache</c> serves a catalog view's rows
/// (<c>CacheHit</c>), regenerates them (<c>CacheBuild</c>), is bypassed
/// (<c>Scan</c>), and when a join probes a cached rowset's persisted index
/// (<c>IndexJoin</c>), through the opt-in <see cref="CatalogPushdownDiagnostics"/>
/// trace. The public <c>CatalogRowCacheTests</c> pin the rows; these pin the path.
/// </summary>
[TestClass]
public sealed class CatalogRowCacheTraceTests
{
    private static List<string> Trace(DbConnection connection, string query)
    {
        CatalogPushdownDiagnostics.Sink = [];
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            do
            {
                while (reader.Read())
                {
                }
            }
            while (reader.NextResult());
            return CatalogPushdownDiagnostics.Sink;
        }
        finally
        {
            CatalogPushdownDiagnostics.Sink = null;
        }
    }

    private static DbConnection Open(string setup)
    {
        var connection = new Simulation().CreateDbConnection();
        connection.Open();
        Execute(connection, setup);
        return connection;
    }

    private static void Execute(DbConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        _ = command.ExecuteNonQuery();
    }

    [TestMethod]
    public void SecondRead_HitsTheCache_AndDdlRebuildsIt()
    {
        var connection = Open("create table t (a int)");
        Contains("CacheBuild(tables)", Trace(connection, "select count(*) from sys.tables"));
        Contains("CacheHit(tables)", Trace(connection, "select count(*) from sys.tables"));
        Execute(connection, "create table u (a int)");
        Contains("CacheBuild(tables)", Trace(connection, "select count(*) from sys.tables"));
    }

    [TestMethod]
    public void OrdinaryDml_KeepsTheCache()
    {
        var connection = Open("create table t (a int); select count(*) from sys.columns");
        Execute(connection, "insert t values (1); update t set a = 2; delete t");
        Contains("CacheHit(columns)", Trace(connection, "select count(*) from sys.columns"));
    }

    [TestMethod]
    public void ReadOnlySystemProcedure_KeepsTheCache_AndAMutatingOneDoesNot()
    {
        var connection = Open("create table t (a int); select count(*) from sys.tables");
        Execute(connection, "exec sp_help 't'");
        Contains("CacheHit(tables)", Trace(connection, "select count(*) from sys.tables"));
        Execute(connection, "exec sp_addextendedproperty N'd', N'x', N'SCHEMA', N'dbo', N'TABLE', N't'");
        Contains("CacheBuild(tables)", Trace(connection, "select count(*) from sys.tables"));
    }

    [TestMethod]
    public void EquiJoin_ProbesThePersistedIndex()
    {
        var connection = Open("create table t (a int, b int)");
        var trace = Trace(connection, "select count(*) from sys.tables t join sys.columns c on c.object_id = t.object_id");
        Contains("IndexJoin(columns)", trace);
    }

    [TestMethod]
    public void IdentityInsert_RebuildsIdentityColumns_AndNothingElse()
    {
        var connection = Open("create table t (id int identity, a int); select count(*) from sys.identity_columns; select count(*) from sys.columns");
        Contains("CacheHit(identity_columns)", Trace(connection, "select count(*) from sys.identity_columns"));
        Execute(connection, "insert t (a) values (1)");
        Contains("CacheBuild(identity_columns)", Trace(connection, "select count(*) from sys.identity_columns"));
        Contains("CacheHit(columns)", Trace(connection, "select count(*) from sys.columns"));
    }

    [TestMethod]
    public void TempdbRead_BypassesTheCache()
    {
        var connection = Open("create table #t (a int)");
        var trace = Trace(connection, "select count(*) from tempdb.sys.tables");
        Contains("Scan(tables)", trace);
        DoesNotContain("CacheBuild(tables)", trace);
    }

    [TestMethod]
    public void RestrictedPrincipal_BypassesTheCache()
    {
        var connection = Open("create table t (a int); create user u without login; grant select on t to u");
        var trace = Trace(connection, "execute as user = 'u'; select count(*) from sys.tables; revert");
        Contains("Scan(tables)", trace);
        DoesNotContain("CacheBuild(tables)", trace);
    }

    [TestMethod]
    public void SeekOnACachedView_ReadsTheIndex()
    {
        var connection = Open("create table t (a int); select count(*) from sys.columns");
        var trace = Trace(connection, "select name from sys.columns where object_id = object_id('t')");
        Contains("Seek(columns.object_id)", trace);
        Contains("CacheHit(columns)", trace);
    }
}
