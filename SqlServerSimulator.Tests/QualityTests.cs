namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
    [AssemblyInitialize]
    public static void HotPath(TestContext context)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        // Triggers JIT compilation of the most common path among all tests, improving the accuracy of their timings.
        // Also functions as a sanity check against the simulator being completely broken.
        Assert.AreEqual(1, new Simulation().ExecuteScalar<int>("select 1"));
    }

    [TestMethod]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var simulation = typeof(Simulation);

        var types = simulation
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        Assert.HasCount(1, types);
        Assert.AreEqual(simulation, types[0]);

        var members = simulation
            .GetMembers()
            .Where(member => member.DeclaringType == simulation)
            .ToArray();

        Assert.HasCount(2, members);

        HashSet<string> allowedMemberNames = [
            ".ctor",
            nameof(Simulation.CreateDbConnection),
        ];

        foreach (var member in members)
            Assert.Contains(member.Name, allowedMemberNames);
    }
}
