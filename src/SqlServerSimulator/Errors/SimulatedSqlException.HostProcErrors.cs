namespace SqlServerSimulator;

// What xp_fixeddrives and xp_servicecontrol report: an extended procedure's
// own message numbers, each leaving the batch running (probed 2026-10-16
// against SQL Server 2025).
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 22005, <c>xp_fixeddrives</c> refusing its arguments: at severity 12
    /// a type that is no integer (or NULL), at 15 a second argument.
    /// </summary>
    internal static SimulatedSqlException FixedDrivesInvalidParameter(bool typeError) => typeError
        ? new("Error executing extended stored procedure: Invalid Parameter Type", 22005, 12, 0)
        : new("Error executing extended stored procedure: Invalid Parameter", 22005, 15, 0);

    /// <summary>Msg 14260: <c>sp_clean_db_free_space</c> or <c>sp_clean_db_file_free_space</c> run by a login without <c>CONTROL SERVER</c>.</summary>
    internal static SimulatedSqlException InsufficientPermissionToRunCommand() =>
        new("You do not have sufficient permission to run this command. Contact your system administrator.", 14260, 16, 1);

    /// <summary>Msg 22003 at severity 15: <c>xp_servicecontrol</c> given fewer than two strings.</summary>
    internal static SimulatedSqlException ServiceControlInvalidParameter() =>
        new("Error executing xp_servicecontrol extended stored procedure: Invalid Parameter", 22003, 15, 0);

    /// <summary>Msg 22003 at severity 15: an action that is none of <c>querystate</c>, <c>start</c>, <c>stop</c>, <c>pause</c>, <c>continue</c>.</summary>
    internal static SimulatedSqlException ServiceControlInvalidCommand() =>
        new("Error executing xp_servicecontrol: Invalid config change command", 22003, 15, 0);

    /// <summary>
    /// Msg 22003 at severity 16: the service manager refused the call — 1060
    /// for a service that isn't installed, 1053 for a start, 1062 for any other
    /// action on the stopped agent.
    /// </summary>
    internal static SimulatedSqlException ServiceControlFailed(string call, int osError) =>
        new($"{call}() returned error {osError}, '{osError switch
        {
            1053 => "The service did not respond to the start or control request in a timely fashion.",
            1060 => "The specified service does not exist as an installed service.",
            _ => "The service has not been started.",
        }}'", 22003, 16, 1);
}
