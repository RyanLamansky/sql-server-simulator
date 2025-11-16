using System.Data.Common;

namespace SqlServerSimulator;

[TestClass]
public class SelectTests
{
    [TestMethod]
    public void Select1ViaExecuteScalar()
        => Assert.AreEqual(1, new Simulation().ExecuteScalar("select 1"));

    private static DbDataReader ScalarViaReaderTest(string commandText)
    {
        var reader = new Simulation().ExecuteReader(commandText);
        Assert.IsTrue(reader.Read());
        return reader;
    }

    [TestMethod]
    public void Select1ViaExecuteReaderIndexer()
        => Assert.AreEqual(1, ScalarViaReaderTest("select 1")[0]);

    [TestMethod]
    public void Select1ViaExecuteReaderGetInt32()
        => Assert.AreEqual(1, ScalarViaReaderTest("select 1").GetInt32(0));

    private static void VersionTest(string commandText) =>
        Assert.AreEqual("SQL Server Simulator", new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    public void SelectVersion() => VersionTest("SELECT @@VERSION");

    [TestMethod]
    public void SelectVersion_LowerCase() => VersionTest("select @@version");

    [TestMethod]
    public void SelectVersion_MixedCase() => VersionTest("Select @@Version");

    [TestMethod]
    public void SelectParameterValue()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0", ("@p0", 5))
            .ExecuteScalar();

        Assert.AreEqual(5, result);
    }

    [TestMethod]
    public void SelectParameterValueWithAtInName()
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand("select @p0", ("@p0", 6))
            .ExecuteScalar();

        Assert.AreEqual(6, result);
    }

    [TestMethod]
    public void Select1Plus1()
    {
        var result = new Simulation().ExecuteScalar("select 1 + 1");

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    public void SelectAliasedExpression()
    {
        using var reader = new Simulation().ExecuteReader("select 1 as c");

        Assert.IsTrue(reader.Read());
        Assert.AreEqual("c", reader.GetName(0));
        Assert.AreEqual(1, reader.GetInt32(0));
    }

    [TestMethod]
    public void SelectExpressionFromSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select 1 from systypes");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetInt32(0))
            .ToArray();

        Assert.HasCount(34, results);
        Assert.HasCount(1, results.ToHashSet());
        Assert.AreEqual(1, results[0]);
    }

    [TestMethod]
    public void SelectExpressionFromAliasedSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select 1 from systypes as s");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetInt32(0))
            .ToArray();

        Assert.HasCount(34, results);
        Assert.HasCount(1, results.ToHashSet());
        Assert.AreEqual(1, results[0]);
    }

    [TestMethod]
    public void SelectColumnFromSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select name from systypes");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetString(0))
            .ToHashSet();

        Assert.HasCount(34, results);
        Assert.Contains("int", results);
    }

    [TestMethod]
    public void SelectAliasedColumnFromSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select name as c from systypes");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetName(0))
            .ToHashSet();

        Assert.HasCount(1, results);
        Assert.Contains("c", results);
    }

    [TestMethod]
    public void SelectAliasedColumnFromAliasedSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select name as c from systypes as s");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetName(0))
            .ToHashSet();

        Assert.HasCount(1, results);
        Assert.Contains("c", results);
    }

    [TestMethod]
    public void SelectMultiPartColumnFromSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select systypes.name from systypes");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetName(0))
            .ToHashSet();

        Assert.HasCount(1, results);
        Assert.Contains("name", results);
    }

    [TestMethod]
    public void SelectMultiPartColumnFromAliasedSystemTable()
    {
        using var reader = new Simulation().ExecuteReader("select s.name from systypes as s");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => reader.GetName(0))
            .ToHashSet();

        Assert.HasCount(1, results);
        Assert.Contains("name", results);
    }

    [TestMethod]
    public void Select1Comma2()
    {
        using var reader = new Simulation().ExecuteReader("select 1, 2");

        var results = reader
            .EnumerateRecords()
            .Select(reader => (C1: reader.GetInt32(0), C2: reader.GetInt32(1)))
            .ToArray();

        Assert.HasCount(1, results);
        var (C1, C2) = results[0];
        Assert.AreEqual(1, C1);
        Assert.AreEqual(2, C2);
    }

    [TestMethod]
    public void SelectTwoColumns()
    {
        using var reader = new Simulation().ExecuteReader("select name, length from systypes");

        var results = reader
            .EnumerateRecords()
            .Take(34) // There might be more someday, but there won't be less.
            .Select(reader => (C1: reader.GetString(0), C2: reader.GetInt32(1)))
            .ToArray();

        Assert.HasCount(34, results);
        var (C1, C2) = results[0];
        Assert.AreEqual("image", C1);
        Assert.AreEqual(16, C2);
    }

    [TestMethod]
    [DataRow("select", "select")]
    [DataRow("select ", "select")]
    [DataRow("select ,", ",")]
    public void SelectSyntaxErrorsAreCorrect(string commandText, string nearSyntax) =>
        new Simulation().ValidateSyntaxError(commandText, nearSyntax);
}
