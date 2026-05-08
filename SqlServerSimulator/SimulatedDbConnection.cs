using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace SqlServerSimulator;

sealed class SimulatedDbConnection(Simulation simulation) : DbConnection
{
    private readonly Simulation simulation = simulation;

    /// <summary>
    /// The single active explicit transaction on this connection, or null if
    /// none. SqlClient rejects parallel transactions on the same connection
    /// (probe-confirmed: <c>InvalidOperationException: SqlConnection does
    /// not support parallel transactions.</c>); the simulator mirrors that.
    /// Statements executed via this connection consult this field through
    /// <see cref="Simulation.RunMutation"/> — when set, mutations append to
    /// the transaction's <see cref="Storage.UndoLog"/> so an eventual
    /// <see cref="SimulatedDbTransaction.Rollback"/> can unwind them.
    /// </summary>
    internal SimulatedDbTransaction? CurrentTransaction;

    [AllowNull]
    public override string ConnectionString { get => ""; set => throw new NotImplementedException(); }

    public override string Database => "master";

    public override string DataSource => "simulator";

    public override string ServerVersion => "16.0.0";

    private ConnectionState state;

    public override ConnectionState State => this.state;

    public override void ChangeDatabase(string databaseName)
    {
        throw new NotImplementedException();
    }

    public override void Close()
    {
        // SqlClient auto-rolls-back any active transaction when its
        // connection closes. The transaction's own dispose handles the
        // explicit using-pattern; this branch covers raw Close() without
        // disposing the transaction first.
        this.CurrentTransaction?.Rollback();
        this.state = ConnectionState.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.CurrentTransaction?.Dispose();
        base.Dispose(disposing);
    }

    public override void Open()
    {
        this.state = ConnectionState.Open;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
    {
        if (this.CurrentTransaction is not null)
            throw new InvalidOperationException("SqlConnection does not support parallel transactions.");
        var tx = new SimulatedDbTransaction(this.simulation, this, isolationLevel);
        this.CurrentTransaction = tx;
        return tx;
    }

    protected override DbCommand CreateDbCommand()
    {
        return new SimulatedDbCommand(this.simulation, this);
    }
}
