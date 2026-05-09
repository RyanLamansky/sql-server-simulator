using Microsoft.EntityFrameworkCore;

namespace SqlServerSimulator;

/// <summary>
/// EF Core's raw-SQL entry points (<c>FromSqlInterpolated</c> /
/// <c>FromSqlRaw</c>) bypass the LINQ translator and pass user-written SQL
/// straight through to the simulator's parser, while still binding C#
/// interpolated values as typed parameters. This is the realistic path
/// where mixed-type comparisons (string parameter vs int column) actually
/// emerge in app code — direct LINQ over strongly-typed entities never
/// produces that shape.
/// </summary>
[TestClass]
public class EFCoreFromSql
{
    public TestContext TestContext { get; set; } = null!;

    private static TestDbContext SeededContext()
    {
        var context = new TestDbContext(TestDbContext.CreateCustomersSimulation());
        context.Customers.AddRange(
            new Customer { Name = "alpha" },
            new Customer { Name = "beta" },
            new Customer { Name = "gamma" });
        _ = context.SaveChanges();
        return context;
    }

    [TestMethod]
    public void FromSqlInterpolated_StringParameterAgainstIntColumn_PromotesAndMatches()
    {
        // The C# string interpolation hands EF Core a string value, which
        // SqlClient binds as nvarchar — so the server-side comparison is
        // `int_column = nvarchar_param`. Cross-category int↔string Promote
        // closes the loop: the parameter's nvarchar parses through the
        // int column's CAST path, the row matches, and entity materialization
        // proceeds normally.
        using var context = SeededContext();
        var idAsString = "2";
        var customer = context.Customers
            .FromSqlInterpolated($"select * from Customers where Id = {idAsString}")
            .Single();
        Assert.AreEqual(2, customer.Id);
        Assert.AreEqual("beta", customer.Name);
    }
}
