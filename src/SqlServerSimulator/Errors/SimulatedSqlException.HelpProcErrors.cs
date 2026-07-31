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
}
