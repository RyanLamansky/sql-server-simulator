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
        Assert.AreEqual("hello", new Simulation().ExecuteScalar("""
            create table t ( v varchar(max) );
            insert t values ('hello');
            select v from t
            """));
    }

    [TestMethod]
    public void Insert_NVarcharMax_SmallValue_RoundTrips()
    {
        Assert.AreEqual("héllo", new Simulation().ExecuteScalar("""
            create table t ( v nvarchar(max) );
            insert t values (N'héllo');
            select v from t
            """));
    }

    [TestMethod]
    public void Insert_VarbinaryMax_SmallValue_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( v varbinary(max) );
            insert t values (0xDEADBEEF)
            """);

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
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", big)).ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NVarcharMax_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v nvarchar(max) )");

        var big = new string('ñ', 12_000);
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", big)).ExecuteNonQuery());

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
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", big)).ExecuteNonQuery());

        CollectionAssert.AreEqual(big, (byte[])simulation.ExecuteScalar("select v from t")!);
    }

    [TestMethod]
    public void Insert_Text_LargeValue_RoundTripsThroughLobChain()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v text )");

        var big = new string('a', 15_000);
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", big)).ExecuteNonQuery());

        Assert.AreEqual(big, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_NText_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v ntext )");

        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", "hello world")).ExecuteNonQuery());
        Assert.AreEqual("hello world", simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Insert_Image_RoundTrips()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t ( v image )");

        var bytes = new byte[] { 0x01, 0x02, 0x03, 0x04 };
        using var connection = simulation.CreateOpenConnection();
        Assert.AreEqual(1, connection.CreateCommand("insert t values (@v)", ("@v", bytes)).ExecuteNonQuery());

        CollectionAssert.AreEqual(bytes, (byte[])simulation.ExecuteScalar("select v from t")!);
    }

    [TestMethod]
    [DataRow("varchar(max)")]
    [DataRow("text")]
    public void Insert_Lob_Null_RoundTrips(string columnType)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        _ = simulation.ExecuteNonQuery("insert t values (NULL)");

        Assert.AreEqual(DBNull.Value, simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Cast_VarcharToMax_Accepted()
        => Assert.AreEqual("hello", new Simulation().ExecuteScalar("select cast('hello' as varchar(max))"));

    [TestMethod]
    public void VarcharMax_NoTruncationCheck_AcceptsLargeBoundedSource()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( v varchar(max) );
            insert t values ('this is well within the inline 8060-byte cap')
            """);

        Assert.AreEqual(
            "this is well within the inline 8060-byte cap",
            simulation.ExecuteScalar("select v from t"));
    }

    [TestMethod]
    public void Where_FiltersByVarcharMaxEquality()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, v varchar(max) );
            insert t values (1, 'one'), (2, 'two'), (3, 'three')
            """);

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
        _ = simulation.ExecuteNonQuery(columnType == "image" ? "insert t values (0x01)" : columnType == "ntext" ? "insert t values (N'hello')" : "insert t values ('hello')");

        var ex = simulation.AssertSqlError(sql, 402);
        Assert.Contains(typeName, ex.Message);
        Assert.Contains("incompatible", ex.Message);
    }

    /// <summary>
    /// The sorting and grouping slots split their rejection by family — the
    /// legacy trio takes Msg 306 at State 2, <c>xml</c> Msg 305 and the two
    /// spatial types Msg 249, which is the only one that names the clause.
    /// Probed against SQL Server 2025 (2026-08-08), on an empty table: all
    /// three bind while compiling.
    /// </summary>
    [TestMethod]
    [DataRow("text", "select v from t order by v", 306, "text, ntext, and image")]
    [DataRow("ntext", "select v from t order by v", 306, "text, ntext, and image")]
    [DataRow("image", "select v from t group by v", 306, "text, ntext, and image")]
    [DataRow("text", "select v from t group by v", 306, "text, ntext, and image")]
    [DataRow("xml", "select v from t order by v", 305, "The XML data type cannot be compared or sorted")]
    [DataRow("xml", "select v from t group by v", 305, "The XML data type cannot be compared or sorted")]
    [DataRow("geography", "select v from t order by v", 249, "The type \"geography\" is not comparable. It cannot be used in the ORDER BY clause.")]
    [DataRow("geometry", "select v from t group by v", 249, "The type \"geometry\" is not comparable. It cannot be used in the GROUP BY clause.")]
    public void NotComparableType_SortOrGroupSlot_RaisesPerFamilyError(string columnType, string sql, int number, string fragment)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        var ex = simulation.AssertSqlError(sql, number);
        Assert.Contains(fragment, ex.Message);
        Assert.AreEqual((byte)(number == 306 ? 2 : 1), ex.State);
    }

    /// <summary>
    /// DISTINCT and the deduping set operators make no such split: one message
    /// each, naming the type, for every non-comparable family. <c>UNION ALL</c>
    /// only concatenates and takes all of them.
    /// </summary>
    [TestMethod]
    [DataRow("text", "select distinct v from t", 421, "The text data type cannot be selected as DISTINCT")]
    [DataRow("ntext", "select distinct id, v from t", 421, "The ntext data type cannot be selected as DISTINCT")]
    [DataRow("image", "select distinct v from t", 421, "The image data type cannot be selected as DISTINCT")]
    [DataRow("xml", "select distinct v from t", 421, "The xml data type cannot be selected as DISTINCT")]
    [DataRow("geography", "select distinct v from t", 421, "The geography data type cannot be selected as DISTINCT")]
    [DataRow("text", "select v from t union select v from t", 5335, "The data type text cannot be used as an operand to the UNION")]
    [DataRow("image", "select v from t except select v from t", 5335, "The data type image cannot be used as an operand to the UNION")]
    [DataRow("xml", "select v from t intersect select v from t", 5335, "The data type xml cannot be used as an operand to the UNION")]
    [DataRow("geometry", "select v from t union select v from t", 5335, "The data type geometry cannot be used as an operand to the UNION")]
    public void NotComparableType_DistinctOrDedupingSetOp_RaisesOneMessageNamingTheType(
        string columnType, string sql, int number, string fragment)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( id int, v {columnType} )");
        var ex = simulation.AssertSqlError(sql, number);
        Assert.Contains(fragment, ex.Message);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("xml")]
    [DataRow("geography")]
    public void NotComparableType_UnionAll_Allowed(string columnType)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from (select v from t union all select v from t) u"));
    }

    /// <summary>
    /// COUNT refuses the legacy trio outright but counts <c>xml</c> and the
    /// spatial types — until a DISTINCT asks it to fold duplicates. The
    /// DISTINCT form reports State 2 where the plain one reports 1, a split
    /// MAX / MIN don't make.
    /// </summary>
    [TestMethod]
    [DataRow("text", "select count(v) from t", "count", (byte)1)]
    [DataRow("ntext", "select count_big(v) from t", "count_big", (byte)1)]
    [DataRow("image", "select count(distinct v) from t", "count", (byte)2)]
    [DataRow("xml", "select count(distinct v) from t", "count", (byte)2)]
    [DataRow("geography", "select count(distinct v) from t", "count", (byte)2)]
    public void NotComparableType_Count_RaisesMsg8117(string columnType, string sql, string operatorName, byte state)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        var ex = simulation.AssertSqlError(sql, 8117);
        Assert.Contains($"is invalid for {operatorName} operator", ex.Message);
        Assert.AreEqual(state, ex.State);
    }

    [TestMethod]
    [DataRow("xml")]
    [DataRow("geography")]
    [DataRow("geometry")]
    public void XmlOrSpatial_UndistinctCount_Allowed(string columnType)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(v) from t"));
    }

    /// <summary>
    /// <c>LIKE</c> is the one comparison the legacy trio keeps — Msg 306's own
    /// wording names it as an exemption — while <c>xml</c> and the spatial pair
    /// are refused in either slot with the ordinary argument-type Msg 8116.
    /// </summary>
    [TestMethod]
    [DataRow("xml", "select v from t where v like '%a%'", 1)]
    [DataRow("xml", "select v from t where 'abc' like v", 2)]
    [DataRow("xml", "select v from t where v like '%a%' escape '!'", 1)]
    [DataRow("geography", "select v from t where v like '%a%'", 1)]
    [DataRow("geometry", "select v from t where 'abc' like v", 2)]
    public void XmlOrSpatial_Like_RaisesMsg8116(string columnType, string sql, int argumentIndex)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        var ex = simulation.AssertSqlError(sql, 8116);
        Assert.AreEqual(
            $"Argument data type {columnType} is invalid for argument {argumentIndex} of like function.",
            ex.Message);
    }

    [TestMethod]
    [DataRow("text")]
    [DataRow("ntext")]
    [DataRow("image")]
    public void LegacyLob_Like_Allowed(string columnType)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        Assert.AreEqual(0, simulation.ExecuteScalar("select count(*) from t where v like '%a%'"));
    }

    /// <summary>
    /// A spatial operand reaching MAX / MIN draws two errors from real, the
    /// CLR-type Msg 6210 ahead of the ordinary Msg 8117 — so the exception's
    /// own number is 6210 and the 8117 follows in <c>Errors</c>.
    /// </summary>
    [TestMethod]
    [DataRow("geography", "max")]
    [DataRow("geometry", "min")]
    public void Spatial_MaxOrMin_LeadsWithMsg6210(string columnType, string aggregate)
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery($"create table t ( v {columnType} )");
        var ex = simulation.AssertSqlError($"select {aggregate}(v) from t", 6210);
        Assert.AreEqual($"CLR type '{columnType}' is not fully comparable.", ex.Message);
        Assert.AreEqual(2, ex.Errors.Count);
        Assert.AreEqual(8117, ex.Errors[1].Number);
        Assert.AreEqual($"Operand data type {columnType} is invalid for {aggregate} operator.", ex.Errors[1].Message);
    }

    [TestMethod]
    public void Text_Like_Allowed()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, v text );
            insert t values (1, 'hello world'), (2, 'goodbye world'), (3, 'farewell')
            """);

        Assert.AreEqual(1, simulation.ExecuteScalar("select id from t where v like 'hello%'"));
    }

    [TestMethod]
    public void Text_CastToVarchar_Allowed()
    {
        Assert.AreEqual("hello", new Simulation().ExecuteScalar("""
            create table t ( v text );
            insert t values ('hello');
            select cast(v as varchar(50)) from t
            """));
    }

    [TestMethod]
    public void NText_CastToNVarcharMax_Allowed()
    {
        Assert.AreEqual("hello", new Simulation().ExecuteScalar("""
            create table t ( v ntext );
            insert t values (N'hello');
            select cast(v as nvarchar(max)) from t
            """));
    }

    [TestMethod]
    public void Image_CastToVarbinaryMax_Allowed()
    {
        CollectionAssert.AreEqual(new byte[] { 0xCA, 0xFE }, (byte[])new Simulation().ExecuteScalar("""
            create table t ( v image );
            insert t values (0xCAFE);
            select cast(v as varbinary(max)) from t
            """)!);
    }

    [TestMethod]
    public void MultiRow_VarcharMax_OffRowAndInline_BothRoundTrip()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t ( id int, v varchar(max) );
            insert t values (1, 'small')
            """);

        var big = new string('B', 20_000);
        using var connection = simulation.CreateOpenConnection();
        _ = connection.CreateCommand("insert t values (2, @v)", ("@v", big)).ExecuteNonQuery();

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
