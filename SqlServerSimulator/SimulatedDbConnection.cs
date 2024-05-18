using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

sealed class SimulatedDbConnection(Simulation simulation) : DbConnection
{
    internal readonly Simulation simulation = simulation;

    [AllowNull]
    public override string ConnectionString { get => ""; set => throw new NotImplementedException(); }

    public override string Database => throw new NotImplementedException();

    public override string DataSource => throw new NotImplementedException();

    public override string ServerVersion => throw new NotImplementedException();

    private ConnectionState state;

    public override ConnectionState State => this.state;

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotImplementedException();
    }

    public override void Close()
    {
        this.state = ConnectionState.Closed;
    }

    public override void Open()
    {
        this.state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        return new SimulatedDbTransaction(this.simulation, this, isolationLevel);
    }

    protected override DbCommand CreateDbCommand()
    {
        return new SimulatedDbCommand(this.simulation, this);
    }
}
