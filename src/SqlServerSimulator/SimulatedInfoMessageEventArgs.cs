namespace SqlServerSimulator;

/// <summary>
/// Carries one batch's coalesced <c>PRINT</c> / severity-0-10
/// <c>RAISERROR</c> output to subscribers of
/// <see cref="SimulatedDbConnection.InfoMessage"/>. Mirrors the public surface
/// of <c>Microsoft.Data.SqlClient.SqlInfoMessageEventArgs</c>:
/// <see cref="Errors"/> is the per-error collection; <see cref="Message"/> /
/// <see cref="Source"/> are shortcuts that read through the first entry.
/// </summary>
/// <remarks>
/// Probe-confirmed batch-coalescing semantic (multiple <c>PRINT</c> /
/// informational <c>RAISERROR</c> statements in one command join their
/// payloads with <c>\n</c> into a single event firing) is implemented where
/// the command's batch is executed; this type only describes the delivered
/// payload.
/// </remarks>
public sealed class SimulatedInfoMessageEventArgs : EventArgs
{
    internal SimulatedInfoMessageEventArgs(SimulatedErrorCollection errors) => this.Errors = errors;

    /// <summary>Per-message collection — currently always one entry per fired event (the coalesced batch payload).</summary>
    public SimulatedErrorCollection Errors { get; }

    /// <summary>The joined message text — shortcut for <c>Errors[0].Message</c>.</summary>
    public string Message => this.Errors[0].Message;

    /// <summary>Provider identifier — shortcut for <c>Errors[0].Source</c>.</summary>
    public string Source => this.Errors[0].Source;

    /// <summary>
    /// 1-based line number of the <em>first</em> contributing statement in
    /// the batch — matches probe-confirmed SqlClient behavior where coalesced
    /// events carry the first contributing statement's line. Not part of the
    /// SqlClient surface (SqlClient exposes line through <c>Errors[i].LineNumber</c>);
    /// retained as a top-level shortcut for the dominant single-error case.
    /// </summary>
    public int LineNumber => this.Errors[0].LineNumber;
}
