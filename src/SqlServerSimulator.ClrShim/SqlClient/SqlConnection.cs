using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Security;
using Microsoft.SqlServer.Server;

#pragma warning disable IDE0130 // The namespace is .NET Framework's own, which a SQLCLR assembly's references name.

namespace System.Data.SqlClient;

/// <summary>
/// The in-process data provider's connection, as far as a SQLCLR routine can
/// use it inside the simulator: the <em>context connection</em>
/// (<c>context connection=true</c>), whose commands run in the calling
/// session — its transaction, security context, temp tables and
/// <c>SET</c> options — one level deeper in <c>@@NESTLEVEL</c>. One may be
/// open per routine at a time, only a routine that may read data opens one,
/// and a <c>SAFE</c> assembly asking for any other connection meets the
/// permission demand the server's code access security raises (all probed
/// 2026-09-28 against SQL Server 2025). The public members are never inlined,
/// so a stack trace names them as the server's does.
/// </summary>
public sealed class SqlConnection(string? connectionString) : DbConnection, ICloneable
{
    private const string ClosedConnection = "Invalid operation. The connection is closed.";
    private const string NotOnContext = "The requested operation is not available on the context connection.";

    private string connectionString = connectionString ?? "";
    private SimulatorBridge.Frame? frame;

    public SqlConnection()
        : this(null)
    {
    }

    public event SqlInfoMessageEventHandler? InfoMessage;

    [AllowNull]
    public override string ConnectionString
    {
        get => this.connectionString;
        set
        {
            if (this.frame is not null)
                throw new InvalidOperationException("Not allowed to change the 'ConnectionString' property. The connection's current state is open.");
            this.connectionString = value ?? "";
        }
    }

    public override int ConnectionTimeout => 15;

    public override string Database => this.frame?.Data!.Database() ?? "";

    public override string DataSource => "";

    public override string ServerVersion => this.frame?.Data!.ServerVersion ?? throw new InvalidOperationException(ClosedConnection);

    public override ConnectionState State => this.frame is null ? ConnectionState.Closed : ConnectionState.Open;

    public int PacketSize
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => this.IsContextConnection() ? throw new InvalidOperationException(NotOnContext) : 8000;
    }

    public string WorkstationId
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => this.IsContextConnection() ? throw new InvalidOperationException(NotOnContext) : Environment.MachineName;
    }

    public Guid ClientConnectionId => Guid.Empty;

    public bool StatisticsEnabled { get; set; }

    public bool FireInfoMessageEventOnUserErrors { get; set; }

    /// <summary>The reader a command on this connection has open, which blocks every other command until it closes.</summary>
    internal SqlDataReader? OpenReader;

    /// <summary>The transaction <see cref="BeginTransaction()"/> started, until it commits or rolls back.</summary>
    internal SqlTransaction? PendingTransaction;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Open()
    {
        if (this.connectionString.Length == 0)
            throw new InvalidOperationException("The ConnectionString property has not been initialized.");
        if (this.frame is not null)
            throw new InvalidOperationException("The connection was not closed. The connection's current state is open.");
        if (!this.IsContextConnection())
        {
            if (SimulatorBridge.Current?.Data is { Safe: false })
                throw new NotSupportedException("A connection other than the context connection, which an EXTERNAL_ACCESS or UNSAFE assembly may open, is not modeled by the simulator.");
            throw new SecurityException("Request for the permission of type 'System.Data.SqlClient.SqlClientPermission, System.Data, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089' failed.");
        }

        var current = SimulatorBridge.Current;
        if (current?.Data is null)
            throw new InvalidOperationException(SqlContext.NoDataAccess);
        if (current.ContextConnectionOpen)
            throw new InvalidOperationException("The context connection is already in use.");
        current.ContextConnectionOpen = true;
        this.frame = current;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void Close()
    {
        if (this.frame is not { } open)
            return;
        this.OpenReader?.Close();
        this.PendingTransaction = null;
        open.ContextConnectionOpen = false;
        this.frame = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public override void ChangeDatabase(string databaseName)
    {
        if (this.frame is null)
            throw new InvalidOperationException(ClosedConnection);
        ArgumentException.ThrowIfNullOrEmpty(databaseName);
        this.RunChecked("USE " + Bracket(databaseName));
    }

    public new SqlTransaction BeginTransaction() => this.BeginTransaction(IsolationLevel.Unspecified, null);

    public new SqlTransaction BeginTransaction(IsolationLevel iso) => this.BeginTransaction(iso, null);

    public SqlTransaction BeginTransaction(string transactionName) => this.BeginTransaction(IsolationLevel.Unspecified, transactionName);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public SqlTransaction BeginTransaction(IsolationLevel iso, string? transactionName)
    {
        if (this.frame is null)
            throw new InvalidOperationException(ClosedConnection);
        if (this.PendingTransaction is not null)
            throw new InvalidOperationException("SqlConnection does not support parallel transactions.");
        var isolation = iso switch
        {
            IsolationLevel.Unspecified => "",
            IsolationLevel.ReadUncommitted => "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED; ",
            IsolationLevel.ReadCommitted => "SET TRANSACTION ISOLATION LEVEL READ COMMITTED; ",
            IsolationLevel.RepeatableRead => "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ; ",
            IsolationLevel.Serializable => "SET TRANSACTION ISOLATION LEVEL SERIALIZABLE; ",
            IsolationLevel.Snapshot => "SET TRANSACTION ISOLATION LEVEL SNAPSHOT; ",
            _ => throw new ArgumentOutOfRangeException(nameof(iso), $"The IsolationLevel enumeration value, {(int)iso}, is not supported by the .Net Framework SqlClient Data Provider."),
        };
        this.RunChecked(isolation + "BEGIN TRANSACTION" + (string.IsNullOrEmpty(transactionName) ? "" : " " + Bracket(transactionName)));
        return this.PendingTransaction = new SqlTransaction(this, iso == IsolationLevel.Unspecified ? IsolationLevel.ReadCommitted : iso);
    }

    public new SqlCommand CreateCommand() => new(null, this);

    public object Clone() => new SqlConnection(this.connectionString);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => this.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => this.CreateCommand();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            this.Close();
        base.Dispose(disposing);
    }

    /// <summary>
    /// Runs <paramref name="command"/> in the calling session, raising the
    /// messages it sent through <see cref="InfoMessage"/>. Its errors are the
    /// caller's to raise, in whichever order its method raises them.
    /// </summary>
    internal ContextResult Run(ContextCommand command)
    {
        var result = this.frame!.Data!.Execute(command);
        List<SqlError>? messages = null;
        foreach (var item in result.Items)
        {
            if (item is ContextMessage { Class: <= 10 } message)
                (messages ??= []).Add(ToError(message));
        }

        if (messages is not null)
            this.InfoMessage?.Invoke(this, new SqlInfoMessageEventArgs(new SqlErrorCollection(messages)));
        return result;
    }

    /// <summary>Sends a result set a command on this connection returned to the routine's client.</summary>
    internal void SendResultSet(object handle, int fromRow) => this.frame!.Data!.Send(handle, fromRow);

    /// <summary>Runs a statement the connection issues on its own behalf, raising any error it meets.</summary>
    internal void RunChecked(string text) => ThrowIfErrors(this.Run((text, false, [], false)).Items);

    /// <summary>Raises the errors among <paramref name="items"/> as one <see cref="SqlException"/>, if there are any.</summary>
    internal static void ThrowIfErrors(object[] items)
    {
        List<SqlError>? errors = null;
        foreach (var item in items)
        {
            if (item is ContextMessage { Class: > 10 } message)
                (errors ??= []).Add(ToError(message));
        }

        if (errors is not null)
            throw new SqlException(new SqlErrorCollection(errors));
    }

    internal static SqlError ToError(ContextMessage message) =>
        new(message.Number, message.Class, message.State, message.LineNumber, message.Procedure, message.Message, message.Server);

    private bool IsContextConnection()
    {
        var isContext = false;
        var otherKeyword = false;
        foreach (var pair in this.connectionString.Split(';'))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var key = (equals < 0 ? pair : pair[..equals]).Trim();
            if (key.Length == 0)
                continue;
            var value = equals < 0 ? "" : pair[(equals + 1)..].Trim();
            if (string.Equals(key, "context connection", StringComparison.OrdinalIgnoreCase))
                isContext = value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
            else if (!string.Equals(key, "type system version", StringComparison.OrdinalIgnoreCase))
                otherKeyword = true;
        }

        return isContext && otherKeyword
            ? throw new InvalidOperationException("The only additional connection string keyword that may be used when requesting the context connection is the Type System Version keyword.")
            : isContext;
    }

    private static string Bracket(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
