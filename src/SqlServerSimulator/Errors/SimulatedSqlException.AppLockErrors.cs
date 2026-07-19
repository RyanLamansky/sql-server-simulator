namespace SqlServerSimulator;

partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 1202: the <c>@DbPrincipal</c> argument of
    /// <c>sp_getapplock</c> / <c>sp_releaseapplock</c> / <c>APPLOCK_MODE</c> /
    /// <c>APPLOCK_TEST</c> names a database principal that doesn't exist (or
    /// the caller isn't a member of). Verbatim wording, class, and state
    /// probe-confirmed against SQL Server 2025.
    /// </summary>
    internal static SimulatedSqlException DatabasePrincipalDoesNotExist(string principalName) =>
        new($"The database-principal '{principalName}' does not exist or user is not a member.", 1202, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1223: <c>sp_releaseapplock</c> on a resource
    /// the specified owner doesn't currently hold. Probe-confirmed verbatim,
    /// including the principal / resource interpolation.
    /// </summary>
    internal static SimulatedSqlException CannotReleaseAppLockNotHeld(string principalName, string resource) =>
        new($"Cannot release the application lock (Database Principal: '{principalName}', Resource: '{resource}') because it is not currently held.", 1223, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1224: a NULL <c>@Resource</c> passed to
    /// <c>sp_getapplock</c> / <c>sp_releaseapplock</c>. (A missing
    /// <c>@Resource</c> on <c>sp_getapplock</c> is different — that returns
    /// -999 silently; only an explicitly-NULL value raises.) Probe-confirmed
    /// verbatim including the internal <c>xp_userlock</c> reference.
    /// </summary>
    internal static SimulatedSqlException InvalidAppLockResource() =>
        new("An invalid application lock resource was passed to xp_userlock.", 1224, 16, 5);

    /// <summary>
    /// Mimics SQL Server error 1225: an unrecognized lock-mode string passed
    /// to <c>APPLOCK_TEST</c>. (The same bad string on <c>sp_getapplock</c>
    /// is different — the proc returns -999 silently.) Probe-confirmed
    /// verbatim.
    /// </summary>
    internal static SimulatedSqlException InvalidAppLockModeForTest() =>
        new("An invalid application lock mode was passed to applock_test.", 1225, 16, 3);

    /// <summary>
    /// Mimics SQL Server error 1226: an unrecognized lock-owner string
    /// passed to <c>APPLOCK_MODE</c> / <c>APPLOCK_TEST</c> (the function's
    /// lowercase name is interpolated). The same bad string on the procs
    /// returns -999 silently. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException InvalidAppLockOwnerForFunction(string functionName) =>
        new($"An invalid application lock owner was passed to {functionName}.", 1226, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1227: <c>sp_getapplock</c>'s
    /// <c>@LockTimeout</c> below -1. Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException InvalidAppLockTimeout() =>
        new("An invalid application lock time-out was passed to xp_userlock.", 1227, 16, 2);

    /// <summary>
    /// Mimics SQL Server error 3918: <c>APPLOCK_MODE</c> / <c>APPLOCK_TEST</c>
    /// evaluated with the <c>Transaction</c> owner (explicit or defaulted via
    /// NULL) outside a user transaction. (The <c>sp_getapplock</c> proc is
    /// different — Transaction owner without a transaction returns -999
    /// silently.) Probe-confirmed verbatim.
    /// </summary>
    internal static SimulatedSqlException MustExecuteInUserTransaction() =>
        new("The statement or function must be executed in the context of a user transaction.", 3918, 16, 2);
}
