namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 1222 — fired when a lock acquisition exceeds the session's
    /// configured <c>@@LOCK_TIMEOUT</c>. Single, fixed wording regardless of
    /// the lock kind that timed out — probe-confirmed against SQL Server 2025
    /// (2026-05-14): both row-level and schema-stability (Sch-S / Sch-M) lock
    /// timeouts surface this exact message. The only differentiator on the
    /// real server is <c>State</c> (45 for row / IS paths; 56 when a schema-
    /// stability lock is involved on either side). The state is parameterized
    /// so callers can match the real-server discriminator.
    /// </summary>
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
    /// <summary>
    /// <b>Msg -2</b> — a <c>CommandTimeout</c> expiry. Like
    /// <see cref="CommandCancelled"/> this is manufactured client-side rather
    /// than sent by a server, and carries SqlClient's own wording (double space
    /// included) with Class 11 / State 0 — probe-confirmed against SqlClient
    /// 7.0.2 driving SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException ExecutionTimeoutExpired() =>
        new("Execution Timeout Expired.  The timeout period elapsed prior to completion of the operation or the server is not responding.", -2, 11, 0);

    internal static SimulatedSqlException CommandCancelled() =>
        new("A severe error occurred on the current command.  The results, if any, should be discarded.", 0, 11, 0);

    internal static SimulatedSqlException LockRequestTimeOutExceeded(byte state = 56) =>
        new("Lock request time out period exceeded.", 1222, 16, state);

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
    internal static SimulatedSqlException TransactionDeadlocked(int victimSpid) =>
        new($"Transaction (Process ID {victimSpid}) was deadlocked on lock resources with another process and has been chosen as the deadlock victim. Rerun the transaction.", 1205, 13, 45);

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
    /// <c>DELETE</c> / <c>MERGE</c>. Probe-confirmed verbatim wording.
    /// </summary>
    internal static SimulatedSqlException NoLockHintNotAllowedOnDmlTarget() =>
        new("The NOLOCK and READUNCOMMITTED lock hints are not allowed for target tables of INSERT, UPDATE, DELETE or MERGE statements.", 1065, 15, 1);

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
    /// Msg 3960 — raised when a SNAPSHOT-isolation transaction attempts to
    /// write a row whose live version was committed by a different
    /// transaction after this transaction's snapshot was taken. Probe-
    /// confirmed verbatim wording (Cls 16, State 2) against SQL Server 2025
    /// — the message embeds the offending table's two-part name and the
    /// containing database. The probed real server auto-rolls back the
    /// failing SI transaction (<c>@@TRANCOUNT</c> drops to 0); the simulator
    /// matches that auto-rollback behavior.
    /// </summary>
    internal static SimulatedSqlException SnapshotIsolationUpdateConflict(string qualifiedTableName, string databaseName) =>
        new($"Snapshot isolation transaction aborted due to update conflict. You cannot use snapshot isolation to access table '{qualifiedTableName}' directly or indirectly in database '{databaseName}' to update, delete, or insert the row that has been modified or deleted by another transaction. Retry the transaction or change the isolation level for the update/delete statement.", 3960, 16, 2);
}
