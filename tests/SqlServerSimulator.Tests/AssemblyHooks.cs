namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// Triggers JIT compilation of the most common path among all tests, improving
    /// the accuracy of their timings. Also functions as a sanity check against the
    /// simulator being completely broken.
    /// Pins <see cref="HostileCulture"/> first.
    /// </summary>
    [AssemblyInitialize]
    public static void HotPath(TestContext _)
    {
        HostileCulture.Pin();
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        Assert.AreEqual(1, new Simulation().ExecuteScalar<int>("select 1"));

        // The SQLCLR fixture compiles once through Roslyn, which every CLR
        // routine test would otherwise queue behind.
        GC.KeepAlive(ClrFrameworkFixture.CreateAssembly);
    }
}
