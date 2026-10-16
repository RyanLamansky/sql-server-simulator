namespace SqlServerSimulator;

// SQL Trace error factories: the sp_trace_* procedures, which report each
// refusal both as an error and as their return code (probed 2026-10-16
// against SQL Server 2025); the code each pairs with is on its caller.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>Msg 19051: a trace real can't make for a reason it doesn't name — a black-box trace on Linux, whose file it can't find.</summary>
    internal static SimulatedSqlException TraceUnknownError() =>
        new("Unknown error occurred in the trace.", 19051, 16, 1);

    /// <summary>Msg 19052: a change to a running trace — its events or filters (state 1), or closing it (state 2).</summary>
    internal static SimulatedSqlException TraceIsRunning(byte state) =>
        new("The active trace must be stopped before modification.", 19052, 16, state);

    /// <summary>Msg 19053: an event id no trace event has, or a user-configurable one outside 82 to 91.</summary>
    internal static SimulatedSqlException TraceEventIdInvalid() =>
        new("The trace event ID is not valid.", 19053, 16, 1);

    /// <summary>Msg 19054: a column id no trace column has, or for a filter a column real doesn't filter on.</summary>
    internal static SimulatedSqlException TraceColumnIdInvalid() =>
        new("The trace column ID is not valid.", 19054, 16, 1);

    /// <summary>Msg 19055: a filter on a column whose filters another column's came between.</summary>
    internal static SimulatedSqlException TraceFiltersNotGrouped() =>
        new("Filters with the same event column ID must be grouped together.", 19055, 16, 1);

    /// <summary>Msg 19056: a comparison operator outside 0 to 7, one the column's type doesn't take, or a NULL or empty value it can't compare.</summary>
    internal static SimulatedSqlException TraceComparisonOperatorInvalid() =>
        new("The comparison operator in the filter is not valid.", 19056, 16, 1);

    /// <summary>Msg 19057: a logical operator other than 0 (AND) or 1 (OR).</summary>
    internal static SimulatedSqlException TraceLogicalOperatorInvalid() =>
        new("The boolean operator in the filter is not valid.", 19057, 16, 1);

    /// <summary>Msg 19058: a negative status.</summary>
    internal static SimulatedSqlException TraceStatusInvalid() =>
        new("The trace status is not valid.", 19058, 16, 2);

    /// <summary>Msg 19059: a trace id no trace holds.</summary>
    internal static SimulatedSqlException TraceNotFound() =>
        new("Could not find the requested trace.", 19059, 16, 1);

    /// <summary>Msg 19060: options outside the four real defines, or a combination it refuses.</summary>
    internal static SimulatedSqlException TraceOptionInvalid() =>
        new("The trace option is not valid.", 19060, 16, 1);

    /// <summary>Msg 19061: turning an event's <c>SPID</c> column off.</summary>
    internal static SimulatedSqlException TraceSpidColumnRequired() =>
        new("Cannot remove SPID trace column.", 19061, 16, 1);

    /// <summary>Msg 19062: a trace file real can't create, after Msg 19097 names the operating system's error.</summary>
    internal static SimulatedSqlException TraceFileNotCreated() =>
        new("Could not create a trace file.", 19062, 16, 1);

    /// <summary>Msg 19064: a stop time already past.</summary>
    internal static SimulatedSqlException TraceStopTimePassed() =>
        new("The requested trace stop time has been already passed.", 19064, 16, 1);

    /// <summary>Msg 19065: a parameter whose value real refuses — no file name, a size or file count out of range, a rowset or black-box trace given what it can't take.</summary>
    internal static SimulatedSqlException TraceParameterInvalid() =>
        new("The parameter is not valid.", 19065, 16, 1);

    /// <summary>Msg 19070: a change to the default trace — stopping it (state 1), its events (2) or its filters (3).</summary>
    internal static SimulatedSqlException DefaultTraceNotModifiable(byte state) =>
        new("The default trace cannot be stopped or modified. Use SP_CONFIGURE to turn it off.", 19070, 16, state);

    /// <summary>
    /// Msg 19097, the informational message <c>sp_trace_create</c> sends ahead
    /// of Msg 19062 or 19051, naming the operating system's error:
    /// <c>0x80070050(The file exists.)</c>, <c>0x80070005(Access is denied.)</c>
    /// or <c>0x80070002(The system cannot find the file specified.)</c>.
    /// </summary>
    internal static SimulatedError TraceOperatingSystemErrorMessage(Parser.BatchContext batch, string error) =>
        SystemProcedureMessage(batch, "sp_trace_create", 1, 19097, $"Operating system error occurred while running SP_TRACE_CREATE. Error = {error}.");
}
