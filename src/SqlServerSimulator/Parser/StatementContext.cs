using System.Runtime.CompilerServices;
using SqlServerSimulator.Storage;

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
    /// execution. A deferred FROM source's materialized rows share it, keyed by
    /// the source's plan (<c>Selection.StatementMaterializedRows</c>). Cleared by the dispatch loop at the top of each statement
    /// iteration alongside the <see cref="UtcNow"/> refresh — the statement is
    /// the scope over which the data a subquery reads is fixed. Lives here —
    /// not on the expression — because a plan-cached <c>Selection</c> shares its
    /// tree across concurrent command executions.
    /// </summary>
    public Dictionary<object, object>? SubqueryResults;

    /// <summary>
    /// The values this statement has drawn, in draw order, from each sequence
    /// its <c>NEXT VALUE FOR … OVER (ORDER BY …)</c> references read: the row
    /// ranked k takes the k-th, so the draws follow the <c>OVER</c> ordering
    /// whatever order the rows are projected in. Keyed by sequence because
    /// every reference to one sequence in a statement writes the same
    /// <c>OVER</c> (Msg 11727). Cleared with the statement's other per-run
    /// caches.
    /// </summary>
    public Dictionary<Schemas.Sequence, List<Storage.SqlValue>>? OrderedSequenceDraws;

    /// <summary>
    /// The heap addresses of the rows this statement's scans and seeks have
    /// handed out, while something in it reads a <see cref="RowLocator"/>;
    /// null otherwise, which is what keeps every producer's per-row cost at
    /// one hoisted null test. Installed by a plan whose query reads a locator
    /// as its execution starts (<see cref="Selection.InstallsRowAddresses"/>)
    /// and by <see cref="Selection.ExecuteWithRowAddresses"/>; cleared with the
    /// statement's other per-run caches.
    /// </summary>
    public RowAddressMap? RowAddresses;

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

#if DEBUG
    /// <summary>
    /// The cached catalog rowsets this statement has already checked against a
    /// fresh generation (<see cref="Schemas.CatalogRowCache"/>), so the Debug
    /// audit runs once per rowset per statement rather than once per read.
    /// </summary>
    public HashSet<Schemas.CatalogRowSet>? AuditedCatalogRowSets;
#endif

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
    /// The statement's <c>WITH CHANGE_TRACKING_CONTEXT (…)</c> value, which
    /// every change-tracked row it writes carries; null without the clause.
    /// A trigger's statements run in their own frame, so they don't inherit it.
    /// </summary>
    public byte[]? ChangeTrackingContext;

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
    /// <see cref="BatchContext.CountedStatementLine"/> as this statement began:
    /// the line real reports an <c>OPEN</c> or <c>FETCH</c> of a missing cursor
    /// or an unallocated cursor variable at (probed 2026-09-29 against SQL
    /// Server 2025).
    /// </summary>
    public int PriorStatementLine;

    /// <summary>
    /// Per table, the row and key locks this statement has taken there, which
    /// is what real's lock escalation counts: a statement, not a transaction,
    /// crossing the threshold escalates (probed 2026-09-28 against SQL Server
    /// 2025 — two SERIALIZABLE reads of 4 000 keys each in one transaction
    /// keep all 8 001 key locks). Cleared by the dispatch loop at the top of
    /// each statement iteration.
    /// </summary>
    public Dictionary<Storage.HeapTable, LockEscalationTally>? LockTallies;

    /// <summary>
    /// Tables this statement escalated outside a transaction, with the table
    /// mode taken — inside one the transaction records it, since the lock
    /// outlives the statement there.
    /// </summary>
    public Dictionary<Storage.HeapTable, Storage.LockMode>? EscalatedTables;

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
    /// (<see cref="BatchContext.AbsorbsArithmeticFault"/>) — Msg 3606 for an
    /// overflow, 3607 for a division by zero, each once. Real sends them after
    /// the statement's rows, as it does Msg 8153, and always 3606 first
    /// whichever fault came first (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool OwesOverflowNotice, OwesDivideByZeroNotice;

    /// <summary>
    /// Set by a statement that writes rows — <c>INSERT</c>, <c>UPDATE</c>,
    /// <c>DELETE</c>, <c>MERGE</c>, <c>SELECT … INTO</c>, and the rewrite
    /// <c>ALTER TABLE … ALTER COLUMN</c> runs — once it begins executing.
    /// An execution error it then ends with is followed by Msg 3621
    /// (see <c>Simulation.IsStatementTerminationNoticed</c>).
    /// </summary>
    public bool WritesRows;

    /// <summary>
    /// Set by <c>WRITETEXT</c> and <c>UPDATETEXT</c> once they reach their
    /// pointer's value: an error after that — the value, an offset, the copy
    /// form's source, the bulk form's data — aborts as under
    /// <c>XACT_ABORT</c> and is followed by Msg 3621 at line 1, and one a
    /// <c>TRY</c> catches leaves the statement's DONE without a count (probed
    /// 2026-10-07 against SQL Server 2025).
    /// </summary>
    public bool WritesText;

    /// <summary>
    /// The kind real names in this statement's own DONE token
    /// (<see cref="StatementDoneKind"/>), read off its leading tokens when it
    /// starts and refined by a parser that learns more — a <c>DECLARE</c>'s
    /// initializer, a <c>SET</c>'s option, a <c>RETURN</c>'s value.
    /// </summary>
    public ushort DoneKind;

    /// <summary>
    /// The count that DONE reports, or -1 for none: 1 for a <c>SET</c> or
    /// <c>DECLARE</c> assigning a variable and a procedure's valued
    /// <c>RETURN</c>, which real counts as one row.
    /// </summary>
    public int DoneCount;

    /// <summary>
    /// The shape of the result set a row-writing statement's <c>OUTPUT</c>
    /// clause returns to the client, recorded as the clause parses; null when
    /// it has none or directs its rows <c>INTO</c> a table.
    /// </summary>
    public (SqlType[] Schema, string[] Names)? ClientOutputShape;

    /// <summary>
    /// Whether this is a <c>SELECT</c> sending its rows to the client — neither
    /// assigning variables nor directing them <c>INTO</c> a table — set as it
    /// starts executing.
    /// </summary>
    public bool SendsRows;

    /// <summary>
    /// Set while a statement writing a table (not a table variable) runs,
    /// inside the transaction real opens for it: <c>@@TRANCOUNT</c> read
    /// there counts that one too, so an auto-commit <c>INSERT … VALUES
    /// (@@TRANCOUNT)</c> stores 2 and one under a single <c>BEGIN TRAN</c>
    /// stores 2 as well (probed 2026-09-26 against SQL Server 2025).
    /// </summary>
    public bool TransactedWrite;

    /// <summary>
    /// The statement's write to a linked server's table, which runs against a
    /// local stand-in and is replayed on the server once the statement's own
    /// work succeeds (<see cref="RemoteWrite.Replay"/>).
    /// </summary>
    public RemoteWrite? RemoteWrite;

    /// <summary>
    /// The alias an UPDATE or DELETE names as its target ahead of a FROM
    /// clause, so a four-part source carrying it becomes the statement's
    /// <see cref="RemoteWrite"/> rather than a read.
    /// </summary>
    public string? RemoteWriteAlias;

    /// <summary>The statement <see cref="RemoteWriteAlias"/> belongs to.</summary>
    public RemoteWriteKind RemoteWriteAliasKind;

    /// <summary>
    /// Set as the statement compiles something real opens a transaction for
    /// even when nothing is written — a FROM source other than a derived table
    /// or <c>VALUES</c>, a function or sequence object, a metadata, security
    /// or session built-in, a CLR type — which <c>XACT_STATE()</c> reads as 1
    /// while <c>@@TRANCOUNT</c> stays 0 (probed 2026-09-28 against SQL Server
    /// 2025). Published to <see cref="TransactionMark"/> once an
    /// <c>XACT_STATE()</c> call has taken one.
    /// </summary>
    public bool OpensTransaction;

    /// <summary>
    /// Set when the statement opened the session's transaction under
    /// <c>SET IMPLICIT_TRANSACTIONS ON</c>; an error real raises compiling the
    /// statement then takes the transaction back with it, since real opens it
    /// only once the statement compiled.
    /// </summary>
    public bool BeganImplicitTransaction;

    /// <summary>
    /// Set as the statement calls a user function, which opens an implicit
    /// transaction only once a query holding the call has parsed — a
    /// <c>SET</c>, a <c>DECLARE</c> initializer or a <c>SELECT</c> assigning
    /// variables with no FROM calls one outside any (probed 2026-09-28 against
    /// SQL Server 2025).
    /// </summary>
    public bool CallsUserFunction;

    /// <summary>
    /// The mark the statement's <c>XACT_STATE()</c> calls read, allocated by
    /// the first of them, so a mark met later in the statement's text — the
    /// FROM clause after the select list — still reaches them, and a cursor
    /// or a cached plan re-running the expression reads what its own
    /// statement compiled.
    /// </summary>
    public StatementTransactionMark? TransactionMark;

    /// <summary>Records one of the constructs <see cref="OpensTransaction"/> lists.</summary>
    public void MarkOpensTransaction()
    {
        this.OpensTransaction = true;
        if (this.TransactionMark is { } mark)
            mark.Opens = true;
    }

    /// <summary>The mark an <c>XACT_STATE()</c> call reads, shared by the statement's calls.</summary>
    public StatementTransactionMark TakeTransactionMark() => this.TransactionMark ??= new() { Opens = this.OpensTransaction };

    /// <summary>
    /// Set in skip mode when a FROM source names an object that doesn't exist,
    /// so the statement parsed over a placeholder. Real binds none of such a
    /// statement until it runs, so a binder error it raises defers with it.
    /// </summary>
    public bool BindsDeferredSource;

    /// <summary>
    /// Set in skip mode when a statement whose write target doesn't exist was
    /// read to its end before raising its deferred name-resolution error, so a
    /// compile walk resumes at the statement after it rather than stopping
    /// there, as real parses the whole batch before deferring any of it.
    /// </summary>
    public bool DeferredReadToEnd;

    /// <summary>
    /// Set by <c>OPTION (USE HINT ('DISABLE_TSQL_SCALAR_UDF_INLINING'))</c>,
    /// which keeps the statement's scalar function calls from inlining (see
    /// <see cref="InlinedScalarCalls"/>).
    /// </summary>
    public bool DisablesScalarUdfInlining;

    /// <summary>
    /// Set by <c>OPTION (RECOMPILE)</c>, which compiles the statement again
    /// every time it runs (see <see cref="InlinedScalarCalls"/>).
    /// </summary>
    public bool Recompiles;

    /// <summary>
    /// Set as the statement resolves a table variable, which real compiles
    /// again when the statement first runs, once it knows the variable's rows
    /// (see <see cref="InlinedScalarCalls"/>).
    /// </summary>
    public bool ReadsTableVariable;

    /// <summary>
    /// Set as the statement resolves a permanent table or view, and as it
    /// resolves a <c>#temp</c> table or a table variable; read through
    /// <see cref="FoldsConstantsAtCompile"/>.
    /// </summary>
    public bool ReadsPermanentObject, ReadsTemporaryObject;

    /// <summary>
    /// Whether real compiles this statement with its batch, folding its
    /// written constants then: one naming permanent tables or views and no
    /// temporary object, whose compilation isn't deferred to run time.
    /// </summary>
    public bool FoldsConstantsAtCompile() => this.ReadsPermanentObject && !this.ReadsTemporaryObject;

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
    /// How to reverse what this statement's DDL changed, recorded outside a
    /// transaction while a DDL trigger could fire for it
    /// (<c>Simulation.RecordDdlUndo</c>). The triggers' auto-commit unit takes
    /// them first, so a body's error or <c>ROLLBACK</c> undoes the DDL with
    /// the body's own writes, as real's does (probed 2026-10-06 against SQL
    /// Server 2025).
    /// </summary>
    public List<Action>? AutocommitDdlUndo;

    /// <summary>
    /// A refusal real settles as it compiles the statement — a DML
    /// <c>TOP</c>'s of its written constant, a <c>NEXT VALUE FOR</c>'s in a
    /// nested query — met in skip mode and raised once the statement has
    /// parsed, unless the statement binds with a source the batch has yet to
    /// create (<see cref="BindsDeferredSource"/>) and so compiles only as it
    /// runs (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    public SimulatedSqlException? PendingCompileRefusal;

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

    /// <summary>
    /// The statement changes a table's or index's structure — ALTER TABLE,
    /// CREATE / ALTER / DROP INDEX, CREATE / UPDATE STATISTICS, DROP TABLE, TRUNCATE
    /// TABLE — whose severity-16 run-time errors end the batch and roll the
    /// transaction back as under <c>SET XACT_ABORT ON</c> (probed 2026-09-26
    /// against SQL Server 2025), save a statistic's computed key failing to
    /// evaluate (<c>SimulatedSqlException.RaisedBuildingStatistics</c>). Set at
    /// dispatch entry.
    /// </summary>
    public bool ChangesTableStructure;

    /// <summary>
    /// Clears what a statement latches as it runs for its dispatch to read
    /// once it ends — once per dispatch of a statement, before its first token
    /// is read. A statement nested in this one (an <c>IF</c>'s branch, a
    /// block's body) clears them again as it starts.
    /// </summary>
    public void BeginDispatch()
    {
        this.SuppressErrorReset = false;
        this.ReportedIgnoredDuplicate = false;
        this.ReportedNoiseWords = false;
        this.NullEliminated = false;
        this.OwesOverflowNotice = this.OwesDivideByZeroNotice = false;
        this.WritesRows = false;
        this.WritesText = false;
        this.ClientOutputShape = null;
        this.SendsRows = false;
        this.TransactedWrite = false;
        this.BindsDeferredSource = false;
        this.DeferredReadToEnd = false;
        this.DisablesScalarUdfInlining = false;
        this.Recompiles = false;
        this.ReadsTableVariable = false;
        this.ReadsPermanentObject = this.ReadsTemporaryObject = false;
        this.OpensTransaction = false;
        this.BeganImplicitTransaction = false;
        this.CallsUserFunction = false;
        this.TransactionMark = null;
        this.PendingDdlEvents = null;
        this.AutocommitDdlUndo = null;
        this.PendingCompileRefusal = null;
        this.DdlTriggerCreatedThisStatement = null;
        this.ProbedTable = null;
    }

    /// <summary>
    /// The row a <c>READ COMMITTED</c> read of the statement probed last
    /// (<c>BatchContext.TouchRowForRead</c>), with <see cref="ProbedPage"/>
    /// and <see cref="ProbedSlot"/>, and how many it has probed: a
    /// <c>SELECT</c> suspended on its client holds that row's S while it
    /// waits when the row is where its scan stands (see
    /// <see cref="ResultStream"/>).
    /// </summary>
    public Storage.HeapTable? ProbedTable;

    public int ProbedPage, ProbedSlot;

    public long RowsProbed;

    /// <summary>
    /// How many times a <c>SELECT</c> of the batch has waited on its client
    /// mid-result (<see cref="ResultStream"/>): a scan that sees it move reads
    /// on as the table stands after another request's or session's writes.
    /// Only ever counts up.
    /// </summary>
    public int Suspensions;

    /// <summary>
    /// The rows a <c>SELECT</c> left to produce as its client reads them,
    /// which the dispatch loop takes over once the statement has sent its
    /// result set (see <see cref="ResultStream"/>); null for any other
    /// statement.
    /// </summary>
    public ResultStream? StreamingResult;

    /// <summary>
    /// Freezes the current time for one run of the statement's text and
    /// clears the caches that run fills — once per run, a re-read for the
    /// statement's whole binder report included.
    /// </summary>
    [MethodImpl(Tiering.OptimizeFirstCall)]
    public void BeginExecution()
    {
        this.UtcNow = DateTime.UtcNow;
        this.StatementScopedValues = null;
        this.SubqueryResults = null;
        this.OrderedSequenceDraws = null;
        this.RowAddresses = null;
        this.CatalogViewRows = null;
#if DEBUG
        this.AuditedCatalogRowSets = null;
#endif
        this.AutocommitTransactionId = 0;
        this.ChangeTrackingContext = null;
        this.LockTallies = null;
        this.EscalatedTables = null;
        this.RemoteWrite = null;
        this.RemoteWriteAlias = null;
    }
}

/// <summary>
/// Whether the statement an <c>XACT_STATE()</c> call was compiled in opens a
/// transaction for itself; see <see cref="StatementContext.OpensTransaction"/>.
/// </summary>
internal sealed class StatementTransactionMark
{
    public bool Opens;
}

/// <summary>
/// One statement's lock count on one table, and when escalation is next
/// attempted there.
/// </summary>
internal sealed class LockEscalationTally
{
    /// <summary>Row and key locks taken.</summary>
    public int Count;

    /// <summary>
    /// The estimated lock total at which escalation is next tried — real's
    /// first attempt comes at 6 250, and a refused one is retried every 1 250
    /// locks after (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    public int NextAttempt = FirstAttempt;

    /// <summary>
    /// Whether any lock counted is a U or X (or a range mode with one as its
    /// key part), which escalates to a table X rather than S.
    /// </summary>
    public bool Exclusive;

    /// <summary>
    /// Whether the statement holds key locks on the table's clustered key,
    /// which its row locks sit under: real holds one lock per key, so a row
    /// lock there adds nothing to the count.
    /// </summary>
    public bool RowsKeyLocked;

    /// <summary>The estimated total real's first escalation attempt comes at.</summary>
    public const int FirstAttempt = 6250;

    /// <summary>How many more locks a refused attempt waits for.</summary>
    public const int RetryInterval = 1250;
}
