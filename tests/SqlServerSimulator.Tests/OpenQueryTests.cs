using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class OpenQueryTests
{
    private static Simulation LocalWithRemote(out Simulation remote, string remoteSetup)
    {
        remote = new Simulation();
        _ = remote.ExecuteNonQuery(remoteSetup);

        var local = new Simulation();
        local.AddRemoteSimulation("RMT", remote);
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'RMT', 'SQL Server'");
        return local;
    }

    [TestMethod]
    public void Basic_ReturnsRemoteRows()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key, name varchar(20) not null); insert t values (1, 'a'), (2, 'b')");
        AreEqual("b", local.ExecuteScalar("select name from OPENQUERY(RMT, 'select id, name from dbo.t where id = 2')"));
    }

    [TestMethod]
    public void Basic_CountAllRows()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (1), (2), (3)");
        AreEqual(3, local.ExecuteScalar("select count(*) from OPENQUERY(RMT, 'select id from dbo.t')"));
    }

    /// <summary>
    /// The pass-through query is arbitrary T-SQL run on the remote, not just a
    /// table name: WHERE / expressions / aggregation all execute remotely.
    /// </summary>
    [TestMethod]
    public void Passthrough_AggregationRunsRemotely()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key, qty int not null); insert t values (1, 5), (2, 10), (3, 7)");
        AreEqual(22, local.ExecuteScalar("select total from OPENQUERY(RMT, 'select sum(qty) as total from dbo.t')"));
    }

    [TestMethod]
    public void Passthrough_ExpressionColumn()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (10)");
        AreEqual(20, local.ExecuteScalar("select doubled from OPENQUERY(RMT, 'select id * 2 as doubled from dbo.t')"));
    }

    /// <summary>
    /// OPENQUERY returns only the FIRST result set of a multi-statement
    /// pass-through batch.
    /// </summary>
    [TestMethod]
    public void FirstResultSetOnly()
    {
        var local = LocalWithRemote(out _, "create table dbo.a (v int not null); insert a values (100); create table dbo.b (v int not null); insert b values (200), (300)");
        // First SELECT yields one row (100); the second SELECT is ignored.
        AreEqual(1, local.ExecuteScalar("select count(*) from OPENQUERY(RMT, 'select v from dbo.a; select v from dbo.b')"));
        AreEqual(100, local.ExecuteScalar("select v from OPENQUERY(RMT, 'select v from dbo.a; select v from dbo.b')"));
    }

    [TestMethod]
    public void Position_InJoin()
    {
        var local = LocalWithRemote(out _, "create table dbo.parts (part_id int not null primary key, name varchar(20) not null); insert parts values (1, 'widget'), (2, 'gadget')");
        _ = local.ExecuteNonQuery("create table dbo.orders (order_id int not null primary key, part_id int not null, qty int not null); insert orders values (1, 1, 5), (2, 2, 10), (3, 1, 7)");

        var qty = local.ExecuteScalar("""
            select sum(o.qty)
            from dbo.orders o
            inner join OPENQUERY(RMT, 'select part_id, name from dbo.parts') p on p.part_id = o.part_id
            where p.name = 'widget'
            """);
        AreEqual(12, qty);
    }

    [TestMethod]
    public void Position_InDerivedTable()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key, v int not null); insert t values (1, 10), (2, 20), (3, 30)");
        AreEqual(50, local.ExecuteScalar("select sum(v) from (select v from OPENQUERY(RMT, 'select id, v from dbo.t where id >= 2')) d"));
    }

    [TestMethod]
    public void Alias_WithoutAs()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (7)");
        AreEqual(7, local.ExecuteScalar("select q.id from OPENQUERY(RMT, 'select id from dbo.t') q"));
    }

    [TestMethod]
    public void Alias_WithAs()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (7)");
        AreEqual(7, local.ExecuteScalar("select q.id from OPENQUERY(RMT, 'select id from dbo.t') as q"));
    }

    [TestMethod]
    public void KeywordIsCaseInsensitive()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (5)");
        AreEqual(5, local.ExecuteScalar("select id from openquery(RMT, 'select id from dbo.t')"));
    }

    [TestMethod]
    public void BracketedServerName_Resolves()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (9)");
        AreEqual(9, local.ExecuteScalar("select id from OPENQUERY([RMT], 'select id from dbo.t')"));
    }

    /// <summary>
    /// A pass-through payload that produces no result set (empty string, a
    /// non-SELECT statement) surfaces a clear <see cref="NotSupportedException"/>
    /// naming the condition — the exact real-server Msg isn't probed.
    /// </summary>
    [TestMethod]
    public void EmptyQuery_NoResultSet_NotSupported()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        var ex = Throws<NotSupportedException>(() => local.ExecuteScalar("select * from OPENQUERY(RMT, '')"));
        Contains("no result set", ex.Message);
    }

    [TestMethod]
    public void NonSelectQuery_NoResultSet_NotSupported()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        var ex = Throws<NotSupportedException>(() => local.ExecuteScalar("select * from OPENQUERY(RMT, 'declare @x int = 5')"));
        Contains("no result set", ex.Message);
    }

    [TestMethod]
    public void UnknownServer_Msg7202()
    {
        var local = new Simulation();
        var ex = local.AssertSqlError("select * from OPENQUERY(NOPE, 'select 1 as x')", 7202);
        Contains("Could not find server 'NOPE' in sys.servers", ex.Message);
    }

    [TestMethod]
    public void VariableQueryArg_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        _ = local.AssertSqlError("declare @q varchar(100) = 'select id from dbo.t'; select * from OPENQUERY(RMT, @q)", 102);
    }

    [TestMethod]
    public void StringLiteralServerArg_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        _ = local.AssertSqlError("select * from OPENQUERY('RMT', 'select id from dbo.t')", 102);
    }

    [TestMethod]
    public void TooFewArgs_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        _ = local.AssertSqlError("select * from OPENQUERY(RMT)", 102);
    }

    [TestMethod]
    public void TooManyArgs_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        _ = local.AssertSqlError("select * from OPENQUERY(RMT, 'select id from dbo.t', 'extra')", 102);
    }

    [TestMethod]
    public void ConcatenatedQueryArg_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        _ = local.AssertSqlError("select * from OPENQUERY(RMT, 'select ' + 'id from dbo.t')", 102);
    }

    /// <summary>
    /// A column-alias list on OPENQUERY is rejected (real SQL Server: Msg 102
    /// near the first alias identifier; the simulator raises Msg 102 near the
    /// opening <c>(</c>). Without the guard the general FROM parser tolerates
    /// and ignores the list, silently keeping the remote column names.
    /// </summary>
    [TestMethod]
    public void ColumnAliasList_Msg102()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int not null primary key); insert t values (1)");
        _ = local.AssertSqlError("select c1 from OPENQUERY(RMT, 'select id from dbo.t') q(c1)", 102);
    }
}
