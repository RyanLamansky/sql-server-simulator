namespace SqlServerSimulator.Parser;

/// <summary>
/// Captured error info for a <c>SimulatedSqlException</c> that the
/// <c>BEGIN TRY ... END TRY BEGIN CATCH ... END CATCH</c> dispatch wrapper
/// intercepted. Lives on <see cref="BatchContext.InFlightError"/> while the
/// matching CATCH body runs; drives <c>ERROR_NUMBER</c> /
/// <c>ERROR_MESSAGE</c> / <c>ERROR_SEVERITY</c> / <c>ERROR_STATE</c> /
/// <c>ERROR_LINE</c> / <c>ERROR_PROCEDURE</c> and the no-arg <c>THROW;</c>
/// re-raise (which reconstructs a <c>SimulatedSqlException</c> from these
/// fields).
/// </summary>
internal readonly struct CaughtError(int number, string message, byte severity, byte state, int line, string? procedure)
{
    /// <summary>SQL error number (e.g. 8134 for divide by zero, 50000 for RAISERROR).</summary>
    public readonly int Number = number;

    /// <summary>The error message text as it'd appear in <c>ERROR_MESSAGE()</c>.</summary>
    public readonly string Message = message;

    /// <summary>Severity class (1-25). Most simulator-emitted errors are class 16.</summary>
    public readonly byte Severity = severity;

    /// <summary>Per-condition state code distinguishing factory call sites.</summary>
    public readonly byte State = state;

    /// <summary>1-based line within the batch where the failing statement started.</summary>
    public readonly int Line = line;

    /// <summary>
    /// Stored procedure name or NULL — always NULL since the dispatch wrapper
    /// that populates this struct doesn't carry the current procedure name
    /// through. Procs themselves ship; <c>ERROR_PROCEDURE()</c> inside a CATCH
    /// that caught a body-fired error surfaces NULL.
    /// </summary>
    public readonly string? Procedure = procedure;
}
