using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

internal static class TestDbContextExtensions
{
    /// <summary>
    /// Adds the entities to the context, saves, and returns the context for fluent chaining.
    /// Compresses the dominant test-setup shape (<c>context.Add</c>/<c>AddRange</c> + <c>SaveChanges</c>)
    /// into one call so a typical setup reads <c>using var context = new TestDbContext(...).WithSaved(...);</c>.
    /// </summary>
    public static T WithSaved<T>(this T context, params object[] entities) where T : DbContext
    {
        context.AddRange(entities);
        _ = context.SaveChanges();
        return context;
    }
}
