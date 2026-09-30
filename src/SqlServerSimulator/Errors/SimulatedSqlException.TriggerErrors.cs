namespace SqlServerSimulator;

// Database- and server-scope trigger factories: the CREATE-time refusals of
// ON DATABASE / ON ALL SERVER triggers and the logon-trigger login refusal.
// All probed 2026-09-28 against SQL Server 2025.
partial class SimulatedSqlException
{
    /// <summary>
    /// Mimics SQL Server error 1094: a database- or server-scope trigger named
    /// with a schema prefix, at CREATE, DROP, ENABLE or DISABLE.
    /// </summary>
    internal static SimulatedSqlException SchemaPrefixOnScopedTrigger() =>
        new("Cannot specify a schema name as a prefix to the trigger name for database and server level triggers.", 1094, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1083: <c>WITH EXECUTE AS OWNER</c> on a
    /// database- or server-scope trigger, which has no owning object.
    /// </summary>
    internal static SimulatedSqlException ExecuteAsOwnerOnScopedTrigger() =>
        new("OWNER is not a valid option for EXECUTE AS in the context of server and database level triggers.", 1083, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 167: a DML trigger's parent is a local or
    /// global temporary table, raised while the batch parses, so the body's syntax
    /// errors follow it (probed 2026-09-30 against SQL Server 2025).
    /// </summary>
    internal static SimulatedSqlException TriggerOnTemporaryObject() =>
        new("Cannot create trigger on a temporary object.", 167, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1084: an event type the
    /// <c>sys.trigger_event_types</c> catalog doesn't carry, echoed as written.
    /// </summary>
    internal static SimulatedSqlException InvalidEventType(string eventName) =>
        new($"'{eventName}' is an invalid event type.", 1084, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 1098: an event the trigger's scope can't raise —
    /// a DML action on either scope, <c>LOGON</c> or a server-level DDL event
    /// on <c>ON DATABASE</c>.
    /// </summary>
    internal static SimulatedSqlException EventTypeInvalidOnTarget() =>
        new("The specified event type(s) is/are not valid on the specified target object.", 1098, 15, 1);

    /// <summary>
    /// Mimics SQL Server error 15151 for a server-scope trigger whose
    /// <c>WITH EXECUTE AS</c> names a login that doesn't exist.
    /// </summary>
    internal static SimulatedSqlException CannotExecuteAsLogin(string loginName) =>
        new($"Cannot execute as the login '{loginName}', because it does not exist or you do not have permission.", 15151, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 1088 state 119: <c>ENABLE</c> / <c>DISABLE
    /// TRIGGER … ON { DATABASE | ALL SERVER }</c> naming a trigger the scope
    /// doesn't hold.
    /// </summary>
    internal static SimulatedSqlException CannotFindScopedTrigger(string name) =>
        new($"Cannot find the object \"{name}\" because it does not exist or you do not have permissions.", 1088, 16, 119);

    /// <summary>
    /// Mimics SQL Server error 17892 as the client sees it: a logon trigger
    /// failed — rolled back, raised an error, or returned a result set — so the
    /// login is refused. The server logs it at severity 20 but sends it at 14,
    /// the class <c>SqlException</c> reports.
    /// </summary>
    internal static SimulatedSqlException LogonFailedDueToTrigger(string loginName) =>
        new($"Logon failed for login '{loginName}' due to trigger execution.", 17892, 14, 1);

    /// <summary>
    /// Mimics SQL Server error 575: a logon trigger body produced a result set.
    /// Real writes it to the error log and refuses the login with Msg 17892,
    /// which is all the client sees.
    /// </summary>
    internal static SimulatedSqlException LogonTriggerReturnedResultSet() =>
        new("A LOGON trigger returned a resultset. Modify the LOGON trigger to not return resultsets.", 575, 16, 1);

    /// <summary>
    /// Mimics SQL Server error 596, which follows Msg 17892 when a logon
    /// trigger refuses a pooled connection's reset: the session is killed and
    /// the request that carried the reset fails with it.
    /// </summary>
    internal static SimulatedSqlException SessionInKillState() =>
        new("Cannot continue the execution because the session is in the kill state.", 596, 21, 1);
}
