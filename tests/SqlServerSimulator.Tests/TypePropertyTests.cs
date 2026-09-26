namespace SqlServerSimulator;

/// <summary>
/// <c>TYPEPROPERTY</c> over built-in, alias and table types, qualified or not.
/// Every expectation probed 2026-09-26 against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class TypePropertyTests
{
    private static readonly Simulation Types = Build();

    private static Simulation Build()
    {
        var sim = new Simulation();
        sim.ExecuteBatches("create type al from varchar(12) not null", "create type an from decimal(9, 2)", "create type tt as table (a int)",
            "create schema s", "create type s.al2 from nvarchar(5)");
        return sim;
    }

    [TestMethod]
    [DataRow("int", "OwnerId", "4")]
    [DataRow("sys.int", "Precision", "10")]
    [DataRow("geography", "Precision", "-1")]
    [DataRow("geometry", "AllowsNull", "1")]
    [DataRow("al", "Precision", "12")]
    [DataRow("dbo.al", "AllowsNull", "0")]
    [DataRow("al", "UsesAnsiTrim", "1")]
    [DataRow("al", "OwnerId", "1")]
    [DataRow("an", "Precision", "9")]
    [DataRow("an", "Scale", "2")]
    [DataRow("s.al2", "Precision", "5")]
    [DataRow("s.al2", "UsesAnsiTrim", "NULL")]
    [DataRow("al2", "Precision", "NULL")]
    [DataRow("tt", "Precision", "0")]
    [DataRow("tt", "AllowsNull", "0")]
    [DataRow("tt", "Scale", "NULL")]
    [DataRow("sys.al", "Precision", "NULL")]
    public void TypeProperty_AnswersForEveryKindOfType(string type, string property, string expected)
        => Assert.AreEqual(expected, Types.ExecuteScalar($"select isnull(cast(typeproperty('{type}', '{property}') as varchar), 'NULL')"));
}
