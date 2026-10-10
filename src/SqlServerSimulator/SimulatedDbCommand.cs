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

    /// <summary>
    /// The parameter declaration string an RPC <c>sp_executesql</c> /
    /// <c>sp_execute</c> / <c>sp_prepexec</c> call carried, set by the TDS
    /// endpoint; null for an in-process command, whose declaration is the one
    /// SqlClient would build from <see cref="Parameters"/>. A declared
    /// parameter the call doesn't supply raises Msg 8178 quoting it.
    /// </summary>
    internal string? ParameterDeclaration;

    /// <summary>
    /// When set, a <c>DECLARE … CURSOR</c> in this command's batch opens an API
    /// server cursor rather than a T-SQL one. Set by the TDS endpoint's
    /// <c>sp_cursoropen</c> family, which synthesizes the DECLARE / OPEN pair;
    /// the two origins differ in one probed respect, so the flag rides along
    /// rather than the parser guessing from the cursor's name.
    /// </summary>
    internal bool ApiServerCursor;

    /// <summary>
    /// Set while <c>ExecuteReader</c> starts the command: its request stays
    /// outstanding until the reader has read past its end, where a command
    /// whose results are drained at once never is (see
    /// <see cref="SessionRequest.Consumed"/>).
    /// </summary>
    internal bool ReadByReader;

    /// <summary>The in-process request the command's latest execution opened, if any.</summary>
    internal SessionRequest? Request;

    /// <summary>
    /// Set by the TDS endpoint on a MARS request's batch: its outcome stream
    /// marks each boundary between the batch's statements
    /// (<see cref="SimulatedStatementBoundary"/>), where the endpoint lets the
    /// session's other requests run.
    /// </summary>
    internal bool YieldsBetweenStatements;

    /// <summary>
    /// Set by a consumer that reads a result set's rows as they come — the
    /// in-process data reader, the TDS endpoint outside MARS — while the
    /// command starts: its batch's statement-level <c>SELECT</c>s produce
    /// their rows as the consumer reads them (see <see cref="ResultStream"/>).
    /// </summary>
    internal bool StreamsResultRows;

    internal SimulatedDbCommand(Simulation simulation, SimulatedDbConnection connection)
    {
        this.simulation = simulation;
        this.Connection = connection;
        this.CommandTimeout = simulation.DefaultCommandTimeout;
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
    }

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

            if (transaction.Owner != this.Connection)
                throw new NotSupportedException("Simulated DbCommands cannot switch to different connections.");

            field = transaction;
        }
    }

    /// <summary>
    /// Requests cancellation of the command currently executing on this
    /// command's connection, the in-process analogue of <c>SqlCommand.Cancel()</c>
    /// sending a server attention. Safe to call from another thread while an
    /// execute is in flight: the engine observes it at the next safe point
    /// (statement boundary, <c>WAITFOR</c> wait) and aborts the batch —
    /// remaining statements are discarded and, under <c>SET XACT_ABORT ON</c>,
    /// an open transaction rolls back. A result larger than what the server
    /// gets ahead of its client is produced as the reader reads it, so a
    /// <c>Cancel</c> while the reader is still reading one ends its statement
    /// as the next read resumes it, and that read raises the cancellation; a
    /// result that fit went out whole before <c>ExecuteReader</c> returned,
    /// leaving nothing in flight for that statement to interrupt — matching
    /// SqlClient's no-op when called with nothing to cancel. A <c>Cancel</c>
    /// with no live execution is a no-op.
    /// <para>A cancel that <em>did</em> abort an execution surfaces as a
    /// <see cref="SimulatedSqlException"/> out of the execute call, carrying
    /// the Msg 0 severe-error wording real SqlClient reports for a cancelled
    /// command rather than returning a truncated result as a successful
    /// one.</para>
    /// </summary>
    public override void Cancel()
    {
        // A command that opened a request of its own cancels that request,
        // even while another command's runs between its statements.
        if (this.Request is { } request)
        {
            if (!request.Finished)
                request.Cancel();
            return;
        }
        this.Connection?.CancelExecution();
    }

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
        using var culture = CultureScope.Engine();
        this.RequireOpenConnection(nameof(ExecuteNonQuery));
        List<SimulatedSqlException>? errors = null;
        List<SimulatedError>? messages = null;
        var affected = 0;
        var counted = false;
        var compileError = new CompileErrorCount();
        foreach (var outcome in simulation.CreateResultSetsForCommand(this))
        {
            if (!compileError.Admits(outcome) && outcome is not SimulatedErrorOutcome)
                continue;
            switch (outcome)
            {
                case SimulatedErrorOutcome error:
                    (errors ??= []).Add(error.Exception);
                    break;
                case SimulatedInfoOutcome info:
                    (messages ??= []).Add(info.Message);
                    break;
                case { ClientRecordsAffected: >= 0 } counting:
                    affected += counting.ClientRecordsAffected;
                    counted = true;
                    break;
            }
        }

        ThrowIfExecutionCancelled();
        this.CompleteDrainedBatch(errors, messages);
        return counted ? affected : -1;
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
        using var culture = CultureScope.Engine();
        this.RequireOpenConnection(nameof(ExecuteScalar));
        List<SimulatedSqlException>? errors = null;
        List<SimulatedError>? messages = null;
        object? scalar = null;
        var haveScalar = false;
        foreach (var outcome in simulation.CreateResultSetsForCommand(this))
        {
            switch (outcome)
            {
                case SimulatedErrorOutcome error:
                    (errors ??= []).Add(error.Exception);
                    break;
                case SimulatedInfoOutcome info:
                    (messages ??= []).Add(info.Message);
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
                            scalar = value.IsNull ? DBNull.Value : value.Type is Storage.XmlSqlType ? SimulatedDbDataReader.ClientString(value) : value.ToObject();
                        }
                    }

                    haveScalar = true;
                    break;
            }
        }

        ThrowIfExecutionCancelled();
        this.CompleteDrainedBatch(errors, messages);
        return scalar;
    }

    /// <summary>
    /// Ends a batch <see cref="ExecuteNonQuery"/> / <see cref="ExecuteScalar"/>
    /// drained whole, the way SqlClient does: any error makes one exception
    /// carrying every error and then every message the batch sent; otherwise
    /// each message fires <see cref="SimulatedDbConnection.InfoMessage"/> in
    /// order (probed 2026-09-23).
    /// </summary>
    private void CompleteDrainedBatch(List<SimulatedSqlException>? errors, List<SimulatedError>? messages)
    {
        if (errors is not null)
            throw SimulatedSqlException.ForClient(errors, messages);
        if (messages is null)
            return;
        foreach (var message in messages)
            this.Connection?.RaiseInfoMessage(message);
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
        using var culture = CultureScope.Engine();
        // The reader's constructor drains outcomes up to the first result set,
        // so a cancellation that aborted the batch is already observable here —
        // the check costs no extra eagerness. Real SqlClient throws out of
        // ExecuteReader rather than handing back an empty reader, so a caller
        // can't mistake a cancelled batch for a zero-row answer.
        this.RequireOpenConnection(nameof(ExecuteReader));
        // KeyInfo runs the batch in browse mode, as SqlClient's
        // `SET NO_BROWSETABLE ON` / `OFF` wrapper around the command text does.
        var browse = behavior.HasFlag(CommandBehavior.KeyInfo) && this.CommandType == CommandType.Text && this.Connection is { NoBrowseTable: false };
        if (browse)
            this.Connection!.NoBrowseTable = true;
        SimulatedDbDataReader reader;
        this.Request = null;
        this.ReadByReader = true;
        this.StreamsResultRows = true;
        try
        {
            reader = new SimulatedDbDataReader(this.simulation.CreateResultSetsForCommand(this), this.Connection, this);
        }
        catch
        {
            if (browse)
                this.Connection!.NoBrowseTable = false;
            // The batch was read whole for the error it raised.
            if (this.Request is { } failed)
                this.Connection!.ConsumeRequest(failed);
            throw;
        }
        finally
        {
            this.ReadByReader = false;
            this.StreamsResultRows = false;
        }
        if (browse)
        {
            var connection = this.Connection!;
            reader.AfterClose = () => connection.NoBrowseTable = false;
        }
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
    /// <remarks>
    /// A cancel that ended a write carries the Msg 3621 real sends ahead of
    /// its acknowledgment, which SqlClient lists after its own error — unless
    /// a transaction began during the batch, when the transaction ENVCHANGEs
    /// sharing that response leave SqlClient showing its own error alone
    /// (probed 2026-09-30 against SQL Server 2025 through SqlClient 7.0.2).
    /// </remarks>
    private SimulatedSqlException CancellationException()
    {
        var connection = this.Connection;
        if (connection is { Killed: true })
            return SimulatedSqlException.SessionKilled();
        var cancelled = connection?.ExecutionTimedOut == true && this.Request is not { CancelledByUser: true }
            ? SimulatedSqlException.ExecutionTimeoutExpired()
            : SimulatedSqlException.CommandCancelled();
        if (connection is not { AttentionEndedWrite: true })
            return cancelled;
        connection.AttentionEndedWrite = false;
        if (connection.TransactionBegunInExecution)
            return cancelled;
        return SimulatedSqlException.Aggregate([cancelled], [SimulatedSqlException.AttentionStatementTerminatedMessage(connection)]);
    }

    /// <summary>
    /// SqlClient's refusal to run a command without an open connection — one
    /// the caller never opened, closed, or lost to an error that ended its
    /// session — as the <see cref="InvalidOperationException"/> it throws,
    /// worded as it words it.
    /// </summary>
    private void RequireOpenConnection(string method)
    {
        if (this.Connection is not { } connection)
            throw new InvalidOperationException($"{method}: Connection property has not been initialized.");
        if (connection is { Killed: true, State: ConnectionState.Open })
        {
            connection.Close();
            throw SimulatedSqlException.ConnectionBroken();
        }
        if (connection.State != ConnectionState.Open)
        {
            var state = connection.State switch
            {
                ConnectionState.Broken => "broken",
                ConnectionState.Connecting => "connecting",
                _ => "closed",
            };
            throw new InvalidOperationException($"{method} requires an open and available Connection. The connection's current state is {state}.");
        }
        // SqlClient's own client-side rule: while a transaction BeginTransaction
        // began or nested is pending, a command must carry it.
        if (this.Transaction is null && connection.CurrentTransaction is { HoldsApiTransaction: true })
            throw new InvalidOperationException($"{method} requires the command to have a transaction when the connection assigned to the command is in a pending local transaction.  The Transaction property of the command has not been initialized.");
    }

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
