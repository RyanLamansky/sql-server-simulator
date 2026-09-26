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
    /// Per-statement results of subquery plans that proved outer-independent on
    /// their first execution, keyed by the consuming expression instance
    /// (reference identity); see <see cref="UncorrelatedSubqueryCache"/> for the
    /// entry shapes and the sentinel that marks a site as needing per-row
    /// execution. Cleared by the dispatch loop at the top of each statement
    /// iteration alongside the <see cref="UtcNow"/> refresh — the statement is
    /// the scope over which the data a subquery reads is fixed. Lives here —
    /// not on the expression — because a plan-cached <c>Selection</c> shares its
    /// tree across concurrent command executions.
    /// </summary>
    public Dictionary<object, object>? SubqueryResults;

    /// <summary>
    /// Existing rows' key tuples for each unique index or constraint whose key
    /// names a <em>non-persisted computed</em> column, keyed by the index /
    /// constraint instance (reference identity). Such a key can't be seeked —
    /// the per-<c>Heap</c> seek cache indexes stored bytes and the value has no
    /// storage slot — so the set is built from one scan and probed per row,
    /// which is what keeps a multi-row INSERT linear rather than re-scanning
    /// the table for every row. Cleared by the dispatch loop at the top of each
    /// statement iteration: the statement is the span over which the caller
    /// owns every write to the heap, and each row it admits is added here as it
    /// goes.
    /// </summary>
    public Dictionary<object, HashSet<SqlValueKey>>? ComputedUniqueKeys;

    /// <summary>
    /// How many rows the FOR JSON / FOR XML clause that finished last
    /// serialized, which a SELECT statement streaming its document reports as
    /// its row count (see <see cref="Selection.CountsForClauseSourceRows"/>).
    /// </summary>
    public int ForClauseSourceRows;

    /// <summary>
    /// Fully-drained catalog-view rows, keyed by the view and the database it
    /// was scoped to (both by reference identity), so every later read of the
    /// same view within the statement is served from here rather than
    /// regenerating it. The scope is the statement because that is the span
    /// over which a metadata view's content is fixed: DDL runs as its own
    /// statement, and the session identity the visibility filter reads can't
    /// change mid-statement either.
    /// <para>
    /// This is what makes a correlated body affordable. A <c>CROSS APPLY</c>
    /// or scalar subquery that reads a catalog view re-executes its plan per
    /// outer row — correctly, since it is correlated — but the view inside it
    /// is not correlated, and regenerating it each time is the whole cost:
    /// <c>sys.columns</c> over a 300-table database takes ~10 ms to project,
    /// which over 4,300 outer rows is 45 seconds of repeated identical work.
    /// </para>
    /// <para>
    /// Only populated once a sequence is drained to completion, so a
    /// <c>TOP 1</c> read still streams and stops early instead of paying to
    /// materialize the whole view. Cleared by the dispatch loop at the top of
    /// each statement iteration alongside <see cref="UtcNow"/>. Lives here —
    /// not on the view — because a <see cref="Schemas.CatalogView"/> is
    /// registered process-wide and shared by every concurrent session.
    /// </para>
    /// </summary>
    public Dictionary<(Schemas.CatalogView View, Database Database), byte[][]>? CatalogViewRows;

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
    /// <para>
    /// Seeded at construction rather than left at <see cref="DateTime"/>'s
    /// default: a body batch that never reaches the dispatch loop (a view or
    /// inline-TVF body, which is parsed and executed directly) would otherwise
    /// serve <c>0001-01-01</c> to every current-time call — a value outside
    /// legacy <c>datetime</c>'s range, so <c>GETDATE()</c> raised Msg 242
    /// instead of returning a time. Such bodies overwrite this with the
    /// referencing statement's own freeze via
    /// <see cref="BatchContext.AdoptStatementFreezeFrom"/>; the seed is the
    /// floor for any batch that inherits nothing.
    /// </para>
    /// </summary>
    public DateTime UtcNow = DateTime.UtcNow;

    /// <summary>
    /// The id of the autocommit transaction this statement runs in when the
    /// session has no user transaction, drawn from the server-wide counter on
    /// first read and 0 until then; cleared alongside the <see cref="UtcNow"/>
    /// refresh, so each autocommit statement reports an id of its own.
    /// </summary>
    public long AutocommitTransactionId;

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
    /// Latched once a full-text predicate in this statement has reported the
    /// words it ignored (Msg 9927), which real does once per statement however
    /// many rows the predicate evaluates.
    /// </summary>
    public bool ReportedNoiseWords;

    /// <summary>
    /// Latched when an aggregate in this statement skipped a NULL input while
    /// <c>ANSI_WARNINGS</c> was on. Real then sends Msg 8153 once, after the
    /// statement's rows (probed 2026-09-23), so the dispatch loop reads this
    /// only once the statement's outcomes have been yielded.
    /// </summary>
    public bool NullEliminated;

    /// <summary>
    /// The class-0 notices an absorbed arithmetic fault owes this statement
    /// (<see cref="BatchContext.AbsorbsArithmeticFault"/>) — Msg 3606 and / or
    /// 3607, each once, in the order they first occurred; zero when unused.
    /// Real sends them after the statement's rows, as it does Msg 8153.
    /// </summary>
    public int FirstArithmeticNotice, SecondArithmeticNotice;

    /// <summary>
    /// Set by a statement that writes rows — <c>INSERT</c>, <c>UPDATE</c>,
    /// <c>DELETE</c>, <c>MERGE</c>, <c>SELECT … INTO</c>, and the rewrite
    /// <c>ALTER TABLE … ALTER COLUMN</c> runs — once it begins executing.
    /// An execution error it then ends with is followed by Msg 3621
    /// (see <c>Simulation.IsStatementTerminationNoticed</c>).
    /// </summary>
    public bool WritesRows;

    /// <summary>
    /// Set in skip mode when a FROM source names an object that doesn't exist,
    /// so the statement parsed over a placeholder. Real binds none of such a
    /// statement until it runs, so a binder error it raises defers with it.
    /// </summary>
    public bool BindsDeferredSource;

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
    /// (probe-confirmed against SQL Server 2025). An <c>EXEC</c> of a procedure
    /// or of dynamic SQL sets it too: <c>@@ERROR</c> after one reads whatever
    /// the body's last statement left (probed 2026-09-24). Reset to false at
    /// the start of each statement iteration by the dispatch loop.
    /// </summary>
    public bool SuppressErrorReset;

    /// <summary>
    /// DDL events this statement raised, appended by the statement's own
    /// processor once its work succeeded and drained by the dispatch loop,
    /// which fires the matching database-scope DDL triggers. Null until a DDL
    /// statement records something; reset at the top of each statement
    /// iteration alongside <see cref="UtcNow"/>. Statement-scoped because the
    /// events belong to one statement's completion and the text span
    /// <see cref="StartIndex"/> anchors is that statement's.
    /// </summary>
    public List<DdlEventInfo>? PendingDdlEvents;

    /// <summary>
    /// Object id of a database-scope DDL trigger this statement <em>created</em>,
    /// excluded from its own statement's fire set: real doesn't run a brand-new
    /// trigger for the <c>CREATE TRIGGER</c> that made it, though a sibling
    /// trigger does see the <c>CREATE_TRIGGER</c> event (probe-confirmed).
    /// An <c>ALTER</c> leaves this null, because the trigger already existed —
    /// which is why real does run the replaced body for its own
    /// <c>ALTER_TRIGGER</c>.
    /// </summary>
    public int? DdlTriggerCreatedThisStatement;

    /// <summary>
    /// The statement name Msg 1934 echoes when a SET-option gate rejects an
    /// expression buried inside the statement rather than the statement's own
    /// target — an XML data-type method is the case that needs it. Real names
    /// the enclosing DML verb (<c>INSERT … SELECT @x.value(…)</c> reports
    /// <c>INSERT</c>) and falls back to <c>SELECT</c> everywhere else,
    /// including a bare <c>SET @i = @x.value(…)</c> (probe-confirmed). Set at
    /// dispatch entry from the leading token; the gates whose statement is
    /// unambiguous (DML targets, CREATE TABLE / INDEX) pass their own verb
    /// instead of reading this.
    /// </summary>
    public string StatementVerb = "SELECT";
}
