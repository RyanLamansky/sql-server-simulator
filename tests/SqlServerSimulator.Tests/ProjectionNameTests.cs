using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>The names a select list gives the columns it doesn't alias.</summary>
[TestClass]
public sealed class ProjectionNameTests
{
    /// <summary>A parenthesized column keeps its name; any other parenthesized expression stays unnamed (probed 2026-09-27).</summary>
    [TestMethod]
    public void AParenthesizedColumn_KeepsItsName()
    {
        var sim = new Simulation();
        _ = sim.ExecuteNonQuery("create table t (e int); insert t values (1)");
        using var connection = sim.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "select (e), ((t.e)), (e) + 1, (1) from t";
        using var reader = command.ExecuteReader();
        AreEqual("e|e||", string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
    }
}
