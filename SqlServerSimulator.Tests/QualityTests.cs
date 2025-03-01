using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
    [TestMethod("Public API Whitelist")]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var simulation = typeof(Simulation);

        var types = simulation
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        Assert.AreEqual(1, types.Length);
        Assert.AreEqual(simulation, types[0]);

        var members = simulation
            .GetMembers()
            .Where(member => member.DeclaringType == simulation)
            .ToArray();

        Assert.AreEqual(2, members.Length);

        HashSet<string> allowedMemberNames = [
            ".ctor",
            nameof(Simulation.CreateDbConnection),
        ];

        foreach (var member in members)
            Assert.Contains(member.Name, allowedMemberNames);
    }
}
