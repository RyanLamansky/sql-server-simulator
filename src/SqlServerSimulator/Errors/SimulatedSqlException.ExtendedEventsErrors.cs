namespace SqlServerSimulator;

// Extended Events error factories: CREATE / ALTER / DROP EVENT SESSION, the
// names, attributes, predicates and options under them (probed 2026-10-16
// against SQL Server 2025). Real fills a %S_MSG slot with what kind of object
// it means — "event", "event action", "target", "customizable attribute",
// "event attribute or predicate source", "event predicate comparator" — which
// the callers pass as `kind`.
//
// A plain comment rather than a doc comment: this type is public, and the
// compiler concatenates every partial's <summary> into the one the consumer
// reads in IntelliSense.
partial class SimulatedSqlException
{
    /// <summary>
    /// Msg 15151: <c>ALTER</c> (<paramref name="verb"/> <c>alter</c>) or
    /// <c>DROP EVENT SESSION</c> naming a session that doesn't exist — state
    /// 28 and 26 — or one the session may not change: 27 for a drop, and for
    /// an alter 20 to start it, 19 to stop it, 21 for its options, 22 to add
    /// an event, 23 to drop one, 24 to add a target and 25 to drop one.
    /// </summary>
    internal static SimulatedSqlException CannotAlterOrDropEventSession(string verb, string name, byte state) =>
        new($"Cannot {verb} the event session '{name}', because it does not exist or you do not have permission.", 15151, 16, state);

    /// <summary>Msg 25623: an event (state 1), action (3) or target (2, severity 15) that no package holds, or one real keeps private.</summary>
    internal static SimulatedSqlException XeObjectNameInvalid(string kind, string name, byte state) =>
        new($"The {kind} name, \"{name}\", is invalid, or the object could not be found", 25623, state == 2 ? (byte)15 : (byte)16, state);

    /// <summary>Msg 25625: an <c>ALTER EVENT SESSION … ADD</c> of an event (state 21) or target (20) the session has.</summary>
    internal static SimulatedSqlException XeObjectAlreadyInSession(string kind, string name, byte state) =>
        new($"The {kind}, \"{name}\", already exists in the event session. Object cannot be added to the event session.", 25625, 16, state);

    /// <summary>Msg 25626: one statement adding an event (state 21) or target (20) twice.</summary>
    internal static SimulatedSqlException XeObjectSpecifiedMultipleTimes(string kind, string name, byte state) =>
        new($"The {kind}, \"{name}\", was specified multiple times.", 25626, 16, state);

    /// <summary>Msg 25629: a <c>SET</c> naming an attribute the event or target doesn't have.</summary>
    internal static SimulatedSqlException XeAttributeMissing(string kind, string name, string attribute) =>
        new($"For {kind}, \"{name}\", the customizable attribute, \"{attribute}\", does not exist.", 25629, 16, 1);

    /// <summary>Msg 25631: <c>CREATE EVENT SESSION</c> naming a session that exists.</summary>
    internal static SimulatedSqlException EventSessionAlreadyExists(string name) =>
        new($"The event session, \"{name}\", already exists.  Choose a unique name for the event session.", 25631, 16, 1);

    /// <summary>Msg 25632: a <c>MAX_MEMORY</c> under the 195,993 bytes a session's buffers need.</summary>
    internal static SimulatedSqlException XeBufferTooSmall() =>
        new("The specified buffer size is less than the minimum size. The minimum allowed size is 195993 bytes.", 25632, 16, 29);

    /// <summary>Msg 25635: an asynchronous target in a session whose <c>MAX_MEMORY</c> is 0.</summary>
    internal static SimulatedSqlException XeAsynchronousTargetWithoutMemory() =>
        new("An attempt was made to add an asynchronous target to a session with a maximum memory of 0. For asynchronous targets to be added to a session, the session must have a maximum memory greater than 0.", 25635, 16, 20);

    /// <summary>Msg 25636: a comparator named as a function whose type isn't the source's.</summary>
    internal static SimulatedSqlException XePredicateTypesMismatch() =>
        new("Source and comparator types of the predicate do not match.", 25636, 16, 30);

    /// <summary>Msg 25640: a nonzero <c>MAX_EVENT_SIZE</c> smaller than one of the session's regular buffers.</summary>
    internal static SimulatedSqlException XeMaxEventSizeTooSmall() =>
        new("Maximum event size is smaller than configured event session memory. Specify a larger value for maximum event size, or specify 0.", 25640, 16, 19);

    /// <summary>Msg 25641: a target attribute its target refuses, with real's explanation after it.</summary>
    internal static SimulatedSqlException XeTargetParameterInvalid(string target, string parameter, string detail) =>
        new($"For target, \"{target}\", the parameter \"{parameter}\" passed is invalid. {detail}", 25641, 16, 0);

    /// <summary>Msg 25642: a target added without an attribute it requires (<c>event_file</c>'s <c>filename</c>).</summary>
    internal static SimulatedSqlException XeMandatoryAttributeMissing(string target) =>
        new($"Mandatory customizable attributes are missing for target, \"{target}\".", 25642, 16, 23);

    /// <summary>Msg 25643: an event that never blocks its caller in a session that may lose no event.</summary>
    internal static SimulatedSqlException XeEventNeedsEventLoss(string name) =>
        new($"The event, \"{name}\", can not be added to an event session that specifies no event loss.", 25643, 16, 21);

    /// <summary>Msg 25650: one <c>SET</c> clause naming an attribute twice, state 23 for a target and 24 for an event.</summary>
    internal static SimulatedSqlException XeAttributeSpecifiedMultipleTimes(string kind, string name, string attribute, byte state) =>
        new($"For {kind}, \"{name}\" the customizable attribute, \"{attribute}\", was specified multiple times.", 25650, 16, state);

    /// <summary>Msg 25653: an <c>ALTER</c> of a running session dropping an event or target it doesn't have.</summary>
    internal static SimulatedSqlException XeObjectNotInSession(string kind, string name) =>
        new($"The {kind}, \"{name}\", does not exist in the event session. Object cannot be dropped from the event session.", 25653, 16, 22);

    /// <summary>Msg 25658: an event named without its package that more than one package holds.</summary>
    internal static SimulatedSqlException XeEventNameNotUnique(string name) =>
        new($"The event name \"{name}\" is not unique.", 25658, 16, 1);

    /// <summary>Msg 25701: a session named like a temporary object, refused as the batch compiles.</summary>
    internal static SimulatedSqlException TemporaryEventSessionName(string name) =>
        new($"Invalid event session name \"{name}\". Temporary event sessions are not allowed.", 25701, 15, 1);

    /// <summary>Msg 25702: one <c>WITH</c> list setting an option twice, named in lower case.</summary>
    internal static SimulatedSqlException EventSessionOptionRepeated(string option) =>
        new($"The event session option, \"{option}\", is set more than once. Remove the duplicate session option and re-issue the statement.", 25702, 16, 1);

    /// <summary>
    /// Msg 25703: an option value real's grammar refuses as the batch
    /// compiles — state 2 for an unknown retention mode, 4 for a dispatch
    /// latency without its unit, 6 for a zero duration and 7 for a memory
    /// size without its unit.
    /// </summary>
    internal static SimulatedSqlException EventSessionOptionInvalid(string option, byte state) =>
        new($"The event session option, \"{option}\", has an invalid value. Correct the value and re-issue the statement.", 25703, 16, state);

    /// <summary>Msg 25704: <c>STATE = STOP</c> for a session that isn't running.</summary>
    internal static SimulatedSqlException EventSessionAlreadyStopped() =>
        new("The event session has already been stopped.", 25704, 16, 1);

    /// <summary>Msg 25705: <c>STATE = START</c> for a session already running.</summary>
    internal static SimulatedSqlException EventSessionAlreadyStarted() =>
        new("The event session has already been started.", 25705, 16, 1);

    /// <summary>Msg 25706: a name that resolves to nothing — a predicate source (state 8), a comparator (2), an event (4) or target (5) a stopped session's <c>ALTER</c> drops.</summary>
    internal static SimulatedSqlException XeObjectNotFound(string kind, string name, byte state) =>
        new($"The {kind}, \"{name}\", could not be found.", 25706, 16, state);

    /// <summary>Msg 25707: an option other than <c>STARTUP_STATE</c> changed on a running session.</summary>
    internal static SimulatedSqlException EventSessionOptionWhileRunning(string option) =>
        new($"Event session option \"{option}\" cannot be changed while the session is running. Stop the event session before changing this session option.", 25707, 16, 1);

    /// <summary>Msg 25708: a dispatch latency (state 1) or memory size (2) past its limit.</summary>
    internal static SimulatedSqlException EventSessionOptionTooLarge(string option, byte state) =>
        new($"The \"{option}\" specified exceeds the maximum allowed value. Specify a smaller configuration value.", 25708, 16, state);

    /// <summary>Msg 25712: <c>LIKE</c> against a source that isn't a string.</summary>
    internal static SimulatedSqlException XeInvalidComparisonOperator() =>
        new("An invalid comparison operator was specified for an event predicate.", 25712, 16, 1);

    /// <summary>Msg 25713: a constant that doesn't convert to the type of what it's compared with or assigned to; the state says how it failed.</summary>
    internal static SimulatedSqlException XeValueInvalid(string kind, string name, string ownerKind, string owner, byte state) =>
        new($"The value specified for {kind}, \"{name}\", {ownerKind}, \"{owner}\", is invalid.", 25713, 16, state);

    /// <summary>Msg 25715: an operator a source's type doesn't define (<c>&gt;</c> on a boolean, any on <c>xml</c>).</summary>
    internal static SimulatedSqlException XeOperatorNotDefined(string @event, string op, string type, string source) =>
        new($"The predicate on event \"{@event}\" is invalid.  Operator '{op}' is not defined for type \"{type}\", event attribute or predicate source: \"{source}\".", 25715, 16, 2);

    /// <summary>Msg 25718: <c>sys.fn_xe_file_target_read_file</c> given no path, named <c>(null)</c>.</summary>
    internal static SimulatedSqlException XeLogFileNameInvalid(string name) =>
        new($"The log file name \"{name}\" is invalid.  Verify that the file exists and that the SQL Server service account has access to it.", 25718, 16, 3);

    /// <summary>Msg 25719: <c>sys.fn_xe_file_target_read_file</c> given an initial file name without its offset, or the reverse.</summary>
    internal static SimulatedSqlException XeInitialFileAndOffsetUnpaired() =>
        new("Initial file name and initial offset must be specified as a pair. Please correct the parameters and retry your query.", 25719, 16, 4);

    /// <summary>Msg 40133: an event session <c>ON DATABASE</c>, an Azure SQL Database surface.</summary>
    internal static SimulatedSqlException DatabaseEventSessionNotSupported() =>
        new("This operation is not supported in this version of SQL Server.", 40133, 16, 13);
}
