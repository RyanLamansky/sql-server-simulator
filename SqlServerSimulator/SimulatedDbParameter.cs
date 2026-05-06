using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

/// <summary>
/// Provider-agnostic <see cref="DbParameter"/> for the simulator's command
/// pipeline.
/// </summary>
/// <remarks>
/// EF Core's SqlServer provider downcasts <see cref="DbParameter"/> to
/// <c>SqlParameter</c> in type mappings that set a non-default
/// <c>SqlDbType</c>. The simulator inherits abstract <see cref="DbParameter"/>
/// (since <c>SqlParameter</c> is sealed) and stays free of
/// <c>Microsoft.Data.SqlClient</c>, so those casts throw at <c>SaveChanges</c>.
/// Raw <c>DbCommand</c> + <see cref="DbType"/> usage is unaffected, as are
/// the EF Core mappings whose default <c>SqlDbType</c> matches the column's
/// type (e.g. <see cref="DateTime"/> → <c>datetime2(N)</c>,
/// <see cref="DateTimeOffset"/> → <c>datetimeoffset(N)</c>). The seven
/// pairs that historically broke — <see cref="DateTime"/> → <c>date</c>,
/// <see cref="DateTime"/> → <c>smalldatetime</c>, <see cref="DateOnly"/>
/// → <c>date</c>, <see cref="TimeOnly"/> / <see cref="TimeSpan"/> →
/// <c>time(N)</c>, and <see cref="decimal"/> → <c>money</c> /
/// <c>smallmoney</c> — are unblocked by the
/// <c>SqlServerSimulator.EFCore</c> adapter, which registers a type-mapping
/// plugin that substitutes provider-agnostic mappings on the affected
/// pairs. Without the adapter (plain <c>UseSqlServer</c>) those casts
/// still throw.
/// </remarks>
sealed class SimulatedDbParameter : DbParameter
{
    private DbType? dbType;

    public override DbType DbType
    {
        get
        {
            var dbType = this.dbType;
            return dbType is not null
                ? dbType.Value
                : this.Value switch
                {
                    int => DbType.Int32,
                    string => DbType.String,
                    DateOnly => DbType.Date,
                    DateTime => DbType.DateTime2,
                    DateTimeOffset => DbType.DateTimeOffset,
                    TimeOnly or TimeSpan => DbType.Time,
                    Guid => DbType.Guid,
                    decimal => DbType.Decimal,
                    double => DbType.Double,
                    float => DbType.Single,
                    null => DbType.String,
                    _ => throw new ArgumentException($"No mapping exists from object type {this.Value.GetType().FullName} to a known managed provider native type."),
                };
        }
        set => this.dbType = value;
    }

    public override ParameterDirection Direction { get; set; }

    public override bool IsNullable { get; set; }

    [AllowNull]
    public override string ParameterName { get; set; }

    /// <summary>
    /// EF Core's SQL Server type mappings set this on string parameters with
    /// the destination column's declared max length. The simulator stores it
    /// for round-trip but does not enforce truncation here — column-level
    /// length enforcement happens at INSERT/UPDATE time against the schema's
    /// authoritative <c>HeapColumn.MaxLength</c>.
    /// </summary>
    public override int Size { get; set; }

    [AllowNull]
    public override string SourceColumn { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public override bool SourceColumnNullMapping { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    public override object? Value { get; set; }

    public override void ResetDbType() => this.dbType = null;
}
