using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using SqlServerSimulator.Storage;

[module: SuppressMessage("Microsoft.Design", "CA1065:DoNotRaiseExceptionsInUnexpectedLocations", Scope = "member", Target = "~P:SqlServerSimulator.SimulatedDbParameter.DbType", Justification = "Mirrors SqlParameter inference: a CLR Value with no DbType mapping is surfaced as ArgumentException at property read, not later at command execution.")]

namespace SqlServerSimulator;

/// <summary>
/// Provider-agnostic <see cref="DbParameter"/> for the simulator's command
/// pipeline. Mirrors enough of <c>Microsoft.Data.SqlClient.SqlParameter</c>'s
/// shape (<see cref="TypeName"/>, defaulted <see cref="SourceColumn"/>) that
/// consumers can downcast a base-typed <see cref="DbParameter"/> the same way
/// they would against a <c>SqlConnection</c>'s parameters.
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
public sealed class SimulatedDbParameter : DbParameter
{
    private DbType? dbType;

    /// <summary>
    /// The typed engine value written back to an output-direction parameter
    /// at end of batch, alongside the CLR conversion stored in
    /// <see cref="Value"/>. The TDS listener's RETURNVALUE writer reads it
    /// for <c>sql_variant</c> / CLR-UDT parameters, whose wire form needs
    /// the inner type identity (variant base type, UDT kind) that the CLR
    /// object no longer carries.
    /// </summary>
    internal SqlValue? OutputSqlValue;

    // A decimal value the linked-server provider sends as numeric, which the
    // remote parameter reports (EXEC ... AT).
    internal bool SpelledNumeric;

    /// <inheritdoc/>
    /// <remarks>
    /// Unset, it is inferred from <see cref="Value"/> as SqlClient infers a
    /// parameter's type: a <see cref="DateTime"/> is
    /// <see cref="DbType.DateTime"/> — <c>datetime</c>, not
    /// <c>datetime2</c> — a <see cref="DBNull"/>, a <see cref="string"/>, a
    /// <see cref="char"/> or a character array is <see cref="DbType.String"/>,
    /// and an <see cref="sbyte"/> or an unsigned integer has no type at all,
    /// raising <see cref="ArgumentException"/>.
    /// </remarks>
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
                    long => DbType.Int64,
                    short => DbType.Int16,
                    byte => DbType.Byte,
                    bool => DbType.Boolean,
                    string or char or char[] or DBNull => DbType.String,
                    byte[] => DbType.Binary,
                    DateOnly => DbType.Date,
                    DateTime => DbType.DateTime,
                    DateTimeOffset => DbType.DateTimeOffset,
                    TimeOnly or TimeSpan => DbType.Time,
                    Guid => DbType.Guid,
                    decimal => DbType.Decimal,
                    double => DbType.Double,
                    float => DbType.Single,
                    null => DbType.String,
                    sbyte or ushort or uint or ulong => throw new ArgumentException($"The parameter data type of {this.Value.GetType().Name} is invalid."),
                    _ => throw new ArgumentException($"No mapping exists from object type {this.Value.GetType().FullName} to a known managed provider native type."),
                };
        }
        set => this.dbType = value;
    }

    /// <inheritdoc/>
    public override ParameterDirection Direction { get; set; }

    /// <inheritdoc/>
    public override bool IsNullable { get; set; }

    /// <inheritdoc/>
    /// <remarks>
    /// Mirrors ADO.NET: an unset or null-assigned name reads back as the empty
    /// string, never null. Native DB-Library RPC callers (pymssql / FreeTDS)
    /// send unnamed positional procedure parameters, which surface here as the
    /// empty name and bind by position.
    /// </remarks>
    [AllowNull]
    public override string ParameterName
    {
        get => field ?? string.Empty;
        set => field = value ?? string.Empty;
    }

    /// <summary>
    /// EF Core's SQL Server type mappings set this on string parameters with
    /// the destination column's declared max length. The simulator stores it
    /// for round-trip but does not enforce truncation here — column-level
    /// length enforcement happens at INSERT/UPDATE time against the schema's
    /// authoritative <c>HeapColumn.MaxLength</c>.
    /// </summary>
    public override int Size { get; set; }

    /// <inheritdoc/>
    [AllowNull]
    public override string SourceColumn { get; set; } = "";

    /// <inheritdoc/>
    public override bool SourceColumnNullMapping { get; set; }

    /// <inheritdoc/>
    public override object? Value { get; set; }

    /// <summary>
    /// User-defined-table-type name (<c>schema.name</c>) for a table-valued
    /// parameter. Defaults to the empty string, matching
    /// <c>SqlParameter.TypeName</c>. The simulator's command-execution path
    /// reads this alongside <see cref="DbParameter.Value"/>: when both a
    /// non-empty <c>TypeName</c> and a <see cref="DataTable"/> /
    /// <see cref="IDataReader"/>-typed <see cref="DbParameter.Value"/>
    /// are present, the parameter binds as a TVP rather than a scalar.
    /// </summary>
    [AllowNull]
    public string TypeName { get; set; } = "";

    /// <inheritdoc/>
    public override void ResetDbType() => this.dbType = null;
}
