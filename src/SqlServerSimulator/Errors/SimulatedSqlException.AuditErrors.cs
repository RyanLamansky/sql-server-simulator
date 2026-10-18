namespace SqlServerSimulator;

// SQL Server Audit's error factories: the server audit and audit
// specification DDL and sys.fn_get_audit_file (probed 2026-10-17 against SQL
// Server 2025).
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 33074: server audit DDL run outside <c>master</c>, <paramref name="verb"/>
    /// and <paramref name="what"/> naming it — state 1 to create an audit, 2
    /// to alter one, 3 to drop one, 4 to create or alter a server audit
    /// specification, 5 to drop one.
    /// </summary>
    internal static SimulatedSqlException AuditDdlOutsideMaster(string verb, string what, byte state) =>
        new($"Cannot {verb} a {what} from a user database. This operation must be performed in the master database.", 33074, 16, state);

    /// <summary>Msg 15151: <c>ALTER</c> or <c>DROP SERVER AUDIT</c> (<paramref name="verb"/>) naming an audit that doesn't exist, or run without <c>ALTER ANY SERVER AUDIT</c>.</summary>
    internal static SimulatedSqlException CannotAlterOrDropAudit(string verb, string name) =>
        new($"Cannot {verb} the audit '{name}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>Msg 15530: an audit's name, or the GUID <c>AUDIT_GUID</c> gives it, taken by another.</summary>
    internal static SimulatedSqlException AuditAlreadyExists(string name) =>
        new($"The audit with name \"{name}\" already exists.", 15530, 16, 1);

    /// <summary>Msg 15530: an audit specification's name taken in its scope.</summary>
    internal static SimulatedSqlException AuditSpecificationAlreadyExists(string name) =>
        new($"The audit specification with name \"{name}\" already exists.", 15530, 16, 1);

    /// <summary>Msg 33071 state 1: a change to a running audit other than its <c>STATE</c>, or its drop.</summary>
    internal static SimulatedSqlException AuditMustBeDisabled() =>
        new("This command requires audit to be disabled. Disable the audit and rerun this command.", 33071, 16, 1);

    /// <summary>Msg 33071 state 3: the drop of an enabled audit specification.</summary>
    internal static SimulatedSqlException AuditSpecificationMustBeDisabled() =>
        new("This command requires audit specification to be disabled. Disable the audit specification and rerun this command.", 33071, 16, 3);

    /// <summary>Msg 33070: a <c>MAXSIZE</c> of 1 MB.</summary>
    internal static SimulatedSqlException AuditMaxSizeTooSmall() =>
        new("The specified maximum size limit for the audit log file is less than the minimum value allowed. The maximum size limit must be at least 2 MB.", 33070, 16, 1);

    /// <summary>Msg 33238: <c>MAX_FILES</c> beside <c>MAX_ROLLOVER_FILES</c>.</summary>
    internal static SimulatedSqlException AuditMaxFilesWithRollover() =>
        new("MAX_FILES and MAX_ROLLOVER_FILES options cannot be specified together.", 33238, 16, 1);

    /// <summary>Msg 33077: <c>RESERVE_DISK_SPACE = ON</c> with no <c>MAXSIZE</c> to reserve.</summary>
    internal static SimulatedSqlException AuditReserveDiskSpaceUnlimited() =>
        new("RESERVE_DISK_SPACE cannot be specified when setting MAXSIZE = UNLIMITED. Either reduce MAXSIZE or do not specify RESERVE_DISK_SPACE.", 33077, 16, 1);

    /// <summary>Msg 33227: a <c>QUEUE_DELAY</c> from 1 to 999, state 1 in <c>CREATE</c> and 2 in <c>ALTER</c>.</summary>
    internal static SimulatedSqlException AuditQueueDelayInvalid(byte state) =>
        new("The specified value for QUEUE_DELAY is not valid. Specify either 0 or 1000 and higher.", 33227, 16, state);

    /// <summary>Msg 33228: <c>ON_FAILURE = SHUTDOWN</c> in a <c>CREATE</c> by a login without <c>SHUTDOWN</c>.</summary>
    internal static SimulatedSqlException AuditShutdownNotPermitted() =>
        new("You cannot configure SQL Server Audit to shutdown the server because you do not have the permission to shut down the server. Contact your system administrator.", 33228, 16, 1);

    /// <summary>Msg 15021: an empty <c>FILEPATH</c>.</summary>
    internal static SimulatedSqlException AuditFilePathEmpty() =>
        new("Invalid value given for parameter FILEPATH. Specify a valid parameter value.", 15021, 16, 1);

    /// <summary>Msg 33072: a <c>FILEPATH</c> naming no directory the server holds, a relative one included.</summary>
    internal static SimulatedSqlException AuditFilePathInvalid() =>
        new("The audit log file path is invalid.", 33072, 16, 5);

    /// <summary>Msg 33223: <c>ALTER SERVER AUDIT … WITH (STATE = …)</c> beside another option or clause.</summary>
    internal static SimulatedSqlException AuditStateNotAlone() =>
        new("ALTER SERVER AUDIT requires the STATE option to be specified without using any other options.", 33223, 16, 1);

    /// <summary>Msg 40517: a target or option a Linux server lacks (<c>URL</c>, <c>EXTERNAL_MONITOR</c>, <c>OPERATOR_AUDIT</c>), named as written.</summary>
    internal static SimulatedSqlException AuditOptionNotSupported(string written) =>
        new($"Keyword or statement option '{written}' is not supported in this version of SQL Server.", 40517, 15, 1);

    /// <summary>Msg 33073: a specification naming an audit that doesn't exist or isn't visible — state 1 in <c>CREATE</c>, 2 in <c>ALTER … FOR SERVER AUDIT</c>.</summary>
    internal static SimulatedSqlException AuditNotFound(string name, byte state) =>
        new($"Cannot find audit '{name}' or you do not have the required permissions.", 33073, 16, state);

    /// <summary>Msg 33073: <c>ALTER</c> or <c>DROP</c> of an audit specification that doesn't exist, or run without its permission.</summary>
    internal static SimulatedSqlException AuditSpecificationNotFound(string name) =>
        new($"Cannot find audit specification '{name}' or you do not have the required permissions.", 33073, 16, 1);

    /// <summary>Msg 33229: a change to an enabled audit specification other than its <c>STATE</c>.</summary>
    internal static SimulatedSqlException AuditSpecificationMustBeDisabledToChange() =>
        new("Changes to an audit specification must be done while the audit specification is disabled.", 33229, 16, 1);

    /// <summary>Msg 33230: a second specification of one scope for an audit — a server one, or another in the same database.</summary>
    internal static SimulatedSqlException AuditSpecificationForAuditExists(string auditName) =>
        new($"An audit specification for audit '{auditName}' already exists.", 33230, 16, 1);

    /// <summary>Msg 33211: an action naming columns (<c>SELECT (a) ON t</c>), as the batch compiles.</summary>
    internal static SimulatedSqlException AuditColumnListNotAllowed() =>
        new("A list of subentities, such as columns, cannot be specified for entity-level audits.", 33211, 15, 1);

    /// <summary>Msg 8169: an <c>AUDIT_GUID</c> that isn't a GUID.</summary>
    internal static SimulatedSqlException AuditGuidInvalid() =>
        new("Conversion failed when converting from a character string to uniqueidentifier.", 8169, 16, 1);

    /// <summary>Msg 33224: <c>sys.fn_get_audit_file</c>'s pattern NULL, or under a directory the server doesn't hold.</summary>
    internal static SimulatedSqlException AuditFilePatternInvalid() =>
        new("The specified pattern did not return any files or does not represent a valid file share. Verify the pattern parameter and rerun the command.", 33224, 16, 4);

    /// <summary>Msg 33225: an initial file and offset naming no record of the files the pattern matches, or one given without the other.</summary>
    internal static SimulatedSqlException AuditFileLocationInvalid() =>
        new("The specified values for initial_file_name and audit_record_offset do not represent a valid location within the audit file set. Verify the file name and offset location, and then rerun the command.", 33225, 16, 5);

    /// <summary>Msg 37620: <c>sys.fn_get_audit_file_v2</c>'s start or end filter (<paramref name="filter"/>) not in the <c>YYYY-MM-DDThh:mm:ssZ</c> form.</summary>
    internal static SimulatedSqlException AuditTimeFilterFormat(string filter) =>
        new($"{filter} is not in a supported format. Please either use DEFAULT or a UTC based timestamp in this format - 'YYYY-MM-DDThh:mm:ssZ'. For example - '2020-12-31T15:30:00Z'.", 37620, 16, 1);
}
