namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>Pins <see cref="HostileCulture"/> before any test runs.</summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext _) => HostileCulture.Pin();
}
