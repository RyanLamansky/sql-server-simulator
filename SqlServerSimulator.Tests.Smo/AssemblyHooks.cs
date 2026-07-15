namespace SqlServerSimulator;

/// <summary>
/// Builds the shared SMO fixture once before the parallel run (a simulation +
/// WWI-shaped schema + login + TDS listener) and tears the listener down after.
/// Warming it here keeps first-touch JIT / TLS-handshake cost out of the test
/// timings, the same rationale as the sibling oracles' assembly-init warm-up.
/// </summary>
[TestClass]
public static class AssemblyHooks
{
    [AssemblyInitialize]
    public static void Initialize(TestContext _) => SmoFixture.Initialize();

    [AssemblyCleanup]
    public static void Cleanup() => SmoFixture.Cleanup();
}
