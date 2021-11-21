using System;
using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

sealed class SimulatedDbCommand : DbCommand
{
    internal Simulation simulation;
    internal SimulatedDbConnection connection;
    internal SimulatedDbTransaction? transaction;

    public SimulatedDbCommand(Simulation simulation, SimulatedDbConnection connection)
    {
        this.simulation = simulation;
        this.connection = connection;
    }

    public SimulatedDbCommand(Simulation simulation, SimulatedDbConnection connection, SimulatedDbTransaction transaction)
    {
        this.simulation = simulation;
        this.connection = connection;
        this.transaction = transaction;
    }

    public override string? CommandText { get; set; }
    public override int CommandTimeout { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override CommandType CommandType { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override bool DesignTimeVisible { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public override UpdateRowSource UpdatedRowSource { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    protected override DbConnection DbConnection { get => this.connection; set => throw new NotImplementedException(); }

    protected override DbParameterCollection DbParameterCollection { get; } = new SimulatedDbParameterCollection();

    protected override DbTransaction? DbTransaction
    {
        get => this.transaction;
        set
        {
            if (value == null)
            {
                this.transaction = null;
                return;
            }

            if (value is not SimulatedDbTransaction transaction)
                throw new NotSupportedException("Simulated DbCommands must use simulation-generated transactions.");

            if (transaction.simulation != this.simulation)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different simulations.");

            if (transaction.connection != this.connection)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different connections.");

            this.transaction = transaction;
        }
    }

    public override void Cancel()
    {
        throw new NotImplementedException();
    }

    public override int ExecuteNonQuery()
    {
        using var reader = new SimulatedDbDataReader(this.simulation, this.simulation.CreateResultSetsForCommand(this));

        while (reader.NextResult())
            continue;

        return -1;
    }

    public override object ExecuteScalar()
    {
        throw new NotImplementedException();
    }

    public override void Prepare()
    {
        throw new NotImplementedException();
    }

    protected override DbParameter CreateDbParameter()
    {
        return new SimulatedDbParameter();
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        return new SimulatedDbDataReader(this.simulation, this.simulation.CreateResultSetsForCommand(this));
    }
}
