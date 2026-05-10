namespace SqlServerSimulator;

/// <summary>
/// Behavioral tests for computed columns: <c>col AS expr [PERSISTED [NOT NULL]]</c>
/// grammar in CREATE TABLE, write reservation (Msg 271), computed-of-computed
/// rejection (Msg 1759), and the "computed column constraints require PERSISTED"
/// rule (Msg 8183).
/// </summary>
[TestClass]
public sealed class ComputedColumnTests
{
    [TestMethod]
    public void Computed_NonPersisted_EvaluatesAtRead()
        => Assert.AreEqual(30, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (10, 20);
            select c from t
            """));

    // Bare INSERT without column list populates only writable columns; computed cols excluded like identity.
    [TestMethod]
    public void Computed_NonPersisted_OmittedFromAutoColumnList()
        => Assert.AreEqual(7, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b);
            insert t values (3, 4);
            select c from t
            """));

    [TestMethod]
    public void Computed_NonPersisted_TracksUnderlyingChanges_AcrossRows()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, c as a * 10);
            insert t (a) values (1), (2), (3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select c from t order by a").ExecuteReader();
        var values = new List<int>();
        while (reader.Read())
            values.Add(reader.GetInt32(0));
        CollectionAssert.AreEqual(new[] { 10, 20, 30 }, values);
    }

    [TestMethod]
    public void Computed_Persisted_StoresAndReadsBack()
        => Assert.AreEqual(12, new Simulation().ExecuteScalar("""
            create table t (a int, b int, c as a + b persisted);
            insert t (a, b) values (5, 7);
            select c from t
            """));

    [TestMethod]
    public void Computed_Persisted_NotNull_AcceptsNonNullSource()
        => Assert.AreEqual(3, new Simulation().ExecuteScalar("""
            create table t (a int not null, b int not null, c as a + b persisted not null);
            insert t (a, b) values (1, 2);
            select c from t
            """));

    [TestMethod]
    public void Computed_ConstantExpression()
        => Assert.AreEqual(42, new Simulation().ExecuteScalar("""
            create table t (a int, c as 42);
            insert t (a) values (1);
            select c from t
            """));

    // Real SQL Server resolves all column refs after the entire column list is parsed.
    [TestMethod]
    public void Computed_ForwardReference()
        => Assert.AreEqual(11, new Simulation().ExecuteScalar("""
            create table t (c as b + 1, b int);
            insert t (b) values (10);
            select c from t
            """));

    [TestMethod]
    public void Computed_TypeInference_FromArithmetic()
        => Assert.AreEqual(41000, new Simulation().ExecuteScalar("""
            create table t (a int, b smallint, c as a + b);
            insert t (a, b) values (40000, 1000);
            select c from t
            """));

    [TestMethod]
    public void Computed_OutputClause_SeesNonPersistedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, c as a * 2)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("insert t (a) output inserted.c values (21)").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void Computed_OutputClause_SeesPersistedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("create table t (a int, c as a * 2 persisted)");

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("insert t (a) output inserted.c values (21)").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(42, reader.GetInt32(0));
    }

    [TestMethod]
    public void Computed_InsertIntoColumn_RaisesMsg271()
        => new Simulation().AssertSqlError("""
            create table t (a int, c as a + 1);
            insert t (a, c) values (1, 99)
            """, 271,
            "The column \"c\" cannot be modified because it is either a computed column or is the result of a UNION operator.");

    [TestMethod]
    public void Computed_ReferencingComputed_RaisesMsg1759()
        => new Simulation().AssertSqlError("create table t (a int, c as a + 1, d as c + 1)", 1759,
            "Computed column 'c' in table 't' is not allowed to be used in another computed-column definition.");

    [TestMethod]
    public void Computed_PersistedReferencingNonPersisted_RaisesMsg1759()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1, d as c + 1 persisted)", 1759);

    [TestMethod]
    public void Computed_SelfReference_RaisesMsg1759()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as c + 1)", 1759);

    [TestMethod]
    public void Computed_Identity_RaisesMsg8183()
        => new Simulation().AssertSqlError("create table t (a int, c as a + 1 identity(1, 1))", 8183,
            "Only UNIQUE or PRIMARY KEY constraints can be created on computed columns, while CHECK, FOREIGN KEY, and NOT NULL constraints require that computed columns be persisted.");

    [TestMethod]
    public void Computed_Default_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 default 5)", 8183);

    [TestMethod]
    public void Computed_NotNullWithoutPersisted_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 not null)", 8183);

    [TestMethod]
    public void Computed_ExplicitNullWithoutPersisted_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 null)", 8183);

    // Real SQL Server rejects explicit NULL after PERSISTED; only `PERSISTED` alone or `PERSISTED NOT NULL` accepted.
    [TestMethod]
    public void Computed_PersistedNull_RaisesMsg8183()
        => _ = new Simulation().AssertSqlError("create table t (a int, c as a + 1 persisted null)", 8183);

    [TestMethod]
    public void Computed_AllColumns_ProjectsAlongsideStored()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (2, 3)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a, b, c from t").ExecuteReader();
        Assert.AreEqual(3, reader.FieldCount);
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(2, reader.GetInt32(0));
        Assert.AreEqual(3, reader.GetInt32(1));
        Assert.AreEqual(5, reader.GetInt32(2));
    }

    [TestMethod]
    public void Computed_WhereClauseFiltersOnComputedValue()
    {
        var simulation = new Simulation();
        _ = simulation.ExecuteNonQuery("""
            create table t (a int, b int, c as a + b);
            insert t (a, b) values (1, 2), (5, 5), (10, 10)
            """);

        using var connection = simulation.CreateOpenConnection();
        using var reader = connection.CreateCommand("select a from t where c >= 10 order by a").ExecuteReader();
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(5, reader.GetInt32(0));
        Assert.IsTrue(reader.Read());
        Assert.AreEqual(10, reader.GetInt32(0));
        Assert.IsFalse(reader.Read());
    }

    /// <summary>
    /// Regression: <c>varchar(N) + varchar(M)</c> in a computed column should
    /// resolve to <c>varchar(N+M)</c>. The earlier behavior dropped to
    /// length-unspecified varchar (defaulting to 1 in column context),
    /// which clobbered the persisted value to its first character.
    /// </summary>
    [TestMethod]
    public void Computed_VarcharConcat_PreservesCombinedLength()
        => Assert.AreEqual(
            "abcdefghijklmnopqrstuvwxyz1234",
            new Simulation().ExecuteScalar("""
                create table t (a varchar(10), b varchar(20), c as a + b persisted);
                insert t (a, b) values ('abcdefghij', 'klmnopqrstuvwxyz1234');
                select c from t
                """));
}
