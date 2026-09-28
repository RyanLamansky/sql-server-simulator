using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// A command on the context connection. Its text runs as a batch of its own in
/// the calling session: an error that ends a statement leaves the rest of the
/// batch running, the whole batch runs before <see cref="ExecuteNonQuery"/>
/// raises what it met — <see cref="ExecuteScalar"/> raising only what came
/// before the first result set — and what it prints reaches only
/// <see cref="SqlConnection.InfoMessage"/>
/// (probed 2026-09-28 against SQL Server 2025). The public members are never
/// inlined, so a stack trace names them as the server's does.
/// </summary>
public sealed class SqlCommand : DbCommand, ICloneable
{
    private string commandText = "";

    public SqlCommand()
    {
    }

    public SqlCommand(string? cmdText) => this.commandText = cmdText ?? "";

    public SqlCommand(string? cmdText, SqlConnection? connection)
        : this(cmdText) => this.Connection = connection;

    public SqlCommand(string? cmdText, SqlConnection? connection, SqlTransaction? transaction)
        : this(cmdText, connection) => this.Transaction = transaction;

    [AllowNull]
    public override string CommandText
    {
        get => this.commandText;
        set => this.commandText = value ?? "";
    }

    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;

    public new SqlConnection? Connection { get; set; }

    public new SqlParameterCollection Parameters { get; } = [];

    public new SqlTransaction? Transaction { get; set; }

    public override bool DesignTimeVisible { get; set; } = true;

    public override UpdateRowSource UpdatedRowSource { get; set; } = UpdateRowSource.Both;

    protected override DbConnection? DbConnection
    {
        get => this.Connection;
        set => this.Connection = (SqlConnection?)value;
    }

    protected override DbParameterCollection DbParameterCollection => this.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => this.Transaction;
        set => this.Transaction = (SqlTransaction?)value;
    }

    public override void Cancel()
    {
    }

    public override void Prepare()
    {
    }

    public void ResetCommandTimeout() => this.CommandTimeout = 30;

    public new SqlParameter CreateParameter() => new();

    public object Clone()
    {
        var clone = new SqlCommand(this.commandText, this.Connection, this.Transaction)
        {
            CommandTimeout = this.CommandTimeout,
            CommandType = this.CommandType,
            UpdatedRowSource = this.UpdatedRowSource,
        };
        foreach (SqlParameter parameter in this.Parameters)
            _ = clone.Parameters.Add((SqlParameter)parameter.Clone());
        return clone;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override int ExecuteNonQuery()
    {
        var (items, recordsAffected, _) = this.Run(nameof(this.ExecuteNonQuery), toPipe: false);
        SqlConnection.ThrowIfErrors(items);
        return recordsAffected;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override object? ExecuteScalar()
    {
        var (items, _, _) = this.Run(nameof(this.ExecuteScalar), toPipe: false);

        // What follows the first row, errors included, goes unread (probed
        // 2026-09-28 against SQL Server 2025) — save the error that cut the
        // first result set short before it had one.
        var first = Array.FindIndex(items, item => item is ContextResultSet);
        if (first < 0)
        {
            SqlConnection.ThrowIfErrors(items);
            return null;
        }

        var resultSet = (ContextResultSet)items[first];
        var last = resultSet is { EndedByError: true, Rows.Length: 0 } ? first + 1 : first;
        while (last < items.Length && last > first && items[last] is ContextMessage)
            last++;
        SqlConnection.ThrowIfErrors(items[..last]);
        return resultSet is { Rows.Length: > 0, Names.Length: > 0 } ? SqlParameter.ToClrValue(resultSet.Rows[0][0]) : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public new SqlDataReader ExecuteReader() => this.ExecuteReader(CommandBehavior.Default);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public new SqlDataReader ExecuteReader(CommandBehavior behavior)
    {
        var (items, recordsAffected, _) = this.Run(nameof(this.ExecuteReader), toPipe: false);
        var reader = new SqlDataReader(this.Connection!, items, recordsAffected, (behavior & CommandBehavior.CloseConnection) != 0);
        this.Connection!.OpenReader = reader;
        try
        {
            _ = reader.MoveToNextResultSet();
        }
        catch (SqlException)
        {
            reader.Close();
            throw;
        }

        return reader;
    }

    protected override DbParameter CreateDbParameter() => this.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => this.ExecuteReader(behavior);

    /// <summary><c>SqlPipe.ExecuteAndSend</c>: runs the command with its outcomes going straight to the routine's client, raising its errors after.</summary>
    internal void ExecuteToPipe() => SqlConnection.ThrowIfErrors(this.Run(nameof(this.ExecuteNonQuery), toPipe: true).Items);

    private ContextResult Run(string method, bool toPipe)
    {
        var open = this.Connection ?? throw new InvalidOperationException($"{method}: Connection property has not been initialized.");
        if (open.State != ConnectionState.Open)
            throw new InvalidOperationException($"{method} requires an open and available Connection. The connection's current state is closed.");
        if (open.OpenReader is not null)
            throw new InvalidOperationException("There is already an open DataReader associated with this Command which must be closed first.");
        if (open.PendingTransaction is { } pending && this.Transaction != pending)
        {
            throw new InvalidOperationException(this.Transaction is null
                ? $"{method} requires the command to have a transaction when the connection assigned to the command is in a pending local transaction.  The Transaction property of the command has not been initialized."
                : "The transaction is either not associated with the current connection or has been completed.");
        }

        if (open.PendingTransaction is null && this.Transaction is not null)
            throw new InvalidOperationException("The transaction is either not associated with the current connection or has been completed.");
        if (this.commandText.Length == 0)
            throw new InvalidOperationException($"{method}: CommandText property has not been initialized");

        var parameters = this.Parameters.ToArray();
        var described = new (string Name, SqlDbType Type, bool Typed, int Size, byte Precision, byte Scale, ParameterDirection Direction, object? Value)[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
            described[i] = parameters[i].Describe();

        var result = open.Run((this.commandText, this.CommandType == CommandType.StoredProcedure, described, toPipe));
        var outputValues = result.OutputValues;
        for (var i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].Direction != ParameterDirection.Input)
                parameters[i].SetOutput(outputValues[i]);
        }

        return result;
    }
}
