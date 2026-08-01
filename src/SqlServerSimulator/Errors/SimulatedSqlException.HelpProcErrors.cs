namespace SqlServerSimulator;

// The error set the sp_help family raises (sp_help / sp_helptext /
// sp_helpindex / sp_helpconstraint). Real SQL Server's procs surface these
// through `raiserror(<msgid>, -1, -1, …)` against sys.messages, so the
// severity / state / wording below are the catalog rows' own — all
// probe-confirmed against SQL Server 2025 (2026-07-31).
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server's Msg 15009 — the sp_help family's "object not
    /// found" error. Both substitutions are as-written: <paramref name="objectName"/>
    /// is the caller's <c>@objname</c> string verbatim (qualifier included)
    /// and <paramref name="databaseName"/> is the current database.
    /// </summary>
    internal static SimulatedSqlException HelpObjectDoesNotExist(string objectName, string databaseName) =>
        new($"The object '{objectName}' does not exist in database '{databaseName}' or is invalid for this operation.", 15009, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15250 — a three-part <c>@objname</c> whose
    /// database component names some database other than the current one.
    /// The message carries no substitution.
    /// </summary>
    internal static SimulatedSqlException HelpObjectNotInCurrentDatabase() =>
        new("The database name component of the object qualifier must be the name of the current database.", 15250, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15197 — <c>sp_helptext</c> on an object that
    /// stores no definition text (a table, a sequence, a synonym). Real
    /// reaches it by finding no <c>syscomments</c> rows for the object.
    /// </summary>
    internal static SimulatedSqlException HelpNoTextForObject(string objectName) =>
        new($"There is no text for object '{objectName}'.", 15197, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15218 — <c>sp_helptext @objname, @columnname</c>
    /// where <c>@objname</c> isn't a table (real accepts a table or a
    /// table-valued function; every other object kind lands here).
    /// </summary>
    internal static SimulatedSqlException HelpObjectIsNotATable(string objectName) =>
        new($"Object '{objectName}' is not a table.", 15218, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15645 — the <c>@columnname</c> argument names
    /// a column the object doesn't have.
    /// </summary>
    internal static SimulatedSqlException HelpColumnDoesNotExist(string columnName) =>
        new($"Column '{columnName}' does not exist.", 15645, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15646 — the <c>@columnname</c> argument names
    /// a real column rather than a computed one. <c>sp_helptext</c>'s column
    /// form only reports computed-column definitions.
    /// </summary>
    internal static SimulatedSqlException HelpColumnIsNotComputed(string columnName) =>
        new($"Column '{columnName}' is not a computed column.", 15646, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15010 — <c>sp_helpdb</c>'s "no such database"
    /// error. A NULL <paramref name="databaseName"/> renders as the empty
    /// string, matching <c>raiserror</c>'s NULL substitution.
    /// </summary>
    internal static SimulatedSqlException HelpDatabaseDoesNotExist(string databaseName) =>
        new($"The database '{databaseName}' does not exist. Supply a valid database name. To see available databases, use sys.databases.", 15010, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15007 — <c>sp_who</c> / <c>sp_who2</c>'s
    /// <c>@loginame</c> argument naming no known login.
    /// </summary>
    internal static SimulatedSqlException HelpLoginIsNotValid(string loginName) =>
        new($"'{loginName}' is not a valid login or you do not have permission.", 15007, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15198 — <c>sp_helpuser</c>'s <c>@name_in_db</c>
    /// argument matching neither a database user nor a database role.
    /// </summary>
    internal static SimulatedSqlException HelpNameIsNotAUserOrRole(string name) =>
        new($"The name supplied ({name}) is not a user, role, or aliased login.", 15198, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15305 — <c>sp_helptrigger</c>'s
    /// <c>@triggertype</c> argument outside <c>insert</c> / <c>update</c> /
    /// <c>delete</c>. The message carries no substitution.
    /// </summary>
    internal static SimulatedSqlException HelpTriggerTypeIsNotValid() =>
        new("The @TriggerType parameter value must be 'insert', 'update', or 'delete'.", 15305, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15234 — <c>sp_spaceused</c> against an object
    /// kind that occupies no storage (anything but a table, a view or a
    /// queue). The message carries no substitution.
    /// </summary>
    internal static SimulatedSqlException SpaceUsedObjectHasNoSpace() =>
        new("Objects of this type have no space allocated.", 15234, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 15143 — <c>sp_spaceused</c>'s
    /// <c>@updateusage</c> argument outside <c>true</c> / <c>false</c>.
    /// </summary>
    internal static SimulatedSqlException SpaceUsedUpdateUsageIsNotValid(string value) =>
        new($"'{value}' is not a valid option for the @updateusage parameter. Enter either 'true' or 'false'.", 15143, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 14822 — <c>sp_spaceused</c>'s <c>@mode</c>
    /// argument outside <c>ALL</c> / <c>LOCAL_ONLY</c> / <c>REMOTE_ONLY</c>.
    /// The doubled space before <c>'ALL'</c> is the catalog row's own.
    /// </summary>
    internal static SimulatedSqlException SpaceUsedModeIsNotValid(string value) =>
        new($"'{value}' is not a valid option for the @mode parameter. Enter  'ALL', 'LOCAL_ONLY' or 'REMOTE_ONLY'.", 14822, 16, 1);

    /// <summary>
    /// Mimics SQL Server's Msg 14821 — <c>sp_spaceused @mode = 'REMOTE_ONLY'</c>
    /// against a database with no stretch (remote) part. State 1 is the
    /// database form's; the object form raises the same message with state 2.
    /// </summary>
    internal static SimulatedSqlException SpaceUsedRemoteOnlyHasNoRemotePart(byte state) =>
        new("Cannot execute in REMOTE_ONLY mode since remote part does not exist or is invalid for this operation.", 14821, 16, state);
}
