namespace SqlServerSimulator;

/// <summary>
/// One informational or error message surfaced through
/// <see cref="SimulatedInfoMessageEventArgs.Errors"/>. Mirrors the public
/// surface of <c>Microsoft.Data.SqlClient.SqlError</c> so apps that subscribe
/// to <see cref="SimulatedDbConnection.InfoMessage"/> can read severity,
/// state, line number, and message text the same way they would against
/// <c>SqlConnection</c>.
/// </summary>
/// <remarks>
/// Populated by the simulator for <c>PRINT</c> (number 0, class 0, state 1)
/// and severity 0-10 <c>RAISERROR</c> (number 50000, class = severity,
/// state = state argument). Severity ≥ 11 routes through
/// <see cref="SimulatedSqlException"/> instead — the informational event
/// surface only carries the non-throwing band.
/// </remarks>
public sealed class SimulatedError
{
    internal SimulatedError(byte @class, int lineNumber, string message, int number, string procedure, string server, string source, byte state)
    {
        this.Class = @class;
        this.LineNumber = lineNumber;
        this.Message = message;
        this.Number = number;
        this.Procedure = procedure;
        this.Server = server;
        this.Source = source;
        this.State = state;
    }

    /// <summary>Severity level (0-10 for the informational event surface). Mirrors <c>SqlError.Class</c>.</summary>
    public byte Class { get; }

    /// <summary>
    /// 1-based line number of the originating statement within the batch.
    /// Mirrors <c>SqlError.LineNumber</c>. The <c>internal set</c> lets the
    /// dispatch loop stamp the resolved line once the failing statement's
    /// frame is known (see
    /// <c>SimulatedSqlException.ResolveDiagnostics</c>) — the public contract
    /// stays get-only, matching <c>SqlError</c>.
    /// </summary>
    public int LineNumber { get; internal set; }

    /// <summary>The message text. Mirrors <c>SqlError.Message</c>.</summary>
    public string Message { get; }

    /// <summary>Error number. <c>0</c> for <c>PRINT</c>; <c>50000</c> for inline-string <c>RAISERROR</c>. Mirrors <c>SqlError.Number</c>.</summary>
    public int Number { get; }

    /// <summary>
    /// Name of the stored procedure or trigger generating the message, or
    /// empty string. Schema-qualified (<c>dbo.p1</c>) when an error fires
    /// inside a procedure body, matching real SqlClient (probe-confirmed).
    /// Mirrors <c>SqlError.Procedure</c>; the <c>internal set</c> lets the
    /// dispatch loop stamp the enclosing procedure once known, keeping the
    /// public contract get-only.
    /// </summary>
    public string Procedure { get; internal set; }

    /// <summary>
    /// Name of the server. Matches <see cref="SimulatedDbConnection.DataSource"/>,
    /// mirroring real SqlClient — <c>SqlError.Server</c> reports the connection's
    /// data source, not the server's <c>@@SERVERNAME</c> (probe-confirmed).
    /// Mirrors <c>SqlError.Server</c>.
    /// </summary>
    public string Server { get; }

    /// <summary>Provider identifier (<c>"SqlServerSimulator"</c>). Mirrors <c>SqlError.Source</c>.</summary>
    public string Source { get; }

    /// <summary>State of the message (1 for <c>PRINT</c>; the <c>RAISERROR</c> state argument otherwise). Mirrors <c>SqlError.State</c>.</summary>
    public byte State { get; }

    /// <summary>Returns <see cref="Message"/>, matching <c>SqlError.ToString()</c>'s shape.</summary>
    public override string ToString() => this.Message;
}
