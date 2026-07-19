namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// Triggers JIT compilation of the most common path among all tests, improving
    /// the accuracy of their timings. Also functions as a sanity check against the
    /// simulator being completely broken.
    /// </summary>
    [AssemblyInitialize]
    public static void HotPath(TestContext _)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        Assert.AreEqual(1, new Simulation().ExecuteScalar<int>("select 1"));
    }
}
