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
    [DataRow("@p0", 1, new[] { 1 })]
    [DataRow("(@p0)", 1, new[] { 1 })]
    [DataRow("@p0", 0, new int[] { })]
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
        var ex = Throws<System.Data.Common.DbException>(() => sim.ExecuteScalar("select top (v) v from t"));
        AreEqual("4115", ex.Data["HelpLink.EvtID"]);
    }

    [TestMethod]
    public void Top_NonIntegerExpression_RaisesMsg1060()
    {
        // TOP requires an integer; a string-typed expression triggers Msg 1060.
        var ex = Throws<System.Data.Common.DbException>(() => new Simulation().ExecuteScalar("select top ('abc') 1"));
        AreEqual("1060", ex.Data["HelpLink.EvtID"]);
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
        var ex = Throws<System.Data.Common.DbException>(() => sim.ExecuteScalar(commandText));
        AreEqual("102", ex.Data["HelpLink.EvtID"]);
        AreEqual($"Incorrect syntax near '{operatorCharacter}'.", ex.Message);
    }
}
