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
}
