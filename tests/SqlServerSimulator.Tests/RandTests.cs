using static Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace SqlServerSimulator;

/// <summary>
/// <c>RAND</c> reproduces real's sequence: a seed restarts it, and an unseeded
/// call continues from wherever the session left it. Every value here was
/// compared against SQL Server 2025.
/// </summary>
[TestClass]
public sealed class RandTests
{
    [TestMethod]
    [DataRow(1, 0.7135919932129235)]
    [DataRow(-1, 0.7135919932129235)]
    [DataRow(0, 0.9435973904241444)]
    [DataRow(12345, 0.9435973904241444)]
    [DataRow(7, 0.7137037910404728)]
    public void Seeded_MatchesReal(int seed, double expected)
        => AreEqual(expected, new Simulation().ExecuteScalar($"select rand({seed})"));

    [TestMethod]
    public void Unseeded_ContinuesTheSessionsSequence()
    {
        using var connection = new Simulation().CreateOpenConnection();
        _ = connection.CreateCommand("select rand(7)").ExecuteScalar();
        AreEqual(0.9457197353968156, connection.CreateCommand("select rand()").ExecuteScalar());
        AreEqual(0.7057602239353887, connection.CreateCommand("select rand()").ExecuteScalar());
    }
}
