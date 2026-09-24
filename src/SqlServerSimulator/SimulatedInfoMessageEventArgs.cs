namespace SqlServerSimulator;

/// <summary>
/// Carries one informational message — a <c>PRINT</c>, a severity-0-10
/// <c>RAISERROR</c>, a warning — to subscribers of
/// <see cref="SimulatedDbConnection.InfoMessage"/>. Mirrors the public surface
/// of <c>Microsoft.Data.SqlClient.SqlInfoMessageEventArgs</c>:
/// <see cref="Errors"/> is the per-message collection; <see cref="Message"/> /
/// <see cref="Source"/> are shortcuts that read through the first entry.
/// </summary>
public sealed class SimulatedInfoMessageEventArgs : EventArgs
{
    internal SimulatedInfoMessageEventArgs(SimulatedErrorCollection errors) => this.Errors = errors;

    /// <summary>The message, as a one-entry collection: SqlClient fires one event per message.</summary>
    public SimulatedErrorCollection Errors { get; }

    /// <summary>The message text — shortcut for <c>Errors[0].Message</c>.</summary>
    public string Message => this.Errors[0].Message;

    /// <summary>Provider identifier — shortcut for <c>Errors[0].Source</c>.</summary>
    public string Source => this.Errors[0].Source;

    /// <summary>
    /// 1-based line number of the statement that produced the message. Not
    /// part of the SqlClient surface (SqlClient exposes the line through
    /// <c>Errors[i].LineNumber</c>); a shortcut for <c>Errors[0].LineNumber</c>.
    /// </summary>
    public int LineNumber => this.Errors[0].LineNumber;
}
