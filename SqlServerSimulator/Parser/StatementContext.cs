namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-statement scratch. One <see cref="StatementContext"/> instance is
/// allocated per batch (stored on <see cref="BatchContext.CurrentStatement"/>)
/// and overwritten in place by the dispatch loop at the top of each
/// statement iteration. Late-bound expressions
/// (<see cref="Expressions.CurrentTimeFunction"/>) read through
/// <see cref="Simulation.ActiveBatch"/> + <see cref="BatchContext.CurrentStatement"/>
/// at runtime so a parsed expression reused across batches (e.g. a column
/// default's <c>getutcdate()</c>) resolves against the *executing*
/// statement's frame rather than a long-frozen capture.
/// </summary>
/// <remarks>
/// Today it's a one-field frame. Future statement-scoped concerns (a
/// <c>TRY ... CATCH</c> error slot, an EXEC return-value slot, nested
/// statement frames for stored-proc calls) land here without needing to
/// invent a new scope.
/// </remarks>
internal sealed class StatementContext
{
    /// <summary>
    /// UTC timestamp captured at the top of each top-level statement and
    /// consumed by the current-time scalar functions (<c>GETDATE</c>,
    /// <c>GETUTCDATE</c>, <c>SYSDATETIME</c>, <c>SYSUTCDATETIME</c>,
    /// <c>SYSDATETIMEOFFSET</c>, <c>CURRENT_TIMESTAMP</c>). Real SQL Server
    /// freezes these within a statement (probe-confirmed 2026-05-09 — two
    /// <c>SYSDATETIME()</c> calls in one SELECT return identical values to
    /// the 7th decimal digit; an UPDATE that stamps every row with
    /// <c>SYSDATETIME()</c> writes the same value into all rows). The
    /// simulator follows by capturing once per statement and serving every
    /// call within that statement from the same snapshot. The simulator
    /// does no local-time conversion: per the Azure SQL Database default,
    /// local-time-returning variants (<c>GETDATE</c> / <c>SYSDATETIME</c> /
    /// <c>CURRENT_TIMESTAMP</c>) and UTC-returning variants share this
    /// single UTC instant, and <c>SYSDATETIMEOFFSET</c> reports a
    /// <c>+00:00</c> offset.
    /// </summary>
    public DateTime UtcNow;
}
