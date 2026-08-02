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

    /// <summary>
    /// When set, local temp tables (<c>#foo</c>) created while this command's
    /// batch runs are dropped when it finishes — the module-scoped temp-table
    /// lifetime a dynamic-SQL scope has. Set by the TDS endpoint's RPC
    /// <c>sp_executesql</c> / <c>sp_execute</c> / <c>sp_prepexec</c> handler,
    /// which executes an ad-hoc statement that SQL Server runs in a nested
    /// scope; a normal session command leaves it false so its temp tables
    /// persist for the session.
    /// </summary>
    internal bool ScopeTempTablesToBatch;

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

    /// <summary>
    /// Requests cancellation of the command currently executing on this
    /// command's connection, the in-process analogue of <c>SqlCommand.Cancel()</c>
    /// sending a server attention. Safe to call from another thread while an
    /// execute is in flight: the engine observes it at the next safe point
    /// (statement boundary, <c>WAITFOR DELAY</c> wait) and aborts the batch —
    /// remaining statements are discarded and, under <c>SET XACT_ABORT ON</c>,
    /// an open transaction rolls back. Because the simulator executes a
    /// statement's result set synchronously into memory before
    /// <c>ExecuteReader</c> returns, a <c>Cancel</c> arriving after that
    /// point has nothing left in flight for that statement to interrupt (the
    /// reader then drains already-materialized rows) — matching SqlClient's
    /// no-op when called with nothing to cancel. A <c>Cancel</c> with no
    /// live execution is a no-op.
    /// <para>A cancel that <em>did</em> abort an execution surfaces as
    /// <see cref="SimulatedSqlException.CommandCancelled"/> (Msg 0) out of the
    /// execute call, mirroring SqlClient rather than returning a truncated
    /// result as a successful one.</para>
    /// </summary>
    public override void Cancel() => this.Connection?.CancelExecution();

    /// <summary>
    /// Drains the whole batch (all statements execute, all side effects
    /// persist), summing the rows-affected of each non-query statement, then
    /// throws if any statement raised a continued error — mirroring real
    /// SqlClient, which runs a batch to completion and surfaces every
    /// statement-terminating error through one aggregated
    /// <see cref="SimulatedSqlException.Errors"/> collection. Returns
    /// <c>-1</c> when no statement contributed a row count (row-returning
    /// SELECTs and DDL don't).
    /// </summary>
    public override int ExecuteNonQuery()
    {
        List<SimulatedSqlException>? errors = null;
        var affected = 0;
        var counted = false;
        foreach (var outcome in simulation.CreateResultSetsForCommand(this))
        {
            switch (outcome)
            {
                case SimulatedErrorOutcome error:
                    (errors ??= []).Add(error.Exception);
                    break;
                case SimulatedNonQuery nonQuery when nonQuery.RecordsAffected != -1:
                    affected += nonQuery.RecordsAffected;
                    counted = true;
                    break;
            }
        }

        ThrowIfExecutionCancelled();
        return errors is not null
            ? throw SimulatedSqlException.Aggregate(errors)
            : counted ? affected : -1;
    }

    /// <summary>
    /// Returns the first column of the first row of the first result set,
    /// matching real SqlClient — but like <see cref="ExecuteNonQuery"/> it
    /// drains the whole batch, so a trailing statement-terminating error
    /// throws instead of the value being returned (probe-confirmed:
    /// <c>SELECT 42; SELECT 1/0</c> throws Msg 8134 rather than returning
    /// 42). An empty first result set yields <see langword="null"/> without
    /// consulting later result sets.
    /// </summary>
    public override object? ExecuteScalar()
    {
        List<SimulatedSqlException>? errors = null;
        object? scalar = null;
        var haveScalar = false;
        foreach (var outcome in simulation.CreateResultSetsForCommand(this))
        {
            switch (outcome)
            {
                case SimulatedErrorOutcome error:
                    (errors ??= []).Add(error.Exception);
                    break;
                case SimulatedQueryResult query when !haveScalar:
#pragma warning disable CA2000 // The using disposes the returned cursor; a TextSizeCursor wrapper disposes its wrapped inner cursor, an ownership transfer the analyzer can't see.
                    using (var cursor = query.CreateClientCursor())
#pragma warning restore CA2000
                    {
                        if (cursor.MoveNext())
                        {
                            var value = cursor[0];
                            // A present-but-NULL first column surfaces as
                            // DBNull.Value (matching SqlClient and the reader's
                            // GetValue); only an empty first result set leaves
                            // the C# null that signals "no value".
                            scalar = value.IsNull ? DBNull.Value : value.ToObject();
                        }
                    }

                    haveScalar = true;
                    break;
            }
        }

        ThrowIfExecutionCancelled();
        return errors is not null ? throw SimulatedSqlException.Aggregate(errors) : scalar;
    }

    /// <summary>
    /// No-op. Statement preparation is a server-side execution-plan
    /// optimization with no observable effect on results; the simulator parses
    /// each execution directly. A future build could cache the parsed plan here.
    /// </summary>
    public override void Prepare() { }

    /// <inheritdoc/>
    protected override DbParameter CreateDbParameter() => new SimulatedDbParameter();

    /// <inheritdoc/>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior = default)
    {
        // The reader's constructor drains outcomes up to the first result set,
        // so a cancellation that aborted the batch is already observable here —
        // the check costs no extra eagerness. Real SqlClient throws out of
        // ExecuteReader rather than handing back an empty reader, so a caller
        // can't mistake a cancelled batch for a zero-row answer.
        var reader = new SimulatedDbDataReader(this.simulation.CreateResultSetsForCommand(this));
        if (WasExecutionCancelled())
        {
            reader.Dispose();
            throw CancellationException();
        }
        return reader;
    }

    /// <summary>
    /// True when the execution that just ran on this command's connection was
    /// cancelled (an <c>ExecuteReaderAsync</c> caller's token — the ADO.NET
    /// base class registers it to call <see cref="Cancel"/> — or a direct
    /// <see cref="Cancel"/> from another thread). The engine aborts at its next
    /// safe point and simply stops producing outcomes, so without this check
    /// the surface would report a truncated batch as a successful one.
    /// </summary>
    private bool WasExecutionCancelled() =>
        this.Connection?.ExecutionCancellationToken.IsCancellationRequested == true;

    /// <summary>
    /// Surfaces a cancelled execution the way real SqlClient does — see
    /// <see cref="SimulatedSqlException.CommandCancelled"/>.
    /// </summary>
    private void ThrowIfExecutionCancelled()
    {
        if (WasExecutionCancelled())
            throw CancellationException();
    }

    /// <summary>
    /// Picks the surface for an aborted execution: <b>Msg -2</b> when the
    /// command's <see cref="CommandTimeout"/> expired, <b>Msg 0</b> when a
    /// caller cancelled — the same split real SqlClient makes.
    /// </summary>
    private SimulatedSqlException CancellationException() =>
        this.Connection?.ExecutionTimedOut == true
            ? SimulatedSqlException.ExecutionTimeoutExpired()
            : SimulatedSqlException.CommandCancelled();

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
