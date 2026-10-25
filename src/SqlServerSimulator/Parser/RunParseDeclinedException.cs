namespace SqlServerSimulator.Parser;

/// <summary>
/// Abandons the batch compile walk's parse of a statement as the run would
/// (<see cref="BatchContext.ParsesForRun"/>) on meeting what the parse would
/// do to the server rather than bind — a wait on another session, a message —
/// so the walk parses the statement its own way instead; never a client's to
/// see.
/// </summary>
#pragma warning disable CA1064 // Never leaves the compile walk, which catches it.
internal sealed class RunParseDeclinedException : Exception
#pragma warning restore CA1064
{
    public RunParseDeclinedException()
        : base("The statement's parse for the run was declined.")
    {
    }

    public RunParseDeclinedException(string message)
        : base(message)
    {
    }

    public RunParseDeclinedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
