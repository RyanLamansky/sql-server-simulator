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

    /// <summary>
    /// Drives one EF Core <see cref="DbContext"/> subtype's model build +
    /// runtime-initializer to completion on a single thread, so subsequent
    /// parallel test methods that share the type don't race on EF Core's
    /// internal model cache. <c>GetRelationalModel</c> is the call that
    /// raises "model must be finalized and its runtime dependencies must be
    /// initialized" when another thread observes a half-initialized cached
    /// model; invoking it here guarantees the cache entry is fully wired
    /// before parallel use begins. Call from each test class's
    /// <c>[ClassInitialize]</c> when the class declares its own context type.
    /// </summary>
    internal static void WarmModel(Func<DbContext> factory)
    {
        using var context = factory();
        _ = context.Model.GetRelationalModel();
    }
}
