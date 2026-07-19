using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace SqlServerSimulator.EFCore;

/// <summary>
/// Registers <see cref="SqlServerSimulatorTypeMappingSourcePlugin"/> with
/// EF Core's relational type-mapping infrastructure when
/// <c>UseSqlServerSimulator</c> is on the options builder. Layers on top of
/// the standard <c>UseSqlServer</c> extension — does not replace the
/// SqlServer provider, only its broken type-mapping cases.
/// </summary>
internal sealed class SqlServerSimulatorOptionsExtension : IDbContextOptionsExtension
{
    public DbContextOptionsExtensionInfo Info => new ExtensionInfo(this);

    public void ApplyServices(IServiceCollection services) =>
        services.AddSingleton<IRelationalTypeMappingSourcePlugin, SqlServerSimulatorTypeMappingSourcePlugin>();

    public void Validate(IDbContextOptions options)
    {
    }

    private sealed class ExtensionInfo(SqlServerSimulatorOptionsExtension extension) : DbContextOptionsExtensionInfo(extension)
    {
        public override bool IsDatabaseProvider => false;

        public override string LogFragment => "using SqlServerSimulator type-mapping plugin ";

        public override int GetServiceProviderHashCode() => 0;

        public override bool ShouldUseSameServiceProvider(DbContextOptionsExtensionInfo other) => other is ExtensionInfo;

        public override void PopulateDebugInfo(IDictionary<string, string> debugInfo) =>
            debugInfo["SqlServerSimulator:TypeMappingPlugin"] = "1";
    }
}
