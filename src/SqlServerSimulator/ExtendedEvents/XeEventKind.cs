namespace SqlServerSimulator.ExtendedEvents;

/// <summary>The events the simulator fires, each a slot of <see cref="ExtendedEventsServer.Taken"/>.</summary>
internal enum XeEventKind
{
    ErrorReported,
    SqlBatchStarting,
    SqlBatchCompleted,
    RpcStarting,
    RpcCompleted,
    XmlDeadlockReport,
    LockDeadlock,
    LockDeadlockChain,
    BlockedProcessReport,
    SqlStatementStarting,
    SqlStatementCompleted,
    SpStatementStarting,
    SpStatementCompleted,
    ModuleStart,
    ModuleEnd,
    Login,
    Logout,
    ExistingConnection,
    Attention,
    WaitInfo,
    WaitCompleted,
    LockTimeout,
    LockTimeoutGreaterThan0,
    ObjectCreated,
    ObjectAltered,
    ObjectDeleted,
    DatabaseCreated,
    DatabaseDropped,
    UserEvent,
}

/// <summary>Each <see cref="XeEventKind"/>'s package and name, and whether a session that starts with the server takes it.</summary>
internal static class XeEventKinds
{
    public static readonly (string Package, string Name)[] Names =
    [
        ("sqlserver", "error_reported"),
        ("sqlserver", "sql_batch_starting"),
        ("sqlserver", "sql_batch_completed"),
        ("sqlserver", "rpc_starting"),
        ("sqlserver", "rpc_completed"),
        ("sqlserver", "xml_deadlock_report"),
        ("sqlserver", "lock_deadlock"),
        ("sqlserver", "lock_deadlock_chain"),
        ("sqlserver", "blocked_process_report"),
        ("sqlserver", "sql_statement_starting"),
        ("sqlserver", "sql_statement_completed"),
        ("sqlserver", "sp_statement_starting"),
        ("sqlserver", "sp_statement_completed"),
        ("sqlserver", "module_start"),
        ("sqlserver", "module_end"),
        ("sqlserver", "login"),
        ("sqlserver", "logout"),
        ("sqlserver", "existing_connection"),
        ("sqlserver", "attention"),
        ("sqlos", "wait_info"),
        ("sqlos", "wait_completed"),
        ("sqlserver", "lock_timeout"),
        ("sqlserver", "lock_timeout_greater_than_0"),
        ("sqlserver", "object_created"),
        ("sqlserver", "object_altered"),
        ("sqlserver", "object_deleted"),
        ("sqlserver", "database_created"),
        ("sqlserver", "database_dropped"),
        ("sqlserver", "user_event"),
    ];

    public static int Count => Names.Length;

    /// <summary>
    /// Per kind, whether a session that starts with the server lists it —
    /// <c>system_health</c> takes <c>error_reported</c>,
    /// <c>xml_deadlock_report</c> and <c>wait_info</c> — read off the default
    /// sessions' resource without loading the catalog.
    /// </summary>
    public static readonly bool[] TakenAtStartup = [.. Names.Select(name => DefaultEventSessions.Started.Contains(name.Name))];

    /// <summary>The kind <paramref name="event"/> is, null for one the simulator never fires.</summary>
    public static XeEventKind? Of(XeObject @event)
    {
        for (var i = 0; i < Names.Length; i++)
        {
            if (Names[i].Name == @event.Name && Names[i].Package == @event.Package.Name)
                return (XeEventKind)i;
        }
        return null;
    }
}
