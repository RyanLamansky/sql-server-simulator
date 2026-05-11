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
/// <param name="Number">SQL error number (e.g. 8134 for divide by zero, 50000 for RAISERROR).</param>
/// <param name="Message">The error message text as it'd appear in <c>ERROR_MESSAGE()</c>.</param>
/// <param name="Severity">Severity class (1-25). Most simulator-emitted errors are class 16.</param>
/// <param name="State">Per-condition state code distinguishing factory call sites.</param>
/// <param name="Line">1-based line within the batch where the failing statement started.</param>
/// <param name="Procedure">Stored procedure name or NULL — always NULL today (procs unmodeled).</param>
internal readonly record struct CaughtError(int Number, string Message, byte Severity, byte State, int Line, string? Procedure);
