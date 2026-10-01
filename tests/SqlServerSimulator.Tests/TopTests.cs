using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

[TestClass]
public class TopTests
{
    [TestMethod]
    [DataRow("1", new[] { 1 })]
    [DataRow("0", new int[] { })]
    [DataRow("(1)", new[] { 1 })]
    [DataRow("(0)", new int[] { })]
    public void TopConstantUnsorted(string topExpression, int[] expectedValues)
    {
        CollectionAssert.AreEquivalent(expectedValues, [.. new Simulation()
            .ExecuteReader($"select top {topExpression} 1")
            .EnumerateRecords()
            .Select(reader => (int)reader[0])], EqualityComparer<int>.Default);
    }

    [TestMethod]
    [DataRow("(@p0)", 1, new[] { 1 })]
    [DataRow("(@p0)", 0, new int[] { })]
    public void TopParameterizedUnsorted(string parameterExpression, int parameterValue, int[] expectedValues)
    {
        CollectionAssert.AreEquivalent(expectedValues, [.. new Simulation()
            .CreateOpenConnection()
            .CreateCommand($"select top {parameterExpression} 1", ("p0", parameterValue))
            .ExecuteReader()
            .EnumerateRecords()
            .Select(reader => (int)reader[0])], EqualityComparer<int>.Default);
    }

    /// <summary>
    /// The legacy unparenthesized count takes a constant only: a variable or
    /// parameter there is Msg 102 near it (probed 2026-10-01 against SQL Server
    /// 2025, a declared variable and an <c>sp_executesql</c> parameter alike).
    /// </summary>
    [TestMethod]
    public void Top_UnparenthesizedVariable_IsSyntaxError()
    {
        _ = new Simulation().AssertSqlError("declare @n int = 2; select top @n 1", 102);
        using var command = new Simulation().CreateOpenConnection().CreateCommand("select top @p0 1", ("p0", 1));
        AreEqual(102, Throws<SimulatedSqlException>(command.ExecuteScalar).Number);
    }

    [TestMethod]
    public void Top_OnTablelessSelect_LargerThanOneStillReturnsOne()
        => AreEqual(1, new Simulation().ExecuteReader("select top 5 1").EnumerateRecords().Count());

    [TestMethod]
    public void Top_OnFromTable_TakesFirstNRows()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 ), ( 4 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select top 2 v from t").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Top_OnFromTable_ZeroReturnsNoRows()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select top 0 v from t").ExecuteReader();
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Top_OnFromTable_LargerThanRowsReturnsAll()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 )").ExecuteNonQuery();

        using var reader = connection.CreateCommand("select top 99 v from t").ExecuteReader();
        IsTrue(reader.Read()); AreEqual(1, reader[0]);
        IsTrue(reader.Read()); AreEqual(2, reader[0]);
        IsFalse(reader.Read());
    }

    [TestMethod]
    public void Top_AppliedAfterWhere()
    {
        using var connection = new Simulation().CreateOpenConnection();

        _ = connection.CreateCommand("create table t ( v int )").ExecuteNonQuery();
        _ = connection.CreateCommand("insert t values ( 1 ), ( 2 ), ( 3 ), ( 4 ), ( 5 )").ExecuteNonQuery();

        using var topReader = connection.CreateCommand("select top 2 v from t where v > 2").ExecuteReader();
        IsTrue(topReader.Read()); AreEqual(3, topReader[0]);
        IsTrue(topReader.Read()); AreEqual(4, topReader[0]);
        IsFalse(topReader.Read());
    }

    [TestMethod]
    public void Top_ColumnReference_RaisesMsg4115()
    {
        // TOP / OFFSET / FETCH cannot reference a column from the same query's FROM.
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int)");
        _ = sim.ExecuteNonQuery("insert t values (1)");
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteScalar("select top (v) v from t"));
        AreEqual(4115, ex.Number);
    }

    [TestMethod]
    public void Top_NonIntegerExpression_RaisesMsg1060()
    {
        // TOP requires an integer; a string-typed expression triggers Msg 1060.
        var ex = Throws<SimulatedSqlException>(() => new Simulation().ExecuteScalar("select top ('abc') 1"));
        AreEqual(1060, ex.Number);
    }

    /// <summary>
    /// The legacy paren-less <c>TOP n</c> takes a bare constant or variable and
    /// no unary prefix at all: real raises Msg 102 naming the operator
    /// (probe-confirmed 2026-08-03). The parenthesized form does take a sign
    /// and validates the resulting value instead.
    /// </summary>
    [TestMethod]
    [DataRow("select top -1 v from t", '-')]
    [DataRow("select top +1 v from t", '+')]
    [DataRow("select top ~1 v from t", '~')]
    [DataRow("select top -1 * from t", '-')]
    public void Top_BareFormWithUnaryPrefix_RaisesMsg102(string commandText, char operatorCharacter)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int)");
        var ex = Throws<SimulatedSqlException>(() => sim.ExecuteScalar(commandText));
        AreEqual(102, ex.Number);
        AreEqual($"Incorrect syntax near '{operatorCharacter}'.", ex.Message);
    }

    /// <summary>
    /// A module body binds without running, so the operand has no value to
    /// read — real settles the question from the operand's declared type
    /// instead, which is what makes WideWorldImporters' <c>Website.SearchFor*</c>
    /// procedures creatable (probe-confirmed: the <c>int</c> parameter creates
    /// and the rest are refused at CREATE).
    /// </summary>
    [TestMethod]
    [DataRow("@n int")]
    [DataRow("@n bigint")]
    public void Top_ParameterOperandInAProcBody_Creates(string parameter)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int)");
        _ = sim.ExecuteNonQuery("insert t values (1), (2), (3)");
        _ = sim.ExecuteNonQuery($"create procedure dbo.p {parameter} as select top(@n) v from t order by v");
        AreEqual(2, sim.ExecuteScalar("declare @c int; create table #r (v int); insert #r exec dbo.p 2; select count(*) from #r"));
    }

    /// <summary>
    /// The type check still fires at CREATE for an operand real refuses —
    /// each of these is Msg 1060 on the CREATE itself, not at EXEC.
    /// </summary>
    [TestMethod]
    [DataRow("@n nvarchar(10)", "select top(@n) v from t")]
    [DataRow("@n decimal(5, 2)", "select top(@n) v from t")]
    [DataRow("@n int", "select top(null) v from t")]
    [DataRow("@n int", "select top(1.5) v from t")]
    public void Top_NonIntegerOperandInAProcBody_Raises1060(string parameter, string body)
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int)");
        sim.AssertSqlError(
            $"create procedure dbo.p {parameter} as {body}",
            1060,
            "The number of rows provided for a TOP or FETCH clauses row count parameter must be an integer.");
    }

    /// <summary><c>OFFSET</c> / <c>FETCH</c> take the same parameter operands.</summary>
    [TestMethod]
    public void OffsetFetch_ParameterOperandsInAProcBody_Create()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (v int)");
        _ = sim.ExecuteNonQuery("insert t values (1), (2), (3)");
        _ = sim.ExecuteNonQuery("create procedure dbo.p @skip int, @take int as select v from t order by v offset @skip rows fetch next @take rows only");
        AreEqual(2, sim.ExecuteScalar("create table #r (v int); insert #r exec dbo.p 1, 1; select v from #r"));
    }

    /// <summary>
    /// A row count is an integer other than <c>bit</c>, or an exact numeric at
    /// scale 0 that real names <c>numeric</c> — a literal, a <c>CAST … AS
    /// numeric</c> or arithmetic over one; the same value typed <c>decimal</c>,
    /// a computation over that and a variable either way are Msg 1060 (probed
    /// 2026-10-01 against SQL Server 2025).
    /// </summary>
    [TestMethod]
    [DataRow("cast(2 as numeric(5, 0))", 2)]
    [DataRow("2.", 2)]
    [DataRow("cast(2 as smallint)", 2)]
    [DataRow("cast(2 as decimal(5, 0))", -1)]
    [DataRow("cast(2 as numeric(5, 0)) + 0", 2)]
    [DataRow("cast(2 as decimal(5, 0)) + 0", -1)]
    [DataRow("cast(1 as bit)", -1)]
    public void Top_OperandType(string operand, int expectedRows)
    {
        const string source = "from (values (1), (2), (3)) v(x)";
        if (expectedRows < 0)
            _ = new Simulation().AssertSqlError($"select top ({operand}) x {source}", 1060);
        else
            AreEqual(expectedRows, new Simulation().ExecuteScalar($"select count(*) from (select top ({operand}) x {source}) z"));
    }

    /// <summary>
    /// The OFFSET's own refusal, Msg 10743, covers a variable of another type
    /// too.
    /// </summary>
    [TestMethod]
    public void Offset_StringVariable_RaisesMsg10743()
        => _ = new Simulation().AssertSqlError("declare @o varchar(3) = '1'; select x from (values (1)) v(x) order by x offset @o rows", 10743);

    /// <summary>
    /// A row count may read an enclosing query's columns, counting per outer
    /// row — in a correlated subquery and an APPLY body alike — where a NULL
    /// is Msg 1014 and a negative Msg 127 as the row reaches it (probed
    /// 2026-10-01 against SQL Server 2025). A column of the query's own source
    /// stays Msg 4115.
    /// </summary>
    [TestMethod]
    public void Top_OuterColumnCount_CountsPerOuterRow()
    {
        const string seed = "create table o (id int, n int); insert o values (1, 1), (2, 2), (3, 0); create table i (id int); insert i values (1), (2), (3); ";
        AreEqual(3, new Simulation().ExecuteScalar(seed + "select count(*) from o cross apply (select top (o.n) id from i order by id) x"));
        AreEqual(2, new Simulation().ExecuteScalar(seed + "select count(*) from o where o.id in (select top (o.n) id from i order by id)"));
        _ = new Simulation().AssertSqlError(seed + "insert o values (4, null); select count(*) from o cross apply (select top (o.n) id from i order by id) x", 1014);
        _ = new Simulation().AssertSqlError(seed + "select count(*) from o cross apply (select top (o.n - 5) id from i order by id) x", 127);
        _ = new Simulation().AssertSqlError(seed + "select top (id) id from i", 4115);
    }
}
