namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// <b>Msg -2</b> — a <c>CommandTimeout</c> expiry. Like
    /// <see cref="CommandCancelled"/> this is manufactured client-side rather
    /// than sent by a server, and carries SqlClient's own wording (double space
    /// included) with Class 11 / State 0 — probe-confirmed against SqlClient
    /// 7.0.2 driving SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ExecutionTimeoutExpired() => new(ExecutionTimeoutExpiredMessage, -2, 11, 0);

    /// <summary>
    /// The client-side exception a cancelled command surfaces — <b>Msg 0</b>,
    /// not a server error: real SQL Server sends no error token for an
    /// attention, so SqlClient manufactures this from its own state and the
    /// simulator's in-process surface mirrors it (probe-confirmed against
    /// SqlClient 7.0.2 for a mid-execution <c>CancellationToken</c> and for
    /// <c>SqlCommand.Cancel()</c> alike; the wording, including its double
    /// space, is SqlClient's own).
    /// <para>Only the mid-execution case reaches here. A token already
    /// cancelled before execute, and one observed while draining an open
    /// reader, both surface <c>TaskCanceledException</c> from the ADO.NET base
    /// class on real and here alike.</para>
    /// </summary>
    internal static SimulatedSqlException CommandCancelled() => new(CommandCancelledMessage, 0, 11, 0);

    private const string ExecutionTimeoutExpiredMessage =
        "Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.";

    private const string CommandCancelledMessage =
        "A severe error occurred on the current command.  The results, if any, should be discarded.";

    /// <summary>
    /// The cancellation a running statement observes at a row-level safe point:
    /// <see cref="ExecutionTimeoutExpired"/> or <see cref="CommandCancelled"/>
    /// by cause, marked <see cref="IsAttention"/> so the dispatch loop ends the
    /// batch with it rather than handing it to a <c>CATCH</c>.
    /// </summary>
    internal static SimulatedSqlException Attention(bool timedOut) => timedOut
        ? new(ExecutionTimeoutExpiredMessage, -2, 11, 0) { IsAttention = true }
        : new(CommandCancelledMessage, 0, 11, 0) { IsAttention = true };

    /// <summary>
    /// <b>Msg 601</b> — real's error for a scan taking no locks (<c>NOLOCK</c>,
    /// <c>READ UNCOMMITTED</c>) that meets data a writer moved under it. The
    /// simulator raises it where such a read follows a row it read to off-row
    /// data freed since — the row's delete or update committed, or its insert
    /// rolled back, between the two reads — since it reclaims a superseded
    /// chain at commit where real defers to its ghost cleanup.
    /// </summary>
    internal static SimulatedSqlException NoLockScanDataMovement() =>
        new("Could not continue scan with NOLOCK due to data movement.", 601, 12, 3);

    /// <summary>
    /// Msg 3621 as it follows an attention that ended a write: always line 1
    /// and no procedure, wherever the write was — a procedure's or a dynamic
    /// batch's statement included (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static SimulatedError AttentionStatementTerminatedMessage(SimulatedDbConnection connection) =>
        new(@class: 0, lineNumber: 1, message: "The statement has been terminated.", number: 3621, procedure: "", server: connection.DataSource, source: "SqlServerSimulator", state: 0);

    /// <summary>
    /// Msg 3997: on a MARS connection a transaction a batch began by SQL text —
    /// <c>BEGIN TRANSACTION</c>, or <c>IMPLICIT_TRANSACTIONS</c> — is scoped to
    /// that batch, and one still open when the batch ends is rolled back with
    /// this, at line 1, whether or not another request was active (probed
    /// 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MarsBatchTransactionStillActive() =>
        new("A transaction that was started in a MARS batch is still active at the end of the batch. The transaction is rolled back.", 3997, 16, 1);

    /// <summary>
    /// Msg 3988: a transaction-manager begin arriving on a MARS connection
    /// while another of its requests is outstanding — one whose response the
    /// client hasn't read past the end of, however small — whatever
    /// transaction that one works in (probed 2026-09-30 and 2026-10-06
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NewTransactionWhileRequestsRunning() =>
        new("New transaction is not allowed because there are other threads running in the session.", 3988, 16, 1);

    /// <summary>
    /// Msg 3981: a commit ending the transaction (state 1) or a save point
    /// (state 2), by SQL text or a transaction-manager request, arriving on a
    /// MARS connection while another request working on that transaction is
    /// outstanding — one that began outside it doesn't count (probed
    /// 2026-10-06 against SQL Server 2025). It is transaction-aborting: the
    /// transaction rolls back and the rest of the batch doesn't run (probed
    /// 2026-09-30).
    /// </summary>
    internal static SimulatedSqlException TransactionOperationWithPendingRequests(byte state) =>
        new("The transaction operation cannot be performed because there are pending requests working on this transaction.", 3981, 16, state)
        {
            AbortsTransaction = true,
        };

    /// <summary>
    /// Msg 3989: a request arriving on a MARS connection while a request
    /// working on a transaction another request ended — by a Msg 3981 abort
    /// or a rollback — is still producing results (probed 2026-09-30 and
    /// 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException RequestWithoutValidTransactionDescriptor() =>
        new("New request is not allowed to start because it should come with valid transaction descriptor.", 3989, 16, 1);

    /// <summary>
    /// Msg 3980: a request arriving on a MARS connection while a DML
    /// statement's <c>OUTPUT</c> rows — an <c>INSERT</c>, <c>UPDATE</c>,
    /// <c>DELETE</c> or <c>MERGE</c>, directly or in a procedure — are still
    /// on their way to the client, which real can't interleave; a
    /// transaction-manager request is refused too, ahead of its own checks
    /// (probed 2026-10-06 against SQL Server 2025, which waits a few seconds
    /// before refusing, so a short <c>CommandTimeout</c> expires first there).
    /// </summary>
    internal static SimulatedSqlException RequestWhileSessionBusy() =>
        new("The request failed to run because the batch is aborted, this can be caused by abort signal sent from client, or another request is running in the same session, which makes the session busy.", 3980, 16, 1);

    /// <summary>
    /// Msg 15386: a request beginning on a MARS connection while another
    /// request's batch has changed the session's security context — an
    /// <c>EXECUTE AS</c>, <c>REVERT</c> or <c>SETUSER</c> outside a module —
    /// and is still running; the next request after it runs in the changed
    /// context (probed 2026-10-07 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException SecurityContextChangingInAnotherBatch() =>
        new("Another batch in the session is changing security context, new batch is not allowed to start.", 15386, 16, 1);

    /// <summary>
    /// Msg 1222 — fired when a lock acquisition exceeds the session's
    /// configured <c>@@LOCK_TIMEOUT</c>. Single, fixed wording regardless of
    /// the lock kind that timed out (probed against SQL Server 2025); the
    /// state names the kind: 51 for a key lock, 45 for a heap row's, 56 for a
    /// table or schema lock (probed 2026-09-28).
    /// </summary>
    internal static SimulatedSqlException LockRequestTimeOutExceeded(byte state) =>
        new("Lock request time out period exceeded.", 1222, 16, state);

    /// <summary>
    /// Mimics SQL Server error 8705 as a <c>MERGE</c> through an outer-join
    /// view raises it when an <c>UPDATE</c> action lands on a NULL-extended
    /// row of the written table — real's own wording, naming that table's
    /// clustered index (or heap) and object id, though no other transaction is
    /// involved (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MissingIndexEntryInDml(int indexId, int tableId, string databaseName) =>
        new($"A DML statement encountered a missing entry in index ID {indexId} of table ID {tableId}, in the database '{databaseName}' due to an interaction with another transaction. If you continue to see this error, consider using Repeatable Read or higher isolation level.", 8705, 16, 1);

    /// <summary>
    /// Msg 1205 — fired on the deadlock victim. The wording embeds the
    /// victim's session SPID (<see cref="SimulatedDbConnection.Spid"/>) verbatim;
    /// probe-confirmed against SQL Server 2025: the parenthesized number is
    /// the VICTIM's process id, not the survivor's. Class 13 (not 16 like
    /// every other lock-manager error) marks this as the
    /// "transaction-was-aborted, but the connection itself stays alive"
    /// signal — SqlClient surfaces it as a retriable transient error.
    /// </summary>
    /// <param name="victimSpid">The chosen victim's session id; threaded
    /// into the message wording at the <c>Process ID &lt;N&gt;</c> slot.</param>
    /// <param name="state">The kind of lock the victim waited on, as Msg 1222
    /// names it: 51 for a key, 45 for a heap's row (probed 2026-10-03 against
    /// SQL Server 2025).</param>
    /// <param name="waitsOnOwnThread">Whether the lock's holder runs on the requester's own thread (<see cref="WaitsOnOwnThread"/>).</param>
    internal static SimulatedSqlException TransactionDeadlocked(int victimSpid, byte state, bool waitsOnOwnThread = false) =>
        new($"Transaction (Process ID {victimSpid}) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", 1205, 13, state) { WaitsOnOwnThread = waitsOnOwnThread };

    /// <summary>
    /// Msg 1047 — raised when an unsupported combination of locking hints
    /// appears on the same source (e.g. <c>NOLOCK + XLOCK</c>,
    /// <c>NOLOCK + UPDLOCK</c>, <c>NOLOCK + HOLDLOCK</c>). Probe-confirmed
    /// verbatim wording against SQL Server 2025 (2026-05-14).
    /// </summary>
    internal static SimulatedSqlException ConflictingLockingHints() =>
        new("Conflicting locking hints specified.", 1047, 15, 1);

    /// <summary>
    /// Msg 1065 — raised when <c>WITH (NOLOCK)</c> or <c>WITH (READUNCOMMITTED)</c>
    /// appears on the target of an <c>INSERT</c> / <c>UPDATE</c> /
    /// <c>DELETE</c> / <c>MERGE</c>. Probe-confirmed verbatim wording. Real
    /// reports it at line 15 wherever the statement sits in the batch (probed
    /// 2026-09-26 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NoLockHintNotAllowedOnDmlTarget() =>
        new SimulatedSqlException("The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.", 1065, 15, 1)
            .PinLine(15);

    /// <summary>
    /// Msg 1069 — raised when an <c>INDEX(…)</c> / <c>FORCESEEK</c> /
    /// <c>FORCESCAN</c> hint appears on the target of an <c>INSERT</c> /
    /// <c>UPDATE</c> / <c>DELETE</c> / <c>MERGE</c>. Probe-confirmed verbatim
    /// wording: "Index hints are only allowed in a FROM or OPTION clause."
    /// </summary>
    internal static SimulatedSqlException IndexHintsOnlyInFromOrOption() =>
        new("Index hints are only allowed in a FROM or OPTION clause.", 1069, 15, 1);

    /// <summary>
    /// Msg 307 — raised when an <c>INDEX(N)</c> hint references an
    /// <c>index_id</c> that doesn't exist on the target table. Probe-confirmed
    /// verbatim wording against SQL Server 2025 (2026-05-14): the message
    /// embeds the (decimal) id and the qualified table name, both 1-part /
    /// 2-part qualified consistently with the <c>FROM</c>-clause spelling. The
    /// rejection only fires for <c>FROM</c>-source / JOIN-RHS positions
    /// because DML targets short-circuit on <see cref="IndexHintsOnlyInFromOrOption"/>
    /// (Msg 1069) before any per-index validation runs.
    /// </summary>
    internal static SimulatedSqlException IndexHintIdNotFound(int indexId, string qualifiedTableName) =>
        new($"Index ID {indexId} on table '{qualifiedTableName}' (specified in the FROM clause) does not exist.", 307, 16, 1);

    /// <summary>
    /// Msg 308 — raised when an <c>INDEX(name)</c> or <c>INDEX = name</c>
    /// hint references an index name that doesn't exist on the target table.
    /// Matched case-insensitively against PRIMARY KEY / UNIQUE constraint
    /// names plus the table's <c>Indexes</c> list. Probe-confirmed verbatim
    /// wording — same single-quote convention and "(specified in the FROM
    /// clause)" suffix as Msg 307.
    /// </summary>
    internal static SimulatedSqlException IndexHintNameNotFound(string indexName, string qualifiedTableName) =>
        new($"Index '{indexName}' on table '{qualifiedTableName}' (specified in the FROM clause) does not exist.", 308, 16, 1);

    /// <summary>
    /// Msg 362 — a nested <c>FORCESEEK(index (col [, …]))</c> named a column
    /// that isn't the index's key column at that position. Real reports the
    /// first offender and names the base table rather than the alias the query
    /// wrote; an <c>INCLUDE</c>d column and a key column out of order both land
    /// here. Probe-confirmed verbatim against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ForceSeekColumnNotAKeyColumn(string columnName, string tableName, string indexName) =>
        new(
            $"The query processor could not produce a query plan because the name '{columnName}' in the FORCESEEK hint on table or view '{tableName}' did not match the key column names of the index '{indexName}'.",
            362,
            16,
            1);

    /// <summary>
    /// Msg 365 — a nested <c>FORCESEEK</c> listed more seek columns than the
    /// named index has key columns. Probe-confirmed to be settled ahead of
    /// <see cref="ForceSeekColumnNotAKeyColumn"/>, so a list that is both too
    /// long and misspelled reports the count.
    /// </summary>
    internal static SimulatedSqlException ForceSeekTooManySeekColumns(string tableName, string indexName) =>
        new(
            $"The query processor could not produce a query plan because the FORCESEEK hint on table or view '{tableName}' specified more seek columns than the number of key columns in index '{indexName}'.",
            365,
            16,
            1);

    /// <summary>
    /// Msg 8622 — a <c>FORCESEEK</c> (or an <c>INDEX</c> hint beside one) that
    /// no predicate of the query can seek on: no predicate at all, one on an
    /// unindexed or non-leading column, one wrapping the column in a function,
    /// a <c>LIKE</c> leading with a wildcard, an <c>OR</c> with an unseekable
    /// branch, a heap or columnstore table — and an <c>OPTION (HASH GROUP)</c>
    /// over a grouped CLR aggregate. Raised while the batch compiles, so
    /// no statement runs and no <c>TRY</c> in the batch catches it (probed
    /// 2026-09-28 against SQL Server 2025).
    /// </summary>
    /// <param name="state">
    /// 1 for a plan no hinted access path or join algorithm can build; 2 for
    /// an <c>INDEX</c> hint naming index 0 beside another index (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </param>
    internal static SimulatedSqlException ForceSeekPlanInfeasible(byte state = 1) =>
        new("Query processor could not produce a query plan because of the hints defined in this query. Resubmit the query without specifying any hints and without using SET FORCEPLAN.", 8622, 16, state);

    /// <summary>
    /// Msg 364 — a <c>FORCESEEK</c> naming an index and its seek columns on a
    /// view read without <c>NOEXPAND</c>, whose index it can't name (probed
    /// 2026-10-07 against SQL Server 2025). Raised while the batch compiles,
    /// as Msg 8622 is.
    /// </summary>
    internal static SimulatedSqlException ForceSeekOnViewWithoutNoExpand(string viewName) =>
        new($"The query processor could not produce a query plan because the FORCESEEK hint on view '{viewName}' is used without a NOEXPAND hint. Resubmit the query with the NOEXPAND hint or remove the FORCESEEK hint on the view.", 364, 16, 1);

    /// <summary>
    /// Msg 10749 — <c>FORCESEEK(0 (col))</c>, the seek naming the heap or
    /// clustered scan (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForceSeekOnIndexZero() =>
        new("The FORCESEEK hint cannot be used with index 0. Correct the index provided to the FORCESEEK hint and resubmit the query.", 10749, 16, 1);

    /// <summary>
    /// Msg 309 — an <c>INDEX</c> hint naming an XML index (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException XmlIndexInHint(string indexName, string qualifiedTableName) =>
        new($"Cannot use index \"{indexName}\" on table \"{qualifiedTableName}\" in a hint. XML indexes are not allowed in hints.", 309, 16, 1);

    /// <summary>
    /// Msg 315 — an <c>INDEX</c> hint naming a disabled index (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException IndexHintNameDisabled(string indexName, string qualifiedTableName) =>
        new($"Index \"{indexName}\" on table \"{qualifiedTableName}\" (specified in the FROM clause) is disabled or resides in a filegroup which is not online.", 315, 16, 1);

    /// <summary>
    /// Msg 316 — an <c>INDEX</c> hint naming a disabled index by its id
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException IndexHintIdDisabled(int indexId, string qualifiedTableName) =>
        new($"The index ID {indexId} on table \"{qualifiedTableName}\" (specified in the FROM clause) is disabled or resides in a filegroup which is not online.", 316, 16, 1);

    /// <summary>
    /// Msg 650 — <c>READPAST</c> under a level other than READ COMMITTED or
    /// REPEATABLE READ, whether a hint beside it or the session sets it
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ReadPastOutsideReadCommitted() =>
        new("You can only specify the READPAST lock in the READ COMMITTED or REPEATABLE READ isolation levels.", 650, 16, 1);

    /// <summary>
    /// Msg 651 — <c>PAGLOCK</c> on a table whose index disallows page locks
    /// (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException PageLockHintInhibited(string qualifiedTableName) =>
        new($"Cannot use the PAGE granularity hint on the table \"{qualifiedTableName}\" because locking at the specified granularity is inhibited.", 651, 16, 1);

    /// <summary>
    /// Msg 4102 — <c>READPAST</c> on an <c>INSERT</c> target (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ReadPastOnInsertTarget() =>
        new("The READPAST lock hint is only allowed on target tables of UPDATE and DELETE and on tables specified in an explicit FROM clause.", 4102, 15, 1);

    /// <summary>
    /// Msg 10724 — <c>FORCESEEK</c> on an <c>INSERT</c>, <c>UPDATE</c> or
    /// <c>DELETE</c> target, which real reports at line 15 wherever the
    /// statement sits (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForceSeekOnDmlTarget() =>
        new SimulatedSqlException("The FORCESEEK hint is not allowed for target tables of INSERT, UPDATE, or DELETE statements.", 10724, 15, 1).PinLine(15);

    /// <summary>Msg 10745 — <see cref="ForceSeekOnDmlTarget"/>'s <c>FORCESCAN</c> sibling.</summary>
    internal static SimulatedSqlException ForceScanOnDmlTarget() =>
        new SimulatedSqlException("The FORCESCAN hint is not allowed for target tables of INSERT, UPDATE, or DELETE statements.", 10745, 15, 1).PinLine(15);

    /// <summary>
    /// Msg 1042 — two values for one <c>OPTION</c> hint, a grant percent
    /// outside 0–100, or an inline join hint the <c>OPTION</c> clause's join
    /// hints don't include (class 16 for the last). Probed 2026-10-05 against
    /// SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ConflictingOptimizerHints(string hint, byte @class = 15) =>
        new($"Conflicting {hint} optimizer hints specified.", 1042, @class, 1);

    /// <summary>Msg 1071 — an inline <c>REMOTE</c> join beside an <c>OPTION</c> join hint.</summary>
    internal static SimulatedSqlException JoinAlgorithmWithRemoteJoin() =>
        new("Cannot specify a JOIN algorithm with a remote JOIN.", 1071, 16, 1);

    /// <summary>Msg 1072 — <c>REMOTE</c> on an outer join.</summary>
    internal static SimulatedSqlException RemoteHintOnOuterJoin() =>
        new("A REMOTE hint can only be specified with an INNER JOIN clause.", 1072, 15, 1);

    /// <summary>
    /// Msg 310 — a <c>MAXRECURSION</c> over 32767 (probed 2026-10-05 against
    /// SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MaxRecursionOutOfRange(string value) =>
        new($"The value {value} specified for the MAXRECURSION option exceeds the allowed maximum of 32767.", 310, 15, 1);

    /// <summary>Msg 10768 — a second <c>LABEL</c> in one <c>OPTION</c> clause.</summary>
    internal static SimulatedSqlException LabelHintRepeated() =>
        new("LABEL hint can only be used one time in the query. Modify the query and re-run it.", 10768, 15, 1);

    /// <summary>Msg 320 — an <c>OPTIMIZE FOR</c> value that isn't a literal.</summary>
    internal static SimulatedSqlException OptimizeForValueNotLiteral(string variable) =>
        new($"The compile-time variable value for '{variable}' in the OPTIMIZE FOR clause must be a literal.", 320, 15, 1);

    /// <summary>Msg 4131 — one variable given twice across the <c>OPTIMIZE FOR</c> clauses.</summary>
    internal static SimulatedSqlException OptimizeForVariableRepeated(string variable) =>
        new($"A compile-time literal value is specified more than once for the variable \"{variable}\" in one or more OPTIMIZE FOR clauses.", 4131, 16, 1);

    /// <summary>Msg 4132 — an <c>OPTIMIZE FOR</c> value the variable's type can't take.</summary>
    internal static SimulatedSqlException OptimizeForValueNotConvertible(string variable) =>
        new($"The value specified for the variable \"{variable}\" in the OPTIMIZE FOR clause could not be implicitly converted to that variable's type.", 4132, 16, 1);

    /// <summary>
    /// Msg 8695 — <c>USE PLAN</c> with an empty plan (probed 2026-10-05
    /// against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException MalformedUsePlan() =>
        new("Cannot execute query because of incorrectly formed XML plan in USE PLAN hint. Verify that XML plan is a legal plan suitable for plan forcing. See Books Online for additional details.", 8695, 16, 4);

    /// <summary>
    /// Msg 6913 — a <c>USE PLAN</c> document whose root element the showplan
    /// schema doesn't declare (probed 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException UsePlanFailsShowplanSchema(string rootName) =>
        new($"XML Validation: Declaration not found for element '{rootName}'. Location: /*:{rootName}[1]", 6913, 16, 1);

    /// <summary>
    /// Msg 8720 — two <c>TABLE HINT</c> clauses for one object (probed
    /// 2026-10-05 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException TableHintClauseRepeated(string objectName) =>
        new($"Cannot execute query. There is more than one TABLE HINT clause specified for object '{objectName}'. Use at most one such TABLE HINT clause per table reference.", 8720, 16, 0);

    /// <summary>
    /// Msg 8722 — a <c>TABLE HINT</c> clause carrying a semantic hint the
    /// object's own <c>WITH</c> clause lacks.
    /// </summary>
    internal static SimulatedSqlException SemanticTableHintMismatch(string hint, string objectName) =>
        new($"Cannot execute query. Semantic affecting hint '{hint}' appears in the 'TABLE HINT' clause of object '{objectName}' but not in the corresponding 'WITH' clause.  Change the OPTION (TABLE HINTS...) clause so the semantic affecting hints match the WITH clause.", 8722, 16, 1);

    /// <summary>
    /// Msg 8723 — a <c>TABLE HINT</c> clause naming no exposed name of the
    /// query: the object must be written as the query exposes it, its alias
    /// when it has one.
    /// </summary>
    internal static SimulatedSqlException TableHintObjectNotInQuery(string objectName) =>
        new($"Cannot execute query. Object '{objectName}' is specified in the TABLE HINT clause, but is not used in the query or does not match the alias specified in the query. Table references in the TABLE HINT clause must match the WITH clause.", 8723, 16, 1);

    /// <summary>
    /// Msg 10746 — <c>FORCESCAN</c> and <c>FORCESEEK</c> on one table
    /// (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForceScanWithForceSeek() =>
        new("The FORCESCAN hint is specified simultaneously with the FORCESEEK hint. Remove one of the hints and resubmit the query.", 10746, 15, 1);

    /// <summary>
    /// Msg 10747 — a nested <c>FORCESEEK(ix (col, …))</c> beside an
    /// <c>INDEX</c> hint on the same table (probed 2026-09-28 against SQL
    /// Server 2025).
    /// </summary>
    internal static SimulatedSqlException ParameterizedForceSeekWithIndexHint() =>
        new("The parameterized FORCESEEK hint cannot be simultaneously used with INDEX hints or a non-parameterized FORCESEEK hint on the same object. Use either INDEX hints and a non-parameterized FORCESEEK hint or use a parameterized FORCESEEK hint without INDEX hints for each table or view.", 10747, 15, 1);

    /// <summary>
    /// Msg 10750 — <c>FORCESCAN</c> beside an <c>INDEX</c> hint naming more
    /// than one index (probed 2026-09-28 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ForceScanWithSeveralIndexes() =>
        new("The FORCESCAN hint cannot be used with more than one INDEX hint. Remove the extra INDEX hints and resubmit the query.", 10750, 15, 1);

    /// <summary>
    /// Msg 3952 — raised when a session whose
    /// <see cref="SimulatedDbConnection.SessionIsolationLevel"/> is
    /// <see cref="System.Data.IsolationLevel.Snapshot"/> accesses a user
    /// table in a database where
    /// <see cref="Database.AllowSnapshotIsolation"/> is <c>false</c>.
    /// Probe-confirmed verbatim wording (Cls 16, State 1) against SQL Server
    /// 2025: fires at first user-table access, not at <c>SET TRANSACTION
    /// ISOLATION LEVEL SNAPSHOT</c> and not at <c>BeginTransaction(Snapshot)</c>.
    /// System-catalog reads (<c>sys.tables</c>, <c>sys.objects</c>) and
    /// statements that never touch a user table both succeed silently
    /// regardless of the ASI flag.
    /// </summary>
    internal static SimulatedSqlException SnapshotIsolationNotAllowed(string databaseName) =>
        new($"Snapshot isolation transaction failed accessing database '{databaseName}' because snapshot isolation is not allowed in this database. Use ALTER DATABASE to allow snapshot isolation.", 3952, 16, 1);

    /// <summary>
    /// Msg 3961 — a SNAPSHOT transaction reaching, to read or write, a table
    /// another transaction created or redefined after its snapshot was taken
    /// (probed 2026-10-01 against SQL Server 2025). Uncaught it ends the
    /// batch and rolls the transaction back; caught, it dooms it.
    /// </summary>
    internal static SimulatedSqlException SnapshotTableDefinitionChanged(string databaseName) =>
        new($"Snapshot isolation transaction failed in database '{databaseName}' because the object accessed by the statement has been modified by a DDL statement in another concurrent transaction since the start of this transaction.  It is disallowed because the metadata is not versioned. A concurrent update to metadata can lead to inconsistency if mixed with snapshot isolation.", 3961, 16, 1)
        {
            AbortsAsUnderXactAbort = true,
        };

    /// <summary>
    /// Msg 3960 — raised when a SNAPSHOT-isolation transaction attempts to
    /// write a row whose live version was committed by a different
    /// transaction after this transaction's snapshot was taken. Probe-
    /// confirmed verbatim wording (Cls 16) against SQL Server 2025
    /// — the message embeds the offending table's two-part name and the
    /// containing database. The probed real server auto-rolls back the
    /// failing SI transaction (<c>@@TRANCOUNT</c> drops to 0); the simulator
    /// matches that auto-rollback behavior. Uncaught it ends the batch
    /// (probed 2026-09-28). The state names the table's organization: 2 for
    /// a table with a clustered index, 6 for a heap (probed 2026-09-28).
    /// </summary>
    internal static SimulatedSqlException SnapshotIsolationUpdateConflict(string qualifiedTableName, string databaseName, bool clustered) =>
        new($"Snapshot isolation transaction aborted due to update conflict. You cannot use snapshot isolation to access table '{qualifiedTableName}' directly or indirectly in database '{databaseName}' to update, delete, or insert the row that has been modified or deleted by another transaction. Retry the transaction or change the isolation level for the update/delete statement.", 3960, 16, clustered ? (byte)2 : (byte)6) { TerminatesBatch = true };
}
