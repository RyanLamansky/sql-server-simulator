using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

sealed class SimulatedDbCommand : DbCommand
{
    internal readonly Simulation simulation;

    public SimulatedDbCommand(Simulation simulation, SimulatedDbConnection connection)
    {
        this.simulation = simulation;
        this.Connection = connection;
    }

    [AllowNull]
    public override string CommandText
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    public override int CommandTimeout
    {
        get;
        set => field = value >= 0 ?
            value :
            throw new ArgumentException($"Invalid {nameof(CommandTimeout)} value {value}; the value must be >= 0.", nameof(CommandTimeout));
        // ArgumentOutOfRangeException would be more appropriate but the official SQL Client uses ArgumentException, so this is more consistent.
    } = 30;

    public override CommandType CommandType
    {
        get;
        set => throw (Enum.IsDefined(value) ? new NotSupportedException() : new ArgumentOutOfRangeException(nameof(CommandType), value, null));
    } = CommandType.Text;

    public override bool DesignTimeVisible { get; set; } = true;

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

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

    protected override DbParameterCollection DbParameterCollection { get; } = new SimulatedDbParameterCollection();

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

            if (transaction.connection != this.Connection)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different connections.");

            field = transaction;
        }
    }

    public override void Cancel() => throw new NotImplementedException();

    public override int ExecuteNonQuery() => simulation
        .CreateResultSetsForCommand(this)
        .OfType<SimulatedNonQuery>()
        .Where(result => result.RecordsAffected != -1)
        .Select(result => result.RecordsAffected)
        .DefaultIfEmpty(-1)
        .Sum();

    public override object? ExecuteScalar()
    {
        using var reader = ExecuteDbDataReader();
        return !reader.Read() ? null : reader[0];
    }

    public override void Prepare() => throw new NotImplementedException();

    protected override DbParameter CreateDbParameter() => new SimulatedDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior = default)
        => new SimulatedDbDataReader(this.simulation, this.simulation.CreateResultSetsForCommand(this).OfType<SimulatedResultSet>());
}
