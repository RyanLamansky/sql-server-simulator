using System.Data.Common;

namespace SqlServerSimulator;

/// <summary>
/// Covers the LOB family: <c>varchar(MAX)</c>, <c>nvarchar(MAX)</c>,
/// <c>varbinary(MAX)</c> (column-level MAX siblings of bounded var types)
/// plus the deprecated always-LOB types <c>text</c>, <c>ntext</c>, <c>image</c>.
/// Tests exercise the full INSERT/SELECT round-trip with sizes that fit
/// inline (small values) and sizes that force off-row LOB-chain storage.
/// </summary>
[TestClass]
public sealed class MaxTypesTests
{
    [TestMethod]
    [DataRow("varchar(max)")]
    [DataRow("VARCHAR(MAX)")]
    [DataRow("nvarchar(max)")]
    [DataRow("varbinary(max)")]
    public void CreateTable_MaxTypes_Accepted(string typeSpec)
    {
        var simulation = new Simulation();
        Assert.AreEqual(-1, simulation.ExecuteNonQuery($"create table t ( v {typeSpec} )"));
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void CreateTable_DeprecatedLobTypes_Accepted(string typeSpec)
    {
        var simulation = new Simulation();
        Assert.AreEqual(-1, simulation.ExecuteNonQuery($"create table t ( v {typeSpec} )"));
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void CreateTable_DeprecatedLobTypes_RejectLengthSpec(string typeSpec)
    {
        // text/ntext/image are always-LOB; SQL Server rejects an explicit
        // length spec the way it does for any fixed-length type.
        var simulation = new Simulation();
        var x = Assert.Throws<DbException>(() => simulation.ExecuteNonQuery($"create table t ( v {typeSpec}(50) )"));
        Assert.Contains("Cannot specify a column width", x.Message);
    }

    [TestMethod]
    public void Insert_VarcharMax_SmallValue_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values ('hello')");
        Assert.AreEqual("hello", simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NVarcharMax_SmallValue_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v nvarchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (N'héllo')");
        Assert.AreEqual("héllo", simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_VarbinaryMax_SmallValue_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varbinary(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (0xDEADBEEF)");

        var result = simulation.ExecuteScalar("select v from t");
        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, (byte[])result!);
    }

    [TestMethod]
    public void Insert_VarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        // 25_000 bytes ≫ both the row's 8060-byte cap and a single LOB
        // page's 8096-byte payload — forces a multi-page LOB chain.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varchar(max) )");

        var big = new string('x', 25_000);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", big));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NVarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v nvarchar(max) )");

        var big = new string('ñ', 12_000);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", big));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_VarbinaryMax_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varbinary(max) )");

        var big = new byte[20_000];
        for (var i = 0; i < big.Length; i++)
            big[i] = (byte)(i & 0xFF);

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", big));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        var result = simulation.ExecuteScalar("select v from t");
        CollectionAssert.AreEqual(big, (byte[])result!);
    }

    [TestMethod]
    public void Insert_Text_LargeValue_RoundTripsThroughLobChain()
    {
        // text is always-LOB; even small values flow through the chain.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");

        var big = new string('a', 15_000);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", big));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NText_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v ntext )");

        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", "hello world"));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        Assert.AreEqual("hello world", simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_Image_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v image )");

        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (@v)", ("@v", bytes));
        Assert.AreEqual(1, command.ExecuteNonQuery());

        var result = simulation.ExecuteScalar("select v from t");
        CollectionAssert.AreEqual(bytes, (byte[])result!);
    }

    [TestMethod]
    public void Insert_VarcharMax_Null_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (NULL)");

        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_Text_Null_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");
        _ = simulation.ExecuteNonQuery("insert into t values (NULL)");

        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Cast_VarcharToMax_Accepted()
    {
        // varchar(max) target in CAST should accept and pass through.
        var simulation = new Simulation();
        Assert.AreEqual("hello", simulation.ExecuteScalar("select cast('hello' as varchar(max))"));
    }

    [TestMethod]
    public void VarcharMax_NoTruncationCheck_AcceptsLargeBoundedSource()
    {
        // varchar(max) doesn't truncate. Inserting any size succeeds.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values ('this is well within the inline 8060-byte cap')");

        Assert.AreEqual(
            "this is well within the inline 8060-byte cap",
            simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Where_FiltersByVarcharMaxEquality()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'one'), (2, 'two'), (3, 'three')");

        Assert.AreEqual(2, simulation.ExecuteScalar("select id from t where v = 'two'"));
    }

    [TestMethod]
    public void Text_ComparisonOperator_RaisesMsg402()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");
        _ = simulation.ExecuteNonQuery("insert into t values ('hello')");

        var x = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select v from t where v = 'hello'"));
        Assert.Contains("incompatible", x.Message);
        Assert.Contains("text", x.Message);
    }

    [TestMethod]
    public void NText_ComparisonOperator_RaisesMsg402()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v ntext )");
        _ = simulation.ExecuteNonQuery("insert into t values (N'hello')");

        var x = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select v from t where v <> N'world'"));
        Assert.Contains("ntext", x.Message);
        Assert.Contains("incompatible", x.Message);
    }

    [TestMethod]
    public void Image_ComparisonOperator_RaisesMsg402()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v image )");
        _ = simulation.ExecuteNonQuery("insert into t values (0x01)");

        var x = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select v from t where v = 0x01"));
        Assert.Contains("image", x.Message);
    }

    [TestMethod]
    public void Text_OrderBy_RaisesMsg306()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");

        var x = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select v from t order by v"));
        Assert.Contains("text, ntext, and image", x.Message);
    }

    [TestMethod]
    public void Text_Distinct_RaisesMsg306()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");

        var x = Assert.Throws<DbException>(() => simulation.ExecuteScalar("select distinct v from t"));
        Assert.Contains("text, ntext, and image", x.Message);
    }

    [TestMethod]
    public void Text_Like_Allowed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, v text )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'hello world'), (2, 'goodbye world'), (3, 'farewell')");

        Assert.AreEqual(1, simulation.ExecuteScalar("select id from t where v like 'hello%'"));
    }

    [TestMethod]
    public void Text_CastToVarchar_Allowed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");
        _ = simulation.ExecuteNonQuery("insert into t values ('hello')");

        Assert.AreEqual("hello", simulation.ExecuteScalar("select cast(v as varchar(50)) from t"));
    }

    [TestMethod]
    public void NText_CastToNVarcharMax_Allowed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v ntext )");
        _ = simulation.ExecuteNonQuery("insert into t values (N'hello')");

        Assert.AreEqual("hello", simulation.ExecuteScalar("select cast(v as nvarchar(max)) from t"));
    }

    [TestMethod]
    public void Image_CastToVarbinaryMax_Allowed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v image )");
        _ = simulation.ExecuteNonQuery("insert into t values (0xCAFE)");

        var result = simulation.ExecuteScalar("select cast(v as varbinary(max)) from t");
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE }, (byte[])result!);
    }

    [TestMethod]
    public void MultiRow_VarcharMax_OffRowAndInline_BothRoundTrip()
    {
        // Insert a small value (inline) and a big value (off-row) into the
        // same table; both should round-trip through their respective paths.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'small')");

        var big = new string('B', 20_000);
        using var connection = simulation.CreateOpenConnection();
        using var command = connection.CreateCommand("insert into t values (2, @v)", ("@v", big));
        _ = command.ExecuteNonQuery();

        using var reader = simulation.ExecuteReader("select id, v from t order by id");
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(1, reader.GetInt32(0));
        Assert.AreEqual("small", reader.GetString(1));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(big, reader.GetString(1));
        Assert.IsFalse(reader.Read());
    }
}
