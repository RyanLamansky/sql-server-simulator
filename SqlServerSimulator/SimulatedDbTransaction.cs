using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

sealed class SimulatedDbTransaction : DbTransaction
{
    internal readonly Simulation simulation;
    internal readonly SimulatedDbConnection connection;

    public SimulatedDbTransaction(Simulation simulation, SimulatedDbConnection connection, IsolationLevel isolationLevel)
    {
        this.simulation = simulation;
        this.connection = connection;
        this.IsolationLevel = isolationLevel;
    }

    public override IsolationLevel IsolationLevel { get; }

    protected override DbConnection DbConnection => this.connection;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
        throw new NotImplementedException();
    }
}
