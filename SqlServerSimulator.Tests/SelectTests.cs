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

    [TestMethod]
    [DataRow("SELECT @@VERSION")]
    [DataRow("select @@version")]
    [DataRow("Select @@Version")]
    public void SelectVersion(string commandText) => Assert.AreEqual("SQL Server Simulator", new Simulation().ExecuteScalar(commandText));

    [TestMethod]
    [DataRow("select @p0", "p0", 5)]
    [DataRow("select @p0", "@p0", 6)]
    public void SelectParameterValue(string commandText, string name, object value)
    {
        var result = new Simulation()
            .CreateOpenConnection()
            .CreateCommand(commandText, (name, value))
            .ExecuteScalar();

        Assert.AreEqual(value, result);
    }

    [TestMethod]
    public void Select1Plus1()
    {
        var result = new Simulation().ExecuteScalar("select 1 + 1");

        Assert.AreEqual(2, result);
    }

    [TestMethod]
    [DataRow("select 1 as c", "c", 1)]
    [DataRow("select 1 + 1 as c", "c", 2)]
    public void Expression(string commandText, string name, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);

        Assert.IsTrue(reader.Read());
        Assert.AreEqual(name, reader.GetName(0));
        Assert.AreEqual(value, reader[0]);
    }

    [TestMethod]
    [DataRow("select 1 from systypes", 34, 1, 1)]
    [DataRow("select 1 from systypes as s", 34, 1, 1)]
    [DataRow("select name from systypes", 34, 34, "image")]
    public void ExpressionFromTable(string commandText, int minimumRows, int uniqueRows, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);

        var results = reader
            .EnumerateRecords()
            .Take(minimumRows) // There might be more someday, but there won't be less.
            .Select(reader => reader[0])
            .ToArray();

        Assert.HasCount(minimumRows, results);
        Assert.HasCount(uniqueRows, results.ToHashSet());
        Assert.AreEqual(value, results[0]);
    }

    [TestMethod]
    [DataRow("select name as c from systypes", 34, 34, "c", "image")]
    [DataRow("select name as c from systypes as s", 34, 34, "c", "image")]
    [DataRow("select systypes.name from systypes", 34, 34, "name", "image")]
    [DataRow("select s.name from systypes as s", 34, 34, "name", "image")]
    public void NamedExpressionFromTable(string commandText, int minimumRows, int uniqueRows, string name, object value)
    {
        using var reader = new Simulation().ExecuteReader(commandText);

        var results = reader
            .EnumerateRecords()
            .Take(minimumRows) // There might be more someday, but there won't be less.
            .Select(reader =>
            {
                Assert.AreEqual(name, reader.GetName(0));
                return reader[0];
            })
            .ToArray();

        Assert.HasCount(minimumRows, results);
        Assert.HasCount(uniqueRows, results.ToHashSet());
        Assert.AreEqual(value, results[0]);
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

    [TestMethod]
    public void SelectFromDerivedTable() =>
        Assert.AreEqual(1, new Simulation().ExecuteScalar("select x from ( select 1 as x ) as x"));
}
