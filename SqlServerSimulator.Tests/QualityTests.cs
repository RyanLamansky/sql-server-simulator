using SqlServerSimulator.Storage.Bacpac;

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

        HashSet<Type> allowedTypes = [
            simulation,
            typeof(TableValuedParameterExtensions),
            typeof(BacpacImportOptions),
            typeof(BacpacImportResult),
            typeof(BacpacSkipped),
        ];
        Assert.HasCount(allowedTypes.Count, types);
        foreach (var type in types)
            Assert.Contains(type, allowedTypes);

        var memberNames = simulation
            .GetMembers()
            .Where(member => member.DeclaringType == simulation)
            .Select(member => member.Name)
            .ToHashSet();

        HashSet<string> allowedMemberNames = [
            ".ctor",
            nameof(Simulation.CreateDbConnection),
            nameof(Simulation.ImportBacpac),
        ];

        Assert.HasCount(allowedMemberNames.Count, memberNames);

        foreach (var name in memberNames)
            Assert.Contains(name, allowedMemberNames);
    }
}
