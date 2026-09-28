using System.Data;
using System.Data.SqlClient;

namespace Microsoft.SqlServer.Server;

/// <summary>
/// The simulator's way in. It binds <see cref="Enter"/> and <see cref="Exit"/>
/// by reflection and passes only framework types across, so the simulator
/// needs no compile-time reference to this assembly.
/// </summary>
internal static class SimulatorBridge
{
    /// <summary>The routine running on this thread, or <see langword="null"/> outside one.</summary>
    [ThreadStatic]
    internal static Frame? Current;

    /// <summary>
    /// Opens a routine's context on this thread and returns the one it
    /// shadows, to hand back to <see cref="Exit"/>. A procedure or trigger
    /// passes the four pipe callbacks; a function or aggregate passes
    /// <see langword="null"/> for them and gets no pipe. A trigger also
    /// passes what its <see cref="SqlTriggerContext"/> reports: the action
    /// (a DML verb or the DDL event type's number), whether each column
    /// counts as updated, and a DDL event's <c>EVENTDATA()</c> document.
    /// A routine that may read data passes what its context connection runs
    /// through: the command executor, the result-set sender
    /// <see cref="SqlPipe.Send(SqlDataReader)"/> uses, the current
    /// database's name, the server version the connection reports, and
    /// whether the routine's assembly is <c>SAFE</c>.
    /// </summary>
    internal static object? Enter(
        Action<string>? message,
        Action<(string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[]>? start,
        Action<object?[]>? row,
        Action? end,
        (int Action, bool[] UpdatedColumns, string? EventData)? trigger,
        (Func<ContextCommand, ContextResult> Execute, Action<object, int> Send, Func<string> Database, string ServerVersion, bool Safe)? data)
    {
        var previous = Current;
        Current = new Frame(
            message is null ? null : new SqlPipe(new Sink(message, start!, row!, end!)),
            trigger is { } fired ? new SqlTriggerContext((TriggerAction)fired.Action, fired.UpdatedColumns, fired.EventData) : null,
            data is { } access ? new ContextAccess(access.Execute, access.Send, access.Database, access.ServerVersion, access.Safe) : null);
        return previous;
    }

    /// <summary>
    /// Closes the running routine's context and restores
    /// <paramref name="previous"/>. A result set the routine left open is the
    /// simulator's to settle, since only it knows whether the routine ended by
    /// returning or by throwing.
    /// </summary>
    internal static void Exit(object? previous) => Current = (Frame?)previous;

    internal sealed class Frame(SqlPipe? pipe, SqlTriggerContext? trigger, ContextAccess? data)
    {
        public readonly SqlPipe? Pipe = pipe;

        public readonly SqlTriggerContext? Trigger = trigger;

        /// <summary>The context connection's way into the session, or <see langword="null"/> where the routine may not read data.</summary>
        public readonly ContextAccess? Data = data;

        /// <summary>Whether a context connection is open in this routine — the server allows one at a time.</summary>
        public bool ContextConnectionOpen;
    }

    internal sealed class Sink(
        Action<string> message,
        Action<(string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[]> start,
        Action<object?[]> row,
        Action end)
    {
        public readonly Action<string> Message = message;
        public readonly Action<(string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[]> Start = start;
        public readonly Action<object?[]> Row = row;
        public readonly Action End = end;
    }

    internal sealed class ContextAccess(
        Func<ContextCommand, ContextResult> execute,
        Action<object, int> send,
        Func<string> database,
        string serverVersion,
        bool safe)
    {
        public readonly Func<ContextCommand, ContextResult> Execute = execute;

        /// <summary>Sends a result set the executor returned, from the given row on, to the routine's client.</summary>
        public readonly Action<object, int> Send = send;

        public readonly Func<string> Database = database;
        public readonly string ServerVersion = serverVersion;
        public readonly bool Safe = safe;
    }
}
