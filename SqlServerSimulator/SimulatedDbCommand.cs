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

    /// <summary>
    /// No-op. <c>SqlCommand.Cancel()</c> sends a server "attention" token that
    /// abandons in-flight result production; it never closes the reader. The
    /// simulator executes synchronously in-process, so a command's result set
    /// is conceptually complete by the time it returns — there is nothing in
    /// flight to interrupt, matching SqlClient's behavior when <c>Cancel()</c>
    /// is called with nothing to cancel.
    /// </summary>
    public override void Cancel() { }

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

        return errors is not null
            ? throw AggregateErrors(errors)
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
                    using (var cursor = query.CreateCursor())
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

        return errors is not null ? throw AggregateErrors(errors) : scalar;
    }

    /// <summary>
    /// Collapses the statement-terminating errors gathered while draining a
    /// batch into a single exception. A lone error is rethrown as-is (its
    /// <see cref="SimulatedSqlException.Errors"/> already carries its own
    /// entries); multiple errors flatten into one exception whose
    /// <c>Errors</c> collection holds every entry in batch order.
    /// </summary>
    private static SimulatedSqlException AggregateErrors(List<SimulatedSqlException> errors)
    {
        if (errors.Count == 1)
            return errors[0];

        var entries = new List<SimulatedError>(errors.Count);
        foreach (var error in errors)
        {
            foreach (var entry in error.Errors)
                entries.Add(entry);
        }

        return SimulatedSqlException.FromErrors(entries);
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
        => new SimulatedDbDataReader(this.simulation.CreateResultSetsForCommand(this));

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
