using System.Data;

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
    /// shadows, to hand back to <see cref="Exit"/>. A procedure passes the
    /// four pipe callbacks; a function or aggregate passes
    /// <see langword="null"/> for them and gets no pipe.
    /// </summary>
    internal static object? Enter(
        Action<string>? message,
        Action<(string Name, SqlDbType Type, long MaxLength, byte Precision, byte Scale)[]>? start,
        Action<object?[]>? row,
        Action? end)
    {
        var previous = Current;
        Current = new Frame(message is null ? null : new SqlPipe(new Sink(message, start!, row!, end!)));
        return previous;
    }

    /// <summary>
    /// Closes the running routine's context and restores
    /// <paramref name="previous"/>. A result set the routine left open is the
    /// simulator's to settle, since only it knows whether the routine ended by
    /// returning or by throwing.
    /// </summary>
    internal static void Exit(object? previous) => Current = (Frame?)previous;

    internal sealed class Frame(SqlPipe? pipe)
    {
        public readonly SqlPipe? Pipe = pipe;
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
}
