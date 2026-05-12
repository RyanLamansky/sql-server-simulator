namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
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

        HashSet<Type> allowedTypes = [simulation, typeof(TableValuedParameterExtensions)];
        Assert.HasCount(allowedTypes.Count, types);
        foreach (var type in types)
            Assert.Contains(type, allowedTypes);

        var members = simulation
            .GetMembers()
            .Where(member => member.DeclaringType == simulation)
            .ToArray();

        HashSet<string> allowedMemberNames = [
            ".ctor",
            nameof(Simulation.CreateDbConnection),
        ];

        Assert.HasCount(allowedMemberNames.Count, members);

        foreach (var member in members)
            Assert.Contains(member.Name, allowedMemberNames);
    }
}
