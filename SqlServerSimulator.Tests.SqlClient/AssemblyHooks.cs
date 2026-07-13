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

        using var connection = new Simulation().CreateDbConnection();
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select 1";
        Assert.AreEqual(1, command.ExecuteScalar());
    }
}
