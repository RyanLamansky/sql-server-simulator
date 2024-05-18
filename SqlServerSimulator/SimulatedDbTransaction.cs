using System.Data;
using System.Data.Common;

namespace SqlServerSimulator;

sealed class SimulatedDbTransaction(Simulation simulation, SimulatedDbConnection connection, IsolationLevel isolationLevel) : DbTransaction
{
    internal readonly Simulation simulation = simulation;
#pragma warning disable CA2213 // Disposable fields should be disposed
    // This is intended to survive even if the transaction is disposed.
    internal readonly SimulatedDbConnection connection = connection;
#pragma warning restore

    public override IsolationLevel IsolationLevel { get; } = isolationLevel;

    protected override DbConnection DbConnection => this.connection;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
        throw new NotImplementedException();
    }
}
