using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

[TestClass]
public static class AssemblyHooks
{
    /// <summary>
    /// Triggers JIT compilation of the most common EF Core path among all tests,
    /// improving the accuracy of their timings. Also functions as a sanity check
    /// against the simulator being completely broken.
    /// </summary>
    [AssemblyInitialize]
    public static async Task HotPath(TestContext context)
    {
        if (System.Diagnostics.Debugger.IsAttached)
            return;

        using var dbContext = new TestDbContext(1, 2);
        _ = await dbContext.Rows.Select(x => x.Id).FirstOrDefaultAsync(context.CancellationToken);
    }
}
