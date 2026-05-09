namespace SqlServerSimulator;

/// <summary>
/// Covers the LOB family: <c>varchar(MAX)</c>, <c>nvarchar(MAX)</c>,
/// <c>varbinary(MAX)</c> plus the deprecated always-LOB types <c>text</c>,
/// <c>ntext</c>, <c>image</c>. Tests exercise INSERT/SELECT round-trip with
/// sizes that fit inline and sizes that force off-row LOB-chain storage.
/// </summary>
[TestClass]
public sealed class MaxTypesTests
{
    [TestMethod]
    [DataRow("varchar(max)")]
    [DataRow("VARCHAR(MAX)")]
    [DataRow("nvarchar(max)")]
    [DataRow("varbinary(max)")]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void CreateTable_LobTypes_Accepted(string typeSpec)
        => Assert.AreEqual(-1, new Simulation().ExecuteNonQuery($"create table t ( v {typeSpec} )"));

    [TestMethod]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void CreateTable_DeprecatedLobTypes_RejectLengthSpec(string typeSpec)
    {
        // text/ntext/image are always-LOB; SQL Server rejects an explicit length spec.
        var ex = new Simulation().AssertSqlError($"create table t ( v {typeSpec}(50) )", 2716);
        Assert.Contains("Cannot specify a column width", ex.Message);
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

        CollectionAssert.AreEqual(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, (byte[])simulation.ExecuteScalar("select v from t")!);
    }

    [TestMethod]
    public void Insert_VarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        // 25_000 bytes ≫ row's 8060-byte cap and a single LOB page's 8096-byte payload — multi-page chain.
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v varchar(max) )");

        var big = new string('x', 25_000);
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", big)).ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NVarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v nvarchar(max) )");

        var big = new string('ñ', 12_000);
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", big)).ExecuteNonQuery());

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
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", big)).ExecuteNonQuery());

        CollectionAssert.AreEqual(big, (byte[])simulation.ExecuteScalar("select v from t")!);
    }

    [TestMethod]
    public void Insert_Text_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");

        var big = new string('a', 15_000);
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", big)).ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NText_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v ntext )");

        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", "hello world")).ExecuteNonQuery());
        Assert.AreEqual("hello world", simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_Image_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v image )");

        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert into t values (@v)", ("@v", bytes)).ExecuteNonQuery());

        CollectionAssert.AreEqual(bytes, (byte[])simulation.ExecuteScalar("select v from t")!);
    }

    [TestMethod]
    [DataRow("varchar(max)")]
    [DataRow("text")]
    public void Insert_Lob_Null_RoundTrips(string columnType)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        _ = simulation.ExecuteNonQuery("insert into t values (NULL)");

        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Cast_VarcharToMax_Accepted()
        => Assert.AreEqual("hello", new Simulation().ExecuteScalar("select cast('hello' as varchar(max))"));

    [TestMethod]
    public void VarcharMax_NoTruncationCheck_AcceptsLargeBoundedSource()
    {
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
    [DataRow("text", "select v from t where v = 'hello'", "text")]
    [DataRow("ntext", "select v from t where v <> N'world'", "ntext")]
    [DataRow("image", "select v from t where v = 0x01", "image")]
    public void LobTypes_ComparisonOperator_RaisesMsg402(string columnType, string sql, string typeName)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        _ = simulation.ExecuteNonQuery(columnType == "image" ? "insert into t values (0x01)" : columnType == "ntext" ? "insert into t values (N'hello')" : "insert into t values ('hello')");

        var ex = simulation.AssertSqlError(sql, 402);
        Assert.Contains(typeName, ex.Message);
        Assert.Contains("incompatible", ex.Message);
    }

    [TestMethod]
    [DataRow("select v from t order by v")]
    [DataRow("select distinct v from t")]
    public void Text_OrderByOrDistinct_RaisesMsg306(string sql)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");
        var ex = simulation.AssertSqlError(sql, 306);
        Assert.Contains("text, ntext, and image", ex.Message);
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
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE }, (byte[])simulation.ExecuteScalar("select cast(v as varbinary(max)) from t")!);
    }

    [TestMethod]
    public void MultiRow_VarcharMax_OffRowAndInline_BothRoundTrip()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( id int, v varchar(max) )");
        _ = simulation.ExecuteNonQuery("insert into t values (1, 'small')");

        var big = new string('B', 20_000);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("insert into t values (2, @v)", ("@v", big)).ExecuteNonQuery();

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
