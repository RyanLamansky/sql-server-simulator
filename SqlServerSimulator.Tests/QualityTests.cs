using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SqlServerSimulator;

[TestClass]
public class QualityTests
{
    [TestMethod("Public API Whitelist")]
    [Description("Prevents unintentional expansion of the public API.")]
    public void PublicApiWhitelist()
    {
        var types = typeof(Simulation)
            .Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .ToArray();

        Assert.AreEqual(1, types.Length);
    }
}
