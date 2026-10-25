namespace SqlServerSimulator;

/// <summary>
/// <see cref="SimulatedDbConnection.PendingMessages"/>: the informational
/// messages a session's statements have raised and no statement has yet
/// placed among its outcomes. Queuing a numbered message is where real raises
/// it, so it fires <c>error_reported</c> there — once, though a wrapper that
/// drains a body's messages hands them back to be queued again.
/// </summary>
internal sealed class PendingMessageQueue(SimulatedDbConnection connection) : Queue<SimulatedError>
{
    /// <summary>
    /// Set while the batch compile walk parses a statement as the run would
    /// (<c>BatchContext.ParsesForRun</c>): a message that parse would send
    /// belongs to the run, so the attempt is declined rather than the message
    /// sent ahead of it.
    /// </summary>
    public bool RefusesMessages;

    public new void Enqueue(SimulatedError message)
    {
        if (this.RefusesMessages)
            throw new Parser.RunParseDeclinedException();
        if (!message.ExtendedEventReported)
        {
            message.ExtendedEventReported = true;
            ExtendedEvents.XeEvents.ErrorReported(connection, message, intercepted: false);
        }
        base.Enqueue(message);
    }
}
