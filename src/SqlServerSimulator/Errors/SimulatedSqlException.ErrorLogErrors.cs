namespace SqlServerSimulator;

// What the error-log procedures report. The extended procedures' Msg 22004 is
// one number at several severities, each naming the check that failed
// (probed 2026-10-16 against SQL Server 2025).
partial class SimulatedSqlException
{
    /// <summary>Msg 22004 at severity 12: an argument of a type the extended procedure doesn't read, or a NULL where it needs a value.</summary>
    internal static SimulatedSqlException ExtendedProcedureInvalidParameterType() =>
        new("Error executing extended stored procedure: Invalid Parameter Type", 22004, 12, 1);

    /// <summary>Msg 22004 at severity 12: an argument of the right type but a value the extended procedure doesn't take.</summary>
    internal static SimulatedSqlException ExtendedProcedureInvalidParameterValue() =>
        new("Invalid parameter value.", 22004, 12, 1);

    /// <summary>Msg 22004 at severity 15: <c>xp_readerrorlog</c>'s log type 3.</summary>
    internal static SimulatedSqlException ExtendedProcedureInvalidParameter() =>
        new("Error executing extended stored procedure: Invalid Parameter", 22004, 15, 1);

    /// <summary>Msg 22004 at severity 16: <c>xp_readerrorlog</c> asked for a log there is no file for.</summary>
    internal static SimulatedSqlException ErrorLogFileNotFound() =>
        new("xp_readerrorlog() returned error 2, 'The system cannot find the file specified.'", 22004, 16, 1);

    /// <summary>
    /// Msg 22004 at severity 15: an <c>xp_readerrorlog</c> start or end time
    /// that doesn't read as one. Real's text carries its curly quotes as UTF-8
    /// replacement characters read a byte to a character, kept here as sent.
    /// </summary>
    internal static SimulatedSqlException ErrorLogDateFilterIncorrect() =>
        new("The format for the date filter is incorrect. Please use a valid SQL Server date or datetime format. For example, valid formats are ï¿½YYYY-MM-DD' and ï¿½MM/DD/YYYYï¿½.", 22004, 15, 1);

    /// <summary>Msg 27219: <c>sp_readerrorlog</c> / <c>sp_enumerrorlogs</c> called by a session that may not read the log.</summary>
    internal static SimulatedSqlException ErrorLogProcedurePermissionDenied() =>
        new("Caller does not have permissions to execute the stored procedure.", 27219, 16, 1);

    /// <summary>
    /// Msg 22048 (<c>xp_create_subdir</c>) or 22049 (<c>xp_delete_file</c>)
    /// at severity 15: a missing argument, or one the procedure can't read.
    /// </summary>
    internal static SimulatedSqlException ExtendedProcedureInvalidParameter(int number) =>
        new("Error executing extended stored procedure: Invalid Parameter", number, 15, 1);

    /// <summary>
    /// Msg 22049 at severity 15: <c>xp_delete_file</c> of one file that holds
    /// no backup — a detached database's (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException NotABackupFile() =>
        new("Error executing xp_delete_file extended stored procedure: Specified file is not a SQL Server backup file.", 22049, 15, 0);

    /// <summary>
    /// Msg 22048 (<c>xp_create_subdir</c>) or 22049 (<c>xp_delete_file</c>)
    /// at severity 16: the operating system refused the path, error 2 for
    /// one that doesn't exist and 183 for a file in the way.
    /// </summary>
    internal static SimulatedSqlException ExtendedProcedureOperatingSystemError(string procedure, int number, int osError) =>
        new($"{procedure}() returned error {osError}, '{(osError == 183 ? "Cannot create a file when that file already exists." : "The system cannot find the file specified.")}'", number, 16, 1);
}
