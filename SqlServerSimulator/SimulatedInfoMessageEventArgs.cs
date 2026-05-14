namespace SqlServerSimulator;

/// <summary>
/// Carries one batch's coalesced <c>PRINT</c> output to subscribers of
/// <see cref="SimulatedDbConnection.InfoMessage"/>. Internal-only surface;
/// not part of the public ADO.NET stand-in until the consumer-facing event
/// shape is settled. Mirrors a subset of SqlClient's
/// <c>SqlInfoMessageEventArgs</c>: <see cref="Message"/>, <see cref="LineNumber"/>,
/// <see cref="Source"/>. The probed batch-coalescing semantic (multiple
/// <c>PRINT</c> statements in one command join with <c>\n</c> into a single
/// event firing) is implemented in <see cref="Parser.BatchContext"/>; this
/// type only describes the delivered payload.
/// </summary>
internal sealed class SimulatedInfoMessageEventArgs(string message, int lineNumber, string source) : EventArgs
{
    /// <summary>The joined <c>PRINT</c> output from the batch.</summary>
    public readonly string Message = message;

    /// <summary>
    /// 1-based line number of the <em>first</em> <c>PRINT</c> in the batch
    /// — matches SqlClient probe behavior where coalesced events carry the
    /// first contributing statement's line.
    /// </summary>
    public readonly int LineNumber = lineNumber;

    /// <summary>
    /// Provider identifier string. SqlClient reports
    /// <c>"Core Microsoft SqlClient Data Provider"</c>; the simulator uses
    /// the simulator's own identifier so subscribers can distinguish.
    /// </summary>
    public readonly string Source = source;
}
