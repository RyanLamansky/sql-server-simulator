using System.Globalization;
using SqlServerSimulator.Parser;
using SqlServerSimulator.Storage;
using SqlServerSimulator.Trace;

namespace SqlServerSimulator;

// SQL Trace's procedures — sp_trace_create, sp_trace_setevent,
// sp_trace_setfilter, sp_trace_setstatus and sp_trace_generateevent — and
// the server's trace state; see docs/claude/trace.md. Each is an extended
// procedure: its arguments bind by position whatever name they're given, each
// must be of the exact type it takes, and a refusal raises its error and sets
// the return code real's documentation lists (probed 2026-10-16 against SQL
// Server 2025).
partial class Simulation
{
    private SqlTraceServer? traces;

    /// <summary>
    /// Per <see cref="ExtendedEvents.XeEventKind"/>, whether a running trace
    /// takes it; null while no trace runs, so a firing site pays one null
    /// check.
    /// </summary>
    internal volatile bool[]? TraceTaken;

    /// <summary>The server's trace state, made on first use.</summary>
    internal SqlTraceServer TracesFor()
    {
        if (Volatile.Read(ref this.traces) is { } existing)
            return existing;
        var created = new SqlTraceServer();
        return Interlocked.CompareExchange(ref this.traces, created, null) ?? created;
    }

    /// <summary>The server's trace state if anything has made it, null before.</summary>
    internal SqlTraceServer? TracesIfMade => Volatile.Read(ref this.traces);

    /// <summary>One parameter of a trace procedure: its name, the type it takes as real's Msg 214 spells it, and whether it takes NULL.</summary>
    private readonly struct TraceParameter(string name, TraceParameterType type, bool nullable, int length = 0)
    {
        public readonly string Name = name;
        public readonly TraceParameterType Type = type;
        public readonly bool Nullable = nullable;

        /// <summary>The longest string an <c>nvarchar</c> parameter takes.</summary>
        public readonly int Length = length;
    }

    private enum TraceParameterType
    {
        Int,
        BigInt,
        Bit,
        DateTime,
        NVarchar,
        Varbinary,
        UniqueIdentifier,
    }

    private static readonly TraceParameter[] TraceCreateParameters =
    [
        new("@traceid", TraceParameterType.Int, nullable: true),
        new("@options", TraceParameterType.Int, nullable: false),
        new("@tracefile", TraceParameterType.NVarchar, nullable: true, 256),
        new("@maxfilesize", TraceParameterType.BigInt, nullable: true),
        new("@stoptime", TraceParameterType.DateTime, nullable: true),
        new("@filecount", TraceParameterType.Int, nullable: true),
    ];

    private static readonly TraceParameter[] TraceSetEventParameters =
    [
        new("@traceid", TraceParameterType.Int, nullable: false),
        new("@eventid", TraceParameterType.Int, nullable: false),
        new("@columnid", TraceParameterType.Int, nullable: true),
        new("@on", TraceParameterType.Bit, nullable: false),
    ];

    private static readonly TraceParameter[] TraceSetFilterParameters =
    [
        new("@traceid", TraceParameterType.Int, nullable: false),
        new("@columnid", TraceParameterType.Int, nullable: true),
        new("@logical_operator", TraceParameterType.Int, nullable: false),
        new("@comparison_operator", TraceParameterType.Int, nullable: false),
        new("@value", TraceParameterType.Int, nullable: true),
    ];

    private static readonly TraceParameter[] TraceSetStatusParameters =
    [
        new("@traceid", TraceParameterType.Int, nullable: false),
        new("@status", TraceParameterType.Int, nullable: false),
    ];

    private static readonly TraceParameter[] TraceGenerateEventParameters =
    [
        new("@eventid", TraceParameterType.Int, nullable: false),
        new("@userinfo", TraceParameterType.NVarchar, nullable: true, 128),
        new("@userdata", TraceParameterType.Varbinary, nullable: true),
    ];

    /// <summary>
    /// Binds a trace procedure's arguments by position, whatever name each is
    /// given: too many is Msg 8144, which ends the batch, and a required one
    /// missing Msg 201 — state 15 for the first parameter, 17 for a later one.
    /// Then the permission check: <c>ALTER TRACE</c>, or Msg 8189 naming
    /// <paramref name="surface"/> at <paramref name="permissionState"/>.
    /// </summary>
    private static ProcArgument?[] BindTraceArguments(BatchContext batch, string procedure, string surface, List<ProcArgument> arguments, TraceParameter[] parameters, int required, byte tooManyState, byte permissionState)
    {
        if (arguments.Count > parameters.Length)
            throw AtProcedureLine(SimulatedSqlException.TooManyArgumentsToFunction(procedure, tooManyState).EndingBatch(), procedure, 1);
        var bound = new ProcArgument?[parameters.Length];
        for (var i = 0; i < arguments.Count; i++)
            bound[i] = arguments[i];
        for (var i = 0; i < required; i++)
        {
            if (bound[i] is null)
                throw AtProcedureLine(SimulatedSqlException.ProcedureExpectsParameter(procedure, parameters[i].Name[1..], i == 0 ? (byte)15 : (byte)17), procedure, 1);
        }
        if (procedure == "sp_trace_create" && bound[0]!.Value.OutputSlot is null)
            throw AtProcedureLine(SimulatedSqlException.FormalParameterNotOutput(procedure, "@traceid", state: 57), procedure, 1);
        if (!batch.Connection.Simulation.SessionHoldsServerPermission(batch.Connection, Permission.AlterTrace))
            throw AtProcedureLine(SimulatedSqlException.TracePermissionDenied(surface, permissionState), procedure, 1);
        return bound;
    }

    /// <summary>
    /// Whether <paramref name="argument"/> fits a parameter taking <paramref name="type"/>:
    /// an argument of exactly its type — <c>@on</c> takes any integer type,
    /// <c>@stoptime</c> <c>datetime2</c> and <c>datetimeoffset</c> too, and a
    /// string no longer than its length — or the <c>NULL</c> keyword or a
    /// NULL of its type where it takes NULL. <c>DEFAULT</c> fits nothing
    /// (probed 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static bool FitsTraceParameter(ProcArgument argument, TraceParameterType type, bool nullable, int length)
    {
        if (argument.IsDefault)
            return false;
        if (argument.IsUntypedNull)
            return nullable;
        var value = argument.Value;
        if (value.IsNull && !nullable)
            return false;
        return type switch
        {
            TraceParameterType.Int => value.Type is Int32SqlType,
            TraceParameterType.BigInt => value.Type is BigIntSqlType,
            TraceParameterType.Bit => value.Type is BitSqlType or TinyIntSqlType or SmallIntSqlType or Int32SqlType or BigIntSqlType,
            TraceParameterType.DateTime => value.Type is DateTimeSqlType or DateTime2SqlType or DateTimeOffsetSqlType,
            TraceParameterType.NVarchar => value.Type is NVarcharSqlType or NTextSqlType && (value.IsNull || value.AsString.Length <= length),
            TraceParameterType.Varbinary => value.Type is VarbinarySqlType or BinarySqlType or ImageSqlType,
            _ => value.Type is UniqueIdentifierSqlType,
        };
    }

    /// <summary>The type Msg 214 names, and its state: 4 for a string, 5 for the rest.</summary>
    private static (string Name, byte State) TraceTypeName(TraceParameterType type, int length) => type switch
    {
        TraceParameterType.Int => ("int", 5),
        TraceParameterType.BigInt => ("bigint", 5),
        TraceParameterType.Bit => ("bit", 5),
        TraceParameterType.DateTime => ("datetime", 5),
        TraceParameterType.NVarchar => (string.Create(CultureInfo.InvariantCulture, $"nvarchar({length})"), 4),
        TraceParameterType.Varbinary => ("varbinary", 5),
        _ => ("uniqueidentifier", 5),
    };

    /// <summary>
    /// Checks each supplied argument against its parameter in order, raising
    /// Msg 214 for the first that doesn't fit, after setting the return code
    /// to 15 as real does.
    /// </summary>
    private static void CheckTraceArgumentTypes(BatchContext batch, string procedure, ProcArgument?[] bound, TraceParameter[] parameters, int count, string? returnCodeVariableName)
    {
        for (var i = 0; i < count; i++)
        {
            var parameter = parameters[i];
            if (bound[i] is not { } argument)
                continue;
            if (i == 0 && procedure == "sp_trace_create")
            {
                if (argument.OutputSlot?.DeclaredType is not Int32SqlType)
                    throw TraceTypeRefusal(batch, procedure, parameter.Name, parameter.Type, parameter.Length, returnCodeVariableName);
                continue;
            }
            if (!FitsTraceParameter(argument, parameter.Type, parameter.Nullable, parameter.Length))
                throw TraceTypeRefusal(batch, procedure, parameter.Name, parameter.Type, parameter.Length, returnCodeVariableName);
        }
    }

    private static SimulatedSqlException TraceTypeRefusal(BatchContext batch, string procedure, string parameterName, TraceParameterType type, int length, string? returnCodeVariableName)
    {
        var (name, state) = TraceTypeName(type, length);
        return TraceRefusal(batch, procedure, returnCodeVariableName, 15, SimulatedSqlException.ProcedureExpectsParameterOfType(parameterName, name, state));
    }

    /// <summary>
    /// Sets the return code real pairs with <paramref name="error"/>, which the
    /// caller throws — unless a <c>TRY</c> will catch the error, which leaves
    /// the return code unassigned as the call never completes (probed
    /// 2026-10-16 against SQL Server 2025).
    /// </summary>
    private static SimulatedSqlException TraceRefusal(BatchContext batch, string procedure, string? returnCodeVariableName, int code, SimulatedSqlException error)
    {
        if (batch.Connection.OpenTryFrames == 0)
            SetProcedureReturnCode(batch, returnCodeVariableName, code);
        return AtProcedureLine(error, procedure, 1);
    }

    private static int TraceInt(ProcArgument? argument) =>
        argument is { IsUntypedNull: false, Value.IsNull: false } given ? given.Value.AsInt32 : 0;

    /// <summary>
    /// The trace <paramref name="traceId"/> names for a change: the default
    /// trace is Msg 19070 (return code 20) at <paramref name="defaultState"/>,
    /// any other id no trace holds Msg 19059 (9).
    /// </summary>
    private static UserTrace FindTraceForChange(BatchContext batch, string procedure, string? returnCodeVariableName, int traceId, byte defaultState)
    {
        var simulation = batch.Connection.Simulation;
        if (traceId == 1 && simulation.DefaultTraceEnabled)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 20, SimulatedSqlException.DefaultTraceNotModifiable(defaultState));
        return simulation.TracesIfMade?.Find(traceId)
            ?? throw TraceRefusal(batch, procedure, returnCodeVariableName, 9, SimulatedSqlException.TraceNotFound());
    }

    /// <summary>
    /// <c>sp_trace_create @traceid OUTPUT, @options [, @tracefile
    /// [, @maxfilesize [, @stoptime [, @filecount]]]]</c>: makes a stopped trace
    /// writing <c>@tracefile</c> with <c>.trc</c> appended — whatever
    /// extension it carries — and answers its id, the lowest above the
    /// default trace's that no trace holds. Options are
    /// <c>TRACE_FILE_ROLLOVER</c> (2) and <c>SHUTDOWN_ON_ERROR</c> (4);
    /// <c>TRACE_PRODUCE_ROWSET</c> (1) is refused, and
    /// <c>TRACE_PRODUCE_BLACKBOX</c> (8) fails as it does on real's Linux
    /// server, which can't find its file.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTraceCreate(BatchContext batch, string? returnCodeVariableName)
    {
        const string procedure = "sp_trace_create";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var bound = BindTraceArguments(batch, procedure, "SP_TRACE_CREATE", arguments, TraceCreateParameters, 2, tooManyState: 4, permissionState: 6);
        CheckTraceArgumentTypes(batch, procedure, bound, TraceCreateParameters, TraceCreateParameters.Length, returnCodeVariableName);
        var output = bound[0]!.Value.OutputSlot!;

        var connection = batch.Connection;
        var now = batch.CurrentStatement.UtcNow;
        DateTime? stopTime = bound[4] is { IsUntypedNull: false, Value.IsNull: false } stop ? stop.Value.CoerceTo(SqlType.DateTime).AsDateTime : null;
        if (stopTime is { } stopAt && stopAt <= now)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 14, SimulatedSqlException.TraceStopTimePassed());
        output.Value = SqlValue.FromInt32(0);

        var options = TraceInt(bound[1]);
        if ((options & ~15) != 0 || ((options & 8) != 0 && options != 8) || ((options & 1) != 0 && options != 1))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 10, SimulatedSqlException.TraceOptionInvalid());
        var file = bound[2] is { IsUntypedNull: false, Value.IsNull: false } given ? given.Value.AsString : null;
        if (options == 1 || (options == 8 && file is not null))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 15, SimulatedSqlException.TraceParameterInvalid());
        if (options == 8)
        {
            yield return Printed(SimulatedSqlException.TraceOperatingSystemErrorMessage(batch, "0x80070002(The system cannot find the file specified.)"));
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 1, SimulatedSqlException.TraceUnknownError());
        }
        var rollover = (options & 2) != 0;
        var maxSize = bound[3] is { IsUntypedNull: false, Value.IsNull: false } size ? size.Value.AsInt64 : 5;
        int? fileCount = bound[5] is { IsUntypedNull: false, Value.IsNull: false } files ? files.Value.AsInt32 : null;
        if (string.IsNullOrEmpty(file) || maxSize <= 0 || (fileCount is { } count && (!rollover || count is 1 or < 0)))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 15, SimulatedSqlException.TraceParameterInvalid());

        var simulation = connection.Simulation;
        var server = simulation.TracesFor();
        var path = UserTrace.FilePath(file, 0);
        if (CanonicalServerPath(path) is { } canonical)
        {
            string? osError = null;
            if (ServerParentDirectory(canonical) is not { } directory || !simulation.IsServerDirectory(directory))
                osError = "0x80070005(Access is denied.)";
            else if (simulation.IsServerFile(canonical) || server.Files.ContainsKey(path))
                osError = "0x80070050(The file exists.)";
            if (osError is not null)
            {
                yield return Printed(SimulatedSqlException.TraceOperatingSystemErrorMessage(batch, osError));
                throw TraceRefusal(batch, procedure, returnCodeVariableName, 12, SimulatedSqlException.TraceFileNotCreated());
            }
        }
        else if (server.Files.ContainsKey(path))
        {
            yield return Printed(SimulatedSqlException.TraceOperatingSystemErrorMessage(batch, "0x80070050(The file exists.)"));
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 12, SimulatedSqlException.TraceFileNotCreated());
        }

        var trace = server.Add(id => new UserTrace(id, options, file, maxSize, stopTime, rollover ? fileCount ?? 0 : 1, connection.Security.OriginalLoginName));
        trace.CreateFile(server);
        output.Value = SqlValue.FromInt32(trace.Id);
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>sp_trace_setevent @traceid, @eventid, @columnid, @on</c>: turns one
    /// column of one event on or off on a stopped trace; a NULL column turns
    /// the event on with no columns, or off. <c>SPID</c> is every event's and
    /// can't be turned off.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTraceSetEvent(BatchContext batch, string? returnCodeVariableName)
    {
        const string procedure = "sp_trace_setevent";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var bound = BindTraceArguments(batch, procedure, "SP_TRACE_SETEVENT", arguments, TraceSetEventParameters, 4, tooManyState: 4, permissionState: 8);
        CheckTraceArgumentTypes(batch, procedure, bound, TraceSetEventParameters, TraceSetEventParameters.Length, returnCodeVariableName);
        var trace = FindTraceForChange(batch, procedure, returnCodeVariableName, TraceInt(bound[0]), defaultState: 2);
        if (trace.IsRunning)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 2, SimulatedSqlException.TraceIsRunning(1));
        var eventId = TraceInt(bound[1]);
        var catalog = TraceCatalog.Instance;
        if (!catalog.IsEvent(eventId))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 3, SimulatedSqlException.TraceEventIdInvalid());
        int? columnId = bound[2] is { IsUntypedNull: false, Value.IsNull: false } column ? column.Value.AsInt32 : null;
        var on = bound[3]!.Value.Value.CoerceTo(SqlType.BigInt).AsInt64 != 0;
        if (columnId is { } id && catalog.Column(id) is null)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 4, SimulatedSqlException.TraceColumnIdInvalid());
        if (columnId == 12 && !on)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 11, SimulatedSqlException.TraceSpidColumnRequired());
        trace.SetEvent(eventId, columnId, on);
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>sp_trace_setfilter @traceid, @columnid, @logical_operator,
    /// @comparison_operator, @value</c>: adds a filter to a stopped trace, its
    /// value of exactly the column's type; a NULL column clears every filter.
    /// A number or time takes the six orderings, a string <c>=</c>,
    /// <c>&lt;&gt;</c>, <c>LIKE</c> and <c>NOT LIKE</c>, a GUID <c>=</c> and
    /// <c>&lt;&gt;</c>, and NULL only <c>=</c> and <c>&lt;&gt;</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTraceSetFilter(BatchContext batch, string? returnCodeVariableName)
    {
        const string procedure = "sp_trace_setfilter";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var bound = BindTraceArguments(batch, procedure, "SP_TRACE_SETFILTER", arguments, TraceSetFilterParameters, 5, tooManyState: 4, permissionState: 9);
        // The value's type is the column's, judged once the column is known.
        CheckTraceArgumentTypes(batch, procedure, bound, TraceSetFilterParameters, 4, returnCodeVariableName);
        var trace = FindTraceForChange(batch, procedure, returnCodeVariableName, TraceInt(bound[0]), defaultState: 3);
        if (trace.IsRunning)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 2, SimulatedSqlException.TraceIsRunning(1));
        if (bound[1] is not { IsUntypedNull: false, Value.IsNull: false } columnArgument)
        {
            trace.ClearFilters();
            SetProcedureReturnCode(batch, returnCodeVariableName, 0);
            yield break;
        }
        var columnId = columnArgument.Value.AsInt32;
        var column = TraceCatalog.Instance.Column(columnId);
        // Real types a column past the last as binary, and refuses it after.
        var valueType = column?.TypeName switch
        {
            null when columnId == TraceCatalog.MaxColumnId + 1 => TraceParameterType.Varbinary,
            null => (TraceParameterType?)null,
            "int" => TraceParameterType.Int,
            "bigint" => TraceParameterType.BigInt,
            "datetime" => TraceParameterType.DateTime,
            "image" => TraceParameterType.Varbinary,
            "uniqueidentifier" => TraceParameterType.UniqueIdentifier,
            _ => TraceParameterType.NVarchar,
        };
        if (valueType is not { } type)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 4, SimulatedSqlException.TraceColumnIdInvalid());
        var argument = bound[4]!.Value;
        if (!FitsTraceParameter(argument, type, nullable: true, 256))
            throw TraceTypeRefusal(batch, procedure, "@value", type, 256, returnCodeVariableName);
        if (column is not { IsFilterable: true })
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 4, SimulatedSqlException.TraceColumnIdInvalid());
        var logical = TraceInt(bound[2]);
        if (logical is not (0 or 1))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 7, SimulatedSqlException.TraceLogicalOperatorInvalid());
        var comparison = TraceInt(bound[3]);
        var value = argument.IsUntypedNull ? SqlValue.Null(SqlType.Int32) : argument.Value;
        var takes = type switch
        {
            TraceParameterType.Int or TraceParameterType.BigInt or TraceParameterType.DateTime => comparison is >= 0 and <= 5,
            TraceParameterType.NVarchar => comparison is 0 or 1 or 6 or 7,
            TraceParameterType.UniqueIdentifier => comparison is 0 or 1,
            _ => false,
        };
        if (!takes || ((value.IsNull || (comparison >= 6 && value.AsString.Length == 0)) && comparison > 1))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 6, SimulatedSqlException.TraceComparisonOperatorInvalid());
        if (!trace.FiltersGrouped(columnId))
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 5, SimulatedSqlException.TraceFiltersNotGrouped());
        if (type == TraceParameterType.DateTime && !value.IsNull)
            value = value.CoerceTo(SqlType.DateTime);
        trace.AddFilter(new TraceFilter(columnId, logical, comparison, value));
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>sp_trace_setstatus @traceid, @status</c>: 0 stops a trace, 1 starts
    /// it, 2 closes a stopped one; a start and a stop each write the error log
    /// line real writes, and starting or stopping a trace already so does
    /// nothing. A status past 2 is taken and does nothing, a negative one is
    /// Msg 19058.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTraceSetStatus(BatchContext batch, string? returnCodeVariableName)
    {
        const string procedure = "sp_trace_setstatus";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var bound = BindTraceArguments(batch, procedure, "SP_TRACE_SETSTATUS", arguments, TraceSetStatusParameters, 2, tooManyState: 4, permissionState: 7);
        CheckTraceArgumentTypes(batch, procedure, bound, TraceSetStatusParameters, TraceSetStatusParameters.Length, returnCodeVariableName);
        var trace = FindTraceForChange(batch, procedure, returnCodeVariableName, TraceInt(bound[0]), defaultState: 1);
        var status = TraceInt(bound[1]);
        if (status < 0)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 8, SimulatedSqlException.TraceStatusInvalid());
        var connection = batch.Connection;
        var simulation = connection.Simulation;
        var server = simulation.TracesFor();
        var now = batch.CurrentStatement.UtcNow;
        var process = ErrorLog.SessionProcess(connection.Spid);
        var login = connection.Security.OriginalLoginName;
        switch (status)
        {
            case 0:
                if (trace.Stop(simulation, server, now))
                    simulation.ErrorLog.Write(now, process, string.Create(CultureInfo.InvariantCulture, $"SQL Trace stopped. Trace ID = '{trace.Id}'. Login Name = '{login}'."));
                break;
            case 1:
                if (!trace.IsRunning)
                {
                    simulation.ErrorLog.Write(now, process, string.Create(CultureInfo.InvariantCulture, $"SQL Trace ID {trace.Id} was started by login \"{login}\"."));
                    trace.Start(simulation, server, now);
                }
                break;
            case 2:
                if (trace.IsRunning)
                    throw TraceRefusal(batch, procedure, returnCodeVariableName, 2, SimulatedSqlException.TraceIsRunning(2));
                server.Remove(trace);
                break;
        }
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }

    /// <summary>
    /// <c>sp_trace_generateevent @eventid [, @userinfo [, @userdata]]</c>:
    /// fires user-configurable event <c>@eventid</c> (82 to 91) — the
    /// <c>user_event</c> Extended Event, which a trace reads as
    /// <c>UserConfigurable:n</c> with <c>@userinfo</c> its <c>TextData</c> and
    /// <c>@userdata</c> its <c>BinaryData</c>.
    /// </summary>
    private static IEnumerable<SimulatedStatementOutcome> InvokeSpTraceGenerateEvent(BatchContext batch, string? returnCodeVariableName)
    {
        const string procedure = "sp_trace_generateevent";
        var arguments = ParseExecArguments(batch.Parser, batch);
        if (batch.IsSkipping)
            yield break;
        var bound = BindTraceArguments(batch, procedure, "SP_TRACE_GENERATEEVENT", arguments, TraceGenerateEventParameters, 1, tooManyState: 1, permissionState: 10);
        CheckTraceArgumentTypes(batch, procedure, bound, TraceGenerateEventParameters, TraceGenerateEventParameters.Length, returnCodeVariableName);
        var eventId = TraceInt(bound[0]);
        if (eventId is < 82 or > 91)
            throw TraceRefusal(batch, procedure, returnCodeVariableName, 3, SimulatedSqlException.TraceEventIdInvalid());
        var info = bound[1] is { IsUntypedNull: false, Value.IsNull: false } given ? given.Value.AsString : "";
        var data = bound[2] is { IsUntypedNull: false, Value.IsNull: false } bytes ? bytes.Value.AsBytes : [];
        ExtendedEvents.XeEvents.Fire(batch.Connection, ExtendedEvents.XeEventKind.UserEvent, @event => ExtendedEvents.XeEvents.Data(@event,
            ("event_id", ExtendedEvents.XeValue.FromSigned(eventId)),
            ("user_info", ExtendedEvents.XeValue.FromUnicode(info)),
            ("user_data", ExtendedEvents.XeValue.FromBinary(data))));
        SetProcedureReturnCode(batch, returnCodeVariableName, 0);
    }
}
