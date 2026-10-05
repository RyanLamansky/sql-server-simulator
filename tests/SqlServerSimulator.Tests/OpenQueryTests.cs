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
        // The catalog is where the pass-through queries' unqualified names
        // bind; without one a session starts in master.
        _ = local.ExecuteNonQuery("exec sp_addlinkedserver 'RMT', '', 'MSOLEDBSQL', @catalog = 'simulated'");
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
    /// What real's provider refuses describing a pass-through query: an empty
    /// one, one that names what doesn't bind, one that doesn't parse, one
    /// returning no rowset, and a rowset with two columns of one name.
    /// </summary>
    [TestMethod]
    [DataRow("''", new[] { 7412, 7399, 7321 })]
    [DataRow("'select * from simulated.dbo.nosuch'", new[] { 7412, 8180, 208 })]
    [DataRow("'selec 1'", new[] { 11529, 2812 })]
    [DataRow("'declare @x int = 5'", new[] { 7357 })]
    [DataRow("'raiserror(''boom'', 16, 1)'", new[] { 7357 })]
    [DataRow("'select 1 a, 2 a'", new[] { 492 })]
    public void DescribingTheQuery_RealRefusals(string query, int[] numbers)
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int)");
        var error = Throws<SimulatedSqlException>(() => local.ExecuteScalar($"select * from openquery(RMT, {query})"));
        CollectionAssert.AreEquivalent(numbers, error.Errors.Select(entry => entry.Number).ToArray());
    }

    /// <summary>
    /// An error the server raises reading the rows reaches the reader after the
    /// rows ahead of it and ends the batch, where a TRY catches it.
    /// </summary>
    [TestMethod]
    public void RuntimeErrorReadingRows_EndsTheBatch()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int); insert t values (1), (2)");
        var error = local.AssertSqlError("select * from openquery(RMT, 'select 1 / (id - 2) x from dbo.t'); select 'after'", 8134);
        AreEqual(1, error.LineNumber);
        AreEqual(8134, local.ExecuteScalar("declare @n int; begin try declare @x int; select @x = x from openquery(RMT, 'select 1 / (id - 2) x from dbo.t') end try begin catch set @n = error_number() end catch; select @n"));
    }

    /// <summary>
    /// The provider reads a <c>smallmoney</c> as <c>money</c>, a <c>json</c> as
    /// its text, and a key column as NOT NULL.
    /// </summary>
    [TestMethod]
    public void ProviderTypesAndNullability()
    {
        var local = LocalWithRemote(out _, "create table dbo.t (id int primary key, m smallmoney, j json); insert t values (1, 1.5, '{\"a\":1}')");
        AreEqual("int|0;money|1;varchar|1", local.ExecuteScalar("""
            select * into #x from openquery(RMT, 'select id, m, j from dbo.t');
            select string_agg(concat(type_name(system_type_id), '|', cast(is_nullable as int)), ';') within group (order by column_id)
            from tempdb.sys.columns where object_id = object_id('tempdb..#x')
            """));
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
