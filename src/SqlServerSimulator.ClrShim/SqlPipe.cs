using System.Data;
using System.Data.SqlClient;
using System.Runtime.CompilerServices;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// The channel a CLR procedure sends messages and result sets through,
/// reached as <see cref="SqlContext.Pipe"/>. Every call hands its payload
/// straight to the simulator's sink for the running procedure, and the guards
/// raise the exceptions and texts the in-server pipe does (probed 2026-09-28
/// against SQL Server 2025). <see cref="ExecuteAndSend"/> runs a
/// context-connection command with everything it produces — result sets, row
/// counts, messages and errors — going to the client, and
/// <see cref="Send(SqlDataReader)"/> sends what a reader has left to read. The
/// public members are never inlined, so a stack trace a routine's exception
/// carries names them as the server's does.
/// </summary>
public sealed class SqlPipe
{
    private readonly SimulatorBridge.Sink sink;
    private SqlMetaData[]? sending;

    internal SqlPipe(SimulatorBridge.Sink sink) => this.sink = sink;

    public bool IsSendingResults => this.sending is not null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Send(string message)
    {
        if (message is null)
            throw new ArgumentNullException(nameof(message));
        if (this.sending is not null)
            throw new InvalidOperationException("A result set is currently being sent to the pipe. End the current result set before calling Send.");
        if (message.Length > 4000)
            throw new ArgumentException($"Message length {message.Length} exceeds maximum length supported of 4000.");
        this.sink.Message(message);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Send(SqlDataRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));
        if (this.sending is not null)
            throw new InvalidOperationException("A result set is currently being sent to the pipe. End the current result set before calling Send.");
        this.sink.Start(Describe(record.MetaData));
        this.sink.Row(record.SnapshotValues());
        this.sink.End();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Send(SqlDataReader reader)
    {
        if (reader is null)
            throw new ArgumentNullException(nameof(reader));
        if (this.sending is not null)
            throw new InvalidOperationException("A result set is currently being sent to the pipe. End the current result set before calling Send.");
        reader.SendRemaining();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void ExecuteAndSend(SqlCommand command)
    {
        if (command is null)
            throw new ArgumentNullException(nameof(command));
        if (this.sending is not null)
            throw new InvalidOperationException("A result set is currently being sent to the pipe. End the current result set before calling ExecuteAndSend.");
        command.ExecuteToPipe();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SendResultsStart(SqlDataRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));
        if (this.sending is not null)
            throw new InvalidOperationException("A result set is currently being sent to the pipe. End the current result set before calling SendResultsStart.");
        this.sending = record.MetaData;
        this.sink.Start(Describe(record.MetaData));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SendResultsRow(SqlDataRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));
        if (this.sending is null)
            throw new InvalidOperationException("Result set has not been initiated.  Call SendResultSetStart before calling SendResultsRow.");
        if (record.MetaData.Length != this.sending.Length)
            throw new InvalidOperationException("The record does not match the metadata of the result set being sent.");
        this.sink.Row(record.SnapshotValues());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SendResultsEnd()
    {
        if (this.sending is null)
            throw new InvalidOperationException("Result set has not been initiated.  Call SendResultSetStart before calling SendResultsEnd.");
        this.sending = null;
        this.sink.End();
    }

    private static (string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[] Describe(SqlMetaData[] metaData)
    {
        var columns = new (string, SqlDbType, long, byte, byte)[metaData.Length];
        for (var i = 0; i < metaData.Length; i++)
        {
            var column = metaData[i];
            columns[i] = (column.Name, column.SqlDbType, column.MaxLength, column.Precision, column.Scale);
        }

        return columns;
    }
}
