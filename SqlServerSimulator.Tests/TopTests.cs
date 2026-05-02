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
}
