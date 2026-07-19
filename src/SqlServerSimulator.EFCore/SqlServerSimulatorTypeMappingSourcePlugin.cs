using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Storage;

namespace SqlServerSimulator.EFCore;

/// <summary>
/// Returns substitute relational type mappings for the (CLR type, store type)
/// pairs whose default <c>SqlServer</c>-provider mappings downcast
/// <see cref="System.Data.Common.DbParameter"/> to <c>SqlParameter</c>. Each
/// substitute inherits from the provider-agnostic base
/// (<see cref="DateOnlyTypeMapping"/>, <see cref="TimeOnlyTypeMapping"/>, …),
/// which uses the default <c>ConfigureParameter</c> that simply sets
/// <see cref="System.Data.Common.DbParameter.DbType"/> — no
/// <c>Microsoft.Data.SqlClient</c> dependency, no failed cast.
/// </summary>
/// <remarks>
/// Pairs covered:
/// <list type="bullet">
/// <item><see cref="DateOnly"/> → <c>date</c></item>
/// <item><see cref="DateTime"/> → <c>date</c></item>
/// <item><see cref="DateTime"/> → <c>smalldatetime</c></item>
/// <item><see cref="TimeOnly"/> → <c>time</c> / <c>time(N)</c></item>
/// <item><see cref="TimeSpan"/> → <c>time</c> / <c>time(N)</c></item>
/// <item><see cref="decimal"/> → <c>money</c></item>
/// <item><see cref="decimal"/> → <c>smallmoney</c></item>
/// </list>
/// All other mappings flow through unchanged: returning <c>null</c> from
/// <see cref="FindMapping"/> tells EF Core to consult the next plugin or
/// the provider's built-in mappings. The default <see cref="DateTime"/>
/// → <c>datetime2</c> path already works under vanilla
/// <c>UseSqlServer</c>, so it's not intercepted here.
/// </remarks>
[SuppressMessage("Performance", "CA1812", Justification = "Activated by EF Core's DI container via IRelationalTypeMappingSourcePlugin.")]
internal sealed class SqlServerSimulatorTypeMappingSourcePlugin : IRelationalTypeMappingSourcePlugin
{
    public RelationalTypeMapping? FindMapping(in RelationalTypeMappingInfo mappingInfo)
    {
        var clrType = mappingInfo.ClrType;
        var storeBase = mappingInfo.StoreTypeNameBase;
        if (clrType is null || storeBase is null)
            return null;

        // For time(N), prefer the user-declared store name (e.g. "time(7)")
        // so EF emits the correct DECLARE; fall back to the base name when
        // no precision was specified.
        var fullStoreName = mappingInfo.StoreTypeName ?? storeBase;

        // money / smallmoney share DbType.Currency. Precision/scale match
        // SQL Server's documented widths so EF migrations and any literal
        // emitter produce the right shape; the simulator's parameter
        // pipeline routes Currency → its Money type regardless.
        return (clrType, storeBase) switch
        {
            (Type t, "date") when t == typeof(DateOnly) => new DateOnlyTypeMapping(fullStoreName, DbType.Date),
            (Type t, "date") when t == typeof(DateTime) => new DateTimeTypeMapping(fullStoreName, DbType.Date),
            (Type t, "smalldatetime") when t == typeof(DateTime) => new DateTimeTypeMapping(fullStoreName, DbType.DateTime),
            (Type t, "time") when t == typeof(TimeOnly) => new TimeOnlyTypeMapping(fullStoreName, DbType.Time),
            (Type t, "time") when t == typeof(TimeSpan) => new TimeSpanTypeMapping(fullStoreName, DbType.Time),
            (Type t, "money") when t == typeof(decimal) => new DecimalTypeMapping(fullStoreName, DbType.Currency, precision: 19, scale: 4),
            (Type t, "smallmoney") when t == typeof(decimal) => new DecimalTypeMapping(fullStoreName, DbType.Currency, precision: 10, scale: 4),
            _ => null,
        };
    }
}
