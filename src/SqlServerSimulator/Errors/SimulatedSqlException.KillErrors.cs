namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// A <c>SHUTDOWN</c> refused for want of the permission, after its
    /// informational Msg 6004: the batch ends, the transaction rolls back and
    /// no <c>CATCH</c> intercepts it, and the client reports its own
    /// severe-error Msg 0 (probed 2026-10-06 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException ShutdownRefused() =>
        new("A severe error occurred on the current command.  The results, if any, should be discarded.", 0, 11, 0)
        {
            AbortsTransaction = true,
            RaisedByClient = true,
            TerminatesBatch = true,
        };

    /// <summary>Mimics SQL Server error 6101: a <c>KILL</c> session id outside 1 to 32767 (probed 2026-09-30 against SQL Server 2025).</summary>
    internal static SimulatedSqlException KillSessionIdNotValid(long sessionId) =>
        new($"Session ID {sessionId} is not valid.", 6101, 16, 1);

    /// <summary>Mimics SQL Server error 6102: a <c>KILL</c> by a principal without <c>ALTER ANY CONNECTION</c>.</summary>
    internal static SimulatedSqlException KillNotPermitted() =>
        new("User does not have permission to use the KILL statement.", 6102, 14, 2);

    /// <summary>Mimics SQL Server error 6104: <c>KILL</c> of the caller's own session.</summary>
    internal static SimulatedSqlException KillOwnProcess() =>
        new("Cannot use KILL to kill your own process.", 6104, 16, 1);

    /// <summary>Mimics SQL Server error 6106: <c>KILL</c> of a session id no session holds.</summary>
    internal static SimulatedSqlException KillProcessNotActive(int sessionId) =>
        new($"Process ID {sessionId} is not an active process ID.", 6106, 16, 2);

    /// <summary>Mimics SQL Server error 6107: <c>KILL</c> of a system session.</summary>
    internal static SimulatedSqlException KillSystemProcess() =>
        new("Only user processes can be killed.", 6107, 14, 1);

    /// <summary>Mimics SQL Server error 6108: <c>KILL &lt;session id&gt; WITH COMMIT | ROLLBACK</c>.</summary>
    internal static SimulatedSqlException KillSessionWithCommitOrRollback() =>
        new("KILL SPID WITH COMMIT/ROLLBACK is not supported by Microsoft SQL Server. Use KILL UOW WITH COMMIT/ROLLBACK to resolve in-doubt distributed transactions involving Microsoft Distributed Transaction Coordinator (MS DTC).", 6108, 14, 1);

    /// <summary>Mimics SQL Server error 6110: <c>KILL</c> of a distributed transaction that doesn't exist.</summary>
    internal static SimulatedSqlException KillUnitOfWorkMissing(Guid unitOfWork) =>
        new($"The distributed transaction with UOW {unitOfWork.ToString("B").ToUpperInvariant()} does not exist.", 6110, 16, 1);

    /// <summary>Msg 8169 as <c>KILL '&lt;text&gt;'</c> raises it, at state 1, for text that isn't a unit-of-work id.</summary>
    internal static SimulatedSqlException KillUnitOfWorkNotGuid() =>
        new("Conversion failed when converting from a character string to uniqueidentifier.", 8169, 16, 1);

    /// <summary>Mimics SQL Server error 6115: <c>KILL</c> inside a user transaction.</summary>
    internal static SimulatedSqlException KillInTransaction() =>
        new("KILL command cannot be used inside user transactions.", 6115, 16, 1);

    /// <summary>Mimics SQL Server error 6120: <c>KILL … WITH STATUSONLY</c> of a session that isn't rolling back.</summary>
    internal static SimulatedSqlException KillNoRollbackInProgress(int sessionId) =>
        new($"Status report cannot be obtained. Rollback operation for Process ID {sessionId} is not in progress.", 6120, 16, 1);

    /// <summary>
    /// What a session killed while running answers: Msg 596 at severity 21,
    /// then the severity-20 Msg 0 SqlClient reports a severed command with,
    /// ending the session and rolling its transaction back with no
    /// <c>CATCH</c> able to intercept it (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException SessionKilled() => KillState(attention: true);

    /// <summary>
    /// What real answers a joined <c>UPDATE</c> whose target is a table value
    /// constructor on an <c>APPLY</c>'s right side reading its left side,
    /// <c>UPDATE d SET id = 5 FROM u CROSS APPLY (VALUES (u.id)) d(id)</c>:
    /// the session ends, even when the batch only compiles the statement
    /// (probed 2026-10-01 against SQL Server 2025). The caller marks the
    /// connection so the command closes it once this has been delivered.
    /// </summary>
    internal static SimulatedSqlException ConstructedRowsApplyUpdateEndsSession() => KillState(attention: false);

    /// <summary>
    /// Msg 596 at severity 21, then the severity-20 Msg 0 SqlClient reports a
    /// severed command with, ending the session and rolling its transaction back.
    /// </summary>
    private static SimulatedSqlException KillState(bool attention)
    {
        List<SimulatedError> entries =
        [
            .. new SimulatedSqlException("Cannot continue the execution because the session is in the kill state.", 596, 21, 1).Errors,
            .. new SimulatedSqlException("A severe error occurred on the current command.  The results, if any, should be discarded.", 0, 20, 0).Errors,
        ];
        return new(string.Join(Environment.NewLine, entries.Select(entry => entry.Message)), System.Runtime.InteropServices.CollectionsMarshal.AsSpan(entries))
        {
            AbortsTransaction = true,
            EndsSession = true,
            IsAttention = attention,
        };
    }

    /// <summary>
    /// What the next command on a session killed while idle meets: SqlClient's
    /// own severity-20 refusal, the connection closed behind it (probed
    /// 2026-09-30 against SQL Server 2025, for a session holding a transaction —
    /// one without any is reconnected transparently by SqlClient's idle
    /// connection resiliency, which an in-process connection can't do).
    /// </summary>
    internal static SimulatedSqlException ConnectionBroken() =>
        new("The connection is broken and recovery is not possible.  The connection is marked by the server as unrecoverable.  No attempt was made to restore the connection.", 0, 20, 0);
}
