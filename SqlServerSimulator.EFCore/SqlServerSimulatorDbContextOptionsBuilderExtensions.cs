using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace SqlServerSimulator.EFCore;

/// <summary>
/// Entry point for using <see cref="Simulation"/> from EF Core. Wraps
/// <see cref="SqlServerDbContextOptionsExtensions.UseSqlServer(DbContextOptionsBuilder, DbConnection, Action{Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder}?)"/>
/// and registers the simulator's type-mapping plugin so the SqlServer
/// provider's <c>SqlParameter</c>-downcast pairs (DateOnly, TimeOnly,
/// TimeSpan, DateTime → date / smalldatetime, decimal → money /
/// smallmoney) succeed against a <see cref="Simulation"/>-backed
/// connection.
/// </summary>
public static class SqlServerSimulatorDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Configures the context to use the SqlServer provider against a
    /// simulator-backed <see cref="DbConnection"/> and registers the
    /// simulator's type-mapping plugin. Drop-in replacement for
    /// <c>UseSqlServer(simulation.CreateDbConnection())</c>.
    /// </summary>
    /// <param name="optionsBuilder">The options builder to configure.</param>
    /// <param name="connection">A connection from <see cref="Simulation.CreateDbConnection"/>.</param>
    /// <returns>The same builder so calls can chain.</returns>
    public static DbContextOptionsBuilder UseSqlServerSimulator(this DbContextOptionsBuilder optionsBuilder, DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);
        _ = optionsBuilder.UseSqlServer(connection);

        var extension = optionsBuilder.Options.FindExtension<SqlServerSimulatorOptionsExtension>() ?? new SqlServerSimulatorOptionsExtension();
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }
}
