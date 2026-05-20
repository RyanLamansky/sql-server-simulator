using System.Reflection;
using SqlServerSimulator.Storage.Bacpac;

namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
    [TestMethod]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var publicTypes = typeof(Simulation)
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        // Per-type whitelist of member names declared directly on the type.
        // Property / event / operator accessors and compiler-generated members
        // (record <Clone>$, the C# 14 extension's <G>$... nested type) are
        // filtered before comparison, so the entries below read as the
        // human-meaningful API surface.
        Dictionary<Type, HashSet<string>> allowedMembers = new()
        {
            [typeof(Simulation)] = [
                ".ctor",
                nameof(Simulation.CreateDbConnection),
                nameof(Simulation.ImportBacpac),
                nameof(Simulation.AddRemoteSimulation),
            ],
            [typeof(SimulatedDbConnection)] = [
                nameof(SimulatedDbConnection.InfoMessage),
                nameof(SimulatedDbConnection.ConnectionString),
                nameof(SimulatedDbConnection.Database),
                nameof(SimulatedDbConnection.DataSource),
                nameof(SimulatedDbConnection.ServerVersion),
                nameof(SimulatedDbConnection.State),
                nameof(SimulatedDbConnection.ChangeDatabase),
                nameof(SimulatedDbConnection.Close),
                nameof(SimulatedDbConnection.Open),
            ],
            [typeof(SimulatedError)] = [
                nameof(SimulatedError.Class),
                nameof(SimulatedError.LineNumber),
                nameof(SimulatedError.Message),
                nameof(SimulatedError.Number),
                nameof(SimulatedError.Procedure),
                nameof(SimulatedError.Server),
                nameof(SimulatedError.Source),
                nameof(SimulatedError.State),
                nameof(SimulatedError.ToString),
            ],
            [typeof(SimulatedErrorCollection)] = [
                "Item",
                nameof(SimulatedErrorCollection.Count),
                nameof(SimulatedErrorCollection.CopyTo),
                nameof(SimulatedErrorCollection.GetEnumerator),
            ],
            [typeof(SimulatedInfoMessageEventArgs)] = [
                nameof(SimulatedInfoMessageEventArgs.Errors),
                nameof(SimulatedInfoMessageEventArgs.LineNumber),
                nameof(SimulatedInfoMessageEventArgs.Message),
                nameof(SimulatedInfoMessageEventArgs.Source),
            ],
            // C# 14 lowers the extension(DbParameter) block to static
            // accessor methods on the host class plus a compiler-generated
            // <G>$... nested type carrying the cross-assembly metadata. The
            // nested type is filtered as compiler-generated; the accessors
            // surface as regular (non-special-name) methods here.
            [typeof(TableValuedParameterExtensions)] = [
                "get_TypeName",
                "set_TypeName",
            ],
            [typeof(BacpacImportOptions)] = [
                ".ctor",
                nameof(BacpacImportOptions.DatabaseName),
                nameof(BacpacImportOptions.MaxDegreeOfParallelism),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
            [typeof(BacpacImportResult)] = [
                ".ctor",
                nameof(BacpacImportResult.ElementCounts),
                nameof(BacpacImportResult.Skipped),
                nameof(BacpacImportResult.Warnings),
            ],
            [typeof(BacpacSkipped)] = [
                ".ctor",
                "Deconstruct",
                nameof(BacpacSkipped.ElementName),
                nameof(BacpacSkipped.ElementType),
                nameof(BacpacSkipped.Reason),
                nameof(Equals),
                nameof(GetHashCode),
                nameof(ToString),
            ],
        };

        Assert.HasCount(allowedMembers.Count, publicTypes);
        foreach (var type in publicTypes)
            Assert.Contains(type, allowedMembers.Keys);

        foreach (var (type, allowedNames) in allowedMembers)
        {
            var memberNames = type
                .GetMembers()
                .Where(member => member.DeclaringType == type)
                .Where(member => member.Name[0] != '<')
                .Where(member => member is not MethodInfo mi || !mi.IsSpecialName)
                .Select(member => member.Name)
                .ToHashSet();

            Assert.HasCount(allowedNames.Count, memberNames, $"Member count mismatch on {type.FullName}");
            foreach (var name in memberNames)
                Assert.Contains(name, allowedNames, $"Unexpected public member '{name}' on {type.FullName}");
        }
    }
}
