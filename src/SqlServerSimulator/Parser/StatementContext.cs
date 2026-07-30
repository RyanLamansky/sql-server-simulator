namespace SqlServerSimulator.Parser;

/// <summary>
/// Per-statement scratch. One <see cref="StatementContext"/> instance is
/// allocated per batch (stored on <see cref="BatchContext.CurrentStatement"/>)
/// and overwritten in place by the dispatch loop at the top of each
/// statement iteration. Late-bound expressions
/// (<see cref="Expressions.CurrentTimeFunction"/>) read through
/// <see cref="RuntimeContext.Batch"/>'s <see cref="BatchContext.CurrentStatement"/>
/// at runtime so a parsed expression reused across batches (e.g. a column
/// default's <c>getutcdate()</c>) resolves against the *executing*
/// statement's frame rather than a long-frozen capture.
/// </summary>
/// <remarks>
/// Statement-scoped concerns (a <c>TRY ... CATCH</c> error slot, an EXEC
/// return-value slot, nested statement frames for stored-proc calls) land
/// here without needing to invent a new scope.
/// </remarks>
internal sealed class StatementContext
{
    /// <summary>
    /// Per-statement-execution values for expressions that freeze once per
    /// statement execution — the <c>RAND()</c> call-site family — keyed by
    /// expression instance (reference identity). Cleared by the dispatch loop
    /// at the top of each statement iteration alongside the
    /// <see cref="UtcNow"/> refresh, so a re-executed statement (WHILE-loop
    /// body, plan-cache replay under a fresh batch) draws fresh values while
    /// every call within one execution reuses its call site's value. Lives
    /// here — not on the expression — because a plan-cached <c>Selection</c>
    /// shares its tree across command executions.
    /// </summary>
    public Dictionary<Expression, Storage.SqlValue>? StatementScopedValues;

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

    /// <summary>
    /// 1-based line within the batch where this statement started (taken
    /// from <see cref="Token.LineNumber"/> of the leading token at dispatch
    /// time). Used as the default for <c>ERROR_LINE()</c> when an error fires
    /// inside this statement: the exception itself doesn't carry a line so
    /// the statement-start line is the closest approximation available.
    /// </summary>
    public int StartLine;

    /// <summary>
    /// Latched once an <c>IGNORE_DUP_KEY</c> index or constraint has made this
    /// statement skip a duplicate row, so the severity-0 Msg 3604 rides the
    /// info-message stream exactly once however many rows were dropped —
    /// probe-confirmed against real, which emits one message for three skipped
    /// rows and none at all when nothing was skipped. Per statement rather than
    /// per batch because that is the scope real resets it at.
    /// See <c>docs/claude/constraints.md</c>.
    /// </summary>
    public bool ReportedIgnoredDuplicate;

    /// <summary>
    /// 0-based character offset within the batch text where this statement's
    /// leading token starts (taken from <see cref="Token.StartIndex"/> of the
    /// leading token at dispatch time). The <c>CREATE</c> / <c>ALTER</c>
    /// handlers use it as the start of the module-definition slice they store
    /// for <c>OBJECT_DEFINITION</c> / <c>sys.sql_modules</c>. Points at the
    /// verb keyword itself (any leading whitespace / comment trivia the
    /// tokenizer already skipped is not included — a documented cosmetic
    /// divergence from SQL Server, which keeps leading trivia in the stored
    /// definition).
    /// </summary>
    public int StartIndex;

    /// <summary>
    /// Set true by a statement whose end-of-dispatch <c>@@ERROR</c> value
    /// should survive the dispatch wrapper's "successful statement clears
    /// <c>@@ERROR</c> to 0" rule. Used by <c>RAISERROR ... WITH SETERROR</c>
    /// at severities ≤ 10: the statement didn't throw (informational
    /// severities don't raise), but <c>WITH SETERROR</c> still forces
    /// <c>@@ERROR</c> to <c>50000</c> for the next statement to observe
    /// (probe-confirmed against SQL Server 2025). Reset to false at the
    /// start of each statement iteration by the dispatch loop.
    /// </summary>
    public bool SuppressErrorReset;

    /// <summary>
    /// Whether this statement's leading keyword makes it row-returning — a
    /// <c>SELECT</c> (bare, CTE-prefixed, or parenthesized) or <c>VALUES</c>.
    /// Set at dispatch entry from the leading token. When such a statement
    /// fails under continue-on-error, real SQL Server has already sent the
    /// result-set metadata (COLMETADATA) before the error, so the in-process
    /// reader surfaces it positionally (the first <c>Read</c> throws, the
    /// reader survives to the next result set). A non-row-returning statement
    /// (INSERT / UPDATE / DELETE / DDL) has no such envelope, so its error
    /// surfaces eagerly when the reader advances onto it (at
    /// <c>ExecuteReader</c> or <c>NextResult</c>) — matching SqlClient and the
    /// way EF Core's no-OUTPUT modification batches, which never call
    /// <c>Read</c>, still observe the failure. Carried onto the emitted
    /// <c>SimulatedErrorOutcome</c>.
    /// </summary>
    public bool LeadingKeywordReturnsRows;
}
