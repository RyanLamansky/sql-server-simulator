using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

/// <summary>
/// <see cref="DbCommand"/> for the simulator's command pipeline. Adds
/// strongly-typed return-type shadows (<see cref="CreateParameter"/>,
/// <see cref="Parameters"/>, <see cref="Connection"/>,
/// <see cref="Transaction"/>, <see cref="ExecuteReader()"/>) so consumers
/// who downcast a base-typed <see cref="DbCommand"/> can stay in
/// <c>Simulated*</c> shapes — same pattern <c>SqlCommand</c> follows
/// against <c>DbCommand</c>. Instances are created via
/// <see cref="SimulatedDbConnection.CreateCommand"/>.
/// </summary>
public sealed class SimulatedDbCommand : DbCommand
{
    internal readonly Simulation simulation;

    internal SimulatedDbCommand(Simulation simulation, SimulatedDbConnection connection)
    {
        this.simulation = simulation;
        this.Connection = connection;
    }

    /// <inheritdoc/>
    [AllowNull]
    public override string CommandText
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <inheritdoc/>
    public override int CommandTimeout
    {
        get;
        set => field = value >= 0 ?
            value :
            throw new ArgumentException($"Invalid {nameof(CommandTimeout)} value {value}; the value must be >= 0.", nameof(CommandTimeout));
        // ArgumentOutOfRangeException would be more appropriate but the official SQL Client uses ArgumentException, so this is more consistent.
    } = 30;

    /// <inheritdoc/>
    public override CommandType CommandType
    {
        get;
        set => field = value is CommandType.Text or CommandType.StoredProcedure
            ? value
            : Enum.IsDefined(value)
                ? throw new NotSupportedException()
                : throw new ArgumentOutOfRangeException(nameof(CommandType), value, null);
    } = CommandType.Text;

    /// <inheritdoc/>
    public override bool DesignTimeVisible { get; set; } = true;

    /// <inheritdoc/>
    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

    /// <inheritdoc/>
    protected override DbConnection? DbConnection
    {
        get;
        set
        {
            if (field is not null) // Set by the constructor.
                throw new NotSupportedException("Simulated DbCommands cannot switch to different connections.");
            field = value;
        }
    }

    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection { get; } = new SimulatedDbParameterCollection();

    /// <inheritdoc/>
    protected override DbTransaction? DbTransaction
    {
        get;
        set
        {
            if (value == null)
            {
                field = null;
                return;
            }

            if (value is not SimulatedDbTransaction transaction)
                throw new NotSupportedException("Simulated DbCommands must use simulation-generated transactions.");

            if (transaction.simulation != this.simulation)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different simulations.");

            if (transaction.Connection != this.Connection)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different connections.");

            field = transaction;
        }
    }

    /// <inheritdoc/>
    public override void Cancel() => throw new NotImplementedException();

    /// <inheritdoc/>
    public override int ExecuteNonQuery() => simulation
        .CreateResultSetsForCommand(this)
        .OfType<SimulatedNonQuery>()
        .Where(result => result.RecordsAffected != -1)
        .Select(result => result.RecordsAffected)
        .DefaultIfEmpty(-1)
        .Sum();

    /// <inheritdoc/>
    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader();
        return !reader.Read() ? null : reader[0];
    }

    /// <inheritdoc/>
    public override void Prepare() => throw new NotImplementedException();

    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter() => new SimulatedDbParameter();

    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior = default)
        => new SimulatedDbDataReader(this.simulation.CreateResultSetsForCommand(this).OfType<SimulatedQueryResult>());

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.CreateParameter"/>.</summary>
    public new SimulatedDbParameter CreateParameter() => (SimulatedDbParameter)base.CreateParameter();

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.Parameters"/>.</summary>
    public new SimulatedDbParameterCollection Parameters => (SimulatedDbParameterCollection)base.Parameters;

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.Connection"/>.</summary>
    public new SimulatedDbConnection? Connection
    {
        get => (SimulatedDbConnection?)base.Connection;
        set => base.Connection = value;
    }

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.Transaction"/>.</summary>
    public new SimulatedDbTransaction? Transaction
    {
        get => (SimulatedDbTransaction?)base.Transaction;
        set => base.Transaction = value;
    }

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.ExecuteReader()"/>.</summary>
    public new SimulatedDbDataReader ExecuteReader() => (SimulatedDbDataReader)base.ExecuteReader();

    /// <summary>Strongly-typed shadow over <see cref="DbCommand.ExecuteReader(CommandBehavior)"/>.</summary>
    public new SimulatedDbDataReader ExecuteReader(CommandBehavior behavior) => (SimulatedDbDataReader)base.ExecuteReader(behavior);
}
